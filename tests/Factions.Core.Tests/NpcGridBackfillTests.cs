using Factions.Core;
using Xunit;
using V = Factions.Core.NpcGridNaming.BackfillVerdict;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-374. A grid that predates the durable marking carries no <c>factions:npc:</c> entity name,
    /// so nothing can identify it and the reaper never considers it. These cover the rule that
    /// decides which of those grids get the name written after the fact.
    /// </summary>
    public class NpcGridBackfillTests
    {
        private static V Decide(bool mine, bool foreign, bool bit, bool tagged)
        {
            return NpcGridNaming.ShouldBackfillName(mine, foreign, bit, tagged);
        }

        [Fact]
        public void TheDisplayTagAloneIsEnough()
        {
            // The case the whole ticket is about: SE's claim timer cleared the bit, the record died
            // with the restart, and the tag is the only mark left standing.
            Assert.Equal(V.Write, Decide(false, false, false, true));
        }

        [Fact]
        public void TheNpcBitAloneIsNotEnough()
        {
            // SE sets the bit on its own contract targets and container drops; promoting one to our
            // name would let the next reap pass close a target a player is searching for.
            Assert.Equal(V.NotOurs, Decide(false, false, true, false));
        }

        [Fact]
        public void BothSignalsStillWrite()
        {
            Assert.Equal(V.Write, Decide(false, false, true, true));
        }

        [Fact]
        public void AGridWithNeitherSignalIsLeftAlone()
        {
            // A player's own ship. This is the verdict that protects it.
            Assert.Equal(V.NotOurs, Decide(false, false, false, false));
        }

        [Fact]
        public void AGridAlreadyCarryingItsOwnNameIsNotRewritten()
        {
            Assert.Equal(V.AlreadyNamed, Decide(true, false, true, true));
            Assert.Equal(V.AlreadyNamed, Decide(true, false, false, false));
        }

        [Fact]
        public void APastedBlueprintOfAnNpcHullIsRefusedDespiteCarryingBothWeakSignals()
        {
            // A blueprint keeps the entity name of the grid it was saved from AND its display tag,
            // so a player's copy arrives looking exactly like ours except for the id. This is the
            // one case where the tag is actively misleading, which is why the foreign-name test
            // sits above the weak signals rather than below them.
            Assert.Equal(V.ForeignName, Decide(false, true, true, true));
            Assert.Equal(V.ForeignName, Decide(false, true, false, true));
            Assert.Equal(V.ForeignName, Decide(false, true, false, false));
        }

        [Fact]
        public void OnlyWriteEverWrites()
        {
            // Guards the caller's contract: it acts on Write and on nothing else, so any verdict
            // added later is inert until someone decides what it means.
            foreach (V v in new[] { V.AlreadyNamed, V.ForeignName, V.NotOurs })
                Assert.NotEqual(V.Write, v);
        }

        // ---- the name round-trip the backfill depends on ----------------------------------------

        [Fact]
        public void ABackfilledNameCarriesNoFleetKeyAndStillParses()
        {
            // An orphan has no fleet record, so it is named with an empty key. The parse has to
            // accept the colon-less form or the grid is unidentifiable on the very next boot.
            string name = NpcGridNaming.NameFor(8877L, "");
            Assert.Equal("factions:npc:8877", name);

            long eid;
            string fleetKey;
            Assert.True(NpcGridNaming.TryParseName(name, out eid, out fleetKey));
            Assert.Equal(8877L, eid);
            Assert.Equal("", fleetKey);
            Assert.True(NpcGridNaming.IsSimBornName(name, 8877L));
        }

        [Fact]
        public void ABackfilledNameIsRejectedForADifferentGrid()
        {
            string name = NpcGridNaming.NameFor(8877L, "");
            Assert.False(NpcGridNaming.IsSimBornName(name, 8878L));
        }

        [Fact]
        public void BackfillIsIdempotentThroughTheVerdict()
        {
            // Run the sweep twice: the second pass sees its own first-pass name and declines.
            string name = NpcGridNaming.NameFor(4242L, "");
            bool mine = NpcGridNaming.IsSimBornName(name, 4242L);
            Assert.Equal(V.AlreadyNamed, Decide(mine, false, true, true));
        }

        [Fact]
        public void TaggingTheDisplayNameIsIdempotent()
        {
            string once = NpcGridNaming.Apply("Kite Miner");
            Assert.Equal("[NPC] Kite Miner", once);
            Assert.Equal(once, NpcGridNaming.Apply(once));
        }

        [Fact]
        public void AnUntaggedNameIsNotReadAsTagged()
        {
            Assert.False(NpcGridNaming.IsTagged("PDC Type 1a GAT"));
            Assert.True(NpcGridNaming.IsTagged("[NPC] PDC Type 1a GAT"));
            Assert.False(NpcGridNaming.IsTagged(null));
        }
    }
}
