using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Built-in personality profiles, shared by the offline sim and the host so a faction reasons
    /// identically everywhere. A profile = drive-weight vector + softmax temperature + interpretive traits.
    /// "default" is a balanced profile used for freshly-registered factions until one is assigned.</summary>
    public static class Personalities
    {
        public static Dictionary<string, Personality> All()
        {
            return new Dictionary<string, Personality>
            {
                // merc=0.65: prefers trading over fighting/reinforcing, but Develop beats Trade
                // when an investment request is urgent (urgency=1.0 → effective 0.808 vs 0.755).
                { "mercantile", Make("mercantile", sec: 0.6, gro: 1.0, wea: 1.4, riv: 0.3, cau: 0.8, temp: 0.6, xeno: 0.05, merc: 0.65, aggr: 0.2, exp: 0.8) },
                { "raider",     Make("raider",     sec: 1.2, gro: 0.7, wea: 0.8, riv: 1.4, cau: 0.5, temp: 0.5, xeno: 0.90, merc: 0.1, aggr: 0.9, exp: 0.6) },
                { "settler",    Make("settler",    sec: 0.9, gro: 1.3, wea: 0.9, riv: 0.4, cau: 0.7, temp: 0.4, xeno: 0.30, merc: 0.4, aggr: 0.3, exp: 1.0) },
                // Abandoned progenitor AI: defends fixed territory, never trades, attacks all intruders on
                // sight. Implacable — it holds every other faction at -1 and cannot be traded, gifted or
                // negotiated back out of it.
                { "factorum",   Make("factorum",   sec: 1.8, gro: 0.1, wea: 0.4, riv: 0.2, cau: 1.3, temp: 0.2, xeno: 1.00, merc: 0.0, aggr: 0.9, exp: 0.1, implacable: true) },
                { "default",    Make("default",    sec: 1.0, gro: 1.0, wea: 1.0, riv: 1.0, cau: 1.0, temp: 0.5, xeno: 0.30, merc: 0.4, aggr: 0.4, exp: 0.6) },
            };
        }

        /// <summary>Profile by id, falling back to "default" for an unknown/null id.</summary>
        public static Personality Get(string id)
        {
            Dictionary<string, Personality> all = All();
            Personality p;
            if (id != null && all.TryGetValue(id, out p)) return p;
            return all["default"];
        }

        /// <summary>Ids whose profile carries <see cref="Traits.Implacable"/>, so a caller holding only a
        /// <see cref="FactionState.PersonalityId"/> can ask the question without resolving a whole profile.
        /// Derived from <see cref="All"/> once, so the table above stays the single source of truth.</summary>
        private static readonly HashSet<string> ImplacableIds = BuildImplacableIds();

        /// <summary>True if the named profile never softens toward anyone. Resolves an unknown/null id the
        /// same way <see cref="Get"/> does — through "default", which is not implacable.</summary>
        public static bool IsImplacable(string personalityId)
        {
            return personalityId != null && ImplacableIds.Contains(personalityId);
        }

        private static HashSet<string> BuildImplacableIds()
        {
            var ids = new HashSet<string>();
            foreach (KeyValuePair<string, Personality> kv in All())
                if (kv.Value.Traits.Implacable) ids.Add(kv.Key);
            return ids;
        }

        private static Personality Make(string id, double sec, double gro, double wea, double riv, double cau,
            double temp, double xeno, double merc, double aggr, double exp, bool implacable = false)
        {
            var p = new Personality { Id = id, Temperature = temp, Traits = new Traits { Xenophobia = xeno, Mercantilism = merc, Aggression = aggr, Expansionism = exp, Implacable = implacable } };
            p.Weights[Drive.Security] = sec; p.Weights[Drive.Growth] = gro; p.Weights[Drive.Wealth] = wea;
            p.Weights[Drive.Rivalry] = riv; p.Weights[Drive.Caution] = cau;
            return p;
        }
    }
}
