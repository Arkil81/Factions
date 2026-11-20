using System;
using System.Diagnostics;

namespace Factions.Core
{
    /// <summary>
    /// One shared per-frame budget across every actor that runs inside the host's Update() call.
    /// Replaces the previous arrangement where each drain either checked a deadline it happened to
    /// receive first, minted its own fresh one, or ignored it entirely — so the frame's real cost was
    /// the sum of however many actors were due rather than a single ceiling.
    ///
    /// <para>Selection is by <em>overdue ratio</em> rather than absolute lateness, so a one-second
    /// actor a second late and a sixty-second actor a minute late rank equally: both are exactly one
    /// interval behind their own contract.</para>
    ///
    /// <para>The first admission of every frame is unconditional. That is what makes this safe to
    /// share: an earlier actor overrunning the whole budget can no longer leave a later one holding
    /// an already-expired deadline forever, which is the failure mode that made the previous attempt
    /// at a shared deadline livelock rather than merely run slowly.</para>
    ///
    /// <para>An actor is admitted at most once per frame. A resumable actor that yields stays
    /// overdue by design, so without this it would win selection repeatedly inside the same frame and
    /// consume every slot — the old drains each ran one unit per frame, and that round-robin property
    /// is worth keeping independently of the budget.</para>
    ///
    /// <para>Pure policy: no SE types, no delegates into game code, and no allocation once
    /// registration closes. The host owns dispatch; this owns only who runs and whether there is room.</para>
    /// </summary>
    public sealed class FrameScheduler
    {
        // Rising fast and decaying slowly keeps the estimate near an actor's recent worst case rather
        // than its mean, which is what admission actually needs to know: the question is "can I
        // afford to also run this", not "what does it usually cost". Over-estimating costs at most
        // one frame of latency, since the actor wins selection again next frame; under-estimating
        // costs an over-budget frame.
        private const double AlphaUp = 0.50;
        private const double AlphaDown = 0.125;
        private const double MinEstimateMilliseconds = 0.01;
        private const double MaxEstimateMilliseconds = 250.0;

        // Never hand out a deadline so small the actor cannot make progress against it.
        private const double MinUnitAllowanceMilliseconds = 0.10;

        // Guards against an interval of zero turning the overdue ratio into a division by zero.
        private const double MinIntervalSeconds = 0.001;

        // Belt and braces against a pathological run of very cheap actors monopolising a frame.
        private const int DefaultMaxUnitsPerFrame = 8;

        private readonly long _frequency;
        private readonly Func<long> _timestamp;

        private FrameActorSpec[] _specs = new FrameActorSpec[0];
        private double[] _intervalSeconds = new double[0];
        private double[] _maxDebtSeconds = new double[0];
        private DateTime[] _nextDueUtc = new DateTime[0];
        private DateTime?[] _sweepDueUtc = new DateTime?[0];
        private double[] _estimateMs = new double[0];
        private double[] _estErrorAvgMs = new double[0];
        private double[] _estErrorMaxMs = new double[0];
        private double[] _maxDeferralMs = new double[0];
        private double[] _lastForcedOverrunMs = new double[0];
        private long[] _admissions = new long[0];
        private long[] _completions = new long[0];
        private long[] _yields = new long[0];
        private long[] _skips = new long[0];
        private long[] _notDue = new long[0];
        private long[] _forcedAdmissions = new long[0];
        private long[] _deniedByBudget = new long[0];
        private FrameTimingWindow[] _windows = new FrameTimingWindow[0];
        private bool[] _admittedThisFrameFlag = new bool[0];
        private DateTime[] _lastAdmittedUtc = new DateTime[0];
        private int _count;

        private bool _sharedBudgetEnabled = true;
        private int _maxUnitsPerFrame = DefaultMaxUnitsPerFrame;

        private long _frameStartTimestamp;
        private DateTime _frameNowUtc;
        private double _frameBudgetMs;
        private int _admittedThisFrame;
        private bool _frameClosed;
        private bool _inFrame;

        public FrameScheduler(long stopwatchFrequency, Func<long> timestampProvider)
        {
            if (stopwatchFrequency <= 0) throw new ArgumentOutOfRangeException("stopwatchFrequency");
            _frequency = stopwatchFrequency;
            _timestamp = timestampProvider ?? StopwatchTimestamp;
        }

        private static long StopwatchTimestamp() { return Stopwatch.GetTimestamp(); }

        public int ActorCount { get { return _count; } }
        public bool SharedBudgetEnabled { get { return _sharedBudgetEnabled; } }
        public double BudgetMilliseconds { get { return _frameBudgetMs; } }
        public int MaxUnitsPerFrame { get { return _maxUnitsPerFrame; } }

        public long FramesWithNoAdmission { get; private set; }
        public long AdmittedUnitCount { get; private set; }
        public long DeniedByBudgetCount { get; private set; }
        public long ForcedAdmissionCount { get; private set; }
        public int MaxUnitsInOneFrame { get; private set; }

        /// <summary>Registers an actor and returns the id the host dispatches on. Call before the
        /// first <see cref="BeginFrame"/>; the arrays are sized here and never grow afterwards.</summary>
        public int RegisterActor(FrameActorSpec spec)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            if (_inFrame) throw new InvalidOperationException("cannot register an actor mid-frame");

            int id = _count;
            Grow(id + 1);

            double interval = spec.IntervalSeconds > 0 ? spec.IntervalSeconds : MinIntervalSeconds;
            _specs[id] = spec;
            _intervalSeconds[id] = interval;
            _maxDebtSeconds[id] = spec.MaxDebtSeconds > 0
                ? spec.MaxDebtSeconds
                : Math.Max(4.0 * interval, 2.0);
            _estimateMs[id] = Clamp(spec.SeedCostMilliseconds, MinEstimateMilliseconds, MaxEstimateMilliseconds);
            _nextDueUtc[id] = DateTime.MinValue;
            _sweepDueUtc[id] = null;
            _lastAdmittedUtc[id] = DateTime.MinValue;
            _windows[id] = new FrameTimingWindow(_frequency, 0.5, 4096);
            _count = id + 1;
            return id;
        }

        public void SetInterval(int actorId, double intervalSeconds)
        {
            double interval = intervalSeconds > 0 ? intervalSeconds : MinIntervalSeconds;
            _intervalSeconds[actorId] = interval;
            if (_specs[actorId].MaxDebtSeconds <= 0)
                _maxDebtSeconds[actorId] = Math.Max(4.0 * interval, 2.0);
        }

        /// <summary>Rollback lever. When disabled every admission is treated as a first admission, so
        /// nothing is ever denied for want of budget — reproducing the previous "every actor gets its
        /// own shot" behaviour through this same code path rather than a second scheduler.</summary>
        public void SetSharedBudgetEnabled(bool enabled) { _sharedBudgetEnabled = enabled; }

        public void SetMaxUnitsPerFrame(int max)
        {
            _maxUnitsPerFrame = max > 0 ? max : DefaultMaxUnitsPerFrame;
        }

        public void BeginFrame(long frameStartTimestamp, DateTime nowUtc, double budgetMilliseconds)
        {
            _frameStartTimestamp = frameStartTimestamp;
            _frameNowUtc = nowUtc;
            _frameBudgetMs = budgetMilliseconds;
            _admittedThisFrame = 0;
            _frameClosed = false;
            _inFrame = true;
            for (int i = 0; i < _count; i++) _admittedThisFrameFlag[i] = false;

            // A never-scheduled actor is due immediately, offset so equal-interval actors do not
            // co-land on the same frame for the rest of the session.
            for (int i = 0; i < _count; i++)
                if (_nextDueUtc[i] == DateTime.MinValue)
                    _nextDueUtc[i] = nowUtc.AddSeconds(_specs[i].InitialPhaseOffsetSeconds);
        }

        /// <summary>Picks the most-overdue-relative-to-its-own-interval actor that fits, and hands it
        /// a deadline that is guaranteed not to be already expired.</summary>
        public bool TryAdmitNext(out int actorId, out FrameDeadline unitDeadline)
        {
            actorId = -1;
            unitDeadline = default(FrameDeadline);
            if (_frameClosed || _admittedThisFrame >= _maxUnitsPerFrame) return false;

            int skip = -1;
            for (int probe = 0; probe < _count; probe++)
            {
                int best = SelectMostOverdue(skip);
                if (best < 0) return false;

                double allowance;
                bool forced;
                if (!Admit(best, out allowance, out forced))
                {
                    // Denied for budget: try the next-best due actor rather than ending the frame,
                    // since a cheaper one may still fit.
                    _deniedByBudget[best]++;
                    DeniedByBudgetCount++;
                    skip = best;
                    continue;
                }

                StampSweepDue(best);
                _admittedThisFrameFlag[best] = true;
                _lastAdmittedUtc[best] = _frameNowUtc;
                _admissions[best]++;
                AdmittedUnitCount++;
                _admittedThisFrame++;
                if (_admittedThisFrame > MaxUnitsInOneFrame) MaxUnitsInOneFrame = _admittedThisFrame;
                if (forced)
                {
                    _forcedAdmissions[best]++;
                    ForcedAdmissionCount++;
                    // A forced admission is already over budget by definition; nothing else runs
                    // behind it this frame.
                    _frameClosed = true;
                }

                actorId = best;
                // Built from the real stopwatch, not the injected clock: the admitted actor checks
                // this deadline with Stopwatch.GetTimestamp(), so it has to be the same clock. The
                // injected clock exists only for this scheduler's own budget arithmetic, which tests
                // drive with simulated costs.
                unitDeadline = new FrameDeadline(Stopwatch.GetTimestamp(), allowance);
                return true;
            }
            return false;
        }

        private bool Admit(int id, out double allowance, out bool forced)
        {
            forced = false;
            double remaining = RemainingBudgetMs();

            // The unconditional first admission. Without it a fat actor running early could leave
            // every later actor permanently holding a spent budget.
            if (_admittedThisFrame == 0 || !_sharedBudgetEnabled)
            {
                allowance = Math.Max(_frameBudgetMs, MinUnitAllowanceMilliseconds);
                return true;
            }

            if (_estimateMs[id] <= remaining)
            {
                allowance = Math.Max(remaining, MinUnitAllowanceMilliseconds);
                return true;
            }

            double overdue = (_frameNowUtc - _nextDueUtc[id]).TotalSeconds;
            if (overdue > _maxDebtSeconds[id])
            {
                forced = true;
                _lastForcedOverrunMs[id] = _estimateMs[id] - remaining;
                allowance = Math.Max(_estimateMs[id], MinUnitAllowanceMilliseconds);
                return true;
            }

            allowance = 0.0;
            return false;
        }

        private int SelectMostOverdue(int skip)
        {
            // Debt outranks ratio. Without this the wall-clock bound the debt rule is supposed to
            // guarantee never actually binds: a short-interval actor that is chronically behind has
            // the highest ratio by construction, wins selection every time, and is immediately due
            // again once it runs -- so a long-interval actor can sit past its threshold forever
            // while never being the argmax. Among debt-exceeded actors the longest absolute wait
            // wins, which is the quantity the threshold is expressed in.
            int best = -1;
            double bestOverdue = 0.0;
            for (int i = 0; i < _count; i++)
            {
                if (i == skip || _admittedThisFrameFlag[i]) continue;
                if (_nextDueUtc[i] > _frameNowUtc) continue;
                double overdue = (_frameNowUtc - _nextDueUtc[i]).TotalSeconds;
                if (overdue <= _maxDebtSeconds[i]) continue;
                if (best < 0 || overdue > bestOverdue)
                {
                    best = i;
                    bestOverdue = overdue;
                }
            }
            if (best >= 0) return best;

            double bestRatio = 0.0;
            for (int i = 0; i < _count; i++)
            {
                if (i == skip || _admittedThisFrameFlag[i]) continue;
                if (_nextDueUtc[i] > _frameNowUtc) continue;
                double ratio = (_frameNowUtc - _nextDueUtc[i]).TotalSeconds
                             / Math.Max(_intervalSeconds[i], MinIntervalSeconds);
                // Ties break on the lower id so selection stays deterministic for tests.
                if (best < 0 || ratio > bestRatio)
                {
                    best = i;
                    bestRatio = ratio;
                }
            }
            return best;
        }

        private void StampSweepDue(int id)
        {
            if (_sweepDueUtc[id] != null) return;
            _sweepDueUtc[id] = _nextDueUtc[id] == DateTime.MinValue ? _frameNowUtc : _nextDueUtc[id];
        }

        /// <summary>Records what an admitted actor did and reschedules it. Returns how late this
        /// unit was relative to when the actor became due, so the host can keep emitting its
        /// familiar <c>*_SWEEP_DEFERRED</c> operator signal with correct semantics.</summary>
        public double RecordCompletion(int actorId, long elapsedTicks, FrameActorOutcome outcome, DateTime nowUtc)
        {
            if (elapsedTicks < 0) elapsedTicks = 0;
            _windows[actorId].Record(elapsedTicks);

            double sampleMs = elapsedTicks * 1000.0 / _frequency;
            double deferredMs = (nowUtc - DueBaseline(actorId)).TotalMilliseconds;
            if (deferredMs > _maxDeferralMs[actorId]) _maxDeferralMs[actorId] = deferredMs;

            if (outcome != FrameActorOutcome.Skipped) UpdateEstimate(actorId, sampleMs);

            double interval = _intervalSeconds[actorId];
            if (outcome == FrameActorOutcome.Completed)
            {
                _completions[actorId]++;
                // Reschedule from when it became due, not from now, so a sweep that ran late does
                // not push its successor out by the same amount.
                DateTime baseline = _sweepDueUtc[actorId] ?? nowUtc;
                DateTime next = baseline.AddSeconds(interval);
                _nextDueUtc[actorId] = next > nowUtc ? next : nowUtc.AddSeconds(interval);
                _sweepDueUtc[actorId] = null;
            }
            else if (outcome == FrameActorOutcome.Yielded)
            {
                // Deliberately not rescheduled: staying overdue is what keeps a mid-sweep actor
                // winning selection until it finishes, without a special case for pinning.
                _yields[actorId]++;
            }
            else
            {
                if (outcome == FrameActorOutcome.Skipped) _skips[actorId]++; else _notDue[actorId]++;
                _nextDueUtc[actorId] = nowUtc.AddSeconds(interval);
                _sweepDueUtc[actorId] = null;
            }
            return deferredMs;
        }

        private DateTime DueBaseline(int actorId)
        {
            DateTime? sweep = _sweepDueUtc[actorId];
            if (sweep != null) return sweep.Value;
            return _nextDueUtc[actorId];
        }

        private void UpdateEstimate(int id, double sampleMs)
        {
            double before = _estimateMs[id];
            double error = Math.Abs(sampleMs - before);
            _estErrorAvgMs[id] = _estErrorAvgMs[id] + AlphaDown * (error - _estErrorAvgMs[id]);
            if (error > _estErrorMaxMs[id]) _estErrorMaxMs[id] = error;

            double alpha = sampleMs > before ? AlphaUp : AlphaDown;
            _estimateMs[id] = Clamp(before + alpha * (sampleMs - before),
                MinEstimateMilliseconds, MaxEstimateMilliseconds);
        }

        public void EndFrame()
        {
            if (_admittedThisFrame == 0) FramesWithNoAdmission++;
            _inFrame = false;
        }

        /// <summary>The worst currently-overdue actor, if any has been waiting past its debt
        /// threshold. Drives the operator-facing starvation alarm.</summary>
        public bool TryGetStarvingActor(DateTime nowUtc, out int actorId, out double deferredMs)
        {
            actorId = -1;
            deferredMs = 0.0;
            for (int i = 0; i < _count; i++)
            {
                if (_nextDueUtc[i] > nowUtc) continue;
                // Starvation is measured from the last time the actor was ADMITTED, not from when it
                // became due. A resumable actor mid-sweep deliberately stays overdue, so a
                // due-based test reports every long sweep as starving even while the actor is being
                // admitted on almost every frame -- which is the opposite of starving.
                DateTime since = _lastAdmittedUtc[i];
                if (since == DateTime.MinValue) since = _nextDueUtc[i];
                double idle = (nowUtc - since).TotalSeconds;
                if (idle <= _maxDebtSeconds[i]) continue;
                double ms = idle * 1000.0;
                if (actorId < 0 || ms > deferredMs)
                {
                    actorId = i;
                    deferredMs = ms;
                }
            }
            return actorId >= 0;
        }

        public FrameActorStats GetStats(int actorId)
        {
            var s = new FrameActorStats();
            s.Name = _specs[actorId].Name;
            s.IntervalSeconds = _intervalSeconds[actorId];
            s.EstimateMilliseconds = _estimateMs[actorId];
            s.EstimateErrorAvgMilliseconds = _estErrorAvgMs[actorId];
            s.EstimateErrorMaxMilliseconds = _estErrorMaxMs[actorId];
            s.MaxDeferralMilliseconds = _maxDeferralMs[actorId];
            s.LastForcedOverrunMilliseconds = _lastForcedOverrunMs[actorId];
            s.Admissions = _admissions[actorId];
            s.Completions = _completions[actorId];
            s.Yields = _yields[actorId];
            s.Skips = _skips[actorId];
            s.NotDue = _notDue[actorId];
            s.ForcedAdmissions = _forcedAdmissions[actorId];
            s.DeniedByBudget = _deniedByBudget[actorId];
            s.SweepInProgress = _sweepDueUtc[actorId] != null;
            return s;
        }

        public FrameTimingWindow WindowOf(int actorId) { return _windows[actorId]; }

        /// <summary>Clears counters for a new telemetry window. Cost estimates and the schedule are
        /// deliberately preserved — a perf flush must not perturb scheduling.</summary>
        public void ResetWindow()
        {
            for (int i = 0; i < _count; i++)
            {
                _windows[i].Reset();
                _admissions[i] = 0;
                _completions[i] = 0;
                _yields[i] = 0;
                _skips[i] = 0;
                _notDue[i] = 0;
                _forcedAdmissions[i] = 0;
                _deniedByBudget[i] = 0;
                _maxDeferralMs[i] = 0.0;
                _estErrorMaxMs[i] = 0.0;
            }
            FramesWithNoAdmission = 0;
            AdmittedUnitCount = 0;
            DeniedByBudgetCount = 0;
            ForcedAdmissionCount = 0;
            MaxUnitsInOneFrame = 0;
        }

        private double RemainingBudgetMs()
        {
            double elapsed = (_timestamp() - _frameStartTimestamp) * 1000.0 / _frequency;
            return _frameBudgetMs - elapsed;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void Grow(int size)
        {
            if (_specs.Length >= size) return;
            int n = size;
            Array.Resize(ref _specs, n);
            Array.Resize(ref _intervalSeconds, n);
            Array.Resize(ref _maxDebtSeconds, n);
            Array.Resize(ref _nextDueUtc, n);
            Array.Resize(ref _sweepDueUtc, n);
            Array.Resize(ref _estimateMs, n);
            Array.Resize(ref _estErrorAvgMs, n);
            Array.Resize(ref _estErrorMaxMs, n);
            Array.Resize(ref _maxDeferralMs, n);
            Array.Resize(ref _lastForcedOverrunMs, n);
            Array.Resize(ref _admissions, n);
            Array.Resize(ref _completions, n);
            Array.Resize(ref _yields, n);
            Array.Resize(ref _skips, n);
            Array.Resize(ref _notDue, n);
            Array.Resize(ref _forcedAdmissions, n);
            Array.Resize(ref _deniedByBudget, n);
            Array.Resize(ref _windows, n);
            Array.Resize(ref _admittedThisFrameFlag, n);
            Array.Resize(ref _lastAdmittedUtc, n);
        }
    }
}
