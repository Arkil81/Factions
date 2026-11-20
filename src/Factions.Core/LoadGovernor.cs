using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The load governor's controller (docs/load-governor.md, build step 4). Decides, each
    /// telemetry window, whether the server is short of frame time and which step it would take.
    /// <b>Dry run:</b> nothing reads its decision except telemetry and the <c>factions governor</c>
    /// verb.
    ///
    /// <para><b>Trigger (operator decisions 3 and 4).</b> Short when the main thread is busy for
    /// <see cref="Settings.AdjustBusyPct"/> or more of the frame, or when sim falls below
    /// <see cref="Settings.SimFloor"/>. Clear only below <see cref="Settings.ReleaseBusyPct"/> with
    /// sim back in band. Between the two it holds: that gap is the hysteresis.</para>
    ///
    /// <para><b>Order (decision 5).</b> Factions is cut first, <see cref="Settings.FactionsStepPct"/>
    /// of its measured cost per step, down to <see cref="Settings.FactionsFloorMs"/>, for at most
    /// <see cref="Settings.FactionsMaxSteps"/> steps. Only then are over-share players stepped
    /// through the toolbox (1 warn, 2 admission, 3 disable block groups, 4 widen). Release runs in
    /// reverse: players first, then Factions.</para>
    ///
    /// <para><b>Lazy.</b> At most one step change per <see cref="Settings.IntervalSeconds"/>.
    /// Physics lags its cause — a hull takes minutes to fall asleep — so a faster controller would
    /// chase its own transients.</para>
    ///
    /// <para><b>Shares (decision 2).</b> The pool is the frame up to the adjust line, minus load no
    /// party owns, minus the Factions floor. Players split it max-min fair (see
    /// <see cref="FairShares"/>): one player alone may use all of it, and a light player's unused
    /// share goes to the heavier ones. A share only binds while the server is short.</para>
    /// </summary>
    public sealed class LoadGovernor
    {
        public const int PlayerMaxStep = 4;

        /// <summary>Below this window sim ratio, with <see cref="Input.FastPath"/>, Factions goes straight
        /// to <see cref="FactionsLoadActuator.CombatStep"/> without waiting out the interval (FAC-425:
        /// the raid that took sim to 0.47 was all in the world 10 s before step 1, and step 2 was first
        /// reachable three minutes later, after the fight).</summary>
        public const double FastSimFloor = 0.6;

        public sealed class Settings
        {
            public double FrameBudgetMs = 1000.0 / 60.0;
            public double AdjustBusyPct = 95.0;
            public double ReleaseBusyPct = 85.0;
            public double SimFloor = 0.8;
            public double IntervalSeconds = 180.0;
            public double FactionsStepPct = 25.0;
            public double FactionsFloorMs = 0.5;
            public int FactionsMaxSteps = 3;
            /// <summary>A player counts as over share beyond this margin, so a rounding
            /// difference does not escalate anyone.</summary>
            public double OverShareMarginMs = 0.05;
            /// <summary>Stall time over the frame budget, summed across a window's slow frames, at which
            /// the tail reads Short; below <see cref="TailReleaseMs"/> it reads Clear.</summary>
            public double TailShortMs = 250.0;
            public double TailReleaseMs = 100.0;
        }

        /// <summary>One window's measurements, all in ms per frame.</summary>
        public sealed class Input
        {
            public double BusyMs;
            public double SimRatio = 1.0;
            /// <summary>Factions' whole cost: its grids in the ledger plus the plugin's own tick.</summary>
            public double FactionsMs;
            /// <summary>Each online player's ledger cost, by party name.</summary>
            public Dictionary<string, double> PlayerMs = new Dictionary<string, double>(StringComparer.Ordinal);
            /// <summary>The window's slow frames (over the hitch threshold) and the time they ran over
            /// the frame budget, in ms per window, not per frame; and that time by its measured cause.</summary>
            public int TailFrames;
            public double TailStallMs;
            public Dictionary<string, double> TailStallByCause = new Dictionary<string, double>(StringComparer.Ordinal);
            /// <summary>Whether a sim ratio under <see cref="FastSimFloor"/> may skip the interval.</summary>
            public bool FastPath;
        }

        public sealed class PlayerView
        {
            public string Party;
            public double CostMs;
            public double ShareMs;
            public bool Over;
            public int Step;
        }

        public enum Pressure { Clear, Hold, Short }

        public sealed class Decision
        {
            public Pressure Pressure;
            /// <summary>Why it is short: "busy", "sim", or "busy+sim"; empty when not short.</summary>
            public string Cause = "";
            public double BusyPct;
            public double ShortMs;
            public double OtherMs;
            public double PlayerPoolMs;
            public double FactionsMs;
            public double FactionsTargetMs;
            public int FactionsStep;
            public readonly List<PlayerView> Players = new List<PlayerView>();
            /// <summary>The step change this window took, or "none" / "holding (interval)" /
            /// "at ceiling".</summary>
            public string Action = "none";
            public bool Changed;
            /// <summary>The stall tail, judged on its own: a window can be Clear on average and Short
            /// here. Reported, not acted on (FAC-445): what the tail is made of decides who could act.</summary>
            public Pressure TailPressure;
            public int TailFrames;
            public double TailStallMs;
            public string TailTopCause = "";
            public double TailTopCauseMs;
            public double TailFactionsMs;
        }

        /// <summary>The cause name the host gives a slow frame Factions' own code dominated.</summary>
        public const string FactionsCause = "Factions";

        private readonly Settings _s;
        private int _factionsStep;
        private readonly Dictionary<string, int> _playerStep = new Dictionary<string, int>(StringComparer.Ordinal);
        private DateTime _lastChange = DateTime.MinValue;

        public LoadGovernor(Settings settings)
        {
            _s = settings ?? new Settings();
        }

        public Decision Last { get; private set; }
        public int FactionsStep { get { return _factionsStep; } }

        public int PlayerStep(string party)
        {
            int s;
            return party != null && _playerStep.TryGetValue(party, out s) ? s : 0;
        }

        public Decision Evaluate(Input input, DateTime now)
        {
            if (input == null) throw new ArgumentNullException("input");
            var d = new Decision();
            double adjustMs = _s.AdjustBusyPct / 100.0 * _s.FrameBudgetMs;
            double releaseMs = _s.ReleaseBusyPct / 100.0 * _s.FrameBudgetMs;
            bool busyShort = input.BusyMs >= adjustMs;
            bool simShort = input.SimRatio < _s.SimFloor;
            d.BusyPct = _s.FrameBudgetMs > 0 ? 100.0 * input.BusyMs / _s.FrameBudgetMs : 0.0;
            d.Pressure = busyShort || simShort ? Pressure.Short
                       : input.BusyMs < releaseMs ? Pressure.Clear
                       : Pressure.Hold;
            d.Cause = busyShort && simShort ? "busy+sim" : busyShort ? "busy" : simShort ? "sim" : "";
            EvaluateTail(input, d);
            d.ShortMs = Math.Max(0.0, input.BusyMs - adjustMs);

            // Players who left take their step with them.
            var gone = new List<string>();
            foreach (string p in _playerStep.Keys) if (!input.PlayerMs.ContainsKey(p)) gone.Add(p);
            foreach (string p in gone) _playerStep.Remove(p);

            double playersMs = 0;
            foreach (double v in input.PlayerMs.Values) playersMs += Math.Max(0.0, v);
            double factionsMs = Math.Max(0.0, input.FactionsMs);
            d.FactionsMs = factionsMs;
            d.OtherMs = Math.Max(0.0, input.BusyMs - playersMs - factionsMs);
            double factionsReserve = Math.Min(_s.FactionsFloorMs, factionsMs);
            d.PlayerPoolMs = Math.Max(0.0, adjustMs - d.OtherMs - factionsReserve);

            Dictionary<string, double> shares = FairShares(input.PlayerMs, d.PlayerPoolMs);
            foreach (KeyValuePair<string, double> kv in input.PlayerMs)
            {
                double share = shares[kv.Key];
                d.Players.Add(new PlayerView
                {
                    Party = kv.Key,
                    CostMs = kv.Value,
                    ShareMs = share,
                    Over = kv.Value > share + _s.OverShareMarginMs,
                });
            }
            d.Players.Sort((a, b) => b.CostMs.CompareTo(a.CostMs));

            bool intervalDue = (now - _lastChange).TotalSeconds >= _s.IntervalSeconds;
            if (input.FastPath && input.SimRatio < FastSimFloor && _factionsStep < FactionsLoadActuator.CombatStep
                && _s.FactionsMaxSteps >= FactionsLoadActuator.CombatStep)
            {
                d.Action = "factions step " + _factionsStep + "->" + FactionsLoadActuator.CombatStep + " (fast: sim)";
                _factionsStep = FactionsLoadActuator.CombatStep;
                d.Changed = true;
                _lastChange = now;
            }
            else if (d.Pressure == Pressure.Short) Escalate(d, factionsMs, intervalDue, now);
            else if (d.Pressure == Pressure.Clear) Release(d, intervalDue, now);

            d.FactionsStep = _factionsStep;
            d.FactionsTargetMs = FactionsTarget(factionsMs);
            foreach (PlayerView v in d.Players) v.Step = PlayerStep(v.Party);
            Last = d;
            return d;
        }

        /// <summary>The window's stall tail. An average cannot see it: one 500 ms frame in a 30 s window
        /// moves average busy time by under 2%, and on 2026-10-03 34% of windows with a player online had a
        /// frame that halved sim while the governor read Clear in ~97% of them (FAC-445).</summary>
        private void EvaluateTail(Input input, Decision d)
        {
            d.TailFrames = input.TailFrames;
            d.TailStallMs = Math.Max(0.0, input.TailStallMs);
            d.TailPressure = d.TailStallMs >= _s.TailShortMs ? Pressure.Short
                           : d.TailStallMs < _s.TailReleaseMs ? Pressure.Clear
                           : Pressure.Hold;
            foreach (KeyValuePair<string, double> kv in input.TailStallByCause)
            {
                if (kv.Key == FactionsCause) d.TailFactionsMs = kv.Value;
                if (kv.Value > d.TailTopCauseMs) { d.TailTopCauseMs = kv.Value; d.TailTopCause = kv.Key; }
            }
        }

        private void Escalate(Decision d, double factionsMs, bool intervalDue, DateTime now)
        {
            bool factionsCanGive = _factionsStep < _s.FactionsMaxSteps && factionsMs > _s.FactionsFloorMs;
            var over = new List<string>();
            foreach (PlayerView v in d.Players)
                if (v.Over && PlayerStep(v.Party) < PlayerMaxStep) over.Add(v.Party);

            if (!factionsCanGive && over.Count == 0) { d.Action = "at ceiling"; return; }
            if (!intervalDue) { d.Action = "holding (interval)"; return; }

            if (factionsCanGive)
            {
                _factionsStep++;
                d.Action = "factions step " + (_factionsStep - 1) + "->" + _factionsStep;
            }
            else
            {
                foreach (string p in over) _playerStep[p] = PlayerStep(p) + 1;
                d.Action = "players step up: " + string.Join(", ", over.ToArray());
            }
            d.Changed = true;
            _lastChange = now;
        }

        private void Release(Decision d, bool intervalDue, DateTime now)
        {
            var stepped = new List<string>();
            foreach (KeyValuePair<string, int> kv in _playerStep) if (kv.Value > 0) stepped.Add(kv.Key);
            if (stepped.Count == 0 && _factionsStep == 0) return;
            if (!intervalDue) { d.Action = "holding (interval)"; return; }

            if (stepped.Count > 0)
            {
                foreach (string p in stepped) _playerStep[p] = _playerStep[p] - 1;
                d.Action = "players step down: " + string.Join(", ", stepped.ToArray());
            }
            else
            {
                _factionsStep--;
                d.Action = "factions step " + (_factionsStep + 1) + "->" + _factionsStep;
            }
            d.Changed = true;
            _lastChange = now;
        }

        /// <summary>What Factions would be held to at the current step.</summary>
        public double FactionsTarget(double factionsMs)
        {
            if (_factionsStep <= 0) return factionsMs;
            double cut = Math.Min(1.0, _factionsStep * _s.FactionsStepPct / 100.0);
            return Math.Max(Math.Min(_s.FactionsFloorMs, factionsMs), factionsMs * (1.0 - cut));
        }

        /// <summary>Max-min fair split of <paramref name="poolMs"/>, as each player's entitlement.
        /// When the pool cannot cover everyone, the heavier players are capped at a common level
        /// and the lighter ones — using less than that level — are entitled to it. When it can,
        /// each player is entitled to what they use plus an equal cut of the slack. One player alone
        /// is entitled to the whole pool.</summary>
        public static Dictionary<string, double> FairShares(IDictionary<string, double> costs, double poolMs)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (costs == null || costs.Count == 0) return result;
            var order = new List<KeyValuePair<string, double>>(costs);
            order.Sort((a, b) => a.Value.CompareTo(b.Value));
            double remaining = Math.Max(0.0, poolMs);
            for (int i = 0; i < order.Count; i++)
            {
                double level = remaining / (order.Count - i);
                double cost = Math.Max(0.0, order[i].Value);
                if (cost > level)
                {
                    foreach (KeyValuePair<string, double> kv in order) result[kv.Key] = level;
                    return result;
                }
                remaining -= cost;
            }
            double slack = remaining / order.Count;
            foreach (KeyValuePair<string, double> kv in order) result[kv.Key] = Math.Max(0.0, kv.Value) + slack;
            return result;
        }
    }
}
