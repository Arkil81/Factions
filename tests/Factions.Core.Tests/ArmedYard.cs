using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A shipyard with armed prefabs to build: the fixture any test about a DPS budget needs — a
    /// catalogue whose hulls have guns, blueprints for them, and one owned Shipyard node already
    /// stocked with a hauler so no Trade demand muddies the log. Shared by the campaign budget
    /// (<see cref="TerrorEscalationTests"/>) and the defensive one
    /// (<see cref="DefenceBudgetTests"/>).
    /// </summary>
    internal static class ArmedYard
    {
        internal static readonly DateTime YardT0 = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>DPS and grid size by prefab id; every prefab is in the universal Raid pool.</summary>
        internal sealed class ArmedCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public readonly Dictionary<string, string> Grid = new Dictionary<string, string>();
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>();
            public List<string> RaidPool = new List<string>();
            public ArmedCatalogue Add(string id, float dps, string grid, long cargo = 0)
            {
                Dps[id] = dps; Grid[id] = grid; Cargo[id] = cargo; RaidPool.Add(id); return this;
            }
            public long CargoLiters(string prefabId) { long v; return Cargo.TryGetValue(prefabId, out v) ? v : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public readonly Dictionary<string, string> Hint = new Dictionary<string, string>();
            public ArmedCatalogue Livery(string id, string hint) { Hint[id] = hint; return this; }
            public string FactionHint(string prefabId) { string h; return Hint.TryGetValue(prefabId, out h) ? h : "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float d;
                return Dps.TryGetValue(prefabId, out d) && d > 0
                    ? new[] { new WeaponBand { Dps = d, RangeM = 800 } } : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { string g; return Grid.TryGetValue(prefabId, out g) ? g : "Small"; }
            /// <summary>Pools for roles other than Raid, by role.</summary>
            public readonly Dictionary<string, List<string>> Pools = new Dictionary<string, List<string>>();
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                List<string> pool;
                if (role != null && Pools.TryGetValue(role, out pool)) return pool;
                return role == "Raid" ? RaidPool : new List<string>();
            }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        internal sealed class CostRecipes : IRecipeRegistry
        {
            public readonly Dictionary<string, long> CostByPrefab = new Dictionary<string, long>();
            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                long cost;
                if (outputTypeId != "Prefab" || !CostByPrefab.TryGetValue(outputSubTypeId, out cost)) yield break;
                yield return new ConversionRecipe
                {
                    BlueprintId = outputSubTypeId,
                    Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Steel", Amount = cost } },
                    Outputs = new RecipeItem[0],
                };
            }
            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return CostByPrefab.Count; } }
        }

        internal static WorldModel Yard(ArmedCatalogue cat, CostRecipes recipes, out SpatialNode yard)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = YardT0;
            yard = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "YARD");
            yard.OwnerFactionTag = "F"; yard.HoldStrength = 1.0; yard.StableStationId = 1;
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            yard.ShipStock["Hauler"] = 1;   // cargo stock, so no Trade demand muddies the log
            world.PrefabCatalogue = cat;
            world.Recipes = recipes;
            return world;
        }
    }
}
