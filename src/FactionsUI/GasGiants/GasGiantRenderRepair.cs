using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;

namespace FactionsUI.GasGiants;

/// <summary>
/// Puts Real Gas Giants' bodies back after a SeamlessClient server switch closes them, so the player sees
/// the gas giant rather than its 9.5 km voxel core until they relog. Mechanism: <see cref="RggRenderRebuild"/>.
///
/// <para>Driven by a once-a-second check rather than by detecting the switch: SE skips
/// <c>LoadData</c> for a component already loaded, which a seamless switch never unloads, and a check that
/// only looks for closed entities also covers a second sweep later in the same switch. It finds nothing to
/// do on every other second.</para>
///
/// <para>The live RGG component is found by type name through the session, not by
/// <c>Type.GetType</c>: every session load compiles mod scripts into a new assembly and the old ones stay
/// loaded, each with its own stale statics.</para>
///
/// <para>A per-mod adapter, which docs/mod-compatibility.md otherwise rules out; the operator chose it as the
/// interim on 2026-11-27 until RGG rebuilds its own render entities. Delete it when RGG does. See
/// docs/issues/FAC-535.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class GasGiantRenderRepair : MySessionComponentBase
{
    private const int CheckIntervalTicks = 60;

    private int _tick;
    private bool _bound;
    private RggRenderRebuild _rebuild;

    public override void UpdateAfterSimulation()
    {
        if (++_tick < CheckIntervalTicks) return;
        _tick = 0;

        if (!_bound) Bind();
        if (_rebuild == null) return;

        List<RggRenderRebuild.Rebuilt> rebuilt;
        try
        {
            rebuilt = _rebuild.Repair(IsClosed, Restyle);
        }
        catch (Exception e)
        {
            _rebuild = null;
            Plugin.Log("gas giant render repair: rebuild threw, off for this session -- "
                       + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message);
            return;
        }

        foreach (RggRenderRebuild.Rebuilt r in rebuilt)
            Plugin.Log("RGG_RENDER_REBUILT planet=" + r.Planet + " model=" + (r.Model ? 1 : 0)
                       + " interior=" + (r.Interior ? 1 : 0) + " occluder=" + (r.Occluder ? 1 : 0));
    }

    private void Bind()
    {
        _bound = true;
        MySessionComponentBase core = MySession.Static?.GetComponentByTypeName(RggRenderRebuild.CoreTypeName);
        if (core == null) return;

        _rebuild = RggRenderRebuild.Bind(core.GetType(), out string missing);
        Plugin.Log(_rebuild != null
            ? "gas giant render repair: bound to Real Gas Giants"
            : "gas giant render repair: Real Gas Giants is loaded but " + missing + " did not resolve -- a "
              + "server switch leaves each gas giant drawn as its voxel core until a relog.");
    }

    private static bool IsClosed(object entity)
    {
        var e = entity as MyEntity;
        return e == null || e.MarkedForClose || e.Closed;
    }

    /// <summary>What RGG's constructor applies to a new sphere model.</summary>
    private void Restyle(object comp, object model)
    {
        var m = (MyEntity)model;
        m.Render.ColorMaskHsv = (Vector3)_rebuild.ColorHsvOf(comp);
        m.Render.UpdateRenderTextureChanges((Dictionary<MyStringId, MyTextureChange>)_rebuild.SkinOf(comp));
        m.PositionComp.Scale = _rebuild.RadiusKmOf(comp) * 1000f;
    }
}
