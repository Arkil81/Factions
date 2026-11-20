using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// The client end of a node's own stock: what a player standing at one of their faction's nodes may
/// move, and the requests that move it.
///
/// <para>Request/response rather than a subscription, and separate from
/// <see cref="NodeDetailComponent"/> on purpose. That component holds the node window's single
/// server-side watch; the store block — which knows a station and nothing about the node graph —
/// asks a question of its own and must not take that watch away from a window open behind it.</para>
///
/// <para><b>Nothing here is decided locally.</b> Whether the controls appear at all is the server's
/// <see cref="MayTransfer"/>; the withdrawable number is the server's
/// <see cref="WireNodeTransferRow.Available"/> with every reserve already deducted; and the listing
/// is re-read from the server after each transfer rather than adjusted here, so what the player sees
/// is what the node actually holds even when a build or another member moved it in between.</para>
///
/// <para><b>One transfer at a time, one target entity per request.</b> The wire carries a single
/// target entity id and the result carries no correlation of its own, so spreading a move across a
/// ship's containers is run here as a sequence: each leg asks for what is still outstanding, the
/// server moves what actually fits there, and the next leg asks for the remainder. A leg that moves
/// nothing still advances, so a hold that is full or an inventory that turned out to be empty ends
/// the run rather than repeating it.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class NodeStockComponent : MySessionComponentBase
{
    /// <summary>How close the player has to be for the buttons to be offered, mirroring the
    /// distance the server re-checks. A courtesy only: the server refuses out-of-range requests on
    /// its own, so a disagreement here shows up as a refusal message, never as a move that should
    /// not have happened.</summary>
    internal const double TransferRangeKm = 5.0;

    /// <summary>How long a transfer's reply line stays on screen. The listing behind it is the
    /// authoritative answer; the line only says what the button did.</summary>
    internal const double ResultMessageSeconds = 12.0;

    /// <summary>A refused request is answered, but a request lost to a disconnect is not, and one
    /// unanswered leg would otherwise leave the buttons disabled for the rest of the session.</summary>
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Floor on how often the same question is asked. Both surfaces poll from their draw
    /// loop, and without this the store screen would ask sixty times a second.</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);

    internal static NodeStockComponent Instance { get; private set; }

    /// <summary>The node the server resolved the current query to. Empty until a snapshot arrives —
    /// the store block asks by station and is told which node that is.</summary>
    internal string NodeId { get; private set; }

    internal string NodeName { get; private set; }

    /// <summary>The server's verdict on whether this viewer may move anything here at all. The only
    /// thing the tabs and buttons are shown on.</summary>
    internal bool MayTransfer { get; private set; }

    /// <summary>Why not, when <see cref="MayTransfer"/> is false. Shown instead of an empty list,
    /// which would read as a station holding nothing.</summary>
    internal string Message { get; private set; }

    internal WireNodeTransferRow[] Rows { get; private set; }

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so a screen can poll rather than hold a callback into
    /// controls it may have already rebuilt.</summary>
    internal int Revision { get; private set; }

    internal string LastResultMessage { get; private set; }
    internal DateTime LastResultUtc { get; private set; }

    private long _queryStationId;
    private string _queryNodeId = "";
    private DateTime _lastRequestUtc;

    private List<long> _legs;
    private int _leg;
    private string _transferNodeId;
    private string _transferKey;
    private bool _transferDeposit;
    private long _transferAsked;
    private long _transferMoved;
    private DateTime _legSentUtc;

    /// <summary>True while a transfer's legs are still going out. The buttons are held down for the
    /// whole run, not per leg: a second transfer started mid-run would answer into the first one's
    /// tally.</summary>
    internal bool IsBusy { get { return _legs != null; } }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.NodeStockSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.NodeTransferResult, OnTransferResult);
    }

    protected override void UnloadData()
    {
        Instance = null;
        _legs = null;
        HasSnapshot = false;
        Rows = null;
    }

    /// <summary>True when the server has granted the node-inventory feature to this client. A server
    /// may grant the node window and withhold this one.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.NodeInventory);
        }
    }

    /// <summary>
    /// Whether the player is close enough to this node for the transfer controls to be worth
    /// offering.
    ///
    /// <para>An enable rule, not a permission: the server re-checks the distance from the sender's
    /// own position and refuses anything out of range, so the two disagreeing shows up as a refusal
    /// message rather than as a move that should not have happened.</para>
    /// </summary>
    internal static bool InRangeOf(double xKm, double yKm, double zKm)
    {
        try
        {
            if (MySession.Static == null) return false;
            MyEntity anchor = MySession.Static.TopMostControlledEntity;
            Vector3D here;
            if (anchor != null && anchor.PositionComp != null)
            {
                here = anchor.PositionComp.GetPosition();
            }
            else
            {
                MyCharacter character = MySession.Static.LocalCharacter;
                if (character == null || character.PositionComp == null) return false;
                here = character.PositionComp.GetPosition();
            }

            double dx = here.X - xKm * 1000.0;
            double dy = here.Y - yKm * 1000.0;
            double dz = here.Z - zKm * 1000.0;
            double range = TransferRangeKm * 1000.0;
            return dx * dx + dy * dy + dz * dz <= range * range;
        }
        catch
        {
            return false;
        }
    }

    // ---- Asking -------------------------------------------------------------------------------

    /// <summary>
    /// Ask what may be moved. Exactly one identifier is meaningful, matching the wire: a station for
    /// the store block, a node id for the node window.
    ///
    /// <para><paramref name="stationId"/> is what the client can resolve from the block's grid —
    /// the <c>MyFactionStation.Id</c> when the game knows one, and the grid's own entity id when it
    /// does not. Both are values the server's node model may be carrying, so a server that misses on
    /// one should try the other rather than answer "no such station".</para>
    ///
    /// <para>Safe to call every frame: repeats of the same question are dropped until
    /// <see cref="MinRequestInterval"/> has passed, and <paramref name="force"/> is for the re-read
    /// after a transfer, which must not be swallowed as one.</para>
    /// </summary>
    internal void Query(long stationId, string nodeId, bool force)
    {
        string wanted = nodeId ?? "";
        bool changed = stationId != _queryStationId
                       || !string.Equals(wanted, _queryNodeId, StringComparison.Ordinal);
        if (changed)
        {
            _queryStationId = stationId;
            _queryNodeId = wanted;
            // The previous node's rows must not be shown under the new node's name.
            HasSnapshot = false;
            Rows = null;
            NodeId = null;
            NodeName = null;
            MayTransfer = false;
            Message = null;
            Revision++;
        }
        else if (!force && DateTime.UtcNow - _lastRequestUtc < MinRequestInterval)
        {
            return;
        }

        if (!Available) return;

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteNodeStockRequest(stationId, wanted), true);
            _lastRequestUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log("node stock request failed: " + ex);
        }
    }

    /// <summary>
    /// Whether what is held here answers this caller's question rather than another surface's.
    ///
    /// <para>Two surfaces can be up at once — a store block in front of a transfer window opened
    /// from the node window — and there is one set of state. A caller that finds the answer is not
    /// its own must show nothing at all: a snapshot saying "yours" about a node somewhere else,
    /// read as though it were about this station, would put the transfer tabs on an NPC store
    /// block, which is precisely the failure this feature has to be unable to produce.</para>
    /// </summary>
    internal bool IsQueryFor(long stationId, string nodeId)
    {
        return stationId == _queryStationId
               && string.Equals(nodeId ?? "", _queryNodeId, StringComparison.Ordinal);
    }

    /// <summary>Drops the current question so the next surface to open starts from nothing rather
    /// than from whatever the last one was looking at.</summary>
    internal void Clear()
    {
        _queryStationId = 0;
        _queryNodeId = "";
        HasSnapshot = false;
        Rows = null;
        NodeId = null;
        NodeName = null;
        MayTransfer = false;
        Message = null;
        Revision++;
    }

    private void OnSnapshot(byte[] data)
    {
        string nodeId, nodeName, message;
        bool mayTransfer;
        WireNodeTransferRow[] rows;
        if (!FactionsWire.TryReadNodeStockSnapshot(
                data, out nodeId, out nodeName, out mayTransfer, out message, out rows))
        {
            Plugin.Log("NODE_STOCK unreadable");
            return;
        }

        // A snapshot in flight when the player moved to another node would otherwise be shown under
        // the new one's name. Only checkable for a query that named a node: the store block asks by
        // station and is being TOLD the node id. A refusal carries no node id and is always kept —
        // it is the only thing that explains an empty window.
        if (_queryNodeId.Length > 0 && nodeId.Length > 0
            && !string.Equals(nodeId, _queryNodeId, StringComparison.Ordinal)) return;

        NodeId = nodeId;
        NodeName = nodeName;
        MayTransfer = mayTransfer;
        Message = message;
        Rows = rows ?? new WireNodeTransferRow[0];
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;

        if (Config.Current.DebugLogging)
        {
            Plugin.Log("NODE_STOCK node=" + (nodeId.Length == 0 ? "(none)" : nodeId)
                       + " may=" + mayTransfer + " rows=" + Rows.Length);
        }
    }

    // ---- Moving -------------------------------------------------------------------------------

    /// <summary>
    /// Start a transfer of one key. <paramref name="targets"/> is tried in order, each leg asking
    /// for whatever the earlier ones did not move, so a single ask can drain into — or out of — a
    /// whole ship's worth of containers without the client ever deciding how much fits where.
    ///
    /// <para>False means nothing was sent, and the caller's own status line should say so; there is
    /// no reply coming for a request that never left.</para>
    /// </summary>
    internal bool BeginTransfer(bool deposit, string invKey, long amount, List<long> targets)
    {
        if (!Available || IsBusy) return false;
        if (!MayTransfer || string.IsNullOrEmpty(NodeId)) return false;
        if (string.IsNullOrEmpty(invKey) || amount <= 0) return false;
        if (targets == null || targets.Count == 0) return false;

        _legs = targets;
        _leg = 0;
        _transferNodeId = NodeId;
        _transferKey = invKey;
        _transferDeposit = deposit;
        _transferAsked = amount;
        _transferMoved = 0;
        LastResultMessage = null;
        Revision++;

        if (SendLeg()) return true;

        // Nothing went out, so the run must not be left holding the single in-flight slot.
        _legs = null;
        Revision++;
        return false;
    }

    private bool SendLeg()
    {
        long remaining = _transferAsked - _transferMoved;
        if (remaining <= 0 || _legs == null || _leg >= _legs.Count) return false;

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteNodeTransferRequest(
                    _transferNodeId, _transferDeposit, _transferKey, remaining, _legs[_leg]),
                true);
            _legSentUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log("node transfer request failed: " + ex);
            return false;
        }
    }

    private void OnTransferResult(byte[] data)
    {
        bool ok;
        long moved;
        string message;
        if (!FactionsWire.TryReadNodeTransferResult(data, out ok, out moved, out message))
        {
            Plugin.Log("NODE_TRANSFER_RESULT unreadable");
            return;
        }

        if (!IsBusy)
        {
            // A late reply to a run that already timed out. Still shown: it is the server's own
            // account of goods that did move, and swallowing it would leave the player with a
            // timeout message and a changed hold.
            LastResultMessage = message;
            LastResultUtc = DateTime.UtcNow;
            Revision++;
            Query(_queryStationId, _queryNodeId, force: true);
            return;
        }

        _transferMoved += moved > 0 ? moved : 0;
        if (!ok)
        {
            Finish(message);
            return;
        }

        // Advanced even when the leg moved nothing: a full hold or an empty container is a legal
        // answer, and asking it again would be a loop rather than a retry.
        _leg++;
        if (_transferMoved >= _transferAsked || _legs == null || _leg >= _legs.Count || !SendLeg())
            Finish(null);
    }

    public override void UpdateAfterSimulation()
    {
        if (!IsBusy) return;
        if (DateTime.UtcNow - _legSentUtc < LegTimeout) return;
        Finish("The server did not answer.");
    }

    /// <summary>Ends the run and re-reads the listing. The amount moved is reported whatever the
    /// verdict: a refusal that arrives after two legs have already landed is still a changed
    /// hold.</summary>
    private void Finish(string refusal)
    {
        bool deposit = _transferDeposit;
        long asked = _transferAsked;
        long moved = _transferMoved;
        string key = _transferKey;
        _legs = null;

        string verb = deposit ? "Deposited " : "Withdrew ";
        if (string.IsNullOrEmpty(refusal))
        {
            LastResultMessage = verb + moved + " of " + asked + " " + key + ".";
        }
        else
        {
            LastResultMessage = moved > 0
                ? refusal + " (" + verb.ToLowerInvariant().Trim() + " " + moved + " first)"
                : refusal;
        }
        LastResultUtc = DateTime.UtcNow;
        Revision++;

        // The server's own numbers, never a locally decremented guess.
        Query(_queryStationId, _queryNodeId, force: true);
    }
}
