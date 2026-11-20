using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Sim
{
    /// <summary>The fleet-doctrine scenario: <c>dotnet run --project src/Factions.Sim -- doctrine
    /// [seed] [ticks]</c>.
    ///
    /// <para>Fleet composition cannot be judged in a twenty-minute live window -- a hull takes hours
    /// to build and days to be lost -- so the behaviour the doctrine is FOR is only visible over
    /// simulated time. This run shows the three things that are new, side by side with an NPC control
    /// that must not move:</para>
    ///
    /// <list type="number">
    ///   <item>the doctrine is the only builder: EXP builds the hull its line names and nothing
    ///   else, and posts no proactive demand at all;</item>
    ///   <item>the target is a CEILING: a live <c>Ship:Trade</c> demand at a line already at target
    ///   produces <c>DOCTRINE_PRESSURE</c> and no build;</item>
    ///   <item>in-flight hulls count: dispatching the whole fleet does not re-open the shortfall.</item>
    /// </list></summary>
    internal static class DoctrineScenario
    {
        private const string Hauler = "C21_Trade_ConsignmentDrone";
        private const string Drone  = "DS_Miner_DrifterDrone";

        internal static int Run(int seed, int ticks)
        {
            var tuning = new FactionTuning();
            var catalogue = new ScenarioCatalogue();
            var recipes = new ScenarioRecipes();
            var world = new WorldModel(new SeededRandom(seed))
            {
                Tuning = tuning,
                UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                PrefabCatalogue = catalogue,
                Recipes = recipes,
            };

            // EXP: the player faction, two yards, one doctrine line.
            SpatialNode expA = AddYard(world, "N600", "EXP", new Vec3(0, 0, 0));
            AddYard(world, "N601", "EXP", new Vec3(120, 0, 0));
            var exp = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            exp.AddDoctrineLine(new DoctrineLine
            {
                Id = exp.MintDoctrineLineId(),
                PrefabId = Hauler,
                TargetCount = 3,
                MaxConcurrent = 1,
                CreatedUtc = world.UtcNow,
            });

            // NPC control on the same clock and the same catalogue: whatever the doctrine changes
            // about EXP must leave this faction's demand and build rate exactly where it was.
            AddYard(world, "N700", "IDIN", new Vec3(9000, 0, 0));
            var npc = new FactionState { Tag = "IDIN" };

            var shipyard = new ShipyardManager();
            var states = new List<FactionState> { exp, npc };

            Console.WriteLine("=== doctrine — seed " + seed + ", " + ticks + " ticks ===");
            Console.WriteLine("EXP (player): doctrine FD-001 = Trade " + Hauler + " target=3 concurrent=1, two yards");
            Console.WriteLine("IDIN (NPC control): one yard, no doctrine — its SHIP_DEMAND_PROACTIVE/BUILD_QUEUED must not change");
            Console.WriteLine("the catalogue also offers " + Drone + ", the cheaper hull the old pool preferred:");
            Console.WriteLine("  a BUILD_QUEUED for EXP naming it is the characteristic failure of this run.");
            Console.WriteLine();

            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(30);

                // Keep both yards supplied, so a placed build actually completes and the fleet can
                // reach its target within the run.
                foreach (SpatialNode n in world.Graph.Nodes.Values)
                    world.AddNodeInventory(n.Id, "Comp:Steel", 200);

                // Halfway: dispatch the whole parked fleet. The shortfall must NOT re-open -- a
                // parked-only count would read zero here and order the fleet again.
                if (tick == ticks / 2) DispatchEverything(exp, world, expA);

                // Then, with every hull in the air, a blocked haul posts a Ship:Trade demand at
                // EXP's yard: the truest signal that work is waiting on hulls, and exactly the
                // signal that used to grow the fleet without anyone asking. Deliberately AFTER the
                // dispatch -- while the hulls are parked the demand is satisfied by local stock and
                // retires, which is a different mechanism and not the ceiling.
                if (tick == (ticks * 2) / 3)
                {
                    world.Demand.Post(DemandEntry.ForShip("EXP", "Trade", expA.Id,
                        world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));
                    Console.WriteLine("t" + tick + "  -- posted a Ship:Trade demand at " + expA.Id
                                      + " (work waiting on hulls)");
                }

                var log = new List<string>();
                shipyard.Tick(states, world, tuning, log);
                foreach (string line in log)
                    if (Interesting(line)) Console.WriteLine("t" + tick + "  " + line);
            }

            Console.WriteLine();
            Console.WriteLine("final: EXP parked=" + ParkedTotal(world, "EXP", Hauler)
                              + " flying=" + ShipyardManager.CountDoctrineHullsInFlight(exp, exp.FleetDoctrine[0])
                              + " target=" + exp.FleetDoctrine[0].TargetCount
                              + "   drones built=" + ParkedTotal(world, "EXP", Drone));
            return 0;
        }

        /// <summary>Moves every parked hull of the line's prefab onto a pending fleet, the way a
        /// dispatch does. The point of the manoeuvre is that <c>have</c> must not fall with it.</summary>
        private static void DispatchEverything(FactionState st, WorldModel world, SpatialNode from)
        {
            int stock;
            if (!from.ShipStock.TryGetValue(Hauler, out stock) || stock <= 0) return;
            from.ShipStock[Hauler] = 0;
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                FleetId = "SIMDSP",
                OriginNodeId = from.Id,
                ReturnNodeId = from.Id,
                TargetNodeId = from.Id,
                ShipPrefabId = Hauler,
                ConvoySize = stock,
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddDays(30),
            });
            Console.WriteLine("     -- dispatched " + stock + " x " + Hauler + " from " + from.Id
                              + "; parked stock is now 0");
        }

        private static bool Interesting(string line)
        {
            return line.Contains("DOCTRINE_") || line.Contains("BUILD_QUEUED")
                || line.Contains("SHIP_DEMAND_PROACTIVE");
        }

        private static long ParkedTotal(WorldModel world, string tag, string prefabId)
        {
            long total = 0;
            foreach (SpatialNode n in world.Graph.OwnedBy(tag))
            {
                int stock;
                if (n.ShipStock.TryGetValue(prefabId, out stock)) total += stock;
            }
            return total;
        }

        private static SpatialNode AddYard(WorldModel world, string id, string tag, Vec3 pos)
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            n.StableStationId = 1;
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        /// <summary>Two hulls and nothing else: the one the doctrine names, and the cheap drone the
        /// BOM-unit-count selection would otherwise prefer for every Trade job.</summary>
        private sealed class ScenarioCatalogue : IPrefabCatalogue
        {
            private static readonly Dictionary<string, long> Cargo = new Dictionary<string, long>
            {
                { Hauler, 42000 }, { Drone, 1200 },
            };

            public long CargoLiters(string prefabId)
            {
                long v;
                return Cargo.TryGetValue(prefabId, out v) ? v : 0;
            }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return CargoLiters(prefabId); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                if (factionHint != "All" || role != "Trade") return new string[0];
                return new[] { Drone, Hauler };
            }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>One Steel-only blueprint per hull, priced by cargo size so the drone is the
        /// cheaper pick -- which is the whole reason the old selection kept choosing it.</summary>
        private sealed class ScenarioRecipes : IRecipeRegistry
        {
            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                if (outputTypeId != "Prefab") yield break;
                long cost;
                if (outputSubTypeId == Hauler) cost = 400;
                else if (outputSubTypeId == Drone) cost = 40;
                else yield break;
                yield return new ConversionRecipe
                {
                    BlueprintId = outputSubTypeId,
                    Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Steel", Amount = cost } },
                    Outputs = new RecipeItem[0],
                };
            }
            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return 2; } }
        }
    }
}
