using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The store-eligibility rule itself (FAC-152/FAC-158). Its whole point is the
    /// difference between "resolved to a non-trading type" and "not resolved at all", so most of
    /// these are about the second one.</summary>
    public class FactionStorePolicyTests
    {
        private sealed class StubCatalogue : IFactionTypeCatalogue
        {
            private static readonly IReadOnlyList<string> Empty = new List<string>().AsReadOnly();
            public readonly Dictionary<string, string> TypeByTag = new Dictionary<string, string>();

            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                return TypeByTag.TryGetValue(factionTag ?? "", out factionType);
            }
            public IReadOnlyList<string> OfferInvKeys(string factionType) { return Empty; }
            public IReadOnlyList<string> OrderInvKeys(string factionType) { return Empty; }
        }

        private static StubCatalogue Catalogue(params string[] tagTypePairs)
        {
            var c = new StubCatalogue();
            for (int i = 0; i + 1 < tagTypePairs.Length; i += 2)
                c.TypeByTag[tagTypePairs[i]] = tagTypePairs[i + 1];
            return c;
        }

        [Fact]
        public void TypeNone_HasNoStore()
        {
            Assert.False(FactionStorePolicy.HasPlayerStore(
                Catalogue("FCTM", "None"), new FactionTuning(), "FCTM"));
        }

        /// <summary>Operator direction 2026-08-29: "SPRT should be selling, FCTM should not." The
        /// ticket's own suggested fix excluded Pirate too; this pins the narrower rule so a later
        /// reader does not restore it from the prose.</summary>
        [Fact]
        public void TypePirate_KeepsItsStore()
        {
            Assert.True(FactionStorePolicy.HasPlayerStore(
                Catalogue("SPRT", "Pirate"), new FactionTuning(), "SPRT"));
        }

        [Theory]
        [InlineData("Trader")]
        [InlineData("Builder")]
        [InlineData("Miner")]
        [InlineData("Military")]
        public void TradingTypes_KeepTheirStore(string type)
        {
            Assert.True(FactionStorePolicy.HasPlayerStore(
                Catalogue("XXXX", type), new FactionTuning(), "XXXX"));
        }

        /// <summary>A world with no faction-type catalogue is supposed to use StationListings'
        /// inventory-threshold fallback. Reading "no catalogue" as "no store" would close every
        /// store in such a world, silently.</summary>
        [Fact]
        public void NoCatalogue_KeepsItsStore()
        {
            Assert.True(FactionStorePolicy.HasPlayerStore(null, new FactionTuning(), "FCTM"));
        }

        /// <summary>The same distinction one level down: a catalogue exists but has never heard of
        /// this tag — a faction that joined after session load, or a player faction the SE-side
        /// build deliberately leaves unresolved.</summary>
        [Fact]
        public void CatalogueDoesNotResolveTag_KeepsItsStore()
        {
            Assert.True(FactionStorePolicy.HasPlayerStore(
                Catalogue("IGSL", "Trader"), new FactionTuning(), "NEWF"));
        }

        [Fact]
        public void EmptyExclusionList_DisablesTheGate()
        {
            var tuning = new FactionTuning { StoreExcludedFactionTypes = new string[0] };
            Assert.True(FactionStorePolicy.HasPlayerStore(Catalogue("FCTM", "None"), tuning, "FCTM"));
        }

        [Fact]
        public void NullExclusionList_DisablesTheGate()
        {
            var tuning = new FactionTuning { StoreExcludedFactionTypes = null };
            Assert.True(FactionStorePolicy.HasPlayerStore(Catalogue("FCTM", "None"), tuning, "FCTM"));
        }

        /// <summary>The list names a type, so an operator can add one without a code change —
        /// which is the whole reason it is tuning rather than a constant.</summary>
        [Fact]
        public void ExclusionListIsHonouredForATypeTheDefaultKeeps()
        {
            var tuning = new FactionTuning { StoreExcludedFactionTypes = new[] { "None", "Pirate" } };
            Assert.False(FactionStorePolicy.HasPlayerStore(Catalogue("SPRT", "Pirate"), tuning, "SPRT"));
        }

        [Fact]
        public void MatchIsCaseInsensitive()
        {
            var tuning = new FactionTuning { StoreExcludedFactionTypes = new[] { "none" } };
            Assert.False(FactionStorePolicy.HasPlayerStore(Catalogue("FCTM", "None"), tuning, "FCTM"));
        }
    }
}
