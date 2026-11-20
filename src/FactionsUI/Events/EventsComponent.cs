using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Events;

/// <summary>
/// Owns the Events tab's data: registers for the server's snapshots, holds the decoded history, and
/// subscribes only while the tab is actually being looked at.
///
/// The tab itself is <see cref="EventsPage"/>, which is rebuilt every time the terminal opens; this
/// outlives it, so a K menu reopened a moment later still has a history to draw.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class EventsComponent : MySessionComponentBase
{
    internal static EventsComponent Instance { get; private set; }

    internal readonly EventsWorld World = new EventsWorld();

    /// <summary>Set while a subscribe has been sent and no snapshot has come back yet, so the page
    /// can say "waiting" instead of showing a convincingly empty history.</summary>
    internal bool AwaitingFirstSnapshot { get; private set; }

    /// <summary>The Events tab.</summary>
    internal const int WantTab = 1;
    /// <summary>The map's selection panel, which names what has recently happened at the selected
    /// node.</summary>
    internal const int WantMap = 2;

    private bool _subscribed;
    private int _wants;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.EventsSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        World.Clear();
        _subscribed = false;
        _wants = 0;
        Instance = null;
    }

    /// <summary>True when the server has granted the events feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Events);
        }
    }

    /// <summary>
    /// Declare whether one surface wants the feed, and stream while any of them does. Idempotent
    /// and safe to call every frame: each surface calls it repeatedly while it is on screen, so one
    /// entered before the uplink was granted starts streaming as soon as it is.
    ///
    /// <para>A want per surface rather than one flag, because two of them now read this feed and a
    /// bare <c>SetStreaming(false)</c> from either would cut the other off.</para>
    /// </summary>
    internal void SetWant(int surface, bool want)
    {
        int next = want ? (_wants | surface) : (_wants & ~surface);
        _wants = next;
        SetStreaming(next != 0);
    }

    private void SetStreaming(bool streaming)
    {
        if (_subscribed == streaming) return;

        if (!Available)
        {
            // Nothing to unsubscribe from if the link is gone; drop the local flag so a later
            // subscribe is not swallowed as a no-op.
            if (!streaming) _subscribed = false;
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteEventsRequest(streaming), true);
            _subscribed = streaming;
            if (streaming) AwaitingFirstSnapshot = !World.HasSnapshot;
        }
        catch (Exception ex)
        {
            Plugin.Log("events subscribe (" + streaming + ") failed: " + ex);
            // Leave _subscribed alone: the send did not happen, so the next call must retry.
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string factionTag;
        List<WireFactionEvent> events;
        if (!FactionsWire.TryReadEventsSnapshot(data, out factionTag, out events))
        {
            Plugin.Log("EVENTS_SNAPSHOT unreadable");
            return;
        }
        World.Apply(factionTag, events);
        AwaitingFirstSnapshot = false;
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("EVENTS_SNAPSHOT faction=" + (string.IsNullOrEmpty(factionTag) ? "(none)" : factionTag)
                       + " events=" + events.Count);
        }
    }
}
