using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// Player-permission commands for a Magnate-tier player faction to manage its own claimed/
    /// founded station(s) — the chat-command interim for station management (see
    /// docs/economy-territory.md -> "Player-faction claiming"). No admin permission required;
    /// every verb resolves the caller's own SE faction and only ever acts on nodes it owns.
    /// </summary>
    public class PlayerStationCommands : CommandModule
    {
        // Node id is resolved from physical position when omitted (via PlayerCommandHelpers), since
        // a Factions node ID isn't shown anywhere in SE's own UI.
        private const int MaxNameLength = 40;

        [Command("factions station status", "Show your faction's claimed/founded node(s): !factions station status [nodeId]")]
        [Permission(MyPromoteLevel.None)]
        public void Status(string nodeId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            var nodes = FactionsPlugin.Instance.Runtime.World.Graph.OwnedBy(owner.Tag).ToList();
            if (nodes.Count == 0) { Context.Respond("[" + owner.Tag + "] holds no territory yet."); return; }

            if (!string.IsNullOrEmpty(nodeId))
            {
                SpatialNode explicitNode = nodes.FirstOrDefault(n => string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase));
                if (explicitNode == null) { Context.Respond(nodeId + " isn't one of your faction's nodes."); return; }
                Context.Respond(DescribeDetail(owner, explicitNode));
                return;
            }

            SpatialNode nearby;
            string resolveError;
            if (PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, nodes, out nearby, out resolveError))
            {
                Context.Respond(DescribeDetail(owner, nearby));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[" + owner.Tag + "] " + nodes.Count + " node(s):");
            foreach (SpatialNode n in nodes.OrderByDescending(n => n.StableStationId != 0))
                sb.AppendLine("  " + DescribeSummary(n));
            Context.Respond(sb.ToString());
        }

        [Command("factions station rename", "Rename your faction's founded station: !factions station rename [nodeId] <new name>")]
        [Permission(MyPromoteLevel.None)]
        public void Rename(string firstToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            List<SpatialNode> ownedFounded = FactionsPlugin.Instance.Runtime.World.Graph
                .OwnedBy(owner.Tag).Where(n => n.StableStationId != 0).ToList();

            string nodeId;
            string newName;
            if (!string.IsNullOrEmpty(firstToken)
                && ownedFounded.Any(n => string.Equals(n.Id, firstToken, StringComparison.OrdinalIgnoreCase)))
            {
                nodeId = firstToken;
                newName = SanitizeName(StripFirstToken(Context.RawArgs));
            }
            else
            {
                newName = SanitizeName(Context.RawArgs);
                SpatialNode nearby;
                string resolveError;
                if (!PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, ownedFounded, out nearby, out resolveError))
                {
                    Context.Respond(resolveError);
                    return;
                }
                nodeId = nearby.Id;
            }

            if (string.IsNullOrEmpty(newName))
            {
                Context.Respond("Usage: !factions station rename [nodeId] <new name>");
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.RenameStationForPlayer(owner, nodeId, newName));
        }

        [Command("factions station found", "Found a station on a claim your faction holds: !factions station found [nodeId] [standard|fortified]")]
        [Permission(MyPromoteLevel.None)]
        public void Found(string firstToken = null, string secondToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            // Only the unfounded claims are candidates, so standing at an already-built station and
            // typing the verb bare resolves to the nearest node that can actually take one rather
            // than to the station underfoot.
            List<SpatialNode> ownedUnfounded = FactionsPlugin.Instance.Runtime.World.Graph
                .OwnedBy(owner.Tag).Where(n => n.StableStationId == 0).ToList();

            string nodeId;
            string style;
            if (!string.IsNullOrEmpty(firstToken)
                && ownedUnfounded.Any(n => string.Equals(n.Id, firstToken, StringComparison.OrdinalIgnoreCase)))
            {
                nodeId = firstToken;
                style = secondToken;
            }
            else
            {
                style = firstToken;
                SpatialNode nearby;
                string resolveError;
                if (!PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, ownedUnfounded, out nearby, out resolveError))
                {
                    Context.Respond(resolveError);
                    return;
                }
                nodeId = nearby.Id;
            }

            bool isPirate = string.Equals(style, "fortified", StringComparison.OrdinalIgnoreCase);
            if (!isPirate && !string.IsNullOrEmpty(style)
                && !string.Equals(style, "standard", StringComparison.OrdinalIgnoreCase))
            {
                Context.Respond("Usage: !factions station found [nodeId] [standard|fortified]");
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.FoundStationForPlayer(owner, nodeId, isPirate));
        }

        [Command("factions station develop", "Install a capability, toggle auto-investment, or rank its install: !factions station develop [nodeId] <capability> | <capability> reserve | <capability> priority <n> | <nodeId> <capability> <level> (admin)")]
        [Permission(MyPromoteLevel.None)]
        public void Develop(string arg1 = null, string arg2 = null, string arg3 = null)
        {
            // "<nodeId> <capability> <level>" is the admin overload: it sets the level outright and
            // charges nothing, on any faction's founded station. It is intercepted before the
            // caller's own faction is resolved because it doesn't have one to resolve -- the owner
            // is read off the node, and an admin need not be in a player faction at all. The install
            // grammar never takes a third token, and the "priority" grammar below claims its own
            // literal, so excluding that one word is enough to keep both reachable.
            int grantLevel;
            if (arg3 != null && !string.Equals(arg2, "priority", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg3, out grantLevel))
            {
                if (!PlayerCommandHelpers.IsAdmin(Context))
                {
                    Context.Respond("Setting a capability's level outright is admin-only. To install one "
                        + "on your own faction's station: !factions station develop [nodeId] <capability>.");
                    return;
                }
                if (FactionsPlugin.Instance == null || FactionsPlugin.Instance.Runtime == null)
                {
                    Context.Respond("Factions plugin not ready.");
                    return;
                }
                NodeCapabilityKind grantCap;
                if (!CapabilityTokens.TryParse(arg2, out grantCap))
                {
                    Context.Respond("Unknown capability '" + arg2 + "'. Valid: " + CapabilityTokens.Names());
                    return;
                }
                Context.Respond(FactionsPlugin.Instance.Runtime.GrantCapabilityAdmin(arg1, grantCap, grantLevel));
                return;
            }

            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            // "<capability> priority <n>" ranks that capability's install against the ship builds
            // competing for the same components (see FactionState.InstallPriority). The install
            // grammar never takes three tokens, so this can't collide with it.
            if (arg3 != null && string.Equals(arg2, "priority", StringComparison.OrdinalIgnoreCase))
            {
                NodeCapabilityKind priorityCap;
                if (!Enum.TryParse(arg1, true, out priorityCap)
                    || !CapabilityTokens.IsPlayerDevelopable(priorityCap))
                {
                    Context.Respond("Unknown capability '" + arg1 + "'. Valid: " + CapabilityTokens.PlayerNames());
                    return;
                }
                int rank;
                if (!int.TryParse(arg3, out rank))
                {
                    Context.Respond("'" + arg3 + "' isn't a number. Usage: !factions station develop <capability> priority <n> "
                        + "(higher outranks a ship build's own priority; 0 is the default, where builds win).");
                    return;
                }
                if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
                Context.Respond(FactionsPlugin.Instance.Runtime.SetInstallPriorityForPlayer(owner, priorityCap, rank));
                return;
            }

            // "<capability> reserve" toggles whether ProductionManager may post InvestmentRequests/
            // reserve real components toward that capability on this faction's behalf (see
            // FactionLifecycle.CanPostInvestmentFor) -- opt-in only, since components were otherwise
            // getting silently locked toward capabilities the player never chose to install. This is
            // the only 2-token combination the install grammar below can't already produce validly
            // ("reserve" is never a real nodeId), so it's safe to intercept first.
            if (arg2 != null && string.Equals(arg2, "reserve", StringComparison.OrdinalIgnoreCase))
            {
                NodeCapabilityKind reserveCap;
                if (!Enum.TryParse(arg1, true, out reserveCap)
                    || !CapabilityTokens.IsPlayerDevelopable(reserveCap))
                {
                    Context.Respond("Unknown capability '" + arg1 + "'. Valid: " + CapabilityTokens.PlayerNames());
                    return;
                }
                if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
                Context.Respond(FactionsPlugin.Instance.Runtime.ToggleReservationForPlayer(owner, reserveCap));
                return;
            }

            // [nodeId] is only ever meaningful together with a following token -- a lone token can
            // only be the capability, since a bare nodeId with no capability isn't a valid call.
            string nodeId, capToken;
            if (arg2 != null) { nodeId = arg1; capToken = arg2; }
            else if (arg1 != null) { nodeId = null; capToken = arg1; }
            else
            {
                Context.Respond("Usage: !factions station develop [nodeId] <capability> (install), "
                    + "!factions station develop <capability> reserve (toggle auto-investment), "
                    + "or !factions station develop <capability> priority <n> (rank the install against ship builds)."
                    + (PlayerCommandHelpers.IsAdmin(Context)
                        ? " Admin: !factions station develop <nodeId> <capability> <level> sets the level outright, free"
                          + " (0 removes it), over " + CapabilityTokens.Names() + "."
                        : "")
                    + " Valid: " + CapabilityTokens.PlayerNames());
                return;
            }

            NodeCapabilityKind capability;
            if (!Enum.TryParse(capToken, true, out capability))
            {
                Context.Respond("Unknown capability '" + capToken + "'. Valid: " + CapabilityTokens.PlayerNames());
                return;
            }

            if (nodeId == null)
            {
                List<SpatialNode> ownedFounded = FactionsPlugin.Instance.Runtime.World.Graph
                    .OwnedBy(owner.Tag).Where(n => n.StableStationId != 0).ToList();
                SpatialNode nearby;
                string resolveError;
                if (!PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, ownedFounded, out nearby, out resolveError))
                {
                    Context.Respond(resolveError);
                    return;
                }
                nodeId = nearby.Id;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.DevelopStationForPlayer(owner, nodeId, capability));
        }

        [Command("factions station hold", "Keep a node's hold up while you are away, or list what you are holding: !factions station hold [nodeId] [rank|off]")]
        [Permission(MyPromoteLevel.None)]
        public void Hold(string arg1 = null, string arg2 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            if (string.IsNullOrEmpty(arg1)) { Context.Respond(runtime.DescribeReinforceOptIn(owner)); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            // "off" removes; a bare node id defaults to rank 0, the same "unset" rank
            // InstallPriority uses, so holding one node needs no number at all.
            int? rank = 0;
            if (string.Equals(arg2, "off", StringComparison.OrdinalIgnoreCase)) rank = null;
            else if (!string.IsNullOrEmpty(arg2))
            {
                int parsed;
                if (!int.TryParse(arg2, out parsed))
                {
                    Context.Respond("Rank must be a number, or 'off'. !factions station hold <nodeId> [rank|off]");
                    return;
                }
                rank = parsed;
            }
            Context.Respond(runtime.SetReinforceOptInForPlayer(owner, arg1, rank));
        }

        [Command("factions station budget", "Show or set how much of your treasury upkeep may take: !factions station budget [income|treasury|floor] [value|default]")]
        [Permission(MyPromoteLevel.None)]
        public void Budget(string knob = null, string value = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            if (string.IsNullOrEmpty(knob)) { Context.Respond(runtime.DescribeReinforceBudget(owner)); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            double? parsed = null;
            if (!string.IsNullOrEmpty(value) && !string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    Context.Respond("Value must be a number, or 'default' to go back to the world's setting.");
                    return;
                }
                parsed = v;
            }
            else if (string.IsNullOrEmpty(value))
            {
                Context.Respond("Give a value, or 'default' to clear it. "
                    + "!factions station budget " + knob + " <value|default>");
                return;
            }
            Context.Respond(runtime.SetReinforceBudgetForPlayer(owner, knob, parsed));
        }

        [Command("factions station trade", "Let Factions trade for you, or show what it would do: !factions station trade [buy|sell|both|off] [on|off]")]
        [Permission(MyPromoteLevel.None)]
        public void Trade(string arg1 = null, string arg2 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            if (string.IsNullOrEmpty(arg1)) { Context.Respond(runtime.DescribeTradeOptIn(owner)); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            // "off" alone stops both directions, so there is a one-word way out that does not
            // require remembering which of the two is running.
            if (string.Equals(arg1, "off", StringComparison.OrdinalIgnoreCase))
            {
                Context.Respond(runtime.SetTradeOptInForPlayer(owner, "both", false));
                return;
            }

            // A bare direction switches it ON: "trade buy" reads as a request to start, and the
            // way to stop is the explicit "off" above or "trade buy off".
            bool on = true;
            if (!string.IsNullOrEmpty(arg2))
            {
                if (string.Equals(arg2, "off", StringComparison.OrdinalIgnoreCase)) on = false;
                else if (!string.Equals(arg2, "on", StringComparison.OrdinalIgnoreCase))
                {
                    Context.Respond("Say 'on' or 'off'. !factions station trade <buy|sell|both> [on|off]");
                    return;
                }
            }
            Context.Respond(runtime.SetTradeOptInForPlayer(owner, arg1, on));
        }

        [Command("factions station banksell", "Show or set what the central bank may buy from you: !factions station banksell [sell|target|keep] [item|all] [n]")]
        [Permission(MyPromoteLevel.None)]
        public void BankSell(string verb = null, string item = null, string amount = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            if (string.IsNullOrEmpty(verb)) { Context.Respond(runtime.DescribeCentralBankRules(owner)); return; }

            BankSaleMode mode;
            if (string.Equals(verb, "sell", StringComparison.OrdinalIgnoreCase)) mode = BankSaleMode.SellOversupply;
            else if (string.Equals(verb, "target", StringComparison.OrdinalIgnoreCase)) mode = BankSaleMode.AboveTarget;
            else if (string.Equals(verb, "keep", StringComparison.OrdinalIgnoreCase)) mode = BankSaleMode.DontSell;
            else
            {
                Context.Respond("Say sell, target or keep. !factions station banksell <sell|target|keep> <item|all> [n]");
                return;
            }
            if (string.IsNullOrEmpty(item))
            {
                Context.Respond("Name an item, or all. Quote a name with spaces: !factions station banksell "
                                + verb.ToLowerInvariant() + " \"Steel Plate\"" + (mode == BankSaleMode.AboveTarget ? " 500" : ""));
                return;
            }
            long target = 0;
            if (mode == BankSaleMode.AboveTarget
                && (!long.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out target) || target <= 0))
            {
                Context.Respond("Give the faction-wide amount to keep, at least 1: !factions station banksell target <item|all> <n>");
                return;
            }

            List<string> keys = runtime.ResolveBankItems(owner, item, mode);
            if (keys.Count == 0)
            {
                Context.Respond(string.Equals(item, "all", StringComparison.OrdinalIgnoreCase)
                    ? "Nothing to change."
                    : "No bank item called '" + item + "'. The Bank sales window lists them all.");
                return;
            }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(runtime.SetCentralBankRuleForPlayer(owner, keys, mode, target));
        }

        [Command("factions station tradebudget", "Show or set how much of your treasury trade may take: !factions station tradebudget [income|treasury|floor] [value|default]")]
        [Permission(MyPromoteLevel.None)]
        public void TradeBudget(string knob = null, string value = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
            if (runtime == null) { Context.Respond("Factions plugin not ready."); return; }

            if (string.IsNullOrEmpty(knob)) { Context.Respond(runtime.DescribeTradeBudget(owner)); return; }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            double? parsed = null;
            if (!string.IsNullOrEmpty(value) && !string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    Context.Respond("Value must be a number, or 'default' to go back to the world's setting.");
                    return;
                }
                parsed = v;
            }
            else if (string.IsNullOrEmpty(value))
            {
                Context.Respond("Give a value, or 'default' to clear it. "
                    + "!factions station tradebudget " + knob + " <value|default>");
                return;
            }
            Context.Respond(runtime.SetTradeBudgetForPlayer(owner, knob, parsed));
        }

        [Command("factions station queue", "List active ship builds, or show the queue syntax: !factions station queue [nodeId|help]")]
        [Permission(MyPromoteLevel.None)]
        public void Queue(string nodeId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            // Torch routes each "queue <sub-verb>" to its own command, so a token arriving here is
            // either a nodeId or a typo -- and every sub-verb is otherwise undiscoverable from chat.
            if (nodeId != null && (nodeId.Equals("help", StringComparison.OrdinalIgnoreCase)
                                || nodeId.Equals("usage", StringComparison.OrdinalIgnoreCase)
                                || nodeId == "?"))
            {
                Context.Respond(QueueUsage());
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListBuildQueueForPlayer(owner, nodeId)
                + Environment.NewLine
                + "Sub-verbs: build, cancel, pause, resume, priority, prefabs — "
                + "!factions station queue help for syntax.");
        }

        /// <summary>Every `queue` grammar in one block. Reachable as `!factions station queue help`
        /// because Torch's own help only lists a command's own signature, never a verb family's.</summary>
        private static string QueueUsage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("!factions station queue [nodeId]                           list active builds");
            sb.AppendLine("!factions station queue build [nodeId] <prefabId>   queue a hull");
            sb.AppendLine("!factions station queue cancel [nodeId] <entryId>          cancel a build, refunding components");
            sb.AppendLine("!factions station queue pause [nodeId]                     stop this shipyard claiming components");
            sb.AppendLine("!factions station queue resume [nodeId]                    let it collect again");
            sb.AppendLine("!factions station queue priority [nodeId] <entryId> <n>    rank a build's claim (higher wins, default 0)");
            sb.AppendLine("!factions station queue prefabs <role>                     list prefab ids for a role");
            sb.AppendLine("Omit [nodeId] to use your nearest owned Shipyard station.");
            sb.AppendLine("<entryId> is the second column of the plain `queue` listing, e.g. Trade:001.");
            sb.AppendLine("Roles: " + string.Join(", ", ShipyardManager.PlayerQueueRoles));
            sb.AppendLine("A station rebuild shows up in this queue too — see !factions station prefabs "
                + "[nodeId], !factions station refit [nodeId] <prefabId> [force], "
                + "!factions station refit apply [nodeId], and "
                + "!factions station scan <own|all> <name>.");
            sb.Append("Related: !factions station develop <capability> priority <n> — ranks a capability "
                + "install against the builds competing for the same components.");
            return sb.ToString();
        }

        [Command("factions station queue cancel", "Cancel a build, refunding its components: !factions station queue cancel [nodeId] <entryId>")]
        [Permission(MyPromoteLevel.None)]
        public void QueueCancel(string arg1 = null, string arg2 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            string nodeId, entryId;
            if (arg2 != null) { nodeId = arg1; entryId = arg2; }
            else if (arg1 != null) { nodeId = null; entryId = arg1; }
            else { Context.Respond("Usage: !factions station queue cancel [nodeId] <entryId>"); return; }

            if (nodeId == null && !TryResolveShipyardNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.CancelBuildForPlayer(owner, nodeId, entryId));
        }

        [Command("factions station queue build", "Queue a hull: !factions station queue build [nodeId] <prefabId>")]
        [Permission(MyPromoteLevel.None)]
        public void QueueBuild(string arg1 = null, string arg2 = null, string arg3 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            // The role argument is retired: a build names a hull. The old three-argument form, and the
            // old two-argument form whose first word is a role, are refused with the new shape rather
            // than read as a node id.
            if (arg3 != null || (arg2 != null && Array.IndexOf(ShipyardManager.PlayerQueueRoles, arg1) >= 0))
            {
                Context.Respond("A build names a hull, not a role: !factions station queue build [nodeId] <prefabId>");
                return;
            }

            string nodeId, prefabId;
            if (arg2 != null) { nodeId = arg1; prefabId = arg2; }
            else if (arg1 != null) { nodeId = null; prefabId = arg1; }
            else
            {
                Context.Respond("Usage: !factions station queue build [nodeId] <prefabId>");
                return;
            }

            if (nodeId == null && !TryResolveShipyardNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.QueueBuildForPlayer(owner, nodeId, prefabId));
        }

        [Command("factions station queue pause", "Stop a shipyard claiming components: !factions station queue pause [nodeId]")]
        [Permission(MyPromoteLevel.None)]
        public void QueuePause(string nodeId = null)
        {
            RespondWithPause(nodeId, paused: true);
        }

        [Command("factions station queue resume", "Let a paused shipyard collect again: !factions station queue resume [nodeId]")]
        [Permission(MyPromoteLevel.None)]
        public void QueueResume(string nodeId = null)
        {
            RespondWithPause(nodeId, paused: false);
        }

        private void RespondWithPause(string nodeId, bool paused)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }
            if (nodeId == null && !TryResolveShipyardNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.PauseShipyardForPlayer(owner, nodeId, paused));
        }

        [Command("factions station queue priority", "Rank a build against other claims: !factions station queue priority [nodeId] <entryId> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void QueuePriority(string arg1 = null, string arg2 = null, string arg3 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            string nodeId, entryId, rankToken;
            if (arg3 != null) { nodeId = arg1; entryId = arg2; rankToken = arg3; }
            else if (arg2 != null) { nodeId = null; entryId = arg1; rankToken = arg2; }
            else
            {
                Context.Respond("Usage: !factions station queue priority [nodeId] <entryId> <n> "
                    + "(higher claims a contested component first; 0 is the default).");
                return;
            }

            int rank;
            if (!int.TryParse(rankToken, out rank))
            {
                Context.Respond("'" + rankToken + "' isn't a number. Usage: !factions station queue priority [nodeId] <entryId> <n>");
                return;
            }

            if (nodeId == null && !TryResolveShipyardNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetBuildPriorityForPlayer(owner, nodeId, entryId, rank));
        }

        [Command("factions station queue prefabs", "List known prefab ids for a role: !factions station queue prefabs <role>")]
        [Permission(MyPromoteLevel.None)]
        public void QueuePrefabs(string role = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(role))
            {
                Context.Respond("Usage: !factions station queue prefabs <role>. Valid roles: "
                    + string.Join(", ", ShipyardManager.PlayerQueueRoles));
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListPrefabsForPlayer(owner, role));
        }

        [Command("factions station scan",
            "Capture the station design you're looking at: !factions station scan <own|all> <name>")]
        [Permission(MyPromoteLevel.None)]
        public void Scan(string scope = null, string name = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(name))
            {
                Context.Respond("Usage: !factions station scan <own|all> <name>");
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.CaptureStationForPlayer(owner, Context.Player, scope, name));
        }

        [Command("factions station prefabs",
            "List the station designs a node could be rebuilt as: !factions station prefabs [nodeId]")]
        [Permission(MyPromoteLevel.None)]
        public void Prefabs(string nodeId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (nodeId == null && !TryResolveFoundedNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListStationPrefabsForPlayer(owner, nodeId));
        }

        [Command("factions station refit",
            "Rebuild a founded station as another design: !factions station refit [nodeId] <prefabId> [force], "
            + "!factions station refit apply [nodeId] to stand a finished rebuild up now, "
            + "or !factions station refit all <design> [force] to bring every node on an older version to the latest")]
        [Permission(MyPromoteLevel.None)]
        public void Refit(string arg1 = null, string arg2 = null, string arg3 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(arg1))
            {
                Context.Respond("Usage: !factions station refit [nodeId] <prefabId> [force]. "
                    + "See !factions station prefabs for what this node could become.");
                return;
            }

            // `apply` is a sub-verb, not a prefab id: a finished rebuild waits for the station to be
            // empty, and this is how a player who would rather be dropped asks for it now. Safe to
            // take the word literally -- a capture id is sanitised at scan time and `station scan`
            // would have to have been given the name "apply" for these to collide, which a node's
            // own candidate list makes obvious.
            if (arg1.Equals("apply", StringComparison.OrdinalIgnoreCase))
            {
                string applyNodeId = arg2;
                if (applyNodeId == null && !TryResolveFoundedNode(owner, out applyNodeId, out error))
                { Context.Respond(error); return; }
                if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
                Context.Respond(FactionsPlugin.Instance.Runtime.ApplyRefitNowForPlayer(owner, applyNodeId));
                return;
            }

            // `all` refits every node standing as an older version of a design to its latest.
            if (arg1.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                bool forceAll = !string.IsNullOrEmpty(arg3) && arg3.Equals("force", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(arg2) || (!string.IsNullOrEmpty(arg3) && !forceAll))
                {
                    Context.Respond("Usage: !factions station refit all <design> [force]");
                    return;
                }
                if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
                Context.Respond(FactionsPlugin.Instance.Runtime.QueueFamilyRefitForPlayer(owner, arg2, forceAll));
                return;
            }

            // A leading token is a nodeId only when it names one of this faction's founded nodes --
            // the same rule `rename` and `found` disambiguate on. Anything else is the prefab id, so
            // `refit <prefabId> force` reads the way it looks.
            List<SpatialNode> ownedFounded = FactionsPlugin.Instance.Runtime.World.Graph
                .OwnedBy(owner.Tag).Where(n => n.StableStationId != 0).ToList();
            bool firstIsNode = ownedFounded.Any(n => string.Equals(n.Id, arg1, StringComparison.OrdinalIgnoreCase));

            string nodeId, prefabId, forceToken;
            if (firstIsNode) { nodeId = arg1; prefabId = arg2; forceToken = arg3; }
            else             { nodeId = null; prefabId = arg1; forceToken = arg2; }

            if (string.IsNullOrEmpty(prefabId))
            {
                Context.Respond("Usage: !factions station refit [nodeId] <prefabId> [force]");
                return;
            }

            bool force = !string.IsNullOrEmpty(forceToken)
                      && forceToken.Equals("force", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(forceToken) && !force)
            {
                Context.Respond("'" + forceToken + "' isn't an option here. Usage: "
                    + "!factions station refit [nodeId] <prefabId> [force]");
                return;
            }

            if (nodeId == null && !TryResolveFoundedNode(owner, out nodeId, out error)) { Context.Respond(error); return; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.QueueRefitForPlayer(owner, nodeId, prefabId, force));
        }

        [Command("factions station mine", "Trigger a one-shot Mine dispatch: !factions station mine <sourceNodeId> [destNodeId] [oreWeights]")]
        [Permission(MyPromoteLevel.None)]
        public void Mine(string sourceNodeId = null, string arg2 = null, string arg3 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(sourceNodeId))
            {
                Context.Respond("Usage: !factions station mine <sourceNodeId> [destNodeId] [oreWeights]");
                return;
            }

            // An ore-weight spec always contains ':' (e.g. "Iron:50,Nickel:50") -- a nodeId never
            // does -- so arg2 disambiguates the same way QueueBuild's own positional args already do,
            // letting both "mine <source> <weights>" and "mine <source> <dest> <weights>" parse.
            string destNodeId, oreWeights;
            if (arg3 != null) { destNodeId = arg2; oreWeights = arg3; }
            else if (arg2 != null && arg2.IndexOf(':') >= 0) { destNodeId = null; oreWeights = arg2; }
            else { destNodeId = arg2; oreWeights = null; }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.TriggerMineForPlayer(owner, sourceNodeId, destNodeId, oreWeights));
        }

        [Command("factions station mine auto", "List, or enable/disable, a standing auto-mine order at a hub: !factions station mine auto [hubNodeId] [periodSeconds|off] [oreWeights]")]
        [Permission(MyPromoteLevel.None)]
        public void MineAuto(string hubNodeId = null, string periodOrOff = null, string oreWeights = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(hubNodeId))
            {
                Context.Respond(FactionsPlugin.Instance.Runtime.ListMineAutoForPlayer(owner));
                return;
            }

            if (string.IsNullOrEmpty(periodOrOff))
            {
                Context.Respond("Usage: !factions station mine auto <hubNodeId> <periodSeconds|off> [oreWeights]");
                return;
            }

            double periodSeconds;
            if (string.Equals(periodOrOff, "off", StringComparison.OrdinalIgnoreCase))
            {
                periodSeconds = 0;
            }
            else if (!double.TryParse(periodOrOff, out periodSeconds) || periodSeconds <= 0)
            {
                Context.Respond("Usage: !factions station mine auto <hubNodeId> <periodSeconds|off> [oreWeights] — periodSeconds must be a positive number.");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.ToggleMineAutoForPlayer(owner, hubNodeId, periodSeconds, oreWeights));
        }

        // ---- standing orders (FAC-008) --------------------------------------------------------
        //
        // Torch routes each "orders <sub-verb>" to its own command, exactly as it does for "queue",
        // so a token arriving at the bare verb is either garbage or a request for help.

        [Command("factions station orders", "List this faction's standing orders, or show the syntax: !factions station orders [help]")]
        [Permission(MyPromoteLevel.None)]
        public void Orders(string arg1 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (arg1 != null && (arg1.Equals("help", StringComparison.OrdinalIgnoreCase)
                              || arg1.Equals("usage", StringComparison.OrdinalIgnoreCase)
                              || arg1 == "?"))
            {
                Context.Respond(OrdersUsage());
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListStandingOrdersForPlayer(owner)
                + Environment.NewLine
                + "Sub-verbs: mine, stock, remove, priority, pause, resume — "
                + "!factions station orders help for syntax.");
        }

        /// <summary>Every `orders` grammar in one block, for the same reason
        /// <see cref="QueueUsage"/> exists: Torch's own help only ever lists a command's own
        /// signature, never a verb family's, so a sub-verb is otherwise undiscoverable from chat.</summary>
        private static string OrdersUsage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("!factions station orders                                    list standing orders, highest rank first");
            sb.AppendLine("!factions station orders mine <hubNodeId> <period> [ores]   add a recurring mine order at a hub");
            sb.AppendLine("!factions station orders stock <nodeId|all> <item> <quantity>   keep that many of an item at a node, or at every station");
            sb.AppendLine("!factions station orders remove <orderId>                   delete an order");
            sb.AppendLine("!factions station orders priority <orderId> <n>             rank it (higher wins a contested deposit; a stock order at 1+ is supplied before ship builds)");
            sb.AppendLine("!factions station orders pause|resume <orderId>             stop/restart evaluating it, keeping its id and rank");
            sb.AppendLine("Several mine orders may share one hub — that is what rank decides between.");
            sb.AppendLine("A stock order asks for the item until the node holds the quantity, and the node keeps");
            sb.AppendLine("that much back from hauls and sales. Items: Ore:Iron, Ingot:Iron, Comp:SteelPlate, NATO_25x184mm.");
            sb.AppendLine("Ranked 1 or more, a stock order's item and the materials to make it are kept from ship");
            sb.AppendLine("builds and capability upgrades ranked below it. At 0 (the default) builds come first.");
            sb.AppendLine("What your fleet is made of is a separate list: !factions fleet help");
            sb.Append("<orderId> is the first column of the plain `orders` listing, e.g. SO-001.");
            return sb.ToString();
        }

        [Command("factions station orders mine", "Add a recurring mine order at a hub: !factions station orders mine <hubNodeId> <periodSeconds> [oreWeights]")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersMine(string hubNodeId = null, string periodSeconds = null, string oreWeights = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            double period;
            if (string.IsNullOrEmpty(hubNodeId) || string.IsNullOrEmpty(periodSeconds)
                || !double.TryParse(periodSeconds, out period) || period <= 0)
            {
                Context.Respond("Usage: !factions station orders mine <hubNodeId> <periodSeconds> [oreWeights] — periodSeconds must be a positive number.");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.AddStandingMineOrderForPlayer(owner, hubNodeId, period, oreWeights));
        }

        [Command("factions station orders stock", "Keep an item in stock at a node, or at every station: !factions station orders stock <nodeId|all> <item> <quantity>")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersStock(string nodeId = null, string item = null, string quantity = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            long target;
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(item) || string.IsNullOrEmpty(quantity)
                || !long.TryParse(quantity, out target) || target <= 0)
            {
                Context.Respond("Usage: !factions station orders stock <nodeId|all> <item> <quantity> — quantity must be a positive number.");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.AddStandingStockOrderForPlayer(owner, nodeId, item, target));
        }

        [Command("factions station orders remove", "Delete a standing order: !factions station orders remove <orderId>")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersRemove(string orderId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(orderId))
            {
                Context.Respond("Usage: !factions station orders remove <orderId> (the first column of `orders`, e.g. SO-001).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.RemoveStandingOrderForPlayer(owner, orderId));
        }

        [Command("factions station orders priority", "Rank a standing order: !factions station orders priority <orderId> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersPriority(string orderId = null, string rankToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int rank;
            if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(rankToken) || !int.TryParse(rankToken, out rank))
            {
                Context.Respond("Usage: !factions station orders priority <orderId> <n> "
                    + "(higher is evaluated first and wins a contested deposit or shipyard; a stock order at 1 or more "
                    + "is supplied before ship builds; 0 is the default).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetStandingOrderPriorityForPlayer(owner, orderId, rank));
        }

        [Command("factions station orders pause", "Stop evaluating a standing order: !factions station orders pause <orderId>")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersPause(string orderId = null)
        {
            RespondToPauseToggle(orderId, true);
        }

        [Command("factions station orders resume", "Resume a paused standing order: !factions station orders resume <orderId>")]
        [Permission(MyPromoteLevel.None)]
        public void OrdersResume(string orderId = null)
        {
            RespondToPauseToggle(orderId, false);
        }

        private void RespondToPauseToggle(string orderId, bool paused)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(orderId))
            {
                Context.Respond("Usage: !factions station orders " + (paused ? "pause" : "resume") + " <orderId>.");
                return;
            }
            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;

            Context.Respond(FactionsPlugin.Instance.Runtime.SetStandingOrderPausedForPlayer(owner, orderId, paused));
        }

        // Resolves the caller's nearest owned Shipyard-capable node -- queue cancel/build only ever
        // act on a Shipyard node's BuildQueue, so (unlike Develop's "nearest founded node") the
        // fallback set here is narrowed to nodes that could actually hold one. Instance method (not
        // static) so it can read Context.Player directly, same as the command methods above.
        /// <summary>The caller's nearest owned <b>founded</b> node — what a refit verb resolves to,
        /// since a refit needs a station to rebuild and no Shipyard capability (the build sweep
        /// services every owned node's queue). Distinct from
        /// <see cref="TryResolveShipyardNode"/> for exactly that reason.</summary>
        private bool TryResolveFoundedNode(FactionState owner, out string nodeId, out string error)
        {
            nodeId = null;
            List<SpatialNode> founded = FactionsPlugin.Instance.Runtime.World.Graph
                .OwnedBy(owner.Tag).Where(n => n.StableStationId != 0).ToList();

            if (founded.Count == 0)
            {
                error = "[" + owner.Tag + "] has founded no stations yet.";
                return false;
            }

            SpatialNode nearby;
            if (!PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, founded, out nearby, out error))
                return false;

            nodeId = nearby.Id;
            return true;
        }

        private bool TryResolveShipyardNode(FactionState owner, out string nodeId, out string error)
        {
            nodeId = null;
            error = null;
            List<SpatialNode> shipyards = FactionsPlugin.Instance.Runtime.World.Graph
                .OwnedBy(owner.Tag).Where(n =>
                {
                    int lvl;
                    return n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out lvl) && lvl >= 1;
                }).ToList();

            if (shipyards.Count == 0)
            {
                error = "[" + owner.Tag + "] has no Shipyard capability installed anywhere yet.";
                return false;
            }

            SpatialNode nearby;
            if (!PlayerCommandHelpers.TryResolveNearestOwnedNode(Context.Player, shipyards, out nearby, out error))
                return false;

            nodeId = nearby.Id;
            return true;
        }

        // Everything after the first whitespace-delimited token in the raw command text — the
        // rest of Args beyond the first bound parameter is dropped by Torch's positional parameter
        // binding (Command.TryInvoke only fills declared parameters), so a multi-word name has to
        // be recovered from RawArgs instead of a second string parameter.
        private static string StripFirstToken(string rawArgs)
        {
            if (string.IsNullOrEmpty(rawArgs)) return null;
            string trimmed = rawArgs.TrimStart();
            int sp = trimmed.IndexOf(' ');
            return sp >= 0 ? trimmed.Substring(sp + 1) : null;
        }

        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string name = raw.Trim().Trim('"');
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static string DescribeSummary(SpatialNode n)
        {
            string name = n.StableStationId != 0
                ? (string.IsNullOrEmpty(n.DisplayName) ? "(unnamed station)" : n.DisplayName)
                : "(not yet founded)";
            return n.Id + "  " + name + "  hold=" + Pct(n.HoldStrength) + "  res=" + Pct(n.ResourceValue);
        }

        private static string DescribeDetail(FactionState owner, SpatialNode n)
        {
            string name = n.StableStationId != 0
                ? (string.IsNullOrEmpty(n.DisplayName) ? "(unnamed station)" : n.DisplayName)
                : "(not yet founded)";

            var sb = new StringBuilder();
            sb.AppendLine(n.Id + "  " + name);
            sb.AppendLine("  hold=" + Pct(n.HoldStrength) + "  resource=" + Pct(n.ResourceValue));

            // Ship-traffic credit sits beside hold because it is what decides which way that hold is
            // moving: at the cap the gain beats decay and the node climbs. Printed only when there
            // is any, so a node nothing flies to reads the same as it always did.
            WorldModel activityWorld = FactionsPlugin.Instance.Runtime.World;
            double activity = ShipActivity.CreditsOf(n, activityWorld.Tuning, activityWorld.UtcNow);
            if (activity > 0)
            {
                double net = ShipActivity.HoldGainForInterval(n, activityWorld.Tuning,
                    activityWorld.UtcNow, 3600.0) - activityWorld.Tuning.HoldDecayForIntervalSeconds(3600.0);
                sb.AppendLine("  ship activity=" + activity.ToString("0.00") + "/"
                    + activityWorld.Tuning.ShipActivityCreditCap.ToString("0.00")
                    + " (hold " + net.ToString("+0.000;-0.000;0.000") + " per hour)");
            }

            if (n.StableStationId == 0)
            {
                long cost, have;
                FoundStationValidator.Validate(FactionsPlugin.Instance.Runtime.World,
                    FactionsPlugin.Instance.Runtime.World.Tuning, owner.Tag, n.Id, out cost, out have);
                sb.AppendLine("  not founded: !factions station found " + n.Id
                    + " costs " + cost + " credits (treasury " + have + ")");
            }

            if (n.Capabilities.Count > 0)
                sb.AppendLine("  capabilities: " + string.Join(", ",
                    n.Capabilities.Select(kv => kv.Key + " x" + kv.Value)));

            // Faction-wide (not per-node, matching DevelopStationValidator's own faction-wide
            // affordability check) — lets the player see whether a capability opted into via
            // `develop <capability> reserve` currently has staging active, rather than guessing.
            List<InvestmentSignal> investments = InvestmentTargeting.SnapshotSignals(
                FactionsPlugin.Instance.Runtime.World, owner.Tag,
                FactionsPlugin.Instance.Runtime.World.Tuning);
            if (investments.Count > 0)
            {
                DateTime now = FactionsPlugin.Instance.Runtime.World.UtcNow;
                string joined = string.Join(", ", investments
                    .Where(sig => sig.Request.ExpiresUtc > now)
                    .Select(sig => sig.Request.Cap
                        // Which node the capability would be installed at -- managers post the
                        // capability only, so without this the player can't tell where it lands.
                        + (sig.Target.Resolved
                            ? " -> " + sig.Target.NodeId
                            : " (no target: " + InvestmentTargeting.Describe(sig.Target.Block) + ")")
                        + " (urgency=" + sig.Request.Urgency.ToString("F2")
                        + ", expires in " + (sig.Request.ExpiresUtc - now).TotalSeconds.ToString("0") + "s"
                        // Which components the install is still short of, so "staging" says whether
                        // it is progressing or stuck, not just that it exists.
                        + ShortfallSuffix(sig) + ")"));
                if (!string.IsNullOrEmpty(joined))
                    sb.AppendLine("  staging: " + joined);
            }

            if (n.Inventory.Count > 0)
            {
                var top = n.Inventory.Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => kv.Key + "=" + kv.Value);
                sb.AppendLine("  inventory: " + string.Join(", ", top));
            }
            return sb.ToString();
        }

        private static string Pct(double v) { return (v * 100.0).ToString("0") + "%"; }

        /// <summary>", short: MetalGrid 128, Motor 20" — the material rows still blocking an
        /// install, or empty when its BOM is covered. Faction-wide reserved + routable stock, the
        /// same sum <see cref="DevelopStationValidator"/> gates on.</summary>
        private static string ShortfallSuffix(InvestmentSignal sig)
        {
            if (sig.Materials == null) return "";
            var parts = sig.Materials
                .Where(r => r.Shortfall > 0)
                .Select(r => StripPrefix(r.Key) + " " + r.Shortfall)
                .ToList();
            return parts.Count == 0 ? "" : ", short: " + string.Join(", ", parts);
        }

        private static string StripPrefix(string invKey)
        {
            int colon = invKey == null ? -1 : invKey.IndexOf(':');
            return colon >= 0 ? invKey.Substring(colon + 1) : invKey;
        }
    }
}
