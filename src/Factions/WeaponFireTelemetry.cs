using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Newtonsoft.Json;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Measures what weapons actually deliver, so the combat model can eventually be calibrated
    /// against observation rather than against a guess.
    ///
    /// <para><b>Why it exists.</b> The prefab scan computes each weapon's damage from its definitions,
    /// and that figure is correct as "damage delivered on a hit". Nothing in this codebase knows what
    /// share of rounds hit — homing, proximity fusing, projectile speed against a moving target and
    /// simple aim error all live outside the definition set. An accuracy term without measurement
    /// would be a fitted constant, which is the failure FAC-069 exists to avoid. This records the
    /// two numbers that settle it: rounds fired, and damage that arrived.</para>
    ///
    /// <para><b>Off unless asked for.</b> Gated on <c>FactionTuning.WeaponTelemetryEnabled</c>, default
    /// false: the shot hook sits on a path a firing turret runs many times a second, and this exists
    /// for short controlled tests, not for standing collection.</para>
    ///
    /// <para><b>The file does not grow with time.</b> One row per (weapon, ammo) pair, mutated in
    /// place — see <see cref="WeaponAccuracyLedger"/>.</para>
    /// </summary>
    internal static class WeaponFireTelemetry
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const string FileName = "weapon_accuracy.json";

        /// <summary>Set from tuning at session load. Everything below returns immediately when false,
        /// so a disabled instrument costs one boolean test per round fired.</summary>
        internal static bool Enabled;

        private static readonly WeaponAccuracyLedger Ledger = new WeaponAccuracyLedger();

        /// <summary>Firing block entity id → its ledger key, so damage arriving later can be
        /// attributed to the weapon that launched it. SE hands the after-damage hook the *launcher
        /// block's* entity id (see <c>MyMissile</c>'s <c>m_launcherId</c>), not the grid's.
        /// <para>Bounded by the number of weapon blocks that have actually fired, and cleared with the
        /// ledger.</para></summary>
        private static readonly Dictionary<long, string> KeyByLauncher = new Dictionary<long, string>();

        /// <summary><c>MyGunBase.m_user</c> is not public, and it is the only handle from inside
        /// <c>ConsumeAmmo</c> to the block doing the firing. Resolved once.</summary>
        private static FieldInfo _gunUserField;
        private static bool _gunUserFieldResolved;

        private static bool _damageHandlerRegistered;
        private static string _storageDir;

        // ---- lifecycle ------------------------------------------------------------------------------

        /// <summary>Patches the per-round hook. Deliberately a manual, wrapped patch rather than an
        /// attribute, so a measurement instrument binds or fails on its own and reports which.</summary>
        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyGunBase), "ConsumeAmmo");
                if (target == null)
                {
                    Log.Info("WeaponFireTelemetry: MyGunBase.ConsumeAmmo not found; shot counting disabled.");
                    return;
                }
                var postfix = new HarmonyMethod(typeof(WeaponFireTelemetry)
                    .GetMethod("OnConsumeAmmo", BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, postfix: postfix);
            }
            catch (Exception ex)
            {
                Log.Info("WeaponFireTelemetry: could not patch the shot hook: " + ex.Message);
            }
        }

        /// <summary>Called at session load, after tuning. Reads any previous sample back so a restart
        /// continues it rather than starting over.</summary>
        internal static void Load(string storageDir, bool enabled)
        {
            Enabled = enabled;
            _storageDir = storageDir;
            if (!enabled) return;

            EnsureDamageHandler();

            try
            {
                string path = PathOrNull();
                if (path != null && File.Exists(path))
                {
                    var persisted = JsonConvert.DeserializeObject<Dictionary<string, WeaponAccuracySample>>(
                        File.ReadAllText(path));
                    Ledger.Merge(persisted);
                }
            }
            catch (Exception ex)
            {
                Log.Info("WeaponFireTelemetry: could not read " + FileName + ": " + ex.Message);
            }

            Log.Info(string.Format("WeaponFireTelemetry: ARMED, {0} row(s) carried forward.", Ledger.RowCount));
        }

        private static void EnsureDamageHandler()
        {
            if (_damageHandlerRegistered) return;
            MyDamageSystem ds = MyDamageSystem.Static;
            if (ds == null) return;   // null until the session component loads; retried on next Load
            ds.RegisterAfterDamageHandler(1000, OnAfterDamage);
            _damageHandlerRegistered = true;
        }

        // ---- collection -----------------------------------------------------------------------------

        /// <summary>One round has left the barrel. <c>MyGunBase.ConsumeAmmo</c> decrements the
        /// magazine exactly once per round and is server-gated, so this counts rounds rather than
        /// trigger pulls.</summary>
        private static void OnConsumeAmmo(MyGunBase __instance)
        {
            if (!Enabled || __instance == null) return;
            try
            {
                MyEntity weapon = WeaponEntityOf(__instance);
                if (weapon == null) return;

                // Only grid-mounted weapons are of interest; a hand weapon has no block definition
                // and nothing in the combat model represents it.
                var block = weapon as MyCubeBlock;
                if (block == null || block.BlockDefinition == null) return;
                string weaponSubtype = block.BlockDefinition.Id.SubtypeName;

                MyAmmoDefinition ammo = __instance.CurrentAmmoDefinition;
                string ammoSubtype = ammo != null ? ammo.Id.SubtypeName : null;

                string key = WeaponAccuracyLedger.KeyFor(weaponSubtype, ammoSubtype);
                KeyByLauncher[weapon.EntityId] = key;

                Ledger.RecordShot(key, ModelledDamagePerShot(__instance, ammo), DateTime.UtcNow);
            }
            catch
            {
                // Never throw out of a hot SE path for the sake of a measurement.
            }
        }

        private static void OnAfterDamage(object target, MyDamageInformation info)
        {
            if (!Enabled || info.Amount <= 0 || info.IsDeformation) return;
            try
            {
                string key;
                if (!KeyByLauncher.TryGetValue(info.AttackerId, out key)) return;
                Ledger.RecordDamage(key, info.Amount, DateTime.UtcNow);
            }
            catch { }
        }

        /// <summary>What the definition set says one round of this ammo delivers, computed by the same
        /// helper the prefab scan uses so the comparison is against the modelled figure rather than a
        /// second opinion.</summary>
        private static double ModelledDamagePerShot(MyGunBase gun, MyAmmoDefinition ammo)
        {
            if (ammo == null) return 0;
            var missile = ammo as MyMissileAmmoDefinition;
            float multiplier = gun.WeaponDefinition != null ? gun.WeaponDefinition.DamageMultiplier : 1f;
            return WeaponDps.DamagePerShot(ammo.GetDamageForMechanicalObjects(),
                                           missile != null ? missile.MissileHealthPool : 0f,
                                           multiplier);
        }

        private static MyEntity WeaponEntityOf(MyGunBase gun)
        {
            if (!_gunUserFieldResolved)
            {
                _gunUserField = AccessTools.Field(typeof(MyGunBase), "m_user");
                _gunUserFieldResolved = true;
            }
            if (_gunUserField == null) return null;

            var user = _gunUserField.GetValue(gun) as IMyGunBaseUser;
            return user == null ? null : user.Weapon;
        }

        // ---- readout --------------------------------------------------------------------------------

        /// <summary>Writes the table. Called on flush and at session unload.</summary>
        internal static void Save()
        {
            if (!Enabled) return;
            try
            {
                string path = PathOrNull();
                if (path == null) return;

                var doc = new Dictionary<string, WeaponAccuracySample>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, WeaponAccuracySample> kv in Ledger.Rows) doc[kv.Key] = kv.Value;
                File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Info("WeaponFireTelemetry: could not write " + FileName + ": " + ex.Message);
            }
        }

        /// <summary>Human-readable dump for the bridge.</summary>
        internal static string Report(long minShots)
        {
            if (!Enabled) return "weapon telemetry is OFF (set WeaponTelemetryEnabled in tuning.json and restart).";
            if (Ledger.RowCount == 0) return "weapon telemetry is ARMED; nothing has fired yet.";

            var sb = new System.Text.StringBuilder();
            sb.Append("weapon telemetry -- ").Append(Ledger.RowCount).Append(" row(s):");
            foreach (KeyValuePair<string, WeaponAccuracySample> kv in Ledger.Rows)
            {
                WeaponAccuracySample r = kv.Value;
                sb.Append("\n  ").Append(kv.Key)
                  .Append(" shots=").Append(r.Shots)
                  .Append(" dmgEvents=").Append(r.DamageEvents)
                  .Append(" dealt=").Append(r.DamageDealt.ToString("F0"))
                  .Append(" perShot=").Append(r.DamagePerShot.ToString("F1"))
                  .Append(" modelled=").Append(r.ModelledDamagePerShot.ToString("F1"))
                  .Append(" realised=").Append(r.RealisedFraction.ToString("F3"));
                if (!r.HasSample(minShots)) sb.Append(" [thin]");
            }
            return sb.ToString();
        }

        /// <summary>Between controlled tests: a sample spanning two configurations is not one sample.</summary>
        internal static string Reset()
        {
            Ledger.Clear();
            KeyByLauncher.Clear();
            Save();
            return "weapon telemetry cleared.";
        }

        private static string PathOrNull()
        {
            return string.IsNullOrEmpty(_storageDir) ? null : Path.Combine(_storageDir, FileName);
        }
    }
}
