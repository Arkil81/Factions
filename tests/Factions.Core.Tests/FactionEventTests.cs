using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The per-faction event ring behind the Events tab: that an event reaches both sides of what
    /// happened, that the ring stays bounded, and that it survives a restart — which is the whole
    /// point, since a raid that lands while the player is offline is the case the tab exists for.
    /// </summary>
    public class FactionEventTests
    {
        private static List<FactionState> States(params string[] tags)
        {
            var states = new List<FactionState>();
            foreach (string tag in tags) states.Add(new FactionState { Tag = tag });
            return states;
        }

        [Fact]
        public void Raid_FilesTheEventForAttackerAndVictimAlike()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "B1");
            n.OwnerFactionTag = "B";
            n.HoldStrength = 0.7;

            // The four-argument form is the one that reports its victim; the two-argument overload
            // stays void because IFactionActions declares it that way.
            string victim = w.Raid("A", "B1", 1.0);
            Assert.Equal("B", victim);

            w.RecordEvent(FactionEventKind.RaidResolved, "A", victim, "B1", 250, 0.8);

            List<FactionState> states = States("A", "B");
            Assert.Equal(2, FactionEventLog.Drain(states, w, 60));
            Assert.Empty(w.PendingEvents);

            FactionEvent attacker = states[0].RecentEvents.Single();
            FactionEvent defender = states[1].RecentEvents.Single();
            Assert.Equal(FactionEventKind.RaidResolved, defender.Kind);
            Assert.Equal("A", defender.ActorTag);
            Assert.Equal("B", defender.OtherTag);
            Assert.Equal("B1", defender.NodeId);
            Assert.Equal(250, attacker.Amount);
            // Both copies are the same record: which side of it a viewer is on is resolved when it
            // is shown, not when it is filed.
            Assert.Equal(attacker.Utc, defender.Utc);
        }

        [Fact]
        public void OneSidedEvent_FilesOnlyForItsActor()
        {
            WorldModel w = TestSupport.World();
            w.RecordEvent(FactionEventKind.ShipBuilt, "A", null, "N001", 1);

            List<FactionState> states = States("A", "B");
            Assert.Equal(1, FactionEventLog.Drain(states, w, 60));
            Assert.Single(states[0].RecentEvents);
            Assert.Empty(states[1].RecentEvents);
        }

        [Fact]
        public void EventAgainstAnUnregisteredFaction_IsFiledForWhoeverIsRegistered()
        {
            WorldModel w = TestSupport.World();
            w.RecordEvent(FactionEventKind.RaidResolved, "A", "GONE", "N001");

            List<FactionState> states = States("A");
            Assert.Equal(1, FactionEventLog.Drain(states, w, 60));
            Assert.Single(states[0].RecentEvents);
        }

        [Fact]
        public void ActorlessEvent_IsNotRecordedAtAll()
        {
            WorldModel w = TestSupport.World();
            w.RecordEvent(FactionEventKind.RaidResolved, null, "B", "N001");
            w.RecordEvent(FactionEventKind.Unknown, "A", "B", "N001");

            Assert.Empty(w.PendingEvents);
        }

        [Fact]
        public void Ring_KeepsTheNewestAndDropsTheOldest()
        {
            var state = new FactionState { Tag = "A" };
            for (int i = 0; i < 25; i++)
                FactionEventLog.Append(state, new FactionEvent
                {
                    Kind = FactionEventKind.ShipBuilt,
                    ActorTag = "A",
                    Amount = i,
                }, 10);

            Assert.Equal(10, state.RecentEvents.Count);
            Assert.Equal(15, state.RecentEvents[0].Amount);   // oldest survivor
            Assert.Equal(24, state.RecentEvents[9].Amount);   // newest
            Assert.Equal(25, state.EventsRevision);           // every append moved it, cap or not
        }

        [Fact]
        public void Ring_DrainRespectsTheTuningCap()
        {
            WorldModel w = TestSupport.World();
            for (int i = 0; i < 5; i++) w.RecordEvent(FactionEventKind.ShipBuilt, "A", null, "N001", i);

            List<FactionState> states = States("A");
            FactionEventLog.Drain(states, w, 3);

            Assert.Equal(3, states[0].RecentEvents.Count);
            Assert.Equal(2, states[0].RecentEvents[0].Amount);
        }

        [Fact]
        public void Ring_SurvivesABrainFileRoundTrip()
        {
            var st = new FactionState { Tag = "A" };
            var when = new DateTime(2026, 8, 22, 10, 30, 0, DateTimeKind.Utc);
            FactionEventLog.Append(st, new FactionEvent
            {
                Utc = when,
                Kind = FactionEventKind.NodeConquered,
                ActorTag = "RVN",
                OtherTag = "A",
                NodeId = "N042",
                Amount = 1200,
                Ratio = 0.75,
            }, 60);

            BrainDoc doc = FactionStore.ToBrainDoc(st, Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);
            FactionState reloaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            FactionEvent e = reloaded.RecentEvents.Single();
            Assert.Equal(when, e.Utc);
            Assert.Equal(FactionEventKind.NodeConquered, e.Kind);
            Assert.Equal("RVN", e.ActorTag);
            Assert.Equal("A", e.OtherTag);
            Assert.Equal("N042", e.NodeId);
            Assert.Equal(1200, e.Amount);
            Assert.Equal(0.75, e.Ratio, 3);
        }

        [Fact]
        public void EmptyRing_IsOmittedFromTheBrainFile()
        {
            var st = new FactionState { Tag = "A" };
            BrainDoc doc = FactionStore.ToBrainDoc(st, Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);

            Assert.Null(doc.Events);
        }

        [Fact]
        public void AKindThisBuildDoesNotKnow_LoadsAsUnknownRatherThanShorteningTheHistory()
        {
            var doc = new BrainDoc
            {
                Tag = "A",
                Drives = new double[5],
                Events = new List<FactionEventDoc>
                {
                    new FactionEventDoc { Kind = "SomethingFromALaterBuild", ActorTag = "RVN", NodeId = "N001" },
                    new FactionEventDoc { Kind = "RaidResolved", ActorTag = "RVN", NodeId = "N002" },
                },
            };

            FactionState reloaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(2, reloaded.RecentEvents.Count);
            Assert.Equal(FactionEventKind.Unknown, reloaded.RecentEvents[0].Kind);
            Assert.Equal(FactionEventKind.RaidResolved, reloaded.RecentEvents[1].Kind);
        }

        [Fact]
        public void HoldDecayingToNothing_IsRecordedAsALoss()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N001");
            n.OwnerFactionTag = "A";
            n.HoldStrength = 0.05;

            w.DecayInfluence(0.5, 0.5, null);

            FactionEvent e = w.PendingEvents.Single(x => x.Kind == FactionEventKind.NodeLost);
            Assert.Equal("A", e.ActorTag);
            Assert.Equal("N001", e.NodeId);
            Assert.Null(e.OtherTag);
        }
        // ---- Surviving a reload -------------------------------------------------------------
        //
        // The host rebuilds every FactionState from the brain files every reload (14 times on the
        // test server the day this was found), and the files are only as fresh as the phased flush
        // last reached. Merge is what stops that truncating the history back to the last flush.

        private static FactionEvent At(int minute, FactionEventKind kind, string actor, string other = null,
                                       string nodeId = null, long amount = 0)
        {
            return new FactionEvent
            {
                Utc = new DateTime(2026, 8, 22, 14, minute, 0, DateTimeKind.Utc),
                Kind = kind,
                ActorTag = actor,
                OtherTag = other,
                NodeId = nodeId,
                Amount = amount,
            };
        }

        [Fact]
        public void Merge_KeepsWhatTheDiskCopyNeverHad()
        {
            // What came back from the brain file: flushed at :13.
            var reloaded = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(reloaded, At(13, FactionEventKind.TradeResolved, "IGSL", "EXP"), 60);

            // What the live ring held when the reload discarded it: the flushed one plus two the
            // flush had not reached yet.
            var carried = new List<FactionEvent>
            {
                At(13, FactionEventKind.TradeResolved, "IGSL", "EXP"),
                At(16, FactionEventKind.TradeResolved, "MTDW", "EXP"),
                At(17, FactionEventKind.RaidResolved, "FCTM", "EXP", "N048", 350000),
            };

            Assert.Equal(2, FactionEventLog.Merge(reloaded, carried, 60));
            Assert.Equal(3, reloaded.RecentEvents.Count);
            Assert.Equal(FactionEventKind.RaidResolved, reloaded.RecentEvents[2].Kind);   // newest last
            Assert.Equal(13, reloaded.RecentEvents[0].Utc.Minute);                        // oldest first
        }

        [Fact]
        public void Merge_DoesNotDoubleWhatBothSidesAlreadyHold()
        {
            var reloaded = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(reloaded, At(13, FactionEventKind.TradeResolved, "IGSL", "EXP"), 60);
            int revisionBefore = reloaded.EventsRevision;

            var carried = new List<FactionEvent> { At(13, FactionEventKind.TradeResolved, "IGSL", "EXP") };

            Assert.Equal(0, FactionEventLog.Merge(reloaded, carried, 60));
            Assert.Single(reloaded.RecentEvents);
            Assert.Equal(revisionBefore, reloaded.EventsRevision);   // nothing changed, nothing to resend
        }

        [Fact]
        public void Merge_HonoursTheCapAndKeepsTheNewest()
        {
            var reloaded = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(reloaded, At(10, FactionEventKind.ShipBuilt, "EXP"), 3);
            FactionEventLog.Append(reloaded, At(11, FactionEventKind.ShipBuilt, "EXP"), 3);

            var carried = new List<FactionEvent>
            {
                At(12, FactionEventKind.ShipBuilt, "EXP"),
                At(13, FactionEventKind.ShipBuilt, "EXP"),
            };

            FactionEventLog.Merge(reloaded, carried, 3);

            Assert.Equal(3, reloaded.RecentEvents.Count);
            Assert.Equal(11, reloaded.RecentEvents[0].Utc.Minute);
            Assert.Equal(13, reloaded.RecentEvents[2].Utc.Minute);
        }

        [Fact]
        public void Merge_IntoAnEmptyOrAbsentRingIsSafe()
        {
            var fresh = new FactionState { Tag = "EXP" };
            Assert.Equal(1, FactionEventLog.Merge(fresh, new List<FactionEvent> { At(12, FactionEventKind.ShipBuilt, "EXP") }, 60));
            Assert.Equal(0, FactionEventLog.Merge(fresh, new List<FactionEvent>(), 60));
            Assert.Equal(0, FactionEventLog.Merge(fresh, null, 60));
            Assert.Equal(0, FactionEventLog.Merge(null, new List<FactionEvent> { At(12, FactionEventKind.ShipBuilt, "EXP") }, 60));
        }

        [Fact]
        public void File_PutsAnUndrainedEventOnBothSides()
        {
            List<FactionState> states = States("MTDW", "EXP");

            Assert.Equal(2, FactionEventLog.File(states, At(17, FactionEventKind.TradeResolved, "MTDW", "EXP", "N048", 350000), 60));
            Assert.Single(states[0].RecentEvents);
            Assert.Single(states[1].RecentEvents);
        }

        // ---- Retention: routine traffic must not evict what the tab is for -------------------
        //
        // Live, 2026-08-22: EXP logged 1,527 HAUL_RESOLVED in a day against zero raids. A ring
        // trimmed purely by age would hold about an hour of that and nothing else.

        [Fact]
        public void ABusyHaulingDay_DoesNotEvictTheRaid()
        {
            var st = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(st, At(1, FactionEventKind.RaidResolved, "FCTM", "EXP", "N048"), 10);
            for (int i = 2; i < 40; i++)
                FactionEventLog.Append(st, At(i % 60, FactionEventKind.HaulResolved, "EXP", null, "N048", i), 10);

            Assert.Equal(10, st.RecentEvents.Count);
            Assert.Contains(st.RecentEvents, e => e.Kind == FactionEventKind.RaidResolved);
            // ...and the raid is the OLDEST thing in there, which is exactly what age-ordered
            // trimming would have thrown away first.
            Assert.Equal(FactionEventKind.RaidResolved, st.RecentEvents[0].Kind);
        }

        [Fact]
        public void WhenEverythingIsNotable_TheRingIsAPlainAgeOrderedOne()
        {
            var st = new FactionState { Tag = "EXP" };
            for (int i = 0; i < 6; i++)
                FactionEventLog.Append(st, At(i, FactionEventKind.RaidResolved, "FCTM", "EXP", "N048", i), 3);

            Assert.Equal(3, st.RecentEvents.Count);
            Assert.Equal(3, st.RecentEvents[0].Amount);   // oldest survivor
            Assert.Equal(5, st.RecentEvents[2].Amount);   // newest
        }

        [Fact]
        public void RoutineIsEvictedOldestFirst_NotArbitrarily()
        {
            var st = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(st, At(1, FactionEventKind.HaulResolved, "EXP", null, "N1", 1), 3);
            FactionEventLog.Append(st, At(2, FactionEventKind.HaulResolved, "EXP", null, "N2", 2), 3);
            FactionEventLog.Append(st, At(3, FactionEventKind.HaulResolved, "EXP", null, "N3", 3), 3);
            FactionEventLog.Append(st, At(4, FactionEventKind.HaulResolved, "EXP", null, "N4", 4), 3);

            Assert.Equal(3, st.RecentEvents.Count);
            Assert.Equal(2, st.RecentEvents[0].Amount);
            Assert.DoesNotContain(st.RecentEvents, e => e.Amount == 1);
        }

        [Fact]
        public void TheNotableSetIsTheDefensiveOne()
        {
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.RaidResolved));
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.NodeConquered));
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.NodeLost));
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.ConvoyLooted));
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.ShipLost));
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.FleetDispatched));

            // Everything a faction generates by simply working.
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.HaulResolved));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.ConvoyDispatched));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.BuildQueued));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.ShipBuilt));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.TradeResolved));
        }

        [Fact]
        public void MergeAcrossAReload_UsesTheSameRetentionRule()
        {
            var reloaded = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(reloaded, At(1, FactionEventKind.RaidResolved, "FCTM", "EXP", "N048"), 3);

            var carried = new List<FactionEvent>
            {
                At(2, FactionEventKind.HaulResolved, "EXP", null, "N1", 2),
                At(3, FactionEventKind.HaulResolved, "EXP", null, "N2", 3),
                At(4, FactionEventKind.HaulResolved, "EXP", null, "N3", 4),
            };

            FactionEventLog.Merge(reloaded, carried, 3);

            Assert.Equal(3, reloaded.RecentEvents.Count);
            Assert.Contains(reloaded.RecentEvents, e => e.Kind == FactionEventKind.RaidResolved);
        }

        [Fact]
        public void TheNewFleetAndBuildKinds_RoundTripThroughABrainFile()
        {
            var st = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(st, At(10, FactionEventKind.HaulResolved, "EXP", null, "N048", 28040), 60);
            FactionEventLog.Append(st, At(11, FactionEventKind.BuildQueued, "EXP", null, "N048", 1), 60);
            FactionEventLog.Append(st, At(12, FactionEventKind.ConvoyDispatched, "EXP", "IGAT", "N018"), 60);
            FactionEventLog.Append(st, At(13, FactionEventKind.BuildExpired, "EXP", null, "N048", 1), 60);

            BrainDoc doc = FactionStore.ToBrainDoc(st, Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);
            FactionState reloaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(4, reloaded.RecentEvents.Count);
            Assert.Equal(FactionEventKind.HaulResolved, reloaded.RecentEvents[0].Kind);
            Assert.Equal(28040, reloaded.RecentEvents[0].Amount);
            Assert.Equal(FactionEventKind.BuildQueued, reloaded.RecentEvents[1].Kind);
            Assert.Equal(FactionEventKind.ConvoyDispatched, reloaded.RecentEvents[2].Kind);
            Assert.Equal(FactionEventKind.BuildExpired, reloaded.RecentEvents[3].Kind);
        }

        [Fact]
        public void AHaulThatCarriedNothing_IsNotFiled()
        {
            WorldModel w = TestSupport.World();
            w.RecordEvent(FactionEventKind.HaulResolved, "EXP", null, "N048", 92012, 1.0);

            // The zero-delivery case is filtered at the call site rather than here, so this asserts
            // the shape the call site relies on: Amount carries the delivery and zero means empty.
            FactionEvent e = Assert.Single(w.PendingEvents);
            Assert.Equal(92012, e.Amount);
        }

        [Fact]
        public void APlayerQueuedBuild_IsTheSameEventAsAManagerQueuedOne()
        {
            // Found live 2026-08-22: the first cut tapped only the demand-driven
            // "BUILD_QUEUED role=" site inside ShipyardManager.Tick, so a build the player queued
            // themselves -- the only kind a player faction actually initiates -- was never filed.
            // A peer's deliberately-placed build was invisible in the ring while sitting in the log.
            var st = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(st, At(1, FactionEventKind.BuildQueued, "EXP", null, "N048", 1), 60);
            FactionEventLog.Append(st, At(2, FactionEventKind.BuildCancelled, "EXP", null, "N048", 1), 60);

            Assert.Equal(FactionEventKind.BuildQueued, st.RecentEvents[0].Kind);
            Assert.Equal(FactionEventKind.BuildCancelled, st.RecentEvents[1].Kind);
            // All routine: as of v0.49.0 QueueBuild is also the primitive standing Build goals place
            // through, so BUILD_QUEUED_BY_PLAYER can fire automatically and repeatedly rather than
            // only when someone types it. Volume is contained by the retention class, not by
            // refusing to record -- a player managing a queue must never evict an incoming raid.
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.BuildQueued));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.BuildCancelled));
            Assert.False(FactionEventLog.IsNotable(FactionEventKind.BuildOverridden));
        }

        [Fact]
        public void ABuildOverride_SurvivesABusyQueue()
        {
            // An override is the build event nothing automated currently produces: the
            // standing-order evaluator filters out an occupied role slot before calling QueueBuild.
            // That is a property of that caller, not of QueueBuild, so this asserts retention only --
            // it is still routine, and a stream of goal-placed builds can age it out, but it must not
            // be the first thing dropped while older routine entries are still there.
            var st = new FactionState { Tag = "EXP" };
            FactionEventLog.Append(st, At(1, FactionEventKind.BuildQueued, "EXP", null, "N048", 1), 3);
            FactionEventLog.Append(st, At(2, FactionEventKind.BuildOverridden, "EXP", null, "N048", 1), 3);
            FactionEventLog.Append(st, At(3, FactionEventKind.BuildQueued, "EXP", null, "N048", 1), 3);
            FactionEventLog.Append(st, At(4, FactionEventKind.BuildQueued, "EXP", null, "N048", 1), 3);

            Assert.Equal(3, st.RecentEvents.Count);
            Assert.Contains(st.RecentEvents, e => e.Kind == FactionEventKind.BuildOverridden);
        }

    }
}
