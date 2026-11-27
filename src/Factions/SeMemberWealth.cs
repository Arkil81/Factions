using Factions.Core;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Reads a faction's members' personal SE balances for the central bank's player-faction grant
    /// rule (<see cref="IMemberWealth"/>). SE keys a player's wallet by identity id and a faction's by
    /// faction id in the same account table, so each member is one account lookup.
    ///
    /// Game thread only, like <see cref="SeEconomyGateway"/>: <c>MyBankingSystem</c> is not
    /// thread-safe, and the income pass that calls this runs inside the frame update.
    /// </summary>
    public sealed class SeMemberWealth : IMemberWealth
    {
        public long MemberWealth(FactionState faction)
        {
            if (faction == null || faction.FactionId == 0) return 0;
            if (MyBankingSystem.Static == null || MyAPIGateway.Session == null || MyAPIGateway.Session.Factions == null)
                return 0;
            IMyFaction f = MyAPIGateway.Session.Factions.TryGetFactionById(faction.FactionId);
            if (f == null) return 0;

            long total = 0;
            foreach (var member in f.Members)
            {
                MyAccountInfo info;
                if (!MyBankingSystem.Static.TryGetAccountInfo(member.Key, out info) || info.Balance <= 0) continue;
                total = info.Balance > long.MaxValue - total ? long.MaxValue : total + info.Balance;
            }
            return total;
        }
    }
}
