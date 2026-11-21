using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;

namespace Factions
{
    /// <summary>One <i>protect me</i> as it reached the server: from a client's toolbar press, a
    /// block the server runs, the bench, or the host itself for an NPC station under attack.</summary>
    internal struct StationCallRequest
    {
        public WingVia Via;
        public long AntennaEntityId;
        /// <summary>The presser (client, bench); zero for a block or the host.</summary>
        public long PresserIdentityId;
        /// <summary>The presser's SE faction tag as the server they pressed on has it; null to look it
        /// up here.</summary>
        public string PresserTag;
        public string BenchLabel;
    }

    /// <summary>
    /// A station calls for protection through its antenna (docs/fleet-tactics.md, "A station calls
    /// for protection"; rules in Core's <see cref="StationCallRules"/> / <see cref="StationCall"/>).
    ///
    /// <para><b>A call</b> — <i>Wing: protect me</i> pressed on a station's antenna, fired by a block,
    /// or raised by the host for a materialised NPC station under attack — does three things through
    /// machinery that already exists: a wing order anchored on the station
    /// (<see cref="WingOrder.StationGridId"/>) that the faction's armed hulls inside the antenna's
    /// radius answer, per hull, with whatever damaged the station as the target; a mark on the
    /// node (<see cref="WorldModel.SetStationCall"/>) that <c>FleetManager</c>'s reactive Guard
    /// dispatch answers; and, for a player faction, a Defend focus placed on the node or refreshed
    /// (<see cref="StationCallRules.PlaceOrRefreshFocus"/>). A repeat call refreshes all three.</para>
    ///
    /// <para><b>It ends</b> on antenna loss past the 10 s grace, on threat cleared, or on the
    /// focus's expiry, read once a second.</para>
    ///
    /// <para><b>The NPC side:</b> the host raises a materialised NPC station's call when it is hit, and a materialised NPC station
    /// with no working antenna gets no reactive Guard (<see cref="WorldModel.SetSilencedStations"/>).
    /// The player path starts from the player's own blocks and is not gated.</para>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private static readonly TimeSpan StationCallReadInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan StationAntennaCacheLife = TimeSpan.FromSeconds(30);

        private struct StationHit
        {
            public long AttackerGridId;
            public DateTime Utc;
        }

        private struct StationAntennaCache
        {
            public List<long> Antennas;
            public DateTime ReadUtc;
        }

        private readonly Dictionary<long, StationCall> _stationCalls = new Dictionary<long, StationCall>();
        private readonly Dictionary<long, StationHit> _stationHits = new Dictionary<long, StationHit>();
        private readonly HashSet<long> _stationWatch = new HashSet<long>();
        private readonly Dictionary<long, StationAntennaCache> _stationAntennas = new Dictionary<long, StationAntennaCache>();
        /// <summary>Silenced NPC stations whose refusal has been written for the current attack.</summary>
        private readonly HashSet<long> _stationRefusedLogged = new HashSet<long>();
        private readonly List<KeyValuePair<string, long>> _stationGridScratch = new List<KeyValuePair<string, long>>();
        private readonly List<string> _silencedScratch = new List<string>();
        private readonly List<long> _stationDropScratch = new List<long>();
        private DateTime _stationCallNextReadUtc;

        /// <summary>Whether a grid's hits are banked: a materialised station, or a station with a
        /// call standing.</summary>
        internal bool IsStationGridWatched(long gridEntityId)
        {
            return _stationWatch.Count > 0 && _stationWatch.Contains(gridEntityId);
        }

        /// <summary>A watched station grid was hit by <paramref name="attackerGridId"/>.</summary>
        internal void RecordStationAttacker(long stationGridId, long attackerGridId)
        {
            RecordStationAttacker(stationGridId, attackerGridId, DateTime.UtcNow);
        }

        /// <summary>A station grid was hit at <paramref name="hitUtc"/>: here, or on the Nexus agent
        /// relaying its call. A hit older than the one on record does not replace it.</summary>
        internal void RecordStationAttacker(long stationGridId, long attackerGridId, DateTime hitUtc)
        {
            if (attackerGridId == 0 || attackerGridId == stationGridId) return;
            StationHit held;
            if (_stationHits.TryGetValue(stationGridId, out held) && held.Utc > hitUtc) return;
            _stationHits[stationGridId] = new StationHit { AttackerGridId = attackerGridId, Utc = hitUtc };
        }

        /// <summary>Whether a station's call stands on the grid.</summary>
        internal bool HasStationCall(long stationGridId)
        {
            return _stationCalls.Count > 0 && _stationCalls.ContainsKey(stationGridId);
        }

        /// <summary>Whether the host raises the call of a station owned by <paramref name="tag"/>
        /// itself: an NPC faction this runtime runs (<see cref="StationCallRules.HostCalls"/>).</summary>
        internal bool HostCallsFor(string tag)
        {
            FactionState st = tag != null ? FindState(tag) : null;
            return st != null && StationCallRules.HostCalls(true, st.IsPlayerFaction);
        }

        /// <summary>An agent's NPC stations that cannot call, held as long as its reports keep
        /// naming them.</summary>
        private struct AgentMuted { public List<string> Nodes; public DateTime Utc; }
        private readonly Dictionary<byte, AgentMuted> _agentMuted = new Dictionary<byte, AgentMuted>();
        private static readonly TimeSpan AgentMutedLife = TimeSpan.FromSeconds(10);

        /// <summary>The NPC stations standing on agent <paramref name="home"/> with no working antenna,
        /// from its latest report; null or empty when it has none.</summary>
        internal void SetAgentMutedStations(byte home, List<string> nodeIds, DateTime nowUtc)
        {
            if (nodeIds == null || nodeIds.Count == 0) _agentMuted.Remove(home);
            else _agentMuted[home] = new AgentMuted { Nodes = nodeIds, Utc = nowUtc };
        }

        /// <summary>Takes one call: validates the antenna and the station, then starts the call or
        /// refreshes the one standing. Writes <c>STATION_CALL</c> or <c>STATION_CALL_REFUSED</c>;
        /// returns what to tell the presser.</summary>
        internal string CallStation(StationCallRequest req, out bool ok)
        {
            ok = false;
            DateTime now = DateTime.UtcNow;
            long gridId;
            WingAntennaReading antenna = WingWorld.ReadAntenna(req.AntennaEntityId, out gridId);
            if (!antenna.Exists) return RefuseStationCall(req, null, null, WingRefusal.NoAntenna);
            string tag = antenna.OwnerFactionTag;
            FactionState st = tag != null ? FindState(tag) : null;
            if (st == null || _world == null) return RefuseStationCall(req, tag, null, WingRefusal.NoFaction);
            if (req.PresserIdentityId != 0)
            {
                string presserTag = req.PresserTag ?? WingWorld.FactionTagOf(req.PresserIdentityId);
                if (!string.Equals(presserTag, st.Tag, StringComparison.OrdinalIgnoreCase))
                    return RefuseStationCall(req, st.Tag, null, WingRefusal.NotYourAntenna);
            }
            WingRefusal refusal = StationCallRules.CheckAntenna(antenna, st.Tag);
            if (refusal != WingRefusal.None) return RefuseStationCall(req, st.Tag, null, refusal);

            string nodeId = StationNodeOf(gridId, st.Tag, antenna.PosM, out refusal);
            if (nodeId == null) return RefuseStationCall(req, st.Tag, null, refusal);

            StationCall call;
            bool repeat = _stationCalls.TryGetValue(gridId, out call);
            if (!repeat)
            {
                call = new StationCall(now)
                {
                    NodeId = nodeId,
                    StationGridId = gridId,
                    FactionTag = st.Tag,
                    PlayerFaction = st.IsPlayerFaction,
                };
                _stationCalls[gridId] = call;
                _stationWatch.Add(gridId);
            }
            call.AntennaEntityId = req.AntennaEntityId;
            call.OwnerIdentityId = antenna.OwnerIdentityId;
            call.Via = req.Via;
            if (req.BenchLabel != null) call.BenchLabel = req.BenchLabel;
            bool logDue = !repeat || call.Repeat(now);

            string attackerFleetId;
            StationCallTargetSource source;
            call.TargetEntityId = ReadStationThreat(call, out source, out attackerFleetId);
            call.TargetSource = source;
            _world.SetStationCall(nodeId, attackerFleetId);

            HashSet<string> members = st.IsPlayerFaction
                ? _world.ComputeCampaignRegionMembers(st.Tag, nodeId, _tuning.CampaignRegionTraverseDepth)
                : null;
            CampaignFocus focus;
            OffensiveCampaigns.PlayerResult focusResult;
            StationCallFocus focusDone = StationCallRules.PlaceOrRefreshFocus(
                st, _tuning, _world.UtcNow, _world, nodeId, members, out focus, out focusResult);
            if (focusDone == StationCallFocus.Placed || focusDone == StationCallFocus.Refreshed) call.TracksFocus = true;
            if (focusDone == StationCallFocus.Placed) Flush();

            WingOrder order = StationOrder(call, now);
            ReadWingOrder(order, now);
            int obeying = 0;
            foreach (KeyValuePair<long, WingLink> kv in order.Links)
                if (!kv.Value.Lapsed) obeying++;

            ok = true;
            if (logDue)
            {
                int repeats = repeat ? call.TakeRepeats(now) : 0;
                _log.Write("STATION_CALL tag=" + st.Tag + " node=" + nodeId + " station=" + gridId
                           + " antenna=" + req.AntennaEntityId + " via=" + ViaToken(req.Via)
                           + " radius=" + antenna.RadiusM.ToString("0", CultureInfo.InvariantCulture)
                           + " wing=" + obeying
                           + " target=" + call.TargetEntityId + " source=" + StationCallRules.Token(source)
                           + (attackerFleetId != null ? " vs=" + attackerFleetId : "")
                           + " focus=" + StationCallRules.Token(focusDone)
                           + (focusDone == StationCallFocus.Refused ? "(" + focusResult + ")" : "")
                           + (repeat ? " repeat=" + repeats : "")
                           + BenchToken(call.BenchLabel));
            }
            return "Protect me: " + obeying + " hull(s) answering around " + nodeId
                   + (focusDone == StationCallFocus.Placed ? "; defensive focus placed."
                      : focusDone == StationCallFocus.Refreshed ? "; defensive focus refreshed." : ".");
        }

        private string RefuseStationCall(StationCallRequest req, string tag, string nodeId, WingRefusal refusal)
        {
            _log.Write("STATION_CALL_REFUSED tag=" + (tag ?? "none") + " node=" + (nodeId ?? "-")
                       + " antenna=" + req.AntennaEntityId + " via=" + ViaToken(req.Via)
                       + " reason=" + WingRules.Token(refusal) + BenchToken(req.BenchLabel));
            switch (refusal)
            {
                case WingRefusal.NotAStation: return "Protect me refused: the antenna is not on a station.";
                case WingRefusal.NoNode:      return "Protect me refused: no node of your faction answers for this station.";
                case WingRefusal.NoFaction:   return "Protect me refused: the station's faction is not a Factions faction.";
                default:                      return "Protect me refused: " + RefusalText(refusal);
            }
        }

        /// <summary>The node a station grid answers for: the node whose materialised station it is,
        /// else, for a static grid, the nearest node <paramref name="factionTag"/> owns within
        /// <see cref="StationCallRules.NodeReachM"/>.</summary>
        private string StationNodeOf(long gridId, string factionTag, Vec3 antennaPosM, out WingRefusal refusal)
        {
            refusal = WingRefusal.None;
            string nodeId = _stationMaterializer.NodeIdForStationGrid(gridId);
            if (nodeId != null)
            {
                SpatialNode n;
                if (_world.Graph.Nodes.TryGetValue(nodeId, out n)
                    && string.Equals(n.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase)) return nodeId;
                refusal = WingRefusal.NoNode;
                return null;
            }
            if (!WingWorld.IsStaticGrid(gridId)) { refusal = WingRefusal.NotAStation; return null; }
            var posKm = new Vec3(antennaPosM.X / 1000.0, antennaPosM.Y / 1000.0, antennaPosM.Z / 1000.0);
            double reachKm = StationCallRules.NodeReachM / 1000.0;
            string best = null;
            double bestD = double.MaxValue;
            foreach (SpatialNode n in _world.Graph.OwnedBy(factionTag))
            {
                double d = n.Position.DistanceTo(posKm);
                if (d <= reachKm && d < bestD) { bestD = d; best = n.Id; }
            }
            if (best == null) refusal = WingRefusal.NoNode;
            return best;
        }

        /// <summary>The call's wing order, created on the first call: anchored on the station, with
        /// the call's target.</summary>
        private WingOrder StationOrder(StationCall call, DateTime now)
        {
            WingOrder order;
            long key = -call.StationGridId;
            if (!_wingOrders.TryGetValue(key, out order))
            {
                order = new WingOrder
                {
                    CommanderIdentityId = call.OwnerIdentityId,
                    FactionTag = call.FactionTag,
                    StationGridId = call.StationGridId,
                };
                _wingOrders[key] = order;
            }
            order.AntennaEntityId = call.AntennaEntityId;
            order.CommanderIdentityId = call.OwnerIdentityId;
            order.Via = call.Via;
            order.Apply(WingCommand.FormOnMe, call.StationGridId, 0, now);
            order.TargetEntityId = call.TargetEntityId;

            // A hull answers the call that reached it last.
            long gridId;
            WingAntennaReading antenna = WingWorld.ReadAntenna(call.AntennaEntityId, out gridId);
            CollectWingHulls(call.FactionTag, null, true, _wingHullScratch);
            TakeHullsFromOtherOrders(order, antenna);
            return order;
        }

        /// <summary>What threatens the station now: the grid that last hit it (inside
        /// <see cref="StationCallRules.DamageMemory"/>), a combat AI block's lock, or the nearest
        /// hostile grid near it — each only while the station's owner is hostile to it. The fleet
        /// behind the target is named when it is one of ours.</summary>
        private long ReadStationThreat(StationCall call, out StationCallTargetSource source, out string attackerFleetId)
        {
            attackerFleetId = null;
            DateTime now = DateTime.UtcNow;
            long damager = 0;
            StationHit hit;
            if (_stationHits.TryGetValue(call.StationGridId, out hit) && now - hit.Utc <= StationCallRules.DamageMemory
                && HostileTo(hit.AttackerGridId, call.OwnerIdentityId))
                damager = hit.AttackerGridId;
            long combat = 0, hostileNear = 0;
            if (damager == 0)
            {
                long pilot, retained;
                WingWorld.GridLocks(call.StationGridId, out pilot, out combat, out retained);
                if (combat != 0 && !HostileTo(combat, call.OwnerIdentityId)) combat = 0;
                if (combat == 0)
                    hostileNear = WingWorld.NearestHostileGrid(call.StationGridId, call.OwnerIdentityId, StationCallRules.HostileNearM);
            }
            long target = StationCallRules.PickTarget(damager, combat, hostileNear, out source);
            if (target != 0)
            {
                FactionState st;
                int fi;
                if (FindFleetOfHull(target, out st, out fi)
                    && !string.Equals(st.Tag, call.FactionTag, StringComparison.OrdinalIgnoreCase))
                    attackerFleetId = st.PendingFleets[fi].FleetId;
            }
            return target;
        }

        private static bool HostileTo(long gridId, long ownerIdentityId)
        {
            WingTargetReading t = WingWorld.ReadTarget(gridId, ownerIdentityId);
            return t.Exists && t.Hostile;
        }

        /// <summary>The once-a-second station read: which grids' hits are banked, which NPC
        /// stations are silenced or raise their own call, and each standing call's end conditions.</summary>
        private void TickStationCalls(DateTime now)
        {
            if (_world == null || now < _stationCallNextReadUtc) return;
            _stationCallNextReadUtc = now + StationCallReadInterval;

            // Hits older than the memory name nobody.
            if (_stationHits.Count > 0)
            {
                _stationDropScratch.Clear();
                foreach (KeyValuePair<long, StationHit> kv in _stationHits)
                    if (now - kv.Value.Utc > StationCallRules.ThreatClearAfter) _stationDropScratch.Add(kv.Key);
                for (int i = 0; i < _stationDropScratch.Count; i++)
                {
                    _stationHits.Remove(_stationDropScratch[i]);
                    _stationRefusedLogged.Remove(_stationDropScratch[i]);
                }
            }

            _stationMaterializer.MaterializedStationGrids(_stationGridScratch);
            _stationWatch.Clear();
            for (int i = 0; i < _stationGridScratch.Count; i++) _stationWatch.Add(_stationGridScratch[i].Value);
            foreach (long grid in _stationCalls.Keys) _stationWatch.Add(grid);

            ReadNpcStations(now);

            if (_stationCalls.Count == 0) return;
            _stationDropScratch.Clear();
            foreach (KeyValuePair<long, StationCall> kv in _stationCalls)
            {
                StationCall call = kv.Value;
                bool exists = WingWorld.GridExists(call.StationGridId);
                WingRefusal antenna = exists ? StationAntenna(call) : WingRefusal.NoAntenna;
                string attackerFleetId = null;
                StationCallTargetSource source = StationCallTargetSource.None;
                long target = exists ? ReadStationThreat(call, out source, out attackerFleetId) : 0;
                FactionState st = FindState(call.FactionTag);
                bool focusLive = call.TracksFocus && StationCallRules.FocusLive(st, call.NodeId, _world.UtcNow);
                StationCallEnd end = call.Read(now, exists, antenna, target != 0, focusLive);
                if (end != StationCallEnd.None)
                {
                    EndStationCall(call, end, now);
                    _stationDropScratch.Add(kv.Key);
                    continue;
                }
                call.TargetEntityId = target;
                call.TargetSource = source;
                _world.SetStationCall(call.NodeId, attackerFleetId);
                WingOrder order;
                if (_wingOrders.TryGetValue(-call.StationGridId, out order))
                {
                    order.AntennaEntityId = call.AntennaEntityId;
                    order.TargetEntityId = target;
                }
            }
            for (int i = 0; i < _stationDropScratch.Count; i++) _stationCalls.Remove(_stationDropScratch[i]);
        }

        /// <summary>The call's antenna standing now. When its own antenna fails and another on the
        /// station carries the call, the call moves to that one: a station is silenced when it has no
        /// working antenna, not when it loses one of several.</summary>
        private WingRefusal StationAntenna(StationCall call)
        {
            long gridId;
            WingRefusal own = StationCallRules.CheckAntenna(WingWorld.ReadAntenna(call.AntennaEntityId, out gridId), call.FactionTag);
            if (own == WingRefusal.None) return own;
            long working = WorkingAntenna(call.StationGridId, call.FactionTag);
            if (working == 0) return own;
            call.AntennaEntityId = working;
            return WingRefusal.None;
        }

        /// <summary>A working antenna of <paramref name="factionTag"/> on the grid, or zero. The
        /// grid's antenna list is cached for <see cref="StationAntennaCacheLife"/>.</summary>
        private long WorkingAntenna(long gridId, string factionTag)
        {
            WingRefusal ignored;
            return WorkingAntenna(gridId, factionTag, out ignored);
        }

        private long WorkingAntenna(long gridId, string factionTag, out WingRefusal firstRefusal)
        {
            firstRefusal = WingRefusal.NoAntenna;
            DateTime now = DateTime.UtcNow;
            StationAntennaCache cache;
            if (!_stationAntennas.TryGetValue(gridId, out cache) || now - cache.ReadUtc > StationAntennaCacheLife)
            {
                cache = new StationAntennaCache { Antennas = cache.Antennas ?? new List<long>(), ReadUtc = now };
                WingWorld.Antennas(gridId, cache.Antennas);
                _stationAntennas[gridId] = cache;
            }
            for (int i = 0; i < cache.Antennas.Count; i++)
            {
                long g;
                WingRefusal r = StationCallRules.CheckAntenna(WingWorld.ReadAntenna(cache.Antennas[i], out g), factionTag);
                if (r == WingRefusal.None) return cache.Antennas[i];
                if (firstRefusal == WingRefusal.NoAntenna) firstRefusal = r;
            }
            return 0;
        }

        /// <summary>The NPC side: every materialised NPC
        /// station with no working antenna is silenced, and one that was hit by a hostile grid calls
        /// through its antenna, or has its refusal written once for the attack.</summary>
        private void ReadNpcStations(DateTime now)
        {
            _silencedScratch.Clear();
            for (int i = 0; i < _stationGridScratch.Count; i++)
            {
                string nodeId = _stationGridScratch[i].Key;
                long gridId = _stationGridScratch[i].Value;
                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node) || node.OwnerFactionTag == null) continue;
                FactionState st = FindState(node.OwnerFactionTag);
                if (st == null) continue;
                WingRefusal firstRefusal;
                long antennaId = WorkingAntenna(gridId, st.Tag, out firstRefusal);
                if (StationCallRules.Silenced(true, st.IsPlayerFaction, antennaId != 0)) _silencedScratch.Add(nodeId);
                if (!StationCallRules.HostCalls(true, st.IsPlayerFaction) || _stationCalls.ContainsKey(gridId)) continue;

                StationHit hit;
                if (!_stationHits.TryGetValue(gridId, out hit) || now - hit.Utc > StationCallRules.DamageMemory) continue;
                long owner = WingWorld.GridOwner(gridId);
                if (!HostileTo(hit.AttackerGridId, owner)) continue;
                if (antennaId != 0)
                {
                    bool ok;
                    CallStation(new StationCallRequest { Via = WingVia.Host, AntennaEntityId = antennaId }, out ok);
                }
                else if (_stationRefusedLogged.Add(gridId))
                    _log.Write("STATION_CALL_REFUSED tag=" + st.Tag + " node=" + nodeId + " antenna=0 via=host"
                               + " reason=" + WingRules.Token(firstRefusal) + " attacker=" + hit.AttackerGridId);
            }
            // Stations standing on the agents, which read their own antennas.
            foreach (AgentMuted muted in _agentMuted.Values)
            {
                if (now - muted.Utc > AgentMutedLife) continue;
                for (int i = 0; i < muted.Nodes.Count; i++)
                {
                    SpatialNode node;
                    FactionState st;
                    if (muted.Nodes[i] != null && _world.Graph.Nodes.TryGetValue(muted.Nodes[i], out node)
                        && (st = FindState(node.OwnerFactionTag)) != null
                        && StationCallRules.Silenced(true, st.IsPlayerFaction, false)) _silencedScratch.Add(muted.Nodes[i]);
                }
            }
            _world.SetSilencedStations(_silencedScratch);
        }

        private void EndStationCall(StationCall call, StationCallEnd end, DateTime now)
        {
            bool nodeStillCalling = false;
            foreach (StationCall other in _stationCalls.Values)
                if (other != call && other.NodeId == call.NodeId) { nodeStillCalling = true; break; }
            if (!nodeStillCalling) _world.ClearStationCall(call.NodeId);
            RemoveWingOrder(-call.StationGridId, now);
            _log.Write("STATION_CALL_ENDED tag=" + call.FactionTag + " node=" + call.NodeId
                       + " station=" + call.StationGridId + " reason=" + StationCallRules.Token(end)
                       + (end == StationCallEnd.AntennaLost ? " antenna=" + WingRules.Token(call.AntennaLostReason) : "")
                       + " lasted=" + (now - call.StartedUtc).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                       + BenchToken(call.BenchLabel));
        }

        // ---- bench -----------------------------------------------------------------------------

        /// <summary>The node a bench label's scenario targets, or the node id itself.</summary>
        private string BenchStationNode(string labelOrNode)
        {
            if (_world == null || string.IsNullOrEmpty(labelOrNode)) return null;
            foreach (FactionState st in _states)
            {
                if (st == null) continue;
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (string.Equals(f.BenchLabel, labelOrNode, StringComparison.OrdinalIgnoreCase) && f.TargetNodeId != null)
                        return f.TargetNodeId;
            }
            return _world.Graph.Nodes.ContainsKey(labelOrNode) ? labelOrNode : null;
        }

        /// <summary><c>bench protect &lt;label|nodeId&gt; [npc|status|antenna on|off]</c>: fires the
        /// station's call as if <i>protect me</i> were pressed on its antenna (<c>npc</c>: as the host
        /// raises an NPC station's call, whatever the switch), reads the call, or switches the
        /// station's antennas.</summary>
        internal string BenchProtect(string labelOrNode, string word, string arg)
        {
            string nodeId = BenchStationNode(labelOrNode);
            if (nodeId == null) return "bench: no scenario or node '" + labelOrNode + "'.";
            long gridId = _stationMaterializer.StationGridEntityIdFor(nodeId);
            if (gridId == 0) return "bench: " + nodeId + "'s station is not materialised; stand near it.";
            string label = string.Equals(labelOrNode, nodeId, StringComparison.Ordinal) ? null : labelOrNode;
            SpatialNode node;
            _world.Graph.Nodes.TryGetValue(nodeId, out node);
            string tag = node != null ? node.OwnerFactionTag : null;

            if (word == "antenna")
            {
                if (arg != "on" && arg != "off") return "bench: usage 'factions bench protect <label|nodeId> antenna on|off'.";
                int n = WingWorld.SetAntennasEnabled(gridId, arg == "on");
                _stationAntennas.Remove(gridId);
                return "bench: " + nodeId + ": " + n + " antenna(s) switched " + arg + " on station " + gridId + ".";
            }
            if (word == "status")
            {
                StationCall call;
                long working = WorkingAntenna(gridId, tag);
                var sb = new StringBuilder("bench: " + nodeId + " station=" + gridId + " tag=" + (tag ?? "none")
                                           + " working_antenna=" + working
                                           + " silenced=" + (_world.IsStationSilenced(nodeId) ? "1" : "0"));
                if (_stationCalls.TryGetValue(gridId, out call))
                {
                    sb.Append(" call via=").Append(ViaToken(call.Via))
                      .Append(" target=").Append(call.TargetEntityId).Append(' ').Append(StationCallRules.Token(call.TargetSource))
                      .Append(" focus=").Append(call.TracksFocus ? "tracked" : "none")
                      .Append(" age=").Append((DateTime.UtcNow - call.StartedUtc).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)).Append('s');
                    if (call.AntennaLostUtc.HasValue) sb.Append(" antenna_lost=").Append(WingRules.Token(call.AntennaLostReason));
                    WingOrder order;
                    if (_wingOrders.TryGetValue(-gridId, out order))
                        foreach (KeyValuePair<long, WingLink> kv in order.Links)
                            sb.Append("\n    hull=").Append(kv.Key).Append(kv.Value.Lapsed ? " lapsed" : kv.Value.LostUtc.HasValue ? " grace" : " linked");
                }
                else sb.Append(" call=none");
                return sb.ToString();
            }

            long antennaId = WorkingAntenna(gridId, tag);
            if (antennaId == 0)
            {
                var all = new List<long>();
                WingWorld.Antennas(gridId, all);
                antennaId = all.Count > 0 ? all[0] : 0;
            }
            if (antennaId == 0) return "bench: " + nodeId + "'s station has no antenna.";
            bool ok;
            string message = CallStation(new StationCallRequest
            {
                Via = word == "npc" ? WingVia.Host : WingVia.Bench,
                AntennaEntityId = antennaId,
                BenchLabel = label ?? nodeId,
            }, out ok);
            return "bench: " + nodeId + ": " + message + " (antenna " + antennaId + ")";
        }
    }
}
