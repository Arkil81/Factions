using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>Fixture for the fleet-borne Reinforce (FAC-327 slice B): a reinforce is a squadron
    /// that flies to the node, holds an <see cref="FleetStopKind.Engage"/> dwell there and delivers
    /// its hold when that leg resolves. So every reinforce fixture needs two things the instant
    /// version did not — a combat hull somewhere other than the node being reinforced, and a clock
    /// run forward far enough for the mission to finish.</summary>
    internal static class ReinforceMissionSupport
    {
        /// <summary>Armed by name: any prefab handed to <see cref="Stock"/> carries guns, everything
        /// else does not.</summary>
        internal sealed class ArmedCatalogue : IPrefabCatalogue
        {
            public readonly HashSet<string> Armed = new HashSet<string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return Armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            /// <summary>Mass makes a hull route-gateable: <see cref="ShipPool.IsRouteFeasible"/>
            /// short-circuits to true at zero mass, so a fixture proving a route refusal has to give
            /// the hull some. With no thrust bands, any mass at all fails a gravity leg.</summary>
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public double DryMassKg(string prefabId)
            {
                double kg;
                return DryMassKgByPrefab.TryGetValue(prefabId, out kg) ? kg : 0;
            }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>Puts <paramref name="hulls"/> combat-suitable hulls in <paramref name="node"/>'s
        /// pool and makes sure the world's catalogue classifies them as armed.</summary>
        internal static void Stock(WorldModel w, SpatialNode node, int hulls, string prefabId = "Gunship")
        {
            var cat = w.PrefabCatalogue as ArmedCatalogue;
            if (cat == null) w.PrefabCatalogue = cat = new ArmedCatalogue();
            cat.Armed.Add(prefabId);
            if (hulls > 0) node.ShipStock[prefabId] = hulls;
        }

        /// <summary>The depot a reinforce flies out of: an owned node well away from a fixture's own
        /// coordinates, at full hold and stocked with warships. Never itself on a
        /// <c>ReinforcePriority</c> list, so it is a source and not also a target.</summary>
        internal static SpatialNode Depot(WorldModel w, string owner = "F", int hulls = 64,
                                          string id = "DEPOT", double distanceKm = 500)
        {
            // ResourceValue 0 deliberately: the depot must not mint, or a fixture asserting an exact
            // net flow would be reading the depot's income as well as the reinforce's cost.
            SpatialNode d = w.Graph.Add(new Vec3(0, 0, distanceKm), 0.0, id);
            d.OwnerFactionTag = owner;
            d.HoldStrength = 1.0;
            d.Radius = 40;
            Stock(w, d, hulls);
            return d;
        }



        /// <summary>One gravity value everywhere — the cheapest way to make a leg one a thrustless
        /// hull cannot fly.</summary>
        internal sealed class ConstantGravity : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravity(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>Total hulls held across every node's pool — what a leak or a duplication shows up
        /// in.</summary>
        internal static int TotalShipStock(WorldModel w)
        {
            int total = 0;
            foreach (SpatialNode n in w.Graph.Nodes.Values)
                foreach (KeyValuePair<string, int> kv in n.ShipStock) total += kv.Value;
            return total;
        }

        /// <summary>Steps the clock to each pending fleet's own arrival in turn and resolves it, up to
        /// <paramref name="legs"/> times — transit, then the dwell, then the leg home. Deterministic:
        /// the clock only ever moves to a time a fleet already named.</summary>
        internal static void FlyFleets(WorldModel w, FactionTuning t, IList<FactionState> states,
                                       IList<string> log, int legs = 8, IRandom rng = null)
        {
            if (rng == null) rng = new SeededRandom(1);
            // The world delivers the step from its OWN tuning --
            // <see cref="WorldModel.Reinforce(string,string,double)"/> reads
            // <see cref="WorldModel.Tuning"/>, the single-source rule, the same way WorldModel.Raid
            // reads its own constants. A fixture that overrides ReinforceStep on a separate
            // FactionTuning would otherwise dispatch on one set of numbers and resolve on another.
            w.Tuning = t;
            for (int i = 0; i < legs; i++)
            {
                DateTime next = DateTime.MaxValue;
                bool any = false;
                for (int si = 0; si < states.Count; si++)
                    for (int fi = 0; fi < states[si].PendingFleets.Count; fi++)
                    {
                        PendingFleetAction f = states[si].PendingFleets[fi];
                        if (f.Resolved || f.Intercepted) continue;
                        any = true;
                        if (f.EstimatedArrivalUtc > w.UtcNow && f.EstimatedArrivalUtc < next)
                            next = f.EstimatedArrivalUtc;
                    }
                if (!any) return;
                if (next != DateTime.MaxValue) w.UtcNow = next;
                PendingFleetResolver.ResolveArrivals(states, w, t, rng, log);
            }
        }
    }
}
