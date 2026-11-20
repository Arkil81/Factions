using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The conquest follow-through on a colonisation beachhead: the outpost is a way in, not the goal.
    /// FAC-106.
    /// </summary>
    public class SeizeObjectiveTests
    {
        private const string AgniId = "72111577199174151";
        private const string KorId  = "88811577199174999";

        private static ColonisationIntent Beachhead()
        {
            return new ColonisationIntent
            {
                SubTypeId = "Ore:Platinum",
                IsBeachhead = true,
                DivertedFrom = new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Agni" },
                Target = new ColonisationCandidate
                {
                    Region = new PlacementRegion { Kind = PlacementRegionKind.PlanetOrbit, Key = "Agni" }
                }
            };
        }

        private static SpatialNode Node(string id, string owner, NodeZone zone, string planetId)
        {
            return new SpatialNode { Id = id, OwnerFactionTag = owner, Zone = zone, PlanetId = planetId };
        }

        private static FactionState WithObjective(CampaignObjective o)
        {
            var s = new FactionState { Tag = "MTDW" };
            var focus = new CampaignFocus { Id = "f1", CenterNodeId = "N1" };
            if (o != null) focus.Objectives.Add(o);
            s.CampaignFoci.Add(focus);
            return s;
        }

        // ---- the objective survives the diversion ----------------------------------------------

        [Fact]
        public void ABeachheadBecomesAnObjectiveNamingWhatItCouldNotReach()
        {
            CampaignObjective o = SeizeObjectives.FromBeachhead(Beachhead(), AgniId);

            Assert.NotNull(o);
            Assert.Equal(CampaignObjectiveKind.SeizeRegion, o.Kind);
            Assert.Equal(PlacementRegionKind.PlanetSurface, o.TargetRegionKind);   // the surface, not the orbit
            Assert.Equal("Agni", o.TargetRegionKey);
            Assert.Equal(AgniId, o.TargetPlanetId);
        }

        [Fact]
        public void AnOrdinaryColonisationCarriesNoObjective()
        {
            // Arriving somewhere with room is the end of the story; nothing to fight for.
            var plain = new ColonisationIntent { SubTypeId = "Ore:Platinum", IsBeachhead = false };
            Assert.Null(SeizeObjectives.FromBeachhead(plain, AgniId));
        }

        // ---- what advances it -------------------------------------------------------------------

        [Fact]
        public void AHostileNodeOnTheTargetSurfaceAdvancesTheObjective()
        {
            CampaignObjective o = SeizeObjectives.FromBeachhead(Beachhead(), AgniId);
            Assert.True(SeizeObjectives.Advances(o, Node("N1", "SPRT", NodeZone.Surface, AgniId), "MTDW"));
        }

        [Fact]
        public void OwnNodesAndOtherBodiesAndTheWrongZoneDoNot()
        {
            CampaignObjective o = SeizeObjectives.FromBeachhead(Beachhead(), AgniId);

            Assert.False(SeizeObjectives.Advances(o, Node("N2", "MTDW", NodeZone.Surface, AgniId), "MTDW"));
            Assert.False(SeizeObjectives.Advances(o, Node("N3", "SPRT", NodeZone.Surface, KorId), "MTDW"));
            Assert.False(SeizeObjectives.Advances(o, Node("N4", "SPRT", NodeZone.Orbit, AgniId), "MTDW"));
        }

        [Fact]
        public void AFeatureRegionDeclinesRatherThanGuessing()
        {
            // Nothing on a node records which authored feature contains it. Returning false is the
            // honest answer; matching on the body underneath a ring would seize the wrong thing.
            var intent = Beachhead();
            intent.DivertedFrom = new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = "Cauldron Ring" };
            CampaignObjective o = SeizeObjectives.FromBeachhead(intent, AgniId);

            Assert.False(SeizeObjectives.Advances(o, Node("N5", "SPRT", NodeZone.Orbit, AgniId), "MTDW"));
            Assert.False(SeizeObjectives.Advances(o, Node("N6", "SPRT", NodeZone.Surface, AgniId), "MTDW"));
        }

        [Fact]
        public void TheDefensiveObjectiveKindIsUnaffected()
        {
            var defend = new CampaignObjective
            {
                Kind = CampaignObjectiveKind.DefendRegion, TargetNodeId = "N1"
            };
            Assert.False(SeizeObjectives.Advances(defend, Node("N1", "SPRT", NodeZone.Surface, AgniId), "MTDW"));
            Assert.Equal(0, (int)CampaignObjectiveKind.DefendRegion);   // persisted ordinal must not move
        }

        // ---- the boost --------------------------------------------------------------------------

        [Fact]
        public void TheBoostAppliesOnlyToNodesInTheObjectiveRegion()
        {
            FactionState s = WithObjective(SeizeObjectives.FromBeachhead(Beachhead(), AgniId));

            Assert.Equal(0.25, SeizeObjectives.QualityBoost(s, Node("N1", "SPRT", NodeZone.Surface, AgniId), 0.25), 6);
            Assert.Equal(0.0,  SeizeObjectives.QualityBoost(s, Node("N2", "SPRT", NodeZone.Surface, KorId), 0.25), 6);
        }

        [Fact]
        public void AFactionWithNoObjectiveIsScoredExactlyAsBefore()
        {
            FactionState s = WithObjective(null);
            Assert.Equal(0.0, SeizeObjectives.QualityBoost(s, Node("N1", "SPRT", NodeZone.Surface, AgniId), 0.25), 6);
        }

        [Fact]
        public void TheBoostIsAWeightingNotAFilter()
        {
            // A faction pursuing an objective must still be able to answer a rival at its back: the
            // boost is additive on the score, so a much more valuable target elsewhere still wins.
            FactionState s = WithObjective(SeizeObjectives.FromBeachhead(Beachhead(), AgniId));

            double objectiveNode = 0.30 + SeizeObjectives.QualityBoost(s, Node("N1", "SPRT", NodeZone.Surface, AgniId), 0.25);
            double richerElsewhere = 0.90 + SeizeObjectives.QualityBoost(s, Node("N2", "SPRT", NodeZone.Surface, KorId), 0.25);

            Assert.True(richerElsewhere > objectiveNode);
        }

        [Fact]
        public void RepeatedDecisionsReinforceOneObjectiveRatherThanStacking()
        {
            CampaignObjective first = SeizeObjectives.FromBeachhead(Beachhead(), AgniId);
            var focus = new CampaignFocus { Id = "f1" };
            focus.Objectives.Add(first);

            Assert.True(SeizeObjectives.AlreadyCarries(focus, SeizeObjectives.FromBeachhead(Beachhead(), AgniId)));
            Assert.False(SeizeObjectives.AlreadyCarries(focus, SeizeObjectives.FromBeachhead(Beachhead(), KorId)));
        }
    }
}
