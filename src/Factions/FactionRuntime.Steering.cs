using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Civilian grids the host steers itself rather than flying straight down their leg: a ship under
    /// hostile fire runs (<see cref="CivilianFlee"/>), and a miner on its dwell flies the ring around
    /// its deposit (<see cref="MineWander"/>). While either holds, the grid's leg is not completed
    /// here -- a fleeing ship is not arriving anywhere, and a miner's dwell is ended by Core on its
    /// own clock -- but every other check (stall, capture, leaving range) still applies.
    /// See docs/fleet-events.md, "Mining ships work a ring" and "Civilian ships run from fire".
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private enum Steer { None, Flee, Wander }

        private struct FleeRun
        {
            public Vec3 PointKm;
            public DateTime LastHitUtc;
            public DateTime AimedUtc;
        }

        private readonly Dictionary<long, FleeRun> _fleeing = new Dictionary<long, FleeRun>();

        /// <summary>The waypoint each wandering miner grid is flying to (<see cref="MineWander.Waypoint"/>).</summary>
        private readonly Dictionary<long, int> _wanderLeg = new Dictionary<long, int>();

        private readonly List<long> _steerScratch = new List<long>();
        private DateTime _nextSteerPruneUtc;
        private static readonly TimeSpan SteerPruneInterval = TimeSpan.FromSeconds(10);

        /// <summary>Whether a materialised civilian grid is steered by flee or wander this frame, and
        /// where to. <see cref="Steer.None"/> leaves it to its leg.</summary>
        private Steer SteerTraffic(PendingFleetAction fleet, Vec3 posKm, DateTime now, out Vec3 steerTo)
        {
            steerTo = default(Vec3);
            long eid = fleet.MaterialisedEntityId;
            if (eid <= 0 || fleet.IsBench || !FactionsLoadActuator.IsTraffic(fleet.Kind)) return Steer.None;
            // An intercepted convoy is parked beside its raider to fight it out, not to run.
            if (fleet.HoldsStation(now, _tuning))
            {
                _fleeing.Remove(eid);
                return Steer.None;
            }
            if (Flee(fleet, eid, posKm, now, out steerTo)) return Steer.Flee;
            if (Wander(fleet, eid, posKm, now, out steerTo)) return Steer.Wander;
            return Steer.None;
        }

        private bool Flee(PendingFleetAction fleet, long eid, Vec3 posKm, DateTime now, out Vec3 steerTo)
        {
            steerTo = default(Vec3);
            FleeRun run;
            bool running = _fleeing.TryGetValue(eid, out run);

            long attacker = 0;
            DateTime? hit = null;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla != null)
                hit = vanilla.RecentHostileHit(eid, fleet.ActorTag, now, out attacker);

            if (hit.HasValue && (!running || hit.Value > run.LastHitUtc))
            {
                bool aim = !running
                           || (now - run.AimedUtc).TotalSeconds >= CivilianFlee.ReaimSeconds
                           || posKm.DistanceTo(run.PointKm) <= CivilianFlee.ReachedKm;
                Vec3? shooterKm = aim ? _spawner.GetGridPositionKm(attacker) : null;
                if (aim && shooterKm.HasValue)
                {
                    run.PointKm = CivilianFlee.PointFrom(posKm, shooterKm.Value, UpAt(posKm));
                    run.AimedUtc = now;
                    bool wired = _spawner.Retarget(eid, run.PointKm);
                    if (!running)
                        _log.Write("FLEET_FLEEING " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                                   + " eid=" + eid + " by=" + attacker + " rewired=" + wired);
                }
                else if (!running)
                {
                    // A shooter already gone leaves nothing to run from.
                    return false;
                }
                run.LastHitUtc = hit.Value;
                _fleeing[eid] = run;
                steerTo = run.PointKm;
                return true;
            }

            if (!running) return false;
            if (CivilianFlee.StillRunning(run.LastHitUtc, now))
            {
                steerTo = run.PointKm;
                return true;
            }

            // Unhit long enough: back to the leg. A miner re-acquires its ring below.
            _fleeing.Remove(eid);
            _wanderLeg.Remove(eid);
            bool resumed = MineWander.Applies(fleet) || _spawner.Retarget(eid, fleet.TargetPosKm);
            _log.Write("FLEET_FLEE_OVER " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " stop=" + fleet.CurrentStopKind + " rewired=" + resumed);
            return false;
        }

        private bool Wander(PendingFleetAction fleet, long eid, Vec3 posKm, DateTime now, out Vec3 steerTo)
        {
            steerTo = default(Vec3);
            int leg;
            bool had = _wanderLeg.TryGetValue(eid, out leg);
            if (!MineWander.Applies(fleet))
            {
                if (!had) return false;
                // The dwell is over and Core has put the fleet on its delivery leg.
                _wanderLeg.Remove(eid);
                bool wired = _spawner.Retarget(eid, fleet.TargetPosKm);
                _log.Write("MINE_WANDER_END " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                           + " -> " + (fleet.TargetNodeId ?? "[navigation]") + " rewired=" + wired);
                return false;
            }

            MineWander.Ring ring = MineWander.RingOf(fleet);
            if (!had)
            {
                double fraction;
                leg = MineWander.LegAt(fleet, ring, now, _tuning, out fraction);
                // A grid still launching is flown to this waypoint by the launch; anything else is
                // re-aimed, which covers a grid that arrived at the deposit on its way in.
                bool wired = _spawner.IsManoeuvring(eid) || _spawner.Retarget(eid, MineWander.Waypoint(ring, leg));
                _log.Write("MINE_WANDER " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                           + " @ " + (fleet.TargetNodeId ?? "?") + " leg=" + leg
                           + " until=" + fleet.EstimatedArrivalUtc.ToString("HH:mm:ss") + " rewired=" + wired);
            }
            else if (posKm.DistanceTo(MineWander.Waypoint(ring, leg)) <= MineWander.ArriveKm)
            {
                leg++;
                _spawner.Retarget(eid, MineWander.Waypoint(ring, leg));
            }
            _wanderLeg[eid] = leg;
            steerTo = MineWander.Waypoint(ring, leg);
            return true;
        }

        /// <summary>Where a miner on its dwell is aimed at spawn: the waypoint its abstract position is
        /// heading for. Any other fleet flies its leg's target.</summary>
        private Vec3 SpawnTargetFor(PendingFleetAction fleet, DateTime now)
        {
            if (!MineWander.Applies(fleet)) return fleet.TargetPosKm;
            MineWander.Ring ring = MineWander.RingOf(fleet);
            double fraction;
            return MineWander.Waypoint(ring, MineWander.LegAt(fleet, ring, now, _tuning, out fraction));
        }

        private Vec3? UpAt(Vec3 posKm)
        {
            IPlanetCatalogue planets = _world != null ? _world.Planets : null;
            if (planets == null || planets.GravityAt(posKm) <= 0) return null;
            return planets.SurfaceNormal(posKm);
        }

        /// <summary>Drops steering state for grids that have left the world, every
        /// <see cref="SteerPruneInterval"/>.</summary>
        private void PruneSteering(DateTime now)
        {
            if (now < _nextSteerPruneUtc || (_fleeing.Count == 0 && _wanderLeg.Count == 0)) return;
            _nextSteerPruneUtc = now + SteerPruneInterval;
            _steerScratch.Clear();
            foreach (long eid in _fleeing.Keys) if (!_spawner.GridExists(eid)) _steerScratch.Add(eid);
            for (int i = 0; i < _steerScratch.Count; i++) _fleeing.Remove(_steerScratch[i]);
            _steerScratch.Clear();
            foreach (long eid in _wanderLeg.Keys) if (!_spawner.GridExists(eid)) _steerScratch.Add(eid);
            for (int i = 0; i < _steerScratch.Count; i++) _wanderLeg.Remove(_steerScratch[i]);
        }
    }
}
