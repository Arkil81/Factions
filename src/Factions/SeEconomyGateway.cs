using Factions.Core;
using Sandbox.Game.GameSystems.BankingAndCurrency;

namespace Factions
{
    /// <summary>
    /// Economy realisation via vanilla SE banking. When a faction performs a Trade action the
    /// spent credits are pushed into the partner's SE wallet, making NPC economic activity
    /// observable in-world (faction balances change, players can see economic activity).
    ///
    /// Safe to call on the game thread only (MyBankingSystem is not thread-safe).
    /// </summary>
    public sealed class SeEconomyGateway : IEconomyGateway
    {
        /// <summary>Returns SE's own verdict on the write. <c>ChangeBalanceInternal</c> refuses --
        /// it does not clamp -- when the identifier holds no account, and when a debit would take the
        /// balance below zero, so a caller charging a wallet as a precondition can commit on true and
        /// abandon on false without a check-then-act window.</summary>
        public bool ChangeFactionBalance(long ownerIdentityId, long amount)
        {
            if (MyBankingSystem.Static == null || amount == 0) return false;
            return MyBankingSystem.ChangeBalance(ownerIdentityId, amount);
        }

        public bool TryGetFactionBalance(long factionId, out long balance)
        {
            MyAccountInfo info;
            if (MyBankingSystem.Static != null && MyBankingSystem.Static.TryGetAccountInfo(factionId, out info))
            {
                balance = info.Balance;
                return true;
            }
            balance = 0L;
            return false;
        }
    }
}
