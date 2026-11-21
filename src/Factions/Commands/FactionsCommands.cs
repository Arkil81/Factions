using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>Admin commands for the faction registry (standalone — no other-plugin hooks).</summary>
    public class FactionsCommands : CommandModule
    {
        private static FactionStore Store
        {
            get { return FactionsPlugin.Instance != null ? FactionsPlugin.Instance.Store : null; }
        }

        [Command("factions crew", "Survey hull crew capacity or run an unarmed crew trial on a bench hull.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Crew(string operation = "status", string target = null, string factionTag = null)
        {
            var args = new List<string> { "factions", "crew", operation };
            if (target != null) args.Add(target);
            if (factionTag != null) args.Add(factionTag);
            Context.Respond(CrewPresence.Handle(args.ToArray()));
        }

        [Command("factions decommission",
            "Dismantle a faction's parked hulls of a design: !factions decommission <tag> <design> [count] [apply]")]
        [Permission(MyPromoteLevel.Admin)]
        public void Decommission(string tag = null, string design = null, string arg3 = null, string arg4 = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            var t = new List<string> { "factions", "decommission" };
            foreach (string a in new[] { tag, design, arg3, arg4 }) if (a != null) t.Add(a);
            Context.Respond(FactionsPlugin.Instance.HandleDecommission(t.ToArray()));
        }

        [Command("factions prefab",
            "Ban a ship design server-wide, lift a ban, or list them: !factions prefab ban|unban <design>, !factions prefab bans")]
        [Permission(MyPromoteLevel.Admin)]
        public void Prefab(string sub = null, string design = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            var t = new List<string> { "factions", "prefab" };
            if (sub != null) t.Add(sub);
            if (design != null) t.Add(design);
            Context.Respond(FactionsPlugin.Instance.HandlePrefabBan(t.ToArray()));
        }

        [Command("factions init", "Initialise the registry from in-game NPC factions.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Init()
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            if (MySession.Static == null) { Context.Respond("No world loaded."); return; }

            FactionsPlugin.InitResult r = FactionsPlugin.Instance.InitializeFromNpcFactions();
            string msg = "Registered " + r.Added + " new NPC faction(s)"
                       + (r.Skipped > 0 ? " (" + r.Skipped + " already present)" : "");
            msg += r.Added > 0 ? ": " + string.Join(", ", r.AddedTags) : ".";
            if (r.NonNpc > 0)
                msg += "\nSkipped " + r.NonNpc + " non-NPC (IsEffectivelyNpc=false): " + string.Join(", ", r.NonNpcTags);
            Context.Respond(msg);
        }

        [Command("factions list", "List registered factions.")]
        [Permission(MyPromoteLevel.Admin)]
        public void List()
        {
            FactionStore store = Store;
            if (store == null) { Context.Respond("Factions plugin not initialised."); return; }

            var index = store.LoadIndex();
            if (index.Count == 0) { Context.Respond("No factions registered. Use !factions init."); return; }

            var sb = new StringBuilder();
            sb.AppendLine("Registered factions (" + index.Count + "):");
            foreach (IndexEntry e in index) sb.AppendLine("  [" + e.Tag + "]  personality=" + e.PersonalityId);
            Context.Respond(sb.ToString());
        }

        [Command("factions forget", "Remove a faction from the registry: !factions forget <tag>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Forget(string tag)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            Context.Respond(FactionsPlugin.Instance.ForgetFaction(tag));
        }

        [Command("factions scan", "Scan the world for NPC-faction stations and record them as influence sources.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Scan()
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            if (MySession.Static == null) { Context.Respond("No world loaded."); return; }

            // Torch dispatches command handlers on the game thread (chat events and the file bridge both
            // run there), so the entity enumeration is safe to do synchronously here.
            FactionsPlugin.ScanResult r = FactionsPlugin.Instance.ScanWorldAssets();
            var sb = new StringBuilder();
            sb.Append("Scanned " + r.GridsExamined + " grid(s) -> wrote " + r.SourcesWritten
                      + " station source(s) across " + r.FactionsTouched + " faction(s).");
            if (r.UnregisteredGrids > 0)
                sb.Append(" " + r.UnregisteredGrids + " station(s) owned by unregistered NPC faction(s) - run !factions init.");
            Context.Respond(sb.ToString());
        }

        [Command("factions sanitize", "Reduce the SE faction list to economy factions holding stations, deleting the removed factions' grids. Dry run unless 'apply': !factions sanitize [apply]")]
        [Permission(MyPromoteLevel.Admin)]
        public void Sanitize(string arg = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            if (MySession.Static == null) { Context.Respond("No world loaded."); return; }
            Context.Respond(FactionsPlugin.Instance.RunSanitize(
                string.Equals(arg, "apply", StringComparison.OrdinalIgnoreCase)));
        }

        [Command("factions resetTimers", "Clear all faction action cooldowns so they can act immediately. Add 'truces' to also clear active non-aggression windows: !factions resetTimers truces")]
        [Permission(MyPromoteLevel.Admin)]
        public void ResetTimers(string arg = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            if (runtime == null) { Context.Respond("Runtime not started (no session loaded)."); return; }

            bool clearTruces = string.Equals(arg, "truces", StringComparison.OrdinalIgnoreCase);
            int count = runtime.ResetCooldowns(clearTruces);
            string msg = "Cleared cooldowns for " + count + " faction(s)"
                       + (clearTruces ? " and all active truces." : ". (Pass 'truces' to also clear non-aggression windows.)");
            Context.Respond(msg);
        }

        [Command("factions debug", "Toggle debug logging per kind. Usage: !factions debug [trade|raid|guard|patrol|ferry|uplink|flight|produced|itemdemand|shipevents|investment|slots|...] — omit kind to toggle all fleet kinds.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Debug(string kind = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            Context.Respond(FactionsPlugin.ToggleDebug(kind));
        }

        [Command("factions reload", "Hot-reload tuning.json without restarting Torch.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Reload()
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            Context.Respond(FactionsPlugin.Instance.ReloadTuning());
        }

        [Command("factions nodes", "Validate and re-classify node zones from live SE gravity. Args: 'fix' to apply corrections, 'verbose' to log per-node gravity values: !factions nodes [fix|verbose]")]
        [Permission(MyPromoteLevel.Admin)]
        public void Nodes(string arg = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            if (runtime == null) { Context.Respond("Runtime not started (no session loaded)."); return; }

            bool verbose = string.Equals(arg, "verbose", StringComparison.OrdinalIgnoreCase);
            if (verbose)
            {
                System.Collections.Generic.List<string> lines = runtime.GetNodeGravityDump();
                foreach (string line in lines)
                    FactionsPlugin.Log.Info(line);
                Context.Respond("Per-node gravity dump written to Torch log (" + lines.Count + " lines).");
                return;
            }

            bool fix = string.Equals(arg, "fix", StringComparison.OrdinalIgnoreCase);
            NodeReclassifyResult r = runtime.ReclassifyNodes(fix);

            var sb = new StringBuilder();
            sb.Append("Nodes: total=").Append(r.Total)
              .Append("  Surface=").Append(r.Surface)
              .Append("  Orbit=").Append(r.Orbit)
              .Append("  DeepSpace=").Append(r.DeepSpace);
            if (fix)
            {
                sb.Append("\nFixed: zones=").Append(r.ZoneChanged)
                  .Append("  repositioned=").Append(r.Repositioned)
                  .Append("  featurePlaced=").Append(r.FeaturePlaced)
                  .Append("  separationBlocked=").Append(r.SeparationBlocked);
                if (r.ZoneChanged > 0 || r.Repositioned > 0 || r.FeaturePlaced > 0)
                    sb.Append("  (flushed to disk)");
            }
            else
            {
                sb.Append("\nWould fix: zones=").Append(r.ZoneChanged)
                  .Append("  reposition=").Append(r.Repositioned)
                  .Append("  featurePlaced=").Append(r.FeaturePlaced)
                  .Append("  separationBlocked=").Append(r.SeparationBlocked)
                  .Append("  (pass 'fix' to apply)");
            }
            sb.Append("\nStanding separation violations: ").Append(r.StandingViolations);
            string msg = sb.ToString();
            Context.Respond(msg);
            FactionsPlugin.Log.Info("!factions nodes: " + msg.Replace("\n", "  "));
        }

        [Command("factions sources", "List recorded influence sources for a faction: !factions sources <tag>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Sources(string tag)
        {
            FactionStore store = Store;
            if (store == null) { Context.Respond("Factions plugin not initialised."); return; }

            BrainDoc brain = store.LoadBrain(tag);
            if (brain == null) { Context.Respond("'" + tag + "' is not registered (no brain file)."); return; }

            List<SourceDoc> sources = brain.Sources ?? new List<SourceDoc>();
            if (sources.Count == 0) { Context.Respond("[" + brain.Tag + "] has no sources. Run !factions scan."); return; }

            var sb = new StringBuilder();
            sb.AppendLine("[" + brain.Tag + "] " + sources.Count + " source(s):");
            foreach (SourceDoc s in sources)
                sb.AppendLine("  " + s.Id + "  pos(" + s.X.ToString("0.#") + "," + s.Y.ToString("0.#") + ","
                              + s.Z.ToString("0.#") + ") km  str=" + s.Strength.ToString("0.00")
                              + "  r=" + s.Radius.ToString("0") + "km");
            Context.Respond(sb.ToString());
        }

        [Command("factions target", "What a node should be holding of each item, and which term decided it: !factions target <nodeId> [keyFilter]")]
        [Permission(MyPromoteLevel.Admin)]
        public void Target(string nodeId = null, string keyFilter = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            // Routed through the same handler the bridge uses so chat and a headless run cannot
            // disagree about what the oracle answered.
            var argv = new List<string> { "factions", "target" };
            if (nodeId != null) argv.Add(nodeId);
            if (keyFilter != null) argv.Add(keyFilter);
            Context.Respond(FactionsPlugin.Instance.HandleTarget(argv.ToArray()));
        }

        [Command("factions dampprobe", "Sample why a grid under dampeners drifts in gravity: !factions dampprobe [<entityId>|<grid name>] [trace [seconds]] (default: the grid you are in or next to; trace records it frame by frame, with your client's corrections)")]
        [Permission(MyPromoteLevel.Admin)]
        public void DampProbe(string a = null, string b = null, string c = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            var argv = new List<string> { "factions", "dampprobe" };
            if (a != null) argv.Add(a);
            if (b != null) argv.Add(b);
            if (c != null) argv.Add(c);
            Context.Respond(FactionsPlugin.Instance.HandleDampProbe(argv.ToArray(), Context.Player));
        }

        [Command("factions gridspeed", "Report or set the world's grid speed limits in m/s: !factions gridspeed [<large> [<small>]]")]
        [Permission(MyPromoteLevel.Admin)]
        public void GridSpeed(string large = null, string small = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            // Routed through the same handler the bridge uses so the two can never disagree about
            // parsing, clamping or what gets persisted.
            var argv = new List<string> { "factions", "gridspeed" };
            if (large != null) argv.Add(large);
            if (small != null) argv.Add(small);
            Context.Respond(FactionsPlugin.Instance.HandleGridSpeed(argv.ToArray()));
        }

        [Command("factions missilespeed", "Report or set the missile munition speed multiplier: !factions missilespeed [<scale>]")]
        [Permission(MyPromoteLevel.Admin)]
        public void MissileSpeed(string scale = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            // Routed through the same handler the bridge uses so the two can never disagree about
            // parsing, clamping or what gets persisted.
            var argv = new List<string> { "factions", "missilespeed" };
            if (scale != null) argv.Add(scale);
            Context.Respond(FactionsPlugin.Instance.HandleMissileSpeed(argv.ToArray()));
        }

        [Command("factions fuel", "Report the modelled fleet fuel burn, or clear it: !factions fuel [reset]")]
        [Permission(MyPromoteLevel.Admin)]
        public void Fuel(string arg = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            // Routed through the same handler the bridge uses so the two can never disagree.
            var argv = new List<string> { "factions", "fuel" };
            if (arg != null) argv.Add(arg);
            Context.Respond(FactionsPlugin.Instance.HandleFuel(argv.ToArray()));
        }

        [Command("factions world purge", "Remove station records: !factions world purge [orphans] [apply] [tag]")]
        [Permission(MyPromoteLevel.Admin)]
        public void WorldPurge(string a = null, string b = null, string c = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            // Routed through the bridge handler so the chat form and the bridge form cannot drift
            // apart in parsing or in what they refuse. The two leading tokens mirror what the bridge
            // splits out of its line, because HandleWorldPurge reads its arguments from index 3.
            var argv = new List<string> { "factions", "world", "purge" };
            foreach (string token in new[] { a, b, c })
                if (!string.IsNullOrEmpty(token)) argv.Add(token);
            Context.Respond(FactionsPlugin.Instance.HandleWorldPurgeCommand(argv.ToArray()));
        }

        [Command("factions reclaim", "Force-respawn an already-spawned station: !factions reclaim <tag> <nodeId>")]
        [Permission(MyPromoteLevel.Admin)]
        public void Reclaim(string tag, string nodeId)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            if (runtime == null) { Context.Respond("Runtime not started (no session loaded)."); return; }
            Context.Respond(runtime.ForceStationRespawn(tag, nodeId));
        }

        [Command("factions livery capture", "Capture the ship you're looking at for one faction, under its livery: !factions livery capture <tag> <name>")]
        [Permission(MyPromoteLevel.Admin)]
        public void LiveryCapture(string tag = null, string name = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(name))
            {
                Context.Respond("Usage: !factions livery capture <tag> <name>");
                return;
            }
            AgentCaptures relayed = FactionsPlugin.Instance.AgentCaptures;
            if (relayed != null)
            {
                Context.Respond(relayed.Chat(NexusCaptureKind.Livery, Context.Player, null, name, tag));
                return;
            }
            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            if (runtime == null) { Context.Respond("Runtime not started (no session loaded)."); return; }
            Context.Respond(runtime.CaptureShipForFaction(Context.Player, tag, name));
        }

        [Command("factions livery set", "Move a captured ship design to one faction under its livery, or back to all: !factions livery set <prefabId> <tag|all>")]
        [Permission(MyPromoteLevel.Admin)]
        public void LiverySet(string prefabId = null, string tag = null)
        {
            if (FactionsPlugin.Instance == null) { Context.Respond("Factions plugin not initialised."); return; }
            FactionRuntime runtime = FactionsPlugin.Instance.Runtime;
            if (runtime == null) { Context.Respond("Runtime not started (no session loaded)."); return; }
            if (string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(tag))
            {
                Context.Respond("Usage: !factions livery set <prefabId> <tag|all>");
                return;
            }
            Context.Respond(runtime.SetCaptureLivery(prefabId, tag));
        }
    }
}
