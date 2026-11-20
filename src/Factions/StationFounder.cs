using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>Summary returned by <see cref="StationFounder.MigrateMismatchedStationPrefabs"/>.</summary>
    public struct StationPrefabMigrationResult
    {
        public int Checked;
        public int Mismatched;
        /// <summary>Of <see cref="Mismatched"/>, the stations standing as a design their owner would
        /// not draw at that site today (<see cref="StationPrefabPool.Classify"/>).</summary>
        public int OutsidePool;
        public int Refounded;
        public List<string> Details;
    }

    /// <summary>Summary returned by <see cref="StationFounder.FixStationOwnershipMismatches"/>.</summary>
    public struct StationOwnershipFixResult
    {
        public int Checked;
        public int Mismatched;
        public int Fixed;
        public List<string> Details;
    }

    /// <summary>
    /// Creates and backfills SE faction stations for brain-owned nodes (ActionKind.Found).
    /// Prefab selection is environment- and personality-aware: Surface nodes get Outpost prefabs,
    /// space/orbit nodes get SpaceStation prefabs, raider factions prefer _Pirate variants.
    /// StableStationId, StationPrefabId, and StationBookValue are all set atomically on success.
    /// </summary>
    internal sealed class StationFounder
    {
        private static readonly MethodInfo GetStationTypeDefinitionM;
        private static readonly FieldInfo StationPrefabsPerFactionTypeF;
        private static readonly PropertyInfo StationNamesP;
        private static readonly PropertyInfo GeneratedItemsContainerTypeP;
        private static readonly FieldInfo StoreItemsGeneratorF;
        internal static readonly MethodInfo StationNameSetter =
            AccessTools.PropertySetter(typeof(MyFactionStation), "Name");

        // MyFactionStation.IsDeepSpaceStation is get-only over this private field. We deliberately
        // never want it true (see ClearDeepSpaceFlags) so a plain field write in place is enough --
        // no need for the full object-replace machinery MigrateMismatchedStationPrefabs uses for
        // Type/PrefabName, since this flag carries no physical/grid consequence to rebuild around.
        private static readonly FieldInfo IsDeepSpaceStationField =
            AccessTools.Field(typeof(MyFactionStation), "m_isDeepSpaceStation");

        // MyFaction.m_stations is a private Dictionary<long, IMyFactionStation>; AddStation is a
        // plain Dictionary.Add that throws on a duplicate key, so replacing an already-founded
        // station's entry (same Id, new object) needs direct dictionary access. Internal (not
        // private) so StationMaterializer.PermanentlyRemove can reuse the same reflected handle
        // rather than duplicating the AccessTools.Field lookup.
        internal static readonly FieldInfo StationsDictField =
            AccessTools.Field(typeof(MyFaction), "m_stations");

        // Used only when the catalogue is missing or unusable for a personality/type; pool size no
        // longer reaches here, since SelectName disambiguates with an ordinal instead of failing.
        // Never a real catalogue name -- safe to match on exactly (Ordinal) to find nodes still
        // stuck on it (see RetryFallbackStationNames).
        private const string FallbackStationName = "Outpost";

        private static bool? _crunchLoaded;

        static StationFounder()
        {
            Type generatorType = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyStationGenerator");
            if (generatorType != null)
                GetStationTypeDefinitionM = AccessTools.Method(generatorType, "GetStationTypeDefinition",
                    new[] { typeof(MyStationTypeEnum) });

            Type defType = AccessTools.TypeByName("Sandbox.Definitions.MyStationsListDefinition");
            if (defType != null)
            {
                // A public field, not a property: a property lookup resolves to null and every read of
                // SE's per-faction-type station tables comes back empty without an error.
                StationPrefabsPerFactionTypeF   = AccessTools.Field(defType, "StationPrefabsPerFactionType");
                if (StationPrefabsPerFactionTypeF == null)
                    FactionsPlugin.Log.Warn("StationFounder: MyStationsListDefinition.StationPrefabsPerFactionType "
                        + "not found; station designs cannot be sited from SE's station-type tables.");
                StationNamesP                   = AccessTools.Property(defType, "StationNames");
                GeneratedItemsContainerTypeP    = AccessTools.Property(defType, "GeneratedItemsContainerType");
            }

            StoreItemsGeneratorF = AccessTools.Field(typeof(MySessionComponentEconomy), "m_storeItemsGenerator");
        }

        private readonly Random _rng = new Random();
        private Func<string, long> _bookValueLookup;
        private Func<string, List<WeaponBand>> _weaponBandsLookup;
        private Func<bool, string, IList<string>> _prefabPool;
        private IPlanetCatalogue _planets;
        private NameSelector _nameSelector;
        private Func<string, bool> _isPlayerFactionLookup;
        private Func<string, long> _catalogueCost;
        private Func<string, List<WeaponBand>> _catalogueWeapons;
        private Func<string, bool> _isCapturedDesign;
        private Func<string, bool> _isFactorumStation;

        /// <summary>
        /// Wires up the book-value lookup, weapon-band lookup, prefab pool, planet catalogue,
        /// and station name selector used by <see cref="FoundStation"/> and <see cref="BackfillStationFields"/>.
        /// Call once after <see cref="PrefabBookValueCache"/> is ready.
        /// </summary>
        public void Configure(Func<string, long> bookValueLookup,
            Func<string, List<WeaponBand>> weaponBandsLookup,
            Func<bool, string, IList<string>> prefabPool,
            IPlanetCatalogue planets,
            NameSelector nameSelector = null,
            Func<string, bool> isPlayerFactionLookup = null,
            Func<string, bool> isFactorumStation = null)
        {
            _bookValueLookup   = bookValueLookup;
            _weaponBandsLookup = weaponBandsLookup;
            _prefabPool        = prefabPool;
            _planets           = planets;
            _nameSelector      = nameSelector;
            _isPlayerFactionLookup = isPlayerFactionLookup;
            _isFactorumStation = isFactorumStation;
        }

        /// <summary>
        /// Returns all known station prefab names for <see cref="PrefabBookValueCache"/> to value.
        /// Merges the static vanilla set with anything discovered via SE reflection.
        /// </summary>
        internal static IEnumerable<string> GetKnownPrefabNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in PrefabBookValueCache.KnownStationPrefabs) names.Add(s);
            try
            {
                if (MySession.Static == null) return names;
                if (GetStationTypeDefinitionM == null) return names;
                object def = GetStationTypeDefinitionM.Invoke(null, new object[] { MyStationTypeEnum.SpaceStation });
                if (def == null) return names;

                if (StationPrefabsPerFactionTypeF != null)
                {
                    var perType = StationPrefabsPerFactionTypeF.GetValue(def) as System.Collections.IDictionary;
                    if (perType != null)
                        foreach (System.Collections.DictionaryEntry entry in perType)
                        {
                            var list = entry.Value as System.Collections.IList;
                            if (list == null) continue;
                            foreach (object item in list)
                            {
                                string prefab = item == null ? null : item.ToString();
                                if (!string.IsNullOrEmpty(prefab)) names.Add(prefab);
                            }
                        }
                }

                if (StationNamesP != null)
                {
                    var allNames = StationNamesP.GetValue(def) as System.Collections.IList;
                    if (allNames != null)
                        foreach (object item in allNames)
                        {
                            string prefab = item == null ? null : item.ToString();
                            if (!string.IsNullOrEmpty(prefab)) names.Add(prefab);
                        }
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// Which of the two refit environments each station prefab SE knows belongs to, keyed by
        /// prefab id. Asked of <c>MyStationGenerator</c>'s own per-<see cref="MyStationTypeEnum"/>
        /// lists, so a prefab SE files under Outpost is Surface and one it files under SpaceStation
        /// or OrbitalStation is Space — the same two-way collapse
        /// <see cref="Factions.Core.StationPrefabSite"/> draws, sourced from the game's own
        /// classification rather than inferred from a name.
        ///
        /// <para>Sparse on purpose: a prefab SE lists under no station type is absent rather than
        /// guessed at, and the caller decides what to do with an unanswerable id.</para>
        /// </summary>
        internal static Dictionary<string, string> GetKnownPrefabSites()
        {
            var sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (GetStationTypeDefinitionM == null || StationPrefabsPerFactionTypeF == null) return sites;

            MyStationTypeEnum[] types =
            {
                MyStationTypeEnum.Outpost, MyStationTypeEnum.OrbitalStation, MyStationTypeEnum.SpaceStation,
            };
            foreach (MyStationTypeEnum type in types)
            {
                string site = StationPrefabSite.For(SiteForStationType(type));
                try
                {
                    object def = GetStationTypeDefinitionM.Invoke(null, new object[] { type });
                    if (def == null) continue;
                    var perType = StationPrefabsPerFactionTypeF.GetValue(def) as System.Collections.IDictionary;
                    if (perType == null) continue;
                    foreach (System.Collections.DictionaryEntry entry in perType)
                    {
                        var list = entry.Value as System.Collections.IList;
                        if (list == null) continue;
                        foreach (object item in list)
                        {
                            string prefab = item == null ? null : item.ToString();
                            // First type wins: Outpost is walked first, so a prefab listed under
                            // both an outpost and a space table stays the surface design it is.
                            if (!string.IsNullOrEmpty(prefab) && !sites.ContainsKey(prefab))
                                sites[prefab] = site;
                        }
                    }
                }
                catch { }
            }
            return sites;
        }

        /// <summary>The inverse of <see cref="StationTypeEnumFor"/>: which site a station type
        /// stands at. Orbital collapses with space, as everywhere else.</summary>
        private static StationSite SiteForStationType(MyStationTypeEnum type)
        {
            if (type == MyStationTypeEnum.Outpost) return StationSite.Surface;
            return type == MyStationTypeEnum.OrbitalStation ? StationSite.Orbital : StationSite.Space;
        }

        /// <summary>Wires what <see cref="ReplaceStationPrefab"/> needs on top of
        /// <see cref="Configure"/>: the prefab catalogue's own cost and weapon bands (which answer
        /// for a scanned design as well as a vanilla one, and are the figures a node's
        /// <c>StationBookValue</c>/<c>StationWeapons</c> are written from), and which ids are player
        /// scans rather than content-walk prefabs.</summary>
        public void ConfigureRefit(Func<string, long> catalogueCost,
            Func<string, List<WeaponBand>> catalogueWeapons,
            Func<string, bool> isCapturedDesign)
        {
            _catalogueCost    = catalogueCost;
            _catalogueWeapons = catalogueWeapons;
            _isCapturedDesign = isCapturedDesign;
        }

        /// <summary>
        /// Founds a new SE station for <paramref name="st"/> at the node identified by
        /// <paramref name="nodeId"/>. Sets StableStationId, StationPrefabId, and StationBookValue
        /// atomically on success. Assigns a deterministic station name from the catalogue if
        /// available, otherwise falls back to the legacy "{TAG} Outpost" format.
        /// Must be called from the SE game thread.
        /// </summary>
        public bool FoundStation(string nodeId, FactionState st, SpatialGraph graph,
            bool? isPirateStyleOverride = null)
        {
            if (MySession.Static == null) return false;

            SpatialNode node;
            if (!graph.Nodes.TryGetValue(nodeId, out node)) return false;
            if (node.StableStationId != 0) return false;

            MyFaction faction = FindFaction(st.Tag);
            // IsEffectivelyNpc, not SE's IsEveryoneNpc(): a station's own crew bots are faction
            // members whose identities SE never marks NPC, so the raw check reads false for any
            // faction that currently has crew standing anywhere.
            bool isNpc = faction != null && FactionsPlugin.IsEffectivelyNpc(faction);
            bool isAllowedPlayerFaction = !isNpc && (_isPlayerFactionLookup?.Invoke(st.Tag) ?? false);
            if (faction == null || (!isNpc && !isAllowedPlayerFaction)) return false;

            // Every founding -- NPC and player-faction alike -- sites the station on the node itself,
            // so the scanner GPS (which reads node.Position) always matches where the grid stands.
            var position = new Vector3D(
                node.Position.X * 1000.0,
                node.Position.Y * 1000.0,
                node.Position.Z * 1000.0);

            // Environment and style stay two independent axes: the caller may override style (a
            // Magnate-tier player picking a pirate look in a ring band is a real combination) but
            // never where the station is.
            StationSite site = SiteFor(node);
            bool isSpace  = site != StationSite.Surface;
            string style = StationStyle.Resolve(isPirateStyleOverride, st.PersonalityId);
            string prefabName = PickPrefab(isSpace, style);

            // Must match PickPrefab's own environment split above, or a surface node gets an
            // Outpost-shaped grid mislabeled MyStationTypeEnum.SpaceStation (wrong spawn-distance/
            // container lookups, and misleading in every log line).
            MyStationTypeEnum stationTypeEnum = StationTypeEnumFor(site);

            MyDefinitionId? containerTypeId = ResolveContainerTypeId(stationTypeEnum);
            long stationId = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STATION);

            // Choose the vanilla FactionNames category that matches the physical node environment.
            string stationType = StationTypeFor(site);

            // Try to select a catalogue name; fall back to a tag-free generic name if unavailable.
            string stationName = null;
            if (_nameSelector != null)
            {
                stationName = _nameSelector.SelectName(st.Tag, st.PersonalityId, stationType, nodeId);
            }
            if (string.IsNullOrEmpty(stationName))
            {
                stationName = FallbackStationName;
                FactionsPlugin.Log.Warn("StationFounder: no catalogue name for " + st.Tag
                    + " (personality=" + st.PersonalityId + ", type=" + stationType
                    + "); founding " + nodeId + " as the generic fallback.");
            }

            var station = new MyFactionStation(
                stationId,
                stationName,
                position,
                stationTypeEnum,
                faction,
                prefabName,
                generatedItemsContainerTypeId: containerTypeId,
                up:      null,
                forward: null,
                // Deliberately always false: IsDeepSpaceStation feeds vanilla's own store-price/
                // contract-reward "hard to reach" bonus (MyFactionTypeBaseStrategy.GenerateOffers,
                // MyContractTypeBaseStrategy), which is redundant with -- and would fight -- our
                // own scarcity-driven pricing (PriceOracle.ScarcityMultiplier). Not a bug fix like
                // Type/prefab above: a deliberate choice to never opt into vanilla's mechanic.
                isDeep:  false);

            faction.AddStation(station);
            node.StableStationId = stationId;
            // The claim set is no longer re-derived every frame, so a founding has to say so. Its
            // whole point is to be standing before vanilla's cell generator reaches this seed.
            StationMaterializer.MarkClaimsDirty();
            node.StationPrefabId = prefabName;
            node.DisplayName = stationName;
            if (_bookValueLookup != null)
            {
                long bookValue = _bookValueLookup(prefabName);
                if (bookValue > 0) node.StationBookValue = bookValue;
            }
            if (_weaponBandsLookup != null)
            {
                List<WeaponBand> bands = _weaponBandsLookup(prefabName);
                if (bands != null && bands.Count > 0) node.StationWeapons = bands;
            }

            PopulateStoreItemsVanilla(faction);
            return true;
        }

        /// <summary>
        /// Scans all nodes that have a StableStationId but are missing StationPrefabId or
        /// StationBookValue and fills in the gaps. Returns the count of nodes patched.
        /// </summary>
        public int BackfillStationFields(SpatialGraph graph, IList<FactionState> states)
        {
            if (graph == null) return 0;

            var stateByTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
            if (states != null)
                foreach (FactionState s in states)
                    if (s != null && s.Tag != null) stateByTag[s.Tag] = s;

            int patched = 0;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node.StableStationId == 0) continue;
                bool needsPrefab  = string.IsNullOrEmpty(node.StationPrefabId);
                bool needsValue   = !node.StationBookValue.HasValue || node.StationBookValue.Value <= 0;
                bool needsWeapons = node.StationWeapons == null || node.StationWeapons.Count == 0;
                if (!needsPrefab && !needsValue && !needsWeapons) continue;

                if (needsPrefab && _prefabPool != null)
                {
                    bool isSpace  = SiteFor(node) != StationSite.Surface;
                    FactionState st;
                    string style = node.OwnerFactionTag != null
                        && stateByTag.TryGetValue(node.OwnerFactionTag, out st)
                        ? StationStyle.ForPersonality(st.PersonalityId)
                        : StationStyle.Civilian;
                    node.StationPrefabId = PickPrefab(isSpace, style);
                    needsValue = !node.StationBookValue.HasValue || node.StationBookValue.Value <= 0;
                    needsWeapons = true;
                }

                if (needsValue && _bookValueLookup != null && !string.IsNullOrEmpty(node.StationPrefabId))
                {
                    long bookValue = _bookValueLookup(node.StationPrefabId);
                    if (bookValue > 0) node.StationBookValue = bookValue;
                }

                if (needsWeapons && _weaponBandsLookup != null && !string.IsNullOrEmpty(node.StationPrefabId))
                {
                    List<WeaponBand> bands = _weaponBandsLookup(node.StationPrefabId);
                    if (bands != null && bands.Count > 0) node.StationWeapons = bands;
                }

                patched++;
            }
            return patched;
        }

        /// <summary>
        /// Re-founds every already-founded station whose live MyStationTypeEnum no longer matches
        /// the environment its node actually stands in (<see cref="StationSiting"/>) -- the "predates
        /// planet support" gap: FoundStation always founded stations as MyStationTypeEnum.SpaceStation
        /// regardless of environment (fixed for future foundings), but MyFactionStation.Type/PrefabName
        /// are get-only after construction, so anything founded before that fix keeps its wrong prefab
        /// forever unless explicitly rebuilt here.
        ///
        /// <para>This is also the retrofit for anything founded while station type was read off
        /// <see cref="SpatialNode.Zone"/> -- a node in a ring around a gas giant founded a surface
        /// Outpost, and re-running this migration is what converts it.</para>
        ///
        /// When <paramref name="applyFix"/> is false this only reports what would change (dry run).
        /// When true, for each mismatch: closes any live grid/safe zone under the OLD station object
        /// (its physical layout no longer matches the new prefab), builds a replacement
        /// MyFactionStation with the SAME Id/Name/Position/Up/Forward (current orientation is already
        /// gravity-corrected -- not re-randomized) and the newly-picked, environment-correct prefab,
        /// and splices it into the faction's station dictionary via reflection (MyFaction.AddStation
        /// is a plain Dictionary.Add that throws on a duplicate key). Does not itself trigger a
        /// respawn -- StationMaterializer's normal player-proximity path picks up the now-zeroed
        /// StationEntityId, or call StationMaterializer.ForceRespawn for an immediate one.
        ///
        /// <para>A station is also rebuilt when it stands as a design its owner would not draw there
        /// today (<see cref="StationPrefabPool.Classify"/>): a Factorum structure under a trading
        /// faction, or a design SE does not found at that site. Three owners are left alone: a
        /// dormant one (a derelict holds whatever it was abandoned with), a player faction, and any
        /// node standing as a scanned design — both of those are choices someone paid for.</para>
        ///
        /// <para><paramref name="onlyNodes"/>, when non-empty, limits every check to those node ids.</para>
        /// </summary>
        public StationPrefabMigrationResult MigrateMismatchedStationPrefabs(SpatialGraph graph,
            IList<FactionState> states, bool applyFix, DateTime nowUtc, StationMaterializer materializer = null,
            ICollection<string> onlyNodes = null)
        {
            var result = new StationPrefabMigrationResult { Details = new List<string>() };
            if (graph == null) return result;

            var stateByTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
            if (states != null)
                foreach (FactionState s in states)
                    if (s != null && s.Tag != null) stateByTag[s.Tag] = s;

            Dictionary<string, string> seListedSites = GetKnownPrefabSites();
            bool filtered = onlyNodes != null && onlyNodes.Count > 0;

            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node.StableStationId == 0 || node.OwnerFactionTag == null) continue;
                if (filtered && !onlyNodes.Contains(node.Id)) continue;

                MyFaction faction = FindFaction(node.OwnerFactionTag);
                if (faction == null) continue;

                MyFactionStation station = faction.GetStationById(node.StableStationId) as MyFactionStation;
                if (station == null) continue;

                result.Checked++;
                StationSite site = SiteFor(node);
                MyStationTypeEnum wantType = StationTypeEnumFor(site);

                FactionState st;
                stateByTag.TryGetValue(node.OwnerFactionTag, out st);
                string style = st != null ? StationStyle.ForPersonality(st.PersonalityId) : StationStyle.Civilian;
                bool isSpace = site != StationSite.Surface;
                bool typeWrong = station.Type != wantType;
                PoolMismatch poolWrong = PoolMismatch.None;
                if (st != null && !st.IsPlayerFaction && !FactionLifecycle.IsDormant(st, nowUtc)
                    && !(_isCapturedDesign != null && _isCapturedDesign(node.StationPrefabId)))
                    poolWrong = StationPrefabPool.Classify(style, node.StationPrefabId, isSpace,
                        _isFactorumStation, seListedSites);
                if (!typeWrong && poolWrong == PoolMismatch.None) continue;

                result.Mismatched++;
                if (poolWrong != PoolMismatch.None) result.OutsidePool++;
                string oldPrefab = station.PrefabName;

                if (!applyFix)
                {
                    string why;
                    if (typeWrong) why = station.Type + "/" + oldPrefab + " -> " + wantType;
                    else if (poolWrong == PoolMismatch.FactorumStructure)
                        why = node.StationPrefabId + " is a Factorum structure -> a " + style + " design";
                    else why = node.StationPrefabId + " is not a design SE founds here -> a " + style + " design";
                    result.Details.Add(node.OwnerFactionTag + " #" + node.StableStationId + " " + node.Id
                        + " (" + node.DisplayName + "): " + why + " (site=" + site + ")");
                    continue;
                }

                // A rebuild the owner has already paid for, waiting for the host to stand it up.
                // ReplaceStationPrefab clears PendingStationPrefabId on the way through -- correct
                // for the refit path, where the pending id IS the target, and a silent write-off
                // here, where the target is a vanilla prefab of this environment.
                if (!string.IsNullOrEmpty(node.PendingStationPrefabId))
                {
                    result.Details.Add(node.OwnerFactionTag + " #" + node.StableStationId
                        + " (" + node.DisplayName + "): has a pending refit to "
                        + node.PendingStationPrefabId + "; apply it first (refitapply) or it would be lost");
                    continue;
                }

                string newPrefab = PickPrefab(isSpace, style);

                string swapDetail;
                if (!ReplaceStationPrefab(node, st, newPrefab, materializer, out swapDetail))
                {
                    result.Details.Add(node.OwnerFactionTag + " #" + node.StableStationId
                        + " (" + node.DisplayName + "): " + oldPrefab + " -> " + newPrefab
                        + " REFUSED (" + swapDetail + ")");
                    continue;
                }

                result.Refounded++;
                result.Details.Add(node.OwnerFactionTag + " #" + node.StableStationId + " " + node.Id
                    + " (" + node.DisplayName + "): " + swapDetail + " (site=" + site + ")");
            }

            return result;
        }

        /// <summary>
        /// Stands a founded node up as a different station design, in place: the live grid and safe
        /// zone go, the SE-side <c>MyFactionStation</c> is rebuilt around the same Id/Name/Position/
        /// Up/Forward, and the node's own prefab identity, collateral and weapon bands are rewritten
        /// from the catalogue. The next player approach materialises the new design
        /// (<see cref="StationMaterializer.BeginSpawn"/>); nothing is spawned here.
        ///
        /// <para>Two callers, one body: <see cref="MigrateMismatchedStationPrefabs"/> swapping a
        /// station onto an environment-correct vanilla prefab, and a player's completed refit
        /// swapping it onto whatever design was paid for.</para>
        ///
        /// <para><b>Order.</b> The crew sweep runs FIRST, because a crew bot's
        /// <c>AnchorEntityId</c> is the SAFE ZONE's id and the replacement station object throws
        /// that id away — after which nothing can match the leftovers. Then the grid closes and
        /// deregisters from the economy component, then the zone.</para>
        ///
        /// <para><b>What SE is told.</b> <c>MyFactionStation.PrefabName</c> stays a vanilla prefab of
        /// the matching site, always: SE's economy component and anything deserialising the faction's
        /// station list read it, and a scanned design's id resolves to nothing there.
        /// <c>node.StationPrefabId</c> is the truth, and the materializer resolves it first. The
        /// vanilla-suppression claim is untouched — it keys on the station Id, which does not
        /// move.</para>
        ///
        /// <para><b>Where the numbers come from.</b> The prefab catalogue, which answers for a
        /// scanned design and a vanilla station alike and is the same source
        /// <c>station prefabs</c> quoted the player a price from. It falls back to the founding
        /// book-value lookup only where the catalogue has no entry at all — the Factorum family,
        /// which carries a build recipe but is deliberately not a refit target, and which
        /// <see cref="PickPrefab"/> can still choose for a migration.</para>
        ///
        /// <para><b>A design that cannot be resolved is refused</b> (<see cref="CanStandAs"/>),
        /// leaving the node standing as what it is and any pending rebuild still pending. Writing an
        /// unresolvable id would cost the node its collateral and its guns and leave the materializer
        /// spawning the SE-facing vanilla name instead — a silent downgrade rather than a
        /// failure.</para>
        /// </summary>
        public bool ReplaceStationPrefab(SpatialNode node, FactionState owner, string prefabId,
            StationMaterializer materializer, out string detail)
        {
            detail = null;
            if (node == null || string.IsNullOrEmpty(prefabId)) { detail = "no node or no design"; return false; }
            if (MySession.Static == null) { detail = "no session loaded"; return false; }
            if (node.StableStationId == 0) { detail = "node " + node.Id + " has no founded station"; return false; }

            string tag = owner != null ? owner.Tag : node.OwnerFactionTag;
            MyFaction faction = FindFaction(tag);
            if (faction == null) { detail = "no SE faction '" + tag + "'"; return false; }

            MyFactionStation station = faction.GetStationById(node.StableStationId) as MyFactionStation;
            if (station == null)
            {
                detail = tag + " holds no station #" + node.StableStationId;
                return false;
            }

            string unresolvable;
            if (!CanStandAs(prefabId, out unresolvable)) { detail = unresolvable; return false; }

            StationSite site = SiteFor(node);
            MyStationTypeEnum wantType = StationTypeEnumFor(site);
            string style = StationStyle.ForPersonality(owner != null ? owner.PersonalityId : null);
            string sePrefabName = SeFacingPrefabName(station.PrefabName, prefabId, site, style);

            string oldPrefab = node.StationPrefabId;
            long oldGridId = station.StationEntityId;

            int crewRemoved = CloseStanding(station, tag, node.Id);

            // Type and PrefabName are get-only after construction, so the station object is rebuilt
            // and spliced into the faction's dictionary: MyFaction.AddStation is a plain
            // Dictionary.Add and throws on the key that is already there.
            MyDefinitionId? containerTypeId = ResolveContainerTypeId(wantType);
            var replacement = new MyFactionStation(
                station.Id, station.Name, station.Position, wantType, faction, sePrefabName,
                generatedItemsContainerTypeId: containerTypeId,
                up:      (Vector3)station.Up,
                forward: (Vector3)station.Forward,
                isDeep:  false); // deliberately never opts into vanilla's deep-space bonus -- see FoundStation

            if (StationsDictField != null)
            {
                var dict = StationsDictField.GetValue(faction) as Dictionary<long, IMyFactionStation>;
                if (dict != null) dict[station.Id] = replacement;
            }

            node.StationPrefabId = prefabId;
            long bookValue = RefitBookValue(prefabId);
            node.StationBookValue = bookValue > 0 ? (long?)bookValue : null;
            List<WeaponBand> bands = RefitWeaponBands(prefabId);
            node.StationWeapons = (bands != null && bands.Count > 0) ? bands : null;
            node.PendingStationPrefabId = null;

            // The replacement carries StationEntityId/SafeZoneEntityId 0 by construction, so SE and
            // vanilla's cell generator both read this station as unspawned. The materializer's own
            // bookkeeping has to agree, or its proximity pass would keep pointing at the grid that
            // was just closed.
            if (materializer != null) materializer.ForgetMaterialization(node.Id);

            detail = (oldPrefab ?? "(none)") + " -> " + prefabId
                     + " se=" + sePrefabName
                     + " gridId=" + oldGridId + " closed"
                     + (crewRemoved > 0 ? ", " + crewRemoved + " crew bot(s) removed" : "");
            return true;
        }

        /// <summary>Closes what stands for <paramref name="station"/> -- its crew, grid, safe zone and
        /// rock -- ahead of the record being replaced. Returns how many crew bots went. Also run by a
        /// Nexus agent, whose copy of the record is replaced when the authority's changes.</summary>
        internal static int CloseStanding(MyFactionStation station, string tag, string nodeId)
        {
            long oldGridId = station.StationEntityId;
            long oldZoneId = station.SafeZoneEntityId;
            // Last moment the bots and this station are still connected -- see ReplaceStationPrefab.
            int crewRemoved = oldZoneId != 0
                ? StationMaterializer.DespawnOrphanedCrew(oldZoneId, tag, station.Id, nodeId)
                : 0;

            if (oldGridId != 0)
            {
                MyCubeGrid existing;
                if (MyEntities.TryGetEntityById(oldGridId, out existing, false)) existing.Close();
                MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(oldGridId);
            }
            if (oldZoneId != 0)
            {
                MySafeZone staleZone;
                if (MyEntities.TryGetEntityById(oldZoneId, out staleZone, false)) staleZone.Close();
            }
            // The old design's rock goes with it; the new design stands its own on its next spawn.
            StationVoxels.CloseAll(station.Id, new HashSet<long> { oldGridId });
            return crewRemoved;
        }

        /// <summary>Records that a node's live station already <b>is</b> <paramref name="prefabId"/>:
        /// its identity, collateral and weapon bands are rewritten from the catalogue and nothing in
        /// world moves. The caller has just scanned this node's own materialised grid as that design,
        /// so the blocks it adds were welded by hand and are not billed again; without this the next
        /// despawn would stand the node back up as what it was.
        ///
        /// <para>A pending refit is left pending: it is a design the owner paid for and will still
        /// stand the next time the station is built in world.</para>
        ///
        /// <para>Refused, leaving the node as it was, for a design <see cref="CanStandAs"/> cannot
        /// resolve.</para></summary>
        public bool AdoptStandingDesign(SpatialNode node, string prefabId, out string detail)
        {
            detail = null;
            if (node == null || string.IsNullOrEmpty(prefabId)) { detail = "no node or no design"; return false; }
            if (node.StableStationId == 0) { detail = "node " + node.Id + " has no founded station"; return false; }
            string unresolvable;
            if (!CanStandAs(prefabId, out unresolvable)) { detail = unresolvable; return false; }

            string oldPrefab = node.StationPrefabId;
            node.StationPrefabId = prefabId;
            long bookValue = RefitBookValue(prefabId);
            node.StationBookValue = bookValue > 0 ? (long?)bookValue : null;
            List<WeaponBand> bands = RefitWeaponBands(prefabId);
            node.StationWeapons = (bands != null && bands.Count > 0) ? bands : null;
            detail = (oldPrefab ?? "(none)") + " -> " + prefabId;
            return true;
        }

        /// <summary>Whether there is anything to stand up under this id, and what it is worth.
        /// Three ways it can be nothing: a scanned design whose <c>.sbc</c> did not come back at boot
        /// (the capture store is grid-keyed, so an id it has lost reads as a vanilla prefab and would
        /// fall through to the SE-facing name), a vanilla name SE holds no prefab definition for, and
        /// a design neither the catalogue nor the book-value walk prices — which is the node's
        /// collateral, so accepting a zero would quietly write it off.
        ///
        /// <para><paramref name="reason"/> starts with <see cref="UnresolvableDetailPrefix"/> so the
        /// caller can tell this permanent refusal from the transient ones (no session, no faction)
        /// that are worth re-asking on the next sweep.</para></summary>
        public bool CanStandAs(string prefabId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(prefabId)) { reason = UnresolvableDetailPrefix + "no design named"; return false; }

            bool isCapture = _isCapturedDesign != null && _isCapturedDesign(prefabId);
            if (!isCapture)
            {
                bool resolves = false;
                try
                {
                    MyPrefabDefinition def = MyDefinitionManager.Static != null
                        ? MyDefinitionManager.Static.GetPrefabDefinition(prefabId) : null;
                    resolves = def != null && def.CubeGrids != null && def.CubeGrids.Length > 0;
                }
                catch { resolves = false; }
                if (!resolves)
                {
                    reason = UnresolvableDetailPrefix + "'" + prefabId + "' is neither a scanned design "
                             + "the capture store still holds nor a prefab SE can resolve";
                    return false;
                }
            }

            if (RefitBookValue(prefabId) <= 0)
            {
                reason = UnresolvableDetailPrefix + "nothing prices '" + prefabId
                         + "', so the node would stand with no collateral";
                return false;
            }
            return true;
        }

        /// <summary>Marks a <see cref="ReplaceStationPrefab"/> refusal that asking again cannot fix.
        /// The sweep reads it to log a held rebuild once instead of on every pass.</summary>
        public const string UnresolvableDetailPrefix = "unresolvable: ";

        /// <summary>The vanilla prefab name SE is told this station is, which is never a scanned
        /// design's id. A vanilla target is itself; for a scanned design the station keeps the
        /// vanilla name it already carried when that name is sited correctly, and takes a fresh draw
        /// from the pool when it is not (or when there was nothing there to keep).</summary>
        private string SeFacingPrefabName(string currentSeName, string prefabId, StationSite site, string style)
        {
            bool isCapture = _isCapturedDesign != null && _isCapturedDesign(prefabId);
            if (!isCapture) return prefabId;

            if (!string.IsNullOrEmpty(currentSeName))
            {
                string wanted = StationPrefabSite.For(site);
                Dictionary<string, string> known = GetKnownPrefabSites();
                string knownSite;
                if (known.TryGetValue(currentSeName, out knownSite)
                    && string.Equals(knownSite, wanted, StringComparison.OrdinalIgnoreCase))
                    return currentSeName;
            }
            return PickPrefab(site != StationSite.Surface, style);
        }

        /// <summary>What a node standing as this design is worth. Catalogue first, because it is the
        /// one source that answers for a player scan; the founding book-value lookup is the fallback
        /// for a vanilla prefab the catalogue holds no station entry for.</summary>
        private long RefitBookValue(string prefabId)
        {
            if (_catalogueCost != null)
            {
                long cost = _catalogueCost(prefabId);
                if (cost > 0) return cost;
            }
            return _bookValueLookup != null ? _bookValueLookup(prefabId) : 0L;
        }

        /// <summary>What a node standing as this design shoots back with — the figure every combat
        /// reader takes the station's defence from. Same source order as <see cref="RefitBookValue"/>,
        /// for the same reason.</summary>
        private List<WeaponBand> RefitWeaponBands(string prefabId)
        {
            if (_catalogueWeapons != null)
            {
                List<WeaponBand> bands = _catalogueWeapons(prefabId);
                if (bands != null && bands.Count > 0) return bands;
            }
            return _weaponBandsLookup != null ? _weaponBandsLookup(prefabId) : null;
        }

        /// <summary>
        /// Design choice, not a bug fix: MyFactionStation.IsDeepSpaceStation feeds vanilla's own
        /// store-price/contract-reward "hard to reach" bonus, which we never want since it's
        /// redundant with (and would fight) our own scarcity-driven pricing. FoundStation now always
        /// founds with isDeep: false, but every station founded before that carries whatever the
        /// hardcoded isDeep: true left behind. A plain field write in place is enough here -- unlike
        /// Type/PrefabName there is no physical/grid consequence to rebuild around, so this never
        /// touches a live grid. Returns how many stations had the flag flipped (0 if applyFix is
        /// false -- dry run, just counts).
        /// </summary>
        public int ClearDeepSpaceFlags(SpatialGraph graph, bool applyFix)
        {
            if (graph == null || IsDeepSpaceStationField == null) return 0;

            int affected = 0;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node.StableStationId == 0 || node.OwnerFactionTag == null) continue;

                MyFaction faction = FindFaction(node.OwnerFactionTag);
                if (faction == null) continue;

                MyFactionStation station = faction.GetStationById(node.StableStationId) as MyFactionStation;
                if (station == null || !station.IsDeepSpaceStation) continue;

                affected++;
                if (applyFix) IsDeepSpaceStationField.SetValue(station, false);
            }
            return affected;
        }

        /// <summary>
        /// Renames a successfully conquered station for its new owner. This is intentionally called
        /// only by the successful Conquer resolution path; startup/backfill never renames stations.
        /// Updates the save-backed MyFactionStation, an already-materialized grid when present,
        /// and the persisted node display name. Also migrates the MyFactionStation's own
        /// registration to the new owner's <c>m_stations</c> dict (see
        /// <see cref="MigrateStationRegistration"/>) -- a rename alone leaves the station enumerable
        /// only under whichever faction originally founded it, so every subsequent
        /// faction.GetStationById(...) lookup scoped to the new owner (StationMaterializer's
        /// BeginSpawn/ForceRespawn, MigrateMismatchedStationPrefabs) keeps silently missing it.
        /// </summary>
        public bool RenameConqueredStation(SpatialNode node, FactionState newOwner)
        {
            if (node == null || newOwner == null || node.StableStationId == 0 || _nameSelector == null)
                return false;

            string stationName = _nameSelector.SelectName(newOwner.Tag, newOwner.PersonalityId,
                StationTypeFor(SiteFor(node)), node.Id);
            if (string.IsNullOrEmpty(stationName)) return false;

            string gridName = stationName;
            MyFaction currentOwningFaction;
            MyFactionStation station = FindStation(node.StableStationId, out currentOwningFaction);
            if (station != null)
            {
                MigrateStationRegistration(node.StableStationId, station, currentOwningFaction, newOwner.Tag);

                try
                {
                    if (StationNameSetter != null)
                    {
                        StationNameSetter.Invoke(station, new object[] { stationName });
                    }
                }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn("Could not rename MyFactionStation " + node.StableStationId + ": " + ex.Message);
                }

                if (station.StationEntityId != 0)
                {
                    MyCubeGrid materialized;
                    if (MyEntities.TryGetEntityById(station.StationEntityId, out materialized, false))
                    {
                        materialized.DisplayName = gridName;
                        RenameBeacons(materialized, gridName);
                    }
                }
            }

            // Scan-backed nodes use their grid entity ID as StableStationId rather than a station ID.
            MyCubeGrid directGrid;
            if (MyEntities.TryGetEntityById(node.StableStationId, out directGrid, false))
            {
                directGrid.DisplayName = gridName;
                RenameBeacons(directGrid, gridName);
            }

            // Persist even if the grid is currently lazy-despawned. It will receive the station
            // name when StationMaterializer next spawns it.
            node.DisplayName = gridName;
            return true;
        }

        /// <summary>
        /// One-time operator backfill for founded stations whose MyFactionStation registration
        /// predates the <see cref="RenameConqueredStation"/> fix above (2026-08-17) that started
        /// migrating <c>MyFaction.m_stations</c> membership alongside every ownership-transfer
        /// rename. Every Conquer/Claim/Withdraw that happened before that fix shipped left its
        /// station filed under whichever faction founded it, invisible to every subsequent
        /// faction-scoped lookup (<c>faction.GetStationById</c>) for the node's real, current owner
        /// -- e.g. `factions reclaim &lt;newOwnerTag&gt; &lt;nodeId&gt;` failing with "not found on
        /// &lt;tag&gt;" despite the dashboard correctly showing the new owner. Scans every founded
        /// node (StableStationId != 0), compares its Factions-model owner against whichever faction
        /// actually holds the MyFactionStation today, and migrates any mismatch found. When
        /// <paramref name="applyFix"/> is false this only reports what would change (dry run).
        /// </summary>
        public StationOwnershipFixResult FixStationOwnershipMismatches(SpatialGraph graph, bool applyFix)
        {
            var result = new StationOwnershipFixResult { Details = new List<string>() };
            if (graph == null) return result;

            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node.StableStationId == 0 || node.OwnerFactionTag == null) continue;

                MyFaction owningFaction;
                MyFactionStation station = FindStation(node.StableStationId, out owningFaction);
                if (station == null) continue; // scan-backed node, or no live faction data at all

                result.Checked++;
                string actualOwnerTag = owningFaction != null ? owningFaction.Tag : "(none)";
                if (string.Equals(actualOwnerTag, node.OwnerFactionTag, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Mismatched++;
                result.Details.Add(node.Id + ": registered=" + actualOwnerTag + " should=" + node.OwnerFactionTag);

                if (!applyFix) continue;

                MigrateStationRegistration(node.StableStationId, station, owningFaction, node.OwnerFactionTag);
                result.Fixed++;
            }

            return result;
        }

        // Vanilla only stamps a beacon's CustomName once, at spawn time (MyPrefabManager
        // .CreateGridsFromPrefab sets every MyObjectBuilder_Beacon's CustomName to the spawn-time
        // BeaconName) -- nothing re-syncs it afterward. A rename that only touches grid.DisplayName
        // therefore leaves an already-spawned station's beacon(s) showing the stale name. Uses the
        // networked SetCustomName (not the plain DisplayNameText setter) so the new name actually
        // propagates to connected clients, not just the server's local state.
        internal static void RenameBeacons(MyCubeGrid grid, string name)
        {
            if (grid == null || string.IsNullOrEmpty(name)) return;
            foreach (MyBeacon beacon in grid.GetFatBlocks<MyBeacon>())
                beacon.SetCustomName(name);
        }

        /// <summary>
        /// Selects a real catalogue name for every already-founded node still stuck on
        /// <see cref="FallbackStationName"/> ("Outpost") from a founding that found no catalogue
        /// name. Selection-only: writes node.DisplayName in the abstract model; live
        /// MyFactionStation/grid reconciliation is a separate concern, handled by
        /// <see cref="ApplyPersistedDisplayName"/> immediately after. Safe to call every boot:
        /// idempotent, only touches nodes whose DisplayName is still exactly the fallback literal,
        /// and leaves a node untouched (to retry next boot) if the catalogue still cannot name it.
        /// Returns the count of nodes renamed.
        /// </summary>
        public int RetryFallbackStationNames(SpatialGraph graph, IList<FactionState> states)
        {
            if (graph == null || _nameSelector == null) return 0;

            var stateByTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
            if (states != null)
                foreach (FactionState s in states)
                    if (s != null && s.Tag != null) stateByTag[s.Tag] = s;

            int renamed = 0;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node.StableStationId == 0 || node.OwnerFactionTag == null) continue;
                if (!string.Equals(node.DisplayName, FallbackStationName, StringComparison.Ordinal)) continue;

                FactionState st;
                if (!stateByTag.TryGetValue(node.OwnerFactionTag, out st)) continue;

                string newName = _nameSelector.SelectName(node.OwnerFactionTag, st.PersonalityId, StationTypeFor(SiteFor(node)), node.Id);
                if (string.IsNullOrEmpty(newName)) continue;  // no usable catalogue entry -- retry next boot

                node.DisplayName = newName;
                renamed++;
            }
            return renamed;
        }

        /// <summary>
        /// Applies the persisted, tag-free node display name to the save-backed station and any
        /// currently materialized grid. Used after the one-shot offline migration so the
        /// dashboard, MyFactionStation, and grid all converge on the same catalogue name.
        /// </summary>
        public bool ApplyPersistedDisplayName(SpatialNode node)
        {
            if (node == null || node.StableStationId == 0 || string.IsNullOrEmpty(node.DisplayName))
                return false;

            bool changed = false;
            string stationName = node.DisplayName;
            MyFactionStation station = FindStation(node.StableStationId);
            if (station != null)
            {
                try
                {
                    if (StationNameSetter != null && !string.Equals(station.Name, stationName, StringComparison.Ordinal))
                    {
                        StationNameSetter.Invoke(station, new object[] { stationName });
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn("Could not apply station name " + node.StableStationId + ": " + ex.Message);
                }

                if (station.StationEntityId != 0)
                {
                    MyCubeGrid materialized;
                    if (MyEntities.TryGetEntityById(station.StationEntityId, out materialized, false)
                        && !string.Equals(materialized.DisplayName, stationName, StringComparison.Ordinal))
                    {
                        materialized.DisplayName = stationName;
                        RenameBeacons(materialized, stationName);
                        changed = true;
                    }
                }
            }

            MyCubeGrid directGrid;
            if (MyEntities.TryGetEntityById(node.StableStationId, out directGrid, false)
                && !string.Equals(directGrid.DisplayName, stationName, StringComparison.Ordinal))
            {
                RenameBeacons(directGrid, stationName);
                directGrid.DisplayName = stationName;
                changed = true;
            }
            return changed;
        }

        /// <summary>What kind of place this node stands in, asked of the world itself.
        ///
        /// <para><b>Not <see cref="SpatialNode.Zone"/>.</b> That label comes from relative gravity,
        /// which on a body whose atmosphere reaches twenty times its voxel radius reads "surface" out
        /// to ~220 km — so a node authored into a ring at 195 km founded a landed-looking Outpost
        /// floating in a debris disc. Station type is a geometric question and
        /// <see cref="StationSiting"/> answers it geometrically. See docs/issues/FAC-096 stage 2.</para>
        ///
        /// <para>Falls back to the old label only when no planet catalogue is wired, which is the
        /// offline/unconfigured case rather than a live one.</para></summary>
        private StationSite SiteFor(SpatialNode node)
        {
            if (node == null) return StationSite.Surface;
            if (_planets == null)
                return node.Zone == NodeZone.Surface ? StationSite.Surface
                     : node.Zone == NodeZone.Orbit   ? StationSite.Orbital
                     : StationSite.Space;
            return StationSiting.Classify(_planets, node.Position);
        }

        /// <summary>The vanilla <c>FactionNames</c> category a station's name is drawn from.</summary>
        private static string StationTypeFor(StationSite site)
        {
            if (site == StationSite.Surface) return "Outposts";
            return site == StationSite.Orbital ? "OrbitalStations" : "SpaceStations";
        }

        private static MyStationTypeEnum StationTypeEnumFor(StationSite site)
        {
            if (site == StationSite.Surface) return MyStationTypeEnum.Outpost;
            return site == StationSite.Orbital ? MyStationTypeEnum.OrbitalStation : MyStationTypeEnum.SpaceStation;
        }

        private string PickPrefab(bool isSpace, string style)
        {
            if (_prefabPool != null)
            {
                IList<string> pool = _prefabPool(isSpace, style);
                if (pool != null && pool.Count > 0)
                    return pool[_rng.Next(0, pool.Count)];

                // No pirate variant for this environment — fall back to the civilian pool, which is
                // the same family in a different livery.
                //
                // The Factorum deliberately does not fall back here. Its family carries no store,
                // no contract board and no medbay, and that is what the style is for; quietly
                // substituting a trading post would undo the decision it encodes. An empty Factorum
                // pool means its prefabs are missing from the catalogue — a fault worth seeing.
                if (string.Equals(style, StationStyle.Pirate, StringComparison.OrdinalIgnoreCase))
                {
                    pool = _prefabPool(isSpace, StationStyle.Civilian);
                    if (pool != null && pool.Count > 0)
                        return pool[_rng.Next(0, pool.Count)];
                }
            }
            // Pool not configured or empty — use hardcoded vanilla names as last resort.
            string[] fallback = isSpace
                ? new[] { "Economy_SpaceStation_10", "Economy_SpaceStation_11", "Economy_SpaceStation_12",
                          "Economy_SpaceStation_13", "Economy_SpaceStation_14", "Economy_SpaceStation_15" }
                : new[] { "Economy_Outpost_10", "Economy_Outpost_11", "Economy_Outpost_12",
                          "Economy_Outpost_13", "Economy_Outpost_14" };
            return fallback[_rng.Next(0, fallback.Length)];
        }

        internal static MyDefinitionId? ResolveContainerTypeId(MyStationTypeEnum stationTypeEnum)
        {
            try
            {
                if (GetStationTypeDefinitionM == null || GeneratedItemsContainerTypeP == null) return null;
                object def = GetStationTypeDefinitionM.Invoke(null, new object[] { stationTypeEnum });
                if (def == null) return null;
                object raw = GeneratedItemsContainerTypeP.GetValue(def);
                if (raw is MyDefinitionId) return (MyDefinitionId)raw;
            }
            catch { }
            return null;
        }

        private void PopulateStoreItemsVanilla(MyFaction faction)
        {
            if (IsCrunchLoaded()) return;
            try
            {
                if (StoreItemsGeneratorF == null) return;
                MySessionComponentEconomy econ = MySession.Static.GetComponent<MySessionComponentEconomy>();
                if (econ == null) return;
                var generator = StoreItemsGeneratorF.GetValue(econ) as MyStoreItemsGenerator;
                if (generator == null) return;
                generator.Update(faction, firstGeneration: true);
                FactionsPlugin.Log.Info("StoreItems generated for " + faction.Tag + " (vanilla path)");
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn("StationFounder.PopulateStoreItemsVanilla failed: " + ex.Message);
            }
        }

        private static bool IsCrunchLoaded()
        {
            if (_crunchLoaded.HasValue) return _crunchLoaded.Value;
            _crunchLoaded = AccessTools.TypeByName("CrunchEconV3.CrunchEconPlugin") != null;
            return _crunchLoaded.Value;
        }

        private static MyFactionStation FindStation(long stationId)
        {
            MyFaction ignored;
            return FindStation(stationId, out ignored);
        }

        /// <summary>Same lookup as <see cref="FindStation(long)"/>, additionally reporting which
        /// faction's <c>m_stations</c> dict the station was actually found under -- the caller needs
        /// this to migrate the entry away from a stale owner (see
        /// <see cref="MigrateStationRegistration"/>).</summary>
        internal static MyFactionStation FindStation(long stationId, out MyFaction owningFaction)
        {
            owningFaction = null;
            if (stationId == 0 || MySession.Static?.Factions == null) return null;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction faction = kv.Value;
                if (faction == null) continue;
                MyFactionStation station = faction.GetStationById(stationId) as MyFactionStation;
                if (station != null) { owningFaction = faction; return station; }
            }
            return null;
        }

        /// <summary>
        /// Moves a founded station's live SE-side registration from whichever faction's
        /// <c>MyFaction.m_stations</c> dict currently holds it to <paramref name="newOwnerTag"/>'s.
        /// Station *membership* in this dictionary -- not just <c>MyFactionStation.Name</c> -- is
        /// what every ownership-scoped lookup keys off (<c>faction.GetStationById</c>, used by
        /// <see cref="StationMaterializer.BeginSpawn"/>/<see cref="StationMaterializer.ForceRespawn"/>
        /// and <see cref="MigrateMismatchedStationPrefabs"/>): a Conquer/Claim that only renames the
        /// station leaves it permanently invisible to the new owner's own respawn/reclaim path, and
        /// live SE-side ownership signals (whatever reads MyFactionStation's owning MyFaction) keep
        /// showing the previous owner indefinitely. No-op if the station is already registered under
        /// the target faction, or if either faction/dict can't be resolved.
        /// </summary>
        internal static void MigrateStationRegistration(long stationId, MyFactionStation station,
            MyFaction currentOwningFaction, string newOwnerTag)
        {
            if (StationsDictField == null || station == null) return;

            MyFaction newFaction = FindFaction(newOwnerTag);
            if (newFaction == null || ReferenceEquals(newFaction, currentOwningFaction)) return;

            if (currentOwningFaction != null)
            {
                var fromDict = StationsDictField.GetValue(currentOwningFaction) as Dictionary<long, IMyFactionStation>;
                fromDict?.Remove(stationId);
            }

            var toDict = StationsDictField.GetValue(newFaction) as Dictionary<long, IMyFactionStation>;
            if (toDict != null) toDict[stationId] = station;
        }

        internal static MyFaction FindFaction(string tag)
        {
            if (MySession.Static?.Factions == null) return null;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                if (string.Equals(kv.Value.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }
}
