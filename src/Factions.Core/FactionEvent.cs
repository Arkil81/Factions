using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// What happened. Byte values are persisted and go on the wire — append only, never renumber.
    /// A kind the reader does not know decodes as <see cref="Unknown"/> rather than failing the
    /// whole feed.
    /// </summary>
    public enum FactionEventKind : byte
    {
        Unknown = 0,

        /// <summary>A raid landed on a node: hold damaged, possibly looted.</summary>
        RaidResolved = 1,
        /// <summary>A raid was stopped before it landed — repelled, broken off or beaten — by a Guard,
        /// a convoy or a station. <see cref="FactionEvent.Amount"/> is the attacker's hulls lost and
        /// <see cref="FactionEvent.Ratio"/> their share of the hulls it brought; 1 means none came home.
        /// Events filed before both were carried read 0.</summary>
        RaidRepelled = 2,
        /// <summary>A node changed hands by force.</summary>
        NodeConquered = 3,
        /// <summary>A node was lost without a fight — hold decayed to nothing.</summary>
        NodeLost = 4,
        /// <summary>A convoy in transit was intercepted and its cargo taken.</summary>
        ConvoyLooted = 5,
        /// <summary>An interception was attempted and the convoy got away.</summary>
        ConvoyEscaped = 6,
        /// <summary>Someone attacked while under an active truce.</summary>
        TruceBreach = 7,
        /// <summary>A convoy was destroyed in transit; its ships are gone.</summary>
        ShipLost = 8,

        /// <summary>A trade convoy arrived and settled.</summary>
        TradeResolved = 9,
        /// <summary>A gift arrived.</summary>
        GiftResolved = 10,

        /// <summary>A shipyard finished a hull.</summary>
        ShipBuilt = 11,
        /// <summary>A mining run delivered.</summary>
        MineResolved = 12,
        /// <summary>A fleet left on a new errand.</summary>
        FleetDispatched = 13,

        /// <summary>A haul arrived and unloaded. The bulk of what a working faction actually does,
        /// and the reason <see cref="FactionEventLog.IsNotable"/> exists.</summary>
        HaulResolved = 14,
        /// <summary>A hull was put on a shipyard queue.</summary>
        BuildQueued = 15,
        /// <summary>A queued build was abandoned without finishing.</summary>
        BuildExpired = 16,
        /// <summary>A non-combat convoy left — a trade, a gift, a patrol. Separate from
        /// <see cref="FleetDispatched"/> so that one can stay in the notable class without every
        /// outbound trade inheriting the protection an incoming raid needs.</summary>
        ConvoyDispatched = 17,
        /// <summary>A queued build was cancelled and its components refunded.</summary>
        BuildCancelled = 18,
        /// <summary>
        /// A queued station rebuild was replaced by another, the old one refunded.
        ///
        /// <para>One producer: <c>ShipyardManager.QueueRefit</c> under <c>force</c>, where a second
        /// refit at a node already collecting one is an explicit overwrite the player asked for. Hull
        /// builds do not reach it -- <c>ShipyardManager.QueueBuild</c> appends, so a second build of a
        /// role queues beside the first rather than displacing it.</para>
        /// </summary>
        BuildOverridden = 19,

        /// <summary>A raid reached its target and the engagement began. The one event that reports
        /// something still in progress rather than already settled: it opens the
        /// <see cref="FleetStopKind.Engage"/> dwell, and its <see cref="FactionEvent.Amount"/> is
        /// how many seconds that dwell lasts -- the time a defender has to reach the fleet and fight
        /// it before <see cref="RaidResolved"/> or <see cref="RaidRepelled"/> closes it out.</summary>
        RaidEngaged = 20,

        /// <summary>A **player** did damage to one of this faction's stations. The first signal in
        /// the model that treats a player as an aggressor rather than as a customer -- until
        /// v0.152.0 nothing a player did reached Core at all. <see cref="FactionEvent.ActorTag"/> is
        /// null because Core does not model a player as an actor; the victim is the station's owner.
        /// </summary>
        PlayerAggression = 21,

        /// <summary>A station refit finished collecting its components: the node has paid for a new
        /// design and <see cref="SpatialNode.PendingStationPrefabId"/> now names it. The rebuild
        /// itself happens on the game thread, so this reports an obligation rather than a finished
        /// grid. <see cref="FactionEvent.NodeId"/> is the node being rebuilt.</summary>
        StationRefitReady = 22,
    }

    /// <summary>
    /// One thing that happened to a faction, as a record rather than a sentence: the tab, the log
    /// line and any later filter all read the same fields instead of parsing prose.
    ///
    /// <para>Recorded into <see cref="WorldModel.RecordEvent"/> at the site that already knows the
    /// outcome, then drained once per tick into the rings of the factions it involves — see
    /// <see cref="FactionEventLog.Drain"/>.</para>
    /// </summary>
    public struct FactionEvent
    {
        public DateTime Utc;
        public FactionEventKind Kind;

        /// <summary>Who did it. Never null for a well-formed event.</summary>
        public string ActorTag;

        /// <summary>Who it was done to (victim, trade partner, gift recipient). Null when the event
        /// has only one side, as a shipyard build does.</summary>
        public string OtherTag;

        /// <summary>Where, when the event has a place. Null for a build with no node, or an
        /// interception in open space.</summary>
        public string NodeId;

        /// <summary>Kind-specific magnitude in whole units: credits looted or settled, ships built,
        /// litres mined. Zero when the kind carries no quantity.</summary>
        public long Amount;

        /// <summary>Kind-specific strength or completion, 0..1. How hard a raid landed, how much of
        /// a trade was delivered. Zero when the kind carries no ratio.</summary>
        public double Ratio;
    }

    /// <summary>
    /// The per-faction event ring: how an event reaches the factions it concerns, and how the ring
    /// stays bounded. Lives in Core so the host and the offline sim keep identical histories.
    /// </summary>
    public static class FactionEventLog
    {
        /// <summary>
        /// Moves everything <see cref="WorldModel.PendingEvents"/> has collected this tick into the
        /// rings of the factions involved, and clears it. Both sides of an event get their own copy:
        /// a raid is the attacker's success and the victim's problem, and it is the victim's ring the
        /// Events tab is really for.
        /// </summary>
        public static int Drain(IList<FactionState> states, WorldModel world, int cap)
        {
            if (states == null || world == null) return 0;
            IList<FactionEvent> pending = world.PendingEvents;
            if (pending.Count == 0) return 0;

            int delivered = 0;
            for (int i = 0; i < pending.Count; i++) delivered += File(states, pending[i], cap);

            world.ClearPendingEvents();
            return delivered;
        }

        /// <summary>Files one event into the rings of the factions it involves — both sides of it —
        /// and reports how many rings actually took it. Shared by <see cref="Drain"/> and by a host
        /// carrying undrained events across a reload.</summary>
        public static int File(IList<FactionState> states, FactionEvent e, int cap)
        {
            if (states == null) return 0;
            int delivered = 0;
            if (Append(Find(states, e.ActorTag), e, cap)) delivered++;
            if (!string.Equals(e.OtherTag, e.ActorTag, StringComparison.OrdinalIgnoreCase)
                && Append(Find(states, e.OtherTag), e, cap)) delivered++;
            return delivered;
        }

        /// <summary>
        /// Folds a ring carried in memory into one just rebuilt from disk, keeping the newest
        /// <paramref name="cap"/> of the union and dropping entries the two have in common.
        ///
        /// <para>A reload is a reconciliation with disk, not a new session — and the brain files are
        /// only as fresh as the phased flush last reached, so the disk copy is routinely behind what
        /// the live ring holds. Without this, every reload silently truncates the history back to the
        /// last flush. (Found live 2026-08-22: the test server reloads every 10-35 minutes, and a
        /// trade recorded 65 seconds before one was gone from both participants' rings afterwards.)</para>
        /// </summary>
        public static int Merge(FactionState state, IList<FactionEvent> carried, int cap)
        {
            if (state == null || carried == null || carried.Count == 0 || cap <= 0) return 0;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < state.RecentEvents.Count; i++) seen.Add(Identity(state.RecentEvents[i]));

            int added = 0;
            for (int i = 0; i < carried.Count; i++)
            {
                if (!seen.Add(Identity(carried[i]))) continue;
                state.RecentEvents.Add(carried[i]);
                added++;
            }
            if (added == 0) return 0;

            state.RecentEvents.Sort(CompareByUtc);
            Trim(state.RecentEvents, cap);
            state.EventsRevision++;
            return added;
        }

        private static int CompareByUtc(FactionEvent a, FactionEvent b)
        {
            return a.Utc.CompareTo(b.Utc);
        }

        /// <summary>What makes two records the same event. Two events sharing all of this are
        /// indistinguishable to anything that reads them, so collapsing them is the safe direction:
        /// the alternative is a reload doubling every entry it carried.</summary>
        private static string Identity(FactionEvent e)
        {
            return e.Utc.Ticks.ToString(CultureInfo.InvariantCulture)
                   + "|" + (byte)e.Kind
                   + "|" + e.ActorTag
                   + "|" + e.OtherTag
                   + "|" + e.NodeId
                   + "|" + e.Amount.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a kind is one the tab exists for — something done TO the faction, or a loss it
        /// took — as opposed to routine traffic it generates itself.
        ///
        /// <para>This is a retention rule, not a display one. A working faction resolves on the order
        /// of a haul a minute (live, 2026-08-22: EXP logged 1,527 `HAUL_RESOLVED` in a day against
        /// zero raids), so a ring trimmed purely by age would evict every attack within the hour and
        /// the one question the feed is for — where is my attention needed — would be unanswerable on
        /// exactly the busiest day.</para>
        /// </summary>
        public static bool IsNotable(FactionEventKind kind)
        {
            switch (kind)
            {
                case FactionEventKind.RaidResolved:
                case FactionEventKind.RaidRepelled:
                case FactionEventKind.RaidEngaged:
                case FactionEventKind.PlayerAggression:
                case FactionEventKind.NodeConquered:
                case FactionEventKind.NodeLost:
                case FactionEventKind.ConvoyLooted:
                case FactionEventKind.ConvoyEscaped:
                case FactionEventKind.TruceBreach:
                case FactionEventKind.ShipLost:
                case FactionEventKind.FleetDispatched:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Appends to one faction's ring, trimming to <paramref name="cap"/>. Returns false
        /// for a faction that is not registered — an event against UNKN or a faction the world has
        /// forgotten is simply not filed.</summary>
        public static bool Append(FactionState state, FactionEvent e, int cap)
        {
            if (state == null || cap <= 0) return false;

            state.RecentEvents.Add(e);
            Trim(state.RecentEvents, cap);
            state.EventsRevision++;
            return true;
        }

        /// <summary>
        /// Brings a ring within <paramref name="cap"/> by dropping the oldest **routine** entry
        /// first, and only falling back to the oldest entry outright once nothing routine is left.
        /// So a quiet week of raids survives a busy day of hauling, while a ring that is nothing but
        /// attacks still behaves as a plain age-ordered ring.
        /// </summary>
        internal static void Trim(List<FactionEvent> ring, int cap)
        {
            while (ring.Count > cap)
            {
                int victim = -1;
                for (int i = 0; i < ring.Count; i++)
                {
                    if (IsNotable(ring[i].Kind)) continue;
                    victim = i;
                    break;
                }
                ring.RemoveAt(victim >= 0 ? victim : 0);
            }
        }

        private static FactionState Find(IList<FactionState> states, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && string.Equals(states[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return states[i];
            return null;
        }
    }
}
