using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Campaigns;

/// <summary>
/// Owns the campaign surfaces' data: subscribes to the viewing player's own faction's campaign
/// list, holds the decoded rows, and sends the two commands — place a raid or conquest directive
/// on a node, cancel one of the player's own.
///
/// Two screens read it, the map and the list window, and either may open first, so the
/// subscription is reference-counted by intent rather than owned by one screen: each caller says
/// whether it wants the stream and the component sends the union.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class CampaignsComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on screen.</summary>
    internal const double CommandMessageSeconds = 12.0;

    internal const int WantMap = 1;
    internal const int WantWindow = 2;

    internal static CampaignsComponent Instance { get; private set; }

    internal bool Streaming { get; private set; }
    private int _wants;

    /// <summary>Faction tag the last snapshot described. Empty when the player is in no faction
    /// Factions has registered.</summary>
    internal string FactionTag { get; private set; }

    /// <summary><c>FactionTuning.PlayerCampaignCap</c>.</summary>
    internal int Cap { get; private set; }
    internal long RaidCost { get; private set; }
    internal long ConquerCost { get; private set; }

    internal List<WireCampaign> Campaigns { get; private set; } = new List<WireCampaign>();

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so a screen can poll rather than hold a callback.</summary>
    internal int Revision { get; private set; }

    internal string LastCommandMessage { get; private set; }
    internal DateTime LastCommandUtc { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.CampaignsSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.CampaignCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        _wants = 0;
        HasSnapshot = false;
        Campaigns = new List<WireCampaign>();
        Instance = null;
    }

    /// <summary>True when the server has granted the campaign feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Campaigns);
        }
    }

    /// <summary>One surface saying whether it wants the stream. Idempotent and safe every frame.</summary>
    internal void SetWant(int surface, bool want)
    {
        int next = want ? (_wants | surface) : (_wants & ~surface);
        _wants = next;
        SetStreaming(next != 0);
    }

    private void SetStreaming(bool stream)
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
                FactionsWire.ChannelId, FactionsWire.WriteCampaignsRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("campaigns subscribe (" + stream + ") failed: " + ex);
        }
    }

    /// <summary>Place a directive on a node. <paramref name="kind"/> is the wire's: 0 raid,
    /// 1 conquest, 2 defend (the node must be the faction's own). Node-scoped: the region form
    /// exists on the wire and is not offered here yet.</summary>
    internal void SendPlace(string nodeId, byte kind)
    {
        if (!Available || string.IsNullOrEmpty(nodeId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteCampaignCommandRequest(WireCampaignCommand.Place, nodeId,
                    kind, false, ""), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("campaign place failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    internal void SendCancel(string focusId)
    {
        if (!Available || string.IsNullOrEmpty(focusId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteCampaignCommandRequest(WireCampaignCommand.Cancel, "", 0, false, focusId), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("campaign cancel failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    /// <summary>The player-issued campaign targeting <paramref name="nodeId"/>, if any — what the
    /// map marks and what its buttons read to offer Cancel instead of Place.</summary>
    internal bool TryGetDirectiveOn(string nodeId, out WireCampaign campaign)
    {
        campaign = default(WireCampaign);
        if (string.IsNullOrEmpty(nodeId)) return false;
        for (int i = 0; i < Campaigns.Count; i++)
        {
            WireCampaign c = Campaigns[i];
            if (!c.PlayerIssued) continue;
            if (!string.Equals(c.TargetNodeId, nodeId, StringComparison.Ordinal)) continue;
            campaign = c;
            return true;
        }
        return false;
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        int cap;
        long raidCost, conquerCost;
        List<WireCampaign> campaigns;
        if (!FactionsWire.TryReadCampaignsSnapshot(data, out tag, out cap, out raidCost, out conquerCost, out campaigns))
        {
            Plugin.Log("CAMPAIGNS_SNAPSHOT unreadable");
            return;
        }
        FactionTag = tag ?? "";
        Cap = cap;
        RaidCost = raidCost;
        ConquerCost = conquerCost;
        Campaigns = campaigns ?? new List<WireCampaign>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("CAMPAIGNS_SNAPSHOT tag=" + FactionTag + " campaigns=" + Campaigns.Count + " cap=" + Cap);
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message, focusId;
        if (!FactionsWire.TryReadCampaignCommandResult(data, out ok, out message, out focusId))
        {
            Plugin.Log("CAMPAIGN_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message ?? (ok ? "Done." : "Refused.");
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("CAMPAIGN_COMMAND_RESULT ok=" + ok + " focus=" + focusId + " message=" + message);
    }
}
