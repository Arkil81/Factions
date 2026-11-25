using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class BlockRestoreTests
    {
        [Fact]
        public void TheDefaultNameIsTheRemovedIdAndIsCleared()
        {
            Assert.Null(BlockRestore.RestoredName("2454382515971754703", 2454382515971754703));
            Assert.Null(BlockRestore.RestoredName("", 7));
            Assert.Null(BlockRestore.RestoredName(null, 7));
        }

        [Fact]
        public void ANameSomeoneSetIsKept()
        {
            Assert.Equal("Bridge", BlockRestore.RestoredName("Bridge", 2454382515971754703));
            // Another block's id is not this block's default.
            Assert.Equal("2454382515971754704", BlockRestore.RestoredName("2454382515971754704", 2454382515971754703));
        }
    }
}
