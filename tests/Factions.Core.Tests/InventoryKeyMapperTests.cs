using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class InventoryKeyMapperTests
    {
        [Fact]
        public void FromTypeIdAndSubtype_MapsConcreteInventoryPrefixes()
        {
            Assert.Equal("Comp:SteelPlate", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Component", "SteelPlate"));
            Assert.Equal("Comp:Thrust", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Component", "Thrust"));
            Assert.Equal("Ore:Iron", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Ore", "Iron"));
            Assert.Equal("Ingot:Nickel", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Ingot", "Nickel"));
        }

        [Fact]
        public void FromTypeIdAndSubtype_MapsAGasTanksStoredGasToTheGasPrefix()
        {
            // The key a gas tank's StoredGasId resolves to. PrefabCatalogueScanner keys per-gas tank
            // capacity by this, so a hull's hydrogen capacity lands under the same key the node
            // inventory and the price table already use for hydrogen.
            Assert.Equal("Gas:Hydrogen", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_GasProperties", "Hydrogen"));
            Assert.Equal("Gas:Oxygen", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_GasProperties", "Oxygen"));

            // A bottle is an assembler product, not gas, and keeps its own bare key.
            Assert.Equal("HydrogenBottle",
                InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_GasContainerObject", "HydrogenBottle"));
        }

        [Fact]
        public void FromTypeIdAndSubtype_KeepsDistinctComponentSubtypesDistinct()
        {
            var steel = InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Component", "SteelPlate");
            var thrust = InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_Component", "Thrust");

            Assert.NotEqual(steel, thrust);
            Assert.Equal("Comp:SteelPlate", steel);
            Assert.Equal("Comp:Thrust", thrust);
        }

        [Fact]
        public void FromTypeIdAndSubtype_PreservesSubtypeForUnknownTypes()
        {
            Assert.Equal("Medical", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_PhysicalGunObject", "Medical"));
        }
    }
}
