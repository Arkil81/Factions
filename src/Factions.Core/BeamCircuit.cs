using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A beam weapon that fires freely and whose beam charges energy of
    /// <see cref="EnergyType"/> into whatever storage block it hits. Its <see cref="DirectDps"/> is
    /// what it deals firing at an enemy instead.</summary>
    public struct CircuitFeeder
    {
        public string EnergyType;
        public float DirectDps;
    }

    /// <summary>A beam weapon that stores energy of <see cref="EnergyType"/> and spends it on its
    /// shots. Everything <see cref="BeamDps.Sustained"/> needs to price it, plus how it takes energy
    /// in from another beam.</summary>
    public struct CircuitConsumer
    {
        public string EnergyType;

        public float DamagePerTick;
        public int DamageTicksPerBeam;
        public float BeamsPerSecond;

        /// <summary>Its energy system with only its own self-generation as income; the circuit
        /// adds the rest.</summary>
        public BeamEnergy Energy;

        /// <summary>Energy gained per point of beam damage absorbed (<c>DamageToEnergyRatio</c>).</summary>
        public float EnergyPerDamage;

        /// <summary>The most energy per second its storage takes in from outside
        /// (<c>EnergyChargeSpeed</c>), however much arrives.</summary>
        public float IntakePerSecond;
    }

    /// <summary>
    /// Prices the beam weapons of one hull as a circuit: free-firing emitters charge the hull's
    /// energy-storing weapons, which spend that energy on stronger shots.
    ///
    /// <para><b>Why a hull and not a block.</b> In this design (the ANPaL framework, as AnEWS uses
    /// it) a storing weapon has no energy source of its own. It is charged by another beam hitting
    /// it, so its output is a property of how the hull is wired, not of the block. Priced alone it
    /// is worth nothing, and its emitters are worth only their direct damage. Building both on one
    /// hull has one purpose, so a hull carrying both is priced as wired.</para>
    ///
    /// <para><b>The allocation.</b> A point of emitter damage diverted into a consumer becomes
    /// <see cref="CircuitConsumer.EnergyPerDamage"/> energy, worth whatever that consumer's damage
    /// per energy is. It is diverted only where that beats firing it directly, best consumer first,
    /// and no consumer takes more than its intake or its fire rate can use. Emitter damage left over
    /// stays direct.</para>
    ///
    /// <para><b>Not modelled:</b> geometry. Whether every emitter can reach a consumer, and the
    /// beam lost to reflectors on the way, are wiring details a definition cannot see.</para>
    /// </summary>
    public static class BeamCircuit
    {
        /// <summary>Fills <paramref name="feederDps"/> with what each feeder still deals directly
        /// and <paramref name="consumerDps"/> with each consumer's total sustained damage, its own
        /// self-generation included.</summary>
        public static void Allocate(IList<CircuitFeeder> feeders, IList<CircuitConsumer> consumers,
                                    float[] feederDps, float[] consumerDps)
        {
            if (feeders == null || consumers == null || feederDps == null || consumerDps == null)
                throw new ArgumentNullException();

            for (int f = 0; f < feeders.Count; f++)
                feederDps[f] = feeders[f].DirectDps > 0f ? feeders[f].DirectDps : 0f;

            var standalone = new float[consumers.Count];
            for (int c = 0; c < consumers.Count; c++)
            {
                standalone[c] = Price(consumers[c], 0f);
                consumerDps[c] = standalone[c];
            }

            // Energy types are independent circuits.
            var types = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < consumers.Count; c++)
                if (!string.IsNullOrEmpty(consumers[c].EnergyType)) types.Add(consumers[c].EnergyType);

            foreach (string type in types)
            {
                float available = 0f;
                for (int f = 0; f < feeders.Count; f++)
                    if (string.Equals(feeders[f].EnergyType, type, StringComparison.Ordinal)) available += feederDps[f];
                if (available <= 0f) continue;
                float total = available;

                // Best value per point of diverted damage first. The value is measured at the
                // consumer's full intake, so a consumer that saturates on a trickle (a threshold to
                // reach, then nothing to spend) ranks first and takes only that trickle.
                var order = new List<KeyValuePair<int, float>>();
                for (int c = 0; c < consumers.Count; c++)
                {
                    if (!string.Equals(consumers[c].EnergyType, type, StringComparison.Ordinal)) continue;
                    float cap = UsefulIntake(consumers[c]);
                    if (cap <= 0f || consumers[c].EnergyPerDamage <= 0f) continue;
                    float gain = Price(consumers[c], cap) - standalone[c];
                    float damageSpent = cap / consumers[c].EnergyPerDamage;
                    if (gain <= damageSpent) continue;     // firing directly is worth more
                    order.Add(new KeyValuePair<int, float>(c, gain / damageSpent));
                }
                order.Sort((a, b) => b.Value.CompareTo(a.Value));

                foreach (KeyValuePair<int, float> pick in order)
                {
                    if (available <= 0f) break;
                    CircuitConsumer consumer = consumers[pick.Key];
                    float wantedDamage = UsefulIntake(consumer) / consumer.EnergyPerDamage;
                    float spent = Math.Min(available, wantedDamage);
                    consumerDps[pick.Key] = Price(consumer, spent * consumer.EnergyPerDamage);
                    available -= spent;
                }

                // Whatever was not diverted is still fired at the enemy, shared out in proportion.
                float keep = available / total;
                for (int f = 0; f < feeders.Count; f++)
                    if (string.Equals(feeders[f].EnergyType, type, StringComparison.Ordinal)) feederDps[f] *= keep;
            }
        }

        /// <summary>The consumer's sustained damage with <paramref name="externalIncome"/> on top of
        /// its own self-generation.</summary>
        private static float Price(CircuitConsumer consumer, float externalIncome)
        {
            BeamEnergy energy = consumer.Energy;
            energy.GuaranteedIncomePerSecond = Math.Max(0f, energy.GuaranteedIncomePerSecond) + Math.Max(0f, externalIncome);
            return BeamDps.Sustained(consumer.DamagePerTick, consumer.DamageTicksPerBeam, consumer.BeamsPerSecond, energy);
        }

        /// <summary>The most outside energy per second the consumer can turn into damage: what its
        /// fire rate spends at the costlier output, net of its own generation, and never above its
        /// storage's intake. A consumer whose shots cost nothing still needs a charge to reach its
        /// threshold, so it is offered a trickle rather than nothing.</summary>
        private static float UsefulIntake(CircuitConsumer consumer)
        {
            BeamEnergy e = consumer.Energy;
            float spend = consumer.BeamsPerSecond * Math.Max(e.ConsumptionAtMinOutput, e.ConsumptionAtMaxOutput);
            float own = Math.Max(0f, e.GuaranteedIncomePerSecond) - Math.Max(0f, e.DrainPerSecond);
            float need = spend - own;
            if (need <= 0f)
            {
                bool threshold = e.EnergyForMinOutput > 0f || e.EnergyForMaxOutput > 0f || e.MinEnergyForFire > 0f;
                if (!threshold || own > 0f) return 0f;
                need = 1f - Math.Min(0f, own);
            }
            return consumer.IntakePerSecond > 0f ? Math.Min(need, consumer.IntakePerSecond) : need;
        }
    }
}
