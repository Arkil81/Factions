using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class OfferAuthorityTests
    {
        [Fact]
        public void Leader_OfTheOfferedFaction_MayAnswer()
        {
            Assert.Equal(OfferReplyVerdict.Answer, OfferAuthority.Judge("EXP", "EXP", true));
        }

        [Fact]
        public void Member_OfTheOfferedFaction_IsRefused_NotIgnored()
        {
            // Refused, not NotForThem: the reply is consumed and the member is told a leader must
            // answer, rather than the "yes" passing into chat or accepting the node.
            Assert.Equal(OfferReplyVerdict.Refused, OfferAuthority.Judge("EXP", "EXP", false));
        }

        [Fact]
        public void Reply_FromAnotherFaction_IsNotForThem_EvenFromALeader()
        {
            Assert.Equal(OfferReplyVerdict.NotForThem, OfferAuthority.Judge("MERC", "EXP", true));
            Assert.Equal(OfferReplyVerdict.NotForThem, OfferAuthority.Judge("MERC", "EXP", false));
        }

        [Fact]
        public void NoStandingOffer_IsNotForThem()
        {
            Assert.Equal(OfferReplyVerdict.NotForThem, OfferAuthority.Judge("EXP", null, true));
            Assert.Equal(OfferReplyVerdict.NotForThem, OfferAuthority.Judge(null, "EXP", true));
        }

        [Fact]
        public void Recipient_IsTheFirstLeader_WhenOneIsOnline()
        {
            Assert.Equal(2, OfferAuthority.PickRecipient(new List<bool> { false, false, true, true }));
        }

        [Fact]
        public void Recipient_FallsBackToTheFirstMember_WhenNoLeaderIsOnline()
        {
            Assert.Equal(0, OfferAuthority.PickRecipient(new List<bool> { false, false }));
            Assert.Equal(-1, OfferAuthority.PickRecipient(new List<bool>()));
        }

        [Fact]
        public void ClaimOffer_ToAMember_SaysALeaderMustAnswer()
        {
            string toMember = OfferAuthority.ClaimOfferText("Iron site", "N-1", "EXP", false);
            string toLeader = OfferAuthority.ClaimOfferText("Iron site", "N-1", "EXP", true);
            Assert.Contains("A leader of [EXP]", toMember);
            Assert.DoesNotContain("A leader of", toLeader);
            Assert.Contains("Reply yes or no", toLeader);
        }

        [Fact]
        public void ConquerOffer_ToAMember_SaysALeaderMustAnswer_ButAFactionlessAttackerIsAskedDirectly()
        {
            Assert.Contains("A leader of [EXP]", OfferAuthority.ConquerOfferText("N-1", "EXP", false));
            Assert.Contains("Take it?", OfferAuthority.ConquerOfferText("N-1", "EXP", true));
            Assert.Contains("Take it?", OfferAuthority.ConquerOfferText("N-1", null, false));
        }

        [Fact]
        public void Refusal_NamesTheFaction()
        {
            Assert.Equal("Only a leader of [EXP] can accept or decline this offer.", OfferAuthority.RefusalText("EXP"));
        }
    }
}
