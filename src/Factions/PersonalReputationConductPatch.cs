using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Sees every additive personal-reputation change SE makes, with the delta it was asked for, so a
    /// player-faction member's conduct reaches their stored record (docs/diplomacy-plan.md rule 4).
    ///
    /// <para><b>Why the entry point and not only the change event.</b>
    /// <c>FactionReputationChanged</c> reports the value SE stored, which for a member is their
    /// projected faction standing plus the delta, clamped to the world's bounds. A member of a faction
    /// at war sits at the hostile floor, so every point they lose by shooting that enemy is clamped
    /// away before the event sees it — and their own record would stay clean through the whole war.
    /// <c>AddFactionPlayerReputation(identity, faction, delta, reason, propagate, adminChange)</c> is
    /// where every vanilla conduct path converges — damage and grinding via
    /// <c>DamageFactionPlayerReputation</c> (Hostility), killing (Hostility), contracts (Contract), the
    /// admin screen (Admin) — and it carries the unclamped delta.</para>
    ///
    /// <para>The prefix/finalizer pair marks the call so the change-event handler can leave it to the
    /// postfix; the postfix hands the request to <see cref="PlayerReputationManager.OnConductAdded"/>
    /// only when SE accepted it (a true return means the target faction's change was generated).</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PersonalReputationConductPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>Whether the patch bound. Without it the change event is the only capture.</summary>
        internal static bool Bound { get { return HarmonyPatcher.IsLive(typeof(PersonalReputationConductPatch)); } }

        [ThreadStatic] private static int _depth;

        /// <summary>True while an <c>AddFactionPlayerReputation</c> call is on this thread's stack.</summary>
        internal static bool InAddCall { get { return _depth > 0; } }

        private static bool _outcomeLogged;

        /// <summary>A miss disables this class only and must never throw: a throw fails its whole
        /// patch group (<c>HarmonyPatcher</c>).</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Member reputation: conduct capture bound.");
                else
                    Log.Error("Member reputation: MyFactionCollection.AddFactionPlayerReputation(long, long, int, "
                            + "ReputationChangeReason, bool, bool) did not resolve. Player-faction members' "
                            + "conduct is captured from SE's change event only, so a loss or gain that "
                            + "meets the bound of a member's projected reputation never reaches their "
                            + "own record.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyFactionCollection), "AddFactionPlayerReputation",
                new[] { typeof(long), typeof(long), typeof(int), typeof(ReputationChangeReason), typeof(bool), typeof(bool) });
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            _depth++;
        }

        [HarmonyPostfix]
        private static void Postfix(bool __result, long playerIdentityId, long factionId, int delta,
                                    ReputationChangeReason reason, bool propagate)
        {
            if (!__result) return;
            if (SimulationGate.Deferred)
            {
                try { FactionsPlugin.Instance?.RecordAgentConduct(playerIdentityId, factionId, delta, reason, propagate); }
                catch (Exception ex) { Log.Warn(ex, "Member reputation: queuing conduct for the authority failed."); }
                return;
            }
            PlayerReputationManager manager = PlayerReputationManager.Current;
            if (manager == null) return;
            try { manager.OnConductAdded(playerIdentityId, factionId, delta, reason, propagate); }
            catch (Exception ex) { Log.Warn(ex, "Member reputation: capturing conduct failed."); }
        }

        /// <summary>A finalizer, not a postfix, so a throw inside SE cannot leave the marker set and
        /// hide every later change from the event handler.</summary>
        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_depth > 0) _depth--;
        }
    }
}
