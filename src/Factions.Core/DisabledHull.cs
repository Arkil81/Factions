using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// A materialised hull that has lost every thruster while under hostile fire is <b>disabled</b>:
    /// shot out of the fight, not stalled. It is written off at once -- out of the meeting's capacity
    /// and out of the hulls its fleet can credit back -- and its grid stays in the world for
    /// <see cref="DespawnDelay"/> as the wreck the player made, then is closed. It is never
    /// rematerialised: a lead's fleet takes the destroyed path under <see cref="Cause"/>, an escort's
    /// hull slot is removed and the fleet flies on without it.
    /// <para>A hull that loses thrust with no hostile hit inside <see cref="HostileHitWindow"/> is not
    /// disabled; the host dematerialises it and lets the leg run abstractly with stock intact.</para>
    /// </summary>
    public static class DisabledHull
    {
        /// <summary>The <see cref="PendingFleetAction.ForcedRatioCause"/> of a fleet whose lead was
        /// disabled: a physical loss, like <c>grid_destroyed</c>.</summary>
        public const string Cause = "grid_disabled";

        /// <summary>How recent the last hostile hit must be for a thrust loss to read as the hull being
        /// shot out of action. The host confirms a thrust loss only after it has lasted five seconds,
        /// so this covers that grace with room for the frames between the shot and the read.</summary>
        public static readonly TimeSpan HostileHitWindow = TimeSpan.FromSeconds(30);

        /// <summary>How long a disabled hull's grid stays in the world before it is closed.</summary>
        public static readonly TimeSpan DespawnDelay = TimeSpan.FromSeconds(25);

        /// <summary>Whether a hull whose thrust loss was just confirmed (<paramref name="thrustLost"/>)
        /// was disabled by hostile fire: its last hostile hit, <paramref name="lastHostileHitUtc"/>
        /// (null when none is on record), is inside <see cref="HostileHitWindow"/>.</summary>
        public static bool IsDisabled(bool thrustLost, DateTime? lastHostileHitUtc, DateTime nowUtc)
        {
            if (!thrustLost || !lastHostileHitUtc.HasValue) return false;
            TimeSpan since = nowUtc - lastHostileHitUtc.Value;
            return since >= TimeSpan.Zero && since <= HostileHitWindow;
        }

        /// <summary>Writes off the lead of <paramref name="fleet"/>: its hull (slot
        /// <paramref name="slot"/>, or <paramref name="prefabId"/> when no slot is known) leaves the
        /// fleet, and the fleet force-resolves next tick at ratio zero, as a physical loss under
        /// <see cref="Cause"/>. The grid is no longer the fleet's --
        /// <see cref="PendingFleetAction.MaterialisedEntityId"/> is the destroyed sentinel, which is
        /// never rematerialised. An attack whose mission hung on the lead
        /// (<see cref="PendingFleetResolver.MissionHangsOnLead"/>) keeps its escorts, which fly out of
        /// the fight when Core settles the lost meeting; any other fleet's escorts leave with the lead.</summary>
        public static void WriteOffLead(ref PendingFleetAction fleet, int slot, string prefabId,
                                        IPrefabCatalogue cat, DateTime nowUtc)
        {
            bool attackLost = PendingFleetResolver.MissionHangsOnLead(fleet);
            RemoveHull(ref fleet, slot, prefabId, cat);
            fleet.MaterialisedEntityId = -1;
            if (!attackLost) fleet.EscortEntityIds = null;
            fleet.EstimatedArrivalUtc = nowUtc;
            fleet.ForcedRatio = 0.0;
            fleet.ForcedRatioCause = Cause;
        }

        /// <summary>Writes off escort <paramref name="escortEid"/> of <paramref name="fleet"/>: its
        /// hull (slot <paramref name="slot"/>, or <paramref name="prefabId"/>) leaves the fleet and the
        /// grid leaves <see cref="PendingFleetAction.EscortEntityIds"/>. The lead and the rest of the
        /// squadron fly on.</summary>
        public static void WriteOffEscort(ref PendingFleetAction fleet, long escortEid, int slot, string prefabId,
                                          IPrefabCatalogue cat)
        {
            RemoveHull(ref fleet, slot, prefabId, cat);
            List<long> escorts = fleet.EscortEntityIds;
            if (escorts == null) return;
            escorts.Remove(escortEid);
            if (escorts.Count == 0) fleet.EscortEntityIds = null;
        }

        /// <summary>Writes off one hull of <paramref name="fleet"/> (slot <paramref name="slot"/>, or
        /// <paramref name="prefabId"/>) and nothing else: the fleet's grids are the caller's. What a fight
        /// site does for a hull it holds that was shot out of action, on its field or leaving it, whose
        /// grid no longer belongs to the fleet (FAC-492).</summary>
        public static void WriteOffHull(ref PendingFleetAction fleet, int slot, string prefabId, IPrefabCatalogue cat)
        {
            RemoveHull(ref fleet, slot, prefabId, cat);
        }

        private static void RemoveHull(ref PendingFleetAction fleet, int slot, string prefabId, IPrefabCatalogue cat)
        {
            if (slot >= 0 && fleet.Squadron != null && fleet.Squadron.Count > 0) fleet.MarkHullLostAt(slot);
            else fleet.MarkHullLost(prefabId);
            HullDamage.RescaleBands(ref fleet, cat);
        }

        /// <summary>The kill confirmation told to the player whose fire disabled the hull, e.g.
        /// <c>Kill confirmed: [SPRT] guard DS_Pirate_Scimitar disabled at N165.</c></summary>
        public static string KillSentence(string victimTag, ActionKind kind, string prefabId, string where)
        {
            return "Kill confirmed: [" + (victimTag ?? "?") + "] " + kind.ToString().ToLowerInvariant()
                   + " " + (string.IsNullOrEmpty(prefabId) ? "hull" : prefabId)
                   + " disabled" + (string.IsNullOrEmpty(where) ? "" : " at " + where) + ".";
        }
    }

    /// <summary>
    /// Disabled hulls waiting out <see cref="DisabledHull.DespawnDelay"/> before their grids are
    /// closed, keyed by entity id. Bounded by the hulls disabled in the last delay's length.
    /// </summary>
    public sealed class DisabledHullQueue
    {
        public struct Entry
        {
            public long EntityId;
            public string FleetLogId;
            public string ActorTag;
            public DateTime DisabledUtc;
            public DateTime DueUtc;
        }

        private readonly Dictionary<long, Entry> _byEid = new Dictionary<long, Entry>();

        public int Count { get { return _byEid.Count; } }

        public bool Contains(long entityId) { return _byEid.ContainsKey(entityId); }

        /// <summary>Queues <paramref name="entityId"/>, disabled at <paramref name="disabledUtc"/>, to
        /// be closed <see cref="DisabledHull.DespawnDelay"/> later. A grid already queued keeps its
        /// first due time.</summary>
        public void Add(long entityId, string fleetLogId, string actorTag, DateTime disabledUtc)
        {
            if (entityId <= 0 || _byEid.ContainsKey(entityId)) return;
            _byEid[entityId] = new Entry
            {
                EntityId = entityId,
                FleetLogId = fleetLogId,
                ActorTag = actorTag,
                DisabledUtc = disabledUtc,
                DueUtc = disabledUtc + DisabledHull.DespawnDelay,
            };
        }

        public bool Remove(long entityId) { return _byEid.Remove(entityId); }

        /// <summary>Moves every entry due at <paramref name="nowUtc"/> into <paramref name="into"/> and
        /// out of the queue; returns how many.</summary>
        public int TakeDue(DateTime nowUtc, List<Entry> into)
        {
            if (_byEid.Count == 0) return 0;
            int before = into.Count;
            foreach (KeyValuePair<long, Entry> kv in _byEid)
                if (nowUtc >= kv.Value.DueUtc) into.Add(kv.Value);
            for (int i = before; i < into.Count; i++) _byEid.Remove(into[i].EntityId);
            return into.Count - before;
        }

        /// <summary>Every queued entry, due or not, into <paramref name="into"/>, and empties the queue.</summary>
        public int TakeAll(List<Entry> into)
        {
            int n = _byEid.Count;
            foreach (KeyValuePair<long, Entry> kv in _byEid) into.Add(kv.Value);
            _byEid.Clear();
            return n;
        }
    }
}
