using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FleetDisplayNameTests
    {
        [Fact]
        public void AFleetHullIsNamedForItsOwnerKindAndId()
        {
            Assert.Equal("[NPC] EXP Haul 646CEA", NpcGridNaming.FleetDisplayName("EXP", "Haul", "646CEA"));
            Assert.True(NpcGridNaming.IsTagged(NpcGridNaming.FleetDisplayName("EXP", "Haul", "646CEA")));
            Assert.Null(NpcGridNaming.FleetDisplayName("EXP", "Haul", ""));
        }

        /// <summary>SE prefixes the owner's tag to a broadcast itself, so the text leaves it out and the
        /// HUD reads "EXP.[NPC] Haul 646CEA".</summary>
        [Fact]
        public void TheBroadcastIsTheNameWithoutTheOwnerTag()
        {
            Assert.Equal("[NPC] Haul 646CEA", NpcGridNaming.FleetBroadcastText("Haul", "646CEA"));
            Assert.Null(NpcGridNaming.FleetBroadcastText("Haul", null));
        }

        [Fact]
        public void TheIdIsTheLastSegmentOfTheSpawnKey()
        {
            Assert.Equal("646CEA", NpcGridNaming.FleetIdOfKey("EXP_638631234567890000_Haul_N600_646CEA"));
            Assert.Equal("646CEA", NpcGridNaming.FleetIdOfKey("EXP_638631234567890000_Raid_N600_646CEA#2"));
            Assert.Null(NpcGridNaming.FleetIdOfKey("EXP_638631234567890000_Haul_N600_"));
            Assert.Null(NpcGridNaming.FleetIdOfKey("nokey"));
            Assert.Null(NpcGridNaming.FleetIdOfKey(null));
        }
    }
}
