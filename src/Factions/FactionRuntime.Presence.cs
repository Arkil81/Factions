using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Traffic control's presence rule (docs/traffic-control.md, slice 2; <see cref="TrafficPresence"/>):
    /// civilian traffic near players materialises only inside each player's share of the Factions
    /// budget, and far materialised traffic is shed through the leave-range exit when a share is
    /// exceeded. A refused fleet stays abstract and is asked again on the next plan, so what a player
    /// sees grows as the budget frees. Bench fleets are never gated.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>One fleet near a player, as the plan sees it.</summary>
        public struct PresenceCandidate
        {
            public PendingFleetAction Fleet;
            public Vec3 PosKm;
            public bool Materialised;
            public bool Sheddable;
            /// <summary>The queued station this fleet is arriving at or leaving; null otherwise.</summary>
            public string QueueStation;
            public bool QueueDeparture;
            public bool Divertable;
            /// <summary>Heading for a queued station.</summary>
            public bool Inbound;
        }

        /// <summary>Set by the host, which owns the players, the budget and the prefab catalogue: turns
        /// this frame's candidates (and, with the queue on, the queued stations and their queues) into
        /// a plan, or null when there is nothing to plan against yet.</summary>
        public Func<List<PresenceCandidate>, IDictionary<string, Vec3>, StationQueues, DateTime, TrafficPresence.Plan> TrafficPlanner { get; set; }

        /// <summary>A fleet this close to a queued station, and not heading for it, is leaving it.</summary>
        private const double QueueDockRadiusKm = 2.0;

        /// <summary>How far past the visible radius, measured from the station, an arrival joins its
        /// queue and holds. At the player's reach (1 km past the visible radius) it held so close that
        /// a player standing a few hundred metres off the station's centre had it inside 15 km, and it
        /// spawned while still queued (2026-10-01 at N600: six in 20 minutes, released 15.2-15.8 km
        /// from the station). A player has to fly this far out toward the holders to see them.</summary>
        private const double HoldMarginKm = 5.0;

        /// <summary>How near a player a joining arrival may hold where it is. Nearer than this it is
        /// moved out to a slot instead: it overshot into view between two plans.</summary>
        private const double HoldInViewMarginKm = 2.0;

        private readonly StationQueues _stationQueues = new StationQueues();
        private readonly Dictionary<string, Vec3> _queuedStations = new Dictionary<string, Vec3>(StringComparer.Ordinal);
        /// <summary>Where each candidate sits in the faction lists, for acting on the plan it was in.</summary>
        private readonly Dictionary<string, KeyValuePair<FactionState, int>> _presenceIndex =
            new Dictionary<string, KeyValuePair<FactionState, int>>(StringComparer.Ordinal);
        /// <summary>When each queued fleet was last frozen, so it is frozen by the frame gap.</summary>
        private readonly Dictionary<string, DateTime> _queueHeldAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private int _queueJoined, _queueCleared, _queueDiverted, _queueDelayed;
        /// <summary>Each queued arrival's holding slot (T4): where it waits abstract, where it spawns
        /// if a player comes near enough and the budget allows, and where a spawned holder parks.</summary>
        private readonly Dictionary<string, Vec3> _holdSlots = new Dictionary<string, Vec3>(StringComparer.Ordinal);
        /// <summary>Spawned holders whose autopilot has been pointed at their slot.</summary>
        private readonly HashSet<long> _holdAimed = new HashSet<long>();
        private readonly List<Vec3> _slotScratch = new List<Vec3>();

        private static readonly TimeSpan PresencePlanInterval = TimeSpan.FromSeconds(1);

        /// <summary>A grid is not shed this soon after it spawned: shedding it would throw away the
        /// spawn's hitch for nothing, and a share that dips once must not undo a spawn it just allowed.</summary>
        private static readonly TimeSpan ShedMinAge = TimeSpan.FromSeconds(60);
        private readonly Dictionary<long, DateTime> _spawnedAt = new Dictionary<long, DateTime>();

        private void NoteSpawned(long eid, DateTime now)
        {
            _spawnedAt[eid] = now;
            if (_spawnedAt.Count > 512)
            {
                var old = new List<long>();
                foreach (KeyValuePair<long, DateTime> kv in _spawnedAt) if (now - kv.Value > ShedMinAge) old.Add(kv.Key);
                foreach (long k in old) _spawnedAt.Remove(k);
            }
        }

        private bool OldEnoughToShed(long eid, DateTime now)
        {
            DateTime at;
            return !_spawnedAt.TryGetValue(eid, out at) || now - at >= ShedMinAge;
        }

        private TrafficPresence.Plan _presencePlan;
        private DateTime _presencePlanAt = DateTime.MinValue;
        private readonly List<PresenceCandidate> _presenceCandidates = new List<PresenceCandidate>();
        /// <summary>Fleets refused since they were last admitted, so each refusal is counted and logged
        /// once rather than every frame.</summary>
        private readonly HashSet<string> _presenceRefused = new HashSet<string>(StringComparer.Ordinal);
        private int _presenceRefusedCount;
        private int _presenceShedCount;

        /// <summary>The plan the gate is reading; null while the rule is off.</summary>
        public TrafficPresence.Plan PresencePlan { get { return _presencePlan; } }

        /// <summary>Distinct fleets refused and fleets shed since the last read.</summary>
        public void TakePresenceCounts(out int refused, out int shed)
        {
            refused = _presenceRefusedCount;
            shed = _presenceShedCount;
            _presenceRefusedCount = 0;
            _presenceShedCount = 0;
        }

        /// <summary>Station-queue events since the last read: joined, cleared, diverted, delay called.</summary>
        public void TakeQueueCounts(int[] into)
        {
            into[0] = _queueJoined; into[1] = _queueCleared; into[2] = _queueDiverted; into[3] = _queueDelayed;
            _queueJoined = _queueCleared = _queueDiverted = _queueDelayed = 0;
        }

        private void RefreshPresencePlan(DateTime now, Func<Vec3, double, bool> isPlayerWithin, bool anyoneOnline)
        {
            if (!anyoneOnline || TrafficPlanner == null)
            {
                _presencePlan = null;
                _presenceRefused.Clear();
                DropQueues();
                return;
            }
            if (_presencePlan != null && now - _presencePlanAt < PresencePlanInterval) return;

            _queuedStations.Clear();
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
                    if (!string.IsNullOrEmpty(node.OwnerFactionTag) && isPlayerWithin(node.Position, _tuning.ObservationRadiusKm))
                        _queuedStations[node.Id] = node.Position;

            _presenceCandidates.Clear();
            _presenceIndex.Clear();
            double reachKm = _tuning.ObservationRadiusKm + DespawnHysteresisKm;
            foreach (FactionState st in _states)
            {
                if (st == null || st.PendingFleets == null) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction fleet = st.PendingFleets[fi];
                    if (fleet.Intercepted || fleet.Resolved || fleet.IsBench) continue;
                    long eid = fleet.MaterialisedEntityId;
                    bool materialised = eid > 0 || eid == PendingFleetAction.SpawningId;
                    if (!materialised && fleet.HasArrived(now) && !fleet.IsActiveGuard(now, _tuning)) continue;

                    Vec3 pos = fleet.CurrentPosKm(now, _tuning);
                    if (eid > 0)
                    {
                        Vec3? phys = _spawner.GetGridPositionKm(eid);
                        if (phys.HasValue) pos = phys.Value;
                    }
                    // A queued or cleared fleet stays a candidate wherever it is: out of every player's
                    // reach it would not be seen, and the queue would drop its place in line or its
                    // clearance, so it rejoins at the back the moment it is back in reach.
                    bool inQueue = fleet.FleetId != null
                                   && (_stationQueues.Find(fleet.FleetId) != null || _stationQueues.HasClearance(fleet.FleetId));
                    // An arrival is a candidate once it is within the hold radius of the queued station
                    // it is heading for, whatever any player's distance: that is where it joins and holds.
                    // A miner on its dwell is out on the ring around its deposit: it is neither arriving
                    // at that station nor leaving it, so no queue holds it.
                    bool wandering = MineWander.Applies(fleet);
                    Vec3 bound;
                    bool approaching = !materialised && !wandering && FactionsLoadActuator.IsTraffic(fleet.Kind)
                                       && fleet.TargetNodeId != null && _queuedStations.TryGetValue(fleet.TargetNodeId, out bound)
                                       && pos.DistanceTo(bound) <= _tuning.ObservationRadiusKm + HoldMarginKm;
                    if (!inQueue && !approaching && !isPlayerWithin(pos, reachKm)) continue;

                    var candidate = new PresenceCandidate
                    {
                        Fleet = fleet,
                        PosKm = pos,
                        Materialised = materialised,
                        Sheddable = eid > 0 && FactionsLoadActuator.IsTraffic(fleet.Kind) && !_spawnSettle.IsHeld(eid, now)
                                    && OldEnoughToShed(eid, now),
                    };
                    candidate.Inbound = !wandering && fleet.TargetNodeId != null && _queuedStations.ContainsKey(fleet.TargetNodeId);
                    if (!wandering && FactionsLoadActuator.IsTraffic(fleet.Kind)
                        && (!materialised || (fleet.FleetId != null && _stationQueues.Find(fleet.FleetId) != null)))
                        FlagStationTraffic(ref candidate, fleet);
                    _presenceCandidates.Add(candidate);
                    if (!string.IsNullOrEmpty(fleet.FleetId))
                        _presenceIndex[fleet.FleetId] = new KeyValuePair<FactionState, int>(st, fi);
                }
            }

            _presencePlan = TrafficPlanner(_presenceCandidates, _queuedStations, _stationQueues, now);
            _presencePlanAt = now;
            if (_presencePlan != null)
            {
                ActOnQueues(_presencePlan, now, isPlayerWithin);
                // Holders beyond everyone's radius are never reached by the per-frame hold in the
                // observation loop, so every queued fleet is frozen here too, once a plan.
                foreach (string key in _presencePlan.Queued)
                {
                    KeyValuePair<FactionState, int> at;
                    if (!_presenceIndex.TryGetValue(key, out at) || at.Value >= at.Key.PendingFleets.Count) continue;
                    PendingFleetAction f = at.Key.PendingFleets[at.Value];
                    if (f.FleetId == key && f.MaterialisedEntityId == 0) FreezeQueued(at.Key, at.Value, f, now);
                }
            }
        }

        /// <summary>Marks a fleet heading for a queued station as its arrival, and one sitting at a
        /// queued station it is not heading for as its departure.</summary>
        private void FlagStationTraffic(ref PresenceCandidate c, PendingFleetAction fleet)
        {
            if (fleet.TargetNodeId != null && _queuedStations.ContainsKey(fleet.TargetNodeId))
            {
                c.QueueStation = fleet.TargetNodeId;
                // A Pickup diverts too: to another node of the same owner holding the item, or it gives
                // up its errand (FAC-514). Without that it waits for as long as the share stays full.
                c.Divertable = fleet.CurrentStopKind == FleetStopKind.Service || fleet.CurrentStopKind == FleetStopKind.Return
                               || fleet.CurrentStopKind == FleetStopKind.Pickup;
                return;
            }
            double best = QueueDockRadiusKm;
            foreach (KeyValuePair<string, Vec3> station in _queuedStations)
            {
                double d = c.PosKm.DistanceTo(station.Value);
                if (d > best) continue;
                best = d;
                c.QueueStation = station.Key;
                c.QueueDeparture = true;
            }
        }

        private void DropQueues()
        {
            if (_stationQueues.Count > 0) _stationQueues.Retain(null, null);
            _queueHeldAt.Clear();
            _holdSlots.Clear();
        }

        /// <summary>Logs the queue's changes and carries out its diverts.</summary>
        private void ActOnQueues(TrafficPresence.Plan plan, DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            foreach (StationQueues.Entry e in plan.Joined)
            {
                _queueJoined++;
                if (!e.Departure) AssignHoldSlot(e, plan, now, isPlayerWithin);
                CallQueue(e.Departure ? TrafficEventKind.Queued : TrafficEventKind.Held, e, now, null);
                if (DebugTraces.Admission)
                    _log.Write("TRAFFIC_QUEUED " + LogIdOf(e.Key) + " station=" + e.Station
                               + " dir=" + (e.Departure ? "departure" : "arrival")
                               + " position=" + _stationQueues.Position(e.Key)
                               + " predicted=" + e.CostMs.ToString("F3", CultureInfo.InvariantCulture) + "ms");
            }
            foreach (StationQueues.Entry e in plan.Cleared)
            {
                _queueCleared++;
                _queueHeldAt.Remove(e.Key);
                _holdSlots.Remove(e.Key);
                ReleaseHolder(e.Key);
                CallQueue(TrafficEventKind.Cleared, e, now, null);
                if (DebugTraces.Admission)
                    _log.Write("TRAFFIC_CLEARED " + LogIdOf(e.Key) + " station=" + e.Station
                               + " dir=" + (e.Departure ? "departure" : "arrival")
                               + " waited=" + (now - e.JoinedUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
            }
            foreach (StationQueues.Entry e in plan.DivertDue)
            {
                // A ship holding visibly is not pulled out from under the player: it diverts once it
                // is abstract again (shed, or the player gone).
                if (e.Materialised) continue;
                string to;
                string logId = LogIdOf(e.Key);
                if (TryDivert(e, now, out to))
                {
                    _queueDiverted++;
                    plan.Queued.Remove(e.Key);
                    // A fleet that gave up its errand is not voiced: it was going nowhere the station
                    // could name.
                    if (to != null) CallQueue(TrafficEventKind.Diverted, e, now, to);
                    if (DebugTraces.Admission)
                        _log.Write("TRAFFIC_DIVERTED " + logId + " station=" + e.Station + " to=" + (to ?? "home")
                                   + " waited=" + (now - e.JoinedUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
                }
                else
                {
                    // Nowhere to go: it waits like a departure, and its delay is called next plan.
                    _stationQueues.MarkUndivertable(e.Key);
                }
            }
            foreach (StationQueues.Entry e in plan.DelayDue)
            {
                _queueDelayed++;
                CallQueue(TrafficEventKind.Delayed, e, now, null);
                if (DebugTraces.Admission)
                    _log.Write("TRAFFIC_DELAYED " + LogIdOf(e.Key) + " station=" + e.Station
                               + " dir=" + (e.Departure ? "departure" : "arrival")
                               + " position=" + _stationQueues.Position(e.Key)
                               + " waited=" + (now - e.JoinedUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
            }

            var stale = new List<string>();
            foreach (string key in _queueHeldAt.Keys) if (!plan.Queued.Contains(key)) stale.Add(key);
            foreach (string key in stale) _queueHeldAt.Remove(key);
            stale.Clear();
            foreach (string key in _holdSlots.Keys) if (!plan.Queued.Contains(key)) stale.Add(key);
            foreach (string key in stale) { _holdSlots.Remove(key); ReleaseHolder(key); }
            WhisperQueues(now);
        }

        /// <summary>Hands a queue change to the traffic-control voice as a station traffic event
        /// (docs/traffic-control.md, "What the player hears"): the controller names the fleet by its
        /// id, and the pilot calls from where the ship is waiting.</summary>
        private void CallQueue(TrafficEventKind kind, StationQueues.Entry e, DateTime now, string divertTo)
        {
            Action<TrafficEvent, string, string> sink = TrafficSink;
            if (sink == null || e == null) return;
            KeyValuePair<FactionState, int> at;
            if (!_presenceIndex.TryGetValue(e.Key, out at) || at.Value >= at.Key.PendingFleets.Count) return;
            PendingFleetAction f = at.Key.PendingFleets[at.Value];
            if (f.FleetId != e.Key) return;
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(e.Station, out node)) return;
            SpatialNode divert = null;
            if (divertTo != null) _world.Graph.Nodes.TryGetValue(divertTo, out divert);
            sink(new TrafficEvent
            {
                Kind = kind, NodeId = e.Station, NodePosKm = node.Position,
                ActorTag = f.ActorTag, FleetKind = f.Kind, StopKind = f.CurrentStopKind,
                FleetKey = f.ActorTag + "|" + f.FleetId, FleetId = f.FleetId,
                QueuePosition = kind == TrafficEventKind.Cleared || kind == TrafficEventKind.Diverted ? 0 : _stationQueues.Position(e.Key),
                Departure = e.Departure, DivertNodeId = divertTo, DivertNodeName = divert != null ? divert.DisplayName : null,
                Utc = now, PilotPosKm = f.CurrentPosKm(now, _tuning), PilotSignature = 1.0,
                PilotEntityId = f.MaterialisedEntityId > 0 ? f.MaterialisedEntityId : 0,
            }, node.DisplayName, node.OwnerFactionTag);
        }

        private static readonly TimeSpan QueueWhisperInterval = TimeSpan.FromSeconds(60);
        /// <summary>Last queue line sent, and when, per player and station.</summary>
        private readonly Dictionary<string, KeyValuePair<DateTime, string>> _queueWhispered =
            new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.Ordinal);

        /// <summary>Players without the traffic voice get the queue in chat (T7): one line from the
        /// station's controller when its queue changes, at most once a minute per player and station,
        /// and nothing while it is empty.</summary>
        private void WhisperQueues(DateTime now)
        {
            if (_chat == null || _playersWithin == null) return;
            foreach (KeyValuePair<string, Vec3> station in _queuedStations)
            {
                int holding = 0, departing = 0;
                foreach (StationQueues.Entry e in _stationQueues.Queue(station.Key))
                    if (e.Departure) departing++; else holding++;
                string text = holding + departing == 0 ? null
                    : holding + " holding, " + departing + " awaiting departure.";
                SpatialNode node;
                string name = _world.Graph.Nodes.TryGetValue(station.Key, out node) && !string.IsNullOrEmpty(node.DisplayName)
                    ? node.DisplayName : station.Key;
                foreach (ulong steamId in _playersWithin(station.Value, _tuning.ObservationRadiusKm))
                {
                    if (SpeaksTraffic != null && SpeaksTraffic(steamId)) continue;
                    string key = steamId.ToString(CultureInfo.InvariantCulture) + "|" + station.Key;
                    KeyValuePair<DateTime, string> last;
                    bool had = _queueWhispered.TryGetValue(key, out last);
                    if (had && string.Equals(last.Value, text, StringComparison.Ordinal)) continue;
                    if (had && now - last.Key < QueueWhisperInterval) continue;
                    if (text != null) _chat.SendMessageAsOther(name + " control", text, VRageMath.Color.CornflowerBlue, steamId);
                    _queueWhispered[key] = new KeyValuePair<DateTime, string>(now, text);
                }
            }
        }

        /// <summary>A held arrival holds where it joined: it joins as it comes within the hold radius of
        /// the station (<see cref="HoldMarginKm"/> past the visible radius), and moving it would show on
        /// the map as a jump. Only one that joined near a player -- a short hop from a nearby node, or
        /// one that overshot between plans -- is moved out to a slot on the hold radius.</summary>
        private void AssignHoldSlot(StationQueues.Entry e, TrafficPresence.Plan plan, DateTime now,
                                    Func<Vec3, double, bool> isPlayerWithin)
        {
            // Cleared in the plan it joined: it is not held at all.
            if (_stationQueues.Find(e.Key) == null) return;
            KeyValuePair<FactionState, int> at;
            if (!_presenceIndex.TryGetValue(e.Key, out at) || at.Value >= at.Key.PendingFleets.Count) return;
            PendingFleetAction fleet = at.Key.PendingFleets[at.Value];
            if (fleet.FleetId != e.Key || fleet.MaterialisedEntityId != 0) return;
            Vec3 station;
            if (!_queuedStations.TryGetValue(e.Station, out station)) return;
            Vec3 here = fleet.CurrentPosKm(now, _tuning);
            if (!isPlayerWithin(here, _tuning.ObservationRadiusKm + HoldInViewMarginKm))
            {
                _holdSlots[e.Key] = here;
                return;
            }

            _slotScratch.Clear();
            foreach (KeyValuePair<string, Vec3> held in _holdSlots)
            {
                StationQueues.Entry other = _stationQueues.Find(held.Key);
                if (other != null && other.Station == e.Station) _slotScratch.Add(held.Value);
            }
            double share;
            if (!plan.StationShareMs.TryGetValue(e.Station, out share)) share = plan.BudgetMs;
            double spacing = HoldingSlots.SpacingKm(e.CostMs, share);
            Vec3? slot = HoldingSlots.Choose(station, here, spacing, _slotScratch, _planets,
                                             _tuning.ObservationRadiusKm + HoldMarginKm);
            if (!slot.HasValue) return;

            _holdSlots[e.Key] = slot.Value;
            at.Key.PendingFleets[at.Value] = HoldingSlots.Reorigin(fleet, slot.Value, _world, _tuning, now);
        }

        /// <summary>A spawned holder that is released (cleared, or no longer queued) is pointed back at
        /// its station: the approach controller alone would not re-aim a parked grid.</summary>
        private void ReleaseHolder(string key)
        {
            KeyValuePair<FactionState, int> at;
            if (key == null || !_presenceIndex.TryGetValue(key, out at) || at.Value >= at.Key.PendingFleets.Count) return;
            PendingFleetAction fleet = at.Key.PendingFleets[at.Value];
            if (fleet.FleetId != key || fleet.MaterialisedEntityId <= 0) return;
            if (!_holdAimed.Remove(fleet.MaterialisedEntityId)) return;
            _spawner.Retarget(fleet.MaterialisedEntityId, fleet.TargetPosKm);
        }

        /// <summary>For a spawned holder: its slot, with the abstract leg frozen for this frame and the
        /// grid's autopilot pointed at the slot once. The caller flies the grid to the slot instead of
        /// its target and skips its launch, so the approach controller parks it there.</summary>
        private bool HoldsLiveAt(FactionState st, int fi, PendingFleetAction fleet, DateTime now, out Vec3 slot)
        {
            slot = default(Vec3);
            TrafficPresence.Plan plan = _presencePlan;
            if (plan == null || string.IsNullOrEmpty(fleet.FleetId) || fleet.MaterialisedEntityId <= 0) return false;
            if (!plan.Queued.Contains(fleet.FleetId) || !_holdSlots.TryGetValue(fleet.FleetId, out slot)) return false;
            FreezeQueued(st, fi, fleet, now);
            if (_holdAimed.Add(fleet.MaterialisedEntityId))
                _spawner.Retarget(fleet.MaterialisedEntityId, slot);
            return true;
        }

        /// <summary>Whether the fleet is frozen where it stands right now, by its station's queue or
        /// by the combat hold. Its leg still has a speed, but it is not moving: a client that
        /// dead-reckons it from that speed draws it creeping forward and snapping back each pass.</summary>
        private bool IsHeldStill(PendingFleetAction fleet, DateTime now)
        {
            if (string.IsNullOrEmpty(fleet.FleetId)) return false;
            if (_combatHolds.IsHolding(fleet.FleetId, now)) return true;
            DateTime last;
            return _queueHeldAt.TryGetValue(fleet.FleetId, out last)
                && (now - last).TotalSeconds <= CombatHoldTracker.GapSeconds
                && _stationQueues.Find(fleet.FleetId) != null;
        }

        private void FreezeQueued(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            DateTime last;
            if (_queueHeldAt.TryGetValue(fleet.FleetId, out last))
            {
                TimeSpan gap = now - last;
                if (gap > TimeSpan.Zero && gap.TotalSeconds <= CombatHoldTracker.GapSeconds)
                    st.PendingFleets[fi] = FactionsLoadActuator.Freeze(fleet, gap);
            }
            _queueHeldAt[fleet.FleetId] = now;
        }

        private string LogIdOf(string key)
        {
            KeyValuePair<FactionState, int> at;
            if (key != null && _presenceIndex.TryGetValue(key, out at) && at.Value < at.Key.PendingFleets.Count)
            {
                PendingFleetAction f = at.Key.PendingFleets[at.Value];
                if (f.FleetId == key) return f.LogId + " " + f.ActorTag + " kind=" + f.Kind;
            }
            return "[" + key + "]";
        }

        /// <summary>Sends a queued arrival to the nearest station its destination's owner holds that no
        /// player is near. Its stop is replaced, so cargo is delivered, or a sale settled, there. A
        /// Pickup is collected from another node of the same owner instead, or, with none, gives up its
        /// errand and goes home: <paramref name="to"/> is then null.</summary>
        private bool TryDivert(StationQueues.Entry e, DateTime now, out string to)
        {
            to = null;
            KeyValuePair<FactionState, int> at;
            if (!_presenceIndex.TryGetValue(e.Key, out at) || at.Value >= at.Key.PendingFleets.Count) return false;
            FactionState st = at.Key;
            int fi = at.Value;
            PendingFleetAction fleet = st.PendingFleets[fi];
            if (fleet.FleetId != e.Key || string.IsNullOrEmpty(fleet.TargetTag)) return false;

            Vec3 pos = fleet.CurrentPosKm(now, _tuning);
            if (fleet.CurrentStopKind == FleetStopKind.Pickup) return TryDivertPickup(st, fi, fleet, e, pos, now, out to);
            SpatialNode best = null;
            double bestKm = double.MaxValue;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (!string.Equals(node.OwnerFactionTag, fleet.TargetTag, StringComparison.Ordinal)) continue;
                if (string.Equals(node.Id, fleet.TargetNodeId, StringComparison.Ordinal) || _queuedStations.ContainsKey(node.Id)
                    || _world.ClosedForCombat.Contains(node.Id)) continue;
                double d = pos.DistanceTo(node.Position);
                if (d < bestKm) { bestKm = d; best = node; }
            }
            if (best == null) return false;

            var lines = new List<string>();
            bool ok = PendingFleetResolver.Retarget(st, fi, best.Id, PendingFleetResolver.FleetRetargetPolicy.Replace,
                                                   _world, _tuning, lines, "traffic_divert");
            foreach (string line in lines) _log.Write(line);
            if (!ok) return false;
            _stationQueues.Remove(e.Key);
            _queueHeldAt.Remove(e.Key);
            to = best.Id;
            return true;
        }

        /// <summary>A queued Pickup's divert: collected from the nearest other node of the pickup node's
        /// owner with the item to spare (<see cref="QueueDivert.PickupSource"/>), or recalled home with
        /// its hull and cargo when there is none. The recall is resolved by the next arrival pass, the
        /// way a dismissed duplicate is; its arrival time is now, so no later plan queues it again.</summary>
        private bool TryDivertPickup(FactionState st, int fi, PendingFleetAction fleet, StationQueues.Entry e, Vec3 pos,
                                     DateTime now, out string to)
        {
            to = null;
            if (fleet.MaterialisedEntityId != 0) return false;
            var excluded = new HashSet<string>(_queuedStations.Keys, StringComparer.Ordinal);
            foreach (string closed in _world.ClosedForCombat) excluded.Add(closed);
            SpatialNode source = QueueDivert.PickupSource(fleet, pos, _world, _tuning, excluded);
            if (source != null)
            {
                var lines = new List<string>();
                bool ok = PendingFleetResolver.Retarget(st, fi, source.Id, PendingFleetResolver.FleetRetargetPolicy.KeepStop,
                                                       _world, _tuning, lines, "traffic_divert");
                foreach (string line in lines) _log.Write(line);
                if (ok)
                {
                    _stationQueues.Remove(e.Key);
                    _queueHeldAt.Remove(e.Key);
                    to = source.Id;
                    return true;
                }
            }

            PendingFleetAction d = fleet;
            d.EstimatedArrivalUtc = now;
            d.ForcedRatio = 0.0;
            d.ForcedRatioCause = PendingFleetResolver.QueueGiveUpCause;
            st.PendingFleets[fi] = d;
            _stationQueues.Remove(e.Key);
            _queueHeldAt.Remove(e.Key);
            if (DebugTraces.Admission)
                _log.Write("TRAFFIC_GAVE_UP " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                           + " station=" + e.Station + " item=" + fleet.PickupItemKey
                           + " waited=" + (now - e.JoinedUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
            return true;
        }

        /// <summary>Holds a queued fleet where it stands for this frame: its schedule is pushed back by
        /// the time since the last frame it was held, so it neither moves nor arrives. A gap longer than
        /// a few seconds (the player was away) is not frozen.</summary>
        private bool QueueHolds(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            TrafficPresence.Plan plan = _presencePlan;
            if (plan == null || string.IsNullOrEmpty(fleet.FleetId) || !plan.Queued.Contains(fleet.FleetId)) return false;
            FreezeQueued(st, fi, fleet, now);
            // Admitted to hold visibly (T4): it spawns at its slot, still queued.
            return !(plan.Admitted.Contains(fleet.FleetId) && _holdSlots.ContainsKey(fleet.FleetId));
        }

        /// <summary>Whether the presence rule keeps this fleet abstract. Only civilian traffic with an
        /// id is gated; a fleet the plan has not seen yet waits for the next one.</summary>
        private bool PresenceRefuses(PendingFleetAction fleet)
        {
            TrafficPresence.Plan plan = _presencePlan;
            if (plan == null || fleet.IsBench || !FactionsLoadActuator.IsTraffic(fleet.Kind)) return false;
            string id = fleet.FleetId;
            if (string.IsNullOrEmpty(id)) return false;
            if (plan.Admitted.Contains(id))
            {
                _presenceRefused.Remove(id);
                return false;
            }
            if (_presenceRefused.Add(id))
            {
                _presenceRefusedCount++;
                if (DebugTraces.Admission)
                    _log.Write("TRAFFIC_REFUSED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                               + " budget=" + plan.BudgetMs.ToString("F2", CultureInfo.InvariantCulture) + "ms");
            }
            return true;
        }

        /// <summary>Whether the plan sheds this materialised fleet now. A grid still inside its settle
        /// grace is never shed.</summary>
        private bool PresenceSheds(PendingFleetAction fleet, DateTime now)
        {
            TrafficPresence.Plan plan = _presencePlan;
            if (plan == null || string.IsNullOrEmpty(fleet.FleetId) || fleet.MaterialisedEntityId <= 0) return false;
            if (!plan.Shed.Contains(fleet.FleetId) || _spawnSettle.IsHeld(fleet.MaterialisedEntityId, now)) return false;
            plan.Shed.Remove(fleet.FleetId);
            _presenceShedCount++;
            return true;
        }
    }
}
