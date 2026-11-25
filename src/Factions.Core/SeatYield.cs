using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>When a bot in a seat gives it up to a player.
    ///
    /// <para>SE decides "occupied" on the player's own client: pressing F on a seat a bot holds
    /// shows "already used" there and sends the server nothing. The server sees only where the
    /// player stands and looks, so the attempt is read from that: a player with access to the seat,
    /// within reach of its entry point and looking at it for <see cref="DwellSamples"/> consecutive
    /// samples. The bot stands up, and the seat is then held for <see cref="HoldSeconds"/> so no
    /// bot takes it before the player presses F again.</para></summary>
    public static class SeatYield
    {
        /// <summary>Head to the seat's entry point, metres: SE's own use reach is about this.</summary>
        public const double ReachM = 3.0;
        /// <summary>Within this of the entry point the look direction no longer matters.</summary>
        public const double CloseM = 0.6;
        public const double MaxAngleDeg = 25;
        public const int DwellSamples = 2;
        public const double HoldSeconds = 30;

        /// <summary>Whether a seat refuses a character sitting down: only a bot, only while the seat is
        /// held, and never a pilot restored with the world.</summary>
        public static bool RefusesBot(bool held, bool realPlayer, bool fromWorldLoad)
        {
            return held && !realPlayer && !fromWorldLoad;
        }

        /// <summary>True when a head at <paramref name="head"/> looking along
        /// <paramref name="forward"/> is reaching for an entry point at <paramref name="entry"/>.</summary>
        public static bool Aims(Vec3 head, Vec3 forward, Vec3 entry)
        {
            double dx = entry.X - head.X, dy = entry.Y - head.Y, dz = entry.Z - head.Z;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > ReachM * ReachM) return false;
            if (d2 <= CloseM * CloseM) return true;
            double f = Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y + forward.Z * forward.Z);
            if (f <= 1e-9) return false;
            double cos = (forward.X * dx + forward.Y * dy + forward.Z * dz) / (f * Math.Sqrt(d2));
            return cos >= Math.Cos(MaxAngleDeg * Math.PI / 180.0);
        }
    }

    /// <summary>The per-pass state behind <see cref="SeatYield"/>: how long each player has been
    /// reaching for each bot-held seat, and which seats are held for a player.</summary>
    public sealed class SeatYieldWatch
    {
        private Dictionary<KeyValuePair<long, long>, int> _counts = new Dictionary<KeyValuePair<long, long>, int>();
        private Dictionary<KeyValuePair<long, long>, int> _next = new Dictionary<KeyValuePair<long, long>, int>();
        private readonly Dictionary<long, DateTime> _held = new Dictionary<long, DateTime>();
        private readonly List<long> _expired = new List<long>();

        /// <summary>Records one sample of <paramref name="player"/> reaching (or not) for
        /// <paramref name="seat"/>. True on the sample that completes the dwell; the count then
        /// restarts, so a seat that is taken again yields again only after a fresh dwell.</summary>
        public bool Observe(long seat, long player, bool reaching)
        {
            if (!reaching) return false;
            var key = new KeyValuePair<long, long>(seat, player);
            int count;
            _counts.TryGetValue(key, out count);
            count++;
            if (count >= SeatYield.DwellSamples) return true;
            _next[key] = count;
            return false;
        }

        /// <summary>Ends a sampling pass: a pair not reaching in this pass starts from zero.</summary>
        public void EndPass()
        {
            Dictionary<KeyValuePair<long, long>, int> done = _counts;
            _counts = _next;
            _next = done;
            _next.Clear();
        }

        public void Hold(long seat, DateTime nowUtc)
        {
            _held[seat] = nowUtc.AddSeconds(SeatYield.HoldSeconds);
        }

        public bool IsHeld(long seat, DateTime nowUtc)
        {
            DateTime until;
            return _held.TryGetValue(seat, out until) && nowUtc < until;
        }

        public int HeldCount(DateTime nowUtc)
        {
            _expired.Clear();
            foreach (KeyValuePair<long, DateTime> e in _held) if (nowUtc >= e.Value) _expired.Add(e.Key);
            for (int i = 0; i < _expired.Count; i++) _held.Remove(_expired[i]);
            return _held.Count;
        }
    }
}
