using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Nodes;

/// <summary>
/// The client end of the Build button: what one shipyard may build, and the one-shot build it
/// queues.
///
/// <para>Request/response rather than a subscription, and separate from
/// <see cref="NodeDetailComponent"/> for the reason <see cref="StationRefitComponent"/> records.
/// That component holds the node window's single server-side watch, and this dialog opens over that
/// window: taking the watch would leave the window behind it frozen on whatever it last showed.</para>
///
/// <para><b>Nothing here is decided locally.</b> The hull list, each hull's numbers and whether it
/// can leave this yard are all the server's, and the build itself is re-authorised through the same
/// entry point the chat verb reaches — the pick-list is a convenience, never an authorisation.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class StationBuildComponent : MySessionComponentBase
{
    /// <summary>How long a build's reply stays on the dialog's status line. Deliberately the node
    /// window's own number: the two lines sit one window apart and should not age differently.</summary>
    internal const double ResultMessageSeconds = NodeDetailComponent.CommandMessageSeconds;

    /// <summary>Floor on how often the same node is asked about. The dialog re-asserts its question
    /// from its draw loop, and without this it would ask sixty times a second.</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);

    internal static StationBuildComponent Instance { get; private set; }

    /// <summary>The node the dialog is asking about, or null. Options for any other node are
    /// dropped — the ids are the only thing pairing a reply with its request.</summary>
    internal string NodeId { get; private set; }

    /// <summary>The roles a player may queue, in the server's own order
    /// (<c>ShipyardManager.PlayerQueueRoles</c>). Never derived client-side.</summary>
    internal List<string> Roles { get; private set; } = new List<string>();

    /// <summary>Every hull this yard may build, each carrying which roles it fits — so one flat
    /// list serves a role-filtered picker.</summary>
    internal List<WireBuildPrefab> Prefabs { get; private set; } = new List<WireBuildPrefab>();

    /// <summary>True once the pick-list for <see cref="NodeId"/> has arrived. An empty list with
    /// this set is a real answer ("this yard can build nothing you may queue"), which is why it is
    /// carried separately from the count.</summary>
    internal bool HasOptions { get; private set; }

    /// <summary>Bumped on every change, so the screen can poll rather than hold a callback into
    /// controls it may have already rebuilt.</summary>
    internal int Revision { get; private set; }

    internal string LastResultMessage { get; private set; }
    internal DateTime LastResultUtc { get; private set; }

    private DateTime _lastRequestUtc;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.BuildOptions, OnOptions);
        UplinkComponent.RegisterHandler(WireMessageKind.BuildResult, OnResult);
    }

    protected override void UnloadData()
    {
        NodeId = null;
        HasOptions = false;
        Roles = new List<string>();
        Prefabs = new List<WireBuildPrefab>();
        Instance = null;
    }

    /// <summary>True when the server has granted the Build feature to this client. A server may
    /// grant the node window and withhold this one: this is the surface that spends a station's
    /// components.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Build);
        }
    }

    // ---- Asking -------------------------------------------------------------------------------

    /// <summary>Ask what <paramref name="nodeId"/> may build. Safe to call every frame: a repeat of
    /// the same question is dropped until <see cref="MinRequestInterval"/> has passed.
    /// <paramref name="force"/> is for the re-read after a build, which must not be swallowed as
    /// one.</summary>
    internal void Query(string nodeId, bool force)
    {
        if (string.IsNullOrEmpty(nodeId)) return;

        if (!string.Equals(NodeId, nodeId, StringComparison.Ordinal))
        {
            NodeId = nodeId;
            // The previous yard's hulls must not be offered under the new yard's name — what can
            // fly out of one is not what can fly out of the other.
            HasOptions = false;
            Roles = new List<string>();
            Prefabs = new List<WireBuildPrefab>();
            Revision++;
        }
        else if (!force && DateTime.UtcNow - _lastRequestUtc < MinRequestInterval)
        {
            return;
        }

        if (!Available) return;
        _lastRequestUtc = DateTime.UtcNow;
        Send(FactionsWire.WriteBuildOptionsRequest(nodeId), "build options request");
    }

    /// <summary>Queues one hull at one yard. The server re-resolves the caller's faction, the node's
    /// ownership, its Shipyard and the hull; nothing here is trusted.</summary>
    internal void SendBuild(string nodeId, string prefabId)
    {
        if (!Available || string.IsNullOrEmpty(nodeId)) return;
        Send(FactionsWire.WriteBuildRequest(nodeId, prefabId ?? ""), "build request");
    }

    private void Send(byte[] payload, string what)
    {
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, payload, true);
        }
        catch (Exception ex)
        {
            Plugin.Log(what + " failed: " + ex);
            LastResultMessage = "Could not reach the server.";
            LastResultUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    // ---- Replies ------------------------------------------------------------------------------

    private void OnOptions(byte[] data)
    {
        string nodeId;
        List<string> roles;
        List<WireBuildPrefab> prefabs;
        if (!FactionsWire.TryReadBuildOptions(data, out nodeId, out roles, out prefabs))
        {
            Plugin.Log("BUILD_OPTIONS unreadable");
            return;
        }
        // A reply in flight when the player changed station names the yard it was asked about, not
        // the one the dialog is on now.
        if (!string.Equals(nodeId, NodeId, StringComparison.Ordinal)) return;

        Roles = roles ?? new List<string>();
        Prefabs = prefabs ?? new List<WireBuildPrefab>();
        HasOptions = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("BUILD_OPTIONS node=" + nodeId + " roles=" + Roles.Count + " prefabs=" + Prefabs.Count);
    }

    private void OnResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadBuildResult(data, out ok, out message))
        {
            Plugin.Log("BUILD_RESULT unreadable");
            return;
        }
        LastResultMessage = message;
        LastResultUtc = DateTime.UtcNow;
        Revision++;
    }
}
