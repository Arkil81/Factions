using System;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Demand;

/// <summary>
/// Owns the faction Demand window's data: subscribes while the window is open and holds the last
/// snapshot. Its own subscription, for the reason <see cref="Market.MarketComponent"/> has one.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class FactionDemandComponent : MySessionComponentBase
{
    internal static FactionDemandComponent Instance { get; private set; }

    /// <summary>Set optimistically at send time: a subscribe has no reply.</summary>
    internal bool Streaming { get; private set; }

    internal WireFactionDemand Snapshot { get; private set; }
    internal bool HasSnapshot { get; private set; }

    /// <summary>Bumped on every change, so the screen can poll rather than hold a callback.</summary>
    internal int Revision { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.FactionDemandSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Snapshot = new WireFactionDemand();
        Instance = null;
    }

    /// <summary>True once the server has granted the faction demand feature.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.FactionDemand);
        }
    }

    /// <summary>Subscribe or unsubscribe. Idempotent and safe to call every frame, so a window
    /// opened before the uplink was granted starts streaming as soon as it is.</summary>
    internal void SetStreaming(bool stream)
    {
        if (Streaming == stream) return;

        if (!Available)
        {
            if (!stream) Streaming = false;
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteFactionDemandRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("faction demand subscribe (" + stream + ") failed: " + ex);
        }
    }

    private void OnSnapshot(byte[] data)
    {
        WireFactionDemand snapshot;
        if (!FactionsWire.TryReadFactionDemandSnapshot(data, out snapshot))
        {
            Plugin.Log("FACTION_DEMAND_SNAPSHOT unreadable");
            return;
        }

        Snapshot = snapshot;
        HasSnapshot = true;
        Revision++;

        if (Config.Current.DebugLogging)
            Plugin.Log("FACTION_DEMAND_SNAPSHOT open=" + snapshot.Demand.Length + "/" + snapshot.DemandTotal
                       + " ended=" + snapshot.Ended.Length + "/" + snapshot.EndedTotal);
    }
}
