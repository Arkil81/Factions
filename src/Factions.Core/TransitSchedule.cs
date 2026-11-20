using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One leg's distance-versus-time curve: a <see cref="LegZoneProfile"/>'s tier run
    /// table integrated against the live speeds, under one acceleration limit.
    ///
    /// <para><b>Why the schedule is an object and not three formulas.</b> A leg's duration, the
    /// fraction of it covered at a given moment, and the moment at which a given fraction was
    /// covered are one curve asked three ways, and they have to agree exactly or an in-flight fleet
    /// jumps on the map. Building the curve once and reading all three off it makes them inverses by
    /// construction.</para>
    ///
    /// <para><b>The speed is limited at both ends of every run.</b> A forward pass accelerates into
    /// each tier's cap and a backward pass sheds speed early enough to meet a lower cap ahead, so a
    /// fleet decelerates on its way into a gravity well rather than stepping down at its edge. A
    /// single deep-space run reduces to the accelerate/cruise/decelerate profile a bare distance has
    /// always had.</para>
    ///
    /// <para><see cref="FactionTuning.FleetAccelKmPerMin2"/> is a constant acceleration here.</para></summary>
    public sealed class TransitSchedule
    {
        // Phase boundaries. Index i covers [_km[i], _km[i+1]) and [_min[i], _min[i+1]), entered at
        // _v0[i] under signed acceleration _a[i]. Arrays rather than a struct list: the schedule is
        // read far more often than it is built.
        private readonly List<double> _km = new List<double>();
        private readonly List<double> _min = new List<double>();
        private readonly List<double> _v0 = new List<double>();
        private readonly List<double> _a = new List<double>();

        public double TotalKm { get; private set; }
        public double TotalMins { get; private set; }

        /// <summary>The cap for one tier, in km/min. An <see cref="TransitTier.Orbit"/> cap that is
        /// unset or out of order collapses to open space, which is what makes the tier shippable
        /// inert: until a number is chosen, an authored region flies as the space around it.</summary>
        public static double CapFor(TransitTier tier, FactionTuning tuning)
        {
            if (tuning == null) return 0;
            if (tier == TransitTier.Gravity) return tuning.FleetSpeedMinKmPerMin;
            if (tier == TransitTier.DeepSpace) return tuning.FleetSpeedKmPerMin;

            double orbit = tuning.FleetSpeedOrbitKmPerMin;
            if (orbit <= 0 || orbit > tuning.FleetSpeedKmPerMin) return tuning.FleetSpeedKmPerMin;
            if (orbit < tuning.FleetSpeedMinKmPerMin) return tuning.FleetSpeedMinKmPerMin;
            return orbit;
        }

        /// <summary>Builds the curve, or reports false on the same inputs the transit model has
        /// always refused: no distance, no cruise speed, no acceleration, or a floor above the
        /// ceiling. A refused schedule means "no transit model", which callers already read as an
        /// immediate arrival.</summary>
        /// <summary><see cref="TryBuild"/>, memoised on <paramref name="zones"/> for as long as the four tuning values
        /// the curve reads are unchanged. A fleet's position is read off its schedule every frame, and building one
        /// allocates arrays and lists: per-frame position reads of zoned legs were 5-13 MB/s of the server's
        /// allocation under 15 phantom patrols (2026-10-06).</summary>
        public static bool TryGet(LegZoneProfile zones, FactionTuning tuning, out TransitSchedule schedule)
        {
            if (zones == null || tuning == null) return TryBuild(zones, tuning, out schedule);
            if (zones.CachedScheduleValid
                && zones.CachedMin == tuning.FleetSpeedMinKmPerMin && zones.CachedMax == tuning.FleetSpeedKmPerMin
                && zones.CachedOrbit == tuning.FleetSpeedOrbitKmPerMin && zones.CachedAccel == tuning.FleetAccelKmPerMin2)
            {
                schedule = zones.CachedSchedule;
                return schedule != null;
            }
            bool ok = TryBuild(zones, tuning, out schedule);
            zones.CachedSchedule = ok ? schedule : null;
            zones.CachedMin = tuning.FleetSpeedMinKmPerMin;
            zones.CachedMax = tuning.FleetSpeedKmPerMin;
            zones.CachedOrbit = tuning.FleetSpeedOrbitKmPerMin;
            zones.CachedAccel = tuning.FleetAccelKmPerMin2;
            zones.CachedScheduleValid = true;
            return ok;
        }

        public static bool TryBuild(LegZoneProfile zones, FactionTuning tuning, out TransitSchedule schedule)
        {
            schedule = null;
            if (zones == null || tuning == null) return false;
            if (zones.TotalKm <= 0 || zones.RunKm.Count == 0) return false;
            if (tuning.FleetSpeedKmPerMin <= 0 || tuning.FleetAccelKmPerMin2 <= 0) return false;

            double floor = tuning.FleetSpeedMinKmPerMin;
            if (floor < 0 || tuning.FleetSpeedKmPerMin < floor) return false;

            double accel = tuning.FleetAccelKmPerMin2;
            int n = zones.RunKm.Count;

            var caps = new double[n];
            for (int i = 0; i < n; i++)
            {
                caps[i] = CapFor(zones.RunTiers[i], tuning);
                if (caps[i] <= 0) return false;
            }

            // A leg leaves and arrives at the world's own grid speed limit, which is also the cap
            // inside a well -- one number, because they are the same statement about a hull that is
            // not yet (or no longer) running its drive.
            double ends = floor;

            var vIn = new double[n];
            var vOut = new double[n];

            double carried = Math.Min(ends, caps[0]);
            for (int i = 0; i < n; i++)
            {
                vIn[i] = Math.Min(carried, caps[i]);
                vOut[i] = Math.Min(caps[i], Reachable(vIn[i], accel, zones.RunKm[i]));
                carried = vOut[i];
            }

            vOut[n - 1] = Math.Min(vOut[n - 1], Math.Min(ends, caps[n - 1]));
            for (int i = n - 1; i >= 0; i--)
            {
                vIn[i] = Math.Min(vIn[i], Reachable(vOut[i], accel, zones.RunKm[i]));
                if (i > 0) vOut[i - 1] = Math.Min(vOut[i - 1], vIn[i]);
            }

            var built = new TransitSchedule();
            built._km.Add(0);
            built._min.Add(0);

            for (int i = 0; i < n; i++)
            {
                double length = zones.RunKm[i];
                if (length <= 0) continue;

                // Peak reachable inside the run: accelerate from vIn and brake to vOut over exactly
                // its length, then clip to the cap.
                double peakSq = (2.0 * accel * length + vIn[i] * vIn[i] + vOut[i] * vOut[i]) * 0.5;
                double peak = Math.Sqrt(peakSq > 0 ? peakSq : 0);
                if (peak > caps[i]) peak = caps[i];
                if (peak < vIn[i]) peak = vIn[i];
                if (peak < vOut[i]) peak = vOut[i];

                double accelKm = (peak * peak - vIn[i] * vIn[i]) / (2.0 * accel);
                double brakeKm = (peak * peak - vOut[i] * vOut[i]) / (2.0 * accel);
                double cruiseKm = length - accelKm - brakeKm;
                if (cruiseKm < 0)
                {
                    // Floating point only; the peak above is solved to make this exactly zero.
                    double overshoot = -cruiseKm;
                    cruiseKm = 0;
                    if (accelKm >= brakeKm) accelKm -= overshoot; else brakeKm -= overshoot;
                    if (accelKm < 0) accelKm = 0;
                    if (brakeKm < 0) brakeKm = 0;
                }

                built.AddPhase(accelKm, vIn[i], accel);
                built.AddPhase(cruiseKm, peak, 0);
                built.AddPhase(brakeKm, peak, -accel);
            }

            if (built._km.Count < 2) return false;

            built.TotalKm = built._km[built._km.Count - 1];
            built.TotalMins = built._min[built._min.Count - 1];
            if (built.TotalMins <= 0) return false;

            schedule = built;
            return true;
        }

        /// <summary>Minutes elapsed once <paramref name="coveredKm"/> has been flown.</summary>
        public double MinsAtKm(double coveredKm)
        {
            if (coveredKm <= 0) return 0;
            if (coveredKm >= TotalKm) return TotalMins;

            int i = IndexFor(_km, coveredKm);
            double d = coveredKm - _km[i];
            double v0 = _v0[i], a = _a[i];
            if (a == 0) return _min[i] + (v0 > 0 ? d / v0 : 0);

            double endSq = v0 * v0 + 2.0 * a * d;
            double end = Math.Sqrt(endSq > 0 ? endSq : 0);
            return _min[i] + (end - v0) / a;
        }

        /// <summary>Kilometres flown after <paramref name="elapsedMins"/>.</summary>
        public double KmAtMins(double elapsedMins)
        {
            if (elapsedMins <= 0) return 0;
            if (elapsedMins >= TotalMins) return TotalKm;

            int i = IndexFor(_min, elapsedMins);
            double t = elapsedMins - _min[i];
            return _km[i] + _v0[i] * t + 0.5 * _a[i] * t * t;
        }

        /// <summary>Fraction of the leg flown after <paramref name="elapsedMins"/>, in [0, 1].</summary>
        public double ProgressRatio(double elapsedMins)
        {
            if (TotalKm <= 0) return elapsedMins > 0 ? 1.0 : 0.0;
            double ratio = KmAtMins(elapsedMins) / TotalKm;
            if (ratio < 0) return 0;
            return ratio > 1 ? 1 : ratio;
        }

        private void AddPhase(double lengthKm, double entrySpeed, double accel)
        {
            if (lengthKm <= 0) return;
            double endSq = entrySpeed * entrySpeed + 2.0 * accel * lengthKm;
            double end = Math.Sqrt(endSq > 0 ? endSq : 0);

            double mins;
            if (accel == 0)
            {
                if (entrySpeed <= 0) return;
                mins = lengthKm / entrySpeed;
            }
            else mins = (end - entrySpeed) / accel;
            if (mins <= 0) return;

            int last = _km.Count - 1;
            _v0.Add(entrySpeed);
            _a.Add(accel);
            _km.Add(_km[last] + lengthKm);
            _min.Add(_min[last] + mins);
        }

        /// <summary>Speed reachable from <paramref name="from"/> over <paramref name="km"/> at
        /// <paramref name="accel"/>.</summary>
        private static double Reachable(double from, double accel, double km)
        {
            double sq = from * from + 2.0 * accel * km;
            return Math.Sqrt(sq > 0 ? sq : 0);
        }

        /// <summary>The phase containing <paramref name="value"/>, by binary search over the phase
        /// boundaries. Returns a phase index, so never the final boundary.</summary>
        private static int IndexFor(List<double> boundaries, double value)
        {
            int lo = 0, hi = boundaries.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (boundaries[mid] <= value) lo = mid; else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>The closed form of a <see cref="TransitSchedule"/> over a single
    /// <see cref="TransitTier.DeepSpace"/> run — one accelerate/cruise/decelerate profile, solved
    /// arithmetically instead of built.
    ///
    /// <para><b>Why a second implementation of the same curve.</b> Every caller that has only a
    /// distance takes this path, including the dispatch scoring loops that ask it per candidate per
    /// tick, and a struct that allocates nothing belongs there. It is the same shortcut, for the
    /// same reason, that <see cref="LegProfileCache.Build"/> takes for a leg that never meets
    /// gravity — and, as there, the two paths are asserted equal rather than assumed so.</para></summary>
    public struct PlainTransit
    {
        public double TotalKm;
        public double TotalMins;
        private double _vMin, _vPeak, _accel, _accelKm, _cruiseKm, _accelMins, _cruiseMins;

        public static bool TryBuild(double distKm, FactionTuning tuning, out PlainTransit transit)
        {
            transit = new PlainTransit();
            if (tuning == null || distKm <= 0) return false;
            if (tuning.FleetSpeedKmPerMin <= 0 || tuning.FleetAccelKmPerMin2 <= 0) return false;

            double vMin = tuning.FleetSpeedMinKmPerMin, vMax = tuning.FleetSpeedKmPerMin;
            if (vMin < 0 || vMax < vMin) return false;

            double accel = tuning.FleetAccelKmPerMin2;
            double peakSq = accel * distKm + vMin * vMin;
            double peak = Math.Sqrt(peakSq > 0 ? peakSq : 0);
            if (peak > vMax) peak = vMax;
            if (peak < vMin) peak = vMin;

            double accelKm = (peak * peak - vMin * vMin) / (2.0 * accel);
            double cruiseKm = distKm - 2.0 * accelKm;
            if (cruiseKm < 0) { accelKm = distKm * 0.5; cruiseKm = 0; }

            transit._vMin = vMin;
            transit._vPeak = peak;
            transit._accel = accel;
            transit._accelKm = accelKm;
            transit._cruiseKm = cruiseKm;
            transit._accelMins = (peak - vMin) / accel;
            transit._cruiseMins = peak > 0 ? cruiseKm / peak : 0;
            transit.TotalKm = distKm;
            transit.TotalMins = transit._accelMins * 2.0 + transit._cruiseMins;
            return transit.TotalMins > 0;
        }

        public double MinsAtKm(double coveredKm)
        {
            if (coveredKm <= 0) return 0;
            if (coveredKm >= TotalKm) return TotalMins;
            if (coveredKm <= _accelKm) return RampMins(coveredKm);
            if (coveredKm <= _accelKm + _cruiseKm)
                return _accelMins + (_vPeak > 0 ? (coveredKm - _accelKm) / _vPeak : 0);
            return TotalMins - RampMins(TotalKm - coveredKm);
        }

        public double KmAtMins(double elapsedMins)
        {
            if (elapsedMins <= 0) return 0;
            if (elapsedMins >= TotalMins) return TotalKm;
            if (elapsedMins <= _accelMins) return RampKm(elapsedMins);
            if (elapsedMins <= _accelMins + _cruiseMins)
                return _accelKm + _vPeak * (elapsedMins - _accelMins);
            return TotalKm - RampKm(TotalMins - elapsedMins);
        }

        public double ProgressRatio(double elapsedMins)
        {
            if (TotalKm <= 0) return elapsedMins > 0 ? 1.0 : 0.0;
            double ratio = KmAtMins(elapsedMins) / TotalKm;
            if (ratio < 0) return 0;
            return ratio > 1 ? 1 : ratio;
        }

        /// <summary>Minutes to cover <paramref name="km"/> of the ramp from <c>vMin</c>.</summary>
        private double RampMins(double km)
        {
            double endSq = _vMin * _vMin + 2.0 * _accel * km;
            double end = Math.Sqrt(endSq > 0 ? endSq : 0);
            return (end - _vMin) / _accel;
        }

        /// <summary>Kilometres covered in <paramref name="mins"/> of the ramp from <c>vMin</c>.</summary>
        private double RampKm(double mins)
        {
            return _vMin * mins + 0.5 * _accel * mins * mins;
        }
    }
}
