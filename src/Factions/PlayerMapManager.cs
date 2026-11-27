using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;


namespace Factions
{
    /// <summary>
    /// Server side of the in-game map window. A linked client subscribes when it opens the map and
    /// unsubscribes when it closes it, so an idle client costs nothing; while subscribed it gets the
    /// slow half of the world on a lazy cadence and fleets on the scanner cadence.
    ///
    /// Both payloads are full state rather than deltas. The sets are small, and it sidesteps the
    /// stranded-contact class of bug that forced the scanner to grow a reset flag: there is no
    /// per-client tracking state here to fall out of sync.
    /// </summary>
    internal sealed class PlayerMapManager
    {
        /// <summary>Fleet sends happen every tick; the world every this-many. At the scanner's
        /// ~5 s cadence that is roughly a minute, which is far faster than nodes change hands.</summary>
        private const int WorldTicksPerFleetTick = 12;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the map window open. A client that disconnects is dropped by
        /// <see cref="PruneOffline"/>; one that closes the map sends its own unsubscribe.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>Subscribers still owed a world send on the next tick, because they only just
        /// subscribed and should not wait out the lazy cadence to see anything.</summary>
        private readonly HashSet<ulong> _owesWorld = new HashSet<ulong>();

        private readonly ViewerRelations _relations = new ViewerRelations("map");
        private readonly PlayerScannerManager _scanner;
        /// <summary>Reused across nodes within one viewer to avoid an allocation per node.</summary>
        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>(2);
        /// <summary>The viewer's sensors -- their own grid plus their faction's estate -- rebuilt once
        /// per viewer per tick rather than per fleet. An estate can be hundreds of nodes and every
        /// fleet would otherwise walk it again.</summary>
        private readonly List<SensorSource> _viewerSensors = new List<SensorSource>();
        /// <summary>The tier each subscriber held for each fleet at the last send, for hysteresis.
        /// Rebuilt every send from the fleets actually sent, so a fleet that left the picture is
        /// forgotten with it.</summary>
        private readonly Dictionary<ulong, Dictionary<string, IntelTier>> _fleetTiers =
            new Dictionary<ulong, Dictionary<string, IntelTier>>();
        /// <summary>Nodes under attack or unprotected this tick, collected once for every viewer
        /// rather than per subscriber: the graph walk is the expensive half and the answer does not
        /// depend on who is looking. Normally empty, which is what makes the whole feature free
        /// when nothing is happening.</summary>
        private readonly List<SpatialNode> _alarmNodes = new List<SpatialNode>();
        /// <summary>The alarm rows for the viewer currently being served.</summary>
        private readonly List<WireMapAlarm> _alarms = new List<WireMapAlarm>();
        private int _tick;

        /// <summary>Grids the sim does not own, refreshed by the plugin before this tick (FAC-533).</summary>
        internal Func<List<ScannerGridCandidate>> Grids;

        public PlayerMapManager(FactionRuntime runtime, UplinkManager uplink, PlayerScannerManager scanner)
        {
            _runtime = runtime;
            _uplink = uplink;
            _scanner = scanner;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.MapRequest, OnMapRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnMapRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadMapRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Map request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                _owesWorld.Add(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("MAP_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _owesWorld.Remove(steamId);
                _fleetTiers.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("MAP_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence. Does nothing at all while no one has the map open,
        /// which is the normal case.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            bool worldDue = (_tick++ % WorldTicksPerFleetTick) == 0;
            List<MapFleetInfo> fleets = _runtime.GetMapFleets();
            CollectAlarmNodes();

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;

                long viewerIdentityId = player.IdentityId;
                // Standings are viewer-relative, so the cache is only valid within one player.
                _relations.Clear();

                bool worldNow = worldDue || _owesWorld.Contains(steamId);
                // One build per viewer per tick, serving both sends. The fleet half needs it only
                // when something is actually under attack, which is almost never.
                if (worldNow || _alarmNodes.Count > 0) BuildViewerSources(viewerIdentityId);

                if (worldNow)
                {
                    _owesWorld.Remove(steamId);
                    SendWorld(steamId, viewerIdentityId);
                }
                SendFleets(steamId, viewerIdentityId, fleets);
            }
        }

        /// <summary>
        /// Every node whose protection is doing something other than holding: under siege, breached,
        /// already raided, or knocked out by a landed raid and not yet back. The whole graph is
        /// walked, but only field tests are done per node, and the result is shared by every
        /// subscriber.
        /// </summary>
        private void CollectAlarmNodes()
        {
            _alarmNodes.Clear();
            DateTime nowUtc = _runtime.World.UtcNow;
            foreach (SpatialNode node in _runtime.World.Graph.Nodes.Values)
            {
                if (node == null || string.IsNullOrEmpty(node.Id)) continue;
                PlayerSiege.ZoneState state = PlayerSiege.StateOf(node, nowUtc);
                if (state == PlayerSiege.ZoneState.NoStation
                    || state == PlayerSiege.ZoneState.Protected) continue;
                _alarmNodes.Add(node);
            }
        }

        /// <summary>The alarm rows this viewer may see, through the same fog seam their nodes come
        /// through: a station being raided on the far side of the world is not news you get for
        /// free because somebody is shooting it.</summary>
        private void BuildAlarmsFor(string viewerFactionTag)
        {
            _alarms.Clear();
            if (_alarmNodes.Count == 0) return;

            DateTime nowUtc = _runtime.World.UtcNow;
            TimeSpan horizon = _runtime.World.Tuning.DiscoveryHorizon;
            double birthrightRange = _runtime.World.Tuning.BirthrightRangeKm;
            FactionTuning tuning = _runtime.World.Tuning;

            for (int i = 0; i < _alarmNodes.Count; i++)
            {
                SpatialNode node = _alarmNodes[i];
                WireKnowledge knowledge;
                NodeBelief belief;
                if (!IsVisibleTo(viewerFactionTag, node, nowUtc, horizon, birthrightRange,
                                 out knowledge, out belief))
                    continue;

                PlayerSiege.ZoneState state = PlayerSiege.StateOf(node, nowUtc);
                DateTime until;
                if (state == PlayerSiege.ZoneState.Besieged) until = PlayerSiege.BreachDueUtc(node, tuning);
                else if (state == PlayerSiege.ZoneState.Down) until = node.SafeZoneDownUntilUtc;
                else until = DateTime.MinValue;

                _alarms.Add(new WireMapAlarm
                {
                    NodeId = node.Id,
                    State = (byte)state,
                    ZoneIntegrity = PlayerSiege.IntegrityFraction(node, tuning, nowUtc),
                    RaidProgress = PlayerSiege.RaidProgressFraction(node, tuning),
                    UntilUtcTicks = until == DateTime.MinValue ? 0L : until.Ticks,
                });
            }
        }

        private void SendWorld(ulong steamId, long viewerIdentityId)
        {
            var nodes = new List<WireMapNode>();
            var nodeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int remembered = 0;

            DateTime nowUtc = _runtime.World.UtcNow;
            TimeSpan horizon = _runtime.World.Tuning.DiscoveryHorizon;
            double birthrightRange = _runtime.World.Tuning.BirthrightRangeKm;
            string viewerFactionTag = ResolveViewerFactionTag(viewerIdentityId);
            NodeStages stages = _runtime.NodeStages;
            var tiers = new int[(int)IntelTier.Full + 1];
            int defenceKnown = 0;

            foreach (SpatialNode node in _runtime.World.Graph.Nodes.Values)
            {
                if (node == null || string.IsNullOrEmpty(node.Id)) continue;
                WireKnowledge knowledge;
                NodeBelief belief;
                if (!IsVisibleTo(viewerFactionTag, node, nowUtc, horizon, birthrightRange,
                                 out knowledge, out belief))
                    continue;

                // Remembered for a node nobody is looking at, live for one under a sensor. The
                // belief is what the viewer may be wrong about; identity and position never are.
                if (!belief.IsLive) remembered++;
                string ownerTag = belief.OwnerTag;
                bool claimable = NeutralFaction.IsClaimable(ownerTag);
                NodeStage stage;
                DefenceBand defence;
                IntelTier tier = GradeNode(viewerFactionTag, node, knowledge, stages, nowUtc, horizon, out stage, out defence);
                if (defence != DefenceBand.Unknown) defenceKnown++;
                tiers[(int)tier]++;
                bool identified = IntelGrade.ShowsIdentity(tier);
                bool classed = IntelGrade.ShowsClass(tier);
                nodes.Add(new WireMapNode
                {
                    Id = node.Id,
                    Tier = (byte)tier,
                    Stage = (byte)stage,
                    Defence = (byte)defence,
                    DisplayName = identified ? node.DisplayName : null,
                    DominantOre = classed ? NodeDisplay.DominantOre(node) : null,
                    Surveyed = classed && NodeDisplay.Surveyed(node),
                    CapabilityMask = identified ? NodeDisplay.CapabilityMask(node) : (ushort)0,
                    Zone = classed ? (byte)node.Zone : (byte)0,
                    OwnerTag = identified && !claimable ? ownerTag : null,
                    Relation = (byte)(identified ? _relations.For(ownerTag, viewerIdentityId, claimable) : WireRelation.Unknown),
                    HasStation = classed && node.StableStationId != 0,
                    HasInfrastructure = classed && node.StableStationId != 0 && HasAnyCapability(node),
                    XKm = node.Position.X,
                    YKm = node.Position.Y,
                    ZKm = node.Position.Z,
                    RadiusKm = node.Radius,
                    HoldStrength = identified ? belief.HoldStrength : 0,
                    Knowledge = (byte)knowledge,
                });

                if (identified && !claimable && !string.IsNullOrEmpty(ownerTag))
                {
                    int count;
                    nodeCounts.TryGetValue(ownerTag, out count);
                    nodeCounts[ownerTag] = count + 1;
                }
            }

            var factions = new List<WireMapFaction>(nodeCounts.Count);
            foreach (KeyValuePair<string, int> pair in nodeCounts)
            {
                factions.Add(new WireMapFaction
                {
                    Tag = pair.Key,
                    Relation = (byte)_relations.For(pair.Key, viewerIdentityId),
                    NodeCount = pair.Value,
                });
            }

            _uplink.Send(steamId, FactionsWire.WriteMapWorld(nodes, factions));
            if (UplinkManager.DebugTraffic)
            {
                // remembered= is how many of those nodes carry a last-known owner rather than the
                // world's own. Zero on a viewer with detail-bearing entries means the map is reading
                // the graph past the memory again.
                _uplink.WriteTraffic("MAP_WORLD steam=" + steamId
                                     + " nodes=" + nodes.Count + " factions=" + factions.Count
                                     + " remembered=" + remembered
                                     + " contact=" + tiers[(int)IntelTier.Contact]
                                     + " class=" + tiers[(int)IntelTier.Class]
                                     + " identity=" + tiers[(int)IntelTier.Identity]
                                     + " armament=" + tiers[(int)IntelTier.Armament]
                                     + " full=" + tiers[(int)IntelTier.Full]
                                     + " capitals=" + stages.CapitalCount
                                     + " defence=" + defenceKnown);
            }
        }

        private void SendFleets(ulong steamId, long viewerIdentityId, List<MapFleetInfo> fleets)
        {
            string viewerFactionTag = ResolveViewerFactionTag(viewerIdentityId);
            BuildViewerSensors(viewerIdentityId, viewerFactionTag);
            FactionTuning tuning = _runtime.World.Tuning;
            Dictionary<string, IntelTier> held;
            _fleetTiers.TryGetValue(steamId, out held);
            var next = new Dictionary<string, IntelTier>(StringComparer.Ordinal);
            var wire = new List<WireMapFleet>(fleets.Count);
            foreach (MapFleetInfo fleet in fleets)
            {
                IntelTier tier;
                if (!TryGrade(viewerFactionTag, fleet, held, tuning, out tier)) continue;
                if (!string.IsNullOrEmpty(fleet.Key)) next[fleet.Key] = tier;
                if (tier == IntelTier.Armament && _scanner != null)
                    _scanner.RecordSighting(viewerIdentityId, fleet.OwnerTag, fleet.WeaponDps);

                WireRelation relation = _relations.For(fleet.OwnerTag, viewerIdentityId);
                bool identified = IntelGrade.ShowsIdentity(tier);
                bool classed = IntelGrade.ShowsClass(tier);
                bool plan = tier == IntelTier.Full || PlayerScannerManager.OffersFlightPlan(relation);

                var w = new WireMapFleet
                {
                    Key = fleet.Key,
                    Tier = (byte)tier,
                    Kind = classed ? (byte)fleet.Kind : (byte)0,
                    OwnerTag = identified ? fleet.OwnerTag : null,
                    Relation = (byte)(identified ? relation : WireRelation.Unknown),
                    XKm = fleet.PositionKm.X,
                    YKm = fleet.PositionKm.Y,
                    ZKm = fleet.PositionKm.Z,
                    VelXKmPerMin = fleet.VelocityKmPerMin.X,
                    VelYKmPerMin = fleet.VelocityKmPerMin.Y,
                    VelZKmPerMin = fleet.VelocityKmPerMin.Z,
                    ConvoySize = classed ? fleet.ConvoySize : 0,
                    EntityId = fleet.MaterialisedEntityId,
                    HasFlightPlan = plan,
                };
                if (plan)
                {
                    w.TargetXKm = fleet.TargetPosKm.X;
                    w.TargetYKm = fleet.TargetPosKm.Y;
                    w.TargetZKm = fleet.TargetPosKm.Z;
                    w.EtaUtcTicks = fleet.EstimatedArrivalUtc.Ticks;
                    w.RoutedStops = StopsOf(fleet);
                }
                wire.Add(w);
            }
            AppendGrids(wire, next, held, viewerIdentityId, viewerFactionTag, tuning);
            _fleetTiers[steamId] = next;

            BuildAlarmsFor(viewerFactionTag);
            _uplink.Send(steamId, FactionsWire.WriteMapFleets(wire, _alarms));
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("MAP_FLEETS steam=" + steamId + " fleets=" + wire.Count
                                     + " alarms=" + _alarms.Count);
            }
        }

        /// <summary>
        /// The fog-of-war seam, now three-state. Unknown is expressed by returning false -- the node
        /// never reaches the wire at all -- so the client is never handed something it has to
        /// remember not to draw, and no cheat surface is opened by the client's rendering choices.
        ///
        /// <para>A node past <see cref="FactionTuning.DiscoveryHorizon"/> falls back to Unknown, not
        /// to Discovered: an entry that has lapsed is forgotten, not merely stale. Dim means
        /// remembered; it does not mean expired.</para>
        ///
        /// <para><paramref name="belief"/> is what the viewer may be <i>wrong</i> about, and it is
        /// the difference between a dim marker and a dim marker that lies. Discovered carries the
        /// owner and hold of the last detection that captured them, through the same
        /// <see cref="KnowledgeGate"/> a brain reads; Detected carries the world's own, because a
        /// sensor is on it. A known node no detection has captured detail for — the birthright floor,
        /// a store written before snapshots existed — reads live and self-heals on the next
        /// detection, which is a store to fill rather than a null owner to draw.</para>
        /// </summary>
        private bool IsVisibleTo(
            string viewerFactionTag, SpatialNode node, DateTime nowUtc, TimeSpan horizon,
            double birthrightRangeKm, out WireKnowledge knowledge, out NodeBelief belief)
        {
            knowledge = WireKnowledge.Discovered;
            if (!KnowledgeGate.TryBelief(
                    _runtime.World.Graph, viewerFactionTag, _viewerSources, node,
                    nowUtc, horizon, birthrightRangeKm, out belief))
                return false;

            if (!ViewerKnowledge.IsDetected(_viewerSources, node.Id, nowUtc)) return true;

            // A sensor has it now, so the world itself is the honest answer and the memory it
            // renewed a moment ago would only lag it.
            knowledge = WireKnowledge.Detected;
            belief.OwnerTag = node.OwnerFactionTag;
            belief.HoldStrength = node.HoldStrength;
            belief.ResourceValue = node.ResourceValue;
            belief.IsLive = true;
            return true;
        }

        /// <summary>
        /// The viewer's grade for a node they know, and the stage they saw (docs/fog-of-war.md →
        /// Remembered grades). The best tier across their stores; their own estate is Full. The
        /// stage is the live one while a sensor has the node and the remembered one otherwise,
        /// falling back to live for an entry graded before stages were recorded. Shared with the
        /// node window so the marker and the window cannot disagree.
        ///
        /// <para>The defence band is the freshest remembered one, with its reading time, and live for
        /// the viewer's own estate (read through <paramref name="defenceOf"/>).</para>
        /// </summary>
        internal static IntelTier GradeNode(
            string viewerFactionTag, IList<FactionKnowledge> sources, SpatialNode node, bool detected,
            NodeStages stages, DateTime nowUtc, TimeSpan horizon, out NodeStage stage,
            Func<SpatialNode, DefenceReading> defenceOf, out DefenceBand defence, out DateTime defenceUtc)
        {
            KnownNode grade = KnowledgeView.GradeOf(viewerFactionTag, sources, node, nowUtc, horizon);
            stage = NodeStage.None;
            if (grade.Tier == IntelTier.Full && defenceOf != null)
            {
                defence = defenceOf(node).Band;
                defenceUtc = nowUtc;
            }
            else
            {
                defence = grade.Defence;
                defenceUtc = grade.DefenceUtc;
            }
            if (!IntelGrade.ShowsClass(grade.Tier)) return grade.Tier;
            stage = detected || grade.Stage == NodeStage.None
                ? (stages != null ? stages.Of(node) : NodeStage.None)
                : grade.Stage;
            return grade.Tier;
        }

        private IntelTier GradeNode(string viewerFactionTag, SpatialNode node, WireKnowledge knowledge,
                                    NodeStages stages, DateTime nowUtc, TimeSpan horizon, out NodeStage stage,
                                    out DefenceBand defence)
        {
            DateTime defenceUtc;
            return GradeNode(viewerFactionTag, _viewerSources, node, knowledge == WireKnowledge.Detected,
                             stages, nowUtc, horizon, out stage, _runtime.NodeDefence, out defence, out defenceUtc);
        }

        private void BuildViewerSources(long viewerIdentityId)
        {
            ViewerKnowledge.BuildSources(_runtime, _scanner, viewerIdentityId, _viewerSources);
        }

        private string ResolveViewerFactionTag(long viewerIdentityId)
        {
            return ViewerKnowledge.FactionTag(viewerIdentityId);
        }

        /// <summary>
        /// The fleet half of the fog seam. A fleet is <b>Detected or Unknown</b> -- there is no
        /// Discovered tier for one, because a remembered fleet has moved and the memory would be
        /// worthless. So it leaves the map rather than dimming, and nothing about it is remembered.
        ///
        /// <para>Unlike the node seam this cannot consult a store: <see cref="KnowledgeSweep"/>
        /// treats fleets as sensors and never as targets, so no fleet is ever written into a
        /// discovered set. The question is pure geometry and is asked fresh every send.</para>
        ///
        /// <para><b>Your own faction's convoys are always visible.</b> Ownership implies knowledge
        /// here for the same reason it does for nodes -- a faction that cannot see its own fleets
        /// would be told it had lost them every time they flew out of its own sensor envelope.</para>
        ///
        /// <para>A visible fleet is also graded: the best margin across the viewer's sensors, with
        /// hysteresis against <paramref name="held"/> and the same transponder rule as the HUD
        /// (<see cref="IntelGrade.Of"/>). Own convoys are <see cref="IntelTier.Full"/>.</para>
        /// </summary>
        private bool TryGrade(string viewerFactionTag, MapFleetInfo fleet,
                              Dictionary<string, IntelTier> held, FactionTuning tuning, out IntelTier tier)
        {
            bool own = !string.IsNullOrEmpty(viewerFactionTag) && fleet.OwnerTag == viewerFactionTag;
            SensorReading reading = own
                ? new SensorReading { Detected = true, SensorSees = true, MarginDb = IntelGrade.MaxMarginDb }
                : FleetSensing.Measure(_viewerSensors, fleet.PositionKm, fleet.Signature,
                                       fleet.BroadcastRadiusKm, _runtime.SensorOcclusion);
            IntelTier previous = IntelTier.None;
            if (held != null && !string.IsNullOrEmpty(fleet.Key)) held.TryGetValue(fleet.Key, out previous);
            bool transponder = IntelGrade.IsCivilianMission(fleet.Kind)
                || fleet.BroadcastRadiusKm > 0
                || StrikesViewer(fleet.EngagementTargetNodeId, viewerFactionTag);
            tier = IntelGrade.Of(reading, previous, own, transponder, tuning);
            return tier != IntelTier.None;
        }

        /// <summary>The grids this viewer's sensors find, after the fleets, by the scanner's rule and
        /// the scanner's label: one grid reads the same on the HUD and on the map. The grid being
        /// flown is left out, as on the HUD.</summary>
        private void AppendGrids(List<WireMapFleet> wire, Dictionary<string, IntelTier> next,
                                 Dictionary<string, IntelTier> held, long viewerIdentityId,
                                 string viewerFactionTag, FactionTuning tuning)
        {
            List<ScannerGridCandidate> grids = Grids != null ? Grids() : null;
            if (grids == null || grids.Count == 0) return;
            long flying = _scanner != null ? _scanner.ActiveGridLeadOf(viewerIdentityId) : 0;
            for (int i = 0; i < grids.Count; i++)
            {
                ScannerGridCandidate g = grids[i];
                if (g.EntityId == 0 || g.EntityId == flying) continue;
                bool own = g.OwnerIdentityId == viewerIdentityId
                           || (!string.IsNullOrEmpty(viewerFactionTag) && g.OwnerTag == viewerFactionTag);
                SensorReading reading = own
                    ? new SensorReading { Detected = true, SensorSees = true, MarginDb = IntelGrade.MaxMarginDb }
                    : FleetSensing.Measure(_viewerSensors, g.PositionKm, g.Signature > 0 ? g.Signature : 1.0,
                                           g.BroadcastRadiusKm, _runtime.SensorOcclusion);
                string key = ScannerGrids.KeyOf(g.EntityId);
                IntelTier previous = IntelTier.None;
                if (held != null) held.TryGetValue(key, out previous);
                IntelTier tier = IntelGrade.Of(reading, previous, own, g.BroadcastRadiusKm > 0, tuning);
                if (tier == IntelTier.None) continue;
                next[key] = tier;
                if (tier == IntelTier.Armament && _scanner != null && !string.IsNullOrEmpty(g.OwnerTag))
                    _scanner.RecordSighting(viewerIdentityId, g.OwnerTag, g.WeaponDps);

                ScannerContact c = ScannerGrids.ToContact(g, reading);
                c.Tier = tier;
                ScannerGrids.Withhold(ref c, tuning);
                wire.Add(new WireMapFleet
                {
                    Key = key,
                    Tier = (byte)tier,
                    OwnerTag = c.OwnerTag,
                    Relation = (byte)PlayerScannerManager.GridRelation(_relations, c.OwnerTag, c.OwnerIdentityId,
                                                                       viewerIdentityId),
                    XKm = g.PositionKm.X,
                    YKm = g.PositionKm.Y,
                    ZKm = g.PositionKm.Z,
                    VelXKmPerMin = c.VelocityKmPerMin.X,
                    VelYKmPerMin = c.VelocityKmPerMin.Y,
                    VelZKmPerMin = c.VelocityKmPerMin.Z,
                    EntityId = g.EntityId,
                    IsGrid = true,
                    Label = c.Label,
                    Name = c.DisplayName,
                });
            }
        }

        private bool StrikesViewer(string nodeId, string viewerFactionTag)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(viewerFactionTag)) return false;
            SpatialNode node;
            return _runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) && node != null
                && node.OwnerFactionTag == viewerFactionTag;
        }

        private static WireMapStop[] StopsOf(MapFleetInfo fleet)
        {
            if (fleet.RoutedStops == null || fleet.RoutedStops.Count == 0) return null;
            var stops = new WireMapStop[fleet.RoutedStops.Count];
            for (int i = 0; i < fleet.RoutedStops.Count; i++)
            {
                MapFleetStopInfo stop = fleet.RoutedStops[i];
                stops[i] = new WireMapStop
                {
                    XKm = stop.PositionKm.X,
                    YKm = stop.PositionKm.Y,
                    ZKm = stop.PositionKm.Z,
                    Kind = (byte)stop.Kind,
                };
            }
            return stops;
        }

        /// <summary>Everything this viewer sees through: the grid they are flying (the shipped
        /// scanner, which is this model's degenerate case at signature 1.0) and, if they are in a
        /// registered faction, that faction's estate including any installed
        /// <see cref="NodeCapabilityKind.SensorArray"/>.
        ///
        /// <para>A player on foot with no faction has no sensors and sees no fleets. That is the
        /// intended floor, not a gap -- the fog is only meaningful if it can be total.</para></summary>
        private void BuildViewerSensors(long viewerIdentityId, string viewerFactionTag)
        {
            _viewerSensors.Clear();
            SensorSource grid;
            if (_scanner != null && _scanner.TryGetGridSensor(viewerIdentityId, out grid))
                _viewerSensors.Add(grid);
            if (string.IsNullOrEmpty(viewerFactionTag)) return;
            FleetSensing.CollectNodeSensors(
                _runtime.World.Graph, viewerFactionTag, _runtime.World.Tuning, _viewerSensors);
        }

        /// <summary>Mirrors the API snapshot's notion of a station that is more than a bare marker:
        /// a scan-backed node counts once it has any capability installed.</summary>
        private static bool HasAnyCapability(SpatialNode node)
        {
            if (!SourceMapping.IsScanBacked(node.Id)) return true;
            return node.Capabilities.Count > 0;
        }

        /// <summary>Drops subscribers that are no longer online, on the same cadence and for the
        /// same reason <see cref="UplinkManager.PruneOffline"/> drops links.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
            List<ulong> stale = null;
            foreach (ulong steamId in _streaming)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _streaming.Remove(steamId);
                _owesWorld.Remove(steamId);
                _fleetTiers.Remove(steamId);
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _owesWorld.Clear();
            _fleetTiers.Clear();
            _relations.Clear();
        }
    }
}
