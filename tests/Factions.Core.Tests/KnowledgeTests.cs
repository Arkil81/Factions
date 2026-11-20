using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class KnowledgeTests
    {
        /// <summary>Shipped default for FactionTuning.BirthrightRangeKm.</summary>
        private const double BR = 100.0;

        private static SpatialNode Node(string id, double x, string owner = null, bool station = true)
        {
            return new SpatialNode
            {
                Id = id,
                Position = new Vec3(x, 0, 0),
                OwnerFactionTag = owner,
                StableStationId = station ? 1 : 0,
            };
        }

        private static SpatialGraph GraphOf(params SpatialNode[] nodes)
        {
            var g = new SpatialGraph();
            foreach (SpatialNode n in nodes) g.Nodes[n.Id] = n;
            return g;
        }

        private static void Link(SpatialNode a, SpatialNode b)
        {
            a.Neighbors.Add(b.Id);
            b.Neighbors.Add(a.Id);
        }

        // ---- the migration property the whole design rests on ------------------------------------

        [Fact]
        public void AbsentStore_MeansKnowsWhatItOwns_NotKnowsNothing()
        {
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode theirs = Node("B", 9000, "RVN");
            SpatialGraph g = GraphOf(mine, theirs);
            var empty = new FactionKnowledge();

            Assert.True(KnowledgeView.IsDiscovered(g, "MERC", empty, "A", DateTime.UtcNow, TimeSpan.FromDays(30), BR));
            Assert.False(KnowledgeView.IsDiscovered(g, "MERC", empty, "B", DateTime.UtcNow, TimeSpan.FromDays(30), BR));
        }

        [Fact]
        public void GenesisFloor_IsAdjacencyBoundedByRange_NotAdjacencyAlone()
        {
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode nearRival = Node("B", 40, "RVN");
            SpatialNode farButLinked = Node("C", 600, "RVN");
            SpatialNode nearButUnlinked = Node("D", 30, "RVN");
            Link(mine, nearRival);
            Link(mine, farButLinked);
            SpatialGraph g = GraphOf(mine, nearRival, farButLinked, nearButUnlinked);

            // Both clauses are required. The node graph is linked by ROUTING reach (625 km in deep
            // space), so adjacency alone would let a routing knob widen what every faction knows --
            // measured at 32.7% of the graph median, 52.5% max, on a station-only world.
            Assert.True(KnowledgeView.IsBirthright(g, "MERC", "B", BR));
            Assert.False(KnowledgeView.IsBirthright(g, "MERC", "C", BR));
            Assert.False(KnowledgeView.IsBirthright(g, "MERC", "D", BR));
        }

        [Fact]
        public void WithNoEdges_ExpandStillWorksButClaimAndGrowthDoNot()
        {
            // Two different things share the word "frontier" and behave oppositely on an unlinked
            // graph, which is why scan has to connect it:
            //   IsFrontier(owned node)  -- an owned-NEIGHBOUR count, used to pick an Expand origin.
            //                              Zero edges makes every owned node qualify, so Expand fires.
            //   Frontier(tag)           -- the UNOWNED neighbours of owned nodes, used for derelict
            //                              Claim and for Sense's hasFrontier. Zero edges makes it
            //                              empty, so reclaim is impossible and Growth is damped to
            //                              25% (FactionBrain.cs:110).
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode derelict = Node("B", 50, NeutralFaction.Tag);
            SpatialGraph unlinked = GraphOf(mine, derelict);

            Assert.True(unlinked.IsFrontier("MERC", mine, 3));
            Assert.Empty(unlinked.Frontier("MERC"));

            Link(mine, derelict);

            Assert.Single(unlinked.Frontier("MERC"));
        }

        [Fact]
        public void ZeroRange_ReducesTheFloorToOwnershipAlone()
        {
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode neighbour = Node("B", 10, "RVN");
            Link(mine, neighbour);
            SpatialGraph g = GraphOf(mine, neighbour);

            Assert.True(KnowledgeView.IsBirthright(g, "MERC", "A", 0));
            Assert.False(KnowledgeView.IsBirthright(g, "MERC", "B", 0));
        }

        [Fact]
        public void GenesisFloor_IsReadTime_SoLosingANodeLosesItsNeighbour()
        {
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode neighbour = Node("B", 100, "RVN");
            Link(mine, neighbour);
            SpatialGraph g = GraphOf(mine, neighbour);
            Assert.True(KnowledgeView.IsBirthright(g, "MERC", "B", BR));

            mine.OwnerFactionTag = "RVN";   // conquered

            Assert.False(KnowledgeView.IsBirthright(g, "MERC", "B", BR));
        }

        // ---- expiry -----------------------------------------------------------------------------

        [Fact]
        public void Entry_LapsesExactlyAtTheHorizon_AndTheNodeGoesUnknownNotStale()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            var k = new FactionKnowledge();
            k.Touch("B", now);

            Assert.True(k.HasLive("B", now + TimeSpan.FromDays(29.9), horizon));
            Assert.False(k.HasLive("B", now + horizon, horizon));
            Assert.False(k.HasLive("B", now + TimeSpan.FromDays(31), horizon));
        }

        [Fact]
        public void Detection_RenewsAnEntryRatherThanDuplicatingIt()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            var k = new FactionKnowledge();
            k.Touch("B", now);
            k.Touch("B", now + TimeSpan.FromDays(20));

            Assert.Equal(1, k.Count);
            Assert.True(k.HasLive("B", now + TimeSpan.FromDays(45), horizon));
        }

        [Fact]
        public void Touch_NeverMovesATimestampBackwards()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            var k = new FactionKnowledge();
            k.Touch("B", now);
            k.Touch("B", now - TimeSpan.FromDays(10));

            DateTime seen;
            Assert.True(k.TryGetLastSeen("B", out seen));
            Assert.Equal(now, seen);
        }

        [Fact]
        public void NonPositiveHorizon_MeansNeverExpires_SoAMissingKeyBlindsNobody()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            var k = new FactionKnowledge();
            k.Touch("B", now);

            // The MineOverburdenFraction failure mode: a field left at its C# zero-value. Here that
            // must be inert, not catastrophic.
            Assert.True(k.HasLive("B", now + TimeSpan.FromDays(3650), TimeSpan.Zero));
        }

        [Fact]
        public void Compact_DropsOnlyLapsedEntries_AndCannotChangeWhatHasLiveAnswers()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            var k = new FactionKnowledge();
            k.Touch("fresh", now);
            k.Touch("stale", now - TimeSpan.FromDays(40));

            bool freshBefore = k.HasLive("fresh", now, horizon);
            bool staleBefore = k.HasLive("stale", now, horizon);
            int dropped = k.Compact(now, horizon);

            Assert.Equal(1, dropped);
            Assert.Equal(1, k.Count);
            Assert.Equal(freshBefore, k.HasLive("fresh", now, horizon));
            Assert.Equal(staleBefore, k.HasLive("stale", now, horizon));
        }

        // ---- detection ---------------------------------------------------------------------------

        [Fact]
        public void WithUnitSignature_AReferenceAntennaAtFullRadiusKeepsTheOldLargeGridRange()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(100.0, Sensing.DetectionRangeKm(ShipSensor.ReferenceKm(t), 1.0), 9);
        }

        [Fact]
        public void SignatureIsClamped_SoNothingIsInvisibleAndNothingIsSystemWide()
        {
            // The floor still clamps -- nothing is ever completely invisible.
            Assert.Equal(Signature.Min, Signature.ForShip(1, 0, 0, 1, 0.000001), 9);
            // The ceiling does NOT, for ships: a player capital under jump is meant to be seen at
            // interplanetary range, and Signature.Max bounds nodes rather than hulls.
            Assert.True(Signature.ForShip(50_000_000, 40_000, 0, 1, 3.0) > Signature.Max);
            // A node is still bounded by its own producer.
            Assert.True(Signature.ForNode(null) >= Signature.Min);
        }

        [Fact]
        public void ALadenArmedConvoyIsLouderThanAnEmptyUnarmedSingleton()
        {
            double quiet = Signature.ForShip(200000, 0, 0.0, 1, 1.0);
            double loud = Signature.ForShip(200000, 1500, 1.0, 4, 1.0);
            Assert.True(loud > quiet, "loud=" + loud + " quiet=" + quiet);
        }

        // ---- FAC-136 slice 1: signature from what a ship IS -------------------------------------

        [Fact]
        public void TheFloorNoLongerTruncatesSmallHulls()
        {
            // At Min 0.25 these two clamped to one identical value despite 2.6x the mass, so the model
            // could not tell a container from a drone. This is the whole point of lowering the floor.
            double container = Signature.ForShip(3221, 0, 0, 1, 1.0);
            double drone     = Signature.ForShip(8481, 0, 0, 1, 1.0);

            Assert.True(container < drone, "container=" + container + " drone=" + drone);
            Assert.True(container > Signature.Min, "the lighter hull must not be sitting on the clamp");
        }

        [Fact]
        public void AMedianArmedHullKeepsRoughlyTheOldFlatMultiplier()
        {
            // The weapon term used to be a flat x1.30 for anything armed. Anchoring ReferenceDps to
            // roughly the median armed hull makes this a spread around that value rather than a shift
            // of it -- so the typical warship is not silently rebalanced by this change.
            double unarmed = Signature.ForShip(500000, 0, 0, 1, 1.0);
            double median  = Signature.ForShip(500000, Signature.ReferenceDps, 0, 1, 1.0);

            Assert.Equal(1.30, median / unarmed, 2);
        }

        [Fact]
        public void ArmamentMovesSignatureByMagnitudeNotByAFlag()
        {
            // A 318x DPS span across the real catalogue must produce a real spread -- under the old
            // bool a 136-DPS drone and a 43,200-DPS warship were exactly equally loud.
            double popgun    = Signature.ForShip(500000, 136, 0, 1, 1.0);
            double warship   = Signature.ForShip(500000, 43200, 0, 1, 1.0);
            double unarmed   = Signature.ForShip(500000, 0, 0, 1, 1.0);

            Assert.True(unarmed < popgun, "any armament must cost something");
            Assert.True(popgun < warship, "popgun=" + popgun + " warship=" + warship);
            // Sub-linear: 318x the guns is far less than 318x the signature.
            Assert.True(warship / popgun < 4.0, "ratio=" + (warship / popgun));
        }

        [Fact]
        public void TheLoudestCatalogueArmament_DoesNotOutweighHullMass()
        {
            // Naginata: 3,561 t, 90,635 DPS -- the top of the catalogue after the kinetic and
            // shrapnel DPS readers. At a square root its weapon term alone was 3.33x and the hull
            // read 6.6 at rest, detectable at 662 km on a 100 km sensor.
            double unarmed = Signature.ForShip(3_561_000, 0, 0, 1, 1.0);
            double armed = Signature.ForShip(3_561_000, 90_635, 0, 1, 1.0);

            Assert.True(armed / unarmed < 2.1, "weapon term=" + (armed / unarmed));
            Assert.True(armed < 4.1, "signature=" + armed);
        }

        // ---- FAC-144: the beacon is its own term, and a heavier one ---------------------------

        [Fact]
        public void ABeaconIsLouderThanAnAntennaAtTheSameShareOfItsOwnMaximum()
        {
            FactionTuning t = TestSupport.Tuning();
            DateTime t0 = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            var antenna = new SignatureExcitation();
            antenna.Observe(SignatureSource.AntennaPower, 1.0, t0);
            var beacon = new SignatureExcitation();
            beacon.Observe(SignatureSource.BeaconPower, 1.0, t0);

            Assert.True(beacon.Multiplier(t, t0) > antenna.Multiplier(t, t0),
                        "beacon=" + beacon.Multiplier(t, t0) + " antenna=" + antenna.Multiplier(t, t0));
            Assert.Equal(1.0 + t.SignatureWeightBeaconPower, beacon.Multiplier(t, t0), 9);
        }

        [Fact]
        public void TheBeaconTermIsSeparate_SoTheAntennaTermIsUnchangedByIt()
        {
            // The antenna weight carries a live measurement behind it (+18.8% against a predicted
            // +20%, 2026-08-28). Running a beacon must not re-scale it.
            FactionTuning t = TestSupport.Tuning();
            DateTime t0 = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            var both = new SignatureExcitation();
            both.Observe(SignatureSource.AntennaPower, 1.0, t0);
            both.Observe(SignatureSource.BeaconPower, 1.0, t0);

            Assert.Equal(1.0 + t.SignatureWeightAntennaPower + t.SignatureWeightBeaconPower,
                         both.Multiplier(t, t0), 9);
        }

        [Fact]
        public void ABeaconTracksItsSliderLive_WithNoTail()
        {
            // Steady advertisement, not an event: turning it down is quiet on the next sample.
            FactionTuning t = TestSupport.Tuning();
            DateTime t0 = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.BeaconPower, 1.0, t0);

            Assert.Equal(1.0 + t.SignatureWeightBeaconPower, ex.Multiplier(t, t0), 9);
            Assert.Equal(1.0, ex.Multiplier(t, t0.AddSeconds(1)), 9);
        }

        [Fact]
        public void ZeroDps_IsUnarmedAndCostsNothing()
        {
            double bare = Signature.ForShip(500000, 0, 0, 1, 1.0);
            Assert.Equal(1.0, bare, 9);   // the reference hull, unarmed, empty, alone
        }

        // ---- FAC-136 slice 2: signature from what a ship is DOING --------------------------------

        [Fact]
        public void APlayerScaleHullIsNotClippedByTheNodeBound()
        {
            // The model's reference hull is 500 t; players build 10,000 t and up. A bound sized to the
            // NPC catalogue sits far inside that distribution, so ships must not be clamped by it.
            double bigShip = Signature.ForShip(10_000_000, 0, 0, 1, 1.0);
            double capital = Signature.ForShip(50_000_000, 40_000, 0, 1, 1.0);

            Assert.True(bigShip > 2.5, "a 10,000 t hull should be loud: " + bigShip);
            Assert.True(capital > Signature.Max, "an armed capital must clear the node bound: " + capital);
        }

        [Fact]
        public void AJumpingCapitalLightsUpAtInterplanetaryRange()
        {
            // The mechanic, stated as an assertion: excitation on a big hull is not a rounding error.
            double atRest  = Signature.ForShip(50_000_000, 40_000, 0, 1, 1.0);
            double jumping = Signature.ForShip(50_000_000, 40_000, 0, 1, 3.0);

            Assert.Equal(3.0, jumping / atRest, 6);                        // nothing eats the burst
            Assert.True(Sensing.DetectionRangeKm(100, jumping) > 2500,
                        "a jumping capital should be visible thousands of km out");
        }

        [Fact]
        public void DetectionRangeDoesNotReClampWhatTheModelProduced()
        {
            // The trap: the consumer used to run Signature.Clamp, which would have silently capped
            // every ship at Signature.Max however loud the model said it was.
            Assert.Equal(3800.0, Sensing.DetectionRangeKm(100, 38.0), 6);
            // The floor still applies at the bottom.
            Assert.Equal(100 * Signature.Min, Sensing.DetectionRangeKm(100, 0.0001), 9);
        }

        [Fact]
        public void AnOptionalCeilingIsAvailableButOffByDefault()
        {
            double unbounded = Signature.ForShip(50_000_000, 40_000, 0, 1, 3.0);
            double bounded   = Signature.ForShip(50_000_000, 40_000, 0, 1, 3.0, 14.0);

            Assert.True(unbounded > 14.0);
            Assert.Equal(14.0, bounded, 9);
            Assert.Equal(0.0, new FactionTuning().SignatureShipCeiling, 9);   // off unless asked for
        }

        [Fact]
        public void AtRestIsExactlyTheBaseSignature_SoSliceOneIsUnchanged()
        {
            Assert.Equal(1.30, Signature.ForShip(500000, Signature.ReferenceDps, 0, 1, 1.0), 2);   // the slice-1 anchor
        }

        [Fact]
        public void Excitation_DecaysBackToRestAtItsOwnRate()
        {
            var t = TestSupport.Tuning();
            t.SignatureWeightWeaponsFire = 0.60;
            t.SignatureDecayWeaponsFireSeconds = 15.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.WeaponsFire, 1.0, t0);

            Assert.Equal(1.60, ex.Multiplier(t, t0), 6);                       // firing now
            Assert.Equal(1.30, ex.Multiplier(t, t0.AddSeconds(7.5)), 6);       // halfway through the tail
            Assert.Equal(1.00, ex.Multiplier(t, t0.AddSeconds(15)), 6);        // back at rest
            Assert.Equal(1.00, ex.Multiplier(t, t0.AddSeconds(60)), 6);        // and stays there
        }

        [Fact]
        public void Excitation_ZeroDecayHasNoTailAtAll()
        {
            // The antenna case. A steady-state setting must go quiet on the next sample rather than
            // linger -- and must not stick at full strength forever if nothing re-observes it.
            var t = TestSupport.Tuning();
            t.SignatureWeightAntennaPower = 0.20;
            t.SignatureDecayAntennaPowerSeconds = 0.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.AntennaPower, 1.0, t0);

            Assert.Equal(1.20, ex.Multiplier(t, t0), 6);
            Assert.Equal(1.00, ex.Multiplier(t, t0.AddSeconds(0.001)), 6);
        }

        [Fact]
        public void Excitation_TermsDecayIndependently()
        {
            // The whole reason decay is per term: one shared constant could not express an antenna
            // with no tail alongside a weapon burst that must linger.
            var t = TestSupport.Tuning();
            t.SignatureWeightThrust = 0.35;      t.SignatureDecayThrustSeconds = 10.0;
            t.SignatureWeightWeaponsFire = 0.60; t.SignatureDecayWeaponsFireSeconds = 15.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.Thrust, 1.0, t0);
            ex.Observe(SignatureSource.WeaponsFire, 1.0, t0);

            Assert.Equal(1.95, ex.Multiplier(t, t0), 6);
            // At 12 s thrust is long gone and fire is still 20% of the way up.
            Assert.Equal(1.0 + 0.60 * 0.2, ex.Multiplier(t, t0.AddSeconds(12)), 6);
        }

        [Fact]
        public void Excitation_NeverGoesBelowOne_SoActivityCannotMakeAHullQuieter()
        {
            var t = TestSupport.Tuning();
            var ex = new SignatureExcitation();
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(1.0, ex.Multiplier(t, t0), 9);                    // nothing observed
            ex.Observe(SignatureSource.Thrust, -5, t0);                    // clamped, not negative
            Assert.Equal(1.0, ex.Multiplier(t, t0), 9);
        }

        [Fact]
        public void Excitation_ObserveReplacesRatherThanAccumulates()
        {
            var t = TestSupport.Tuning();
            t.SignatureWeightThrust = 0.35; t.SignatureDecayThrustSeconds = 10.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            for (int i = 0; i < 10; i++) ex.Observe(SignatureSource.Thrust, 1.0, t0);
            Assert.Equal(1.35, ex.Multiplier(t, t0), 6);   // a state, not a running total
        }

        [Fact]
        public void AJumpFlashIsBrightAndBrief()
        {
            // The operator's shape: "a very bright short flash". Amplitude and duration are
            // independent knobs and this one is loud and short -- you see it if you are looking when
            // it happens, and you cannot follow it afterwards.
            var t = TestSupport.Tuning();
            t.SignatureWeightJumpFlash = 4.0;
            t.SignatureDecayJumpFlashSeconds = 5.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.JumpFlash, 1.0, t0);

            Assert.Equal(5.0, ex.Multiplier(t, t0), 6);                  // bright
            Assert.Equal(1.0, ex.Multiplier(t, t0.AddSeconds(5)), 6);    // and gone
        }

        [Fact]
        public void AJumpingCapitalReachesInterplanetaryRange()
        {
            // The whole point of removing the ceiling, as an assertion rather than a hope.
            var t = TestSupport.Tuning();
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.JumpFlash, 1.0, t0);

            double capital = Signature.ForShip(50_000_000, 40_000, 0, 1, ex.Multiplier(t, t0));
            Assert.True(Sensing.DetectionRangeKm(100, capital) > 4000,
                        "a 50,000 t capital under jump should be seen thousands of km out, got "
                        + Sensing.DetectionRangeKm(100, capital));
        }

        [Fact]
        public void ChargingIsSustainedWhileTheJumpIsNot()
        {
            // Charging tracks live (no tail) so winding down goes quiet immediately; the flash lingers
            // briefly. One shared decay constant could not express both.
            var t = TestSupport.Tuning();
            t.SignatureWeightJumpCharge = 0.8;  t.SignatureDecayJumpChargeSeconds = 0.0;
            t.SignatureWeightJumpFlash = 4.0;   t.SignatureDecayJumpFlashSeconds = 5.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.JumpCharge, 1.0, t0);
            ex.Observe(SignatureSource.JumpFlash, 1.0, t0);
            Assert.Equal(5.8, ex.Multiplier(t, t0), 6);

            // One instant later the charge term is already gone; the flash is still fading.
            Assert.Equal(1.0 + 4.0 * (1 - 2.5 / 5.0), ex.Multiplier(t, t0.AddSeconds(2.5)), 6);
        }

        [Fact]
        public void AbsoluteTermsMakeABigHullLoudWhileMerelyRunning()
        {
            // Operator direction: emission should track hull size. With absolute references a large
            // ship saturates its activity terms just by operating, while a small one has to work.
            var t = TestSupport.Tuning();
            t.SignatureWeightThrust = 0.35;
            t.SignatureThrustReferenceN = 40_000_000.0;
            t.SignatureDecayThrustSeconds = 10.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            // Both pulling 1 g. Under the old acceleration reading these were identical.
            var small = new SignatureExcitation();
            var big = new SignatureExcitation();
            small.Observe(SignatureSource.Thrust, 500_000 * 9.81 / t.SignatureThrustReferenceN, t0);
            big.Observe(SignatureSource.Thrust, 50_000_000 * 9.81 / t.SignatureThrustReferenceN, t0);

            Assert.True(big.Multiplier(t, t0) > small.Multiplier(t, t0),
                        "big=" + big.Multiplier(t, t0) + " small=" + small.Multiplier(t, t0));
            Assert.Equal(1.35, big.Multiplier(t, t0), 6);       // saturated just by burning
            Assert.True(small.Multiplier(t, t0) < 1.10, "a small hull at 1 g should barely register");
        }

        [Fact]
        public void PowerDrawIsItsOwnTermAndDecaysWithItsOwnWindow()
        {
            var t = TestSupport.Tuning();
            t.SignatureWeightPowerDraw = 0.25;
            t.SignatureDecayPowerDrawSeconds = 5.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.PowerDraw, 1.0, t0);

            Assert.Equal(1.25, ex.Multiplier(t, t0), 6);
            Assert.Equal(1.00, ex.Multiplier(t, t0.AddSeconds(5)), 6);
        }

        [Fact]
        public void PrototechIsQuieterAndUnknownBlocksAreNot()
        {
            var t = new FactionTuning();

            Assert.True(t.BlockEmissionFor("LargePrototechJumpDrive") < 1.0);
            Assert.True(t.BlockEmissionFor("LargeBlockPrototechThruster") < 1.0);

            // Loud by default: an unlisted or modded subtype must not get the discount for free.
            Assert.Equal(1.0, t.BlockEmissionFor("LargeJumpDrive"), 9);
            Assert.Equal(1.0, t.BlockEmissionFor("SomeModdedDrive"), 9);
            Assert.Equal(1.0, t.BlockEmissionFor(null), 9);
            Assert.Equal(1.0, t.BlockEmissionFor(""), 9);
        }

        [Fact]
        public void BlockEmissionIsKeyedOnSubtypeNotType()
        {
            // Prototech and vanilla drives share MyJumpDrive, so only the SubtypeId separates them.
            // A table that could not tell them apart would silently give a refit nothing.
            var t = new FactionTuning();
            Assert.NotEqual(t.BlockEmissionFor("LargePrototechJumpDrive"),
                            t.BlockEmissionFor("LargeJumpDrive"));
        }

        [Fact]
        public void AQuieterDriveMakesAJumpMeasurablyLessBright()
        {
            // The mechanic end to end: the same jump, one hull refitted, seen from meaningfully closer.
            var t = TestSupport.Tuning();
            t.SignatureWeightJumpFlash = 4.0;
            t.SignatureDecayJumpFlashSeconds = 5.0;
            DateTime t0 = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var vanilla = new SignatureExcitation();
            var proto = new SignatureExcitation();
            vanilla.Observe(SignatureSource.JumpFlash, t.BlockEmissionFor("LargeJumpDrive"), t0);
            proto.Observe(SignatureSource.JumpFlash, t.BlockEmissionFor("LargePrototechJumpDrive"), t0);

            double hull = 10_000_000;
            double loud = Signature.ForShip(hull, 0, 0, 1, vanilla.Multiplier(t, t0));
            double quiet = Signature.ForShip(hull, 0, 0, 1, proto.Multiplier(t, t0));

            Assert.True(quiet < loud, "quiet=" + quiet + " loud=" + loud);
            // Worth flying: the refit should buy a real fraction of the range back, not a rounding error.
            Assert.True(quiet < loud * 0.85, "ratio=" + (quiet / loud));
        }

        [Fact]
        public void UnsetTable_LeavesEverythingLoud()
        {
            var t = new FactionTuning();
            t.SignatureBlockEmission = null;
            Assert.Equal(1.0, t.BlockEmissionFor("LargePrototechJumpDrive"), 9);
        }

        [Fact]
        public void SupercruiseGlowScalesWithSpeed()
        {
            // "A bright glow proportional to power(speed)" -- cruising slowly is quieter than cruising
            // fast, which is the throttle-versus-stealth lever.
            var t = TestSupport.Tuning();
            t.SignatureWeightSupercruise = 1.0;
            t.SignatureDecaySupercruiseSeconds = 0.0;
            DateTime t0 = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

            var slow = new SignatureExcitation();
            var fast = new SignatureExcitation();
            slow.Observe(SignatureSource.Supercruise, 100_000.0 / t.SignatureSupercruiseReferenceMps, t0);
            fast.Observe(SignatureSource.Supercruise, 500_000.0 / t.SignatureSupercruiseReferenceMps, t0);

            Assert.Equal(1.20, slow.Multiplier(t, t0), 6);
            Assert.Equal(2.00, fast.Multiplier(t, t0), 6);
        }

        [Fact]
        public void SupercruiseHasNoTailButItsEntryDoes()
        {
            var t = TestSupport.Tuning();
            t.SignatureWeightSupercruise = 1.0;      t.SignatureDecaySupercruiseSeconds = 0.0;
            t.SignatureWeightSupercruiseEntry = 1.5; t.SignatureDecaySupercruiseEntrySeconds = 10.0;
            DateTime t0 = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

            var ex = new SignatureExcitation();
            ex.Observe(SignatureSource.Supercruise, 1.0, t0);
            ex.Observe(SignatureSource.SupercruiseEntry, 1.0, t0);
            Assert.Equal(3.50, ex.Multiplier(t, t0), 6);

            // Drop out of cruise: the glow is gone instantly, the entry burst is still fading.
            Assert.Equal(1.0 + 1.5 * 0.5, ex.Multiplier(t, t0.AddSeconds(5)), 6);
        }

        [Fact]
        public void TheSupercruiseSpeedBandRejectsFlyingAndJumping()
        {
            // Both bounds carry weight. The floor keeps ordinary flight out; the ceiling keeps a jump
            // out, because a jump teleports and its implied speed is astronomically higher than any
            // cruise. Without the ceiling every jump would light the entry burst for its full window.
            var t = new FactionTuning();

            Assert.True(100.0 < t.SignatureSupercruiseMinMps, "vanilla flight must be below the floor");
            Assert.True(t.SignatureSupercruiseMaxMps > 2_000_000.0,
                        "the mod's own 2,000 km/s maximum must be inside the band");
            // A 1,000 km jump resolved in one 5 s sample implies 200,000 km/s -- far outside.
            Assert.True(1_000_000_000.0 / 5.0 > t.SignatureSupercruiseMaxMps,
                        "a jump's implied speed must fall outside the band");
        }

        [Fact]
        public void SensorArray_WidensANodesEnvelopeOverThePassiveOne()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode plain = Node("A", 0, "MERC");
            SpatialNode arrayed = Node("B", 0, "MERC");
            arrayed.Capabilities[NodeCapabilityKind.SensorArray] = 2;

            Assert.Equal(t.SensorNodePassiveRangeKm, Sensing.NodeStrengthKm(plain, t), 9);
            Assert.Equal(t.SensorNodePassiveRangeKm + 2 * t.SensorArrayRangeKmPerLevel,
                Sensing.NodeStrengthKm(arrayed, t), 9);
        }

        // ---- the sweep, and its bound ------------------------------------------------------------

        [Fact]
        public void OwningWideTerritoryIsNotWatchingIt()
        {
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode nearRival = Node("B", 20, "RVN");
            SpatialNode farRival = Node("C", 5000, "RVN");
            SpatialGraph g = GraphOf(home, nearRival, farRival);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepOwnedNodes(g, index, state, t, now);

            Assert.True(state.Knowledge.HasLive("B", now, t.DiscoveryHorizon));
            Assert.False(state.Knowledge.HasLive("C", now, t.DiscoveryHorizon));
        }

        [Fact]
        public void AFleetSurveysWhatItPasses_WhichIsHowKnowledgeGrowsFromTraffic()
        {
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode remote = Node("B", 5000, "RVN");
            SpatialGraph g = GraphOf(home, remote);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepOwnedNodes(g, index, state, t, now);
            Assert.False(state.Knowledge.HasLive("B", now, t.DiscoveryHorizon));

            KnowledgeSweep.SweepFleetSegment(g, index, state,
                new Vec3(4990, 0, 0), new Vec3(4995, 0, 0), t, now);
            Assert.True(state.Knowledge.HasLive("B", now, t.DiscoveryHorizon));
        }

        // ---- the swept sensor (FAC-081) ---------------------------------------------------------

        [Fact]
        public void AFleetSurveysItsWholeLeg_NotJustWhereItWasSampled()
        {
            // The FAC-081 regression, stated as the arithmetic that produced it: at shipped defaults
            // a fleet covers 4500km between sweeps against a 60km-wide sensor. A node passed
            // mid-route is invisible to a point sample and must not be to a swept one.
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode enRoute = Node("B", 2000, "RVN");
            SpatialGraph g = GraphOf(home, enRoute);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            // Point sample at each end of the leg: the node sits 2000km from both, so neither sees it.
            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(0, 0, 0), new Vec3(0, 0, 0), t, now);
            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(4500, 0, 0), new Vec3(4500, 0, 0), t, now);
            Assert.False(state.Knowledge.HasLive("B", now, t.DiscoveryHorizon));

            // The same leg swept as a segment passes directly over it.
            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(0, 0, 0), new Vec3(4500, 0, 0), t, now);
            Assert.True(state.Knowledge.HasLive("B", now, t.DiscoveryHorizon));
        }

        [Fact]
        public void CoverageDoesNotDependOnHowOftenTheSweepRuns()
        {
            // The property that makes the segment the right shape: one long leg and the same leg cut
            // into pieces must learn the same set. Without it the fix would only move the cadence at
            // which the channel is broken.
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            var nodes = new List<SpatialNode> { Node("A", 0, "MERC") };
            for (int i = 1; i <= 9; i++) nodes.Add(Node("N" + i, i * 500, "RVN"));
            SpatialGraph g = GraphOf(nodes.ToArray());
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);

            var coarse = new FactionState { Tag = "MERC" };
            KnowledgeSweep.SweepFleetSegment(g, index, coarse, new Vec3(0, 0, 0), new Vec3(4500, 0, 0), t, now);

            var fine = new FactionState { Tag = "MERC" };
            for (int step = 0; step < 9; step++)
                KnowledgeSweep.SweepFleetSegment(g, index, fine,
                    new Vec3(step * 500, 0, 0), new Vec3((step + 1) * 500, 0, 0), t, now);

            Assert.Equal(9, coarse.Knowledge.Count);
            Assert.Equal(coarse.Knowledge.Count, fine.Knowledge.Count);
        }

        [Fact]
        public void ResweepingASegmentIsIdempotent()
        {
            // A restart re-derives the prior position and re-sweeps a leg it already swept. That must
            // renew rather than duplicate, which is what lets the prior position be derived instead
            // of persisted.
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode enRoute = Node("B", 2000, "RVN");
            SpatialGraph g = GraphOf(home, enRoute);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(0, 0, 0), new Vec3(4500, 0, 0), t, now);
            int after = state.Knowledge.Count;
            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(0, 0, 0), new Vec3(4500, 0, 0), t, now.AddMinutes(3));

            Assert.Equal(after, state.Knowledge.Count);
        }

        [Fact]
        public void SegmentQuery_AgreesWithABruteForceScan()
        {
            var g = new SpatialGraph();
            var rng = new Random(20260824);
            for (int i = 0; i < 400; i++)
            {
                var n = new SpatialNode
                {
                    Id = "N" + i,
                    Position = new Vec3(rng.NextDouble() * 6000 - 3000, rng.NextDouble() * 6000 - 3000,
                                        rng.NextDouble() * 6000 - 3000),
                };
                g.Nodes[n.Id] = n;
            }
            NodeProximityIndex index = NodeProximityIndex.Build(g, 250);
            var a = new Vec3(-2500, -100, 400);
            var b = new Vec3(2500, 300, -400);
            const double radius = 400;

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode n in g.Nodes.Values)
                if (Sensing.DistanceToSegmentSquared(n.Position, a, b) <= radius * radius) expected.Add(n.Id);

            var hits = new List<SpatialNode>();
            index.QueryWithinSegment(g, a, b, radius, hits);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode n in hits) actual.Add(n.Id);

            Assert.True(expected.Count > 0, "probe found nothing; the test proves nothing");
            Assert.Equal(expected, actual);
            Assert.Equal(hits.Count, actual.Count);   // no duplicates across marched cells
        }

        [Fact]
        public void DistanceToSegment_DegeneratesToPointDistance()
        {
            var p = new Vec3(0, 30, 0);
            var a = new Vec3(0, 0, 0);
            Assert.Equal(900.0, Sensing.DistanceToSegmentSquared(p, a, a), 6);
            // and clamps to the endpoints rather than the infinite line
            Assert.Equal(Sensing.DistanceSquared(new Vec3(-100, 0, 0), a),
                         Sensing.DistanceToSegmentSquared(new Vec3(-100, 0, 0), a, new Vec3(100, 0, 0)), 6);
        }

        [Fact]
        public void TheSweepNeverStoresWhatOwnershipAlreadyGrants()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode a = Node("A", 0, "MERC");
            SpatialNode b = Node("B", 10, "MERC");
            SpatialGraph g = GraphOf(a, b);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepOwnedNodes(g, index, state, t, DateTime.UtcNow);

            // Both are in range of each other, and both are already known by ownership. Storing them
            // would make the file grow with every faction's whole estate for nothing.
            Assert.Equal(0, state.Knowledge.Count);
        }

        [Fact]
        public void ProximityIndex_AgreesWithABruteForceScan()
        {
            var g = new SpatialGraph();
            var rng = new Random(20260824);
            for (int i = 0; i < 400; i++)
            {
                var n = new SpatialNode
                {
                    Id = "N" + i,
                    Position = new Vec3(rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 4000 - 2000,
                                        rng.NextDouble() * 4000 - 2000),
                };
                g.Nodes[n.Id] = n;
            }
            NodeProximityIndex index = NodeProximityIndex.Build(g, 250);
            var probe = new Vec3(13, -47, 220);
            const double radius = 600;

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode n in g.Nodes.Values)
                if (Sensing.DistanceSquared(probe, n.Position) <= radius * radius) expected.Add(n.Id);

            var hits = new List<SpatialNode>();
            index.QueryWithin(g, probe, radius, hits);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode n in hits) actual.Add(n.Id);

            Assert.True(expected.Count > 0, "probe found nothing; the test proves nothing");
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void QueryWithinAny_CostsNothingAndFindsNothingWithNoObservers()
        {
            SpatialGraph g = GraphOf(Node("A", 0), Node("B", 5), Node("C", 10));
            NodeProximityIndex index = NodeProximityIndex.Build(g, 100);

            var hits = new List<SpatialNode>();
            index.QueryWithinAny(g, new List<Vec3>(), 50, hits);
            Assert.Empty(hits);

            // The gate the per-frame observation scan depends on: a null list is the same as none,
            // not an exception on the game thread.
            index.QueryWithinAny(g, null, 50, hits);
            Assert.Empty(hits);
        }

        [Fact]
        public void QueryWithinAny_ReturnsANodeInRangeOfTwoObserversExactlyOnce()
        {
            // B sits 4 km from each observer, so a 10 km radius reaches it from both. A and C are
            // reachable from one observer each -- without them a dedup bug and a "return only the
            // first point's hits" bug look identical.
            SpatialNode a = Node("A", -8), b = Node("B", 0), c = Node("C", 8);
            SpatialGraph g = GraphOf(a, b, c);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 100);
            var observers = new List<Vec3> { new Vec3(-4, 0, 0), new Vec3(4, 0, 0) };

            var hits = new List<SpatialNode>();
            index.QueryWithinAny(g, observers, 10, hits);

            Assert.Equal(3, hits.Count);
            Assert.Equal(
                new HashSet<string>(new[] { "A", "B", "C" }, StringComparer.Ordinal),
                new HashSet<string>(hits.ConvertAll(n => n.Id), StringComparer.Ordinal));
        }

        [Fact]
        public void QueryWithinAny_AgreesWithTheWholeGraphWalkItReplaces()
        {
            // The property that matters: inverting the loop must not change the answer. Random
            // positions, several observers, a radius smaller than the cell so the cell span is 1.
            var rng = new Random(90287);
            var g = new SpatialGraph();
            for (int i = 0; i < 400; i++)
            {
                var n = new SpatialNode
                {
                    Id = "N" + i.ToString("D3"),
                    Position = new Vec3(rng.NextDouble() * 2000 - 1000,
                                        rng.NextDouble() * 2000 - 1000,
                                        rng.NextDouble() * 2000 - 1000),
                };
                g.Nodes[n.Id] = n;
            }
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var observers = new List<Vec3>();
            for (int i = 0; i < 4; i++)
                observers.Add(new Vec3(rng.NextDouble() * 1200 - 600,
                                       rng.NextDouble() * 1200 - 600,
                                       rng.NextDouble() * 1200 - 600));
            const double radius = 150;

            // What the old per-frame code did: every node, asking whether any observer is near.
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode n in g.Nodes.Values)
                foreach (Vec3 p in observers)
                    if (Sensing.DistanceSquared(p, n.Position) <= radius * radius)
                    { expected.Add(n.Id); break; }

            var hits = new List<SpatialNode>();
            var scratch = new HashSet<string>(StringComparer.Ordinal);
            index.QueryWithinAny(g, observers, radius, hits, scratch);

            Assert.True(expected.Count > 0, "no observer reached a node; the test proves nothing");
            Assert.True(expected.Count < g.Nodes.Count, "every node is in range; dedup is untested");
            Assert.Equal(expected.Count, hits.Count);
            Assert.Equal(expected, new HashSet<string>(hits.ConvertAll(n => n.Id), StringComparer.Ordinal));
        }

        [Fact]
        public void QueryWithinAny_ReusesTheCallersScratchSetAcrossCalls()
        {
            // A stale scratch set would make the second call return nothing at all -- the failure
            // mode of reusing a dedup set without clearing it.
            SpatialNode a = Node("A", -8), b = Node("B", 0), c = Node("C", 8);
            SpatialGraph g = GraphOf(a, b, c);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 100);
            var observers = new List<Vec3> { new Vec3(-4, 0, 0), new Vec3(4, 0, 0) };
            var scratch = new HashSet<string>(StringComparer.Ordinal);

            var first = new List<SpatialNode>();
            index.QueryWithinAny(g, observers, 10, first, scratch);
            var second = new List<SpatialNode>();
            index.QueryWithinAny(g, observers, 10, second, scratch);

            Assert.Equal(3, first.Count);
            Assert.Equal(first.Count, second.Count);
        }

        [Fact]
        public void ProximityIndex_IsStaleOnlyWhenTheNodeSetChanges()
        {
            SpatialNode a = Node("A", 0, "MERC");
            SpatialGraph g = GraphOf(a);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);

            Assert.False(index.IsStaleFor(g));
            a.OwnerFactionTag = "RVN";            // ownership drift does not move a node
            Assert.False(index.IsStaleFor(g));
            g.Nodes["B"] = Node("B", 10, "RVN");  // a new node does
            Assert.True(index.IsStaleFor(g));
        }

        // ---- the viewer union (the store behind it is slice B; the rule is settled here) ---------

        [Fact]
        public void AViewerSeesTheUnionOfEveryStoreTheyDrawOn()
        {
            var now = DateTime.UtcNow;
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode scouted = Node("B", 9000, "RVN");
            SpatialNode heardOf = Node("C", 12000, "RVN");
            SpatialGraph g = GraphOf(home, scouted, heardOf);

            var mine = new FactionKnowledge();
            mine.Touch("B", now);
            var factions = new FactionKnowledge();
            factions.Touch("C", now);

            var both = new List<FactionKnowledge> { mine, factions };
            Assert.True(KnowledgeView.IsDiscoveredBy(g, "MERC", both, "B", now, horizon, BR));
            Assert.True(KnowledgeView.IsDiscoveredBy(g, "MERC", both, "C", now, horizon, BR));
        }

        [Fact]
        public void LeavingAFactionDropsItsMapButKeepsWhatYouSawYourself()
        {
            var now = DateTime.UtcNow;
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode home = Node("A", 0, "MERC");
            SpatialNode scouted = Node("B", 9000, "RVN");
            SpatialNode factionOnly = Node("C", 12000, "RVN");
            SpatialGraph g = GraphOf(home, scouted, factionOnly);

            var mine = new FactionKnowledge();
            mine.Touch("B", now);
            var factions = new FactionKnowledge();
            factions.Touch("C", now);

            // A member draws on both, and on the faction's birthright.
            var member = new List<FactionKnowledge> { mine, factions };
            Assert.True(KnowledgeView.IsDiscoveredBy(g, "MERC", member, "C", now, horizon, BR));
            Assert.True(KnowledgeView.IsDiscoveredBy(g, "MERC", member, "A", now, horizon, BR));

            // After leaving, the union is computed without them. Because the union is never merged
            // into a store, there is nothing to walk away with -- join-learn-leave buys nothing.
            var alone = new List<FactionKnowledge> { mine };
            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, alone, "C", now, horizon, BR));
            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, alone, "A", now, horizon, BR));
            Assert.True(KnowledgeView.IsDiscoveredBy(g, null, alone, "B", now, horizon, BR));
        }

        // ---- persistence --------------------------------------------------------------------------

        [Fact]
        public void KnowledgeSurvivesABrainRoundTrip()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            var s = new FactionState { Tag = "MERC", PersonalityId = "merchant" };
            s.Knowledge.Touch("B", now);

            BrainDoc doc = FactionStore.ToBrainDoc(s, new List<SpatialNode>(), 10);
            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            DateTime seen;
            Assert.True(back.Knowledge.TryGetLastSeen("B", out seen));
            Assert.Equal(now, seen);
        }

        [Fact]
        public void AnEmptyKnowledgeStoreIsOmittedFromTheBrainDoc()
        {
            var s = new FactionState { Tag = "MERC", PersonalityId = "merchant" };
            BrainDoc doc = FactionStore.ToBrainDoc(s, new List<SpatialNode>(), 10);
            Assert.Null(doc.Knowledge);
        }

        [Fact]
        public void PlayerStore_RoundTripsAndOmitsPlayersWhoKnowNothing()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fogb-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
                var store = new PlayerKnowledgeStore(dir);
                store.Touch(1234, "B", now);
                store.For(5678);            // touched by nobody -- must not reach the file
                store.SaveIfDirty();

                var reloaded = new PlayerKnowledgeStore(dir);
                DateTime seen;
                Assert.True(reloaded.For(1234).TryGetLastSeen("B", out seen));
                Assert.Equal(now, seen);
                Assert.Equal(1, reloaded.PlayerCount);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PlayerStore_SurvivesACorruptFileRatherThanRefusingToBoot()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fogb-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "knowledge.json"), "{ this is not json");
                var store = new PlayerKnowledgeStore(dir);
                Assert.Equal(0, store.PlayerCount);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ASoloPlayersOwnStoreIsTheirWholeMap()
        {
            var now = DateTime.UtcNow;
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode a = Node("A", 0, "MERC");
            SpatialNode b = Node("B", 9000, "RVN");
            SpatialGraph g = GraphOf(a, b);

            var mine = new FactionKnowledge();
            var sources = new List<FactionKnowledge> { mine };

            // No faction, nothing detected: a solo player's map is empty, not the whole world.
            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, sources, "A", now, horizon, BR));
            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, sources, "B", now, horizon, BR));

            mine.Touch("B", now);
            Assert.True(KnowledgeView.IsDiscoveredBy(g, null, sources, "B", now, horizon, BR));
            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, sources, "A", now, horizon, BR));
        }

        [Fact]
        public void ALapsedEntryGoesUnknownNotDim()
        {
            // The map's rule: dim means remembered, never expired. A node past the horizon must fall
            // out of the union entirely so it is never written to the wire.
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode far = Node("B", 9000, "RVN");
            SpatialGraph g = GraphOf(far);
            var mine = new FactionKnowledge();
            mine.Touch("B", now - TimeSpan.FromDays(31));
            var sources = new List<FactionKnowledge> { mine };

            Assert.False(KnowledgeView.IsDiscoveredBy(g, null, sources, "B", now, horizon, BR));
        }

        // ---- slice C: the gate, and a belief that can be wrong -----------------------------------

        private static FactionTuning Gated(bool on)
        {
            FactionTuning t = TestSupport.Tuning();
            t.KnowledgeGatedBrains = on;
            return t;
        }

        private static NodeBelief BeliefFor(
            SpatialGraph g, string tag, FactionKnowledge k, DateTime now, FactionTuning t, string nodeId)
        {
            foreach (NodeBelief b in KnowledgeGate.Beliefs(g, tag, k, now, t))
                if (b.Id == nodeId) return b;
            return default(NodeBelief);
        }

        [Fact]
        public void WithTheFlagOff_EveryNodeIsYieldedWithLiveValues()
        {
            // The revert property. Off must be the pre-slice-C behaviour exactly, or the flag is not
            // a revert but a second code path that hopefully agrees with the first.
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode unseen = Node("B", 9000, "RVN");
            unseen.HoldStrength = 0.9;
            SpatialGraph g = GraphOf(mine, unseen);

            List<NodeBelief> beliefs = KnowledgeGate.Beliefs(
                g, "MERC", new FactionKnowledge(), DateTime.UtcNow, Gated(false)).ToList();

            Assert.Equal(2, beliefs.Count);
            Assert.All(beliefs, b => Assert.True(b.IsLive));
            Assert.Equal(0.9, beliefs.Single(b => b.Id == "B").HoldStrength);
        }

        [Fact]
        public void WithTheFlagOn_ANodeNeverSeenIsNotYieldedAtAll()
        {
            // Unknown is a stronger statement than "considered and rejected": the node must not reach
            // a candidate generator, so it cannot be raided, traded with or allied with.
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode unseen = Node("B", 9000, "RVN");
            SpatialGraph g = GraphOf(mine, unseen);

            List<NodeBelief> beliefs = KnowledgeGate.Beliefs(
                g, "MERC", new FactionKnowledge(), DateTime.UtcNow, Gated(true)).ToList();

            Assert.Equal(new[] { "A" }, beliefs.Select(b => b.Id).ToArray());
        }

        [Fact]
        public void AnOwnedNodeIsBelievedLive_WithNoEntryAndNoTimestamp()
        {
            // Ownership is checked before the store, which is what stops an owned node expiring and
            // what makes the bootstrap solvable.
            SpatialNode mine = Node("A", 0, "MERC");
            mine.HoldStrength = 0.42;
            SpatialGraph g = GraphOf(mine);

            NodeBelief b = BeliefFor(g, "MERC", new FactionKnowledge(), DateTime.UtcNow, Gated(true), "A");
            Assert.True(b.IsLive);
            Assert.Equal(0.42, b.HoldStrength);
            Assert.Equal("MERC", b.OwnerTag);
        }

        [Fact]
        public void ABeliefIsWhatWasSeen_NotWhatIsTrueNow()
        {
            // The point of the slice. A raider must be able to strike where the defence used to be.
            var seenAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode theirs = Node("B", 9000, "RVN");
            theirs.HoldStrength = 0.2;
            theirs.ResourceValue = 0.8;
            SpatialGraph g = GraphOf(mine, theirs);

            var k = new FactionKnowledge();
            k.Touch(theirs, seenAt);

            // The world moves on: they reinforced, lost the node to a third party, and it was mined out.
            theirs.HoldStrength = 0.95;
            theirs.OwnerFactionTag = "STL";
            theirs.ResourceValue = 0.1;

            NodeBelief b = BeliefFor(g, "MERC", k, seenAt.AddDays(1), Gated(true), "B");
            Assert.False(b.IsLive);
            Assert.Equal(0.2, b.HoldStrength);
            Assert.Equal(0.8, b.ResourceValue);
            Assert.Equal("RVN", b.OwnerTag);
            // Identity and position are static, so they are read live and are not part of the memory.
            Assert.Same(theirs, b.Node);
        }

        [Fact]
        public void ALapsedEntryIsForgotten_NotBelievedStale()
        {
            var seenAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode theirs = Node("B", 9000, "RVN");
            SpatialGraph g = GraphOf(mine, theirs);
            var k = new FactionKnowledge();
            k.Touch(theirs, seenAt);

            FactionTuning t = Gated(true);
            Assert.NotNull(BeliefFor(g, "MERC", k, seenAt.Add(t.DiscoveryHorizon) - TimeSpan.FromMinutes(1), t, "B").Node);
            Assert.Null(BeliefFor(g, "MERC", k, seenAt.Add(t.DiscoveryHorizon), t, "B").Node);
        }

        [Fact]
        public void AnEntryWithNoDetailFallsBackToLiveValues_RatherThanToZeroes()
        {
            // A player scanner contact and every pre-snapshot brain entry are timestamp-only. Reading
            // those as a memory would hand the brain a node with owner null and hold 0 -- which every
            // candidate generator would then skip as unowned. Known-with-no-detail must read live.
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode mine = Node("A", 0, "MERC");
            SpatialNode theirs = Node("B", 9000, "RVN");
            theirs.HoldStrength = 0.7;
            SpatialGraph g = GraphOf(mine, theirs);
            var k = new FactionKnowledge();
            k.Touch("B", now);                       // timestamp only

            NodeBelief b = BeliefFor(g, "MERC", k, now, Gated(true), "B");
            Assert.True(b.IsLive);
            Assert.Equal("RVN", b.OwnerTag);
            Assert.Equal(0.7, b.HoldStrength);
        }

        [Fact]
        public void ATimestampOnlyTouchNeverDiscardsDetailAnEarlierDetectionCaptured()
        {
            var seenAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode theirs = Node("B", 9000, "RVN");
            theirs.HoldStrength = 0.3;
            var k = new FactionKnowledge();
            k.Touch(theirs, seenAt);
            theirs.HoldStrength = 0.9;
            k.Touch("B", seenAt.AddHours(1));        // the scanner path, later but detail-free

            KnownNode snap;
            Assert.True(k.TryGetSnapshot("B", seenAt.AddHours(2), TimeSpan.FromDays(30), out snap));
            Assert.Equal(0.3, snap.HoldStrength);                 // detail kept
            Assert.Equal(seenAt.AddHours(1), snap.LastSeenUtc);   // timestamp renewed
        }

        [Fact]
        public void ASnapshotSurvivesABrainRoundTrip()
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode theirs = Node("B", 9000, "RVN");
            theirs.HoldStrength = 0.25;
            theirs.ResourceValue = 0.75;
            var s = new FactionState { Tag = "MERC", PersonalityId = "merchant" };
            s.Knowledge.Touch(theirs, now);

            BrainDoc doc = FactionStore.ToBrainDoc(s, new List<SpatialNode>(), 10);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);
            BrainDoc reparsed = Newtonsoft.Json.JsonConvert.DeserializeObject<BrainDoc>(json);
            FactionState back = FactionStore.ApplyBrainDoc(reparsed, new SpatialGraph());

            KnownNode snap;
            Assert.True(back.Knowledge.TryGetSnapshot("B", now, TimeSpan.FromDays(30), out snap));
            Assert.Equal("RVN", snap.OwnerTag);
            Assert.Equal(0.25, snap.HoldStrength);
            Assert.Equal(0.75, snap.ResourceValue);
            Assert.Equal(now, snap.LastSeenUtc);
        }

        [Fact]
        public void ABrainWrittenBeforeSnapshotsStillLoads_AsKnownWithoutDetail()
        {
            // The exact shape every brain on both live hosts carries today: a bare timestamp. If this
            // throws, a deploy of this slice loses every faction's learned set on first load.
            const string legacy = "{\"Tag\":\"MERC\",\"Knowledge\":{\"B\":\"2026-08-24T12:00:00Z\"}}";
            BrainDoc doc = Newtonsoft.Json.JsonConvert.DeserializeObject<BrainDoc>(legacy);
            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            DateTime seen;
            Assert.True(back.Knowledge.TryGetLastSeen("B", out seen));
            Assert.Equal(now, seen);
            KnownNode snap;
            Assert.False(back.Knowledge.TryGetSnapshot("B", now, TimeSpan.FromDays(30), out snap));
        }

        [Fact]
        public void AnEntryWithoutDetailStillSerialisesAsABareTimestamp()
        {
            // Not cosmetic: one entry per (faction, node) at thousands of nodes is where the lean-JSON
            // rule bites first, and a detail-free entry must not start costing an object per node.
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            var s = new FactionState { Tag = "MERC", PersonalityId = "merchant" };
            s.Knowledge.Touch("B", now);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                FactionStore.ToBrainDoc(s, new List<SpatialNode>(), 10));
            Assert.Contains("\"B\":\"2026-08-24T12:00:00Z\"", json);
        }

        // ---- what the gate does to the brain itself ----------------------------------------------

        [Fact]
        public void AGatedBrainWillNotRaidANodeItHasNeverSeen()
        {
            Assert.Empty(RaidTargets(Gated(true), seenByUs: false));
        }

        [Fact]
        public void TheSameBrainRaidsThatNodeWithTheFlagOff()
        {
            // The control for the test above: without it, "no raid" could equally mean the fixture
            // never produced a raid candidate for any reason at all.
            Assert.Equal(new[] { "B" }, RaidTargets(Gated(false), seenByUs: false));
        }

        [Fact]
        public void AGatedBrainRaidsANodeItHasSeen()
        {
            Assert.Equal(new[] { "B" }, RaidTargets(Gated(true), seenByUs: true));
        }

        /// <summary>Runs one Think against a two-faction world where MERC hates RVN and can reach it,
        /// and reports which nodes it generated Raid intents for.</summary>
        private static string[] RaidTargets(FactionTuning t, bool seenByUs)
        {
            var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode mine = Node("A", 0, "MERC");
            mine.HoldStrength = 0.9;
            mine.Threat = 0.8;                  // AddRaids only runs under the Security/Rivalry voices
            SpatialNode theirs = Node("B", 20, "RVN");
            theirs.HoldStrength = 0.1;
            theirs.ResourceValue = 0.9;
            // Deliberately NOT linked: adjacency inside BirthrightRangeKm would make B known for
            // free, and the unseen case would then be testing nothing.

            var world = new WorldModel(new SeededRandom(7)) { Tuning = t, UtcNow = now };
            world.Graph.Nodes[mine.Id] = mine;
            world.Graph.Nodes[theirs.Id] = theirs;
            world.SetTreasury("MERC", 500000000);

            var s = new FactionState { Tag = "MERC", PersonalityId = "raider", RngSeed = 7 };
            s.Relations["RVN"] = -1.0;
            if (seenByUs) s.Knowledge.Touch(theirs, now);
            world.PrepareInfluenceSnapshot();

            var brain = new FactionBrain();
            Decision d = brain.Think(s, Personalities.Get("raider"), world, new SeededRandom(7), t);
            return CampaignNomination.RaidedNodeIds(d, world);
        }

        // ---- what a node the viewer only remembers reports about itself -------------------------

        [Fact]
        public void ARememberedNodeReportsTheOwnerItWasSeenUnder()
        {
            // The defect this guards: a node discovered weeks ago under one flag, relabelled by the
            // map the moment somebody else expands into it, with no one having looked again.
            var seen = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            DateTime now = seen.AddDays(13);
            TimeSpan horizon = TimeSpan.FromDays(30);

            SpatialNode n = Node("N283", 0, "UNKN");
            n.HoldStrength = 0.2;
            SpatialGraph g = GraphOf(n);

            var mine = new FactionKnowledge();
            mine.Touch(n, seen);
            var sources = new List<FactionKnowledge> { mine };

            n.OwnerFactionTag = "FCTM";     // expanded into while nobody was watching
            n.HoldStrength = 0.9;

            NodeBelief belief;
            Assert.True(KnowledgeGate.TryBelief(g, null, sources, n, now, horizon, BR, out belief));
            Assert.Equal("UNKN", belief.OwnerTag);
            Assert.Equal(0.2, belief.HoldStrength);
            Assert.False(belief.IsLive);
            Assert.Same(n, belief.Node);        // identity and position are never remembered
        }

        [Fact]
        public void TheFreshestStoreWinsRegardlessOfOrder()
        {
            // A player unions their own store with their faction's. Whichever looked last is what
            // they know; picking the first in the list would make the answer depend on assembly
            // order, which no caller controls.
            DateTime now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode n = Node("N301", 0, "UVCO");
            SpatialGraph g = GraphOf(n);

            var older = new FactionKnowledge();
            older.Touch(n, now.AddDays(-5));
            n.OwnerFactionTag = "UNKN";
            var newer = new FactionKnowledge();
            newer.Touch(n, now.AddDays(-1));
            n.OwnerFactionTag = "FCTM";

            NodeBelief belief;
            Assert.True(KnowledgeGate.TryBelief(
                g, null, new List<FactionKnowledge> { older, newer }, n, now, horizon, BR, out belief));
            Assert.Equal("UNKN", belief.OwnerTag);

            Assert.True(KnowledgeGate.TryBelief(
                g, null, new List<FactionKnowledge> { newer, older }, n, now, horizon, BR, out belief));
            Assert.Equal("UNKN", belief.OwnerTag);
        }

        [Fact]
        public void AKnownNodeWithNoDetailReadsLiveRatherThanUnowned()
        {
            // Every entry written before snapshots existed is detail-free, and so is the birthright
            // floor. Reading those as a null owner would draw them as free to claim -- a lie in the
            // opposite direction, and a worse one.
            DateTime now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode n = Node("N461", 0, "FCTM");
            SpatialGraph g = GraphOf(n);

            var mine = new FactionKnowledge();
            mine.Touch("N461", now.AddDays(-3));     // timestamp only

            NodeBelief belief;
            Assert.True(KnowledgeGate.TryBelief(
                g, null, new List<FactionKnowledge> { mine }, n, now, horizon, BR, out belief));
            Assert.Equal("FCTM", belief.OwnerTag);
            Assert.True(belief.IsLive);
        }

        [Fact]
        public void AViewersOwnEstateIsLiveEvenWhenAStoreRemembersItOtherwise()
        {
            DateTime now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan horizon = TimeSpan.FromDays(30);
            SpatialNode n = Node("N749", 0, "UNKN");
            SpatialGraph g = GraphOf(n);

            var mine = new FactionKnowledge();
            mine.Touch(n, now.AddDays(-2));          // seen while it was still unclaimed
            n.OwnerFactionTag = "EXP";               // and then claimed by the viewer

            NodeBelief belief;
            Assert.True(KnowledgeGate.TryBelief(
                g, "EXP", new List<FactionKnowledge> { mine }, n, now, horizon, BR, out belief));
            Assert.Equal("EXP", belief.OwnerTag);
            Assert.True(belief.IsLive);
        }

        [Fact]
        public void AnUndiscoveredNodeYieldsNoBeliefAtAll()
        {
            DateTime now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode n = Node("N141", 9000, "FCTM");
            SpatialGraph g = GraphOf(n);
            NodeBelief belief;
            Assert.False(KnowledgeGate.TryBelief(
                g, "EXP", new List<FactionKnowledge> { new FactionKnowledge() }, n, now,
                TimeSpan.FromDays(30), BR, out belief));
        }

        [Fact]
        public void PlayerStore_RoundTripsWhatWasSeenAndNotOnlyWhen()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fac357-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
                SpatialNode n = Node("N283", 0, "UNKN");
                n.HoldStrength = 0.42;
                n.ResourceValue = 0.31;

                var store = new PlayerKnowledgeStore(dir);
                store.Touch(1234, n, now);
                store.Touch(1234, "N999", now);      // detail-free, the shape a legacy entry has
                store.SaveIfDirty();

                var reloaded = new PlayerKnowledgeStore(dir);
                KnownNode snapshot;
                Assert.True(reloaded.For(1234).TryGetSnapshot("N283", now, TimeSpan.FromDays(30), out snapshot));
                Assert.Equal("UNKN", snapshot.OwnerTag);
                Assert.Equal(0.42, snapshot.HoldStrength);
                Assert.Equal(0.31, snapshot.ResourceValue);
                Assert.False(reloaded.For(1234).TryGetSnapshot("N999", now, TimeSpan.FromDays(30), out snapshot));
                Assert.True(reloaded.For(1234).HasLive("N999", now, TimeSpan.FromDays(30)));

                // Lean JSON: a detection that carried nothing still costs one timestamp, not an object.
                string json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "knowledge.json"));
                Assert.Contains("\"N999\": \"2026-09-20T12:00:00Z\"", json, StringComparison.Ordinal);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PlayerStore_LoadsAFileWrittenBeforeEntriesCarriedDetail()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fac357-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "knowledge.json"),
                    "{ \"SchemaVersion\": 1, \"Players\": { \"1234\": { \"N283\": \"2026-09-08T12:02:47Z\" } } }");
                var store = new PlayerKnowledgeStore(dir);
                DateTime seen;
                Assert.Equal(1, store.PlayerCount);
                Assert.True(store.For(1234).TryGetLastSeen("N283", out seen));
                Assert.Equal(new DateTime(2026, 9, 8, 12, 2, 47, DateTimeKind.Utc), seen);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ShippedDefaults_CarryARealHorizonAndRanges()
        {
            // The trap this guards is a field left at its C# zero-value: a zero horizon expires every
            // entry on the first read, and a zero range detects nothing. Both look like "the feature
            // does not work" rather than like a config error.
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(TimeSpan.FromDays(30), t.DiscoveryHorizon);
            // Must exceed the passive sensor's effective reach (25km x a station's 2.0 signature =
            // 50km) or the birthright clause is a no-op the sweep would satisfy anyway.
            Assert.True(t.BirthrightRangeKm > t.SensorNodePassiveRangeKm * Signature.ForNode(
                new SpatialNode { StableStationId = 1 }));
            Assert.True(t.SensorNodePassiveRangeKm > 0);
            Assert.True(t.SensorArrayRangeKmPerLevel > 0);
            Assert.True(t.SensorFleetRangeKm > 0);
        }
    }
}
