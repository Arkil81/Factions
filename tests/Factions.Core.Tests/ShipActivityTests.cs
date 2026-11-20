using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Ship traffic as a passive hold trickle (<see cref="ShipActivity"/>): a node an
    /// owning-faction fleet keeps visiting climbs back towards full hold on traffic alone, a dead
    /// corridor bleeds out at the full <see cref="FactionTuning.HoldDecayPerHour"/> and abandons.</summary>
    public class ShipActivityTests
    {
        private sealed class FixedRandom : IRandom
        {
            public double NextDouble() { return 0.0; }
            public int Next(int minInclusive, int maxExclusive) { return minInclusive; }
        }

        private static SpatialNode Owned(WorldModel w, string id, string owner, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        /// <summary>A node whose credit is at <paramref name="credits"/> as of the world's clock.</summary>
        private static SpatialNode WithCredit(WorldModel w, SpatialNode n, double credits)
        {
            n.ShipActivityCredits = credits;
            n.ShipActivityAtUtc = w.UtcNow;
            return n;
        }

        // ---- the accumulator -------------------------------------------------------------------

        [Fact]
        public void Credit_HalvesOverExactlyOneHalfLife()
        {
            var t = new FactionTuning
            {
                ShipActivityCreditPerVisit = 1.0,
                ShipActivityCreditCap = 3.0,
                ShipActivityHalfLifeSeconds = 3600.0,
                ShipActivityHoldPerHour = 0.30,
            };
            var node = new SpatialNode { Id = "N1" };
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            node.ShipActivityCredits = 3.0;
            node.ShipActivityAtUtc = t0;

            Assert.Equal(3.0, ShipActivity.CreditsOf(node, t, t0), 9);
            Assert.Equal(1.5, ShipActivity.CreditsOf(node, t, t0.AddSeconds(3600)), 9);
            Assert.Equal(0.75, ShipActivity.CreditsOf(node, t, t0.AddSeconds(7200)), 9);
        }

        [Fact]
        public void Credit_SaturatesAtTheCap_HoweverManyVisitsLand()
        {
            var t = new FactionTuning
            {
                ShipActivityCreditPerVisit = 1.0,
                ShipActivityCreditCap = 3.0,
                ShipActivityHalfLifeSeconds = 3600.0,
                ShipActivityHoldPerHour = 0.30,
            };
            var node = new SpatialNode { Id = "N1" };
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            for (int i = 0; i < 20; i++) ShipActivity.Credit(node, t, t0);

            Assert.Equal(3.0, node.ShipActivityCredits, 9);
            Assert.Equal(3.0, ShipActivity.CreditsOf(node, t, t0), 9);
        }

        // ---- the trickle, in the decay pass ----------------------------------------------------

        /// <summary>The pair is the test, and it has to stay one test: a change that zeroed the whole
        /// decay pass would leave the busy node climbing and be caught only by the dead one, which
        /// must still lose the full hour's worth.</summary>
        [Fact]
        public void SaturatedNodeGainsHold_WhileADeadCorridorLosesTheFullHourlyDecay()
        {
            WorldModel w = TestSupport.World();
            Assert.Equal(0.24, w.Tuning.HoldDecayForIntervalSeconds(3600.0), 9);   // shipped default
            Assert.Equal(1.20, w.Tuning.ShipActivityHoldPerHour, 9);

            // A TEN-MINUTE pass, not an hour: at the shipped 1.20/h a saturated node gains 1.20 in an
            // hour and clamps at full hold, which would make this assert a ceiling instead of a rate.
            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            Assert.Equal(0.04, per10, 9);

            SpatialNode busy = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            SpatialNode dead = Owned(w, "N002", "F", 0.5);      // never visited: zero credit

            w.DecayInfluence(per10, per10, null, 600.0);

            Assert.Equal(0.5 + 0.20 - 0.04, busy.HoldStrength, 9);   // +1.20/h gained, -0.24/h decayed
            Assert.Equal(0.5 - 0.04, dead.HoldStrength, 9);
        }


        // ---- FAC-362: a contested node decays but does not heal ---------------------------------

        /// <summary>The pair that carries the ticket. Two identically-trafficked, identically-held
        /// nodes in one pass; only one of them has been raided. The uncontested arm is the control,
        /// and it must still gain — a change that simply switched the channel off would pass the
        /// contested assert and fail this one.</summary>
        [Fact]
        public void AContestedNodeDecaysWithoutHealing_AnUncontestedOneStillGains()
        {
            WorldModel w = TestSupport.World();
            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);

            SpatialNode quiet = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            SpatialNode besieged = WithCredit(w, Owned(w, "N002", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            besieged.ContestedUntilUtc = w.UtcNow.AddSeconds(w.Tuning.ContestedHoldGainSuppressedSeconds);

            w.DecayInfluence(per10, per10, null, 600.0);

            Assert.Equal(0.5 + 0.20 - 0.04, quiet.HoldStrength, 9);    // gains, as it always did
            Assert.Equal(0.5 - 0.04, besieged.HoldStrength, 9);        // decays only
        }

        /// <summary>The window is a window: once it lapses the node resumes climbing on the credit it
        /// went on banking throughout, so a siege withholds regeneration rather than destroying it.</summary>
        [Fact]
        public void TheContestWindowLapses_AndTheNodeResumesGaining()
        {
            WorldModel w = TestSupport.World();
            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            SpatialNode n = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            n.ContestedUntilUtc = w.UtcNow.AddSeconds(60);

            w.DecayInfluence(per10, per10, null, 600.0);
            Assert.Equal(0.5 - 0.04, n.HoldStrength, 9);

            w.UtcNow = w.UtcNow.AddSeconds(120);
            WithCredit(w, n, w.Tuning.ShipActivityCreditCap);
            w.DecayInfluence(per10, per10, null, 600.0);
            Assert.Equal(0.5 - 0.04 + 0.20 - 0.04, n.HoldStrength, 9);
        }

        /// <summary>A landed raid is what stamps the window — the two halves have to be connected or
        /// the flag is inert. Read through <see cref="WorldModel.Raid"/> rather than set by hand.</summary>
        [Fact]
        public void ALandedRaidMarksTheNodeContested()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Owned(w, "N001", "F", 1.0);
            Assert.True(w.UtcNow >= n.ContestedUntilUtc, "a node starts uncontested");

            w.Raid("RVN", "N001", 1.0);

            Assert.True(n.ContestedUntilUtc > w.UtcNow, "a landed raid must contest the node");
            Assert.Equal(w.UtcNow.AddSeconds(w.Tuning.ContestedHoldGainSuppressedSeconds),
                         n.ContestedUntilUtc);
        }

        /// <summary>Zero is the kill switch, and it has to restore the previous behaviour exactly
        /// rather than leaving a node permanently contested or permanently healing.</summary>
        [Fact]
        public void ZeroSuppressionSeconds_LeavesTheChannelExactlyAsItWas()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.ContestedHoldGainSuppressedSeconds = 0;
            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            SpatialNode n = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap);

            w.Raid("RVN", "N001", 1.0);
            double afterStrip = n.HoldStrength;
            WithCredit(w, n, w.Tuning.ShipActivityCreditCap);
            w.DecayInfluence(per10, per10, null, 600.0);

            Assert.Equal(default(DateTime), n.ContestedUntilUtc);
            Assert.Equal(afterStrip + 0.20 - 0.04, n.HoldStrength, 9);
        }

        /// <summary>Half the credit gains half the hourly rate — proportional, not a switch. Break-even
        /// sits at <c>HoldDecayPerHour / ShipActivityHoldPerHour</c> = 0.2 of the cap, so half
        /// saturation gains.</summary>
        [Fact]
        public void HalfCreditGainsHalfTheHourlyRate()
        {
            WorldModel w = TestSupport.World();
            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            SpatialNode half = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap / 2.0);

            w.DecayInfluence(per10, per10, null, 600.0);

            // Half the cap gains half the rate: 1.20/2 = 0.60/h = 0.10 over ten minutes, against
            // 0.04 of decay. Break-even is HoldDecayPerHour/ShipActivityHoldPerHour = 0.2 of the cap,
            // so half saturation is well inside credit -- the point of the shipped pair.
            Assert.Equal(0.5 + 0.10 - 0.04, half.HoldStrength, 9);
        }

        /// <summary>Traffic alone genuinely gains hold — that is what lets a node taken at
        /// <c>ConquerHoldCeiling</c> recover without a paid Reinforce — and stops at full hold.</summary>
        [Fact]
        public void SaturatedTrafficRaisesHold_AndIsCappedAtFullHold()
        {
            WorldModel w = TestSupport.World();
            double net = w.Tuning.ShipActivityHoldPerHour - w.Tuning.HoldDecayPerHour;
            Assert.True(net > 0, "a saturated node must gain, not merely hold steady");

            SpatialNode taken = WithCredit(w, Owned(w, "N001", "F", w.Tuning.ConquerHoldCeiling),
                w.Tuning.ShipActivityCreditCap);
            SpatialNode full = WithCredit(w, Owned(w, "N002", "F", 1.0), w.Tuning.ShipActivityCreditCap);

            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            w.DecayInfluence(per10, per10, null, 600.0);

            Assert.Equal(w.Tuning.ConquerHoldCeiling + net / 6.0, taken.HoldStrength, 9);
            Assert.Equal(1.0, full.HoldStrength, 9);            // clamped, never above full hold
        }

        /// <summary>A pass given no interval (every existing overload) behaves exactly as before —
        /// credit is ignored entirely.</summary>
        [Fact]
        public void PassWithNoInterval_IgnoresCreditAndDecaysAsBefore()
        {
            WorldModel w = TestSupport.World();
            SpatialNode busy = WithCredit(w, Owned(w, "N001", "F", 0.9), w.Tuning.ShipActivityCreditCap);

            w.DecayInfluence(0.03);

            Assert.Equal(0.87, busy.HoldStrength, 9);
        }

        /// <summary>Grace skips the whole pass, so a graced node neither decays nor gains. It carries
        /// saturated credit deliberately: with zero credit only half the claim would be tested.</summary>
        [Fact]
        public void NodeInsideReinforceGrace_IsUntouchedByBothDecayAndTrickle()
        {
            WorldModel w = TestSupport.World();
            Assert.True(w.Tuning.ReinforceGracePeriod > TimeSpan.Zero);

            SpatialNode graced = WithCredit(w, Owned(w, "N001", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            graced.LastReinforcedUtc = w.UtcNow;
            SpatialNode busy = WithCredit(w, Owned(w, "N002", "F", 0.5), w.Tuning.ShipActivityCreditCap);
            SpatialNode dead = Owned(w, "N003", "F", 0.5);      // outside grace, zero credit

            double per10 = w.Tuning.HoldDecayForIntervalSeconds(600.0);
            w.DecayInfluence(per10, per10, null, 600.0);

            Assert.Equal(0.5, graced.HoldStrength, 9);          // no decay and no gain
            Assert.Equal(0.5 + 0.20 - 0.04, busy.HoldStrength, 9);   // the trickle is live this pass
            Assert.Equal(0.5 - 0.04, dead.HoldStrength, 9);
        }

        /// <summary>The two existing end-of-decay behaviours survive the trickle: an engine node at
        /// zero credit still abandons to UNKN, a scan-backed one still floors at 0.1.</summary>
        [Fact]
        public void ZeroCreditNodesStillAbandonToUnkn_AndScanBackedOnesStillFloor()
        {
            WorldModel w = TestSupport.World();
            SpatialNode engine = Owned(w, "N001", "F", 0.1);
            string scanBackedId = SourceMapping.GridSourceId(456);
            SpatialNode scanBacked = Owned(w, scanBackedId, "F", 0.1);
            SpatialNode busy = WithCredit(w, Owned(w, "N002", "F", 0.1), w.Tuning.ShipActivityCreditCap);

            w.DecayInfluence(0.24, 0.24, null, 3600.0);

            Assert.True(w.Graph.Nodes.ContainsKey("N001"));
            Assert.Equal(NeutralFaction.Tag, engine.OwnerFactionTag);
            Assert.Equal(0.1, scanBacked.HoldStrength, 9);
            Assert.Equal("F", scanBacked.OwnerFactionTag);
            // At the shipped 1.20/h an hour's saturated gain exceeds full hold, so this clamps at 1.0.
            // It still carries the claim: without the trickle this node reaches 0 and abandons like
            // the engine node above, so 1.0 and "still owned by F" cannot both hold by accident.
            Assert.Equal(1.0, busy.HoldStrength, 9);            // saturated traffic saved it and gained
            Assert.Equal("F", busy.OwnerFactionTag);
        }

        // ---- crediting a visit -----------------------------------------------------------------

        private static PendingFleetAction ArrivingPatrol(WorldModel w, string actor, string originId, string nodeId)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Patrol,
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                ActorTag = actor,
                OriginNodeId = originId,
                TargetNodeId = nodeId,
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = originId, Kind = FleetStopKind.Return }
                },
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 500, RangeM = 2000 } },
                LastResolvedSegment = -1,
            };
        }

        /// <summary>Someone else's convoy passing through is not this node's territorial presence.
        /// The owning-faction stop beside it is the control: the same fleet kind on the same code
        /// path, differing only in who owns the node it lands on.</summary>
        [Fact]
        public void OnlyTheOwningFactionsFleetCreditsTheNode()
        {
            WorldModel w = TestSupport.World();
            var mercs = new FactionState { Tag = "MERC", RngSeed = 1 };
            Owned(w, "HOME", "MERC", 0.8);
            SpatialNode own = Owned(w, "MERC1", "MERC", 0.8);
            SpatialNode foreign = Owned(w, "RVN1", "RVN", 0.8);
            mercs.PendingFleets.Add(ArrivingPatrol(w, "MERC", "HOME", "MERC1"));
            mercs.PendingFleets.Add(ArrivingPatrol(w, "MERC", "HOME", "RVN1"));

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { mercs }, w, w.Tuning,
                new FixedRandom(), new List<string>(), null);

            Assert.Equal(w.Tuning.ShipActivityCreditPerVisit, own.ShipActivityCredits, 9);
            Assert.Equal(0.0, foreign.ShipActivityCredits, 9);
        }

        [Fact]
        public void CreditShipActivity_IgnoresAnUnknownNodeAndAForeignActor()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Owned(w, "N001", "F", 0.8);

            Assert.Equal(0.0, w.CreditShipActivity("N999", "F"), 9);
            Assert.Equal(0.0, w.CreditShipActivity("N001", "OTHER"), 9);
            Assert.Equal(0.0, n.ShipActivityCredits, 9);

            Assert.Equal(w.Tuning.ShipActivityCreditPerVisit, w.CreditShipActivity("N001", "F"), 9);
            Assert.Equal(w.UtcNow, n.ShipActivityAtUtc);
        }

        // ---- persistence -----------------------------------------------------------------------

        /// <summary>Durable: it is an accumulator, rebuildable from nothing else. A node that has
        /// never been visited writes no field at all — the brain files are loaded one at a time and
        /// every node carries this pair.</summary>
        [Fact]
        public void Credit_RoundTrips_AndIsOmittedWhenZero()
        {
            var graph = new SpatialGraph();
            DateTime stamp = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);
            SpatialNode visited = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            visited.OwnerFactionTag = "F";
            visited.ShipActivityCredits = 2.5;
            visited.ShipActivityAtUtc = stamp;
            SpatialNode never = graph.Add(new Vec3(50, 0, 0), 1.0, "N2");
            never.OwnerFactionTag = "F";

            var st = new FactionState { Tag = "F" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("F"), reloaded);

                Assert.Equal(2.5, reloaded.Nodes["N1"].ShipActivityCredits, 9);
                Assert.Equal(stamp, reloaded.Nodes["N1"].ShipActivityAtUtc);
                Assert.Equal(0.0, reloaded.Nodes["N2"].ShipActivityCredits, 9);
                Assert.Equal(default(DateTime), reloaded.Nodes["N2"].ShipActivityAtUtc);

                string json = File.ReadAllText(Path.Combine(dir, "brains", "F.json"));
                Assert.Equal(1, CountOccurrences(json, "ShipActivityCredits"));
                Assert.Equal(1, CountOccurrences(json, "ShipActivityAtUtc"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>A node file written before this shipped carries neither field; it loads at zero
        /// credit and simply starts accruing.</summary>
        [Fact]
        public void ANodeFileWithoutTheFields_LoadsAtZeroCreditAndAccruesNormally()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";

            var st = new FactionState { Tag = "F" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap));
                string path = Path.Combine(dir, "brains", "F.json");
                Assert.DoesNotContain("ShipActivityCredits", File.ReadAllText(path));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("F"), reloaded);
                SpatialNode loaded = reloaded.Nodes["N1"];

                Assert.Equal(0.0, loaded.ShipActivityCredits, 9);

                FactionTuning t = TestSupport.Tuning();
                DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Assert.Equal(t.ShipActivityCreditPerVisit, ShipActivity.Credit(loaded, t, now), 9);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }
    }
}
