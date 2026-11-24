using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Resolves how a faction stands towards one viewing player, for anything the uplink sends to
    /// a client. Reads SE's own faction relations so the answer agrees with the rest of that
    /// player's HUD.
    ///
    /// Lives here rather than in Core, which must stay SE-free, and is never done on the client,
    /// which has no business being handed the standings table.
    ///
    /// One instance per feature per pass: a relationship is viewer-relative, so <see cref="Clear"/>
    /// must be called whenever the viewer changes. The cache exists because resolving the same
    /// owner tag for every node or contact in one pass would otherwise hit the SE faction tables
    /// dozens of times.
    /// </summary>
    internal sealed class ViewerRelations
    {
        private readonly Dictionary<string, WireRelation> _cache =
            new Dictionary<string, WireRelation>(StringComparer.Ordinal);
        private readonly string _surface;

        /// <param name="surface">The feature this instance answers for, named in RELATION_RESOLVE.</param>
        public ViewerRelations(string surface)
        {
            _surface = surface;
        }

        /// <summary>Drops the cached answers. Call between viewers, never within one.</summary>
        public void Clear()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Standing of <paramref name="ownerTag"/> towards the player behind
        /// <paramref name="viewerIdentityId"/>. Unowned and claimable things read as
        /// <see cref="WireRelation.Unknown"/> rather than neutral, so the client can colour "nobody
        /// holds this" differently from "someone holds this and you are not at war".
        /// </summary>
        public WireRelation For(string ownerTag, long viewerIdentityId, bool unclaimed = false)
        {
            if (unclaimed || NeutralFaction.IsClaimable(ownerTag)) return WireRelation.Unknown;

            WireRelation cached;
            if (_cache.TryGetValue(ownerTag, out cached)) return cached;

            WireRelation relation = Resolve(ownerTag, viewerIdentityId, _surface);
            _cache[ownerTag] = relation;
            return relation;
        }

        /// <summary>The answer, and under <c>factions debug relation</c> the step that decided it:
        /// <c>no-session</c>, <c>no-owner</c>, <c>no-viewer</c>, <c>reputation</c>, <c>own</c>,
        /// <c>table</c>, <c>unprojected</c> or <c>error</c> (<see cref="RelationTrace"/>).</summary>
        private static WireRelation Resolve(string ownerTag, long viewerIdentityId, string surface)
        {
            WireRelation result = WireRelation.Neutral;
            string step = "error";
            MyFaction owner = null, viewer = null;
            Tuple<MyRelationsBetweenFactions, int> relation = null;
            try
            {
                if (MySession.Static == null || MySession.Static.Factions == null)
                {
                    step = "no-session";
                }
                else if ((owner = MySession.Static.Factions.TryGetFactionByTag(ownerTag)) == null)
                {
                    step = "no-owner";
                }
                else if (viewerIdentityId == 0L)
                {
                    step = "no-viewer";
                }
                // A factionless player has no faction standing, but SE keeps their own reputation
                // with every faction, and colours their antenna markers from it.
                else if ((viewer = MySession.Static.Factions.GetPlayerFaction(viewerIdentityId)) == null)
                {
                    step = "reputation";
                    relation = MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(
                        viewerIdentityId, owner.FactionId);
                    result = relation != null
                        ? ViewerRelationRule.FromStanding(Standing(relation.Item1))
                        : WireRelation.Neutral;
                }
                else if (viewer.FactionId == owner.FactionId)
                {
                    step = "own";
                    result = WireRelation.Own;
                }
                else
                {
                    // No row is a pair Factions has never projected, a player faction it does not
                    // register, and SE answers that with its session default, Enemies, for every NPC.
                    // The member's own reputation is what SE colours their antenna markers from, so
                    // it is the answer both layers then agree on.
                    if (ViewerRelationRule.ReadsReputation(
                            FactionDiplomacyPatch.HasRelationRow(MySession.Static.Factions, viewer.FactionId, owner.FactionId)))
                    {
                        step = "unprojected";
                        relation = MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(
                            viewerIdentityId, owner.FactionId);
                    }
                    else
                    {
                        step = "table";
                        relation = MySession.Static.Factions.GetRelationBetweenFactions(viewer.FactionId, owner.FactionId);
                    }
                    result = relation != null
                        ? ViewerRelationRule.FromStanding(Standing(relation.Item1))
                        : WireRelation.Neutral;
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Viewer relation resolve failed for '" + ownerTag + "'.");
                step = "error";
                result = WireRelation.Neutral;
            }
            if (DebugTraces.Relation)
                RelationTrace.Resolved(surface, viewerIdentityId, viewer, ownerTag, owner, step, relation, result);
            return result;
        }

        private static int Standing(MyRelationsBetweenFactions relation)
        {
            switch (relation)
            {
                case MyRelationsBetweenFactions.Enemies: return ViewerRelationRule.SeEnemies;
                case MyRelationsBetweenFactions.Friends: return ViewerRelationRule.SeFriends;
#pragma warning disable CS0618 // obsolete in SE, still a value its tables can hold
                case MyRelationsBetweenFactions.Allies: return ViewerRelationRule.SeAllies;
#pragma warning restore CS0618
                default: return ViewerRelationRule.SeNeutral;
            }
        }
    }
}
