using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// A dwell fight near a player, flown in waves (docs/fight-control.md, FAC-425 slice 3).
    ///
    /// <para>A site opens when a raid begins its dwell with a player in range. Every fleet of the
    /// meeting — the raid and the fleets standing as its target's defence — joins it: grids already in
    /// the world are adopted onto the field, the rest of each fleet's hulls queue. From then on the
    /// site owns those fleets' materialisation: the ordinary spawn path is skipped for them
    /// (<see cref="WaveHolds"/>), and each review clears queue heads onto the field within the budget
    /// and spawns them as a sub-squadron under <c>&lt;fleetKey&gt;#w&lt;n&gt;</c>, so the grid marks
    /// still belong to the fleet. A hull leaving the field — retreating, rotating, shed — is settled
    /// into its slot, detached from its fleet and flown out, then closed; a lead is replaced by the next
    /// hull on the field rather than ending the fleet.</para>
    ///
    /// <para><b>Which grid is which hull.</b> Slots are matched to grids by prefab in spawn order
    /// everywhere else (<see cref="MapHullSlots"/>), which is only consistent while the set of grids does
    /// not change. Waves change it constantly, so a waved fleet's grids carry an explicit slot each
    /// (<see cref="_waveSlots"/>), renumbered when a slot is lost.</para>
    ///
    /// <para>While two sides stand, every fleet of the site has its schedule frozen — the raid's dwell
    /// and a Guard's duty window alike — so the fight runs until one side has nothing left (operator,
    /// 2026-10-07: physical only, no cap). It ends when it is decided, or the moment no player can see
    /// it, which hands it to the duel as a player leaving always has.</para>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private sealed class WaveSite
        {
            public FightSite Site;
            public string RaidFleetId;
            public string NodeId;
            public Vec3 PosKm;
            public readonly HashSet<string> Fleets = new HashSet<string>(StringComparer.Ordinal);
            public DateTime LastFreezeUtc;
            public bool Decided;
            public int WaveSeq;
        }

        private sealed class WaveSpawn
        {
            public string SiteId;
            public string FleetId;
            public string Key;
            public List<string> Prefabs;
            public List<int> Slots;
            public Vec3 PosKm;
            public DateTime StartedUtc;
        }

        private sealed class WaveLeave
        {
            public string SiteId;
            public string Fleet;
            public Vec3 FromKm;
            public double CostMs;
            public DateTime GiveUpUtc;
            /// <summary>The hull's fleet and slot, so a hull shot out of action while it leaves can be
            /// written off; the slot is renumbered with the fleet's (<see cref="WaveSlotRemoved"/>).</summary>
            public string FleetId;
            public int Slot = -1;
            public string ActorTag;
            /// <summary>Where the grid was when it was taken off the field.</summary>
            public Vec3? StartKm;
            public DateTime StartedUtc;
        }

        private readonly Dictionary<string, WaveSite> _waveSites = new Dictionary<string, WaveSite>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _waveSiteOfFleet = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<long, int>> _waveSlots = new Dictionary<string, Dictionary<long, int>>(StringComparer.Ordinal);
        private readonly List<WaveSpawn> _waveSpawns = new List<WaveSpawn>();
        private readonly Dictionary<long, WaveLeave> _waveLeaving = new Dictionary<long, WaveLeave>();
        private readonly List<long> _waveScratch = new List<long>();
        /// <summary>Fleets no site may fly for the rest of the dwell they are on: a lead the cruise
        /// autopilot would not fly. A site spawns its own hulls, so it would put the same hull straight
        /// back into the launch that just failed; the fight goes to the duel instead
        /// (<see cref="BarFromWaves"/>).</summary>
        private readonly HashSet<string> _waveBarred = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _nextWaveReviewUtc = DateTime.MinValue;

        /// <summary>A hull's predicted combat cost on the field, ms/frame, by prefab. Null until the
        /// governor slice wires it; <see cref="FightWaves.DefaultHullCostMs"/> then.</summary>
        public Func<string, double> WaveHullCostMs { get; set; }

        /// <summary>The field budget for one of <c>sites</c> live sites, with the fights' own sim floor
        /// tripped or not, ms/frame (<see cref="FightWaves.FieldMs"/> over the governor's budget and step).
        /// Null in a host without telemetry: the default budget then.</summary>
        public Func<int, bool, double> WaveFieldMs { get; set; }

        /// <summary>How long a node stays closed after the last fight near it closed: its wreckage is
        /// still being swept, and traffic does not need to arrive into the tail of a fight.</summary>
        private const double ReopenGraceSeconds = 60.0;

        /// <summary>Closed nodes and when each reopens if no fight renews it.</summary>
        private readonly Dictionary<string, DateTime> _closedUntil = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        /// <summary>Civilian fleets this closure already tried to divert, so a fleet with nowhere to go
        /// is not retried every second; and when each was last held, for the freeze gap.</summary>
        private readonly HashSet<string> _combatDivertTried = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _combatHeldAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private readonly SimFloorWatch _waveSimWatch = new SimFloorWatch();
        private bool _waveSimTripped;

        /// <summary>Whether a grid is a hull leaving a fight's field, for the reaper.</summary>
        internal bool IsWaveLeaving(long eid) { return _waveLeaving.ContainsKey(eid); }

        /// <summary>The hulls the ordinary spawn path materialises for <paramref name="fleet"/>: every
        /// survivor, or with the switch on and a combat fleet, the first
        /// <see cref="FightWaves.TransitMaxHulls"/>.</summary>
        private static IReadOnlyList<string> SpawnHullsFor(PendingFleetAction fleet)
        {
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            return FactionsLoadActuator.IsCombat(fleet.Kind) ? FightWaves.TransitHulls(hulls) : hulls;
        }

        /// <summary>Called where the ordinary path would begin materialising <paramref name="fleet"/>.
        /// True when a fight site owns it — joined now if it has just come to stand at a site's node —
        /// so the caller must not spawn it.</summary>
        private bool WaveHolds(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            if (fleet.FleetId == null || _waveBarred.Contains(fleet.FleetId)) return false;
            if (_waveSiteOfFleet.ContainsKey(fleet.FleetId)) return true;
            if (!FactionsLoadActuator.IsCombat(fleet.Kind)) return false;
            foreach (WaveSite site in _waveSites.Values)
            {
                if (site.Decided || !string.Equals(site.NodeId, fleet.TargetNodeId, StringComparison.Ordinal)) continue;
                if (!fleet.StandsAsDefender(now, _tuning) && fleet.FleetId != site.RaidFleetId) continue;
                JoinWaveSite(site, st, fi, now);
                return true;
            }
            return false;
        }

        /// <summary>Once per observation pass. Spawns and departures every pass; sites, exits and
        /// admissions every <see cref="FightWaves.ReviewSeconds"/>.</summary>
        private void TickFightWaves(DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            SweepWaveLeaving(now, isPlayerWithin);
            PollWaveSpawns(now);
            if (now < _nextWaveReviewUtc) return;
            _nextWaveReviewUtc = now.AddSeconds(FightWaves.ReviewSeconds);

            PruneWaveBars(now);
            OpenWaveSites(now, isPlayerWithin);
            UpdateClosedNodes(now);
            if (_world.ClosedForCombat.Count > 0) KeepTrafficClear(now);
            if (_waveSites.Count == 0) { PruneWaveSlots(); return; }

            bool tripped = _waveSimWatch.Update(Sandbox.Engine.Physics.MyPhysics.SimulationRatio, now);
            if (tripped != _waveSimTripped)
            {
                _waveSimTripped = tripped;
                _log.Write("FIGHT_SIM_FLOOR " + (tripped ? "tripped" : "cleared") + " sites=" + _waveSites.Count
                           + " sim=" + Sandbox.Engine.Physics.MyPhysics.SimulationRatio.ToString("0.00", CultureInfo.InvariantCulture));
            }
            var ids = new List<string>(_waveSites.Keys);
            foreach (string id in ids)
            {
                WaveSite site;
                if (_waveSites.TryGetValue(id, out site)) ReviewWaveSite(site, now, isPlayerWithin);
            }
            PruneWaveSlots();
        }

        /// <summary>Bridge and chat verb <c>factions fight</c>: every live site, each side's field and
        /// queue, the hulls leaving and the spawns in flight.</summary>
        internal string DescribeFightSites()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("fight: sites=").Append(_waveSites.Count)
              .Append(" leaving=").Append(_waveLeaving.Count)
              .Append(" spawning=").Append(_waveSpawns.Count);
            foreach (WaveSite site in _waveSites.Values)
            {
                sb.Append("\n  [").Append(site.RaidFleetId).Append("] @").Append(site.NodeId ?? "?")
                  .Append(site.Decided ? " decided" : "")
                  .Append(" fleets=").Append(site.Fleets.Count)
                  .Append(" usedMs=").Append(site.Site.UsedMs.ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" ").Append(SiteCounts(site));
            }
            return sb.ToString();
        }

        // ---- sites -------------------------------------------------------------------------------

        private void OpenWaveSites(DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            if (isPlayerWithin == null) return;
            for (int si = 0; si < _states.Count; si++)
            {
                List<PendingFleetAction> fleets = _states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction raid = fleets[fi];
                    if (raid.FleetId == null || raid.CurrentStopKind != FleetStopKind.Engage
                        || _waveSites.ContainsKey(raid.FleetId) || _waveBarred.Contains(raid.FleetId)) continue;
                    List<(int si, int fi)> meeting = PendingFleetResolver.MeetingOf(_states, _world, _tuning, si, fi);
                    if (meeting.Count == 0) continue;
                    Vec3 pos = NodePosOr(raid.TargetNodeId, raid.TargetPosKm);
                    if (!isPlayerWithin(pos, _tuning.ObservationRadiusKm)) continue;
                    // One site a node: a second raid on it fights as it always has, beside the waves,
                    // rather than opening a site that would share the first one's defence.
                    if (SiteAtNode(raid.TargetNodeId) != null) continue;

                    var site = new WaveSite
                    {
                        Site = new FightSite(raid.FleetId, pos, now),
                        RaidFleetId = raid.FleetId,
                        NodeId = raid.TargetNodeId,
                        PosKm = pos,
                        LastFreezeUtc = now,
                    };
                    _waveSites[raid.FleetId] = site;
                    _world.WavedMeetings.Add(raid.FleetId);
                    foreach ((int si, int fi) m in meeting) JoinWaveSite(site, _states[m.si], m.fi, now);
                    _log.Write("FIGHT_SITE_OPENED " + raid.LogId + " " + raid.ActorTag + " @" + (raid.TargetNodeId ?? "?")
                               + " fleets=" + site.Fleets.Count + " " + SiteCounts(site));
                }
            }
        }

        private WaveSite SiteAtNode(string nodeId)
        {
            if (nodeId == null) return null;
            foreach (WaveSite s in _waveSites.Values)
                if (string.Equals(s.NodeId, nodeId, StringComparison.Ordinal)) return s;
            return null;
        }

        private Vec3 NodePosOr(string nodeId, Vec3 fallback)
        {
            SpatialNode node;
            return nodeId != null && _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.Position : fallback;
        }

        /// <summary>Joins one fleet to <paramref name="site"/>: its grids in the world go on the field
        /// with the slot they fly, every other surviving hull queues.</summary>
        private void JoinWaveSite(WaveSite site, FactionState st, int fi, DateTime now)
        {
            PendingFleetAction fleet = st.PendingFleets[fi];
            // A spawn already in flight on the ordinary path lands first and is adopted next review;
            // joining now would queue, and then spawn a second time, the hulls it is bringing.
            // A fleet whose lead went down outside the site (-1) is already resolving.
            if (fleet.FleetId == null || fleet.MaterialisedEntityId < 0) return;
            if (_waveBarred.Contains(fleet.FleetId)) return;
            if (_waveSiteOfFleet.ContainsKey(fleet.FleetId)) return;   // flown by a site already
            if (!site.Fleets.Add(fleet.FleetId)) return;
            _waveSiteOfFleet[fleet.FleetId] = site.Site.Id;

            var slots = new Dictionary<long, int>();
            if (fleet.MaterialisedEntityId > 0)
            {
                CollectHullEids(fleet, fleet.MaterialisedEntityId);
                int[] mapped = MapHullSlots(fleet, _hullEids);
                var used = new HashSet<int>();
                for (int k = 0; k < _hullEids.Count; k++)
                    if (mapped[k] >= 0) { slots[_hullEids[k]] = mapped[k]; used.Add(mapped[k]); }
                // A grid no prefab names (adopted across a restart) still flies a hull: it takes the
                // first slot nothing else has, or that slot would spawn a second grid.
                for (int k = 0; k < _hullEids.Count; k++)
                {
                    if (mapped[k] >= 0) continue;
                    foreach (int free in FightWaves.FreeSlots(fleet.HullSlotCount, used))
                    {
                        slots[_hullEids[k]] = free;
                        used.Add(free);
                        break;
                    }
                }
            }
            _waveSlots[fleet.FleetId] = slots;

            Func<string, double> cost = WaveHullCostMs;
            var onField = new HashSet<int>(slots.Values);
            foreach (int slot in onField)
                site.Site.PlaceOnField(FightWaves.HullOf(fleet, slot, st.Tag, _world.PrefabCatalogue, cost), now);
            foreach (int slot in FightWaves.FreeSlots(fleet.HullSlotCount, onField))
                site.Site.Enqueue(FightWaves.HullOf(fleet, slot, st.Tag, _world.PrefabCatalogue, cost), now);
        }

        /// <summary>Takes <paramref name="fleet"/> out of every fight site for the rest of its dwell: the
        /// site that flies it closes when the fleet is its raid, and otherwise lets it go. The caller
        /// has settled and despawned its grids. With no site and no grid the fleet is neither waved
        /// nor materialised, so Core hands its fight to the duel for the turns left, from the hulls as
        /// they stand. Only a fleet a site could fly is barred: one on its raid dwell, or standing as
        /// a defence. Logs <c>FIGHT_FLEET_BARRED</c>.</summary>
        private void BarFromWaves(PendingFleetAction fleet, string why, DateTime now)
        {
            if (fleet.FleetId == null) return;
            if (fleet.CurrentStopKind != FleetStopKind.Engage && !fleet.StandsAsDefender(now, _tuning)) return;
            _waveBarred.Add(fleet.FleetId);
            _waveSlots.Remove(fleet.FleetId);
            for (int i = _waveSpawns.Count - 1; i >= 0; i--)
                if (string.Equals(_waveSpawns[i].FleetId, fleet.FleetId, StringComparison.Ordinal)) _waveSpawns.RemoveAt(i);
            WaveSite site = SiteOf(fleet);
            _log.Write("FIGHT_FLEET_BARRED " + fleet.LogId + " " + fleet.ActorTag + " why=" + why
                       + " site=" + (site != null ? "[" + site.RaidFleetId + "]" : "none"));
            if (site == null) return;
            if (string.Equals(site.RaidFleetId, fleet.FleetId, StringComparison.Ordinal)) CloseWaveSite(site, now, why);
            else LeaveWaveSite(site, fleet.FleetId);
        }

        /// <summary>Lifts the bar from fleets whose dwell is over: gone, or neither on a raid dwell nor
        /// standing as a defence.</summary>
        private void PruneWaveBars(DateTime now)
        {
            if (_waveBarred.Count == 0) return;
            List<string> lift = null;
            foreach (string id in _waveBarred)
            {
                int si, fi;
                bool held = FindFleet(id, out si, out fi)
                            && (_states[si].PendingFleets[fi].CurrentStopKind == FleetStopKind.Engage
                                || _states[si].PendingFleets[fi].StandsAsDefender(now, _tuning));
                if (!held) (lift ?? (lift = new List<string>())).Add(id);
            }
            if (lift != null) foreach (string id in lift) _waveBarred.Remove(id);
        }

        private void LeaveWaveSite(WaveSite site, string fleetId)
        {
            site.Site.Withdraw(fleetId);
            site.Fleets.Remove(fleetId);
            _waveSiteOfFleet.Remove(fleetId);
        }

        private void ReviewWaveSite(WaveSite site, DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            int rsi, rfi;
            if (!FindFleet(site.RaidFleetId, out rsi, out rfi))
            {
                CloseWaveSite(site, now, "resolved");
                return;
            }
            List<(int si, int fi)> meeting = PendingFleetResolver.MeetingOf(_states, _world, _tuning, rsi, rfi);
            if (meeting.Count == 0)
            {
                CloseWaveSite(site, now, "resolved");
                return;
            }
            if (isPlayerWithin == null || !isPlayerWithin(site.PosKm, _tuning.ObservationRadiusKm + DespawnHysteresisKm))
            {
                CloseWaveSite(site, now, "unobserved");
                return;
            }

            // Fleets that came to stand at the node since the last review join; fleets that stopped
            // standing leave with whatever they have.
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach ((int si, int fi) m in meeting)
            {
                PendingFleetAction f = _states[m.si].PendingFleets[m.fi];
                if (f.FleetId == null) continue;
                members.Add(f.FleetId);
                if (!site.Fleets.Contains(f.FleetId)) JoinWaveSite(site, _states[m.si], m.fi, now);
            }
            foreach (string id in new List<string>(site.Fleets))
                if (!members.Contains(id)) LeaveWaveSite(site, id);

            if (site.Decided) return;

            // Physical only: the dwell and every duty window stand still while more than one side does.
            TimeSpan gap = now - site.LastFreezeUtc;
            site.LastFreezeUtc = now;
            if (site.Site.SidesStanding >= 2 && gap > TimeSpan.Zero && gap.TotalSeconds <= CombatHoldTracker.GapSeconds)
                foreach ((int si, int fi) m in meeting)
                    _states[m.si].PendingFleets[m.fi] = FactionsLoadActuator.Freeze(_states[m.si].PendingFleets[m.fi], gap);

            string winner = null;
            bool decided = site.Site.Sides.Count >= 2 && site.Site.IsDecided(out winner);
            // A raid with no hull left on the field or in the queue has lost its meeting, whatever else
            // the site counts: a station is not a side, so a raid on one alone would otherwise sit out
            // its dwell with nothing in it.
            if (!decided && RaidSpent(site, _states[rsi].Tag)) { decided = true; winner = null; }
            if (decided)
            {
                site.Decided = true;
                bool attackerStands = winner != null
                                      && string.Equals(winner, _states[rsi].Tag, StringComparison.OrdinalIgnoreCase);
                var lines = new List<string>();
                PendingFleetResolver.DecideWaved(_states, _world, _tuning, rsi, rfi, attackerStands, lines);
                foreach (string line in lines) _log.Write(line);
                PendingFleetAction raid = _states[rsi].PendingFleets[rfi];
                _log.Write("FIGHT_SITE_DECIDED " + raid.LogId + " @" + (site.NodeId ?? "?")
                           + " winner=" + (winner ?? "none") + " " + SiteCounts(site));
                return;
            }

            // A wave still landing is left to land: the site's field already counts its hulls, and an
            // exit taken now would have no grid to take while the grid that does land went untracked.
            if (HasPendingWaveSpawn(site.Site.Id)) return;

            double fieldMs = WaveFieldMs != null ? WaveFieldMs(_waveSites.Count, _waveSimTripped)
                                                 : FightWaves.FieldMs(double.NaN, _waveSites.Count, 0, _waveSimTripped);
            foreach (FightExit exit in site.Site.Review(now, h => WaveIntegrity(h), fieldMs))
                TakeOffField(site, exit.Hull, exit.Cause, now);

            double leavingMs = 0;
            foreach (WaveLeave l in _waveLeaving.Values) if (l.SiteId == site.Site.Id) leavingMs += l.CostMs;
            List<FightHull> cleared = site.Site.Admit(Math.Max(0, fieldMs - leavingMs), now, h => WaveIntegrity(h));
            if (cleared.Count > 0) StartWaveSpawns(site, cleared, now);
        }

        private static bool RaidSpent(WaveSite site, string raidTag)
        {
            IReadOnlyList<string> sides = site.Site.Sides;
            bool has = false;
            for (int i = 0; i < sides.Count; i++) if (string.Equals(sides[i], raidTag, StringComparison.Ordinal)) has = true;
            return has && site.Site.OnField(raidTag) + site.Site.Queued(raidTag) == 0;
        }

        /// <summary>A hull the site holds — on its field (<paramref name="onField"/>) or leaving it —
        /// that has no thrust or no owner with hostile fire on record is out of action: its hull is
        /// written off in slot <paramref name="slot"/> of its fleet (not flown home), its grid is left as
        /// a wreck for <see cref="DisabledHull.DespawnDelay"/>, and the player whose fire did it is told.
        /// What <see cref="TryDisableLead"/> and <see cref="TryDisableEscort"/> do outside a site, for a
        /// grid the site may already have taken from its fleet. Logs <c>FIGHT_HULL_LOST … cause=disabled
        /// at=exit|leaving|stuck|thrust_lost</c>.</summary>
        private bool WaveHullShotOutOfAction(FactionState st, int fi, long eid, int slot, string prefab, bool onField,
                                             string at, DateTime now)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null || slot < 0 || !vanilla.IsOutOfAction(eid)) return false;
            long attackerGrid;
            DateTime? hitUtc;
            if (!ShotOutOfAction(st.PendingFleets[fi], eid, now, out attackerGrid, out hitUtc)) return false;
            WriteOffWaveHull(st, fi, eid, slot, prefab, onField, at, attackerGrid, hitUtc.Value, now);
            return true;
        }

        private void WriteOffWaveHull(FactionState st, int fi, long eid, int slot, string prefab, bool onField,
                                      string at, long attackerGrid, DateTime hitUtc, DateTime now)
        {
            var vanilla = (VanillaFleetSpawner)_spawner;
            PendingFleetAction fleet = st.PendingFleets[fi];
            prefab = _spawner.GridPrefabId(eid) ?? prefab;
            _waveScratch.Clear();
            _waveScratch.Add(eid);
            BankFightDamage(vanilla, ref fleet, _waveScratch);
            _spawner.ReleaseControl(eid);
            WithholdWreck(eid, fleet, now);
            if (onField) fleet = DetachWaveGrid(fleet, eid);
            DisabledHull.WriteOffHull(ref fleet, slot, prefab, _world.PrefabCatalogue);
            st.PendingFleets[fi] = fleet;
            Dictionary<long, int> slots;
            if (fleet.FleetId != null && _waveSlots.TryGetValue(fleet.FleetId, out slots)) slots.Remove(eid);
            WaveSite site = SiteOf(fleet);
            if (site != null && onField) site.Site.Exit(fleet.FleetId, slot, FightExitCause.Disabled, now);
            WaveSlotRemoved(fleet, slot, now);

            long by;
            ulong told = ConfirmKill(fleet, prefab, WhereFought(fleet, now), attackerGrid, out by);
            _log.Write("FIGHT_HULL_LOST " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " prefab=" + (prefab ?? "?") + " cause=disabled at=" + at
                       + " last_damage=" + hitUtc.ToString("HH:mm:ss")
                       + " shotBy=" + attackerGrid + " by=" + by + " told=" + told
                       + " wreck_for=" + (int)DisabledHull.DespawnDelay.TotalSeconds + "s"
                       + " lead=" + fleet.MaterialisedEntityId
                       + (site != null ? " " + SiteCounts(site) : ""));
        }

        /// <summary>A hull leaving the field is lost when it is shot out of action on the way, or when its
        /// leave times out without its having moved a quarter of the way clear and hostile fire hit it
        /// since it left the field: a wreck that cannot fly is not a ship withdrawing.</summary>
        private bool LeavingHullLost(long eid, WaveLeave l, Vec3? pos, bool timedOut, DateTime now)
        {
            int si, fi;
            if (l.Slot < 0 || !FindFleet(l.FleetId, out si, out fi)) return false;
            if (WaveHullShotOutOfAction(_states[si], fi, eid, l.Slot, null, false, "leaving", now)) return true;
            if (!timedOut || !pos.HasValue || !l.StartKm.HasValue) return false;
            if (pos.Value.DistanceTo(l.StartKm.Value) * 1000.0 >= FightWaves.LeaveClearM / 4) return false;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return false;
            long attackerGrid;
            DateTime? hitUtc = vanilla.LastHostileHit(eid, l.ActorTag, out attackerGrid);
            if (!hitUtc.HasValue || hitUtc.Value < l.StartedUtc - DisabledHull.HostileHitWindow) return false;
            WriteOffWaveHull(_states[si], fi, eid, l.Slot, null, false, "stuck", attackerGrid, hitUtc.Value, now);
            return true;
        }

        private void CloseWaveSite(WaveSite site, DateTime now, string why)
        {
            foreach (string id in new List<string>(site.Fleets)) LeaveWaveSite(site, id);
            for (int i = _waveSpawns.Count - 1; i >= 0; i--)
                if (_waveSpawns[i].SiteId == site.Site.Id) _waveSpawns.RemoveAt(i);
            _waveSites.Remove(site.RaidFleetId);
            _world.WavedMeetings.Remove(site.RaidFleetId);
            _log.Write("FIGHT_SITE_CLOSED [" + site.RaidFleetId + "] @" + (site.NodeId ?? "?") + " why=" + why
                       + " lasted=" + (now - site.Site.OpenedUtc).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s");
        }

        private static string SiteCounts(WaveSite site)
        {
            var parts = new List<string>();
            foreach (string side in site.Site.Sides)
                parts.Add(side + "=" + site.Site.OnField(side) + "/" + site.Site.Queued(side)
                          + (site.Site.Lost(side) > 0 ? " lost=" + site.Site.Lost(side) : ""));
            return "field/queue " + string.Join(" ", parts);
        }

        // ---- closed for combat (FAC-384) --------------------------------------------------------

        /// <summary>Every live site closes its node and each node within <see cref="FightDebris.RadiusKm"/>
        /// of it; a node no site renews reopens <see cref="ReopenGraceSeconds"/> later.</summary>
        private void UpdateClosedNodes(DateTime now)
        {
            DateTime until = now.AddSeconds(ReopenGraceSeconds);
            foreach (WaveSite site in _waveSites.Values)
            {
                foreach (SpatialNode node in _world.Graph.Nodes.Values)
                {
                    if (!string.Equals(node.Id, site.NodeId, StringComparison.Ordinal)
                        && node.Position.DistanceTo(site.PosKm) > FightDebris.RadiusKm) continue;
                    _closedUntil[node.Id] = until;
                    if (_world.ClosedForCombat.Add(node.Id))
                        _log.Write("FIGHT_NODE_CLOSED " + node.Id + " by=[" + site.RaidFleetId + "]");
                }
            }
            if (_closedUntil.Count == 0) return;
            List<string> reopen = null;
            foreach (KeyValuePair<string, DateTime> kv in _closedUntil)
                if (kv.Value <= now) (reopen ?? (reopen = new List<string>())).Add(kv.Key);
            if (reopen == null) return;
            foreach (string id in reopen)
            {
                _closedUntil.Remove(id);
                _world.ClosedForCombat.Remove(id);
                _log.Write("FIGHT_NODE_REOPENED " + id);
            }
            if (_world.ClosedForCombat.Count == 0) { _combatDivertTried.Clear(); _combatHeldAt.Clear(); }
        }

        /// <summary>Civilian traffic not yet in the world keeps out of a fight: a fleet bound for a closed
        /// node is diverted once to the nearest node of the same owner that is open, and one with nowhere
        /// to go — or one standing at a fight — is held where it is until the node reopens. A fleet
        /// traffic control has queued is left to the queue, which holds and diverts it the same way.</summary>
        private void KeepTrafficClear(DateTime now)
        {
            TrafficPresence.Plan plan = _presencePlan;
            foreach (FactionState st in _states)
            {
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction fleet = st.PendingFleets[fi];
                    if (fleet.IsBench || fleet.FleetId == null || fleet.MaterialisedEntityId != 0
                        || !FactionsLoadActuator.IsTraffic(fleet.Kind)) continue;
                    if (plan != null && plan.Queued.Contains(fleet.FleetId)) continue;

                    bool bound = fleet.TargetNodeId != null && _world.ClosedForCombat.Contains(fleet.TargetNodeId);
                    if (bound && _combatDivertTried.Add(fleet.FleetId) && TryCombatDivert(st, fi, fleet, now)) continue;

                    Vec3 pos = fleet.CurrentPosKm(now, _tuning);
                    bool atFight = false;
                    foreach (WaveSite site in _waveSites.Values)
                        if (pos.DistanceTo(site.PosKm) <= FightDebris.RadiusKm) { atFight = true; break; }
                    bool near = bound && pos.DistanceTo(NodePosOr(fleet.TargetNodeId, fleet.TargetPosKm))
                                         <= _tuning.ObservationRadiusKm + 5.0;
                    if (!atFight && !near) { _combatHeldAt.Remove(fleet.FleetId); continue; }

                    DateTime last;
                    if (_combatHeldAt.TryGetValue(fleet.FleetId, out last))
                    {
                        TimeSpan gap = now - last;
                        if (gap > TimeSpan.Zero && gap.TotalSeconds <= CombatHoldTracker.GapSeconds)
                            st.PendingFleets[fi] = FactionsLoadActuator.Freeze(fleet, gap);
                    }
                    else _log.Write("TRAFFIC_HELD_FOR_COMBAT " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                                    + " @" + (fleet.TargetNodeId ?? "?") + (atFight ? " at_fight" : " bound"));
                    _combatHeldAt[fleet.FleetId] = now;
                }
            }
        }

        /// <summary>Sends a civilian fleet bound for a closed node to the nearest open node its
        /// destination's owner holds. Its stop is replaced, as traffic control's divert does.</summary>
        private bool TryCombatDivert(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            string owner = fleet.TargetTag ?? fleet.ActorTag;
            if (string.IsNullOrEmpty(owner)) return false;
            Vec3 pos = fleet.CurrentPosKm(now, _tuning);
            SpatialNode best = null;
            double bestKm = double.MaxValue;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (!string.Equals(node.OwnerFactionTag, owner, StringComparison.Ordinal)) continue;
                if (_world.ClosedForCombat.Contains(node.Id) || _queuedStations.ContainsKey(node.Id)) continue;
                double d = pos.DistanceTo(node.Position);
                if (d < bestKm) { bestKm = d; best = node; }
            }
            if (best == null) return false;
            string from = fleet.TargetNodeId;
            var lines = new List<string>();
            bool ok = PendingFleetResolver.Retarget(st, fi, best.Id, PendingFleetResolver.FleetRetargetPolicy.Replace,
                                                   _world, _tuning, lines, "combat_divert");
            foreach (string line in lines) _log.Write(line);
            if (ok) _log.Write("TRAFFIC_DIVERTED_FOR_COMBAT " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                               + " from=" + (from ?? "?") + " to=" + best.Id);
            return ok;
        }

        // ---- the field ---------------------------------------------------------------------------

        /// <summary>The hull's integrity now: its grid's reading when it is on the field, else its slot.</summary>
        private double WaveIntegrity(FightHull h)
        {
            int si, fi;
            if (!FindFleet(h.FleetId, out si, out fi)) return 1.0;
            PendingFleetAction fleet = _states[si].PendingFleets[fi];
            long eid = WaveEidOf(h.FleetId, h.Member);
            var vanilla = _spawner as VanillaFleetSpawner;
            double? reading = eid > 0 && vanilla != null ? vanilla.ReadIntegrity(eid) : null;
            return reading ?? fleet.IntegrityOf(h.Member);
        }

        private long WaveEidOf(string fleetId, int slot)
        {
            Dictionary<long, int> slots;
            if (fleetId == null || !_waveSlots.TryGetValue(fleetId, out slots)) return 0;
            foreach (KeyValuePair<long, int> kv in slots) if (kv.Value == slot) return kv.Key;
            return 0;
        }

        /// <summary>Takes a hull off the field for a reason the site gave: its grid's integrity into its
        /// slot, the grid out of the fleet (a lead replaced by the next hull on the field), and the grid
        /// flown clear and closed. The hull stays the fleet's.</summary>
        private void TakeOffField(WaveSite site, FightHull hull, FightExitCause cause, DateTime now)
        {
            int si, fi;
            long eid = WaveEidOf(hull.FleetId, hull.Member);
            if (!FindFleet(hull.FleetId, out si, out fi) || eid <= 0)
            {
                site.Site.Exit(hull.FleetId, hull.Member, cause, now);
                return;
            }
            PendingFleetAction fleet = _states[si].PendingFleets[fi];
            // A hull below the retreat line may be a wreck rather than a ship withdrawing: shot out of
            // action, it is lost, not flown home.
            if (cause != FightExitCause.Destroyed
                && WaveHullShotOutOfAction(_states[si], fi, eid, hull.Member, hull.Prefab, true, "exit", now)) return;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla != null)
            {
                _waveScratch.Clear();
                _waveScratch.Add(eid);
                BankFightDamage(vanilla, ref fleet, _waveScratch);
                double? reading = vanilla.ReadIntegrity(eid);
                if (reading.HasValue) fleet.SetIntegrity(hull.Member, reading.Value);
                HullDamage.RescaleBands(ref fleet, _world.PrefabCatalogue);
            }
            fleet = DetachWaveGrid(fleet, eid);
            _states[si].PendingFleets[fi] = fleet;
            _waveSlots[hull.FleetId].Remove(eid);
            site.Site.Exit(hull.FleetId, hull.Member, cause, now);
            StartWaveLeave(site, eid, fleet, hull.Member, hull.CostMs, now);
            _log.Write("FIGHT_WAVE_EXIT " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " prefab=" + (hull.Prefab ?? "?") + " why=" + cause.ToString().ToLowerInvariant()
                       + " integrity=" + fleet.IntegrityOf(hull.Member).ToString("0.00", CultureInfo.InvariantCulture)
                       + " " + SiteCounts(site));
        }

        /// <summary>The fleet without grid <paramref name="eid"/>: an escort is dropped from the list; a
        /// lead is replaced by the first escort, or by nothing — the fleet then waits at its site with
        /// no grid, which the site, not the fleet's loss, decides.</summary>
        private PendingFleetAction DetachWaveGrid(PendingFleetAction fleet, long eid)
        {
            if (fleet.EscortEntityIds != null && fleet.EscortEntityIds.Remove(eid))
            {
                if (fleet.EscortEntityIds.Count == 0) fleet.EscortEntityIds = null;
                return fleet;
            }
            if (fleet.MaterialisedEntityId != eid) return fleet;
            _stalledSince.Remove(eid);
            _safezoneArrivedSince.Remove(eid);
            _engagementHeldSince.Remove(eid);
            _settleHeldReported.Remove(eid);
            if (fleet.EscortEntityIds != null && fleet.EscortEntityIds.Count > 0)
            {
                long next = fleet.EscortEntityIds[0];
                fleet.EscortEntityIds.RemoveAt(0);
                if (fleet.EscortEntityIds.Count == 0) fleet.EscortEntityIds = null;
                fleet.MaterialisedEntityId = next;
                _log.Write("FIGHT_LEAD_HANDOVER " + fleet.LogId + " " + fleet.ActorTag + " from=" + eid + " to=" + next);
            }
            else fleet.MaterialisedEntityId = 0;
            return fleet;
        }

        private void StartWaveLeave(WaveSite site, long eid, PendingFleetAction fleet, int slot, double costMs, DateTime now)
        {
            Vec3? startKm = _spawner.GetGridPositionKm(eid);
            var vanilla = _spawner as VanillaFleetSpawner;
            bool flying = vanilla != null && vanilla.LeaveFrom(eid, site.PosKm, FightWaves.LeaveClearM, "rotated");
            if (!flying)
            {
                Vec3? pos = _spawner.GetGridPositionKm(eid);
                Vec3 from = pos.HasValue ? pos.Value : site.PosKm;
                flying = _spawner.Retarget(eid, AwayFrom(site.PosKm, from, FightWaves.LeaveClearM * 2.5 / 1000.0));
            }
            if (!flying)
            {
                _spawner.Despawn(eid);
                return;
            }
            _waveLeaving[eid] = new WaveLeave
            {
                SiteId = site.Site.Id,
                Fleet = fleet.LogId,
                FromKm = site.PosKm,
                CostMs = costMs,
                GiveUpUtc = now.AddSeconds(FightWaves.LeaveTimeoutSeconds),
                FleetId = fleet.FleetId,
                Slot = slot,
                ActorTag = fleet.ActorTag,
                StartKm = startKm,
                StartedUtc = now,
            };
        }

        private static Vec3 AwayFrom(Vec3 anchorKm, Vec3 ownKm, double km)
        {
            double dx = ownKm.X - anchorKm.X, dy = ownKm.Y - anchorKm.Y, dz = ownKm.Z - anchorKm.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) { dx = 0; dy = 1; dz = 0; len = 1; }
            return new Vec3(ownKm.X + dx / len * km, ownKm.Y + dy / len * km, ownKm.Z + dz / len * km);
        }

        /// <summary>Every pass: a hull leaving the field is closed once it is
        /// <see cref="FightWaves.LeaveClearM"/> from its site, out of every player's sight, or out of time.</summary>
        private void SweepWaveLeaving(DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            if (_waveLeaving.Count == 0) return;
            List<long> done = null;
            foreach (KeyValuePair<long, WaveLeave> kv in _waveLeaving)
            {
                long eid = kv.Key;
                WaveLeave l = kv.Value;
                if (!_spawner.GridExists(eid)) { (done ?? (done = new List<long>())).Add(eid); continue; }
                Vec3? pos = _spawner.GetGridPositionKm(eid);
                bool clear = pos.HasValue && pos.Value.DistanceTo(l.FromKm) * 1000.0 >= FightWaves.LeaveClearM;
                bool seen = pos.HasValue && isPlayerWithin != null && isPlayerWithin(pos.Value, _tuning.ObservationRadiusKm);
                bool timedOut = !clear && seen && now >= l.GiveUpUtc;
                if (LeavingHullLost(eid, l, pos, timedOut, now)) { (done ?? (done = new List<long>())).Add(eid); continue; }
                if (!clear && seen && now < l.GiveUpUtc) continue;
                (done ?? (done = new List<long>())).Add(eid);
                int closed = _spawner.Despawn(eid);
                _log.Write("FIGHT_WAVE_LEFT " + l.Fleet + " eid=" + eid + " closed=" + closed
                           + " reason=" + (clear ? "clear" : !seen ? "out_of_sight" : "timeout"));
            }
            if (done != null) foreach (long eid in done) _waveLeaving.Remove(eid);
        }

        // ---- spawning ----------------------------------------------------------------------------

        private bool HasPendingWaveSpawn(string siteId)
        {
            for (int i = 0; i < _waveSpawns.Count; i++) if (_waveSpawns[i].SiteId == siteId) return true;
            return false;
        }

        /// <summary>One sub-squadron spawn per fleet with hulls cleared this review, placed at the
        /// meeting's bracket from its opponent as a fleet joining a fight always is.</summary>
        private void StartWaveSpawns(WaveSite site, List<FightHull> cleared, DateTime now)
        {
            var byFleet = new Dictionary<string, List<FightHull>>(StringComparer.Ordinal);
            foreach (FightHull h in cleared)
            {
                List<FightHull> list;
                if (!byFleet.TryGetValue(h.FleetId, out list)) byFleet[h.FleetId] = list = new List<FightHull>();
                list.Add(h);
            }
            foreach (KeyValuePair<string, List<FightHull>> kv in byFleet)
            {
                int si, fi;
                if (!FindFleet(kv.Key, out si, out fi))
                {
                    foreach (FightHull h in kv.Value) site.Site.Exit(h.FleetId, h.Member, FightExitCause.Rotated, now);
                    continue;
                }
                PendingFleetAction fleet = PlaceInMeeting(_states[si], fi, _states[si].PendingFleets[fi], now);
                string key = FleetKey(fleet);
                Vec3 pos;
                if (!_meetingSpawnAt.TryGetValue(key, out pos)) pos = fleet.CurrentPosKm(now, _tuning);
                _meetingSpawnAt.Remove(key);
                var spawn = new WaveSpawn
                {
                    SiteId = site.Site.Id,
                    FleetId = kv.Key,
                    Key = key + "#w" + (++site.WaveSeq).ToString(CultureInfo.InvariantCulture),
                    Prefabs = new List<string>(),
                    Slots = new List<int>(),
                    PosKm = pos,
                    StartedUtc = now,
                };
                foreach (FightHull h in kv.Value) { spawn.Prefabs.Add(h.Prefab); spawn.Slots.Add(h.Member); }
                _waveSpawns.Add(spawn);
            }
        }

        /// <summary>Every pass: polls each sub-squadron spawn, and attaches what landed to its fleet — as
        /// its lead when it has none, else as escorts — each grid with the slot it was cleared for.</summary>
        private void PollWaveSpawns(DateTime now)
        {
            for (int i = _waveSpawns.Count - 1; i >= 0; i--)
            {
                WaveSpawn w = _waveSpawns[i];
                WaveSite site;
                int si, fi;
                if (!_waveSites.TryGetValue(w.SiteId, out site) || !FindFleet(w.FleetId, out si, out fi))
                {
                    _waveSpawns.RemoveAt(i);
                    continue;
                }
                PendingFleetAction fleet = _states[si].PendingFleets[fi];
                _escortScratch.Clear();
                long lead = _spawner.SpawnSquadron(w.Key, fleet.Kind, fleet.ActorTag, _states[si].FactionId, w.PosKm,
                                                   fleet.TargetPosKm, w.Prefabs, _escortScratch);
                if (lead <= 0)
                {
                    if ((now - w.StartedUtc).TotalSeconds < SpawnInFlightTimeout.TotalSeconds * Math.Max(1, w.Prefabs.Count)) continue;
                    _waveSpawns.RemoveAt(i);
                    // A hull that would not load would be cleared and fail again for as long as a
                    // player watches, holding the whole fight frozen: it leaves the fight instead.
                    for (int k = 0; k < w.Slots.Count; k++)
                        if (w.Slots[k] >= 0) site.Site.Exit(w.FleetId, w.Slots[k], FightExitCause.Retreated, now);
                    _log.Write("FIGHT_WAVE_TIMEOUT " + fleet.LogId + " " + fleet.ActorTag + " key=" + w.Key + " hulls=" + w.Prefabs.Count);
                    continue;
                }
                _waveSpawns.RemoveAt(i);

                var grids = new List<long> { lead };
                grids.AddRange(_escortScratch);
                var used = new bool[w.Prefabs.Count];
                Dictionary<long, int> slots = _waveSlots.ContainsKey(w.FleetId) ? _waveSlots[w.FleetId] : (_waveSlots[w.FleetId] = new Dictionary<long, int>());
                var vanilla = _spawner as VanillaFleetSpawner;
                foreach (long eid in grids)
                {
                    string prefab = _spawner.GridPrefabId(eid);
                    int k = -1;
                    for (int j = 0; j < w.Prefabs.Count; j++)
                        if (!used[j] && string.Equals(w.Prefabs[j], prefab, StringComparison.Ordinal)) { k = j; break; }
                    if (k < 0) for (int j = 0; j < w.Prefabs.Count; j++) if (!used[j]) { k = j; break; }
                    if (k < 0) continue;
                    used[k] = true;
                    if (w.Slots[k] < 0 || fleet.MaterialisedEntityId < 0) { _spawner.Despawn(eid); continue; }
                    slots[eid] = w.Slots[k];
                    if (fleet.MaterialisedEntityId <= 0) fleet.MaterialisedEntityId = eid;
                    else (fleet.EscortEntityIds ?? (fleet.EscortEntityIds = new List<long>())).Add(eid);
                    if (vanilla != null) vanilla.WatchIntegrity(eid, fleet.IntegrityOf(w.Slots[k]));
                    _spawnSettle.Arm(eid, now, SpawnSettleGrace);
                    NoteSpawned(eid, now);
                }
                // A hull that did not land leaves the fight, as one that timed out does.
                for (int k = 0; k < used.Length; k++)
                    if (!used[k] && w.Slots[k] >= 0) site.Site.Exit(w.FleetId, w.Slots[k], FightExitCause.Retreated, now);
                _states[si].PendingFleets[fi] = fleet;
                _log.Write("FIGHT_WAVE_SPAWNED " + fleet.LogId + " " + fleet.ActorTag + " key=" + w.Key
                           + " hulls=" + grids.Count + "/" + w.Prefabs.Count + " " + SiteCounts(site));
            }
        }

        // ---- losses reported by the ordinary paths ----------------------------------------------

        /// <summary>Whether <paramref name="fleet"/>'s grids carry explicit slots — while it is at a site,
        /// and after, until its grids leave.</summary>
        private bool HasWaveSlots(PendingFleetAction fleet)
        {
            return fleet.FleetId != null && _waveSlots.ContainsKey(fleet.FleetId);
        }

        /// <summary>Whether <paramref name="fleet"/> is flown by a live site.</summary>
        private bool IsWaved(PendingFleetAction fleet)
        {
            return fleet.FleetId != null && _waveSiteOfFleet.ContainsKey(fleet.FleetId);
        }

        private WaveSite SiteOf(PendingFleetAction fleet)
        {
            string id;
            WaveSite site;
            return fleet.FleetId != null && _waveSiteOfFleet.TryGetValue(fleet.FleetId, out id)
                   && _waveSites.TryGetValue(id, out site) ? site : null;
        }

        /// <summary>The waved fleet's slot <paramref name="slot"/> was removed (a hull lost): the site and
        /// the grid map renumber past it. Call after <see cref="PendingFleetAction.MarkHullLostAt"/>.</summary>
        private void WaveSlotRemoved(PendingFleetAction fleet, int slot, DateTime now)
        {
            if (fleet.FleetId == null || slot < 0) return;
            WaveSite site = SiteOf(fleet);
            if (site != null)
            {
                site.Site.Exit(fleet.FleetId, slot, FightExitCause.Destroyed, now);
                site.Site.OnSlotRemoved(fleet.FleetId, slot);
            }
            // A hull leaving the field keeps its slot until it is gone.
            foreach (WaveLeave l in _waveLeaving.Values)
            {
                if (!string.Equals(l.FleetId, fleet.FleetId, StringComparison.Ordinal)) continue;
                if (l.Slot == slot) l.Slot = -1;
                else if (l.Slot > slot) l.Slot--;
            }
            // A wave still landing names slots too.
            foreach (WaveSpawn w in _waveSpawns)
            {
                if (!string.Equals(w.FleetId, fleet.FleetId, StringComparison.Ordinal)) continue;
                for (int k = w.Slots.Count - 1; k >= 0; k--)
                {
                    // The prefab list is the spawner's key state and must not change under it: a hull
                    // lost meanwhile is marked dropped, and its grid, if it lands, is closed.
                    if (w.Slots[k] == slot) w.Slots[k] = -1;
                    else if (w.Slots[k] > slot) w.Slots[k]--;
                }
            }
            Dictionary<long, int> slots;
            if (!_waveSlots.TryGetValue(fleet.FleetId, out slots)) return;
            var keys = new List<long>(slots.Keys);
            foreach (long eid in keys)
            {
                int s = slots[eid];
                if (s == slot) slots.Remove(eid);
                else if (s > slot) slots[eid] = s - 1;
            }
        }

        /// <summary>A waved fleet's grid left without being lost (closed, vanished): its hull goes back
        /// to the queue.</summary>
        private void WaveGridGone(PendingFleetAction fleet, long eid, DateTime now)
        {
            Dictionary<long, int> slots;
            if (fleet.FleetId == null || !_waveSlots.TryGetValue(fleet.FleetId, out slots)) return;
            int slot;
            if (!slots.TryGetValue(eid, out slot)) return;
            slots.Remove(eid);
            WaveSite site = SiteOf(fleet);
            if (site != null) site.Site.Exit(fleet.FleetId, slot, FightExitCause.Rotated, now);
        }

        /// <summary>The lead of a waved fleet is gone. Its hull is settled — written off when it went
        /// down in combat — and the next hull on the field leads; the fleet's mission does not hang on
        /// it while its site stands. Answers whether the site handled it; the caller then skips the
        /// ordinary lead-loss path.</summary>
        private bool WaveLeadGone(FactionState st, int fi, PendingFleetAction fleet, long goneEid, bool destroyed,
                                  string prefab, string cause, DateTime now)
        {
            if (!IsWaved(fleet)) return false;
            fleet = SweepEscorts(st, fi, fleet, now);
            int slot;
            Dictionary<long, int> slots;
            if (!_waveSlots.TryGetValue(fleet.FleetId, out slots) || !slots.TryGetValue(goneEid, out slot)) slot = -1;
            fleet = SettleHulls(fleet, goneEid, cause, destroyed ? goneEid : 0, prefab);
            _spawner.ReleaseControl(goneEid);
            fleet = DetachWaveGrid(fleet, goneEid);
            st.PendingFleets[fi] = fleet;
            if (destroyed && slot >= 0) WaveSlotRemoved(fleet, slot, now);
            else WaveGridGone(fleet, goneEid, now);
            _log.Write("FIGHT_HULL_" + (destroyed ? "LOST" : "GONE") + " " + fleet.LogId + " " + fleet.ActorTag
                       + " eid=" + goneEid + " prefab=" + (prefab ?? "?") + " cause=" + cause
                       + " lead=" + fleet.MaterialisedEntityId);
            return true;
        }

        /// <summary>Drops grid maps of fleets no longer in the world: a map outlives its site so the
        /// grids still flying after a site closes keep their slots until they leave.</summary>
        private void PruneWaveSlots()
        {
            if (_waveSlots.Count == 0) return;
            List<string> drop = null;
            foreach (KeyValuePair<string, Dictionary<long, int>> kv in _waveSlots)
            {
                if (_waveSiteOfFleet.ContainsKey(kv.Key)) continue;
                int si, fi;
                bool live = FindFleet(kv.Key, out si, out fi)
                            && (_states[si].PendingFleets[fi].MaterialisedEntityId > 0
                                || _states[si].PendingFleets[fi].EscortEntityIds != null);
                if (!live) (drop ?? (drop = new List<string>())).Add(kv.Key);
            }
            if (drop != null) foreach (string id in drop) _waveSlots.Remove(id);
        }

        /// <summary>The slot <paramref name="eid"/> flies in a waved fleet, or -1 when it carries no
        /// explicit one.</summary>
        private int WaveSlotOf(PendingFleetAction fleet, long eid)
        {
            Dictionary<long, int> slots;
            int slot;
            return fleet.FleetId != null && _waveSlots.TryGetValue(fleet.FleetId, out slots)
                   && slots.TryGetValue(eid, out slot) && slot < fleet.HullSlotCount ? slot : -1;
        }

        private bool FindFleet(string fleetId, out int si, out int fi)
        {
            si = fi = -1;
            if (fleetId == null) return false;
            for (int s = 0; s < _states.Count; s++)
            {
                List<PendingFleetAction> fleets = _states[s].PendingFleets;
                for (int f = 0; f < fleets.Count; f++)
                    if (string.Equals(fleets[f].FleetId, fleetId, StringComparison.Ordinal)) { si = s; fi = f; return true; }
            }
            return false;
        }
    }
}
