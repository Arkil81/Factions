using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The two SE-free halves of grid marking: the name a simulation-born grid carries, and the
    /// per-process record of which grids are ours.
    /// </summary>
    public sealed class SimGridMarkingTests
    {
        [Theory]
        [InlineData("Hull Breaker",            "[NPC] Hull Breaker")]
        [InlineData("  PDC Type 1a GAT ",      "[NPC] PDC Type 1a GAT")]
        [InlineData("[NPC] Hull Breaker",      "[NPC] Hull Breaker")]
        [InlineData("[NPC] Debris",            "[NPC] Debris")]
        [InlineData("",                        "[NPC]")]
        [InlineData(null,                      "[NPC]")]
        public void ApplyTagsOnceAndOnlyOnce(string name, string expected)
        {
            Assert.Equal(expected, NpcGridNaming.Apply(name));
            Assert.Equal(expected, NpcGridNaming.Apply(NpcGridNaming.Apply(name)));
        }

        [Fact]
        public void IsTaggedIsExactAndOrdinal()
        {
            Assert.True(NpcGridNaming.IsTagged("[NPC] Debris"));
            Assert.True(NpcGridNaming.IsTagged("  [NPC] x"));
            Assert.False(NpcGridNaming.IsTagged("[npc] x"));
            Assert.False(NpcGridNaming.IsTagged("NPC x"));
            Assert.False(NpcGridNaming.IsTagged("Small Grid 1234"));
            Assert.False(NpcGridNaming.IsTagged(null));
            Assert.True(NpcGridNaming.IsTagged(NpcGridNaming.DebrisName));
        }

        [Fact]
        public void MarkRecordsOnceAndRefusesBadIds()
        {
            var reg = new SimGridRegistry();
            DateTime now = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

            Assert.True(reg.Mark(10, "F1", "SPRT", "Raid", 0, now));
            Assert.False(reg.Mark(10, "F1", "SPRT", "Raid", 0, now));   // already marked
            Assert.False(reg.Mark(0,  "F1", "SPRT", "Raid", 0, now));
            Assert.False(reg.Mark(-1, "F1", "SPRT", "Raid", 0, now));
            Assert.Equal(1, reg.Count);
            Assert.True(reg.IsMarked(10));
            Assert.False(reg.IsMarked(0));

            SimGridRecord rec;
            Assert.True(reg.TryGet(10, out rec));
            Assert.Equal("F1", rec.FleetKey);
            Assert.Equal("SPRT", rec.FactionTag);
            Assert.Equal("Raid", rec.Kind);
            Assert.Equal(0L, rec.ParentEid);
            Assert.Equal(now, rec.MarkedUtc);
        }

        [Fact]
        public void ChildrenCarryTheirParentAndTheFleetCountFollows()
        {
            var reg = new SimGridRegistry();
            DateTime now = DateTime.UtcNow;
            reg.Mark(10, "F1", "SPRT", "Raid", 0, now);
            reg.Mark(11, "F1", "SPRT", "Raid", 10, now);    // a split piece
            reg.Mark(20, "F2", "EXP",  "Trade", 0, now);

            Assert.Equal(2, reg.CountForFleet("F1"));
            Assert.Equal(1, reg.CountForFleet("F2"));
            Assert.Equal(0, reg.CountForFleet("F9"));
            Assert.Equal(0, reg.CountForFleet(null));

            SimGridRecord child;
            Assert.True(reg.TryGet(11, out child));
            Assert.Equal(10L, child.ParentEid);

            var eids = new List<long> { 999 };
            reg.CopyEids(eids);
            Assert.Equal(3, eids.Count);
            Assert.DoesNotContain(999L, eids);

            Assert.True(reg.Forget(10));
            Assert.False(reg.Forget(10));
            Assert.Equal(1, reg.CountForFleet("F1"));   // the child stays marked after its parent closes
            reg.Clear();
            Assert.Equal(0, reg.Count);
        }

        /// <summary>The set a despawn closes beyond the hull's own mechanical group. A split
        /// fragment and released ordnance are attached to nothing, so the fleet key is the only
        /// thing left pointing at them; a hull of another fleet must never come back in that set.</summary>
        [Fact]
        public void FleetEidsAreCopiedByKeyAndNothingElse()
        {
            var reg = new SimGridRegistry();
            DateTime now = DateTime.UtcNow;
            reg.Mark(10, "F1#0", "SPRT", "Raid", 0, now);    // the lead
            reg.Mark(11, "F1#0", "SPRT", "Raid", 10, now);   // its turret head
            reg.Mark(12, "F1#0", "SPRT", "Raid", 10, now);   // a piece that split off it
            reg.Mark(20, "F1#1", "SPRT", "Raid", 0, now);    // an escort: its own spawn, its own key
            reg.Mark(30, "F2#0", "EXP",  "Trade", 0, now);

            var into = new List<long> { 999 };
            reg.CopyEidsForFleet("F1#0", into);
            Assert.Equal(3, into.Count);
            Assert.Contains(10L, into);
            Assert.Contains(11L, into);
            Assert.Contains(12L, into);
            Assert.DoesNotContain(20L, into);   // closing the lead must not take the escort with it
            Assert.DoesNotContain(30L, into);
            Assert.DoesNotContain(999L, into);

            reg.CopyEidsForFleet("f1#0", into);
            Assert.Empty(into);                 // ordinal, like CountForFleet

            reg.CopyEidsForFleet("", into);
            Assert.Empty(into);
            reg.CopyEidsForFleet(null, into);
            Assert.Empty(into);
        }

        /// <summary>A spawner that materialises nothing closes nothing, and says so with a count
        /// rather than a failure: every call returns 0 and no caller may read that as a fault.</summary>
        [Fact]
        public void AbstractSpawnerClosesNothingAndReportsZero()
        {
            IFleetSpawner spawner = new Factions.Sim.SimTransitSpawner();
            Assert.Equal(0, spawner.Despawn(123456789L));
            Assert.Equal(0, spawner.Despawn(0L));
        }

        /// <summary>The entity name round-trips the id and the fleet key, colons in the key
        /// included: the parse takes everything past the third colon, so a key is never quoted and
        /// never truncated.</summary>
        [Theory]
        [InlineData(4711L, "SPRT_638000000000000000_Raid_node-7_f12", "factions:npc:4711:SPRT_638000000000000000_Raid_node-7_f12")]
        [InlineData(4711L, "key#3",                                   "factions:npc:4711:key#3")]
        [InlineData(4711L, "a:b:c",                                   "factions:npc:4711:a:b:c")]
        [InlineData(4711L, "",                                        "factions:npc:4711")]
        [InlineData(4711L, null,                                      "factions:npc:4711")]
        public void TheEntityNameCarriesTheIdAndTheFleetKey(long eid, string fleetKey, string expected)
        {
            string name = NpcGridNaming.NameFor(eid, fleetKey);
            Assert.Equal(expected, name);

            long parsedEid;
            string parsedKey;
            Assert.True(NpcGridNaming.TryParseName(name, out parsedEid, out parsedKey));
            Assert.Equal(eid, parsedEid);
            Assert.Equal(fleetKey ?? "", parsedKey);
            Assert.True(NpcGridNaming.IsSimBornName(name));
            Assert.True(NpcGridNaming.IsSimBornName(name, eid));
        }

        /// <summary>Exact and ordinal. SE names every unnamed entity after its own id, a player can
        /// set an entity name through the mod API, and neither may read as ours.</summary>
        [Fact]
        public void IsSimBornNameAcceptsOnlyWhatWeWrite_Positive()
        {
            Assert.True(NpcGridNaming.IsSimBornName("factions:npc:1"));
            Assert.True(NpcGridNaming.IsSimBornName("factions:npc:144115188075855872"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("144115188075855872")]      // SE's default: the entity id, no prefix
        [InlineData("Factions:npc:4711")]       // case
        [InlineData("factions:NPC:4711")]
        [InlineData(" factions:npc:4711")]      // leading space
        [InlineData("x factions:npc:4711")]
        [InlineData("factions:npc:")]           // no id
        [InlineData("factions:npc::key")]
        [InlineData("factions:npc:abc")]
        [InlineData("factions:npc:-4711")]      // no real entity id is negative
        [InlineData("factions:npc:+4711")]
        [InlineData("factions:npc: 4711")]
        [InlineData("factions:npc:4711 ")]      // trailing space is part of the id segment
        [InlineData("[NPC] Debris")]            // the display tag is a different string entirely
        public void IsSimBornNameAcceptsOnlyWhatWeWrite_Negative(string name)
        {
            Assert.False(NpcGridNaming.IsSimBornName(name));
            long eid;
            string key;
            Assert.False(NpcGridNaming.TryParseName(name, out eid, out key));
            Assert.Equal(0L, eid);
        }

        /// <summary>A blueprint carries the entity name of the grid it was saved from, so a player's
        /// pasted copy of an NPC hull arrives with our name and a new entity id. The id-checked form
        /// is what everything acting on a grid uses, and it refuses the copy.</summary>
        [Fact]
        public void APastedCopyIsNotOurs()
        {
            string name = NpcGridNaming.NameFor(4711L, "SPRT_1_Raid_n_f");
            Assert.True(NpcGridNaming.IsSimBornName(name, 4711L));
            Assert.False(NpcGridNaming.IsSimBornName(name, 9999L));
        }

        /// <summary>A record rebuilt from a name says so, and dates nothing: its MarkedUtc is the
        /// boot of the process that rebuilt it, not when the grid was marked.</summary>
        [Fact]
        public void ARestoredRecordSaysSo()
        {
            var reg = new SimGridRegistry();
            DateTime boot = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(reg.Mark(10, "F1", "", "", 0, boot, true));
            Assert.True(reg.Mark(11, "F1", "SPRT", "Raid", 0, boot));

            SimGridRecord restored, marked;
            Assert.True(reg.TryGet(10, out restored));
            Assert.True(reg.TryGet(11, out marked));
            Assert.True(restored.Restored);
            Assert.False(marked.Restored);

            // The fleet key is what the name carried, so a restored grid is closed with its fleet.
            var mates = new List<long>();
            reg.CopyEidsForFleet("F1", mates);
            Assert.Equal(2, mates.Count);
        }

        [Fact]
        public void NullStringsAreStoredEmptyNotNull()
        {
            var reg = new SimGridRegistry();
            reg.Mark(5, null, null, null, 0, DateTime.UtcNow);
            SimGridRecord rec;
            Assert.True(reg.TryGet(5, out rec));
            Assert.Equal("", rec.FleetKey);
            Assert.Equal("", rec.FactionTag);
            Assert.Equal("", rec.Kind);
        }

        /// <summary>A hull adopted by a neighbour while it was still being assembled is reclaimed by
        /// its own spawn, and the neighbour's despawn no longer lists it.</summary>
        [Fact]
        public void ReleaseForeign_DropsANeighboursAdoption_SoTheSpawnCanMarkItsOwnHull()
        {
            var reg = new SimGridRegistry();
            DateTime now = DateTime.UtcNow;
            reg.Mark(10, "UNGD:BAD918", "UNGD", "Haul", 0, now);            // the neighbour
            reg.Mark(20, "UNGD:BAD918", "UNGD", "Haul", 10, now);           // the guard, adopted

            Assert.True(reg.ReleaseForeign(20, "UNGD:8E08CF"));
            Assert.True(reg.Mark(20, "UNGD:8E08CF", "UNGD", "Guard", 0, now));

            var mates = new List<long>();
            reg.CopyEidsForFleet("UNGD:BAD918", mates);
            Assert.Equal(new long[] { 10 }, mates.ToArray());
            reg.CopyEidsForFleet("UNGD:8E08CF", mates);
            Assert.Equal(new long[] { 20 }, mates.ToArray());
        }

        [Fact]
        public void ReleaseForeign_KeepsARecordAlreadyUnderTheSameKey_AndIgnoresUnmarkedIds()
        {
            var reg = new SimGridRegistry();
            reg.Mark(20, "UNGD:8E08CF", "UNGD", "Guard", 0, DateTime.UtcNow);
            Assert.False(reg.ReleaseForeign(20, "UNGD:8E08CF"));
            Assert.True(reg.IsMarked(20));
            Assert.False(reg.ReleaseForeign(99, "UNGD:8E08CF"));
            Assert.False(reg.ReleaseForeign(0, "UNGD:8E08CF"));
        }
    }
}
