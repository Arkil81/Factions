using System;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// Player-permission commands for a faction's <b>shelf</b> — what its shop sells
    /// (see docs/issues/FAC-322-player-cannot-say-what-their-nodes-sell.md). The chat interim for the
    /// store window: every verb resolves the caller's own SE faction and routes into the same
    /// <see cref="FactionRuntime"/> entry point the bridge verb reaches, so there is one set of rules.
    ///
    /// <para>Its own module beside <see cref="PlayerFleetCommands"/>, and deliberately the same
    /// grammar: the two lists answer different questions about the same hulls — what the fleet owns
    /// and what the shop sells — and a player who has learned one has learned both.</para>
    /// </summary>
    public class PlayerShelfCommands : CommandModule
    {
        [Command("factions store", "List what your shop sells, or show the syntax: !factions store [help]")]
        [Permission(MyPromoteLevel.None)]
        public void Store(string arg1 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (arg1 != null && (arg1.Equals("help", StringComparison.OrdinalIgnoreCase)
                              || arg1.Equals("usage", StringComparison.OrdinalIgnoreCase)
                              || arg1 == "?"))
            {
                Context.Respond(StoreUsage());
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListShelfForPlayer(owner)
                + Environment.NewLine
                + "Sub-verbs: add, remove, target, pause, resume, on, off — !factions store help for syntax.");
        }

        /// <summary>Every `store` grammar in one block, for the reason the `fleet` and `queue`
        /// families carry one: Torch's own help lists a command's own signature, never a verb
        /// family's, so a sub-verb is otherwise undiscoverable from chat.</summary>
        private static string StoreUsage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("!factions store                        list the shelf");
            sb.AppendLine("!factions store add <prefabId> <count>  keep <count> of a hull on sale");
            sb.AppendLine("!factions store remove <id>            stop selling it (copies on sale are handed back)");
            sb.AppendLine("!factions store target <id> <n>        change how many to keep on sale");
            sb.AppendLine("!factions store pause|resume <id>      stop/restart restocking it, keeping what it holds");
            sb.AppendLine("!factions store off|on                 stop/restart restocking the shop entirely");
            sb.AppendLine("No role argument: the shop files a hull under its own capability class.");
            sb.AppendLine("<count> is how many are RESERVED FOR SALE, which fleet dispatch may not take.");
            sb.AppendLine("  A hull your fleet owns is not on the shelf, so this list and !factions fleet");
            sb.AppendLine("  never compete for the same ship.");
            sb.AppendLine("While this list is non-empty it is the only thing that stocks your shop, and the");
            sb.AppendLine("  small/large slot targets stop applying. An empty list gives that choice back.");
            sb.Append("<id> is the first column of the plain `store` listing, e.g. SH-001.");
            return sb.ToString();
        }

        [Command("factions store add", "Keep N of a hull on sale: !factions store add <prefabId> <count>")]
        [Permission(MyPromoteLevel.None)]
        public void StoreAdd(string prefabId = null, string count = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int targetCount;
            if (string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(count)
                || !int.TryParse(count, out targetCount) || targetCount <= 0)
            {
                Context.Respond("Usage: !factions store add <prefabId> <count> — count must be a positive number.");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.AddShelfLineForPlayer(owner, prefabId, targetCount));
        }

        [Command("factions store remove", "Stop selling a hull: !factions store remove <id>")]
        [Permission(MyPromoteLevel.None)]
        public void StoreRemove(string lineId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(lineId))
            {
                Context.Respond("Usage: !factions store remove <id> (the first column of `store`, e.g. SH-001).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.RemoveShelfLineForPlayer(owner, lineId));
        }

        [Command("factions store target", "Change how many to keep on sale: !factions store target <id> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void StoreTarget(string lineId = null, string targetToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int target;
            if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(targetToken) || !int.TryParse(targetToken, out target))
            {
                Context.Respond("Usage: !factions store target <id> <n> (how many to keep reserved for sale).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetShelfLineTargetForPlayer(owner, lineId, target));
        }

        [Command("factions store pause", "Stop restocking one line: !factions store pause <id>")]
        [Permission(MyPromoteLevel.None)]
        public void StorePause(string lineId = null)
        {
            Paused(lineId, true);
        }

        [Command("factions store resume", "Restart restocking one line: !factions store resume <id>")]
        [Permission(MyPromoteLevel.None)]
        public void StoreResume(string lineId = null)
        {
            Paused(lineId, false);
        }

        private void Paused(string lineId, bool paused)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(lineId))
            {
                Context.Respond("Usage: !factions store " + (paused ? "pause" : "resume") + " <id> "
                    + "(the first column of `store`, e.g. SH-001).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetShelfLinePausedForPlayer(owner, lineId, paused));
        }

        [Command("factions store off", "Stop restocking the shop entirely: !factions store off")]
        [Permission(MyPromoteLevel.None)]
        public void StoreOff()
        {
            OptOut(true);
        }

        [Command("factions store on", "Restart restocking the shop: !factions store on")]
        [Permission(MyPromoteLevel.None)]
        public void StoreOn()
        {
            OptOut(false);
        }

        private void OptOut(bool optOut)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetStoreShelfOptOutForPlayer(owner, optOut));
        }
    }
}
