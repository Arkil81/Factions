using System.Collections.Generic;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    public class BlockPauseWireTests
    {
        [Fact]
        public void BlockPauseStatus_RoundTrips()
        {
            var sent = new List<WirePausedBlock>
            {
                new WirePausedBlock { EntityId = 12345678901234L, Class = 0, ByAdmin = false },
                new WirePausedBlock { EntityId = -7L, Class = 2, ByAdmin = true },
            };
            List<WirePausedBlock> read;
            Assert.True(FactionsWire.TryReadBlockPauseStatus(FactionsWire.WriteBlockPauseStatus(sent), out read));
            Assert.Equal(sent, read);
        }

        [Fact]
        public void AnEmptyListIsAValidClear()
        {
            List<WirePausedBlock> read;
            Assert.True(FactionsWire.TryReadBlockPauseStatus(FactionsWire.WriteBlockPauseStatus(new WirePausedBlock[0]), out read));
            Assert.Empty(read);
            Assert.True(FactionsWire.TryReadBlockPauseStatus(FactionsWire.WriteBlockPauseStatus(null), out read));
            Assert.Empty(read);
        }

        [Fact]
        public void AnotherKindsPayloadIsRejected()
        {
            List<WirePausedBlock> read;
            Assert.False(FactionsWire.TryReadBlockPauseStatus(FactionsWire.WriteSignatureStatus(1.5), out read));
            Assert.Null(read);
        }

        [Fact]
        public void ATruncatedPayloadIsRejectedNotMisread()
        {
            byte[] data = FactionsWire.WriteBlockPauseStatus(new[] { new WirePausedBlock { EntityId = 1, Class = 1 } });
            byte[] cut = new byte[data.Length - 3];
            System.Array.Copy(data, cut, cut.Length);
            List<WirePausedBlock> read;
            Assert.False(FactionsWire.TryReadBlockPauseStatus(cut, out read));
        }

        [Fact]
        public void TheFeatureBitIsItsOwn()
        {
            Assert.Equal(1L << 18, (long)WireFeatures.BlockPause);
            Assert.Equal(227, (int)WireMessageKind.BlockPauseStatus);
        }
    }
}
