using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ContractWaypointTests
    {
        [Fact]
        public void Name_LeadsWithThePlaceThenTheItem()
        {
            Assert.Equal("Agni Refinery: Iron Ingot", ContractWaypoint.Name("Agni Refinery", "Iron Ingot"));
        }

        [Fact]
        public void Name_TwoItemsAtOneStationGetDistinctNames()
        {
            Assert.NotEqual(ContractWaypoint.Name("Agni Refinery", "Iron Ingot"),
                            ContractWaypoint.Name("Agni Refinery", "Nickel Ingot"));
        }

        [Fact]
        public void Name_NeverExceedsTheSeCap_AndCutsTheItemBeforeThePlace()
        {
            string name = ContractWaypoint.Name("Kor Deep Orbital Yard", "Superconductor Component");
            Assert.True(name.Length <= ContractWaypoint.NameMaxLength);
            Assert.StartsWith("Kor Deep Orbital Yard: ", name);
        }

        [Fact]
        public void Name_AnOverlongPlaceIsCutToTheCap()
        {
            string place = new string('x', 40);
            Assert.Equal(ContractWaypoint.NameMaxLength, ContractWaypoint.Name(place, "Iron Ingot").Length);
        }

        [Fact]
        public void Name_NoPlaceLeavesTheVanillaNameInForce()
        {
            Assert.Null(ContractWaypoint.Name(null, "Iron Ingot"));
            Assert.Null(ContractWaypoint.Name("  ", "Iron Ingot"));
        }

        [Fact]
        public void Name_NoItemIsJustThePlace()
        {
            Assert.Equal("Agni Refinery", ContractWaypoint.Name("Agni Refinery", null));
        }

        [Fact]
        public void Description_NamesAmountItemPlaceAndFaction()
        {
            Assert.Equal("Deliver 400 x Iron Ingot to Agni Refinery (Ironclad Mining).",
                ContractWaypoint.Description("Agni Refinery", "Ironclad Mining", 400, "Iron Ingot"));
        }

        [Fact]
        public void Description_OmitsWhatIsUnknown()
        {
            Assert.Equal("Deliver Iron Ingot to Agni Refinery.",
                ContractWaypoint.Description("Agni Refinery", null, 0, "Iron Ingot"));
        }

        [Fact]
        public void Place_PrefersTheNodeNameThenTheGridThenTheTag()
        {
            Assert.Equal("Agni Refinery", ContractWaypoint.Place("Agni Refinery", "Economy Station 4", "IRN"));
            Assert.Equal("Economy Station 4", ContractWaypoint.Place(null, "Economy Station 4", "IRN"));
            Assert.Equal("IRN station", ContractWaypoint.Place("", null, "IRN"));
            Assert.Null(ContractWaypoint.Place(null, null, null));
        }
    }
}
