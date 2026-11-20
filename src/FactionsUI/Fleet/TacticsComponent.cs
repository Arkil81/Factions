using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Fleet;

/// <summary>
/// Owns the Tactics window's data: subscribes to the viewing player's own faction's learned tactics
/// while the window is up and holds the last snapshot.
///
/// <para>Its own subscription, for the reason <see cref="FleetDoctrineComponent"/> has one: the
/// window is opened from the Fleet window, itself opened from the Assets tab, and neither of those
/// caches keeps refreshing for a window left open on top of them. The server re-sends on the scanner
/// cadence, because evidence decays and a fight can land at any moment.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class TacticsComponent : MySessionComponentBase
{
    internal static TacticsComponent Instance { get; private set; }

    /// <summary>Whether this client has asked the server to stream. Set optimistically at send time:
    /// a subscribe has no reply.</summary>
    internal bool Streaming { get; private set; }

    internal WireTacticsHeader Header { get; private set; }
    internal List<WireTacticOpponent> Opponents { get; private set; } = new List<WireTacticOpponent>();
    /// <summary>Newest first.</summary>
    internal List<WireTacticReview> Reviews { get; private set; } = new List<WireTacticReview>();

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so the screen can poll rather than hold a callback.</summary>
    internal int Revision { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.TacticsSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Opponents = new List<WireTacticOpponent>();
        Reviews = new List<WireTacticReview>();
        Instance = null;
    }

    /// <summary>True when the server has granted the Tactics window to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Tactics);
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
                FactionsWire.ChannelId, FactionsWire.WriteTacticsRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("tactics subscribe (" + stream + ") failed: " + ex);
        }
    }

    private void OnSnapshot(byte[] data)
    {
        WireTacticsHeader header;
        List<WireTacticOpponent> opponents;
        List<WireTacticReview> reviews;
        if (!FactionsWire.TryReadTacticsSnapshot(data, out header, out opponents, out reviews))
        {
            Plugin.Log("TACTICS_SNAPSHOT unreadable");
            return;
        }

        Header = header;
        Opponents = opponents ?? new List<WireTacticOpponent>();
        Reviews = reviews ?? new List<WireTacticReview>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
        if (Config.Current.DebugLogging)
            Plugin.Log("TACTICS_SNAPSHOT tag=" + header.FactionTag + " opponents=" + Opponents.Count
                       + " reviews=" + Reviews.Count + " updates=" + header.Updates);
    }
}
