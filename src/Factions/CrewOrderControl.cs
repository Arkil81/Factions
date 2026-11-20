using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using DoorStatus = Sandbox.ModAPI.Ingame.DoorStatus;

namespace Factions
{
    /// <summary>
    /// Crew orders on a dynamic hull (docs/ground-operations.md): an owner tells a hull's entry crew
    /// to mount up, dismount and form up behind the hull, stand at ease, or follow the automatic
    /// default — seated in natural gravity, native walking in space.
    ///
    /// <para>A crewman under a seated or form-up stance is driven here and the native astronaut
    /// tree is skipped for him (<see cref="AllowTree"/>), so nothing stands him up or walks him
    /// to another grid's seat. An alarm still wins: a crewman <see cref="CrewCombat"/> controls is
    /// left alone, and resumes his order when it stands down.</para>
    ///
    /// <para>Between the deck and the ground a crewman goes through a door (<see cref="CrewExits"/>):
    /// he walks to it on his own side — on the hull's own navmesh aboard
    /// (<see cref="CrewNavigationArea"/>), on the terrain outside — the door is opened, and he
    /// crosses the threshold on his jetpack. A crossing that has not arrived in
    /// <see cref="Core.CrewOrders.CrossTimeoutSeconds"/> places him on the far side, and a crewman
    /// still short of his seat or slot when the stance's time limit runs out is placed there; both
    /// are counted, so the cost of a failed walk is visible per crewman.</para>
    ///
    /// <para>Orders live in memory, keyed by hull entity id; a restart returns every hull to
    /// automatic.</para>
    /// </summary>
    internal static class CrewOrderControl
    {
        private const double UpdateSeconds = 0.5;
        private const double ArriveM = 1.0;
        private const double DriftM = 2.0;
        private const double ExitArriveM = 1.0;
        private const double CrossArriveM = 0.8;
        private const double DoorWaitSeconds = 3.0;
        private const double PathRetrySeconds = 2.0;
        private const float SeatAttachM = 2.5f;
        /// <summary>One crewman per hull stands up per this many seconds: several standing at once
        /// take each other's room, and vanilla's seat exit then puts them outside the hull.</summary>
        private const double StandUpGapSeconds = 2.0;

        private sealed class Agent
        {
            public MyAgentBot Bot;
            public long Hull;
            public CrewStance Stance;
            public MyCockpit Seat;
            public CrewExits.Exit Exit;
            public bool Holding;
            public double Walking;
            public double PathIn;
            public Vector3D Goal;
            public bool GoalSet;
            public bool Crossing, Flying, OpenedDoor, WantsDoor;
            public double CrossFor;
            public Vector3D[] CrossPath;
            public int CrossLeg;
            public string State = "new";
            public int Walks, Arrivals, Snaps, Unplaced, Crossings, CrossPlaced, CrossMissed, StandMoved, StandNoDeck;
            public bool CrossInward;
            /// <summary>Level direction along the doorway, the way he crosses.</summary>
            public Vector3D CrossFace;
            public string StandWhy = "none";
        }

        private static readonly Dictionary<long, CrewOrder> Orders = new Dictionary<long, CrewOrder>();
        private static readonly Dictionary<long, List<Vector3D>> Formations = new Dictionary<long, List<Vector3D>>();
        private static readonly Dictionary<long, Agent> Agents = new Dictionary<long, Agent>();
        private static volatile HashSet<long> Driven = new HashSet<long>();
        private static readonly List<MyPhysics.HitInfo> Hits = new List<MyPhysics.HitInfo>();

        internal static CrewOrder OrderOf(long hullId)
        {
            CrewOrder order;
            return Orders.TryGetValue(hullId, out order) ? order : CrewOrder.Auto;
        }

        /// <summary>False while this crewman is under a seated or form-up stance.</summary>
        internal static bool AllowTree(IMyBot bot)
        {
            var native = bot as MyAgentBot;
            return native == null || native.AgentEntity == null || !Driven.Contains(native.AgentEntity.EntityId);
        }

        /// <summary>True while this crewman is crossing a door on his jetpack.</summary>
        internal static bool IsCrossing(MyCharacter character)
        {
            Agent agent;
            return character != null && Agents.TryGetValue(character.EntityId, out agent) && agent.Crossing;
        }

        internal static bool Drives(MyCharacter character)
        {
            return character != null && Driven.Contains(character.EntityId);
        }

        internal static double GravityG(MyCubeGrid hull)
        {
            Vector3D g = MyGravityProviderSystem.CalculateNaturalGravityInPoint(hull.PositionComp.WorldAABB.Center);
            return g.Length() / 9.81;
        }

        /// <summary>The owner, or a member of an owner's faction.</summary>
        internal static bool MayCommand(long identityId, MyCubeGrid hull)
        {
            if (identityId == 0 || hull == null || hull.BigOwners == null || hull.BigOwners.Count == 0) return false;
            if (hull.BigOwners.Contains(identityId)) return true;
            IMyFaction mine = MySession.Static.Factions.TryGetPlayerFaction(identityId);
            if (mine == null) return false;
            foreach (long owner in hull.BigOwners)
            {
                IMyFaction theirs = MySession.Static.Factions.TryGetPlayerFaction(owner);
                if (theirs != null && theirs.FactionId == mine.FactionId) return true;
            }
            return false;
        }

        private static readonly Dictionary<long, bool> HeldBefore = new Dictionary<long, bool>();
        private static readonly Dictionary<long, DateTime> NextStandUp = new Dictionary<long, DateTime>();
        /// <summary>Whose turn it is at each door: the crewman walking to it or crossing it. The rest
        /// wait clear of the doorway.</summary>
        private static readonly Dictionary<CrewExits.Exit, Agent> DoorTurn = new Dictionary<CrewExits.Exit, Agent>();
        private static double _botRangeM;

        /// <summary>Vanilla's bot range: <c>MyEnvironmentBotSpawningSystem</c> removes every bot
        /// farther than this from all real players, every two minutes. Read from SE; 400 m, its
        /// value in SE 1.210, only if the field cannot be read.</summary>
        internal static double BotRangeM
        {
            get
            {
                if (_botRangeM > 0) return _botRangeM;
                try
                {
                    var field = AccessTools.Field(typeof(MyEnvironmentBotSpawningSystem), "BOT_DESPAWN_DISTANCE");
                    _botRangeM = field != null ? Convert.ToDouble(field.GetValue(null), CultureInfo.InvariantCulture) : 400;
                }
                catch { _botRangeM = 400; }
                if (_botRangeM <= 0) _botRangeM = 400;
                return _botRangeM;
            }
        }

        /// <summary>A hull under a mount or dismount order keeps its crew in the world after the exit
        /// grace while a player who may command it is within <see cref="BotRangeM"/> of a crewman.
        /// When the last one leaves that range, each commanding player is warned once.</summary>
        internal static bool Held(MyCubeGrid hull, List<MyAgentBot> bots)
        {
            CrewOrder order = OrderOf(hull.EntityId);
            bool held = Core.CrewOrders.HoldsCrew(order) && MySession.Static.Players != null && CommanderInRange(hull, bots);
            bool before;
            HeldBefore.TryGetValue(hull.EntityId, out before);
            HeldBefore[hull.EntityId] = held;
            if (before && !held && Core.CrewOrders.HoldsCrew(order)) WarnLeftBehind(hull);
            return held;
        }

        private static bool CommanderInRange(MyCubeGrid hull, List<MyAgentBot> bots)
        {
            double r2 = BotRangeM * BotRangeM;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.IsBot || player.Id.SerialId != 0 || player.Character == null || player.Character.IsDead
                    || !MayCommand(player.Identity.IdentityId, hull)) continue;
                Vector3D at = player.Controller != null && player.Controller.ControlledEntity != null
                    ? player.GetPosition() : player.Character.PositionComp.GetPosition();
                bool anyCrew = false;
                foreach (MyAgentBot bot in bots)
                {
                    MyCharacter character = bot != null ? bot.AgentEntity : null;
                    if (character == null || character.IsDead) continue;
                    anyCrew = true;
                    if (Vector3D.DistanceSquared(at, character.PositionComp.GetPosition()) <= r2) return true;
                }
                if (!anyCrew && Vector3D.DistanceSquared(at, hull.PositionComp.WorldAABB.Center) <= r2) return true;
            }
            return false;
        }

        private static void WarnLeftBehind(MyCubeGrid hull)
        {
            string text = "Crew of " + hull.DisplayName + ": you are more than " + BotRangeM.ToString("0", CultureInfo.InvariantCulture)
                + " m away. Come back within 20 s or they are dismissed.";
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player.IsBot || player.Id.SerialId != 0 || !MayCommand(player.Identity.IdentityId, hull)) continue;
                long identity = player.Identity.IdentityId;
                MyVisualScriptLogicProvider.ShowNotification(text, 8000, "Red", identity);
                MyVisualScriptLogicProvider.SendChatMessage(text, "Crew", identity, "Red");
            }
        }

        /// <summary>Records the order and, for a dismount, lays out the form-up slots. The reply is
        /// shown to the player who gave it.</summary>
        internal static string Issue(MyCubeGrid hull, long identityId, bool admin, CrewOrder order, int crew, out bool ok)
        {
            ok = false;
            if (hull == null || hull.Closed || hull.MarkedForClose || hull.IsStatic)
                return "Crew order refused: no ship found.";
            if (!admin && !MayCommand(identityId, hull))
                return "Crew order refused: you do not command " + hull.DisplayName + ".";
            if (order == CrewOrder.Dismount)
            {
                List<Vector3D> slots = Formation(hull, Math.Max(1, Math.Min(8, crew > 0 ? crew : 8)));
                if (slots == null) return "Crew order refused: no ground behind " + hull.DisplayName + " to form up on.";
                Formations[hull.EntityId] = slots;
            }
            else Formations.Remove(hull.EntityId);
            if (order == CrewOrder.Auto) Orders.Remove(hull.EntityId);
            else Orders[hull.EntityId] = order;
            foreach (Agent agent in Agents.Values) if (agent.Hull == hull.EntityId) { agent.Walking = 0; agent.PathIn = 0; }
            ok = true;
            CrewStance stance = Core.CrewOrders.Stance(order, GravityG(hull));
            string what = order == CrewOrder.Mount ? "mount up and stay seated"
                : order == CrewOrder.Dismount ? "dismount and form up behind the ship"
                : order == CrewOrder.Ease ? "stand at ease"
                : "automatic (" + (stance == CrewStance.Seated ? "seated on the ground" : "at ease in space") + ")";
            return "Crew of " + hull.DisplayName + ": " + what + (crew > 0 ? " — " + crew + " aboard." : " — no crew aboard yet.");
        }

        /// <summary>Drives one hull's crew under its order. <paramref name="bots"/> in serial order.</summary>
        internal static void Update(MyCubeGrid hull, List<MyAgentBot> bots)
        {
            CrewOrder order = OrderOf(hull.EntityId);
            CrewStance stance = Core.CrewOrders.Stance(order, GravityG(hull));
            List<Vector3D> slots = null;
            if (stance == CrewStance.FormUp && (!Formations.TryGetValue(hull.EntityId, out slots) || slots.Count < bots.Count))
            {
                slots = Formation(hull, Math.Max(1, Math.Min(8, bots.Count)));
                if (slots != null) Formations[hull.EntityId] = slots;
                else stance = CrewStance.Seated;
            }
            int slot = 0;
            foreach (MyAgentBot bot in bots)
            {
                MyCharacter character = bot != null ? bot.AgentEntity : null;
                if (character == null || character.IsDead || character.MarkedForClose) continue;
                if (stance == CrewStance.AtEase || CrewCombat.Controls(bot)) { Release(character.EntityId, !CrewCombat.Controls(bot)); continue; }
                Agent agent;
                if (!Agents.TryGetValue(character.EntityId, out agent))
                {
                    agent = new Agent { Bot = bot, Hull = hull.EntityId, Stance = stance };
                    Agents.Add(character.EntityId, agent);
                    Driven = new HashSet<long>(Agents.Keys);
                    bot.Navigation.StopImmediate(true);
                    if (bot.AgentActions != null && bot.AgentActions.AiTargetBase != null) bot.AgentActions.AiTargetBase.UnsetTarget();
                }
                if (agent.Stance != stance && !agent.Crossing)
                {
                    agent.Stance = stance;
                    agent.Seat = null;
                    agent.Exit = null;
                    agent.Holding = false;
                    agent.Walking = 0;
                    agent.PathIn = 0;
                    agent.GoalSet = false;
                }
                Step(agent, character, hull, stance,
                    stance == CrewStance.FormUp ? slots[Math.Min(slot++, slots.Count - 1)] : Vector3D.Zero);
            }
        }

        private static void Step(Agent agent, MyCharacter character, MyCubeGrid hull, CrewStance stance, Vector3D slot)
        {
            if (agent.Crossing) { StepCrossing(agent, character, hull); return; }
            var seatNow = character.Parent as MyCockpit;
            if (seatNow != null && seatNow.CubeGrid != hull) { seatNow.RemovePilot(); seatNow = null; }
            bool seated = seatNow != null;
            Vector3D at = character.PositionComp.GetPosition();
            bool aboard = seated || CrewNavigationArea.Inside(hull, at);
            Vector3D target;
            bool atTarget;
            if (stance == CrewStance.Seated)
            {
                if (seated) { agent.Seat = seatNow; target = at; atTarget = true; }
                else
                {
                    if (agent.Seat == null || !Free(agent.Seat, character, hull)) agent.Seat = PickSeat(hull, character);
                    if (agent.Seat == null) { agent.State = "no_seat"; agent.Bot.Navigation.StopImmediate(true); return; }
                    target = agent.Seat.GetDetectorPosition();
                    atTarget = Vector3D.DistanceSquared(character.PositionComp.WorldAABB.Center, target) <= SeatAttachM * SeatAttachM
                        && agent.Seat.HasLineOfSightToDetector(character.GetHeadMatrix(true).Translation);
                }
            }
            else
            {
                target = slot;
                double d = Vector3D.Distance(at, slot);
                atTarget = d <= ArriveM || (agent.Holding && d <= DriftM);
            }
            // Going out, the door nearest the slot; coming in, the door nearest the crewman.
            if (agent.Exit == null || agent.Exit.Door.Closed)
                agent.Exit = CrewExits.Nearest(hull, aboard ? target : at);
            CrewExits.Exit exit = agent.Exit;
            Vector3D exitHere = exit != null
                ? Vector3D.Transform(aboard ? exit.InsideLocal : exit.OutsideLocal, hull.WorldMatrix) : at;
            bool atExit = exit != null && Vector3D.Distance(at, exitHere) <= ExitArriveM;
            CrewLeg leg = Core.CrewOrders.Next(stance, seated, aboard, atTarget, exit != null, atExit);
            switch (leg)
            {
                case CrewLeg.Hold:
                    agent.Walking = 0;
                    if (seated) { agent.State = "seated"; return; }
                    if (!agent.Holding) { agent.Arrivals++; agent.Bot.Navigation.StopImmediate(true); }
                    agent.Holding = true;
                    agent.State = "holding";
                    return;
                case CrewLeg.Sit:
                    Sit(agent, character);
                    agent.Arrivals++;
                    return;
                case CrewLeg.StandUp:
                    DateTime now = DateTime.UtcNow, next;
                    if (NextStandUp.TryGetValue(hull.EntityId, out next) && now < next) { agent.State = "waiting_stand"; return; }
                    NextStandUp[hull.EntityId] = now.AddSeconds(StandUpGapSeconds);
                    StandUp(agent, character, hull, seatNow, exit != null);
                    agent.Holding = false;
                    agent.GoalSet = false;
                    agent.State = "standing";
                    return;
            }
            bool doorLeg = leg == CrewLeg.WalkToExit || leg == CrewLeg.Cross;
            agent.WantsDoor = doorLeg;
            if (!doorLeg) ReleaseTurn(agent);
            if (doorLeg && !TakeTurn(agent, exit))
            {
                agent.Walking += UpdateSeconds;
                agent.State = "waiting_door";
                if (agent.Walking >= (stance == CrewStance.Seated ? Core.CrewOrders.SeatSnapSeconds : Core.CrewOrders.FormUpGiveUpSeconds))
                {
                    Place(agent, character, stance, target);
                    return;
                }
                if (aboard) agent.Bot.Navigation.StopImmediate(true);
                else Walk(agent, character, WaitSpot(agent, exit, hull), null, false);
                return;
            }
            if (leg == CrewLeg.Cross)
            {
                // A crossing that ends on the wrong side leaves him at the door, where the next one
                // starts at once; the stance's limit has to be checked here too, or he never gets placed.
                if (agent.Walking >= (stance == CrewStance.Seated ? Core.CrewOrders.SeatSnapSeconds : Core.CrewOrders.FormUpGiveUpSeconds))
                {
                    Place(agent, character, stance, target);
                    return;
                }
                BeginCrossing(agent, hull, exit, aboard);
                return;
            }
            agent.Holding = false;
            agent.Walking += UpdateSeconds;
            if (agent.Walking >= (stance == CrewStance.Seated ? Core.CrewOrders.SeatSnapSeconds : Core.CrewOrders.FormUpGiveUpSeconds))
            {
                Place(agent, character, stance, target);
                return;
            }
            if (leg == CrewLeg.WalkToExit)
            {
                agent.State = aboard ? "to_door_inside" : "to_door_outside";
                Walk(agent, character, exitHere, aboard ? hull : null, false);
            }
            else
            {
                agent.State = stance == CrewStance.Seated ? "to_seat" : "to_slot";
                Walk(agent, character, target, aboard ? hull : null, stance == CrewStance.Seated);
            }
        }

        /// <summary>Vanilla's seat exit can put a crewman outside the hull, depending on how the seat
        /// sits in it. A crewman bound for the door is set on the deck beside his seat instead.</summary>
        private static void StandUp(Agent agent, MyCharacter character, MyCubeGrid hull, MyCockpit seat, bool viaDoor)
        {
            string why = null;
            Vector3D? deck = viaDoor ? CrewExits.DeckBeside(hull, seat, character, out why) : null;
            if (viaDoor && !deck.HasValue) { agent.StandNoDeck++; agent.StandWhy = why; }
            seat.RemovePilot();
            if (!deck.HasValue || CrewNavigationArea.Inside(hull, character.PositionComp.GetPosition())) return;
            Teleport(character, deck.Value);
            agent.StandMoved++;
        }

        private static void Walk(Agent agent, MyCharacter character, Vector3D goal, MyCubeGrid relative, bool interactable)
        {
            agent.PathIn -= UpdateSeconds;
            bool newGoal = !agent.GoalSet || Vector3D.DistanceSquared(goal, agent.Goal) > 0.25;
            if (!newGoal && (agent.PathIn > 0 || (agent.Bot.Navigation.Navigating && !agent.Bot.Navigation.Stuck))) return;
            agent.PathIn = PathRetrySeconds;
            agent.Goal = goal;
            agent.GoalSet = true;
            Vector3D reach = goal;
            Vector3D reachable;
            if (interactable && agent.Bot.Navigation.CheckReachability(goal, 3, out reachable, relative)) reach = reachable;
            agent.Bot.Navigation.AimWithMovement();
            agent.Bot.Navigation.Goto(reach, 0.5f, relative);
            character.Walk();
            agent.Walks++;
        }

        private static void Sit(Agent agent, MyCharacter character)
        {
            agent.Bot.Navigation.StopImmediate(true);
            agent.Seat.AttachPilot(character, MyUtils.GetRandomInt(0, 1000000), false);
            agent.Walking = 0;
            agent.GoalSet = false;
            agent.State = character.Parent == agent.Seat ? "seated" : "seat_refused";
        }

        /// <summary>The stance's time limit ran out: into the seat, or onto the slot when a standing
        /// capsule there is clear.</summary>
        private static void Place(Agent agent, MyCharacter character, CrewStance stance, Vector3D target)
        {
            agent.Walking = 0;
            agent.Bot.Navigation.StopImmediate(true);
            if (stance == CrewStance.Seated) { Sit(agent, character); agent.Snaps++; return; }
            if (CrewRecovery.Clear(character, target, false, null))
            {
                Teleport(character, target);
                agent.Snaps++;
                agent.Holding = true;
                agent.State = "holding";
            }
            else { agent.Unplaced++; agent.State = "slot_blocked"; }
        }

        /// <summary>True when the door is this crewman's: free, already his, or held by a crewman no
        /// longer bound for it.</summary>
        private static bool TakeTurn(Agent agent, CrewExits.Exit exit)
        {
            Agent holder;
            if (DoorTurn.TryGetValue(exit, out holder) && holder != agent)
            {
                bool live = holder.Exit == exit && (holder.Crossing || holder.WantsDoor)
                    && holder.Bot != null && holder.Bot.AgentEntity != null && Agents.ContainsValue(holder);
                if (live) return false;
            }
            DoorTurn[exit] = agent;
            return true;
        }

        private static void ReleaseTurn(Agent agent)
        {
            if (agent.Exit != null)
            {
                Agent holder;
                if (DoorTurn.TryGetValue(agent.Exit, out holder) && holder == agent) DoorTurn.Remove(agent.Exit);
            }
        }

        /// <summary>Where a crewman waits his turn outside a door: rows of three, 1.2 m apart,
        /// starting 2.5 m out from the door's outside point, so the doorway stays clear.</summary>
        private static Vector3D WaitSpot(Agent agent, CrewExits.Exit exit, MyCubeGrid hull)
        {
            Vector3D outside = Vector3D.Transform(exit.OutsideLocal, hull.WorldMatrix);
            Vector3D inside = Vector3D.Transform(exit.InsideLocal, hull.WorldMatrix);
            Vector3D up = Up(hull);
            Vector3D outward = Flat(outside - inside, up);
            if (outward.LengthSquared() < 1e-6) return outside;
            outward.Normalize();
            Vector3D side = Vector3D.Cross(outward, up);
            int rank = 0;
            foreach (Agent other in Agents.Values)
                if (other != agent && other.Exit == exit && other.WantsDoor && !other.Crossing
                    && other.Bot != null && agent.Bot != null && other.Bot.Player.Id.SerialId < agent.Bot.Player.Id.SerialId)
                    rank++;
            return outside + outward * (2.5 + (rank / 3) * 1.2) + side * ((rank % 3) - 1) * 1.2;
        }

        private static Vector3D Up(MyCubeGrid hull)
        {
            Vector3D g = MyGravityProviderSystem.CalculateNaturalGravityInPoint(hull.PositionComp.WorldAABB.Center);
            return g.LengthSquared() > 1e-4 ? -Vector3D.Normalize(g) : hull.WorldMatrix.Up;
        }

        private static Vector3D Flat(Vector3D v, Vector3D up)
        {
            return v - up * Vector3D.Dot(v, up);
        }

        private static void BeginCrossing(Agent agent, MyCubeGrid hull, CrewExits.Exit exit, bool aboard)
        {
            agent.Bot.Navigation.StopImmediate(true);
            // Face along the doorway, level, for the whole crossing (see Face).
            Vector3D inside = Vector3D.Transform(exit.InsideLocal, hull.WorldMatrix);
            Vector3D porch = Vector3D.Transform(exit.PorchLocal, hull.WorldMatrix);
            Vector3D face = Flat(aboard ? porch - inside : inside - porch, Up(hull));
            agent.CrossFace = face.LengthSquared() > 1e-6 ? Vector3D.Normalize(face) : Vector3D.Zero;
            agent.Crossing = true;
            agent.Flying = false;
            agent.CrossFor = 0;
            agent.CrossLeg = 0;
            agent.CrossInward = !aboard;
            agent.CrossPath = aboard
                ? new[] { exit.PorchLocal, exit.OutsideLocal }
                : new[] { exit.PorchLocal, exit.InsideLocal };
            agent.OpenedDoor = false;
            if (exit.Door.Status != DoorStatus.Open && exit.Door.Status != DoorStatus.Opening)
            {
                exit.Door.OpenDoor();
                agent.OpenedDoor = true;
            }
            agent.State = "door_opening";
        }

        private static void StepCrossing(Agent agent, MyCharacter character, MyCubeGrid hull)
        {
            agent.CrossFor += UpdateSeconds;
            agent.Walking += UpdateSeconds;
            Face(agent, character);
            Vector3D to = Vector3D.Transform(agent.CrossPath[agent.CrossLeg], hull.WorldMatrix);
            if (!agent.Flying)
            {
                if (agent.Exit.Door.Status != DoorStatus.Open && agent.CrossFor < DoorWaitSeconds) return;
                agent.Bot.Navigation.FlyTo(to, hull);
                agent.Flying = true;
                agent.Crossings++;
                agent.State = "crossing";
                return;
            }
            // Arrival measured across the ground plane: a hovering jetpack holds the feet above the
            // point. A rise or a level leg must also have reached the point's height; the last leg
            // ends between 0.5 m below and 1.5 m above it, and the jetpack going off sets him down.
            Vector3D up = Up(hull);
            Vector3D off = character.PositionComp.GetPosition() - to;
            double rise = Vector3D.Dot(off, up);
            bool last = agent.CrossLeg + 1 >= agent.CrossPath.Length;
            if (Flat(off, up).Length() <= CrossArriveM && (last ? rise <= 1.5 && rise >= -0.5 : rise >= -0.3))
            {
                if (agent.CrossLeg + 1 >= agent.CrossPath.Length) { EndCrossing(agent, character); return; }
                agent.CrossLeg++;
                agent.Bot.Navigation.FlyTo(Vector3D.Transform(agent.CrossPath[agent.CrossLeg], hull.WorldMatrix), hull);
                return;
            }
            if (agent.CrossFor < Core.CrewOrders.CrossTimeoutSeconds) return;
            to = Vector3D.Transform(agent.CrossPath[agent.CrossPath.Length - 1], hull.WorldMatrix);
            // Across the doorway: square to its axis (porch to inside), level with the deck.
            Vector3D axis = Vector3D.TransformNormal(agent.Exit.InsideLocal - agent.Exit.PorchLocal, hull.WorldMatrix);
            Vector3D? spot = ClearNear(character, to, Vector3D.Cross(axis, Vector3D.TransformNormal(agent.Exit.PorchLocal - agent.Exit.OutsideLocal, hull.WorldMatrix)));
            if (spot.HasValue) { Teleport(character, spot.Value); agent.CrossPlaced++; }
            EndCrossing(agent, character);
        }

        /// <summary>Aims at a point 20 m along the doorway at the crewman's own height, refreshed
        /// every update. Vanilla movement holds still while the aim is more than 10° off
        /// (<c>MyBotNavigation.CorrectMovement</c>): a point in the doorway above a crewman on the
        /// ground needs a pitch the body never makes, so he stood and turned; with no aim at all a
        /// straight-up leg has no direction to face, and he spun.</summary>
        private static void Face(Agent agent, MyCharacter character)
        {
            if (agent.CrossFace.LengthSquared() < 1e-6) return;
            agent.Bot.Navigation.AimAt(null, character.PositionComp.GetPosition() + agent.CrossFace * 20);
        }

        private static void EndCrossing(Agent agent, MyCharacter character)
        {
            agent.Bot.Navigation.StopImmediate(true);
            agent.Bot.Navigation.StopAiming();
            agent.Bot.Navigation.AimWithMovement();
            if (character.JetpackComp != null) character.JetpackComp.TurnOnJetpack(false);
            character.MoveAndRotate(Vector3.Zero, Vector2.Zero, 0);
            agent.Crossing = false;
            ReleaseTurn(agent);
            MyCubeGrid hull = MyEntities.GetEntityById(agent.Hull) as MyCubeGrid;
            if (hull != null && CrewNavigationArea.Inside(hull, character.PositionComp.GetPosition()) != agent.CrossInward)
                agent.CrossMissed++;
            agent.Flying = false;
            agent.GoalSet = false;
            agent.PathIn = 0;
            if (agent.OpenedDoor && !OtherCrossing(agent)) agent.Exit.Door.CloseDoor();
            agent.OpenedDoor = false;
        }

        /// <summary><paramref name="feet"/> when a standing capsule there is clear, else 0.7 m to
        /// either side along <paramref name="across"/>; null when all three are taken.</summary>
        private static Vector3D? ClearNear(MyCharacter character, Vector3D feet, Vector3D across)
        {
            if (CrewRecovery.Clear(character, feet, false, null)) return feet;
            if (across.LengthSquared() < 1e-6) return null;
            across.Normalize();
            foreach (double offset in new[] { 0.7, -0.7 })
            {
                Vector3D candidate = feet + across * offset;
                if (CrewRecovery.Clear(character, candidate, false, null)) return candidate;
            }
            return null;
        }

        private static bool OtherCrossing(Agent agent)
        {
            foreach (Agent other in Agents.Values)
                if (other != agent && (other.Crossing || other.WantsDoor) && other.Exit == agent.Exit) return true;
            return false;
        }

        private static void Teleport(MyCharacter character, Vector3D feet)
        {
            MatrixD destination = character.WorldMatrix;
            destination.Translation = feet;
            character.Teleport(destination);
        }

        private static bool Free(MyCockpit seat, MyCharacter character, MyCubeGrid hull)
        {
            if (seat.Closed || seat.MarkedForClose || !seat.IsFunctional || seat.CubeGrid != hull) return false;
            if (seat.Pilot != null && seat.Pilot != character) return false;
            foreach (KeyValuePair<long, Agent> entry in Agents)
                if (entry.Key != character.EntityId && entry.Value.Seat == seat) return false;
            return true;
        }

        /// <summary>The nearest free passenger seat on the hull; control seats are left for players.</summary>
        private static MyCockpit PickSeat(MyCubeGrid hull, MyCharacter character)
        {
            MyCockpit best = null;
            double nearest = double.MaxValue;
            Vector3D at = character.PositionComp.GetPosition();
            foreach (MyCockpit seat in hull.GetFatBlocks<MyCockpit>())
            {
                if (seat is MyCryoChamber || seat.BlockDefinition.EnableShipControl || !Free(seat, character, hull)) continue;
                double d = Vector3D.DistanceSquared(at, seat.PositionComp.GetPosition());
                if (d < nearest) { nearest = d; best = seat; }
            }
            return best;
        }

        /// <summary>World positions behind the hull's rear face, each on the ground under it; null
        /// when no ground is found (a hull in space, or over a drop).</summary>
        private static List<Vector3D> Formation(MyCubeGrid hull, int count)
        {
            MatrixD m = hull.WorldMatrix;
            BoundingBoxD local = hull.PositionComp.LocalAABB;
            Vector3D centre = Vector3D.Transform(local.Center, m);
            Vector3D gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre);
            if (gravity.LengthSquared() < 1e-4) return null;
            Vector3D up = -Vector3D.Normalize(gravity);
            Vector3D forward = HullForward(hull);
            Vector3D rear = -(forward - up * Vector3D.Dot(forward, up));
            if (rear.LengthSquared() < 1e-4) return null;
            rear.Normalize();
            Vector3D side = Vector3D.Cross(rear, up);
            Vector3D half = local.HalfExtents;
            double extent = Extent(m, half, rear);
            double height = Extent(m, half, up);
            var slots = new List<Vector3D>(count);
            foreach (CrewSlot s in Core.CrewOrders.FormUpSlots(count))
            {
                Vector3D p = centre + rear * (extent + s.Back) + side * s.Side;
                Vector3D? ground = Ground(p + up * (height + 5), p - up * (height + 30), hull);
                if (!ground.HasValue) return null;
                slots.Add(ground.Value + up * 0.1);
            }
            return slots;
        }

        private static double Extent(MatrixD m, Vector3D half, Vector3D axis)
        {
            return Math.Abs(half.X * Vector3D.Dot(m.Right, axis)) + Math.Abs(half.Y * Vector3D.Dot(m.Up, axis))
                + Math.Abs(half.Z * Vector3D.Dot(m.Backward, axis));
        }

        /// <summary>The direction the hull is driven: its main cockpit's, else its first control
        /// seat's, else the grid's own Forward.</summary>
        private static Vector3D HullForward(MyCubeGrid hull)
        {
            var main = hull.MainCockpit as MyShipController;
            if (main != null) return main.WorldMatrix.Forward;
            foreach (MyCockpit seat in hull.GetFatBlocks<MyCockpit>())
                if (!(seat is MyCryoChamber) && seat.BlockDefinition.EnableShipControl) return seat.WorldMatrix.Forward;
            return hull.WorldMatrix.Forward;
        }

        private static Vector3D? Ground(Vector3D from, Vector3D to, MyCubeGrid hull)
        {
            Hits.Clear();
            MyPhysics.CastRay(from, to, Hits, 30);
            double nearest = double.MaxValue;
            Vector3D? hitAt = null;
            foreach (MyPhysics.HitInfo hit in Hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity();
                if (entity is MyCharacter || (entity != null && entity.GetTopMostParent() == hull)) continue;
                double d = Vector3D.DistanceSquared(from, hit.Position);
                if (d < nearest) { nearest = d; hitAt = hit.Position; }
            }
            return hitAt;
        }

        /// <summary>Hands a crewman back to the native tree, or to combat.</summary>
        internal static void Release(long characterId, bool stop)
        {
            Agent agent;
            if (!Agents.TryGetValue(characterId, out agent)) return;
            ReleaseTurn(agent);
            Agents.Remove(characterId);
            Driven = new HashSet<long>(Agents.Keys);
            MyCharacter character = agent.Bot != null ? agent.Bot.AgentEntity : null;
            if (agent.Crossing && character != null && character.JetpackComp != null) character.JetpackComp.TurnOnJetpack(false);
            if (stop && agent.Bot != null && agent.Bot.Navigation != null) agent.Bot.Navigation.StopImmediate(true);
        }

        /// <summary>Drops orders and slots for hulls that no longer exist.</summary>
        internal static void Sweep()
        {
            foreach (long id in new List<long>(Orders.Keys))
                if (MyEntities.GetEntityById(id) == null) { Orders.Remove(id); Formations.Remove(id); HeldBefore.Remove(id); NextStandUp.Remove(id); CrewExits.Forget(id); }
            foreach (long id in new List<long>(Formations.Keys))
                if (!Orders.ContainsKey(id)) Formations.Remove(id);
        }

        internal static void Close()
        {
            Orders.Clear();
            Formations.Clear();
            Agents.Clear();
            HeldBefore.Clear();
            NextStandUp.Clear();
            DoorTurn.Clear();
            CrewExits.Close();
            Driven = new HashSet<long>();
        }

        /// <summary>One hull's order and each driven crewman's state, for <c>crew visits</c>.</summary>
        internal static string Describe(MyCubeGrid hull)
        {
            CrewOrder order = OrderOf(hull.EntityId);
            return " order=" + Core.CrewOrders.Word(order)
                + " stance=" + Core.CrewOrders.Stance(order, GravityG(hull)).ToString().ToLowerInvariant()
                + " gravity_g=" + GravityG(hull).ToString("0.00", CultureInfo.InvariantCulture)
                + " exits=" + CrewExits.Of(hull).Count
                + " held=" + (HeldBefore.ContainsKey(hull.EntityId) && HeldBefore[hull.EntityId])
                + " bot_range_m=" + BotRangeM.ToString("0", CultureInfo.InvariantCulture);
        }

        internal static string Describe(long characterId)
        {
            Agent agent;
            if (!Agents.TryGetValue(characterId, out agent)) return " order_state=native";
            MyCharacter character = agent.Bot != null ? agent.Bot.AgentEntity : null;
            return " order_state=" + agent.State + " walks=" + agent.Walks + " arrivals=" + agent.Arrivals
                + " crossings=" + agent.Crossings + " cross_placed=" + agent.CrossPlaced + " cross_missed=" + agent.CrossMissed + " stand_moved=" + agent.StandMoved + " stand_nodeck=" + agent.StandNoDeck + " stand_why=" + agent.StandWhy
                + " snaps=" + agent.Snaps + " unplaced=" + agent.Unplaced
                + " nav_area=" + (character != null ? CrewNavigationArea.AreaKind(character.PositionComp.GetPosition()) : "none");
        }
    }
}
