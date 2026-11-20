using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Which knowledge stores a viewer draws on, and how recently an entry must have been renewed to
    /// count as seen right now rather than remembered.
    ///
    /// <para>Shared by every player-facing surface that answers "may they see this, and what do they
    /// believe about it" — the map, the node window and the market. Held in one place because those
    /// surfaces sit one click apart: a map marker drawn from a memory beside a node window drawn
    /// from the live graph is the disagreement <see cref="PlayerScannerManager"/> and
    /// <see cref="PlayerMapManager"/> have already had twice over range and over detail.</para>
    /// </summary>
    internal static class ViewerKnowledge
    {
        /// <summary>How recently an entry must have been renewed to count as currently detected
        /// rather than remembered. Comfortably longer than the sweep cadence that renews it, so a
        /// node in continuous coverage never flickers between the two states.</summary>
        internal static readonly TimeSpan DetectedFreshness = TimeSpan.FromMinutes(10);

        /// <summary>The viewer's SE faction tag, or null when they are in none. This is the tag the
        /// birthright and own-estate clauses are asked about, so it is the SE membership rather than
        /// the narrower "registered player faction" a command would check.</summary>
        internal static string FactionTag(long viewerIdentityId)
        {
            if (viewerIdentityId == 0L || MySession.Static == null || MySession.Static.Factions == null)
                return null;
            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(viewerIdentityId);
            return faction == null ? null : faction.Tag;
        }

        /// <summary>The stores this viewer draws on: their own, plus their faction's if they are in a
        /// registered one. Unioned at read and never merged, so leaving a faction drops its map while
        /// the player keeps what they detected themselves.</summary>
        internal static void BuildSources(
            FactionRuntime runtime, PlayerScannerManager scanner, long viewerIdentityId,
            List<FactionKnowledge> into)
        {
            if (into == null) return;
            into.Clear();
            if (scanner != null) into.Add(scanner.KnowledgeFor(viewerIdentityId));
            string tag = FactionTag(viewerIdentityId);
            if (string.IsNullOrEmpty(tag) || runtime == null) return;
            FactionState state = runtime.FindStateForMap(tag);
            if (state != null) into.Add(state.Knowledge);
        }

        /// <summary>True when some store the viewer draws on has this node under a sensor now.
        /// Detected is the strictly narrower claim: an entry renewed within this cadence means a
        /// sensor has it right now, rather than merely having had it at some point.</summary>
        internal static bool IsDetected(IList<FactionKnowledge> sources, string nodeId, DateTime nowUtc)
        {
            if (sources == null) return false;
            for (int i = 0; i < sources.Count; i++)
            {
                DateTime lastSeen;
                if (sources[i] != null && sources[i].TryGetLastSeen(nodeId, out lastSeen)
                    && nowUtc - lastSeen <= DetectedFreshness)
                    return true;
            }
            return false;
        }
    }
}
