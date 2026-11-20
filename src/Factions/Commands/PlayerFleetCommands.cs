using System;
using System.Collections.Generic;
using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// Player-permission commands for a faction's <b>fleet doctrine</b> — what its fleet is made of
    /// (see docs/fleet-doctrine-plan.md). The chat interim for the Fleet window: every verb resolves
    /// the caller's own SE faction and routes into the same <see cref="FactionRuntime"/> entry point
    /// the bridge verb and (from the window slice) the client reach, so there is one set of rules.
    ///
    /// <para>Its own module rather than more <c>station orders</c> sub-verbs: the doctrine is not
    /// anchored at a station, and the retired Build goal living under <c>station orders</c> is
    /// exactly what made "keep N of a hull" read as one more thing a station does rather than as the
    /// shape of the faction's fleet.</para>
    /// </summary>
    public class PlayerFleetCommands : CommandModule
    {
        [Command("factions fleet", "List your faction's fleet doctrine, or show the syntax: !factions fleet [help]")]
        [Permission(MyPromoteLevel.None)]
        public void Fleet(string arg1 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (arg1 != null && (arg1.Equals("help", StringComparison.OrdinalIgnoreCase)
                              || arg1.Equals("usage", StringComparison.OrdinalIgnoreCase)
                              || arg1 == "?"))
            {
                Context.Respond(FleetUsage());
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.ListFleetDoctrineForPlayer(owner)
                + Environment.NewLine
                + "Sub-verbs: add, remove, priority, target, concurrent, pause, resume, refit, decommission — "
                + "!factions fleet help for syntax.");
        }

        /// <summary>Every `fleet` grammar in one block, for the same reason the `orders` and `queue`
        /// families carry one: Torch's own help only ever lists a command's own signature, never a
        /// verb family's, so a sub-verb is otherwise undiscoverable from chat.</summary>
        private static string FleetUsage()
        {
            var sb = new StringBuilder();
            sb.AppendLine("!factions fleet                                       list the doctrine, highest rank first");
            sb.AppendLine("!factions fleet add <prefabId> <count> [opts]   own <count> of a hull");
            sb.AppendLine("!factions fleet remove <id>                           delete a line");
            sb.AppendLine("!factions fleet target <id> <n>                       change how many of it you want");
            sb.AppendLine("!factions fleet priority <id> <n>                     rank it (higher wins a contested shipyard)");
            sb.AppendLine("!factions fleet concurrent <id> <n>                   change how many of it may build at once");
            sb.AppendLine("!factions fleet pause|resume <id>                     stop/restart evaluating it, keeping its id and rank");
            sb.AppendLine("!factions fleet refit [<design> <n>]                  refit parked hulls of an older version to the latest, n at a time");
            sb.AppendLine("!factions fleet decommission <design> [n] [confirm]   dismantle parked hulls of a design for their components");
            sb.AppendLine("add opts: concurrent=<n> node=<nodeId>");
            sb.AppendLine("  concurrent= how many may build at once (default 1). Reached across passes, one per yard each.");
            sb.AppendLine("  node=       confine both the count and the building to one node; omit for any owned Shipyard");
            sb.AppendLine("Roles: " + string.Join(", ", ShipyardManager.PlayerQueueRoles)
                          + ".  !factions station queue prefabs <role> lists the hulls a role can build.");
            sb.AppendLine("<count> is how many you OWN — parked plus in flight. It is a CEILING as well as a floor:");
            sb.AppendLine("  a line at its target stays there even while work waits on hulls. Raise it yourself.");
            sb.AppendLine("While this list is non-empty it is the only thing that builds FLEET hulls for you, and a");
            sb.AppendLine("  role with no line is not built at all. An empty list gives hull choice back to Factions.");
            sb.AppendLine("Shop stock is a separate list: !factions store.");
            sb.Append("<id> is the first column of the plain `fleet` listing, e.g. FD-001.");
            return sb.ToString();
        }

        [Command("factions fleet add", "Own N of a hull: !factions fleet add <prefabId> <count> [concurrent=<n>] [node=<nodeId>]")]
        [Permission(MyPromoteLevel.None)]
        public void FleetAdd(string prefabId = null, string count = null,
            string opt1 = null, string opt2 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int targetCount;
            if (string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(count)
                || !int.TryParse(count, out targetCount) || targetCount <= 0)
            {
                Context.Respond("Usage: !factions fleet add <prefabId> <count> [concurrent=<n>] [node=<nodeId>] "
                    + "— count must be a positive number, and a line names a hull, not a role. "
                    + "!factions fleet help for the options.");
                return;
            }

            // key=value rather than two more positional slots: the options are independent and mostly
            // omitted. Parsed by the same shared reader the bridge verb uses, so the two grammars
            // cannot drift and a typo is refused in both rather than silently dropped.
            int maxConcurrent = 1;
            string scopeNodeId = null;
            string optionError;
            if (!FactionRuntime.TryReadDoctrineOptions(new List<string> { opt1, opt2 }, 0,
                    ref maxConcurrent, ref scopeNodeId, out optionError))
            {
                Context.Respond(optionError);
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.AddDoctrineLineForPlayer(
                owner, prefabId, targetCount, maxConcurrent, scopeNodeId));
        }

        [Command("factions fleet remove", "Delete a doctrine line: !factions fleet remove <id>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetRemove(string lineId = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(lineId))
            {
                Context.Respond("Usage: !factions fleet remove <id> (the first column of `fleet`, e.g. FD-001).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.RemoveDoctrineLineForPlayer(owner, lineId));
        }

        [Command("factions fleet priority", "Rank a doctrine line: !factions fleet priority <id> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetPriority(string lineId = null, string rankToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int rank;
            if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(rankToken) || !int.TryParse(rankToken, out rank))
            {
                Context.Respond("Usage: !factions fleet priority <id> <n> "
                    + "(higher is evaluated first and wins a contested shipyard; 0 is the default).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetDoctrineLinePriorityForPlayer(owner, lineId, rank));
        }

        [Command("factions fleet target", "Change how many of a hull you want: !factions fleet target <id> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetTarget(string lineId = null, string targetToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int target;
            if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(targetToken) || !int.TryParse(targetToken, out target))
            {
                Context.Respond("Usage: !factions fleet target <id> <n> (how many you want to OWN — parked plus in flight).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetDoctrineLineTargetForPlayer(owner, lineId, target));
        }

        [Command("factions fleet concurrent", "Change how many may build at once: !factions fleet concurrent <id> <n>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetConcurrent(string lineId = null, string concurrencyToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int concurrency;
            if (string.IsNullOrEmpty(lineId) || string.IsNullOrEmpty(concurrencyToken)
                || !int.TryParse(concurrencyToken, out concurrency))
            {
                Context.Respond("Usage: !factions fleet concurrent <id> <n> (builds of this hull in progress at once; "
                    + "a line places one per yard per pass, so it reaches the count across passes).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetDoctrineLineConcurrencyForPlayer(
                owner, lineId, concurrency));
        }

        [Command("factions fleet refit",
            "Refit parked hulls of an older version of a design to its latest, n at a time: "
            + "!factions fleet refit [<design> <n>] (0 stops; no arguments lists)")]
        [Permission(MyPromoteLevel.None)]
        public void FleetRefit(string design = null, string capToken = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(design))
            {
                Context.Respond(FactionsPlugin.Instance.Runtime.DescribeHullRefitsForPlayer(owner));
                return;
            }
            int cap;
            if (string.IsNullOrEmpty(capToken) || !int.TryParse(capToken, out cap))
            {
                Context.Respond("Usage: !factions fleet refit <design> <n> (hulls in refit at once; 0 stops).");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetHullRefitCapForPlayer(owner, design, cap));
        }

        [Command("factions fleet decommission",
            "Dismantle parked hulls of a design (shelf included) for their components, and drop its lines: "
            + "!factions fleet decommission <design> [count] [confirm] (without confirm it only says what it would do)")]
        [Permission(MyPromoteLevel.None)]
        public void Decommission(string design = null, string arg2 = null, string arg3 = null)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            int count;
            bool apply;
            if (!FactionRuntime.TryReadDecommissionArgs(new List<string> { arg2, arg3 }, "confirm",
                    out count, out apply, out error))
            {
                Context.Respond(error + " Usage: !factions fleet decommission <design> [count] [confirm]");
                return;
            }
            // Without confirm it only reports what it would dismantle, which any member may read.
            if (apply && PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.DecommissionForPlayer(owner, design, count, apply));
        }

        [Command("factions fleet pause", "Stop evaluating a doctrine line: !factions fleet pause <id>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetPause(string lineId = null)
        {
            RespondToPauseToggle(lineId, true);
        }

        [Command("factions fleet resume", "Resume a paused doctrine line: !factions fleet resume <id>")]
        [Permission(MyPromoteLevel.None)]
        public void FleetResume(string lineId = null)
        {
            RespondToPauseToggle(lineId, false);
        }

        private void RespondToPauseToggle(string lineId, bool paused)
        {
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(lineId))
            {
                Context.Respond("Usage: !factions fleet " + (paused ? "pause" : "resume") + " <id>.");
                return;
            }

            if (PlayerCommandHelpers.RefuseSpend(Context, owner)) return;
            Context.Respond(FactionsPlugin.Instance.Runtime.SetDoctrineLinePausedForPlayer(owner, lineId, paused));
        }
    }
}
