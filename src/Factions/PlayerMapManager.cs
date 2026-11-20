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

        private readonly ViewerRelations _relations = new ViewerRelations();
        private readonly PlayerScannerManager _scanner;
        /// <summary>Reused across nodes within one viewer to avoid an allocation per node.</summary>
        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>(2);
        /// <summary>The viewer's sensors -- their own grid plus their faction's estate -- rebuilt once
        /// per viewer per tick rather than per fleet. An estate can be hundreds of nodes and every
        /// fleet would otherwise walk it again.</summary>
        private readonly List<SensorSource> _viewerSensors = new List<SensorSource>();
        /// <summary>Nodes under attack or unprotected this tick, collected once for every viewer
        /// rather than per subscriber: the graph walk is the expensive half and the answer does not
        /// depend on who is looking. Normally empty, which is what makes the whole feature free
        /// when nothing is happening.</summary>
        private readonly List<SpatialNode> _alarmNodes = new List<SpatialNode>();
        /// <summary>The alarm rows for the viewer currently being served.</summary>
        private readonly List<WireMapAlarm> _alarms = new List<WireMapAlarm>();
        private int _tick;

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
                nodes.Add(new WireMapNode
                {
                    Id = node.Id,
                    DisplayName = node.DisplayName,
                    DominantOre = NodeDisplay.DominantOre(node),
                    Surveyed = NodeDisplay.Surveyed(node),
                    CapabilityMask = NodeDisplay.CapabilityMask(node),
                    Zone = (byte)node.Zone,
                    OwnerTag = claimable ? null : ownerTag,
                    Relation = (byte)_relations.For(ownerTag, viewerIdentityId, claimable),
                    HasStation = node.StableStationId != 0,
                    HasInfrastructure = node.StableStationId != 0 && HasAnyCapability(node),
                    XKm = node.Position.X,
                    YKm = node.Position.Y,
                    ZKm = node.Position.Z,
                    RadiusKm = node.Radius,
                    HoldStrength = belief.HoldStrength,
                    Knowledge = (byte)knowledge,
                });

                if (!claimable && !string.IsNullOrEmpty(ownerTag))
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
                                     + " remembered=" + remembered);
            }
        }

        private void SendFleets(ulong steamId, long viewerIdentityId, List<MapFleetInfo> fleets)
        {
            string viewerFactionTag = ResolveViewerFactionTag(viewerIdentityId);
            BuildViewerSensors(viewerIdentityId, viewerFactionTag);
            var wire = new List<WireMapFleet>(fleets.Count);
            foreach (MapFleetInfo fleet in fleets)
            {
                if (!IsVisibleTo(viewerFactionTag, fleet)) continue;

                WireMapStop[] stops = null;
                if (fleet.RoutedStops != null && fleet.RoutedStops.Count > 0)
                {
                    stops = new WireMapStop[fleet.RoutedStops.Count];
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
                }

                wire.Add(new WireMapFleet
                {
                    Key = fleet.Key,
                    Kind = (byte)fleet.Kind,
                    OwnerTag = fleet.OwnerTag,
                    Relation = (byte)_relations.For(fleet.OwnerTag, viewerIdentityId),
                    XKm = fleet.PositionKm.X,
                    YKm = fleet.PositionKm.Y,
                    ZKm = fleet.PositionKm.Z,
                    VelXKmPerMin = fleet.VelocityKmPerMin.X,
                    VelYKmPerMin = fleet.VelocityKmPerMin.Y,
                    VelZKmPerMin = fleet.VelocityKmPerMin.Z,
                    TargetXKm = fleet.TargetPosKm.X,
                    TargetYKm = fleet.TargetPosKm.Y,
                    TargetZKm = fleet.TargetPosKm.Z,
                    EtaUtcTicks = fleet.EstimatedArrivalUtc.Ticks,
                    ConvoySize = fleet.ConvoySize,
                    EntityId = fleet.MaterialisedEntityId,
                    RoutedStops = stops,
                });
            }

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
        /// </summary>
        private bool IsVisibleTo(string viewerFactionTag, MapFleetInfo fleet)
        {
            if (!string.IsNullOrEmpty(viewerFactionTag) && fleet.OwnerTag == viewerFactionTag) return true;
            return FleetSensing.IsDetectedBy(_viewerSensors, fleet.PositionKm, fleet.Signature,
                                             fleet.BroadcastRadiusKm, _runtime.SensorOcclusion);
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
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _owesWorld.Clear();
            _relations.Clear();
        }
    }
}
