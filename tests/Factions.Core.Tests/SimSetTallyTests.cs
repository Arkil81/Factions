using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SimSetTallyTests
    {
        [Fact]
        public void NpcProvenanceIsChargedToFactionsEvenWithAPlayerOwner()
        {
            Assert.Equal(SimSetTally.Factions, SimSetTally.PartyFor(true, "Arkil"));
        }

        [Fact]
        public void APlayersGridIsChargedToThatPlayer()
        {
            Assert.Equal("Arkil", SimSetTally.PartyFor(false, "Arkil"));
        }

        [Fact]
        public void NoProvenanceAndNoPlayerOwnerIsUnowned()
        {
            Assert.Equal(SimSetTally.Unowned, SimSetTally.PartyFor(false, null));
            Assert.Equal(SimSetTally.Unowned, SimSetTally.PartyFor(false, ""));
        }

        [Fact]
        public void OnlySimulatedGridsAreSummedPerParty()
        {
            var t = new SimSetTally();
            t.Add("Arkil", true, 100, 500, 40, true, true, 12.5);
            t.Add("Arkil", true, 50, 200, 10, false, false, 2.5);
            t.Add("Arkil", false, 9999, 9999, 9999, true, true, 9999);

            SimSetTally.Party p;
            Assert.True(t.TryGet("Arkil", out p));
            Assert.Equal(2, p.Grids);
            Assert.Equal(1, p.Awake);
            Assert.Equal(1, p.Moving);
            Assert.Equal(150, p.Blocks);
            Assert.Equal(700, p.Pcu);
            Assert.Equal(50, p.Shapes);
            Assert.Equal(15.0, p.MassTons, 3);
            Assert.Equal(2, t.SimulatedGrids);
            Assert.Equal(1, t.UnsimulatedGrids);
        }

        [Fact]
        public void AnEmptyPartyNameFallsBackToUnowned()
        {
            var t = new SimSetTally();
            t.Add(null, true, 1, 1, 1, false, false, 0);
            SimSetTally.Party p;
            Assert.True(t.TryGet(SimSetTally.Unowned, out p));
            Assert.Equal(1, p.Grids);
        }

        [Fact]
        public void ClearEmptiesEverything()
        {
            var t = new SimSetTally();
            t.Add("x", true, 1, 1, 1, true, true, 1);
            t.Add("x", false, 1, 1, 1, true, true, 1);
            t.Clear();
            Assert.Equal(0, t.PartyCount);
            Assert.Equal(0, t.SimulatedGrids);
            Assert.Equal(0, t.UnsimulatedGrids);
        }
    }
}
