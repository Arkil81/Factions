using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public enum NexusStoreEventKind : byte
    {
        /// <summary>An item or gas bought from or sold to the store block.</summary>
        Trade = 1,
        /// <summary>A shelf hull bought and spawned.</summary>
        Ship = 2,
        Salvage = 3,
        Repair = 4,
        Expedite = 5,
        /// <summary>Wreckage the agent's debris sweep closed near a node, for the node's stock.</summary>
        Debris = 6,
        /// <summary>Node stock a player carried in at a block, or a withdrawal's units that did not
        /// fit and go back: <see cref="NexusStoreEvent.Amount"/> of <see cref="NexusStoreEvent.Key"/>
        /// is credited to the node.</summary>
        Stock = 7,
        /// <summary>A player faction's truce request to a Factions-managed NPC, made on the vanilla
        /// Factions screen and judged on the agent: <see cref="NexusStoreEvent.Seller"/> is the player
        /// faction's tag, <see cref="NexusStoreEvent.Key"/> the NPC's, <see cref="NexusStoreEvent.Buy"/>
        /// true to offer peace and false to withdraw the offer, <see cref="NexusStoreEvent.Buyer"/>
        /// the sender's identity. No node.</summary>
        Diplomacy = 8,
        /// <summary>A registered player-faction member's personal reputation conduct toward an NPC
        /// faction, summed per member, faction, reason and propagation since the last report:
        /// <see cref="NexusStoreEvent.Key"/> the NPC's tag, <see cref="NexusStoreEvent.Amount"/> the
        /// raw delta SE was asked for, <see cref="NexusStoreEvent.Blocks"/> the SE reason as a number,
        /// <see cref="NexusStoreEvent.Buy"/> whether it propagates, <see cref="NexusStoreEvent.Buyer"/>
        /// the member's identity here. No node.</summary>
        Conduct = 9,
        /// <summary>A Services Terminal refuel, rearm or restore paid for at the station:
        /// <see cref="NexusStoreEvent.Parts"/> the units delivered per key, out of node stock,
        /// <see cref="NexusStoreEvent.Credits"/> what the player paid, <see cref="NexusStoreEvent.Seller"/>
        /// the service ("refuel", "rearm", "restore").</summary>
        Supply = 10,
        /// <summary>A hull out of a player faction's node stock into grid storage (<see cref="NexusStoreEvent.Buy"/>
        /// true), or a ship sent into it (false) with <see cref="NexusStoreEvent.Parts"/> its cargo,
        /// credited to the node. <see cref="NexusStoreEvent.Prefab"/> is the hull's design. With
        /// <see cref="NexusStoreEvent.Hold"/> set, it settles that hold (<see cref="NexusHullHolds"/>):
        /// withdrawn when <see cref="NexusStoreEvent.Buy"/>, released when not.</summary>
        Hull = 11,
    }

    /// <summary>
    /// Something a player did at a Factions station's store or Services Terminal on a Nexus agent,
    /// for the authority to book into node stock, the faction ledger and reputation as it books the
    /// same thing on its own stations. The agent already moved the goods and the wallets.
    /// </summary>
    public sealed class NexusStoreEvent : INexusSequenced
    {
        public long Seq;
        public NexusStoreEventKind Kind;
        /// <summary>The station's node; null for <see cref="NexusStoreEventKind.Ship"/>, whose hull
        /// may be held at any node of the seller.</summary>
        public string Node;
        /// <summary>Trade: the inventory key.</summary>
        public string Key;
        /// <summary>Trade: units moved (litres for gas).</summary>
        public long Amount;
        /// <summary>Trade: the player bought from the station.</summary>
        public bool Buy;
        /// <summary>Trade: the faction wallet's change. Services: what the player paid or was paid,
        /// always positive.</summary>
        public long Credits;
        /// <summary>The player's identity on the agent.</summary>
        public long Buyer;
        /// <summary>The player's Steam id, from which the authority resolves its own identity.</summary>
        public ulong Steam;
        /// <summary>Ship: the selling faction's tag.</summary>
        public string Seller;
        /// <summary>Ship: the prefab bought.</summary>
        public string Prefab;
        /// <summary>Salvage and debris: blocks closed, for the log line.</summary>
        public int Blocks;
        /// <summary>Debris: wrecks closed.</summary>
        public int Grids;
        /// <summary>Salvage and debris: present components; repair: the missing ones the job consumed.</summary>
        public Dictionary<string, long> Parts;
        /// <summary>Salvage and debris: cargo aboard the wreck.</summary>
        public Dictionary<string, long> Cargo;
        /// <summary>Hull: the authority's hold the withdrawal was staged under; zero for none.</summary>
        public long Hold;

        long INexusSequenced.Sequence { get { return Seq; } set { Seq = value; } }
    }

    /// <summary>One shelf hull row as the authority prices it.</summary>
    public sealed class NexusShelfShip
    {
        public string Prefab;
        public int Count;
        public long Price;
        public int Pcu;
    }

    /// <summary>Authority to agent: everything one station's store block carries, replacing what it
    /// carries now. No rows and no ships is an empty store.</summary>
    public sealed class NexusShelf
    {
        public string Node;
        /// <summary>The owning faction, which a shelf hull sale names as the seller.</summary>
        public string Seller;
        public List<StationListing> Rows;
        public List<NexusShelfShip> Ships;
    }

    /// <summary>
    /// Which standing agent stations the authority sends a shelf for in this reply: one that newly
    /// stands, one a booked trade changed, and every one each <c>every</c>, which also heals a lost
    /// reply. A new agent session sends them all again.
    /// </summary>
    public sealed class NexusShelfSchedule
    {
        private sealed class Home
        {
            public string Session;
            public DateTime FullUtc = DateTime.MinValue;
            public HashSet<string> Sent = new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly Dictionary<byte, Home> _homes = new Dictionary<byte, Home>();
        private readonly HashSet<string> _dirty = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Sends this node's shelf in the next reply that has it standing.</summary>
        public void MarkDirty(string node)
        {
            if (node != null) _dirty.Add(node);
        }

        public List<string> Due(byte home, string session, IEnumerable<string> standing, DateTime nowUtc, TimeSpan every)
        {
            Home h;
            if (!_homes.TryGetValue(home, out h) || h.Session != session)
            {
                h = new Home { Session = session };
                _homes[home] = h;
            }
            bool full = nowUtc - h.FullUtc >= every;
            var due = new List<string>();
            var current = new HashSet<string>(StringComparer.Ordinal);
            if (standing != null)
                foreach (string node in standing)
                {
                    if (node == null || !current.Add(node)) continue;
                    if (full || !h.Sent.Contains(node) || _dirty.Contains(node)) due.Add(node);
                }
            for (int i = 0; i < due.Count; i++) _dirty.Remove(due[i]);
            // A station that stops standing is forgotten, so standing again sends it again.
            h.Sent = current;
            if (full) h.FullUtc = nowUtc;
            return due;
        }
    }

    /// <summary>The authority's mark of each agent's store events.</summary>
    public sealed class NexusStoreInbox : NexusInbox<NexusStoreEvent>
    {
    }

    /// <summary>
    /// An agent's conduct events between reports, summed per member, NPC faction, reason and
    /// propagation: SE's conduct hook fires once per hit in a fight, and the authority books the
    /// sum through the same path as one call (<see cref="NexusStoreEventKind.Conduct"/>).
    /// </summary>
    public sealed class NexusConductTally
    {
        private readonly Dictionary<string, NexusStoreEvent> _rows = new Dictionary<string, NexusStoreEvent>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public int Count { get { return _rows.Count; } }

        public void Add(long identityId, ulong steamId, string npcTag, int delta, int reason, bool propagate)
        {
            if (delta == 0 || string.IsNullOrEmpty(npcTag)) return;
            string key = identityId + "|" + npcTag.ToUpperInvariant() + "|" + reason + "|" + (propagate ? "1" : "0");
            NexusStoreEvent e;
            if (!_rows.TryGetValue(key, out e))
            {
                e = new NexusStoreEvent
                {
                    Kind = NexusStoreEventKind.Conduct, Key = npcTag, Blocks = reason, Buy = propagate,
                    Buyer = identityId, Steam = steamId,
                };
                _rows[key] = e;
                _order.Add(key);
            }
            e.Amount += delta;
        }

        /// <summary>The rows in first-seen order, each clamped to an int, net-zero rows dropped; the
        /// tally is empty afterwards.</summary>
        public List<NexusStoreEvent> Drain()
        {
            var list = new List<NexusStoreEvent>(_order.Count);
            foreach (string key in _order)
            {
                NexusStoreEvent e = _rows[key];
                if (e.Amount == 0) continue;
                e.Amount = Math.Max(int.MinValue, Math.Min(int.MaxValue, e.Amount));
                list.Add(e);
            }
            _rows.Clear();
            _order.Clear();
            return list;
        }
    }
}
