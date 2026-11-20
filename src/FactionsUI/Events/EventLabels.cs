using System;

namespace FactionsUI.Events;

/// <summary>
/// How one faction event is worded, wherever it is shown. Both surfaces that name an event — the
/// Events tab's table and the map's selection panel — read these, so a raid cannot be called two
/// different things on two screens.
/// </summary>
internal static class EventLabels
{
    /// <summary>
    /// The row's headline, written from the viewer's side. The same record reads as a success or a
    /// problem depending on which end of it the player's faction is on, and the leading "!" is what
    /// makes the problems findable in a list of thirteen rows.
    /// </summary>
    public static string Headline(EventsWorld.FactionEventRow e)
    {
        switch (e.Kind)
        {
            case EventsWorld.EventKind.RaidEngaged:
                return e.ViewerIsActor ? "Our raid is engaging" : "! Under attack now";
            case EventsWorld.EventKind.RaidResolved:
                return e.ViewerIsActor ? "Raid landed" : "! Raided";
            case EventsWorld.EventKind.RaidRepelled:
                if (RaidDestroyed(e)) return e.ViewerIsActor ? "Our raid was destroyed" : "Raid destroyed";
                return e.ViewerIsActor ? "Our raid was repelled" : "Raid repelled";
            case EventsWorld.EventKind.NodeConquered:
                return e.ViewerIsActor ? "Station captured" : "! Station lost to attack";
            case EventsWorld.EventKind.NodeLost:
                return "! Station abandoned";
            case EventsWorld.EventKind.ConvoyLooted:
                return e.ViewerIsActor ? "Convoy looted" : "! Convoy plundered";
            case EventsWorld.EventKind.ConvoyEscaped:
                return e.ViewerIsActor ? "Convoy got away from us" : "Convoy escaped a raider";
            case EventsWorld.EventKind.TruceBreach:
                return e.ViewerIsActor ? "We broke a truce" : "! Truce broken against us";
            case EventsWorld.EventKind.ShipLost:
                return "! Ships lost in transit";
            case EventsWorld.EventKind.TradeResolved:
                return e.ViewerIsActor ? "Trade delivered" : "Trade received";
            case EventsWorld.EventKind.GiftResolved:
                return e.ViewerIsActor ? "Gift sent" : "Gift received";
            case EventsWorld.EventKind.ShipBuilt:
                return "Ship built";
            case EventsWorld.EventKind.MineResolved:
                return "Ore delivered";
            case EventsWorld.EventKind.FleetDispatched:
                return e.ViewerIsActor ? "Attack dispatched" : "! Attack incoming";
            case EventsWorld.EventKind.HaulResolved:
                return "Cargo delivered";
            case EventsWorld.EventKind.ConvoyDispatched:
                return "Convoy sent";
            case EventsWorld.EventKind.BuildQueued:
                return "Build queued";
            case EventsWorld.EventKind.BuildExpired:
                return "! Build abandoned";
            case EventsWorld.EventKind.BuildCancelled:
                return "Build cancelled";
            case EventsWorld.EventKind.BuildOverridden:
                return "Build replaced";
            default:
                return "Unrecognised event";
        }
    }

    /// <summary>
    /// Whether a stopped raid lost every hull it brought. The server files the hulls lost as the
    /// event's amount and their share of the hulls brought as its ratio; anything short of all of
    /// them came home, and that raid was repelled, not destroyed. An event filed before the server
    /// carried the counts reads zero, and is called repelled.
    /// </summary>
    public static bool RaidDestroyed(EventsWorld.FactionEventRow e)
    {
        return e.Kind == EventsWorld.EventKind.RaidRepelled && e.Amount > 0 && e.Ratio >= 0.999;
    }

    /// <summary>Compact enough for a narrow column: the exact timestamp is in the tooltip and the
    /// detail panel.</summary>
    public static string Age(DateTime utc)
    {
        TimeSpan since = DateTime.UtcNow - utc;
        if (since.TotalSeconds < 45) return "now";
        if (since.TotalMinutes < 60) return (int)since.TotalMinutes + "m";
        if (since.TotalHours < 24) return (int)since.TotalHours + "h";
        return (int)since.TotalDays + "d";
    }
}
