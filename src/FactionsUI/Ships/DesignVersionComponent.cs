using System;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Ships;

/// <summary>
/// Client half of the scan dialogs' "Publish as" choice: asks the server which design a selected
/// hull or this terminal's station is, and keeps the last answer for the dialog to draw. The answer
/// is advice only — the scan the dialog then sends is judged on its own when it arrives.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class DesignVersionComponent : MySessionComponentBase
{
    internal static DesignVersionComponent Instance { get; private set; }

    /// <summary>The last answer, and whether one has arrived since the last <see cref="Query"/>.</summary>
    internal WireDesignVersionInfo Info { get; private set; }
    internal bool HasInfo { get; private set; }

    /// <summary>Bumped on every answer, so a dialog redraws once per change.</summary>
    internal int Revision { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.DesignVersionInfo, OnInfo);
    }

    protected override void UnloadData()
    {
        HasInfo = false;
        Instance = null;
    }

    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.DesignVersions);
        }
    }

    /// <summary>Asks about one hull (<paramref name="gridEntityId"/>, with
    /// <paramref name="station"/> false) or this terminal's station (grid 0, station true).</summary>
    internal void Query(long terminalEntityId, long gridEntityId, bool station)
    {
        HasInfo = false;
        Revision++;
        if (!Available || terminalEntityId == 0) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteDesignVersionQuery(terminalEntityId, gridEntityId, station), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("design version query failed: " + ex);
        }
    }

    private void OnInfo(byte[] data)
    {
        WireDesignVersionInfo info;
        if (!FactionsWire.TryReadDesignVersionInfo(data, out info))
        {
            Plugin.Log("DESIGN_VERSION_INFO unreadable");
            return;
        }
        Info = info;
        HasInfo = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("DESIGN_VERSION_INFO entity=" + info.EntityId + " current=" + info.CurrentId
                       + " offered=" + info.Offered + " next=" + info.NextId);
    }
}
