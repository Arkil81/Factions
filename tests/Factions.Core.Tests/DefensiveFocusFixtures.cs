using System;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>Builds the defensive focus a Patrol circuit now dispatches under. Before
    /// FAC-309 slice 3 a patrol fired on raw <c>SpatialNode.Threat</c> alone, so most Patrol tests
    /// established their trigger by setting a node's threat and nothing else; the focus is what
    /// dispatches one now, and threat only orders the circuit.</summary>
    internal static class DefensiveFocusFixtures
    {
        /// <summary>A brain-issued defensive focus centred on <paramref name="centerNodeId"/> and
        /// covering it plus <paramref name="alsoMembers"/>, live for an hour from
        /// <paramref name="nowUtc"/>.</summary>
        public static CampaignFocus Defensive(DateTime nowUtc, string centerNodeId, params string[] alsoMembers)
        {
            var focus = new CampaignFocus
            {
                Id = centerNodeId,
                CenterNodeId = centerNodeId,
                Priority = 0.8,
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddHours(1),
            };
            focus.RegionMembers.Add(centerNodeId);
            if (alsoMembers != null)
                foreach (string memberId in alsoMembers) focus.RegionMembers.Add(memberId);
            focus.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.DefendRegion,
                TargetNodeId = centerNodeId,
            });
            return focus;
        }

        /// <summary>Gives <paramref name="state"/> a defensive focus over the named nodes.</summary>
        public static void GiveDefensiveFocus(FactionState state, DateTime nowUtc, string centerNodeId,
                                              params string[] alsoMembers)
        {
            state.CampaignFoci.Add(Defensive(nowUtc, centerNodeId, alsoMembers));
            CampaignFocusLifecycle.RefreshCompatibilityView(state);
        }
    }
}
