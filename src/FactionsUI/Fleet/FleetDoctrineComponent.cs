using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Fleet;

/// <summary>
/// Owns the Fleet window's data: subscribes to the viewing player's own faction doctrine, holds the
/// decoded lines, the pressure rows and the census, and sends the window's actions.
///
/// It has its own subscription rather than reading the Assets tab's register, for the reason
/// <see cref="Nodes.NodeDetailComponent"/> records: <see cref="Assets.AssetsComponent"/> stops
/// refreshing the moment the player leaves that tab, so a window opened from it and left open would
/// be reading state that stopped moving behind it — and would look entirely normal doing so. This
/// component outlives that screen and that subscription.
///
/// <para>Unlike the Mine list, the server re-sends this on its own cadence rather than only when the
/// player edits something: a line's Have, Flying, Building and Pressure all move without the list
/// moving. <see cref="Revision"/> here is bumped on every arrival, so the screen rebuilds on the
/// cadence; <see cref="DoctrineRevision"/> is the server's own list revision, which the screen uses
/// to tell an edit apart from a count changing.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class FleetDoctrineComponent : MySessionComponentBase
{
    /// <summary>How long a command's reply stays on screen. The snapshot behind it is the
    /// authoritative answer; the line only says what the button did.</summary>
    internal const double CommandMessageSeconds = 12.0;

    internal static FleetDoctrineComponent Instance { get; private set; }

    /// <summary>Whether this client has asked the server to stream the doctrine. Set optimistically
    /// at send time: a subscribe has no reply, and the alternative is re-sending every frame.</summary>
    internal bool Streaming { get; private set; }

    /// <summary>Faction tag the last snapshot described. Empty when the player is in no faction, or
    /// in one Factions has not registered as a player faction — a real answer, not a missing one.</summary>
    internal string FactionTag { get; private set; }

    /// <summary><c>FactionTuning.FleetDoctrineCap</c>, so the window can show "3/32" rather than a
    /// bare count and a player can tell a full doctrine from a short one.</summary>
    internal int Cap { get; private set; }

    /// <summary>The server's own <c>FactionState.FleetDoctrineRevision</c>: bumped when a player
    /// edits the doctrine and NOT when the counts beside it move.</summary>
    internal int DoctrineRevision { get; private set; }

    internal List<WireDoctrineLine> Lines { get; private set; } = new List<WireDoctrineLine>();

    /// <summary>Work waiting on hulls no unpaused line covers.</summary>
    internal List<WireDoctrinePressureRow> Unlined { get; private set; } = new List<WireDoctrinePressureRow>();

    /// <summary>Every hull the faction owns, whether or not a line names it.</summary>
    internal List<WireFleetCensusRow> Census { get; private set; } = new List<WireFleetCensusRow>();

    /// <summary>Designs with hulls on an older version or a refit set — the Refits window. Empty
    /// from a server that predates it.</summary>
    internal List<WireHullRefitRow> Refits { get; private set; } = new List<WireHullRefitRow>();

    /// <summary>The build roles a line may name, in the server's own order.</summary>
    internal List<string> Roles { get; private set; } = new List<string>();

    /// <summary>The owned yards a line may be scoped to. Resolved server-side; the client has no
    /// graph.</summary>
    internal List<WireOrderOption> ScopeNodes { get; private set; } = new List<WireOrderOption>();

    /// <summary>Every hull a line could name, each carrying the numbers a player chooses on and
    /// which roles it fits — so one flat list serves a role-filtered picker.</summary>
    internal List<WireBuildPrefab> Prefabs { get; private set; } = new List<WireBuildPrefab>();

    /// <summary>True once the pick-lists have arrived. The new-line dialog cannot usefully open
    /// before then.</summary>
    internal bool HasOptions { get; private set; }

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
        UplinkComponent.RegisterHandler(WireMessageKind.FleetDoctrineSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.FleetDoctrineCommandResult, OnCommandResult);
        UplinkComponent.RegisterHandler(WireMessageKind.FleetDoctrineOptions, OnOptions);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        HasOptions = false;
        Lines = new List<WireDoctrineLine>();
        Unlined = new List<WireDoctrinePressureRow>();
        Census = new List<WireFleetCensusRow>();
        Refits = new List<WireHullRefitRow>();
        Roles = new List<string>();
        ScopeNodes = new List<WireOrderOption>();
        Prefabs = new List<WireBuildPrefab>();
        Instance = null;
    }

    /// <summary>True when the server has granted the fleet-doctrine feature to this client. A server
    /// may withhold it while still granting the read-only surfaces, because this one mutates — and
    /// what it mutates decides everything the faction builds.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.FleetDoctrine);
        }
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
                FactionsWire.ChannelId, FactionsWire.WriteFleetDoctrineRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("fleet doctrine subscribe (" + stream + ") failed: " + ex);
            // Leave Streaming alone: the send did not happen, so the next call must retry.
        }
    }

    /// <summary>Sends one action against one line. The server re-resolves the caller's faction and
    /// the line within it; nothing here is trusted.</summary>
    internal void SendCommand(WireDoctrineCommand command, string lineId, int value)
    {
        if (!Available || string.IsNullOrEmpty(lineId)) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteFleetDoctrineCommandRequest(lineId, command, value), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("fleet doctrine command (" + command + ") failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    /// <summary>Sends a create request. Every field is re-validated server-side through the same
    /// entry point the chat verb uses — the pick-lists this dialog was built from are a convenience,
    /// not an authorisation.</summary>
    internal void SendCreate(string prefabId, int targetCount, int maxConcurrent,
        string scopeNodeId)
    {
        if (!Available) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId,
                FactionsWire.WriteFleetDoctrineCreateRequest(prefabId, targetCount, maxConcurrent,
                    scopeNodeId), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("fleet doctrine create failed: " + ex);
            LastCommandMessage = "Could not reach the server.";
            LastCommandUtc = DateTime.UtcNow;
            Revision++;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string tag;
        int cap, revision;
        List<WireDoctrineLine> lines;
        List<WireDoctrinePressureRow> unlined;
        List<WireFleetCensusRow> census;
        List<WireHullRefitRow> refits;
        if (!FactionsWire.TryReadFleetDoctrineSnapshot(data, out tag, out cap, out revision,
                out lines, out unlined, out census, out refits))
        {
            Plugin.Log("DOCTRINE_SNAPSHOT unreadable");
            return;
        }

        FactionTag = tag ?? "";
        Cap = cap;
        DoctrineRevision = revision;
        Lines = lines ?? new List<WireDoctrineLine>();
        Unlined = unlined ?? new List<WireDoctrinePressureRow>();
        Census = census ?? new List<WireFleetCensusRow>();
        Refits = refits ?? new List<WireHullRefitRow>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("DOCTRINE_SNAPSHOT tag=" + FactionTag + " lines=" + Lines.Count
                       + " unlined=" + Unlined.Count + " census=" + Census.Count + " rev=" + revision);
    }

    private void OnOptions(byte[] data)
    {
        List<string> roles;
        List<WireOrderOption> nodes;
        List<WireBuildPrefab> prefabs;
        if (!FactionsWire.TryReadFleetDoctrineOptions(data, out roles, out nodes, out prefabs))
        {
            Plugin.Log("DOCTRINE_OPTIONS unreadable");
            return;
        }
        Roles = roles ?? new List<string>();
        ScopeNodes = nodes ?? new List<WireOrderOption>();
        Prefabs = prefabs ?? new List<WireBuildPrefab>();
        HasOptions = true;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("DOCTRINE_OPTIONS roles=" + Roles.Count + " nodes=" + ScopeNodes.Count
                       + " prefabs=" + Prefabs.Count);
    }

    private void OnCommandResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadFleetDoctrineCommandResult(data, out ok, out message))
        {
            Plugin.Log("DOCTRINE_COMMAND_RESULT unreadable");
            return;
        }
        LastCommandMessage = message;
        LastCommandUtc = DateTime.UtcNow;
        Revision++;
    }
}
