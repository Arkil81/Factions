using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Kits: one claim per cooldown per player, a rank gate that hides what it refuses, a
    /// catalogue that drops what it cannot hand out, and a ledger that survives its own file.</summary>
    public class KitTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        private const ulong Alice = 76561198000000001UL;
        private const ulong Bob = 76561198000000002UL;

        private static KitCatalogue Catalogue() { return KitCatalogue.Defaults(); }

        private static NexusKitAsk Claim(ulong steam, string kit, int rank = 0)
        {
            return new NexusKitAsk { Id = 1, Op = KitOp.Claim, Steam = steam, Kit = kit, Rank = rank };
        }

        [Fact]
        public void ASecondClaimInsideTheCooldownIsRefusedWithTheWait()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            bool changed;

            NexusKitAnswer first = KitDesk.Answer(Claim(Alice, "starter"), cat, cd, T0, out changed);
            Assert.True(first.Ok);
            Assert.True(changed);
            Assert.Equal(T0, first.StampedUtc);

            NexusKitAnswer second = KitDesk.Answer(Claim(Alice, "STARTER"), cat, cd, T0.AddMinutes(30), out changed);
            Assert.False(second.Ok);
            Assert.False(changed);
            Assert.Equal("You can claim the starter kit again in 1 h 30 min.", second.Message);

            // Another player's wait is their own.
            Assert.True(KitDesk.Answer(Claim(Bob, "starter"), cat, cd, T0.AddMinutes(30), out changed).Ok);

            // At exactly the cooldown it is ready again.
            Assert.True(KitDesk.Answer(Claim(Alice, "starter"), cat, cd, T0.AddMinutes(120), out changed).Ok);
        }

        [Fact]
        public void AKitAboveTheCallersRankReadsAsUnknown()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            bool changed;

            NexusKitAnswer player = KitDesk.Answer(Claim(Alice, "sc", rank: 0), cat, cd, T0, out changed);
            NexusKitAnswer missing = KitDesk.Answer(Claim(Alice, "nosuch", rank: 0), cat, cd, T0, out changed);
            Assert.False(player.Ok);
            Assert.Equal(missing.Message.Replace("nosuch", "sc"), player.Message);
            Assert.Equal(0, cd.PlayerCount);

            KitRank.TryParse("Scripter", out int scripter);
            Assert.True(KitDesk.Answer(Claim(Alice, "sc", scripter), cat, cd, T0, out changed).Ok);
        }

        [Fact]
        public void TheListingHidesKitsAboveTheRankAndShowsTheWait()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            TimeSpan unused;
            cd.TryStamp(Alice, cat.Find("starter"), T0, out unused);

            string listing = KitDesk.Listing(cat, 0, k => cd.Remaining(Alice, k, T0.AddMinutes(55)));
            Assert.Contains("starter  in 1 h 05 min, every 2 h - A pistol", listing);
            Assert.DoesNotContain(" sc ", listing);

            string staff = KitDesk.Listing(cat, 1, k => cd.Remaining(Alice, k, T0.AddMinutes(55)));
            Assert.Contains("sc       ready, every 4 h", staff);
        }

        [Fact]
        public void TheListAskReturnsOnlyTheKitsStillWaiting()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            bool changed;
            KitDesk.Answer(Claim(Alice, "starter"), cat, cd, T0, out changed);

            NexusKitAnswer list = KitDesk.Answer(new NexusKitAsk { Op = KitOp.List, Steam = Alice }, cat, cd,
                T0.AddMinutes(1), out changed);
            Assert.True(list.Ok);
            Assert.False(changed);
            Assert.Equal(119 * 60, list.WaitSeconds["starter"]);
            Assert.False(list.WaitSeconds.ContainsKey("sc"));
        }

        [Fact]
        public void AReleaseUndoesOnlyTheStampItNames()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            bool changed;
            NexusKitAnswer claimed = KitDesk.Answer(Claim(Alice, "starter"), cat, cd, T0, out changed);

            var stale = new NexusKitAsk { Op = KitOp.Release, Steam = Alice, Kit = "starter", StampedUtc = T0.AddSeconds(-1) };
            Assert.False(KitDesk.Answer(stale, cat, cd, T0, out changed).Ok);
            Assert.False(changed);
            Assert.True(cd.Remaining(Alice, cat.Find("starter"), T0) > TimeSpan.Zero);

            var release = new NexusKitAsk { Op = KitOp.Release, Steam = Alice, Kit = "starter", StampedUtc = claimed.StampedUtc };
            Assert.True(KitDesk.Answer(release, cat, cd, T0, out changed).Ok);
            Assert.True(changed);
            Assert.Equal(TimeSpan.Zero, cd.Remaining(Alice, cat.Find("starter"), T0));
        }

        [Fact]
        public void AKitWithoutACooldownIsNeverRecorded()
        {
            var cat = new KitCatalogue();
            cat.Kits.Add(new KitDefinition { Name = "free", Items = new List<KitItem> { new KitItem { TypeId = "Ore", SubtypeId = "Ice", Amount = 1 } } });
            var cd = new KitCooldowns();
            bool changed;
            Assert.True(KitDesk.Answer(Claim(Alice, "free"), cat, cd, T0, out changed).Ok);
            Assert.False(changed);
            Assert.True(KitDesk.Answer(Claim(Alice, "free"), cat, cd, T0, out changed).Ok);
            Assert.Equal(0, cd.PlayerCount);
        }

        [Fact]
        public void TheLedgerRoundTripsAndPrunesWhatHasRunOut()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            TimeSpan unused;
            cd.TryStamp(Alice, cat.Find("starter"), T0, out unused);
            cd.TryStamp(Bob, cat.Find("sc"), T0.AddMinutes(100), out unused);

            KitCooldowns read = KitCooldowns.FromJson(cd.ToJson());
            Assert.Equal(TimeSpan.FromMinutes(60), read.Remaining(Alice, cat.Find("starter"), T0.AddMinutes(60)));

            // At T0+130 Alice's 2 h wait is over and Bob's 4 h wait is not.
            Assert.Equal(1, read.Prune(cat, T0.AddMinutes(130)));
            Assert.Equal(1, read.PlayerCount);
            Assert.True(read.Remaining(Bob, cat.Find("sc"), T0.AddMinutes(130)) > TimeSpan.Zero);

            // A kit taken out of the catalogue takes its waits with it.
            cat.Kits.RemoveAll(k => k.Name == "sc");
            Assert.Equal(1, read.Prune(cat, T0.AddMinutes(130)));
            Assert.Equal(0, read.PlayerCount);
        }

        [Fact]
        public void ACooldownShortenedInTheCatalogueShortensARunningWait()
        {
            KitCatalogue cat = Catalogue();
            var cd = new KitCooldowns();
            TimeSpan unused;
            cd.TryStamp(Alice, cat.Find("starter"), T0, out unused);
            cat.Find("starter").CooldownMinutes = 30;
            Assert.Equal(TimeSpan.Zero, cd.Remaining(Alice, cat.Find("starter"), T0.AddMinutes(30)));
        }

        [Fact]
        public void TheCatalogueDropsKitsItCannotHandOut()
        {
            const string json = @"{ ""Kits"": [
                { ""Name"": ""ok"", ""CooldownMinutes"": 5, ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 10 } ] },
                { ""Name"": ""OK"", ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 10 } ] },
                { ""Name"": ""rank"", ""RequiredRank"": ""Captain"", ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 1 } ] },
                { ""Name"": ""empty"", ""Items"": [] },
                { ""Name"": ""zero"", ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 0 } ] },
                { ""Name"": ""two words"", ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 1 } ] },
                { ""Name"": ""late"", ""CooldownMinutes"": -1, ""Items"": [ { ""TypeId"": ""Ore"", ""SubtypeId"": ""Ice"", ""Amount"": 1 } ] }
            ] }";
            var problems = new List<string>();
            KitCatalogue cat = KitCatalogue.Parse(json, problems);
            Assert.Single(cat.Kits);
            Assert.Equal("ok", cat.Kits[0].Name);
            Assert.Equal(6, problems.Count);
        }

        [Fact]
        public void TheDefaultsSurviveTheirOwnFile()
        {
            string json = KitCatalogue.Defaults().ToJson();
            Assert.DoesNotContain("RankOrdinal", json);
            var problems = new List<string>();
            KitCatalogue read = KitCatalogue.Parse(json, problems);
            Assert.Empty(problems);
            Assert.Equal(2, read.Kits.Count);
            Assert.Equal(8, read.Find("starter").Items.Count);
            Assert.Equal(10, read.Find("starter").Items.Find(i => i.SubtypeId == "FullAutoPistolMagazine").Amount);
            Assert.Equal(1, read.Find("sc").RankOrdinal);
        }

        [Fact]
        public void AsksAndAnswersCrossTheWire()
        {
            var ask = new NexusKitAsk { Id = 7, Op = KitOp.Release, Steam = Alice, Kit = "starter", Rank = 2, StampedUtc = T0 };
            NexusKitAsk readAsk;
            Assert.True(NexusKitAsk.TryDecode(ask.Encode(), out readAsk));
            Assert.Equal(KitOp.Release, readAsk.Op);
            Assert.Equal(Alice, readAsk.Steam);
            Assert.Equal(T0, readAsk.StampedUtc);
            Assert.Equal(2, readAsk.Rank);

            var answer = new NexusKitAnswer { Id = 7, Op = KitOp.List, Ok = true,
                WaitSeconds = new Dictionary<string, long> { { "starter", 60 } } };
            NexusKitAnswer readAnswer;
            Assert.True(NexusKitAnswer.TryDecode(answer.Encode(), out readAnswer));
            Assert.Equal(60, readAnswer.WaitSeconds["starter"]);
        }

        [Theory]
        [InlineData(0.2, "1 min")]
        [InlineData(12, "12 min")]
        [InlineData(60, "1 h")]
        [InlineData(65, "1 h 05 min")]
        [InlineData(119.01, "2 h")]
        public void WaitsRoundUpToTheMinute(double minutes, string expected)
        {
            Assert.Equal(expected, KitDesk.FormatWait(TimeSpan.FromMinutes(minutes)));
        }
    }
}
