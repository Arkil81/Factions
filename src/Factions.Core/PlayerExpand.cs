using System;

namespace Factions.Core
{
    /// <summary>Why a player-driven Expand did not happen. <see cref="None"/> is the success case.</summary>
    public enum PlayerExpandRefusal
    {
        None = 0,
        /// <summary>Something already sits inside the separation in force where the node would land.
        /// Reported to the player as "occupied" and nothing else — never by whom, never where.</summary>
        Occupied,
        /// <summary>No SE economy to charge: the gateway is absent (offline sim, dry run) or the
        /// banking system is not loaded.</summary>
        EconomyUnavailable,
        /// <summary>SE refused the debit — no account behind the identifier, or the balance would
        /// have gone below zero. SE refuses such a subtraction outright rather than clamping it, so
        /// this is authoritative and nothing was charged.</summary>
        ChargeRefused,
        /// <summary>The caller could not be resolved to a faction to own the node.</summary>
        NoFaction,
        /// <summary>Over a planet, and the ground under the point could not be found
        /// (<see cref="NodePlacementProbe.NoGround"/>).</summary>
        NoGround,
    }

    /// <summary>Everything the host has to resolve before a player-driven Expand can be decided.</summary>
    public sealed class PlayerExpandRequest
    {
        /// <summary>Tag the new node will belong to.</summary>
        public string FactionTag;

        /// <summary>Live state for <see cref="FactionTag"/>, or null when this faction has never been
        /// registered with Factions. Null is a normal, expected case: registration happens on the
        /// first claim, and a faction that owns nothing must still be able to expand.</summary>
        public FactionState Owner;

        /// <summary>SE identity of the player committing this. Recorded, never charged.</summary>
        public long PlayerIdentityId;

        /// <summary>SE faction id — the account every Expand is charged to. SE keys players and
        /// factions into one account dictionary by a plain identifier, so this needs no seam of its
        /// own.</summary>
        public long FactionIdentityId;

        /// <summary>Where the player is standing. Snapped by the probe before anything is decided.</summary>
        public Vec3 PositionKm;
    }

    /// <summary>What an Expand would cost and where it would land, decided without charging or
    /// committing anything — what both the in-flight readout and the chat verb print.</summary>
    public sealed class PlayerExpandQuote
    {
        public NodePlacementProbe Probe;
        public long CostCredits;
        /// <summary>The faction account that would be debited — always the faction's own.</summary>
        public long ChargedIdentityId;
        /// <summary>What that account holds now, or -1 when there is no economy to ask.</summary>
        public long WalletBalance = -1;

        /// <summary>Credits still to deposit before an Expand here can be paid for; 0 when it can, or
        /// when the balance is unknown.</summary>
        public long Shortfall
        {
            get { return WalletBalance < 0 ? 0 : Math.Max(0L, CostCredits - WalletBalance); }
        }
    }

    /// <summary>Outcome of an attempted Expand. On a refusal nothing was charged and nothing created.</summary>
    public sealed class PlayerExpandResult
    {
        public PlayerExpandRefusal Refusal;
        public PlayerExpandQuote Quote;
        /// <summary>The node created; null on any refusal.</summary>
        public SpatialNode Node;
        public bool Ok { get { return Refusal == PlayerExpandRefusal.None; } }
    }

    /// <summary>
    /// A player faction creating a node where there is none (FAC-197) — the player's own Expand.
    /// Design: docs/economy-territory.md → "Expanding — creating a node where there is none".
    ///
    /// <para><b>Charge, then commit.</b> The debit is the gate. SE's own
    /// <c>MyBankingSystem.ChangeBalance</c> refuses a below-zero subtraction rather than clamping it,
    /// so a successful write is proof the money was there and is now gone — one atomic answer, with
    /// no window between reading a balance and spending against it, and no partial state to unwind.
    /// The alternative (read the balance, decide, then charge) is a check-then-act that a second
    /// purchase in the same tick walks straight through.</para>
    ///
    /// <para><b>The faction's wallet, and the ledger with it.</b> Every Expand is charged to the
    /// faction's own SE account, which members fund through the faction terminal's Deposit. The
    /// ledger is debited by the same amount once SE accepts, because <see cref="LedgerMirror"/>
    /// pushes the ledger into the wallet and would otherwise hand the charge back.</para>
    ///
    /// <para><b>Lives in Core</b> even though the wallet is SE's, because none of the decisions
    /// are: what it costs, whether the ground is free and where the node lands are all
    /// world-model questions. The host contributes the identifiers and
    /// <see cref="IEconomyGateway"/>.</para>
    /// </summary>
    public sealed class PlayerExpandService
    {
        private readonly WorldModel _world;
        private readonly IEconomyGateway _economy;
        private readonly Action<string> _log;

        public PlayerExpandService(WorldModel world, IEconomyGateway economy, Action<string> log)
        {
            if (world == null) throw new ArgumentNullException("world");
            _world = world;
            _economy = economy;
            _log = log;
        }

        /// <summary>What an Expand at <paramref name="req"/>'s position would land on and cost.
        /// Read-only — safe to run on every readout tick.</summary>
        public PlayerExpandQuote Quote(PlayerExpandRequest req)
        {
            if (req == null) throw new ArgumentNullException("req");

            // Always the faction's own account. Members fund it from their own wallets through the
            // faction terminal's Deposit, which reaches the ledger like any other deposit.
            long balance;
            return new PlayerExpandQuote
            {
                Probe = _world.ProbePlacement(req.PositionKm),
                CostCredits = _world.Tuning.PlayerExpandCost,
                ChargedIdentityId = req.FactionIdentityId,
                WalletBalance = _economy != null && req.FactionIdentityId != 0
                                && _economy.TryGetFactionBalance(req.FactionIdentityId, out balance) ? balance : -1,
            };
        }

        /// <summary>
        /// Charge for and create the node. <paramref name="ensureRegistered"/> brings the faction onto
        /// the Factions roster and is invoked <b>only after the money has moved</b> — a refused charge
        /// must not leave a registered landless faction behind as its only trace.
        /// </summary>
        public PlayerExpandResult Commit(PlayerExpandRequest req, Func<FactionState> ensureRegistered)
        {
            if (req == null) throw new ArgumentNullException("req");

            PlayerExpandQuote quote = Quote(req);
            var result = new PlayerExpandResult { Quote = quote };

            if (string.IsNullOrEmpty(req.FactionTag) || req.FactionIdentityId == 0)
                return Refuse(result, PlayerExpandRefusal.NoFaction, req);

            if (quote.Probe.NoGround)
                return Refuse(result, PlayerExpandRefusal.NoGround, req);
            if (!quote.Probe.Clear)
                return Refuse(result, PlayerExpandRefusal.Occupied, req);

            if (quote.CostCredits > 0)
            {
                if (_economy == null)
                    return Refuse(result, PlayerExpandRefusal.EconomyUnavailable, req);
                if (!_economy.ChangeFactionBalance(quote.ChargedIdentityId, -quote.CostCredits))
                    return Refuse(result, PlayerExpandRefusal.ChargeRefused, req);
            }

            // The wallet has moved; the ledger has to move with it, or the mirror's next push from
            // the ledger hands the charge straight back. The same rule the faction terminal's own
            // Deposit and Withdraw follow (FactionWalletTransferPatch). A faction registering now
            // starts its ledger from the wallet as it stands after the charge, so it is not debited
            // twice.
            if (req.Owner != null && quote.CostCredits > 0) _world.Spend(req.FactionTag, quote.CostCredits);
            if (req.Owner == null && ensureRegistered != null) ensureRegistered();

            // The RAW position, not the probe's placed one. SeedNodeAt projects whatever it is
            // given, and the probe projected this same raw point -- so both run the placement rules
            // exactly once, from the same input, and the node cannot land anywhere the player was not
            // quoted. Handing it the already-placed point would project a second time, and a snap is
            // not guaranteed to be its own fixed point: re-classifying a point that has moved onto a
            // surface or into an orbit band can pick a different zone than the snap that put it there.
            result.Node = _world.SeedNodeAt(req.FactionTag, req.PositionKm);

            if (_log != null)
            {
                _log("PLAYER_EXPAND " + req.FactionTag + " node=" + result.Node.Id
                     + " zone=" + result.Node.Zone
                     + " at=" + Describe(quote.Probe.PlacedKm)
                     + " snap=" + quote.Probe.SnapDistanceKm.ToString("0.0") + "km"
                     + " sep=" + quote.Probe.SeparationKm.ToString("0.#") + "km"
                     + " nearest=" + DescribeNearest(quote.Probe.NearestNodeKm)
                     + " cost=" + quote.CostCredits
                     + " wallet=faction id=" + quote.ChargedIdentityId);
            }
            return result;
        }

        private PlayerExpandResult Refuse(PlayerExpandResult result, PlayerExpandRefusal reason,
                                          PlayerExpandRequest req)
        {
            result.Refusal = reason;
            if (_log != null)
            {
                _log("PLAYER_EXPAND_REFUSED " + (req.FactionTag ?? "?") + " reason=" + reason
                     + " at=" + Describe(result.Quote.Probe.PlacedKm)
                     + " sep=" + result.Quote.Probe.SeparationKm.ToString("0.#") + "km"
                     + " nearest=" + DescribeNearest(result.Quote.Probe.NearestNodeKm)
                     + " cost=" + result.Quote.CostCredits
                     + " wallet=faction id=" + result.Quote.ChargedIdentityId
                     + (result.Quote.WalletBalance >= 0 ? " balance=" + result.Quote.WalletBalance : ""));
            }
            return result;
        }

        private static string Describe(Vec3 p)
        {
            return p.X.ToString("0.#") + "," + p.Y.ToString("0.#") + "," + p.Z.ToString("0.#");
        }

        private static string DescribeNearest(double km)
        {
            return km == double.MaxValue ? "none" : km.ToString("0.#") + "km";
        }
    }
}
