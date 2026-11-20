using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One weighted candidate deposit material for an asteroid field, resolved down to a
    /// bare SE voxel-material-ish ore name (the "Ore:"/"RareOre:" prefix from
    /// <see cref="NodeYieldEntry.SubTypeId"/> stripped off). The host resolves this name to an
    /// actual <c>MyVoxelMaterialDefinition</c> (there may be several variants per ore, e.g.
    /// "Iron_01"/"Iron_02" -- picking among them is a host-side, SE-specific concern).</summary>
    public struct AsteroidDepositCandidate
    {
        public string OreName;
        public double Weight;
        public AsteroidDepositCandidate(string oreName, double weight) { OreName = oreName; Weight = weight; }
    }

    /// <summary>Pure (SE-free) plan for one node's physical asteroid field: how many asteroids, how
    /// big, how far apart, and what they're made of. All of it single-sourced from
    /// <see cref="FactionTuning.AsteroidField"/> (count/size/radius) and the existing
    /// <see cref="FactionTuning.NodeYieldTable"/>/<see cref="FactionTuning.FreeMintScale"/>
    /// (which ores, and how heavily rare ore is favoured over common ore) -- the same tables that
    /// already govern a node's economic yield now also decide what its asteroids visually contain,
    /// so there is nothing new to separately balance.</summary>
    public sealed class AsteroidFieldPlan
    {
        public int    Count;
        public double SizeMin;
        public double SizeMax;
        public double RadiusKm;
        public string BaseMaterialOreName;                    // "Ice" or "Stone"
        public List<AsteroidDepositCandidate> Deposits = new List<AsteroidDepositCandidate>();
    }

    public static class AsteroidFieldPlanner
    {
        /// <summary>Build the field plan for a node of the given kind, resource richness, and zone.
        /// Never null; an absent profile (<c>MaxCount == 0</c>) yields a
        /// zero-count plan the caller should simply skip -- as does any zone other than
        /// <see cref="NodeZone.DeepSpace"/>: Surface/Orbit nodes sit near a planet, where a
        /// materialized voxel field could obstruct terrain, vanilla asteroid belts, or a station's
        /// approach, none of which DeepSpace has to worry about.</summary>
        /// <summary>Allocation-free necessary condition for <see cref="Plan"/> returning a plan
        /// with a nonzero <see cref="AsteroidFieldPlan.Count"/>. Necessary but NOT sufficient --
        /// Count also scales with <c>resourceValue</c>, so a caller still has to run Plan and honour
        /// its own <c>Count &lt;= 0</c> check. The point is that Plan allocates (the plan object plus
        /// its Deposits list) before its first branch resolves, so a per-node, per-frame caller can
        /// use this to avoid allocating just to discover a node can never have a field. Zone is
        /// tested first: a plain enum compare rejects every Surface/Orbit node without touching the
        /// tuning dictionaries at all.</summary>
        public static bool CouldHaveField(SpatialNode node, NodeZone zone, FactionTuning tuning)
        {
            return zone == NodeZone.DeepSpace
                && tuning != null
                && NodeContent.IsMineable(node)
                && tuning.AsteroidField != null
                && tuning.AsteroidField.MaxCount > 0;
        }

        public static AsteroidFieldPlan Plan(SpatialNode node, double resourceValue, NodeZone zone, FactionTuning tuning)
        {
            if (zone != NodeZone.DeepSpace) return new AsteroidFieldPlan();
            // Rock is put where there is ore to find. The three per-kind rows this replaced carried
            // near-identical counts and sizes and were only ever nonzero for the mineable kinds, so
            // the label was a slow way of asking whether the node has a deposit.
            if (!NodeContent.IsMineable(node)) return new AsteroidFieldPlan();

            NodeKindAsteroidFieldEntry profile = tuning.GetAsteroidFieldProfile();
            double rv = MathUtil.Clamp01(resourceValue);

            var plan = new AsteroidFieldPlan
            {
                Count    = (int)System.Math.Round(profile.MaxCount * rv),
                SizeMin  = profile.SizeMin,
                SizeMax  = profile.SizeMax,
                RadiusKm = profile.RadiusKm,
                BaseMaterialOreName = NodeContent.IsIce(node, tuning) ? "Ice" : "Stone",
            };
            if (plan.Count <= 0) return plan;

            NodeYieldEntry[] yield = tuning.NodeYieldTable;
            if (yield == null) return plan;

            double oreScale     = tuning.FreeMintScaleFor(ItemKind.Ore);
            double rareOreScale = tuning.FreeMintScaleFor(ItemKind.RareOre);

            foreach (NodeYieldEntry entry in yield)
            {
                double scale;
                if (entry.ItemKind == "Ore") scale = oreScale;
                else if (entry.ItemKind == "RareOre") scale = rareOreScale;
                else continue; // Ingot/ProcessedGood/etc. entries aren't physical deposit materials

                string oreName = StripItemKindPrefix(entry.SubTypeId);
                if (oreName == null || oreName == plan.BaseMaterialOreName) continue; // already the base fill

                double weight = entry.Weight * scale;
                if (weight <= 0) continue;
                plan.Deposits.Add(new AsteroidDepositCandidate(oreName, weight));
            }
            return plan;
        }

        /// <summary>"Ore:Iron" / "RareOre:Silver" -> "Iron" / "Silver". Null if there's no ':'.</summary>
        private static string StripItemKindPrefix(string subTypeId)
        {
            if (string.IsNullOrEmpty(subTypeId)) return null;
            int i = subTypeId.IndexOf(':');
            return i >= 0 && i + 1 < subTypeId.Length ? subTypeId.Substring(i + 1) : null;
        }
    }
}
