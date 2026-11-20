using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Keeps a faction's host-visible wallet equal to its sim treasury (FAC-181). Authority is
    /// unchanged: <see cref="WorldModel.GetTreasury"/> stays the number every gate reads, and this
    /// only stops the copy from drifting away from it. Before this existed
    /// <see cref="IEconomyGateway"/> was wired on the host and never called, so every NPC account
    /// still held its seeded 1,000,000,000 against a sim treasury three orders of magnitude smaller.
    ///
    /// <para>Player factions are mirrored on the same terms as NPC ones (FAC-186). A faction's
    /// account is the faction's, not any member's personal wallet, and every way credits reach it is
    /// already captured into the ledger first -- a store trade at one of its stations through
    /// <c>StationStoreManager.HandleStationTransaction</c>, and a member's own deposit or withdrawal
    /// on the vanilla faction terminal through <c>FactionWalletTransferPatch</c> -- so the push that
    /// follows finds the two numbers already equal and writes nothing.</para>
    ///
    /// Lives in Core rather than beside the SE adapter because none of the decisions here are
    /// SE-specific -- the host contributes only the tag-to-account lookup, passed in as a delegate
    /// over <see cref="FactionState"/>, and the gateway that performs the write.
    /// </summary>
    public sealed class LedgerMirror : ITreasuryMirror
    {
        private readonly IEconomyGateway _economy;
        private readonly Func<string, FactionState> _resolveFaction;
        private readonly Action<string> _log;
        // Tags already reconciled since this mirror was built. Only the first push per tag is logged:
        // that one is the rebase off the seed constant and is worth an operator seeing; the rest are
        // ordinary economy already visible in the ledger.
        private readonly HashSet<string> _reconciled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Tags currently held at the wallet floor, so a faction that stays in debt for a week costs
        // two log lines (entry and exit) rather than one per global pass.
        private readonly HashSet<string> _floored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LedgerMirror(IEconomyGateway economy, Func<string, FactionState> resolveFaction, Action<string> log)
        {
            if (economy == null) throw new ArgumentNullException("economy");
            if (resolveFaction == null) throw new ArgumentNullException("resolveFaction");
            _economy = economy;
            _resolveFaction = resolveFaction;
            _log = log;
        }

        public bool MirrorTreasury(string factionTag, long treasury)
        {
            if (string.IsNullOrEmpty(factionTag)) return true;

            FactionState st = _resolveFaction(factionTag);
            // Not on the live roster yet, or no account behind the tag: nothing to write. Returning
            // false keeps the tag dirty so a faction that registers later is reconciled then.
            if (st == null || st.FactionId == 0) return false;

            long target = WalletTarget(factionTag, treasury);

            long current;
            if (!_economy.TryGetFactionBalance(st.FactionId, out current)) return false;

            long delta = target - current;
            if (delta == 0)
            {
                _reconciled.Add(factionTag);
                return true;
            }
            if (!_economy.ChangeFactionBalance(st.FactionId, delta)) return false;

            if (_log != null && _reconciled.Add(factionTag))
                _log("TREASURY_REBASED " + factionTag + " wallet=" + current + " -> " + target
                     + " delta=" + delta);
            return true;
        }

        /// <summary>An SE wallet cannot go negative -- <c>MyBankingSystem.ChangeBalanceInternal</c>
        /// refuses outright any subtraction that would cross zero. A faction in debt is therefore
        /// shown at 0 rather than at its real ledger value, and that divergence is logged rather than
        /// applied quietly, because an unannounced clamp is the same drift this mirror exists to
        /// remove.</summary>
        private long WalletTarget(string factionTag, long treasury)
        {
            if (treasury < 0)
            {
                if (_log != null && _floored.Add(factionTag))
                    _log("TREASURY_MIRROR_FLOORED " + factionTag + " ledger=" + treasury
                         + " wallet=0 (wallets cannot go negative)");
                return 0;
            }
            if (_log != null && _floored.Remove(factionTag))
                _log("TREASURY_MIRROR_UNFLOORED " + factionTag + " ledger=" + treasury);
            return treasury;
        }
    }
}
