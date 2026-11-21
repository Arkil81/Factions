using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// A materialised hull that loses its last thruster while under hostile fire is disabled
    /// (<see cref="DisabledHull"/>): written off at once -- a lead takes its fleet down the destroyed
    /// path under <see cref="DisabledHull.Cause"/>, an escort leaves its squadron -- while its grid
    /// stays in the world as a wreck for <see cref="DisabledHull.DespawnDelay"/> and is then closed.
    /// The player whose fire did it is told (<c>Kill confirmed: ...</c>) by chat and on the
    /// FactionsUI HUD. A hull that loses thrust with no hostile hit on record keeps the thrust-loss
    /// dematerialise.
    /// See docs/fleet-events.md, "A hull shot out of action is written off".
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private readonly DisabledHullQueue _disabledHulls = new DisabledHullQueue();
        private readonly List<DisabledHullQueue.Entry> _disabledDue = new List<DisabledHullQueue.Entry>();

        /// <summary>Set by the host when a client uplink exists: the player to tell, the tone, and the
        /// sentence, for the FactionsUI HUD.</summary>
        internal Action<ulong, CombatTone, string> KillNoticeSink { get; set; }

        /// <summary>Nexus authority: tells a player on an agent of their kill, by the identity that
        /// agent reported; answers their Steam id, or 0 when no agent reports them.</summary>
        internal Func<long, CombatTone, string, ulong> RemoteKillSink { get; set; }

        /// <summary>Whether hull <paramref name="eid"/>, whose thrust loss was just confirmed, was shot
        /// out of action: a hit from a grid outside its faction inside
        /// <see cref="DisabledHull.HostileHitWindow"/>. Names that grid and when it hit.</summary>
        private bool ShotOutOfAction(PendingFleetAction fleet, long eid, DateTime now,
                                     out long attackerGrid, out DateTime? hitUtc)
        {
            attackerGrid = 0;
            hitUtc = null;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return false;
            hitUtc = vanilla.LastHostileHit(eid, fleet.ActorTag, out attackerGrid);
            return DisabledHull.IsDisabled(true, hitUtc, now);
        }

        /// <summary>The lead of <paramref name="fleet"/> lost its thrust. When hostile fire did it the
        /// fleet is written off as destroyed under <see cref="DisabledHull.Cause"/>, its escorts close,
        /// and its grid is left as a wreck; answers whether that happened. Otherwise the caller
        /// dematerialises the fleet.</summary>
        private bool TryDisableLead(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            long eid = fleet.MaterialisedEntityId;
            long attackerGrid;
            DateTime? hitUtc;
            if (!ShotOutOfAction(fleet, eid, now, out attackerGrid, out hitUtc)) return false;

            string prefab = _spawner.GridPrefabId(eid) ?? fleet.ShipPrefabId;
            int slot = HullSlotOf(fleet, eid);
            string where = WhereFought(fleet, now);

            // The wreck is withheld first, so the escorts closing below cannot take it with them.
            WithholdWreck(eid, fleet, now);
            fleet = SettleHulls(fleet, eid, DisabledHull.Cause);
            // An attack that lost its lead keeps its escorts: they are its survivors, and fly out.
            bool attackLost = PendingFleetResolver.MissionHangsOnLead(fleet);
            int escortsClosed = attackLost ? 0 : DespawnEscorts(fleet.EscortEntityIds, out _);
            var written = fleet;
            DisabledHull.WriteOffLead(ref written, slot, prefab, _world.PrefabCatalogue, now);
            st.PendingFleets[fi] = written;

            long by;
            ulong told = ConfirmKill(fleet, prefab, where, attackerGrid, out by);
            _log.Write("FLEET_DESTROYED " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " last_damage=" + hitUtc.Value.ToString("HH:mm:ss")
                       + " stop=" + fleet.CurrentStopKind
                       + EscortsToken(escortsClosed)
                       + (attackLost ? " survivors_leave=" + EscortCount(written) : "")
                       + " why=disabled shotBy=" + attackerGrid + " by=" + by + " told=" + told
                       + " wreck_for=" + (int)DisabledHull.DespawnDelay.TotalSeconds + "s"
                       + " -> force-resolve next tick (ratio="
                       + (written.ForcedRatio ?? 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")");
            if (!string.IsNullOrEmpty(fleet.FleetId))
                OnPvEBountyTargetDestroyed?.Invoke(fleet.FleetId);
            DismissSiblings(st, fi, fleet, now);
            return true;
        }

        /// <summary>Escort <paramref name="escortEid"/> of <paramref name="swept"/> is still in the
        /// world and still its faction's. When its thrust loss is confirmed and hostile fire did it,
        /// its hull is written off, it leaves the squadron and its grid is left as a wreck; answers
        /// whether that happened. The lead and the rest fly on.</summary>
        private bool TryDisableEscort(PendingFleetAction fleet, ref PendingFleetAction swept, long escortEid, DateTime now)
        {
            if (!_spawner.ConsumeThrustLost(escortEid)) return false;
            long attackerGrid;
            DateTime? hitUtc;
            if (!ShotOutOfAction(fleet, escortEid, now, out attackerGrid, out hitUtc)) return false;

            string prefab = _spawner.GridPrefabId(escortEid);
            int slot = HullSlotOf(swept, escortEid);
            var vanilla = (VanillaFleetSpawner)_spawner;
            _escortEidScratch.Clear();
            _escortEidScratch.Add(escortEid);
            BankFightDamage(vanilla, ref swept, _escortEidScratch);
            WithholdWreck(escortEid, fleet, now);
            DisabledHull.WriteOffEscort(ref swept, escortEid, slot, prefab, _world.PrefabCatalogue);
            if (slot >= 0 && HasWaveSlots(swept)) WaveSlotRemoved(swept, slot, now);

            long by;
            ulong told = ConfirmKill(fleet, prefab, WhereFought(fleet, now), attackerGrid, out by);
            int remaining = swept.EscortEntityIds != null ? swept.EscortEntityIds.Count : 0;
            _log.Write("ESCORT_LOST " + fleet.LogId + " " + fleet.ActorTag
                       + " eid=" + escortEid + " prefab=" + (prefab ?? "?")
                       + " remaining=" + remaining
                       + " cause=disabled last_damage=" + hitUtc.Value.ToString("HH:mm:ss")
                       + " shotBy=" + attackerGrid + " by=" + by + " told=" + told
                       + " wreck_for=" + (int)DisabledHull.DespawnDelay.TotalSeconds + "s");
            return true;
        }

        /// <summary>Hands a disabled hull's grid to the wreck queue: out of every fleet mechanism and
        /// every tracking map, to be closed after <see cref="DisabledHull.DespawnDelay"/>.</summary>
        private void WithholdWreck(long eid, PendingFleetAction fleet, DateTime now)
        {
            _stalledSince.Remove(eid);
            _safezoneArrivedSince.Remove(eid);
            _engagementHeldSince.Remove(eid);
            _settleHeldReported.Remove(eid);
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla != null) vanilla.Withhold(eid);
            _disabledHulls.Add(eid, fleet.LogId, fleet.ActorTag, now);
        }

        /// <summary>Every frame: closes the wrecks whose delay is up. A wreck already gone is
        /// forgotten; one a player has taken for their own faction is left to them.</summary>
        private void SweepDisabledHulls(DateTime now)
        {
            if (_disabledHulls.Count == 0) return;
            _disabledDue.Clear();
            if (_disabledHulls.TakeDue(now, _disabledDue) == 0) return;
            var vanilla = _spawner as VanillaFleetSpawner;
            for (int i = 0; i < _disabledDue.Count; i++)
            {
                DisabledHullQueue.Entry e = _disabledDue[i];
                if (!_spawner.GridExists(e.EntityId))
                {
                    if (vanilla != null) vanilla.ForgetWithheld(e.EntityId);
                    _log.Write("DISABLED_HULL_GONE " + e.FleetLogId + " " + e.ActorTag + " eid=" + e.EntityId);
                    continue;
                }
                string owner = _spawner.GetGridOwnerFactionTag(e.EntityId);
                if (owner != null && !string.Equals(owner, e.ActorTag, StringComparison.OrdinalIgnoreCase))
                {
                    if (vanilla != null) vanilla.ForgetWithheld(e.EntityId);
                    _log.Write("DISABLED_HULL_KEPT " + e.FleetLogId + " " + e.ActorTag + " eid=" + e.EntityId
                               + " new_owner=" + owner);
                    continue;
                }
                int closed = _spawner.Despawn(e.EntityId);
                _log.Write("DISABLED_HULL_DESPAWNED " + e.FleetLogId + " " + e.ActorTag + " eid=" + e.EntityId
                           + " closed=" + closed
                           + " after=" + (int)(now - e.DisabledUtc).TotalSeconds + "s");
            }
        }

        /// <summary>Whether a grid is a disabled hull's wreck waiting out its delay, for the reaper.</summary>
        internal bool IsDisabledWreck(long entityId)
        {
            return _disabledHulls.Contains(entityId)
                   || (_spawner is VanillaFleetSpawner && ((VanillaFleetSpawner)_spawner).IsWithheld(entityId));
        }

        /// <summary>The node a hull was disabled at: its target while it stands, dwells or has
        /// arrived there; null in transit.</summary>
        private string WhereFought(PendingFleetAction fleet, DateTime now)
        {
            return fleet.HasArrived(now) || fleet.HoldsStation(now, _tuning) ? fleet.TargetNodeId : null;
        }

        /// <summary>Tells the player who owns <paramref name="attackerGrid"/> that their fire disabled
        /// the hull: a chat whisper and, when their client is linked, a HUD notice. Returns their
        /// Steam id, 0 when the grid has no player owner (an NPC's, or gone); <paramref name="by"/> is
        /// the owning identity.</summary>
        private ulong ConfirmKill(PendingFleetAction fleet, string prefab, string where, long attackerGrid, out long by)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            by = vanilla != null ? vanilla.OwnerIdentityOf(attackerGrid) : 0;
            if (by == 0) return 0;
            Func<long, CombatTone, string, ulong> remote = RemoteKillSink;
            if (remote != null)
            {
                ulong there = remote(by, CombatTone.Win, DisabledHull.KillSentence(fleet.ActorTag, fleet.Kind, prefab, where));
                if (there != 0) return there;
            }
            MySession session = MySession.Static;
            ulong steam = session != null && session.Players != null ? session.Players.TryGetSteamId(by) : 0UL;
            if (steam == 0) return 0;
            string sentence = DisabledHull.KillSentence(fleet.ActorTag, fleet.Kind, prefab, where);
            if (_chat != null)
            {
                try { _chat.SendMessageAsOther(FleetWhisperAuthor, sentence, Color.CornflowerBlue, steam); }
                catch { /* a chat failure must not stop the write-off */ }
            }
            Action<ulong, CombatTone, string> sink = KillNoticeSink;
            if (sink != null) sink(steam, CombatTone.Win, sentence);
            return steam;
        }
    }
}
