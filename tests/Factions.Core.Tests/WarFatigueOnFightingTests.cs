using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-253: war fatigue is weariness from <b>fighting</b>, not from disliking someone.
    ///
    /// <para>Measured live 2026-09-04T11:10Z, and the numbers are the whole argument: across 88
    /// raid/conquer resolutions in the preceding 79 hours, <b>every one of the five saturated fatigue
    /// entries sat on a pair that had never exchanged a raid</b>, and 29 of 41 entries overall were on
    /// never-fought pairs — while SPRT, which committed <b>74 of the 88</b> resolutions, peaked at
    /// 0.64 with nothing saturated. War-weariness was anti-correlated with fighting a war.</para>
    ///
    /// <para>The window is measured too, not chosen: per-pair inter-arrival ran p25 28 min, median
    /// 72 min, p75 163 min, so the shipped 14400 s spans 80% of real gaps.</para></summary>
    public class WarFatigueOnFightingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Two factions with territory, so `AccrueFatigue`'s presence test passes and the
        /// only thing under examination is whether they have actually fought.</summary>
        private static WorldModel World(DateTime now)
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
            w.UtcNow = now;
            return w;
        }

        private static FactionState Hostile()
        {
            var s = new FactionState { Tag = "A" };
            s.Relations["B"] = -0.9;          // well past HostileRelationThreshold
            s.EarnedRelations["B"] = -0.9;    // and on the record, so Drift cannot talk it back up
            return s;
        }

        /// <summary>A convoy sent at "B" and destroyed — the only thing that now costs a faction
        /// anything, and therefore the only thing it can tire from. <paramref name="dps"/> is the
        /// convoy's own combat power, which is what the duel spends as its effective HP.</summary>
        private static void LostAConvoyTo(FactionState s, string otherTag, DateTime whenUtc, long dps = 1000)
        {
            FightLedger.Append(s, new FightRecord
            {
                Utc = whenUtc,
                Kind = ActionKind.Raid,
                Attacking = true,
                OpponentTag = otherTag,
                NodeId = "B1",
                AttackerDps = dps,
                AttackerRatio = 0,
                AttackerHullsLost = 1,
            }, 64);
            Diplomacy.HitFatigue(s, otherTag, 0.0, whenUtc);   // the blow, stamped
        }

        private static double Accrue(FactionState s, FactionTuning t, DateTime from, int passes)
        {
            for (int i = 0; i < passes; i++)
            {
                DateTime now = from.AddSeconds(i * 180.0);   // one global pass
                Diplomacy.AccrueFatigue(s, World(now), t);
            }
            return s.FatigueTo("B");
        }

        // ---- the defect ---------------------------------------------------------------------------

        /// <summary>The headline. A pair that merely dislikes each other and has never exchanged a blow
        /// must not tire. Before this fix a sour standing alone saturated fatigue in about eight
        /// minutes of live time.</summary>
        [Fact]
        public void APairThatHasNeverFought_DoesNotTire()
        {
            var t = TestSupport.Tuning();
            var s = Hostile();

            Assert.Equal(0.0, Accrue(s, t, T0, 200), 9);
            Assert.Empty(s.LastHostileActUtc);
        }

        /// <summary>...and a pair that IS fighting still does. This is the arm that keeps the change
        /// from being "delete war fatigue".</summary>
        [Fact]
        public void APairFightingNow_StillTires()
        {
            var t = TestSupport.Tuning();
            var s = Hostile();
            LostAConvoyTo(s, "B", T0);                         // a raid goes in and does not come back

            double settled = Accrue(s, t, T0, 40);             // 40 passes = 2 h, inside the window

            Assert.True(settled > 0,
                "a pair still exchanging blows must tire; got " + settled);
        }

        /// <summary>Past the window, the war is over and the weariness fades — which is what lets a
        /// stalemate resolve instead of freezing. The live world had gone <b>11.6 hours</b> without a
        /// single resolution while six entries sat pinned at 1.0.</summary>
        [Fact]
        public void OnceTheFightingStops_FatigueDecaysAgain()
        {
            var t = TestSupport.Tuning();
            var s = Hostile();
            LostAConvoyTo(s, "B", T0);                        // spent, right now
            Assert.True(Accrue(s, t, T0, 1) > 0);

            // Well past WarFatigueActWindowSeconds, with no further blows.
            DateTime later = T0.AddSeconds(t.WarFatigueActWindowSeconds + 600);
            double after = Accrue(s, t, later, 40);

            Assert.True(after < 0.7,
                "with the fighting stopped, fatigue must fade rather than hold at 1.0; got " + after);
        }

        /// <summary>Inside the window it holds; a blow re-arms it. A war that is still being fought
        /// does not cool just because the last exchange was an hour ago.</summary>
        [Fact]
        public void AFreshBlow_ReArmsTheWindow()
        {
            var t = TestSupport.Tuning();
            var s = Hostile();
            LostAConvoyTo(s, "B", T0);

            DateTime nearlyExpired = T0.AddSeconds(t.WarFatigueActWindowSeconds - 300);
            Assert.True(Accrue(s, t, nearlyExpired, 1) > 0, "still inside the window, so the loss still counts");

            DateTime afterOldWindow = T0.AddSeconds(t.WarFatigueActWindowSeconds + 600);
            LostAConvoyTo(s, "B", afterOldWindow.AddSeconds(-60));      // a fresh exchange
            Assert.Equal(afterOldWindow.AddSeconds(-60), s.LastHostileActUtc["B"]);
            Assert.True(Accrue(s, t, afterOldWindow, 1) > 0,
                "the fresh blow must carry the pair past where the original window would have expired");
        }

        // ---- the knob and the seams ---------------------------------------------------------------

        /// <summary>A window of 0 or less disables the recency gate, so the knob can be turned off live
        /// without a deploy if the constant proves wrong. It has to mean the same thing on both halves
        /// of the derivation: with the gate off a long-past loss still counts, where with the gate on
        /// the same loss has aged out. A window that said "always at war" while counting a window of
        /// no losses would disable the feature by making it read zero.</summary>
        [Fact]
        public void AZeroWindow_CountsEveryRecordedLoss()
        {
            var t = TestSupport.Tuning();
            var s = Hostile();
            DateTime longAgo = T0.AddSeconds(-(t.WarFatigueActWindowSeconds * 4));
            LostAConvoyTo(s, "B", longAgo);

            Assert.Equal(0.0, Accrue(s, t, T0, 1), 9);          // gated: aged out

            t.WarFatigueActWindowSeconds = 0;
            Assert.True(Accrue(s, t, T0, 1) > 0,
                "with the gate disabled the same loss must count");
        }

        /// <summary>The stamp lives in `HitFatigue` precisely because every conflict path already funnels
        /// through it for both sides. `DeclareWar` is the other caller, and it must stamp too — a
        /// declared war is a hostile act.</summary>
        [Fact]
        public void DeclareWar_StampsTheActToo()
        {
            var t = TestSupport.Tuning();
            var victim = new FactionState { Tag = "A" };

            Diplomacy.DeclareWar(victim, "B", t, T0);

            Assert.Equal(T0, victim.LastHostileActUtc["B"]);

            // The stamp is what the window reads; the cost is what the ledger records. A declared war
            // with nothing spent on it yet is a war this faction is not yet tired of.
            victim.Relations["B"] = -0.9; victim.EarnedRelations["B"] = -0.9;
            Assert.Equal(0.0, Accrue(victim, t, T0, 40), 9);
            LostAConvoyTo(victim, "B", T0);
            Assert.True(Accrue(victim, t, T0, 1) > 0, "once it has cost something, it tires");
        }

        /// <summary>The stamp has to survive a restart, or every reload forgives every war in progress.</summary>
        [Fact]
        public void TheStamp_RoundTripsThroughPersistence()
        {
            var s = new FactionState { Tag = "A" };
            Diplomacy.HitFatigue(s, "B", 0.4, T0);

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "factions-fac253-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                var graph = new SpatialGraph();
                store.SaveBrain(FactionStore.ToBrainDoc(s, graph.OwnedBy("A"), FactionStore.DefaultLogCap));
                FactionState loaded = FactionStore.ApplyBrainDoc(store.LoadBrain("A"), new SpatialGraph());

                Assert.Equal(T0, loaded.LastHostileActUtc["B"]);
                Assert.Equal(0.4, loaded.FatigueTo("B"), 6);
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }
    }
}
