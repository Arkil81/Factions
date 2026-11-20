using System;

namespace Factions.Core
{
    /// <summary>What a beam's energy system lets it do: the output range it can reach, what each beam
    /// costs, and what the weapon can count on earning back. Read from a beam framework's definitions
    /// by the host; every field is a plain number so the arithmetic below needs no SE.</summary>
    public struct BeamEnergy
    {
        /// <summary>False when the beam names no energy type, or one its block does not store. Such
        /// a beam fires at base output, or not at all when <see cref="RequireEnergyToFire"/>.</summary>
        public bool HasStorage;

        /// <summary>A beam that requires energy refuses to fire below its requirement; one that does
        /// not fires anyway, at whatever output its stored energy gives.</summary>
        public bool RequireEnergyToFire;

        public float MinEnergyForFire;
        public float EnergyForMinOutput;
        public float EnergyForMaxOutput;
        public float ConsumptionAtMinOutput;
        public float ConsumptionAtMaxOutput;

        /// <summary>The damage multiplier at full output. Below 1 is a heat-style mechanic, where
        /// stored energy makes the beam weaker, and full output is then never worth firing.</summary>
        public float MultiplierAtMaxOutput;

        public float MaxStorage;

        /// <summary>Energy per second the weapon is <b>guaranteed</b> to earn: its own
        /// self-generation. Energy fed in from outside, such as another beam charging it, depends on
        /// what a player builds around the block and is deliberately not counted.</summary>
        public float GuaranteedIncomePerSecond;

        /// <summary>Energy per second the storage loses on its own (a positive
        /// <c>EnergySelfConsumptionSpeed</c>). Income has to beat it before any is left to fire.</summary>
        public float DrainPerSecond;
    }

    /// <summary>
    /// Sustained damage per second of a scripted beam weapon, from the numbers its framework
    /// declares. The host reads the definitions (<c>AnpalBeamBridge</c>); this owns the arithmetic so
    /// the tests and the scan run the identical formula.
    ///
    /// <para><b>Mechanics, from the ANPaL framework's own source.</b></para>
    /// <list type="bullet">
    /// <item><description>A vanilla shot opens a firing window of <c>FireTime</c> ticks, and a new
    /// window opens only once the last one has closed.</description></item>
    /// <item><description>A window spawns <c>RateOfFire / 3600</c> beams per tick, rounded up, so at
    /// least one, each × <c>FireCount</c> × <c>BurstCount</c>.</description></item>
    /// <item><description>A live beam deals its damage on every tick t with
    /// <c>DamageTicks.X</c> &lt; t &lt; <c>DamageTicks.Y</c>, 0 &lt; t ≤ <c>BeamLifeTime</c>, and t a
    /// multiple of <c>DamageInterval</c>.</description></item>
    /// <item><description>Stored energy scales the damage linearly between base and full output, and
    /// each beam spends the matching consumption.</description></item>
    /// </list>
    ///
    /// <para><b>Deliberately not modelled:</b> damage past the first impact (piercing, reflection —
    /// the framework's damage-pool cap binds only those), distance falloff, and whether the beam
    /// hits. Each would need the target, which a price has not got.</para>
    /// </summary>
    public static class BeamDps
    {
        /// <summary>Ticks on which one beam deals damage over its life.
        ///
        /// <para>Counted arithmetically rather than by walking ticks: an open-ended damage window is
        /// declared as <c>int.MaxValue</c>, and a lifetime can be long.</para></summary>
        public static int DamageTicksPerBeam(float lifeTimeTicks, int damageInterval, int damageFromTick, int damageToTick)
        {
            long life = (long)Math.Floor(lifeTimeTicks);
            if (life < 1) return 0;

            // The framework clamps the interval to [1, life + 1].
            long interval = damageInterval < 1 ? 1 : damageInterval;
            if (interval > life + 1) interval = life + 1;

            long lo = Math.Max(1L, (long)damageFromTick + 1);
            long hi = Math.Min(life, (long)damageToTick - 1);
            if (hi < lo) return 0;

            long count = hi / interval - (lo - 1) / interval;
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }

        /// <summary>Damage ticks one beam lands on a target it passes, for a beam that travels.
        ///
        /// <para>A stationary beam sits on its target for its whole damage window, so every tick of
        /// <see cref="DamageTicksPerBeam"/> counts. A beam that moves forward
        /// (<c>BeamForwardMovementSpeed</c>) — a damage trail sliding down the line of fire — covers any
        /// one point only for its own length over its speed. Only the ticks inside that window can
        /// land, and at least one does. Counting the point rather than a guessed hull length keeps
        /// the price from ever exceeding what the definition guarantees.</para></summary>
        public static int LandingTicksPerBeam(int damageTicksPerBeam, int damageInterval, float lengthM, float forwardSpeedMps)
        {
            if (damageTicksPerBeam <= 0) return 0;
            if (forwardSpeedMps <= 0f || lengthM <= 0f) return damageTicksPerBeam;

            double dwellTicks = 60.0 * lengthM / forwardSpeedMps;
            int interval = damageInterval < 1 ? 1 : damageInterval;
            int landing = (int)Math.Ceiling(dwellTicks / interval - 1e-6);
            if (landing < 1) landing = 1;
            return Math.Min(damageTicksPerBeam, landing);
        }

        /// <summary>Beams per second while the vanilla gun keeps firing at
        /// <paramref name="vanillaShotsPerSecond"/>: one window per shot, at most one window per
        /// <paramref name="fireTimeTicks"/>.</summary>
        public static float BeamsPerSecond(float vanillaShotsPerSecond, float rateOfFirePerMinute,
                                           int fireTimeTicks, int fireCount, int burstCount)
        {
            if (vanillaShotsPerSecond <= 0f || rateOfFirePerMinute <= 0f) return 0f;
            if (fireCount <= 0 || burstCount <= 0) return 0f;

            int window = fireTimeTicks < 1 ? 1 : fireTimeTicks;
            float windowsPerSecond = Math.Min(vanillaShotsPerSecond, 60f / window);

            // Fractional shots accumulate across the window and are fired on the ceiling, so the
            // first tick always fires.
            double perWindow = Math.Ceiling(window * (double)rateOfFirePerMinute / 3600.0 - 1e-6);
            if (perWindow < 1.0) perWindow = 1.0;

            return windowsPerSecond * (float)perWindow * fireCount * burstCount;
        }

        /// <summary>Sustained damage per second of one beam type.
        ///
        /// <para>Of the two output extremes it takes the better one; a mix of the two never beats
        /// both, because damage and consumption both scale linearly with output.</para>
        /// <list type="bullet">
        /// <item><description><b>Base output</b> (multiplier 1) at the full beam rate.</description></item>
        /// <item><description><b>Full output</b> where it is reachable, at the rate guaranteed income
        /// can pay for.</description></item>
        /// </list>
        /// <para>A beam that does not require energy fires at base output regardless, and full-output
        /// shots are added on top. A beam that requires energy fires only what its income
        /// sustains.</para></summary>
        public static float Sustained(float damagePerTick, int damageTicksPerBeam, float beamsPerSecond, BeamEnergy energy)
        {
            if (damagePerTick <= 0f || damageTicksPerBeam <= 0 || beamsPerSecond <= 0f) return 0f;
            float perBeam = damagePerTick * damageTicksPerBeam;

            if (!energy.HasStorage)
                return energy.RequireEnergyToFire ? 0f : perBeam * beamsPerSecond;

            float income = Math.Max(0f, energy.GuaranteedIncomePerSecond) - Math.Max(0f, energy.DrainPerSecond);
            if (income < 0f) income = 0f;
            float maxMultiplier = energy.MultiplierAtMaxOutput;
            bool maxReachable = energy.EnergyForMaxOutput <= energy.MaxStorage
                                && energy.MinEnergyForFire <= energy.MaxStorage
                                && (income > 0f || (energy.EnergyForMaxOutput <= 0f && energy.MinEnergyForFire <= 0f));
            float maxRate = maxReachable
                ? RateIncomePays(beamsPerSecond, energy.ConsumptionAtMaxOutput, income)
                : 0f;

            if (!energy.RequireEnergyToFire)
            {
                float extra = maxMultiplier > 1f ? (maxMultiplier - 1f) * perBeam * maxRate : 0f;
                return perBeam * beamsPerSecond + extra;
            }

            bool baseReachable = energy.EnergyForMinOutput <= energy.MaxStorage
                                 && energy.MinEnergyForFire <= energy.MaxStorage
                                 && (income > 0f || (energy.EnergyForMinOutput <= 0f && energy.MinEnergyForFire <= 0f));
            float baseRate = baseReachable
                ? RateIncomePays(beamsPerSecond, energy.ConsumptionAtMinOutput, income)
                : 0f;

            float atBase = perBeam * baseRate;
            float atMax = perBeam * maxMultiplier * maxRate;
            return Math.Max(atBase, atMax);
        }

        /// <summary>Beams per second that <paramref name="incomePerSecond"/> pays for at
        /// <paramref name="consumptionPerBeam"/>, never above the rate the gun fires at.</summary>
        private static float RateIncomePays(float beamsPerSecond, float consumptionPerBeam, float incomePerSecond)
        {
            if (consumptionPerBeam <= 0f) return beamsPerSecond;
            return Math.Min(beamsPerSecond, incomePerSecond / consumptionPerBeam);
        }
    }
}
