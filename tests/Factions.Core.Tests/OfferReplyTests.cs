using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class OfferReplyTests
    {
        private static OpenOffer Claim(string node, bool may = true) { return new OpenOffer { NodeId = node, MayAnswer = may }; }
        private static OpenOffer Conquer(string node, bool may = true) { return new OpenOffer { NodeId = node, Conquer = true, MayAnswer = may }; }

        [Theory]
        [InlineData("yes", true, null)]
        [InlineData("Y!", true, null)]
        [InlineData("/r no", false, null)]
        [InlineData("yes N123", true, "N123")]
        [InlineData("no n045.", false, "n045")]
        [InlineData("/reply yes (N007)", true, "N007")]
        public void Parses_AReply_AndTheNodeItNames(string text, bool yes, string node)
        {
            bool isYes;
            string nodeId;
            Assert.True(OfferReply.TryParse(text, out isYes, out nodeId));
            Assert.Equal(yes, isYes);
            Assert.Equal(node, nodeId);
        }

        [Theory]
        [InlineData("yes please")]
        [InlineData("no thanks")]
        [InlineData("yes N123 now")]
        [InlineData("yeah")]
        [InlineData("N123")]
        [InlineData("")]
        public void OrdinaryChat_IsNotAReply(string text)
        {
            bool isYes;
            string nodeId;
            Assert.False(OfferReply.TryParse(text, out isYes, out nodeId));
            Assert.Null(nodeId);
        }

        [Fact]
        public void AClaimAndAConquerOpen_AndNoneNamed_IsAskedNotAnswered()
        {
            // The defect: one "yes" took both. Neither may be answered from it.
            int index;
            var open = new List<OpenOffer> { Claim("N001"), Conquer("N002") };
            Assert.Equal(OfferReplyChoice.Ambiguous, OfferReply.Choose(open, null, out index));
            Assert.Equal(-1, index);
        }

        [Fact]
        public void TwoClaimsOpen_AndNoneNamed_IsAsked()
        {
            int index;
            Assert.Equal(OfferReplyChoice.Ambiguous,
                OfferReply.Choose(new List<OpenOffer> { Claim("N001"), Claim("N003") }, null, out index));
        }

        [Fact]
        public void TheNamedNode_IsTheOneAnswered()
        {
            int index;
            var open = new List<OpenOffer> { Claim("N001"), Conquer("N002") };
            Assert.Equal(OfferReplyChoice.One, OfferReply.Choose(open, "n002", out index));
            Assert.Equal(1, index);
        }

        [Fact]
        public void ANodeWithNoOffer_IsNoSuchNode_WhenOffersAreOpen()
        {
            int index;
            Assert.Equal(OfferReplyChoice.NoSuchNode,
                OfferReply.Choose(new List<OpenOffer> { Claim("N001") }, "N999", out index));
        }

        [Fact]
        public void NothingOpen_IsNone_SoTheLineStaysChat()
        {
            int index;
            Assert.Equal(OfferReplyChoice.None, OfferReply.Choose(new List<OpenOffer>(), null, out index));
            Assert.Equal(OfferReplyChoice.None, OfferReply.Choose(new List<OpenOffer>(), "N001", out index));
        }

        [Fact]
        public void OneOpen_IsAnsweredWithoutNamingIt()
        {
            int index;
            Assert.Equal(OfferReplyChoice.One, OfferReply.Choose(new List<OpenOffer> { Conquer("N002") }, null, out index));
            Assert.Equal(0, index);
        }

        [Fact]
        public void OnlyTheOffersTheyMayAnswer_CountTowardsAsking()
        {
            int index;
            var open = new List<OpenOffer> { Claim("N001", false), Conquer("N002", true) };
            Assert.Equal(OfferReplyChoice.One, OfferReply.Choose(open, null, out index));
            Assert.Equal(1, index);
        }

        [Fact]
        public void NoneTheyMayAnswer_PicksOne_SoTheyAreRefused()
        {
            int index;
            var open = new List<OpenOffer> { Claim("N001", false), Conquer("N002", false) };
            Assert.Equal(OfferReplyChoice.One, OfferReply.Choose(open, null, out index));
            Assert.Equal(0, index);
        }

        [Fact]
        public void TheQuestion_NamesEachNodeAndHowToAnswerOne()
        {
            string text = OfferReply.WhichText(new List<OpenOffer> { Claim("N001"), Conquer("N002") });
            Assert.Equal("You have 2 offers open: N001 (claim it), N002 (take it from its owner). "
                       + "Reply \"yes N001\" or \"no N001\" to answer one of them.", text);
        }
    }
}
