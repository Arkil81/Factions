using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Scans <c>Content/Data/Prefabs</c> subfolders at session load, plus every prefab a mod
    /// contributes, and produces a <see cref="SePrefabCatalogue"/> with per-prefab cost, faction
    /// hint, roles, weapon power, and cargo capacity.
    ///
    /// <para><b>Two discovery sources.</b> Vanilla content is discovered by walking the folders
    /// below, because folder membership carries curation and role intent no scan can recover. Mod
    /// content has no such folder, so it is discovered off <c>MyDefinitionManager</c>'s resolved
    /// prefab set (filtered to non-base-game <see cref="MyDefinitionBase.Context"/>) and takes the
    /// capability-derived roles only. See docs/mod-compatibility.md.</para>
    ///
    /// <para>Results are written to <c>prefab_catalogue.json</c> in the Factions storage
    /// directory for human inspection. The in-memory catalogue is used directly by the
    /// plugin without a round-trip through disk.</para>
    ///
    /// <para>One capability-derived role assignment applied to every discovered prefab, plus one
    /// folder-curated tag layered on top of it — not one folder→role table:</para>
    /// <list type="bullet">
    ///   <item><b>Trade/Ferry/Gift/Raid/Patrol/Guard/Mine are capability-derived</b> — computed from
    ///   each prefab's own scanned stats (<see cref="Factions.Core.ShipPool.IsCargoSuitableValue"/>/
    ///   <see cref="Factions.Core.ShipPool.IsCombatSuitableValue"/>/
    ///   <see cref="Factions.Core.ShipPool.IsMineSuitableValue"/>, floored via <see
    ///   cref="Factions.Core.FactionTuning"/>'s <c>MinCargoLitersForRoleClassification*</c>/
    ///   <c>MinCombatDpsForRoleClassification</c>/<c>MinDrillYieldForRoleClassification</c>), not
    ///   folder placement — a prefab is Raid-eligible because it has real weapon Dps, regardless of
    ///   whether it lives in <c>Cargoships</c> or <c>Drones</c>. <see cref="FleetPrefabFolders"/>
    ///   (<c>Cargoships</c>, <c>Drones</c>) still drives <i>discovery</i> only — which <c>.sbc</c>
    ///   files get walked into the catalogue at all.</item>
    ///   <item><b>GlobalEncounter/PlanetaryEncounter/Encounter are folder-fixed</b> (<see
    ///   cref="EncounterFolderRoles"/>) — encounter-table membership is spawn design intent, not a
    ///   scannable physical property. Added <i>on top of</i> the capability roles above, which every
    ///   discovered prefab gets regardless of the folder that found it.</item>
    ///   <item><b>No store role.</b> <c>Economy/Sales</c> is a discovery folder like any other
    ///   now (FAC-060): its prefabs take the capability-derived roles above and nothing else. What
    ///   a faction shelves is decided at stocking time by <c>StoreShelfPlan</c>, not carried on the
    ///   prefab, so a hull is never merchandise by classification.</item>
    /// </list>
    ///
    /// <para>Faction hint derivation order (unrelated to the role mechanisms above — a separate
    /// "which livery a faction prefers" signal). The first two are SE <i>stating</i> ownership; the
    /// third only infers it from a filename, which is why it ranks last:</para>
    /// <list type="number">
    ///   <item><c>MyFactionTypeDefinition.GridsForSale</c> reverse map (accurate for Economy folder).</item>
    ///   <item><see cref="SpawnGroupFactionIndex"/> — the <c>&lt;Factions&gt;</c> block of the
    ///   spawn-group definitions. Most Factorum content does not name itself, so the keyword rule
    ///   below found 7 of 22 on the live content set.</item>
    ///   <item>Prefab-name keyword matching (e.g. <c>*_Trade_*</c> → "Trader").</item>
    ///   <item>Fallback: "All".</item>
    /// </list>
    /// </summary>
    public static class PrefabCatalogueScanner
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        // Folders walked purely for discovery -- fleet-ship prefabs live here, but which of these two
        // folders a prefab happens to be in no longer decides its roles (see class doc above); every
        // discovered prefab gets the same capability-derived role check regardless of origin folder.
        private static readonly string[] FleetPrefabFolders = { "Cargoships", "Drones" };

        // Encounter-table membership is spawn design intent, not a scannable physical property, so
        // it stays folder-fixed (unlike FleetPrefabFolders above).
        private static readonly Dictionary<string, string[]> EncounterFolderRoles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["GlobalEncounters"]    = new[] { "GlobalEncounter" },
                ["PlanetaryEncounters"] = new[] { "PlanetaryEncounter" },
                ["RandomEncounters"]    = new[] { "Encounter" },
            };

        // Only Economy/Sales contains purchasable ships. Missions/ and Stations/ are out of scope.
        private const string EconomyFolder = "Economy/Sales";

        // Keyword fragments in prefab names → faction hint.
        // Ordered from most specific to least specific — first match wins.
        private static readonly string[][] NameFactionHints = new[]
        {
            new[] { "_Factorum",   "Factorum" },
            new[] { "_Pirate",     "Pirate"   },
            new[] { "Pirate_",     "Pirate"   },
            new[] { "_Military",   "Military" },
            new[] { "Military_",   "Military" },
            new[] { "_Builder",    "Builder"  },
            new[] { "Builder_",    "Builder"  },
            new[] { "_Trade_",     "Trader"   },
            new[] { "_Trader",     "Trader"   },
            new[] { "Trader_",     "Trader"   },
            new[] { "_Mining",     "Miner"    },
            new[] { "Mining_",     "Miner"    },
            new[] { "_Miner",      "Miner"    },
            new[] { "Miner_",      "Miner"    },
        };

        /// <summary>
        /// Scans SE content prefab folders and returns the populated catalogue.
        /// Also writes <c>prefab_catalogue.json</c> to <paramref name="storageDir"/> for inspection.
        /// Call on the game thread after <c>MyDefinitionManager.Static</c> is available.
        /// </summary>
        /// <param name="tuning">Supplies the capability-classification floors (<see
        /// cref="FactionTuning.MinCargoLitersForRoleClassificationSmall"/>/<c>...Large</c>/
        /// <see cref="FactionTuning.MinCombatDpsForRoleClassification"/>/
        /// <see cref="FactionTuning.MinDrillYieldForRoleClassification"/>) used to derive
        /// Trade/Ferry/Gift/Raid/Patrol/Guard/Mine roles. Null uses an all-zero (nonzero-only, today's
        /// original gate) default.</param>
        public static SePrefabCatalogue Scan(string storageDir, IItemPriceCatalogue prices, FactionTuning tuning = null)
        {
            tuning = tuning ?? new FactionTuning();
            if (MyDefinitionManager.Static == null)
            {
                Log.Warn("PrefabCatalogueScanner: MyDefinitionManager not ready — returning empty catalogue.");
                return SePrefabCatalogue.Empty;
            }

            // Build faction-hint reverse map from MyFactionTypeDefinition.GridsForSale.
            var gridSaleFactionMap = BuildGridsForSaleMap();

            // Who SE says owns each prefab, from the spawn-group definitions. Read once; consulted
            // by DeriveFactionHint below.
            Dictionary<string, string> spawnGroupOwners = SpawnGroupFactionIndex.Build();

            // Index all SE prefab definitions keyed by SubtypeId for O(1) lookup.
            var prefabDefs = new Dictionary<string, MyPrefabDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, MyPrefabDefinition> kv in MyDefinitionManager.Static.GetPrefabDefinitions())
                prefabDefs[kv.Key] = kv.Value;

            string prefabRoot = Path.Combine(MyFileSystem.ContentPath, "Data", "Prefabs");
            var entries = new Dictionary<string, SePrefabCatalogue.Entry>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0, missing = 0;

            // Weapon blocks recognised as weapons and not priceable, tallied by subtype and by the
            // mod their own definition names. Keyed off the block rather than a list of known mods, so
            // it reports a framework nobody here has heard of on the same terms as a familiar one.
            var unscoredWeapons = new Dictionary<string, int>(StringComparer.Ordinal);

            // Fleet-ship folders: discovery only (which .sbc files exist) -- role assignment below is
            // capability-derived from each prefab's own scanned stats, not folder membership.
            foreach (string folderName in FleetPrefabFolders)
            {
                string folder = Path.Combine(prefabRoot, folderName);
                if (!Directory.Exists(folder)) continue;

                foreach (string file in Directory.GetFiles(folder, "*.sbc"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(name)) continue;

                    MyPrefabDefinition def;
                    if (!prefabDefs.TryGetValue(name, out def) || def == null || def.CubeGrids == null)
                    { missing++; continue; }

                    if (entries.ContainsKey(name)) { scanned++; continue; } // already scanned via the other folder

                    SePrefabCatalogue.Entry existing = new SePrefabCatalogue.Entry
                    {
                        Roles       = new List<string>(),
                        FactionHint = DeriveFactionHint(name, gridSaleFactionMap, spawnGroupOwners),
                        GridSize    = DetectGridSize(def),
                    };
                    ScanPrefab(def, prices, existing, unscoredWeapons);
                    entries[name] = existing;

                    // Capability-derived roles: eligibility comes from the prefab's own real stats
                    // (same suitability rules ShipPool uses at dispatch time), not from which folder
                    // discovered it -- an armed hull in Cargoships is Raid-eligible, a cargo-carrying
                    // hull in Drones is Trade-eligible, symmetrically.
                    AssignCapabilityRoles(existing, tuning);

                    scanned++;
                }
            }

            // Encounter folders: spawn-table membership is fixed by folder (design intent, not a
            // scannable property). What the prefab can DO is still read from the prefab.
            foreach (KeyValuePair<string, string[]> folderPair in EncounterFolderRoles)
            {
                string folder = Path.Combine(prefabRoot, folderPair.Key);
                if (!Directory.Exists(folder)) continue;

                // Recursive: GlobalEncounters/GlobalSubEncounters holds the Factorum gun platforms,
                // and a flat walk left them out of the catalogue entirely -- absent rather than
                // unroled, which is the quieter of the two failures. A sub-table is still that
                // folder's spawn intent, so it inherits the parent's encounter role.
                foreach (string file in Directory.GetFiles(folder, "*.sbc", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(name)) continue;

                    MyPrefabDefinition def;
                    if (!prefabDefs.TryGetValue(name, out def) || def == null || def.CubeGrids == null)
                    { missing++; continue; }

                    SePrefabCatalogue.Entry existing;
                    if (!entries.TryGetValue(name, out existing))
                    {
                        existing = new SePrefabCatalogue.Entry
                        {
                            Roles       = new List<string>(),
                            FactionHint = DeriveFactionHint(name, gridSaleFactionMap, spawnGroupOwners),
                            GridSize    = DetectGridSize(def),
                        };
                        ScanPrefab(def, prices, existing, unscoredWeapons);
                        // Encounter membership and capability are separate questions and a prefab
                        // answers both. Carrying only the spawn-table role makes the hull unreachable
                        // through GetNamesForRole however well armed or capacious it is -- which is
                        // why FCTM flew a generic hauler with four Factorum warships in the
                        // catalogue.
                        AssignCapabilityRoles(existing, tuning);
                        entries[name] = existing;
                    }

                    foreach (string role in folderPair.Value)
                    {
                        bool has = false;
                        foreach (string r in existing.Roles)
                            if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) { has = true; break; }
                        if (!has) existing.Roles.Add(role);
                    }
                    scanned++;
                }
            }

            // Economy folder: discovery only. Its prefabs take the same capability-derived roles
            // every other pass assigns and carry no store tag of their own.
            {
                string economyFolder = Path.Combine(prefabRoot, EconomyFolder);
                if (Directory.Exists(economyFolder))
                {
                    foreach (string file in Directory.GetFiles(economyFolder, "*.sbc"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrEmpty(name)) continue;

                        MyPrefabDefinition def;
                        if (!prefabDefs.TryGetValue(name, out def) || def == null || def.CubeGrids == null)
                        { missing++; continue; }

                        SePrefabCatalogue.Entry existing;
                        if (!entries.TryGetValue(name, out existing))
                        {
                            existing = new SePrefabCatalogue.Entry
                            {
                                Roles       = new List<string>(),
                                FactionHint = DeriveFactionHint(name, gridSaleFactionMap, spawnGroupOwners),
                                GridSize    = DetectGridSize(def),
                            };
                            ScanPrefab(def, prices, existing, unscoredWeapons);
                            // Being listed for sale and being flyable are separate questions, and a
                            // prefab answers both from the same scanned stats every other pass reads.
                            AssignCapabilityRoles(existing, tuning);
                            entries[name] = existing;
                        }

                        scanned++;
                    }
                }
            }

            // Mod prefabs: the folder walks above discover vanilla content by file, so a prefab a mod
            // contributes is loaded by MyDefinitionManager and still invisible here. Discovery for
            // those comes off the definition manager instead -- the engine's own resolved set is what
            // exists, and no folder describes it. Roles are capability-derived from the prefab's own
            // scanned stats, the same check the fleet folders use; a hull that clears no threshold
            // simply carries a book value and is never dispatched.
            //
            // Deliberately scoped to non-base-game definitions rather than everything the definition
            // manager holds: the vanilla folder list above is a real curation decision (respawn and
            // tutorial prefabs are out of scope on purpose), and widening it is a separate question
            // from making mod content visible. See docs/mod-compatibility.md, class 1.
            int fromMods = 0;
            foreach (KeyValuePair<string, MyPrefabDefinition> kv in prefabDefs)
            {
                MyPrefabDefinition def = kv.Value;
                if (def == null || def.CubeGrids == null) continue;
                if (entries.ContainsKey(kv.Key)) continue;
                // A null context is unknown provenance, not evidence of a mod -- leave it alone.
                if (def.Context == null || def.Context.IsBaseGame) continue;

                var entry = new SePrefabCatalogue.Entry
                {
                    Roles       = new List<string>(),
                    FactionHint = DeriveFactionHint(kv.Key, gridSaleFactionMap, spawnGroupOwners),
                    GridSize    = DetectGridSize(def),
                };
                ScanPrefab(def, prices, entry, unscoredWeapons);
                AssignCapabilityRoles(entry, tuning);
                entries[kv.Key] = entry;
                fromMods++;
            }

            // withoutWeaponBands is the acceptance number for FAC-221: a scan that resolves no DPS on
            // armed hulls is otherwise indistinguishable from a catalogue of genuinely unarmed ones.
            int withoutWeaponBands = 0;
            foreach (KeyValuePair<string, SePrefabCatalogue.Entry> kv in entries)
                if (kv.Value == null || kv.Value.WeaponBands == null || kv.Value.WeaponBands.Count == 0)
                    withoutWeaponBands++;

            int prefabsWithUnscored = 0, unscoredBlocks = 0;
            foreach (KeyValuePair<string, SePrefabCatalogue.Entry> kv in entries)
                if (kv.Value != null && kv.Value.UnscoredWeaponBlocks.HasValue)
                { prefabsWithUnscored++; unscoredBlocks += kv.Value.UnscoredWeaponBlocks.Value; }

            // Fuel coverage, the same shape as withoutWeaponBands: a catalogue whose hulls burn
            // nothing and one the fuel pass never reached read alike without these. The distinct
            // reactor-fuel count is also the bound on the mixed-fuel hull -- at one, no prefab can
            // hold reactors on two different ingots (see AccumulateReactorFuel).
            int withFuelData = 0, fuelledThrustBands = 0;
            var reactorFuels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, SePrefabCatalogue.Entry> kv in entries)
            {
                SePrefabCatalogue.Entry e = kv.Value;
                if (e == null) continue;
                if (e.HasFuelData) withFuelData++;
                if (!string.IsNullOrEmpty(e.ReactorFuelKey)) reactorFuels.Add(e.ReactorFuelKey);
                if (e.ThrustBands == null) continue;
                foreach (KeyValuePair<ThrustDirection, List<ThrustBand>> dir in e.ThrustBands)
                {
                    if (dir.Value == null) continue;
                    foreach (ThrustBand band in dir.Value)
                        if (!string.IsNullOrEmpty(band.FuelId)) fuelledThrustBands++;
                }
            }

            Log.Info(string.Format(
                "PrefabCatalogueScanner: scanned {0} prefab(s) across {1} folder(s) plus {2} from mod(s); "
                + "{3} not in definition manager; {4} with no weapon bands; "
                + "{5} unscoreable weapon block(s) on {6} prefab(s); "
                + "{7} with fuel data; {8} thrust band(s) on gas; {9} distinct reactor fuel(s).",
                entries.Count, FleetPrefabFolders.Length + EncounterFolderRoles.Count, fromMods, missing,
                withoutWeaponBands, unscoredBlocks, prefabsWithUnscored,
                withFuelData, fuelledThrustBands, reactorFuels.Count));

            // World-level coverage, independent of what any prefab happens to mount: how much of this
            // world's weapon content the definition-read chain can price at all. The per-prefab count
            // above can legitimately be zero while this is not -- that is the difference between "no
            // catalogued hull carries an unreadable weapon" and "this world contains none".
            int weaponDefs, unpriceableDefs;
            var unpriceableBySource = new Dictionary<string, int>(StringComparer.Ordinal);
            var shrapnelBySource = new Dictionary<string, string>(StringComparer.Ordinal);
            var beamBySource = new Dictionary<string, string>(StringComparer.Ordinal);
            WeaponStatsHelper.SurveyWeaponCoverage(out weaponDefs, out unpriceableDefs,
                                                   unpriceableBySource, shrapnelBySource, beamBySource);
            Log.Info(string.Format(
                "PrefabCatalogueScanner: weapon coverage -- {0} weapon block definition(s) in this world, "
                + "{1} priceable, {2} not, {3} priced with shrapnel, {4} firing framework beams.",
                weaponDefs, weaponDefs - unpriceableDefs, unpriceableDefs, shrapnelBySource.Count, beamBySource.Count));
            ReportUnscored(unpriceableBySource, "unpriceable weapon definition");

            // Named, because "the framework's definitions were read" and "shrapnel reached a Dps
            // number" are different claims and only the second is the point of the feature.
            foreach (KeyValuePair<string, string> kv in shrapnelBySource)
                Log.Info(string.Format("PrefabCatalogueScanner: shrapnel-priced weapon {0}: {1}",
                                       kv.Key, kv.Value));
            foreach (KeyValuePair<string, string> kv in beamBySource)
                Log.Info(string.Format("PrefabCatalogueScanner: beam weapon {0}: {1}", kv.Key, kv.Value));

            // Named, because the aggregate says a gap exists and this says what to go and look at.
            // Capped so a mod that ships a hundred unreadable weapons cannot flood the log.
            ReportUnscored(unscoredWeapons, "unscoreable weapon block");

            // Write JSON sidecar for human inspection.
            if (!string.IsNullOrEmpty(storageDir))
            {
                try
                {
                    Directory.CreateDirectory(storageDir);
                    string outPath = Path.Combine(storageDir, "prefab_catalogue.json");
                    var catalogue = new SePrefabCatalogue(entries);
                    File.WriteAllText(outPath, catalogue.ToJson());
                    Log.Info("PrefabCatalogueScanner: wrote " + outPath);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "PrefabCatalogueScanner: could not write prefab_catalogue.json.");
                }
            }

            return new SePrefabCatalogue(entries);
        }

        // ---- station designs (refit targets) ----------------------------------------------------

        /// <summary>
        /// Makes every vanilla station prefab answerable as a refit target: a
        /// <c>Prefab/&lt;id&gt;</c> recipe so the rebuild has a bill, and a
        /// <c>Kind = "Station"</c> catalogue entry carrying the site, cost, weapon bands and block
        /// count <c>station prefabs</c> lists and the refit gate compares on. Without it a founded
        /// node could only ever be rebuilt as a player capture, because
        /// <c>RefitStationValidator</c> refuses a design the catalogue does not hold and one with no
        /// recipe behind it.
        ///
        /// <para><b>Recipes are topped up, not re-registered.</b> The registry's own definition walk
        /// already gives most prefabs a bill; only the ids it missed are aggregated here, so the
        /// common case does no work and the uncommon one is covered.</para>
        ///
        /// <para><b>Cost and weapon bands come from the book-value cache, not from this scanner.</b>
        /// They are the same numbers a founded node's <c>StationBookValue</c> and
        /// <c>StationWeapons</c> are written from, and a refit that changed the listed price into
        /// something the node would never take would be lying about the upgrade. Everything the
        /// cache does not compute — blocks, cargo, mass, fingerprint — is scanned here.</para>
        ///
        /// <para><b>The Factorum family gets a recipe and no station entry.</b> Its structures carry
        /// no store, no contract board and no medbay, so they are not designs a trading node may be
        /// rebuilt as; but a node that already stands as one still needs its bill priced, or the net
        /// difference would charge gross for refitting away from it.</para>
        /// </summary>
        internal static int RegisterStationPrefabs(SePrefabCatalogue catalogue, SeRecipeRegistry recipes,
            PrefabBookValueCache bookValues, IItemPriceCatalogue prices,
            Func<string, bool> isCapture = null)
        {
            if (catalogue == null || bookValues == null || MyDefinitionManager.Static == null) return 0;

            var prefabDefs = new Dictionary<string, MyPrefabDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, MyPrefabDefinition> kv in MyDefinitionManager.Static.GetPrefabDefinitions())
                prefabDefs[kv.Key] = kv.Value;

            Dictionary<string, string> sitesByName = StationFounder.GetKnownPrefabSites();
            Log.Info("PrefabCatalogueScanner: SE station-type tables site " + sitesByName.Count + " prefab(s).");
            var unclassified = new List<string>();
            var takenByCaptures = new List<string>();
            int registered = 0;

            foreach (string prefabId in bookValues.StationPrefabIds)
            {
                if (string.IsNullOrEmpty(prefabId)) continue;
                // A player scan holding this id owns it: the capture's entry is what
                // CustomStationGrids resolves and what any node standing as it was priced from, and
                // overwriting it here would leave a public station design whose grids are a private
                // ship. The vanilla design loses its refit slot, which is the cheaper of the two.
                if (isCapture != null && isCapture(prefabId)) { takenByCaptures.Add(prefabId); continue; }
                MyPrefabDefinition def;
                if (!prefabDefs.TryGetValue(prefabId, out def) || def == null || def.CubeGrids == null) continue;

                if (recipes != null && !recipes.HasPrefabRecipe(prefabId))
                    recipes.RegisterPrefabRecipe(prefabId, def.CubeGrids);

                if (bookValues.IsFactorumStation(prefabId)) continue;

                string site = StationPrefabSite.OfPrefab(prefabId, sitesByName);
                if (site == null) { unclassified.Add(prefabId); continue; }

                var entry = new SePrefabCatalogue.Entry
                {
                    Roles       = new List<string>(),
                    FactionHint = "All",
                    GridSize    = DetectGridSize(def),
                    Kind        = "Station",
                    Site        = site,
                };
                ScanPrefab(def.CubeGrids, prices, entry);
                entry.Cost = bookValues.StationBookValue(prefabId);
                entry.WeaponBands = new List<WeaponBand>(bookValues.StationWeaponBands(prefabId));
                // Null owner: a vanilla design belongs to nobody and every faction may refit to it.
                catalogue.RegisterCustomPrefab(prefabId, entry, null);
                registered++;
            }

            if (takenByCaptures.Count > 0)
                Log.Warn("PrefabCatalogueScanner: " + takenByCaptures.Count + " vanilla station prefab id(s) "
                    + "are held by player-scanned designs of the same name and are not offered as refit "
                    + "targets: " + string.Join(", ", takenByCaptures.ToArray()));

            if (unclassified.Count > 0)
                Log.Warn("PrefabCatalogueScanner: " + unclassified.Count + " station prefab(s) name no "
                    + "environment and SE lists them under no station type, so they are not offered as "
                    + "refit targets: " + string.Join(", ", unclassified.ToArray()));

            return registered;
        }

        // ---- per-prefab metadata ----------------------------------------------------------------

        /// <summary>
        /// Returns the dominant grid class for the prefab:
        /// "Station" if any grid is static, "Large" if any grid is large-grid, "Small" otherwise.
        /// Static check takes priority — a large static grid is a station, not a ship.
        /// </summary>
        private static string DetectGridSize(MyPrefabDefinition def)
        {
            return DetectGridSize(def.CubeGrids);
        }

        /// <summary>Grid-array overload of <see cref="DetectGridSize(MyPrefabDefinition)"/> — the real
        /// body, reused by `!factions ship capture` (<c>ShipCaptureScanner</c>) against a live grid's
        /// own object builders instead of a static prefab definition's.</summary>
        internal static string DetectGridSize(MyObjectBuilder_CubeGrid[] grids)
        {
            bool hasLarge = false;
            foreach (MyObjectBuilder_CubeGrid grid in grids)
            {
                if (grid == null) continue;
                if (grid.IsStatic) return "Station";
                if (grid.GridSizeEnum == MyCubeSize.Large) hasLarge = true;
            }
            return hasLarge ? "Large" : "Small";
        }

        /// <summary>How many distinct unscoreable weapon subtypes get named in the boot log before the
        /// report collapses to a count. The aggregate is always exact; only the naming is capped.</summary>
        private const int UnscoredWeaponReportLimit = 12;

        /// <summary>Names the heaviest contributors to an unscoreable tally, so the aggregate above says
        /// a gap exists and this says what to go and look at. Capped: a mod shipping a hundred
        /// unreadable weapons must not flood the boot log.</summary>
        private static void ReportUnscored(Dictionary<string, int> tally, string label)
        {
            if (tally == null || tally.Count == 0) return;

            var byCount = new List<KeyValuePair<string, int>>(tally);
            byCount.Sort((a, b) => b.Value.CompareTo(a.Value));
            int shown = Math.Min(byCount.Count, UnscoredWeaponReportLimit);
            for (int i = 0; i < shown; i++)
                Log.Info(string.Format("PrefabCatalogueScanner: {0} {1} x{2}",
                                       label, byCount[i].Key, byCount[i].Value));
            if (byCount.Count > shown)
                Log.Info(string.Format("PrefabCatalogueScanner: {0} further {1} kind(s) not listed.",
                                       byCount.Count - shown, label));
        }

        private static void ScanPrefab(MyPrefabDefinition def, IItemPriceCatalogue prices,
                                       SePrefabCatalogue.Entry entry,
                                       Dictionary<string, int> unscoredWeapons = null)
        {
            ScanPrefab(def.CubeGrids, prices, entry, unscoredWeapons);
            entry.CrewLayout.AuthoredCrew = Math.Max(0, def.MaxNPCCount);
            if (def.NPCSpawnPointsOverride != null)
                foreach (var point in def.NPCSpawnPointsOverride)
                    if (point != null) entry.CrewLayout.SpawnPoints++;
        }

        /// <summary>Grid-array overload of <see cref="ScanPrefab(MyPrefabDefinition, IItemPriceCatalogue,
        /// SePrefabCatalogue.Entry)"/> — the real body, reused by `!factions ship capture`
        /// (<c>ShipCaptureScanner</c>) against a live grid's own object builders instead of a static
        /// prefab definition's.</summary>
        internal static void ScanPrefab(MyObjectBuilder_CubeGrid[] grids, IItemPriceCatalogue prices,
                                        SePrefabCatalogue.Entry entry,
                                        Dictionary<string, int> unscoredWeapons = null)
        {
            var weaponBlockStats = new List<(float dps, float rangeM)>();
            var munitionSlots = new List<MunitionSlot>();
            var hullWeapons = new WeaponStatsHelper.HullWeaponPricer();
            int unscored = 0;
            var thrustBlockStats = new Dictionary<ThrustDirection, List<ThrustBand>>();
            // Block census for the fingerprint, taken in this loop rather than a second pass of its
            // own: the key has to be the RESOLVED definition's id, and this is where the resolution
            // already happens. Reading it anywhere else risks the object-builder id and the live
            // block's definition id drifting apart, and the symptom of that would be a hull that
            // silently matches nothing.
            var census = new List<KeyValuePair<string, long>>();
            int gridsSeen = 0;
            int blocksSeen = 0;
            // The hull's flight frame, resolved the same way the spawner resolves it on the live grid
            // (HullOrientation.SelectNose): the seats its author placed, ranked against the thrust
            // behind each facing. The remote control the spawner injects is mounted to match, so the
            // frame here is the one the hull will actually be flown in.
            // The frame is resolved from the LEAD grid alone, and from its seats in the order the
            // live ladder sees them: cockpits, then remote controls. VanillaFleetSpawner wires
            // resultList[0] and scans that grid only, so a seat or thruster on a subgrid — a
            // rotor-mounted turret, a docked shuttle — is not evidence about how the hull flies.
            // Live 2026-09-17: Nagamaki scanned as (nose Backward, up Down) against a spawn that
            // injected (Up, Backward), and the gate judged an attitude the flight layer never holds.
            var seats = new List<HullSeat>();
            var remoteSeats = new List<HullSeat>();
            // Vanilla encounter prefabs can put small wreck fragments before the inhabited hull.
            // Crew evidence follows the largest grid, independently of the existing flight frame.
            MyObjectBuilder_CubeGrid crewGrid = null;
            foreach (MyObjectBuilder_CubeGrid candidate in grids)
                if (candidate != null && candidate.CubeBlocks != null && candidate.CubeBlocks.Count > 0
                    && (crewGrid == null || candidate.CubeBlocks.Count > crewGrid.CubeBlocks.Count))
                    crewGrid = candidate;
            var crew = new CrewLayout { Known = crewGrid != null, IsStation = crewGrid != null && crewGrid.IsStatic };
            var pushForceN = new Dictionary<ThrustDirection, double>();

            foreach (MyObjectBuilder_CubeGrid grid in grids)
            {
                if (grid == null) continue;
                bool leadGrid = gridsSeen == 0;
                bool habitationGrid = grid == crewGrid;
                gridsSeen++;
                if (grid.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                {
                    if (block == null) continue;
                    // Counted before the definition lookup, so the number agrees with the live
                    // MyCubeGrid.BlocksCount a capture is capped against rather than dropping the
                    // blocks whose definitions no longer resolve.
                    blocksSeen++;

                    MyCubeBlockDefinition blockDef;
                    if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef)
                        || blockDef == null)
                    {
                        if (habitationGrid) crew.Known = false;
                        continue;
                    }

                    census.Add(new KeyValuePair<string, long>(DefinitionKey(blockDef.Id), 1));

                    // Dry mass: sum every block's precomputed component mass (SE itself computes
                    // MyCubeBlockDefinition.Mass as Sum(component.Count * component.Definition.Mass)).
                    entry.DryMassKg += blockDef.Mass;

                    // A resolved block means the power blocks below were actually looked at, so the
                    // manifest's zeros read as "this hull has none" rather than "never scanned" --
                    // the same distinction DryMassKg's own zero carries.
                    entry.HasFuelData = true;

                    // Thrust: real per-block force/curve data, keyed by the block's own mounted
                    // facing (the same value SE uses to resolve applied thrust direction).
                    ThrustDirection thrustDirection;
                    ThrustBand? thrustBand;
                    ThrustStatsHelper.ComputeThrustStats(blockDef, block.BlockOrientation.Forward,
                        out thrustDirection, out thrustBand);
                    if (thrustBand.HasValue)
                    {
                        List<ThrustBand> forDirection;
                        if (!thrustBlockStats.TryGetValue(thrustDirection, out forDirection))
                            thrustBlockStats[thrustDirection] = forDirection = new List<ThrustBand>();
                        forDirection.Add(thrustBand.Value);

                        // Keyed by the direction the force pushes the grid, which is the opposite of the
                        // bucket above: HullOrientation ranks a facing by what the hull can accelerate
                        // along it.
                        if (leadGrid)
                        {
                            ThrustDirection push = HullOrientation.Opposite(thrustDirection);
                            double already;
                            pushForceN.TryGetValue(push, out already);
                            pushForceN[push] = already + thrustBand.Value.ForceMagnitudeN;
                        }
                    }

                    var seatDef = blockDef as MyCockpitDefinition;
                    if (habitationGrid && seatDef != null)
                    {
                        if (seatDef is MyCryoChamberDefinition) crew.CryoChambers++;
                        else if (seatDef.EnableShipControl) crew.PilotStations++;
                        else crew.CrewSeats++;
                    }
                    if (leadGrid && (seatDef != null || blockDef is MyRemoteControlDefinition))
                    {
                        var cockpit = block as MyObjectBuilder_Cockpit;
                        (seatDef != null ? seats : remoteSeats).Add(new HullSeat
                        {
                            Forward      = ThrustStatsHelper.ToThrustDirection(block.BlockOrientation.Forward),
                            Up           = ThrustStatsHelper.ToThrustDirection(block.BlockOrientation.Up),
                            IsMain       = cockpit != null && cockpit.IsMainCockpit,
                            // A remote control controls the ship by definition; a cockpit class also
                            // covers furniture, and only the definition separates the two.
                            ControlsShip = seatDef == null || seatDef.EnableShipControl,
                        });
                    }

                    // Cost: sum component costs.
                    if (blockDef.Components != null)
                    {
                        foreach (MyCubeBlockDefinition.Component comp in blockDef.Components)
                        {
                            if (comp == null || comp.Count <= 0 || comp.Definition == null) continue;
                            string subType = comp.Definition.Id.SubtypeName;
                            if (string.IsNullOrEmpty(subType)) continue;

                            long unitPrice = prices != null ? prices.BasePrice(subType) : 0;
                            if (unitPrice <= 0)
                            {
                                var physDef = comp.Definition as MyPhysicalItemDefinition;
                                if (physDef != null) unitPrice = physDef.MinimalPricePerUnit;
                            }
                            entry.Cost += comp.Count * unitPrice;
                        }
                    }

                    // Cargo liters: cargo container blocks only.
                    var cargoDef = blockDef as MyCargoContainerDefinition;
                    if (cargoDef != null)
                    {
                        float invVol = cargoDef.InventorySize.X * cargoDef.InventorySize.Y * cargoDef.InventorySize.Z;
                        if (invVol > 0f)
                            entry.CargoLiters += (long)(invVol * 1000f);
                        else
                        {
                            float cellM = cargoDef.CubeSize == MyCubeSize.Large ? 2.5f : 0.5f;
                            entry.CargoLiters += (long)(cargoDef.Size.X * cellM
                                                      * cargoDef.Size.Y * cellM
                                                      * cargoDef.Size.Z * cellM * 1000f);
                        }
                        continue; // cargo blocks are not weapons
                    }

                    // Gas tanks: litres per stored gas, kept apart from CargoLiters. A
                    // MyGasTankDefinition is a sibling of MyCargoContainerDefinition, not a
                    // subclass, so nothing here is double-counted above. StoredGasId falls back to
                    // Oxygen when the block leaves it null, which is what the vanilla large Oxygen
                    // Tank (empty SubtypeId) relies on.
                    var gasTankDef = blockDef as MyGasTankDefinition;
                    if (gasTankDef != null)
                    {
                        if (gasTankDef.Capacity > 0f)
                        {
                            string gasKey = InventoryKeyMapper.FromTypeIdAndSubtype(
                                gasTankDef.StoredGasId.TypeId.ToString(), gasTankDef.StoredGasId.SubtypeName);
                            if (!string.IsNullOrEmpty(gasKey))
                            {
                                if (entry.GasTankLiters == null)
                                    entry.GasTankLiters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                                long held;
                                entry.GasTankLiters.TryGetValue(gasKey, out held);
                                entry.GasTankLiters[gasKey] = held + (long)gasTankDef.Capacity;
                            }
                        }
                        continue; // gas tanks are not weapons
                    }

                    // Battery store: the electric half of the fuel manifest, in MWh. A sibling cast
                    // like the gas tank above, and for the same reason -- MyBatteryBlockDefinition
                    // is a MyPowerProducerDefinition, unrelated to anything counted so far.
                    var batteryDef = blockDef as MyBatteryBlockDefinition;
                    if (batteryDef != null)
                    {
                        if (batteryDef.MaxStoredPower > 0f) entry.BatteryMwh += batteryDef.MaxStoredPower;
                        continue; // batteries are not weapons
                    }

                    // Reactor fuel: which ingot the hull's reactors burn, and how many kilograms of
                    // it they hold.
                    var reactorDef = blockDef as MyReactorDefinition;
                    if (reactorDef != null)
                    {
                        AccumulateReactorFuel(reactorDef, entry);
                        continue; // reactors are not weapons
                    }

                    // Drill yield: CutOutRadius is the only MyShipDrillDefinition field carrying
                    // throughput meaning (DiscardingMultiplier is a debris-scatter radius, not a yield
                    // term; the ore-mass conversion is a fixed engine constant, not block-specific --
                    // see docs/managers.md's "Mine" section). Speed is excluded deliberately: it drives
                    // the drill head's visual spin animation only (decompiled SE MyShipDrill.cs:
                    // Render.UpdateSpeed), so it is not a cutting rate. The score itself is the swept
                    // cross-section (MineTargeting.DrillBlockScore); Mine's liters/second rate comes
                    // from it via the Factions-owned FactionTuning.MineDrillFallbackLitersPerScorePoint,
                    // decoupled from any live/mod-adjustable SE mining mechanic.
                    var drillDef = blockDef as MyShipDrillDefinition;
                    if (drillDef != null)
                    {
                        entry.DrillYieldScore += MineTargeting.DrillBlockScore(drillDef.CutOutRadius);
                        continue; // drill blocks are not weapons
                    }

                    // Weapon stats: real per-block Dps/range, resolved via the weapon/ammo chain, and
                    // priced together once the hull is read so wired beam weapons count as wired.
                    float blockDps, blockRangeM;
                    WeaponStatsHelper.ComputeWeaponStats(blockDef, out blockDps, out blockRangeM);
                    if (WeaponStatsHelper.HullWeaponPricer.Prices(blockDef, blockDps))
                    {
                        hullWeapons.Add(blockDef, blockDps, blockRangeM);
                    }
                    else if (WeaponStatsHelper.IsScoreableWeapon(blockDef))
                    {
                        // A weapon we recognised and could not price. Recorded rather than silently
                        // counted as unarmed -- the block's own definition names where it came from,
                        // so this needs no list of which mods behave this way.
                        unscored++;
                        if (unscoredWeapons != null)
                        {
                            string key = blockDef.Id.SubtypeName + " <- " + DefinitionDumpWriter.SourceOf(blockDef);
                            int seen;
                            unscoredWeapons.TryGetValue(key, out seen);
                            unscoredWeapons[key] = seen + 1;
                        }
                    }
                }
            }

            foreach (var priced in hullWeapons.Finish())
            {
                if (priced.dps <= 0f) continue;
                weaponBlockStats.Add((priced.dps, priced.rangeM));
                MunitionSlot slot;
                if (WeaponStatsHelper.TryReadMunitionSlot(priced.def, priced.dps, priced.rangeM, out slot))
                    munitionSlots.Add(slot);
            }

            entry.BlockCount = blocksSeen;
            crew.Known = crew.Known && gridsSeen > 0 && census.Count > 0;
            entry.CrewLayout = crew;
            entry.WeaponBands = WeaponStatsHelper.GroupIntoBands(weaponBlockStats);
            entry.MunitionSlots = munitionSlots.Count > 0 ? WeaponStatsHelper.GroupSlots(munitionSlots) : null;
            entry.ThrustBands = ThrustStatsHelper.GroupIntoBands(thrustBlockStats);

            seats.AddRange(remoteSeats);

            ThrustDirection nose, up;
            HullNoseSource noseSource;
            HullOrientation.SelectNose(seats, pushForceN, out nose, out up, out noseSource);
            entry.HullNose = nose;
            entry.HullUp = up;
            entry.HullNoseSource = noseSource;
            entry.HasHullFrame = true;
            entry.UnscoredWeaponBlocks = unscored > 0 ? (int?)unscored : null;
            // Never entry.GridSize: a caller sets that field before calling in some paths and after
            // in others, and the fingerprint has to be a function of the grids alone.
            entry.Fingerprint = PrefabFingerprint.Compute(census, DetectGridSize(grids), gridsSeen);
        }

        /// <summary>Adds one reactor's fuel key and capacity to <paramref name="entry"/>'s manifest.
        ///
        /// <para>Capacity is the block's inventory volume divided by the fuel item's volume per
        /// unit, times its mass per unit — <c>MyPhysicalItemDefinition.Volume</c> is in cubic
        /// metres (its <c>Init</c> divides the definition file's litres by 1000) and
        /// <c>MyReactorDefinition.InventoryMaxVolume</c> is the same unit, so the ratio is a unit
        /// count and the product is kilograms. Vanilla large reactor: 1 m³ ÷ 0.000052 m³ × 1 kg =
        /// 19,231 kg of uranium.</para>
        ///
        /// <para>A reactor naming several fuels contributes its first; a hull whose reactors name
        /// different fuels keeps the first key seen and counts capacity only for reactors on that
        /// key, since one number cannot mean two items. No vanilla reactor has more than one
        /// <c>FuelInfo</c> and every one of them is <c>Ingot:Uranium</c>, so the case needs a modded
        /// reactor to arise at all; the scan summary's distinct-fuel count is what says whether this
        /// world has one.</para></summary>
        private static void AccumulateReactorFuel(MyReactorDefinition reactorDef, SePrefabCatalogue.Entry entry)
        {
            if (reactorDef.FuelInfos == null || reactorDef.FuelInfos.Length == 0) return;

            MyReactorDefinition.FuelInfo fuel = reactorDef.FuelInfos[0];
            string fuelKey = InventoryKeyMapper.FromTypeIdAndSubtype(
                fuel.FuelId.TypeId.ToString(), fuel.FuelId.SubtypeName);
            if (string.IsNullOrEmpty(fuelKey)) return;

            if (string.IsNullOrEmpty(entry.ReactorFuelKey)) entry.ReactorFuelKey = fuelKey;
            else if (!string.Equals(entry.ReactorFuelKey, fuelKey, StringComparison.OrdinalIgnoreCase)) return;

            MyPhysicalItemDefinition fuelItem = fuel.FuelDefinition;
            if (fuelItem == null || fuelItem.Volume <= 0f || fuelItem.Mass <= 0f) return;
            if (reactorDef.InventoryMaxVolume <= 0f) return;

            entry.ReactorFuelCapacityKg += reactorDef.InventoryMaxVolume / fuelItem.Volume * fuelItem.Mass;
        }

        /// <summary>One block's identity in a fingerprint census: the definition id as
        /// <c>TypeId/SubtypeName</c>. Both fingerprint sources go through this, so the string can
        /// only be written one way.</summary>
        private static string DefinitionKey(MyDefinitionId id)
        {
            return id.TypeId.ToString() + "/" + id.SubtypeName;
        }

        /// <summary>
        /// The fingerprint of a LIVE mechanical group — the other half of the match the Services
        /// Terminal's "Send to Node" makes, against the value <see cref="ScanPrefab(
        /// MyObjectBuilder_CubeGrid[], IItemPriceCatalogue, SePrefabCatalogue.Entry,
        /// Dictionary{string,int})"/> stored for a prefab.
        ///
        /// <para>It sits here, beside that one, because the two mappings only mean anything if they
        /// agree: same definition-id spelling, same size class, same grid count. Splitting them
        /// across two files is how they would come to disagree.</para>
        /// </summary>
        internal static string ComputeLiveFingerprint(List<MyCubeGrid> grids)
        {
            if (grids == null || grids.Count == 0) return PrefabFingerprint.None;

            var census = new List<KeyValuePair<string, long>>();
            bool hasLarge = false, isStatic = false;
            int gridsSeen = 0;
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null) continue;
                gridsSeen++;
                if (grid.IsStatic) isStatic = true;
                if (grid.GridSizeEnum == MyCubeSize.Large) hasLarge = true;
                foreach (MySlimBlock block in grid.CubeBlocks)
                {
                    if (block == null || block.BlockDefinition == null) continue;
                    census.Add(new KeyValuePair<string, long>(DefinitionKey(block.BlockDefinition.Id), 1));
                }
            }
            // Mirrors DetectGridSize's own precedence exactly (static wins, then large).
            string size = isStatic ? "Station" : (hasLarge ? "Large" : "Small");
            return PrefabFingerprint.Compute(census, size, gridsSeen);
        }

        /// <summary>Assigns Trade/Ferry/Gift/Haul/Raid/Patrol/Guard/Mine roles from <paramref
        /// name="entry"/>'s already-scanned stats (cargo/weapon/drill) against <paramref
        /// name="tuning"/>'s classification floors — the same capability-derived logic <see
        /// cref="Scan"/> applies to every <see cref="FleetPrefabFolders"/> prefab, extracted so
        /// `!factions ship capture` (<c>ShipCaptureScanner</c>) can apply it identically to a
        /// player-captured entry.</summary>
        internal static void AssignCapabilityRoles(SePrefabCatalogue.Entry entry, FactionTuning tuning)
        {
            double dps = ShipPool.TotalDps(entry.WeaponBands);
            double minCargo = string.Equals(entry.GridSize, "Small", StringComparison.OrdinalIgnoreCase)
                ? tuning.MinCargoLitersForRoleClassificationSmall
                : tuning.MinCargoLitersForRoleClassificationLarge;

            if (ShipPool.IsCargoSuitableValue(entry.CargoLiters, minCargo, entry.GridSize))
            {
                entry.Roles.Add("Trade");
                entry.Roles.Add("Ferry");
                entry.Roles.Add("Gift");
                entry.Roles.Add("Haul");
            }
            if (ShipPool.IsCombatSuitableValue(dps, tuning.MinCombatDpsForRoleClassification, entry.GridSize))
            {
                entry.Roles.Add("Raid");
                entry.Roles.Add("Patrol");
                entry.Roles.Add("Guard");
            }
            if (ShipPool.IsMineSuitableValue(entry.DrillYieldScore, entry.CargoLiters, tuning.MinDrillYieldForRoleClassification, entry.GridSize))
                entry.Roles.Add("Mine");
        }

        // ---- faction hint -----------------------------------------------------------------------

        private static string DeriveFactionHint(string prefabName,
                                                Dictionary<string, string> gridSaleMap,
                                                Dictionary<string, string> spawnGroupOwners)
        {
            // 1. GridsForSale provides ground truth for Economy-folder prefabs.
            string hint;
            if (gridSaleMap.TryGetValue(prefabName, out hint))
                return hint;

            // 2. The spawn-group owner, where SE names one. Ahead of the name rule because it is a
            // statement rather than an inference: most Factorum content does not say "Factorum" in
            // its filename, and the name rule found 7 of 22 on the live content set. Behind
            // GridsForSale because that is the same kind of statement about the prefabs it covers,
            // and the two do not overlap. See SpawnGroupFactionIndex for why only some faction
            // subtypes are adopted.
            if (spawnGroupOwners != null && spawnGroupOwners.TryGetValue(prefabName, out hint))
                return hint;

            // 3. Name keyword matching.
            foreach (string[] pair in NameFactionHints)
                if (prefabName.IndexOf(pair[0], StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair[1];

            return "All";
        }

        /// <summary>Builds a reverse map: prefabId → faction type name, from
        /// <c>MyFactionTypeDefinition.GridsForSale</c>.</summary>
        private static Dictionary<string, string> BuildGridsForSaleMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MyDefinitionManager.Static == null) return map;
            try
            {
                foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
                {
                    var ftd = def as MyFactionTypeDefinition;
                    if (ftd == null || ftd.GridsForSale == null) continue;
                    string typeName = ftd.Id.SubtypeName;
                    if (string.IsNullOrEmpty(typeName)) continue;
                    foreach (string prefabId in ftd.GridsForSale)
                    {
                        if (!string.IsNullOrEmpty(prefabId) && !map.ContainsKey(prefabId))
                            map[prefabId] = typeName;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PrefabCatalogueScanner: could not read GridsForSale definitions.");
            }
            return map;
        }
    }
}
