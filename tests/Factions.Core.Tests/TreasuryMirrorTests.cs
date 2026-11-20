using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-181: the sim ledger keeps authority; the host-visible wallet is a copy that is no
    /// longer allowed to drift away from it.</summary>
    public class TreasuryMirrorTests
    {
        /// <summary>Records what the host would have written, and can be told to refuse a push the way
        /// a live gateway does when SE banking is unavailable.</summary>
        private sealed class RecordingMirror : ITreasuryMirror
        {
            public readonly List<KeyValuePair<string, long>> Pushes = new List<KeyValuePair<string, long>>();
            public readonly HashSet<string> Refuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool MirrorTreasury(string factionTag, long treasury)
            {
                if (Refuse.Contains(factionTag)) return false;
                Pushes.Add(new KeyValuePair<string, long>(factionTag, treasury));
                return true;
            }
        }

        private static WorldModel WorldWith(RecordingMirror mirror)
        {
            WorldModel w = TestSupport.World();
            w.TreasuryMirror = mirror;
            return w;
        }

        [Fact]
        public void FlushMirrorsTheAbsoluteTreasuryNotTheDelta()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);

            w.SetTreasury("F", 200_000);
            w.Spend("F", 50_000);
            w.FlushTreasuryMirror();

            // One push for the tag, carrying where the ledger ended up -- not 200,000 then -50,000.
            Assert.Single(mirror.Pushes);
            Assert.Equal("F", mirror.Pushes[0].Key);
            Assert.Equal(150_000, mirror.Pushes[0].Value);
        }

        [Fact]
        public void EveryTreasuryWriteIsMirrored_NotJustEarnAndSpend()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);

            // AddTreasury is what the central bank's grants and the income pass use; a mirror hung off
            // Earn/Spend alone would let those two drift the wallet back out of step.
            w.AddTreasury("F", 30_000);
            w.FlushTreasuryMirror();

            Assert.Single(mirror.Pushes);
            Assert.Equal(30_000, mirror.Pushes[0].Value);
        }

        [Fact]
        public void ManyMovesInOnePassCostOneWalletWrite()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);

            w.SetTreasury("F", 100_000);
            for (int i = 0; i < 20; i++) w.Spend("F", 1_000);
            w.FlushTreasuryMirror();

            Assert.Single(mirror.Pushes);
            Assert.Equal(80_000, mirror.Pushes[0].Value);
        }

        [Fact]
        public void FlushIsNotRepeatedForAnUnchangedTreasury()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);

            w.SetTreasury("F", 100_000);
            w.FlushTreasuryMirror();
            w.FlushTreasuryMirror();

            Assert.Single(mirror.Pushes);
            Assert.Equal(0, w.PendingTreasuryMirrorCount);
        }

        [Fact]
        public void ARefusedPushIsRetriedOnTheNextFlush()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);
            mirror.Refuse.Add("F");

            w.SetTreasury("F", 100_000);
            Assert.Equal(0, w.FlushTreasuryMirror());
            Assert.Empty(mirror.Pushes);
            Assert.Equal(1, w.PendingTreasuryMirrorCount);

            mirror.Refuse.Clear();
            Assert.Equal(1, w.FlushTreasuryMirror());
            Assert.Single(mirror.Pushes);
            Assert.Equal(100_000, mirror.Pushes[0].Value);
        }

        [Fact]
        public void ARefusedPushDoesNotRollBackTheLedger()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);
            mirror.Refuse.Add("F");

            w.SetTreasury("F", 100_000);
            w.Spend("F", 40_000);
            w.FlushTreasuryMirror();

            Assert.Equal(60_000, w.GetTreasury("F"));
        }

        [Fact]
        public void OneRefusalDoesNotBlockAnotherFactionsPush()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);
            mirror.Refuse.Add("A");

            w.SetTreasury("A", 10_000);
            w.SetTreasury("B", 20_000);
            w.FlushTreasuryMirror();

            Assert.Single(mirror.Pushes);
            Assert.Equal("B", mirror.Pushes[0].Key);
            Assert.Equal(1, w.PendingTreasuryMirrorCount);
        }

        [Fact]
        public void AuthorityStaysWithTheSimLedger()
        {
            var mirror = new RecordingMirror();
            WorldModel w = WorldWith(mirror);

            // GetTreasury must answer from the ledger whether or not a mirror is wired, and must not
            // consult the mirror at all -- FAC-181 links the ledgers without moving authority.
            w.SetTreasury("F", 207_300);
            w.FlushTreasuryMirror();

            Assert.Equal(207_300, w.GetTreasury("F"));
        }

        [Fact]
        public void AWorldWithNoMirrorWiredStillKeepsItsLedger()
        {
            WorldModel w = TestSupport.World();   // Factions.Sim's shape: no mirror, no host

            w.SetTreasury("F", 42_000);
            w.Spend("F", 2_000);
            Assert.Equal(1, w.FlushTreasuryMirror());   // NullTreasuryMirror accepts and drops
            Assert.Equal(40_000, w.GetTreasury("F"));
            Assert.Equal(0, w.PendingTreasuryMirrorCount);
        }
    }
}
