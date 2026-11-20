using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Why a founded node cannot take a respawn right now. <see cref="None"/> is the
    /// spawnable case. The byte values travel on the wire to the respawn screen.</summary>
    public enum NodeSpawnBlock : byte
    {
        None = 0,
        /// <summary>No such node, or it carries no station to arrive at.</summary>
        NoStation = 1,
        /// <summary>The node belongs to a faction other than the spawner's, or the spawner has none.</summary>
        NotYours = 2,
        /// <summary>The station's safe zone is down: a siege broke through or a raid landed.</summary>
        ZoneDown = 3,
        /// <summary>The spawner used a node spawn too recently.</summary>
        Cooldown = 4,
    }

    /// <summary>One of the spawner's own founded nodes, as the respawn screen lists it.</summary>
    public sealed class NodeSpawnOption
    {
        public string NodeId;
        public string DisplayName;
        public long StableStationId;
        public StationSite Site;
        public NodeSpawnBlock Block;
        /// <summary>Whole seconds until <see cref="NodeSpawnBlock.Cooldown"/> lifts; 0 otherwise.</summary>
        public int CooldownSeconds;
    }

    /// <summary>
    /// Whether a player may respawn at one of their faction's founded nodes
    /// (docs/new-player-experience.md → Respawning at your own nodes).
    ///
    /// <para>Pure policy so the list the respawn screen shows and the check the server makes at the
    /// moment of spawning refuse for the same reasons. The spawn itself is free; the only price is
    /// <see cref="CooldownSeconds"/> per player, across all of their nodes, so a node is a way back
    /// into the world and not a revolving door into a fight.</para>
    /// </summary>
    public static class NodeSpawnPolicy
    {
        /// <summary>Seconds a player waits between node spawns. A code constant, not tuning.</summary>
        public const int CooldownSeconds = 300;

        /// <summary>
        /// Why <paramref name="node"/> cannot take a respawn from a member of
        /// <paramref name="spawnerTag"/>, or <see cref="NodeSpawnBlock.None"/>.
        /// <paramref name="lastSpawnUtc"/> is the spawner's previous node spawn, default for none.
        /// Checks run in the order a player can do something about them last: a node that is not
        /// theirs never reports a cooldown.
        /// </summary>
        public static NodeSpawnBlock Check(SpatialNode node, string spawnerTag, DateTime now, DateTime lastSpawnUtc)
        {
            if (node == null || node.StableStationId == 0) return NodeSpawnBlock.NoStation;
            if (string.IsNullOrEmpty(spawnerTag)
                || !string.Equals(node.OwnerFactionTag, spawnerTag, StringComparison.Ordinal))
                return NodeSpawnBlock.NotYours;
            if (!PlayerSiege.ZoneShouldBeEnabled(node, now)) return NodeSpawnBlock.ZoneDown;
            if (CooldownRemainingSeconds(now, lastSpawnUtc) > 0) return NodeSpawnBlock.Cooldown;
            return NodeSpawnBlock.None;
        }

        /// <summary>Whole seconds, rounded up, until the spawner may use a node again; 0 when free.</summary>
        public static int CooldownRemainingSeconds(DateTime now, DateTime lastSpawnUtc)
        {
            if (lastSpawnUtc == default(DateTime)) return 0;
            double left = CooldownSeconds - (now - lastSpawnUtc).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }

        /// <summary>Every founded node of <paramref name="spawnerTag"/>, blocked ones included so
        /// the screen can say why, ordered by display name. Empty for a factionless spawner.</summary>
        public static List<NodeSpawnOption> Options(SpatialGraph graph, string spawnerTag, DateTime now, DateTime lastSpawnUtc)
        {
            var options = new List<NodeSpawnOption>();
            if (graph == null || string.IsNullOrEmpty(spawnerTag)) return options;

            int cooldown = CooldownRemainingSeconds(now, lastSpawnUtc);
            foreach (SpatialNode node in graph.OwnedBy(spawnerTag))
            {
                if (node == null || node.StableStationId == 0) continue;
                NodeSpawnBlock block = Check(node, spawnerTag, now, lastSpawnUtc);
                options.Add(new NodeSpawnOption
                {
                    NodeId = node.Id,
                    DisplayName = !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName : node.Id,
                    StableStationId = node.StableStationId,
                    Site = node.Site,
                    Block = block,
                    CooldownSeconds = block == NodeSpawnBlock.Cooldown ? cooldown : 0
                });
            }
            options.Sort((a, b) =>
            {
                int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.CompareOrdinal(a.NodeId, b.NodeId);
            });
            return options;
        }

        /// <summary>The founded node carrying <paramref name="stableStationId"/>, or null.</summary>
        public static SpatialNode FindByStation(SpatialGraph graph, long stableStationId)
        {
            if (graph == null || stableStationId == 0) return null;
            foreach (SpatialNode node in graph.Nodes.Values)
                if (node != null && node.StableStationId == stableStationId) return node;
            return null;
        }
    }
}
