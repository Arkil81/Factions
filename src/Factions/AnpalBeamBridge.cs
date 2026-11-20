using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using ProtoBuf;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Reads beam weapons from the ANPaL Beam Weapon Framework, whose damage is invisible to
    /// <see cref="Sandbox.Definitions.MyDefinitionManager"/>.
    ///
    /// <para><b>Why this exists at all.</b> A beam weapon is an ordinary vanilla gun firing a
    /// placeholder round, and the beam is spawned by the framework's C# whenever that gun shoots.
    /// AnEWS's round carries <c>ProjectileMassDamage 1</c>, so the definition chain prices a laser
    /// that deals 160 per damage tick at a few DPS (FAC-478). The beam's own damage, timing and
    /// energy system exist only in the framework's definitions, which it publishes through its API.</para>
    ///
    /// <para><b>Not a per-mod adapter.</b> This reads one framework's API, so any weapon any author
    /// builds on it is covered. Nothing here names a weapon, a mod or a subtype.</para>
    ///
    /// <para><b>Read from the content mods, the way the framework itself reads them.</b> The framework
    /// collects definitions only from its first simulation update, after Torch reports the session
    /// loaded, so asking <i>it</i> at session load finds nothing (v0.521.0 read 0 of everything:
    /// its <c>Sending Beam System registration request</c> came 0.6 s after our read). Every content
    /// mod's <c>ANPaL_Initer</c> registers from <c>LoadData</c> and answers a <c>null</c> on channel
    /// 8493 by broadcasting its serialised <c>ANPaL_ContainerDefinition</c> on 8492. This sends that
    /// same request and keeps the replies. The framework's own handler is not registered yet, and
    /// asks again at its first update, so the extra broadcast reaches nobody else that acts on it.
    /// Definitions are merged by the framework's rule: a later one replaces an earlier only at a
    /// strictly higher <c>Priority</c>.</para>
    ///
    /// <para><b>The contract below is deliberately partial, and every field defaults to zero.</b>
    /// protobuf-net skips members it has no field for, and it omits a zero value from the stream.
    /// The framework's own initialisers (<c>DamageInterval = 10</c> and the like) therefore arrive as
    /// written values whenever they are non-zero. A field initialised here would wrongly survive a
    /// genuine zero. <c>DamageTicks</c> uses SE's own <see cref="VRageMath.Vector2I"/>, whose members
    /// are numbered 1 and 4, not 1 and 2.</para>
    /// </summary>
    internal static class AnpalBeamBridge
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>A content mod answers a null here (<c>ANPaL_Initer.BeamSystemHandler</c>).</summary>
        private const long RequestChannel = 8493L;

        /// <summary>...with its serialised container here (<c>NetworkUtils.RegistrationChannel</c>).</summary>
        private const long ReplyChannel = 8492L;

        // ---- the partial contract ---------------------------------------------------------------

        [ProtoContract]
        private class AnpalContainer
        {
            [ProtoMember(1)] public string ModPath;
            [ProtoMember(2)] public AnpalBeam[] BeamProjectileDefinitions;
            [ProtoMember(3)] public AnpalBlock[] BeamSystemBlockDefinitions;
        }

        [ProtoContract]
        private class AnpalBeam
        {
            [ProtoMember(1)] public string BeamName;
            [ProtoMember(2)] public AnpalBasic BasicDefinition;
            [ProtoMember(3)] public AnpalBehavior BehaviorDefinition;
            [ProtoMember(4)] public AnpalDamage DamageDefinition;
            [ProtoMember(6)] public AnpalEnergy EnergyDefinition;
            [ProtoMember(99)] public int Priority;
        }

        [ProtoContract]
        private class AnpalBasic
        {
            [ProtoMember(1)] public float Damage;
            [ProtoMember(3)] public float BeamTrajectory;
            [ProtoMember(4)] public float RateOfFire;
            [ProtoMember(5)] public float BeamLifeTime;
        }

        [ProtoContract]
        private class AnpalBehavior
        {
            [ProtoMember(13)] public float BeamForwardMovementSpeed;
        }

        [ProtoContract]
        private class AnpalDamage
        {
            [ProtoMember(1)] public int DamageInterval;
            [ProtoMember(8)] public VRageMath.Vector2I DamageTicks;
            [ProtoMember(13)] public float LargeGridMultiplier;
        }

        [ProtoContract]
        private class AnpalEnergy
        {
            [ProtoMember(1)] public string BeamEnergyTypeId;
            [ProtoMember(2)] public bool RequireEnergyToFire;
            [ProtoMember(3)] public string RequiredEnergyType;
            [ProtoMember(4)] public float MinEnergyRequiredForFire;
            [ProtoMember(6)] public float EnergyRequiredForMinOutput;
            [ProtoMember(7)] public float EnergyConsumptionAtMinOutput;
            [ProtoMember(8)] public float EnergyRequiredForMaxOutput;
            [ProtoMember(9)] public float EnergyConsumptionAtMaxOutput;
            [ProtoMember(10)] public float BeamDamageMultiplierAtMaxOutput;
        }

        [ProtoContract]
        private class AnpalBlock
        {
            [ProtoMember(1)] public string[] BlockSubtypeIds;
            [ProtoMember(2)] public AnpalWeapon[] WeaponDefinition;
            [ProtoMember(3)] public AnpalStorage[] StorageDefinition;
            [ProtoMember(99)] public int Priority;
        }

        [ProtoContract]
        private class AnpalWeapon
        {
            [ProtoMember(1)] public string BeamName;
            [ProtoMember(2)] public int WCWeaponIndex;
            [ProtoMember(4)] public int FireCount;
            [ProtoMember(5)] public int BurstCount;
            [ProtoMember(7)] public int FireTime;
            [ProtoMember(8)] public string[] AmmoIds;
        }

        [ProtoContract]
        private class AnpalStorage
        {
            [ProtoMember(1)] public string StoredEnergyType;
            [ProtoMember(2)] public float MaxEnergyStorage;
            [ProtoMember(3)] public float DamageToEnergyRatio;
            [ProtoMember(5)] public float EnergyChargeSpeed;
            [ProtoMember(6)] public float EnergySelfConsumptionSpeed;
        }

        // ---- what the rest of the host consumes --------------------------------------------------

        /// <summary>One beam a weapon block fires, resolved against that block's own energy storage.
        /// Everything here is framework knowledge; the vanilla shot rate that drives it is the
        /// caller's, from SE's own weapon definition.</summary>
        internal sealed class BeamShot
        {
            public string BeamName;
            public float DamagePerTick;
            public int DamageTicksPerBeam;
            public float RateOfFirePerMinute;
            public float TrajectoryM;
            public int FireTimeTicks;
            public int FireCount;
            public int BurstCount;

            /// <summary>Magazine subtypes that trigger this beam; null when any does.</summary>
            public string[] AmmoIds;

            /// <summary>The energy this beam stores into a block it hits; empty when it charges
            /// nothing.</summary>
            public string ChargesEnergyType;

            /// <summary>The energy this beam spends; empty when it uses none.</summary>
            public string SpendsEnergyType;

            /// <summary>Income is self-generation only; a hull circuit adds the rest.</summary>
            public BeamEnergy Energy;

            public float EnergyPerDamage;
            public float IntakePerSecond;
        }

        private static Dictionary<string, List<BeamShot>> _byBlockSubtype;

        /// <summary>True once the content mods' definitions have been read. False on any world without
        /// the framework, which is the ordinary case and not an error.</summary>
        internal static bool Loaded { get { return _byBlockSubtype != null; } }

        /// <summary>The beams a block of this subtype fires, or null when the framework claims no
        /// such block.</summary>
        internal static List<BeamShot> ShotsFor(string blockSubtype)
        {
            if (_byBlockSubtype == null || string.IsNullOrEmpty(blockSubtype)) return null;
            List<BeamShot> shots;
            return _byBlockSubtype.TryGetValue(blockSubtype, out shots) ? shots : null;
        }

        // ---- lifecycle ----------------------------------------------------------------------------

        /// <summary>Asks every content mod for its beam definitions and merges them. Call once the
        /// session is loaded; content mods register their answer from <c>LoadData</c>.</summary>
        internal static void Load()
        {
            _byBlockSubtype = null;

            var replies = new List<byte[]>();
            Action<object> handler = payload =>
            {
                var bytes = payload as byte[];
                if (bytes != null && bytes.Length > 0) replies.Add(bytes);
            };
            try
            {
                MyAPIUtilities.Static.RegisterMessageHandler(ReplyChannel, handler);
                // Answered synchronously, from inside this call, by every content mod loaded.
                MyAPIUtilities.Static.SendModMessage(RequestChannel, null);
            }
            catch (Exception ex)
            {
                Log.Info("AnpalBeamBridge: could not ask content mods for beam definitions: " + ex.Message);
            }
            finally
            {
                try { MyAPIUtilities.Static.UnregisterMessageHandler(ReplyChannel, handler); }
                catch { }
            }
            if (replies.Count == 0) return;

            var mods = new HashSet<string>(StringComparer.Ordinal);
            var beams = new Dictionary<string, AnpalBeam>(StringComparer.Ordinal);
            var claims = new Dictionary<string, AnpalBlock>(StringComparer.Ordinal);
            int unreadable = 0;
            foreach (byte[] bytes in replies)
            {
                AnpalContainer container = Read<AnpalContainer>(bytes);
                if (container == null) { unreadable++; continue; }
                if (!mods.Add(container.ModPath ?? "")) continue;

                if (container.BeamProjectileDefinitions != null)
                {
                    foreach (AnpalBeam beam in container.BeamProjectileDefinitions)
                    {
                        if (beam == null || string.IsNullOrEmpty(beam.BeamName)) continue;
                        AnpalBeam held;
                        if (beams.TryGetValue(beam.BeamName, out held) && beam.Priority <= held.Priority) continue;
                        beams[beam.BeamName] = beam;
                    }
                }
                if (container.BeamSystemBlockDefinitions != null)
                {
                    foreach (AnpalBlock block in container.BeamSystemBlockDefinitions)
                    {
                        if (block == null || block.BlockSubtypeIds == null) continue;
                        foreach (string subtype in block.BlockSubtypeIds)
                        {
                            if (string.IsNullOrEmpty(subtype)) continue;
                            AnpalBlock held;
                            if (claims.TryGetValue(subtype, out held) && block.Priority <= held.Priority) continue;
                            claims[subtype] = block;
                        }
                    }
                }
            }

            var bySubtype = new Dictionary<string, List<BeamShot>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, AnpalBlock> claim in claims)
            {
                List<BeamShot> shots = ShotsOf(claim.Value, beams);
                if (shots.Count > 0) bySubtype[claim.Key] = shots;
            }

            _byBlockSubtype = bySubtype;
            Log.Info(string.Format(
                "AnpalBeamBridge: {0} content mod(s) answered{1}; {2} beam definition(s), {3} block subtype(s) claimed, {4} fire beams.",
                mods.Count, unreadable > 0 ? string.Format(" ({0} reply/replies unreadable)", unreadable) : "",
                beams.Count, claims.Count, bySubtype.Count));
        }

        private static T Read<T>(byte[] bytes) where T : class
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new System.IO.MemoryStream(bytes))
                    return Serializer.Deserialize<T>(stream);
            }
            catch
            {
                // A shape this contract no longer matches. Skipping one definition must not cost
                // the rest.
                return null;
            }
        }

        /// <summary>The beams one block fires through vanilla shots: weapon group 0, the only one
        /// the framework drives without WeaponCore.</summary>
        private static List<BeamShot> ShotsOf(AnpalBlock block, Dictionary<string, AnpalBeam> beams)
        {
            var shots = new List<BeamShot>();
            if (block.WeaponDefinition == null) return shots;

            foreach (AnpalWeapon weapon in block.WeaponDefinition)
            {
                if (weapon == null || weapon.WCWeaponIndex != 0 || string.IsNullOrEmpty(weapon.BeamName)) continue;
                AnpalBeam beam;
                if (!beams.TryGetValue(weapon.BeamName, out beam) || beam.BasicDefinition == null) continue;

                AnpalBasic basic = beam.BasicDefinition;
                AnpalDamage damage = beam.DamageDefinition ?? new AnpalDamage();
                AnpalBehavior behavior = beam.BehaviorDefinition ?? new AnpalBehavior();
                int window = BeamDps.DamageTicksPerBeam(basic.BeamLifeTime, damage.DamageInterval,
                                                        damage.DamageTicks.X, damage.DamageTicks.Y);
                AnpalEnergy energy = beam.EnergyDefinition ?? new AnpalEnergy();

                var shot = new BeamShot
                {
                    BeamName = beam.BeamName,
                    DamagePerTick = basic.Damage * damage.LargeGridMultiplier,
                    DamageTicksPerBeam = BeamDps.LandingTicksPerBeam(window, damage.DamageInterval,
                                                                     basic.BeamTrajectory, behavior.BeamForwardMovementSpeed),
                    RateOfFirePerMinute = basic.RateOfFire,
                    TrajectoryM = basic.BeamTrajectory,
                    FireTimeTicks = weapon.FireTime,
                    FireCount = weapon.FireCount,
                    BurstCount = weapon.BurstCount,
                    AmmoIds = weapon.AmmoIds != null && weapon.AmmoIds.Length > 0 ? weapon.AmmoIds : null,
                    ChargesEnergyType = energy.BeamEnergyTypeId ?? "",
                    SpendsEnergyType = energy.RequiredEnergyType ?? "",
                    Energy = new BeamEnergy
                    {
                        RequireEnergyToFire = energy.RequireEnergyToFire,
                        MinEnergyForFire = energy.MinEnergyRequiredForFire,
                        EnergyForMinOutput = energy.EnergyRequiredForMinOutput,
                        EnergyForMaxOutput = energy.EnergyRequiredForMaxOutput,
                        ConsumptionAtMinOutput = energy.EnergyConsumptionAtMinOutput,
                        ConsumptionAtMaxOutput = energy.EnergyConsumptionAtMaxOutput,
                        MultiplierAtMaxOutput = energy.BeamDamageMultiplierAtMaxOutput,
                    },
                };

                // The framework matches a beam to the block's first storage of its required type.
                AnpalStorage storage = null;
                if (!string.IsNullOrEmpty(shot.SpendsEnergyType) && block.StorageDefinition != null)
                {
                    foreach (AnpalStorage s in block.StorageDefinition)
                    {
                        if (s == null || !string.Equals(s.StoredEnergyType, shot.SpendsEnergyType, StringComparison.Ordinal)) continue;
                        storage = s;
                        break;
                    }
                }
                if (storage != null)
                {
                    shot.Energy.HasStorage = true;
                    shot.Energy.MaxStorage = storage.MaxEnergyStorage;
                    shot.Energy.GuaranteedIncomePerSecond = Math.Max(0f, -storage.EnergySelfConsumptionSpeed);
                    shot.Energy.DrainPerSecond = Math.Max(0f, storage.EnergySelfConsumptionSpeed);
                    shot.EnergyPerDamage = storage.DamageToEnergyRatio;
                    shot.IntakePerSecond = storage.EnergyChargeSpeed;
                }

                shots.Add(shot);
            }
            return shots;
        }
    }
}
