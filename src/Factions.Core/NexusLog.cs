using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One DecisionLog line written on a Nexus agent, for the authority's DecisionLog.</summary>
    public sealed class NexusLogLine : INexusSequenced
    {
        public long Seq;
        /// <summary>When the agent wrote it, UTC: the authority files it under this time, not arrival.</summary>
        public DateTime At;
        public string Text;

        long INexusSequenced.Sequence { get { return Seq; } set { Seq = value; } }
    }

    /// <summary>
    /// An agent's DecisionLog lines not yet acknowledged. Unlike the event outboxes this one is
    /// bounded twice: a report carries at most a slice of it, so a backlog built while the
    /// authority was away drains over several reports rather than riding every one whole; and it
    /// holds at most <see cref="Capacity"/> lines, past which new ones are counted and dropped, and
    /// the count is queued as a line of its own once there is room.
    ///
    /// <para>Locked: a DecisionLog is written from any thread.</para>
    /// </summary>
    public sealed class NexusLogOutbox
    {
        public const int Capacity = 20000;
        public const int SliceLines = 500;

        private readonly object _gate = new object();
        private readonly List<NexusLogLine> _unacked = new List<NexusLogLine>();
        private long _seq;
        private int _dropped;

        public void Add(DateTime atUtc, string text)
        {
            if (text == null) return;
            lock (_gate)
            {
                if (_unacked.Count >= Capacity) { _dropped++; return; }
                if (_dropped > 0)
                {
                    _unacked.Add(new NexusLogLine { Seq = ++_seq, At = atUtc, Text = "DECISION_LOG_DROPPED lines=" + _dropped });
                    _dropped = 0;
                }
                _unacked.Add(new NexusLogLine { Seq = ++_seq, At = atUtc, Text = text });
            }
        }

        /// <summary>The oldest unacknowledged lines, at most <paramref name="max"/>; null when none.</summary>
        public List<NexusLogLine> Pending(int max = SliceLines)
        {
            lock (_gate)
            {
                if (_unacked.Count == 0) return null;
                int n = Math.Min(_unacked.Count, Math.Max(1, max));
                return _unacked.GetRange(0, n);
            }
        }

        public void Ack(long through)
        {
            if (through <= 0) return;
            lock (_gate) { _unacked.RemoveAll(l => l.Seq <= through); }
        }

        public int Count { get { lock (_gate) { return _unacked.Count; } } }
        public int Dropped { get { lock (_gate) { return _dropped; } } }
    }
}
