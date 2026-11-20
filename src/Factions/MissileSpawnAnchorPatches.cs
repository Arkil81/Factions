using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.Common.ObjectBuilders;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Carries a missile's spawn point along with the grid that fired it while SE builds the entity.
    ///
    /// <para><b>The mechanism.</b> <c>MyGunBase.AddMissile</c> computes the spawn point at fire time —
    /// the muzzle plus <c>MyMissile.SPAWN_DISTANCE</c> (4 m) along the shot — and writes it into an
    /// object builder. <c>MyMissiles</c> never reuses a missile (<c>Return</c> removes its pool key
    /// and closes the entity), so every shot goes through <c>CreateFromObjectBuilderParallel</c>: a
    /// worker thread, then a <c>MySandboxGame.Invoke</c> back to the main thread, then
    /// <c>OnAddedToScene</c>. That is at least one frame, and the missile appears at the fire-time
    /// position. A grid at 500 m/s covers 8.3 m a frame, so the spawn point is behind the muzzle by
    /// the time the missile exists, and the server's physics body starts inside the firing grid.</para>
    ///
    /// <para><b>The fix.</b> At fire time the firer's top-most parent (the grid, or a character for a
    /// hand-held launcher) and its world matrix are recorded against the builder's entity id. When
    /// the missile is added to the scene, its position is moved by however far that parent has moved
    /// and turned since. A stationary firer moves the missile by zero.</para>
    ///
    /// <para>Collision is server-only — <c>MyMissile.Init</c> enables physics only when
    /// <c>Sync.IsServer</c> — so this end is the one that stops the explosion. <c>FactionsUI</c>
    /// carries the same pair so the drawn round starts where the server's does. FAC-389.</para>
    /// </summary>
    internal static class MissileSpawnAnchors
    {
        private struct Anchor
        {
            public long ParentId;
            public MatrixD ParentMatrix;
        }

        /// <summary>Entries are removed when their missile enters the scene. A missile whose entity
        /// never initialises would leave one behind, so the table is cleared past this size.</summary>
        private const int MaxPending = 256;

        private static readonly Dictionary<long, Anchor> Pending = new Dictionary<long, Anchor>();
        private static readonly object Gate = new object();

        /// <summary>Missiles moved at spawn, and the largest move, for <c>factions missilespeed</c>.</summary>
        internal static int Anchored;
        internal static double MaxShiftMeters;

        /// <summary>Records the firer's pose. Called from a gun's update, which can run in parallel,
        /// hence the lock. An existing entry is kept: it is the earlier, fire-time one.</summary>
        internal static void Record(long missileId, long launcherId)
        {
            if (missileId == 0) return;
            MyEntity launcher;
            if (!MyEntities.TryGetEntityById(launcherId, out launcher, allowClosed: false)) return;
            MyEntity parent = launcher.GetTopMostParent();
            if (parent == null || parent.Physics == null) return;

            var anchor = new Anchor { ParentId = parent.EntityId, ParentMatrix = parent.WorldMatrix };
            lock (Gate)
            {
                if (Pending.ContainsKey(missileId)) return;
                if (Pending.Count >= MaxPending) Pending.Clear();
                Pending[missileId] = anchor;
            }
        }

        /// <summary>Moves the missile with its firer. Runs before vanilla <c>OnAddedToScene</c>
        /// reads the position into <c>Start</c>.</summary>
        internal static void Apply(MyMissile missile)
        {
            if (missile == null) return;
            Anchor anchor;
            lock (Gate)
            {
                if (!Pending.TryGetValue(missile.EntityId, out anchor)) return;
                Pending.Remove(missile.EntityId);
            }

            MyEntity parent;
            if (!MyEntities.TryGetEntityById(anchor.ParentId, out parent, allowClosed: false)) return;

            Vector3D was = missile.PositionComp.GetPosition();
            Vector3D local = Vector3D.Transform(was, MatrixD.Invert(anchor.ParentMatrix));
            Vector3D now = Vector3D.Transform(local, parent.WorldMatrix);
            double shift = Vector3D.Distance(was, now);
            if (shift < 0.01) return;

            missile.PositionComp.SetPosition(now);
            lock (Gate)
            {
                Anchored++;
                if (shift > MaxShiftMeters) MaxShiftMeters = shift;
            }
        }
    }

    /// <summary>Fire time on the server: <c>MyGunBase.AddMissile</c> builds the missile here.</summary>
    [HarmonyPatch]
    [PatchGroup("MissileSpawnAnchor")]
    internal static class MissileSpawnFirePatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile spawn anchor: fire patch bound.");
                else
                    Log.Error("Missile spawn anchor: MyMissile.PrepareBuilder did not resolve, so a missile "
                            + "fired from a moving grid spawns where the muzzle was when it fired.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "PrepareBuilder");
        }

        [HarmonyPostfix]
        private static void Postfix(MyObjectBuilder_Missile __result, long launcherId)
        {
            if (__result != null) MissileSpawnAnchors.Record(__result.EntityId, launcherId);
        }
    }

    /// <summary>Where the missile enters the world. The prefix moves it before vanilla's
    /// <c>Start</c> reads its position.</summary>
    [HarmonyPatch]
    [PatchGroup("MissileSpawnAnchor")]
    internal static class MissileSpawnScenePatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile spawn anchor: scene patch bound.");
                else
                    Log.Error("Missile spawn anchor: MyMissile.OnAddedToScene(object) did not resolve, so a "
                            + "missile fired from a moving grid spawns where the muzzle was when it fired.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
        }

        [HarmonyPrefix]
        private static void Prefix(MyMissile __instance)
        {
            MissileSpawnAnchors.Apply(__instance);
        }
    }
}
