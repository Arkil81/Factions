using System;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Nodes;

/// <summary>
/// Owns the per-node window's data: asks the server to watch one node, holds the decoded detail,
/// and sends the window's queue commands.
///
/// It has its own subscription rather than reading either existing cache, and that is the whole
/// point of it. <see cref="Map.MapComponent"/>'s world refreshes only while the map window is open,
/// and <see cref="Assets.AssetsComponent"/>'s register stops the moment the player leaves the
/// Assets tab — so a window opened from either one and left open would be reading state that
/// stopped moving behind it. This outlives both screens and both subscriptions.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class NodeDetailComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on screen before the window stops showing it. The
    /// snapshot behind it is the authoritative answer; the line is only there to say what the
    /// button did.</summary>
    internal const double CommandMessageSeconds = 12.0;

    internal static NodeDetailComponent Instance { get; private set; }

    /// <summary>The node this client has asked the server to watch, or null. Set optimistically at
    /// send time: the server has no reply for a subscribe, and re-sending on every frame would be
    /// the alternative.</summary>
    internal string WatchedNodeId { get; private set; }

    /// <summary>Last snapshot decoded. Only meaningful when <see cref="HasSnapshot"/> is true, and
    /// only about <see cref="WatchedNodeId"/> — a snapshot for a node the window has since moved
    /// off is dropped rather than shown.</summary>
    internal WireNodeDetail Detail;

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so the screen can poll rather than hold a callback into
    /// controls it may have already rebuilt.</summary>
    internal int Revision { get; private set; }

    internal string LastCommandMessage { get; private set; }
    internal DateTime LastCommandUtc { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.NodeDetailSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.NodeCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        WatchedNodeId = null;
        HasSnapshot = false;
        Instance = null;
    }

    /// <summary>True when the server has granted the node-detail feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.NodeDetail);
        }
    }

    /// <summary>
    /// Ask the server to watch <paramref name="nodeId"/>, or null to stop watching. Idempotent and
    /// safe to call every frame: the screen calls it repeatedly while it is up, so a window opened
    /// before the uplink was granted starts streaming as soon as it is.
    /// </summary>
    internal void Watch(string nodeId)
    {
        if (string.Equals(WatchedNodeId, nodeId, StringComparison.Ordinal)) return;

        if (!Available)
        {
            // Nothing to unwatch if the link is gone; drop the local id so a later watch is not
            // swallowed as a no-op.
            if (nodeId == null) WatchedNodeId = null;
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteNodeDetailRequest(nodeId ?? ""), true);
            WatchedNodeId = nodeId;
            // The previous node's detail must not be shown under the new node's name.
            HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("node detail watch (" + (nodeId ?? "none") + ") failed: " + ex);
            // Leave WatchedNodeId alone: the send did not happen, so the next call must retry.
        }
    }

    /// <summary>Sends one queue command for the watched node. The server re-authorises it against
    /// the sender's own faction; nothing here is trusted.</summary>
    internal void SendCommand(WireNodeCommand command, string entryId, int value)
    {
        if (!Available || string.IsNullOrEmpty(WatchedNodeId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteNodeCommandRequest(WatchedNodeId, command, entryId ?? "", value), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("node command (" + command + ") failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        WireNodeDetail detail;
        if (!FactionsWire.TryReadNodeDetailSnapshot(data, out detail))
        {
            Plugin.Log("NODE_DETAIL unreadable");
            return;
        }
        // A snapshot in flight when the player changed node would otherwise be shown against the
        // new one; the ids are the only thing that pairs a reply with its request.
        if (!string.Equals(detail.Id, WatchedNodeId, StringComparison.Ordinal)) return;

        Detail = detail;
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("NODE_DETAIL node=" + detail.Id + " exists=" + detail.Exists
                       + " owned=" + detail.Owned
                       + " stock=" + (detail.Stock == null ? 0 : detail.Stock.Length)
                       + " builds=" + (detail.Builds == null ? 0 : detail.Builds.Length));
        }
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadNodeCommandResult(data, out ok, out message))
        {
            Plugin.Log("NODE_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
