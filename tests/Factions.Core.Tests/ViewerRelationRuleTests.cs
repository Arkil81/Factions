using Factions;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// SE's standings, from the faction table or from a factionless player's own reputation, to the
    /// colour a client draws.
    /// </summary>
    public class ViewerRelationRuleTests
    {
        [Fact]
        public void Friends_reads_as_friendly()
        {
            Assert.Equal(WireRelation.Friendly, ViewerRelationRule.FromStanding(ViewerRelationRule.SeFriends));
            Assert.Equal(WireRelation.Friendly, ViewerRelationRule.FromStanding(ViewerRelationRule.SeAllies));
        }

        [Fact]
        public void Enemies_reads_as_enemy_and_neutral_as_neutral()
        {
            Assert.Equal(WireRelation.Enemy, ViewerRelationRule.FromStanding(ViewerRelationRule.SeEnemies));
            Assert.Equal(WireRelation.Neutral, ViewerRelationRule.FromStanding(ViewerRelationRule.SeNeutral));
            Assert.Equal(WireRelation.Neutral, ViewerRelationRule.FromStanding(99));
        }

        [Fact]
        public void Wire_values_are_pinned()
        {
            // A client draws by byte value, so a renumbering would recolour every older client.
            Assert.Equal((byte)0, (byte)WireRelation.Unknown);
            Assert.Equal((byte)1, (byte)WireRelation.Own);
            Assert.Equal((byte)2, (byte)WireRelation.Neutral);
            Assert.Equal((byte)3, (byte)WireRelation.Enemy);
            Assert.Equal((byte)4, (byte)WireRelation.Friendly);
        }

        [Fact]
        public void Only_a_readable_table_without_the_row_reads_reputation()
        {
            Assert.True(ViewerRelationRule.ReadsReputation(false));
            Assert.False(ViewerRelationRule.ReadsReputation(true));
            Assert.False(ViewerRelationRule.ReadsReputation(null));
        }
    }
}
