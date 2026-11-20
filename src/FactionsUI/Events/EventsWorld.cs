using System;
using System.Collections.Generic;
using Factions.Wire;

namespace FactionsUI.Events;

/// <summary>
/// The client's copy of the viewing player's faction history, newest first — the order the server
/// sends it in and the order the tab reads it.
/// </summary>
internal sealed class EventsWorld
{
    public readonly List<FactionEventRow> Events = new List<FactionEventRow>();

    /// <summary>Empty for a player in no faction, or one Factions has never registered. A real
    /// state the tab explains rather than an error.</summary>
    public string FactionTag = "";

    public bool HasSnapshot { get; private set; }
    public DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change. The page polls this rather than subscribing to an event, so
    /// a screen that has already closed cannot hold a live callback into a rebuilt table.</summary>
    public int Revision { get; private set; }

    public int Count => Events.Count;

    public void Apply(string factionTag, List<WireFactionEvent> events)
    {
        FactionTag = factionTag ?? "";

        Events.Clear();
        for (int i = 0; i < events.Count; i++) Events.Add(new FactionEventRow(events[i]));

        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
    }

    public void Clear()
    {
        Events.Clear();
        FactionTag = "";
        HasSnapshot = false;
        Revision++;
    }

    /// <summary>
    /// One thing that happened. Mirrors the server's <c>FactionEventKind</c> byte values; a value
    /// this build does not know decodes as <see cref="EventKind.Unknown"/> and is still shown,
    /// because an unrecognised entry is a newer server, not a corrupt feed.
    /// </summary>
    internal sealed class FactionEventRow
    {
        public readonly DateTime Utc;
        public readonly EventKind Kind;
        public readonly string ActorTag;
        public readonly string OtherTag;
        public readonly string NodeId;
        public readonly string NodeName;
        public readonly long Amount;
        public readonly double Ratio;
        public readonly bool ViewerIsActor;

        /// <summary>Stable across rebuilds, so a snapshot arriving while the player reads a row does
        /// not move the selection out from under them.</summary>
        public readonly string Key;

        public FactionEventRow(WireFactionEvent w)
        {
            Utc = new DateTime(w.UtcTicks, DateTimeKind.Utc);
            Kind = Enum.IsDefined(typeof(EventKind), (int)w.Kind) ? (EventKind)w.Kind : EventKind.Unknown;
            ActorTag = w.ActorTag ?? "";
            OtherTag = w.OtherTag ?? "";
            NodeId = w.NodeId ?? "";
            NodeName = string.IsNullOrEmpty(w.NodeName) ? NodeId : w.NodeName;
            Amount = w.Amount;
            Ratio = w.Ratio;
            ViewerIsActor = w.ViewerIsActor;
            Key = w.UtcTicks + ":" + w.Kind + ":" + ActorTag + ":" + NodeId;
        }

        /// <summary>The other faction involved, whichever end of it the viewer is on. Empty for a
        /// one-sided event such as a build.</summary>
        public string CounterpartyTag => ViewerIsActor ? OtherTag : ActorTag;
    }

    /// <summary>Mirror of the server's <c>Factions.Core.FactionEventKind</c>. Kept as its own copy
    /// so the client project stays free of a reference to Core, exactly as the map mirrors
    /// <c>NodeKind</c>. Values must match; never renumber.</summary>
    internal enum EventKind
    {
        Unknown = 0,
        RaidResolved = 1,
        RaidRepelled = 2,
        NodeConquered = 3,
        NodeLost = 4,
        ConvoyLooted = 5,
        ConvoyEscaped = 6,
        TruceBreach = 7,
        ShipLost = 8,
        TradeResolved = 9,
        GiftResolved = 10,
        ShipBuilt = 11,
        MineResolved = 12,
        FleetDispatched = 13,
        HaulResolved = 14,
        BuildQueued = 15,
        BuildExpired = 16,
        ConvoyDispatched = 17,
        BuildCancelled = 18,
        BuildOverridden = 19,
        RaidEngaged = 20,
    }
}
