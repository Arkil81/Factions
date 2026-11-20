using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Definitions;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Which faction owns a prefab, read from SE's own spawn-group definitions.
    ///
    /// <para><b>Why this exists.</b> The prefab-name keyword table (<c>PrefabCatalogueScanner</c>'s
    /// <c>NameFactionHints</c>) can only see what a filename says, and most of SE's Factorum content
    /// does not say it: <c>GE_ListeningPost</c>, <c>GE_StorageFacility</c>, <c>GE_LogisticsFacility</c>,
    /// <c>GE_BioResearch</c>, <c>GE_DroneDepot</c>, <c>GE_Ambush</c> and <c>GE_HighProfileTransport</c>
    /// are all Factorum and none carries the word. Measured on the live content set 2026-09-01: the
    /// name rule found 7 of the 22 Factorum prefabs.</para>
    ///
    /// <para><c>MySpawnGroupDefinition.FactionSubtypeIds</c> is the <c>&lt;Factions&gt;</c> block of
    /// <c>SpawnGroups_*.sbc</c> — SE stating who a spawn belongs to — and it is a plain public
    /// property, so this needs no reflection and picks up mod content the same way vanilla does.</para>
    ///
    /// <para><b>Only subtypes that name a livery are adopted, and that is a measured decision.</b>
    /// The live content maps 80 prefabs across three faction subtypes: <c>Factorum</c> (19),
    /// <c>Unknown</c> (56) and <c>SpacePirates</c> (5). Diffed against the current hints before any
    /// of this was wired:
    /// <list type="bullet">
    ///   <item><c>SpacePirates</c> changes nothing — all four catalogued prefabs already read
    ///   <c>Pirate</c> from their names, so the two sources agree wherever both speak.</item>
    ///   <item><c>Factorum</c> is the whole gain: 15 prefabs move from <c>All</c> to
    ///   <c>Factorum</c>.</item>
    ///   <item><c>Unknown</c> is <b>not</b> adopted. It is SE's owner for derelicts and wrecks, not
    ///   a fleet livery, and taking it would move 38 prefabs out of the universal pool — including
    ///   six with real fleet roles (<c>C55_Derelict_CommercialHopper</c>, <c>DS_MiniMuleDrone</c>,
    ///   <c>DS_OldContainer</c>, <c>DS_OldSupplyModule</c>, <c>DS_PerimeterDrone</c>,
    ///   <c>DS_TwinrailDrone</c>). A hint that is neither <c>All</c> nor a match is in neither list
    ///   in <c>GetNamesForRole</c>, so those hulls would leave every faction's pool to reach UNKN,
    ///   which is dormant and dispatches nothing.</item>
    /// </list>
    /// Adding a row here widens what the world can field, so a new one wants the same diff.</para>
    /// </summary>
    internal static class SpawnGroupFactionIndex
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        // SE faction subtype -> catalogue livery. Deliberately partial; see the class remarks.
        private static readonly Dictionary<string, string> LiveryByFactionSubtype =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Factorum"]     = StationStyle.Factorum,
                ["SpacePirates"] = "Pirate",
            };

        /// <summary>Prefab SubtypeId → livery, for every prefab a spawn group assigns to a faction
        /// this index has a livery for. Empty when the definition manager is unavailable, which
        /// leaves every caller on the name rule exactly as before.</summary>
        public static Dictionary<string, string> Build()
        {
            var byPrefab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MyDefinitionManager.Static == null) return byPrefab;

            int groups = 0, conflicts = 0;
            try
            {
                foreach (MySpawnGroupDefinition group in MyDefinitionManager.Static.GetSpawnGroupDefinitions())
                {
                    if (group == null || group.Prefabs == null) continue;
                    if (group.FactionSubtypeIds == null || group.FactionSubtypeIds.Count == 0) continue;

                    string livery = null;
                    foreach (string subtype in group.FactionSubtypeIds)
                    {
                        string mapped;
                        if (string.IsNullOrEmpty(subtype)) continue;
                        if (!LiveryByFactionSubtype.TryGetValue(subtype, out mapped)) continue;
                        livery = mapped;
                        break;
                    }
                    if (livery == null) continue;
                    groups++;

                    foreach (MySpawnGroupDefinition.SpawnGroupPrefab prefab in group.Prefabs)
                    {
                        string id = prefab.SubtypeId;
                        if (string.IsNullOrEmpty(id)) continue;

                        string existing;
                        if (byPrefab.TryGetValue(id, out existing))
                        {
                            // A prefab in two groups of different factions has no single livery.
                            // Leave the first and say so rather than letting enumeration order
                            // decide -- the live content has no such case, so one appearing is news.
                            if (!string.Equals(existing, livery, StringComparison.OrdinalIgnoreCase))
                                conflicts++;
                            continue;
                        }
                        byPrefab[id] = livery;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "SpawnGroupFactionIndex: failed to read spawn-group definitions; "
                           + "falling back to prefab-name hints.");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Log.Info("SpawnGroupFactionIndex: " + byPrefab.Count + " prefab(s) owned across "
                     + groups + " spawn group(s)"
                     + (conflicts > 0 ? "; " + conflicts + " conflicting assignment(s) ignored" : "")
                     + ".");
            return byPrefab;
        }
    }
}
