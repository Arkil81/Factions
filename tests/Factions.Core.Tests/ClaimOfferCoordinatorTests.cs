using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ClaimOfferCoordinatorTests
    {
        [Fact]
        public void FirstPresentFaction_GetsTheOffer()
        {
            var state = new ClaimOfferState();
            string offeree = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP", "MERC" });
            Assert.Equal("EXP", offeree);
        }

        [Fact]
        public void OutstandingOffer_StaysWithTheSameFaction_WhileStillPresent()
        {
            var state = new ClaimOfferState();
            ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP", "MERC" });
            string second = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "MERC", "EXP" });
            Assert.Equal("EXP", second);
        }

        [Fact]
        public void Decline_PassesTheOfferToTheNextPresentFaction()
        {
            var state = new ClaimOfferState();
            ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP", "MERC" });
            ClaimOfferCoordinator.Decline(state, "EXP");

            string next = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP", "MERC" });
            Assert.Equal("MERC", next);
        }

        [Fact]
        public void NobodyLeft_AfterEveryonePresentDeclines_ReturnsNull()
        {
            var state = new ClaimOfferState();
            ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP" });
            ClaimOfferCoordinator.Decline(state, "EXP");

            string result = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP" });
            Assert.Null(result);
        }

        [Fact]
        public void NodeGoingEmpty_ClearsDeclines_SoAReentrantFactionIsReofferable()
        {
            var state = new ClaimOfferState();
            ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP" });
            ClaimOfferCoordinator.Decline(state, "EXP");
            Assert.Null(ClaimOfferCoordinator.ResolveOfferee(state, new List<string>()));

            string reoffered = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP" });
            Assert.Equal("EXP", reoffered);
        }

        [Fact]
        public void LeavingWithoutAnswering_IsNotADecline_SoTheyStayFirstInLineOnReturn()
        {
            var state = new ClaimOfferState();
            ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "EXP", "MERC" });
            // EXP leaves without replying; MERC is still present.
            string whileGone = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "MERC" });
            Assert.Equal("MERC", whileGone);

            // EXP returns before MERC answers — MERC's offer is still outstanding, EXP waits.
            string afterReturn = ClaimOfferCoordinator.ResolveOfferee(state, new List<string> { "MERC", "EXP" });
            Assert.Equal("MERC", afterReturn);
        }
    }
}
