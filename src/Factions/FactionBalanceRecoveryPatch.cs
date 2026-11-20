using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Stops vanilla SE topping every faction's wallet back up toward its initial currency (FAC-187).
    ///
    /// <para><c>MySessionComponentEconomy.UpdateStations()</c> calls the private
    /// <c>UpdateUpdateFactionBalance(MyFaction)</c> for <b>every</b> faction in the session on each
    /// <c>EconomyTickInSeconds</c> tick (600 s), and that method does:</para>
    /// <code>
    /// amount = (PerFactionInitialCurrency - balance) * FactionBalanceRecoveryModifier;  // 1e9, 0.4
    /// MyBankingSystem.ChangeBalance(faction.FactionId, amount);                         // if Stations.Count != 0
    /// </code>
    /// <para>Measured live 2026-08-30: ~400,000,000 credited to each of twelve factions every tick,
    /// against sim treasuries of 250,000-450,000. The treasury mirror overwrites that within a frame
    /// for any faction whose ledger moves, so the two fought each other every cycle; a faction the
    /// mirror never re-pushes (dormant <c>UNKN</c>) simply kept the credit. World money supply held
    /// ~5.2B — thirteen factions' worth of recovery credit — rather than the ~4.6M the factions
    /// actually hold.</para>
    ///
    /// <para>This replaces the equivalent data edit (<c>FactionBalanceRecoveryModifier</c> 0.4 -> 0 in
    /// <c>Content\Data\Game\SessionComponents_Economy.sbc</c>), which worked but sits in base game
    /// content and is reverted silently by any SE update or file verify. The patch ships with the
    /// plugin instead.</para>
    ///
    /// <para>It also closes the same hole on the player side: the vanilla loop is over every faction,
    /// so a player faction owning a station was being topped toward a billion credits too.</para>
    ///
    /// <para>On a deferred server (FAC-507) it suppresses too. Production 2026-11-20: Main, deferred,
    /// moved every NPC wallet one 40% step toward 1e9 on its first economy tick while Dawn held them,
    /// and no mirror runs there to undo it. The decision is <see cref="VanillaBalanceRecovery.Suppress"/>.</para>
    /// </summary>
    [HarmonyPatch(typeof(MySessionComponentEconomy), "UpdateUpdateFactionBalance")]  // private; string required
    internal static class FactionBalanceRecoveryPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>True once the prefix has actually skipped a call, so "the patch is bound" and
        /// "the patch is doing something" stay separate claims.</summary>
        internal static bool HasSuppressed;

        /// <summary>Harmony calls this once for the class and again for the patch method, so the
        /// outcome is logged only the first time — a diagnostic channel that says the same thing
        /// twice per boot invites the reader to wonder which one matters.</summary>
        private static bool _outcomeLogged;

        /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
        /// never throw: a throw fails its whole patch group (<c>HarmonyPatcher</c>). Returning false
        /// from a class-level <c>HarmonyPrepare</c> skips the class before the target method is
        /// resolved.</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(
                typeof(MySessionComponentEconomy), "UpdateUpdateFactionBalance", new[] { typeof(MyFaction) });
            bool resolved = target != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Vanilla faction-balance recovery: suppression patch bound.");
                else
                    Log.Error("Vanilla faction-balance recovery: MySessionComponentEconomy."
                            + "UpdateUpdateFactionBalance(MyFaction) did not resolve, so the vanilla top-up "
                            + "toward PerFactionInitialCurrency still runs. Faction wallets will be credited "
                            + "back toward 1e9 on every economy tick and will disagree with the sim ledger "
                            + "for any faction the treasury mirror does not re-push. Nothing else is affected.");
            }
            return resolved;
        }

        /// <summary>Returns false to skip the vanilla body entirely; see
        /// <see cref="VanillaBalanceRecovery.Suppress"/> for when.</summary>
        [HarmonyPrefix]
        private static bool Prefix()
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            bool deferred = SimulationGate.Deferred;
            FactionTuning runtimeTuning = plugin?.Runtime?.World?.Tuning;
            if (!VanillaBalanceRecovery.Suppress(deferred, runtimeTuning, plugin?.Tuning)) return true;

            if (!HasSuppressed)
            {
                HasSuppressed = true;
                Log.Info(deferred
                    ? "Vanilla faction-balance recovery: first top-up suppressed on a deferred server. "
                      + "Faction wallets here move by trade only."
                    : "Vanilla faction-balance recovery: first top-up suppressed. "
                      + "Faction wallets now follow the sim ledger alone.");
            }
            return false;
        }
    }
}
