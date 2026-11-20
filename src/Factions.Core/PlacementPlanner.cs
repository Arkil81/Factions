using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One faction's claim on one region.</summary>
    public sealed class PlacementAssignment
    {
        public string Tag;
        public PlacementRegion Region;
        /// <summary>How many nodes. Always explicit — see <see cref="PlacementPlanner"/> for why a
        /// region alone is not a budget.</summary>
        public int Nodes;
    }

    /// <summary>What one region was asked for against what it can hold.</summary>
    public sealed class PlacementRegionDemand
    {
        public PlacementRegion Region;
        public int Claimants;
        public int NodesRequested;
        public double Capacity;

        public bool IsOverSubscribed { get { return NodesRequested > Capacity; } }

        /// <summary>Nodes per claimant the region can actually support. Reported so an author can
        /// re-cut the allocation rather than guess at it.</summary>
        public double NodesPerClaimant
        {
            get { return Claimants > 0 ? Capacity / Claimants : Capacity; }
        }
    }

    /// <summary>The outcome of planning an estate: the demand table, and whether it can be met.</summary>
    public sealed class PlacementPlan
    {
        public readonly List<PlacementRegionDemand> Regions = new List<PlacementRegionDemand>();
        public readonly List<string> Refusals = new List<string>();

        public bool IsFeasible { get { return Refusals.Count == 0; } }

        public int TotalNodesRequested
        {
            get { int n = 0; foreach (PlacementRegionDemand d in Regions) n += d.NodesRequested; return n; }
        }
    }

    /// <summary>
    /// Turns per-faction estate assignments into a feasibility verdict, region by region.
    ///
    /// <para><b>This refuses; it does not degrade.</b> <c>WorldModel.ExpandFrom</c> retries a bounded
    /// number of times and then commits the node regardless, so an over-subscribed region yields a
    /// world that looks seeded and quietly breaks its own separation floor. Every capacity finding on
    /// the live system has that shape: Kor's 9.5 km surface holds four nodes at a 15 km floor against
    /// eleven assigned factions, and nothing in the placement path would have said so.</para>
    ///
    /// <para><b>Why an explicit node count is required.</b> A region used to be its own budget — at a
    /// 500 km deep-space floor a 200 km feature held 0.31 nodes, so "fill it" was bounded by geometry.
    /// At a 50 km floor the same feature holds ~307, and an unbounded fill would produce an estate
    /// orders of magnitude past what was intended, with no error anywhere. So an assignment carries a
    /// count, and a non-positive one is refused rather than treated as "as many as fit".</para>
    /// </summary>
    public static class PlacementPlanner
    {
        /// <summary>
        /// Build the plan. <paramref name="geometryFor"/> resolves a region to its live geometry and
        /// <paramref name="separationFor"/> to its separation floor; both return false for a region
        /// the world does not contain, which is itself a refusal rather than a skip.
        /// </summary>
        public static PlacementPlan Plan(
            IEnumerable<PlacementAssignment> assignments,
            Func<PlacementRegion, PlacementRegionGeometry> geometryFor,
            Func<PlacementRegion, double> separationFor)
        {
            var plan = new PlacementPlan();
            if (assignments == null) return plan;

            var byRegion = new Dictionary<string, PlacementRegionDemand>(StringComparer.Ordinal);
            var seenTags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (PlacementAssignment a in assignments)
            {
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.Tag)) { plan.Refusals.Add("an assignment has no faction tag."); continue; }
                if (a.Region == null) { plan.Refusals.Add(a.Tag + ": assignment has no region."); continue; }

                // A region with no count is not "as many as fit" -- see the class remarks.
                if (a.Nodes <= 0)
                {
                    plan.Refusals.Add(a.Tag + " -> " + a.Region
                        + ": node count must be explicit and positive (got " + a.Nodes + ").");
                    continue;
                }

                string key = a.Region.Kind + "|" + (a.Region.Key ?? "");
                PlacementRegionDemand demand;
                if (!byRegion.TryGetValue(key, out demand))
                {
                    PlacementRegionGeometry geometry = geometryFor != null ? geometryFor(a.Region) : null;
                    if (geometry == null)
                    {
                        plan.Refusals.Add(a.Tag + " -> " + a.Region + ": no such region in this world.");
                        continue;
                    }

                    double separation = separationFor != null ? separationFor(a.Region) : 0;
                    demand = new PlacementRegionDemand
                    {
                        Region = a.Region,
                        Capacity = PlacementCapacity.MaxNodes(a.Region.Kind, geometry, separation),
                    };
                    byRegion[key] = demand;
                    seenTags[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    plan.Regions.Add(demand);
                }

                demand.NodesRequested += a.Nodes;
                if (seenTags[key].Add(a.Tag)) demand.Claimants++;
            }

            foreach (PlacementRegionDemand d in plan.Regions)
            {
                if (!d.IsOverSubscribed) continue;
                plan.Refusals.Add(Describe(d));
            }

            return plan;
        }

        /// <summary>One over-subscribed region, in the terms an author needs to re-cut it.</summary>
        public static string Describe(PlacementRegionDemand d)
        {
            return d.Region + ": " + d.NodesRequested + " node(s) requested by " + d.Claimants
                 + " faction(s), capacity " + d.Capacity.ToString("0.0")
                 + " -- " + d.NodesPerClaimant.ToString("0.00") + " per faction. "
                 + "Placement would commit the overflow anyway and break the separation floor.";
        }
    }
}
