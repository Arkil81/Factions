namespace Factions.Wire
{
    /// <summary>
    /// Whether a fleet on the wire is a raid holding at its target and fighting there right now, as
    /// opposed to one still flying at it. Both client surfaces that distinguish the two need the
    /// same answer, so the rule lives here rather than once per feature -- the same reason
    /// <see cref="DeadReckon"/> does, and it gains the same thing: it is exercised by the wire tests
    /// instead of only by a live client.
    ///
    /// <para><b>Why this is derived rather than sent.</b> <see cref="WireMapFleet"/> carries the
    /// fleet's <c>ActionKind</c> but nothing about its current leg, and adding a field for one is a
    /// layout change -- a protocol bump, both server-side checkouts moving together, and every
    /// linked client refused its link until its player restarts the game. That is a steep price for
    /// a marker, and it is avoidable, because the server already tells us by construction.</para>
    ///
    /// <para><b>The guarantee this stands on.</b> Core's <c>AdvanceToNextStop</c> gives a dwell leg
    /// the <i>same point</i> for its origin and its target, so the interpolated position is an
    /// identical vector at every timestamp and the host's velocity sample -- a finite difference
    /// over exactly that interpolation -- cancels to <b>exactly</b> zero. Not nearly zero. In
    /// transit it is never zero: the progress curve saturates at precisely the value the ETA is set
    /// from, and an arrived fleet has already been dropped from the list, so there is no window in
    /// which a moving fleet reads as stationary. <c>DwellStationarityTests</c> in Factions.Core.Tests
    /// pins that property against the Core code that produces it.</para>
    ///
    /// <para><b>Do not soften the comparison to an epsilon.</b> The exactness is the mechanism, not
    /// a lucky rounding: a tolerance would start catching fleets that are merely slow, and drifting
    /// ones near the end of a leg would drop in and out of "fighting" between samples.</para>
    /// </summary>
    public static class FleetEngagement
    {
        // Core ActionKind ordinals, as the wire carries them. Named here rather than left as bare
        // numbers at each call site: a client reading `Kind == 8` says nothing about what 8 is.
        public const byte ReinforceKind = 3;
        public const byte RetaliateKind = 6;
        public const byte RaidKind = 8;
        public const byte ConquerKind = 19;

        /// <summary>The three kinds that hold at their target for an engagement dwell instead of
        /// resolving the moment they arrive. Mirrors Core's own <c>EngagesOnArrival</c>; if a fourth
        /// combat kind is ever added there, it belongs here too.</summary>
        public static bool EngagesOnArrival(byte actionKind)
        {
            return actionKind == RaidKind
                || actionKind == RetaliateKind
                || actionKind == ConquerKind;
        }

        /// <summary>
        /// True while this fleet is holding at its target for its engagement dwell.
        ///
        /// <para><b>The kind test is load-bearing, not defensive.</b> A zero velocity sample
        /// identifies a <i>dwell</i>, not an engagement: a Mine leg is stationary by the identical
        /// mechanism and runs longer than a raid's, so velocity alone would report every mining
        /// operation in sensor range as a raid in progress. Removing either half of this conjunction
        /// breaks it, in opposite directions.</para>
        /// </summary>
        public static bool IsEngaging(
            byte actionKind, double velXPerMin, double velYPerMin, double velZPerMin)
        {
            return EngagesOnArrival(actionKind)
                && velXPerMin == 0.0
                && velYPerMin == 0.0
                && velZPerMin == 0.0;
        }

        /// <summary>As above, read straight off a map fleet -- the shape every caller actually
        /// holds.</summary>
        public static bool IsEngaging(WireMapFleet fleet)
        {
            return IsEngaging(fleet.Kind, fleet.VelXKmPerMin, fleet.VelYKmPerMin, fleet.VelZKmPerMin);
        }

        /// <summary>The kind that holds the same dwell in order to DEFEND the node it is parked on.
        /// <c>Guard</c> is not one: a Guard stands a duty window after resolving, not a dwell leg.
        /// </summary>
        public static bool DefendsOnArrival(byte actionKind)
        {
            return actionKind == ReinforceKind;
        }

        /// <summary>
        /// True while this fleet is holding at a node it is defending -- the same stationarity test
        /// as <see cref="IsEngaging(byte, double, double, double)"/>, against the other half of the
        /// dwell.
        ///
        /// <para>Kept apart from the attack reading rather than folded into a "dwelling" one,
        /// because the two are drawn differently on purpose: a garrison standing at a station is
        /// not an alarm, and a surface that cannot tell them apart paints a defender's own hull as
        /// an attack on its own node. On a live board the reinforces outnumber the raids.</para>
        /// </summary>
        public static bool IsHoldingDefence(
            byte actionKind, double velXPerMin, double velYPerMin, double velZPerMin)
        {
            return DefendsOnArrival(actionKind)
                && velXPerMin == 0.0
                && velYPerMin == 0.0
                && velZPerMin == 0.0;
        }

        /// <summary>As above, read straight off a map fleet.</summary>
        public static bool IsHoldingDefence(WireMapFleet fleet)
        {
            return IsHoldingDefence(
                fleet.Kind, fleet.VelXKmPerMin, fleet.VelYKmPerMin, fleet.VelZKmPerMin);
        }
    }
}
