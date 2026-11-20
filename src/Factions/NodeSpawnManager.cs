using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Server side of respawning at a founded node (docs/new-player-experience.md → Respawning at
    /// your own nodes). Two doors:
    ///
    /// <list type="bullet">
    ///   <item><b>The list.</b> A linked client with the respawn screen open sends
    ///   <see cref="WireMessageKind.NodeSpawnListRequest"/> and is answered with its faction's
    ///   founded nodes, each with <see cref="NodeSpawnPolicy.Check"/>'s verdict.</item>
    ///   <item><b>The spawn.</b> The client sends SE's own respawn request naming
    ///   <see cref="NodeSpawnSentinel"/>; <see cref="NodeSpawnPatches"/> hands it to
    ///   <see cref="TryPlace"/>, which re-runs the same policy and answers where the character
    ///   stands.</item>
    /// </list>
    ///
    /// <para>The arrival is a suit beside the station, outside its hull and normally inside its safe
    /// zone. Nothing waits for the grid: a remote node's station is not standing when the player
    /// picks it, and the ordinary proximity rule materialises it around them a moment later. That is
    /// why the radius comes from the design and not only from a live grid.</para>
    ///
    /// <para>Cooldowns live in memory. A restart forgets them, which costs at most one early spawn
    /// per player and needs no persisted state.</para>
    ///
    /// <para>Withheld from a relayed client (<see cref="UplinkManager.RemoteWithheldFeatures"/>): the
    /// respawn request is handled by the process the player is connected to.</para>
    /// </summary>
    internal sealed class NodeSpawnManager
    {
        /// <summary>Metres between the station's clearance radius and the arrival.</summary>
        private const double ArrivalMarginM = 20.0;

        internal static NodeSpawnManager Instance;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly DecisionLog _log;
        private readonly Dictionary<long, DateTime> _lastSpawnUtc = new Dictionary<long, DateTime>();
        private readonly Random _rng = new Random();

        public NodeSpawnManager(FactionRuntime runtime, UplinkManager uplink, DecisionLog log)
        {
            _runtime = runtime;
            _uplink = uplink;
            _log = log;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.NodeSpawnListRequest, OnListRequest);
        }

        private void OnListRequest(ulong steamId, byte[] data)
        {
            if (!FactionsWire.TryReadNodeSpawnListRequest(data))
            {
                FactionsPlugin.Log.Warn("Node spawn list request from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.NodeSpawn) == 0) return;
            SendList(steamId, WingWorld.PlayerOfSteam(steamId));
        }

        /// <summary>Answers with the player's founded nodes, or an empty list for a player with no
        /// faction, which leaves the respawn screen vanilla.</summary>
        private void SendList(ulong steamId, MyPlayer player)
        {
            if (_uplink == null) return;
            var rows = new List<WireNodeSpawnRow>();
            if (player != null && player.Identity != null && _runtime != null && _runtime.World != null)
            {
                long identityId = player.Identity.IdentityId;
                foreach (NodeSpawnOption o in NodeSpawnPolicy.Options(_runtime.World.Graph,
                             FactionTagOf(identityId), DateTime.UtcNow, LastSpawn(identityId)))
                {
                    rows.Add(new WireNodeSpawnRow
                    {
                        NodeId = o.NodeId,
                        DisplayName = o.DisplayName,
                        StationId = o.StableStationId,
                        Site = (byte)o.Site,
                        Block = (byte)o.Block,
                        CooldownSeconds = o.CooldownSeconds
                    });
                }
            }
            _uplink.Send(steamId, FactionsWire.WriteNodeSpawnList(rows));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("NODE_SPAWN_LIST steam=" + steamId + " rows=" + rows.Count);
        }

        /// <summary>
        /// Where <paramref name="player"/> should arrive for a respawn naming
        /// <paramref name="stationId"/>, or false with the reason. Runs inside SE's respawn handler on
        /// the game thread. A refusal re-sends the list, so the row the player picked shows why.
        /// </summary>
        public bool TryPlace(MyPlayer player, long stationId, out MatrixD spawn, out string nodeId)
        {
            spawn = MatrixD.Identity;
            nodeId = null;
            if (player == null || player.Identity == null || _runtime == null || _runtime.World == null) return false;

            long identityId = player.Identity.IdentityId;
            ulong steamId = player.Id.SteamId;
            SpatialNode node = NodeSpawnPolicy.FindByStation(_runtime.World.Graph, stationId);
            string tag = FactionTagOf(identityId);

            string refusal = null;
            if (_uplink == null || (_uplink.GrantedFeatures(steamId) & WireFeatures.NodeSpawn) == 0) refusal = "not-granted";
            else
            {
                NodeSpawnBlock block = NodeSpawnPolicy.Check(node, tag, DateTime.UtcNow, LastSpawn(identityId));
                if (block != NodeSpawnBlock.None) refusal = block.ToString();
            }

            Vector3D stationPos = Vector3D.Zero, stationUp = Vector3D.Up;
            float clearanceM = 0f;
            if (refusal == null)
            {
                string error;
                if (!_runtime.TryGetStationArrivalFrame(node, out stationPos, out stationUp, out clearanceM, out error))
                    refusal = "no-frame (" + error + ")";
            }

            if (refusal != null)
            {
                Write("NODE_SPAWN_REFUSED identity=" + identityId + " station=" + stationId
                    + " node=" + (node != null ? node.Id : "-") + " faction=" + (tag ?? "-") + " reason=" + refusal);
                SendList(steamId, player);
                return false;
            }

            bool surface;
            spawn = ArrivalMatrix(node, stationPos, stationUp, clearanceM + ArrivalMarginM, out surface);
            nodeId = node.Id;
            Write("NODE_SPAWN identity=" + identityId + " node=" + node.Id + " owner=" + node.OwnerFactionTag
                + " site=" + node.Site + " env=" + (surface ? "surface" : "space")
                + " clearance=" + clearanceM.ToString("0") + "m"
                + " dist=" + (spawn.Translation - stationPos).Length().ToString("0") + "m");
            return true;
        }

        /// <summary>Starts the player's cooldown. Called once SE has actually spawned them, so a
        /// respawn that fails after placement costs nothing.</summary>
        public void RecordSpawn(long identityId)
        {
            _lastSpawnUtc[identityId] = DateTime.UtcNow;
        }

        // ---- placement ---------------------------------------------------------------------------

        private MatrixD ArrivalMatrix(SpatialNode node, Vector3D stationPos, Vector3D stationUp, double radiusM, out bool surface)
        {
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(stationPos);
            surface = node.Site == StationSite.Surface && planet != null
                && planet.PositionComp.WorldAABB.Contains(stationPos) != ContainmentType.Disjoint;

            // One random bearing around the station. SpawnAt's own free-place search nudges the
            // character clear of anything standing there, and leaves a safe zone alone.
            Vector3D up = UpAt(stationPos, stationUp);
            Vector3D tangent = Vector3D.Cross(up, RandomUnit());
            if (tangent.LengthSquared() < 1e-6) tangent = Perpendicular(up);
            tangent.Normalize();
            Vector3D position = stationPos + tangent * radiusM;
            if (surface) position = planet.GetClosestSurfacePointGlobal(position);

            Vector3D arrivalUp = UpAt(position, up);
            // Face the station: the first thing an arrival should see is where they are.
            Vector3D toStation = stationPos - position;
            Vector3D forward = toStation - arrivalUp * Vector3D.Dot(toStation, arrivalUp);
            if (forward.LengthSquared() < 1e-6) forward = Perpendicular(arrivalUp);
            forward.Normalize();
            return MatrixD.CreateWorld(position, forward, arrivalUp);
        }

        /// <summary>Away from natural gravity where there is any, otherwise the station's own up.</summary>
        private static Vector3D UpAt(Vector3D position, Vector3D fallbackUp)
        {
            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(position);
            if (!Vector3.IsZero(gravity)) return -Vector3D.Normalize(gravity);
            Vector3D up = fallbackUp;
            if (up.LengthSquared() < 1e-6) return Vector3D.Up;
            up.Normalize();
            return up;
        }

        private static Vector3D Perpendicular(Vector3D v)
        {
            Vector3D p = Vector3D.CalculatePerpendicularVector(v);
            p.Normalize();
            return p;
        }

        private Vector3D RandomUnit()
        {
            Vector3D v = new Vector3D(_rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1, _rng.NextDouble() * 2 - 1);
            if (v.LengthSquared() < 1e-6) return Vector3D.Forward;
            v.Normalize();
            return v;
        }

        // ---- lookups -----------------------------------------------------------------------------

        private DateTime LastSpawn(long identityId)
        {
            DateTime last;
            return _lastSpawnUtc.TryGetValue(identityId, out last) ? last : default(DateTime);
        }

        private static string FactionTagOf(long identityId)
        {
            if (MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
            return faction != null ? faction.Tag : null;
        }

        private void Write(string line)
        {
            if (_log != null) _log.Write("respawn " + line);
        }
    }
}
