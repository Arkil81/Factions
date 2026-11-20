using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Nutrition per unit of every SE consumable that feeds: the <c>Food</c> stat's
    /// <c>Value × Time</c> on its <c>ConsumableItemDefinition</c>, so a <c>MealPack_SteakDinner</c>
    /// (0.0333 a second for 30 s) is 1.00 and a raw fruit 0.06. Built once at session load.
    ///
    /// <para>Keys are bare subtypes, the inventory key every consumable already lives under in node
    /// stock. Grain and algae are not consumables in SE and so are absent: they feed nobody until a
    /// recipe cooks them. FAC-493.</para>
    /// </summary>
    public sealed class SeNutritionCatalogue : INutritionCatalogue
    {
        private readonly Dictionary<string, double> _nutrition =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _nutrition.Count; } }

        public SeNutritionCatalogue()
        {
            try { Build(); } catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Reading consumable nutrition failed."); }
        }

        private void Build()
        {
            if (MyDefinitionManager.Static == null) return;
            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var def = defBase as MyConsumableItemDefinition;
                if (def == null || def.Stats == null || string.IsNullOrEmpty(def.Id.SubtypeName)) continue;
                double food = 0;
                foreach (MyConsumableItemDefinition.StatValue stat in def.Stats)
                    if (string.Equals(stat.Name, "Food", StringComparison.OrdinalIgnoreCase))
                        food += (double)stat.Value * stat.Time;
                if (food > 0) _nutrition[def.Id.SubtypeName] = food;
            }
        }

        public double NutritionPerUnit(string invKey)
        {
            double v;
            return !string.IsNullOrEmpty(invKey) && _nutrition.TryGetValue(invKey, out v) ? v : 0;
        }
    }
}
