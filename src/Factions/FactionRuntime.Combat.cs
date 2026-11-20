using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>The observation loop's side of materialised combat: which holding leads fight, and
    /// the fleet-to-bands lookup the flight controller reads a target's weapons from. See
    /// docs/fleet-tactics.md.</summary>
    public sealed partial class FactionRuntime
    {
        private Func<long, IReadOnlyList<WeaponBand>> _fleetBandsForGrid;
        private Func<string, ICollection<long>, string> _opponentsForFleet;

        /// <summary>What each fighting squadron's lead is flying, for its scanner contact and the
        /// whisper when that changes.</summary>
        private readonly TacticHintTracker _tacticHints = new TacticHintTracker();

        /// <summary>Arms set with <c>factions bench arm</c>, by bench fleet id. Not persisted: a
        /// scenario is not either.</summary>
        private readonly Dictionary<string, TacticOrder> _benchArms = new Dictionary<string, TacticOrder>(StringComparer.Ordinal);


        /// <summary>Called every frame a materialised fleet holds station. A fleet that fights
        /// there (<see cref="TacticalRead.FightsAtHold"/>) has its lead handed to the flight
        /// controller's combat manoeuvres, which fly it only while a hostile grid is in reach; every
        /// other holding fleet is told to stop, which is a no-op for one that never started.</summary>
        private void HoldFight(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return;

            long eid = fleet.MaterialisedEntityId;
            // A Guard shadowing an intruder holds its weapons, turrets included, until the ladder
            // reaches Intercept or something at the node gives it a fight.
            bool holdFire = TrespassLadder.GuardHoldsFire(fleet, _world, _states);
            vanilla.HoldTurrets(eid, fleet.EscortEntityIds, holdFire);
            if (holdFire)
            {
                // It shadows the intruder it was sent at; out of the leash, the shadow reads idle and
                // the hold at the node flies it until the intruder comes back in reach.
                long intruder = _world.IntruderGridAt(fleet.TargetNodeId);
                if (intruder == 0) vanilla.StopFighting(eid);
                else vanilla.ShadowAtHold(eid, fleet.TargetPosKm, intruder,
                        TrespassLadder.ShadowStandoffM, TrespassLadder.ShadowLeashM, fleet.FleetId);
                _tacticHints.Observe(fleet.FleetId, null, now);
                return;
            }
            if (!TacticalRead.FightsAtHold(fleet, now, _tuning))
            {
                vanilla.StopFighting(eid);
                _tacticHints.Observe(fleet.FleetId, null, now);
                return;
            }

            if (_fleetBandsForGrid == null) _fleetBandsForGrid = FleetBandsForGrid;
            if (_opponentsForFleet == null) _opponentsForFleet = OpponentsForFleet;
            if (_zonePacing == null) _zonePacing = ZonePacingFor;
            vanilla.SetFleetBandsLookup(_fleetBandsForGrid);
            vanilla.SetOpponentsLookup(_opponentsForFleet);
            vanilla.SetZonePacing(_zonePacing);
            vanilla.FightAtHold(eid, fleet.TargetPosKm, fleet.WeaponBands, fleet.FleetId, true,
                TargetSelection.ScopeFor(fleet), TacticOverrideFor(fleet), LearnedOrder(st, fi, fleet, now),
                PursuitFor(fleet, now));

            string announce = _tacticHints.Observe(fleet.FleetId, vanilla.CombatManoeuvre(eid), now);
            if (announce != null) SendNearbyFleetMessage(fleet.TargetPosKm, TacticHint.Whisper(fleet.ActorTag, announce));
        }

        private Func<long, double, double, ZoneFireCall> _zonePacing;

        /// <summary>Per Guard fleet id: when its pursuit was last read, and the grid it pursues (0 for
        /// none). Read once a second; the space test walks every node.</summary>
        private readonly Dictionary<string, KeyValuePair<DateTime, long>> _pursuitRead =
            new Dictionary<string, KeyValuePair<DateTime, long>>(StringComparer.Ordinal);
        private static readonly TimeSpan PursuitReadInterval = TimeSpan.FromSeconds(1);

        /// <summary>The intruder a Guard at Intercept pursues past its node's leash, while that grid
        /// is still in the faction's space (<see cref="TrespassLadder.PursuitTarget"/>); 0 for none.
        /// A pursuit re-marks the Guard's own node, so the chase keeps it on duty as the intruder
        /// runs past nodes nearer to it.</summary>
        private long PursuitFor(PendingFleetAction fleet, DateTime now)
        {
            if (!fleet.AgainstIntruder || fleet.FleetId == null) return 0;
            KeyValuePair<DateTime, long> read;
            if (_pursuitRead.TryGetValue(fleet.FleetId, out read) && now - read.Key < PursuitReadInterval)
                return read.Value;

            long pursue = 0;
            long grid = _world.IntruderGridAt(fleet.TargetNodeId);
            var field = _world.Influence as InfluenceField;
            VRage.ModAPI.IMyEntity entity;
            if (grid != 0 && field != null && Sandbox.ModAPI.MyAPIGateway.Entities.TryGetEntityById(grid, out entity) && entity != null
                && !entity.MarkedForClose)
            {
                VRageMath.Vector3D pos = entity.GetPosition();
                pursue = TrespassLadder.PursuitTarget(fleet, _world, field,
                    new Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0), _tuning.TradeReachThreshold);
            }
            if (pursue != 0) _world.MarkIntruder(fleet.TargetNodeId, TrespassRung.Intercept, pursue);
            if (_pursuitRead.Count > 64) _pursuitRead.Clear();
            _pursuitRead[fleet.FleetId] = new KeyValuePair<DateTime, long>(now, pursue);
            return pursue;
        }

        /// <summary>The siege pacing for fire on station grid <paramref name="stationGridId"/>'s zone
        /// (<see cref="ZoneSiegePacing.Decide"/>), on the world clock the siege is stamped with. A grid that
        /// is no Factions station fires freely.</summary>
        private ZoneFireCall ZonePacingFor(long stationGridId, double attackerDps, double secondsSinceOwnBurst)
        {
            string nodeId = _stationMaterializer.NodeIdForStationGrid(stationGridId);
            SpatialNode n;
            if (nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out n)) return ZoneFireCall.Push;
            return ZoneSiegePacing.Decide(n, _tuning, _world.UtcNow, attackerDps, secondsSinceOwnBurst);
        }

        /// <summary>The phrase a fleet's scanner contact carries: how its lead is fighting right now,
        /// or null.</summary>
        private string TacticHintFor(PendingFleetAction fleet, DateTime now)
        {
            return _tacticHints.Count == 0 ? null : _tacticHints.Current(fleet.FleetId, now);
        }

        /// <summary>
        /// The player-override rung of <see cref="TacticResolver"/> for a fleet's lead, or null. The
        /// one place that rung is sourced: today only <c>factions bench arm</c> sets it, for a bench
        /// fleet. A player's standing override is to be read here too, so the flight controller and
        /// the resolver need no change when it lands.
        /// </summary>
        private TacticOrder? TacticOverrideFor(PendingFleetAction fleet)
        {
            if (_benchArms.Count == 0 || !fleet.IsBench || fleet.FleetId == null) return null;
            TacticOrder order;
            return _benchArms.TryGetValue(fleet.FleetId, out order) ? order : (TacticOrder?)null;
        }

        /// <summary><c>bench arm</c>: sets (or, with a null <paramref name="arm"/>, clears) the arm a
        /// scenario fleet's lead fights with, through the player-override rung. <paramref name="side"/>
        /// is the fleet id's last part (<c>atk</c>, <c>def</c>, <c>raid</c>) or <c>all</c>. Returns
        /// the command response, or null when the scenario has no such fleet.</summary>
        internal string BenchArm(string label, string side, TacticArm? arm, BreakOffLevel breakOff)
        {
            var hit = new List<string>();
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (!f.IsBench || f.FleetId == null
                        || !string.Equals(f.BenchLabel, label, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(side, "all", StringComparison.OrdinalIgnoreCase)
                        && !f.FleetId.EndsWith("." + side, StringComparison.OrdinalIgnoreCase)) continue;
                    if (arm.HasValue)
                        _benchArms[f.FleetId] = new TacticOrder { Arm = arm.Value, BreakOff = breakOff };
                    else
                        _benchArms.Remove(f.FleetId);
                    hit.Add(f.LogId);
                }
            if (hit.Count == 0) return null;
            _log.Write("BENCH_ARM " + label + " " + string.Join(",", hit)
                       + " arm=" + (arm.HasValue ? arm.Value + "/" + breakOff : "cleared"));
            return string.Join(", ", hit) + (arm.HasValue ? " fight " + arm.Value + "/" + breakOff : " fight their own pick")
                   + " from the next tactical read (about a second).";
        }

        /// <summary>Drops bench arms whose fleet is gone.</summary>
        private void SweepBenchArms()
        {
            if (_benchArms.Count == 0) return;
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (f.IsBench && f.FleetId != null) live.Add(f.FleetId);
            List<string> gone = null;
            foreach (string id in _benchArms.Keys)
                if (!live.Contains(id)) (gone ?? (gone = new List<string>())).Add(id);
            if (gone != null) foreach (string id in gone) _benchArms.Remove(id);
        }

        /// <summary>A fighting fleet's learned pick, per fleet id, re-read every
        /// <see cref="LearnedOrderRefresh"/>: the pick changes only when an outcome is recorded.</summary>
        private readonly Dictionary<string, KeyValuePair<DateTime, TacticOrder?>> _learnedOrders =
            new Dictionary<string, KeyValuePair<DateTime, TacticOrder?>>(StringComparer.Ordinal);

        private static readonly TimeSpan LearnedOrderRefresh = TimeSpan.FromSeconds(5);

        /// <summary>The faction's learned pick for the dwell fight this fleet is in, for its lead and,
        /// through the lead's plan, its escorts: the same pick the duel and the commit gates use
        /// (<see cref="PendingFleetResolver.LearnedStanceFor"/>). Null when it learns nothing there.</summary>
        private TacticOrder? LearnedOrder(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            string key = fleet.FleetId;
            if (string.IsNullOrEmpty(key)) return null;
            KeyValuePair<DateTime, TacticOrder?> cached;
            if (_learnedOrders.TryGetValue(key, out cached) && now < cached.Key) return cached.Value;
            if (_learnedOrders.Count > 256) _learnedOrders.Clear();

            TacticOrder? order = null;
            int si = _states.IndexOf(st);
            TacticArm arm;
            BreakOffLevel breakOff;
            if (si >= 0 && PendingFleetResolver.LearnedStanceFor(_states, _world, _tuning, si, fi, out arm, out breakOff))
                order = new TacticOrder { Arm = arm, BreakOff = breakOff, Source = TacticSource.Learned };
            _learnedOrders[key] = new KeyValuePair<DateTime, TacticOrder?>(now + LearnedOrderRefresh, order);
            return order;
        }

        /// <summary>The opponent of the fleet with id <paramref name="fleetId"/>, for the flight
        /// controller's once-a-second tactical read: its grids into <paramref name="grids"/>, and the
        /// faction whose grids count too, or null. See <see cref="TargetSelection.CollectOpponents"/>.</summary>
        private string OpponentsForFleet(string fleetId, ICollection<long> grids)
        {
            if (string.IsNullOrEmpty(fleetId) || _states == null) return null;
            for (int si = 0; si < _states.Count; si++)
            {
                List<PendingFleetAction> fleets = _states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                    if (string.Equals(fleets[fi].FleetId, fleetId, StringComparison.Ordinal))
                        return TargetSelection.CollectOpponents(_states, si, fi, grids);
            }
            return null;
        }

        /// <summary>The weapon bands of the fleet a grid belongs to — as its lead or an escort — or
        /// null when it belongs to none. A copy: the controller holds it across reads.</summary>
        private IReadOnlyList<WeaponBand> FleetBandsForGrid(long entityId)
        {
            if (entityId <= 0 || _states == null) return null;
            foreach (FactionState st in _states)
            {
                for (int i = 0; i < st.PendingFleets.Count; i++)
                {
                    PendingFleetAction f = st.PendingFleets[i];
                    bool ours = f.MaterialisedEntityId == entityId
                                || (f.EscortEntityIds != null && f.EscortEntityIds.Contains(entityId));
                    if (!ours) continue;
                    return f.WeaponBands != null && f.WeaponBands.Count > 0
                        ? new List<WeaponBand>(f.WeaponBands)
                        : null;
                }
            }
            return null;
        }
    }
}
