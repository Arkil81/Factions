using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The whole world's station listings for one pass, and the contact question the market's
    /// freshness rule turns on. Split out of <see cref="PlayerMarketManager"/> so both halves are
    /// reachable without SE session state: everything here is graph, tuning and arithmetic.
    /// </summary>
    internal sealed class PlayerMarketWorld
    {
        /// <summary>node id -> that station's rows, rebuilt once per pass. Built once for the world
        /// rather than once per subscriber: the rows do not depend on who is looking, only on
        /// whether they are allowed to look.</summary>
        public readonly Dictionary<string, List<StationListing>> Listings =
            new Dictionary<string, List<StationListing>>(StringComparer.Ordinal);

        /// <summary>node id -> owner tag, for the stations in <see cref="Listings"/>.</summary>
        public readonly Dictionary<string, string> Owners =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Rebuilds the table. A station is included when it has a stable station id — <b>not</b>
        /// when its grid is spawned. That distinction is the whole feature: `StationStoreManager`
        /// gates on a live grid because it is writing into one, and a market view that copied that
        /// gate would show only the station the player is already standing at.
        /// </summary>
        public void Rebuild(
            WorldModel world, Func<SpatialNode, List<StationListing>, int> build,
            HashSet<string> registeredTags)
        {
            Listings.Clear();
            Owners.Clear();
            if (world == null || world.Graph == null || build == null) return;

            foreach (SpatialNode node in world.Graph.Nodes.Values)
            {
                if (node == null || node.StableStationId == 0) continue;
                string tag = node.OwnerFactionTag;
                if (string.IsNullOrEmpty(tag) || tag == NeutralFaction.Tag) continue;
                if (registeredTags != null && !registeredTags.Contains(tag)) continue;

                var rows = new List<StationListing>();
                build(node, rows);
                if (rows.Count == 0) continue;

                Listings[node.Id] = rows;
                Owners[node.Id] = tag;
            }
        }

        /// <summary>
        /// Whether any node of <paramref name="factionTag"/> currently detects this observer.
        ///
        /// <para><b>Liveness is scoped to the faction, not the node.</b> Being seen by one of a
        /// faction's pickets means that faction is talking to you, so every station of theirs you
        /// have already discovered reports live prices; break contact and the whole faction's table
        /// freezes together. The alternative — per-node liveness — would give one faction's table a
        /// dozen different ages and make "go and look" a per-station errand rather than a
        /// relationship.</para>
        ///
        /// <para>Every owned node counts, not only stations: <see cref="Sensing.NodeStrengthKm"/>
        /// gives each one a passive envelope, and a picket that sees you is a picket that can report
        /// you regardless of whether anything is for sale there.</para>
        /// </summary>
        public static bool FactionDetects(
            WorldModel world, FactionTuning tuning, string factionTag,
            Vec3 observerPosKm, double observerSignature)
        {
            if (world == null || world.Graph == null || tuning == null) return false;
            if (string.IsNullOrEmpty(factionTag) || observerSignature <= 0) return false;

            foreach (SpatialNode node in world.Graph.Nodes.Values)
            {
                if (node == null || node.OwnerFactionTag != factionTag) continue;
                double strength = Sensing.NodeStrengthKm(node, tuning);
                if (strength <= 0) continue;
                if (Sensing.Detects(node.Position, strength, observerPosKm, observerSignature))
                    return true;
            }
            return false;
        }

        /// <summary>The frozen form of a live row set — price and side, never quantity. What a
        /// faction wanted survives being remembered; how much of it does not.</summary>
        public static List<FrozenMarketRow> Freeze(List<StationListing> rows)
        {
            var frozen = new List<FrozenMarketRow>(rows == null ? 0 : rows.Count);
            if (rows == null) return frozen;
            for (int i = 0; i < rows.Count; i++)
            {
                frozen.Add(new FrozenMarketRow
                {
                    InvKey = rows[i].InvKey,
                    Side = (byte)rows[i].Side,
                    UnitPrice = (int)Math.Min(rows[i].UnitPrice, int.MaxValue),
                });
            }
            return frozen;
        }
    }
}
