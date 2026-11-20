using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Lever (b) of FAC-109: FuelDepot growth for a faction whose fleet burn exceeds its installed
    /// depot output — sized to the gap, sited in the region the burn is in, gated on ice held in that
    /// region, in flight, or ordered, and never for a player faction that has not opted in.
    ///
    /// <para>Rates here are the recipe's own at an unupgraded machine: one level makes
    /// 6 × 500 L/s = 180,000 L/min of hydrogen and eats 6 × 25/s = 9,000 ice/min. The live level is
    /// five times both (productivity bonus 4.0); the ratios the stage decides on are the same.</para>
    /// </summary>
    public class FuelDepotGrowthTests
    {
        private const string H2 = "Gas:Hydrogen";
        private const double LevelLitresPerMin = 6 * 500.0 * 60.0;
        private const double LevelIcePerHour = 6 * 25.0 * 3600.0;

        private static ProductionManager Manager()
        {
            return new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.FuelDepot, new List<ConversionRecipe>
                    {
                        new ConversionRecipe
                        {
                            BlueprintId = "Gas/OxygenGenerator/Hydrogen",
                            Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                            Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Hydrogen", Amount = 500.0 } },
                            TimeSecs = 1.0f,
                        },
                    }
                },
            });
        }

        private static WorldModel World()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            return world;
        }

        /// <summary>An owned, founded node. <paramref name="planet"/> null puts it in open space;
        /// <paramref name="iceDeposit"/> makes its ground ice, which is what the FuelDepot affinity
        /// row prefers (0.9 against 0.1 for anywhere else).</summary>
        private static SpatialNode Node(WorldModel world, string id, string planet, int depotLevel,
            double ice, double burnLitresPerMin, bool iceDeposit = false)
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 1;
            n.PlanetId = planet;
            n.Site = StationSite.Surface;
            if (depotLevel > 0) n.Capabilities[NodeCapabilityKind.FuelDepot] = depotLevel;
            if (ice > 0) n.Inventory["Ore:" + world.Tuning.IceOreSubTypeId] = (long)ice;
            if (iceDeposit)
            {
                n.Deposit = new NodeDeposit();
                n.Deposit.Entries.Add(new DepositEntry(world.Tuning.IceOreSubTypeId, 1.0, 100.0));
            }
            if (burnLitresPerMin > 0)
                FuelDraw.CreditBurn(n, world.UtcNow, world.Tuning, H2,
                                    burnLitresPerMin * 60.0 * world.Tuning.FuelBurnEmaHours);
            return n;
        }

        private static StandingExtractionOrder IceOrder(WorldModel world, string hub, bool paused, double inFlightPerHour)
        {
            return new StandingExtractionOrder
            {
                HubNodeId = hub,
                OreKey = "Ore:" + world.Tuning.IceOreSubTypeId,
                TargetPerHour = paused ? 0 : 1_000_000,
                Paused = paused,
                InFlightPerHour = inFlightPerHour,
            };
        }

        private sealed class Pass
        {
            public readonly List<string> Posts = new List<string>();
            public readonly List<string> Holds = new List<string>();
            public List<InvestmentRequest> Requests;
        }

        /// <summary>Measure and decide the way the production pass does, <paramref name="passes"/>
        /// times on one manager, and collect the FuelDepot lines and the register.</summary>
        private static Pass Run(WorldModel world, FactionState st, int passes = 1, ProductionManager manager = null)
        {
            manager = manager ?? Manager();
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            var log = new List<string>();
            var networkCaps = new HashSet<NodeCapabilityKind>();
            foreach (SpatialNode n in owned)
                foreach (KeyValuePair<NodeCapabilityKind, int> c in n.Capabilities)
                    if (c.Value > 0) networkCaps.Add(c.Key);

            MethodInfo measure = typeof(ProductionManager).GetMethod("MeasureGasStage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo decide = typeof(ProductionManager).GetMethod("DecideThroughputInvestments",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(measure);
            Assert.NotNull(decide);

            for (int i = 0; i < passes; i++)
            {
                var ctx = new LiveProductionExecutionContext(world, log);
                object gas = measure.Invoke(manager, new object[] { st, owned, ctx, world.Tuning });
                decide.Invoke(manager, new object[]
                {
                    world.Tuning, st, networkCaps, ctx, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, gas,
                });
            }

            var pass = new Pass();
            foreach (string line in log)
            {
                if (!line.Contains("FuelDepot")) continue;
                if (line.Contains("THROUGHPUT_INVEST")) pass.Posts.Add(line);
                if (line.Contains("THROUGHPUT_HOLD")) pass.Holds.Add(line);
            }
            // Stage 1 posts its Refinery bootstrap for an estate with none; only the depot asks count.
            pass.Requests = world.Investments.SnapshotFor(st.Tag)
                .FindAll(r => r.Cap == NodeCapabilityKind.FuelDepot);
            return pass;
        }

        private static FactionState Npc() { return new FactionState { Tag = "F" }; }

        /// <summary>A live burn in ML/h, at this fixture's level rate: one live level is 54 ML/h.</summary>
        private static double Mlph(double liveMlPerHour) { return liveMlPerHour / 54.0 * LevelLitresPerMin; }

        /// <summary>A live ice count, at this fixture's level rate: one live level-hour is 2.7 M.</summary>
        private static double Ice(double liveUnits) { return liveUnits / 2_700_000.0 * LevelIcePerHour; }

        // ---- the trigger and its size ---------------------------------------------------------

        [Fact]
        public void BurnAboveOutput_PostsARequestSizedToTheGap()
        {
            WorldModel world = World();
            // 1 level against 3.5 levels of burn: 2.5 levels short, so three are wanted.
            Node(world, "D", null, 1, LevelIcePerHour, 3.5 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Empty(pass.Holds);
            Assert.Single(pass.Requests);
            Assert.Contains("urgency=1.00", pass.Posts[0]);
            Assert.EndsWith("levels=1 output=180000.0 gap=450000.0 want=3 ice_src=held region=none)", pass.Posts[0]);
        }

        [Fact]
        public void TheUrgencyIsTheGapOverOutput()
        {
            WorldModel world = World();
            // 4 levels, 5 levels of burn: a quarter over, one level wanted.
            Node(world, "D", null, 4, LevelIcePerHour, 5 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Contains("urgency=0.25", pass.Posts[0]);
            Assert.Contains(" want=1 ", pass.Posts[0]);
        }

        [Fact]
        public void BurnAtOrBelowOutput_PostsNothing()
        {
            WorldModel world = World();
            Node(world, "D", null, 3, LevelIcePerHour, 3 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Empty(pass.Posts);
            Assert.Empty(pass.Holds);
            Assert.Empty(pass.Requests);
        }

        // ---- the ice gate ---------------------------------------------------------------------

        [Fact]
        public void NoIceAndNoIceOrder_WaitsAndSaysSoOnce()
        {
            WorldModel world = World();
            Node(world, "D", null, 1, 0, 3 * LevelLitresPerMin);

            Pass pass = Run(world, Npc(), passes: 3);

            Assert.Empty(pass.Posts);
            Assert.Empty(pass.Requests);
            Assert.Single(pass.Holds);
            Assert.Contains("THROUGHPUT_HOLD FuelDepot reason=no-ice", pass.Holds[0]);
            Assert.Contains("ice_src=none", pass.Holds[0]);
        }

        [Fact]
        public void NoIceButAnOpenIceOrder_Posts()
        {
            WorldModel world = World();
            Node(world, "D", null, 1, 0, 3 * LevelLitresPerMin);
            FactionState st = Npc();
            st.ExtractionOrders.Add(IceOrder(world, "D", paused: false, inFlightPerHour: 0));

            Pass pass = Run(world, st);

            Assert.Single(pass.Posts);
            Assert.Empty(pass.Holds);
            // The imbalance saturates at 1.00 and the ordered cover is the default 0.10.
            Assert.Contains("urgency=0.10", pass.Posts[0]);
            Assert.Contains("ice_src=order", pass.Posts[0]);
        }

        /// <summary>A paused order is stock at its ceiling, not ice on its way: it cannot stand in for
        /// ice the region does not hold.</summary>
        [Fact]
        public void APausedIceOrder_IsNotAnOpenOne()
        {
            WorldModel world = World();
            Node(world, "D", null, 1, 0, 3 * LevelLitresPerMin);
            FactionState st = Npc();
            st.ExtractionOrders.Add(IceOrder(world, "D", paused: true, inFlightPerHour: 0));

            Pass pass = Run(world, st);

            Assert.Empty(pass.Posts);
            Assert.Single(pass.Holds);
        }

        [Fact]
        public void IceInFlightToTheRegion_CountsAsCover()
        {
            WorldModel world = World();
            Node(world, "D", "Tellus", 1, 0, 3 * LevelLitresPerMin, iceDeposit: true);
            FactionState st = Npc();
            // Paused so the order path is off: only the trips already bound for D can answer.
            st.ExtractionOrders.Add(IceOrder(world, "D", paused: true, inFlightPerHour: LevelIcePerHour / 2));

            Pass pass = Run(world, st);

            Assert.Single(pass.Posts);
            Assert.Contains("ice_cover=0.50", pass.Posts[0]);
            Assert.Contains("ice_src=held", pass.Posts[0]);
        }

        /// <summary>Ice on another body is not cover for this one: the region burning past its output
        /// is asked for, and it holds none.</summary>
        [Fact]
        public void IceInAnotherRegion_IsNotCover()
        {
            WorldModel world = World();
            Node(world, "A", "Agni", 1, 10 * LevelIcePerHour, 0);
            Node(world, "T", "Tellus", 0, 0, 3 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Empty(pass.Posts);
            Assert.Single(pass.Holds);
            Assert.Contains("reason=no-ice", pass.Holds[0]);
            Assert.Contains("planet=Tellus site=Surface", pass.Holds[0]);
        }

        [Fact]
        public void OrderedCoverOfZero_TurnsTheOrderPathOff()
        {
            WorldModel world = World();
            world.Tuning.FuelDepotOrderedIceCover = 0;
            Node(world, "D", null, 1, 0, 3 * LevelLitresPerMin);
            FactionState st = Npc();
            st.ExtractionOrders.Add(IceOrder(world, "D", paused: false, inFlightPerHour: 0));

            Pass pass = Run(world, st);

            Assert.Empty(pass.Posts);
            Assert.Single(pass.Holds);
        }

        // ---- the level cap ------------------------------------------------------------------

        [Fact]
        public void AnEstateAtTheLevelCap_WaitsAtCap()
        {
            WorldModel world = World();
            world.Tuning.MaxFuelDepotUpgrades = 3;
            Node(world, "D", null, 3, LevelIcePerHour, 6 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Empty(pass.Posts);
            Assert.Empty(pass.Requests);
            Assert.Single(pass.Holds);
            Assert.Contains("reason=at-cap", pass.Holds[0]);
        }

        [Fact]
        public void TheSizeIsCappedByTheLevelsTheEstateCanStillTake()
        {
            WorldModel world = World();
            world.Tuning.MaxFuelDepotUpgrades = 3;
            // Five levels short, two levels of headroom.
            Node(world, "D", null, 1, LevelIcePerHour, 6 * LevelLitresPerMin);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Contains(" want=2 ", pass.Posts[0]);
        }

        // ---- players -----------------------------------------------------------------------

        [Fact]
        public void APlayerFaction_IsNeverAskedForADepotItDidNotOptInto()
        {
            WorldModel world = World();
            Node(world, "D", null, 1, LevelIcePerHour, 3 * LevelLitresPerMin);
            var st = new FactionState { Tag = "F", IsPlayerFaction = true };

            Pass pass = Run(world, st);

            Assert.Empty(pass.Posts);
            Assert.Empty(pass.Requests);
            Assert.Single(pass.Holds);
            Assert.Contains("reason=opt-in", pass.Holds[0]);
        }

        /// <summary>The gate is <see cref="FactionLifecycle.CanPostInvestmentFor"/>, so a player who
        /// opted the capability in has chosen it.</summary>
        [Fact]
        public void APlayerFactionThatOptedIn_IsAsked()
        {
            WorldModel world = World();
            Node(world, "D", null, 1, LevelIcePerHour, 3 * LevelLitresPerMin);
            var st = new FactionState { Tag = "F", IsPlayerFaction = true };
            st.ReservationOptIn.Add(NodeCapabilityKind.FuelDepot);

            Pass pass = Run(world, st);

            Assert.Single(pass.Posts);
        }

        // ---- siting, on fixtures shaped like the three small factions --------------------------

        /// <summary>MEMN on 2026-09-27 23:32: four levels (N020 3 on Agni, N090 1 on Tellus),
        /// burn 4.13 M L/min against 3.6 M, split by the nodes' measured 09-29 shares. Agni burns
        /// less than its three levels make; Tellus burns twice its one. The level goes to Tellus —
        /// where the 09-27 Develop did not put it (N020, on Agni, 0.04 M ice).</summary>
        [Fact]
        public void MemnShaped_TheLevelIsAskedForWhereTheBurnOutrunsTheDepots()
        {
            WorldModel world = World();
            // Burn in live ML/h (248 in all, 144 on Agni against 162 made there); ice in live units.
            Node(world, "N020", "Agni", 3, Ice(40_000), Mlph(91.4));
            Node(world, "N021", "Agni", 0, 0, Mlph(52.6));
            Node(world, "N090", "Tellus", 1, Ice(110_000), Mlph(39.7), iceDeposit: true);
            Node(world, "N091", "Tellus", 0, 0, Mlph(30.3), iceDeposit: true);
            Node(world, "N092", "Tellus", 0, Ice(580_000), Mlph(34.2), iceDeposit: true);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Contains("planet=Tellus site=Surface", pass.Posts[0]);
            Assert.Single(pass.Requests);
            InvestmentTarget target = InvestmentTargeting.Resolve(world, "F", pass.Requests[0], world.Tuning);
            Assert.True(target.Resolved);
            Assert.Contains(target.NodeId, new[] { "N090", "N091", "N092" });
        }

        /// <summary>STCG on 2026-09-27 22:07: three levels (N027 2 on Agni, N097 1 on Tellus), burn
        /// 2.84 M L/min against 2.7 M. Agni carries the larger overrun, and neither Agni node stands
        /// on ice, so the level goes to an Agni depot-suitable node by graph order.</summary>
        [Fact]
        public void StcgShaped_TheLevelIsAskedForOnTheBodyWithTheLargerOverrun()
        {
            WorldModel world = World();
            // The nodes' shares of the short-departure need 00-02h 09-29 (N097's estimated), scaled
            // so the whole is 2.84/2.70 of three levels: Agni 2.12 levels on 2, Tellus 1.04 on 1.
            double total = 2.84 / 2.70 * 3 * 54.0;
            double sum = 53.7 + 38.5 + 30.0 + 15.1;
            Node(world, "N027", "Agni", 2, Ice(10_000), Mlph(total * 53.7 / sum));
            Node(world, "N028", "Agni", 0, 0, Mlph(total * 38.5 / sum));
            Node(world, "N097", "Tellus", 1, Ice(870_000), Mlph(total * 30.0 / sum), iceDeposit: true);
            Node(world, "N098", "Tellus", 0, Ice(1_400_000), Mlph(total * 15.1 / sum), iceDeposit: true);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Contains("planet=Agni site=Surface", pass.Posts[0]);
            InvestmentTarget target = InvestmentTargeting.Resolve(world, "F", pass.Requests[0], world.Tuning);
            Assert.Equal("N027", target.NodeId);
        }

        /// <summary>UNGD on 2026-09-29 00-02h: eleven levels (N109 10 on Tellus, N034 1 on Kor)
        /// against a measured need of ~218 ML/h at its short departures — under half of the 594 ML/h
        /// the levels make. Its short nodes lack hydrogen, not depot levels, so this lever posts
        /// nothing for it.</summary>
        [Fact]
        public void UngdShaped_BurnUnderInstalledOutput_PostsNothing()
        {
            WorldModel world = World();
            // N109 and N034 were never short, so their burn is an estimate; tripled it is still under.
            Node(world, "N109", "Tellus", 10, Ice(1_080_000), Mlph(12.0), iceDeposit: true);
            Node(world, "N034", "Kor", 1, Ice(1_860_000), Mlph(9.0), iceDeposit: true);
            Node(world, "N104", "Tellus", 0, Ice(810_000), Mlph(48.1));
            Node(world, "N105", "Tellus", 0, Ice(810_000), Mlph(43.8), iceDeposit: true);
            Node(world, "N106", "Tellus", 0, Ice(810_000), Mlph(37.8), iceDeposit: true);
            Node(world, "N107", "Tellus", 0, Ice(810_000), Mlph(56.4));
            Node(world, "N089", "Tellus", 0, Ice(410_000), Mlph(27.4));

            Pass pass = Run(world, Npc());

            Assert.Empty(pass.Posts);
            Assert.Empty(pass.Holds);
        }

        /// <summary>A short region with no room left falls back to the unscoped request: the estate
        /// still needs the level, and it resolves wherever one can go.</summary>
        [Fact]
        public void AShortRegionAtTheCap_FallsBackToTheUnscopedRequest()
        {
            WorldModel world = World();
            world.Tuning.MaxFuelDepotUpgrades = 2;
            Node(world, "T", "Tellus", 2, LevelIcePerHour, 4 * LevelLitresPerMin);
            Node(world, "A", "Agni", 0, LevelIcePerHour, 0);

            Pass pass = Run(world, Npc());

            Assert.Single(pass.Posts);
            Assert.Contains("region=none", pass.Posts[0]);
            InvestmentTarget target = InvestmentTargeting.Resolve(world, "F", pass.Requests[0], world.Tuning);
            Assert.Equal("A", target.NodeId);
        }
    }
}
