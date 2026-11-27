using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>Server-side Level 1 ship scanner. A SteamID owns the upgrade level; the current
    /// identity receives temporary HUD GPS contacts while actively controlling a ship controller.</summary>
    internal sealed class PlayerScannerManager
    {
        private readonly FactionRuntime _runtime;
        private readonly ScannerProfileStore _profiles;
        private readonly PlayerKnowledgeStore _knowledge;
        private readonly UplinkManager _uplink;
        private readonly Dictionary<long, PlayerScannerState> _states =
            new Dictionary<long, PlayerScannerState>();
        private readonly List<AntennaReading> _antennas = new List<AntennaReading>();

        /// <summary>Authority on a cluster: the players on agents, with the seat each reports. Null
        /// everywhere else.</summary>
        internal Func<IList<RemotePlayer>> RemotePlayers;

        /// <summary>Grids the sim does not own, refreshed by the plugin before this tick (FAC-533).
        /// Null where the plugin has none to offer.</summary>
        internal Func<List<ScannerGridCandidate>> Grids;

        public PlayerScannerManager(FactionRuntime runtime, string storageDir, UplinkManager uplink)
        {
            _runtime = runtime;
            _profiles = new ScannerProfileStore(storageDir, 1);
            _knowledge = new PlayerKnowledgeStore(storageDir);
            _uplink = uplink;
            if (_uplink != null) _uplink.ClientLinked += SendScannerConfig;
        }

        /// <summary>Runs once, when a player's client plugin links. Pushes the scanner parameters so
        /// the client can size its distance falloff without hard-coding our constants, and hands the
        /// player over from the GPS path cleanly.
        ///
        /// <para>Sensor range now follows the antennas on whatever the player is flying, so no single
        /// number is "the" range. The two slots carry the antenna-less floor and one reference antenna
        /// at full radius; the client scales its falloff against the second, as it did against the
        /// large-grid range.</para></summary>
        private void SendScannerConfig(ulong steamId)
        {
            int level = LevelFor(steamId.ToString());
            FactionTuning tuning = _runtime != null && _runtime.World != null ? _runtime.World.Tuning : null;
            _uplink.Send(steamId, FactionsWire.WriteScannerConfig(
                level, tuning != null ? tuning.SensorFloorKm : 0, ShipSensor.ReferenceKm(tuning)));
            HandOverFromGps(steamId);
        }

        /// <summary>
        /// A client cannot uplink until it has a session, so the first scanner pass or two of every
        /// login run down the GPS path and push markers. Once the link is granted the wire path
        /// takes over and those markers are orphaned: static ones fade out on their 180 s expiry,
        /// but fleet markers are deliberately non-temporary (Keen's modify API clears expiry, so
        /// they are manager-owned and only ever removed explicitly) and would linger for the rest
        /// of the session. Delete them here rather than leaving them to a TTL that fleet contacts
        /// do not have.
        ///
        /// The tracker is reset too: it is shared by both paths, so a player who links mid-session
        /// would otherwise have every current contact already recorded as present, and the first
        /// wire delta would carry only subsequent changes -- leaving the client blind to everything
        /// already around it.
        /// </summary>
        private void HandOverFromGps(ulong steamId)
        {
            if (MySession.Static == null) return;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.Identity == null || player.Id.SteamId != steamId) continue;
                long identityId = player.Identity.IdentityId;
                PlayerScannerState state;
                if (!_states.TryGetValue(identityId, out state)) return;
                ClearState(identityId, state, "link");
                return;
            }
        }

        public int Tick()
        {
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return 0;
            var online = new HashSet<long>();
            List<ScannerFleetCandidate> fleets = _runtime.GetScannerFleetCandidates();
            List<ScannerGridCandidate> grids = Grids != null ? Grids() : null;
            int visible = 0;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                long identityId = player.Identity.IdentityId;
                online.Add(identityId);
                PlayerScannerState state = GetState(identityId);
                string steamId = player.Id.SteamId.ToString();
                int level = _profiles.GetLevel(steamId);
                MyShipController controller = player.Controller != null
                    ? player.Controller.ControlledEntity as MyShipController
                    : null;
                if (level < 1)
                {
                    ClearState(identityId, state, "no-level");
                    state.ActiveGridEntityId = 0;
                    state.HasGridSensor = false;
                    SendResetIfLinked(player.Id.SteamId, state);
                    continue;
                }
                if (controller == null || controller.CubeGrid == null)
                {
                    // Hide scanner-owned HUD entries while the player is not at a controller, but
                    // preserve sphere membership. Re-entering the same ship must not make every
                    // unchanged contact beep again. Switching grids below resets membership.
                    ClearMarkers(identityId, state);
                    // Out of the seat is out of the sensor: a player standing on a station sees
                    // fleets through their faction's nodes, not through the ship they left.
                    state.HasGridSensor = false;
                    // A linked client draws from its own contact set, so hiding the GPS markers is
                    // not enough -- it has to be told to drop them, or the HUD keeps showing
                    // contacts for a scanner that is no longer running.
                    if (_uplink != null && _uplink.IsLinked(player.Id.SteamId))
                    {
                        state.Tracker.Clear();
                        state.OwesClientReset = true;
                        state.ResetReason = "unseated";
                        SendResetIfLinked(player.Id.SteamId, state);
                    }
                    continue;
                }

                Vector3D pos = controller.CubeGrid.PositionComp.GetPosition();
                ShipAntennas.Read(controller.CubeGrid, identityId, _antennas);
                visible += Scan(identityId, player.Id.SteamId, state, level, controller.CubeGrid.EntityId,
                    FactionsPlugin.GroupLeadOf(controller.CubeGrid),
                    new Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0),
                    ShipSensor.StrengthKm(_antennas, _runtime.World.Tuning), _antennas.Count, fleets, grids);
            }

            visible += TickRemote(online, fleets, grids);

            var stale = new List<long>();
            foreach (KeyValuePair<long, PlayerScannerState> pair in _states)
                if (!online.Contains(pair.Key)) stale.Add(pair.Key);
            foreach (long identityId in stale)
            {
                ClearState(identityId, _states[identityId], "offline");
                _states.Remove(identityId);
            }
            _profiles.SaveIfDirty();
            // Compaction is a file-size measure only; expiry is evaluated at read either way.
            _knowledge.Compact(_runtime.World.UtcNow, _runtime.World.Tuning.DiscoveryHorizon);
            _knowledge.SaveIfDirty();
            return visible;
        }

        /// <summary>One scan from a seated player's grid: resolve the contacts in its sensor sphere,
        /// record the nodes seen, and reconcile them onto the HUD or the linked client. Returns the
        /// number of contacts visible.</summary>
        private int Scan(long identityId, ulong steamId, PlayerScannerState state, int level,
                         long gridEntityId, long gridLeadId, Vec3 origin, double rangeKm, int antennaCount,
                         List<ScannerFleetCandidate> fleets, List<ScannerGridCandidate> grids)
        {
            if (state.ActiveGridEntityId != 0 && state.ActiveGridEntityId != gridEntityId)
            {
                // A new ship defines a new scanner sphere. Remove every marker from the old
                // sphere before resetting membership, otherwise old-only contacts linger until TTL.
                ClearMarkers(identityId, state);
                state.Tracker.Clear();
                state.Tokens.Clear();
                state.OwesClientReset = true;
                state.ResetReason = "grid-switch";
            }
            state.ActiveGridEntityId = gridEntityId;
            state.ActiveGridLeadId = gridLeadId;

            state.AntennaCount = antennaCount;
            state.GridSensor = new SensorSource { PositionKm = origin, StrengthKm = rangeKm };
            state.HasGridSensor = true;
            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                level, origin, rangeKm, _runtime.World.Graph.Nodes.Values, fleets,
                _runtime.World.Tuning, _runtime.World.PriceCatalogue, _runtime.World.Registry,
                _runtime.SensorOcclusion, grids, gridLeadId);
            // Grade before Reconcile: the tracker still holds last pass's tiers, which the
            // hysteresis reads. Withhold after it, so nothing above the tier reaches the wire.
            PlayerScanner.Grade(contacts, state.Tracker,
                tag => _relations.For(tag, identityId, false) == WireRelation.Own, _runtime.World.Tuning,
                identityId);
            // The player's own detections are their personal map. Nearly free here: the scan
            // that produced these contacts has already run for the HUD.
            //
            // Each entry carries what was seen, not just when -- the owner and hold as of this
            // moment, and the tier the HUD graded it at -- because the map draws a remembered node
            // from that snapshot and has nothing else to draw it from. Recorded after Grade and
            // before Withhold, so the tier is known and the owner is still the true one.
            if (_knowledge != null)
            {
                NodeStages stages = _runtime.NodeStages;
                Func<SpatialNode, DefenceReading> defenceOf = _runtime.NodeDefence;
                TimeSpan fleetWindow = FactionIntelEstimate.FleetWindow(_runtime.World.Tuning);
                TimeSpan staleAfter = IntelGrade.StaleAfter(_runtime.World.Tuning);
                DateTime seenUtc = _runtime.World.UtcNow;
                for (int c = 0; c < contacts.Count; c++)
                {
                    ScannerContact contact = contacts[c];
                    if (contact.Kind == ScannerContactKind.Fleet || contact.Kind == ScannerContactKind.RaidEngagement
                        || contact.Kind == ScannerContactKind.Grid)
                    {
                        // A fleet is never remembered, but a close look at one is a fact about its
                        // faction: the strongest seen feeds the Factions page's strength estimate.
                        // Full is the viewer's own, whose figures are exact.
                        if (contact.Tier == IntelTier.Armament && !string.IsNullOrEmpty(contact.OwnerTag))
                            _knowledge.RecordSighting(identityId, contact.OwnerTag, contact.WeaponDps, seenUtc, fleetWindow);
                        continue;
                    }
                    if (string.IsNullOrEmpty(contact.Key)) continue;
                    const string nodePrefix = "node:";
                    if (!contact.Key.StartsWith(nodePrefix, StringComparison.Ordinal)) continue;
                    string detectedId = contact.Key.Substring(nodePrefix.Length);
                    SpatialNode detected;
                    if (_runtime.World.Graph.Nodes.TryGetValue(detectedId, out detected) && detected != null)
                        _knowledge.Touch(identityId, detected, seenUtc, contact.Tier,
                                         stages != null ? stages.Of(detected) : NodeStage.None, staleAfter,
                                         contact.Tier >= IntelTier.Armament && defenceOf != null
                                             ? defenceOf(detected) : default(DefenceReading));
                    else
                        _knowledge.Touch(identityId, detectedId, seenUtc);
                }
            }
            PlayerScanner.Withhold(contacts, tag => OffersFlightPlan(_relations.For(tag, identityId, false)),
                                   _runtime.World.Tuning);
            if (DebugTraces.Intel) TraceIntel(steamId, state, contacts);
            Reconcile(identityId, state, contacts, steamId);
            _relations.Clear();
            return contacts.Count;
        }

        /// <summary>Authority on a cluster: the players relayed from agents. Their grid is not here,
        /// so the scan runs from the seat their agent reports, with the sensor strength it read from
        /// the grid's antennas. Only a linked client is scanned for. A relayed player has no GPS
        /// path, because GPS markers would be added on a server they are not on.</summary>
        private int TickRemote(HashSet<long> online, List<ScannerFleetCandidate> fleets,
                               List<ScannerGridCandidate> grids)
        {
            Func<IList<RemotePlayer>> source = RemotePlayers;
            if (source == null || _uplink == null) return 0;
            IList<RemotePlayer> remote = source();
            if (remote == null || remote.Count == 0) return 0;
            int visible = 0;
            for (int i = 0; i < remote.Count; i++)
            {
                RemotePlayer p = remote[i];
                if (p.IdentityId == 0 || !_uplink.IsRemote(p.SteamId)) continue;
                if (!online.Add(p.IdentityId)) continue;
                PlayerScannerState state = GetState(p.IdentityId);
                int level = _profiles.GetLevel(p.SteamId.ToString());
                if (level < 1)
                {
                    ClearState(p.IdentityId, state, "no-level");
                    state.ActiveGridEntityId = 0;
                    state.HasGridSensor = false;
                    SendResetIfLinked(p.SteamId, state);
                    continue;
                }
                if (p.SeatGridId == 0)
                {
                    state.HasGridSensor = false;
                    state.Tracker.Clear();
                    state.OwesClientReset = true;
                    state.ResetReason = "unseated";
                    SendResetIfLinked(p.SteamId, state);
                    continue;
                }
                Vec3 m = p.GridPositionM;
                visible += Scan(p.IdentityId, p.SteamId, state, level, p.SeatGridId, p.SeatGridId,
                    new Vec3(m.X / 1000.0, m.Y / 1000.0, m.Z / 1000.0), p.SensorKm, p.AntennaCount, fleets, grids);
            }
            return visible;
        }

        /// <summary>This player's own grid sensor as of the last tick, or false if they are not at a
        /// controller or carry no scanner level. The map's fleet fog reads it; the scanner ticks one
        /// line earlier in the same frame, so it is never a frame stale.</summary>
        public bool TryGetGridSensor(long identityId, out SensorSource sensor)
        {
            sensor = default(SensorSource);
            PlayerScannerState state;
            if (!_states.TryGetValue(identityId, out state) || !state.HasGridSensor) return false;
            sensor = state.GridSensor;
            return true;
        }

        /// <summary>The group lead of the grid this player's scanner is running on, or zero. The map
        /// leaves it out the way the scanner does.</summary>
        public long ActiveGridLeadOf(long identityId)
        {
            PlayerScannerState state;
            return _states.TryGetValue(identityId, out state) && state.HasGridSensor ? state.ActiveGridLeadId : 0;
        }

        /// <summary>One player's personal detections, for the viewer union. Never null.</summary>
        public FactionKnowledge KnowledgeFor(long identityId)
        {
            return _knowledge.For(identityId);
        }

        /// <summary>One player's fleet sightings, for the Factions page. Never null.</summary>
        public FleetSightings SightingsFor(long identityId)
        {
            return _knowledge.SightingsFor(identityId);
        }

        /// <summary>A fleet seen at Armament from the map, which grades fleets on its own pass.</summary>
        public void RecordSighting(long identityId, string tag, double dps)
        {
            if (_runtime == null || _runtime.World == null) return;
            _knowledge.RecordSighting(identityId, tag, dps, _runtime.World.UtcNow,
                                      FactionIntelEstimate.FleetWindow(_runtime.World.Tuning));
        }

        public string SetLevel(string steamId, int level)
        {
            if (string.IsNullOrEmpty(steamId)) return "scanner: SteamID required.";
            _profiles.SetLevel(steamId, level);
            _profiles.SaveIfDirty();
            return "scanner: " + steamId + " level=" + Math.Max(0, level);
        }

        /// <summary>Scanner level for a SteamID, as used by the uplink handshake. Reads through
        /// the same persisted profile store the GPS path uses, so a linked and an unlinked player
        /// are always told the same level.</summary>
        public int LevelFor(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return 0;
            int level = _profiles.GetLevel(steamId);
            _profiles.SaveIfDirty();
            return level;
        }

        public string Profile(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return "scanner: SteamID required.";
            int level = _profiles.GetLevel(steamId);
            _profiles.SaveIfDirty();
            return "scanner: " + steamId + " level=" + level;
        }

        /// <summary><paramref name="large"/> probes with one reference antenna at full radius;
        /// otherwise with the antenna-less floor. Grid size itself no longer sets range.</summary>
        public string Probe(bool large, Vec3 origin)
        {
            FactionTuning tuning = _runtime.World.Tuning;
            double range = large ? ShipSensor.ReferenceKm(tuning) : tuning.SensorFloorKm;
            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, origin, range, _runtime.World.Graph.Nodes.Values, _runtime.GetScannerFleetCandidates(),
                _runtime.World.Tuning, _runtime.World.PriceCatalogue, _runtime.World.Registry,
                _runtime.SensorOcclusion);
            // Graded cold and as nobody's faction: the tiers a stranger arriving here would see.
            PlayerScanner.Grade(contacts, null, null, tuning);
            var tiers = new int[(int)IntelTier.Full + 1];
            foreach (ScannerContact contact in contacts) tiers[(int)contact.Tier]++;
            var counts = new Dictionary<ScannerContactKind, int>();
            // The motion sample a client dead-reckons from, summarised: a contact that reaches the
            // wire with no velocity is drawn as a five-second step, and this is the only way to see
            // that from the bridge, without a player in a seat next to a convoy.
            int moving = 0;
            double fastest = 0.0;
            foreach (ScannerContact contact in contacts)
            {
                int count;
                counts.TryGetValue(contact.Kind, out count);
                counts[contact.Kind] = count + 1;
                if (!contact.Moving) continue;
                moving++;
                Vec3 v = contact.VelocityKmPerMin;
                double speed = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                if (speed > fastest) fastest = speed;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<ScannerContactKind, int> pair in counts)
                parts.Add(pair.Key + "=" + pair.Value);
            return "scanner probe " + (large ? "large" : "small") + " range=" + range
                + "km contacts=" + contacts.Count + (parts.Count > 0 ? " " + string.Join(" ", parts) : "")
                + " moving=" + moving
                + " fastest=" + fastest.ToString("0.00", CultureInfo.InvariantCulture) + "km/min"
                + " tiers contact=" + tiers[(int)IntelTier.Contact] + " class=" + tiers[(int)IntelTier.Class]
                + " identity=" + tiers[(int)IntelTier.Identity] + " armament=" + tiers[(int)IntelTier.Armament];
        }

        public void Close()
        {
            foreach (KeyValuePair<long, PlayerScannerState> pair in _states)
                ClearState(pair.Key, pair.Value, "close");
            _states.Clear();
            _profiles.SaveIfDirty();
        }

        private PlayerScannerState GetState(long identityId)
        {
            PlayerScannerState state;
            if (!_states.TryGetValue(identityId, out state))
            {
                state = new PlayerScannerState();
                _states[identityId] = state;
            }
            return state;
        }

        private void Reconcile(
            long identityId, PlayerScannerState state, IList<ScannerContact> contacts, ulong steamId)
        {
            ScannerContactDelta delta = state.Tracker.Reconcile(contacts);

            // A linked client renders the contacts itself. Send the same delta the GPS path would
            // have enacted and stop there — pushing GPS as well would double every marker.
            if (_uplink != null && _uplink.IsLinked(steamId))
            {
                SendContactDelta(steamId, identityId, delta, state);
                return;
            }

            foreach (string key in delta.RemovedKeys) Delete(identityId, state, key, true);

            // Grid contacts are for the client plugin only. On the GPS path SE's own antenna HUD
            // already draws every grid that broadcasts, and a GPS per derelict is clutter.
            foreach (ScannerContact contact in delta.UpdatedContacts)
                if (contact.Kind != ScannerContactKind.Grid) AddOrRefresh(identityId, state, contact, false);

            // A scan can discover a cluster in one pass. Play at most one notification sound for
            // that pass; every genuinely new contact is still recorded as present and remains silent
            // until it leaves the scanner sphere and later re-enters.
            bool playSound = true;
            foreach (ScannerContact contact in delta.NewContacts)
            {
                if (contact.Kind == ScannerContactKind.Grid) continue;
                AddOrRefresh(identityId, state, contact, playSound);
                playSound = false;
            }
        }

        /// <summary>Pushes an empty reset so a linked client drops everything it holds. Used when
        /// the scanner stops running for a player: the delta stream simply stops, so without this
        /// the client would keep drawing its last known contacts indefinitely.</summary>
        private void SendResetIfLinked(ulong steamId, PlayerScannerState state)
        {
            if (_uplink == null || !_uplink.IsLinked(steamId) || !state.OwesClientReset) return;
            state.OwesClientReset = false;
            _uplink.Send(steamId, FactionsWire.WriteScannerContacts(
                new List<WireContact>(), new List<string>(), true));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("SCANNER_CONTACTS steam=" + steamId + " reset (scanner inactive)");
            if (DebugTraces.Relation)
                FactionsPlugin.Log.Info("SCANNER_RELATION steam=" + steamId + " reset=" + (state.ResetReason ?? "unknown")
                    + " sent=0 first=0 changed=0 shifts=-");
        }

        /// <summary>Wire equivalent of the GPS enactment below. Nothing is sent when nothing
        /// changed, so a stationary player in a quiet sphere costs no traffic at all.</summary>
        private void SendContactDelta(
            ulong steamId, long viewerIdentityId, ScannerContactDelta delta, PlayerScannerState state)
        {
            bool reset = state.OwesClientReset;
            int changed = delta.NewContacts.Count + delta.UpdatedContacts.Count + delta.RemovedKeys.Count;
            // A reset must go out even with nothing to report -- that is exactly the case where the
            // client is holding contacts the server can no longer name.
            if (changed == 0 && !reset) return;
            state.OwesClientReset = false;

            var present = new List<WireContact>(delta.NewContacts.Count + delta.UpdatedContacts.Count);
            AppendWireContacts(present, delta.NewContacts, viewerIdentityId);
            AppendWireContacts(present, delta.UpdatedContacts, viewerIdentityId);

            _uplink.Send(steamId, FactionsWire.WriteScannerContacts(present, delta.RemovedKeys, reset));
            // Before this pass clears _relations, so the tally reads the answers just sent.
            if (DebugTraces.Relation) TraceRelations(steamId, viewerIdentityId, delta, state, reset);
            // Guarded at the call site, not inside the writer: the concatenation below must not
            // run at all on the normal path, which is every linked player every scanner pass.
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("SCANNER_CONTACTS steam=" + steamId
                    + " new=" + delta.NewContacts.Count
                    + " updated=" + delta.UpdatedContacts.Count
                    + " removed=" + delta.RemovedKeys.Count
                    + " sensorKm=" + state.GridSensor.StrengthKm.ToString("0.0", CultureInfo.InvariantCulture)
                    + " antennas=" + state.AntennaCount
                    + (reset ? " reset" : ""));
        }

        private void AppendWireContacts(
            List<WireContact> into, List<ScannerContact> from, long viewerIdentityId)
        {
            foreach (ScannerContact c in from)
            {
                into.Add(new WireContact
                {
                    Key = c.Key,
                    Kind = (byte)c.Kind,
                    Label = c.Label,
                    DisplayName = c.DisplayName,
                    XKm = c.PositionKm.X,
                    YKm = c.PositionKm.Y,
                    ZKm = c.PositionKm.Z,
                    EntityId = c.EntityId,
                    Unclaimed = c.Unclaimed,
                    Relation = (byte)RelationFor(c, viewerIdentityId),
                    Tier = (byte)c.Tier,
                    Moving = c.Moving,
                    HasTarget = c.HasFlightPlan,
                    VelXKmPerMin = c.VelocityKmPerMin.X,
                    VelYKmPerMin = c.VelocityKmPerMin.Y,
                    VelZKmPerMin = c.VelocityKmPerMin.Z,
                    TargetXKm = c.TargetPosKm.X,
                    TargetYKm = c.TargetPosKm.Y,
                    TargetZKm = c.TargetPosKm.Z,
                    FleetKind = c.FleetKind,
                    // Clamped, not wrapped: a byte is what the wire carries, and the echelon
                    // ladder's top band opens at 18, so every value past 255 draws identically
                    // anyway. Wrapping would turn a 256-hull convoy into a lone trader.
                    ConvoySize = (byte)(c.ConvoySize < 0 ? 0
                                        : c.ConvoySize > 255 ? 255 : c.ConvoySize),
                });
            }
        }

        /// <summary>SCANNER_RELATION: what this send did to the colours the client holds. A contact
        /// that left the sphere keeps its last-sent relation, so one that comes back in another colour
        /// counts as a shift, and so does a reset followed by a resend.</summary>
        private void TraceRelations(ulong steamId, long viewerIdentityId, ScannerContactDelta delta,
                                    PlayerScannerState state, bool reset)
        {
            try
            {
                RelationTrace.EnsureArmed();
                if (state.SentGeneration != RelationTrace.Generation)
                {
                    state.SentRelations.Clear();
                    state.SentGeneration = RelationTrace.Generation;
                }
                var tally = new RelationShiftTally();
                TallyRelations(tally, delta.NewContacts, viewerIdentityId, state);
                TallyRelations(tally, delta.UpdatedContacts, viewerIdentityId, state);
                if (tally.Changed == 0 && !reset) return;
                FactionsPlugin.Log.Info("SCANNER_RELATION steam=" + steamId + " viewer=" + viewerIdentityId
                    + " reset=" + (reset ? state.ResetReason ?? "unknown" : "no")
                    + " sent=" + tally.Sent + " first=" + tally.First + " changed=" + tally.Changed
                    + " shifts=" + tally.FormatShifts());
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Scanner relation trace failed.");
            }
        }

        private void TallyRelations(RelationShiftTally tally, List<ScannerContact> contacts, long viewerIdentityId,
                                    PlayerScannerState state)
        {
            foreach (ScannerContact c in contacts)
            {
                string now = RelationFor(c, viewerIdentityId).ToString();
                string previous;
                if (!state.SentRelations.TryGetValue(c.Key, out previous)) previous = null;
                tally.Add(c.OwnerTag, previous, now);
                state.SentRelations[c.Key] = now;
            }
        }

        // Cleared per player per pass: a relationship is viewer-relative. Shared implementation with
        // the map feature, so both send a player the same standing for the same faction.
        private readonly ViewerRelations _relations = new ViewerRelations("scanner");

        /// <summary>INTEL_GRADES: this viewer's contacts by tier, logged when the census changes.</summary>
        private static void TraceIntel(ulong steamId, PlayerScannerState state, IList<ScannerContact> contacts)
        {
            int contact = 0, cls = 0, identity = 0, armament = 0, full = 0, squawking = 0;
            for (int i = 0; i < contacts.Count; i++)
            {
                switch (contacts[i].Tier)
                {
                    case IntelTier.Contact:  contact++; break;
                    case IntelTier.Class:    cls++; break;
                    case IntelTier.Identity: identity++; break;
                    case IntelTier.Armament: armament++; break;
                    case IntelTier.Full:     full++; break;
                }
                if (contacts[i].Transponder) squawking++;
            }
            string census = "contacts=" + contacts.Count + " contact=" + contact + " class=" + cls
                + " identity=" + identity + " armament=" + armament + " full=" + full + " squawking=" + squawking;
            if (census == state.LastIntelCensus) return;
            state.LastIntelCensus = census;
            FactionsPlugin.Log.Info("INTEL_GRADES steam=" + steamId + " sensorKm="
                + state.GridSensor.StrengthKm.ToString("0.0", CultureInfo.InvariantCulture) + " " + census);
        }

        /// <summary>Whether a fleet's owner shares its flight plan with a viewer at this standing.
        /// The map reads the same rule.</summary>
        internal static bool OffersFlightPlan(WireRelation relation)
        {
            return relation == WireRelation.Own || relation == WireRelation.Friendly;
        }

        private WireRelation RelationFor(ScannerContact contact, long viewerIdentityId)
        {
            if (contact.Kind == ScannerContactKind.Grid) return GridRelation(_relations, contact.OwnerTag,
                contact.OwnerIdentityId, viewerIdentityId);
            return _relations.For(contact.OwnerTag, viewerIdentityId, contact.Unclaimed);
        }

        /// <summary>A grid's colour for one viewer. A grid the viewer owns is their own; a faction's
        /// grid takes the faction's standing; a factionless owner's takes SE's player-to-player
        /// relation; an unowned grid, and one whose owner is withheld, is grey. The map reads the same.</summary>
        internal static WireRelation GridRelation(ViewerRelations relations, string ownerTag, long ownerIdentityId,
                                                  long viewerIdentityId)
        {
            if (ownerIdentityId == 0) return WireRelation.Unknown;
            if (ownerIdentityId == viewerIdentityId) return WireRelation.Own;
            if (!string.IsNullOrEmpty(ownerTag)) return relations.For(ownerTag, viewerIdentityId);
            switch (MyIDModule.GetRelationPlayerPlayer(ownerIdentityId, viewerIdentityId))
            {
                case MyRelationsBetweenPlayers.Self: return WireRelation.Own;
                case MyRelationsBetweenPlayers.Allies: return WireRelation.Friendly;
                case MyRelationsBetweenPlayers.Neutral: return WireRelation.Neutral;
                default: return WireRelation.Enemy;
            }
        }

        private static void AddOrRefresh(
            long identityId, PlayerScannerState state, ScannerContact contact, bool playSound)
        {
            Color colour = Colour(contact.Kind, contact.Unclaimed);
            var gps = new MyGps
            {
                // Keen keys GPS entries by Name + coordinates (or Name + EntityId). Keep the visible
                // DisplayName abstract, but give each scanner contact a stable opaque internal name
                // so co-located contacts cannot overwrite or delete one another.
                Name = state.Tokens.GetOrCreate(contact.Key),
                DisplayName = contact.Label,
                Coords = new Vector3D(contact.PositionKm.X * 1000.0,
                                      contact.PositionKm.Y * 1000.0,
                                      contact.PositionKm.Z * 1000.0),
                GPSColor = colour,
                ShowOnHud = true,
                AlwaysVisible = false,
                // Keen's modify API clears temporary expiry and offers no way to restore it. Moving
                // fleet markers are therefore explicitly manager-owned/final and are deleted on
                // departure, range exit, grid switch, disconnect, disable, and shutdown. Static
                // contacts remain temporary with the normal 180-second safety expiry.
                DiscardAt = PlayerScanner.IsTemporaryGps(contact.Kind)
                    ? MySession.Static.ElapsedPlayTime.Add(TimeSpan.FromSeconds(180))
                    : (TimeSpan?)null
            };
            gps.EntityId = contact.EntityId;
            gps.UpdateHash();

            int oldHash;
            bool hasOldHash = state.Hashes.TryGetValue(contact.Key, out oldHash);
            ScannerGpsUpdateMode updateMode = PlayerScanner.SelectGpsUpdateMode(
                hasOldHash, oldHash, gps.Hash);
            if (updateMode == ScannerGpsUpdateMode.Modify)
            {
                // Modify must carry the OLD hash. Retrieve Keen's stored GPS object, change its
                // payload without calling UpdateHash, and let SendModifyGpsRequest re-key it on both
                // server and client without a creation event/sound.
                MyGps existingGps = MySession.Static.Gpss.GetGps(identityId, oldHash);
                if (existingGps != null)
                {
                    existingGps.Name = gps.Name;
                    existingGps.Coords = gps.Coords;
                    existingGps.GPSColor = gps.GPSColor;
                    existingGps.EntityId = gps.EntityId;
                    MySession.Static.Gpss.SendModifyGpsRequest(identityId, existingGps);
                    gps = existingGps;
                }
                else
                {
                    // The client-side temporary marker may have expired during a long stall. Treat
                    // the missing server copy as a silent add; membership still suppresses the beep.
                    updateMode = ScannerGpsUpdateMode.Refresh;
                }
            }
            state.Hashes[contact.Key] = gps.Hash;
            // Keen's SendAddGpsRequest copies this method argument into AddMsg.EntityId; assigning
            // gps.EntityId alone does not transmit entity tracking to the client. Static contacts
            // are silently re-added to refresh TTL; final fleet markers simply de-duplicate.
            MySession.Static.Gpss.SendAddGpsRequest(
                identityId, ref gps, contact.EntityId,
                playSoundOnCreation: updateMode == ScannerGpsUpdateMode.Add && playSound);
        }

        private static void Delete(
            long identityId, PlayerScannerState state, string key, bool removeToken)
        {
            int hash;
            if (state.Hashes.TryGetValue(key, out hash))
            {
                MySession.Static.Gpss.SendDeleteGpsRequest(identityId, hash);
                state.Hashes.Remove(key);
            }
            if (removeToken) state.Tokens.Remove(key);
        }

        private static void ClearMarkers(long identityId, PlayerScannerState state)
        {
            var keys = new List<string>(state.Hashes.Keys);
            foreach (string key in keys) Delete(identityId, state, key, false);
        }

        private static void ClearState(long identityId, PlayerScannerState state, string reason)
        {
            ClearMarkers(identityId, state);
            state.Tracker.Clear();
            state.Tokens.Clear();
            state.OwesClientReset = true;
            state.ResetReason = reason;
        }

        private static Color Colour(ScannerContactKind kind, bool unclaimed)
        {
            // Ownership status overrides the kind palette: a gray contact reads as "claimable"
            // regardless of what it is, so it stands out from every owned/kind-colored contact.
            if (unclaimed) return Color.Gray;
            switch (kind)
            {
                case ScannerContactKind.Station: return Color.LightBlue;
                case ScannerContactKind.Outpost: return Color.Cyan;
                case ScannerContactKind.OreField: return Color.Orange;
                case ScannerContactKind.RareOreField: return Color.Purple;
                case ScannerContactKind.IceField: return new Color(170, 225, 255);
                case ScannerContactKind.Refinery: return Color.Yellow;
                case ScannerContactKind.FuelDepot: return Color.Green;
                case ScannerContactKind.Fleet: return Color.Red;
                default: return Color.White;
            }
        }

        private sealed class PlayerScannerState
        {
            public readonly ScannerContactTracker Tracker = new ScannerContactTracker();
            public readonly ScannerOpaqueTokenRegistry Tokens = new ScannerOpaqueTokenRegistry();
            public readonly Dictionary<string, int> Hashes =
                new Dictionary<string, int>(StringComparer.Ordinal);
            public long ActiveGridEntityId;
            public long ActiveGridLeadId;
            /// <summary>Where this player's own scanner is looking from and how far, as of the last
            /// tick. Stashed rather than recomputed by the map, which runs one line later in the same
            /// frame: two derivations of "which grid is this player flying" would drift apart, and
            /// the one that matters for fog is whichever the scanner actually used.</summary>
            public SensorSource GridSensor;
            public bool HasGridSensor;
            /// <summary>Working antennas the pilot may use, as of the last tick.</summary>
            public int AntennaCount;
            /// <summary>Set whenever this player's tracker is dropped. The next wire message must
            /// carry reset=true, or contacts the client already holds are stranded: the server no
            /// longer knows their keys, so it can never name them for removal.</summary>
            public bool OwesClientReset;
            /// <summary>Why <see cref="OwesClientReset"/> was last set, for SCANNER_RELATION.</summary>
            public string ResetReason;
            /// <summary>Under <c>factions debug relation</c> only: the relation last sent per contact
            /// key, kept across resets. Discarded when <see cref="SentGeneration"/> is stale.</summary>
            public readonly Dictionary<string, string> SentRelations =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public int SentGeneration;
            /// <summary>Under <c>factions debug intel</c> only: the last INTEL_GRADES census logged.</summary>
            public string LastIntelCensus;
        }
    }
}
