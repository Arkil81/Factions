using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Traffic control's admission budget (docs/traffic-control.md, FAC-436): how much frame is left
    /// for a new Factions grid, and whether one predicted to cost so much would fit. Its per-spawn
    /// verdict is recorded, not acted on; what gates spawns is <see cref="TrafficPresence"/>, which
    /// divides <see cref="FactionsBudgetMs"/> between players with the multipliers learnt here.
    ///
    /// <para><b>Headroom</b> is the target (<see cref="TargetBusyPct"/> of the 16.67 ms frame) less
    /// the last window's measured busy time, less the predicted cost of every spawn that window did
    /// not see in full. A spawn is reserved from the moment it starts until a whole window has closed
    /// after it settled, so the lag between spawning a grid and measuring it is covered by the
    /// prediction rather than chased by the controller.</para>
    ///
    /// <para><b>Calibration.</b> The bench law is scaled per <see cref="TrafficClass"/> by a
    /// multiplier learnt from the ledger: each window, the measured cost of the materialised fleet
    /// grids against their unscaled prediction. A window teaches a class only when that class carries
    /// at least <see cref="DominantShare"/> of the prediction, with the other class's share removed
    /// at its current multiplier; a window with too little in the world teaches nothing.</para>
    ///
    /// <para>Game thread only; not synchronised.</para>
    /// </summary>
    public sealed class TrafficBudget
    {
        public const double FrameBudgetMs = 1000.0 / 60.0;

        public double TargetBusyPct = 80.0;
        /// <summary>How long after starting a spawn is assumed to have landed and be simulating.</summary>
        public double SettleSeconds = 10.0;
        /// <summary>A reservation no window has released by then is dropped anyway, so a stalled
        /// measurement cannot close the budget for good.</summary>
        public double ReservationMaxSeconds = 300.0;
        public double CalibrationAlpha = 0.2;
        /// <summary>Unscaled prediction below which a window teaches nothing: a few small hulls are
        /// lost in the noise of the rest of the frame.</summary>
        public double MinCalibrationMs = 0.5;
        public double DominantShare = 0.8;
        public double MultiplierMin = 0.1;
        public double MultiplierMax = 50.0;

        public double CivilianMultiplier = 1.0;
        public double CombatMultiplier = 1.0;
        public int CivilianSamples { get; private set; }
        public int CombatSamples { get; private set; }

        public bool HasWindow { get; private set; }
        public double LastBusyMs { get; private set; }
        /// <summary>Measured cost of the fleet grids the last window's census saw.</summary>
        public double LastFleetMeasuredMs { get; private set; }
        /// <summary>Predicted cost of the fleets materialised in the last window that its census had
        /// not seen: already inside the busy time, not yet in <see cref="LastFleetMeasuredMs"/>.</summary>
        public double LastUnmatchedFleetMs { get; private set; }

        /// <summary>What Factions fleets may cost in all: the target less everything that is not a
        /// fleet grid (players, stations, the world, this plugin's tick). NaN before the first window.
        /// The presence rule divides it between players' neighbourhoods.</summary>
        public double FactionsBudgetMs
        {
            get
            {
                if (!HasWindow) return double.NaN;
                // A fleet spawned since the census is in the busy time but not in the measured fleet
                // cost; left there it would read as someone else's load, and be charged twice against
                // the fleets, once as that and once as its own predicted use.
                double others = Math.Max(0.0, LastBusyMs - LastFleetMeasuredMs - LastUnmatchedFleetMs);
                return Math.Max(0.0, TargetMs - others - SpawnStallLoadMs);
            }
        }

        /// <summary>Unscaled ms per frame charged for a hull whose prefab has no block count.</summary>
        public const double UnknownHullMs = 0.25;

        /// <summary>A spawn stalls the frame it lands in: building the grids, compiling their scripts.
        /// Estimated as <c>SpawnStallBaseMs + SpawnStallPerBlockMs · blocks</c>, anchored on the one
        /// measured case, a 2,026-block Naginata at 158-175 ms (hitch probe, 2026-09-25).</summary>
        public const double SpawnStallBaseMs = 40.0;
        public const double SpawnStallPerBlockMs = 0.065;
        /// <summary>Frames per second the stall is spread over when it is charged per frame.</summary>
        public const double FramesPerSecond = 60.0;

        public static double SpawnStallMs(int blocks)
        {
            return SpawnStallBaseMs + SpawnStallPerBlockMs * Math.Max(0, blocks);
        }

        /// <summary>Length of the telemetry window, seconds: a stall is charged over this many seconds'
        /// frames, which is how it will show in that window's measured busy time.</summary>
        public double WindowSeconds = 30.0;

        private readonly List<KeyValuePair<DateTime, double>> _stalls = new List<KeyValuePair<DateTime, double>>();

        /// <summary>Records a spawn's stall. It is charged against the budget until the window it
        /// happened in closes; after that the measured busy time holds it.</summary>
        public void NoteSpawnStall(double stallMs, DateTime now)
        {
            if (stallMs > 0) _stalls.Add(new KeyValuePair<DateTime, double>(now, stallMs));
        }

        /// <summary>Spawn stalls in the window still open, as ms per frame of that window. A dozen
        /// spawns in thirty seconds is one to two ms a frame on top of what the hulls then cost to
        /// simulate -- the part a budget built on steady cost alone does not see.</summary>
        public double SpawnStallLoadMs
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < _stalls.Count; i++) sum += _stalls[i].Value;
                return sum / (Math.Max(1.0, WindowSeconds) * FramesPerSecond);
            }
        }

        public struct Decision
        {
            public double PredictedMs;
            /// <summary>NaN before the first window.</summary>
            public double HeadroomMs;
            public bool WouldAdmit;
        }

        /// <summary>One window's reading, from the ledger's census and the runtime's fleets.</summary>
        public struct WindowInput
        {
            public DateTime WindowStartUtc;
            public double BusyMs;
            /// <summary>Measured ms per frame of the fleet grids the census saw.</summary>
            public double FleetMeasuredMs;
            /// <summary>Unscaled prediction for the same fleets, per class.</summary>
            public double CivilianBaseMs;
            public double CombatBaseMs;
            /// <summary>Scaled prediction of materialised fleets the census did not see.</summary>
            public double UnmatchedFleetMs;
        }

        private sealed class Reservation
        {
            public string Key;
            public double Ms;
            public DateTime Since;
        }

        private readonly List<Reservation> _reservations = new List<Reservation>();

        // window counters, drained by TakeWindow
        private int _wouldAdmit, _wouldRefuse;
        private double _refusedMs, _admittedMs;

        public double TargetMs { get { return TargetBusyPct / 100.0 * FrameBudgetMs; } }

        public double Multiplier(TrafficClass cls)
        {
            return cls == TrafficClass.Combat ? CombatMultiplier : CivilianMultiplier;
        }

        public double ReservedMs(DateTime now)
        {
            Expire(now);
            double sum = 0.0;
            for (int i = 0; i < _reservations.Count; i++) sum += _reservations[i].Ms;
            return sum;
        }

        public int ReservationCount { get { return _reservations.Count; } }

        public double HeadroomMs(DateTime now)
        {
            if (!HasWindow) return double.NaN;
            return TargetMs - LastBusyMs - ReservedMs(now);
        }

        /// <summary>Asks whether a fleet predicted at <paramref name="baseMs"/> (unscaled) fits now.
        /// Before the first window the answer is yes: there is nothing to refuse on.</summary>
        public Decision Consider(TrafficClass cls, double baseMs, DateTime now)
        {
            double predicted = Math.Max(0.0, baseMs) * Multiplier(cls);
            double headroom = HeadroomMs(now);
            bool admit = double.IsNaN(headroom) || predicted <= headroom;
            if (admit) { _wouldAdmit++; _admittedMs += predicted; }
            else { _wouldRefuse++; _refusedMs += predicted; }
            return new Decision { PredictedMs = predicted, HeadroomMs = headroom, WouldAdmit = admit };
        }

        /// <summary>Holds <paramref name="predictedMs"/> against the budget for a spawn that started
        /// at <paramref name="now"/>. A second reservation under one key replaces the first.</summary>
        public void Reserve(string key, double predictedMs, DateTime now)
        {
            if (predictedMs <= 0) return;
            if (key != null)
                for (int i = _reservations.Count - 1; i >= 0; i--)
                    if (string.Equals(_reservations[i].Key, key, StringComparison.Ordinal)) _reservations.RemoveAt(i);
            _reservations.Add(new Reservation { Key = key, Ms = predictedMs, Since = now });
        }

        /// <summary>Takes a closed window: its busy time becomes the measured base, reservations it
        /// saw in full are released, and the multipliers learn from it.</summary>
        public void ObserveWindow(WindowInput w, DateTime now)
        {
            HasWindow = true;
            LastBusyMs = Math.Max(0.0, w.BusyMs);
            LastFleetMeasuredMs = Math.Min(LastBusyMs, Math.Max(0.0, w.FleetMeasuredMs));
            LastUnmatchedFleetMs = Math.Max(0.0, w.UnmatchedFleetMs);
            // Stalls up to now happened inside the window just closed, so its busy time holds them.
            _stalls.RemoveAll(s => s.Key <= now);

            // Settled before the window opened: the whole window measured it.
            for (int i = _reservations.Count - 1; i >= 0; i--)
                if (_reservations[i].Since.AddSeconds(SettleSeconds) <= w.WindowStartUtc) _reservations.RemoveAt(i);
            Expire(now);

            Calibrate(w);
        }

        private void Calibrate(WindowInput w)
        {
            double civ = Math.Max(0.0, w.CivilianBaseMs);
            double combat = Math.Max(0.0, w.CombatBaseMs);
            double total = civ + combat;
            if (total < MinCalibrationMs || w.FleetMeasuredMs <= 0) return;

            if (civ / total >= DominantShare)
            {
                double sample = (w.FleetMeasuredMs - CombatMultiplier * combat) / civ;
                CivilianMultiplier = Blend(CivilianMultiplier, sample);
                CivilianSamples++;
            }
            else if (combat / total >= DominantShare)
            {
                double sample = (w.FleetMeasuredMs - CivilianMultiplier * civ) / combat;
                CombatMultiplier = Blend(CombatMultiplier, sample);
                CombatSamples++;
            }
        }

        private double Blend(double current, double sample)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample)) return current;
            sample = Math.Max(MultiplierMin, Math.Min(MultiplierMax, sample));
            return current + CalibrationAlpha * (sample - current);
        }

        private void Expire(DateTime now)
        {
            for (int i = _reservations.Count - 1; i >= 0; i--)
                if (_reservations[i].Since.AddSeconds(ReservationMaxSeconds) <= now) _reservations.RemoveAt(i);
        }

        /// <summary>Drains the per-window decision counters.</summary>
        public void TakeWindow(out int wouldAdmit, out int wouldRefuse, out double admittedMs, out double refusedMs)
        {
            wouldAdmit = _wouldAdmit; wouldRefuse = _wouldRefuse;
            admittedMs = _admittedMs; refusedMs = _refusedMs;
            _wouldAdmit = 0; _wouldRefuse = 0; _admittedMs = 0; _refusedMs = 0;
        }
    }
}
