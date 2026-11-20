using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a wing commander can tell their faction's hulls (docs/fleet-tactics.md,
    /// Package 2). The host maps the wire's command byte onto this by name.</summary>
    public enum WingCommand
    {
        FormOnMe = 1,
        FocusTarget = 2,
        Hold = 3,
        BreakOff = 4,
        Return = 5,
        Release = 6,
        /// <summary>A station's antenna calls for protection (<see cref="StationCall"/>): not an
        /// order of the presser's own wing but the station's.</summary>
        ProtectMe = 7,
    }

    /// <summary>Where a command came from: a toolbar press on the presser's client, a block the
    /// server runs (button panel, timer, event controller, programmable block), or the operator's
    /// <c>factions bench wing</c>, or the host itself — a materialised NPC station under attack
    /// calling through its antenna with nobody pressing anything.</summary>
    public enum WingVia
    {
        Client = 0,
        Block = 1,
        Bench = 2,
        Host = 3,
    }

    /// <summary>Why a command was refused, or why a hull's link dropped.</summary>
    public enum WingRefusal
    {
        None = 0,
        NoAntenna,
        AntennaDamaged,
        AntennaOff,
        AntennaUnpowered,
        NotBroadcasting,
        NotYourAntenna,
        NoPlayerFaction,
        NoHulls,
        NoTarget,
        TargetGone,
        TargetNotHostile,
        TargetOutOfBubble,
        TargetNotVisible,
        NoOrder,
        OutOfRange,
        OutOfBubble,
        Cut,
        /// <summary>A station call from an antenna that stands on no station.</summary>
        NotAStation,
        /// <summary>A station call from a station no node of its faction answers for.</summary>
        NoNode,
        /// <summary>A station call from a faction Factions does not run.</summary>
        NoFaction,
    }

    /// <summary>Which lock or pointer "my target" was read from, in the order
    /// <see cref="WingRules.PickTarget"/> tries them.</summary>
    public enum WingTargetSource
    {
        None = 0,
        PresserLock,
        Crosshair,
        PilotLock,
        CombatBlockLock,
        CockpitRetained,
    }

    /// <summary>An antenna as the host read it this second.</summary>
    public struct WingAntennaReading
    {
        public bool Exists;
        public bool Functional;
        public bool Enabled;
        public bool Powered;
        public bool Broadcasting;
        public double RadiusM;
        public long OwnerIdentityId;
        /// <summary>The owner's SE faction tag, null when the owner is in none.</summary>
        public string OwnerFactionTag;
        /// <summary>World metres.</summary>
        public Vec3 PosM;
    }

    /// <summary>A candidate target as the host read it: whether the entity is still there, whether
    /// the commander's relation to its owner is hostile, and where it is (world metres).</summary>
    public struct WingTargetReading
    {
        public long EntityId;
        public bool Exists;
        public bool Hostile;
        public Vec3 PosM;
    }

    /// <summary>The tactic a commanded fight was fought with: learned against the commanded arm as a
    /// demonstration (<see cref="TacticOutcome.Commanded"/>), and the stance the abstract duel fights
    /// for that side.</summary>
    public struct CommandedTactic
    {
        public TacticArm Arm;
        public BreakOffLevel BreakOff;

        /// <summary><paramref name="stance"/> with this arm and break-off; its mobility is kept.</summary>
        public DuelStance ApplyTo(DuelStance stance)
        {
            stance.Arm = Arm;
            stance.BreakOff = BreakOff;
            return stance;
        }
    }

    /// <summary>The wing's rules, SE-free: when an antenna carries an order, which hulls it reaches,
    /// which target a command names and whether that target may be named. The host reads the
    /// world into <see cref="WingAntennaReading"/> / <see cref="WingTargetReading"/> and asks here.</summary>
    public static class WingRules
    {
        /// <summary>How long a hull keeps its last order after its link drops, so a flickering power
        /// bus does not scatter the wing.</summary>
        public static readonly TimeSpan LinkGrace = TimeSpan.FromSeconds(10);

        /// <summary>A target further than this from the commander's grid is outside the bubble the
        /// command is about.</summary>
        public const double TargetBubbleM = 10000.0;

        /// <summary>A target is visible to the faction when the commander's grid or a wing hull is
        /// within this of it.</summary>
        public const double TargetSightM = 6000.0;

        /// <summary>A hull breaking off leaves its target until they are this far apart, then holds.</summary>
        public const double BreakOffSeparationM = 3000.0;

        /// <summary>Whether the antenna carries a commander's order: functional, on, powered,
        /// broadcasting at a radius, and owned by the commander or by their faction.</summary>
        public static WingRefusal CheckAntenna(WingAntennaReading a, long commanderIdentityId, string commanderFactionTag)
        {
            if (!a.Exists) return WingRefusal.NoAntenna;
            if (!a.Functional) return WingRefusal.AntennaDamaged;
            if (!a.Enabled) return WingRefusal.AntennaOff;
            if (!a.Powered) return WingRefusal.AntennaUnpowered;
            if (!a.Broadcasting || a.RadiusM <= 0) return WingRefusal.NotBroadcasting;
            if (!OwnedBy(a, commanderIdentityId, commanderFactionTag)) return WingRefusal.NotYourAntenna;
            return WingRefusal.None;
        }

        private static bool OwnedBy(WingAntennaReading a, long commanderIdentityId, string commanderFactionTag)
        {
            if (a.OwnerIdentityId == 0) return false;
            if (commanderIdentityId != 0 && a.OwnerIdentityId == commanderIdentityId) return true;
            return !string.IsNullOrEmpty(commanderFactionTag)
                && string.Equals(a.OwnerFactionTag, commanderFactionTag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a hull at <paramref name="hullPosM"/> is inside the antenna's current
        /// broadcast radius. Asked per hull, every read.</summary>
        public static bool InRange(WingAntennaReading a, Vec3 hullPosM)
        {
            return a.RadiusM > 0 && a.PosM.DistanceSquaredTo(hullPosM) <= a.RadiusM * a.RadiusM;
        }

        /// <summary>Whether a target may be named: it exists, the commander is hostile to it, it is
        /// inside <see cref="TargetBubbleM"/> of the commander's grid, and the commander's grid or a
        /// wing hull in <paramref name="observersM"/> is within <see cref="TargetSightM"/> of it.</summary>
        public static WingRefusal CheckTarget(WingTargetReading t, Vec3 commanderPosM, IReadOnlyList<Vec3> observersM)
        {
            if (t.EntityId == 0) return WingRefusal.NoTarget;
            if (!t.Exists) return WingRefusal.TargetGone;
            if (!t.Hostile) return WingRefusal.TargetNotHostile;
            if (t.PosM.DistanceSquaredTo(commanderPosM) > TargetBubbleM * TargetBubbleM) return WingRefusal.TargetOutOfBubble;
            if (t.PosM.DistanceSquaredTo(commanderPosM) <= TargetSightM * TargetSightM) return WingRefusal.None;
            if (observersM != null)
                for (int i = 0; i < observersM.Count; i++)
                    if (t.PosM.DistanceSquaredTo(observersM[i]) <= TargetSightM * TargetSightM) return WingRefusal.None;
            return WingRefusal.TargetNotVisible;
        }

        /// <summary>"My target", first non-zero in order. From a toolbar press (client or bench):
        /// the presser's own lock — locked or still locking — then the grid under their crosshair.
        /// From a block with no presser: the antenna grid's seated pilot's lock, a combat AI block's
        /// lock on that grid, then the lock a cockpit kept from its last pilot.</summary>
        public static long PickTarget(WingVia via, long presserLock, long crosshair, long pilotLock,
                                      long combatBlockLock, long cockpitRetained, out WingTargetSource source)
        {
            if (via == WingVia.Block)
            {
                if (pilotLock != 0) { source = WingTargetSource.PilotLock; return pilotLock; }
                if (combatBlockLock != 0) { source = WingTargetSource.CombatBlockLock; return combatBlockLock; }
                if (cockpitRetained != 0) { source = WingTargetSource.CockpitRetained; return cockpitRetained; }
            }
            else
            {
                if (presserLock != 0) { source = WingTargetSource.PresserLock; return presserLock; }
                if (crosshair != 0) { source = WingTargetSource.Crosshair; return crosshair; }
            }
            source = WingTargetSource.None;
            return 0;
        }

        /// <summary>Whether the command names a target.</summary>
        public static bool NeedsTarget(WingCommand command)
        {
            return command == WingCommand.FocusTarget;
        }

        /// <summary>Whether the command ends the order instead of changing it.</summary>
        public static bool EndsOrder(WingCommand command)
        {
            return command == WingCommand.Return || command == WingCommand.Release;
        }

        public static string Token(WingCommand command)
        {
            switch (command)
            {
                case WingCommand.FormOnMe:    return "form";
                case WingCommand.FocusTarget: return "focus";
                case WingCommand.Hold:        return "hold";
                case WingCommand.BreakOff:    return "break_off";
                case WingCommand.Return:      return "return";
                case WingCommand.Release:     return "release";
                case WingCommand.ProtectMe:   return "protect";
                default:                      return "unknown";
            }
        }

        /// <summary>The bench verb's command word: the <see cref="Token(WingCommand)"/> spelling, or
        /// "breakoff".</summary>
        public static bool TryParse(string word, out WingCommand command)
        {
            command = WingCommand.Release;
            if (string.IsNullOrEmpty(word)) return false;
            switch (word.ToLowerInvariant())
            {
                case "form":      command = WingCommand.FormOnMe; return true;
                case "focus":     command = WingCommand.FocusTarget; return true;
                case "hold":      command = WingCommand.Hold; return true;
                case "break_off":
                case "breakoff":  command = WingCommand.BreakOff; return true;
                case "return":    command = WingCommand.Return; return true;
                case "release":   command = WingCommand.Release; return true;
                case "protect":   command = WingCommand.ProtectMe; return true;
                default:          return false;
            }
        }

        public static string Token(WingRefusal refusal)
        {
            switch (refusal)
            {
                case WingRefusal.None:              return "none";
                case WingRefusal.NoAntenna:         return "no_antenna";
                case WingRefusal.AntennaDamaged:    return "antenna_damaged";
                case WingRefusal.AntennaOff:        return "antenna_off";
                case WingRefusal.AntennaUnpowered:  return "antenna_unpowered";
                case WingRefusal.NotBroadcasting:   return "not_broadcasting";
                case WingRefusal.NotYourAntenna:    return "not_your_antenna";
                case WingRefusal.NoPlayerFaction:   return "no_player_faction";
                case WingRefusal.NoHulls:           return "no_hulls_in_range";
                case WingRefusal.NoTarget:          return "no_target";
                case WingRefusal.TargetGone:        return "target_gone";
                case WingRefusal.TargetNotHostile:  return "target_not_hostile";
                case WingRefusal.TargetOutOfBubble: return "target_out_of_bubble";
                case WingRefusal.TargetNotVisible:  return "target_not_visible";
                case WingRefusal.NoOrder:           return "no_order";
                case WingRefusal.OutOfRange:        return "out_of_range";
                case WingRefusal.OutOfBubble:       return "out_of_bubble";
                case WingRefusal.Cut:               return "cut";
                case WingRefusal.NotAStation:       return "not_a_station";
                case WingRefusal.NoNode:            return "no_node";
                case WingRefusal.NoFaction:         return "no_faction";
                default:                            return "unknown";
            }
        }

        public static string Token(WingTargetSource source)
        {
            switch (source)
            {
                case WingTargetSource.PresserLock:     return "presser_lock";
                case WingTargetSource.Crosshair:       return "crosshair";
                case WingTargetSource.PilotLock:       return "pilot_lock";
                case WingTargetSource.CombatBlockLock: return "combat_block_lock";
                case WingTargetSource.CockpitRetained: return "cockpit_retained";
                default:                               return "none";
            }
        }
    }

    /// <summary>What one read of a hull's link changed; see <see cref="WingOrder.UpdateLink"/>.</summary>
    public enum WingLinkChange
    {
        None = 0,
        /// <summary>The hull came into the link and now obeys the order.</summary>
        Joined,
        /// <summary>The link dropped; the hull keeps its last order through <see cref="WingRules.LinkGrace"/>.</summary>
        Dropped,
        /// <summary>The link came back inside the grace.</summary>
        Restored,
        /// <summary>The grace ran out: the hull reverts to its faction's own tactics. Reported once.</summary>
        Lapsed,
    }

    /// <summary>One hull's link to an order.</summary>
    public sealed class WingLink
    {
        public DateTime? LostUtc;
        public WingRefusal LostReason;
        public bool Lapsed;
    }

    /// <summary>
    /// A commander's standing order to their wing: what shapes the hulls' flight and fight, and which
    /// hulls hold the link to it. One per commander; a new command changes it, <c>return</c> and
    /// <c>release</c> end it. Never persisted: a restart ends every order.
    ///
    /// <para><b>The commands compose.</b> <c>form</c> sets the anchor, the commander's grid;
    /// <c>focus</c> sets the target, kept through a later <c>form</c> or <c>hold</c>, so a formed
    /// wing attacks and then falls back into formation; <c>hold</c> drops the anchor and holds —
    /// at range on the target when there is one, else where it is; <c>break off</c> drops the anchor
    /// and disengages from the target to <see cref="WingRules.BreakOffSeparationM"/>, then holds.</para>
    ///
    /// <para><b>Links are per hull.</b> A hull obeys while it holds the link or is inside the grace
    /// after losing it (<see cref="Obeys"/>). A lapsed hull that comes back into the link rejoins
    /// while the order is live; the order ends once no hull obeys it.</para>
    /// </summary>
    public sealed class WingOrder
    {
        public long CommanderIdentityId;
        public string FactionTag;
        public long AntennaEntityId;
        /// <summary>Non-zero for a station's call (<see cref="StationCall"/>): the station grid, the
        /// order's anchor. Such an order reaches the station faction's armed hulls whether it is a
        /// player's or an NPC's, and it stands with no hull obeying it until the call ends.</summary>
        public long StationGridId;
        public WingVia Via;
        /// <summary>A bench-issued order reaches only this scenario's fleets; null for any other.</summary>
        public string BenchLabel;
        public DateTime IssuedUtc;
        public WingCommand LastCommand;

        /// <summary>The commander's grid, set by <c>form</c>; zero when the wing is not formed.</summary>
        public long AnchorEntityId;
        /// <summary>The focus target, set by <c>focus</c>; zero for none.</summary>
        public long TargetEntityId;
        public bool Holding;
        public bool BreakingOff;
        /// <summary>The operator cut the link (<c>factions bench wing &lt;label&gt; cut</c>): every hull
        /// reads it as lost.</summary>
        public bool Cut;

        private readonly Dictionary<long, WingLink> _links = new Dictionary<long, WingLink>();

        /// <summary>The order's key among the runtime's orders: the commander's identity for a
        /// commander's own order, the station grid negated for a station's call, so a station and
        /// its owner can each hold one.</summary>
        public long Key { get { return StationGridId != 0 ? -StationGridId : CommanderIdentityId; } }

        public IEnumerable<KeyValuePair<long, WingLink>> Links { get { return _links; } }
        public int LinkCount { get { return _links.Count; } }

        /// <summary>Changes the order by one command. <c>return</c> and <c>release</c> change
        /// nothing: they end the order, which is the caller's to drop.</summary>
        public void Apply(WingCommand command, long commanderGridId, long targetEntityId, DateTime utcNow)
        {
            LastCommand = command;
            IssuedUtc = utcNow;
            switch (command)
            {
                case WingCommand.FormOnMe:
                    AnchorEntityId = commanderGridId;
                    Holding = false;
                    BreakingOff = false;
                    break;
                case WingCommand.FocusTarget:
                    TargetEntityId = targetEntityId;
                    BreakingOff = false;
                    break;
                case WingCommand.Hold:
                    AnchorEntityId = 0;
                    Holding = true;
                    BreakingOff = false;
                    break;
                case WingCommand.BreakOff:
                    AnchorEntityId = 0;
                    Holding = false;
                    BreakingOff = true;
                    break;
            }
        }

        /// <summary>The order as <see cref="TacticResolver"/>'s commander rung: hold fights
        /// <see cref="TacticArm.Standoff"/>, break off leaves at <see cref="BreakOffLevel.High"/>, and
        /// otherwise the arm and break-off are the ones the learned rung would have given.</summary>
        public TacticOrder ToTacticOrder(TacticArm learnedArm, BreakOffLevel learnedBreakOff)
        {
            CommandedTactic t = Tactic(learnedArm, learnedBreakOff);
            return new TacticOrder
            {
                Arm = t.Arm,
                BreakOff = t.BreakOff,
                TargetEntityId = TargetEntityId,
                AnchorEntityId = AnchorEntityId,
                Source = TacticSource.Commander,
            };
        }

        /// <summary>The arm and break-off this order fights with, for the resolver and the learner.</summary>
        public CommandedTactic Tactic(TacticArm learnedArm, BreakOffLevel learnedBreakOff)
        {
            return new CommandedTactic
            {
                Arm = Holding ? TacticArm.Standoff : learnedArm,
                BreakOff = BreakingOff ? BreakOffLevel.High : learnedBreakOff,
            };
        }

        /// <summary>Whether a hull obeys this order now: it holds the link, or lost it inside the grace.</summary>
        public bool Obeys(long hullId)
        {
            WingLink l;
            return _links.TryGetValue(hullId, out l) && !l.Lapsed;
        }

        /// <summary>Whether a hull currently holds the link, grace excluded.</summary>
        public bool Holds(long hullId)
        {
            WingLink l;
            return _links.TryGetValue(hullId, out l) && !l.Lapsed && !l.LostUtc.HasValue;
        }

        /// <summary>One read of one hull's link. A hull that never held it and is not in it now is
        /// nothing to this order; a lapsed one reports nothing until it rejoins.</summary>
        public WingLinkChange UpdateLink(long hullId, bool linked, WingRefusal reason, DateTime utcNow)
        {
            WingLink l;
            if (!_links.TryGetValue(hullId, out l))
            {
                if (!linked) return WingLinkChange.None;
                _links[hullId] = new WingLink();
                return WingLinkChange.Joined;
            }
            if (l.Lapsed)
            {
                if (!linked) return WingLinkChange.None;
                l.Lapsed = false;
                l.LostUtc = null;
                l.LostReason = WingRefusal.None;
                return WingLinkChange.Joined;
            }
            if (linked)
            {
                if (!l.LostUtc.HasValue) return WingLinkChange.None;
                l.LostUtc = null;
                l.LostReason = WingRefusal.None;
                return WingLinkChange.Restored;
            }
            if (!l.LostUtc.HasValue)
            {
                l.LostUtc = utcNow;
                l.LostReason = reason;
                return WingLinkChange.Dropped;
            }
            if (utcNow - l.LostUtc.Value < WingRules.LinkGrace) return WingLinkChange.None;
            l.Lapsed = true;
            return WingLinkChange.Lapsed;
        }

        /// <summary>Forgets a hull that has left the world.</summary>
        public void Forget(long hullId)
        {
            _links.Remove(hullId);
        }

        /// <summary>Whether any hull still obeys.</summary>
        public bool AnyObeys()
        {
            foreach (KeyValuePair<long, WingLink> kv in _links)
                if (!kv.Value.Lapsed) return true;
            return false;
        }
    }
}
