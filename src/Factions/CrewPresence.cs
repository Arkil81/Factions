using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game.AI;
using Sandbox.Game.AI.Navigation;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.AI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.AI.Bot;
using VRageMath;
using VRage.ModAPI;

namespace Factions
{
    /// <summary>Civilian presence when a player enters a dynamic ship, plus explicit bench
    /// diagnostics. Native astronaut creation/navigation; no proximity activation. Civilians carry
    /// an unequipped combat kit and arm while an intruder (<see cref="CrewIntruders"/>) is aboard.</summary>
    internal static class CrewPresence
    {
        private sealed class Sample
        {
            public Vector3D Local;
            public bool Seated;
            public int Readings;
            public double FootTravel;
            public int Stuck;
            public int Outside;
            public double Health;
            public bool Helmet;
            public long CharacterId;
            public long SimulationCalls;
            public long SimulatedMoveCalls;
            public long SimulatedRotationCalls;
            public long ZeroedMoveCalls;
            public long PreservedMoveCalls;
            public MyAgentBot Bot;
            public MyCubeGrid Grid;
            public long CornerToleranceCalls;
            public readonly WaypointProgress Progress = new WaypointProgress();
            public bool WaypointKnown;
            public double WaypointDistance;
            public int ProgressStalls;
            public int Recoveries;
            public int RecoveryCooldown;
            public bool Civilian;
        }

        private static MyGridBotPopulator _populator;
        private static MySpawnGroupBotAnchor _anchor;
        private static readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();
        // Published snapshots: UpdateZeroMovement also runs in the character's parallel pass.
        private static volatile Dictionary<long, Sample> CharacterSamples = new Dictionary<long, Sample>();
        private static volatile bool _preserveMovement;
        private static volatile float _cornerRadius;
        private static bool _recoverProgress;
        private static bool _combatTrial;
        private static readonly FieldInfo PathSteeringField = typeof(MyBotNavigation)
            .GetField("m_pathSteering", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Queue<string> Ended = new Queue<string>();
        // Late native creation callbacks must remove crew whose trial ended while they loaded.
        // Such a hull must be respawned before another trial, so callbacks cannot join a new trial.
        private static readonly HashSet<long> Retired = new HashSet<long>();
        private static int _frame;
        private sealed class Population
        {
            public MySpawnGroupBotAnchor Anchor;
            public readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();
            public readonly CrewVisit Visit = new CrewVisit();
        }
        private static readonly Dictionary<long, Population> Populations = new Dictionary<long, Population>();
        private static readonly Dictionary<int, DateTime> Cancelled = new Dictionary<int, DateTime>();
        private static readonly Dictionary<long, string> Watched = new Dictionary<long, string>();
        private static readonly List<MyEntity> EntryCandidates = new List<MyEntity>();
        private static readonly HashSet<long> Occupied = new HashSet<long>();
        private static readonly FieldInfo PendingSpawns = typeof(MyGridBotPopulator)
            .GetField("m_pendingBotSpawns", BindingFlags.Instance | BindingFlags.NonPublic);
        private static string _lastVisit = "none";

        /// <summary>A Nexus agent's source for a hull's prefab, which it has no runtime to ask; set by
        /// the plugin there, null elsewhere.</summary>
        internal static Func<long, string> AgentPrefabOf;
        /// <summary>A Nexus agent's prefab catalogue, read from the authority's
        /// <c>prefab_catalogue.json</c> when the data directory has it; null otherwise, and a hull is
        /// crewed from its seats alone.</summary>
        internal static SePrefabCatalogue AgentCatalogue;
        private static readonly FieldInfo SimulatedEntities = typeof(MyParallelEntityUpdateOrchestrator)
            .GetField("m_entitiesForSimulate", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static string Handle(string[] t)
        {
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "status";
            var catalogue = FactionsPlugin.Instance != null && FactionsPlugin.Instance.Runtime != null
                ? FactionsPlugin.Instance.Runtime.BenchCatalogue as SePrefabCatalogue : null;
            if (sub == "catalogue")
            {
                if (catalogue == null) return "crew: no catalogue loaded.";
                var ids = new List<string>(catalogue.Ids);
                ids.Sort(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder("crew catalogue (seat evidence; walking remains to verify):");
                foreach (string id in ids)
                {
                    CrewLayout layout = catalogue.CrewLayout(id);
                    if (layout == null || layout.IsStation) continue;
                    sb.Append('\n').Append(id).Append(' ').Append(Describe(layout));
                }
                return sb.ToString();
            }
            if (sub == "inspect" && t.Length == 4)
                return catalogue != null && catalogue.Contains(t[3])
                    ? "crew " + t[3] + ": " + Describe(catalogue.CrewLayout(t[3]))
                    : "crew: unknown prefab '" + t[3] + "'.";
            if (sub == "start" && t.Length == 5) return Start(t[3], t[4]);
            if (sub == "stop" && t.Length == 3)
            {
                if (_anchor == null) return "crew: no trial active.";
                long gridId = _anchor.AnchorEntityId;
                Stop();
                return "crew: stopped trial on grid=" + gridId + "; native crew removal requested.";
            }
            if (sub == "status" && t.Length <= 3) return Status();
            if (sub == "visits" && t.Length == 3) return VisitStatus();
            if (sub == "order" && t.Length == 5) return AdminOrder(t[3], t[4]);
            if (sub == "watch" && t.Length == 5)
            {
                MyCubeGrid watched = BenchSpawner.CrewGrid(t[3]);
                MyFaction owner = MySession.Static.Factions.TryGetFactionByTag(t[4]);
                if (watched == null || owner == null) return "crew: unknown bench hull or faction.";
                if (_anchor != null && _anchor.Grid == watched) return "crew: stop the explicit trial before entry testing.";
                Watched[watched.EntityId] = owner.Tag;
                return "crew: entry watch grid=" + watched.EntityId + "; remains empty until a real player enters.";
            }
            if (sub == "combat" && t.Length == 3) return CrewCombat.Status();
            if (sub == "intruders" && t.Length == 3) return CrewIntruders.Status();
            if (sub == "combat" && t.Length == 4 && (t[3] == "on" || t[3] == "off"))
            {
                if (_anchor == null) return "crew: start a selected bench trial first.";
                if (t[3] == "on" && !CrewCombat.Bound) return "crew: combat tree control unavailable.";
                _combatTrial = t[3] == "on";
                if (!_combatTrial) CrewCombat.Close();
                else
                {
                    _preserveMovement = true;
                    _cornerRadius = 0.5f;
                    _recoverProgress = true;
                    foreach (Sample sample in Samples.Values) CrewCombat.Add(sample.Bot, _anchor.Grid, false);
                }
                return "crew: selected trial combat=" + (_combatTrial ? "on" : "off")
                    + "; nearest real player aboard is the target; civilian visits remain passive.\n" + CrewCombat.Status();
            }
            if (sub == "repair" && t.Length == 3) return RepairRegistration();
            if (sub == "movement" && t.Length == 4 && (t[3] == "on" || t[3] == "off"))
            {
                if (_anchor == null) return "crew: no trial active.";
                _preserveMovement = t[3] == "on";
                return "crew: native movement input preservation=" + (_preserveMovement ? "on" : "off") + "; trial characters only.";
            }
            if (sub == "recovery" && t.Length == 4 && (t[3] == "on" || t[3] == "off"))
            {
                if (_anchor == null) return "crew: no trial active.";
                if (PathSteeringField == null) return "crew: current waypoint unavailable; recovery remains unchanged.";
                _recoverProgress = t[3] == "on";
                foreach (var sample in Samples.Values) { sample.Progress.Reset(); sample.RecoveryCooldown = 0; }
                return "crew: waypoint progress recovery=" + (_recoverProgress ? "on" : "off")
                    + "; fail a goal after 8 s without 0.25 m improvement; trial characters only.";
            }
            if (sub == "corner" && t.Length == 4)
            {
                if (_anchor == null) return "crew: no trial active.";
                float radius = 0;
                if (t[3] != "off" && (!float.TryParse(t[3], NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                    || float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0.25f || radius > 0.8f))
                    return "crew: corner tolerance must be off or 0.25..0.8 metres.";
                if (!CrewCornerTolerance.Bound) return "crew: corner tolerance patch is unavailable.";
                _cornerRadius = radius;
                return "crew: intermediate waypoint tolerance=" + (radius > 0 ? Number(radius) + " m" : "native")
                    + "; trial characters only; final arrival distances unchanged.";
            }
            return "Usage: factions crew catalogue | inspect <prefabId> | start <benchLabel> <factionTag> | status | movement on|off | corner off|<metres> | recovery on|off | combat [on|off] | intruders | visits | order <gridId|benchLabel> mount|dismount|ease|auto | watch <benchLabel> <factionTag> | repair | stop";
        }

        private static MyAgentDefinition[] UnarmedDefinitions()
        {
            var bots = new List<MyAgentDefinition>();
            foreach (string subtype in new[] { "Astronaut", "Female_Astronaut" })
            {
                MyBotDefinition definition;
                if (!MyDefinitionManager.Static.TryGetBotDefinition(new MyDefinitionId(typeof(MyObjectBuilder_HumanoidBot), subtype), out definition)) continue;
                var humanoid = definition as MyHumanoidBotDefinition;
                if (humanoid != null && humanoid.BotBehaviorTree.SubtypeName == "AstronautBehavior"
                    && !humanoid.StartingWeaponDefinitionId.HasValue) bots.Add(humanoid);
            }
            return bots.ToArray();
        }

        /// <summary>SE 1.211 (Warfare 3) anchor: no weapons container, so the populator's weapon roll is skipped and
        /// the bots stay unarmed; no subtype override, chat set, spawn blocks, seatless direction or health override.</summary>
        private static MySpawnGroupBotAnchor NewAnchor(MyCubeGrid grid, MyFaction faction, int count, MyAgentDefinition[] bots)
        {
            return new MySpawnGroupBotAnchor(grid, faction, count, 4000f, bots, null, null, null, null, null, null, null);
        }

        private static void EnsurePopulator()
        {
            if (_populator != null) return;
            // SE 1.211 gave the populator a squad threat scan. A zero scan radius keeps it off: these crews are
            // unarmed and CrewCombat owns their fighting, so vanilla's turret/threat bookkeeping has nothing to drive.
            _populator = new MyGridBotPopulator(30000, default(MySquadThreatState.Settings));
            _populator.Subscribe();
            MyAIComponent.Static.BotCreatedEvent += OnCreated;
        }

        private static void ForgetCharacters(Dictionary<int, Sample> samples)
        {
            var characters = new Dictionary<long, Sample>(CharacterSamples);
            foreach (Sample sample in samples.Values)
            {
                CrewCombat.Remove(sample.CharacterId);
                CrewOrderControl.Release(sample.CharacterId, false);
                characters.Remove(sample.CharacterId);
            }
            CharacterSamples = characters;
        }

        private static void Unregister(long id)
        {
            // Native UnregisterAnchor clears slots/bots but leaves queued creation metadata.
            // Cancel those serials too, so a late old callback cannot join a later visit.
            var pending = PendingSpawns != null ? PendingSpawns.GetValue(_populator) as IDictionary : null;
            var remove = new List<int>();
            if (pending != null)
                foreach (DictionaryEntry entry in pending)
                {
                    FieldInfo anchorId = entry.Value.GetType().GetField("AnchorEntityId");
                    if (anchorId != null && (long)anchorId.GetValue(entry.Value) == id) remove.Add((int)entry.Key);
                }
            foreach (int serial in remove) Cancelled[serial] = DateTime.UtcNow.AddMinutes(5);
            _populator.UnregisterAnchor(id);
            foreach (int serial in remove) pending.Remove(serial);
        }

        private static void StopPopulation(long id)
        {
            Population population;
            if (!Populations.TryGetValue(id, out population)) return;
            Populations.Remove(id);
            PublishHulls();
            population.Anchor.Grid.OnClose -= OnGridClose;
            Unregister(id);
            ForgetCharacters(population.Samples);
            _lastVisit = "removed grid=" + id + " created=" + population.Samples.Count;
        }

        internal static bool HasPlayerAboard(MyCubeGrid grid)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return true;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (Entered(player, grid)) return true;
                if (player.IsBot || player.Id.SerialId != 0 || player.Character == null) continue;
                BoundingBoxD box = grid.PositionComp.WorldAABB;
                box.Inflate(3);
                if (box.Contains(player.Character.PositionComp.GetPosition()) == ContainmentType.Contains) return true;
            }
            return false;
        }

        /// <summary>A live, dynamic hull with a physics body; never a projector's preview grid (see
        /// <see cref="ShipCrew.MayCrewHull"/>).</summary>
        internal static bool MayCrew(MyCubeGrid grid)
        {
            return grid != null && !grid.Closed && !grid.MarkedForClose
                && ShipCrew.MayCrewHull(grid.IsStatic, grid.IsPreview, grid.Physics != null);
        }

        private static bool Entered(MyPlayer player, MyCubeGrid grid)
        {
            if (player == null || player.IsBot || player.Id.SerialId != 0 || player.Character == null
                || player.Character.IsDead || grid == null || grid.Closed || grid.MarkedForClose) return false;
            return IsEntered(player.Character, grid);
        }

        internal static bool IsEntered(MyCharacter character, MyCubeGrid grid)
        {
            if (character == null || character.IsDead || grid == null || grid.Closed || grid.MarkedForClose) return false;
            var seat = character.Parent as MyCockpit;
            if (seat != null && seat.CubeGrid == grid) return true;
            // World AABBs widen dramatically while turning; transform into the hull's own box.
            // Body centre excludes somebody standing on the outer roof or brushing its skin.
            Vector3D local = Vector3D.Transform(character.PositionComp.WorldAABB.Center,
                grid.PositionComp.WorldMatrixNormalizedInv);
            BoundingBoxD box = grid.PositionComp.LocalAABB;
            box.Inflate(-0.2);
            return box.Contains(local) == ContainmentType.Contains;
        }

        private static void TickVisits()
        {
            if (MyAIComponent.Static.Bots == null || MyAIComponent.Static.Pathfinding == null
                || MySession.Static.Players == null || PendingSpawns == null) return;
            Occupied.Clear();
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.IsBot || player.Id.SerialId != 0 || player.Character == null || player.Character.IsDead) continue;
                EntryCandidates.Clear();
                BoundingBoxD box = player.Character.PositionComp.WorldAABB;
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, EntryCandidates);
                var seat = player.Character.Parent as MyCockpit;
                if (seat != null) EntryCandidates.Add(seat.CubeGrid);
                foreach (MyEntity entity in EntryCandidates)
                {
                    MyCubeGrid grid = entity as MyCubeGrid;
                    if (!MayCrew(grid) || (_anchor != null && _anchor.Grid == grid)
                        || Occupied.Contains(grid.EntityId) || !Entered(player, grid)) continue;
                    // Solo-owned/unowned ships use neutral native astronauts; no faction is required.
                    Occupied.Add(grid.EntityId);
                    if (!Populations.ContainsKey(grid.EntityId)) StartPopulation(grid, HullFaction(grid));
                }
            }
            foreach (long id in new List<long>(Populations.Keys))
            {
                Population population = Populations[id];
                if (!MayCrew(population.Anchor.Grid)
                    || !population.Visit.Update(Occupied.Contains(id), CrewOrderControl.Held(population.Anchor.Grid, BotsOf(population)), 2))
                    StopPopulation(id);
            }
            foreach (int serial in new List<int>(Cancelled.Keys))
                if (Cancelled[serial] < DateTime.UtcNow) Cancelled.Remove(serial);
            foreach (long id in new List<long>(Watched.Keys))
                if (MyEntities.GetEntityById(id) == null) Watched.Remove(id);
        }

        /// <summary>The side a ship's crew belongs to: a Factions or watched hull's faction, else its
        /// majority owner's faction. Null for a solo-owned or unowned ship.</summary>
        internal static MyFaction HullFaction(MyCubeGrid grid)
        {
            SimGridRecord record;
            string tag;
            if (!Watched.TryGetValue(grid.EntityId, out tag))
                tag = SimGridMarks.TryGet(grid.EntityId, out record) ? record.FactionTag : null;
            MyFaction faction = tag != null ? MySession.Static.Factions.TryGetFactionByTag(tag) : null;
            if (faction == null && grid.BigOwners != null && grid.BigOwners.Count > 0)
                faction = MySession.Static.Factions.TryGetPlayerFaction(grid.BigOwners[0]) as MyFaction;
            return faction;
        }

        /// <summary>The hull a managed crew member belongs to. Safe off the game thread.</summary>
        internal static MyCubeGrid CrewHull(long characterId)
        {
            Sample sample;
            return CharacterSamples.TryGetValue(characterId, out sample) ? sample.Grid : null;
        }

        /// <summary>Arms or stands down a hull's crew; the number of crew it applied to.</summary>
        internal static int OnAlarm(MyCubeGrid grid, bool active)
        {
            Population population;
            if (!Populations.TryGetValue(grid.EntityId, out population)) return 0;
            foreach (Sample sample in population.Samples.Values)
            {
                if (sample.Bot == null) continue;
                if (active) CrewCombat.Add(sample.Bot, grid, true);
                else CrewCombat.Remove(sample.CharacterId);
            }
            return population.Samples.Count;
        }

        private static void StartPopulation(MyCubeGrid grid, MyFaction faction)
        {
            int emptySeats;
            CrewLayout layout = Survey(grid, out emptySeats);
            // The authored crew of a fleet hull's prefab overrides the seat count where it is known;
            // without it the seats alone decide, so a hull with no runtime behind it is still crewed.
            var runtime = FactionsPlugin.Instance != null ? FactionsPlugin.Instance.Runtime : null;
            SePrefabCatalogue catalogue = runtime != null ? runtime.BenchCatalogue as SePrefabCatalogue : AgentCatalogue;
            string prefab = runtime != null ? runtime.CrewPrefab(grid.EntityId)
                          : AgentPrefabOf != null ? AgentPrefabOf(grid.EntityId) : null;
            CrewLayout authored = prefab != null && catalogue != null ? catalogue.CrewLayout(prefab) : null;
            if (authored != null) layout.AuthoredCrew = authored.AuthoredCrew;
            int count = ShipCrew.TrialCount(layout, emptySeats);
            if (count == 0 || FactionsPlugin.Instance == null) return;
            MyAgentDefinition[] bots = UnarmedDefinitions();
            if (bots.Length == 0) return;
            EnsurePopulator();
            var population = new Population
            {
                Anchor = NewAnchor(grid, faction, count, bots)
            };
            population.Visit.Update(true, 0);
            Populations.Add(grid.EntityId, population);
            PublishHulls();
            grid.OnClose += OnGridClose;
            _populator.RegisterAnchor(population.Anchor);
            _populator.TickAnchor(population.Anchor);
            _lastVisit = "entered grid=" + grid.EntityId + " requested=" + count;
        }

        private static void TickOrders()
        {
            try
            {
                foreach (Population population in Populations.Values)
                    CrewOrderControl.Update(population.Anchor.Grid, BotsOf(population));
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Crew order update failed.");
                CrewOrderControl.Close();
            }
        }

        /// <summary>Every managed crewman and the hull he belongs to, entry crews and the bench trial
        /// alike; <paramref name="bots"/> and <paramref name="hulls"/> are filled pairwise.</summary>
        internal static void FillCrew(List<MyAgentBot> bots, List<MyCubeGrid> hulls)
        {
            foreach (Sample sample in CharacterSamples.Values)
            {
                if (sample.Bot == null || sample.Grid == null) continue;
                bots.Add(sample.Bot);
                hulls.Add(sample.Grid);
            }
        }

        /// <summary>A population's crew in serial order.</summary>
        private static List<MyAgentBot> BotsOf(Population population)
        {
            var serials = new List<int>(population.Samples.Keys);
            serials.Sort();
            var bots = new List<MyAgentBot>(serials.Count);
            foreach (int serial in serials) bots.Add(population.Samples[serial].Bot);
            return bots;
        }

        private static void PublishHulls()
        {
            var hulls = new List<MyCubeGrid>(Populations.Count);
            foreach (Population population in Populations.Values) hulls.Add(population.Anchor.Grid);
            CrewNavigationArea.Hulls = hulls.ToArray();
        }

        /// <summary>The number of crew created on a hull, zero when it has no population.</summary>
        internal static int CrewCount(long hullId)
        {
            Population population;
            return Populations.TryGetValue(hullId, out population) ? population.Samples.Count : 0;
        }

        /// <summary>The hull a player's crew order addresses: the crewed hull their seat is on or in
        /// the same construct as, the crewed hull they stand in, else the nearest crewed hull within
        /// vanilla's bot range (<see cref="CrewOrderControl.BotRangeM"/>); the hull of their seat when
        /// none is crewed.</summary>
        internal static MyCubeGrid OrderHull(MyCharacter character, MyCubeGrid seatGrid)
        {
            if (seatGrid != null)
            {
                if (Populations.ContainsKey(seatGrid.EntityId)) return seatGrid;
                foreach (Population population in Populations.Values)
                    if (((VRage.Game.ModAPI.IMyCubeGrid)seatGrid).IsSameConstructAs(population.Anchor.Grid)) return population.Anchor.Grid;
                return seatGrid;
            }
            if (character == null) return null;
            var seat = character.Parent as MyCockpit;
            if (seat != null) return OrderHull(character, seat.CubeGrid);
            MyCubeGrid nearest = null;
            double best = CrewOrderControl.BotRangeM * CrewOrderControl.BotRangeM;
            Vector3D at = character.PositionComp.GetPosition();
            foreach (Population population in Populations.Values)
            {
                MyCubeGrid grid = population.Anchor.Grid;
                if (IsEntered(character, grid)) return grid;
                double d = Vector3D.DistanceSquared(at, grid.PositionComp.WorldAABB.Center);
                if (d < best) { best = d; nearest = grid; }
            }
            return nearest;
        }

        private static string AdminOrder(string target, string word)
        {
            CrewOrder order;
            if (!CrewOrders.TryParse(word, out order)) return "crew: order must be mount, dismount, ease or auto.";
            long id;
            MyCubeGrid grid = long.TryParse(target, out id) ? MyEntities.GetEntityById(id) as MyCubeGrid : BenchSpawner.CrewGrid(target);
            if (grid == null) return "crew: no grid '" + target + "'.";
            bool ok;
            string reply = CrewOrderControl.Issue(grid, 0, true, order, CrewCount(grid.EntityId), out ok);
            return "crew: " + reply;
        }

        private static string VisitStatus()
        {
            var sb = new StringBuilder("crew visits: active=").Append(Populations.Count)
                .Append(" watched=").Append(Watched.Count).Append(" entry=oriented_hull_or_seat exit_grace_s=20")
                .Append(" pending_cancellation_bound=").Append(PendingSpawns != null)
                .Append(" last=").Append(_lastVisit);
            foreach (var entry in Populations)
            {
                Population population = entry.Value;
                sb.Append("\n grid=").Append(entry.Key).Append(" requested=").Append(population.Anchor.MaxBots)
                    .Append(" created=").Append(population.Samples.Count)
                    .Append(" occupied=").Append(Occupied.Contains(entry.Key))
                    .Append(" alarm=").Append(CrewIntruders.IsArmed(entry.Key) ? "armed" : "passive")
                    .Append(" intruders=").Append(CrewIntruders.Count(entry.Key))
                    .Append(" absent_s=").Append(Number(population.Visit.AbsentSeconds))
                    .Append(CrewOrderControl.Describe(population.Anchor.Grid));
                foreach (var actor in population.Samples)
                {
                    Sample sample = actor.Value;
                    MyCharacter character = sample.Bot != null ? sample.Bot.AgentEntity as MyCharacter : null;
                    sb.Append("\n serial=").Append(actor.Key).Append(" character=").Append(sample.CharacterId)
                        .Append(" alive=").Append(character != null && !character.IsDead)
                        .Append(" seated=").Append(sample.Seated).Append(" health=").Append(Number(sample.Health))
                        .Append(" suit_energy_pct=").Append(character != null ? Number(character.SuitEnergyLevel * 100f) : "unknown")
                        .Append(" suit_hydrogen_pct=").Append(SuitHydrogen(character))
                        .Append(" armed=").Append(character != null && character.CurrentWeapon != null)
                        .Append(" saved=").Append(character != null && character.Save)
                        .Append(" foot_travel_m=").Append(Number(sample.FootTravel))
                        .Append(" outside_samples=").Append(sample.Outside)
                        .Append(" goal_recoveries=").Append(sample.Recoveries)
                        .Append(CrewOrderControl.Describe(sample.CharacterId))
                        .Append(CrewFooting.Describe(sample.CharacterId));
                }
            }
            return sb.ToString();
        }

        private static bool? IsSimulated(MyCharacter character)
        {
            if (SimulatedEntities == null || !(MyEntities.Orchestrator is MyParallelEntityUpdateOrchestrator)) return null;
            var entities = SimulatedEntities.GetValue(MyEntities.Orchestrator) as List<MyEntity>;
            return entities != null ? (bool?)entities.Contains(character) : null;
        }

        internal static float CornerRadius(MyCharacter character)
        {
            if (character == null || character.IsDead || character.Parent != null)
                return 0;
            Sample sample;
            return CharacterSamples.TryGetValue(character.EntityId, out sample) ? (sample.Civilian ? 0.5f : _cornerRadius) : 0;
        }

        internal static void ObserveCornerTolerance(MyCharacter character)
        {
            Sample sample;
            if (character != null && CharacterSamples.TryGetValue(character.EntityId, out sample))
                Interlocked.Increment(ref sample.CornerToleranceCalls);
        }

        internal static void ObserveSimulation(MyCharacter character)
        {
            Sample sample;
            if (!CharacterSamples.TryGetValue(character.EntityId, out sample)) return;
            sample.SimulationCalls++;
            if (character.MoveIndicator.LengthSquared() > 0.0001f) sample.SimulatedMoveCalls++;
            if (character.RotationIndicator.LengthSquared() > 0.0001f || Math.Abs(character.RollIndicator) > 0.0001f)
                sample.SimulatedRotationCalls++;
        }

        internal static bool BeforeZeroMovement(MyCharacter character, bool commandWasCalled)
        {
            if (commandWasCalled || character.Parent != null || character.IsDead
                || !character.ControllerInfo.IsLocallyControlled())
                return true;
            bool hasMove = character.MoveIndicator.LengthSquared() > 0.0001f;
            bool hasRotation = character.RotationIndicator.LengthSquared() > 0.0001f || Math.Abs(character.RollIndicator) > 0.0001f;
            if (!hasMove && !hasRotation) return true;
            Sample sample;
            if (!CharacterSamples.TryGetValue(character.EntityId, out sample)) return true;
            bool combatAim = sample.Bot != null && CrewCombat.Controls(sample.Bot) && hasRotation;
            if ((!sample.Civilian && !_preserveMovement) || sample.Bot == null || (!sample.Bot.Navigation.Navigating && !combatAim)
                || (!hasRotation && sample.Bot.Navigation.WantedSpeed < 0.1f) || character.Physics == null || !character.Physics.Enabled)
            {
                Interlocked.Increment(ref sample.ZeroedMoveCalls);
                return true;
            }
            // Preserve only a current native movement command until character simulation consumes it.
            // Native zero-input commands, idle/finished navigation and every other character are unchanged.
            Interlocked.Increment(ref sample.PreservedMoveCalls);
            return false;
        }

        // FAC-440: an explicit causal control for a missing native simulation registration.
        // Never re-register a seated actor or one already in the native simulation list.
        private static string RepairRegistration()
        {
            if (_anchor == null) return "crew: no trial active.";
            int repaired = 0;
            foreach (int serial in Samples.Keys)
            {
                MyAgentBot bot = MyAIComponent.Static.Bots.TryGetBot<MyAgentBot>(serial);
                MyCharacter character = bot != null ? bot.AgentEntity as MyCharacter : null;
                if (character == null || character.IsDead || character.Parent != null || !character.InScene
                    || bot.AnchorEntityId != _anchor.AnchorEntityId || IsSimulated(character) != false) continue;
                MyEntities.UnregisterForUpdate(character, true);
                character.NeedsUpdate |= MyEntityUpdateEnum.SIMULATE;
                MyEntities.RegisterForUpdate(character);
                repaired++;
            }
            return "crew: missing simulation registrations requeued=" + repaired + "; inspect status after the next frame.";
        }

        private static string Describe(CrewLayout layout)
        {
            CrewEstimate e = ShipCrew.Estimate(layout);
            if (layout == null) return "class=Unknown (not surveyed)";
            return "class=" + e.Class + " desired=" + e.Desired + " slots=" + e.PresenceSlots
                + " pilots=" + layout.PilotStations + " crew_seats=" + layout.CrewSeats
                + " cryo=" + layout.CryoChambers + " spawn_points=" + layout.SpawnPoints
                + " source=" + (e.Authored ? "authored" : "seats")
                + (!layout.Known ? " (incomplete definition resolution)" : "");
        }

        private static CrewLayout Survey(MyCubeGrid grid, out int emptySeats)
        {
            var layout = new CrewLayout { Known = true, IsStation = grid.IsStatic };
            emptySeats = 0;
            foreach (MyCockpit seat in grid.GetFatBlocks<MyCockpit>())
            {
                if (seat is MyCryoChamber) { layout.CryoChambers++; continue; }
                if (seat.BlockDefinition.EnableShipControl) { layout.PilotStations++; continue; }
                layout.CrewSeats++;
                if (seat.IsFunctional && seat.Pilot == null
                    && (MyAiTargetManager.Static == null || !MyAiTargetManager.Static.IsEntityReserved(seat.EntityId)))
                    emptySeats++;
            }
            return layout;
        }

        private static string Start(string label, string tag)
        {
            if (_anchor != null) return "crew: stop the current trial first.";
            MyCubeGrid grid = BenchSpawner.CrewGrid(label);
            if (grid == null) return "crew: no live bench hull '" + label + "'; use factions bench spawn first.";
            if (Retired.Contains(grid.EntityId)) return "crew: respawn this bench hull before another trial (old bot callbacks may still be finishing).";
            if (!MayCrew(grid) || grid.GridSizeEnum != MyCubeSize.Large)
                return "crew: the first movement trial requires a dynamic large-grid hull with physics (not a projection).";
            MyFaction faction = MySession.Static.Factions.TryGetFactionByTag(tag);
            if (faction == null) return "crew: no faction '" + tag + "'.";
            if (!FactionsPlugin.IsEffectivelyNpc(faction)) return "crew: the trial requires an NPC faction.";
            if (MyAIComponent.Static == null || MyAIComponent.Static.Bots == null || MyAIComponent.Static.Pathfinding == null)
                return "crew: native AI/pathfinding is not available.";
            double observerDistance = double.MaxValue;
            foreach (MyPlayer observer in Sync.Players.GetOnlinePlayers())
                if (observer.Id.SerialId == 0 && observer.Controller.ControlledEntity != null)
                    observerDistance = Math.Min(observerDistance, Vector3D.Distance(observer.GetPosition(), grid.PositionComp.GetPosition()));
            // Environment-bot cleanup ignores humanoid anchor ownership and removes any bot
            // beyond 400 m from a real player's controlled entity every two minutes.
            if (observerDistance > 300)
                return "crew: stage the hull within 300 m of a real player's controlled entity (vanilla bot cleanup radius is 400 m); distance_m=" + Number(observerDistance);
            int emptySeats;
            CrewLayout layout = Survey(grid, out emptySeats);
            var catalogue = FactionsPlugin.Instance.Runtime.BenchCatalogue as SePrefabCatalogue;
            CrewLayout authored = catalogue != null ? catalogue.CrewLayout(BenchSpawner.CrewPrefab(label)) : null;
            if (authored != null) layout.AuthoredCrew = authored.AuthoredCrew;
            int count = ShipCrew.TrialCount(layout, emptySeats);
            if (count < 2) return "crew: the movement trial needs at least two empty, functional non-control seats; " + Describe(layout) + " empty=" + emptySeats;
            var bots = new List<MyAgentDefinition>();
            foreach (string subtype in new[] { "Astronaut", "Female_Astronaut" })
            {
                MyBotDefinition definition;
                if (!MyDefinitionManager.Static.TryGetBotDefinition(new MyDefinitionId(typeof(MyObjectBuilder_HumanoidBot), subtype), out definition)) continue;
                var humanoid = definition as MyHumanoidBotDefinition;
                if (humanoid != null && humanoid.BotBehaviorTree.SubtypeName == "AstronautBehavior"
                    && !humanoid.StartingWeaponDefinitionId.HasValue)
                    bots.Add(humanoid);
            }
            if (bots.Count == 0) return "crew: no unarmed native AstronautBehavior definitions resolved.";
            EnsurePopulator();
            // This named, unsaved bench hull is a controlled presence test. Native prefab
            // flight/security logic must not introduce thrust, rotation or shooting during it.
            int disabled = 0;
            foreach (MyCubeGrid trialGrid in BenchSpawner.CrewGrids(label))
            {
                if (trialGrid == null || trialGrid.Closed || trialGrid.MarkedForClose) continue;
                foreach (MyThrust thrust in trialGrid.GetFatBlocks<MyThrust>()) { thrust.Enabled = false; disabled++; }
                foreach (MyGyro gyro in trialGrid.GetFatBlocks<MyGyro>()) { gyro.Enabled = false; disabled++; }
                foreach (MyRemoteControl remote in trialGrid.GetFatBlocks<MyRemoteControl>()) remote.SetAutoPilotEnabled(false);
                foreach (MyProgrammableBlock program in trialGrid.GetFatBlocks<MyProgrammableBlock>()) { program.Enabled = false; disabled++; }
                foreach (MyUserControllableGun gun in trialGrid.GetFatBlocks<MyUserControllableGun>()) { gun.Enabled = false; disabled++; }
            }
            Samples.Clear();
            Ended.Clear();
            _preserveMovement = false;
            _cornerRadius = 0;
            _recoverProgress = false;
            _anchor = NewAnchor(grid, faction, count, bots.ToArray());
            grid.OnClose += OnGridClose;
            _populator.RegisterAnchor(_anchor);
            _populator.TickAnchor(_anchor);
            return "crew: unarmed trial started label=" + label + " grid=" + grid.EntityId
                + " faction=" + tag + " requested=" + count + " empty_seats=" + emptySeats
                + " disabled_flight_weapon_program_blocks=" + disabled
                + "; " + Describe(layout) + ". Keep a real player within 400 m; use status to inspect movement.";
        }

        private static void OnCreated(int serial, MyBotDefinition definition)
        {
            try { PrepareCreated(serial); }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Crew character preparation failed.");
                Remove(serial);
            }
        }

        private static void PrepareCreated(int serial)
        {
            MyAgentBot bot = MyAIComponent.Static.Bots.TryGetBot<MyAgentBot>(serial);
            if (bot == null) return;
            if (Cancelled.Remove(serial)) { Remove(serial); return; }
            Population population;
            Populations.TryGetValue(bot.AnchorEntityId, out population);
            if (population == null && Retired.Contains(bot.AnchorEntityId))
            {
                Remove(serial);
                return;
            }
            if (population == null && (_anchor == null || bot.AnchorEntityId != _anchor.AnchorEntityId)) return;
            MyPlayer player;
            if (!Sync.Players.TryGetPlayerById(new MyPlayer.PlayerId(Sync.MyId, serial), out player) || player.Character == null) return;
            MyCharacter character = player.Character;
            character.Save = false;
            MaintainSuitResources(character);
            character.SwitchToWeapon((MyDefinitionId?)null);
            // The populator adds faction loot after the bot's own starting inventory. Bench crew
            // carry nothing until combat supplies them; civilians carry an unequipped kit and take
            // normal damage, so shooting one is an act their hull's alarm can answer.
            character.GetInventory().Clear();
            if (population != null)
            {
                character.CharacterGeneralDamageModifier = 1f;
                CrewCombat.SupplyKit(character);
            }
            var sample = new Sample { CharacterId = character.EntityId, Bot = bot, Civilian = population != null,
                Grid = population != null ? population.Anchor.Grid : _anchor.Grid };
            (population != null ? population.Samples : Samples)[serial] = sample;
            var characters = new Dictionary<long, Sample>(CharacterSamples);
            characters[character.EntityId] = sample;
            CharacterSamples = characters;
            if (population == null && _combatTrial) CrewCombat.Add(bot, _anchor.Grid, false);
            else if (population != null && CrewIntruders.IsArmed(bot.AnchorEntityId)) CrewCombat.Add(bot, population.Anchor.Grid, true);
        }

        private static void OnGridClose(MyEntity entity)
        {
            if (_anchor != null && _anchor.AnchorEntityId == entity.EntityId) Stop();
            StopPopulation(entity.EntityId);
            Watched.Remove(entity.EntityId);
        }

        private static void Remove(int serial)
        {
            var id = new MyPlayer.PlayerId(Sync.MyId, serial);
            MyPlayer player;
            if (Sync.Players.TryGetPlayerById(id, out player) && player.Character != null)
            {
                var seat = player.Character.Parent as MyCockpit;
                if (seat != null) seat.RemovePilot();
            }
            MyAIComponent.Static.RemoveBot(id, true);
        }

        private static void Stop()
        {
            MySpawnGroupBotAnchor anchor = _anchor;
            if (anchor == null) return;
            _anchor = null;
            _combatTrial = false;
            CrewCombat.Close();
            long id = anchor.Grid.EntityId;
            Retired.Add(id);
            anchor.Grid.OnClose -= OnGridClose;
            _populator.ProcessPendingPlacements();
            Unregister(id);
            ForgetCharacters(Samples);
            Samples.Clear();
            _preserveMovement = false;
            _cornerRadius = 0;
            _recoverProgress = false;
        }

        internal static void Close()
        {
            Stop();
            CrewCombat.Close();
            CrewIntruders.Close();
            foreach (long id in new List<long>(Populations.Keys)) StopPopulation(id);
            Watched.Clear();
            Cancelled.Clear();
            CharacterSamples = new Dictionary<long, Sample>();
            if (MyAIComponent.Static != null) MyAIComponent.Static.BotCreatedEvent -= OnCreated;
            if (_populator != null) _populator.Unsubscribe();
            _populator = null;
            CrewOrderControl.Close();
            CrewFooting.Close();
            CrewNavigationArea.Hulls = new MyCubeGrid[0];
            Retired.Clear();
            _frame = 0;
        }

        internal static void Tick()
        {
            CrewIntruders.Tick();
            CrewCombat.Tick();
            CrewFooting.Tick();
            ++_frame;
            if (MySession.Static == null || MyAIComponent.Static == null) return;
            if (_frame % 30 == 0) TickOrders();
            if (_frame % 120 != 0) return;
            try
            {
                TickVisits();
                CrewOrderControl.Sweep();
                if (_populator == null) return;
                _populator.ProcessPendingPlacements();
                if (_anchor != null) { _populator.TickAnchor(_anchor); ReadMovement(); }
                foreach (Population population in Populations.Values)
                {
                    if (Occupied.Contains(population.Anchor.AnchorEntityId)) _populator.TickAnchor(population.Anchor);
                    ReadMovement(population.Anchor.Grid, population.Samples, null);
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Crew presence update failed.");
                Stop();
                foreach (long id in new List<long>(Populations.Keys)) StopPopulation(id);
            }
        }

        private static string SuitHydrogen(MyCharacter character)
        {
            return character != null && character.OxygenComponent != null
                ? Number(character.OxygenComponent.GetGasFillLevel(MyCharacterOxygenComponent.HydrogenId) * 100f)
                : "unknown";
        }

        private static void MaintainSuitResources(MyCharacter character)
        {
            // Crew suits have unlimited power/fuel. Keep normal character damage and native
            // jetpack/navigation behavior; only provision resources on our managed NPC actors.
            if (character.SuitBattery != null)
                character.SuitBattery.ResourceSource.SetRemainingCapacityByType(
                    MyResourceDistributorComponent.ElectricityId, 1e-5f); // Native full suit capacity in MWh.
            if (character.OxygenComponent != null)
            {
                var hydrogen = MyCharacterOxygenComponent.HydrogenId;
                character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, 1f);
            }
        }

        private static void ReadMovement()
        {
            if (_anchor == null) return;
            ReadMovement(_anchor.Grid, Samples, Ended);
        }

        private static void ReadMovement(MyCubeGrid grid, Dictionary<int, Sample> samples, Queue<string> ended)
        {
            var gone = new List<int>();
            foreach (KeyValuePair<int, Sample> entry in samples)
            {
                MyAgentBot bot = MyAIComponent.Static.Bots.TryGetBot<MyAgentBot>(entry.Key);
                MyCharacter character = bot != null ? bot.AgentEntity as MyCharacter : null;
                if (character == null || character.IsDead)
                {
                    if (ended != null)
                    {
                        if (ended.Count == 8) ended.Dequeue();
                        ended.Enqueue(" serial=" + entry.Key + " ended=" + (character == null ? "agent_missing" : "dead")
                        + " last_health=" + Number(entry.Value.Health) + " last_helmet=" + entry.Value.Helmet
                        + " samples=" + entry.Value.Readings + " foot_travel_m=" + Number(entry.Value.FootTravel));
                    }
                    gone.Add(entry.Key);
                    continue;
                }
                MaintainSuitResources(character);
                Sample s = entry.Value;
                s.Health = character.StatComp != null && character.StatComp.Health != null ? character.StatComp.Health.Value : -1;
                s.Helmet = character.OxygenComponent != null && character.OxygenComponent.HelmetEnabled;
                Vector3D world = character.PositionComp.GetPosition();
                Vector3D local = Vector3D.Transform(world, grid.PositionComp.WorldMatrixNormalizedInv);
                bool seated = character.Parent is MyCockpit;
                double delta = s.Readings > 0 ? Vector3D.Distance(local, s.Local) : 0;
                // Seat attachment/detachment and position snaps are not evidence of walking.
                if (s.Readings > 0 && !seated && !s.Seated && delta > 0.05 && delta < 8)
                    s.FootTravel += delta;
                if (!seated && bot.Navigation.Stuck) s.Stuck++;
                ReadWaypointProgress(s, bot, character, grid, seated);
                BoundingBoxD box = grid.PositionComp.WorldAABB;
                box.Inflate(3);
                if (!box.Contains(world).Equals(ContainmentType.Contains)) s.Outside++;
                s.Local = local;
                s.Seated = seated;
                s.Readings++;
            }
            foreach (int serial in gone)
            {
                var characters = new Dictionary<long, Sample>(CharacterSamples);
                CrewCombat.Remove(samples[serial].CharacterId);
                characters.Remove(samples[serial].CharacterId);
                CharacterSamples = characters;
                samples.Remove(serial);
            }
        }

        private static void ReadWaypointProgress(Sample sample, MyAgentBot bot, MyCharacter character, MyCubeGrid grid, bool seated)
        {
            var steering = PathSteeringField != null ? PathSteeringField.GetValue(bot.Navigation) as MyPathSteering : null;
            Vector3D? waypoint = steering != null ? steering.TargetWorld : null;
            sample.WaypointKnown = waypoint.HasValue;
            if (waypoint.HasValue)
            {
                Vector3D measuredTarget = waypoint.Value;
                sample.WaypointDistance = Math.Sqrt(steering.TargetDistanceSq(ref measuredTarget));
            }
            if (sample.RecoveryCooldown > 0) sample.RecoveryCooldown--;
            bool active = !seated && !CrewCombat.IsRecovering(character) && bot.Navigation.Navigating && waypoint.HasValue
                && !bot.Navigation.IsWaitingForTileGeneration && bot.Navigation.WaitForClearPathCountdown <= 0
                && !steering.TargetReached() && character.Physics != null && character.Physics.Enabled;
            if (!active || sample.RecoveryCooldown > 0)
            {
                sample.Progress.Reset();
                return;
            }
            Vector3D target = waypoint.Value;
            Vector3D localTarget = Vector3D.Transform(target, grid.PositionComp.WorldMatrixNormalizedInv);
            if (!sample.Progress.Observe(true, localTarget.X, localTarget.Y, localTarget.Z, sample.WaypointDistance, 2)) return;
            sample.ProgressStalls++;
            if ((!sample.Civilian && !_recoverProgress) || bot.AgentActions == null || bot.AgentActions.AiTargetBase == null
                || CrewOrderControl.Drives(character)) return;
            // Native failure unsets and temporarily blacklists the goal so the behavior can choose again.
            // This changes no character position, collision layer or movement animation.
            bot.Navigation.StopImmediate(true);
            bot.AgentActions.AiTargetBase.GotoFailed();
            CrewCombat.Recover(character);
            sample.Recoveries++;
            sample.RecoveryCooldown = 5;
            sample.Progress.Reset();
        }

        private static string Status()
        {
            if (_anchor == null) return VisitStatus();
            MyCubeGrid grid = _anchor.Grid;
            var sb = new StringBuilder("crew: grid=").Append(grid.EntityId)
                .Append(" combat=").Append(_combatTrial ? "on" : "off")
                .Append(" requested=").Append(_anchor.MaxBots).Append(" created=").Append(Samples.Count)
                .Append(" speed_ms=").Append(Number(grid.Physics != null ? grid.Physics.LinearVelocity.Length() : 0))
                .Append(" spin_rad_s=").Append(Number(grid.Physics != null ? grid.Physics.AngularVelocity.Length() : 0))
                .Append(" navigation=").Append(MyAIComponent.Static.Pathfinding.GetType().Name)
                .Append(" sim_probe_bound=").Append(CrewSimulationProbe.Bound)
                .Append(" movement_probe_bound=").Append(CrewSimulationProbe.MovementBound)
                .Append(" movement_preservation=").Append(_preserveMovement ? "on" : "off")
                .Append(" progress_recovery=").Append(_recoverProgress ? "on" : "off")
                .Append(" waypoint_probe_available=").Append(PathSteeringField != null)
                .Append(" corner_probe_bound=").Append(CrewCornerTolerance.Bound)
                .Append(" corner_tolerance_m=").Append(_cornerRadius > 0 ? Number(_cornerRadius) : "native")
                .Append(" world=").Append(grid.PositionComp.GetPosition());
            foreach (KeyValuePair<int, Sample> entry in Samples)
            {
                MyAgentBot bot = MyAIComponent.Static.Bots.TryGetBot<MyAgentBot>(entry.Key);
                MyCharacter character = bot != null ? bot.AgentEntity as MyCharacter : null;
                Sample s = entry.Value;
                sb.Append("\n serial=").Append(entry.Key).Append(" character=").Append(character != null ? character.EntityId : 0)
                    .Append(" alive=").Append(character != null && !character.IsDead)
                    .Append(" seated=").Append(character != null && character.Parent is MyCockpit)
                    .Append(" navigating=").Append(bot != null && bot.Navigation.Navigating)
                    .Append(" stuck=").Append(bot != null && bot.Navigation.Stuck)
                    .Append(" waiting_tiles=").Append(bot != null && bot.Navigation.IsWaitingForTileGeneration)
                    .Append(" wanted_speed=").Append(Number(bot != null ? bot.Navigation.WantedSpeed : 0))
                    .Append(" armed=").Append(character != null && character.CurrentWeapon != null)
                    .Append(" saved=").Append(character != null && character.Save)
                    .Append(" health=").Append(Number(s.Health)).Append(" helmet=").Append(s.Helmet)
                    .Append(" suit_energy_pct=").Append(character != null ? Number(character.SuitEnergyLevel * 100f) : "unknown")
                    .Append(" suit_hydrogen_pct=").Append(SuitHydrogen(character))
                    .Append(" samples=").Append(s.Readings).Append(" foot_travel_m=").Append(Number(s.FootTravel))
                    .Append(" char_sim_calls=").Append(s.SimulationCalls).Append(" sim_move_calls=").Append(s.SimulatedMoveCalls)
                    .Append(" sim_rotation_calls=").Append(s.SimulatedRotationCalls)
                    .Append(" zeroed_input_calls=").Append(Interlocked.Read(ref s.ZeroedMoveCalls))
                    .Append(" preserved_input_calls=").Append(Interlocked.Read(ref s.PreservedMoveCalls))
                    .Append(" corner_tolerance_calls=").Append(Interlocked.Read(ref s.CornerToleranceCalls))
                    .Append(" waypoint_known=").Append(s.WaypointKnown)
                    .Append(" waypoint_distance_m=").Append(s.WaypointKnown ? Number(s.WaypointDistance) : "unknown")
                    .Append(" no_progress_s=").Append(Number(s.Progress.NoProgressSeconds))
                    .Append(" progress_stuck=").Append(s.Progress.Stuck)
                    .Append(" progress_stall_samples=").Append(s.ProgressStalls)
                    .Append(" goal_recoveries=").Append(s.Recoveries)
                    .Append(" recovery_cooldown_s=").Append(s.RecoveryCooldown * 2)
                    .Append(" stuck_samples=").Append(s.Stuck).Append(" outside_samples=").Append(s.Outside)
                    .Append(" local=").Append(Number(s.Local.X)).Append(',').Append(Number(s.Local.Y)).Append(',').Append(Number(s.Local.Z));
                if (bot != null && character != null)
                {
                    Vector3D target = Vector3D.Transform(bot.Navigation.TargetPoint, grid.PositionComp.WorldMatrixNormalizedInv);
                    var pathfinding = MyAIComponent.Static.Pathfinding as MyRDPathfinding;
                    // Inspect only an existing area: status must not create a navigation area or
                    // freeze its orientation as a side effect of observing the trial.
                    var area = pathfinding != null ? pathfinding.GetNavigationArea(character.PositionComp.GetPosition(), false) : null;
                    sb.Append(" area=").Append(area != null ? area.GetType().Name : "none")
                        .Append(" target_local=").Append(Number(target.X)).Append(',').Append(Number(target.Y)).Append(',').Append(Number(target.Z))
                        .Append(" target_distance_m=").Append(Number(Vector3D.Distance(character.PositionComp.GetPosition(), bot.Navigation.TargetPoint)))
                        .Append(" movement=").Append(character.CurrentMovementState)
                        .Append(" controlled_local=").Append(character.ControllerInfo.IsLocallyControlled())
                        .Append(" controller_serial=").Append(character.ControllerInfo.Controller != null ? character.ControllerInfo.Controller.Player.Id.SerialId : -1)
                        .Append(" move_input=").Append(character.MoveIndicator)
                        .Append(" nav_forward_body=").Append(Vector3D.TransformNormal(bot.Navigation.ForwardVector, character.PositionComp.WorldMatrixNormalizedInv))
                        .Append(" gravity=").Append(character.Gravity)
                        .Append(" physics=").Append(character.Physics != null)
                        .Append(" physics_enabled=").Append(character.Physics != null && character.Physics.Enabled)
                        .Append(" in_scene=").Append(character.InScene)
                        .Append(" update_flags=").Append(character.NeedsUpdate)
                        .Append(" sim_registered=").Append(IsSimulated(character)?.ToString() ?? "unknown")
                        .Append(" character_proxy=").Append(character.Physics != null && character.Physics.CharacterProxy != null)
                        .Append(" proxy_state=").Append(character.Physics != null && character.Physics.CharacterProxy != null ? character.Physics.CharacterProxy.GetState().ToString() : "none")
                        .Append(" proxy_speed=").Append(Number(character.Physics != null && character.Physics.CharacterProxy != null ? character.Physics.CharacterProxy.Speed : 0))
                        .Append(" jetpack=").Append(character.JetpackComp != null && character.JetpackComp.TurnedOn)
                        .Append(" boots_enabled=").Append(character.IsMagneticBootsEnabled)
                        .Append(" boots_active=").Append(character.IsMagneticBootsActive);
                }
            }
            foreach (string ended in Ended) sb.Append("\n").Append(ended);
            return sb.Append("\nGrid-relative movement is diagnostic; doorway/stair traversal, clipping and confinement need visual observation.").ToString();
        }

        private static string Number(double value) { return value.ToString("0.00", CultureInfo.InvariantCulture); }
    }
}
