using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// Every border a seated pilot crosses, for the <c>factions debug trespass</c> trace
    /// (TRESPASS_CROSSING). The hail book (<see cref="TrespassHailBook"/>) only records the
    /// crossings a faction answers: an entry it detected, outside the repeat guard, into an NPC's
    /// space. This one records the raw space the pilot is in, whoever owns it and whether its owner
    /// sees them, so an entry that produced no hail can be told apart from one that never happened.
    ///
    /// <para><b>A change of space or of sight is a crossing.</b> Slipping into a faction's space
    /// unseen and being detected later is two lines: the entry (<c>seen=0</c>) and the sighting
    /// (same space, <c>seen=1</c>). A pilot going offline is an exit.</para>
    ///
    /// <para>Keyed by pilot, like the hail book: changing ships inside one space is not a crossing.
    /// A pilot who is not seated is not observed, so standing up keeps the last space.</para>
    ///
    /// <para>Provenance: operator request 2026-10-18, telemetry for border crossings (FAC-483).</para>
    /// </summary>
    public sealed class TrespassCrossingBook
    {
        /// <summary>Where one pilot was on the last pass.</summary>
        public struct Where
        {
            /// <summary>The space owner's tag, or null outside anyone's space.</summary>
            public string Tag;
            /// <summary>The owner is a player faction.</summary>
            public bool PlayerOwned;
            /// <summary>1 seen by the owner, 0 not seen, -1 not asked (no space, or a player
            /// faction's, which has no sensors to ask).</summary>
            public int Seen;

            public Where(string tag, bool playerOwned, int seen)
            {
                Tag = tag;
                PlayerOwned = tag != null && playerOwned;
                Seen = tag != null ? seen : -1;
            }

            public bool SameAs(Where o)
            {
                return string.Equals(Tag, o.Tag, StringComparison.Ordinal)
                       && PlayerOwned == o.PlayerOwned && Seen == o.Seen;
            }

            public static Where Nowhere { get { return new Where(null, false, -1); } }
        }

        private readonly Dictionary<long, Where> _where = new Dictionary<long, Where>();

        /// <summary>Records where <paramref name="identityId"/> is now. True when that differs from
        /// the last pass; <paramref name="was"/> is the previous place (<see cref="Where.Nowhere"/>
        /// for a pilot not yet seen). A first sighting outside anyone's space is not a crossing.</summary>
        public bool Observe(long identityId, Where now, out Where was)
        {
            if (!_where.TryGetValue(identityId, out was)) was = Where.Nowhere;
            if (now.SameAs(was))
            {
                if (now.Tag == null) _where.Remove(identityId);
                return false;
            }
            if (now.Tag == null) _where.Remove(identityId);
            else _where[identityId] = now;
            return true;
        }

        /// <summary>Forgets every pilot no longer online and returns those who were inside someone's
        /// space, with where they were: each is an exit.</summary>
        public List<KeyValuePair<long, Where>> Prune(ICollection<long> onlineIdentities)
        {
            if (_where.Count == 0) return NoneGone;
            var gone = new List<KeyValuePair<long, Where>>();
            foreach (KeyValuePair<long, Where> kv in _where)
                if (onlineIdentities == null || !onlineIdentities.Contains(kv.Key)) gone.Add(kv);
            for (int i = 0; i < gone.Count; i++) _where.Remove(gone[i].Key);
            return gone;
        }

        /// <summary>Returned while nobody is inside anyone's space, so the 5 s pass allocates nothing.
        /// Read-only by convention.</summary>
        private static readonly List<KeyValuePair<long, Where>> NoneGone = new List<KeyValuePair<long, Where>>(0);

        public int Count { get { return _where.Count; } }

        /// <summary>The TRESPASS_CROSSING line. <paramref name="why"/> is <c>move</c> for a pass
        /// that saw the change and <c>offline</c> for a pilot who logged out, whose position and speed
        /// are no longer known (null, written as <c>-</c>).</summary>
        public static string Line(long identityId, long gridEntityId, Where from, Where to, Vec3? positionKm,
                                  double? speedMs, string why)
        {
            var inv = CultureInfo.InvariantCulture;
            return "TRESPASS_CROSSING identity=" + identityId
                   + " eid=" + gridEntityId
                   + " from=" + Token(from)
                   + " to=" + Token(to)
                   + " wasSeen=" + SeenToken(from.Seen)
                   + " seen=" + SeenToken(to.Seen)
                   + " posKm=" + (positionKm.HasValue
                       ? positionKm.Value.X.ToString("0.#", inv)
                         + "," + positionKm.Value.Y.ToString("0.#", inv)
                         + "," + positionKm.Value.Z.ToString("0.#", inv)
                       : "-")
                   + " speed=" + (speedMs.HasValue ? speedMs.Value.ToString("0", inv) : "-")
                   + " why=" + why;
        }

        private static string Token(Where w)
        {
            if (w.Tag == null) return "-";
            return w.PlayerOwned ? w.Tag + "(player)" : w.Tag;
        }

        private static string SeenToken(int seen)
        {
            return seen < 0 ? "-" : seen.ToString(CultureInfo.InvariantCulture);
        }
    }
}
