using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The inventory arithmetic behind the Services Terminal settlements (FAC-280). The
    /// host side is three Harmony patches with no offline harness, so this is the part that can be
    /// pinned: the census scales down and never up, a repair debit floors at zero and reports the
    /// exact deficit, and an empty census moves nothing.</summary>
    public class ServiceSettlementTests
    {
        private static Dictionary<string, long> Census(params object[] pairs)
        {
            var d = new Dictionary<string, long>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                d[(string)pairs[i]] = System.Convert.ToInt64(pairs[i + 1]);
            return d;
        }

        [Fact]
        public void RecoveredUnits_FullFraction_ReturnsCensusUnchanged()
        {
            Assert.Equal(7L, ServiceSettlement.RecoveredUnits(7, 1.0));
            Assert.Equal(1L, ServiceSettlement.RecoveredUnits(1, 1.0));
            Assert.Equal(7L, ServiceSettlement.RecoveredUnits(7, 1.5));
        }

        [Fact]
        public void RecoveredUnits_RoundsDown_AndNeverYieldsFromUnderOneUnit()
        {
            Assert.Equal(3L, ServiceSettlement.RecoveredUnits(7, 0.5));
            Assert.Equal(0L, ServiceSettlement.RecoveredUnits(1, 0.5));
            Assert.Equal(0L, ServiceSettlement.RecoveredUnits(7, 0.0));
            Assert.Equal(0L, ServiceSettlement.RecoveredUnits(7, -1.0));
            Assert.Equal(0L, ServiceSettlement.RecoveredUnits(0, 1.0));
            Assert.Equal(0L, ServiceSettlement.RecoveredUnits(-5, 1.0));
        }

        [Fact]
        public void CreditSalvage_AddsToExistingStock_AndCountsKeysAndUnits()
        {
            var inv = Census("Comp:SteelPlate", 10);
            var census = Census("Comp:SteelPlate", 25, "Comp:Motor", 2, "Ore:Iron", 400);

            ServiceSettlement.SalvageCredit credit = ServiceSettlement.CreditSalvage(inv, census, 1.0);

            Assert.Equal(3, credit.Keys);
            Assert.Equal(427L, credit.Units);
            Assert.Equal(35L, inv["Comp:SteelPlate"]);
            Assert.Equal(2L, inv["Comp:Motor"]);
            Assert.Equal(400L, inv["Ore:Iron"]);
        }

        [Fact]
        public void CreditSalvage_HalfFraction_SkipsKeysThatRoundToZero_AndLeavesNoEmptyKey()
        {
            var inv = new Dictionary<string, long>();
            var census = Census("Comp:SteelPlate", 25, "Comp:Motor", 1);

            ServiceSettlement.SalvageCredit credit = ServiceSettlement.CreditSalvage(inv, census, 0.5);

            Assert.Equal(1, credit.Keys);
            Assert.Equal(12L, credit.Units);
            Assert.Equal(12L, inv["Comp:SteelPlate"]);
            Assert.False(inv.ContainsKey("Comp:Motor"));
        }

        [Fact]
        public void CreditSalvage_EmptyOrNullCensus_MovesNothing()
        {
            var inv = Census("Comp:SteelPlate", 10);

            ServiceSettlement.SalvageCredit a = ServiceSettlement.CreditSalvage(inv, new Dictionary<string, long>(), 1.0);
            ServiceSettlement.SalvageCredit b = ServiceSettlement.CreditSalvage(inv, null, 1.0);

            Assert.Equal(0, a.Keys); Assert.Equal(0L, a.Units);
            Assert.Equal(0, b.Keys); Assert.Equal(0L, b.Units);
            Assert.Equal(10L, inv["Comp:SteelPlate"]);
            Assert.Single(inv);
        }

        [Fact]
        public void DebitRepair_TakesWhatIsHeld_AndReportsTheExactShortfall()
        {
            var inv = Census("Comp:SteelPlate", 10, "Comp:Motor", 5);
            var census = Census("Comp:SteelPlate", 4, "Comp:Motor", 8, "Comp:Computer", 3);

            ServiceSettlement.RepairDebit debit = ServiceSettlement.DebitRepair(inv, census);

            Assert.Equal(9L, debit.Debited);      // 4 + 5 + 0
            Assert.Equal(6L, debit.Shortfall);    // 0 + 3 + 3
            Assert.Equal(6L, inv["Comp:SteelPlate"]);
            Assert.Equal(0L, inv["Comp:Motor"]);
            Assert.False(inv.ContainsKey("Comp:Computer"));
        }

        [Fact]
        public void DebitRepair_NeverGoesNegative()
        {
            var inv = Census("Comp:SteelPlate", 1);

            ServiceSettlement.DebitRepair(inv, Census("Comp:SteelPlate", 100));

            Assert.Equal(0L, inv["Comp:SteelPlate"]);
        }

        [Fact]
        public void DebitRepair_EmptyCensus_IsANoOp()
        {
            var inv = Census("Comp:SteelPlate", 10);

            ServiceSettlement.RepairDebit a = ServiceSettlement.DebitRepair(inv, new Dictionary<string, long>());
            ServiceSettlement.RepairDebit b = ServiceSettlement.DebitRepair(inv, null);

            Assert.Equal(0L, a.Debited); Assert.Equal(0L, a.Shortfall);
            Assert.Equal(0L, b.Debited); Assert.Equal(0L, b.Shortfall);
            Assert.Equal(10L, inv["Comp:SteelPlate"]);
        }
    }
}
