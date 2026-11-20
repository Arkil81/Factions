using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Node threat is a level, not a ratchet (FAC-261). It rises when a node is attacked, ages away
    /// on its own in the global decay pass, and survives a restart — before this it did none of the
    /// last two, which made every threshold reading it a threshold on server uptime.
    /// </summary>
    public class ThreatDecayTests
    {
        private static SpatialNode Node(WorldModel w, string id, string owner, double threat)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            n.Threat = threat;
            return n;
        }

        // ---- the rate ----------------------------------------------------------------------------

        [Fact]
        public void TheRateIsAShareOfTheHour_AndZeroDisablesIt()
        {
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(0.7, t.ThreatDecayPerHour);                            // shipped default
            Assert.Equal(0.7, t.ThreatDecayForIntervalSeconds(3600.0), 6);
            Assert.Equal(0.35, t.ThreatDecayForIntervalSeconds(1800.0), 6);

            t.ThreatDecayPerHour = 0.0;                                          // the disable switch
            Assert.Equal(0.0, t.ThreatDecayForIntervalSeconds(3600.0));
            t.ThreatDecayPerHour = 0.7;
            Assert.Equal(0.0, t.ThreatDecayForIntervalSeconds(0));               // no elapsed time, no decay
        }

        /// <summary>What the default rate actually decides, and the number it was chosen against.
        /// A raid adds <c>RaidThreat</c>; an hour removes <c>ThreatDecayPerHour</c>. Their quotient is
        /// a break-even CADENCE: raided more often than every 30 minutes a node climbs to a
        /// flashpoint and stays one, raided less often it fades however long the attacker persists.
        ///
        /// <para>Measured live 2026-09-10/11 over 977 gaps between raids landing on one node: median
        /// 6.2 min, p90 23.8 min, p95 43.5 min. So the line sits between p90 and p95 — an assault at
        /// the cadence this world's raids actually keep sustains a flashpoint, and the tail that does
        /// not is a node being visited twice an hour, which is not a siege.</para></summary>
        [Fact]
        public void TheRateSetsABreakEvenRaidCadence_NotAnAbsoluteLevel()
        {
            FactionTuning t = TestSupport.Tuning();

            double breakEvenMinutes = t.RaidThreat / t.ThreatDecayPerHour * 60.0;
            Assert.Equal(30.0, breakEvenMinutes, 1);

            // Raided at the live median and p90 cadence: each raid adds more than the gap removes,
            // so threat climbs toward saturation however low it started.
            Assert.True(t.RaidThreat - t.ThreatDecayForIntervalSeconds(6.2 * 60) > 0, "median cadence must climb");
            Assert.True(t.RaidThreat - t.ThreatDecayForIntervalSeconds(23.8 * 60) > 0, "p90 cadence must climb");

            // And the other direction, which is the half that could only pass once decay exists:
            // beyond the break-even the same repeated raid no longer sustains anything.
            Assert.True(t.RaidThreat - t.ThreatDecayForIntervalSeconds(43.5 * 60) < 0, "p95 cadence must fade");

            // A node whose attacker has left drops under the 0.5 defensive trigger inside the hour
            // even from saturation, and is fully cold inside two.
            Assert.True(1.0 - t.ThreatDecayForIntervalSeconds(60 * 60) < 0.5, "an hour of quiet clears the trigger");
            Assert.True(1.0 - t.ThreatDecayForIntervalSeconds(120 * 60) <= 0, "two hours of quiet is cold");
        }

        // ---- the pass ----------------------------------------------------------------------------

        [Fact]
        public void DecayThreat_LowersEveryNode_AndFloorsAtZeroWithoutGoingNegative()
        {
            WorldModel w = TestSupport.World();
            SpatialNode hot = Node(w, "HOT", "DEF", 0.9);
            SpatialNode faint = Node(w, "FAINT", "DEF", 0.05);

            Assert.Equal(2, w.DecayThreat(0.2));                 // both moved

            Assert.Equal(0.7, hot.Threat, 6);
            Assert.Equal(0.0, faint.Threat);                     // floored, not negative
            Assert.Equal(1, w.DecayThreat(0.2));                 // the cold node is no longer counted
        }

        /// <summary>Threat is not territory. The reinforce grace period and the owner checks that
        /// gate hold decay must not gate this one: a node nobody owns still collects a raid's threat,
        /// and a node reinforced a minute ago is not thereby less dangerous.</summary>
        [Fact]
        public void DecayThreat_IgnoresOwnershipAndTheReinforceGrace_UnlikeHoldDecay()
        {
            WorldModel w = TestSupport.World();
            SpatialNode graveyard = Node(w, "UNOWNED", NeutralFaction.Tag, 0.8);
            SpatialNode justHeld = Node(w, "HELD", "DEF", 0.8);
            justHeld.LastReinforcedUtc = w.UtcNow;               // inside ReinforceGracePeriod

            // The control: hold decay declines both, for those two reasons.
            w.DecayInfluence(0.3);
            Assert.Equal(0.9, graveyard.HoldStrength, 6);
            Assert.Equal(0.9, justHeld.HoldStrength, 6);

            Assert.Equal(2, w.DecayThreat(0.3));
            Assert.Equal(0.5, graveyard.Threat, 6);
            Assert.Equal(0.5, justHeld.Threat, 6);
        }

        [Fact]
        public void DecayThreat_WithNoAmount_ChangesNothing()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Node(w, "N", "DEF", 0.8);

            Assert.Equal(0, w.DecayThreat(0));
            Assert.Equal(0, w.DecayThreat(-1));
            Assert.Equal(0.8, n.Threat);
        }

        // ---- rise and fall together --------------------------------------------------------------

        /// <summary>The behaviour the ticket is named for, on the real node rather than on the rate.
        /// A raid raises threat, time lowers it, and two raids inside the median gap still cross the
        /// trigger. The middle assertion is the one that could not have passed before: under the
        /// ratchet ten quiet minutes cost exactly nothing.</summary>
        [Fact]
        public void ThreatRisesOnARaid_FallsWithTime_AndStacksWhenRaidsAreClose()
        {
            WorldModel w = TestSupport.World();
            FactionTuning t = w.Tuning;
            SpatialNode n = Node(w, "N", "DEF", 0.0);

            n.Threat = Math.Min(1.0, n.Threat + t.RaidThreat);
            Assert.Equal(t.RaidThreat, n.Threat, 6);

            w.DecayThreat(t.ThreatDecayForIntervalSeconds(600.0));          // ten quiet minutes
            double afterTenMinutes = n.Threat;
            Assert.True(afterTenMinutes < t.RaidThreat, "ten minutes of quiet has to cost something");

            // A second raid at the live median gap still carries the node over the trigger.
            n.Threat = Math.Min(1.0, n.Threat + t.RaidThreat);
            Assert.True(n.Threat > 0.5, "two raids inside the median gap are a flashpoint");
        }

        // ---- the raise ---------------------------------------------------------------------------

        /// <summary>The paired write. Every "something hostile happened here" goes through one place
        /// now that there is a rate for these to balance against, so a new source of threat cannot
        /// pick its own clamping or skip the ceiling.</summary>
        [Fact]
        public void AddThreat_Clamps_IgnoresNonPositive_AndTakesAnIdOrANode()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Node(w, "N", "DEF", 0.4);

            w.AddThreat(n, 0.35);
            Assert.Equal(0.75, n.Threat, 6);

            w.AddThreat("N", 0.5);
            Assert.Equal(1.0, n.Threat, 6);          // ceiling, not 1.25

            w.AddThreat(n, 0);
            w.AddThreat(n, -1);                       // a decrease is DecayThreat's job, not this one
            Assert.Equal(1.0, n.Threat, 6);

            w.AddThreat("NOT_A_NODE", 0.5);           // no throw, nothing created
            Assert.False(w.Graph.Nodes.ContainsKey("NOT_A_NODE"));
        }

        // ---- persistence -------------------------------------------------------------------------

        [Fact]
        public void Threat_SurvivesASaveAndLoad()
        {
            WorldModel w = TestSupport.World();
            Node(w, "HOT", "DEF", 0.83);
            Node(w, "COLD", "DEF", 0.0);

            string dir = Path.Combine(Path.GetTempPath(), "factions-threat-" + Guid.NewGuid().ToString("N"));
            try
            {
                var st = new FactionState { Tag = "DEF", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, w.Graph.OwnedBy("DEF"), FactionStore.DefaultLogCap));

                // Lean JSON: the quiet node writes no key at all, which is nearly every node on a
                // nearly-always-quiet world.
                string json = File.ReadAllText(Path.Combine(dir, "brains", "DEF.json"));
                Assert.Equal(1, CountOccurrences(json, "\"Threat\""));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("DEF"), reloaded);

                Assert.Equal(0.83, reloaded.Nodes["HOT"].Threat, 6);
                Assert.Equal(0.0, reloaded.Nodes["COLD"].Threat);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>A brain written before this field existed carries no key; Newtonsoft leaves the
        /// default, which is the quiet node every node already was on every boot before threat was
        /// saved at all. Nothing to migrate.</summary>
        [Fact]
        public void ABrainWrittenBeforeTheFieldExisted_LoadsQuiet()
        {
            WorldModel w = TestSupport.World();
            Node(w, "N", "DEF", 0.9);

            string dir = Path.Combine(Path.GetTempPath(), "factions-threat-" + Guid.NewGuid().ToString("N"));
            try
            {
                var st = new FactionState { Tag = "DEF", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, w.Graph.OwnedBy("DEF"), FactionStore.DefaultLogCap));

                string path = File.ReadAllText(Path.Combine(dir, "brains", "DEF.json")).Contains("\"Threat\"")
                    ? Path.Combine(dir, "brains", "DEF.json")
                    : null;
                Assert.NotNull(path);
                string legacy = System.Text.RegularExpressions.Regex.Replace(
                    File.ReadAllText(path), "\\s*\"Threat\":\\s*[0-9.eE+-]+,?", "");
                Assert.Equal(0, CountOccurrences(legacy, "\"Threat\""));
                File.WriteAllText(path, legacy);

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("DEF"), reloaded);

                Assert.Equal(0.0, reloaded.Nodes["N"].Threat);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>Ordinal, deliberately. Every substring assertion in this file goes through it
        /// rather than through <c>Assert.Contains</c>/<c>DoesNotContain</c>, which compare by the
        /// current culture: the container these tests also run in forces invariant globalization,
        /// which turns culture comparisons ordinal, so a culture-sensitive assertion is verified by
        /// only one of the two places this suite runs. This repo has already shipped that bug once.</summary>
        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }
    }
}
