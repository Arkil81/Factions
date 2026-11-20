using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Resolves physical item mass (kg per unit) for mass-aware cargo/thrust-to-weight feasibility.
    /// Pre-built at session load by scanning all MyPhysicalItemDefinition entries.
    /// InvKey convention mirrors ProductionManager/SeItemVolumeCatalogue: "Ore:SubType",
    /// "Ingot:SubType", "Comp:SubType", or bare SubTypeId for tools/ammo/bottles/etc.
    /// </summary>
    public sealed class SeItemMassCatalogue : IItemMassCatalogue
    {
        private readonly Dictionary<string, double> _masses;
        public int Count { get { return _masses.Count; } }

        public SeItemMassCatalogue()
        {
            _masses = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try { Build(); } catch { }
        }

        private void Build()
        {
            if (MyDefinitionManager.Static == null) return;
            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var def = defBase as MyPhysicalItemDefinition;
                if (def == null || def.Mass <= 0f) continue;
                string invKey = ToInvKey(def.Id.TypeId.ToString(), def.Id.SubtypeName);
                if (!string.IsNullOrEmpty(invKey))
                    _masses[invKey] = def.Mass; // already kg/unit — no conversion needed
            }
        }

        public double MassKgPerUnit(string invKey)
        {
            if (string.IsNullOrEmpty(invKey)) return 0;
            double v;
            return _masses.TryGetValue(invKey, out v) ? v : 0;
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
