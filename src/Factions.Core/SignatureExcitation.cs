using System;

namespace Factions.Core
{
    /// <summary>What a ship is <b>doing</b> that makes it louder. Every member here is wired end to
    /// end — a source is added to this enum when something measures it and something reads it, not
    /// before, so an inert member can never be mistaken for a live one.</summary>
    public enum SignatureSource
    {
        /// <summary>Thrust actually being applied, as <b>absolute force</b> against a fixed reference.
        ///
        /// <para>Force rather than acceleration, deliberately. Acceleration is a utilisation measure —
        /// a 50,000 t hull and a 500 t hull pulling the same g read identically — which erases the very
        /// size signal this model exists to create. A big hull burning gently is already emitting more
        /// than a small one burning hard, and it should read that way.</para></summary>
        Thrust = 0,

        /// <summary>Configured broadcast radius against the antenna's maximum. The one term that is
        /// also a <i>resolution</i> input, so turning it up extends what you hear and what hears you.</summary>
        AntennaPower = 1,

        /// <summary>Fraction of the grid's weapons currently firing.</summary>
        WeaponsFire = 2,

        /// <summary>A jump drive drawing to fill. Sustained while it charges, so a ship winding up to
        /// jump is conspicuous <i>before</i> it goes, which is what makes the wind-up a decision.</summary>
        JumpCharge = 3,

        /// <summary>The jump itself. A very bright, very short flash: you see it only if you happen to
        /// be looking when it happens, and you cannot follow it afterwards.</summary>
        JumpFlash = 4,

        /// <summary>Total electrical draw across the grid, absolute against a fixed reference — the
        /// same size-correlated reading as <see cref="Thrust"/>, and for the same reason. Draw tracks
        /// hull volume, so a large ship is fully lit while merely running.</summary>
        PowerDraw = 5,

        /// <summary>In FSD supercruise. Sustained and scaled by speed, so cruising slowly is quieter
        /// than cruising fast — a throttle-versus-stealth choice, and the counterpart to the antenna's
        /// reach-versus-anonymity one.</summary>
        Supercruise = 6,

        /// <summary>Entering supercruise. The transition, not the state.</summary>
        SupercruiseEntry = 7,

        /// <summary>Configured beacon radius against the beacon's maximum.
        ///
        /// <para>Separate from <see cref="AntennaPower"/> rather than folded into it, and weighted
        /// higher. A beacon exists to be seen — it declares a reach four times an antenna's (200 km
        /// against 50) and has no receive side to trade against — so charging the same for both would
        /// teach players the wrong thing about which block is risky.</para></summary>
        BeaconPower = 8,
    }

    /// <summary>
    /// One object's activity excitation: a multiplier over its base signature, decaying back toward
    /// 1.0 at a rate chosen <b>per source</b>.
    ///
    /// <para>Per-term decay is not a tuning convenience. The sources are different kinds of thing: an
    /// antenna's broadcast radius is a steady-state <i>setting</i> and should have no tail at all,
    /// while a burst of fire is an <i>event</i> that must linger or "you were seen shooting" carries no
    /// consequence. One shared constant could express neither.</para>
    ///
    /// <para><b>Nothing here is persisted.</b> It is derived state on a seconds timescale; a reloaded
    /// world starts every object at rest, which is the same rule the detected sets follow.</para>
    /// </summary>
    public sealed class SignatureExcitation
    {
        private const int SourceCount = 9;

        private readonly double[] _intensity = new double[SourceCount];
        private readonly DateTime[] _observedUtc = new DateTime[SourceCount];

        /// <summary>Records a source at an intensity of 0..1, clamped. Observing is idempotent within
        /// a tick and always replaces rather than accumulates — a source describes a current state,
        /// not a running total.</summary>
        public void Observe(SignatureSource source, double intensity, DateTime nowUtc)
        {
            int i = (int)source;
            if (i < 0 || i >= SourceCount) return;
            _intensity[i] = intensity < 0 ? 0 : (intensity > 1 ? 1 : intensity);
            _observedUtc[i] = nowUtc;
        }

        /// <summary>The multiplier to hand <see cref="Signature.ForShip"/> as its state term. 1.0 when
        /// nothing has been observed or everything has decayed away, so an object at rest is exactly
        /// its base signature and this term can never make anything quieter than its own hull.</summary>
        public double Multiplier(FactionTuning tuning, DateTime nowUtc)
        {
            if (tuning == null) return 1.0;
            double sum = 0;
            for (int i = 0; i < SourceCount; i++)
            {
                if (_observedUtc[i] == default(DateTime)) continue;
                double weight = WeightOf(tuning, (SignatureSource)i);
                if (weight <= 0) continue;
                double decayed = Decayed(_intensity[i], _observedUtc[i], nowUtc,
                                         DecaySecondsOf(tuning, (SignatureSource)i));
                if (decayed > 0) sum += weight * decayed;
            }
            return 1.0 + sum;
        }

        public void Clear()
        {
            for (int i = 0; i < SourceCount; i++)
            {
                _intensity[i] = 0;
                _observedUtc[i] = default(DateTime);
            }
        }

        /// <summary>Linear fade to zero over the source's own window.
        ///
        /// <para><c>decaySeconds &lt;= 0</c> means <b>no tail</b>: the value counts only on the tick it
        /// was observed. That is the "read live" case the antenna wants — turn the antenna down and be
        /// quiet on the next sample, with nothing lingering. It is expressed as an immediate expiry
        /// rather than as "never decays", because a value that never decays and stops being re-observed
        /// would stick at full strength forever.</para></summary>
        private static double Decayed(double intensity, DateTime observedUtc, DateTime nowUtc, double decaySeconds)
        {
            if (intensity <= 0) return 0;
            double elapsed = (nowUtc - observedUtc).TotalSeconds;
            if (elapsed <= 0) return intensity;
            if (decaySeconds <= 0) return 0;
            if (elapsed >= decaySeconds) return 0;
            return intensity * (1.0 - elapsed / decaySeconds);
        }

        private static double WeightOf(FactionTuning t, SignatureSource s)
        {
            switch (s)
            {
                case SignatureSource.Thrust:       return t.SignatureWeightThrust;
                case SignatureSource.AntennaPower: return t.SignatureWeightAntennaPower;
                case SignatureSource.BeaconPower:  return t.SignatureWeightBeaconPower;
                case SignatureSource.WeaponsFire:  return t.SignatureWeightWeaponsFire;
                case SignatureSource.JumpCharge:   return t.SignatureWeightJumpCharge;
                case SignatureSource.JumpFlash:    return t.SignatureWeightJumpFlash;
                case SignatureSource.PowerDraw:    return t.SignatureWeightPowerDraw;
                case SignatureSource.Supercruise:  return t.SignatureWeightSupercruise;
                case SignatureSource.SupercruiseEntry: return t.SignatureWeightSupercruiseEntry;
                default: return 0;
            }
        }

        private static double DecaySecondsOf(FactionTuning t, SignatureSource s)
        {
            switch (s)
            {
                case SignatureSource.Thrust:       return t.SignatureDecayThrustSeconds;
                case SignatureSource.AntennaPower: return t.SignatureDecayAntennaPowerSeconds;
                case SignatureSource.BeaconPower:  return t.SignatureDecayBeaconPowerSeconds;
                case SignatureSource.WeaponsFire:  return t.SignatureDecayWeaponsFireSeconds;
                case SignatureSource.JumpCharge:   return t.SignatureDecayJumpChargeSeconds;
                case SignatureSource.JumpFlash:    return t.SignatureDecayJumpFlashSeconds;
                case SignatureSource.PowerDraw:    return t.SignatureDecayPowerDrawSeconds;
                case SignatureSource.Supercruise:  return t.SignatureDecaySupercruiseSeconds;
                case SignatureSource.SupercruiseEntry: return t.SignatureDecaySupercruiseEntrySeconds;
                default: return 0;
            }
        }
    }
}
