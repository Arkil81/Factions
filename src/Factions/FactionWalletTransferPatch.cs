using System;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Captures a member's Deposit or Withdraw on the vanilla faction terminal into the faction's sim
    /// ledger (FAC-186), so the two numbers stay equal in the one direction
    /// <see cref="LedgerMirror"/> cannot see.
    ///
    /// <para>The mirror pushes the ledger into the SE wallet. Vanilla's own transfer moves the wallet
    /// underneath it: <c>MyBankingSystem.RequestDeposit_Server</c> and <c>RequestWithdraw_Server</c>
    /// both settle through the private <c>Transfer_Internal</c>, between a member's personal account
    /// and the faction's. Without this, a 50,000-credit deposit would raise the wallet, the mirror's
    /// next push would take it straight back to the ledger's figure, and the member's money would be
    /// gone with nothing to show for it.</para>
    ///
    /// <para>Ordering makes this safe rather than double-counted: SE has already moved the wallet by
    /// the time the postfix runs, and crediting the ledger by the same amount leaves the mirror
    /// computing a zero delta on its next pass. The two accounts converge instead of chasing.</para>
    ///
    /// <para>A withdrawal is booked through <see cref="WorldModel.BookMemberWithdrawal"/>, not as a
    /// spend: the money stays with the faction's own people, where the central bank's grant rule reads
    /// it back through <see cref="IMemberWealth"/>, and it must not read as burn.</para>
    ///
    /// <para><c>Transfer_Internal</c> rather than the two RPC entry points: it is the only place that
    /// reports whether the transfer actually happened (SE validates online-ness, faction membership,
    /// and sufficient funds inside it, returning false on each), and it carries both identifiers and
    /// the amount. The <c>MyAccount</c> out-parameters are deliberately not declared — Harmony binds
    /// by name, and that type is not public to this assembly.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyBankingSystem), "Transfer_Internal")]  // private; string required
    internal static class FactionWalletTransferPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static bool _outcomeLogged;

        /// <summary>A Nexus agent's route: it has no ledger, so the transfer goes to the authority's.
        /// Set by the plugin there, null elsewhere. Takes the debited and credited faction tags (null
        /// for a person) and the amount.</summary>
        internal static Action<string, string, long> AgentSink;

        /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
        /// never throw: a throw fails its whole patch group (<c>HarmonyPatcher</c>).</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(typeof(MyBankingSystem), "Transfer_Internal");
            bool resolved = target != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Faction wallet transfers: ledger capture bound.");
                else
                    Log.Error("Faction wallet transfers: MyBankingSystem.Transfer_Internal did not "
                            + "resolve, so a member's Deposit or Withdraw on the faction terminal will "
                            + "not reach the sim ledger. The treasury mirror will undo such a transfer "
                            + "on its next push. Nothing else is affected.");
            }
            return resolved;
        }

        /// <summary>Both ends are checked independently: a transfer between two factions is one
        /// debit and one credit, and neither side may be assumed to be a person.</summary>
        [HarmonyPostfix]
        private static void Postfix(long fromIdentifier, long toIdentifier, long amount, bool __result)
        {
            if (!__result || amount <= 0) return;

            WorldModel world = FactionsPlugin.Instance?.Runtime?.World;
            if (world == null)
            {
                // Every faction end goes: which factions the authority tracks is its question.
                Action<string, string, long> sink = AgentSink;
                if (sink != null) sink(AnyFactionTag(fromIdentifier), AnyFactionTag(toIdentifier), amount);
                return;
            }

            string from = TagFor(fromIdentifier);
            if (from != null)
            {
                world.BookMemberWithdrawal(from, amount);
                Log.Info("Faction ledger debited " + amount + " for " + from + " (terminal withdrawal)");
            }

            string to = TagFor(toIdentifier);
            if (to != null)
            {
                world.Earn(to, amount);
                Log.Info("Faction ledger credited " + amount + " for " + to + " (terminal deposit)");
            }
        }

        /// <summary>The Factions tag behind an SE account identifier, or null when it belongs to a
        /// person or to a faction this plugin does not track. SE keys players and factions into one
        /// account dictionary by a plain identifier, so the faction lookup is what separates them.</summary>
        private static string AnyFactionTag(long identifier)
        {
            if (identifier == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            IMyFaction faction = MySession.Static.Factions.TryGetFactionById(identifier);
            return faction != null ? faction.Tag : null;
        }

        private static string TagFor(long identifier)
        {
            if (identifier == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            IMyFaction faction = MySession.Static.Factions.TryGetFactionById(identifier);
            if (faction == null) return null;
            return FactionsPlugin.Instance?.Runtime?.FindFaction(faction.Tag) != null ? faction.Tag : null;
        }
    }
}
