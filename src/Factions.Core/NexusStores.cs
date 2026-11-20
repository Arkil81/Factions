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
}
