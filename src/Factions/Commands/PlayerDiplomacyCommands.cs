using System;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// A player faction's diplomacy toward the NPC factions: <c>!factions diplomacy &lt;verb&gt; &lt;npcTag&gt;</c>
    /// (docs/diplomacy-plan.md, rule 3). The rules are <see cref="PlayerDiplomacy"/> in Core, reached
    /// through the same <see cref="FactionRuntime.LeaderDiplomacy(FactionState, LeaderAct, string, out bool)"/> entry point the bridge verb uses.
    ///
    /// <para><b>Only a leader acts.</b> The check is ours and server-side, read from the SE faction's
    /// own member list (a member marked leader or founder) by <see cref="PlayerDiplomacyAccess"/>,
    /// which the FactionsUI Diplomacy window goes through too. Vanilla has no sender check on its own
    /// diplomacy buttons. <c>status</c> is open to any member. A server admin passes the check, and may
    /// act for any registered player faction with a trailing <c>as &lt;tag&gt;</c>, keeping the
    /// authority SE's own diplomacy gives an admin (<see cref="PlayerAuthority"/>).</para>
    /// </summary>
    public class PlayerDiplomacyCommands : CommandModule
    {
        [Command("factions diplomacy",
            "Your faction's stance toward the NPC factions, set by its leader: !factions diplomacy [status [npc] | war|embargo|lift|gift|peace|withdraw <npc> | propose <npc> nonaggression|alliance | help] [as <tag>: admin]")]
        [Permission(MyPromoteLevel.None)]
        public void Run(string verb = null, string npcTag = null, string kind = null, string arg4 = null, string arg5 = null)
        {
            if (verb != null && (verb.Equals("help", StringComparison.OrdinalIgnoreCase)
                              || verb.Equals("usage", StringComparison.OrdinalIgnoreCase)
                              || verb == "?"))
            {
                Context.Respond(Usage());
                return;
            }

            // "as <tag>" closes the line wherever it falls: after the npc, or after a propose's kind.
            string asTag = null;
            if (string.Equals(kind, "as", StringComparison.OrdinalIgnoreCase)) { asTag = arg4; kind = null; }
            else if (string.Equals(arg4, "as", StringComparison.OrdinalIgnoreCase)) asTag = arg5;
            else if (string.Equals(npcTag, "as", StringComparison.OrdinalIgnoreCase)) { asTag = kind; npcTag = null; kind = null; }

            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            FactionState owner;
            string error;
            if (asTag != null)
            {
                // The admin backdoor: act for any registered player faction, as SE's own diplomacy
                // lets an admin act for any faction. The admin need not belong to it.
                if (!PlayerCommandHelpers.IsAdmin(Context)) { Context.Respond("Acting for another faction is admin-only."); return; }
                owner = runtime.FindFaction(asTag);
                if (owner == null || !owner.IsPlayerFaction)
                {
                    Context.Respond("[" + asTag + "] is not a player faction registered with Factions.");
                    return;
                }
            }
            else if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            LeaderAct act;
            if (!PlayerDiplomacy.TryParse(verb, kind, out act, out error)) { Context.Respond(error); return; }

            // The same door the FactionsUI Diplomacy window uses: leader check, then the act.
            bool done;
            Context.Respond(PlayerDiplomacyAccess.Act(runtime, owner, PlayerCommandHelpers.CallerIdentity(Context),
                                                      act, npcTag, out done));
        }

        /// <summary>The whole verb family in one block: Torch's own help lists only the command's
        /// signature, so a sub-verb is otherwise undiscoverable from chat.</summary>
        private static string Usage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("!factions diplomacy [status] [npc]              your stance, their view, embargoes, truces, offers");
            sb.AppendLine("!factions diplomacy war <npc>                   declare war: your stance goes hostile, trade stops");
            sb.AppendLine("!factions diplomacy embargo|lift <npc>          stop / restart convoy trade both ways; stance unchanged");
            sb.AppendLine("!factions diplomacy gift <npc>                  pay from the faction treasury to improve their view of you");
            sb.AppendLine("!factions diplomacy peace <npc>                 offer a truce; they accept when war-weary or offering too");
            sb.AppendLine("!factions diplomacy propose <npc> nonaggression|alliance");
            sb.AppendLine("!factions diplomacy withdraw <npc>              withdraw your outstanding offer");
            sb.AppendLine("Admin: add 'as <tag>' to act for any player faction, e.g. !factions diplomacy war IMPR as EXP");
            sb.Append("Only a faction leader or an admin can act; any member can read the status. Your stance does not drift: ");
            sb.Append("it changes only when your leader acts or a truce is concluded.");
            return sb.ToString();
        }
    }
}
