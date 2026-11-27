using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>FAC-440: marks players who act against a ship or its crew as intruders on that
    /// hull, and keeps each marked hull's alarm: up while an intruder is aboard. A transition arms or
    /// stands down the hull's crew (<see cref="CrewPresence"/>) and fires the hull's Event Controllers
    /// set to <see cref="IntruderAlarmEvent"/>, crewed or not. The damage hook only queues; judging
    /// runs on the game thread in <see cref="Tick"/>. Grinding raises only SE's before-damage event,
    /// so that is the one hooked.</summary>
    internal static class CrewIntruders
    {
        private struct Act
        {
            public long Hull;
            public long Identity;
            public string Type;
        }

        private static readonly ConcurrentQueue<Act> Pending = new ConcurrentQueue<Act>();
        private static readonly IntruderLedger Ledger = new IntruderLedger();
        private static readonly Dictionary<long, MyCubeGrid> Hooked = new Dictionary<long, MyCubeGrid>();
        private static readonly Dictionary<long, CrewAlarm> Alarms = new Dictionary<long, CrewAlarm>();
        private static readonly List<MyCubeGrid> Changed = new List<MyCubeGrid>();
        private static MyDamageSystem _damageSystem;
        private static int _frame;

        /// <summary>Factions day-log writer, wired by the plugin.</summary>
        internal static Action<string> Sink;

        private static void EnsureHandler()
        {
            var system = MyDamageSystem.Static;
            if (system == null || ReferenceEquals(system, _damageSystem)) return;
            system.RegisterBeforeDamageHandler(1000, OnBeforeDamage);
            _damageSystem = system;
        }

        private static void OnBeforeDamage(object target, ref MyDamageInformation info)
        {
            try
            {
                if (!CrewIntrusion.IsHostileAct(info.Type.String)) return;
                MyCubeGrid hull = null;
                var block = target as MySlimBlock;
                if (block != null && block.CubeGrid != null) hull = WingWorld.HullOf(block.CubeGrid);
                else
                {
                    var character = target as MyCharacter;
                    if (character != null) hull = CrewPresence.CrewHull(character.EntityId);
                }
                if (hull == null || hull.IsStatic) return;
                long identity = AttackerIdentity(info.AttackerId);
                if (identity != 0) Pending.Enqueue(new Act { Hull = hull.EntityId, Identity = identity, Type = info.Type.String });
            }
            catch { /* a throwing handler must not break SE's damage pipeline */ }
        }

        /// <summary>The player behind a hit, resolved the way <c>MyCharacter.DoDamage</c> does: a
        /// character, a hand weapon or tool's carrier, or a block's owner (ship grinders, turrets,
        /// warheads). Zero when nothing resolves.</summary>
        internal static long AttackerIdentity(long attackerId)
        {
            MyEntity attacker;
            if (attackerId == 0 || !MyEntities.TryGetEntityById(attackerId, out attacker) || attacker == null) return 0;
            var carrier = attacker as MyCharacter;
            if (carrier != null) return carrier.GetPlayerIdentityId();
            var tool = attacker as IMyHandheldGunObject<MyToolBase>;
            if (tool != null) return tool.OwnerIdentityId;
            var gun = attacker as IMyHandheldGunObject<MyGunBase>;
            if (gun != null) return gun.OwnerIdentityId;
            var owned = attacker as MyCubeBlock;
            return owned != null ? owned.OwnerId : 0;
        }

        internal static void Tick()
        {
            EnsureHandler();
            Act act;
            while (Pending.TryDequeue(out act)) Judge(act);
            if (++_frame % 120 != 0 || Hooked.Count == 0 || MySession.Static.Players == null) return;
            Changed.Clear();
            foreach (var entry in Hooked)
            {
                bool aboard = false;
                foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                {
                    if (player.IsBot || player.Id.SerialId != 0 || player.Character == null) continue;
                    if (CrewPresence.IsEntered(player.Character, entry.Value) && IsIntruder(entry.Value, player.Character))
                    { aboard = true; break; }
                }
                CrewAlarm alarm;
                if (!Alarms.TryGetValue(entry.Key, out alarm)) { alarm = new CrewAlarm(); Alarms[entry.Key] = alarm; }
                bool was = alarm.Active;
                if (alarm.Update(aboard, 2) != was) Changed.Add(entry.Value);
            }
            foreach (MyCubeGrid hull in Changed) OnAlarm(hull, Alarms[hull.EntityId].Active);
        }

        internal static bool IsArmed(long hull)
        {
            CrewAlarm alarm;
            return Alarms.TryGetValue(hull, out alarm) && alarm.Active;
        }

        private static void OnAlarm(MyCubeGrid hull, bool active)
        {
            int crew = CrewPresence.OnAlarm(hull, active);
            int fired = 0;
            foreach (MyCubeGrid grid in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(hull))
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    var controller = block as Sandbox.ModAPI.IMyEventControllerBlock;
                    if (controller == null || !(controller.SelectedEvent is IntruderAlarmEvent)) continue;
                    controller.TriggerAction(active ? 0 : 1);
                    fired++;
                }
            if (Sink != null)
                Sink("CREW_ALARM hull=" + hull.EntityId + " state=" + (active ? "armed" : "stood_down")
                    + " crew=" + crew + " intruders=" + Ledger.Count(hull.EntityId) + " event_controllers=" + fired);
        }

        private static void Judge(Act act)
        {
            MyCubeGrid hull;
            if (!MyEntities.TryGetEntityById(act.Hull, out hull, false) || hull.Closed || hull.MarkedForClose) return;
            if (!FactionsPlugin.IsHumanIdentity(act.Identity)) return;
            MyFaction side = CrewPresence.HullFaction(hull);
            long owner = hull.BigOwners != null && hull.BigOwners.Count > 0 ? hull.BigOwners[0] : 0;
            long faction = FactionOf(act.Identity);
            if (!CrewIntrusion.IsOutsider(act.Identity, faction, side != null ? side.FactionId : 0, owner)) return;
            if (!Ledger.Mark(act.Hull, act.Identity, faction)) return;
            if (!Hooked.ContainsKey(act.Hull)) { hull.OnClose += OnHullClose; Hooked[act.Hull] = hull; }
            if (Sink != null)
                Sink("CREW_INTRUDER hull=" + act.Hull + " identity=" + act.Identity + " faction=" + faction
                    + " side=" + (side != null ? side.Tag : "none") + " act=" + act.Type);
        }

        internal static bool IsIntruder(MyCubeGrid hull, MyCharacter character)
        {
            if (hull == null || character == null || !Ledger.Knows(hull.EntityId)) return false;
            long identity = character.GetPlayerIdentityId();
            return Ledger.IsIntruder(hull.EntityId, identity, FactionOf(identity));
        }

        internal static int Count(long hull) { return Ledger.Count(hull); }

        private static long FactionOf(long identity)
        {
            var faction = MySession.Static.Factions.TryGetPlayerFaction(identity);
            return faction != null ? faction.FactionId : 0;
        }

        private static void OnHullClose(MyEntity entity)
        {
            entity.OnClose -= OnHullClose;
            Hooked.Remove(entity.EntityId);
            Alarms.Remove(entity.EntityId);
            Ledger.Forget(entity.EntityId);
        }

        internal static string Status()
        {
            var text = new StringBuilder("crew intruders: hulls=").Append(Hooked.Count)
                .Append(" acts=grind,drill,weapons,explosions event_attached=").Append(IntruderAlarmEvent.Active);
            foreach (long hull in Ledger.Hulls)
            {
                CrewAlarm alarm;
                Alarms.TryGetValue(hull, out alarm);
                text.Append("\n hull=").Append(hull).Append(" intruders=").Append(Ledger.Count(hull))
                    .Append(" alarm=").Append(alarm != null && alarm.Active ? "armed" : "passive")
                    .Append(" quiet_s=").Append(alarm != null ? (int)alarm.QuietSeconds : 0);
            }
            return text.ToString();
        }

        internal static void Close()
        {
            foreach (MyCubeGrid grid in Hooked.Values) grid.OnClose -= OnHullClose;
            Hooked.Clear();
            Alarms.Clear();
            Ledger.Clear();
            _frame = 0;
            Act discarded;
            while (Pending.TryDequeue(out discarded)) { }
        }
    }
}
