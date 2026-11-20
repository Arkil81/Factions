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

            WireRelation relation = Resolve(ownerTag, viewerIdentityId);
            _cache[ownerTag] = relation;
            return relation;
        }

        private static WireRelation Resolve(string ownerTag, long viewerIdentityId)
        {
            try
            {
                if (MySession.Static == null || MySession.Static.Factions == null)
                    return WireRelation.Neutral;

                MyFaction owner = MySession.Static.Factions.TryGetFactionByTag(ownerTag);
                if (owner == null) return WireRelation.Neutral;

                MyFaction viewer = MySession.Static.Factions.GetPlayerFaction(viewerIdentityId);
                // A factionless player has no "own" and no standings; everything reads neutral
                // until they join or found one.
                if (viewer == null) return WireRelation.Neutral;
                if (viewer.FactionId == owner.FactionId) return WireRelation.Own;

                Tuple<MyRelationsBetweenFactions, int> relation =
                    MySession.Static.Factions.GetRelationBetweenFactions(viewer.FactionId, owner.FactionId);
                return relation != null && relation.Item1 == MyRelationsBetweenFactions.Enemies
                    ? WireRelation.Enemy
                    : WireRelation.Neutral;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Viewer relation resolve failed for '" + ownerTag + "'.");
                return WireRelation.Neutral;
            }
        }
    }
}
