using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> for catalogues that hold
    /// published versions of a design (see <see cref="PrefabVersions"/>). Kept separate for the
    /// reason <see cref="IOwnedPrefabCatalogue"/> is. An absent implementation means no prefab has a
    /// version: every id is its own family and its own latest.</summary>
    public interface IPrefabVersionCatalogue
    {
        /// <summary>The id of the design <paramref name="prefabId"/> is a version of, or
        /// <paramref name="prefabId"/> itself when it is not a published version.</summary>
        string FamilyOf(string prefabId);

        /// <summary>1 for a design's original, n for its <c>@n</c> version.</summary>
        int VersionOf(string prefabId);

        /// <summary>The newest member of <paramref name="prefabId"/>'s family that
        /// <paramref name="factionTag"/> may use, or <paramref name="prefabId"/> itself when nothing
        /// newer is visible to that faction.</summary>
        string LatestFor(string prefabId, string factionTag);
    }

    /// <summary>One published version of a design, as <see cref="PrefabVersions.Latest"/> reads
    /// it.</summary>
    public struct PrefabVersionMember
    {
        public string Id;
        public int Version;
        /// <summary>The faction this version is restricted to; null or empty for every faction.</summary>
        public string OwnerFactionTag;
    }

    /// <summary>
    /// Versions of a prefab design. A version is an <b>immutable id</b>: the original keeps its
    /// name and later versions are <c>name@2</c>, <c>name@3</c>… Everything keyed on a prefab id —
    /// grids, recipes, a node's <c>StationPrefabId</c>, <c>ShipStock</c> — keeps its meaning, so a
    /// node standing as v1 is still v1 and its upgrade is priced v2 against v1.
    ///
    /// <para><b>Numbering is per family, across lines.</b> A content prefab can carry one line per
    /// faction; the next number is one past the family's highest, so two lines never share an
    /// id.</para>
    ///
    /// <para><b>Latest wins for new work.</b> A version is superseded for a faction once a higher
    /// version of the same family is usable by that faction. Superseded ids stay valid for
    /// everything already built on them.</para>
    /// </summary>
    public static class PrefabVersions
    {
        public const char Separator = '@';

        public static string VersionId(string family, int version)
        {
            return family + Separator + version.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Whether <paramref name="factionTag"/> may use a version restricted to
        /// <paramref name="ownerFactionTag"/>.</summary>
        public static bool VisibleTo(string ownerFactionTag, string factionTag)
        {
            return string.IsNullOrEmpty(ownerFactionTag)
                || string.Equals(ownerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The newest member of <paramref name="family"/> visible to
        /// <paramref name="factionTag"/>, provided it is newer than <paramref name="prefabId"/>;
        /// otherwise <paramref name="prefabId"/>. A member absent from <paramref name="family"/> reads
        /// as version 1, the original.</summary>
        public static string Latest(string prefabId, string factionTag, IEnumerable<PrefabVersionMember> family)
        {
            if (family == null) return prefabId;
            int own = 1;
            foreach (PrefabVersionMember m in family)
                if (string.Equals(m.Id, prefabId, StringComparison.OrdinalIgnoreCase)) { own = m.Version; break; }

            string best = prefabId;
            int bestVersion = own;
            foreach (PrefabVersionMember m in family)
            {
                if (m.Version <= bestVersion || !VisibleTo(m.OwnerFactionTag, factionTag)) continue;
                best = m.Id;
                bestVersion = m.Version;
            }
            return best;
        }

        /// <summary>The next version number for a family whose members hold
        /// <paramref name="versions"/>: one past the highest, never below 2.</summary>
        public static int NextVersion(IEnumerable<int> versions)
        {
            int max = 1;
            if (versions != null)
                foreach (int v in versions) if (v > max) max = v;
            return max + 1;
        }

        public static string FamilyOf(IPrefabCatalogue catalogue, string prefabId)
        {
            var versions = catalogue as IPrefabVersionCatalogue;
            return versions == null || string.IsNullOrEmpty(prefabId) ? prefabId : versions.FamilyOf(prefabId);
        }

        public static string LatestFor(IPrefabCatalogue catalogue, string prefabId, string factionTag)
        {
            var versions = catalogue as IPrefabVersionCatalogue;
            return versions == null || string.IsNullOrEmpty(prefabId) ? prefabId : versions.LatestFor(prefabId, factionTag);
        }

        /// <summary>True when a newer version of <paramref name="prefabId"/> is usable by
        /// <paramref name="factionTag"/>.</summary>
        public static bool IsSupersededFor(IPrefabCatalogue catalogue, string prefabId, string factionTag)
        {
            return !string.Equals(LatestFor(catalogue, prefabId, factionTag), prefabId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The <paramref name="candidates"/> no document names. A document is serialized
        /// JSON, and an id counts as named wherever it appears as a JSON string — a value or a key —
        /// matched without regard to case. Deliberately a text census rather than a walk of known
        /// fields: a field added later that holds a prefab id is covered without anyone remembering
        /// to list it here, and the only error it can make is to keep an id a little longer.</summary>
        public static List<string> Unreferenced(IEnumerable<string> candidates, IEnumerable<string> documents)
        {
            var remaining = new List<string>();
            if (candidates == null) return remaining;
            foreach (string c in candidates) if (!string.IsNullOrEmpty(c)) remaining.Add(c);
            if (documents == null || remaining.Count == 0) return remaining;
            foreach (string doc in documents)
            {
                if (string.IsNullOrEmpty(doc)) continue;
                remaining.RemoveAll(id => doc.IndexOf(Newtonsoft.Json.JsonConvert.ToString(id), StringComparison.OrdinalIgnoreCase) >= 0);
                if (remaining.Count == 0) break;
            }
            return remaining;
        }

        /// <summary>Whether two prefab ids are versions of one design. Exact equality when the
        /// catalogue carries no versions.</summary>
        public static bool SameFamily(IPrefabCatalogue catalogue, string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var versions = catalogue as IPrefabVersionCatalogue;
            return versions != null
                && string.Equals(versions.FamilyOf(a), versions.FamilyOf(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
