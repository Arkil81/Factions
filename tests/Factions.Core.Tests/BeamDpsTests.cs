using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="BeamDps"/> and <see cref="BeamCircuit"/>, the pricing of scripted beam
    /// weapons (FAC-478).
    ///
    /// <para>The characteristic failure is a beam weapon priced from the placeholder round its
    /// vanilla gun fires — AnEWS's carries <c>ProjectileMassDamage 1</c>, so a laser that deals 160
    /// per damage tick scored about 6 DPS, and no census line said so. The fixtures are AnEWS's own
    /// beam definitions (<c>3352658929</c>, <c>ANPaL/BeamSystemConfig/Beams_*.cs</c>), read
    /// 2026-10-12.</para>
    /// </summary>
    public class BeamDpsTests
    {
        // Beams_LaserBlocks.cs, LaserBeam: Damage 160, BeamLifeTime 20, DamageInterval 1, DamageTicks (0, 12).
        private const float LaserDamage = 160f;
        private const int LaserTicks = 11;

        // ---- damage ticks ---------------------------------------------------------------------

        [Fact]
        public void ALaserBeamDamagesOnEveryTickOfItsWindow()
        {
            // Ticks 1..11: the window is open on both ends and t = 0 never damages.
            Assert.Equal(LaserTicks, BeamDps.DamageTicksPerBeam(20f, 1, 0, 12));
        }

        [Fact]
        public void AnIonBeamDamagesOnceThoughItLivesAHundredTicks()
        {
            // Beams_IonCannon.cs: lifetime 100, interval 1, DamageTicks (0, 2) -- only t = 1.
            Assert.Equal(1, BeamDps.DamageTicksPerBeam(100f, 1, 0, 2));
        }

        [Fact]
        public void AnOpenEndedWindowIsBoundedByTheLifetime()
        {
            // Beams_FissionRailgun.cs: lifetime 800, interval 30, DamageTicks (0, int.MaxValue) -> 30..780.
            Assert.Equal(26, BeamDps.DamageTicksPerBeam(800f, 30, 0, int.MaxValue));
        }

        [Fact]
        public void TheWindowEndIsExclusive()
        {
            // Beams_DimensionSlicer.cs: lifetime 710, interval 30, DamageTicks (0, 710) -> 30..690.
            Assert.Equal(23, BeamDps.DamageTicksPerBeam(710f, 30, 0, 710));
        }

        [Fact]
        public void ABeamWithNoLifetimeNeverDamages()
        {
            Assert.Equal(0, BeamDps.DamageTicksPerBeam(0f, 1, 0, int.MaxValue));
        }

        [Fact]
        public void AStationaryBeamLandsEveryTickOfItsWindow()
        {
            // Beams_PositronCannon.cs: 72,000 on ticks 1..9, BeamForwardMovementSpeed 0.
            int window = BeamDps.DamageTicksPerBeam(160f, 1, 0, 10);
            Assert.Equal(9, window);
            Assert.Equal(9, BeamDps.LandingTicksPerBeam(window, 1, 7000f, 0f));
        }

        [Fact]
        public void AMovingTrailLandsOnlyWhileItPassesAPoint()
        {
            // Beams_FissionRailgun.cs: a 50 m trail at 100 m/s covers a point for 30 ticks, one
            // damage interval -- not the 26 ticks of its 800-tick life.
            Assert.Equal(1, BeamDps.LandingTicksPerBeam(26, 30, 50f, 100f));
        }

        [Fact]
        public void ASlowTrailLandsAsManyTicksAsItDwells()
        {
            // 50 m at 10 m/s dwells 300 ticks: ten 30-tick intervals.
            Assert.Equal(10, BeamDps.LandingTicksPerBeam(26, 30, 50f, 10f));
        }

        [Fact]
        public void APassingBeamAlwaysLandsAtLeastOnce()
        {
            Assert.Equal(1, BeamDps.LandingTicksPerBeam(26, 30, 1f, 1000f));
            Assert.Equal(0, BeamDps.LandingTicksPerBeam(0, 30, 50f, 100f));
        }

        // ---- beams per second -----------------------------------------------------------------

        [Fact]
        public void EachVanillaShotOpensOneWindowOfAtLeastOneBeam()
        {
            // Laser: RateOfFire 360/min over a 3-tick window is 0.3 beams, fired on the ceiling.
            Assert.Equal(6f, BeamDps.BeamsPerSecond(6f, 360f, 3, 1, 1), 3);
        }

        [Fact]
        public void ShotsArrivingFasterThanTheWindowAreAbsorbedByIt()
        {
            // A 30-tick window allows two windows a second, whatever the gun does; 3 beams in each,
            // twice over for FireCount 2.
            Assert.Equal(12f, BeamDps.BeamsPerSecond(10f, 360f, 30, 2, 1), 3);
        }

        [Fact]
        public void ALongWindowFiresTheBeamRateNotTheShotRate()
        {
            // One shot a second opens a 60-tick window spawning a beam every tick.
            Assert.Equal(60f, BeamDps.BeamsPerSecond(1f, 3600f, 60, 1, 1), 3);
        }

        [Fact]
        public void AGunThatDoesNotFireSpawnsNoBeams()
        {
            Assert.Equal(0f, BeamDps.BeamsPerSecond(0f, 360f, 3, 1, 1));
        }

        // ---- sustained ------------------------------------------------------------------------

        [Fact]
        public void AnUnpoweredBeamThatNeedsNoEnergyFiresAtBaseOutput()
        {
            var none = new BeamEnergy();
            Assert.Equal(LaserDamage * LaserTicks * 6f, BeamDps.Sustained(LaserDamage, LaserTicks, 6f, none), 1);
        }

        [Fact]
        public void ABeamThatNeedsEnergyItHasNoStorageForNeverFires()
        {
            var orphan = new BeamEnergy { RequireEnergyToFire = true };
            Assert.Equal(0f, BeamDps.Sustained(LaserDamage, LaserTicks, 6f, orphan));
        }

        [Fact]
        public void SelfGenerationBuysFullOutputShotsOnTopOfBase()
        {
            // Ion cannon: Damage 130 (1 tick), x50 at full output for 500,000 energy, self-generating
            // 50,000/s -- one full-output shot every ten seconds on top of base fire.
            var ion = new BeamEnergy
            {
                HasStorage = true,
                EnergyForMaxOutput = 480000f,
                ConsumptionAtMinOutput = 500000f,
                ConsumptionAtMaxOutput = 500000f,
                MultiplierAtMaxOutput = 50f,
                MaxStorage = 500000f,
                GuaranteedIncomePerSecond = 50000f,
            };
            float expected = 130f * 1f + 49f * 130f * 0.1f;
            Assert.Equal(expected, BeamDps.Sustained(130f, 1, 1f, ion), 1);
        }

        [Fact]
        public void AHeatStyleMultiplierNeverRaisesTheBase()
        {
            // LaserBeam itself: multiplier 0 at full output -- stored energy is heat, not charge.
            var heat = new BeamEnergy
            {
                HasStorage = true,
                EnergyForMaxOutput = 10f,
                MultiplierAtMaxOutput = 0f,
                MaxStorage = 10f,
                GuaranteedIncomePerSecond = 100f,
            };
            Assert.Equal(LaserDamage * LaserTicks * 6f, BeamDps.Sustained(LaserDamage, LaserTicks, 6f, heat), 1);
        }

        [Fact]
        public void AnUnfedCapacitorWeaponPricesAtZeroAlone()
        {
            Assert.Equal(0f, BeamDps.Sustained(LaserDamage, LaserTicks, 6f, CapacitorLaser()));
        }

        [Fact]
        public void DrainIsPaidBeforeAnyIncomeReachesTheShots()
        {
            // 600/s of feed against a 600/s drain leaves nothing to fire with.
            BeamEnergy fed = CapacitorLaser();
            fed.GuaranteedIncomePerSecond = 600f;
            Assert.Equal(0f, BeamDps.Sustained(LaserDamage, LaserTicks, 6f, fed));
        }

        // ---- circuit --------------------------------------------------------------------------

        [Fact]
        public void AnEmitterWiredIntoACapacitorIsWorthMoreThanFiringDirectly()
        {
            // One Laser Emitter (6 beams/s at 1,760 each = 10,560 direct) feeding one capacitor laser
            // (ratio 0.98, intake 20,000/s, drain 600/s). All of the emitter goes in: 10,349 energy/s,
            // 9,749 after drain, buys 9,749 / 6,100 = 1.598 full-output shots a second at
            // 160 x 11 x 40 = 70,400 each.
            float direct = LaserDamage * LaserTicks * 6f;
            var feeders = new[] { new CircuitFeeder { EnergyType = "Laser", DirectDps = direct } };
            var consumers = new[] { CapacitorConsumer() };
            var feederDps = new float[1];
            var consumerDps = new float[1];

            BeamCircuit.Allocate(feeders, consumers, feederDps, consumerDps);

            float income = direct * 0.98f - 600f;
            float expected = income / 6100f * (LaserDamage * LaserTicks * 40f);
            Assert.Equal(0f, feederDps[0], 1);
            Assert.Equal(expected, consumerDps[0], 0);
            Assert.True(consumerDps[0] > 10f * direct);
        }

        [Fact]
        public void EmitterOutputBeyondWhatConsumersCanTakeStaysDirect()
        {
            // Three emitters (31,680 direct) against one capacitor whose intake stops at 20,000/s:
            // 20,000 / 0.98 = 20,408 damage goes in and the remaining 11,272 is still fired.
            float each = LaserDamage * LaserTicks * 6f;
            var feeders = new[]
            {
                new CircuitFeeder { EnergyType = "Laser", DirectDps = each },
                new CircuitFeeder { EnergyType = "Laser", DirectDps = each },
                new CircuitFeeder { EnergyType = "Laser", DirectDps = each },
            };
            var consumers = new[] { CapacitorConsumer() };
            var feederDps = new float[3];
            var consumerDps = new float[1];

            BeamCircuit.Allocate(feeders, consumers, feederDps, consumerDps);

            float left = 3f * each - 20000f / 0.98f;
            Assert.Equal(left, feederDps[0] + feederDps[1] + feederDps[2], 0);
            Assert.Equal(feederDps[0], feederDps[2], 3);
        }

        [Fact]
        public void AFeederOfAnotherEnergyTypeChargesNothing()
        {
            var feeders = new[] { new CircuitFeeder { EnergyType = "Ion", DirectDps = 10000f } };
            var consumers = new[] { CapacitorConsumer() };
            var feederDps = new float[1];
            var consumerDps = new float[1];

            BeamCircuit.Allocate(feeders, consumers, feederDps, consumerDps);

            Assert.Equal(10000f, feederDps[0]);
            Assert.Equal(0f, consumerDps[0]);
        }

        [Fact]
        public void AConsumerWorthLessThanDirectFireIsNotFed()
        {
            // A consumer that turns 1 energy into 0.5 damage loses to firing the emitter directly.
            CircuitConsumer poor = CapacitorConsumer();
            poor.Energy.MultiplierAtMaxOutput = 1f;
            poor.Energy.ConsumptionAtMinOutput = 3520f;
            poor.Energy.ConsumptionAtMaxOutput = 3520f;
            var feeders = new[] { new CircuitFeeder { EnergyType = "Laser", DirectDps = 10560f } };
            var feederDps = new float[1];
            var consumerDps = new float[1];

            BeamCircuit.Allocate(feeders, new[] { poor }, feederDps, consumerDps);

            Assert.Equal(10560f, feederDps[0]);
            Assert.Equal(0f, consumerDps[0]);
        }

        /// <summary>Beams_LaserBlocks.cs LaserBeamCapacitor with Blocks_LaserBlocks.cs's storage:
        /// needs 10,000 stored to fire at all, x40 at 200,000, drains 600/s, generates nothing.</summary>
        private static BeamEnergy CapacitorLaser()
        {
            return new BeamEnergy
            {
                HasStorage = true,
                RequireEnergyToFire = true,
                EnergyForMinOutput = 10000f,
                ConsumptionAtMinOutput = 150f,
                EnergyForMaxOutput = 200000f,
                ConsumptionAtMaxOutput = 6100f,
                MultiplierAtMaxOutput = 40f,
                MaxStorage = 200000f,
                DrainPerSecond = 600f,
            };
        }

        private static CircuitConsumer CapacitorConsumer()
        {
            return new CircuitConsumer
            {
                EnergyType = "Laser",
                DamagePerTick = LaserDamage,
                DamageTicksPerBeam = LaserTicks,
                BeamsPerSecond = 6f,
                Energy = CapacitorLaser(),
                EnergyPerDamage = 0.98f,
                IntakePerSecond = 20000f,
            };
        }
    }
}
