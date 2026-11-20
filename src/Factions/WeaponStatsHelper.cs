using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Computes real per-block combat stats (damage-per-second, engagement range) from SE weapon
    /// block definitions, for caching into <see cref="SePrefabCatalogue"/> (ship prefabs) and
    /// <c>PrefabBookValueCache</c> (station prefabs). Shared so both scanners walk the block loop
    /// once and use the same weapon-resolution chain rather than duplicating it.
    ///
    /// <para>API chain confirmed against decompiled SE/VRage source and live Content/Data/Weapons.sbc
    /// (2026-07-31): turret blocks (<see cref="MyLargeTurretBaseDefinition"/>) expose their AI
    /// engagement range directly via <c>MaxRangeMeters</c> — no ammo resolution needed. Fixed
    /// (non-turret) weapon blocks have no such field; their range and damage are derived from the
    /// ammo they can fire, resolved via <see cref="MyDefinitionManager"/>'s weapon/ammo-magazine/ammo
    /// lookups.</para>
    ///
    /// <para><b>Damage sits in one of two channels.</b> Explosive ammo declares
    /// <c>MissileExplosionDamage</c>, which <c>GetDamageForMechanicalObjects()</c> returns. Kinetic
    /// ammo (the Warfare 2 calibre shells and railgun slugs) leaves that at zero and carries its
    /// damage in <c>MissileHealthPool</c>, which SE spends against each block the round penetrates
    /// (<c>MyMissile.m_healthPool</c> → <c>MySlimBlock.DoMaximumDamage</c>). Reading only the
    /// first channel scores every such weapon at zero. <see cref="WeaponDps.DamagePerShot"/> reads
    /// both.</para>
    ///
    /// <para><b>DPS uses a reload duty cycle, not a flat rate.</b> <c>MyWeaponAmmoData.ShotsInBurst</c>
    /// is not "simultaneous rounds per RateOfFire tick" — confirmed against
    /// <c>MyLargeGatlingBarrel.Shoot</c>, it is the magazine size fired one round at a time before the
    /// weapon pauses for <c>ReloadTime</c> ms and refills. Vanilla example: <c>LargeGatlingTurret</c>
    /// (RateOfFire=700/min, ShotsInBurst=140, ReloadTime=4000ms) empties its 140-round magazine in 12s
    /// then reloads for 4s — a 75% duty cycle, not a 140x multiplier on every RateOfFire tick. The
    /// arithmetic itself lives in <see cref="WeaponDps"/> so Core and the tests share it.</para>
    /// </summary>
    internal static class WeaponStatsHelper
    {
        // Interior Turret block definitions carry TypeId "InteriorTurret" (both LargeInteriorTurret
        // and SmallInteriorTurret SubtypeIds) -- confirmed against decompiled SE source and live
        // Content/Data/CubeBlocks: they share MyLargeTurretBaseDefinition's XML shape with every
        // other turret family (so the MyWeaponBlockDefinition cast below succeeds for them too) but
        // are distinguished purely by this TypeId. Anti-boarder point-defense, not real ship-to-ship
        // combat power -- excluded so a token interior turret on an otherwise unarmed trade/cargo
        // hull doesn't make it register as combat-suitable.
        private static readonly VRage.ObjectBuilders.MyObjectBuilderType InteriorTurretTypeId =
            (VRage.ObjectBuilders.MyObjectBuilderType)typeof(MyObjectBuilder_InteriorTurret);

        /// <summary>True for a block this scan is expected to price: a
        /// <see cref="MyWeaponBlockDefinition"/> (or subtype) that is not an Interior Turret.
        ///
        /// <para>The distinction it buys is between "no weapon" and "a weapon nothing here could
        /// read". A block that is true here and still scores zero Dps is the second — its capability
        /// lives somewhere <see cref="MyDefinitionManager"/> cannot see, and treating it as unarmed is
        /// a guess rather than a reading. Shares <see cref="ComputeWeaponStats"/>'s test rather than
        /// restating it, so the two cannot disagree about what a weapon is.</para></summary>
        public static bool IsScoreableWeapon(MyCubeBlockDefinition blockDef)
        {
            var weaponBlock = blockDef as MyWeaponBlockDefinition;
            return weaponBlock != null && weaponBlock.Id.TypeId != InteriorTurretTypeId;
        }

        /// <summary>Per-block combat stats. Returns (0, 0) for any block that is not a weapon
        /// (<see cref="MyWeaponBlockDefinition"/> or a subtype), or that is an Interior Turret.
        ///
        /// <para>Damage and ammo range are those of <b>one</b> magazine, the weapon's default load
        /// (<see cref="TryResolveDefaultLoad"/>), never a sum or a max across every magazine it can
        /// accept: it fires one at a time, and is loaded with that one. A turret's own
        /// <c>MaxRangeMeters</c> still overrides a shorter ammo range.</para></summary>
        public static void ComputeWeaponStats(MyCubeBlockDefinition blockDef, out float dps, out float rangeM)
        {
            float shrapnelDps;
            ComputeWeaponStats(blockDef, out dps, out rangeM, out shrapnelDps);
        }

        /// <summary>As <see cref="ComputeWeaponStats(MyCubeBlockDefinition, out float, out float)"/>,
        /// additionally reporting how much of <paramref name="dps"/> came from shrapnel children.
        /// Zero for every weapon that does not burst, which is nearly all of them — the split exists so
        /// a boot can say whether shrapnel pricing reached a real number, rather than only that the
        /// framework's definitions were read.</summary>
        public static void ComputeWeaponStats(MyCubeBlockDefinition blockDef, out float dps, out float rangeM,
                                              out float shrapnelDps)
        {
            float beamDps;
            ComputeWeaponStats(blockDef, out dps, out rangeM, out shrapnelDps, out beamDps);
        }

        /// <summary>As above, additionally reporting how much of <paramref name="dps"/> came from
        /// framework beams (<see cref="AnpalBeamBridge"/>) — the block priced <b>alone</b>, with only
        /// the energy it generates itself. A hull that wires emitters into its storing weapons is
        /// priced by <see cref="HullWeaponPricer"/> instead.</summary>
        public static void ComputeWeaponStats(MyCubeBlockDefinition blockDef, out float dps, out float rangeM,
                                              out float shrapnelDps, out float beamDps)
        {
            dps = 0f;
            rangeM = 0f;
            shrapnelDps = 0f;
            beamDps = 0f;

            if (!IsScoreableWeapon(blockDef)) return;
            var weaponBlock = (MyWeaponBlockDefinition)blockDef;

            MyWeaponDefinition weaponDef = MyDefinitionManager.Static.GetWeaponDefinition(weaponBlock.WeaponDefinitionId);
            if (weaponDef == null || weaponDef.AmmoMagazinesId == null) return;

            MyDefinitionId magId;
            MyAmmoMagazineDefinition magDef;
            MyAmmoDefinition ammoDef;
            MyWeaponDefinition.MyWeaponAmmoData ammoData;
            if (TryResolveDefaultLoad(weaponDef, out magId, out magDef, out ammoDef, out ammoData))
            {
                // Kinetic ammo leaves GetDamageForMechanicalObjects() at zero and carries its damage
                // in MissileHealthPool instead -- see WeaponDps.DamagePerShot.
                var missileAmmo = ammoDef as MyMissileAmmoDefinition;
                float dmgPerShot = WeaponDps.DamagePerShot(
                    ammoDef.GetDamageForMechanicalObjects(),
                    missileAmmo != null ? missileAmmo.MissileHealthPool : 0f,
                    weaponDef.DamageMultiplier);

                float shrapnelPerShot = ShrapnelDamageOf(ammoDef, 0);
                dmgPerShot += shrapnelPerShot;

                float shotsPerSecond = ShotsPerSecond(blockDef, weaponDef, magId, magDef, ammoData);
                dps = dmgPerShot * shotsPerSecond;
                shrapnelDps = shrapnelPerShot * shotsPerSecond;
                rangeM = ammoDef.MaxTrajectory * weaponDef.RangeMultiplier;

                BlockBeams beams = CachedBeams(blockDef);
                if (beams != null)
                {
                    beamDps = beams.StandaloneDps;
                    dps += beamDps;
                    if (beams.RangeM > rangeM) rangeM = beams.RangeM;
                }
            }

            var turret = blockDef as MyLargeTurretBaseDefinition;
            if (turret != null && turret.MaxRangeMeters > rangeM)
                rangeM = turret.MaxRangeMeters;
        }

        /// <summary>What one weapon block fires and how much of it it holds, for
        /// <see cref="MunitionSlot"/>. The magazine is the weapon's default load
        /// (<see cref="TryResolveDefaultLoad"/>) — SE's own, the one a hull pasted from a prefab is
        /// fired with, and the one <paramref name="dps"/> was priced on. Full load is the block's inventory volume over one
        /// magazine's; both are m³ on the resolved definitions. Consumption is sustained rounds per
        /// second over rounds per magazine, reload duty cycle included, the same cycle
        /// <see cref="WeaponDps.Sustained"/> prices damage with.
        ///
        /// <para>False for a block that is not a scoreable weapon, has no inventory, whose
        /// magazines all fail to resolve, or whose default load declares no volume or capacity — such a weapon is left out of the munition model rather
        /// than given a load it cannot hold.</para></summary>
        public static bool TryReadMunitionSlot(MyCubeBlockDefinition blockDef, float dps, float rangeM,
                                               out MunitionSlot slot)
        {
            slot = default(MunitionSlot);
            if (dps <= 0f || !IsScoreableWeapon(blockDef)) return false;
            var weaponBlock = (MyWeaponBlockDefinition)blockDef;
            if (weaponBlock.InventoryMaxVolume <= 0f) return false;

            MyWeaponDefinition weaponDef = MyDefinitionManager.Static.GetWeaponDefinition(weaponBlock.WeaponDefinitionId);
            MyDefinitionId magId;
            MyAmmoMagazineDefinition magDef;
            MyAmmoDefinition ammoDef;
            MyWeaponDefinition.MyWeaponAmmoData ammoData;
            if (!TryResolveDefaultLoad(weaponDef, out magId, out magDef, out ammoDef, out ammoData)) return false;

            // The default load is the magazine dps was priced on. One with no volume or capacity is
            // left out rather than replaced by a later magazine, which would pair this magazine's
            // consumption with another magazine's damage.
            if (magDef.Volume <= 0f || magDef.Capacity <= 0) return false;
            int fullLoad = (int)Math.Floor(weaponBlock.InventoryMaxVolume / magDef.Volume + 1e-4);
            if (fullLoad <= 0) return false;

            // Rounds per second with the reload pause folded in, and capped by the framework's
            // ammo generation where the weapon makes its own magazine.
            float roundsPerSecond = ShotsPerSecond(blockDef, weaponDef, magId, magDef, ammoData);
            string key = InventoryKeyMapper.FromTypeIdAndSubtype(magId.TypeId.ToString(), magId.SubtypeName);
            if (string.IsNullOrEmpty(key)) return false;

            slot = new MunitionSlot
            {
                MagazineKey        = key,
                RangeM             = rangeM,
                Dps                = dps,
                MagazinesPerSecond = roundsPerSecond / magDef.Capacity,
                FullLoad           = fullLoad,
                SelfSupplied       = GeneratesOwnMagazine(blockDef, magId),
            };
            return true;
        }

        /// <summary>The magazine a weapon is priced and loaded on, resolved through to its ammo and
        /// its <c>WeaponAmmoData</c>: the first entry in <c>AmmoMagazinesId</c> that resolves with a
        /// positive rate of fire (<see cref="WeaponDps.DefaultLoadIndex{T}"/>). SE starts a weapon on
        /// that magazine (<c>MyGunBase</c> → <c>AmmoMagazinesId[0]</c>) and conveyor-fed weapons pull
        /// only their current one, and <see cref="FleetMunitionLoader"/> loads it, so a weapon's Dps,
        /// range and munition slot all describe this one ammo. False when none resolves.</summary>
        private static bool TryResolveDefaultLoad(MyWeaponDefinition weaponDef, out MyDefinitionId magId,
                                                  out MyAmmoMagazineDefinition magDef, out MyAmmoDefinition ammoDef,
                                                  out MyWeaponDefinition.MyWeaponAmmoData ammoData)
        {
            magId = default(MyDefinitionId);
            magDef = null;
            ammoDef = null;
            ammoData = null;
            if (weaponDef == null || weaponDef.AmmoMagazinesId == null) return false;

            int index = WeaponDps.DefaultLoadIndex(weaponDef.AmmoMagazinesId, id =>
            {
                MyAmmoMagazineDefinition m;
                MyAmmoDefinition a;
                MyWeaponDefinition.MyWeaponAmmoData d;
                return TryResolveMagazine(weaponDef, id, out m, out a, out d);
            });
            if (index < 0) return false;

            magId = weaponDef.AmmoMagazinesId[index];
            return TryResolveMagazine(weaponDef, magId, out magDef, out ammoDef, out ammoData);
        }

        private static bool TryResolveMagazine(MyWeaponDefinition weaponDef, MyDefinitionId magId,
                                               out MyAmmoMagazineDefinition magDef, out MyAmmoDefinition ammoDef,
                                               out MyWeaponDefinition.MyWeaponAmmoData ammoData)
        {
            ammoDef = null;
            ammoData = null;
            magDef = MyDefinitionManager.Static.GetAmmoMagazineDefinition(magId);
            if (magDef == null) return false;

            if (!MyDefinitionManager.Static.TryGetAmmoDefinition(magDef.AmmoDefinitionId, out ammoDef) || ammoDef == null)
                return false;

            int ammoTypeIndex = (int)ammoDef.AmmoType;
            if (weaponDef.WeaponAmmoDatas == null || ammoTypeIndex < 0 || ammoTypeIndex >= weaponDef.WeaponAmmoDatas.Length)
                return false;
            ammoData = weaponDef.WeaponAmmoDatas[ammoTypeIndex];
            return ammoData != null && ammoData.RateOfFire > 0;
        }

        /// <summary>Groups a prefab's per-block slots by (magazine, range) the way
        /// <see cref="GroupIntoBands"/> groups bands — Dps, consumption and load all sum.</summary>
        public static List<MunitionSlot> GroupSlots(List<MunitionSlot> perBlock)
        {
            const float bucketSizeM = 50f;
            var grouped = new List<MunitionSlot>();
            foreach (MunitionSlot slot in perBlock)
            {
                bool merged = false;
                for (int i = 0; i < grouped.Count; i++)
                {
                    MunitionSlot g = grouped[i];
                    if (!string.Equals(g.MagazineKey, slot.MagazineKey, StringComparison.Ordinal)) continue;
                    if (g.SelfSupplied != slot.SelfSupplied) continue;
                    if (Math.Abs(g.RangeM - slot.RangeM) > bucketSizeM) continue;
                    g.Dps += slot.Dps;
                    g.MagazinesPerSecond += slot.MagazinesPerSecond;
                    g.FullLoad += slot.FullLoad;
                    grouped[i] = g;
                    merged = true;
                    break;
                }
                if (!merged) grouped.Add(slot);
            }
            return grouped;
        }

        /// <summary>Census of every weapon block definition this world resolved, and how many of them
        /// the definition-read chain can actually price.
        ///
        /// <para>This is coverage, not usage: it asks what the scorer can read about the world's
        /// content, independent of whether any prefab happens to mount it. A block counted in
        /// <paramref name="unpriceable"/> is unmistakably a weapon and yields zero damage from every
        /// field <see cref="MyDefinitionManager"/> exposes — its capability lives in a mod's own code.
        /// Nothing here knows which mods those are; the block's own definition names its origin.</para>
        ///
        /// <para>Walks both definition accessors because they hold disjoint stores, deduplicating by
        /// id — the same reason <c>DefinitionDumpWriter.ReadDefinitions</c> does.</para></summary>
        public static void SurveyWeaponCoverage(out int weaponBlocks, out int unpriceable,
                                                Dictionary<string, int> unpriceableBySource,
                                                Dictionary<string, string> shrapnelBySource = null,
                                                Dictionary<string, string> beamBySource = null)
        {
            weaponBlocks = 0;
            unpriceable = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
                Consider(def, seen, ref weaponBlocks, ref unpriceable, unpriceableBySource, shrapnelBySource, beamBySource);
            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions<MyDefinitionBase>())
                Consider(def, seen, ref weaponBlocks, ref unpriceable, unpriceableBySource, shrapnelBySource, beamBySource);
        }

        private static void Consider(MyDefinitionBase def, HashSet<string> seen,
                                     ref int weaponBlocks, ref int unpriceable,
                                     Dictionary<string, int> unpriceableBySource,
                                     Dictionary<string, string> shrapnelBySource,
                                     Dictionary<string, string> beamBySource)
        {
            var blockDef = def as MyCubeBlockDefinition;
            if (blockDef == null || !IsScoreableWeapon(blockDef)) return;
            if (!seen.Add(blockDef.Id.ToString())) return;

            weaponBlocks++;

            float dps, rangeM, shrapnelDps, beamDps;
            ComputeWeaponStats(blockDef, out dps, out rangeM, out shrapnelDps, out beamDps);
            if (shrapnelDps > 0f && shrapnelBySource != null)
            {
                shrapnelBySource[blockDef.Id.SubtypeName] =
                    string.Format("{0:N0} dps of which {1:N0} shrapnel", dps, shrapnelDps);
            }

            // A beam block is reported whether or not it prices alone: one that stores energy is
            // worth nothing until a hull wires an emitter into it, and saying so is the point.
            BlockBeams beams = beamBySource != null ? CachedBeams(blockDef) : null;
            if (beams != null)
                beamBySource[blockDef.Id.SubtypeName] = beams.Describe(dps);
            if (dps > 0f) return;

            unpriceable++;
            if (unpriceableBySource == null) return;

            string key = blockDef.Id.SubtypeName + " <- " + DefinitionDumpWriter.SourceOf(blockDef);
            int count;
            unpriceableBySource.TryGetValue(key, out count);
            unpriceableBySource[key] = count + 1;
        }

        /// <summary>Damage a round delivers through shrapnel children, resolved through
        /// <see cref="VpfAmmoBridge"/>. Zero on any world without a weapon framework, and zero for
        /// every round that does not burst — which is nearly all of them.
        ///
        /// <para>The child's own damage comes from SE's definitions like any other ammo, so only the
        /// <i>count</i> and the child's identity are framework knowledge. Recursion is bounded
        /// independently of what the framework declares: a definition set that cycles must not be able
        /// to hang a boot scan.</para></summary>
        private static float ShrapnelDamageOf(MyAmmoDefinition parentAmmo, int depth)
        {
            const int maxDepth = 3;
            if (parentAmmo == null || depth >= maxDepth || !VpfAmmoBridge.Loaded) return 0f;

            List<VpfAmmoBridge.ShrapnelPayload> payloads =
                VpfAmmoBridge.PayloadFor(parentAmmo.Id.SubtypeName);
            if (payloads == null) return 0f;

            float total = 0f;
            foreach (VpfAmmoBridge.ShrapnelPayload payload in payloads)
            {
                MyAmmoDefinition childAmmo;
                var childId = new MyDefinitionId(parentAmmo.Id.TypeId, payload.ChildAmmoSubtype);
                if (!MyDefinitionManager.Static.TryGetAmmoDefinition(childId, out childAmmo) || childAmmo == null)
                    continue;

                var childMissile = childAmmo as MyMissileAmmoDefinition;
                float childDamage = WeaponDps.DamagePerShot(
                    childAmmo.GetDamageForMechanicalObjects(),
                    childMissile != null ? childMissile.MissileHealthPool : 0f,
                    1f);

                // A child that bursts in turn contributes its own children too.
                childDamage += ShrapnelDamageOf(childAmmo, depth + 1);

                total += WeaponDps.ShrapnelDamage(childDamage, payload.MinCount, payload.MaxCount,
                                                  VpfAmmoBridge.DamageFraction);
            }
            return total;
        }

        /// <summary>Rounds per second the weapon keeps firing: its reload duty cycle, capped by the
        /// framework's ammo generation when the default load is a magazine the block makes for
        /// itself, since it cannot fire faster than it makes rounds.</summary>
        private static float ShotsPerSecond(MyCubeBlockDefinition blockDef, MyWeaponDefinition weaponDef,
                                            MyDefinitionId magId, MyAmmoMagazineDefinition magDef,
                                            MyWeaponDefinition.MyWeaponAmmoData ammoData)
        {
            float shots = WeaponDps.Sustained(1f, ammoData.RateOfFire, ammoData.ShotsInBurst, weaponDef.ReloadTime);
            VpfAmmoBridge.MagazineGeneration generation;
            if (magDef != null && magDef.Capacity > 0
                && VpfAmmoBridge.TryGetGeneration(blockDef.Id.SubtypeName, out generation)
                && string.Equals(generation.MagazineSubtype, magId.SubtypeName, StringComparison.Ordinal))
            {
                float generated = generation.MagazinesPerSecond * magDef.Capacity;
                if (generated < shots) shots = generated;
            }
            return shots;
        }

        private static bool GeneratesOwnMagazine(MyCubeBlockDefinition blockDef, MyDefinitionId magId)
        {
            VpfAmmoBridge.MagazineGeneration generation;
            return VpfAmmoBridge.TryGetGeneration(blockDef.Id.SubtypeName, out generation)
                   && string.Equals(generation.MagazineSubtype, magId.SubtypeName, StringComparison.Ordinal);
        }

        // ---- framework beams ---------------------------------------------------------------------

        /// <summary>One beam a block fires, with the rate its vanilla gun drives it at and its price
        /// alone.</summary>
        internal sealed class PricedBeam
        {
            public AnpalBeamBridge.BeamShot Shot;
            public float BeamsPerSecond;
            public float StandaloneDps;

            /// <summary>Fires without stored energy and charges a block it hits.</summary>
            public bool IsFeeder
            {
                get { return !Shot.Energy.RequireEnergyToFire && !string.IsNullOrEmpty(Shot.ChargesEnergyType); }
            }

            /// <summary>Spends energy its block stores.</summary>
            public bool IsConsumer
            {
                get { return Shot.Energy.HasStorage && !string.IsNullOrEmpty(Shot.SpendsEnergyType); }
            }

            public CircuitConsumer AsConsumer()
            {
                return new CircuitConsumer
                {
                    EnergyType = Shot.SpendsEnergyType,
                    DamagePerTick = Shot.DamagePerTick,
                    DamageTicksPerBeam = Shot.DamageTicksPerBeam,
                    BeamsPerSecond = BeamsPerSecond,
                    Energy = Shot.Energy,
                    EnergyPerDamage = Shot.EnergyPerDamage,
                    IntakePerSecond = Shot.IntakePerSecond,
                };
            }
        }

        /// <summary>Every beam one block definition fires.</summary>
        internal sealed class BlockBeams
        {
            public readonly List<PricedBeam> Beams = new List<PricedBeam>();
            public float StandaloneDps;
            public float RangeM;

            /// <summary>The census line for one beam block: its price alone, and the role it can
            /// play in a hull circuit.</summary>
            public string Describe(float blockDps)
            {
                var parts = new List<string>();
                foreach (PricedBeam beam in Beams)
                {
                    var roles = new List<string>();
                    if (beam.IsFeeder) roles.Add("charges " + beam.Shot.ChargesEnergyType);
                    if (beam.IsConsumer) roles.Add("stores " + beam.Shot.SpendsEnergyType);
                    string role = roles.Count > 0 ? string.Join(", ", roles.ToArray()) : "no energy";
                    parts.Add(string.Format("{0} {1:N0} ({2:0.##}/s, {3})",
                                            beam.Shot.BeamName, beam.StandaloneDps, beam.BeamsPerSecond, role));
                }
                return string.Format("{0:N0} dps alone; {1}", blockDps, string.Join(", ", parts.ToArray()));
            }
        }

        private static readonly Dictionary<MyDefinitionId, BlockBeams> BeamCache =
            new Dictionary<MyDefinitionId, BlockBeams>(MyDefinitionId.Comparer);

        /// <summary>Forget priced beams. Call after <see cref="AnpalBeamBridge.Load"/> and
        /// <see cref="VpfAmmoBridge.Load"/>, which change what a block fires and how fast.</summary>
        internal static void ResetBeamCache()
        {
            BeamCache.Clear();
        }

        /// <summary>The beams a block fires, priced alone; null when no framework claims it. Cached
        /// per definition, since a definition prices the same everywhere.</summary>
        internal static BlockBeams CachedBeams(MyCubeBlockDefinition blockDef)
        {
            if (blockDef == null || !AnpalBeamBridge.Loaded || !IsScoreableWeapon(blockDef)) return null;
            BlockBeams beams;
            if (BeamCache.TryGetValue(blockDef.Id, out beams)) return beams;

            beams = null;
            var weaponBlock = (MyWeaponBlockDefinition)blockDef;
            MyWeaponDefinition weaponDef = MyDefinitionManager.Static.GetWeaponDefinition(weaponBlock.WeaponDefinitionId);
            MyDefinitionId magId;
            MyAmmoMagazineDefinition magDef;
            MyAmmoDefinition ammoDef;
            MyWeaponDefinition.MyWeaponAmmoData ammoData;
            if (TryResolveDefaultLoad(weaponDef, out magId, out magDef, out ammoDef, out ammoData))
                beams = BeamsOf(blockDef, magId, ShotsPerSecond(blockDef, weaponDef, magId, magDef, ammoData));
            BeamCache[blockDef.Id] = beams;
            return beams;
        }

        /// <summary>The beams a block fires while its vanilla gun shoots
        /// <paramref name="shotsPerSecond"/> rounds of <paramref name="magId"/>, each priced alone.
        /// A beam keyed to other magazines does not fire on this one.</summary>
        private static BlockBeams BeamsOf(MyCubeBlockDefinition blockDef, MyDefinitionId magId, float shotsPerSecond)
        {
            List<AnpalBeamBridge.BeamShot> shots = AnpalBeamBridge.ShotsFor(blockDef.Id.SubtypeName);
            if (shots == null) return null;

            var beams = new BlockBeams();
            foreach (AnpalBeamBridge.BeamShot shot in shots)
            {
                if (shot.AmmoIds != null && Array.IndexOf(shot.AmmoIds, magId.SubtypeName) < 0) continue;

                float perSecond = BeamDps.BeamsPerSecond(shotsPerSecond, shot.RateOfFirePerMinute,
                                                         shot.FireTimeTicks, shot.FireCount, shot.BurstCount);
                float standalone = BeamDps.Sustained(shot.DamagePerTick, shot.DamageTicksPerBeam, perSecond, shot.Energy);
                beams.Beams.Add(new PricedBeam { Shot = shot, BeamsPerSecond = perSecond, StandaloneDps = standalone });
                beams.StandaloneDps += standalone;
                if (shot.DamagePerTick > 0f && shot.TrajectoryM > beams.RangeM) beams.RangeM = shot.TrajectoryM;
            }
            return beams.Beams.Count > 0 ? beams : null;
        }

        /// <summary>
        /// Prices the weapons of one hull together, so that framework beams are priced as the circuit
        /// the hull wires them into (<see cref="BeamCircuit"/>) rather than block by block. Every
        /// other weapon passes through unchanged, so a hull with no beam blocks prices exactly as
        /// summing its blocks always did.
        /// </summary>
        internal sealed class HullWeaponPricer
        {
            private struct Entry
            {
                public MyCubeBlockDefinition Def;
                public float Dps;
                public float RangeM;
                public BlockBeams Beams;
            }

            private readonly List<Entry> _entries = new List<Entry>();

            /// <summary>True when this block takes part even at zero standalone Dps: a storing beam
            /// weapon is worthless alone and worth a great deal wired.</summary>
            public static bool Prices(MyCubeBlockDefinition blockDef, float standaloneDps)
            {
                return standaloneDps > 0f || CachedBeams(blockDef) != null;
            }

            /// <summary>Adds one weapon block at its standalone price, as
            /// <see cref="ComputeWeaponStats(MyCubeBlockDefinition, out float, out float)"/> gave it.</summary>
            public void Add(MyCubeBlockDefinition blockDef, float dps, float rangeM)
            {
                _entries.Add(new Entry { Def = blockDef, Dps = dps, RangeM = rangeM, Beams = CachedBeams(blockDef) });
            }

            /// <summary>Each added block's price within this hull, in the order added.</summary>
            public List<(float dps, float rangeM, MyCubeBlockDefinition def)> Finish()
            {
                var feeders = new List<CircuitFeeder>();
                var feederOwner = new List<int>();
                var consumers = new List<CircuitConsumer>();
                var consumerOwner = new List<int>();
                var consumerStandalone = new List<float>();

                for (int i = 0; i < _entries.Count; i++)
                {
                    BlockBeams beams = _entries[i].Beams;
                    if (beams == null) continue;
                    // A beam can be both: AnEWS's emitter charges Laser into what it hits and keeps a
                    // heat store of its own. Each role is priced as a delta, so neither counts twice.
                    foreach (PricedBeam beam in beams.Beams)
                    {
                        if (beam.IsConsumer)
                        {
                            consumers.Add(beam.AsConsumer());
                            consumerOwner.Add(i);
                            consumerStandalone.Add(beam.StandaloneDps);
                        }
                        if (beam.IsFeeder && beam.StandaloneDps > 0f)
                        {
                            feeders.Add(new CircuitFeeder { EnergyType = beam.Shot.ChargesEnergyType, DirectDps = beam.StandaloneDps });
                            feederOwner.Add(i);
                        }
                    }
                }

                var dps = new float[_entries.Count];
                for (int i = 0; i < _entries.Count; i++) dps[i] = _entries[i].Dps;

                if (feeders.Count > 0 && consumers.Count > 0)
                {
                    var feederDps = new float[feeders.Count];
                    var consumerDps = new float[consumers.Count];
                    BeamCircuit.Allocate(feeders, consumers, feederDps, consumerDps);
                    for (int f = 0; f < feeders.Count; f++)
                        dps[feederOwner[f]] += feederDps[f] - feeders[f].DirectDps;
                    for (int c = 0; c < consumers.Count; c++)
                        dps[consumerOwner[c]] += consumerDps[c] - consumerStandalone[c];
                }

                var result = new List<(float dps, float rangeM, MyCubeBlockDefinition def)>(_entries.Count);
                for (int i = 0; i < _entries.Count; i++)
                    result.Add((Math.Max(0f, dps[i]), _entries[i].RangeM, _entries[i].Def));
                return result;
            }
        }

        /// <summary>Groups a prefab's per-block weapon stats into one <see cref="WeaponBand"/> per
        /// distinct range present (summing Dps across blocks that share a range), rather than
        /// collapsing the whole loadout to a single total/max pair — a ship carrying both
        /// short-range and long-range weapons must not fire its full Dps at its longest weapon's
        /// range. Ranges within ~50m of each other are treated as the same band (absorbs float
        /// jitter between otherwise-identical turret instances). Blocks with zero computed Dps are
        /// skipped (not weapons, or a weapon whose ammo chain could not be resolved).</summary>
        public static List<WeaponBand> GroupIntoBands(List<(float dps, float rangeM)> perBlockStats)
        {
            const float bucketSizeM = 50f;
            var bands = new List<WeaponBand>();
            foreach (var stat in perBlockStats)
            {
                if (stat.dps <= 0) continue;

                bool merged = false;
                for (int i = 0; i < bands.Count; i++)
                {
                    WeaponBand b = bands[i];
                    if (System.Math.Abs(b.RangeM - stat.rangeM) <= bucketSizeM)
                    {
                        b.Dps += stat.dps;
                        bands[i] = b;
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    bands.Add(new WeaponBand { Dps = stat.dps, RangeM = stat.rangeM });
            }
            return bands;
        }
    }
}
