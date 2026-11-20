using System;

namespace Factions.Core
{
    /// <summary>
    /// A player besieging a station's safe zone, and the raid that follows once it is down.
    ///
    /// <para><b>Why this exists at all.</b> A Factions station's safe zone forbids Damage, Shooting
    /// and Grinding, so a player cannot scratch a protected station: rounds are absorbed and no
    /// damage event is ever raised. Without a siege there is no way for a player to start a fight
    /// with any faction that owns one, and no player-initiated raid is possible. Absorbed fire is
    /// therefore counted as an attack on the zone itself.</para>
    ///
    /// <para><b>Core owns the clocks, the host owns the SE entity.</b> Nothing here touches a
    /// <c>MySafeZone</c>; the host asks <see cref="ZoneShouldBeEnabled"/> and acts on the answer.
    /// That keeps the whole state machine testable without a live game, and means an unobserved
    /// node's siege still expires correctly.</para>
    ///
    /// <para><b>The pool is stored as a value and a timestamp, not ticked.</b> Regeneration is
    /// linear, so the level is derived on read. Nothing runs per frame, and a restart cannot lose
    /// progress that was never written.</para>
    /// </summary>
    public static class PlayerSiege
    {
        /// <summary>What a siege did to the pool this flush -- the caller logs and reacts on the
        /// transitions rather than re-deriving them.</summary>
        public struct SiegeStep
        {
            /// <summary>Pool level after the damage, 0..max.</summary>
            public double Integrity;
            /// <summary>True when this flush is what took the zone down.</summary>
            public bool Breached;
            /// <summary>True when this is the first damage of a fresh siege.</summary>
            public bool SiegeStarted;
        }

        /// <summary>Full pool for this node, or 0 when the mechanic is switched off.
        /// <para>Takes the node although it does not yet read it: a capability that reinforces a
        /// station's zone is planned, and it belongs here as a per-node term. Having the parameter
        /// now makes that a one-line change rather than a signature change through every call site
        /// and test.</para></summary>
        public static double MaxIntegrity(SpatialNode n, FactionTuning t)
        {
            return t == null || t.RaidZoneIntegrityPool <= 0 ? 0.0 : t.RaidZoneIntegrityPool;
        }

        /// <summary>The pool right now. Full for a node that has never been besieged, frozen while
        /// the zone is breached, otherwise the stored value plus linear regeneration since it was
        /// stored.</summary>
        public static double IntegrityOf(SpatialNode n, FactionTuning t, DateTime now)
        {
            double max = MaxIntegrity(n, t);
            if (max <= 0 || n == null) return 0.0;
            if (n.ZoneIntegrity < 0) return max;                       // never besieged
            if (n.ZoneBreachedUtc != default(DateTime)) return n.ZoneIntegrity;   // frozen in phase 2

            double regenSeconds = t.RaidZoneIntegrityRegenSeconds;
            if (regenSeconds <= 0) return max;
            double elapsed = (now - n.ZoneIntegrityAtUtc).TotalSeconds;
            if (elapsed <= 0) return Clamp(n.ZoneIntegrity, 0, max);
            return Clamp(n.ZoneIntegrity + max * (elapsed / regenSeconds), 0, max);
        }

        /// <summary>Absorbed fire from <paramref name="attackerFactionTag"/>. The owner's own fire is
        /// not an attack (<see cref="IsOwnersFire"/>): it starts no siege and leaves the pool untouched.
        /// Every other attacker, a factionless one included, is charged as below.</summary>
        public static SiegeStep AbsorbZoneDamage(SpatialNode n, FactionTuning t, DateTime now, double damage,
                                                 string attackerFactionTag)
        {
            if (IsOwnersFire(n, attackerFactionTag))
                return new SiegeStep { Integrity = n == null || t == null ? 0 : IntegrityOf(n, t, now) };
            return AbsorbZoneDamage(n, t, now, damage);
        }

        /// <summary>Absorbed player fire, charged against the zone. Returns what changed so the
        /// caller can log the transitions instead of inferring them.
        ///
        /// <para>The zone drops only when BOTH the siege dwell has elapsed and the pool is empty.
        /// Emptying it in one burst is not enough: the attacker has to hold it down for
        /// <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/>, which is the same time an NPC raid
        /// spends holding station.</para></summary>
        public static SiegeStep AbsorbZoneDamage(SpatialNode n, FactionTuning t, DateTime now, double damage)
        {
            var step = new SiegeStep();
            double max = MaxIntegrity(n, t);
            if (max <= 0 || n == null || damage <= 0) { step.Integrity = max; return step; }
            // Already through -- phase 2 fire belongs to the station, not the shield.
            if (n.ZoneBreachedUtc != default(DateTime)) { step.Integrity = n.ZoneIntegrity; return step; }

            double before = IntegrityOf(n, t, now);
            if (n.SiegeStartedUtc == default(DateTime))
            {
                n.SiegeStartedUtc = now;
                step.SiegeStarted = true;
            }

            n.ZoneIntegrity = Clamp(before - damage, 0, max);
            n.ZoneIntegrityAtUtc = now;
            n.SiegeLapseSinceUtc = default(DateTime);   // any damage clears the lapse clock
            step.Integrity = n.ZoneIntegrity;

            if (n.ZoneIntegrity <= 0
                && (now - n.SiegeStartedUtc).TotalSeconds >= t.RaidZoneSiegeDwellSeconds)
            {
                n.ZoneBreachedUtc = now;
                step.Breached = true;
            }
            return step;
        }

        /// <summary>Per-tick maintenance. Ends an abandoned siege and, with it, any raid that was
        /// riding on it. Returns true when something was reset, so the caller can log it.
        ///
        /// <para>A siege is abandoned when the pool has stood at full for
        /// <see cref="FactionTuning.RaidZoneRaidLapseSeconds"/>. The dwell then resets to zero: an
        /// attacker who walks away starts the five minutes again rather than resuming.</para>
        ///
        /// <para><paramref name="attackerPresent"/> holds a breached zone open. Phase 2 is sustained
        /// by presence, so a player who leaves loses the breach; while they are there the pool is
        /// frozen and cannot lapse.</para></summary>
        public static bool Maintain(SpatialNode n, FactionTuning t, DateTime now, bool attackerPresent)
        {
            if (n == null || t == null) return false;

            // A presence raid is answered before anything reads the pool: the node has no zone, so
            // the pool is not what expresses "the attacker is still here" and its size must not
            // decide whether this raid can be maintained. Leaving ends it outright rather than
            // dropping a phase -- presence was the whole raid, so there is no earlier phase to fall
            // back to, and the attacker starts the clock again on their next visit.
            if (IsPresenceRaid(n))
            {
                if (attackerPresent) return false;
                Reset(n);
                return true;
            }

            double max = MaxIntegrity(n, t);
            if (max <= 0) return false;
            if (n.ZoneIntegrity < 0 && n.SiegeStartedUtc == default(DateTime)) return false;

            if (n.ZoneBreachedUtc != default(DateTime))
            {
                if (attackerPresent) return false;      // holding it open
                // Left. Unfreeze the pool from empty and let it regenerate from now.
                n.ZoneBreachedUtc = default(DateTime);
                n.ZoneIntegrityAtUtc = now;
                return false;
            }

            if (IntegrityOf(n, t, now) < max)
            {
                n.SiegeLapseSinceUtc = default(DateTime);
                return false;
            }

            if (n.SiegeLapseSinceUtc == default(DateTime)) { n.SiegeLapseSinceUtc = now; return false; }
            if ((now - n.SiegeLapseSinceUtc).TotalSeconds < t.RaidZoneRaidLapseSeconds) return false;

            Reset(n);
            return true;
        }

        /// <summary>True for a node with a station that carries any siege state -- a siege running,
        /// a breach, or a won raid. <see cref="Maintain"/> must keep running for every such node
        /// whether or not its station is materialised: it is the only thing that ends a siege, and
        /// an attacker who leaves takes the materialisation with them. Nodes without a station are
        /// the presence path's (<see cref="IsPresenceRaid"/>).</summary>
        public static bool HasStationSiege(SpatialNode n)
        {
            return n != null
                && Economy.HasPhysicalStation(n)
                && (n.SiegeStartedUtc != default(DateTime)
                    || n.ZoneBreachedUtc != default(DateTime)
                    || n.PlayerRaidSucceededUtc != default(DateTime));
        }

        /// <summary>True while this node is being taken by presence alone -- an unfounded node
        /// sitting in phase 2. The two raid shapes are disjoint by construction: a node carrying a
        /// station can only be reached through its zone, and a node without one can only be reached
        /// by standing on it, so no node is ever under both.</summary>
        public static bool IsPresenceRaid(SpatialNode n)
        {
            return n != null
                && n.ZoneBreachedUtc != default(DateTime)
                && !Economy.HasPhysicalStation(n);
        }

        /// <summary>Presence against a node with no station on it: enters phase 2 directly and
        /// counts the raid as already successful, leaving the conquer offer's wait as the only clock
        /// that has to run. Neither earlier step has anything to act on -- there is no zone to empty
        /// and no blocks to damage -- so what an attacker spends here is time on station, and
        /// <see cref="Maintain"/> takes the raid back the moment they stop spending it.
        ///
        /// <para>Refuses a founded node, a node already under a raid, and a zero
        /// <see cref="FactionTuning.PresenceRaidRadiusKm"/>. Ownership and hostility are the
        /// caller's to check: neither is knowable here, and standing is the host's.</para></summary>
        public static bool BeginPresenceRaid(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || t.PresenceRaidRadiusKm <= 0) return false;
            if (Economy.HasPhysicalStation(n)) return false;
            if (n.PlayerRaidSucceededUtc != default(DateTime)) return false;

            n.SiegeStartedUtc = now;
            n.ZoneIntegrity = 0;
            n.ZoneIntegrityAtUtc = now;
            n.SiegeLapseSinceUtc = default(DateTime);
            n.ZoneBreachedUtc = now;
            n.PlayerRaidBlockDamage = 0;
            n.PlayerRaidSucceededUtc = now;
            n.PlayerPresenceStripUtc = default(DateTime);
            n.PresenceStrips = 0;
            return true;
        }

        /// <summary>Whether fire from <paramref name="attackerFactionTag"/> at this node's station may
        /// add phase-2 damage right now. The one answer to "who may add to this raid":
        /// <list type="bullet">
        /// <item>A breached zone (<see cref="SpatialNode.ZoneBreachedUtc"/>) is open to any fire --
        /// phase 2 is the station's state, not one attacker's.</item>
        /// <item>A zone that is down because a raid landed (<see cref="SpatialNode.SafeZoneDownUntilUtc"/>
        /// still in the future) is open only to the faction whose raid set that window
        /// (<see cref="SpatialNode.SafeZoneDownByTag"/>). Its players shooting a station their own
        /// fleet just opened are continuing their raid; anyone else shooting it is not. A pact
        /// partner is not the raiding faction: nothing in the siege models a pact, and the conquer
        /// offer goes to one faction.</item>
        /// </list>
        /// Refuses the owner's own tag, breached or not (<see cref="IsOwnersFire"/>), a node with no
        /// station (the presence path owns those), and a switched-off pool: <see cref="Maintain"/>
        /// cannot lift a breach when the pool is off, so entering one there would hold the zone down
        /// for good.</summary>
        public static bool MayAddRaidDamage(SpatialNode n, FactionTuning t, DateTime now, string attackerFactionTag)
        {
            if (n == null || t == null) return false;
            if (IsOwnersFire(n, attackerFactionTag)) return false;
            if (n.ZoneBreachedUtc != default(DateTime)) return true;
            if (MaxIntegrity(n, t) <= 0 || !Economy.HasPhysicalStation(n)) return false;
            if (string.IsNullOrEmpty(attackerFactionTag) || now >= n.SafeZoneDownUntilUtc) return false;
            return string.Equals(n.SafeZoneDownByTag, attackerFactionTag, StringComparison.Ordinal);
        }

        /// <summary>True when the fire comes from the faction that owns the node. That is never a raid,
        /// breached zone or not: counting it would let an owner strip their own hold. A factionless
        /// attacker (null tag) owns nothing, so their fire is never the owner's.</summary>
        public static bool IsOwnersFire(SpatialNode n, string attackerFactionTag)
        {
            return n != null && !string.IsNullOrEmpty(attackerFactionTag)
                && string.Equals(n.OwnerFactionTag, attackerFactionTag, StringComparison.Ordinal);
        }

        /// <summary>Enters phase 2 through a window a raid opened: the raiding faction's first fire at
        /// the station does what a breach does, so every later step -- block damage toward
        /// <see cref="FactionTuning.PlayerRaidBlockDamageForSuccess"/>, the hold strip per success,
        /// the conquer-offer clock, presence holding the breach, abandonment -- runs on the one
        /// state machine. Returns true only on the call that entered it.
        ///
        /// <para>The pool is set empty, because the raid that opened the window already did what
        /// phase 1 exists to do.
        /// <see cref="SpatialNode.SafeZoneDownUntilUtc"/> is not touched: while an attacker is present
        /// the breach holds the zone down as any breach does, and once they leave
        /// <see cref="Maintain"/> lifts the breach and the window's own deadline governs again.</para></summary>
        public static bool EnterOpenedRaid(SpatialNode n, FactionTuning t, DateTime now, string attackerFactionTag)
        {
            if (n == null || n.ZoneBreachedUtc != default(DateTime)) return false;
            if (!MayAddRaidDamage(n, t, now, attackerFactionTag)) return false;

            if (n.SiegeStartedUtc == default(DateTime)) n.SiegeStartedUtc = now;
            n.ZoneIntegrity = 0;
            n.ZoneIntegrityAtUtc = now;
            n.SiegeLapseSinceUtc = default(DateTime);
            n.ZoneBreachedUtc = now;
            return true;
        }

        /// <summary>Player block damage against a breached station. Returns true on each flush that
        /// meets the success criterion -- <see cref="FactionTuning.PlayerRaidBlockDamageForSuccess"/>
        /// cumulative damage, or, on the first success only, the station powered down. The caller
        /// applies a hold strip per true.
        ///
        /// <para><b>It fires repeatedly within one raid.</b> Sustained fire is sustained hold loss,
        /// the way repeated NPC raids are: the threshold is SUBTRACTED on each success rather than
        /// zeroed, so overkill carries into the next strip and a player is credited for every round
        /// that lands rather than only for the one that crossed the line.</para>
        ///
        /// <para><paramref name="poweredDown"/> is a state, not an event -- it stays true for as
        /// long as the station is dark -- so it can only produce the FIRST success. Every later one
        /// has to be bought with damage.</para></summary>
        public static bool RecordRaidDamage(SpatialNode n, FactionTuning t, DateTime now,
                                            double blockDamage, bool poweredDown)
        {
            if (n == null || t == null) return false;
            if (n.ZoneBreachedUtc == default(DateTime)) return false;     // not in a raid

            if (blockDamage > 0) n.PlayerRaidBlockDamage += blockDamage;
            bool first = n.PlayerRaidSucceededUtc == default(DateTime);
            double threshold = t.PlayerRaidBlockDamageForSuccess;
            bool succeeded = (poweredDown && first)
                          || (threshold > 0 && n.PlayerRaidBlockDamage >= threshold);
            if (!succeeded) return false;

            if (n.PlayerRaidBlockDamage >= threshold && threshold > 0)
                n.PlayerRaidBlockDamage -= threshold;
            if (first) n.PlayerRaidSucceededUtc = now;
            return true;
        }

        /// <summary>The earliest moment this node can actually change hands, or
        /// <see cref="DateTime.MaxValue"/> when no siege is running.
        ///
        /// <para>A defender only needs to arrive before the node is lost, and a siege says exactly
        /// when that is. Once the raid has succeeded the answer is exact -- the conquer offer is due
        /// at a known time. Before then it is the earliest the sequence permits: the dwell that has
        /// to elapse before the zone can break at all, plus the offer wait, with phase 2 taken as
        /// instantaneous. That is a floor, so it under-estimates the time a defender has rather than
        /// over-estimating it.</para>
        ///
        /// <para>Derived from the same knobs that govern the siege, so retuning the siege moves the
        /// defensive response with it.</para></summary>
        public static DateTime LossDeadlineUtc(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null) return DateTime.MaxValue;
            if (n.PlayerRaidSucceededUtc != default(DateTime))
                return n.PlayerRaidSucceededUtc.AddMinutes(t.PlayerConquerOfferMinutes);
            if (n.SiegeStartedUtc == default(DateTime)) return DateTime.MaxValue;
            return n.SiegeStartedUtc
                    .AddSeconds(t.RaidZoneSiegeDwellSeconds)
                    .AddMinutes(t.PlayerConquerOfferMinutes);
        }

        /// <summary>True once a successful raid has been held long enough for the node to be offered
        /// to its attacker, and the node's hold has been stripped to the same ceiling an NPC
        /// conquest has to reach (<see cref="FactionTuning.ConquerHoldCeiling"/>).
        ///
        /// <para>Hold damage persists on the node across lapsed sieges, so repeated visits
        /// accumulate toward the ceiling exactly as repeated raids by a faction do — a player takes
        /// a node over several raid cycles rather than one.</para></summary>
        public static bool ConquerOfferDue(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || n.PlayerRaidSucceededUtc == default(DateTime)) return false;
            if (n.HoldStrength > t.ConquerHoldCeiling) return false;
            return (now - n.PlayerRaidSucceededUtc).TotalMinutes >= t.PlayerConquerOfferMinutes;
        }

        /// <summary>Whether the host should have this node's safe zone switched on. The single
        /// question the host asks; it folds both reasons a zone is down -- a player siege that broke
        /// through, and an NPC raid that landed -- so neither can silently re-enable the other's.</summary>
        public static bool ZoneShouldBeEnabled(SpatialNode n, DateTime now)
        {
            if (n == null) return true;
            if (n.ZoneBreachedUtc != default(DateTime)) return false;
            return now >= n.SafeZoneDownUntilUtc;
        }

        /// <summary>0..1 for the zone's colour, 1 being untouched. The host renders the siege with
        /// it, which is the only feedback a player gets that their fire is doing anything.</summary>
        public static double IntegrityFraction(SpatialNode n, FactionTuning t, DateTime now)
        {
            double max = MaxIntegrity(n, t);
            if (max <= 0) return 1.0;
            return Clamp(IntegrityOf(n, t, now) / max, 0, 1);
        }

        /// <summary>Clears every siege and raid field. The raid is the unit: accumulated block
        /// damage does not survive an abandoned siege any more than the dwell does.</summary>
        public static void Reset(SpatialNode n)
        {
            if (n == null) return;
            n.ZoneIntegrity = -1;
            n.ZoneIntegrityAtUtc = default(DateTime);
            n.SiegeStartedUtc = default(DateTime);
            n.SiegeLapseSinceUtc = default(DateTime);
            n.ZoneBreachedUtc = default(DateTime);
            n.PlayerRaidBlockDamage = 0;
            n.PlayerRaidSucceededUtc = default(DateTime);
            n.PlayerPresenceStripUtc = default(DateTime);
            n.PresenceStrips = 0;
        }

        /// <summary>What a station's protection is doing right now, as one reading for every
        /// surface that draws it. The ordinals are duplicated by <c>Factions.Wire.WireZoneState</c>
        /// and must not be renumbered: the host casts this straight onto the wire, which is what
        /// keeps the in-game map, the node window and the safe zone the player is shooting from
        /// disagreeing about the same station.</summary>
        public enum ZoneState
        {
            /// <summary>Nothing is founded here, so there is no protection to have a state.</summary>
            NoStation = 0,
            /// <summary>Intact, whether or not it has ever been shot at.</summary>
            Protected = 1,
            /// <summary>A siege is running and the shield is still up.</summary>
            Besieged = 2,
            /// <summary>The shield is down and the station itself is being shot.</summary>
            Breached = 3,
            /// <summary>The raid has met its criterion; the conquer clock is running.</summary>
            Raided = 4,
            /// <summary>No siege, but the zone is off: a raid landed and the protection has not
            /// come back yet. The window a station can be taken in without firing a shot at a
            /// shield.</summary>
            Down = 5,
        }

        /// <summary>
        /// <paramref name="n"/>'s protection as one value, most urgent first: a won raid outranks a
        /// breach, a breach outranks a siege in progress, and a siege outranks a zone that is merely
        /// off. Reading them in any other order reports a station about to change hands as one
        /// waiting for its shield back.
        /// </summary>
        public static ZoneState StateOf(SpatialNode n, DateTime now)
        {
            if (n == null || n.StableStationId == 0) return ZoneState.NoStation;
            if (n.PlayerRaidSucceededUtc != default(DateTime)) return ZoneState.Raided;
            if (n.ZoneBreachedUtc != default(DateTime)) return ZoneState.Breached;
            if (n.SiegeStartedUtc != default(DateTime)) return ZoneState.Besieged;
            return now < n.SafeZoneDownUntilUtc ? ZoneState.Down : ZoneState.Protected;
        }

        /// <summary>When the shield can break at the earliest -- the moment the dwell has been held
        /// long enough -- or <see cref="DateTime.MinValue"/> when no siege is running. This is half
        /// the breach condition: the pool must also be empty at that moment, so a countdown at zero
        /// says the attacker has held it long enough, not that the zone has dropped.</summary>
        public static DateTime BreachDueUtc(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null || n.SiegeStartedUtc == default(DateTime)
                || n.ZoneBreachedUtc != default(DateTime))
                return DateTime.MinValue;
            return n.SiegeStartedUtc.AddSeconds(t.RaidZoneSiegeDwellSeconds);
        }

        /// <summary>How far a player raid has got toward taking the node, 0..1. Clamped: the last
        /// flush of damage overshoots the threshold, and a bar past 100% reads as a broken gauge
        /// rather than as a raid already won.</summary>
        public static double RaidProgressFraction(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null || t.PlayerRaidBlockDamageForSuccess <= 0) return 0.0;
            return Clamp(n.PlayerRaidBlockDamage / t.PlayerRaidBlockDamageForSuccess, 0, 1);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
