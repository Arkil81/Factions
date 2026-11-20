using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Maps abstract item categories to their economy properties.
    /// The offline sim uses <see cref="TuningItemCatalogue"/> (backed by <see cref="FactionTuning"/>);
    /// the SE plugin overrides this with an SE-backed implementation before the first tick.</summary>
    public interface IItemCatalogue
    {
        /// <summary>Base policy price per abstract item unit. Used by central-bank asset purchases,
        /// trade realization, and ledger valuation.</summary>
        long CreditValuePer(ItemKind kind);

        /// <summary>Human-readable name for logging and future UI.</summary>
        string DisplayName(ItemKind kind);
    }

    /// <summary>Offline-sim implementation of <see cref="IItemCatalogue"/> backed by
    /// <see cref="FactionTuning"/> constants. Used by the visualiser, unit tests, and any
    /// context that has no SE runtime.</summary>
    public sealed class TuningItemCatalogue : IItemCatalogue
    {
        private readonly FactionTuning _t;

        public TuningItemCatalogue(FactionTuning t) { _t = t; }

        public long CreditValuePer(ItemKind kind) => _t.FallbackFor(kind);

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
    }

    // ---- per-SubTypeId price catalogue ----------------------------------------------------------

    /// <summary>
    /// Returns a base credit price per individual item unit for a given SE SubTypeId.
    /// The SE implementation (<c>SeItemPriceCatalogue</c>) loads from <c>item_prices.json</c>
    /// (primary) and seeds from <c>MySessionComponentEconomy.GetMinimumItemPrice</c> on first
    /// boot or for unknown entries. The offline/test sim uses null (callers fall back to
    /// <see cref="FactionTuning.FallbackFor"/>).
    /// </summary>
    public interface IItemPriceCatalogue
    {
        /// <summary>Base credit value per unit. Returns 0 for unknown SubTypeIds
        /// (caller should fall back to <see cref="FactionTuning.FallbackFor"/>).</summary>
        long BasePrice(string subTypeId);
    }

    // ---- item registry (Phase 3) ----------------------------------------------------------------

    /// <summary>
    /// Maps SE SubTypeId strings to their brain-facing <see cref="ItemKind"/> bucket.
    /// The SE plugin wires <c>SeItemRegistry</c> (backed by MyDefinitionManager); the offline sim
    /// and tests use <see cref="NullItemRegistry"/> (hard-coded common vanilla subtypes).
    ///
    /// Note: Some SubTypeIds are shared across TypeIds (e.g. "Iron" = Iron ore AND Iron ingot).
    /// By convention the sim's virtual inventory uses ore SubTypeIds only for <see cref="ItemKind.Ore"/>
    /// and component SubTypeIds for <see cref="ItemKind.ProcessedGood"/>, avoiding the collision.
    /// </summary>
    public interface IItemRegistry
    {
        /// <summary>Look up which ItemKind bucket a SubTypeId belongs to.
        /// Returns false for unknown subtypes (brain ignores them; managers may still use them).</summary>
        bool TryGetItemKind(string subTypeId, out ItemKind kind);

        /// <summary>One canonical SubTypeId for <paramref name="kind"/>. Used when adding or
        /// deducting items by ItemKind without a specific SubTypeId (e.g. action-cost deductions).</summary>
        string RepresentativeSubType(ItemKind kind);

        /// <summary>Sub-classification within ProcessedGood for cap sizing. Component for
        /// non-ProcessedGood kinds and anything unrecognized.</summary>
        ProcessedGoodCategory CategoryFor(string subTypeId);
    }

    /// <summary>
    /// Hard-coded <see cref="IItemRegistry"/> for the offline sim and unit tests.
    /// Covers SE vanilla ore and component SubTypeIds.
    /// <see cref="SeItemRegistry"/> (SE project) replaces this at plugin startup via
    /// <see cref="WorldModel.Registry"/>.
    /// </summary>
    public sealed class NullItemRegistry : IItemRegistry
    {
        public static readonly NullItemRegistry Instance = new NullItemRegistry();

        private static readonly Dictionary<string, ItemKind> s_map = new Dictionary<string, ItemKind>
        {
            // Ores (MyObjectBuilder_Ore) — keyed by bare SubTypeId after "Ore:" prefix is stripped
            { "Iron",      ItemKind.Ore }, { "Nickel",    ItemKind.Ore }, { "Cobalt",   ItemKind.Ore },
            { "Silicon",   ItemKind.Ore }, { "Magnesium", ItemKind.Ore }, { "Stone",    ItemKind.Ore },
            { "Ice",       ItemKind.Ore },
            // Every ore is ItemKind.Ore. "Rare" is not a category any more -- it is a continuous,
            // price-derived property of a deposit (DepositModel.LabelFor). The three-entry list that
            // used to live here disagreed with the host's, which made Ore:Uranium rare offline and
            // common live.
            { "Silver",    ItemKind.Ore }, { "Gold",    ItemKind.Ore }, { "Platinum", ItemKind.Ore },
            { "Uranium",   ItemKind.Ore },
            // Ingots (MyObjectBuilder_Ingot) — keyed with "Ingot:" prefix retained (StripInvKeyPrefix does NOT strip Ingot:)
            { "Ingot:Iron",      ItemKind.Ingot }, { "Ingot:Nickel",    ItemKind.Ingot },
            { "Ingot:Cobalt",    ItemKind.Ingot }, { "Ingot:Silicon",   ItemKind.Ingot },
            { "Ingot:Magnesium", ItemKind.Ingot }, { "Ingot:Silver",    ItemKind.Ingot },
            { "Ingot:Gold",      ItemKind.Ingot }, { "Ingot:Stone",     ItemKind.Ingot },
            { "Ingot:Platinum",  ItemKind.Ingot }, { "Ingot:Uranium",   ItemKind.Ingot },
            // Components (MyObjectBuilder_Component) → ProcessedGood
            { "SteelPlate",      ItemKind.ProcessedGood }, { "Construction",     ItemKind.ProcessedGood },
            { "MetalGrid",       ItemKind.ProcessedGood }, { "InteriorPlate",    ItemKind.ProcessedGood },
            { "GirderComponent", ItemKind.ProcessedGood }, { "SmallTube",        ItemKind.ProcessedGood },
            { "LargeTube",       ItemKind.ProcessedGood }, { "Motor",            ItemKind.ProcessedGood },
            { "Display",         ItemKind.ProcessedGood }, { "BulletproofGlass", ItemKind.ProcessedGood },
            { "Superconductor",  ItemKind.ProcessedGood }, { "Thrust",           ItemKind.ProcessedGood },
            { "Reactor",         ItemKind.ProcessedGood }, { "Medical",          ItemKind.ProcessedGood },
            { "SolarCell",       ItemKind.ProcessedGood }, { "PowerCell",        ItemKind.ProcessedGood },
            { "Computer",        ItemKind.ProcessedGood }, { "RadioCommunication", ItemKind.ProcessedGood },
            { "Detector",        ItemKind.ProcessedGood }, { "Explosives",       ItemKind.ProcessedGood },
            { "Girder",          ItemKind.ProcessedGood }, { "Canvas",           ItemKind.ProcessedGood },
            // Gas, in litres of an SE GasProperties definition. The "Gas:" prefix is retained
            // exactly like "Ingot:" -- StripInvKeyPrefix does NOT strip it.
            { "Gas:Hydrogen", ItemKind.Hydrogen },
            { "Gas:Oxygen",   ItemKind.Oxygen },
            // Bottles stay where they were. They are assembler-built EVA gear (80 Iron + 10 Silicon
            // + 30 Nickel) that SeItemRegistry classifies as ProcessedGood, so the two registries
            // disagree about them — but re-classing them here is an economy change, not a gas one,
            // and it belongs to whoever takes that question rather than to this chain.
            { "HydrogenBottle", ItemKind.Hydrogen },
            { "OxygenBottle",   ItemKind.Oxygen },
            // Crops, as SeItemRegistry classes them: Fruit/Vegetables/Mushrooms are ConsumableItems,
            // Grain and Algae PhysicalObjects, all ProcessedGood. Seeds are keyed "Seed:" and caught
            // by the prefix arm in TryGetItemKind, because their bare subtype is the crop's.
            { "Fruit",      ItemKind.ProcessedGood }, { "Vegetables", ItemKind.ProcessedGood },
            { "Mushrooms",  ItemKind.ProcessedGood }, { "Grain",      ItemKind.ProcessedGood },
            { "Algae",      ItemKind.ProcessedGood },
        };

        private static readonly string[] s_repr = new string[]
        {
            "Iron",            // Ore         (0) — ApplyInvKeyPrefix prepends "Ore:"
            "SteelPlate",      // ProcessedGood (1)
            "Gas:Hydrogen",    // Hydrogen    (2) - the gas itself; a bottle is an assembler product
            "Gas:Oxygen",      // Oxygen      (3) - that comes out empty, so it cannot represent gas
            null,              // RareOre     (4) — SE-specific; not needed in offline registry
            "Iron",            // Ingot       (5) — ApplyInvKeyPrefix prepends "Ingot:" → "Ingot:Iron"
        };

        public bool TryGetItemKind(string subTypeId, out ItemKind kind)
        {
            // SE names every meal MealPack_<dish>, all of them ConsumableItems.
            if (InventoryKeyMapper.IsSeedKey(subTypeId)
                || (subTypeId != null && subTypeId.StartsWith("MealPack_", System.StringComparison.Ordinal)))
            {
                kind = ItemKind.ProcessedGood;
                return true;
            }
            return s_map.TryGetValue(subTypeId, out kind);
        }

        public string RepresentativeSubType(ItemKind kind)
        {
            int i = (int)kind;
            return (i >= 0 && i < s_repr.Length) ? s_repr[i] : null;
        }

        public ProcessedGoodCategory CategoryFor(string subTypeId)
        {
            return ProcessedGoodCategory.Component;
        }
    }
}
