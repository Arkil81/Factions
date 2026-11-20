using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>factions bench phantom</c>: stand-in players, so per-player load can be measured at n
    /// players on a server that has one or none.
    ///
    /// <para>A phantom is a non-NPC identity <c>phantom-&lt;label&gt;</c>, an optional hull it owns,
    /// and an SE character it owns, floating beside the hull when there is one. Its position is
    /// appended to the plugin's per-frame player cache, so every observation scan, the census and
    /// the presence plan count it as a player; the census charges its hull to it as a party of its
    /// own. It is left out of every delivery list (<see cref="FactionsPlugin"/>'s
    /// <c>PlayersWithinKm</c>): Torch resolves an unknown steam id to identity 0, which is a chat
    /// broadcast to everyone.</para>
    ///
    /// <para>What a phantom does not reproduce: a real client's network cost — replication, voxel
    /// streaming, the uplink — because it has no connection.</para>
    ///
    /// <para><b>Park</b> holds the phantom at a standoff from a node, putting it back when it
    /// wanders and otherwise letting it sleep. <b>Patrol</b> flies a seeded loop of nodes
    /// (<see cref="PhantomPatrol"/>). Nothing is saved; an identity a save kept anyway is removed at
    /// the next boot by <see cref="SweepStaleIdentities"/>.</para>
    /// </summary>
    internal static class BenchPhantoms
    {
        private sealed class Phantom
        {
            public string Label;
            public string Group;
            public string Prefab;
            public string Where;
            public long IdentityId;
            public ulong SteamId;
            public bool WantsCharacter;
            public MyCharacter Character;
            public int CharacterSpawns;
            public Vector3D CharacterOffsetM;
            public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public bool HullPending;
            public Vec3 AnchorKm;
            public PhantomPatrol Patrol;
            public int Teleports;
        }

        private static readonly List<Phantom> Phantoms = new List<Phantom>();
        private static int _frame;
        private static ulong _serial;

        private const double CharacterHoldKm = 0.1;
        private const int MaxHops = 16;
        private const double MaxStandoffKm = 10;

        /// <summary>The radius of a <c>near=</c> patrol's area when <c>within=</c> is not given.</summary>
        private const double DefaultWithinKm = 100;

        internal const string Usage =
            "bench phantom: usage 'factions bench phantom add <label> park <nodeId|here|spread> [n=N] [hull=<prefabId>] [character=0] [standoff=km] [seed=S]'"
            + " | 'factions bench phantom add <label> patrol <speedMs> [n=N] [hops=K] [near=<nodeId> [within=km]] [hull=<prefabId>] [character=0] [standoff=km] [seed=S]'"
            + " | 'factions bench phantom clear [label]' | 'factions bench phantom list'";

        internal static int Count { get { return Phantoms.Count; } }

        /// <summary>The <paramref name="index"/>th phantom as the player cache wants it: where it is
        /// (its hull, else its character, else the bare presence point), how fast it moves, and who
        /// it is.</summary>
        internal static void Presence(int index, out Vector3D posM, out Vector3 velMs, out long identityId, out ulong steamId)
        {
            Phantom p = Phantoms[index];
            identityId = p.IdentityId;
            steamId = p.SteamId;
            MyCubeGrid hull = MainGrid(p);
            if (hull != null)
            {
                posM = hull.PositionComp.GetPosition();
                velMs = hull.Physics != null ? hull.Physics.LinearVelocity : Vector3.Zero;
                return;
            }
            if (CharacterAlive(p))
            {
                posM = p.Character.PositionComp.GetPosition();
                velMs = p.Character.Physics != null ? p.Character.Physics.LinearVelocity : Vector3.Zero;
                return;
            }
            posM = M(p.AnchorKm);
            velMs = Vector3.Zero;
        }

        internal static string Handle(string[] t)
        {
            string sub = t.Length > 3 ? t[3].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "add": return Add(t);
                case "clear": return Clear(t.Length > 4 ? t[4] : null);
                case "list": return List();
                default: return Usage;
            }
        }

        // ---- add ---------------------------------------------------------------------------------

        private static string Add(string[] t)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var a = new List<string>();
            for (int i = 4; i < t.Length; i++)
            {
                int eq = t[i].IndexOf('=');
                if (eq > 0) options[t[i].Substring(0, eq).ToLowerInvariant()] = t[i].Substring(eq + 1);
                else a.Add(t[i]);
            }
            if (a.Count < 3) return Usage;

            FactionsPlugin plugin = FactionsPlugin.Instance;
            FactionRuntime rt = plugin != null ? plugin.Runtime : null;
            if (rt == null || rt.BenchWorld == null || MySession.Static == null) return "bench phantom: no session loaded.";

            string group = a[0];
            string mode = a[1].ToLowerInvariant();
            if (mode != "park" && mode != "patrol") return Usage;

            int n = 1, seed = 1, hops = 4;
            double standoff = PhantomPlan.DefaultStandoffKm, speed = 0;
            string v;
            if (options.TryGetValue("n", out v) && (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1))
                return "bench phantom: n must be a whole number >= 1.";
            if (options.TryGetValue("seed", out v) && !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                return "bench phantom: seed must be a whole number.";
            if (options.TryGetValue("hops", out v) && (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out hops) || hops < 2 || hops > MaxHops))
                return "bench phantom: hops must be 2.." + MaxHops + ".";
            if (options.TryGetValue("standoff", out v) && (!TryDouble(v, out standoff) || standoff < 0.5 || standoff > MaxStandoffKm))
                return "bench phantom: standoff must be 0.5.." + MaxStandoffKm + " km.";
            bool wantsCharacter = !(options.TryGetValue("character", out v) && v == "0");
            string prefab;
            options.TryGetValue("hull", out prefab);
            if (prefab != null && rt.Catalogue != null && !rt.Catalogue.Contains(prefab))
                return "bench phantom: prefab '" + prefab + "' is not in the prefab catalogue.";
            if (mode == "patrol" && (!TryDouble(a[2], out speed) || speed <= 0 || speed > 1000))
                return "bench phantom: patrol speed must be 0..1000 m/s.";

            if (Phantoms.Count + n > PhantomPlan.MaxPhantoms)
                return "bench phantom: at most " + PhantomPlan.MaxPhantoms + " phantoms; " + Phantoms.Count + " are out.";
            var labels = new List<string>();
            for (int i = 1; i <= n; i++) labels.Add(n == 1 ? group : group + "-" + i);
            foreach (string label in labels)
            {
                if (!BenchScenario.IsValidLabel(label))
                    return "bench phantom: label must be 1.." + BenchScenario.MaxLabelLength + " letters, digits, '-' or '_' (with n, including the '-N').";
                if (Find(label) != null) return "bench phantom: '" + label + "' is already out; clear it first.";
            }

            List<SpatialNode> candidates = PhantomPlan.Candidates(rt.BenchWorld.Graph.Nodes.Values);
            if (candidates.Count == 0) return "bench phantom: the world has no owned nodes to place a phantom at.";
            string near;
            if (mode == "patrol" && options.TryGetValue("near", out near))
            {
                SpatialNode anchor;
                string err = ResolveNode(rt, near, out anchor);
                if (err != null) return err;
                double within = DefaultWithinKm;
                if (options.TryGetValue("within", out v) && (!TryDouble(v, out within) || within <= 0))
                    return "bench phantom: within must be a distance in km above 0.";
                candidates = PhantomPlan.Within(candidates, anchor.Position, within);
                if (candidates.Count < 2)
                    return "bench phantom: " + candidates.Count + " owned node(s) within " + within.ToString("0", CultureInfo.InvariantCulture)
                           + " km of " + anchor.Id + "; a patrol needs two.";
            }
            var rng = new Random(seed);

            // Where each phantom starts, and for a patrol, its loop.
            var starts = new List<Vec3>();
            var wheres = new List<string>();
            var patrols = new List<PhantomPatrol>();
            if (mode == "park")
            {
                List<SpatialNode> nodes;
                string target = a[2];
                if (string.Equals(target, "spread", StringComparison.OrdinalIgnoreCase))
                    nodes = PhantomPlan.Spread(candidates, n, seed);
                else
                {
                    SpatialNode node;
                    string err = ResolveNode(rt, target, out node);
                    if (err != null) return err;
                    nodes = new List<SpatialNode>();
                    for (int i = 0; i < n; i++) nodes.Add(node);
                }
                foreach (SpatialNode node in nodes)
                {
                    starts.Add(PhantomPlan.Standoff(node.Position, PlanetCentreKm(node.Position), standoff, rng));
                    wheres.Add("park@" + node.Id);
                    patrols.Add(null);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    List<SpatialNode> route = PhantomPlan.Route(candidates, hops, rng);
                    var waypoints = new List<Vec3>(route.Count);
                    var ids = new List<string>(route.Count);
                    foreach (SpatialNode node in route)
                    {
                        waypoints.Add(PhantomPlan.Standoff(node.Position, PlanetCentreKm(node.Position), standoff, rng));
                        ids.Add(node.Id);
                    }
                    var patrol = new PhantomPatrol(waypoints, speed);
                    starts.Add(waypoints[0]);
                    wheres.Add("patrol " + speed.ToString("0", CultureInfo.InvariantCulture) + "m/s " + string.Join(">", ids));
                    patrols.Add(patrol);
                }
            }

            var sb = new StringBuilder();
            for (int i = 0; i < labels.Count; i++)
            {
                Phantom p = Spawn(labels[i], group, prefab, wantsCharacter, starts[i], wheres[i], patrols[i]);
                sb.Append(" ").Append(p.Label).Append(" ").Append(p.Where).Append(";");
            }
            FactionsPlugin.Log.Info("Bench: phantoms out " + labels.Count + " group=" + group
                                    + " hull=" + (prefab ?? "none") + " character=" + (wantsCharacter ? 1 : 0)
                                    + " seed=" + seed + ":" + sb);
            return "bench phantom: " + labels.Count + " out (hull=" + (prefab ?? "none") + ", character="
                   + (wantsCharacter ? "yes" : "no") + "):" + sb
                   + " They count as players for observation from the next frame.";
        }

        private static string ResolveNode(FactionRuntime rt, string target, out SpatialNode node)
        {
            node = null;
            if (!string.Equals(target, "here", StringComparison.OrdinalIgnoreCase))
                return rt.BenchWorld.Graph.Nodes.TryGetValue(target, out node) ? null : "bench phantom: no node '" + target + "'.";
            Vector3D me;
            if (!TryFirstRealPlayer(out me)) return "bench phantom: 'here' needs an online player; name a node or use 'spread'.";
            Vec3 meKm = Km(me);
            double best = double.MaxValue;
            foreach (SpatialNode n in rt.BenchWorld.Graph.Nodes.Values)
            {
                double d = n.Position.DistanceTo(meKm);
                if (d < best) { best = d; node = n; }
            }
            return node != null ? null : "bench phantom: the world has no nodes.";
        }

        private static Phantom Spawn(string label, string group, string prefab, bool wantsCharacter,
                                     Vec3 startKm, string where, PhantomPatrol patrol)
        {
            MyIdentity identity = MySession.Static.Players.CreateNewIdentity(PhantomPlan.IdentityPrefix + label);
            var p = new Phantom
            {
                Label = label,
                Group = group,
                Prefab = prefab,
                Where = where,
                IdentityId = identity.IdentityId,
                SteamId = PhantomPlan.SteamIdBase + (++_serial),
                WantsCharacter = wantsCharacter,
                AnchorKm = startKm,
                Patrol = patrol,
            };
            Phantoms.Add(p);

            Vector3D posM = M(startKm);
            if (prefab == null)
            {
                if (wantsCharacter) SpawnCharacter(p, posM);
                return p;
            }

            p.HullPending = true;
            Vector3 velocity = patrol != null ? (Vector3)(Vector3D.Normalize(M(patrol.WaypointsKm[1 % patrol.WaypointsKm.Count]) - posM) * patrol.SpeedMs) : Vector3.Zero;
            if (!velocity.IsValid()) velocity = Vector3.Zero;
            var callbacks = new Stack<Action>();
            callbacks.Push(() =>
            {
                p.HullPending = false;
                foreach (MyCubeGrid g in p.Grids)
                    if (g != null) g.DisplayName = "phantom:" + p.Label;
                MyCubeGrid hull = MainGrid(p);
                if (p.WantsCharacter && Find(p.Label) == p)
                    SpawnCharacter(p, hull != null ? hull.PositionComp.WorldAABB.Max + new Vector3D(10, 10, 10) : posM);
                FactionsPlugin.Log.Info("Bench: phantom '" + p.Label + "' hull " + prefab + ", " + p.Grids.Count + " grid(s), "
                                        + BlockCount(p) + " blocks, owner " + p.IdentityId + ".");
            });
            MyPrefabManager.Static.SpawnPrefab(
                p.Grids, prefab, MyEntities.FindFreePlaceCustom(posM, 100f) ?? posM, Vector3.Forward, Vector3.Up,
                initialLinearVelocity: velocity,
                spawningOptions: SpawningOptions.DisableSave | SpawningOptions.UseOnlyWorldMatrix
                                 | (patrol != null ? SpawningOptions.DisableDampeners : SpawningOptions.None),
                ownerId: p.IdentityId,
                updateSync: true,
                callbacks: callbacks);
            return p;
        }

        private static void SpawnCharacter(Phantom p, Vector3D posM)
        {
            MyCharacter c = MyCharacter.CreateCharacter(MatrixD.CreateWorld(posM, Vector3D.Forward, Vector3D.Up), Vector3.Zero,
                PhantomPlan.IdentityPrefix + p.Label, null, null, null,
                findNearPos: true, AIMode: false, cockpit: null, useInventory: true,
                identityId: p.IdentityId, addDefaultItems: false);
            if (c == null) return;
            c.Save = false;
            p.Character = c;
            p.CharacterSpawns++;
            MyCubeGrid hull = MainGrid(p);
            p.CharacterOffsetM = hull != null ? c.PositionComp.GetPosition() - hull.PositionComp.GetPosition() : Vector3D.Zero;
        }

        // ---- clear / list ------------------------------------------------------------------------

        private static string Clear(string label)
        {
            var drop = new List<Phantom>();
            foreach (Phantom p in Phantoms)
                if (label == null || string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Group, label, StringComparison.OrdinalIgnoreCase)) drop.Add(p);
            if (drop.Count == 0) return label == null ? "bench phantom: none out." : "bench phantom: nothing out under '" + label + "'.";
            int grids = 0;
            foreach (Phantom p in drop)
            {
                grids += Remove(p);
                Phantoms.Remove(p);
            }
            FactionsPlugin.Log.Info("Bench: phantoms cleared " + drop.Count + ", " + grids + " grid(s).");
            return "bench phantom: cleared " + drop.Count + " phantom(s), " + grids + " grid(s), and their identities.";
        }

        private static int Remove(Phantom p)
        {
            int closed = 0;
            foreach (MyCubeGrid g in p.Grids)
            {
                if (g == null || g.Closed || g.MarkedForClose) continue;
                g.Close();
                closed++;
            }
            if (p.Character != null && !p.Character.Closed) p.Character.Close();
            if (MySession.Static != null && MySession.Static.Players != null)
                MySession.Static.Players.RemoveIdentity(p.IdentityId);
            return closed;
        }

        private static string List()
        {
            if (Phantoms.Count == 0) return "bench phantom: none out.";
            var sb = new StringBuilder("bench phantom: " + Phantoms.Count + " out;");
            foreach (Phantom p in Phantoms)
            {
                Vector3D pos; Vector3 vel; long id; ulong steam;
                Presence(Phantoms.IndexOf(p), out pos, out vel, out id, out steam);
                int awake = 0, alive = 0;
                foreach (MyCubeGrid g in p.Grids)
                {
                    if (g == null || g.Closed) continue;
                    alive++;
                    if (g.Physics != null && g.Physics.IsActive) awake++;
                }
                sb.Append(" ").Append(p.Label).Append(" ").Append(p.Where)
                  .Append(" party=").Append(PhantomPlan.IdentityPrefix).Append(p.Label)
                  .Append(" hull=").Append(p.Prefab == null ? "none" : p.HullPending ? "spawning" : alive + "grid(s)/" + BlockCount(p) + "blocks/" + awake + "awake")
                  .Append(" character=").Append(!p.WantsCharacter ? "none" : CharacterAlive(p) ? "alive" : "down")
                  .Append(p.CharacterSpawns > 1 ? " respawns=" + (p.CharacterSpawns - 1) : "")
                  .Append(" speed=").Append(vel.Length().ToString("0", CultureInfo.InvariantCulture)).Append("m/s")
                  .Append(p.Patrol != null ? " leg=" + p.Patrol.Next + " laps=" + p.Patrol.Laps : "")
                  .Append(p.Teleports > 0 ? " teleports=" + p.Teleports : "")
                  .Append(" km=").Append(Km(pos)).Append(";");
            }
            return sb.ToString();
        }

        // ---- frame -------------------------------------------------------------------------------

        /// <summary>Called every frame; once a second, keeps each phantom's character up and moves
        /// its body: held in place for a park, steered along its loop for a patrol. Costs nothing
        /// when none is out.</summary>
        internal static void Tick()
        {
            if (Phantoms.Count == 0 || ++_frame % 60 != 0) return;
            foreach (Phantom p in Phantoms)
            {
                try { Step(p); }
                catch (Exception ex) { FactionsPlugin.Log.Warn("Bench: phantom '" + p.Label + "' step failed: " + ex.Message); }
            }
        }

        private static void Step(Phantom p)
        {
            if (p.HullPending) return;
            MyCubeGrid hull = MainGrid(p);

            if (p.WantsCharacter && !CharacterAlive(p))
            {
                if (p.Character != null && !p.Character.Closed) p.Character.Close();
                p.Character = null;
                SpawnCharacter(p, hull != null ? hull.PositionComp.GetPosition() + p.CharacterOffsetM : M(p.AnchorKm));
            }

            if (p.Patrol == null)
            {
                if (hull != null) HoldGrid(p, hull, p.AnchorKm);
                if (CharacterAlive(p))
                    HoldCharacter(p, hull != null ? Km(hull.PositionComp.GetPosition() + p.CharacterOffsetM) : p.AnchorKm);
                return;
            }

            if (hull != null)
            {
                PhantomPatrol.Step s = p.Patrol.Advance(Km(hull.PositionComp.GetPosition()));
                if (s.Teleport) { TeleportGrid(hull, s.TeleportToKm); p.Teleports++; }
                else
                {
                    var vel = new Vector3((float)s.VelocityMs.X, (float)s.VelocityMs.Y, (float)s.VelocityMs.Z);
                    foreach (MyCubeGrid g in p.Grids)
                    {
                        if (g == null || g.Closed || g.Physics == null || g.IsStatic) continue;
                        g.Physics.LinearVelocity = vel;
                        g.Physics.AngularVelocity = Vector3.Zero;
                    }
                }
                if (CharacterAlive(p)) Follow(p, hull);
                return;
            }

            if (CharacterAlive(p))
            {
                PhantomPatrol.Step s = p.Patrol.Advance(Km(p.Character.PositionComp.GetPosition()));
                if (s.Teleport) { TeleportCharacter(p.Character, M(s.TeleportToKm)); p.Teleports++; }
                else if (p.Character.Physics != null)
                    p.Character.Physics.LinearVelocity = new Vector3((float)s.VelocityMs.X, (float)s.VelocityMs.Y, (float)s.VelocityMs.Z);
                return;
            }

            p.AnchorKm = p.Patrol.AdvanceVirtual(p.AnchorKm, 1.0);
        }

        private static void HoldGrid(Phantom p, MyCubeGrid hull, Vec3 anchorKm)
        {
            if (hull.IsStatic || hull.Physics == null) return;
            bool teleport, stop;
            PhantomPlan.Hold(Km(hull.PositionComp.GetPosition()), anchorKm, hull.Physics.LinearVelocity.Length(), out teleport, out stop);
            if (teleport) { TeleportGrid(hull, anchorKm); p.Teleports++; }
            if (teleport || stop)
                foreach (MyCubeGrid g in p.Grids)
                {
                    if (g == null || g.Closed || g.Physics == null || g.IsStatic) continue;
                    g.Physics.LinearVelocity = Vector3.Zero;
                    g.Physics.AngularVelocity = Vector3.Zero;
                }
        }

        private static void HoldCharacter(Phantom p, Vec3 anchorKm)
        {
            MyCharacter c = p.Character;
            if (c.Physics == null) return;
            bool teleport, stop;
            PhantomPlan.Hold(Km(c.PositionComp.GetPosition()), anchorKm, c.Physics.LinearVelocity.Length(), out teleport, out stop);
            if (teleport) { TeleportCharacter(c, M(anchorKm)); p.Teleports++; }
            else if (stop) c.Physics.LinearVelocity = Vector3.Zero;
        }

        /// <summary>A patrolling hull's character keeps its place beside it: the hull's velocity,
        /// and put back when it has fallen behind.</summary>
        private static void Follow(Phantom p, MyCubeGrid hull)
        {
            MyCharacter c = p.Character;
            Vector3D want = hull.PositionComp.GetPosition() + p.CharacterOffsetM;
            if (Vector3D.Distance(c.PositionComp.GetPosition(), want) > CharacterHoldKm * 1000.0)
            {
                TeleportCharacter(c, want);
                p.Teleports++;
            }
            if (c.Physics != null && hull.Physics != null) c.Physics.LinearVelocity = hull.Physics.LinearVelocity;
        }

        private static void TeleportGrid(MyCubeGrid hull, Vec3 toKm)
        {
            Vector3D to = M(toKm);
            MatrixD m = hull.WorldMatrix;
            m.Translation = MyEntities.FindFreePlaceCustom(to, (float)hull.PositionComp.WorldVolume.Radius) ?? to;
            hull.Teleport(m);
        }

        private static void TeleportCharacter(MyCharacter c, Vector3D to)
        {
            MatrixD m = c.WorldMatrix;
            m.Translation = MyEntities.FindFreePlaceCustom(to, 2f) ?? to;
            c.Teleport(m);
            if (c.Physics != null) c.Physics.LinearVelocity = Vector3.Zero;
        }

        // ---- lifecycle ---------------------------------------------------------------------------

        /// <summary>Session unload: the world is going, and the entities with it. Forgets them.</summary>
        internal static void Unload()
        {
            Phantoms.Clear();
        }

        /// <summary>Session load: removes every <c>phantom-</c> identity with no player behind it. A
        /// save taken while phantoms were out keeps their identities, whatever the entities' save
        /// flags say.</summary>
        internal static void SweepStaleIdentities()
        {
            if (MySession.Static == null || MySession.Static.Players == null) return;
            var stale = new List<long>();
            foreach (MyIdentity identity in MySession.Static.Players.GetAllIdentities())
            {
                MyPlayer.PlayerId unused;
                if (identity != null && identity.DisplayName != null
                    && identity.DisplayName.StartsWith(PhantomPlan.IdentityPrefix, StringComparison.Ordinal)
                    && !MySession.Static.Players.TryGetPlayerId(identity.IdentityId, out unused))
                    stale.Add(identity.IdentityId);
            }
            foreach (long id in stale) MySession.Static.Players.RemoveIdentity(id);
            if (stale.Count > 0) FactionsPlugin.Log.Info("Bench: removed " + stale.Count + " phantom identities left in the save.");
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static Phantom Find(string label)
        {
            foreach (Phantom p in Phantoms)
                if (string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private static MyCubeGrid MainGrid(Phantom p)
        {
            MyCubeGrid main = null;
            foreach (MyCubeGrid g in p.Grids)
                if (g != null && !g.Closed && !g.MarkedForClose && (main == null || g.BlocksCount > main.BlocksCount)) main = g;
            return main;
        }

        private static bool CharacterAlive(Phantom p)
        {
            return p.Character != null && !p.Character.Closed && !p.Character.MarkedForClose && !p.Character.IsDead;
        }

        private static int BlockCount(Phantom p)
        {
            int n = 0;
            foreach (MyCubeGrid g in p.Grids) if (g != null && !g.Closed) n += g.BlocksCount;
            return n;
        }

        private static Vec3? PlanetCentreKm(Vec3 posKm)
        {
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(M(posKm));
            return planet != null ? Km(planet.PositionComp.GetPosition()) : (Vec3?)null;
        }

        private static bool TryFirstRealPlayer(out Vector3D pos)
        {
            pos = Vector3D.Zero;
            foreach (MyPlayer pl in MySession.Static.Players.GetOnlinePlayers())
            {
                if (pl == null || pl.IsBot || pl.Character == null) continue;
                pos = pl.Character.PositionComp.GetPosition();
                return true;
            }
            return false;
        }

        private static bool TryDouble(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static Vec3 Km(Vector3D m) { return new Vec3(m.X / 1000.0, m.Y / 1000.0, m.Z / 1000.0); }
        private static Vector3D M(Vec3 km) { return new Vector3D(km.X * 1000.0, km.Y * 1000.0, km.Z * 1000.0); }
    }
}
