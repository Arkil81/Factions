using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The programmable-block limiter (docs/load-governor.md, toolbox step 3, second line). It never
    /// damages or disables a block; it has two tiers.
    ///
    /// <para><b>Slow.</b> A script whose self-scheduled runs would cost more than its cap is run on
    /// every Nth opportunity instead of every one. N is the unthrottled cost over the cap, where
    /// unthrottled cost is the measured cost of one scheduled run times how often the script asks to
    /// run. N rises at once and falls a quarter at a time, and falls only once the script is 10% under
    /// the cap at the lower N, so it does not flap at the boundary. Runs started from outside the
    /// script (terminal, timer, sensor, another script, a mod) are never skipped by this tier.</para>
    ///
    /// <para><b>Stop.</b> A script whose average run stays above <see cref="Settings.StopRunMs"/>
    /// for <see cref="Settings.StopWindows"/> consecutive windows runs not at all, scheduled or
    /// external, for <see cref="Settings.StopSeconds"/>, and is then retried from scratch. A slowdown
    /// bounds a script's share of the frame but never the length of one run, and a run that long is a
    /// visible stall. One long run, such as a script's first after spawn, does not stop it: it has to
    /// last whole windows. A recompile retries at once (<see cref="Reset"/>).</para>
    ///
    /// <para>Operator, 2026-09-24: cap 0.25 ms/frame per block; pressure cap 0.1 when the governor
    /// picks a player's programmable blocks at step 3 (or Factions' from its step 2); stop at runs
    /// averaging over 10 ms for 60 s.</para>
    ///
    /// <para><b>Threads.</b> The hot-path calls come from the game thread, but a mod may start a script
    /// from anywhere, so every entry point takes the one lock.</para>
    /// </summary>
    public sealed class PbLimiter
    {
        public sealed class Settings
        {
            public double CapMsPerFrame = 0.25;
            public double PressureCapMsPerFrame = 0.1;
            public double StopRunMs = 10.0;
            public int StopWindows = 2;
            public double StopSeconds = 600.0;
            /// <summary>Slowest the slow tier goes: once per this many opportunities.</summary>
            public int MaxDivisor = 600;
        }

        public enum State { Normal, Slowed, Stopped }

        public sealed class BlockView
        {
            public long BlockId;
            public long GridId;
            public State State;
            public int Divisor;
            /// <summary>This window's average ms per run, scheduled and external together.</summary>
            public double MsPerRun;
        }

        public sealed class Change
        {
            public long BlockId;
            public long GridId;
            public State From;
            public State To;
            public int Divisor;
            public double MsPerRun;
        }

        private sealed class Block
        {
            public long GridId;
            public int Divisor = 1;
            public int Skip;
            public int OverStopWindows;
            public DateTime StoppedUntil;
            public State State;
            public double LastMsPerRun;
            // window
            public double ScheduledMs;
            public double ExternalMs;
            public long ScheduledRuns;
            public long ExternalRuns;
            public long Opportunities;
        }

        private readonly Settings _s;
        private readonly object _sync = new object();
        private readonly Dictionary<long, Block> _blocks = new Dictionary<long, Block>();

        public PbLimiter(Settings settings)
        {
            _s = settings ?? new Settings();
        }

        public Settings Config { get { return _s; } }

        /// <summary>A script asks for a scheduled run. True runs it; false skips it, and the caller
        /// carries the run's update flags into the next opportunity.</summary>
        public bool AllowScheduled(long blockId)
        {
            lock (_sync)
            {
                Block b;
                if (!_blocks.TryGetValue(blockId, out b))
                {
                    b = new Block();
                    _blocks[blockId] = b;
                }
                b.Opportunities++;
                if (b.State == State.Stopped) return false;
                if (b.Divisor <= 1) return true;
                if (++b.Skip >= b.Divisor) { b.Skip = 0; return true; }
                return false;
            }
        }

        /// <summary>Whether any run at all may execute.</summary>
        public bool IsStopped(long blockId)
        {
            lock (_sync)
            {
                Block b;
                return _blocks.TryGetValue(blockId, out b) && b.State == State.Stopped;
            }
        }

        /// <summary>A run that executed, and what it cost.</summary>
        public void RecordRun(long blockId, long gridId, double ms, bool external)
        {
            lock (_sync)
            {
                Block b;
                if (!_blocks.TryGetValue(blockId, out b))
                {
                    b = new Block();
                    _blocks[blockId] = b;
                }
                b.GridId = gridId;
                if (external) { b.ExternalMs += ms; b.ExternalRuns++; }
                else { b.ScheduledMs += ms; b.ScheduledRuns++; }
            }
        }

        /// <summary>A recompile: the script is new, so whatever the limiter held against the old one
        /// no longer applies.</summary>
        public Change Reset(long blockId)
        {
            lock (_sync)
            {
                Block b;
                if (!_blocks.TryGetValue(blockId, out b)) return null;
                _blocks.Remove(blockId);
                return b.State == State.Normal ? null
                    : new Change { BlockId = blockId, GridId = b.GridId, From = b.State, To = State.Normal, Divisor = 1 };
            }
        }

        /// <summary>Closes one telemetry window: sets each block's tier and returns the blocks whose
        /// tier changed.</summary>
        /// <param name="frames">Frames in the window.</param>
        /// <param name="capForGrid">The per-block cap for a block on this grid, ms per frame.</param>
        public List<Change> Evaluate(long frames, DateTime now, Func<long, double> capForGrid)
        {
            var changes = new List<Change>();
            lock (_sync)
            {
                var forget = new List<long>();
                foreach (KeyValuePair<long, Block> kv in _blocks)
                {
                    Block b = kv.Value;
                    State before = b.State;
                    long runs = b.ScheduledRuns + b.ExternalRuns;
                    b.LastMsPerRun = runs > 0 ? (b.ScheduledMs + b.ExternalMs) / runs : 0.0;

                    if (b.State == State.Stopped)
                    {
                        if (now >= b.StoppedUntil)
                        {
                            b.State = State.Normal;
                            b.Divisor = 1;
                            b.Skip = 0;
                            b.OverStopWindows = 0;
                        }
                    }
                    else
                    {
                        if (runs > 0 && b.LastMsPerRun > _s.StopRunMs) b.OverStopWindows++;
                        else if (runs > 0) b.OverStopWindows = 0;

                        if (b.OverStopWindows >= _s.StopWindows)
                        {
                            b.State = State.Stopped;
                            b.StoppedUntil = now.AddSeconds(_s.StopSeconds);
                            b.OverStopWindows = 0;
                        }
                        else
                        {
                            double cap = capForGrid != null ? capForGrid(b.GridId) : _s.CapMsPerFrame;
                            b.Divisor = NextDivisor(b, frames, cap);
                            b.State = b.Divisor > 1 ? State.Slowed : State.Normal;
                        }
                    }

                    if (b.State != before)
                        changes.Add(new Change
                        {
                            BlockId = kv.Key, GridId = b.GridId, From = before, To = b.State,
                            Divisor = b.Divisor, MsPerRun = b.LastMsPerRun,
                        });

                    bool idle = b.Opportunities == 0 && runs == 0;
                    b.ScheduledMs = b.ExternalMs = 0;
                    b.ScheduledRuns = b.ExternalRuns = b.Opportunities = 0;
                    if (idle && b.State == State.Normal) forget.Add(kv.Key);
                }
                foreach (long id in forget) _blocks.Remove(id);
            }
            return changes;
        }

        private int NextDivisor(Block b, long frames, double cap)
        {
            if (frames <= 0 || cap <= 0 || b.ScheduledRuns == 0) return b.Divisor;
            double perRun = b.ScheduledMs / b.ScheduledRuns;
            double unthrottled = perRun * b.Opportunities / frames;
            int up = Clamp((int)Math.Ceiling(unthrottled / cap));
            if (up > b.Divisor) return up;
            int down = Clamp((int)Math.Ceiling(unthrottled / (cap * 0.9)));
            if (down < b.Divisor) return Math.Max(down, b.Divisor - Math.Max(1, b.Divisor / 4));
            return b.Divisor;
        }

        private int Clamp(int n)
        {
            if (n < 1) return 1;
            return n > _s.MaxDivisor ? _s.MaxDivisor : n;
        }

        /// <summary>Every block the limiter currently slows or stops.</summary>
        public List<BlockView> Limited()
        {
            var list = new List<BlockView>();
            lock (_sync)
            {
                foreach (KeyValuePair<long, Block> kv in _blocks)
                    if (kv.Value.State != State.Normal)
                        list.Add(new BlockView
                        {
                            BlockId = kv.Key, GridId = kv.Value.GridId, State = kv.Value.State,
                            Divisor = kv.Value.Divisor, MsPerRun = kv.Value.LastMsPerRun,
                        });
            }
            return list;
        }

        public State StateOf(long blockId)
        {
            lock (_sync)
            {
                Block b;
                return _blocks.TryGetValue(blockId, out b) ? b.State : State.Normal;
            }
        }
    }
}
