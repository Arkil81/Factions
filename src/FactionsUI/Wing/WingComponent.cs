using System;
using System.Collections.Generic;
using Factions;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace FactionsUI.Wing;

/// <summary>
/// The client end of the wing actions (docs/client-plugin.md, Part 18): registers
/// <i>Wing: form on me … release</i> on every antenna's toolbar list through the shared
/// <see cref="WingAntennaActions"/>, and turns a press into a
/// <see cref="WireMessageKind.WingCommandRequest"/> — the antenna, the command, and the grid under
/// the crosshair. The server reads the presser's own target lock itself and re-validates everything;
/// its answer is shown on the HUD.
///
/// <para>The actions are registered whatever the server, so a slot keeps resolving across servers
/// and sessions; a press on a server that did not grant <see cref="WireFeatures.WingCommand"/> says
/// so and sends nothing.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class WingComponent : MySessionComponentBase
{
    /// <summary>How far the crosshair is read: the server's own sight limit for a named target.</summary>
    private const double CrosshairRangeM = 6000.0;

    private const int NoticeMs = 4000;

    private readonly List<IHitInfo> _hits = new List<IHitInfo>();

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.WingCommandResult, OnResult);
    }

    public override void BeforeStart()
    {
        if (!WingAntennaActions.Register(OnPress))
            Plugin.Log("wing actions not registered: " + WingAntennaActions.FailureReason);
    }

    protected override void UnloadData()
    {
        WingAntennaActions.Unregister();
    }

    private static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.WingCommand);
        }
    }

    private void OnPress(IMyTerminalBlock antenna, WireWingCommand command)
    {
        try
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.MultiplayerActive
                || MyAPIGateway.Multiplayer.IsServer)
                return;
            if (!Available)
            {
                MyAPIGateway.Utilities.ShowNotification("Wing commands need a Factions server.", NoticeMs, MyFontEnum.Red);
                return;
            }
            long crosshair = CrosshairGrid(antenna);
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId,
                FactionsWire.WriteWingCommandRequest(antenna.EntityId, command, crosshair), true);
            if (Config.Current.DebugLogging)
                Plugin.Log("WING_COMMAND sent antenna=" + antenna.EntityId + " cmd=" + command + " crosshair=" + crosshair);
        }
        catch (Exception ex)
        {
            Plugin.Log("wing command (" + command + ") failed: " + ex);
        }
    }

    /// <summary>The first grid along the camera's line of sight that is not the presser's own
    /// construct, or zero.</summary>
    private long CrosshairGrid(IMyTerminalBlock antenna)
    {
        IMyCamera camera = MyAPIGateway.Session != null ? MyAPIGateway.Session.Camera : null;
        if (camera == null || MyAPIGateway.Physics == null) return 0;
        MatrixD m = camera.WorldMatrix;
        Vector3D from = m.Translation;
        Vector3D to = from + m.Forward * CrosshairRangeM;
        _hits.Clear();
        MyAPIGateway.Physics.CastRay(from, to, _hits);
        IMyCubeGrid own = antenna.CubeGrid;
        foreach (IHitInfo hit in _hits)
        {
            IMyEntity top = hit.HitEntity != null ? hit.HitEntity.GetTopMostParent() : null;
            var grid = top as IMyCubeGrid;
            if (grid == null) continue;
            if (own != null && (grid == own || grid.IsSameConstructAs(own))) continue;
            return grid.EntityId;
        }
        return 0;
    }

    private void OnResult(byte[] data)
    {
        bool ok;
        WireWingCommand command;
        string message;
        if (!FactionsWire.TryReadWingCommandResult(data, out ok, out command, out message))
        {
            Plugin.Log("WING_COMMAND_RESULT unreadable");
            return;
        }
        if (!string.IsNullOrEmpty(message))
            MyAPIGateway.Utilities.ShowNotification(message, NoticeMs, ok ? MyFontEnum.Green : MyFontEnum.Red);
    }
}
