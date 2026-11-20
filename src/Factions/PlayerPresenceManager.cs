using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Scans online player positions each observation tick and populates
    /// <see cref="SpatialNode.Presences"/> so <see cref="FactionBrain"/> can sense player
    /// proximity at owned nodes (trade opportunity, threat signals).
    ///
    /// <para>Runs on the game thread. Must NOT be called during the SE-free sim tick.</para>
    /// </summary>
    internal sealed class PlayerPresenceManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        // "At a station" threshold in km. Matches the scale of node Radius (40 km default) but
        // smaller so presence only fires when the player is genuinely close to the station grid.
        private const double PresenceRadiusKm = 5.0;

        private readonly WorldModel _world;
        private readonly FactionRuntime _runtime;

        // Edge-triggered enter/exit logging for claimable (unowned/UNKN) nodes only — this is the
        // observable signal for the Magnate-claiming feature's "player detected inside a node"
        // milestone. Keyed "<identityId>@<nodeId>"; deliberately not extended to owned nodes to
        // avoid spamming the log with routine NPC-station visits.
        private readonly HashSet<string> _claimablePresence = new HashSet<string>(StringComparer.Ordinal);

        // Node ids this scan put a player presence on last pass. Clearing stale presences used to
        // mean a walk of every node in the graph; only the nodes this manager itself touched can
        // hold a Player-kind presence, and it is the only writer of one.
        private readonly HashSet<string> _presenceNodes = new HashSet<string>(StringComparer.Ordinal);

        // Reused across passes so the scan allocates nothing per player.
        private readonly List<SpatialNode> _nearScratch = new List<SpatialNode>();

        /// <summary>Authority on a Nexus cluster: the players on agents, whose stations stand there
        /// and are this graph's nodes all the same. Null on a lone server.</summary>
        public Func<IList<RemotePlayer>> RemotePlayers { get; set; }

        public PlayerPresenceManager(FactionRuntime runtime)
        {
            _runtime = runtime;
            _world = runtime != null ? runtime.World : null;
        }

        /// <summary>
        /// Refresh all player presences across the node graph.
        /// Called from <see cref="FactionsPlugin.Update"/> on a slow tick (same cadence as GPS update).
        ///
        /// <para>Asks the proximity index which nodes are near each player rather than asking every
        /// node whether a player is near it, so the cost is the players and their neighbourhoods
        /// rather than the world. Same answer either way -- the radius is applied exactly.</para>
        /// </summary>
        public void Refresh()
        {
            if (_world?.Graph == null || _runtime == null || MySession.Static == null) return;

            // Drop last pass's presences, from the nodes that actually carry one.
            foreach (string nodeId in _presenceNodes)
            {
                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) continue;
                for (int i = node.Presences.Count - 1; i >= 0; i--)
                    if (node.Presences[i].Kind == ActorKind.Player)
                        node.Presences.RemoveAt(i);
            }
            _presenceNodes.Clear();

            var nowClaimablePresence = new HashSet<string>(StringComparer.Ordinal);

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.IsBot) continue;
                var ch = player.Character;
                if (ch == null) continue;

                Vector3D worldPos = ch.PositionComp.GetPosition();
                // SE world coords are in metres; nodes use km.
                var posKm = new Vec3(worldPos.X / 1000.0, worldPos.Y / 1000.0, worldPos.Z / 1000.0);
                Mark(player.Identity.IdentityId.ToString(), player.DisplayName, posKm, nowClaimablePresence);
            }

            Func<IList<RemotePlayer>> remote = RemotePlayers;
            IList<RemotePlayer> others = remote != null ? remote() : null;
            if (others != null)
                for (int i = 0; i < others.Count; i++)
                {
                    RemotePlayer r = others[i];
                    long identity = LocalIdentity(r);
                    if (identity == 0) continue;
                    MyIdentity id = MySession.Static.Players.TryGetIdentity(identity);
                    Mark(identity.ToString(), id != null ? id.DisplayName : null,
                         new Vec3(r.PositionM.X / 1000.0, r.PositionM.Y / 1000.0, r.PositionM.Z / 1000.0),
                         nowClaimablePresence);
                }
            _nearScratch.Clear();

            foreach (string key in _claimablePresence)
                if (!nowClaimablePresence.Contains(key))
                    Log.Info("Presence ended: " + key + " left a claimable node.");

            _claimablePresence.Clear();
            foreach (string key in nowClaimablePresence) _claimablePresence.Add(key);
        }

        /// <summary>This server's identity for a player an agent hosts: by Steam id, which every
        /// shard shares, else the identity the agent reported.</summary>
        private static long LocalIdentity(RemotePlayer r)
        {
            if (r.SteamId != 0)
            {
                long local = MySession.Static.Players.TryGetIdentityId(r.SteamId);
                if (local != 0) return local;
            }
            return r.IdentityId;
        }

        /// <summary>Puts one player's presence on every owned node within reach of them.</summary>
        private void Mark(string actorId, string displayName, Vec3 posKm, HashSet<string> nowClaimablePresence)
        {
            _nearScratch.Clear();
            _runtime.NodesWithinKmOf(posKm, PresenceRadiusKm, _nearScratch);

            for (int i = 0; i < _nearScratch.Count; i++)
            {
                SpatialNode node = _nearScratch[i];
                if (node.OwnerFactionTag == null) continue;
                node.Presences.Add(new Presence
                {
                    ActorId = actorId,
                    Kind    = ActorKind.Player,
                    Hostile = false,
                });
                _presenceNodes.Add(node.Id);

                if (NeutralFaction.IsClaimable(node.OwnerFactionTag))
                {
                    string key = actorId + "@" + node.Id;
                    nowClaimablePresence.Add(key);
                    if (!_claimablePresence.Contains(key))
                        Log.Info("Presence detected: " + (displayName ?? actorId)
                            + " entered claimable node " + node.Id + " (" + NodeDisplay.DominantOre(node) + ")");
                }
            }
        }
    }
}
