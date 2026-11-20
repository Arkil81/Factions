using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers ShipyardManager's resumable sweep: that it chunks at the faction boundary,
    /// that the chunked walk produces exactly what the synchronous <c>Tick()</c> produces, and that
    /// the cursor survives the roster changing underneath it between calls.</summary>
    public sealed class ShipyardCycleTests
    {
        private static readonly string[] Tags = { "AAA", "BBB", "CCC" };

        /// <summary>Three factions, one free-building shipyard each and no ship stock anywhere, so
        /// every faction's pass posts proactive demands and queues a build under its own tag. Built
        /// from scratch on each call so two runs can be compared against each other.</summary>
        private static void Build(out WorldModel world, out List<FactionState> states)
        {
            world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            states = new List<FactionState>();
            for (int i = 0; i < Tags.Length; i++) AddFaction(world, states, Tags[i], i);
        }

        private static void AddFaction(WorldModel world, List<FactionState> states, string tag, int slot)
        {
            SpatialNode n = world.Graph.Add(new Vec3(slot * 100000.0, 0, 0), 1.0, tag + "1");
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.StableStationId = 42 + slot;
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            states.Add(new FactionState { Tag = tag });
        }

        /// <summary>A sweep suspended across frames still lands every faction's whole pass. The world
        /// clock advances between steps exactly as it does between host frames -- the one input that
        /// is guaranteed to differ from the old one-call Tick() -- and each faction still queues and
        /// free-builds its own hull at its own node.
        /// <para>Deliberately not written as "stepped run == Tick() run": Tick() is now a wrapper over
        /// the same TryAdvance, so comparing the two compares the implementation with itself and
        /// cannot fail. The pre-chunking outcome is asserted directly instead.</para></summary>
        [Fact]
        public void SweepSuspendedAcrossFrames_StillCompletesEveryFactionsPass()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);

            int steps = 0;
            while (!Step(mgr, states, world, log))
            {
                world.UtcNow = world.UtcNow.AddSeconds(5);   // one frame's worth of clock per chunk
                Assert.True(++steps < 1000, "Shipyard sweep did not complete");
            }

            foreach (string tag in Tags)
            {
                Assert.Contains(log, l => l.StartsWith(tag + " BUILD_QUEUED role=Trade node=" + tag + "1",
                                                       StringComparison.Ordinal));
                Assert.True(TotalShipStock(world.Graph.Nodes[tag + "1"]) > 0, tag + " built nothing");
                Assert.Equal(0, world.Demand.SnapshotShipDemands(tag).Count(d => d.Kind == "Ship:Trade"));
            }
        }

        /// <summary>The chunking claim itself: one call advances by one faction, not by the roster.
        /// Without this the sweep can be "resumable" and still be one 18 ms call.</summary>
        [Fact]
        public void StepSweepOnce_AdvancesExactlyOneFactionPerCall()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);

            Step(mgr, states, world, log);
            Assert.Contains(log, l => Tagged(l, "AAA"));
            Assert.DoesNotContain(log, l => Tagged(l, "BBB"));
            Assert.DoesNotContain(log, l => Tagged(l, "CCC"));

            Step(mgr, states, world, log);
            Assert.Contains(log, l => Tagged(l, "BBB"));
            Assert.DoesNotContain(log, l => Tagged(l, "CCC"));
        }

        /// <summary>Every faction is visited once and only once, and the sweep reports complete only
        /// after the whole roster has been walked -- the unit-test counterpart of FAC-291's
        /// acceptance 4 (a chunked sweep that silently drops a faction per cycle).</summary>
        [Fact]
        public void Sweep_VisitsEveryFactionExactlyOnce_AndReportsCompleteOnlyAtTheEnd()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);
            Assert.True(IsSweepInProgress(mgr));

            int steps = RunToCompletion(mgr, states, world, log);

            // Three faction units plus the one extra call that notices roster exhaustion.
            Assert.Equal(Tags.Length + 1, steps);
            Assert.False(IsSweepInProgress(mgr));
            foreach (string tag in Tags)
                Assert.Single(log.Where(l => l.StartsWith(tag + " SHIP_DEMAND_PROACTIVE role=Trade",
                                                          StringComparison.Ordinal)));
        }

        /// <summary>The cursor holds tags, not list indices. Both an already-visited faction and a
        /// not-yet-visited one disappear mid-sweep: the one that is gone is skipped, and the survivor
        /// is still reached at its own tag rather than at a list index two entries have shifted out
        /// from under.</summary>
        [Fact]
        public void RosterShrinkingMidSweep_SkipsTheGoneFaction_AndStillReachesTheRest()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);
            Step(mgr, states, world, log);      // AAA

            states.RemoveAll(s => s.Tag == "AAA" || s.Tag == "BBB");
            RunToCompletion(mgr, states, world, log);

            Assert.False(IsSweepInProgress(mgr));
            Assert.DoesNotContain(log, l => Tagged(l, "BBB"));
            Assert.Contains(log, l => Tagged(l, "CCC"));
        }

        /// <summary>A faction that joins mid-sweep is picked up by the NEXT sweep. The roster order
        /// is snapshotted when the cursor is created, so a late arrival cannot be half-serviced by a
        /// pass that already computed its floors without it.</summary>
        [Fact]
        public void FactionAddedMidSweep_WaitsForTheNextSweep()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);
            Step(mgr, states, world, log);      // AAA

            AddFaction(world, states, "DDD", Tags.Length);
            RunToCompletion(mgr, states, world, log);
            Assert.DoesNotContain(log, l => Tagged(l, "DDD"));

            var nextLog = new List<string>();
            mgr.Tick(states, world, world.Tuning, nextLog);
            Assert.Contains(nextLog, l => Tagged(l, "DDD"));
        }

        /// <summary>Demand/investment expiry is charged once at the head of a sweep, not on every
        /// resumed call. A TTL that lapses while the sweep is suspended must not retire a posting the
        /// factions still ahead of the cursor were about to be serviced against; it is the next
        /// sweep's business.</summary>
        [Fact]
        public void RegisterPrune_RunsOnceAtSweepStart_NotOnEveryResumedCall()
        {
            WorldModel world; List<FactionState> states;
            Build(out world, out states);
            // Owned by a faction that is not on the roster, so nothing re-posts it and its expiry
            // line is unambiguously the prune's.
            world.Demand.Post(new DemandEntry
            {
                FactionTag     = "ZZZ",
                Kind           = "Ship:Trade",
                NodeId         = "AAA1",
                FirstPostedUtc = world.UtcNow,
                ExpiresUtc     = world.UtcNow.AddSeconds(30),
            });

            var log = new List<string>();
            var mgr = new ShipyardManager();
            BeginSweep(mgr, states, world, log);   // still live at this instant
            Step(mgr, states, world, log);         // AAA
            world.UtcNow = world.UtcNow.AddSeconds(120);   // lapses mid-sweep
            RunToCompletion(mgr, states, world, log);

            Assert.DoesNotContain(log, l => l.StartsWith("ZZZ SHIP_DEMAND_EXPIRED", StringComparison.Ordinal));

            // Retired by the next sweep's head prune, so nothing is left stranded either.
            var nextLog = new List<string>();
            mgr.Tick(states, world, world.Tuning, nextLog);
            Assert.Contains(nextLog, l => l.StartsWith("ZZZ SHIP_DEMAND_EXPIRED", StringComparison.Ordinal));
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static bool Tagged(string line, string tag)
        {
            return line.StartsWith(tag + " ", StringComparison.Ordinal);
        }

        private static long TotalShipStock(SpatialNode node)
        {
            long total = 0;
            foreach (KeyValuePair<string, int> kv in node.ShipStock) total += kv.Value;
            return total;
        }

        /// <summary>Steps until the sweep reports complete; returns how many calls that took.</summary>
        private static int RunToCompletion(ShipyardManager mgr, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            int steps = 0;
            while (!Step(mgr, states, world, log))
            {
                steps++;
                Assert.True(steps < 1000, "Shipyard sweep did not complete");
            }
            return steps + 1;
        }

        private static void BeginSweep(ShipyardManager mgr, IList<FactionState> states, WorldModel world,
            List<string> log)
        {
            Invoke(mgr, "BeginSweepForTesting", states, world, log);
        }

        private static bool Step(ShipyardManager mgr, IList<FactionState> states, WorldModel world,
            List<string> log)
        {
            return (bool)Invoke(mgr, "StepSweepOnceForTesting", states, world, world.Tuning, log);
        }

        private static bool IsSweepInProgress(ShipyardManager mgr)
        {
            return (bool)Invoke(mgr, "IsSweepInProgressForTesting");
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    }
}
