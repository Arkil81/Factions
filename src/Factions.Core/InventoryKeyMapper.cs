using System;

namespace Factions.Core
{
    public static class InventoryKeyMapper
    {
        public static string FromTypeIdAndSubtype(string typeIdStr, string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return null;
            if (string.IsNullOrEmpty(typeIdStr)) return subtype;

            if (typeIdStr.EndsWith("_Ore", StringComparison.OrdinalIgnoreCase)) return "Ore:" + subtype;
            if (typeIdStr.EndsWith("_Ingot", StringComparison.OrdinalIgnoreCase)) return "Ingot:" + subtype;
            if (typeIdStr.EndsWith("_Component", StringComparison.OrdinalIgnoreCase)) return "Comp:" + subtype;
            // Gas has no MyPhysicalItemDefinition, so it never reaches this mapper from an item scan
            // -- only from a recipe output or a faction type's offer list. Without this arm it fell
            // through as a bare "Hydrogen", which no registry can classify.
            if (typeIdStr.EndsWith("_GasProperties", StringComparison.OrdinalIgnoreCase)) return "Gas:" + subtype;
            // SE names each seed after its crop (SeedItem/Fruit grows ConsumableItem/Fruit), so a
            // bare key would put a 100-credit seed and a 1,240-credit fruit in one stock row.
            if (typeIdStr.EndsWith("_SeedItem", StringComparison.OrdinalIgnoreCase)) return SeedPrefix + subtype;
            return subtype;
        }

        /// <summary>Prefix every seed inventory key carries. Retained through classification the way
        /// <c>Ingot:</c> and <c>Gas:</c> are, because the bare subtype is the crop's.</summary>
        public const string SeedPrefix = "Seed:";

        /// <summary>Whether an inventory key names a seed.</summary>
        public static bool IsSeedKey(string invKey)
        {
            return invKey != null && invKey.StartsWith(SeedPrefix, StringComparison.Ordinal);
        }

        /// <summary>Prefix every ore inventory key carries. Rare ores share it: the mapper keys off
        /// the definition's <c>_Ore</c> TypeId suffix, which <c>MyObjectBuilder_Ore</c> satisfies for
        /// common and rare alike.</summary>
        public const string OrePrefix = "Ore:";

        /// <summary>Prefix every gas inventory key carries. One unit is one litre — the unit SE's own
        /// <c>IceToGasRatio</c> is quoted in.</summary>
        public const string GasPrefix = "Gas:";

        /// <summary>Whether an inventory key names gas. Prefix, never a subtype: gas and its bottle
        /// share a name, and they are not the same thing.</summary>
        public static bool IsGasKey(string invKey)
        {
            return invKey != null
                && invKey.StartsWith(GasPrefix, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether an inventory key names an ore. Matches on the prefix, never a subtype —
        /// testing "Stone" by subtype also matches <c>Ingot:Stone</c>.</summary>
        public static bool IsOreKey(string invKey)
        {
            return !string.IsNullOrEmpty(invKey)
                && invKey.StartsWith(OrePrefix, StringComparison.Ordinal);
        }
    }
}
