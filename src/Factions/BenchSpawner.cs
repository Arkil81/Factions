using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>factions bench</c>: spawns and removes test hulls on command, so the load ledger can be
    /// validated against a controlled change instead of whatever the world happens to be doing, and
    /// runs the fleet path's RC injection on one (<c>bench rc</c>) so it can be checked without
    /// waiting for a fleet to materialise.
    ///
    /// <para>A bench hull is unowned, has dampeners off and leaves at the given speed, directly
    /// away from the first online player, so it stays awake and never touches that player's
    /// island. Every grid it spawns, subgrids included, is renamed <c>bench:&lt;label&gt;</c> so
    /// its ledger rows can be summed by name, and is not saved.</para>
    ///
    /// <para><b>Scenarios</b> (<c>fight</c>, <c>raid</c>) are different in kind: real
    /// <see cref="PendingFleetAction"/>s (<see cref="BenchScenario"/>) put in front of the first
    /// online player, so materialisation, the flight controller and the meeting duel all run on
    /// them. <c>leave</c>/<c>return</c> take a scenario's grids out of the world and back with the
    /// player still present, <c>status</c> reads it, and <c>duel</c> runs the abstract duel alone,
    /// with no player and no world.</para>
    /// </summary>
    internal static class BenchSpawner
    {
        private static readonly Dictionary<string, List<MyCubeGrid>> Spawned =
            new Dictionary<string, List<MyCubeGrid>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The drift each label was given, re-applied once a second by <see cref="Tick"/>.
        /// A spawn option alone did not hold it: on 2026-09-23 a small-grid bench hull slowed and
        /// fell asleep in 2 to 5 of 9 censuses, which removes the condition being measured.</summary>
        private static readonly Dictionary<string, Vector3> Drift =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        /// <summary>A <c>bench motion</c> drift in the main hull's own frame, so it turns with the hull.</summary>
        private static readonly Dictionary<string, Vector3> DriftLocal =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Vector3> Spin =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        private static int _frame;

        private static readonly Dictionary<string, string> Prefabs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>An RC injection waiting for SE's split pass, which runs after the frame the
        /// exchange was made in, and then its result.</summary>
        private sealed class RcCheck
        {
            public int DueFrame;
            public string Donor;
            public string Tag;
            public int BlocksBefore;
            public int GridsBefore;
            public int SplitGrids;
            public int SplitBlocks;
            public string Result;
        }

        private static readonly Dictionary<string, RcCheck> RcChecks =
            new Dictionary<string, RcCheck>(StringComparer.OrdinalIgnoreCase);

        // Grid entity id -> the label whose RC check is watching it for a split.
        private static readonly Dictionary<long, string> SplitWatch = new Dictionary<long, string>();

        internal const string Usage =
            "bench: usage 'factions bench fight <label> <atkTag> <atkPrefab,..> <defTag> <defPrefab,..> [sepM] [distM] [dwell=S] [ledger=1] [hostile=any]'"
            + " | 'factions bench raid <label> <atkTag> <prefab,..> <nodeId|here> [dwell=S] [ledger=1] [react=1] [hostile=any]'"
            + " | 'factions bench duel <atkPrefab,..> <defPrefab,..> [n] [seed] [flat=N] [atkArm=A] [defArm=A] [atkBreak=L] [defBreak=L] [contested=0]'"
            + " | 'factions bench leave|return|status <label>' | 'factions bench damage <label> <integrity 0..1>'"
            + " | 'factions bench tactics <tag> [opponentTag]'"
            + " | 'factions bench arm <label> <atk|def|raid|all> <Brawl|Standoff|Kite|AttackRun|clear> [Low|Mid|High]'"
            + " | 'factions bench wing <label> <form|focus|hold|breakoff|return|release|cut|status> [steamId]'"
            + " | 'factions bench protect <label|nodeId> [npc|status|antenna on|off]'"
            + " | 'factions bench spawn <label> <prefabId> <distanceM> <speedMs>' | 'factions bench rc <label>' | 'factions bench clear [label]' | 'factions bench list'"
            + " | 'factions bench motion <label> <speedMs> <turnDegreesPerSecond>'"
            + " | 'factions bench restore-plan <gridEntityId|looking> [prefab=<id>]' | 'factions bench register-design <gridEntityId|looking> [force=1]'"
            + " | 'factions bench restore <gridEntityId|looking> [confirm]'"
            + " | 'factions bench phantom add|clear|list ...' (stand-in players; 'factions bench phantom help' for its usage)"
            + " | 'factions bench gc' (blocking full collection; heap before/after with entity and voxel counts)"
            + " | 'factions bench roots' (static-field paths that still reach closed voxel maps; seconds on the game thread)";

        internal static string Handle(string[] t)
        {
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "fight":  return Fight(t);
                case "raid":   return Raid(t);
                case "duel":   return Duel(t);
                case "leave":  return t.Length > 3 ? Leave(t[3]) : Usage;
                case "return": return t.Length > 3 ? Return(t[3]) : Usage;
                case "status": return t.Length > 3 ? Status(t[3]) : Usage;
                case "damage": return t.Length > 4 ? Damage(t[3], t[4]) : Usage;
                case "tactics": return t.Length > 3 ? Tactics(t[3], t.Length > 4 ? t[4] : null) : Usage;
                case "arm": return t.Length > 5 ? Arm(t[3], t[4], t[5], t.Length > 6 ? t[6] : null) : Usage;
                case "wing":  return Wing(t);
                case "protect": return Protect(t);
                case "spawn": return Spawn(t);
                case "phantom": return BenchPhantoms.Handle(t);
                case "rock": return BenchRocks.Handle(t);
                case "gc": return BenchGc.Run();
                case "roots": return BenchRoots.Run();
                case "motion": return Motion(t);
                case "rc":    return t.Length > 3 ? Rc(t[3]) : Usage;
                case "clear": return Clear(t.Length > 3 ? t[3] : null);
                case "list": return List();
                case "restore-plan": return RestorePlan(t);
                case "register-design": return RegisterDesign(t);
                case "restore": return Restore(t);
                default: return Usage;
            }
        }

        /// <summary><c>restore-plan</c>: the read-only restoration plan of one grid (see
        /// <see cref="FactionRuntime.BenchRestorePlan"/>). Changes nothing.</summary>
        private static string RestorePlan(string[] t)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> args = Args(t, options);
            if (args.Count < 1) return Usage;
            string prefab;
            options.TryGetValue("prefab", out prefab);
            return rt.BenchRestorePlan(args[0], prefab);
        }

        /// <summary><c>restore</c>: the Services Terminal's restore bill for one grid, and with
        /// <c>confirm</c> the timed restore itself (<see cref="RestorationService.BenchRestore"/>).
        /// <c>confirm</c> on a grid already restoring stops it.</summary>
        private static string Restore(string[] t)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            RestorationService service = plugin != null ? plugin.Restoration : null;
            if (service == null) return "bench: no session loaded.";
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> args = Args(t, options);
            if (args.Count < 1) return Usage;
            bool confirm = args.Count > 1 && string.Equals(args[1], "confirm", StringComparison.OrdinalIgnoreCase);
            return service.BenchRestore(args[0], confirm);
        }

        /// <summary><c>register-design</c>: snapshots a whole grid as its restoration design.</summary>
        private static string RegisterDesign(string[] t)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> args = Args(t, options);
            if (args.Count < 1) return Usage;
            string force;
            return rt.BenchRegisterDesign(args[0], options.TryGetValue("force", out force) && force == "1");
        }

        private static string Spawn(string[] t)
        {
            if (t.Length < 7) return Usage;
            string label = t[3];
            string prefab = t[4];
            double distance, speed;
            if (!double.TryParse(t[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out distance)
                || !double.TryParse(t[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out speed))
                return Usage;
            if (Spawned.ContainsKey(label)) return "bench: '" + label + "' is already out; clear it first.";

            Vector3D origin;
            if (!TryFirstPlayerPosition(out origin)) return "bench: no online player to spawn beside.";

            // Labels fan out on different axes so two hulls never share a path.
            Vector3D dir = DirectionFor(Spawned.Count);
            Vector3D pos = origin + dir * distance;
            var grids = new List<MyCubeGrid>();
            Spawned[label] = grids;
            Drift[label] = (Vector3)(dir * speed);
            Prefabs[label] = prefab;
            var callbacks = new Stack<Action>();
            callbacks.Push(() =>
            {
                foreach (MyCubeGrid g in grids)
                {
                    if (g == null) continue;
                    g.DisplayName = "bench:" + label;
                }
                FactionsPlugin.Log.Info("Bench: spawned '" + label + "' (" + prefab + "), " + grids.Count
                                        + " grid(s), " + BlockCount(grids) + " blocks, " + distance + " m out at "
                                        + speed + " m/s.");
            });
            MyPrefabManager.Static.SpawnPrefab(
                grids, prefab, pos, Vector3.Forward, Vector3.Up,
                initialLinearVelocity: (Vector3)(dir * speed),
                spawningOptions: SpawningOptions.DisableDampeners | SpawningOptions.DisableSave | SpawningOptions.UseOnlyWorldMatrix,
                ownerId: 0,
                updateSync: true,
                callbacks: callbacks);
            return "bench: spawning '" + label + "' (" + prefab + ") " + distance + " m from the player, drifting at " + speed + " m/s.";
        }

        /// <summary>
        /// Runs the fleet path's RC injection (<see cref="VanillaFleetSpawner.InjectRc"/>) on the
        /// label's largest grid and watches every grid of the label for a split. SE splits a
        /// disconnected piece after the frame, so the verdict — blocks before and after, and any
        /// grid split off — is logged as <c>Bench: rc ...</c> about a second later and shown by
        /// <c>factions bench list</c>. A split-off piece joins the label, named
        /// <c>bench:&lt;label&gt;:split</c>, so <c>clear</c> removes it too.
        ///
        /// <para>A hull that already carries a remote control is refused: the fleet path would fly
        /// it on that one and inject nothing. AI blocks are not consulted — the fleet path tries
        /// them first, and the bench exists to exercise the injection.</para>
        /// </summary>
        private static string Rc(string label)
        {
            List<MyCubeGrid> grids;
            if (!Spawned.TryGetValue(label, out grids)) return "bench: nothing out under '" + label + "'.";
            if (RcChecks.ContainsKey(label)) return "bench: '" + label + "' has already had its RC injection; clear and respawn to repeat it.";

            MyCubeGrid main = null;
            int alive = 0;
            foreach (MyCubeGrid g in grids)
            {
                if (g == null || g.Closed || g.MarkedForClose) continue;
                alive++;
                if (main == null || g.BlocksCount > main.BlocksCount) main = g;
            }
            if (main == null) return "bench: '" + label + "' has no live grid.";
            foreach (MyRemoteControl unused in main.GetFatBlocks<MyRemoteControl>())
                return "bench: '" + label + "' already carries a remote control; the fleet path would use it and inject nothing.";

            var check = new RcCheck { BlocksBefore = BlockCount(grids), GridsBefore = alive };
            foreach (MyCubeGrid g in grids)
            {
                if (g == null || g.Closed) continue;
                SplitWatch[g.EntityId] = label;
                g.OnGridSplit += OnWatchedGridSplit;
            }

            string donor;
            MyRemoteControl rc = VanillaFleetSpawner.InjectRc(main, out donor);
            check.Donor = rc != null ? donor : "none";
            check.Tag = rc != null ? VanillaFleetSpawner.LastRcTag() : "";
            check.DueFrame = _frame + 120;
            RcChecks[label] = check;

            return rc != null
                ? "bench: '" + label + "' took an RC in place of " + donor + check.Tag
                  + ". Split verdict follows in the Torch log as 'Bench: rc' and in 'factions bench list'."
                : "bench: '" + label + "' refused every donor; the fleet path would leave this hull unwired.";
        }

        private static void OnWatchedGridSplit(MyCubeGrid parent, MyCubeGrid child)
        {
            string label;
            if (parent == null || child == null || !SplitWatch.TryGetValue(parent.EntityId, out label)) return;
            RcCheck check;
            List<MyCubeGrid> grids;
            if (!RcChecks.TryGetValue(label, out check) || !Spawned.TryGetValue(label, out grids)) return;
            check.SplitGrids++;
            check.SplitBlocks += child.BlocksCount;
            child.DisplayName = "bench:" + label + ":split";
            grids.Add(child);
        }

        /// <summary>Closes any RC check whose split pass has had time to run: logs the verdict and
        /// stops watching its grids.</summary>
        private static void SettleRcChecks()
        {
            foreach (KeyValuePair<string, RcCheck> kv in RcChecks)
            {
                RcCheck c = kv.Value;
                if (c.Result != null || _frame < c.DueFrame) continue;

                List<MyCubeGrid> grids;
                int blocksAfter = Spawned.TryGetValue(kv.Key, out grids) ? BlockCount(grids) : 0;
                Unwatch(kv.Key);

                string prefab;
                Prefabs.TryGetValue(kv.Key, out prefab);
                c.Result = "rc=" + c.Donor + " blocks " + c.BlocksBefore + " -> " + blocksAfter
                         + " split=" + c.SplitGrids + " grid(s)/" + c.SplitBlocks + " block(s)";
                FactionsPlugin.Log.Info("Bench: rc '" + kv.Key + "' (" + prefab + ") donor=" + c.Donor + c.Tag
                                        + " blocks " + c.BlocksBefore + " -> " + blocksAfter
                                        + ", split off " + c.SplitGrids + " grid(s), " + c.SplitBlocks + " block(s).");
            }
        }

        private static void Unwatch(string label)
        {
            List<MyCubeGrid> grids;
            if (Spawned.TryGetValue(label, out grids))
                foreach (MyCubeGrid g in grids)
                    if (g != null) g.OnGridSplit -= OnWatchedGridSplit;
            var drop = new List<long>();
            foreach (KeyValuePair<long, string> kv in SplitWatch)
                if (string.Equals(kv.Value, label, StringComparison.OrdinalIgnoreCase)) drop.Add(kv.Key);
            foreach (long id in drop) SplitWatch.Remove(id);
        }

        private static string Clear(string label)
        {
            FactionRuntime rt = Runtime();
            string scenarios = rt != null && (label == null || rt.BenchLabelInUse(label)) ? rt.ClearBench(label) : null;

            var labels = new List<string>();
            if (label == null) labels.AddRange(Spawned.Keys);
            else if (Spawned.ContainsKey(label)) labels.Add(label);
            else if (scenarios != null) return "bench: cleared scenario '" + label + "': " + scenarios + ".";
            else return "bench: nothing out under '" + label + "'.";

            int closed = 0, cleared = 0, occupied = 0;
            foreach (string l in labels)
            {
                if (HasPlayerAboard(Spawned[l])) { occupied++; continue; }
                cleared++;
                foreach (MyCubeGrid g in Spawned[l])
                {
                    if (g == null || g.Closed || g.MarkedForClose) continue;
                    g.Close();
                    closed++;
                }
                Unwatch(l);
                Spawned.Remove(l);
                Drift.Remove(l);
                DriftLocal.Remove(l);
                Spin.Remove(l);
                Prefabs.Remove(l);
                RcChecks.Remove(l);
            }
            return "bench: cleared " + cleared + " label(s), " + closed + " grid(s)."
                   + (occupied > 0 ? " Kept " + occupied + " label(s): player aboard or within 3 m of hull bounds." : "")
                   + (scenarios != null ? " Scenarios: " + scenarios + "." : "");
        }

        private static bool HasPlayerAboard(List<MyCubeGrid> grids)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return true;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Character == null) continue;
                foreach (MyCubeGrid grid in grids)
                {
                    if (grid == null || grid.Closed) continue;
                    BoundingBoxD box = grid.PositionComp.WorldAABB;
                    box.Inflate(3);
                    if (box.Contains(player.Character.PositionComp.GetPosition()) == ContainmentType.Contains) return true;
                }
            }
            return false;
        }

        private static string List()
        {
            FactionRuntime rt = Runtime();
            string scenarios = rt != null ? rt.BenchLabels() : null;
            if (Spawned.Count == 0) return scenarios != null ? "bench: scenarios " + scenarios : "bench: nothing out.";
            var sb = new StringBuilder("bench:");
            foreach (KeyValuePair<string, List<MyCubeGrid>> kv in Spawned)
            {
                int alive = 0, awake = 0;
                foreach (MyCubeGrid g in kv.Value)
                {
                    if (g == null || g.Closed) continue;
                    alive++;
                    if (g.Physics != null && g.Physics.IsActive) awake++;
                }
                sb.Append(" ").Append(kv.Key).Append("=").Append(alive).Append(" grid(s), ")
                  .Append(BlockCount(kv.Value)).Append(" blocks, ").Append(awake).Append(" awake");
                RcCheck c;
                if (RcChecks.TryGetValue(kv.Key, out c))
                    sb.Append(", ").Append(c.Result ?? "rc=" + c.Donor + " (split pass pending)");
                sb.Append(";");
            }
            if (scenarios != null) sb.Append(" scenarios ").Append(scenarios);
            return sb.ToString();
        }

        /// <summary>Called every frame: sets every bench grid's velocity to its label's requested
        /// drift and spin, so a bench hull moves smoothly — and stays awake — whatever its own
        /// thrusters, dampeners or collisions do. A drift set by <c>bench motion</c> turns with the
        /// hull, as a ship's velocity follows its heading. Once a second, the bench sweep. Costs nothing
        /// when none is out.
        ///
        /// <para>Applied every frame, not once a second: a once-a-second reset is a velocity step
        /// every second on a hull with live propulsion, which is not a flight anyone would fly and was
        /// indistinguishable from a crew-footing failure.</para></summary>
        internal static void Tick()
        {
            if (++_frame % 60 == 0)
            {
                try
                {
                    FactionRuntime rt = Runtime();
                    if (rt != null) rt.BenchSweep();
                }
                catch { /* a test harness must not be able to fail the frame */ }
                QuietOwnFlight();
            }
            if (Spawned.Count == 0) return;
            try
            {
                foreach (KeyValuePair<string, List<MyCubeGrid>> kv in Spawned)
                {
                    Vector3 v;
                    if (!Drift.TryGetValue(kv.Key, out v)) continue;
                    MyCubeGrid main = CrewGrid(kv.Key);
                    Vector3 local;
                    if (main != null && main.Physics != null && DriftLocal.TryGetValue(kv.Key, out local))
                        v = Vector3.TransformNormal(local, main.WorldMatrix);
                    Vector3 spin;
                    if (!Spin.TryGetValue(kv.Key, out spin)) spin = Vector3.Zero;
                    foreach (MyCubeGrid g in kv.Value)
                    {
                        if (g == null || g.Closed || g.Physics == null || g.IsStatic) continue;
                        // Connected subgrids need the tangential velocity around the main hull.
                        Vector3 offset = main != null ? (Vector3)(g.PositionComp.GetPosition() - main.PositionComp.GetPosition()) : Vector3.Zero;
                        g.Physics.LinearVelocity = v + Vector3.Cross(spin, offset);
                        g.Physics.AngularVelocity = spin;
                    }
                }
                if (RcChecks.Count > 0 && _frame % 60 == 0) SettleRcChecks();
            }
            catch { /* a test harness must not be able to fail the frame */ }
        }

        /// <summary>Once a second, switches off the thrusters, gyroscopes and remote-control
        /// autopilot of every bench grid under bench motion. The bench sets the hull's velocity and
        /// spin every frame; its own gyros and thrusters pushing back shook the deck every frame,
        /// which held armed crew jittering in their aim and was taken for a crew defect (operator,
        /// 2026-10-08: gyros off, "jitter stopped"). A block switched back on is switched off again.</summary>
        private static void QuietOwnFlight()
        {
            if (Spawned.Count == 0) return;
            try
            {
                foreach (KeyValuePair<string, List<MyCubeGrid>> kv in Spawned)
                {
                    if (!Drift.ContainsKey(kv.Key)) continue;
                    foreach (MyCubeGrid g in kv.Value)
                    {
                        if (g == null || g.Closed || g.MarkedForClose) continue;
                        foreach (MyThrust thrust in g.GetFatBlocks<MyThrust>()) if (thrust.Enabled) thrust.Enabled = false;
                        foreach (MyGyro gyro in g.GetFatBlocks<MyGyro>()) if (gyro.Enabled) gyro.Enabled = false;
                        foreach (MyRemoteControl remote in g.GetFatBlocks<MyRemoteControl>())
                            if (remote.IsAutopilotEnabled()) remote.SetAutoPilotEnabled(false);
                    }
                }
            }
            catch { /* a test harness must not be able to fail the frame */ }
        }

        private static int BlockCount(List<MyCubeGrid> grids)
        {
            int n = 0;
            foreach (MyCubeGrid g in grids) if (g != null && !g.Closed) n += g.BlocksCount;
            return n;
        }

        /// <summary>The largest live bench hull for the unarmed crew trial. Selecting an explicit test
        /// label prevents a diagnostics command from populating a player's ship.</summary>
        internal static MyCubeGrid CrewGrid(string label)
        {
            List<MyCubeGrid> grids;
            if (!Spawned.TryGetValue(label, out grids)) return null;
            MyCubeGrid hull = null;
            foreach (MyCubeGrid grid in grids)
                if (grid != null && !grid.Closed && !grid.MarkedForClose
                    && (hull == null || grid.BlocksCount > hull.BlocksCount)) hull = grid;
            return hull;
        }

        internal static IEnumerable<MyCubeGrid> CrewGrids(string label)
        {
            List<MyCubeGrid> grids;
            return Spawned.TryGetValue(label, out grids) ? (IEnumerable<MyCubeGrid>)grids : new MyCubeGrid[0];
        }

        internal static string CrewPrefab(string label)
        {
            string prefab;
            return Prefabs.TryGetValue(label, out prefab) ? prefab : null;
        }

        private static string Motion(string[] t)
        {
            double speed, turn;
            if (t.Length != 6 || !TryDouble(t[4], out speed) || !TryDouble(t[5], out turn)
                || double.IsNaN(speed) || double.IsInfinity(speed) || double.IsNaN(turn) || double.IsInfinity(turn)
                || speed < 0 || speed > 100 || Math.Abs(turn) > 5) return Usage;
            MyCubeGrid grid = CrewGrid(t[3]);
            if (grid == null || grid.IsStatic) return "bench: no dynamic test hull '" + t[3] + "'.";
            Vector3 current;
            Drift.TryGetValue(t[3], out current);
            Vector3 direction = current.LengthSquared() > 0 ? Vector3.Normalize(current) : (Vector3)grid.WorldMatrix.Forward;
            Drift[t[3]] = direction * (float)speed;
            DriftLocal[t[3]] = Vector3.TransformNormal(direction * (float)speed, grid.PositionComp.WorldMatrixNormalizedInv);
            Spin[t[3]] = (Vector3)grid.WorldMatrix.Up * MathHelper.ToRadians((float)turn);
            return "bench: motion '" + t[3] + "' speed_ms=" + speed.ToString("0.00", CultureInfo.InvariantCulture)
                + " turn_deg_s=" + turn.ToString("0.00", CultureInfo.InvariantCulture) + ". Applied every frame; the drift turns with the hull; the hull's own thrusters and gyros are kept off.";
        }

        private static Vector3D DirectionFor(int index)
        {
            switch (index % 4)
            {
                case 0: return Vector3D.Right;
                case 1: return Vector3D.Left;
                case 2: return Vector3D.Up;
                default: return Vector3D.Down;
            }
        }

        // ---- scenarios -------------------------------------------------------------------------

        private const int MaxHullsPerSide = 8;
        private const double DefaultSeparationM = 600;
        private const double DefaultDistanceM = 1500;
        private const double MinDistanceM = 200;
        private const double MinDwellSeconds = 30;
        private const double MaxDwellSeconds = 3600;

        private const string NoPlayer = "bench: no online player to stage it beside; fight and raid are placed relative to one. 'factions bench duel' needs none.";

        private static FactionRuntime Runtime()
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            return plugin != null ? plugin.Runtime : null;
        }

        /// <summary>Splits the arguments after the sub-verb into positionals and key=value options.</summary>
        private static List<string> Args(string[] t, Dictionary<string, string> options)
        {
            var positional = new List<string>();
            for (int i = 3; i < t.Length; i++)
            {
                int eq = t[i].IndexOf('=');
                if (eq > 0) options[t[i].Substring(0, eq).ToLowerInvariant()] = t[i].Substring(eq + 1);
                else positional.Add(t[i]);
            }
            return positional;
        }

        private static bool TryDouble(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static BenchFlags Flags(Dictionary<string, string> options)
        {
            BenchFlags f = BenchFlags.None;
            string v;
            if (options.TryGetValue("ledger", out v) && v == "1") f |= BenchFlags.Ledger;
            if (options.TryGetValue("react", out v) && v == "1") f |= BenchFlags.React;
            if (options.TryGetValue("hostile", out v) && string.Equals(v, "any", StringComparison.OrdinalIgnoreCase))
                f |= BenchFlags.HostileAny;
            return f;
        }

        /// <summary>Parses <c>dwell=S</c>, defaulting to the live raid dwell.</summary>
        private static string Dwell(Dictionary<string, string> options, FactionTuning tuning, out double dwell)
        {
            dwell = tuning.RaidDwellSeconds > 0 ? tuning.RaidDwellSeconds : 300;
            string v;
            if (!options.TryGetValue("dwell", out v)) return null;
            if (!TryDouble(v, out dwell) || dwell < MinDwellSeconds || dwell > MaxDwellSeconds)
                return "bench: dwell must be " + MinDwellSeconds + ".." + MaxDwellSeconds + " seconds.";
            return null;
        }

        /// <summary>A comma-separated prefab list, each checked against the prefab catalogue.</summary>
        private static string ParsePrefabs(FactionRuntime rt, string arg, out List<string> prefabs)
        {
            prefabs = new List<string>();
            foreach (string p in arg.Split(','))
                if (p.Trim().Length > 0) prefabs.Add(p.Trim());
            if (prefabs.Count == 0) return "bench: empty prefab list '" + arg + "'.";
            if (prefabs.Count > MaxHullsPerSide) return "bench: at most " + MaxHullsPerSide + " hulls a side.";
            SePrefabCatalogue cat = rt.Catalogue;
            if (cat != null)
                foreach (string p in prefabs)
                    if (!cat.Contains(p)) return "bench: prefab '" + p + "' is not in the prefab catalogue.";
            return null;
        }

        /// <summary>A registered faction with an SE faction behind it, so its grids can be owned.</summary>
        private static string ParseTag(FactionRuntime rt, string tag, out FactionState st)
        {
            st = rt.BenchState(tag);
            if (st == null) return "bench: '" + tag + "' is not a registered faction.";
            if (MySession.Static == null || MySession.Static.Factions.TryGetFactionByTag(st.Tag) == null)
                return "bench: '" + tag + "' has no SE faction to own its grids.";
            return null;
        }

        private static string LabelCheck(FactionRuntime rt, string label)
        {
            if (!BenchScenario.IsValidLabel(label))
                return "bench: label must be 1.." + BenchScenario.MaxLabelLength + " letters, digits, '-' or '_'.";
            if (rt.BenchLabelInUse(label)) return "bench: scenario '" + label + "' is already running; clear it first.";
            return null;
        }

        private static double Dps(List<WeaponBand> bands)
        {
            double s = 0;
            if (bands != null) foreach (WeaponBand b in bands) s += b.Dps;
            return s;
        }

        private static Vec3 Km(Vector3D m) { return new Vec3(m.X / 1000.0, m.Y / 1000.0, m.Z / 1000.0); }

        /// <summary>
        /// <c>bench fight</c>: two hostile squadrons at a free point <c>distM</c> ahead of the first
        /// online player, <c>sepM</c> apart across the line of sight -- the attacker a Raid already
        /// on its Engage dwell, the defender a Guard standing its duty, both at the bench node
        /// <c>bench:&lt;label&gt;</c>. When the dwell ends the ordinary meeting duel runs between
        /// them and <c>BENCH_RESOLVED</c> writes it up.
        /// </summary>
        private static string Fight(string[] t)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> a = Args(t, options);
            if (a.Count < 5) return Usage;
            FactionRuntime rt = Runtime();
            if (rt == null || rt.BenchWorld == null) return "bench: no session loaded.";
            FactionTuning tuning = rt.BenchTuning;

            string label = a[0];
            string err = LabelCheck(rt, label);
            if (err != null) return err;
            FactionState atk, def;
            if ((err = ParseTag(rt, a[1], out atk)) != null) return err;
            if ((err = ParseTag(rt, a[3], out def)) != null) return err;
            if (string.Equals(atk.Tag, def.Tag, StringComparison.OrdinalIgnoreCase)) return "bench: the two sides need different factions.";
            List<string> atkPrefabs, defPrefabs;
            if ((err = ParsePrefabs(rt, a[2], out atkPrefabs)) != null) return err;
            if ((err = ParsePrefabs(rt, a[4], out defPrefabs)) != null) return err;

            double sep = DefaultSeparationM, dist = DefaultDistanceM, dwell;
            if (a.Count > 5 && !TryDouble(a[5], out sep)) return Usage;
            if (a.Count > 6 && !TryDouble(a[6], out dist)) return Usage;
            if ((err = Dwell(options, tuning, out dwell)) != null) return err;
            double reachM = tuning.ObservationRadiusKm * 1000.0 - 1000.0;
            if (sep < 0 || dist < MinDistanceM || dist + sep / 2 > reachM)
                return "bench: need sepM >= 0 and " + MinDistanceM + " <= distM, with distM + sepM/2 <= "
                       + reachM.ToString("0", CultureInfo.InvariantCulture) + " so both sides are inside the observation radius.";

            Vector3D origin, forward, right;
            if (!TryFirstPlayerFrame(out origin, out forward, out right)) return NoPlayer;
            Vector3D centre = origin + forward * dist;
            Vec3 atkPos = Km(centre + right * (sep / 2));
            Vec3 defPos = Km(centre - right * (sep / 2));

            DateTime now = rt.ActiveUtc;
            BenchFlags flags = Flags(options);
            string node = BenchScenario.FightNodeId(label);
            PendingFleetAction attacker = BenchScenario.Attacker(label, "atk", atk.Tag, def.Tag, node, atkPos,
                atkPrefabs, rt.BenchCatalogue, now, dwell, flags);
            PendingFleetAction defender = BenchScenario.Defender(label, "def", def.Tag, node, defPos,
                defPrefabs, rt.BenchCatalogue, now, dwell, tuning, flags);

            string until = attacker.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string staged = "BENCH_STAGED " + label + " fight " + attacker.LogId + " " + atk.Tag
                            + " hulls=" + attacker.HullCount + " dps=" + Dps(attacker.WeaponBands).ToString("0", CultureInfo.InvariantCulture)
                            + " vs " + defender.LogId + " " + def.Tag
                            + " hulls=" + defender.HullCount + " dps=" + Dps(defender.WeaponBands).ToString("0", CultureInfo.InvariantCulture)
                            + " @" + node + " sep=" + sep.ToString("0", CultureInfo.InvariantCulture) + "m"
                            + " dist=" + dist.ToString("0", CultureInfo.InvariantCulture) + "m"
                            + " dwell=" + dwell.ToString("0", CultureInfo.InvariantCulture) + "s until=" + until
                            + " flags=" + flags;
            string stance = rt.StageBench(label,
                new List<PendingFleetAction> { attacker, defender },
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(atk.Tag, def.Tag) },
                staged);
            return "bench: fight '" + label + "' staged " + dist.ToString("0", CultureInfo.InvariantCulture) + " m ahead of the player: "
                   + attacker.LogId + " " + atk.Tag + " x" + attacker.HullCount + " vs " + defender.LogId + " " + def.Tag + " x" + defender.HullCount
                   + ", " + sep.ToString("0", CultureInfo.InvariantCulture) + " m apart. SE stance " + stance
                   + ". The meeting duel runs at " + until + " UTC and writes BENCH_RESOLVED " + label + ".";
        }

        /// <summary>
        /// <c>bench raid</c>: a Raid already on its Engage dwell at a real node -- <c>here</c> is the
        /// node nearest the first online player -- that meets the node's station and any Guard
        /// standing there when the dwell ends. The node itself is untouched whatever the outcome;
        /// real defenders present fight it for real.
        /// </summary>
        private static string Raid(string[] t)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> a = Args(t, options);
            if (a.Count < 4) return Usage;
            FactionRuntime rt = Runtime();
            if (rt == null || rt.BenchWorld == null) return "bench: no session loaded.";
            FactionTuning tuning = rt.BenchTuning;

            string label = a[0];
            string err = LabelCheck(rt, label);
            if (err != null) return err;
            FactionState atk;
            if ((err = ParseTag(rt, a[1], out atk)) != null) return err;
            List<string> prefabs;
            if ((err = ParsePrefabs(rt, a[2], out prefabs)) != null) return err;
            double dwell;
            if ((err = Dwell(options, tuning, out dwell)) != null) return err;

            Vector3D origin, forward, right;
            if (!TryFirstPlayerFrame(out origin, out forward, out right)) return NoPlayer;
            Vec3 playerKm = Km(origin);

            SpatialNode node = null;
            if (string.Equals(a[3], "here", StringComparison.OrdinalIgnoreCase))
            {
                double best = double.MaxValue;
                foreach (SpatialNode n in rt.BenchWorld.Graph.Nodes.Values)
                {
                    double d = n.Position.DistanceTo(playerKm);
                    if (d < best) { best = d; node = n; }
                }
                if (node == null) return "bench: the world has no nodes.";
            }
            else if (!rt.BenchWorld.Graph.Nodes.TryGetValue(a[3], out node))
                return "bench: no node '" + a[3] + "'.";
            if (string.Equals(node.OwnerFactionTag, atk.Tag, StringComparison.OrdinalIgnoreCase))
                return "bench: " + node.Id + " is " + atk.Tag + "'s own node.";

            double awayKm = node.Position.DistanceTo(playerKm);
            DateTime now = rt.ActiveUtc;
            BenchFlags flags = Flags(options);
            PendingFleetAction raid = BenchScenario.Attacker(label, "raid", atk.Tag, node.OwnerFactionTag, node.Id,
                node.Position, prefabs, rt.BenchCatalogue, now, dwell, flags);
            var pairs = new List<KeyValuePair<string, string>>();
            if (node.OwnerFactionTag != null && rt.BenchState(node.OwnerFactionTag) != null)
                pairs.Add(new KeyValuePair<string, string>(atk.Tag, node.OwnerFactionTag));

            string until = raid.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string staged = "BENCH_STAGED " + label + " raid " + raid.LogId + " " + atk.Tag
                            + " hulls=" + raid.HullCount + " dps=" + Dps(raid.WeaponBands).ToString("0", CultureInfo.InvariantCulture)
                            + " vs " + (node.OwnerFactionTag ?? "unowned") + " @" + node.Id
                            + " player_km=" + awayKm.ToString("0.0", CultureInfo.InvariantCulture)
                            + " dwell=" + dwell.ToString("0", CultureInfo.InvariantCulture) + "s until=" + until
                            + " flags=" + flags;
            string stance = rt.StageBench(label, new List<PendingFleetAction> { raid }, pairs, staged);
            return "bench: raid '" + label + "' staged: " + raid.LogId + " " + atk.Tag + " x" + raid.HullCount + " on " + node.Id
                   + " (" + (node.OwnerFactionTag ?? "unowned") + ", " + awayKm.ToString("0.0", CultureInfo.InvariantCulture) + " km from the player"
                   + (awayKm > tuning.ObservationRadiusKm ? " -- outside the " + tuning.ObservationRadiusKm.ToString("0", CultureInfo.InvariantCulture) + " km observation radius, so it will not materialise until you close in" : "")
                   + "). SE stance " + (stance.Length > 0 ? stance : "n/a")
                   + ". The node's station and any Guard there meet it at " + until + " UTC; BENCH_RESOLVED " + label + " writes it up."
                   + " The node itself is not changed; real defenders fight it for real.";
        }

        /// <summary><c>bench duel</c>: the abstract meeting duel run n times on a seeded stream,
        /// with the range contested as every real meeting's is. <c>atkArm= defArm=</c>
        /// (<see cref="TacticArm"/>) and <c>atkBreak= defBreak=</c> (<see cref="BreakOffLevel"/>) set
        /// the stances (the default personality's where one is not given); <c>contested=0</c> fights
        /// the fixed bracket order instead, for comparison. Needs a loaded session for the prefab
        /// catalogue and tuning, and nothing else.</summary>
        private static string Duel(string[] t)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> a = Args(t, options);
            if (a.Count < 2) return Usage;
            FactionRuntime rt = Runtime();
            if (rt == null || rt.BenchCatalogue == null) return "bench: no session loaded (the duel reads the prefab catalogue).";
            List<string> atk, def;
            string err;
            if ((err = ParsePrefabs(rt, a[0], out atk)) != null) return err;
            if ((err = ParsePrefabs(rt, a[1], out def)) != null) return err;
            double runs = 100, seed = 1, flat = 0;
            if (a.Count > 2 && (!TryDouble(a[2], out runs) || runs < 1 || runs > 10000)) return "bench: n must be 1..10000.";
            if (a.Count > 3 && !TryDouble(a[3], out seed)) return Usage;
            string v;
            if (options.TryGetValue("flat", out v) && (!TryDouble(v, out flat) || flat < 0)) return "bench: flat must be >= 0.";
            TacticArm? atkArm, defArm;
            BreakOffLevel? atkBreak, defBreak;
            if ((err = ParseOption(options, "atkarm", out atkArm)) != null
                || (err = ParseOption(options, "defarm", out defArm)) != null
                || (err = ParseOption(options, "atkbreak", out atkBreak)) != null
                || (err = ParseOption(options, "defbreak", out defBreak)) != null) return err;
            bool contested = !(options.TryGetValue("contested", out v) && v == "0");
            if (!contested && (atkArm.HasValue || defArm.HasValue || atkBreak.HasValue || defBreak.HasValue))
                return "bench: contested=0 fights the fixed bracket order, which takes no arm or break-off.";
            BenchDuelReport r = contested
                ? BenchScenario.Duel(rt.BenchCatalogue, atk, def, (long)flat, rt.BenchTuning, (int)runs, (int)seed,
                    BenchScenario.Stance(rt.BenchCatalogue, atk, atkArm, atkBreak),
                    BenchScenario.Stance(rt.BenchCatalogue, def, defArm, defBreak))
                : BenchScenario.Duel(rt.BenchCatalogue, atk, def, (long)flat, rt.BenchTuning, (int)runs, (int)seed);
            return r.Format();
        }

        /// <summary>An enum-valued key=value option, by name, case-insensitive; null when absent.</summary>
        private static string ParseOption<T>(Dictionary<string, string> options, string key, out T? value) where T : struct
        {
            value = null;
            string v;
            if (!options.TryGetValue(key, out v)) return null;
            T parsed;
            if (!Enum.TryParse(v, true, out parsed) || !Enum.IsDefined(typeof(T), parsed))
                return "bench: " + key + " must be one of " + string.Join(", ", Enum.GetNames(typeof(T))) + ".";
            value = parsed;
            return null;
        }

        private static string Leave(string label)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            return rt.BenchLeave(label) is string s ? "bench: " + label + ": " + s : "bench: no scenario '" + label + "'.";
        }

        private static string Return(string label)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            return rt.BenchReturn(label) is string s ? "bench: " + label + ": " + s : "bench: nothing of '" + label + "' is held out.";
        }

        /// <summary><c>bench damage</c>: every hull of the scenario at the given integrity, its grids
        /// damaged to match when they are in the world.</summary>
        private static string Damage(string label, string fraction)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            double integrity;
            if (!TryDouble(fraction, out integrity) || integrity < 0 || integrity > 1)
                return "bench: integrity must be 0..1.";
            return rt.BenchDamage(label, integrity) is string s ? "bench: " + label + ": " + s : "bench: no scenario '" + label + "'.";
        }

        /// <summary><c>bench tactics</c>: a faction's learned tactic memory, per opponent and context.</summary>
        private static string Tactics(string tag, string opponent)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            return rt.BenchTactics(tag, opponent) ?? "bench: no faction '" + tag + "'.";
        }

        /// <summary><c>bench arm</c>: the arm a scenario fleet's lead fights with, set through the
        /// player-override rung of the tactic resolver; <c>clear</c> hands it back to its own pick.
        /// Break-off defaults to Mid.</summary>
        private static string Arm(string label, string side, string armText, string breakText)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            TacticArm? arm = null;
            if (!string.Equals(armText, "clear", StringComparison.OrdinalIgnoreCase))
            {
                TacticArm parsed;
                if (!Enum.TryParse(armText, true, out parsed) || !Enum.IsDefined(typeof(TacticArm), parsed))
                    return "bench: arm must be one of " + string.Join(", ", Enum.GetNames(typeof(TacticArm))) + ", or clear.";
                arm = parsed;
            }
            BreakOffLevel breakOff = BreakOffLevel.Mid;
            if (breakText != null
                && (!Enum.TryParse(breakText, true, out breakOff) || !Enum.IsDefined(typeof(BreakOffLevel), breakOff)))
                return "bench: break-off must be one of " + string.Join(", ", Enum.GetNames(typeof(BreakOffLevel))) + ".";
            return rt.BenchArm(label, side, arm, breakOff) is string s
                ? "bench: " + label + ": " + s
                : "bench: scenario '" + label + "' has no '" + side + "' fleet.";
        }

        /// <summary>
        /// <c>bench wing &lt;label&gt; &lt;command&gt; [steamId]</c>: a wing command issued as if the player
        /// (the given one, else the first online) had pressed it on an antenna of the ship they sit in —
        /// the same validation, target read and <c>WING_ORDER</c>/<c>WING_REFUSED</c> line as a toolbar
        /// press, with only that scenario's fleets in the wing. <c>cut</c> reads the order's link as
        /// lost, as an antenna switched off would; <c>status</c> shows each order and hull link.
        /// </summary>
        private static string Wing(string[] t)
        {
            if (t.Length < 5) return Usage;
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            string label = t[3];
            string word = t[4].ToLowerInvariant();
            if (word == "cut")
                return rt.BenchWingCut(label) is string cut ? "bench: " + label + ": " + cut : "bench: no wing order reaches '" + label + "'.";
            if (word == "status")
                return rt.BenchWingStatus(label) ?? "bench: no wing order reaches '" + label + "'.";
            WingCommand command;
            if (!WingRules.TryParse(word, out command)) return Usage;
            ulong steamId = 0;
            if (t.Length > 5 && !ulong.TryParse(t[5], NumberStyles.None, CultureInfo.InvariantCulture, out steamId)) return Usage;

            long identity, antenna;
            string err = WingWorld.SeatedAntenna(steamId, out identity, out antenna);
            if (err != null) return "bench: " + err;
            bool ok;
            string message = rt.IssueWing(new WingRequest
            {
                Via = WingVia.Bench,
                Command = command,
                AntennaEntityId = antenna,
                CommanderIdentityId = identity,
                PresserLock = WingWorld.PresserLock(identity),
                BenchLabel = label,
            }, out ok);
            return "bench: " + label + ": " + message + " (antenna " + antenna + ")";
        }

        /// <summary><c>bench protect &lt;label|nodeId&gt; [npc|status|antenna on|off]</c>: a station's
        /// call for protection, fired on the materialised station of the scenario's target node (or of
        /// the node named) as if <i>protect me</i> were pressed on its antenna; <c>npc</c> fires it as
        /// the host raises an NPC station's call;
        /// <c>antenna off</c> switches the station's antennas off for the negative check.</summary>
        private static string Protect(string[] t)
        {
            if (t.Length < 4) return Usage;
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            string word = t.Length > 4 ? t[4].ToLowerInvariant() : null;
            string arg = t.Length > 5 ? t[5].ToLowerInvariant() : null;
            if (word != null && word != "npc" && word != "status" && word != "antenna") return Usage;
            return rt.BenchProtect(t[3], word, arg);
        }

        private static string Status(string label)
        {
            FactionRuntime rt = Runtime();
            if (rt == null) return "bench: no session loaded.";
            return rt.BenchStatus(label) ?? "bench: no scenario '" + label + "'.";
        }

        /// <summary>The first online player's position and facing: forward is where the character
        /// looks, right the direction the two sides of a fight are spread along.</summary>
        private static bool TryFirstPlayerFrame(out Vector3D pos, out Vector3D forward, out Vector3D right)
        {
            pos = forward = right = Vector3D.Zero;
            if (MySession.Static == null || MySession.Static.Players == null) return false;
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
            {
                if (p == null || p.IsBot || p.Character == null) continue;
                MatrixD m = p.Character.WorldMatrix;
                pos = m.Translation;
                forward = Vector3D.Normalize(m.Forward);
                right = Vector3D.Normalize(m.Right);
                return true;
            }
            return false;
        }

        private static bool TryFirstPlayerPosition(out Vector3D pos)
        {
            pos = Vector3D.Zero;
            if (MySession.Static == null || MySession.Static.Players == null) return false;
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
            {
                if (p == null || p.IsBot || p.Character == null) continue;
                pos = p.Character.PositionComp.GetPosition();
                return true;
            }
            return false;
        }
    }
}
