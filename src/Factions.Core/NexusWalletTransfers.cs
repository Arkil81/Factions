using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>An agent-to-authority event booked exactly once: numbered in the order it happened,
    /// re-sent until acknowledged.</summary>
    public interface INexusSequenced
    {
        long Sequence { get; set; }
    }

    /// <summary>
    /// A faction-terminal Deposit or Withdraw made on a Nexus agent, for the authority's ledger. Nexus
    /// replicates the wallet change to every shard, but the authority's treasury mirror treats a
    /// wallet change its ledger never booked as drift and pushes it back; the agent has no ledger, so
    /// it books the transfer on the authority instead.
    /// </summary>
    public sealed class NexusWalletTransfer : INexusSequenced
    {
        public long Seq;
        /// <summary>The faction tag debited; null when that end is a person.</summary>
        public string From;
        /// <summary>The faction tag credited; null when that end is a person.</summary>
        public string To;
        public long Amount;

        long INexusSequenced.Sequence { get { return Seq; } set { Seq = value; } }
    }

    /// <summary>
    /// An agent's events not yet acknowledged, numbered in the order they happened. Each report
    /// carries all of them; the authority's reply acknowledges through a sequence number.
    /// </summary>
    public class NexusOutbox<T> where T : class, INexusSequenced
    {
        private readonly List<T> _unacked = new List<T>();
        private long _seq;

        /// <summary>Numbers <paramref name="item"/> and queues it.</summary>
        public void Add(T item)
        {
            item.Sequence = ++_seq;
            _unacked.Add(item);
        }

        /// <summary>Every unacknowledged event, oldest first; null when there are none.</summary>
        public List<T> Pending()
        {
            return _unacked.Count > 0 ? new List<T>(_unacked) : null;
        }

        public void Ack(long through)
        {
            if (through > 0) _unacked.RemoveAll(t => t.Sequence <= through);
        }

        public int Count { get { return _unacked.Count; } }
    }

    /// <summary>
    /// The authority's high-water mark of an event stream per agent. A new agent session starts the
    /// count again; an event at or below the mark is a re-send and is not applied twice.
    /// </summary>
    public class NexusInbox<T> where T : class, INexusSequenced
    {
        private sealed class Mark
        {
            public string Session;
            public long Through;
        }

        private readonly Dictionary<byte, Mark> _byHome = new Dictionary<byte, Mark>();

        /// <summary>The events not yet applied, in order; <paramref name="through"/> is what to
        /// acknowledge (0 when the agent sent none).</summary>
        public List<T> Accept(byte home, string session, IList<T> events, out long through)
        {
            var fresh = new List<T>();
            Mark m;
            if (!_byHome.TryGetValue(home, out m) || m.Session != session)
            {
                m = new Mark { Session = session };
                _byHome[home] = m;
            }
            if (events != null)
                for (int i = 0; i < events.Count; i++)
                {
                    T e = events[i];
                    if (e == null || e.Sequence <= m.Through) continue;
                    fresh.Add(e);
                    m.Through = e.Sequence;
                }
            through = events != null && events.Count > 0 ? m.Through : 0;
            return fresh;
        }
    }

    /// <summary>An agent's faction-terminal transfers not yet acknowledged.</summary>
    public sealed class NexusTransferOutbox : NexusOutbox<NexusWalletTransfer>
    {
        /// <summary>Queues a transfer with at least one faction end; false when neither end is one.</summary>
        public bool Add(string from, string to, long amount)
        {
            if (amount <= 0 || (from == null && to == null)) return false;
            Add(new NexusWalletTransfer { From = from, To = to, Amount = amount });
            return true;
        }
    }

    /// <summary>The authority's mark of each agent's transfers.</summary>
    public sealed class NexusTransferInbox : NexusInbox<NexusWalletTransfer>
    {
    }
}
