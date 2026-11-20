using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which station designs a founding may draw for a given site and <see cref="StationStyle"/>.
    ///
    /// <para><b>A civilian or pirate draw is a design SE itself founds</b>: one listed in SE's
    /// per-station-type tables, at the site that table stands for. The name is not enough. SE ships
    /// <c>Economy_Outpost_1-9</c> and <c>Economy_SpaceStation_1-5</c> with their entries commented out
    /// of those tables, and lists <c>Economy_OrbitalStation_*</c> and <c>Economy_MiningStation_*</c>
    /// under no type at all; all of them are still valued, and all of them are refit targets where
    /// their name sites them, but none is a design a faction founds.</para>
    ///
    /// <para><b>The Factorum family is drawn only by the Factorum style.</b> Its structures carry
    /// no store, contract board or medbay, so it is excluded by family and not left to the site
    /// tables to keep out.</para>
    /// </summary>
    public static class StationPrefabPool
    {
        /// <summary>The designs, in ordinal order, a founding at a node of this environment and
        /// style may draw from <paramref name="prefabIds"/>. <paramref name="seListedSites"/> maps a
        /// prefab to the <see cref="StationPrefabSite"/> token of the SE station type listing it;
        /// null or empty leaves the civilian and pirate pools empty.</summary>
        public static List<string> Select(IEnumerable<string> prefabIds, bool isSpace, string style,
            Func<string, bool> isFactorumStation, IDictionary<string, string> seListedSites)
        {
            var pool = new List<string>();
            if (prefabIds == null) return pool;
            foreach (string id in prefabIds)
                if (Admits(id, isSpace, style, isFactorumStation, seListedSites)) pool.Add(id);
            pool.Sort(StringComparer.Ordinal);   // the caller draws at random; order keeps a report reproducible
            return pool;
        }

        /// <summary>Whether a founding of this environment and style may draw
        /// <paramref name="prefabId"/>. The Factorum family is one set with no outpost/space pair,
        /// so it is not filtered by site.</summary>
        public static bool Admits(string prefabId, bool isSpace, string style,
            Func<string, bool> isFactorumStation, IDictionary<string, string> seListedSites)
        {
            if (string.IsNullOrEmpty(prefabId)) return false;
            bool factorum = isFactorumStation != null && isFactorumStation(prefabId);
            if (string.Equals(style, StationStyle.Factorum, StringComparison.OrdinalIgnoreCase)) return factorum;
            if (factorum) return false;

            string site;
            if (seListedSites == null || !seListedSites.TryGetValue(prefabId, out site)) return false;
            string wantSite = isSpace ? StationPrefabSite.Space : StationPrefabSite.Surface;
            if (!string.Equals(site, wantSite, StringComparison.OrdinalIgnoreCase)) return false;

            bool wantPirate = string.Equals(style, StationStyle.Pirate, StringComparison.OrdinalIgnoreCase);
            return prefabId.EndsWith("_Pirate", StringComparison.OrdinalIgnoreCase) == wantPirate;
        }

        /// <summary>Why a node standing as <paramref name="prefabId"/> is outside what its owner
        /// would draw there today, or <see cref="PoolMismatch.None"/>.
        ///
        /// <para>A Factorum owner is never outside: a civilian design under it is a capture it
        /// inherited, not a draw it made. A pirate owner accepts the civilian family too, because
        /// that is where a founding falls back when no pirate variant fits the site.</para>
        ///
        /// <para>With no SE station tables to read, only the Factorum test is answered. An empty read
        /// would otherwise put every station in the world outside its pool.</para></summary>
        public static PoolMismatch Classify(string ownerStyle, string prefabId, bool isSpace,
            Func<string, bool> isFactorumStation, IDictionary<string, string> seListedSites)
        {
            if (string.IsNullOrEmpty(prefabId)) return PoolMismatch.None;
            if (string.Equals(ownerStyle, StationStyle.Factorum, StringComparison.OrdinalIgnoreCase)) return PoolMismatch.None;
            if (isFactorumStation != null && isFactorumStation(prefabId)) return PoolMismatch.FactorumStructure;
            if (seListedSites == null || seListedSites.Count == 0) return PoolMismatch.None;

            if (Admits(prefabId, isSpace, ownerStyle, isFactorumStation, seListedSites)) return PoolMismatch.None;
            if (string.Equals(ownerStyle, StationStyle.Pirate, StringComparison.OrdinalIgnoreCase)
                && Admits(prefabId, isSpace, StationStyle.Civilian, isFactorumStation, seListedSites))
                return PoolMismatch.None;
            return PoolMismatch.NotFoundedHere;
        }
    }

    /// <summary>What <see cref="StationPrefabPool.Classify"/> found wrong with a standing design.</summary>
    public enum PoolMismatch
    {
        None,
        /// <summary>A Factorum structure under an owner that is not the Factorum.</summary>
        FactorumStructure,
        /// <summary>A design SE does not found at this site: an old generation, an unlisted family,
        /// or the other site's design.</summary>
        NotFoundedHere,
    }
}
