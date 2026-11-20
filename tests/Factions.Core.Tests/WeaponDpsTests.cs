using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="WeaponDps"/>, the damage-per-second arithmetic the prefab weapon scan runs
    /// per block.
    ///
    /// <para>The characteristic failure these guard against (FAC-221) is a heavily-armed hull
    /// scoring zero DPS and therefore never taking a combat role. Its cause was reading only the
    /// explosive damage channel: SE's Warfare 2 calibre shells and railgun slugs declare
    /// <c>MissileExplosionDamage</c> 0 and carry their damage in <c>MissileHealthPool</c>, so every
    /// weapon firing them resolved to nothing. The numbers below are the real vanilla values, read
    /// from <c>Content/Data/Ammos.sbc</c> and <c>Weapons.sbc</c> on the live server 2026-09-01.</para>
    /// </summary>
    public class WeaponDpsTests
    {
        // Weapons.sbc / Ammos.sbc, vanilla.
        private const float GatlingDamage         = 90f;      // LargeCaliber, ProjectileMassDamage
        private const float MediumCalibreDamage   = 4000f;    // MediumCalibreShell, MissileHealthPool
        private const float LargeCalibreDamage    = 17000f;   // LargeCalibreShell, MissileHealthPool
        private const float LargeRailgunDamage    = 50000f;   // LargeRailgunSlug, MissileHealthPool
        private const float VanillaMissileDamage  = 500f;     // Missile, MissileExplosionDamage

        [Fact]
        public void KineticAmmoDamageComesFromTheHealthPool()
        {
            // MediumCalibreShell: explosion damage 0, health pool 4000. Reading only the first
            // channel is what made an armed hull look unarmed.
            Assert.Equal(MediumCalibreDamage, WeaponDps.DamagePerShot(0f, MediumCalibreDamage, 1f));
        }

        [Fact]
        public void ExplosiveAmmoDamageStillComesFromTheExplosionChannel()
        {
            // Vanilla Missile: explosion damage 500, no health pool. Unchanged by the FAC-221 fix.
            Assert.Equal(VanillaMissileDamage, WeaponDps.DamagePerShot(VanillaMissileDamage, 0f, 1f));
        }

        [Fact]
        public void DamageMultiplierScalesBothChannels()
        {
            Assert.Equal(1000f, WeaponDps.DamagePerShot(500f, 0f, 2f));
            Assert.Equal(8000f, WeaponDps.DamagePerShot(0f, 4000f, 2f));
        }

        [Fact]
        public void AnUnarmedBlockStillResolvesToZero()
        {
            // The fix must not turn "no damage in either channel" into a nonzero band.
            Assert.Equal(0f, WeaponDps.DamagePerShot(0f, 0f, 1f));
            Assert.Equal(0f, WeaponDps.Sustained(0f, 700, 140, 4000));
        }

        [Fact]
        public void ARateOfFireOfZeroResolvesToZero()
        {
            Assert.Equal(0f, WeaponDps.Sustained(4000f, 0, 2, 6000));
        }

        [Theory]
        // LargeGatlingTurret: RoF 700, burst 140, reload 4000ms -> 75% duty cycle.
        // This is the one weapon that already worked before FAC-221, and it pins the formula:
        // two of these are the whole of C30_Military_Escort's live 1575 DPS band.
        [InlineData(GatlingDamage,       700, 140, 4000,  787.5f)]
        // MediumCalibreTurret: RoF 180, burst 2, reload 6000ms -> 10% duty cycle.
        [InlineData(MediumCalibreDamage, 180,   2, 6000, 1200f)]
        // LargeCalibreGun (artillery): RoF 80, burst 1, reload 12000ms.
        [InlineData(LargeCalibreDamage,   80,   1, 12000, 1333.3333f)]
        // LargeRailgun: RoF 20, burst 1, reload 4000ms.
        [InlineData(LargeRailgunDamage,   20,   1, 4000,  7142.857f)]
        public void VanillaWeaponsResolveTheirRealSustainedDps(
            float damagePerShot, int rateOfFire, int shotsInBurst, int reloadMs, float expected)
        {
            float actual = WeaponDps.Sustained(damagePerShot, rateOfFire, shotsInBurst, reloadMs);
            Assert.True(actual > 0f, "a vanilla armed weapon must not resolve to zero DPS");
            Assert.Equal(expected, actual, 2);
        }

        [Fact]
        public void AWeaponWithNoBurstFiresContinuously()
        {
            // No magazine limit configured: no reload pause, so the duty cycle does not apply.
            Assert.Equal(1050f, WeaponDps.Sustained(GatlingDamage, 700, 0, 4000), 2);
            Assert.Equal(1050f, WeaponDps.Sustained(GatlingDamage, 700, 140, 0), 2);
        }

        // Ace_80mmShellFlak: 100 explosive damage, bursting into 20-35
        // Ace_80mmShellFlak_Shrapnel, each a ProjectileAmmoDefinition with ProjectileMassDamage 40.
        // Read from the live definitions and the framework's own declaration, 2026-09-01.
        private const float FlakShellDamage    = 100f;
        private const float FlakShrapnelDamage = 40f;
        private const int   FlakShrapnelMin    = 20;
        private const int   FlakShrapnelMax    = 35;

        [Fact]
        public void ShrapnelAveragesTheDeclaredCount()
        {
            // (20+35)/2 = 27.5 children x 40 damage.
            Assert.Equal(1100f, WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 1f), 2);
        }

        [Fact]
        public void ShrapnelDominatesTheParentShell()
        {
            // The whole point of FAC-069's shrapnel case: reading only the parent's own damage
            // understates a flak shell by an order of magnitude.
            float total = FlakShellDamage
                        + WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 1f);
            Assert.Equal(1200f, total, 2);
            Assert.True(total > FlakShellDamage * 10f,
                        "a bursting shell must not be priced as its parent alone");
        }

        [Fact]
        public void ShrapnelFractionScalesTheContributionButNotTheParent()
        {
            Assert.Equal(550f, WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 0.5f), 2);
            Assert.Equal(0f,   WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 0f), 2);
        }

        [Fact]
        public void ANonBurstingRoundGainsNothing()
        {
            // Nearly every round in the game. A zero count must not become a phantom band.
            Assert.Equal(0f, WeaponDps.ShrapnelDamage(FlakShrapnelDamage, 0, 0, 1f));
            Assert.Equal(0f, WeaponDps.ShrapnelDamage(0f, FlakShrapnelMin, FlakShrapnelMax, 1f));
        }

        [Fact]
        public void ShrapnelToleratesAnInvertedOrNegativeCount()
        {
            // The counts come from a third-party definition, not from us.
            Assert.Equal(800f, WeaponDps.ShrapnelDamage(FlakShrapnelDamage, 20, 5, 1f), 2);   // max<min -> min
            Assert.Equal(0f,   WeaponDps.ShrapnelDamage(FlakShrapnelDamage, -10, -5, 1f), 2);
        }

        [Fact]
        public void AFlakTurretsSustainedDpsRisesWithItsShrapnel()
        {
            // Same weapon, same rate of fire; only the payload accounting differs.
            float parentOnly = WeaponDps.Sustained(FlakShellDamage, 120, 4, 3000);
            float withBurst  = WeaponDps.Sustained(
                FlakShellDamage + WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 1f),
                120, 4, 3000);
            Assert.True(withBurst > parentOnly * 10f);
        }

        [Fact]
        public void TheEnforcerLoadoutBecomesCombatSuitable()
        {
            // C33_Military_Enforcer: 2x LargeBlockMediumCalibreTurret + 1x LargeBlockLargeCalibreGun.
            // It scanned to WeaponBands: [] live on 2026-09-01 and was offered to the shipyard as a
            // hauler. IsCombatSuitableValue gates Raid/Patrol/Guard on this total.
            float turrets = WeaponDps.Sustained(
                WeaponDps.DamagePerShot(0f, MediumCalibreDamage, 1f), 180, 2, 6000) * 2f;
            float artillery = WeaponDps.Sustained(
                WeaponDps.DamagePerShot(0f, LargeCalibreDamage, 1f), 80, 1, 12000);

            Assert.Equal(2400f, turrets, 2);
            Assert.Equal(1333.3333f, artillery, 2);
            Assert.True(ShipPool.IsCombatSuitableValue(turrets + artillery, 0, "Large"));
        }

        // Ace_Turret_Flak300 (AWP, Weapons_Retired_AWP.sbc): MissileAmmoData RateOfFire 180, no burst
        // or reload declared; magazines Ace_300mmShellFlak then Ace_80mmMagazine, in that order.
        // Ace_300mmShellFlak is 100 explosive damage and does not burst. Read live 2026-10-09.
        private const int Flak300RateOfFire = 180;

        private sealed class Magazine
        {
            public string Name;
            public bool Resolves;
            public float DamagePerShot;
        }

        [Fact]
        public void AWeaponIsPricedOnItsFirstMagazineNotTheSumOfAll()
        {
            // FAC-223's characteristic failure: the 300 mm flak turret was priced at 3,900 dps -- its
            // own 300 mm shell plus a fully-loaded 80 mm flak gun, shrapnel included -- because every
            // magazine it could accept was added together. It fires the first; the munition model
            // loads the first.
            var magazines = new[]
            {
                new Magazine { Name = "Ace_300mmShellFlak", Resolves = true, DamagePerShot = FlakShellDamage },
                new Magazine { Name = "Ace_80mmMagazine",   Resolves = true,
                               DamagePerShot = FlakShellDamage
                                             + WeaponDps.ShrapnelDamage(FlakShrapnelDamage, FlakShrapnelMin, FlakShrapnelMax, 1f) },
            };

            int index = WeaponDps.DefaultLoadIndex(magazines, m => m.Resolves);

            Assert.Equal(0, index);
            Assert.Equal(300f, WeaponDps.Sustained(magazines[index].DamagePerShot, Flak300RateOfFire, 0, 0), 2);
        }

        [Fact]
        public void TheFirstMagazineWinsEvenWhenALaterOneIsBetter()
        {
            // Not the best ammo the weapon could take: a hull is loaded with the first, so pricing it
            // on the best would credit rounds it never carries.
            var damage = new[] { 100f, 1200f, 5000f };
            Assert.Equal(0, WeaponDps.DefaultLoadIndex(damage, d => d > 0f));
        }

        [Fact]
        public void AnUnresolvableFirstMagazineFallsThroughToTheNext()
        {
            // A magazine whose ammo or weapon ammo data is missing cannot be fired, so it is not the load.
            var magazines = new[]
            {
                new Magazine { Name = "Missing", Resolves = false },
                new Magazine { Name = "Second",  Resolves = true },
                new Magazine { Name = "Third",   Resolves = true },
            };
            Assert.Equal(1, WeaponDps.DefaultLoadIndex(magazines, m => m.Resolves));
        }

        [Fact]
        public void AWeaponWithNoResolvableMagazineHasNoLoad()
        {
            var unresolved = new[] { new Magazine { Resolves = false }, new Magazine { Resolves = false } };
            Assert.Equal(-1, WeaponDps.DefaultLoadIndex(unresolved, m => m.Resolves));
            Assert.Equal(-1, WeaponDps.DefaultLoadIndex(new Magazine[0], m => m.Resolves));
            Assert.Equal(-1, WeaponDps.DefaultLoadIndex<Magazine>(null, m => m.Resolves));
        }
    }
}
