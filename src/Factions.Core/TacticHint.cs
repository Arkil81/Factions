using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What a player near a materialised squadron is told about how it is fighting: a short phrase
    /// on its scanner contact, and a whisper when the manoeuvre changes (docs/fleet-tactics.md,
    /// "Legibility"). The phrase follows the manoeuvre the flight controller is flying, which the
    /// arm decides, so a squadron that changes arm reads differently.
    /// </summary>
    public static class TacticHint
    {
        /// <summary>The phrase for a combat manoeuvre; null for anything that is not one.</summary>
        public static string Phrase(ManoeuvreKind kind)
        {
            switch (kind)
            {
                case ManoeuvreKind.HoldAtRange: return "holding at range";
                case ManoeuvreKind.Orbit: return "closing to brawl";
                case ManoeuvreKind.Kite: return "falling back to kite";
                case ManoeuvreKind.AttackRun: return "making attack runs";
                case ManoeuvreKind.Disengage: return "breaking off";
                default: return null;
            }
        }

        /// <summary>The nearby-fleet whisper: <c>[SPRT] squadron holding at range.</c></summary>
        public static string Whisper(string factionTag, string phrase)
        {
            if (string.IsNullOrEmpty(phrase)) return null;
            return "[" + (string.IsNullOrEmpty(factionTag) ? "?" : factionTag) + "] squadron " + phrase + ".";
        }

        /// <summary>A scanner contact's label with the phrase appended; the label unchanged without one.</summary>
        public static string ContactLabel(string label, string phrase)
        {
            if (string.IsNullOrEmpty(phrase)) return label;
            return string.IsNullOrEmpty(label) ? phrase : label + " · " + phrase;
        }
    }

    /// <summary>
    /// Per squadron, the manoeuvre it is flying now and the last one announced, so a whisper goes
    /// out once per change and no more often than <see cref="MinAnnounceInterval"/>. Fed every frame
    /// a squadron holds; everything it does is a dictionary lookup, and it sends nothing itself.
    ///
    /// <para>A change inside the interval is announced when the interval ends if the squadron is
    /// still flying it; one that flips back first is never announced. A squadron that stops
    /// fighting is not announced, and resuming the manoeuvre last announced is not a change.</para>
    /// </summary>
    public sealed class TacticHintTracker
    {
        public static readonly TimeSpan MinAnnounceInterval = TimeSpan.FromSeconds(20);

        /// <summary>A squadron not observed for this long reads as having no phrase: it stopped
        /// holding, or its grid went away.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

        /// <summary>Entries not observed for this long are dropped.</summary>
        public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);

        private sealed class Entry
        {
            public string Phrase;
            public string Announced;
            public DateTime AnnouncedUtc = DateTime.MinValue;
            public DateTime SeenUtc;
        }

        private readonly Dictionary<string, Entry> _byFleet = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private DateTime _nextPruneUtc = DateTime.MinValue;

        public int Count { get { return _byFleet.Count; } }

        /// <summary>Records that <paramref name="fleetId"/> is flying <paramref name="kind"/> (null:
        /// holding without a fight). Returns the phrase to announce now, or null.</summary>
        public string Observe(string fleetId, ManoeuvreKind? kind, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(fleetId)) return null;
            Prune(nowUtc);

            Entry e;
            if (!_byFleet.TryGetValue(fleetId, out e))
            {
                if (!kind.HasValue) return null;
                e = new Entry();
                _byFleet[fleetId] = e;
            }
            e.SeenUtc = nowUtc;
            e.Phrase = kind.HasValue ? TacticHint.Phrase(kind.Value) : null;

            if (e.Phrase == null || e.Phrase == e.Announced) return null;
            if (nowUtc - e.AnnouncedUtc < MinAnnounceInterval) return null;
            e.Announced = e.Phrase;
            e.AnnouncedUtc = nowUtc;
            return e.Phrase;
        }

        /// <summary>The phrase <paramref name="fleetId"/> is flying now, or null.</summary>
        public string Current(string fleetId, DateTime nowUtc)
        {
            Entry e;
            if (string.IsNullOrEmpty(fleetId) || !_byFleet.TryGetValue(fleetId, out e)) return null;
            return nowUtc - e.SeenUtc <= StaleAfter ? e.Phrase : null;
        }

        private void Prune(DateTime nowUtc)
        {
            if (nowUtc < _nextPruneUtc) return;
            _nextPruneUtc = nowUtc + TimeSpan.FromMinutes(1);
            List<string> gone = null;
            foreach (KeyValuePair<string, Entry> kv in _byFleet)
                if (nowUtc - kv.Value.SeenUtc > ForgetAfter) (gone ?? (gone = new List<string>())).Add(kv.Key);
            if (gone != null) foreach (string id in gone) _byFleet.Remove(id);
        }
    }
}
