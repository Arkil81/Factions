using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public static partial class PendingFleetResolver
    {
        /// <summary>
        /// Enemy fleets in a faction's space (<see cref="FleetTrespass.Scan"/>), and the catch: a fleet
        /// whose stay has reached <see cref="TrespassRung.Intercept"/> meets the faction's Guard standing
        /// at the marked node. A carrier gets the convoy verdict with the Guard in the raider's place
        /// (<see cref="ResolveConvoyEncounter"/>: held for the interception window, then robbed, fought
        /// or let go); an armed fleet in transit duels the Guard and the node's guns in the attacker's
        /// place (<see cref="ResolvePatrolVsRaid"/>).
        ///
        /// <para>Abstract only. A fleet or Guard with a grid is settled by the grids: the mark names the
        /// fleet's grid, and a materialised Guard shadows and pursues it. One catch per Guard per pass,
        /// and a Guard catches a given convoy once (<see cref="AlreadyMet"/>), so a robbed convoy still
        /// in the space keeps the node marked without being robbed again.</para>
        ///
        /// <para>Provenance: FAC-483 slice 5.</para>
        /// </summary>
        private static void ResolveTrespassCatches(
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng, IList<string> log,
            IItemPriceCatalogue prices, IItemVolumeCatalogue itemVolumes, HashSet<(int si, int fi)> encountered)
        {
            var sightings = new List<FleetTrespass.Sighting>();
            FleetTrespass.Scan(states, world, tuning, itemVolumes, log, sightings);
            if (sightings.Count == 0) return;

            DateTime now = world.UtcNow;
            HashSet<(int si, int fi)> caughtBy = null;
            for (int i = 0; i < sightings.Count; i++)
            {
                FleetTrespass.Sighting seen = sightings[i];
                if (seen.Rung != TrespassRung.Intercept) continue;
                PendingFleetAction f = states[seen.Si].PendingFleets[seen.Fi];
                if (f.Intercepted || f.CurrentStopKind == FleetStopKind.Intercept) continue;
                if (f.MaterialisedEntityId != 0) continue;
                FleetTrespass.CatchKind kind = FleetTrespass.CatchOf(f);
                if (kind == FleetTrespass.CatchKind.None) continue;
                if (encountered != null && encountered.Contains((seen.Si, seen.Fi))) continue;

                if (caughtBy == null) caughtBy = new HashSet<(int si, int fi)>();
                (int si, int fi) guardRef = TrespassCatcher(states, seen, f, now, tuning, caughtBy);
                if (guardRef.si < 0) continue;
                caughtBy.Add(guardRef);
                if (encountered != null) encountered.Add((seen.Si, seen.Fi));

                PendingFleetAction guard = states[guardRef.si].PendingFleets[guardRef.fi];
                if (log != null)
                    log.Add("TRESPASS_CATCH " + guard.LogId + " " + guard.ActorTag + " caught " + f.LogId + " " + f.ActorTag
                            + " kind=" + f.Kind + " @" + seen.NodeId
                            + " verdict=" + (kind == FleetTrespass.CatchKind.Convoy ? "convoy" : "duel"));
                if (kind == FleetTrespass.CatchKind.Convoy)
                    ResolveConvoyEncounter(guardRef, (seen.Si, seen.Fi), seen.NodeId, 1.0, "", "",
                                           states, world, tuning, rng, log, prices);
                else
                    ResolvePatrolVsRaid((seen.Si, seen.Fi), guardRef, states, world, tuning, rng, log, seen.NodeId);
            }
        }

        /// <summary>The space owner's abstract Guard standing its duty at the marked node that has not
        /// caught anything this pass and has not already met this convoy, or (-1, -1).</summary>
        private static (int si, int fi) TrespassCatcher(IList<FactionState> states, FleetTrespass.Sighting seen,
            PendingFleetAction intruder, DateTime now, FactionTuning tuning, HashSet<(int si, int fi)> caughtBy)
        {
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st == null || !string.Equals(st.Tag, seen.OwnerTag, StringComparison.Ordinal)) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction g = st.PendingFleets[fi];
                    if (g.Kind != ActionKind.Guard || g.IsBench || g.MaterialisedEntityId != 0) continue;
                    if (!string.Equals(g.TargetNodeId, seen.NodeId, StringComparison.Ordinal)) continue;
                    if (!g.IsActiveGuard(now, tuning) || string.IsNullOrEmpty(g.FleetId)) continue;
                    if (caughtBy.Contains((si, fi)) || AlreadyMet(intruder, g)) continue;
                    return (si, fi);
                }
            }
            return (-1, -1);
        }

        /// <summary>Whether <paramref name="captor"/> is a Guard standing its duty at
        /// <paramref name="node"/>: it holds a convoy there by its duty, as a dwelling raid does by its
        /// dwell, and is not pinned again.</summary>
        private static bool GuardHeldAt(PendingFleetAction captor, string node, DateTime now, FactionTuning tuning)
        {
            return captor.Kind == ActionKind.Guard && captor.IsActiveGuard(now, tuning)
                   && string.Equals(captor.TargetNodeId, node, StringComparison.Ordinal);
        }
    }
}
