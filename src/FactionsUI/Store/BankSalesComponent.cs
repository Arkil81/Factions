using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Store;

/// <summary>
/// Owns the Bank sales window's data: subscribes to the viewing player's own faction's bank-sale
/// list, holds the decoded rows and sends the window's keep/sell commands.
///
/// <para>Its own subscription, for the reason <see cref="ShelfComponent"/> has one: a window left
/// open must not read a cache that stopped refreshing when its opener closed.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class BankSalesComponent : MySessionComponentBase
{
    internal const double CommandMessageSeconds = 12.0;

    internal static BankSalesComponent Instance { get; private set; }

    /// <summary>Set optimistically at send time: a subscribe has no reply.</summary>
    internal bool Streaming { get; private set; }

    /// <summary>Empty when the player is in no faction Factions has registered.</summary>
    internal string FactionTag { get; private set; } = "";

    internal List<WireBankSalesRow> Rows { get; private set; } = new List<WireBankSalesRow>();

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so the screen polls rather than holding a callback.</summary>
    internal int Revision { get; private set; }

    internal string LastCommandMessage { get; private set; }
    internal DateTime LastCommandUtc { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.BankSalesSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.BankSalesCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Rows = new List<WireBankSalesRow>();
        Instance = null;
    }

    /// <summary>Granted with the Store window, which is where this one opens from.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.PlayerShelf);
        }
    }

    /// <summary>Subscribe or unsubscribe. Idempotent and safe to call every frame.</summary>
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
                FactionsWire.ChannelId, FactionsWire.WriteBankSalesRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("bank sales subscribe (" + stream + ") failed: " + ex);
        }
    }

    /// <summary>Sets one rule on <paramref name="keys"/>: <paramref name="mode"/> as in
    /// <see cref="WireBankSalesRow.Mode"/>, <paramref name="target"/> read only for mode 1. The server
    /// re-resolves the caller's faction and checks every key; nothing here is trusted.</summary>
    internal void SendCommand(byte mode, long target, IList<string> keys)
    {
        if (!Available || keys == null || keys.Count == 0) return;
        if (keys.Count > FactionsWire.MaxBankSalesRows)
        {
            LastCommandMessage = "Too many items at once; narrow the filter.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
            return;
        }
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteBankSalesCommandRequest(mode, target, keys), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("bank sales command failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        List<WireBankSalesRow> rows;
        if (!FactionsWire.TryReadBankSalesSnapshot(data, out tag, out rows))
        {
            Plugin.Log("BANK_SALES_SNAPSHOT unreadable");
            return;
        }
        FactionTag = tag ?? "";
        Rows = rows ?? new List<WireBankSalesRow>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("BANK_SALES_SNAPSHOT tag=" + FactionTag + " rows=" + Rows.Count);
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadBankSalesCommandResult(data, out ok, out message))
        {
            Plugin.Log("BANK_SALES_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
