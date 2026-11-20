using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class MineTargetingTests
    {
        private static SpatialNode MakeOreFieldNode(double resourceValue = 1.0, double holdStrength = 1.0)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), resourceValue, "OreField1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = holdStrength;
            // The ground is what an ore field is, since FAC-102: the curated fallback mix the yield
            // table used to supply is gone, so the fixture states its own composition.
            TestSupport.WithOre(n, "Iron", "Nickel", "Cobalt", "Silicon", "Magnesium");
            // After WithOre, which derives its own: this fixture's parameter is the quality dial and
            // since FAC-219 Mine ranks and paces on Richness.
            n.Richness = resourceValue;
            return n;
        }

        [Fact]
        public void ComputeTargetWeights_NoRequest_ReturnsNodesNaturalComposition_SummingToOne()
        {
            var node = MakeOreFieldNode();
            var tuning = TestSupport.Tuning();

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, null);

            Assert.NotEmpty(weights);
            double sum = 0;
            foreach (var kv in weights) sum += kv.Value;
            Assert.Equal(1.0, sum, 6);
        }

        [Fact]
        public void ComputeTargetWeights_RequestedRatio_DrivesOutput_NotNaturalRenormalization()
        {
            // Regression for the confirmed design decision: "50/50 iron/nickel" must produce a
            // 50/50 split of OUTPUT, not the node's own natural (unequal) relative abundance.
            var node = MakeOreFieldNode();
            var tuning = TestSupport.Tuning();
            var requested = new Dictionary<string, double> { ["Ore:Iron"] = 50, ["Ore:Nickel"] = 50 };

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, requested);

            Assert.Equal(2, weights.Count);
            Assert.Equal(0.5, weights["Ore:Iron"], 6);
            Assert.Equal(0.5, weights["Ore:Nickel"], 6);
        }

        [Fact]
        public void ComputeTargetWeights_UnequalRequestedRatio_Normalizes_NotForcedTo5050()
        {
            var node = MakeOreFieldNode();
            var tuning = TestSupport.Tuning();
            var requested = new Dictionary<string, double> { ["Ore:Iron"] = 75, ["Ore:Nickel"] = 25 };

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, requested);

            Assert.Equal(0.75, weights["Ore:Iron"], 6);
            Assert.Equal(0.25, weights["Ore:Nickel"], 6);
        }

        [Fact]
        public void ComputeTargetWeights_DropsRequestedOreTheNodeDoesNotYield()
        {
            var node = MakeOreFieldNode();
            var tuning = TestSupport.Tuning();
            var requested = new Dictionary<string, double> { ["Ore:Iron"] = 50, ["Ore:Unobtainium"] = 50 };

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, requested);

            Assert.Single(weights);
            Assert.Equal(1.0, weights["Ore:Iron"], 6);
        }

        [Fact]
        public void ComputeTargetWeights_EmptyIntersection_ReturnsEmpty()
        {
            var node = MakeOreFieldNode();
            var tuning = TestSupport.Tuning();
            var requested = new Dictionary<string, double> { ["Ore:Unobtainium"] = 100 }; // yielded by no node kind

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, requested);

            Assert.Empty(weights);
        }

        [Fact]
        public void NodeRichnessFactor_ScalesWithRichness()
        {
            var richNode = MakeOreFieldNode(resourceValue: 1.0);
            var poorNode = MakeOreFieldNode(resourceValue: 0.2);
            var tuning = TestSupport.Tuning();
            var weights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 };

            double richRate = MineTargeting.NodeRichnessFactor(richNode, tuning, weights);
            double poorRate = MineTargeting.NodeRichnessFactor(poorNode, tuning, weights);

            Assert.True(richRate > poorRate);
            Assert.Equal(richRate * 0.2, poorRate, 6);
        }

        /// <summary>FAC-228: a territorial claim is not a property of rock. Held at 1.0 or crumbling
        /// to 0.1, the same drill cuts the same stone at the same speed.</summary>
        [Fact]
        public void NodeRichnessFactor_IsUnmovedByHoldStrength()
        {
            var tuning = TestSupport.Tuning();
            var weights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 };
            SpatialNode held = MakeOreFieldNode(holdStrength: 1.0);
            SpatialNode slipping = MakeOreFieldNode(holdStrength: 0.1);

            Assert.Equal(MineTargeting.NodeRichnessFactor(held, tuning, weights),
                         MineTargeting.NodeRichnessFactor(slipping, tuning, weights), 9);
            // The discriminator: the fixture really does differ, so this is not two identical nodes.
            Assert.NotEqual(held.HoldStrength, slipping.HoldStrength);
        }

        /// <summary>FAC-228: the rate carries SE's own MinedOreRatio, so a platinum basket is 5x
        /// slower per litre than an iron one -- not the 20x the passive-income mint weight imposed,
        /// and not the 1x that dropping the term entirely would have given.</summary>
        [Fact]
        public void NodeRichnessFactor_CarriesSeOreDensity_IronFiveTimesPlatinum()
        {
            var tuning = TestSupport.Tuning();
            var node = MakeOreFieldNode();
            TestSupport.WithOre(node, "Iron", "Platinum");
            node.Richness = 1.0;

            double iron = MineTargeting.NodeRichnessFactor(node, tuning,
                new Dictionary<string, double> { ["Ore:Iron"] = 1.0 });
            double platinum = MineTargeting.NodeRichnessFactor(node, tuning,
                new Dictionary<string, double> { ["Ore:Platinum"] = 1.0 });

            Assert.Equal(5.0, iron / platinum, 6);   // SE: Iron 5, Platinum 1
        }

        /// <summary>Eligibility used to ride on the mint-scale term. It has to survive its removal:
        /// a node holding none of the requested ore still yields a zero rate, which is what makes
        /// ComputeMineDwellSeconds refuse the dispatch rather than divide by nothing.</summary>
        [Fact]
        public void NodeRichnessFactor_OreTheNodeDoesNotHold_IsStillZero()
        {
            var tuning = TestSupport.Tuning();
            var node = MakeOreFieldNode();          // iron/nickel/cobalt/silicon/magnesium
            var absent = new Dictionary<string, double> { ["Ore:Platinum"] = 1.0 };

            Assert.Equal(0.0, MineTargeting.NodeRichnessFactor(node, tuning, absent));
            // Discriminator: the same node scores non-zero for ore it does hold.
            Assert.True(MineTargeting.NodeRichnessFactor(node, tuning,
                new Dictionary<string, double> { ["Ore:Iron"] = 1.0 }) > 0);
        }

        [Fact]
        public void OreWeightSpec_ParsesAndNormalizesValidSpec()
        {
            Dictionary<string, double> weights;
            string error;
            bool ok = OreWeightSpec.TryParse("Iron:50,Nickel:50", out weights, out error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(0.5, weights["Ore:Iron"], 6);
            Assert.Equal(0.5, weights["Ore:Nickel"], 6);
        }

        [Fact]
        public void OreWeightSpec_NormalizesUnequalValues()
        {
            Dictionary<string, double> weights;
            string error;
            OreWeightSpec.TryParse("Iron:75,Nickel:25", out weights, out error);

            Assert.Equal(0.75, weights["Ore:Iron"], 6);
            Assert.Equal(0.25, weights["Ore:Nickel"], 6);
        }

        [Fact]
        public void OreWeightSpec_EmptyOrWhitespace_ReturnsTrueWithEmptyMap()
        {
            Dictionary<string, double> weights;
            string error;
            bool ok = OreWeightSpec.TryParse("", out weights, out error);

            Assert.True(ok);
            Assert.Empty(weights);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("Iron")]
        [InlineData("Iron:")]
        [InlineData(":50")]
        [InlineData("Iron:-5")]
        [InlineData("Iron:0")]
        [InlineData("Iron:abc")]
        public void OreWeightSpec_RejectsMalformedTokens(string spec)
        {
            Dictionary<string, double> weights;
            string error;
            bool ok = OreWeightSpec.TryParse(spec, out weights, out error);

            Assert.False(ok);
            Assert.Null(weights);
            Assert.NotNull(error);
        }

        // ---- FAC-056: selection-time richness scoring ------------------------------------------

        private static SpatialNode MakeNode(double resourceValue = 1.0, double holdStrength = 1.0,
            params string[] groundOres)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), resourceValue, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = holdStrength;
            // A node's ores are its deposit's since FAC-102 -- the yield table's curated fallback mix
            // is gone, so a fixture that names no ground states its own.
            if (groundOres == null || groundOres.Length == 0)
                groundOres = new[] { "Iron", "Nickel", "Cobalt", "Silicon", "Magnesium" };
            {
                n.Deposit = new NodeDeposit();
                foreach (string ore in groundOres)
                    n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / groundOres.Length, 100.0 / groundOres.Length));
            }
            // The fixture's quality dial, applied to the reading Mine actually paces on (FAC-219).
            n.Richness = resourceValue;
            return n;
        }

        [Fact]
        public void SelectionScore_ARareBasket_IsServedByGround()
        {
            // Supersedes FAC-056's 20x cross-kind scale gap. That heuristic guessed at richness from
            // a node's label; the deposit knows. A node whose ground holds gold serves a gold basket
            // and one whose ground does not scores zero -- a stronger guarantee than 20x, because
            // 20x still dispatched a fleet to a field that had no gold in it.
            var tuning = TestSupport.Tuning();
            var gold = new Dictionary<string, double> { ["Ore:Gold"] = 1 };

            double hasGold = MineTargeting.SelectionScore(
                MakeNode(1.0, 1.0, "Gold"), tuning, gold, NullItemRegistry.Instance);
            double noGold = MineTargeting.SelectionScore(
                MakeNode(1.0, 1.0, "Iron"), tuning, gold, NullItemRegistry.Instance);

            Assert.True(hasGold > 0);
            Assert.Equal(0.0, noGold, 6);
        }

        [Fact]
        public void SelectionScore_TheLabelCarriesNoYieldConsequence()
        {
            // Every mineable kind shares one ore scale now, so a node cannot be penalised for the
            // label its own deposit earned it. The label is descriptive; the ground is the truth.
            var tuning = TestSupport.Tuning();
            var iron = new Dictionary<string, double> { ["Ore:Iron"] = 1 };

            double atOreField = MineTargeting.SelectionScore(
                MakeNode(1.0, 1.0, "Iron"), tuning, iron, NullItemRegistry.Instance);
            double atRareField = MineTargeting.SelectionScore(
                MakeNode(1.0, 1.0, "Iron"), tuning, iron, NullItemRegistry.Instance);

            Assert.True(atOreField > 0);
            Assert.Equal(atOreField, atRareField, 6);
        }

        [Fact]
        public void SelectionScore_IceGroundForAnIronBasket_IsZero_DespiteTheSharedOreScale()
        {
            // The trap this scoring has to avoid: every mineable node shares one Ore scale, so scoring
            // at ItemKind granularity would rank a node holding nothing but ice as a top-tier iron
            // source. The score is keyed on the ore's own entry instead, so a basket the node's ground
            // cannot serve scores zero and is ineligible.
            var tuning = TestSupport.Tuning();
            var iron = new Dictionary<string, double> { ["Ore:Iron"] = 1 };

            Assert.Equal(2.0, tuning.FreeMintScaleFor(ItemKind.Ore), 6);
            Assert.Equal(0.0, MineTargeting.SelectionScore(MakeNode(1.0, 1.0, "Ice"), tuning, iron), 6);
            Assert.True(MineTargeting.SelectionScore(MakeNode(1.0, 1.0, "Ice"), tuning,
                new Dictionary<string, double> { ["Ore:Ice"] = 1 }) > 0);
        }

        /// <summary>Selection and rate are deliberately different questions since FAC-228: which
        /// deposit is worth sending a hull to, versus how fast that rock gives up ore. HoldStrength
        /// belongs to the first -- you would rather invest a trip in ground you actually hold -- and
        /// not to the second.</summary>
        [Fact]
        public void SelectionScore_StillScalesWithRichnessAndHoldStrength_WhereTheRateDoesNot()
        {
            var tuning = TestSupport.Tuning();
            var iron = new Dictionary<string, double> { ["Ore:Iron"] = 1 };

            double full = MineTargeting.SelectionScore(MakeNode(), tuning, iron);
            double poor = MineTargeting.SelectionScore(
                MakeNode(resourceValue: 0.5, holdStrength: 0.5), tuning, iron);

            Assert.Equal(full * 0.25, poor, 6);

            // The asymmetry itself: halving hold alone moves selection and leaves the rate alone.
            SpatialNode held = MakeNode(holdStrength: 1.0);
            SpatialNode slipping = MakeNode(holdStrength: 0.5);
            Dictionary<string, double> w = MineTargeting.ComputeTargetWeights(held, tuning, iron);
            Assert.NotEqual(MineTargeting.SelectionScore(held, tuning, iron),
                            MineTargeting.SelectionScore(slipping, tuning, iron));
            Assert.Equal(MineTargeting.NodeRichnessFactor(held, tuning, w),
                         MineTargeting.NodeRichnessFactor(slipping, tuning, w), 9);
        }

        [Fact]
        public void SelectionScore_NullWeights_MinesTheGroundsOwnMix()
        {
            // An unweighted order mines the node's natural mix, which is now the deposit's own
            // composition rather than the kind's table row. The wrong-KIND penalty this used to pin
            // is gone with the cross-kind scale gap; what remains, and matters more, is that a basket
            // the ground cannot serve is ineligible outright.
            var tuning = TestSupport.Tuning();
            var gold = new Dictionary<string, double> { ["Ore:Gold"] = 1 };

            SpatialNode goldGround = MakeNode(1.0, 1.0, "Gold");
            double natural = MineTargeting.SelectionScore(goldGround, tuning, null, NullItemRegistry.Instance);
            double focused = MineTargeting.SelectionScore(goldGround, tuning, gold, NullItemRegistry.Instance);
            double wrongGround = MineTargeting.SelectionScore(
                MakeNode(1.0, 1.0, "Iron"), tuning, gold, NullItemRegistry.Instance);

            Assert.True(natural > 0);
            Assert.Equal(natural, focused, 6);   // the ground is pure gold, so both baskets agree
            Assert.Equal(0.0, wrongGround, 6);
        }

        // --- DrillBlockScore: swept cross-section, not cut-sphere volume ---

        private const double VanillaSmallDrillCutOutRadius = 1.3;   // CubeBlocks_Tools.sbc
        private const double VanillaLargeDrillCutOutRadius = 1.9;   // SE LargeBlockDrill
        private const double GiantDrillCutOutRadius        = 15.0;  // Giant Drills XL_Advanced_Drill

        [Fact]
        public void DrillBlockScore_IsCutOutRadiusCubed()
        {
            // FAC-228: SE removes a SPHERE of CutOutRadius on every cut, so what a drill clears
            // scales with the cube of its radius, not the swept cross-section this used to assume.
            Assert.Equal(6.859, MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius), 6);
            Assert.Equal(3375.0, MineTargeting.DrillBlockScore(GiantDrillCutOutRadius), 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void DrillBlockScore_NonPositiveRadius_IsZero(double radius)
        {
            Assert.Equal(0.0, MineTargeting.DrillBlockScore(radius));
        }

        /// <summary>FAC-228: the squared form compressed the range between hulls. A vanilla large
        /// drill (r=1.9) against a small one (r=1.3) is 3.12x by SE's geometry and was 2.14x here,
        /// underrating big drills by 1.46x. The cubic form restores it.</summary>
        [Fact]
        public void DrillBlockScore_RatesBigDrillsByCutVolume_NotCrossSection()
        {
            double bigOverSmall = MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius)
                                / MineTargeting.DrillBlockScore(VanillaSmallDrillCutOutRadius);

            Assert.Equal(Math.Pow(VanillaLargeDrillCutOutRadius / VanillaSmallDrillCutOutRadius, 3),
                         bigOverSmall, 6);
            Assert.Equal(3.122, bigOverSmall, 3);          // was 2.136 under the squared score
        }

        /// <summary>The whole derivation in one assertion, against SE's own numbers rather than
        /// against the formula's own shape: one cut every 90 frames (1.5 s at 60 fps,
        /// <c>MyShipDrill.UpdateBeforeSimulation10</c>) of a sphere of CutOutRadius, of which
        /// <c>MyDrillBase.VoxelHarvestRatio</c> 0.009 becomes ore, times SE's MinedOreRatio (Iron 5).
        /// A vanilla LargeBlockDrill therefore peaks near 862 L/s.</summary>
        [Fact]
        public void DrillRate_ForVanillaLargeDrill_MatchesSeOwnHarvestMath()
        {
            const double SeCutIntervalSeconds = 1.5;      // 90 frames at 60 fps
            const double SeVoxelHarvestRatio = 0.009;     // MyDrillBase.VoxelHarvestRatio
            const double SeIronMinedOreRatio = 5.0;       // VoxelMaterials: Iron_01

            double cutSphereM3 = 4.0 / 3.0 * Math.PI
                               * Math.Pow(VanillaLargeDrillCutOutRadius, 3);
            double seLitersPerSecond = cutSphereM3 * SeVoxelHarvestRatio * SeIronMinedOreRatio
                                     * 1000.0 / SeCutIntervalSeconds;

            var tuning = TestSupport.Tuning();
            double modelPeak = MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius)
                             * tuning.MineDrillLitersPerScorePoint
                             * tuning.OreDensityRatioFor("Ore:Iron");

            Assert.Equal(seLitersPerSecond, modelPeak, 1);
            Assert.Equal(861.9, modelPeak, 1);
        }

        /// <summary>The duty cycle is the ONLY thing standing between that peak and the shipped rate,
        /// which is the point of FAC-228: one named number instead of three borrowed ones.</summary>
        [Fact]
        public void ShippedDrillRate_IsThePeakTimesTheDutyCycleAndNothingElse()
        {
            var tuning = TestSupport.Tuning();
            double peak = MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius)
                        * tuning.MineDrillLitersPerScorePoint;

            Assert.Equal(0.5, tuning.MineDutyCycle, 6);
            Assert.Equal(peak * 0.5, peak * tuning.MineDutyCycle, 6);
        }

        // ---- catchment gravity (FAC-216) ---------------------------------------------------

        /// <summary>Gravity by node id, so a fixture can state a catchment's profile directly instead
        /// of encoding it in positions and a falloff curve.</summary>
        private sealed class NamedGravityCatalogue : IPlanetCatalogue
        {
            private readonly Dictionary<string, double> _byPos = new Dictionary<string, double>();
            public NamedGravityCatalogue Set(SpatialNode n, double g) { _byPos[Key(n.Position)] = g; return this; }
            private static string Key(Vec3 p) { return p.X + "|" + p.Y + "|" + p.Z; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm)
            {
                double g;
                return _byPos.TryGetValue(Key(posKm), out g) ? g : 0;
            }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static SpatialNode AddDeposit(SpatialGraph graph, string id, Vec3 pos)
        {
            SpatialNode n = graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return TestSupport.WithOre(n, "Iron");
        }

        [Fact]
        public void HardestGravityDeposit_PrefersTheDeepestWellInTheCatchment_NotTheChosenSource()
        {
            // The whole point of FAC-216: a Mine hull serves the catchment, not one deposit. Live
            // 2026-08-31 ISBT sized a hull against a 0 g node and could not lift it off two thirds
            // of its own deposits.
            var graph = new SpatialGraph();
            SpatialNode shallow = AddDeposit(graph, "Shallow", new Vec3(0, 0, 0));
            SpatialNode deep    = AddDeposit(graph, "Deep",    new Vec3(1, 0, 0));
            var planets = new NamedGravityCatalogue().Set(shallow, 0.17).Set(deep, 1.01);
            var owned = new List<SpatialNode> { shallow, deep };

            SpatialNode via = MineTargeting.HardestGravityDeposit(owned, new Vec3(0, 0, 0), planets,
                TestSupport.Tuning(), shallow);

            Assert.Equal("Deep", via.Id);
        }

        [Fact]
        public void HardestGravityDeposit_CountsDepositsOnCooldownAndOnesYieldingNothingToday()
        {
            // Membership is IsInCatchment, which asks only "mineable and in range". Cooldown and
            // yield are conditions the hull outlives, so a catchment filtered by either would size
            // the hull against a set smaller than the one it actually gets sent to.
            var graph = new SpatialGraph();
            SpatialNode source = AddDeposit(graph, "Source", new Vec3(0, 0, 0));
            SpatialNode deep   = AddDeposit(graph, "Deep",   new Vec3(1, 0, 0));
            deep.MineCooldownUntilUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var planets = new NamedGravityCatalogue().Set(source, 0.1).Set(deep, 0.9);

            SpatialNode via = MineTargeting.HardestGravityDeposit(
                new List<SpatialNode> { source, deep }, new Vec3(0, 0, 0), planets,
                TestSupport.Tuning(), source);

            Assert.Equal("Deep", via.Id);
        }

        [Fact]
        public void HardestGravityDeposit_IgnoresADeepWellOutsideTheSearchRadius()
        {
            // A deposit this dispatch could never choose must not raise the bar for the hull --
            // a catchment walk would never send it there, so refusing hulls on its account would
            // shrink the buildable pool for nothing.
            var graph = new SpatialGraph();
            SpatialNode source = AddDeposit(graph, "Source", new Vec3(0, 0, 0));
            SpatialNode faraway = AddDeposit(graph, "Faraway", new Vec3(9999, 0, 0));
            var planets = new NamedGravityCatalogue().Set(source, 0.1).Set(faraway, 1.5);
            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineSearchRadiusKm = 100;

            SpatialNode via = MineTargeting.HardestGravityDeposit(
                new List<SpatialNode> { source, faraway }, new Vec3(0, 0, 0), planets, tuning, source);

            Assert.Equal("Source", via.Id);
        }

        [Fact]
        public void HardestGravityDeposit_KeepsTheChosenSourceWhenGravityDoesNotDifferentiate()
        {
            // A wholly deep-space estate samples 0 everywhere. Strict ">" means the incumbent holds,
            // so this reduces to the chosen-source rule it replaced rather than to "lowest node id".
            var graph = new SpatialGraph();
            SpatialNode source = AddDeposit(graph, "Zsource", new Vec3(0, 0, 0));
            SpatialNode other  = AddDeposit(graph, "Aother",  new Vec3(1, 0, 0));

            SpatialNode via = MineTargeting.HardestGravityDeposit(
                new List<SpatialNode> { source, other }, new Vec3(0, 0, 0),
                NullPlanetCatalogue.Instance, TestSupport.Tuning(), source);

            Assert.Equal("Zsource", via.Id);
        }

        [Fact]
        public void HardestGravityDeposit_TiedChallengersResolveOnNodeId_NotGraphOrder()
        {
            // Two deposits equally harder than the source: the answer must not depend on which the
            // enumeration reached first, or the demand's via flips between cycles for no reason.
            var graph = new SpatialGraph();
            SpatialNode source = AddDeposit(graph, "Source", new Vec3(0, 0, 0));
            SpatialNode bTie   = AddDeposit(graph, "Btie",   new Vec3(1, 0, 0));
            SpatialNode aTie   = AddDeposit(graph, "Atie",   new Vec3(2, 0, 0));
            var planets = new NamedGravityCatalogue().Set(source, 0.1).Set(bTie, 0.9).Set(aTie, 0.9);
            FactionTuning tuning = TestSupport.Tuning();

            SpatialNode forward = MineTargeting.HardestGravityDeposit(
                new List<SpatialNode> { source, bTie, aTie }, new Vec3(0, 0, 0), planets, tuning, source);
            SpatialNode reversed = MineTargeting.HardestGravityDeposit(
                new List<SpatialNode> { source, aTie, bTie }, new Vec3(0, 0, 0), planets, tuning, source);

            Assert.Equal("Atie", forward.Id);
            Assert.Equal("Atie", reversed.Id);
        }

        [Fact]
        public void IsInCatchment_RejectsANodeWithNoGroundToMine()
        {
            // The Kind label confers nothing since FAC-102; a station in range is not a deposit.
            var graph = new SpatialGraph();
            SpatialNode station = graph.Add(new Vec3(0, 0, 0), 1.0, "Station");
            station.OwnerFactionTag = "F";

            Assert.False(MineTargeting.IsInCatchment(station, new Vec3(0, 0, 0), TestSupport.Tuning()));
        }
    }
}
