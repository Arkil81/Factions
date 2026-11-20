using System;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A client refused for a protocol mismatch reads the server's protocol from the
    /// refusal's header, because the version-checked reader cannot read past it.</summary>
    public class RefusalProtocolTests
    {
        /// <summary>A refusal as a server on <paramref name="serverVersion"/> would write it: the same
        /// bytes as this build's, with the header's version field rewritten.</summary>
        private static byte[] RefusalFrom(int serverVersion, string reason = "protocol 1 != 2")
        {
            byte[] data = FactionsWire.WriteUplinkRefused(reason);
            Buffer.BlockCopy(BitConverter.GetBytes(serverVersion), 0, data, 2, 4);
            return data;
        }

        /// <summary>The failure this exists for: the reason text is unreadable across the mismatch.</summary>
        [Fact]
        public void TheVersionCheckedReader_CannotReadARefusalFromAnotherProtocol()
        {
            string reason;
            Assert.False(FactionsWire.TryReadUplinkRefused(RefusalFrom(FactionsWire.ProtocolVersion + 1), out reason));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(-1)]
        public void ARefusalFromAnotherProtocol_NamesTheServersVersion(int offset)
        {
            int server;
            Assert.True(FactionsWire.TryReadRefusalProtocol(RefusalFrom(FactionsWire.ProtocolVersion + offset), out server));
            Assert.Equal(FactionsWire.ProtocolVersion + offset, server);
        }

        [Fact]
        public void ARefusalFromThisProtocol_IsNotAMismatch()
        {
            int server;
            Assert.False(FactionsWire.TryReadRefusalProtocol(FactionsWire.WriteUplinkRefused("authority:offline"), out server));
        }

        [Fact]
        public void AGrantFromAnotherProtocol_IsNotARefusal()
        {
            byte[] grant = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.None);
            Buffer.BlockCopy(BitConverter.GetBytes(FactionsWire.ProtocolVersion + 1), 0, grant, 2, 4);
            int server;
            Assert.False(FactionsWire.TryReadRefusalProtocol(grant, out server));
        }

        [Fact]
        public void AShortFrame_IsNotAMismatch()
        {
            int server;
            Assert.False(FactionsWire.TryReadRefusalProtocol(new byte[] { 1, 0, 2 }, out server));
            Assert.False(FactionsWire.TryReadRefusalProtocol(null, out server));
        }

        [Fact]
        public void TheNotice_NamesBothVersions_AndTellsABehindClientToRestart()
        {
            string notice = FactionsWire.ProtocolMismatchNotice(46, 47);
            Assert.Contains("protocol 46", notice, StringComparison.Ordinal);
            Assert.Contains("the server 47", notice, StringComparison.Ordinal);
            Assert.Contains("Restart Space Engineers", notice, StringComparison.Ordinal);
        }

        [Fact]
        public void TheNotice_TellsAClientAheadToWaitForTheServer()
        {
            Assert.Contains("server is behind", FactionsWire.ProtocolMismatchNotice(48, 47), StringComparison.Ordinal);
        }
    }
}
