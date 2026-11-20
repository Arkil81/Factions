using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Store;

/// <summary>
/// Owns the Store window's data: subscribes to the viewing player's own faction shelf, holds the
/// decoded lines and the census, and sends the window's actions.
///
/// It has its own subscription rather than reading the Assets tab's register, for the reason
/// <see cref="Fleet.FleetDoctrineComponent"/> records: the Assets component stops refreshing the
/// moment the player leaves that tab, so a window opened from it and left open would be reading
/// state that stopped moving behind it — and would look entirely normal doing so.
///
/// <para>The server re-sends on its own cadence rather than only when the player edits something: a
/// line's OnSale falls the moment a customer buys, and Building changes when a yard takes the build.
/// <see cref="Revision"/> is bumped on every arrival so the screen rebuilds on the cadence.</para>
///
/// <para><b>It reads the Fleet window's pick-list rather than asking for its own.</b> A shelf line
/// names one hull and a count — no role, no scope node — so the hull rows
/// <see cref="Fleet.FleetDoctrineComponent.Prefabs"/> already holds are the whole picker. Asking the
/// server for a second copy would duplicate the largest payload in the protocol to show the same
/// rows.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class ShelfComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on screen. The snapshot behind it is the
    /// authoritative answer; the line only says what the button did.</summary>
    internal const double CommandMessageSeconds = 12.0;

    internal static ShelfComponent Instance { get; private set; }

    /// <summary>Whether this client has asked the server to stream the shelf. Set optimistically at
    /// send time: a subscribe has no reply.</summary>
    internal bool Streaming { get; private set; }

    /// <summary>Faction tag the last snapshot described. Empty when the player is in no faction, or
    /// in one Factions has not registered as a player faction — a real answer, not a missing one.</summary>
    internal string FactionTag { get; private set; }

    /// <summary><c>FactionTuning.PlayerShelfCap</c>, so the window can show "3/32" rather than a bare
    /// count.</summary>
    internal int Cap { get; private set; }

    /// <summary>The player has switched restocking off entirely. Carried separately from the lines
    /// because a shop that is off and a shop with nothing to do are otherwise the same picture.</summary>
    internal bool StockingOff { get; private set; }
    internal List<WireShelfLine> Lines { get; private set; } = new List<WireShelfLine>();

    /// <summary>Every hull actually on sale, including the ones no line names — which is the thing
    /// no other window in the game shows.</summary>
    internal List<WireShelfCensusRow> Census { get; private set; } = new List<WireShelfCensusRow>();

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
        UplinkComponent.RegisterHandler(WireMessageKind.PlayerShelfSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.PlayerShelfCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Lines = new List<WireShelfLine>();
        Census = new List<WireShelfCensusRow>();
        Instance = null;
    }

    /// <summary>True when the server has granted the shelf feature to this client. A server may
    /// withhold it while still granting the read-only surfaces, because this one mutates.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.PlayerShelf);
        }
    }

    /// <summary>Subscribe or unsubscribe. Idempotent and safe to call every frame: the screen calls
    /// it while it is up, so a window opened before the uplink was granted starts streaming as soon
    /// as it is.</summary>
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
                FactionsWire.ChannelId, FactionsWire.WritePlayerShelfRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("shelf subscribe (" + stream + ") failed: " + ex);
            // Leave Streaming alone: the send did not happen, so the next call must retry.
        }
    }

    /// <summary>Sends one action against one line, or a shop-wide stocking switch. The server
    /// re-resolves the caller's faction and the line within it; nothing here is trusted.</summary>
    internal void SendCommand(WireShelfCommand command, string lineId, int value)
    {
        if (!Available) return;
        bool shopWide = command == WireShelfCommand.StockingOff || command == WireShelfCommand.StockingOn;
        if (!shopWide && string.IsNullOrEmpty(lineId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WritePlayerShelfCommandRequest(lineId ?? "", command, value), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("shelf command (" + command + ") failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    /// <summary>Sends a create request. Both fields are re-validated server-side through the same
    /// entry point the chat verb uses — the pick-list this dialog was built from is a convenience,
    /// not an authorisation.</summary>
    internal void SendCreate(string prefabId, int targetCount)
    {
        if (!Available) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WritePlayerShelfCreateRequest(prefabId, targetCount), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("shelf create failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        int cap;
        bool off;
        List<WireShelfLine> lines;
        List<WireShelfCensusRow> census;
        if (!FactionsWire.TryReadPlayerShelfSnapshot(data, out tag, out cap, out off, out lines, out census))
        {
            Plugin.Log("SHELF_SNAPSHOT unreadable");
            return;
        }

        FactionTag = tag ?? "";
        Cap = cap;
        StockingOff = off;
        Lines = lines ?? new List<WireShelfLine>();
        Census = census ?? new List<WireShelfCensusRow>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("SHELF_SNAPSHOT tag=" + FactionTag + " lines=" + Lines.Count
                       + " census=" + Census.Count + " off=" + off);
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadPlayerShelfCommandResult(data, out ok, out message))
        {
            Plugin.Log("SHELF_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
