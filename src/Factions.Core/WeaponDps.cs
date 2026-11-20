using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The damage-per-second arithmetic behind <see cref="WeaponBand"/>, in Core so the host's
    /// SE-facing scanner and the tests run the identical formula. The host layer resolves the
    /// weapon/ammo definition chain and passes the raw numbers in; nothing here knows about SE.
    ///
    /// <para><b>A shell carries its damage in one of two places.</b> Explosive ammo declares
    /// <c>MissileExplosionDamage</c>, which SE exposes through
    /// <c>MyAmmoDefinition.GetDamageForMechanicalObjects()</c>. Kinetic ammo declares a
    /// <c>MissileHealthPool</c> instead and leaves the explosion damage at zero: SE spends that pool
    /// against each block the round penetrates (<c>MyMissile.m_healthPool</c> →
    /// <c>MySlimBlock.DoMaximumDamage</c>) and detonates once it is empty. Both are damage the round
    /// really delivers, so both count.</para>
    ///
    /// <para><b>DPS uses a reload duty cycle, not a flat rate.</b> <c>ShotsInBurst</c> is the
    /// magazine size fired one round at a time before the weapon pauses for the reload and refills
    /// — not simultaneous rounds per rate-of-fire tick.</para>
    /// </summary>
    public static class WeaponDps
    {
        /// <summary>Damage one round delivers to blocks, summing the explosive and kinetic
        /// channels because a penetrating round spends its pool and <i>then</i> detonates. In
        /// vanilla content exactly one of the two is ever nonzero.</summary>
        public static float DamagePerShot(float mechanicalDamage, float missileHealthPool, float damageMultiplier)
        {
            float perShot = 0f;
            if (mechanicalDamage > 0f) perShot += mechanicalDamage;
            if (missileHealthPool > 0f) perShot += missileHealthPool;
            return perShot * damageMultiplier;
        }

        /// <summary>Damage a round delivers through the children it spawns on detonation.
        ///
        /// <para>A shell that bursts into shrapnel is not modelled by any field on its own ammo
        /// definition: the children are separate ammo entities carrying their own damage, and the
        /// count lives in the weapon framework that spawns them. This is real damage the round
        /// delivers, not a hit-probability adjustment — the count is averaged over the framework's
        /// declared min/max because that is the round's expected payload, and
        /// <paramref name="fraction"/> is an operator balance lever, defaulting to no adjustment.</para>
        ///
        /// <para>What this deliberately does <b>not</b> model is how much of a burst connects. Shrapnel
        /// spreads, so the realised share is below one and is unmeasured; that belongs with every other
        /// hit-rate question, not folded into a damage figure.</para></summary>
        public static float ShrapnelDamage(float childDamagePerShot, int minCount, int maxCount, float fraction)
        {
            if (childDamagePerShot <= 0f || fraction <= 0f) return 0f;

            int lo = minCount < 0 ? 0 : minCount;
            int hi = maxCount < lo ? lo : maxCount;
            if (hi <= 0) return 0f;

            float averageCount = (lo + hi) / 2f;
            return childDamagePerShot * averageCount * fraction;
        }

        /// <summary>Sustained damage per second, scaled by the burst/reload duty cycle. A
        /// non-positive <paramref name="rateOfFire"/> yields zero. <paramref name="shotsInBurst"/>
        /// at or below zero (or no configured reload) means the weapon fires continuously.</summary>
        public static float Sustained(float damagePerShot, int rateOfFire, int shotsInBurst, int reloadTimeMs)
        {
            if (damagePerShot <= 0f || rateOfFire <= 0) return 0f;

            float dpsWhileFiring = damagePerShot * (rateOfFire / 60f);
            if (shotsInBurst <= 0 || reloadTimeMs <= 0) return dpsWhileFiring;

            float burstDurationMs = shotsInBurst * (60000f / rateOfFire);
            return dpsWhileFiring * (burstDurationMs / (burstDurationMs + reloadTimeMs));
        }

        /// <summary>Which of a weapon's magazines it is priced on: the first, in declaration order,
        /// that <paramref name="resolves"/> accepts. -1 when none does.
        ///
        /// <para>A weapon fires one magazine at a time, so its damage and range describe one ammo
        /// choice, never a sum across the magazines it could accept (FAC-223). The first is the
        /// choice SE makes: <c>MyGunBase</c> starts a weapon on <c>AmmoMagazinesId[0]</c> and walks
        /// the list in order only when the current one is out of stock. It is also the magazine the
        /// munition model draws and loads, so a hull is priced on the ammo it actually carries —
        /// which is why this is not the best magazine the weapon could take.</para></summary>
        public static int DefaultLoadIndex<T>(IList<T> magazines, Func<T, bool> resolves)
        {
            if (magazines == null || resolves == null) return -1;
            for (int i = 0; i < magazines.Count; i++)
                if (resolves(magazines[i])) return i;
            return -1;
        }
    }
}
