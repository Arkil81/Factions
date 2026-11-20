namespace Factions.Core
{
    /// <summary>Aggregated counters and timing for one Trade sweep. Stopwatch timestamp ticks are
    /// used so the host can aggregate Core-only data without any Torch/SE dependency.
    ///
    /// <para>The counters follow the pass they measure: how many partners were examined, how many
    /// unserved demand rows were seen, how many of those became vectors, and what each vector's
    /// dispatch produced. There is no route-selection decomposition because there is no route
    /// selection -- a vector's source is the nearest consenting node and its destination is the row's
    /// own node. See docs/trade-haul-shaped.md.</para></summary>
    public sealed class TradeTickTelemetry
    {
        /// <summary>Factions visited this sweep. Counted on the buy pass alone, so this stays a
        /// roster count rather than twice one -- the sweep walks the roster once per direction.</summary>
        public int Factions { get; internal set; }
        /// <summary>Factions that cleared dormancy, autonomy, an empty whitelist and the in-flight
        /// fleet cap. Counted on the buy pass alone, for the same reason as <see cref="Factions"/>.</summary>
        public int EligibleFactions { get; internal set; }
        /// <summary>(actor, partner) pairs examined, across both directions.</summary>
        public int Partners { get; internal set; }
        /// <summary>Unserved demand rows read, after folding rows that name the same (node, item).</summary>
        public int UnservedSeen { get; internal set; }
        /// <summary>Rows that found a consenting source and became a <see cref="MaterialVector"/>.</summary>
        public int Vectors { get; internal set; }
        /// <summary>Sell-direction convoys dispatched (seller flies to a partner's row).</summary>
        public int Dispatched { get; internal set; }
        /// <summary>Buy-direction convoys dispatched (buyer flies out to a partner's stock).</summary>
        public int Procured { get; internal set; }
        /// <summary>Vectors with no hull anywhere in the acting faction's estate that could fly them,
        /// which staged a <c>Ship:Trade</c> demand instead. Always equal to <see cref="Tier3"/>;
        /// carried separately because the acceptance question ("is trade hull-starved?") is asked of
        /// this name.</summary>
        public int NoShip { get; internal set; }
        /// <summary>Rows no whitelisted partner would part with the stock for: nothing had routable
        /// surplus, or every holder's own scarcity sat above the seller-consent ceiling.</summary>
        public int RefusedConsent { get; internal set; }
        /// <summary>Whitelisted partners skipped because <see cref="Diplomacy.TradeBarred"/> held for
        /// the pair: either side hostile toward the other, or either side embargoing the other.
        /// Counted once per (actor, partner) per pass.</summary>
        public int Barred { get; internal set; }
        /// <summary>Whitelisted player-faction partners skipped because their switch for the
        /// direction they would trade in is off (<see cref="FactionState.TradeSellOptIn"/> when the
        /// actor buys, <see cref="FactionState.TradeBuyOptIn"/> when it sells).</summary>
        public int OptedOut { get; internal set; }
        /// <summary>Rows skipped because some faction -- or a player contract -- already has an
        /// in-flight convoy claiming that (delivery node, item).</summary>
        public int Claimed { get; internal set; }
        /// <summary>Convoys <c>AssessConvoy</c> judged not worth their hull, which handed the hull
        /// back rather than flying a dribble.</summary>
        public int AssessRefused { get; internal set; }
        /// <summary>Hull taken at the loading node.</summary>
        public int Tier1 { get; internal set; }
        /// <summary>Hull fetched from elsewhere in the estate, flying a Pickup leg first.</summary>
        public int Tier2 { get; internal set; }
        /// <summary>No hull: a <c>Ship:Trade</c> demand was staged and nothing flew.</summary>
        public int Tier3 { get; internal set; }
        /// <summary>Faction passes that stopped at the treasury/window budget gate.</summary>
        public int BudgetBlocked { get; internal set; }

        // ---- resumable sweep ---------------------------------------------------------------------
        public int Yields { get; internal set; }
        public int Restarts { get; internal set; }
        public int SweepsCompleted { get; internal set; }

        // ---- phase ticks ---------------------------------------------------------------------------
        public long EligibilityFilterTicks { get; internal set; }
        /// <summary>Everything from the first vector formed to the last convoy dispatched, inclusive
        /// of the shared core's own work.</summary>
        public long DispatchTicks { get; internal set; }
    }
}
