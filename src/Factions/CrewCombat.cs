using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Factions.Core;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Definitions;
using Sandbox.Game.AI;
using Sandbox.Game.AI.BehaviorTree;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.AI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace Factions
{
    /// <summary>FAC-440: combat controller for existing native humanoids. Entry crew join it as
    /// defenders while their hull's alarm is up and target only that hull's intruders; the bench
    /// trial's actors target every real player aboard. Population and entry policy remain in
    /// CrewPresence.</summary>
    internal static class CrewCombat
    {
        private const string RifleSubtype = "OPC_Rifle1_item";
        private const string MagazineSubtype = "OPC_Rifle_Ammo_Mag30";
        private const float CombatMaxHealth = 250f;
        private static readonly MyDefinitionId MedkitId = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), "Medkit");
        private sealed class Agent
        {
            public MyAgentBot Bot;
            public MyCubeGrid Grid;
            public bool Defence;
            public MyCharacter Target;
            public float OriginalDamageModifier, OriginalMaxHealth;
            public readonly CrewRecovery Recovery = new CrewRecovery();
            public readonly CrewTreatment Treatment = new CrewTreatment();
            public readonly CrewCover Cover = new CrewCover();
            public int MedkitsUsed;
            public bool MedpackActive;
            public readonly CrewEngagement Policy = new CrewEngagement();
            public CrewCombatIntent Intent;
            public Vector3D GoalLocal;
            public bool GoalKnown, Shooting, Visible, Aligned;
            public double Distance;
            public int SelectAt, PathAt, BurstUntil, FireAt, Offset, EquipAt;
            public int Starts, SightLosses, Paths, TreeSkips;
            public int RayOwnHits;
            public long RayEntityId;
            public string RayEntityType = "none";
            public double RayHitDistance;
            public string GunStatus = "none";
        }
        private static readonly Dictionary<long, Agent> Agents = new Dictionary<long, Agent>();
        private static volatile HashSet<long> Controlled = new HashSet<long>();
        private static readonly List<MyPhysics.HitInfo> Hits = new List<MyPhysics.HitInfo>();
        private sealed class DamageRecord
        {
            public string Type;
            public float Amount, Energy, Health;
            public long Attacker;
            public DateTime Utc;
        }
        private static readonly ConcurrentDictionary<long, DamageRecord> Damage = new ConcurrentDictionary<long, DamageRecord>();
        private static readonly Queue<string> Ended = new Queue<string>();
        private static MyDamageSystem _damageSystem;
        private static int _frame;

        private static void EnsureDamageHandler()
        {
            var system = MyDamageSystem.Static;
            if (system == null || ReferenceEquals(system, _damageSystem)) return;
            system.RegisterAfterDamageHandler(1000, OnAfterDamage);
            _damageSystem = system;
        }
        private static void OnAfterDamage(object target, MyDamageInformation info)
        {
            var character = target as MyCharacter;
            if (character == null || !Controlled.Contains(character.EntityId) || info.Amount <= 0) return;
            Damage[character.EntityId] = new DamageRecord {
                Type = info.Type.ToString(), Amount = info.Amount, Attacker = info.AttackerId,
                Energy = character.SuitEnergyLevel, Health = character.StatComp != null ? character.StatComp.Health.Value : -1f,
                Utc = DateTime.UtcNow
            };
        }
        private static void AppendDamage(StringBuilder text, DamageRecord damage)
        {
            text.Append(" last_damage=").Append(damage != null ? damage.Type : "none");
            if (damage == null) return;
            text.Append(" last_damage_amount=").Append(damage.Amount.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" last_attacker=").Append(damage.Attacker)
                .Append(" damage_health=").Append(damage.Health.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" damage_energy_pct=").Append((damage.Energy * 100f).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" damage_utc=").Append(damage.Utc.ToString("o", CultureInfo.InvariantCulture));
        }
        internal static bool Bound
        {
            get
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(MyBehaviorTree), "Tick"));
                if (info == null) return false;
                foreach (var prefix in info.Prefixes)
                    if (prefix.PatchMethod.DeclaringType == typeof(CrewCombatTree)) return true;
                return false;
            }
        }
        internal static bool Controls(IMyBot bot)
        {
            var agent = bot as MyAgentBot;
            return agent != null && agent.AgentEntity != null && Controlled.Contains(agent.AgentEntity.EntityId);
        }
        internal static bool AllowTree(IMyBot bot)
        {
            var native = bot as MyAgentBot;
            Agent agent;
            if (native == null || native.AgentEntity == null || !Agents.TryGetValue(native.AgentEntity.EntityId, out agent)) return true;
            agent.TreeSkips++;
            return false;
        }
        private static readonly MyDefinitionId RifleId = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), RifleSubtype);

        private static bool KitDefined()
        {
            MyPhysicalItemDefinition definition;
            return MyDefinitionManager.Static.TryGetPhysicalItemDefinition(RifleId, out definition)
                && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(
                    new MyDefinitionId(typeof(MyObjectBuilder_AmmoMagazine), MagazineSubtype), out definition)
                && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(MedkitId, out definition);
        }

        /// <summary>Puts a rifle, ten magazines and two medkits in a crew member's inventory without
        /// equipping anything. False, and nothing added, when the item definitions are missing.</summary>
        internal static bool SupplyKit(MyCharacter character)
        {
            if (!KitDefined()) return false;
            var inventory = character.GetInventory();
            inventory.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_PhysicalGunObject>(RifleSubtype));
            inventory.AddItems(10, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_AmmoMagazine>(MagazineSubtype));
            inventory.AddItems(2, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ConsumableItem>("Medkit"));
            return true;
        }

        /// <summary>A defender arms from the kit it carries and is skipped without one; a bench
        /// actor is resupplied with a fresh kit.</summary>
        internal static void Add(MyAgentBot bot, MyCubeGrid grid, bool defence)
        {
            EnsureDamageHandler();
            MyCharacter character = bot.AgentEntity;
            if (character == null || character.IsDead || Agents.ContainsKey(character.EntityId)) return;
            if (defence ? !character.GetInventory().ContainItems(1, RifleId) : !KitDefined())
            {
                if (defence) return;
                throw new InvalidOperationException("Selected crew requires Consolidation rifle, ammunition and medkit definitions.");
            }
            var health = character.StatComp != null ? character.StatComp.Health : null;
            if (health == null) throw new InvalidOperationException("Selected crew requires a native health stat.");
            float originalMaxHealth = health.MaxValue;
            // Finish any seat/emote action before acquiring navigation. Keep the native tree assigned
            // so its PreTickClear still advances the tick counter used by native stuck detection.
            bot.BotMemory.ClearPathMemory(true);
            var seat = character.Parent as MyCockpit;
            if (seat != null) seat.RemovePilot();
            bot.Navigation.StopImmediate(true);
            if (!defence)
            {
                character.GetInventory().Clear();
                SupplyKit(character);
            }
            character.SwitchToWeapon(RifleId);
            // Vanilla astronaut definitions are invulnerable; armed crew must take normal damage.
            // Preserve the prior setting for a living actor when it stands down. Arming keeps the
            // health fraction, so a wounded crewman is not healed by the alarm.
            float originalDamage = character.CharacterGeneralDamageModifier;
            character.CharacterGeneralDamageModifier = 1f;
            float fraction = originalMaxHealth > 0 ? health.Value / originalMaxHealth : 1f;
            health.SetMaxValue(CombatMaxHealth);
            health.Value = fraction * CombatMaxHealth;
            Agents.Add(character.EntityId, new Agent { Bot = bot, Grid = grid, Defence = defence, OriginalDamageModifier = originalDamage, OriginalMaxHealth = originalMaxHealth });
            Controlled = new HashSet<long>(Agents.Keys);
        }
        internal static void Remove(long characterId)
        {
            Agent agent;
            if (!Agents.TryGetValue(characterId, out agent)) return;
            DamageRecord damage;
            Damage.TryRemove(characterId, out damage);
            var ended = new StringBuilder("\n ended_character=").Append(characterId);
            AppendDamage(ended, damage);
            Ended.Enqueue(ended.ToString());
            while (Ended.Count > 8) Ended.Dequeue();
            Agents.Remove(characterId);
            Controlled = new HashSet<long>(Agents.Keys);
            MyCharacter character = agent.Bot.AgentEntity;
            if (character == null) return;
            if (!character.IsDead)
            {
                character.CharacterGeneralDamageModifier = agent.OriginalDamageModifier;
                var health = character.StatComp != null ? character.StatComp.Health : null;
                if (health != null)
                {
                    float ratio = health.MaxValue > 0 ? health.Value / health.MaxValue : 0f;
                    health.SetMaxValue(agent.OriginalMaxHealth);
                    health.Value = ratio * agent.OriginalMaxHealth;
                }
            }
            if (agent.Recovery.Flying) character.JetpackComp?.TurnOnJetpack(false);
            StopFire(agent, character);
            agent.Bot.Navigation.StopImmediate(true);
            agent.Bot.Navigation.StopAiming();
            agent.Bot.AgentActions.AiTargetBase.UnsetTarget();
            character.SwitchToWeapon((MyDefinitionId?)null);
            // The dead keep their kit as loot; a defender standing down keeps it for the next alarm.
            if (!character.IsDead && !agent.Defence) character.GetInventory().Clear();
            agent.Bot.BotMemory.ResetMemory(true);
        }
        internal static void Close()
        {
            foreach (long id in new List<long>(Agents.Keys)) Remove(id);
            Damage.Clear();
            Ended.Clear();
            _frame = 0;
        }
        internal static bool IsRecovering(MyCharacter character)
        {
            Agent agent;
            return Agents.TryGetValue(character.EntityId, out agent) && agent.Recovery.Flying;
        }
        internal static void Recover(MyCharacter character)
        {
            Agent agent;
            if (!Agents.TryGetValue(character.EntityId, out agent)) return;
            StopFire(agent, character);
            if (agent.Recovery.Begin(agent.Bot, agent.Grid, character, agent.Target))
            { agent.GoalKnown = false; agent.PathAt = _frame + 60; return; }
            agent.GoalKnown = false;
            agent.Offset = (agent.Offset + 1) % 5;
            agent.PathAt = _frame + 120;
        }
        internal static void Tick()
        {
            EnsureDamageHandler();
            _frame++;
            if (Agents.Count == 0 || MySession.Static == null) return;
            var gone = new List<long>();
            foreach (var entry in Agents)
            {
                Agent agent = entry.Value;
                MyCharacter character = agent.Bot.AgentEntity;
                if (character == null || character.IsDead || character.MarkedForClose || agent.Grid.MarkedForClose)
                { gone.Add(entry.Key); continue; }
                try { Update(agent, character); }
                catch (Exception ex)
                {
                    StopFire(agent, character);
                    FactionsPlugin.Log.Warn(ex, "Crew combat update failed.");
                    gone.Add(entry.Key);
                }
            }
            foreach (long id in gone) Remove(id);
        }
        private static MyCharacter Nearest(Agent agent, MyCharacter character)
        {
            MyCharacter nearest = null;
            double best = double.MaxValue;
            foreach (MyPlayer player in Sync.Players.GetOnlinePlayers())
            {
                MyCharacter candidate = player.Character;
                if (player.Id.SerialId != 0 || player.IsBot || candidate == null || candidate.IsDead
                    || candidate.MarkedForClose || !CrewPresence.IsEntered(candidate, agent.Grid)
                    || (agent.Defence && !CrewIntruders.IsIntruder(agent.Grid, candidate))) continue;
                AdminSettingsEnum settings;
                MySession.Static.RemoteAdminSettings.TryGetValue(player.Id.SteamId, out settings);
                if ((settings & AdminSettingsEnum.Untargetable) != 0) continue;
                double distance = Vector3D.DistanceSquared(character.PositionComp.GetPosition(), candidate.PositionComp.GetPosition());
                if (distance < best || (distance == best && (nearest == null || candidate.EntityId < nearest.EntityId)))
                { best = distance; nearest = candidate; }
            }
            return nearest;
        }
        private static bool ClearShot(Agent agent, MyCharacter character, MyCharacter target)
        {
            // Dedicated MyCharacter.UpdateShoot uses LogicalPositionWorld, and the rifle shifts
            // that origin 0.25m backwards along the shot direction. The animated muzzle can be
            // somewhere else (including inside nearby geometry); use the actual server origin.
            Vector3D from = character.WeaponPosition != null ? character.WeaponPosition.LogicalPositionWorld
                - character.GetHeadMatrix(false, true).Forward * 0.25 : character.GetHeadMatrix(true).Translation;
            Vector3D to = target.PositionComp.WorldVolume.Center;
            Hits.Clear();
            MyPhysics.CastRay(from, to, Hits, 30);
            var sightline = new CrewSightline();
            agent.RayOwnHits = 0;
            agent.RayEntityId = 0;
            agent.RayEntityType = "none";
            agent.RayHitDistance = 0;
            foreach (var hit in Hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity() as MyEntity;
                bool own = entity != null && (entity == character || entity == character.CurrentWeapon as MyEntity);
                if (own) agent.RayOwnHits++;
                double distance = Vector3D.DistanceSquared(from, hit.Position);
                if (sightline.Observe(distance, own, entity == target))
                {
                    agent.RayEntityId = entity != null ? entity.EntityId : 0;
                    agent.RayEntityType = entity != null ? entity.GetType().Name : "unknown";
                    agent.RayHitDistance = Math.Sqrt(distance);
                }
            }
            return sightline.Clear;
        }
        private static void Update(Agent agent, MyCharacter character)
        {
            // Native pending cockpit attachment can run after BotCreatedEvent and holster the gun.
            // Acquire the character even before a visitor boards, then equip from the existing
            // inventory (never replenish ammunition here). Retry the replicated equip at most 1 Hz.
            var seat = character.Parent as MyCockpit;
            if (seat != null) seat.RemovePilot();
            if (character.CurrentWeapon == null && _frame >= agent.EquipAt)
            {
                agent.EquipAt = _frame + 60;
                if (character.GetInventory().ContainItems(1, RifleId)) character.SwitchToWeapon(RifleId);
                agent.GunStatus = "equipping";
            }
            if (agent.Recovery.Tick(agent.Bot, agent.Grid, character, agent.Target, agent.Visible, _frame))
            {
                StopFire(agent, character);
                agent.GoalKnown = false;
                agent.PathAt = _frame + 60;
                return;
            }
            // Revalidate occupancy every frame, including during bursts; reselection at 2 Hz.
            if (_frame >= agent.SelectAt)
            {
                MyCharacter target = Nearest(agent, character);
                if (target != agent.Target)
                {
                    StopFire(agent, character);
                    agent.Policy.Update(false, 0, false, false);
                    agent.GoalKnown = false;
                    agent.PathAt = _frame;
                    agent.Offset = 0;
                    agent.Cover.Reset();
                    var aiTarget = agent.Bot.AgentActions.AiTargetBase;
                    aiTarget.UnsetTarget();
                    if (target != null)
                    {
                        MyBBMemoryTarget memory = null;
                        MyBBMemoryTarget.SetTargetEntity(ref memory, MyAiTargetEnum.CHARACTER, target.EntityId);
                        aiTarget.SetTargetFromMemory(memory);
                    }
                }
                agent.Target = target;
                agent.SelectAt = _frame + 30;
            }
            MyCharacter player = agent.Target;
            var health = character.StatComp.Health;
            agent.MedpackActive = false;
            foreach (var effect in health.GetEffects())
                if (effect.Value.Enabled && effect.Value.Duration > 0 && effect.Value.Amount > 0)
                { agent.MedpackActive = true; break; }
            bool hasThreat = player != null && !player.IsDead && !player.MarkedForClose
                && CrewPresence.IsEntered(player, agent.Grid);
            bool hasKit = character.GetInventory().ContainItems(1, MedkitId);
            var previousTreatment = agent.Treatment.Intent;
            var treatment = agent.Treatment.Update(1.0 / 60, health.Value / health.MaxValue,
                hasKit, hasThreat, agent.MedpackActive, false);
            if (treatment == CrewTreatmentIntent.Healing)
            {
                StopFire(agent, character);
                agent.Bot.Navigation.StopImmediate(true);
                agent.Bot.Navigation.StopAiming();
                agent.GoalKnown = false;
                agent.Cover.Reset();
                return;
            }
            if (treatment == CrewTreatmentIntent.Retreat)
            {
                StopFire(agent, character);
                agent.GoalKnown = false;
                if (previousTreatment != CrewTreatmentIntent.Retreat && previousTreatment != CrewTreatmentIntent.UseKit)
                    agent.Bot.Navigation.StopImmediate(true);
                if (agent.Cover.Update(agent.Bot, agent.Grid, character, player, agent.Recovery, _frame)
                    && agent.Recovery.Safe(character, agent.Grid, character.PositionComp.GetPosition())
                    && agent.Cover.Hidden(character, agent.Grid, player, character.PositionComp.GetPosition()))
                {
                    agent.Treatment.Update(0, health.Value / health.MaxValue, hasKit, true, false, true);
                    bool available;
                    var inventory = character.GetInventory() as MyInventory;
                    if (inventory != null && MyInventory.TryConsumeItem(character, inventory, MedkitId, out available, false))
                        agent.MedkitsUsed++;
                    else agent.Treatment.Defer();
                }
                return;
            }
            agent.Cover.Reset();
            if (player == null || player.IsDead || player.MarkedForClose || !CrewPresence.IsEntered(player, agent.Grid))
            {
                StopFire(agent, character);
                agent.Intent = agent.Policy.Update(false, 0, false, false);
                agent.Bot.Navigation.StopImmediate(true);
                agent.Bot.Navigation.StopAiming();
                agent.GoalKnown = false;
                agent.Target = null;
                return;
            }
            Vector3D aim = player.PositionComp.WorldVolume.Center;
            agent.Distance = Vector3D.Distance(character.PositionComp.GetPosition(), player.PositionComp.GetPosition());
            bool visible = ClearShot(agent, character, player);
            if (agent.Visible && !visible) agent.SightLosses++;
            agent.Visible = visible;
            Vector3D direction = aim - character.GetHeadMatrix(false, true).Translation;
            if (direction.LengthSquared() > 0.0001) direction.Normalize();
            agent.Aligned = Vector3D.Dot(character.GetHeadMatrix(false, true).Forward, direction) >= 0.996194698;
            agent.Intent = agent.Policy.Update(true, agent.Distance, visible, agent.Aligned);
            if (agent.Intent == CrewCombatIntent.Pursue)
            {
                StopFire(agent, character);
                // Aim along the route through corners. Face the player while holding a firing position.
                agent.Bot.Navigation.AimWithMovement();
                Vector3D goal = player.PositionComp.GetPosition();
                if (agent.Offset == 1) goal += agent.Grid.WorldMatrix.Right * 2;
                else if (agent.Offset == 2) goal -= agent.Grid.WorldMatrix.Right * 2;
                else if (agent.Offset == 3) goal += agent.Grid.WorldMatrix.Forward * 2;
                else if (agent.Offset == 4) goal -= agent.Grid.WorldMatrix.Forward * 2;
                Vector3D local = Vector3D.Transform(goal, agent.Grid.PositionComp.WorldMatrixNormalizedInv);
                if (_frame >= agent.PathAt && (!agent.GoalKnown || !agent.Bot.Navigation.Navigating
                    || Vector3D.DistanceSquared(local, agent.GoalLocal) > 1 || agent.Bot.Navigation.Stuck))
                {
                    Vector3D reachable;
                    agent.PathAt = _frame + 60;
                    if (agent.Bot.Navigation.CheckReachability(goal, 2, out reachable))
                    {
                        agent.Bot.Navigation.Goto(reachable, 0.5f, agent.Grid);
                        character.Walk();
                        agent.GoalLocal = local;
                        agent.GoalKnown = true;
                        agent.Paths++;
                    }
                    else { agent.Offset = (agent.Offset + 1) % 5; agent.GoalKnown = false; }
                }
                return;
            }
            agent.Bot.Navigation.StopImmediate(true);
            agent.GoalKnown = false;
            agent.Bot.Navigation.AimAt(player, aim);
            var gun = character.CurrentWeapon as MyAutomaticRifleGun;
            if (gun == null) { StopFire(agent, character); agent.GunStatus = "weapon_missing"; return; }
            MyGunStatusEnum status;
            bool ready = gun.CanShoot(MyShootActionEnum.PrimaryAction, character.ControllerInfo.ControllingIdentityId, out status);
            agent.GunStatus = status.ToString();
            if (gun.NeedsReload && gun.CanReload()) gun.Reload();
            if (agent.Intent != CrewCombatIntent.Fire || (!ready && status != MyGunStatusEnum.Cooldown)
                || (agent.Shooting && _frame >= agent.BurstUntil))
            {
                if (agent.Shooting) agent.FireAt = _frame + 24;
                StopFire(agent, character);
            }
            else if (!agent.Shooting && ready && _frame >= agent.FireAt)
            {
                character.BeginShoot(MyShootActionEnum.PrimaryAction);
                agent.Shooting = true;
                agent.BurstUntil = _frame + 36;
                agent.Starts++;
            }
        }
        private static void StopFire(Agent agent, MyCharacter character)
        {
            if (!agent.Shooting) return;
            // EndShoot() only synchronizes LocalCharacter; a dedicated-server bot needs EndShootSync.
            character.EndShootSync(MyShootActionEnum.PrimaryAction);
            agent.Shooting = false;
        }
        internal static string Status()
        {
            var text = new StringBuilder("crew combat: actors=").Append(Agents.Count).Append(" tree_bound=").Append(Bound);
            foreach (var entry in Agents)
            {
                Agent agent = entry.Value;
                var gun = agent.Bot.AgentEntity != null ? agent.Bot.AgentEntity.CurrentWeapon as MyAutomaticRifleGun : null;
                text.Append("\n character=").Append(entry.Key).Append(" role=").Append(agent.Defence ? "defender" : "bench").Append(" target=").Append(agent.Target != null ? agent.Target.EntityId : 0)
                    .Append(" intent=").Append(agent.Intent).Append(" distance_m=").Append(agent.Distance.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(" los=").Append(agent.Visible).Append(" aligned=").Append(agent.Aligned).Append(" firing=").Append(agent.Shooting)
                    .Append(" gun_status=").Append(agent.GunStatus).Append(" rounds=").Append(gun != null ? gun.CurrentMagazineAmmunition : -1)
                    .Append(" magazines=").Append(gun != null ? gun.CurrentMagazineAmount : -1)
                    .Append(" bursts=").Append(agent.Starts).Append(" sight_losses=").Append(agent.SightLosses)
                    .Append(" paths=").Append(agent.Paths).Append(" tree_skips=").Append(agent.TreeSkips)
                    .Append(" ray_origin=server_logical ray_entity=").Append(agent.RayEntityId)
                    .Append(" ray_type=").Append(agent.RayEntityType)
                    .Append(" ray_hit_m=").Append(agent.RayHitDistance.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(" ray_own_hits=").Append(agent.RayOwnHits)
                    .Append(" damage_modifier=").Append(agent.Bot.AgentEntity != null
                        ? agent.Bot.AgentEntity.CharacterGeneralDamageModifier.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")
                    .Append(" can_die=").Append(agent.Bot.AgentEntity != null && agent.Bot.AgentEntity.CharacterCanDie)
                    .Append(" treatment=").Append(agent.Treatment.Intent)
                    .Append(" medpack_active=").Append(agent.MedpackActive)
                    .Append(" medkits=").Append(agent.Bot.AgentEntity.GetInventory().GetItemAmount(MedkitId))
                    .Append(" medkits_used=").Append(agent.MedkitsUsed)
                    .Append(" cover=").Append(agent.Cover.Last)
                    .Append(" cover_searches=").Append(agent.Cover.Searches).Append(" cover_failures=").Append(agent.Cover.Failures)
                    .Append(" recovery=").Append(agent.Recovery.Last).Append(" embedded=").Append(agent.Recovery.Embedded)
                    .Append(" recovery_flying=").Append(agent.Recovery.Flying).Append(" recovery_flights=").Append(agent.Recovery.Flights)
                    .Append(" recovery_teleports=").Append(agent.Recovery.Teleports).Append(" recovery_no_destination=").Append(agent.Recovery.NoDestination);
                var character = agent.Bot.AgentEntity;
                if (character != null)
                    text.Append(" weapon=").Append(character.CurrentWeapon != null ? character.CurrentWeapon.DefinitionId.ToString() : "none")
                        .Append(" health=").Append(character.StatComp != null ? character.StatComp.Health.Value.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")
                        .Append(" max_health=").Append(character.StatComp != null ? character.StatComp.Health.MaxValue.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")
                        .Append(" suit_energy_pct=").Append((character.SuitEnergyLevel * 100f).ToString("0.00", CultureInfo.InvariantCulture))
                        .Append(" suit_hydrogen_pct=").Append(character.OxygenComponent != null
                            ? (character.OxygenComponent.GetGasFillLevel(MyCharacterOxygenComponent.HydrogenId) * 100f).ToString("0.00", CultureInfo.InvariantCulture) : "unknown");
                DamageRecord damage;
                Damage.TryGetValue(entry.Key, out damage);
                AppendDamage(text, damage);
            }
            foreach (string ended in Ended) text.Append(ended);
            return text.ToString();
        }
    }
    [HarmonyPatch(typeof(MyBehaviorTree), "Tick")]
    internal static class CrewCombatTree
    {
        [HarmonyPrefix] private static bool Before(IMyBot bot) { return CrewCombat.AllowTree(bot) && CrewOrderControl.AllowTree(bot); }
    }
}
