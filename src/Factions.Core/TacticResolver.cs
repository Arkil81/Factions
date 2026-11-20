namespace Factions.Core
{
    /// <summary>Which rung of <see cref="TacticResolver"/> an order came from, lowest first.</summary>
    public enum TacticSource
    {
        Learned = 0,
        PlayerOverride = 1,
        Commander = 2,
    }

    /// <summary>What a squadron is told to do in a fight: how to fight, when to leave, whom to
    /// shoot and what to form on. Entity ids are the host's; zero means none.</summary>
    public struct TacticOrder
    {
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        public long TargetEntityId;
        public long AnchorEntityId;
        public TacticSource Source;
    }

    /// <summary>Everything <see cref="TacticResolver.Resolve"/> reads. The target and anchor are
    /// what the tactical layer found for itself; an order from a higher rung may name its own.</summary>
    public struct TacticRequest
    {
        /// <summary>The target the tactical layer acquired.</summary>
        public long TargetEntityId;

        /// <summary>What the squadron forms on — its lead.</summary>
        public long AnchorEntityId;

        /// <summary>A live order from a commanding player. Outranks everything.</summary>
        public TacticOrder? Commander;

        /// <summary>The owning player's standing override. Outranks the learned pick.</summary>
        public TacticOrder? PlayerOverride;

        /// <summary>The faction's learned pick for the fight this squadron is in — the arm and
        /// break-off the duel and the commit gates use for the same meeting — or null when its
        /// faction learns nothing there, which leaves <see cref="TacticResolver.DefaultArm"/>.</summary>
        public TacticOrder? Learned;
    }

    /// <summary>The single entry point that decides a squadron's tactic, as a ladder: a commander's
    /// order, then the player's override, then the faction's learned pick. See
    /// docs/fleet-tactics.md, "The layers".
    ///
    /// <para>The player-override rung is filled by the host's <c>FactionRuntime.TacticOverrideFor</c>,
    /// which today only <c>factions bench arm</c> sets. The commander rung's producer is a wing commander's
    /// order (<see cref="WingOrder"/>, flown by the host for each hull that obeys it).</para>
    ///
    /// <para>The learned rung: a materialised lead's request carries its faction's learned pick for
    /// the meeting it fights (<see cref="TacticRequest.Learned"/>, the same <see cref="TacticChoice"/>
    /// pick the duel and the commit gates use); without one it answers <see cref="DefaultArm"/> /
    /// <see cref="DefaultBreakOff"/>. Callers go through <see cref="Resolve"/> regardless, so the
    /// higher rungs slot in without touching them.</para></summary>
    public static class TacticResolver
    {
        public const TacticArm DefaultArm = TacticArm.Standoff;
        public const BreakOffLevel DefaultBreakOff = BreakOffLevel.Mid;

        public static TacticOrder Resolve(TacticRequest request)
        {
            if (request.Commander.HasValue)
                return Complete(request.Commander.Value, request, TacticSource.Commander);
            if (request.PlayerOverride.HasValue)
                return Complete(request.PlayerOverride.Value, request, TacticSource.PlayerOverride);
            return LearnedPick(request);
        }

        /// <summary>The anchor <see cref="Resolve"/> would name, by the same ladder, without
        /// resolving the rest of the order. For the per-frame formation pass, which needs only the
        /// grid to form on and must not draw a learned pick every frame.</summary>
        public static long ResolveAnchor(TacticRequest request)
        {
            TacticOrder? rung = request.Commander.HasValue ? request.Commander : request.PlayerOverride;
            if (rung.HasValue && rung.Value.AnchorEntityId != 0) return rung.Value.AnchorEntityId;
            return request.AnchorEntityId;
        }

        /// <summary>The faction's own choice: its learned pick when the request carries one, a fixed
        /// default otherwise.</summary>
        private static TacticOrder LearnedPick(TacticRequest request)
        {
            if (request.Learned.HasValue)
                return Complete(request.Learned.Value, request, TacticSource.Learned);
            return new TacticOrder
            {
                Arm = DefaultArm,
                BreakOff = DefaultBreakOff,
                TargetEntityId = request.TargetEntityId,
                AnchorEntityId = request.AnchorEntityId,
                Source = TacticSource.Learned,
            };
        }

        /// <summary>An order names its own target and anchor when it has them; a zero one takes the
        /// request's.</summary>
        private static TacticOrder Complete(TacticOrder order, TacticRequest request, TacticSource source)
        {
            if (order.TargetEntityId == 0) order.TargetEntityId = request.TargetEntityId;
            if (order.AnchorEntityId == 0) order.AnchorEntityId = request.AnchorEntityId;
            order.Source = source;
            return order;
        }
    }
}
