using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Resolves physical item volume (litres per unit) for volume-aware cargo loading.
    /// Pre-built at session load by scanning all MyPhysicalItemDefinition entries.
    /// InvKey convention mirrors ProductionManager: "Ore:SubType", "Ingot:SubType",
    /// "Comp:SubType", or bare SubTypeId for tools/ammo/bottles/etc.
    /// </summary>
    public sealed class SeItemVolumeCatalogue : IItemVolumeCatalogue
    {
        private readonly Dictionary<string, double> _volumes;
        public int Count { get { return _volumes.Count; } }

        public SeItemVolumeCatalogue()
        {
            _volumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try { Build(); } catch { }
        }

        private void Build()
        {
            if (MyDefinitionManager.Static == null) return;
            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var def = defBase as MyPhysicalItemDefinition;
                if (def == null || def.Volume <= 0f) continue;
                string invKey = ToInvKey(def.Id.TypeId.ToString(), def.Id.SubtypeName);
                if (!string.IsNullOrEmpty(invKey))
                    _volumes[invKey] = def.Volume * 1000.0; // m³/unit → litres/unit
            }

            AddGasVolumes();
        }

        /// <summary>Gas has no <see cref="MyPhysicalItemDefinition"/> — it is not an item — so the
        /// scan above can never see it, and an unknown key reads as volume 0, which would make gas
        /// free to haul. One unit of gas is one litre, by definition: an O2/H2 generator's
        /// <c>IceToGasRatio</c> is litres of gas per unit of ice, and that is the unit
        /// <c>SeRecipeRegistry</c>'s gas recipes are written in. So the number is read, not invented.
        ///
        /// <para>What a hull can actually carry is a separate question answered by its gas tanks
        /// (<c>MyGasTankDefinition.Capacity</c>), not by cargo volume; that belongs with the haulage
        /// leg, not here.</para></summary>
        private void AddGasVolumes()
        {
            foreach (string gas in new[] { "Hydrogen", "Oxygen", "Water" })
                _volumes["Gas:" + gas] = 1.0;
        }

        public double VolumeLitersPerUnit(string invKey)
        {
            if (string.IsNullOrEmpty(invKey)) return 0;
            double v;
            return _volumes.TryGetValue(invKey, out v) ? v : 0;
        }

        private static string ToInvKey(string typeId, string subType)
        {
            if (string.IsNullOrEmpty(subType)) return null;
            // TypeId strings are like "MyObjectBuilder_Ore", "MyObjectBuilder_Ingot", etc.
            if (typeId.Contains("Ore"))       return "Ore:"   + subType;
            if (typeId.Contains("Ingot"))     return "Ingot:" + subType;
            if (typeId.Contains("Component")) return "Comp:"  + subType;
            if (typeId.EndsWith("_SeedItem", StringComparison.Ordinal)) return InventoryKeyMapper.SeedPrefix + subType;
            return subType; // bare SubTypeId for tools, ammo, bottles, hand weapons, etc.
        }
    }
}
