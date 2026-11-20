using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-108: what a node is worth is read from what is known about it, not dealt to it.
    /// <para>Before this, <c>ResourceValue</c> was a dice roll at creation and a one-time downward
    /// clamp, and nothing ever raised it. It multiplies income directly and drives Claim and Found
    /// ordering, raid scoring and influence, so these pin the whole curve.</para></summary>
    public class ResourceValueTests
    {
        private sealed class Prices : IItemPriceCatalogue
        {
            private readonly Dictionary<string, long> _p = new Dictionary<string, long>();
            public Prices Set(string sub, long price) { _p[sub] = price; return this; }
            public long BasePrice(string sub)
            {
                long v;
                return !string.IsNullOrEmpty(sub) && _p.TryGetValue(sub, out v) ? v : 0;
            }
        }

        private sealed class OnBody : IPlanetCatalogue
        {
            private readonly PlanetRecord _r = new PlanetRecord
            {
                Id = "1", Name = "Tellus", Generator = "EarthLike",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 30, GravityRadius = 48,
            };
            public IEnumerable<PlanetRecord> Planets { get { yield return _r; } }
            public double GravityAt(Vec3 p) { double d = p.DistanceTo(_r.Center); return d >= 48 ? 0 : 1.0 * (1.0 - d / 48); }
            public Vec3 NearestSurfacePoint(Vec3 p) { return p; }
            public Vec3 SurfaceNormal(Vec3 p) { return new Vec3(0, 1, 0); }
        }

        /// <summary>The live world's gas giant: a 9.5 km voxel core with a 304.9 km gravity radius,
        /// so its well is ~32x its own body and almost all of it is nowhere near any ground.
        /// <para><paramref name="landable"/> is the whole point of the fixture — it is the only thing
        /// that differs between the pair of tests below, so whichever way they come out, the
        /// difference is attributable to that and to nothing else.</para></summary>
        private sealed class BigWell : IPlanetCatalogue
        {
            internal const double SurfaceR = 9.5;
            internal const double GravityR = 304.89;

            private readonly PlanetRecord _r;

            internal BigWell(bool landable)
            {
                _r = new PlanetRecord
                {
                    Id = "GG", Name = "Cauldron", Generator = "RealGasGiant",
                    Center = new Vec3(0, 0, 0),
                    SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                    AtmosphereRadius = 191.3, HasAtmosphere = true,
                    AllowsSurfaceStations = landable,
                };
            }

            public IEnumerable<PlanetRecord> Planets { get { yield return _r; } }

            public double GravityAt(Vec3 p)
            {
                double d = p.DistanceTo(_r.Center);
                return d >= GravityR ? 0.0 : 1.0 - d / GravityR;
            }

            /// <summary>Terrain sits on the 9.5 km core, so a point out in the well is nowhere near
            /// it — which is what makes the well Orbital rather than Surface even when landable.</summary>
            public Vec3 NearestSurfacePoint(Vec3 p)
            {
                double d = p.DistanceTo(_r.Center);
                if (d < 1e-9) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(p.X * k, p.Y * k, p.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 p) { return new Vec3(0, 1, 0); }
        }

        /// <summary>A deliberately simple price scale — baseline ore at 10 and the rare one at 4x it.
        /// Used by the tests whose subject is one axis in isolation, so the density axis is what
        /// separates two deposits. Anything measured against an absolute threshold, or about how
        /// density and price trade off, uses <see cref="RealPrices"/> instead.</summary>
        private static Prices Priced()
        {
            return new Prices().Set("Ore:Iron", 10).Set("Ore:Platinum", 40);
        }

        /// <summary>SE's ore prices as the running catalogue resolves them: each ingot price the live
        /// <c>item_prices.json</c> carries (2026-08-30) times SE's refine yield from the live recipe
        /// cache — Fe 143 x 0.7, Pt 70,000 x 0.005. Keyed <c>Ore:</c> because that is the key a
        /// deposit is priced by; a bare subtype reads the ingot (FAC-191). Platinum and uranium are
        /// 3.5x iron, and any test asking whether a deposit is rich <i>enough</i> has to be posed on
        /// that scale.</summary>
        private static Prices RealPrices()
        {
            return new Prices().Set("Ore:Iron", 100).Set("Ore:Nickel", 100).Set("Ore:Silicon", 100)
                               .Set("Ore:Cobalt", 300).Set("Ore:Magnesium", 200).Set("Ore:Silver", 200)
                               .Set("Ore:Gold", 200).Set("Ore:Uranium", 350).Set("Ore:Platinum", 350)
                               .Set("Ore:Stone", 10).Set("Ore:Ice", 50);
        }

        /// <summary>A node whose deposit reads as <paramref name="coverage"/> of its ground bearing
        /// <paramref name="ore"/> in bands <paramref name="bandDepthM"/> thick — the shape a real
        /// sample produces, where richness scales with how many probes were taken.</summary>
        private static SpatialNode Mineable(string ore, long pointsExamined,
            double coverage = 0.03, double bandDepthM = 20.0)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 20, 0), 0.0, "N1");
            double richness = pointsExamined * coverage * bandDepthM;
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry(ore, richness, richness));
            n.Deposit.PointsExamined = pointsExamined;
            return n;
        }

        private static long FullSurvey(FactionTuning t)
        {
            return SurveyMath.PointsToResolve(t.SurveyTargetShare);
        }

        // ---- an unsurveyed node is unproven ------------------------------------------------------

        [Fact]
        public void AnUnsurveyedMineableNode_IsWorthTheUnprovenFloor()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = Mineable("Iron", 0);

            double rv = NodeContent.ResourceValueFor(n, new OnBody(), t, Priced(), NullItemRegistry.Instance);

            Assert.Equal(t.UnsurveyedResourceValue, rv, 9);
        }

        [Fact]
        public void TheUnprovenFloor_IsAboveZero_SoAnUnprovenNodeIsStillWorthClaiming()
        {
            // A node worth literally nothing is never claimed, so it is never held, so it is never
            // surveyed -- the floor has to leave a way out of its own starting state.
            Assert.True(TestSupport.Tuning().UnsurveyedResourceValue > 0.0);
        }

        // ---- worth follows the evidence ----------------------------------------------------------

        [Fact]
        public void Worth_RisesMonotonicallyAsTheSurveyProgresses()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = RealPrices();   // worth has to rise TOWARD something: a deposit that is
            long full = FullSurvey(t);      // poor on the real scale correctly falls instead

            double previous = -1;
            foreach (long points in new[] { 0L, full / 8, full / 4, full / 2, full })
            {
                double rv = NodeContent.ResourceValueFor(
                    Mineable("Platinum", points), planets, t, prices, NullItemRegistry.Instance);
                Assert.True(rv > previous, "worth must rise with evidence, got " + rv + " after " + previous);
                previous = rv;
            }
        }

        [Fact]
        public void Worth_StopsRisingOnceTheSurveyTargetIsReached()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = Priced();
            long full = FullSurvey(t);

            double atTarget = NodeContent.ResourceValueFor(
                Mineable("Platinum", full), planets, t, prices, NullItemRegistry.Instance);
            double wayPast = NodeContent.ResourceValueFor(
                Mineable("Platinum", full * 50), planets, t, prices, NullItemRegistry.Instance);

            Assert.Equal(atTarget, wayPast, 9);
        }

        // ---- a site with no ore has nothing to survey ---------------------------------------------

        [Fact]
        public void AnOnBodySiteWithNoOre_IsWorthItsCeiling_Deterministically()
        {
            FactionTuning t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 20, 0), 0.0, "N1");
            n.Deposit = new NodeDeposit();

            double rv = NodeContent.ResourceValueFor(n, new OnBody(), t, Priced(), NullItemRegistry.Instance);

            Assert.Equal(t.InhabitableResourceValueCap, rv, 9);
        }

        [Fact]
        public void ASpaceSiteWithNoOre_IsWorthNothing()
        {
            FactionTuning t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 5000, 0), 0.0, "N1");
            n.Deposit = new NodeDeposit();

            Assert.Equal(0.0, NodeContent.ResourceValueFor(n, new OnBody(), t, Priced(), NullItemRegistry.Instance), 9);
        }

        // ---- no dice anywhere --------------------------------------------------------------------

        [Fact]
        public void TwoNodesOverIdenticalGround_AreWorthExactlyTheSame()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = Priced();

            double a = NodeContent.ResourceValueFor(Mineable("Iron", 500), planets, t, prices, NullItemRegistry.Instance);
            double b = NodeContent.ResourceValueFor(Mineable("Iron", 500), planets, t, prices, NullItemRegistry.Instance);

            Assert.Equal(a, b, 12);
        }

        // ---- worth is value per unit GROUND, not per unit ore --------------------------------------

        /// <summary>How density and price trade off, so it is posed on the real price scale: a
        /// fixture that compresses price cannot fail on this subject. Iron at 4 m per probe against
        /// platinum at 0.02 is 4.0 against 0.07 priced as ore; priced at the ingot it was 4.0 against
        /// 9.8, and the trace won (FAC-191).</summary>
        [Fact]
        public void ABroadCommonDeposit_OutranksAThinRareOne()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = RealPrices();
            long full = FullSurvey(t);

            // Iron over a fifth of the ground against platinum over a thousandth of it.
            SpatialNode broad = Mineable("Iron", full, coverage: 0.20, bandDepthM: 20.0);
            SpatialNode thin = Mineable("Platinum", full, coverage: 0.001, bandDepthM: 20.0);

            double broadRv = NodeContent.ResourceValueFor(broad, planets, t, prices, NullItemRegistry.Instance);
            double thinRv = NodeContent.ResourceValueFor(thin, planets, t, prices, NullItemRegistry.Instance);

            Assert.True(broadRv > thinRv,
                "a deposit you can actually mine should beat a trace of something precious: "
                + broadRv + " vs " + thinRv);
        }

        /// <summary>The defect FAC-191 measured: a deposit's subtypes are bare, and a bare key reads
        /// the collapsed price table — whose entry for a refinable ore is its ingot. The catalogue
        /// here carries both, the way the live one does; worth must follow the ore. Read through the
        /// bare key, platinum is 490x iron and IsRare labels it; through the ore, 3.5x.</summary>
        [Fact]
        public void ADeposit_IsPricedAsItsOre_NotAsItsIngot()
        {
            Prices prices = RealPrices().Set("Iron", 143).Set("Platinum", 70000);
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = Mineable("Platinum", FullSurvey(t), coverage: 0.6);

            double ratio;
            Assert.True(DepositModel.TryValueRatio(n.Deposit, prices, NullItemRegistry.Instance, out ratio));
            Assert.Equal(3.5, ratio, 9);

            double perGround;
            Assert.True(DepositModel.TryValuePerGround(n.Deposit, prices, NullItemRegistry.Instance, out perGround));
            Assert.Equal(12.0 * 3.5, perGround, 9);
        }

        /// <summary>The rarity label on the ore scale: no SE ore reaches the 4x the ingot reading was
        /// labelled at, so the bar has to sit where an ore mix can clear it. Platinum does, iron does
        /// not.</summary>
        [Fact]
        public void APreciousOre_ReadsRare_AndIronDoesNot()
        {
            FactionTuning t = TestSupport.Tuning();
            Prices prices = RealPrices();
            long full = FullSurvey(t);

            Assert.True(DepositModel.IsRare(Mineable("Platinum", full).Deposit, t, prices, NullItemRegistry.Instance));
            Assert.False(DepositModel.IsRare(Mineable("Iron", full).Deposit, t, prices, NullItemRegistry.Instance));
        }

        [Fact]
        public void TheSameOreSpreadThinner_IsWorthLess()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = Priced();
            long full = FullSurvey(t);

            double rich = NodeContent.ResourceValueFor(
                Mineable("Iron", full, coverage: 0.10), planets, t, prices, NullItemRegistry.Instance);
            double sparse = NodeContent.ResourceValueFor(
                Mineable("Iron", full, coverage: 0.01), planets, t, prices, NullItemRegistry.Instance);

            Assert.True(rich > sparse, "quantity must count: " + rich + " vs " + sparse);
        }

        [Fact]
        public void Density_IsInvariantToHowFinelyTheGroundWasProbed()
        {
            // Density is a coverage fraction, so probe spacing and catchment size cancel in the
            // ratio. Two bodies separated differently must stay comparable -- otherwise Kor's nodes
            // and Tellus's would be judged on different scales for no reason anyone chose.
            SpatialNode coarse = Mineable("Iron", 9279, coverage: 0.03);
            SpatialNode fine = Mineable("Iron", 20878, coverage: 0.03);

            Assert.Equal(coarse.Deposit.RichnessPerPoint, fine.Deposit.RichnessPerPoint, 9);
        }

        [Fact]
        public void Density_IsZeroBeforeAnythingIsExamined()
        {
            Assert.Equal(0.0, new NodeDeposit().RichnessPerPoint, 9);
        }

        // ---- the survey has to have something left to do -------------------------------------------

        /// <summary>The defect that started this: at a 0.05 target a planet node was born already
        /// surveyed, so FAC-101's paced survey never ran on one. Confirmed live on four nodes, all
        /// pegged at 1.000 with 9,279 and 20,878 points. The target must stay tighter than a first
        /// sample or the mechanic is inert.</summary>
        [Fact]
        public void APlanetNodesFirstSample_DoesNotAlreadySatisfyTheSurveyTarget()
        {
            FactionTuning t = TestSupport.Tuning();

            foreach (long firstSample in new long[] { 9279, 20878 })
                Assert.False(SurveyMath.IsSurveyed(firstSample, t.SurveyTargetShare),
                    "a first sample of " + firstSample + " must leave the survey work to do");
        }

        /// <summary>Tightening without bound makes a survey run forever, which is indistinguishable
        /// from one that never converges. The real ceiling is the map: a 7.5 km catchment holds about
        /// 334,000 ore-cubemap texels, and asking for more probes than that samples ground already
        /// read. Pinned so a future retune has to argue with the number rather than drift past it.</summary>
        [Fact]
        public void TheSurveyTarget_StaysWithinWhatTheMapCanActuallyResolve()
        {
            FactionTuning t = TestSupport.Tuning();
            long needed = SurveyMath.PointsToResolve(t.SurveyTargetShare);

            Assert.InRange(needed, 100000, 400000);
        }

        /// <summary>Operator direction, 2026-08-31: a full survey should take about two days — and it
        /// must take about two days at <b>any</b> estate size, which is the whole reason the knob is a
        /// duration. The characteristic failure of the rate it replaced was invisible: a fixed
        /// probes-per-tick reads as correct on the estate it was set for and silently becomes 7x
        /// slower when the world grows 7x, so this asserts across a 20x span of node counts rather
        /// than at the one that happens to be current.</summary>
        [Theory]
        [InlineData(55)]
        [InlineData(110)]
        [InlineData(760)]
        [InlineData(1101)]
        public void AFullSurvey_TakesTheTunedDuration_AtAnyEstateSize(int nodes)
        {
            FactionTuning t = TestSupport.Tuning();

            int budget = SurveyMath.ProbesPerVisit(t.SurveyTargetShare, nodes, t.SurveyDurationHours,
                                                   60.0, t.SurveyMaxProbesPerTick);
            Assert.True(budget > 0, "a live rotation must always be given some budget");

            // One node is advanced per frame, so a node is visited 60/nodes times a second.
            double visitsPerSecond = 60.0 / nodes;
            double hours = SurveyMath.PointsToResolve(t.SurveyTargetShare)
                           / (budget * visitsPerSecond) / 3600.0;

            Assert.InRange(hours, t.SurveyDurationHours * 0.5, t.SurveyDurationHours * 1.5);
        }

        /// <summary>The cap is what keeps a short duration on a large estate from asking one frame for
        /// unbounded ore-map reads. It must bite rather than being a formality.</summary>
        [Fact]
        public void ThePerVisitBudget_IsBoundedByItsCap()
        {
            FactionTuning t = TestSupport.Tuning();
            int budget = SurveyMath.ProbesPerVisit(t.SurveyTargetShare, 100000, 0.01, 60.0,
                                                   t.SurveyMaxProbesPerTick);
            Assert.Equal(t.SurveyMaxProbesPerTick, budget);
        }

        /// <summary>A rotation is never starved to zero: however long the requested duration, a node
        /// must still creep, or the survey stops being a survey.</summary>
        [Fact]
        public void ThePerVisitBudget_NeverFallsBelowOne()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(1, SurveyMath.ProbesPerVisit(t.SurveyTargetShare, 1, 100000.0, 60.0,
                                                      t.SurveyMaxProbesPerTick));
        }

        /// <summary>The version gate is the load-bearing half. Every live server persisted
        /// <c>SurveyTargetShare: 0.05</c>, and <c>LoadTuning</c> lets <c>tuning.json</c> override the
        /// defaults — so without an explicit line in <c>ApplyVersionedContractDefaults</c> the new C#
        /// default would never reach a running server and the survey would stay inert exactly where
        /// it was measured inert.</summary>
        [Fact]
        public void ASurveyTargetPersistedByAnOlderBuild_IsMigratedRatherThanKept()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 35, \"SurveyTargetShare\": 0.05, \"SurveyDurationHours\": 168.0 }");

            Assert.Equal(58, t.TuneVersion);
            Assert.Equal(0.0008, t.SurveyTargetShare, 9);
            Assert.Equal(48.0, t.SurveyDurationHours, 6);
            Assert.False(SurveyMath.IsSurveyed(20878, t.SurveyTargetShare),
                "the migrated target must leave a planet node's first sample unfinished");
        }

        /// <summary>The renamed saturation knob takes the opposite route on purpose: the units
        /// changed from value-per-ore to value-per-ground, so a stale entry under the old name must
        /// fall back to the new default rather than be reinterpreted against it.</summary>
        [Fact]
        public void AStaleSaturationKnobUnderTheOldName_IsIgnored()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"ResourceValueSaturationRatio\": 99.0 }");

            Assert.Equal(140.0, t.ResourceValueSaturationPerProbe, 9);
        }

        // ---- degradation -------------------------------------------------------------------------

        [Fact]
        public void WithNoPrices_ASurveyedMineableNodeConvergesOnItsCeiling()
        {
            // Knowing nothing about worth is not the same as knowing a deposit is poor. The offline
            // sim has no price catalogue and must not read every node as worthless.
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = Mineable("Iron", FullSurvey(t));

            double rv = NodeContent.ResourceValueFor(n, new OnBody(), t, null, NullItemRegistry.Instance);

            Assert.Equal(t.MineableResourceValueCap, rv, 9);
        }

        [Fact]
        public void ANodeNoSamplerCouldReach_IsWorthTheUnprovenFloor()
        {
            // ResolveDeposit returns early when the seam declines -- every node under
            // NullOreGeography. Such a node must not be committed worth nothing, and must not be
            // committed worth an inspected site either: nobody has inspected it.
            var world = new WorldModel(new SeededRandom(3)) { Tuning = TestSupport.Tuning() };
            world.Planets = new OnBody();
            SpatialNode parent = world.Graph.Add(new Vec3(0, 20, 0), 0.0, "P001");
            parent.OwnerFactionTag = "F";

            SpatialNode child = world.ExpandFrom("F", "P001");

            Assert.NotNull(child);
            Assert.Null(child.Deposit);
            Assert.Equal(world.Tuning.UnsurveyedResourceValue, child.ResourceValue, 9);
            Assert.True(child.ResourceValue > 0.0, "an unsampled site on a body is still worth claiming");
        }

        // ---- FAC-139: not having looked is not the same as having found nothing --------------------

        /// <summary>The pair the whole ticket turns on. Two nodes on identical ground in the same
        /// well, differing only in whether the seam ever answered for them: one holds an EMPTY
        /// deposit (asked, and the ground is bare), the other holds NO deposit (never asked).
        /// <see cref="NodeContent.IsMineable"/> is false for both, which is how the second came to be
        /// paid the first one's ceiling. Asserting each value separately would let a build that
        /// collapsed them back together pass one of the two; asserting they differ cannot.</summary>
        [Fact]
        public void AnUnsampledSite_AndAProvenEmptyOne_AreNotWorthTheSame()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            var graph = new SpatialGraph();

            SpatialNode neverAsked = graph.Add(new Vec3(0, 20, 0), 0.0, "N1");
            SpatialNode provenBare = graph.Add(new Vec3(0, 20, 0), 0.0, "N2");
            provenBare.Deposit = new NodeDeposit();

            double unsampled = NodeContent.ResourceValueFor(neverAsked, planets, t, Priced(), NullItemRegistry.Instance);
            double bare = NodeContent.ResourceValueFor(provenBare, planets, t, Priced(), NullItemRegistry.Instance);

            Assert.True(NodeContent.IsUnsampled(neverAsked));
            Assert.False(NodeContent.IsUnsampled(provenBare));
            Assert.NotEqual(bare, unsampled, 9);
            Assert.Equal(t.UnsurveyedResourceValue, unsampled, 9);
            Assert.Equal(t.InhabitableResourceValueCap, bare, 9);
        }

        /// <summary>An unsampled node in open space stays at zero: the floor is clamped by the cap,
        /// and open space has no ceiling to floor against. Without this the fix would raise 372 live
        /// deep-space nodes off 0.0 as a side effect of rescuing the 85 in the well.</summary>
        [Fact]
        public void AnUnsampledSiteInOpenSpace_IsStillWorthNothing()
        {
            FactionTuning t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 5000, 0), 0.0, "N1");

            Assert.Equal(0.0, NodeContent.ResourceValueFor(n, new OnBody(), t, Priced(), NullItemRegistry.Instance), 9);
        }

        // ---- FAC-139: gravity is not habitation ----------------------------------------------------

        /// <summary>The second pair. One position, 300 km out in a 304.9 km well around a 9.5 km
        /// body — Orbital either way, since there is no terrain within reach of it. What changes is
        /// whether the body can be landed on at all. The live gas giant is authored
        /// <c>AllowSurfaceNodes=false</c> in world_recipe.json, and 85 nodes were reading its outer
        /// well as somewhere industry and habitation could stand.</summary>
        [Fact]
        public void TheOuterWellOfAnUnlandableBody_IsWorthNothing()
        {
            FactionTuning t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 300), 0.0, "N1");
            n.Deposit = new NodeDeposit();   // proven bare, so only the site is in question

            var unlandable = new BigWell(landable: false);
            var landable = new BigWell(landable: true);

            Assert.Equal(StationSite.Orbital, StationSiting.Classify(unlandable, n.Position));
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(landable, n.Position));

            double overNothing = NodeContent.ResourceValueFor(n, unlandable, t, Priced(), NullItemRegistry.Instance);
            double overGround = NodeContent.ResourceValueFor(n, landable, t, Priced(), NullItemRegistry.Instance);

            Assert.Equal(0.0, overNothing, 9);
            Assert.Equal(t.InhabitableResourceValueCap, overGround, 9);
        }

        /// <summary>Gravity alone must not carry the ceiling — the reading the previous rule used.
        /// A point deep inside the unlandable well, where gravity is strongest, is worth no more than
        /// one at its edge.</summary>
        [Fact]
        public void StrongGravityOverNothingLandable_BuysNoWorthAtAll()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new BigWell(landable: false);
            var graph = new SpatialGraph();

            foreach (double radius in new[] { 20.0, 100.0, 300.0 })
            {
                SpatialNode n = graph.Add(new Vec3(0, 0, radius), 0.0, "N" + (int)radius);
                n.Deposit = new NodeDeposit();
                Assert.True(planets.GravityAt(n.Position) > 0, "fixture must actually report gravity");
                Assert.Equal(0.0, NodeContent.ResourceValueFor(n, planets, t, Priced(), NullItemRegistry.Instance), 9);
            }
        }

        /// <summary>Ore is read from the ground, not from the sky: an unlandable well is worth
        /// nothing as a <i>site</i>, and that must not reach through and devalue a real deposit
        /// sitting in it. The ring this world is being authored into is exactly such a place.</summary>
        [Fact]
        public void ADepositInAnUnlandableWell_KeepsTheMineableCeiling()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = Mineable("Platinum", FullSurvey(t), coverage: 0.6);   // the estate's median 12 m
            n.Position = new Vec3(0, 0, 300);

            double rv = NodeContent.ResourceValueFor(n, new BigWell(landable: false), t, RealPrices(), NullItemRegistry.Instance);

            Assert.Equal(t.MineableResourceValueCap, NodeContent.ResourceValueCap(n, new BigWell(landable: false), t), 9);
        }

        // ---- FAC-139 / FAC-117: the bar and the curve are one calibration ---------------------------

        /// <summary>The calibration, pinned against the estate it was measured on: the ore-priced
        /// value-per-ground percentiles of the 789 sampled live nodes on 2026-10-09 (FAC-191; the
        /// FAC-117 calibration before it was read off ingot prices). Read linearly at the 2.0 FAC-117
        /// replaced, every node returned the ceiling, which is a number that cannot rank anything; the
        /// test that would have caught that is this one. Poor ground must stay well clear of rich,
        /// and neither end may collapse onto the other.</summary>
        [Fact]
        public void TheSaturationCurve_SeparatesTheMeasuredEstate()
        {
            FactionTuning t = TestSupport.Tuning();
            var planets = new OnBody();
            Prices prices = RealPrices();
            long full = FullSurvey(t);

            // Density chosen so value-per-ground lands on the measured p02 / p50 / p95: iron at
            // 0.98 m per probe, iron at 16.2, platinum at 24.8 (x3.5 = 86.8).
            double poor = NodeContent.ResourceValueFor(
                Mineable("Iron", full, coverage: 1.0, bandDepthM: 0.98), planets, t, prices, NullItemRegistry.Instance);
            double middling = NodeContent.ResourceValueFor(
                Mineable("Iron", full, coverage: 1.0, bandDepthM: 16.2), planets, t, prices, NullItemRegistry.Instance);
            double rich = NodeContent.ResourceValueFor(
                Mineable("Platinum", full, coverage: 1.0, bandDepthM: 24.8), planets, t, prices, NullItemRegistry.Instance);

            Assert.True(poor < middling, "p05 must rank below p50: " + poor + " vs " + middling);
            Assert.True(middling < rich, "p50 must rank below p95: " + middling + " vs " + rich);
            Assert.True(rich - poor > 0.5, "the estate must spread, not bunch: " + poor + " .. " + rich);
        }

        // ---- FAC-139: the fix has to reach nodes that already exist ---------------------------------

        /// <summary>Worth is persisted, and the four sites that re-derive it are commit, deposit
        /// resolution, a survey step and abandonment — <b>none of which a declined node reaches
        /// again</b>. It gains no deposit, so it is never surveyed; it round-trips its stored value
        /// across every restart; and the backfill's re-sample declines and returns before refreshing.
        /// So without a boot pass the whole fix would apply to nodes created afterwards and to none
        /// of the 90 it was written for — a change that is correct and completely inert.</summary>
        [Fact]
        public void ANodeCarryingAStaleWorth_IsReDerivedByTheBootPass()
        {
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = TestSupport.Tuning(), Planets = new BigWell(landable: false),
            };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 300), 0.8, "N1");   // what the old rule stored
            n.OwnerFactionTag = "F";

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.WorthChanged);
            Assert.Equal(0.0, n.ResourceValue, 9);
        }

        /// <summary>The pass is a bridge verb with a dry run, and a dry run must not write. Same node
        /// and same count, so the pair says the counter is reporting the work rather than the write.</summary>
        [Fact]
        public void ADryRunCountsTheReDerivation_AndChangesNothing()
        {
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = TestSupport.Tuning(), Planets = new BigWell(landable: false),
            };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 300), 0.8, "N1");
            n.OwnerFactionTag = "F";

            Assert.Equal(1, w.ReclassifyNodes(false).WorthChanged);
            Assert.Equal(0.8, n.ResourceValue, 9);
        }

        /// <summary>A node already carrying the right number must not be reported, or every boot
        /// counts the whole estate and rewrites every brain file for no change.</summary>
        [Fact]
        public void ANodeAlreadyCarryingItsDerivedWorth_IsNotReported()
        {
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = TestSupport.Tuning(), Planets = new BigWell(landable: false),
            };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 300), 0.0, "N1");
            n.OwnerFactionTag = "F";

            Assert.Equal(0, w.ReclassifyNodes(true).WorthChanged);
            Assert.Equal(0, w.ReclassifyNodes(true).WorthChanged);   // and still none on the next boot
        }

        /// <summary>The curve has to reach a running server and cannot arrive on its C# initialiser:
        /// every live tuning.json already carries the old value and LoadTuning lets it override the
        /// defaults.</summary>
        [Fact]
        public void TheCurvePersistedByAnOlderBuild_IsMigrated()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 38, \"ResourceValueSaturationPerProbe\": 2.0 }");

            Assert.Equal(58, t.TuneVersion);
            Assert.Equal(140.0, t.ResourceValueSaturationPerProbe, 9);
        }

        /// <summary>TuneVersion 57: the curve and the rarity bar moved with the price scale (FAC-191).
        /// Every live tuning.json carries 9000 and 4.0, and 4.0 is a bar no ore-priced deposit can
        /// clear, so both have to come through the gate; an override at the current version is kept.</summary>
        [Fact]
        public void TheOrePricedCurveAndRarityBar_AreMigrated_AndACurrentOverrideIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 56, \"ResourceValueSaturationPerProbe\": 9000.0, \"RareDepositValueRatio\": 4.0 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(140.0, migrated.ResourceValueSaturationPerProbe, 9);
            Assert.Equal(2.0, migrated.RareDepositValueRatio, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"RareDepositValueRatio\": 2.5 }");
            Assert.Equal(2.5, kept.RareDepositValueRatio, 9);
        }
    }
}
