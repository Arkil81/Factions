using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The grids of an attack its meeting stopped -- one that broke off, or one beaten with hulls
    /// still standing (<see cref="PendingFleetAction.HullsLeaving"/>). Core settles such an attack at
    /// once (its survivors are credited home, its dead written off) and retires the fleet in the same
    /// pass; its surviving grids are not closed where they stand but seen to leave: each turns tail
    /// under <see cref="ManoeuvreKind.Disengage"/> until no hostile grid is within
    /// <see cref="CombatLeash.DisengageClearM"/>, then cruises toward home, and is closed once no
    /// player is within <see cref="FactionTuning.ObservationRadiusKm"/> of it or
    /// <see cref="DepartCruiseCap"/> has passed. The grids of hulls the settlement wrote off are
    /// closed at once. Nothing here touches the ledger. See docs/fleet-events.md, "A stopped attack
    /// leaves in the world".
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>How long a departing grid may take to break clear before it cruises anyway.</summary>
        private static readonly TimeSpan DepartClearTimeout = TimeSpan.FromSeconds(90);

        /// <summary>How long a departing grid cruises before it is closed, if a player still sees it.</summary>
        private static readonly TimeSpan DepartCruiseCap = TimeSpan.FromSeconds(120);

        private sealed class Departure
        {
            public string Fleet;
            public string BenchLabel;
            public Vec3 HomeKm;
            public bool Cruising;
            public DateTime GiveUpUtc;
        }

        /// <summary>Grids of stopped attacks on their way out, by entity id.</summary>
        private readonly Dictionary<long, Departure> _departing = new Dictionary<long, Departure>();

        /// <summary>Per fleet retired in the current arrivals pass, the hulls still allowed to leave
        /// (<see cref="PendingFleetAction.DepartingHulls"/>). Cleared before each pass.</summary>
        private readonly Dictionary<string, List<string>> _departSlots = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>Called for each grid Core retires. A grid carrying a surviving hull of a stopped
        /// attack is taken to fly out instead of being closed; answers whether it was.</summary>
        private bool DepartBeforeClose(long eid, DateTime now, IList<string> log)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null || !_spawner.GridExists(eid)) return false;
            PendingFleetAction owner;
            if (!TryFleetOfGrid(eid, out owner) || !owner.Intercepted || owner.HullsLeaving <= 0) return false;

            string key = owner.FleetId ?? FleetKey(owner);
            List<string> slots;
            if (!_departSlots.TryGetValue(key, out slots))
            {
                slots = owner.DepartingHulls();
                _departSlots[key] = slots;
            }
            if (!PendingFleetAction.TakeDeparting(slots, _spawner.GridPrefabId(eid))) return false;

            Vec3 from = DepartAnchorKm(owner);
            Vec3 home = owner.IsBench ? BenchScenario.LeavePoint(owner.TargetPosKm, from) : HomeKm(owner);
            bool breaking = vanilla.LeaveFrom(eid, from, CombatLeash.DisengageClearM, owner.StoppedHow ?? "beaten");
            if (!breaking && !_spawner.Retarget(eid, home)) return false;
            _departing[eid] = new Departure
            {
                Fleet = owner.LogId,
                BenchLabel = owner.BenchLabel,
                HomeKm = home,
                Cruising = !breaking,
                GiveUpUtc = now + (breaking ? DepartClearTimeout : DepartCruiseCap),
            };
            log.Add("FLEET_DEPARTING " + owner.LogId + " " + owner.ActorTag + " eid=" + eid
                    + " hulls_leaving=" + owner.HullsLeaving + " why=" + (owner.StoppedHow ?? "beaten")
                    + (breaking ? " clear=" + CombatLeash.DisengageClearM.ToString("0", CultureInfo.InvariantCulture) + "m" : " cruise"));
            return true;
        }

        /// <summary>What a departing grid breaks away from: a bench attacker's scenario defender,
        /// else the node it attacked, else where it held.</summary>
        private Vec3 DepartAnchorKm(PendingFleetAction fleet)
        {
            if (fleet.IsBench)
                foreach (FactionState st in _states)
                    foreach (PendingFleetAction f in st.PendingFleets)
                        if (f.FleetId != fleet.FleetId && string.Equals(f.BenchLabel, fleet.BenchLabel, StringComparison.Ordinal))
                            return f.TargetPosKm;
            SpatialNode node;
            if (fleet.TargetNodeId != null && _world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out node)) return node.Position;
            return fleet.TargetPosKm;
        }

        /// <summary>Where a real fleet's hulls go home to: its return node, else the node it left,
        /// else where its leg began.</summary>
        private Vec3 HomeKm(PendingFleetAction fleet)
        {
            SpatialNode node;
            string id = fleet.ReturnNodeId ?? fleet.OriginNodeId;
            if (id != null && _world.Graph.Nodes.TryGetValue(id, out node)) return node.Position;
            return fleet.OriginPosKm;
        }

        private bool TryFleetOfGrid(long eid, out PendingFleetAction fleet)
        {
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (f.MaterialisedEntityId == eid || (f.EscortEntityIds != null && f.EscortEntityIds.Contains(eid)))
                    {
                        fleet = f;
                        return true;
                    }
                }
            fleet = default(PendingFleetAction);
            return false;
        }

        /// <summary>Every frame: a departing grid that is clear of hostiles (or out of time to get
        /// clear) is put on the cruise autopilot toward home; a cruising one is closed once no player
        /// sees it or its time is up; a grid that is gone is forgotten.</summary>
        private void SweepDepartures(DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            if (_departing.Count == 0) return;
            var vanilla = _spawner as VanillaFleetSpawner;
            List<long> done = null;
            List<KeyValuePair<long, Departure>> turned = null;
            foreach (KeyValuePair<long, Departure> kv in _departing)
            {
                long eid = kv.Key;
                Departure d = kv.Value;
                if (!_spawner.GridExists(eid)) { (done ?? (done = new List<long>())).Add(eid); continue; }
                if (!d.Cruising)
                {
                    bool clear = vanilla == null || vanilla.IsLeaveDone(eid);
                    if (!clear && now < d.GiveUpUtc) continue;
                    (turned ?? (turned = new List<KeyValuePair<long, Departure>>())).Add(kv);
                    continue;
                }
                Vec3? pos = _spawner.GetGridPositionKm(eid);
                bool seen = pos.HasValue && isPlayerWithin != null && isPlayerWithin(pos.Value, _tuning.ObservationRadiusKm);
                if (seen && now < d.GiveUpUtc) continue;
                (done ?? (done = new List<long>())).Add(eid);
                int closed = _spawner.Despawn(eid);
                _log.Write("FLEET_DEPARTED " + d.Fleet + " eid=" + eid + " closed=" + closed
                           + (seen ? " reason=cap" : " reason=out_of_sight"));
            }
            if (turned != null)
                foreach (KeyValuePair<long, Departure> kv in turned)
                {
                    bool clear = vanilla == null || vanilla.IsLeaveDone(kv.Key);
                    bool wired = _spawner.Retarget(kv.Key, kv.Value.HomeKm);
                    kv.Value.Cruising = true;
                    kv.Value.GiveUpUtc = now + DepartCruiseCap;
                    _log.Write("FLEET_DEPART_CLEAR " + kv.Value.Fleet + " eid=" + kv.Key + " rewired=" + wired
                               + (clear ? " left=clear" : " left=timeout"));
                }
            if (done != null) foreach (long eid in done) _departing.Remove(eid);
        }

        /// <summary><c>bench clear</c>: closes the departing grids of <paramref name="label"/> (every
        /// scenario's when null). Returns how many grids were closed.</summary>
        private int CloseBenchDepartures(string label)
        {
            if (_departing.Count == 0) return 0;
            var ids = new List<long>();
            foreach (KeyValuePair<long, Departure> kv in _departing)
                if (kv.Value.BenchLabel != null
                    && (label == null || string.Equals(kv.Value.BenchLabel, label, StringComparison.OrdinalIgnoreCase)))
                    ids.Add(kv.Key);
            int closed = 0;
            foreach (long eid in ids)
            {
                _departing.Remove(eid);
                if (_spawner != null && _spawner.GridExists(eid)) closed += _spawner.Despawn(eid);
            }
            return closed;
        }

        /// <summary>Whether a grid is a stopped attack's hull on its way out, for the reaper.</summary>
        internal bool IsDeparting(long entityId)
        {
            return _departing.ContainsKey(entityId);
        }
    }
}
