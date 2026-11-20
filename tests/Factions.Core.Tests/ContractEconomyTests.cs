using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ContractEconomyTests
    {
        [Fact]
        public void ComputePremiumReward_ZeroUrgency_ReturnsActionCostRoundedToThousand()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, 0.0, 0.5);
            Assert.Equal(50000, reward);
        }

        [Fact]
        public void ComputePremiumReward_MaxUrgency_AppliesFullPremium()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, 1.0, 0.5);
            Assert.Equal(75000, reward);
        }

        [Fact]
        public void ComputePremiumReward_PartialUrgency_ScalesLinearly()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, 0.4, 0.5);
            // 50000 * (1 + 0.4*0.5) = 60000
            Assert.Equal(60000, reward);
        }

        [Fact]
        public void ComputePremiumReward_RoundsToNearestThousand()
        {
            // 50000 * (1 + 0.3*0.5) = 57500 -> rounds down to 57000 (integer division truncation)
            long reward = ContractEconomy.ComputePremiumReward(50000, 0.3, 0.5);
            Assert.Equal(57000, reward);
        }

        [Fact]
        public void ComputePremiumReward_ClampsUrgencyBelowZero()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, -1.0, 0.5);
            Assert.Equal(50000, reward);
        }

        [Fact]
        public void ComputePremiumReward_ClampsUrgencyAboveOne()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, 5.0, 0.5);
            Assert.Equal(75000, reward);
        }

        [Fact]
        public void ComputePremiumReward_ZeroPremiumMax_AlwaysReturnsActionCost()
        {
            long reward = ContractEconomy.ComputePremiumReward(50000, 1.0, 0.0);
            Assert.Equal(50000, reward);
        }
    }
    /// <summary>The FAC-156 affordability bound on item-value contract rewards.</summary>
    public sealed class ContractAffordabilityTests
    {
        [Fact]
        public void TheLiveCaseIsTrimmedToWhatTheFactionOwns()
        {
            // ISBT, 2026-08-29: 200 Explosives at basePrice 57,214 x scarcity 3 = 34,328,000 credits
            // against a 150,000 treasury. 229x everything the faction had.
            const long unitReward = 57214L * 3;
            Assert.Equal(34328400L, unitReward * 200);

            int affordable = ContractEconomy.AffordableContractQuantity(200, unitReward, 150000);

            Assert.Equal(0, affordable);   // cannot fund even one unit at 171,642 each
        }

        [Fact]
        public void APartialQuantityIsOfferedWhenOneUnitFits()
        {
            int affordable = ContractEconomy.AffordableContractQuantity(200, 1000, 15500);
            Assert.Equal(15, affordable);
        }

        [Fact]
        public void AnAffordableRequestIsUntouched()
        {
            Assert.Equal(200, ContractEconomy.AffordableContractQuantity(200, 1000, 500000));
        }

        [Fact]
        public void UncappedPassesEverythingThrough()
        {
            Assert.Equal(200, ContractEconomy.AffordableContractQuantity(200, 171642, long.MaxValue));
        }

        [Fact]
        public void ALargeQuantityAtAHighUnitRewardDoesNotOverflow()
        {
            // Divides rather than multiplies precisely so this cannot wrap: the product is ~2.1e14.
            int affordable = ContractEconomy.AffordableContractQuantity(int.MaxValue, 100000, 1000000);
            Assert.Equal(10, affordable);
        }

        [Fact]
        public void ZeroAndNegativeInputsAreRefusedRatherThanGuessed()
        {
            Assert.Equal(0, ContractEconomy.AffordableContractQuantity(0, 1000, 500000));
            Assert.Equal(0, ContractEconomy.AffordableContractQuantity(200, 1000, 0));
            Assert.Equal(0, ContractEconomy.AffordableContractQuantity(200, 1000, -5));
            Assert.Equal(200, ContractEconomy.AffordableContractQuantity(200, 0, 10));   // free: nothing to bound
        }
    }

}
