using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The authority's order to an agent to carry node stock into or out of one block's inventory, for
    /// a player connected there. The node's stock is the authority's and the block is the agent's, so
    /// the authority decides and the agent moves: a withdrawal is taken off the node before this is
    /// sent, a deposit is credited only when the agent reports what it removed.
    /// </summary>
    public sealed class NexusStockMove
    {
        public long Seq;
        public ulong Steam;
        public string Node;
        public string NodeName;
        /// <summary>The faction that owns the node; the target grid must be theirs.</summary>
        public string Owner;
        public string Key;
        /// <summary>Withdraw: units already taken off the node. Deposit: units asked for.</summary>
        public long Amount;
        /// <summary>Deposit: what the player asked for, for the reply's wording.</summary>
        public long Asked;
        public bool Deposit;
        /// <summary>The block whose inventory the goods move through.</summary>
        public long Target;
    }

    /// <summary>
    /// The authority's stock moves not yet acknowledged, per agent. Every reply carries all of them
    /// until the agent's report acknowledges through a sequence number. A withdrawal that lapses
    /// unacknowledged (the agent restarted, or stopped reporting) is handed back so its units can be
    /// returned to the node.
    /// </summary>
    public sealed class NexusStockMoveOutbox
    {
        private sealed class Held
        {
            public NexusStockMove Move;
            public DateTime AddedUtc;
        }

        private sealed class Queue
        {
            public string Session;
            public readonly List<Held> Items = new List<Held>();
        }

        private readonly Dictionary<byte, Queue> _byHome = new Dictionary<byte, Queue>();
        private long _last;

        public void Add(byte home, NexusStockMove move, DateTime nowUtc)
        {
            if (move == null) return;
            Queue q;
            if (!_byHome.TryGetValue(home, out q)) { q = new Queue(); _byHome[home] = q; }
            _last = Math.Max(_last + 1, nowUtc.Ticks);
            move.Seq = _last;
            q.Items.Add(new Held { Move = move, AddedUtc = nowUtc });
        }

        /// <summary>What to send <paramref name="home"/> now, oldest first; null when nothing is held.
        /// Moves at or below <paramref name="appliedThrough"/> are dropped as done. Moves older than
        /// <paramref name="maxAge"/>, or held for an earlier session of the agent, are dropped as
        /// lapsed and added to <paramref name="lapsed"/>.</summary>
        public List<NexusStockMove> Pending(byte home, string session, long appliedThrough, DateTime nowUtc,
                                            TimeSpan maxAge, List<NexusStockMove> lapsed)
        {
            Queue q;
            if (!_byHome.TryGetValue(home, out q)) return null;
            if (q.Session != session)
            {
                // Moves queued before any report from this agent was seen belong to whichever session
                // reports next; moves queued for a session that has since restarted are lapsed.
                if (q.Session != null)
                {
                    for (int i = 0; i < q.Items.Count; i++) if (lapsed != null) lapsed.Add(q.Items[i].Move);
                    q.Items.Clear();
                }
                q.Session = session;
            }
            for (int i = q.Items.Count - 1; i >= 0; i--)
            {
                Held h = q.Items[i];
                if (h.Move.Seq <= appliedThrough) { q.Items.RemoveAt(i); continue; }
                if (nowUtc - h.AddedUtc > maxAge)
                {
                    if (lapsed != null) lapsed.Add(h.Move);
                    q.Items.RemoveAt(i);
                }
            }
            if (q.Items.Count == 0) return null;
            var list = new List<NexusStockMove>(q.Items.Count);
            for (int i = 0; i < q.Items.Count; i++) list.Add(q.Items[i].Move);
            return list;
        }

        public int Count(byte home)
        {
            Queue q;
            return _byHome.TryGetValue(home, out q) ? q.Items.Count : 0;
        }
    }
}
