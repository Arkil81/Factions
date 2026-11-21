using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a capture on a Nexus agent is of.</summary>
    public enum NexusCaptureKind
    {
        Ship = 1,
        Station = 2,
        /// <summary>An admin's ship capture for one faction, under its livery.</summary>
        Livery = 3,
        /// <summary>No grid: which design a ship is, and the version a scan of it would publish.</summary>
        PreviewShip = 4,
        /// <summary>No grid: the same for the station a terminal stands on.</summary>
        PreviewStation = 5,
        /// <summary>A whole ship registered as its own restoration design, its fee paid to the owner
        /// of the node it was registered at. Unlike a capture it names nothing and enters no catalogue.</summary>
        Design = 6,
    }

    /// <summary>Where the player asked, which is where the answer goes.</summary>
    public enum NexusCaptureSurface
    {
        Chat = 1,
        TerminalShip = 2,
        TerminalStation = 3,
        DesignQuery = 4,
    }

    /// <summary>
    /// A capture made on a Nexus agent, for the authority to register. The grid is the agent's, so
    /// the agent runs every check that reads it -- access, static or not, the block cap, the root
    /// grid -- and sends the result; the catalogue, the recipe and the store are the authority's,
    /// so it names the design, checks the name and registers it. Its answer is a
    /// <see cref="NexusCaptureResult"/> with the same <see cref="Id"/>.
    /// </summary>
    public sealed class NexusCaptureUpload
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public long Id;
        public int Kind;
        public int Surface;
        public ulong Steam;
        /// <summary>The caller's identity on the agent.</summary>
        public long Identity;
        /// <summary>The caller's faction; for a livery capture, the faction it is for.</summary>
        public string Owner;
        public string Scope;
        public string Name;
        public int Blocks;
        /// <summary>Where the root grid stood, km. A station's site is measured from it.</summary>
        public double Px, Py, Pz;
        /// <summary>The node whose standing station was scanned, when it was one; null otherwise.</summary>
        public string AdoptNode;
        /// <summary>The design the agent's own records name for the grid (a fleet hull's prefab),
        /// for a preview; null when it has none.</summary>
        public string KnownId;
        /// <summary>The grid's display name, which the authority's catalogue may recognise.</summary>
        public string DisplayName;
        /// <summary>Design: the registered grid's entity id, which the design is keyed to.</summary>
        public long EntityId;
        /// <summary>Design: the fingerprint of the shape registered.</summary>
        public string Fingerprint;
        /// <summary>Design: the fee the payer was charged on the agent, owed to the node's owner.</summary>
        public long Fee;
        /// <summary>The grids as an SE definitions document; null for a preview. The frame's own
        /// compression carries it.</summary>
        public string GridsXml;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusCaptureUpload upload)
        {
            upload = NexusHandsCodec.Decode<NexusCaptureUpload>(payload, NexusHandsCodec.Lean);
            if (upload == null || upload.Version != CurrentVersion) { upload = null; return false; }
            return true;
        }
    }

    /// <summary>The authority's answer to a <see cref="NexusCaptureUpload"/>.</summary>
    public sealed class NexusCaptureResult
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public long Id;
        public bool Ok;
        public string Message;
        /// <summary>The node a station scan was of, for its refit panel.</summary>
        public string Node;
        public bool Offered;
        public string Family;
        public string CurrentId;
        public string NextId;
        public bool ScopeAll;
        public string ScopeLabel;
        public string Reason;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusCaptureResult result)
        {
            result = NexusHandsCodec.Decode<NexusCaptureResult>(payload, NexusHandsCodec.Lean);
            if (result == null || result.Version != CurrentVersion) { result = null; return false; }
            return true;
        }
    }

    /// <summary>
    /// Agent: the captures sent and not yet answered. One answer each; an upload the authority
    /// never answers lapses after <see cref="Timeout"/> and is handed back for the caller to be told.
    /// </summary>
    public sealed class NexusCapturePending<T> where T : class
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private sealed class Held { public T Item; public DateTime SentUtc; }

        private readonly Dictionary<long, Held> _held = new Dictionary<long, Held>();
        private long _last;

        /// <summary>Holds <paramref name="item"/> and returns the id to send it under.</summary>
        public long Add(T item, DateTime nowUtc)
        {
            _last = Math.Max(_last + 1, nowUtc.Ticks);
            _held[_last] = new Held { Item = item, SentUtc = nowUtc };
            return _last;
        }

        /// <summary>The item an answer is for, once; null for an unknown or already answered id.</summary>
        public T Take(long id)
        {
            Held h;
            if (!_held.TryGetValue(id, out h)) return null;
            _held.Remove(id);
            return h.Item;
        }

        /// <summary>Removes and returns every item older than <see cref="Timeout"/>; null when none.</summary>
        public List<T> Lapse(DateTime nowUtc)
        {
            List<long> ids = null;
            foreach (KeyValuePair<long, Held> kv in _held)
                if (nowUtc - kv.Value.SentUtc > Timeout) (ids ?? (ids = new List<long>())).Add(kv.Key);
            if (ids == null) return null;
            var lapsed = new List<T>(ids.Count);
            for (int i = 0; i < ids.Count; i++) { lapsed.Add(_held[ids[i]].Item); _held.Remove(ids[i]); }
            return lapsed;
        }

        public int Count { get { return _held.Count; } }
    }
}
