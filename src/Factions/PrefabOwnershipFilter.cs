using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>Shared filter applied everywhere a candidate prefab-id list is narrowed to what one
    /// faction may actually use — <see cref="VanillaFleetSpawner.PickPrefabName"/> (fleet-dispatch
    /// selection) and <c>FactionRuntime</c>'s <c>PrefabListPicker</c>/<c>ListPrefabsForPlayer</c>
    /// (discovery + <c>ShipyardManager</c> auto-build). A player-captured prefab
    /// (`!factions ship capture`) restricted to one faction (<see
    /// cref="IOwnedPrefabCatalogue.OwnerFactionTag"/>) must never surface for, or be selectable by,
    /// any other faction — this is the single place that rule is enforced for id *lists* (see also
    /// <c>FactionRuntime.QueueBuildForPlayer</c>, which enforces the same rule for a single explicit
    /// id typed by a player).
    ///
    /// <para>It also drops a version another version supersedes for that faction
    /// (<see cref="PrefabVersions"/>): new work takes the latest, so an older version is never
    /// offered once a newer one is usable.</para></summary>
    internal static class PrefabOwnershipFilter
    {
        public static IReadOnlyList<string> Apply(IReadOnlyList<string> candidateIds, IPrefabCatalogue catalogue, string factionTag)
        {
            var owned = catalogue as IOwnedPrefabCatalogue;
            var versions = catalogue as IPrefabVersionCatalogue;
            if ((owned == null && versions == null) || candidateIds == null || candidateIds.Count == 0) return candidateIds;

            List<string> filtered = null;
            for (int i = 0; i < candidateIds.Count; i++)
            {
                string id = candidateIds[i];
                bool allowed = owned == null || PrefabVersions.VisibleTo(owned.OwnerFactionTag(id), factionTag);
                if (allowed && versions != null
                    && !string.Equals(versions.LatestFor(id, factionTag), id, StringComparison.OrdinalIgnoreCase))
                    allowed = false;

                if (!allowed && filtered == null)
                {
                    filtered = new List<string>(candidateIds.Count);
                    for (int j = 0; j < i; j++) filtered.Add(candidateIds[j]);
                }
                if (allowed) filtered?.Add(id);
            }
            return filtered ?? candidateIds;
        }
    }
}
