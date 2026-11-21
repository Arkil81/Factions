using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>An agent's request that the authority hold one hull of a node's stock for a
    /// withdrawal its player started there (<see cref="NexusHullHolds"/>).</summary>
    public sealed class NexusHullAsk
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        /// <summary>The agent's number for the ask, echoed in the answer.</summary>
        public long Id;
        public string Node;
        public string Prefab;
        /// <summary>The caller's SE faction tag on the agent.</summary>
        public string Tag;
        public long Identity;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusHullAsk ask)
        {
            ask = NexusHandsCodec.Decode<NexusHullAsk>(payload, NexusHandsCodec.Lean);
            if (ask == null || ask.Version != CurrentVersion) { ask = null; return false; }
            return true;
        }
    }

    /// <summary>The authority's answer to a <see cref="NexusHullAsk"/>: the hold's number, or why
    /// there is none.</summary>
    public sealed class NexusHullAnswer
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public long Id;
        public bool Ok;
        /// <summary>Zero when refused.</summary>
        public long Hold;
        public string Node;
        public string Prefab;
        /// <summary>What to tell the player when refused.</summary>
        public string Message;
        /// <summary>The node's stock of the design once the hold was taken.</summary>
        public int Remaining;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusHullAnswer answer)
        {
            answer = NexusHandsCodec.Decode<NexusHullAnswer>(payload, NexusHandsCodec.Lean);
            if (answer == null || answer.Version != CurrentVersion) { answer = null; return false; }
            return true;
        }
    }

    /// <summary>How a hold ended (<see cref="NexusHullHolds.Settle"/>).</summary>
    public enum NexusHullSettled
    {
        /// <summary>Withdrawn: the hull left the stock when the hold was taken.</summary>
        Withdrawn,
        /// <summary>Released: the hull is back in the stock.</summary>
        Released,
        /// <summary>The hold had lapsed and its hull gone back before the agent settled it.</summary>
        Lapsed,
        /// <summary>No such hold: taken before this authority's restart.</summary>
        Unknown,
    }

    /// <summary>
    /// Authority: the hulls held for withdrawals staged on agents. A withdrawal on an agent asks
    /// here first, and the hull leaves the node's stock when the hold is taken, so two withdrawals of
    /// the last hull, from one agent or from two, cannot both pass. The agent settles the hold
    /// through its store events: withdrawn, or released when staging failed. A hold the agent never
    /// settles goes back to the stock after <see cref="Life"/>.
    /// </summary>
    public sealed class NexusHullHolds
    {
        public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);
        private const int LapsedKept = 256;

        public struct Hold
        {
            public long Id;
            public byte Home;
            public string Node;
            public string Prefab;
            public DateTime Utc;
        }

        private readonly Dictionary<long, Hold> _holds = new Dictionary<long, Hold>();
        private readonly HashSet<long> _lapsed = new HashSet<long>();
        private readonly Queue<long> _lapsedOrder = new Queue<long>();
        private long _last;

        public int Count { get { return _holds.Count; } }

        /// <summary>Takes one hull of <paramref name="ask"/>'s design out of its node's stock and
        /// answers its hold, or refuses with the reason the player is told.</summary>
        public NexusHullAnswer Take(NexusHullAsk ask, byte home, SpatialGraph graph, DateTime nowUtc)
        {
            var answer = new NexusHullAnswer { Id = ask != null ? ask.Id : 0, Node = ask != null ? ask.Node : null, Prefab = ask != null ? ask.Prefab : null };
            SpatialNode node;
            if (ask == null || string.IsNullOrEmpty(ask.Prefab) || graph == null || ask.Node == null
                || !graph.Nodes.TryGetValue(ask.Node, out node) || node == null)
            {
                answer.Message = "That station no longer exists.";
                return answer;
            }
            if (ask.Tag == null || !string.Equals(node.OwnerFactionTag, ask.Tag, StringComparison.OrdinalIgnoreCase))
            {
                answer.Message = "Your faction does not own " + node.Id + ".";
                return answer;
            }
            int stock, listed;
            node.ShipStock.TryGetValue(ask.Prefab, out stock);
            node.ListedForSale.TryGetValue(ask.Prefab, out listed);
            if (ShipAssetRules.WithdrawableCount(stock, listed) <= 0)
            {
                answer.Message = "No " + ask.Prefab + " is free to withdraw at " + node.Id + ".";
                return answer;
            }
            if (stock > 1) node.ShipStock[ask.Prefab] = stock - 1;
            else node.ShipStock.Remove(ask.Prefab);

            _last = Math.Max(_last + 1, nowUtc.Ticks);
            _holds[_last] = new Hold { Id = _last, Home = home, Node = node.Id, Prefab = ask.Prefab, Utc = nowUtc };
            answer.Ok = true;
            answer.Hold = _last;
            answer.Remaining = stock - 1;
            return answer;
        }

        /// <summary>Ends hold <paramref name="id"/>: withdrawn, or released and its hull returned.
        /// A release of a hold this authority does not know returns the hull too, since the hold was
        /// taken out of a stock that has been saved without it since.</summary>
        public NexusHullSettled Settle(long id, bool withdrawn, SpatialGraph graph, string nodeId, string prefab)
        {
            Hold h;
            if (_holds.TryGetValue(id, out h))
            {
                _holds.Remove(id);
                if (!withdrawn) Return(graph, h.Node, h.Prefab);
                return withdrawn ? NexusHullSettled.Withdrawn : NexusHullSettled.Released;
            }
            if (_lapsed.Contains(id)) return NexusHullSettled.Lapsed;
            if (!withdrawn) Return(graph, nodeId, prefab);
            return NexusHullSettled.Unknown;
        }

        /// <summary>Returns every hold older than <see cref="Life"/> to its stock; null when none.</summary>
        public List<Hold> Lapse(SpatialGraph graph, DateTime nowUtc)
        {
            List<Hold> lapsed = null;
            foreach (KeyValuePair<long, Hold> kv in _holds)
                if (nowUtc - kv.Value.Utc > Life) (lapsed ?? (lapsed = new List<Hold>())).Add(kv.Value);
            if (lapsed == null) return null;
            for (int i = 0; i < lapsed.Count; i++)
            {
                Hold h = lapsed[i];
                _holds.Remove(h.Id);
                Return(graph, h.Node, h.Prefab);
                _lapsed.Add(h.Id);
                _lapsedOrder.Enqueue(h.Id);
                while (_lapsedOrder.Count > LapsedKept) _lapsed.Remove(_lapsedOrder.Dequeue());
            }
            return lapsed;
        }

        private static void Return(SpatialGraph graph, string nodeId, string prefab)
        {
            SpatialNode node;
            if (graph == null || nodeId == null || string.IsNullOrEmpty(prefab)
                || !graph.Nodes.TryGetValue(nodeId, out node) || node == null) return;
            int stock;
            node.ShipStock.TryGetValue(prefab, out stock);
            node.ShipStock[prefab] = stock + 1;
        }
    }
}
