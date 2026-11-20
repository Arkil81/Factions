using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// The client half of the server's missile spawn anchor (FAC-389).
///
/// <para>A missile is created through <c>CreateFromObjectBuilderParallel</c> and enters the world at
/// least a frame after its builder arrives, at the builder's position, while the grid that fired it
/// keeps moving. The server moves its copy by however far the firer's grid moved in that time;
/// without the same move here, the drawn round starts behind the server's. This end has no missile
/// physics, so it cannot collide. The server-side reasoning is in
/// <c>Factions/MissileSpawnAnchorPatches.cs</c>.</para>
///
/// <para>The pose is taken when the builder arrives (<c>MyMissiles.Add</c>). The server takes it at
/// fire time, because <c>MyGunBase.AddMissile</c> returns early off the server.</para>
/// </summary>
internal static class MissileSpawnAnchors
{
    private struct Anchor
    {
        public long ParentId;
        public MatrixD ParentMatrix;
    }

    private const int MaxPending = 256;

    private static readonly Dictionary<long, Anchor> Pending = new();
    private static readonly object Gate = new();

    internal static void Record(long missileId, long launcherId)
    {
        if (missileId == 0) return;
        if (!MyEntities.TryGetEntityById(launcherId, out MyEntity launcher, allowClosed: false)) return;
        MyEntity parent = launcher.GetTopMostParent();
        if (parent?.Physics == null) return;

        var anchor = new Anchor { ParentId = parent.EntityId, ParentMatrix = parent.WorldMatrix };
        lock (Gate)
        {
            if (Pending.ContainsKey(missileId)) return;
            if (Pending.Count >= MaxPending) Pending.Clear();
            Pending[missileId] = anchor;
        }
    }

    internal static void Apply(MyMissile missile)
    {
        if (missile == null) return;
        Anchor anchor;
        lock (Gate)
        {
            if (!Pending.TryGetValue(missile.EntityId, out anchor)) return;
            Pending.Remove(missile.EntityId);
        }

        if (!MyEntities.TryGetEntityById(anchor.ParentId, out MyEntity parent, allowClosed: false)) return;

        Vector3D was = missile.PositionComp.GetPosition();
        Vector3D local = Vector3D.Transform(was, MatrixD.Invert(anchor.ParentMatrix));
        Vector3D now = Vector3D.Transform(local, parent.WorldMatrix);
        if (Vector3D.DistanceSquared(was, now) < 0.0001) return;

        missile.PositionComp.SetPosition(now);
    }
}

[HarmonyPatch]
[Factions.PatchGroup("MissileSpawnAnchor")]
internal static class MissileSpawnArrivalPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile spawn anchor: arrival patch bound"
            : "missile spawn anchor: MyMissiles.Add did not resolve -- rounds fired from a moving grid "
              + "will be drawn behind the server's copy.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissiles), "Add", new[] { typeof(MyObjectBuilder_Missile) });
    }

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static void Prefix(MyObjectBuilder_Missile builder)
    {
        if (builder != null) MissileSpawnAnchors.Record(builder.EntityId, builder.LauncherId);
    }
}

[HarmonyPatch]
[Factions.PatchGroup("MissileSpawnAnchor")]
internal static class MissileSpawnScenePatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile spawn anchor: scene patch bound"
            : "missile spawn anchor: MyMissile.OnAddedToScene did not resolve -- rounds fired from a "
              + "moving grid will be drawn behind the server's copy.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
    }

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static void Prefix(MyMissile __instance)
    {
        MissileSpawnAnchors.Apply(__instance);
    }
}
