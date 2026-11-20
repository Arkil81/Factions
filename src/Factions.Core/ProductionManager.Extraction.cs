using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public sealed partial class ProductionManager
    {
        /// <summary>Ice units a FuelDepot eats per unit of each gas it makes, keyed by the gas's
        /// inventory key — the conversion <see cref="ExtractionPlanner"/> needs to turn a hub's
        /// measured fuel burn (<see cref="SpatialNode.FuelBurnEma"/>) into the ice that burn implies.
        ///
        /// <para>Read from the same recipes and machine model <c>MeasureGasStage</c> sizes depot
        /// capacity from, input side through <see cref="MachinePhysics.InputAmount"/> and output side
        /// through <see cref="MachineModel.OutputScale"/>, so the ratio is the one a running depot
        /// actually achieves. Where several recipes make one gas the cheapest in ice wins: that is
        /// the recipe a depot short of ice would be worth running.</para>
        ///
        /// <para>A recipe that makes two gases from one ice input attributes the whole input to each,
        /// which overstates the ice when both gases are burned. Every shipped depot recipe makes one
        /// gas per slot (see <c>MeasureGasStage</c>), so this does not arise today.</para>
        ///
        /// <para>Empty when no FuelDepot recipe is registered, which the planner reads as "no ice is
        /// implied by any burn".</para></summary>
        public Dictionary<string, double> DepotIcePerGasUnit(FactionTuning tuning)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (tuning == null) return result;

            int capIdx = (int)NodeCapabilityKind.FuelDepot;
            if (capIdx < 0 || capIdx >= _models.Length) return result;
            MachineModel model = _models[capIdx];
            if (model.OutputScale <= 0) return result;

            List<ConversionRecipe> recipes;
            if (!_capRecipes.TryGetValue(NodeCapabilityKind.FuelDepot, out recipes) || recipes == null)
                return result;

            string iceKey = "Ore:" + tuning.IceOreSubTypeId;
            foreach (ConversionRecipe recipe in recipes)
            {
                if (recipe == null || recipe.Inputs == null || recipe.Outputs == null) continue;

                double iceIn = 0;
                foreach (RecipeItem inp in recipe.Inputs)
                    if (inp.Amount > 0 && InvKey(inp) == iceKey)
                        iceIn += MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                if (iceIn <= 0) continue;

                foreach (RecipeItem outp in recipe.Outputs)
                {
                    if (outp.Amount <= 0) continue;
                    string gasKey = InvKey(outp);
                    if (!gasKey.StartsWith(InventoryKeyMapper.GasPrefix, StringComparison.Ordinal)) continue;
                    double ratio = iceIn / (outp.Amount * model.OutputScale);
                    double prior;
                    if (!result.TryGetValue(gasKey, out prior) || ratio < prior) result[gasKey] = ratio;
                }
            }
            return result;
        }
    }
}
