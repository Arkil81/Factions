using System;
using Factions.Core;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// The one door a player's diplomacy act goes through, whichever surface it came from: the
    /// <c>!factions diplomacy</c> chat command and the FactionsUI Diplomacy window
    /// (<see cref="PlayerDiplomacyManager"/>). It resolves nothing the caller could fake — the
    /// faction and the leader flag are both read from SE for the caller's own identity — and hands
    /// the act to <see cref="FactionRuntime.LeaderDiplomacy(FactionState, LeaderAct, string, out bool)"/>.
    ///
    /// <para>The bridge verb and the vanilla Declare-war button do not come through here: the
    /// bridge is the operator, and the vanilla button is judged by SE's own leader check before
    /// <see cref="FactionDiplomacyPatch"/> sees it. See docs/diplomacy-plan.md, rule 3.</para>
    /// </summary>
    internal static class PlayerDiplomacyAccess
    {
        /// <summary>True when <paramref name="identityId"/> may act for <paramref name="factionTag"/>:
        /// its leader or founder, or a server admin (<see cref="PlayerAuthority.MayCommand"/>). An admin
        /// keeps the authority SE's own diplomacy gives them over every faction.</summary>
        internal static bool IsLeader(long identityId, string factionTag)
        {
            return PlayerAuthority.MayCommand(identityId, factionTag);
        }

        /// <summary>The registered player faction <paramref name="identityId"/> belongs to, or null
        /// when they are in no SE faction or in one Factions has not registered. An NPC faction is
        /// never returned: its tag must not be reachable from a player.</summary>
        internal static FactionState CallerFaction(FactionRuntime runtime, long identityId)
        {
            if (runtime == null || identityId == 0L || MySession.Static == null || MySession.Static.Factions == null)
                return null;
            MyFaction se = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (se == null) return null;
            FactionState owner = runtime.FindFaction(se.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
        }

        /// <summary>Runs one act for <paramref name="identityId"/> on behalf of
        /// <paramref name="owner"/>: the leader check, then the act. Returns the sentence the
        /// caller is shown, refusal included; <paramref name="done"/> is true when the act changed
        /// something.</summary>
        internal static string Act(FactionRuntime runtime, FactionState owner, long identityId,
                                   LeaderAct act, string npcTag, out bool done)
        {
            done = false;
            if (runtime == null) return "Factions plugin not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            string refusal = PlayerDiplomacy.Authorise(act, IsLeader(identityId, owner.Tag), owner.Tag);
            if (refusal != null) return refusal;

            return runtime.LeaderDiplomacy(owner, act, npcTag, out done);
        }
    }
}
