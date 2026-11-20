using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Nodes;

/// <summary>
/// Owns the station Orders window's data: subscribes to the viewing player's own faction standing
/// orders (Mine and Stock), holds the decoded rows and pick-lists, and sends the window's actions.
///
/// <para>The list it subscribes to is <b>faction-wide</b>, not per station: a Mine order is anchored
/// at a hub, but the list is one list and the server sends all of it. The window filters to the
/// station it was opened on. A per-station subscription would cost a snapshot each and would tell
/// the server something it does not need to know.</para>
///
/// <para>It has its own subscription rather than reading the node window's, for the reason
/// <see cref="StationRefitComponent"/> records: <see cref="NodeDetailComponent"/> holds this
/// client's single server-side node watch, and taking it would freeze the window this dialog was
/// opened from on whatever it last showed.</para>
///
/// <para>This was <c>Orders.StandingOrdersComponent</c>, the server half of a faction-wide
/// standing-order window. That window is retired (docs/fleet-doctrine-plan.md, decision 4): fleet
/// composition is the Fleet window's, and what is left here — recurring mining around a hub — is
/// about one station and lives on that station's window.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class StationMineComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on screen. The snapshot behind it is the
    /// authoritative answer; the line only says what the button did.</summary>
    internal const double CommandMessageSeconds = 12.0;

    internal static StationMineComponent Instance { get; private set; }

    /// <summary>Whether this client has asked the server to stream the list. Set optimistically at
    /// send time: a subscribe has no reply, and the alternative is re-sending every frame.</summary>
    internal bool Streaming { get; private set; }

    /// <summary>Faction tag the last snapshot described. Empty when the player is in no faction, or
    /// in one Factions has not registered as a player faction — a real answer, not a missing one.</summary>
    internal string FactionTag { get; private set; }

    /// <summary><c>FactionTuning.StandingOrderCap</c>, so the window can show a faction-wide "3/32"
    /// beside the orders at this station and a player can tell a full list from a short one.</summary>
    internal int Cap { get; private set; }

    /// <summary>Every Mine order the faction holds, at every hub. The window shows the ones anchored
    /// at the station it was opened on.</summary>
    internal List<WireStandingOrder> Orders { get; private set; } = new List<WireStandingOrder>();

    /// <summary>Nodes this faction could anchor an order at, flagged by what each is eligible for
    /// (<see cref="WireNodeOptionFlags"/>). Resolved server-side; the client has no graph.</summary>
    internal List<WireOrderOption> NodeOptions { get; private set; } = new List<WireOrderOption>();

    /// <summary>True once the pick-list has arrived. The create dialog uses it to say whether this
    /// station can anchor a mine order at all.</summary>
    internal bool HasOptions { get; private set; }

    /// <summary>Every item a Stock order may name, sorted by category then name, each with who could
    /// supply it. Arrives once per subscribe beside <see cref="NodeOptions"/>.</summary>
    internal List<WireStockItem> StockItems { get; private set; } = new List<WireStockItem>();

    internal bool HasStockItems { get; private set; }

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
        UplinkComponent.RegisterHandler(WireMessageKind.StandingOrdersSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.StandingOrderCommandResult, OnCommandResult);
        UplinkComponent.RegisterHandler(WireMessageKind.StandingOrderOptions, OnOptions);
        UplinkComponent.RegisterHandler(WireMessageKind.StockItemOptions, OnStockItems);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        HasOptions = false;
        HasStockItems = false;
        Orders = new List<WireStandingOrder>();
        NodeOptions = new List<WireOrderOption>();
        StockItems = new List<WireStockItem>();
        Instance = null;
    }

    /// <summary>True when the server has granted the standing-order feature to this client. A server
    /// may withhold it while still granting the read-only surfaces, because this one mutates.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.StandingOrders);
        }
    }

    /// <summary>Whether <paramref name="nodeId"/> can anchor a Mine order at all, by the server's
    /// own eligibility flag. False while the pick-list has not arrived — the window says which of
    /// the two it is rather than showing a dead button.</summary>
    internal bool IsMineHub(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return false;
        for (int i = 0; i < NodeOptions.Count; i++)
        {
            if (!string.Equals(NodeOptions[i].Id, nodeId, StringComparison.Ordinal)) continue;
            return (NodeOptions[i].Flags & (int)WireNodeOptionFlags.MineHub) != 0;
        }
        return false;
    }

    /// <summary>
    /// Subscribe or unsubscribe. Idempotent and safe to call every frame: the screen calls it while
    /// it is up, so a window opened before the uplink was granted starts streaming as soon as it is.
    /// </summary>
    internal void SetStreaming(bool stream)
    {
        if (Streaming == stream) return;

        if (!Available)
        {
            // Nothing to unsubscribe from if the link is gone; drop the local flag so a later
            // subscribe is not swallowed as a no-op.
            if (!stream) Streaming = false;
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteStandingOrdersRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("mine orders subscribe (" + stream + ") failed: " + ex);
            // Leave Streaming alone: the send did not happen, so the next call must retry.
        }
    }

    /// <summary>Sends one action against one order. The server re-resolves the caller's faction and
    /// the order within it; nothing here is trusted.</summary>
    internal void SendCommand(WireStandingOrderCommand command, string orderId, int value)
    {
        if (!Available || string.IsNullOrEmpty(orderId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteStandingOrderCommandRequest(orderId, command, value), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("mine order command (" + command + ") failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    /// <summary>Sends a create request. Every field is re-validated server-side through the same
    /// entry point the chat verb uses — the pick-list this dialog was built from is a convenience,
    /// not an authorisation.</summary>
    internal void SendCreate(string hubNodeId, double periodSeconds, string oreWeights)
    {
        if (!Available) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteStandingOrderCreateRequest(
                    (byte)0, hubNodeId, periodSeconds, oreWeights), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("mine order create failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    /// <summary>Sends a Stock-order create: keep <paramref name="quantity"/> of
    /// <paramref name="itemKey"/> at <paramref name="nodeId"/>. Validated server-side like a Mine
    /// create.</summary>
    internal void SendCreateStock(string nodeId, string itemKey, long quantity)
    {
        if (!Available) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteStandingOrderCreateRequest(
                    (byte)1, nodeId, 0, "", itemKey, quantity), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("stock order create failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnStockItems(byte[] data)
    {
        List<WireStockItem> items;
        if (!FactionsWire.TryReadStockItemOptions(data, out items))
        {
            Plugin.Log("STOCK_ITEMS unreadable");
            return;
        }
        StockItems = items ?? new List<WireStockItem>();
        HasStockItems = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("STOCK_ITEMS items=" + StockItems.Count);
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        int cap;
        List<WireStandingOrder> orders;
        if (!FactionsWire.TryReadStandingOrdersSnapshot(data, out tag, out cap, out orders))
        {
            Plugin.Log("ORDERS_SNAPSHOT unreadable");
            return;
        }

        FactionTag = tag ?? "";
        Cap = cap;
        Orders = orders ?? new List<WireStandingOrder>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("ORDERS_SNAPSHOT tag=" + FactionTag + " orders=" + Orders.Count + " cap=" + Cap);
    }

    private void OnOptions(byte[] data)
    {
        List<WireOrderOption> nodes;
        if (!FactionsWire.TryReadStandingOrderOptions(data, out nodes))
        {
            Plugin.Log("ORDERS_OPTIONS unreadable");
            return;
        }
        NodeOptions = nodes ?? new List<WireOrderOption>();
        HasOptions = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("ORDERS_OPTIONS nodes=" + NodeOptions.Count);
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadStandingOrderCommandResult(data, out ok, out message))
        {
            Plugin.Log("ORDER_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
