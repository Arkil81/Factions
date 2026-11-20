using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of client-derived feature rock: tells a linked client which pristine rock stands
    /// near it, so the client can build the geometry locally and the server can stop streaming it.
    ///
    /// <para><b>What this actually saves, stated plainly.</b> The server keeps spawning every rock —
    /// it owns collision, drilling and physics, and none of that moves. The client ends up with the
    /// same entity, octree, physics body and clipmap it has today; it derives them instead of
    /// receiving them. The single thing that stops crossing the wire is each rock's serialized
    /// octree storage, which is the large part by orders of magnitude. Nothing else about this
    /// feature is a saving, and it should not be sold as one.</para>
    ///
    /// <para><b>Why the server names the rock instead of the client deriving it.</b> See
    /// <see cref="WireMessageKind.FeatureRockObjects"/>. In one line: a client that walked the cell
    /// grid itself would have to agree with the server about eight independent things — seed, cell
    /// size, density ceiling, feature geometry, station suppression, spawn ORDER, despawn vetoes,
    /// and the entity-id hash — and every disagreement presents as rock one side has and the other
    /// does not. Naming it costs about sixty bytes per rock and makes all eight impossible.</para>
    ///
    /// <para><b>Degrades per client, never globally.</b> A vanilla client, or a linked one this
    /// server withheld <see cref="WireFeatures.FeatureRock"/> from, is not in
    /// <see cref="_derives"/>, so <see cref="FeatureRockReplicationGate"/> leaves SE's own answer
    /// alone and that player is streamed rock exactly as today.</para>
    /// </summary>
    internal sealed class PlayerRockManager
    {
        /// <summary>Frames between diffs. The materialiser runs every frame; re-diffing every
        /// player against the whole live set at that rate would be the per-tick cost this feature
        /// exists to remove. Half a second is well inside the time it takes a ship to cross a cell.</summary>
        private const int TickInterval = 30;

        /// <summary>Frames between unconditional full restatements. A client whose set has drifted
        /// is not merely stale — the gate is withholding the rock it is missing, so it is standing
        /// in geometry nobody will send it. One periodic reset removes that as a permanent failure;
        /// it is a handful of kilobytes a minute against the megabytes this feature saves.</summary>
        private const int ResetInterval = 60 * 60;   // ~1 minute at 60 frames/s

        /// <summary>Matches the materialiser's own despawn band, so the set a client holds is the
        /// set the server keeps for it rather than a second, subtly different radius.</summary>
        private const double KeepHysteresis = 1.15;


        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>Clients deriving their own rock. Static-reachable because the replication gate
        /// is a Harmony patch on an SE type and has no other route to it.</summary>
        private static readonly HashSet<ulong> _derives = new HashSet<ulong>();

        /// <summary>Storage names each client has been told to hold, keyed by steam id. This is the
        /// delta baseline and nothing more; it is not authority over anything.</summary>
        private readonly Dictionary<ulong, HashSet<string>> _held = new Dictionary<ulong, HashSet<string>>();

        /// <summary>Live-set revision each client's set was computed against, so an unchanged world
        /// costs one integer comparison per player per tick.</summary>
        private readonly Dictionary<ulong, int> _seenRevision = new Dictionary<ulong, int>();

        /// <summary>Where each client stood when its set was last computed. Paired with
        /// <see cref="_seenRevision"/>: a client that has neither moved nor seen the world change
        /// is owed nothing, which is the common case for a docked or idle player.</summary>
        private readonly Dictionary<ulong, Vec3> _lastPosKm = new Dictionary<ulong, Vec3>();

        /// <summary>How far a player must move before the set is recomputed despite an unchanged
        /// world. A tenth of a cell: far enough to skip the diff while hovering, short enough that
        /// the recompute always precedes the rock coming into view.</summary>
        private const double RecomputeMoveKm = 0.5;

        private int _frame;
        private int _resetFrame;

        public static bool DebugEnabled;

        public PlayerRockManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink != null)
            {
                _uplink.ClientLinked += OnClientLinked;
                _uplink.RegisterHandler(WireMessageKind.FeatureRockRequest, OnRockRequest);
            }
        }

        public int DerivingCount { get { return _derives.Count; } }

        /// <summary>Whether the replication gate should withhold pristine feature rock from this
        /// client. False for every client this manager has not explicitly enrolled — including
        /// every vanilla client, and every client while no session is loaded.</summary>
        public static bool ClientDerivesRock(ulong steamId)
        {
            return _derives.Contains(steamId);
        }

        /// <summary>A link by itself enrols nobody. The grant only makes enrolment possible; the
        /// client asks for it once it knows it can actually build rock.</summary>
        private void OnClientLinked(ulong steamId)
        {
            _derives.Remove(steamId);
            Forget(steamId);
        }

        /// <summary>
        /// The client saying whether it can derive rock, and whether its own material-control patch
        /// took. Enrolment happens here and nowhere else.
        ///
        /// <para><b>A material-control mismatch refuses the enrolment rather than degrading it.</b>
        /// Both ends narrow SE's voxel-material pool through a Harmony patch on a reflected private
        /// member (<see cref="FeatureRockMaterialControl"/>); if one binding holds and the other
        /// does not, the same rock is ice on one side and gold on the other, and nothing anywhere
        /// says so. Refusing costs that one client the bandwidth saving and costs correctness
        /// nothing — the server simply keeps replicating to it, which is what a vanilla client gets
        /// today.</para>
        /// </summary>
        private void OnRockRequest(ulong steamId, byte[] data)
        {
            bool canDerive, clientMaterialControl;
            if (!FactionsWire.TryReadFeatureRockRequest(data, out canDerive, out clientMaterialControl))
            {
                FactionsPlugin.Log.Warn("Feature-rock request from " + steamId + " was unreadable.");
                return;
            }

            WireFeatures granted = _uplink != null ? _uplink.GrantedFeatures(steamId) : WireFeatures.None;
            string refusal = null;
            if ((granted & WireFeatures.FeatureRock) == 0) refusal = "feature not granted";
            else if (!canDerive) refusal = "client cannot derive";
            else if (clientMaterialControl != FeatureRockMaterialControl.Active)
                refusal = "material control mismatch (server=" + FeatureRockMaterialControl.Active
                        + " client=" + clientMaterialControl + ")";

            if (refusal != null)
            {
                bool wasEnrolled = _derives.Remove(steamId);
                Forget(steamId);
                // Always logged, not debug-gated: this decides whether a player is being sent rock
                // at all, and a silent refusal presents as a bandwidth feature that simply never
                // engaged. It is once per client per session.
                FactionsPlugin.Log.Info("ROCK_UPLINK steam=" + steamId
                    + (wasEnrolled ? " WITHDRAWN" : " REFUSED") + " -- " + refusal
                    + "; the server keeps replicating rock to this client.");
                return;
            }

            _derives.Add(steamId);
            Forget(steamId);   // owed a full reset on the next tick
            FactionsPlugin.Log.Info("ROCK_UPLINK steam=" + steamId
                + " ENROLLED -- derives its own feature rock (material control="
                + FeatureRockMaterialControl.Active + ")");
        }

        /// <summary>Drops clients that are no longer online. Called on the same prune pass as the
        /// uplink's own, so a disconnected player cannot leave the gate withholding rock from a
        /// steam id that later belongs to nobody.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_derives.Count == 0) return;
            List<ulong> stale = null;
            foreach (ulong steamId in _derives)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _derives.Remove(steamId);
                Forget(steamId);
            }
        }

        public void Close()
        {
            if (_uplink != null) _uplink.ClientLinked -= OnClientLinked;
            _derives.Clear();
            _held.Clear();
            _seenRevision.Clear();
            _lastPosKm.Clear();
        }

        private void Forget(ulong steamId)
        {
            _held.Remove(steamId);
            _seenRevision.Remove(steamId);
            _lastPosKm.Remove(steamId);
        }

        /// <summary>Called every frame from the sim tick, immediately after the materialiser has
        /// updated the live set — and returns immediately on all but every
        /// <see cref="TickInterval"/>th frame, and on every frame at all while nobody is deriving.</summary>
        public void Tick()
        {
            if (_derives.Count == 0) return;
            if (++_frame % TickInterval != 0) return;
            if (_runtime == null || MySession.Static == null) return;

            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            if (rock == null) return;

            bool forceReset = (_resetFrame += TickInterval) >= ResetInterval;
            if (forceReset) _resetFrame = 0;

            double keepKm = FeatureRockMaterializer.ViewRadiusKm(_runtime.Tuning) * KeepHysteresis;
            int revision = rock.Revision;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot) continue;
                ulong steamId = player.Id.SteamId;
                if (!_derives.Contains(steamId)) continue;

                Vec3 posKm;
                if (!TryPlayerPositionKm(player, out posKm)) continue;

                if (!forceReset && Unchanged(steamId, revision, posKm)) continue;
                _seenRevision[steamId] = revision;
                _lastPosKm[steamId] = posKm;

                SendDelta(steamId, rock, posKm, keepKm, forceReset);
            }
        }

        private bool Unchanged(ulong steamId, int revision, Vec3 posKm)
        {
            int seen;
            if (!_seenRevision.TryGetValue(steamId, out seen) || seen != revision) return false;
            Vec3 last;
            if (!_lastPosKm.TryGetValue(steamId, out last)) return false;
            return last.DistanceTo(posKm) < RecomputeMoveKm;
        }

        private void SendDelta(ulong steamId, FeatureRockMaterializer rock, Vec3 posKm, double keepKm, bool reset)
        {
            HashSet<string> held;
            if (reset || !_held.TryGetValue(steamId, out held))
            {
                held = new HashSet<string>(StringComparer.Ordinal);
                _held[steamId] = held;
            }

            double keep2 = keepKm * keepKm;
            List<WireRockObject> present = null;
            var stillNear = new HashSet<string>(StringComparer.Ordinal);

            foreach (FeatureRockMaterializer.LiveRock live in rock.LiveRocks())
            {
                FeatureRockObject plan = live.Plan;
                if (plan.PositionKm.DistanceSquaredTo(posKm) > keep2) continue;

                // A rock stays in the materialiser's records after a player has edited it, right up
                // until its cell despawns. Naming one would tell a client to derive geometry the
                // server is already replicating a corrected version of.
                if (live.Spawned)
                {
                    if (!rock.OwnsPristine(live.EntityId)) continue;
                }
                // With rock shells off, only rock the server has spawned is named, so collision exists before
                // geometry. With them on, the server spawns rock only inside the shell around each player's own
                // body and names the rest of the plan, which the client builds as scenery; an entity already
                // standing under the id is edited rock SE replicates itself.
                else if (FeatureRockMaterializer.HasForeignEntity(live.EntityId)) continue;

                string name = plan.StorageName ?? "";
                stillNear.Add(name);
                if (!reset && held.Contains(name)) continue;   // already built client-side

                if (present == null) present = new List<WireRockObject>();
                present.Add(new WireRockObject
                {
                    StorageName = name,
                    EntityId = live.EntityId,
                    XKm = plan.PositionKm.X,
                    YKm = plan.PositionKm.Y,
                    ZKm = plan.PositionKm.Z,
                    SizeM = plan.SizeM,
                    MaterialSubTypeId = plan.MaterialSubTypeId,
                    ShapeSeed = plan.ShapeSeed,
                    // Sent per rock because they are authored per material. Without them the client
                    // generates against the world's own DepositsCountCoefficient/SizeDenominator and
                    // builds the same seed into a rock with a different amount of ore -- identity
                    // agreeing while volume diverges, which is FAC-092's failure in a new dimension
                    // and just as silent.
                    HasDepositCount = plan.DepositCountCoefficient.HasValue,
                    DepositCountCoefficient = plan.DepositCountCoefficient ?? 0.0,
                    HasDepositSize = plan.DepositSizeDenominator.HasValue,
                    DepositSizeDenominator = plan.DepositSizeDenominator ?? 0.0,
                });
            }

            List<string> removed = null;
            if (!reset)
            {
                foreach (string name in held)
                {
                    if (stillNear.Contains(name)) continue;
                    if (removed == null) removed = new List<string>();
                    removed.Add(name);
                }
            }

            // A reset restates the whole set even when it is identical, because its purpose is to
            // repair a client whose set we cannot see. Otherwise an empty diff sends nothing.
            if (!reset && present == null && removed == null) return;

            _held[steamId] = stillNear;
            _uplink.Send(steamId, FactionsWire.WriteFeatureRockObjects(
                present, removed, reset, FeatureRockMaterializer.GeneratorVersion));

            if (DebugEnabled)
                FactionsPlugin.Log.Info("ROCK_WIRE steam=" + steamId
                    + " present=" + (present == null ? 0 : present.Count)
                    + " removed=" + (removed == null ? 0 : removed.Count)
                    + " held=" + stillNear.Count
                    + (reset ? " reset" : ""));
        }

        private static bool TryPlayerPositionKm(MyPlayer player, out Vec3 posKm)
        {
            posKm = default(Vec3);
            try
            {
                if (player.Character == null) return false;
                VRageMath.Vector3D p = player.Character.PositionComp.GetPosition();
                posKm = new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0);
                return true;
            }
            catch { return false; }
        }

    }
}
