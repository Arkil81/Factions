using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One hull the Build dialog may offer at one yard, with the numbers a player needs to
    /// choose between them. Assembled here rather than in the host so the sim and the tests see the
    /// same list the client does — the rule is in Core, the SE catalogue is behind
    /// <see cref="IPrefabCatalogue"/>.</summary>
    public sealed class BuildPrefabOption
    {
        public string PrefabId;
        /// <summary>Display name, or empty to show the id. No catalogue carries one today; the
        /// field exists because the wire row has it and a display name is a host concern.</summary>
        public string Label;
        /// <summary>Every queueable role this hull passes <see cref="ShipPool.IsSuitableForRole"/>
        /// for, in <see cref="ShipyardManager.PlayerQueueRoles"/> order. Never empty.</summary>
        public readonly List<string> Roles = new List<string>();
        public long CargoLiters;
        public double Dps;
        public string GridSize;
        /// <summary>Pre-rendered thrust summary ("atmo+hydro"), or "none". A display string.</summary>
        public string Propulsion;
        public double DrillYieldScore;
        /// <summary>Total units across the hull's BOM, 0 when the recipe registry has no bill for
        /// it.</summary>
        public long BomUnits;
        public bool CanFlyFromHere;
    }

    /// <summary>
    /// The Build dialog's hull pick-list for one shipyard.
    ///
    /// <para>It is a <b>convenience, never an authorisation</b>. Everything here is re-checked when
    /// the build is actually queued (<see cref="ShipyardManager.QueueBuild"/>), and this list is
    /// deliberately allowed to be wider than what a doctrine line accepts: the one-shot build sits
    /// outside the doctrine, so a hull that no line names is still a legal thing to build once.</para>
    ///
    /// <para>A hull the recipe registry holds no bill for is not listed: <see cref="ShipyardManager.QueueBuild"/>
    /// answers <see cref="PlayerQueueResult.UnknownPrefab"/> for it, so a row would be an offer of a
    /// refusal. A recipe with zero inputs is a real free build and is listed at 0 units.</para>
    /// </summary>
    public static class BuildOptionsCatalogue
    {
        /// <summary>Assembles the pick-list for <paramref name="yard"/> from a caller-supplied
        /// (role, prefabId) candidate set — the host resolves that from its own faction-hint pool
        /// and its ownership filter, both of which are host concerns Core has no view of.
        ///
        /// <para>Rows are per prefab, deduplicated across roles and ordered by prefab id, so the
        /// dialog's combo is stable between two openings of the same window.</para></summary>
        public static List<BuildPrefabOption> Assemble(WorldModel world, FactionTuning tuning,
            SpatialNode yard, IEnumerable<KeyValuePair<string, string>> roleCandidates)
        {
            // A null yard is still nothing rather than "everything, stranded": this overload's
            // caller is a dialog opened ON a yard, and a missing one is a missing question.
            if (yard == null) return new List<BuildPrefabOption>();
            return AssembleAcrossYards(world, tuning, new List<SpatialNode> { yard }, roleCandidates);
        }

        /// <summary>The same pick-list over SEVERAL yards, for the fleet doctrine's new-line dialog:
        /// a line is not about one yard, so <see cref="BuildPrefabOption.CanFlyFromHere"/> means
        /// "could leave at least one of <paramref name="yards"/>". Everything else about a hull is a
        /// property of the hull and does not vary by yard.
        ///
        /// <para>An empty or null yard list still lists every hull, with
        /// <see cref="BuildPrefabOption.CanFlyFromHere"/> false throughout: a faction with no yard
        /// can still write a doctrine for the yard it is about to build, and a dialog that offered
        /// nothing would say the catalogue was empty rather than that the faction has nowhere to
        /// build.</para></summary>
        public static List<BuildPrefabOption> AssembleAcrossYards(WorldModel world, FactionTuning tuning,
            IList<SpatialNode> yards, IEnumerable<KeyValuePair<string, string>> roleCandidates)
        {
            var rows = new List<BuildPrefabOption>();
            if (world == null || world.PrefabCatalogue == null || roleCandidates == null)
                return rows;

            IPrefabCatalogue cat = world.PrefabCatalogue;
            var stations = cat as IStationPrefabCatalogue;
            var byPrefab = new Dictionary<string, BuildPrefabOption>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> candidate in roleCandidates)
            {
                string role = candidate.Key;
                string prefabId = candidate.Value;
                if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(prefabId)) continue;
                if (Array.IndexOf(ShipyardManager.PlayerQueueRoles, role) < 0) continue;
                // A station design is not a hull. An absent IStationPrefabCatalogue means "no
                // station designs" (its own contract), so nothing is dropped on a catalogue that
                // cannot tell them apart.
                if (stations != null && stations.IsStationPrefab(prefabId)) continue;
                if (!ShipPool.IsSuitableForRole(role, cat, prefabId)) continue;

                BuildPrefabOption row;
                if (!byPrefab.TryGetValue(prefabId, out row))
                {
                    row = Describe(world, tuning, yards, cat, prefabId);
                    // Null is a hull the registry holds no bill for. QueueBuild refuses such a
                    // prefab (UnknownPrefab), so offering it would be offering a refusal.
                    byPrefab[prefabId] = row;
                    if (row != null) rows.Add(row);
                }
                if (row != null && !row.Roles.Contains(role)) row.Roles.Add(role);
            }

            foreach (BuildPrefabOption row in rows) SortRoles(row.Roles);
            rows.Sort(delegate (BuildPrefabOption a, BuildPrefabOption b)
            {
                return string.Compare(a.PrefabId, b.PrefabId, StringComparison.Ordinal);
            });
            return rows;
        }

        private static BuildPrefabOption Describe(WorldModel world, FactionTuning tuning,
            IList<SpatialNode> yards, IPrefabCatalogue cat, string prefabId)
        {
            bool hasRecipe;
            Dictionary<string, long> bom = ProductionManager.GetPrefabBom(world.Recipes, prefabId, out hasRecipe);
            if (!hasRecipe) return null;
            long units = 0;
            if (bom != null)
                foreach (KeyValuePair<string, long> kv in bom) units += kv.Value;

            double dps = 0;
            IReadOnlyList<WeaponBand> bands = cat.WeaponBands(prefabId);
            if (bands != null)
                for (int i = 0; i < bands.Count; i++) dps += bands[i].Dps;

            return new BuildPrefabOption
            {
                PrefabId = prefabId,
                Label = "",
                CargoLiters = cat.CargoLiters(prefabId),
                Dps = dps,
                GridSize = cat.GridSize(prefabId),
                Propulsion = DescribePropulsion(cat, prefabId),
                DrillYieldScore = cat.DrillYieldScore(prefabId),
                BomUnits = units,
                CanFlyFromHere = CanFlyFromAny(world, tuning, yards, prefabId),
            };
        }

        /// <summary>Whether <paramref name="prefabId"/> could leave any of <paramref name="yards"/>.
        /// One yard for the Build dialog, every owned yard for the doctrine dialog -- but always
        /// <see cref="ShipyardManager.CanFlyFromYard"/>, never a second route judgement.</summary>
        private static bool CanFlyFromAny(WorldModel world, FactionTuning tuning, IList<SpatialNode> yards,
            string prefabId)
        {
            if (yards == null) return false;
            for (int i = 0; i < yards.Count; i++)
            {
                if (yards[i] == null) continue;
                if (ShipyardManager.CanFlyFromYard(world, tuning, yards[i], prefabId)) return true;
            }
            return false;
        }

        /// <summary>Which of the three vanilla thrust kinds a hull actually mounts, as one string.
        /// Read off <see cref="IPrefabCatalogue.ThrustBands"/>, which is where the scanner puts the
        /// raw <c>MyThrustDefinition</c> type name, so a modded thruster is reported under its own
        /// name rather than dropped.
        ///
        /// <para>This is the field that answers the complaint the doctrine was written for — an ion
        /// hull looks cheap by unit count and cannot leave a planet. The order is fixed
        /// (atmospheric, ion, hydrogen) rather than taken from the dictionary, whose iteration order
        /// is not a contract.</para></summary>
        public static string DescribePropulsion(IPrefabCatalogue cat, string prefabId)
        {
            if (cat == null) return "none";
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = cat.ThrustBands(prefabId);
            if (bands == null || bands.Count == 0) return "none";

            var kinds = new List<string>();
            foreach (KeyValuePair<ThrustDirection, IReadOnlyList<ThrustBand>> kv in bands)
            {
                IReadOnlyList<ThrustBand> forDirection = kv.Value;
                if (forDirection == null) continue;
                for (int i = 0; i < forDirection.Count; i++)
                {
                    string name = ShortThrusterName(forDirection[i].ThrusterType);
                    if (name.Length > 0 && !kinds.Contains(name)) kinds.Add(name);
                }
            }
            if (kinds.Count == 0) return "none";
            SortThrusterNames(kinds);
            return string.Join("+", kinds.ToArray());
        }

        private static string ShortThrusterName(string thrusterType)
        {
            if (string.IsNullOrEmpty(thrusterType)) return "";
            if (string.Equals(thrusterType, "Atmospheric", StringComparison.OrdinalIgnoreCase)) return "atmo";
            if (string.Equals(thrusterType, "Hydrogen", StringComparison.OrdinalIgnoreCase)) return "hydro";
            if (string.Equals(thrusterType, "Ion", StringComparison.OrdinalIgnoreCase)) return "ion";
            return thrusterType.ToLowerInvariant();
        }

        /// <summary>Vanilla first in a fixed order, then anything modded alphabetically. A modded
        /// thruster sorting into the middle of the three the player recognises would make two hulls
        /// with the same vanilla loadout read differently.</summary>
        private static void SortThrusterNames(List<string> kinds)
        {
            kinds.Sort(delegate (string a, string b)
            {
                int ra = ThrusterRank(a), rb = ThrusterRank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a, b, StringComparison.Ordinal);
            });
        }

        private static int ThrusterRank(string shortName)
        {
            if (shortName == "atmo") return 0;
            if (shortName == "ion") return 1;
            if (shortName == "hydro") return 2;
            return 3;
        }

        /// <summary><see cref="ShipyardManager.PlayerQueueRoles"/> order, so the dialog's role
        /// column reads the same way the role combo does.</summary>
        private static void SortRoles(List<string> roles)
        {
            roles.Sort(delegate (string a, string b)
            {
                int ia = Array.IndexOf(ShipyardManager.PlayerQueueRoles, a);
                int ib = Array.IndexOf(ShipyardManager.PlayerQueueRoles, b);
                if (ia != ib) return ia.CompareTo(ib);
                return string.Compare(a, b, StringComparison.Ordinal);
            });
        }
    }
}
