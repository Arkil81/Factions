using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Diplomacy;

/// <summary>
/// Owns the diplomacy readout: subscribes to the viewing player's own faction's stance toward the
/// NPC factions, holds the decoded rows by NPC tag, and sends the Diplomacy window's acts.
///
/// <para>One subscription serves both surfaces — the stance line on the terminal's Factions page and
/// the Diplomacy window — and <see cref="FactionsPageReadout.Tick"/> drives it from whether either is
/// up. The server re-sends only when a row changed, so an idle subscription costs one comparison per
/// scanner tick on the server and nothing on the wire.</para>
///
/// <para>Nothing here is authoritative. The rows say what the server believes, and every act is
/// re-checked there — the caller's faction, the leader flag, the target — exactly as the
/// <c>!factions diplomacy</c> chat verb is.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class DiplomacyComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on the window's status line.</summary>
    internal const double CommandMessageSeconds = 15.0;

    internal static DiplomacyComponent Instance { get; private set; }

    internal bool Streaming { get; private set; }

    /// <summary>The viewer's registered player faction, or empty when they have none — then there
    /// are no rows either, and both surfaces stay vanilla.</summary>
    internal string FactionTag { get; private set; } = "";

    /// <summary>The server's leader check for this viewer, as of the last snapshot.</summary>
    internal bool IsLeader { get; private set; }

    internal long GiftCost { get; private set; }
    internal long Treasury { get; private set; }

    internal bool HasSnapshot { get; private set; }

    /// <summary>Rows by NPC tag, case-insensitive: the vanilla page names a faction by its SE tag.</summary>
    internal Dictionary<string, WireDiplomacyRow> Rows { get; private set; } =
        new Dictionary<string, WireDiplomacyRow>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows in the server's order (by tag), for the window's offers list.</summary>
    internal List<WireDiplomacyRow> Ordered { get; private set; } = new List<WireDiplomacyRow>();

    private long _serverNowTicks;
    private DateTime _receivedUtc;

    /// <summary>Bumped on every change, so a surface polls instead of holding a callback into
    /// controls that may already have been rebuilt.</summary>
    internal int Revision { get; private set; }

    internal string LastCommandMessage { get; private set; }
    internal string LastCommandTag { get; private set; }
    internal DateTime LastCommandUtc { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.DiplomacySnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.DiplomacyCommandResult, OnCommandResult);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Rows = new Dictionary<string, WireDiplomacyRow>(StringComparer.OrdinalIgnoreCase);
        Ordered = new List<WireDiplomacyRow>();
        Instance = null;
    }

    /// <summary>True when the server granted the diplomacy feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Diplomacy);
        }
    }

    /// <summary>The server's clock now, estimated from the last snapshot's stamp plus the time since
    /// it arrived. Every countdown is measured against this, never the client's own clock.</summary>
    internal long ServerNowTicks
    {
        get
        {
            if (_serverNowTicks <= 0) return DateTime.UtcNow.Ticks;
            return _serverNowTicks + (DateTime.UtcNow - _receivedUtc).Ticks;
        }
    }

    internal bool TryGetRow(string npcTag, out WireDiplomacyRow row)
    {
        row = default(WireDiplomacyRow);
        return !string.IsNullOrEmpty(npcTag) && Rows.TryGetValue(npcTag, out row);
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
                FactionsWire.ChannelId, FactionsWire.WriteDiplomacyRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("diplomacy subscribe (" + stream + ") failed: " + ex);
        }
    }

    /// <summary>Sends one act toward one NPC. The server answers with a
    /// <see cref="WireMessageKind.DiplomacyCommandResult"/> and, when anything changed, a fresh
    /// snapshot.</summary>
    internal void SendCommand(WireDiplomacyAct act, string npcTag)
    {
        if (!Available || string.IsNullOrEmpty(npcTag)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteDiplomacyCommandRequest(act, npcTag), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("diplomacy command (" + act + ") failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandTag = npcTag;
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        bool leader;
        long cost, treasury, now;
        List<WireDiplomacyRow> rows;
        if (!FactionsWire.TryReadDiplomacySnapshot(data, out tag, out leader, out cost, out treasury, out now, out rows))
        {
            Plugin.Log("DIPLOMACY_SNAPSHOT unreadable");
            return;
        }

        var byTag = new Dictionary<string, WireDiplomacyRow>(StringComparer.OrdinalIgnoreCase);
        foreach (WireDiplomacyRow row in rows)
            if (!string.IsNullOrEmpty(row.NpcTag)) byTag[row.NpcTag] = row;

        FactionTag = tag ?? "";
        IsLeader = leader;
        GiftCost = cost;
        Treasury = treasury;
        _serverNowTicks = now;
        _receivedUtc = DateTime.UtcNow;
        Rows = byTag;
        Ordered = rows;
        HasSnapshot = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("DIPLOMACY_SNAPSHOT tag=" + FactionTag + " rows=" + rows.Count + " leader=" + leader);
    }

    private void OnCommandResult(byte[] data)
    {
        bool done;
        string tag, message;
        if (!FactionsWire.TryReadDiplomacyCommandResult(data, out done, out tag, out message))
        {
            Plugin.Log("DIPLOMACY_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandTag = tag;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
