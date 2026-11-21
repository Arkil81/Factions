using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The Search contract area: AWL's 750 m by default, and 0 hands the definition back to
    /// whatever SE and the mods loaded.</summary>
    public sealed class SearchContractPolicyTests
    {
        [Fact]
        public void TheDefaultIsAwls750Metres()
        {
            Assert.Equal(750.0, new FactionTuning().SearchContractAreaM);
            Assert.True(SearchContractPolicy.IsOverride(new FactionTuning().SearchContractAreaM));
        }

        [Fact]
        public void ZeroLeavesTheDefinitionAlone()
        {
            Assert.False(SearchContractPolicy.IsOverride(SearchContractPolicy.NoOverride));
        }

        [Fact]
        public void NonsenseLeavesTheDefinitionAlone()
        {
            Assert.False(SearchContractPolicy.IsOverride(-750.0));
            Assert.False(SearchContractPolicy.IsOverride(double.NaN));
            Assert.False(SearchContractPolicy.IsOverride(double.PositiveInfinity));
        }
    }
}
