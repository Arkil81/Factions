using System;

namespace Factions.Core
{
    /// <summary>
    /// What happens to a pilot's radio call on its way to a listener (FAC-419 slice 5,
    /// docs/atc-voice.md). Real phenomena, computed from positions and velocities the simulation
    /// already has; the client renders the result. Constants are the operator's, tuned by ear on the
    /// radio physics bench (2026-09-29): a sideband link at 450 MHz.
    ///
    /// <list type="bullet">
    /// <item><b>Path loss</b> -- received power falls with the square of distance, so the signal-to-
    /// noise ratio drops 6 dB per doubling (<see cref="SnrDb"/>).</item>
    /// <item><b>Doppler</b> -- on a sideband link every voice frequency moves by carrier x radial
    /// speed / c; approaching raises it (<see cref="DopplerHz"/>).</item>
    /// <item><b>Multipath flutter</b> -- a moving end sweeps through the reflections' interference
    /// pattern at speed / wavelength times a second (<see cref="FlutterHz"/>).</item>
    /// <item><b>Planet shadow</b> -- knife-edge diffraction over the limb, ITU-R P.526
    /// (<see cref="KnifeEdgeLossDb"/>, <see cref="SensorOcclusion.WorstDiffractionNu"/>).</item>
    /// </list>
    ///
    /// <para><b>Reach</b> is the listener's set (<see cref="RadioReceiver"/>): a spacesuit hears
    /// <see cref="FactionTuning.AtcSuitRadioRangeKm"/>; a ship's controls with no antenna
    /// <see cref="FactionTuning.AtcRadioRangeKm"/>; antennas hear what the ship's scanner sees
    /// (<see cref="StationRangeKm"/>, <see cref="PilotRangeKm"/>). A longer reach is a better
    /// receiver, not a louder world, so it also lifts the signal: <see cref="AntennaGainDb"/>. A dish or a
    /// parabolic antenna adds reach toward whatever it points at (<see cref="DirectionalGainDb"/>).</para>
    /// </summary>
    public static class RadioPath
    {
        public const double LightSpeedMs = 299792458.0;

        public static double WavelengthM(double carrierMHz)
        {
            return carrierMHz > 0 ? LightSpeedMs / (carrierMHz * 1e6) : double.PositiveInfinity;
        }

        /// <summary>Frequency offset of the received voice. <paramref name="radialSpeedMs"/> is the
        /// rate the distance changes: positive receding, negative approaching (which raises it).</summary>
        public static double DopplerHz(double carrierMHz, double radialSpeedMs)
        {
            return -carrierMHz * 1e6 * radialSpeedMs / LightSpeedMs;
        }

        /// <summary>How many times a second the multipath pattern is swept at this speed.</summary>
        public static double FlutterHz(double carrierMHz, double speedMs)
        {
            return Math.Abs(speedMs) / WavelengthM(carrierMHz);
        }

        /// <summary>Inverse-square path loss, referenced to 1 km, less any diffraction loss. Distance
        /// is floored at 50 m: the formula is far-field and a closer pilot is simply loud.</summary>
        public static double SnrDb(double snrAt1kmDb, double distanceKm, double extraLossDb)
        {
            return snrAt1kmDb - 20.0 * Math.Log10(Math.Max(distanceKm, 0.05)) - extraLossDb;
        }

        /// <summary>ITU-R P.526 single knife-edge diffraction loss for parameter ν: 0 dB below
        /// ν = -0.78 (clear), about 6 dB at grazing (ν = 0), rising steeply behind the edge.</summary>
        public static double KnifeEdgeLossDb(double nu)
        {
            if (nu <= -0.78) return 0.0;
            double a = nu - 0.1;
            return 6.9 + 20.0 * Math.Log10(Math.Sqrt(a * a + 1.0) + a);
        }

        /// <summary>The diffracted path rendered as a muffle: 3,400 Hz (the radio band) with no loss,
        /// falling log-linearly to <paramref name="floorHz"/> at <paramref name="floorAtLossDb"/>.</summary>
        public static double MuffleCutoffHz(double lossDb, double floorHz, double floorAtLossDb)
        {
            const double open = 3400.0;
            if (lossDb <= 0 || floorAtLossDb <= 0) return open;
            double t = Math.Min(lossDb / floorAtLossDb, 1.0);
            return Math.Exp(Math.Log(open) + (Math.Log(Math.Max(floorHz, 50)) - Math.Log(open)) * t);
        }

        /// <summary>ν for a line passing a sphere: h is how far the sphere's surface stands above the
        /// line at its closest point (positive = the line cuts into it), d1 and d2 the distances from
        /// each end to that point. Only an obstruction BETWEEN the ends counts: when the closest point
        /// is an end itself, the body is behind one of them, not in the way.</summary>
        public static double SphereNu(Vec3 aKm, Vec3 bKm, Vec3 centerKm, double radiusKm, double wavelengthM)
        {
            double abx = bKm.X - aKm.X, aby = bKm.Y - aKm.Y, abz = bKm.Z - aKm.Z;
            double len2 = abx * abx + aby * aby + abz * abz;
            if (len2 <= 0 || !(wavelengthM > 0) || double.IsInfinity(wavelengthM)) return double.NegativeInfinity;
            double t = ((centerKm.X - aKm.X) * abx + (centerKm.Y - aKm.Y) * aby + (centerKm.Z - aKm.Z) * abz) / len2;
            if (t <= 0 || t >= 1) return double.NegativeInfinity;
            var p = new Vec3(aKm.X + abx * t, aKm.Y + aby * t, aKm.Z + abz * t);
            double hM = (radiusKm - p.DistanceTo(centerKm)) * 1000.0;
            double d1 = aKm.DistanceTo(p) * 1000.0, d2 = p.DistanceTo(bKm) * 1000.0;
            if (d1 <= 0 || d2 <= 0) return double.NegativeInfinity;
            return hM * Math.Sqrt(2.0 * (d1 + d2) / (wavelengthM * d1 * d2));
        }

        /// <summary>How far this listener hears a station's controller. A suit hears its own short
        /// range whatever the ship carries; a seat with no antenna the ship default; antennas the
        /// scanner's node range -- the sensor strength, which rises with each antenna as a root sum of
        /// squares -- floored at the ship default so fitting an antenna never shortens it.</summary>
        public static double StationRangeKm(RadioReceiver r, FactionTuning t)
        {
            if (t == null) return 0;
            if (!r.Seated) return Math.Max(0, t.AtcSuitRadioRangeKm);
            double ship = Math.Max(0, t.AtcRadioRangeKm);
            return r.AntennaCount > 0 ? Math.Max(ship, r.StrengthKm) : ship;
        }

        /// <summary>How far this listener hears a pilot of this signature. With antennas it is the
        /// scanner's fleet range (<see cref="Sensing.DetectionRangeKm(double,double,double)"/>): a
        /// loud hull, a fight, a fresh departure all carry further, and a hull broadcasting on its own
        /// antenna is heard at least that far. A suit and a bare seat hear their fixed range.</summary>
        public static double PilotRangeKm(RadioReceiver r, double signature, double broadcastKm, FactionTuning t)
        {
            double range = StationRangeKm(r, t);
            if (!r.Seated || r.AntennaCount <= 0) return range;
            double sensed = Sensing.DetectionRangeKm(r.StrengthKm, signature > 0 ? signature : 1.0, broadcastKm);
            return Math.Max(range, sensed);
        }

        /// <summary>The listener's reach toward a station at <paramref name="nodeKm"/>, a dish's gain
        /// that way included.</summary>
        public static double StationRangeKm(RadioReceiver r, FactionTuning t, Vec3 listenerKm, Vec3 nodeKm)
        {
            return StationRangeKm(r, t) * GainFactor(DirectionalGainDb(r, listenerKm, nodeKm, t));
        }

        /// <summary>The listener's reach toward a pilot at <paramref name="pilotKm"/>, a dish's gain
        /// that way included.</summary>
        public static double PilotRangeKm(RadioReceiver r, double signature, double broadcastKm, FactionTuning t,
                                          Vec3 listenerKm, Vec3 pilotKm)
        {
            return PilotRangeKm(r, signature, broadcastKm, t) * GainFactor(DirectionalGainDb(r, listenerKm, pilotKm, t));
        }

        /// <summary>The best gain any of the listener's reflectors has toward a source, dB, never
        /// below 0: one off axis is no worse than the ship's other antennas, which still hear all
        /// round. The pattern is a parabola in dB, <c>peak - 12 (angle / beamwidth)^2</c>: 3 dB down
        /// at half the beamwidth off axis, nothing at <c>beamwidth x sqrt(peak / 12)</c>. Each kind
        /// has its own peak and beamwidth (<see cref="PatternOf"/>).</summary>
        public static double DirectionalGainDb(RadioReceiver r, Vec3 listenerKm, Vec3 sourceKm, FactionTuning t)
        {
            if (t == null || !r.Seated || r.Dishes == null || r.Dishes.Length == 0) return 0;
            double dx = sourceKm.X - listenerKm.X, dy = sourceKm.Y - listenerKm.Y, dz = sourceKm.Z - listenerKm.Z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d <= 1e-9) return 0;
            double best = 0;
            for (int i = 0; i < r.Dishes.Length; i++)
            {
                double peak, beam;
                PatternOf(r.Dishes[i].Kind, t, out peak, out beam);
                if (peak <= 0 || beam <= 0) continue;
                Vec3 a = r.Dishes[i].Axis;
                double len = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
                if (len <= 1e-9) continue;
                double cos = (a.X * dx + a.Y * dy + a.Z * dz) / (len * d);
                double angleDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
                double ratio = angleDeg / beam;
                double g = peak - 12.0 * ratio * ratio;
                if (g > best) best = g;
            }
            return best;
        }

        /// <summary>A reflector's peak gain and beamwidth from the tuning.</summary>
        public static void PatternOf(RadioDishKind kind, FactionTuning t, out double peakDb, out double beamwidthDeg)
        {
            if (kind == RadioDishKind.Parabolic) { peakDb = t.AtcParabolicGainDb; beamwidthDeg = t.AtcParabolicBeamwidthDeg; }
            else { peakDb = t.AtcDishGainDb; beamwidthDeg = t.AtcDishBeamwidthDeg; }
        }

        /// <summary>The most any of the listener's reflectors could add, dB, aimed perfectly: how far
        /// the server looks for calls a listener could bring in by turning (FAC-419, aiming by ear).</summary>
        public static double MaxPeakDb(RadioReceiver r, FactionTuning t)
        {
            if (t == null || !r.Seated || r.Dishes == null) return 0;
            double best = 0;
            for (int i = 0; i < r.Dishes.Length; i++)
            {
                double peak, beam;
                PatternOf(r.Dishes[i].Kind, t, out peak, out beam);
                if (beam > 0 && peak > best) best = peak;
            }
            return best;
        }

        /// <summary>How much further a gain carries: received power falls with the square of
        /// distance, so reach scales with the square root of the power gain.</summary>
        public static double GainFactor(double gainDb)
        {
            return Math.Pow(10.0, gainDb / 20.0);
        }

        /// <summary>What a reach is worth in signal: 20 log10(reach / the ship default). Keeps the
        /// signal-to-noise ratio at the edge of every reach the same, so a suit at 10 km and a ship at
        /// 30 km both hear a call on its last legs; a suit is 9.5 dB down on a bare seat, and a big
        /// array well up.</summary>
        public static double AntennaGainDb(double rangeKm, FactionTuning t)
        {
            if (t == null || t.AtcRadioRangeKm <= 0 || rangeKm <= 0) return 0;
            return 20.0 * Math.Log10(rangeKm / t.AtcRadioRangeKm);
        }
    }

    /// <summary>A listener's radio set (FAC-419): whether they are at a ship's controls, and what the
    /// ship's antennas add up to (<see cref="ShipSensor.StrengthKm"/>), and which way its dishes point.
    /// The default is a spacesuit.</summary>
    public struct RadioReceiver
    {
        public bool Seated;
        public int AntennaCount;
        public double StrengthKm;
        /// <summary>Each working reflector, null for none. Its antenna value is already in
        /// <see cref="StrengthKm"/>, which hears all round; this is only the extra reach along its
        /// axis (<see cref="RadioPath.DirectionalGainDb"/>).</summary>
        public RadioDish[] Dishes;
    }

    /// <summary>Which reflector: its gain pattern's tuning (<see cref="RadioPath.PatternOf"/>).</summary>
    public enum RadioDishKind
    {
        /// <summary>The Antenna Dish.</summary>
        Dish = 0,
        /// <summary>The Parabolic Antenna.</summary>
        Parabolic = 1,
    }

    /// <summary>One reflector aboard: which kind, and which way it points in world space now.</summary>
    public struct RadioDish
    {
        public RadioDishKind Kind;
        public Vec3 Axis;
    }
}
