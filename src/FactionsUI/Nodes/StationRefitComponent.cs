using System;
using System.Collections.Generic;
using Factions.Wire;
using FactionsUI.Ships;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Nodes;

/// <summary>
/// The client end of station refit: what one founded node could be rebuilt as, and the four actions
/// that act on it.
///
/// <para>Request/response rather than a subscription, and separate from
/// <see cref="NodeDetailComponent"/> on purpose — the reason <see cref="NodeStockComponent"/>
/// records. That component holds the node window's single server-side watch, and the refit panel
/// opens over that window: taking the watch would leave the window behind it frozen on whatever it
/// last showed.</para>
///
/// <para><b>Nothing here is decided locally.</b> The candidate list, each design's net bill, the
/// site match and the siege gate are all the server's; the panel's buttons are enabled by selection
/// alone and a refused action comes back as a sentence. The scan is the same: this collects a name
/// and a scope, and the server re-resolves the terminal, the caller's distance and the design's
/// acceptance rules itself.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class StationRefitComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on the panel's status line. Deliberately the node
    /// window's own number: the two lines sit one window apart and should not age differently.</summary>
    internal const double CommandMessageSeconds = NodeDetailComponent.CommandMessageSeconds;

    /// <summary>Floor on how often the same node is asked about. The panel polls from its draw
    /// loop, and without this it would ask sixty times a second.</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);

    internal static StationRefitComponent Instance { get; private set; }

    /// <summary>The node the panel is asking about, or null. A snapshot for any other node is
    /// dropped — the ids are the only thing pairing a reply with its request.</summary>
    internal string NodeId { get; private set; }

    internal WireStationRefit Refit;
    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so a screen can poll rather than hold a callback into
    /// controls it may have already rebuilt.</summary>
    internal int Revision { get; private set; }

    internal string LastCommandMessage { get; private set; }

    /// <summary>The node the last reply was about. Stamped rather than assumed current: a refusal
    /// naming one node must not be shown on the panel after it has been reopened on another, and the
    /// message outlives the window it was raised in for <see cref="CommandMessageSeconds"/>.</summary>
    internal string LastCommandNodeId { get; private set; }

    internal DateTime LastCommandUtc { get; private set; }

    private DateTime _lastRequestUtc;

    /// <summary>One entry per command still waiting for its answer, in the order it was sent. The
    /// Services Terminal's scan shares the single command result with the panel's three verbs, and
    /// the two answers belong in different places — a scan's in a message box over the terminal, a
    /// panel verb's on the status line of the node it named. The server answers every readable
    /// command exactly once on one reliable channel, so send order pairs them.</summary>
    private readonly Queue<PendingCommand> _pending = new Queue<PendingCommand>();

    /// <summary>Bound on the queue above. Only an answer the server never sent can leave an entry
    /// behind, and dropping the oldest keeps a one-off from misrouting every later reply.</summary>
    private const int MaxPending = 8;

    /// <summary>What one outstanding command was, so its reply can be routed. <see cref="NodeId"/>
    /// is empty for a scan, which names a terminal rather than a node.</summary>
    private struct PendingCommand
    {
        public bool Scan;
        public string NodeId;
    }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.StationRefitSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.StationRefitCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        NodeId = null;
        HasSnapshot = false;
        _pending.Clear();
        Instance = null;
    }

    /// <summary>True when the server has granted the station-refit feature to this client. A server
    /// may grant the node window and withhold this one: this is the surface that rebuilds a live
    /// station grid.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.StationRefit);
        }
    }

    // ---- Asking -------------------------------------------------------------------------------

    /// <summary>
    /// Ask what <paramref name="nodeId"/> could be rebuilt as. Safe to call every frame: a repeat of
    /// the same question is dropped until <see cref="MinRequestInterval"/> has passed.
    /// <paramref name="force"/> is for the re-read after a command, which must not be swallowed as
    /// one.
    /// </summary>
    internal void Query(string nodeId, bool force)
    {
        if (string.IsNullOrEmpty(nodeId)) return;

        if (!string.Equals(NodeId, nodeId, StringComparison.Ordinal))
        {
            NodeId = nodeId;
            // The previous node's designs must not be shown under the new node's name.
            HasSnapshot = false;
            Refit = new WireStationRefit();
            Revision++;
        }
        else if (!force && DateTime.UtcNow - _lastRequestUtc < MinRequestInterval)
        {
            return;
        }

        if (!Available) return;
        _lastRequestUtc = DateTime.UtcNow;
        Send(FactionsWire.WriteStationRefitRequest(nodeId), "station refit request");
    }

    /// <summary>Queue, force or apply against the node the panel is showing. The server re-authorises
    /// it against the sender's own faction; nothing here is trusted.</summary>
    internal void SendCommand(WireStationRefitCommand command, string prefabId)
    {
        if (!Available || string.IsNullOrEmpty(NodeId)) return;
        string node = NodeId;
        if (Send(FactionsWire.WriteStationRefitCommandRequest(
                     node, command, prefabId ?? "", 0L, "", ""),
                 "station refit command"))
        {
            Expect(new PendingCommand { Scan = false, NodeId = node });
        }
    }

    /// <summary>Scan the station the player is standing in, from its Services Terminal. Named by the
    /// terminal's entity id and no node: the server resolves which node that terminal stands on, and
    /// refuses if it is not one of the caller's.</summary>
    internal void RequestStationScan(long terminalEntityId, string scope, string name)
    {
        if (!Available || terminalEntityId == 0) return;
        if (Send(FactionsWire.WriteStationRefitCommandRequest(
                     "", WireStationRefitCommand.Scan, "", terminalEntityId, scope ?? "", name ?? ""),
                 "station scan request"))
        {
            Expect(new PendingCommand { Scan = true, NodeId = "" });
        }
    }

    /// <summary>Records a command whose answer has not arrived yet. Only reached once the send
    /// itself succeeded: a command that never left must not consume the next reply.</summary>
    private void Expect(PendingCommand command)
    {
        while (_pending.Count >= MaxPending) _pending.Dequeue();
        _pending.Enqueue(command);
    }

    /// <summary>True when the payload went out. False is the send failing, which is reported on the
    /// panel's status line against the node the panel is on.</summary>
    private bool Send(byte[] payload, string what)
    {
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, payload, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log(what + " failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandNodeId = NodeId;
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
            return false;
        }
    }

    // ---- Replies ------------------------------------------------------------------------------

    private void OnSnapshot(byte[] data)
    {
        WireStationRefit refit;
        if (!FactionsWire.TryReadStationRefitSnapshot(data, out refit))
        {
            Plugin.Log("STATION_REFIT unreadable");
            return;
        }
        // A snapshot pushed after a scan names the terminal's node, which is not necessarily the one
        // the panel asked about — and a snapshot in flight when the player changed node would
        // otherwise be shown against the new one.
        if (!string.Equals(refit.NodeId, NodeId, StringComparison.Ordinal)) return;

        Refit = refit;
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("STATION_REFIT node=" + refit.NodeId + " may=" + refit.MayRefit
                       + " designs=" + (refit.Candidates == null ? 0 : refit.Candidates.Length)
                       + " current=" + refit.CurrentPrefabId);
        }
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadStationRefitCommandResult(data, out ok, out message))
        {
            Plugin.Log("STATION_REFIT_RESULT unreadable");
            return;
        }

        PendingCommand pending = _pending.Count > 0
            ? _pending.Dequeue()
            // Nothing outstanding: an answer to a command this client did not send, or one whose
            // record was dropped. The panel's own node is the only place left for it.
            : new PendingCommand { Scan = false, NodeId = NodeId };

        if (pending.Scan)
        {
            // The scan's answer belongs over the terminal it was sent from, in the same box the ship
            // scan's answer appears in — verbatim, because every refusal on this path is written to
            // be read by a player.
            TerminalShipAssetsComponent.ShowMessage(ok ? "SCAN STATION" : "SCAN REFUSED", message);
            return;
        }

        LastCommandMessage = message;
        LastCommandNodeId = pending.NodeId;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
        // The panel is request/response, so the effect of the command is only visible once it asks
        // again. The server pushes one too; this covers the case where it could not.
        Query(NodeId, force: true);
    }
}
