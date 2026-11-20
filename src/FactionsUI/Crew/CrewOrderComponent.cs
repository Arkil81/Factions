using System;
using Factions;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;

namespace FactionsUI.Crew;

/// <summary>
/// The client end of the crew orders on seats (docs/ground-operations.md): registers
/// <i>Crew: mount up … automatic</i> on every seat's toolbar list through the shared
/// <see cref="CrewSeatActions"/>, and turns a press into a
/// <see cref="WireMessageKind.CrewOrderRequest"/> naming the seat. The server finds the hull,
/// checks the presser commands it, and answers for the HUD.
///
/// <para>The actions are registered whatever the server, so a slot keeps resolving across servers
/// and sessions; a press on a server that did not grant <see cref="WireFeatures.CrewOrders"/> says
/// so and sends nothing.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class CrewOrderComponent : MySessionComponentBase
{
    private const int NoticeMs = 4000;

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.CrewOrderResult, OnResult);
    }

    public override void BeforeStart()
    {
        if (!CrewSeatActions.Register(OnPress))
            Plugin.Log("crew order actions not registered: " + CrewSeatActions.FailureReason);
    }

    protected override void UnloadData()
    {
        CrewSeatActions.Unregister();
    }

    private static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.CrewOrders);
        }
    }

    private void OnPress(IMyTerminalBlock seat, WireCrewOrder order)
    {
        try
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.MultiplayerActive
                || MyAPIGateway.Multiplayer.IsServer)
                return;
            if (!Available)
            {
                MyAPIGateway.Utilities.ShowNotification("Crew orders need a Factions server.", NoticeMs, MyFontEnum.Red);
                return;
            }
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId,
                FactionsWire.WriteCrewOrderRequest(seat.EntityId, order), true);
            if (Config.Current.DebugLogging)
                Plugin.Log("CREW_ORDER sent seat=" + seat.EntityId + " order=" + order);
        }
        catch (Exception ex)
        {
            Plugin.Log("crew order (" + order + ") failed: " + ex);
        }
    }

    private void OnResult(byte[] data)
    {
        bool ok;
        WireCrewOrder order;
        string message;
        if (!FactionsWire.TryReadCrewOrderResult(data, out ok, out order, out message))
        {
            Plugin.Log("CREW_ORDER_RESULT unreadable");
            return;
        }
        if (!string.IsNullOrEmpty(message))
            MyAPIGateway.Utilities.ShowNotification(message, NoticeMs, ok ? MyFontEnum.Green : MyFontEnum.Red);
    }
}
