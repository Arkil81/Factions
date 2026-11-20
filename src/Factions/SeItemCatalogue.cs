using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// SE Economy 2-backed item catalogue. Reads <see cref="MyPhysicalItemDefinition.MinimalPricePerUnit"/>
    /// for one representative item per <see cref="ItemKind"/> and uses those values for the
    /// surplus→credits conversion in <see cref="WorldModel.AccrueIncome"/>.
    /// Falls back to <see cref="FactionTuning.FallbackFor"/> for any kind whose SE definition is
    /// unavailable (Economy 2 disabled, definition manager not yet ready, etc.).
    /// </summary>
    public sealed class SeItemCatalogue : IItemCatalogue
    {
        private readonly FactionTuning _fallback;
        private readonly Dictionary<ItemKind, long> _cache = new Dictionary<ItemKind, long>();

        // One representative SE item per ItemKind (type string / subtype string).
        // Iron ore  → baseline commodity; SteelPlate → mid-tier component.
        // Hydrogen and Oxygen deliberately have none: SE has no physical item for a gas, and the
        // bottles that used to stand in for them are assembler products that come out EMPTY. Pricing
        // a litre as a bottle multiplied every litre a node held by a manufactured hull price --
        // WorldModel.NodeInventoryValue would have moved faction net worth by orders of magnitude
        // the first tick gas appeared. Both kinds fall through to FactionTuning.FallbackFor, which
        // carries the kilolitre price -- the unit SE quotes MinimumHydrogenPrice in.
        private static readonly string[] s_types    = new string[]
        {
            "MyObjectBuilder_Ore",
            "MyObjectBuilder_Component",
            null,                    // Hydrogen: no physical item exists; see the note above
            null,                    // Oxygen:   ditto
            "MyObjectBuilder_Ore",   // RareOre: Silver ore as price representative
            "MyObjectBuilder_Ingot", // Ingot: Iron ingot as price representative
        };
        private static readonly string[] s_subtypes = new string[]
        {
            "Iron",
            "SteelPlate",
            null,
            null,
            "Silver",
            "Iron",
        };
        private static readonly ItemKind[] s_kinds = new ItemKind[]
        {
            ItemKind.Ore,
            ItemKind.ProcessedGood,
            ItemKind.Hydrogen,
            ItemKind.Oxygen,
            ItemKind.RareOre,
            ItemKind.Ingot,
        };

        /// <param name="fallback">Used for any kind whose SE price is unavailable.</param>
        public SeItemCatalogue(FactionTuning fallback)
        {
            _fallback = fallback;
            TryBuildCache();
        }

        private void TryBuildCache()
        {
            if (MyDefinitionManager.Static == null) return;
            for (int i = 0; i < s_kinds.Length; i++)
            {
                try
                {
                    if (s_types[i] == null || s_subtypes[i] == null) continue;
                    MyDefinitionId id;
                    if (!MyDefinitionId.TryParse(s_types[i], s_subtypes[i], out id)) continue;
                    MyPhysicalItemDefinition def;
                    if (!MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out def) || def == null) continue;
                    if (def.MinimalPricePerUnit > 0)
                        _cache[s_kinds[i]] = def.MinimalPricePerUnit;
                }
                catch { /* definition manager not ready or Economy 2 unavailable — fall through */ }
            }
        }

        public long CreditValuePer(ItemKind kind)
        {
            long v;
            return _cache.TryGetValue(kind, out v) ? v : _fallback.FallbackFor(kind);
        }

        public string DisplayName(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return "Ore";
                case ItemKind.ProcessedGood: return "ProcessedGood";
                case ItemKind.Hydrogen:      return "Hydrogen";
                case ItemKind.Oxygen:        return "Oxygen";
                default:                     return kind.ToString();
            }
        }

        /// <summary>Returns how many ItemKind mappings were resolved from SE definitions.
        /// Used for logging in the plugin startup path.</summary>
        public int ResolvedCount { get { return _cache.Count; } }

        /// <summary>One line per <see cref="ItemKind"/> whose representative item's price sits far
        /// outside the spread of real prices among the items <paramref name="registry"/> assigns to
        /// that kind. Empty when every representative looks typical of its kind.
        ///
        /// <para>Detection only -- nothing here changes a price. A whole kind is valued from a single
        /// hand-picked subtype, so a mod that rewrites just that one definition in place moves the
        /// kind's entire valuation and nothing else in the load path would say so. Whether the right
        /// answer is a different statistic is a balance question, not a reporting one; this makes the
        /// condition visible so it can be judged against real numbers. See docs/mod-compatibility.md,
        /// class 4.</para></summary>
        public List<string> RepresentativeOutliers(IItemRegistry registry)
        {
            var report = new List<string>();
            if (registry == null || MyDefinitionManager.Static == null) return report;

            var byKind = new Dictionary<ItemKind, List<long>>();
            try
            {
                foreach (MyPhysicalItemDefinition def in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
                {
                    if (def == null || def.MinimalPricePerUnit <= 0) continue;
                    ItemKind kind;
                    if (!registry.TryGetItemKind(def.Id.SubtypeName, out kind)) continue;

                    List<long> prices;
                    if (!byKind.TryGetValue(kind, out prices)) byKind[kind] = prices = new List<long>();
                    prices.Add(def.MinimalPricePerUnit);
                }
            }
            catch { return report; }

            for (int i = 0; i < s_kinds.Length; i++)
            {
                ItemKind kind = s_kinds[i];
                long representative;
                if (!_cache.TryGetValue(kind, out representative) || representative <= 0) continue;

                List<long> prices;
                if (!byKind.TryGetValue(kind, out prices) || prices.Count < 3) continue;

                prices.Sort();
                long median = prices[prices.Count / 2];
                if (median <= 0) continue;

                // An order of magnitude either way. Wide on purpose: ore prices legitimately span a
                // large range, so a narrow band would report the normal case every boot.
                double ratio = (double)representative / median;
                if (ratio >= 0.1 && ratio <= 10.0) continue;

                report.Add(string.Format(
                    "{0}: representative {1}/{2} = {3}, but the {4} priced item(s) in this kind have median {5} (min {6}, max {7})",
                    kind, s_types[i], s_subtypes[i], representative, prices.Count, median,
                    prices[0], prices[prices.Count - 1]));
            }
            return report;
        }
    }
}
