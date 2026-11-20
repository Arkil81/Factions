using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-181: what the mirror decides to write into a faction's wallet, given what the
    /// ledger says and what the wallet currently holds.</summary>
    public class LedgerMirrorTests
    {
        /// <summary>Stands in for MyBankingSystem: remembers balances, refuses a subtraction that
        /// would cross zero exactly as <c>ChangeBalanceInternal</c> does, and can be switched off to
        /// stand for an economy that is not loaded.</summary>
        private sealed class FakeBank : IEconomyGateway
        {
            public readonly Dictionary<long, long> Accounts = new Dictionary<long, long>();
            public bool Available = true;
            public int Writes;

            public bool ChangeFactionBalance(long factionId, long amount)
            {
                long balance;
                if (!Available || amount == 0 || !Accounts.TryGetValue(factionId, out balance)) return false;
                if (balance + amount < 0) return false;   // SE refuses; it does not clamp
                Accounts[factionId] = balance + amount;
                Writes++;
                return true;
            }

            public bool TryGetFactionBalance(long factionId, out long balance)
            {
                balance = 0L;
                return Available && Accounts.TryGetValue(factionId, out balance);
            }
        }

        private const long IsbtId = 286600012956907027L;

        private static Func<string, FactionState> Roster(params FactionState[] states)
        {
            var byTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
            foreach (FactionState s in states) byTag[s.Tag] = s;
            return tag =>
            {
                FactionState found;
                return byTag.TryGetValue(tag, out found) ? found : null;
            };
        }

        private static FactionState Npc(string tag, long factionId)
        {
            return new FactionState { Tag = tag, FactionId = factionId };
        }

        [Fact]
        public void TheSeededBillionIsRebasedToTheLedgerInOnePush()
        {
            var bank = new FakeBank();
            bank.Accounts[IsbtId] = 1_000_000_000L;      // the measured live seed
            var log = new List<string>();
            var mirror = new LedgerMirror(bank, Roster(Npc("ISBT", IsbtId)), log.Add);

            Assert.True(mirror.MirrorTreasury("ISBT", 207_300));

            Assert.Equal(207_300, bank.Accounts[IsbtId]);
            Assert.Equal(1, bank.Writes);
            Assert.Contains(log, l => l.StartsWith("TREASURY_REBASED ISBT ", StringComparison.Ordinal));
        }

        [Fact]
        public void TheRebaseIsLoggedOnceNotOnEveryPush()
        {
            var bank = new FakeBank();
            bank.Accounts[IsbtId] = 1_000_000_000L;
            var log = new List<string>();
            var mirror = new LedgerMirror(bank, Roster(Npc("ISBT", IsbtId)), log.Add);

            mirror.MirrorTreasury("ISBT", 207_300);
            mirror.MirrorTreasury("ISBT", 208_000);
            mirror.MirrorTreasury("ISBT", 209_000);

            Assert.Equal(209_000, bank.Accounts[IsbtId]);
            Assert.Single(log);
        }

        [Fact]
        public void APushIsIdempotent()
        {
            var bank = new FakeBank();
            bank.Accounts[IsbtId] = 207_300L;
            var mirror = new LedgerMirror(bank, Roster(Npc("ISBT", IsbtId)), null);

            Assert.True(mirror.MirrorTreasury("ISBT", 207_300));
            Assert.True(mirror.MirrorTreasury("ISBT", 207_300));

            Assert.Equal(207_300, bank.Accounts[IsbtId]);
            Assert.Equal(0, bank.Writes);   // already equal: nothing written at all
        }

        [Fact]
        public void ADebtorIsFlooredAtZeroAndSaysSo()
        {
            // TFEG sat at -49,227 in the live ledger on 2026-08-30. SE refuses any change that would
            // take a wallet below zero, so the honest wallet value is 0 and the gap is announced.
            var bank = new FakeBank();
            bank.Accounts[7L] = 1_000_000_000L;
            var log = new List<string>();
            var mirror = new LedgerMirror(bank, Roster(Npc("TFEG", 7L)), log.Add);

            Assert.True(mirror.MirrorTreasury("TFEG", -49_227));

            Assert.Equal(0, bank.Accounts[7L]);
            Assert.Contains(log, l => l.StartsWith("TREASURY_MIRROR_FLOORED TFEG ", StringComparison.Ordinal));
        }

        [Fact]
        public void StayingInDebtDoesNotRepeatTheFlooredLine()
        {
            var bank = new FakeBank();
            bank.Accounts[7L] = 1_000L;
            var log = new List<string>();
            var mirror = new LedgerMirror(bank, Roster(Npc("TFEG", 7L)), log.Add);

            mirror.MirrorTreasury("TFEG", -1_000);
            mirror.MirrorTreasury("TFEG", -5_000);
            mirror.MirrorTreasury("TFEG", -9_000);

            Assert.Single(log, l => l.StartsWith("TREASURY_MIRROR_FLOORED", StringComparison.Ordinal));
        }

        [Fact]
        public void RecoveringFromDebtIsAnnouncedAndRestoresTheWallet()
        {
            var bank = new FakeBank();
            bank.Accounts[7L] = 1_000L;
            var log = new List<string>();
            var mirror = new LedgerMirror(bank, Roster(Npc("TFEG", 7L)), log.Add);

            mirror.MirrorTreasury("TFEG", -1_000);
            Assert.Equal(0, bank.Accounts[7L]);

            mirror.MirrorTreasury("TFEG", 12_500);

            Assert.Equal(12_500, bank.Accounts[7L]);
            Assert.Contains(log, l => l.StartsWith("TREASURY_MIRROR_UNFLOORED TFEG ", StringComparison.Ordinal));
        }

        [Fact]
        public void APlayerFactionsWalletFollowsTheLedgerLikeAnyOther()
        {
            // A faction's account is the faction's, not any member's personal wallet, and every way
            // credits reach it is captured into the ledger first -- so the ledger is what it shows.
            // Pushed DOWN as well as up: a treasury below the wallet is the direction that proves
            // this is a mirror rather than a top-up, and it is the one the retired player-faction
            // skip existed to prevent.
            var bank = new FakeBank();
            bank.Accounts[99L] = 4_250_000L;
            var player = new FactionState { Tag = "PLYR", FactionId = 99L, IsPlayerFaction = true };
            var mirror = new LedgerMirror(bank, Roster(player), null);

            Assert.True(mirror.MirrorTreasury("PLYR", 150_000));

            Assert.Equal(150_000L, bank.Accounts[99L]);
            Assert.Equal(1, bank.Writes);
        }

        [Fact]
        public void AnUnreadableWalletIsNotWrittenTo()
        {
            // TryGetFactionBalance failing means the base is unknown, not that it is zero. Writing a
            // delta against an assumed 0 would credit a whole treasury on top of the real balance.
            var bank = new FakeBank();
            bank.Accounts[IsbtId] = 1_000_000_000L;
            bank.Available = false;
            var mirror = new LedgerMirror(bank, Roster(Npc("ISBT", IsbtId)), null);

            Assert.False(mirror.MirrorTreasury("ISBT", 207_300));

            Assert.Equal(1_000_000_000L, bank.Accounts[IsbtId]);
            Assert.Equal(0, bank.Writes);
        }

        [Fact]
        public void AnEconomyThatComesUpLateIsReconciledThen()
        {
            var bank = new FakeBank();
            bank.Accounts[IsbtId] = 1_000_000_000L;
            bank.Available = false;
            var mirror = new LedgerMirror(bank, Roster(Npc("ISBT", IsbtId)), null);

            Assert.False(mirror.MirrorTreasury("ISBT", 207_300));
            bank.Available = true;
            Assert.True(mirror.MirrorTreasury("ISBT", 207_300));

            Assert.Equal(207_300, bank.Accounts[IsbtId]);
        }

        [Fact]
        public void ATagWithNoAccountBehindItIsRetriedNotDropped()
        {
            var bank = new FakeBank();
            var unregistered = Npc("NEW", 0L);
            var mirror = new LedgerMirror(bank, Roster(unregistered), null);

            Assert.False(mirror.MirrorTreasury("NEW", 50_000));

            // The SE roster sync fills FactionId in later; the retry is what lets the rebase land then.
            unregistered.FactionId = 55L;
            bank.Accounts[55L] = 1_000_000_000L;
            Assert.True(mirror.MirrorTreasury("NEW", 50_000));
            Assert.Equal(50_000, bank.Accounts[55L]);
        }

        [Fact]
        public void AnUnknownTagIsRetriedNotDropped()
        {
            var bank = new FakeBank();
            var mirror = new LedgerMirror(bank, Roster(), null);

            Assert.False(mirror.MirrorTreasury("GHST", 1_000));
            Assert.Equal(0, bank.Writes);
        }
    }
}
