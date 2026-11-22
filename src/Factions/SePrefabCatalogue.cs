using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>
    /// Full prefab metadata catalogue for the Factions plugin.
    ///
    /// <para>Populated by <see cref="PrefabCatalogueScanner.Scan"/> at session load, which walks
    /// <c>Content/Data/Prefabs/{Cargoships,Drones,Economy,…}</c>, computes per-prefab metadata
    /// from SE block definitions, and writes a human-readable <c>prefab_catalogue.json</c> to the
    /// Factions storage directory for inspection.</para>
    ///
    /// <para>Trade/Ferry/Gift/Raid/Patrol/Guard/Mine roles are capability-derived from each prefab's
    /// own scanned stats (cargo/weapon/drill), not the source folder — see
    /// <see cref="PrefabCatalogueScanner"/>'s class doc for the full breakdown, including the two
    /// roles that do stay folder-fixed (encounter categories, Store). Faction hints come from
    /// <c>MyFactionTypeDefinition.GridsForSale</c> or prefab-name keyword matching.</para>
    /// </summary>
    public sealed class SePrefabCatalogue : IPrefabCatalogue, IOwnedPrefabCatalogue, IStationPrefabCatalogue,
                                            IFuelPrefabCatalogue,
                                            IHullFramePrefabCatalogue,
                                            IMunitionPrefabCatalogue,
                                            IPrefabVersionCatalogue
    {
        // ---- serialisable entry -----------------------------------------------------------------

        public sealed class Entry
        {
            [JsonProperty] public long Cost;
            [JsonProperty] public string FactionHint = "All";
            [JsonProperty] public List<string> Roles;
            /// <summary>One band per distinct weapon range present on the prefab (Dps summed across
            /// blocks sharing that range) — see <see cref="WeaponStatsHelper.GroupIntoBands"/>.
            /// Empty for unarmed prefabs.</summary>
            [JsonProperty] public List<WeaponBand> WeaponBands = new List<WeaponBand>();
            /// <summary>What the prefab's weapons fire: one slot per (magazine, range) — see
            /// <see cref="WeaponStatsHelper.TryReadMunitionSlot"/>. Null, and absent from the JSON,
            /// for an unarmed prefab.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<MunitionSlot> MunitionSlots;
            [JsonProperty] public long CargoLiters;
            /// <summary>Gas-tank capacity in litres, keyed by gas inventory key ("Gas:Hydrogen").
            /// Null — and so absent from the JSON — for a prefab with no gas tanks, which is most
            /// of them.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, long> GasTankLiters;
            /// <summary>"Small", "Large", or "Station" — derived from grid IsStatic + GridSizeEnum.</summary>
            [JsonProperty] public string GridSize = "Large";
            /// <summary>Sparse, keyed by mounted thruster facing (see <see cref="ThrustDirection"/>) —
            /// only directions with at least one mounted thruster get a key. Empty until scanned by
            /// <c>PrefabCatalogueScanner</c>/<c>ThrustStatsHelper</c>.</summary>
            [JsonProperty] public Dictionary<ThrustDirection, List<ThrustBand>> ThrustBands
                = new Dictionary<ThrustDirection, List<ThrustBand>>();
            /// <summary>Dry hull mass (kg, no cargo) — 0 means "never scanned," not "massless."</summary>
            [JsonProperty] public double DryMassKg;
            /// <summary>The attitude this hull is built to fly (<see cref="Factions.Core.HullFrame"/>):
            /// the nose its author gave it and the side that ends up on top. The lift gate reads the
            /// thrusters pushing toward <see cref="HullUp"/>, not the Down bucket.</summary>
            [JsonProperty] public ThrustDirection HullNose = ThrustDirection.Forward;
            [JsonProperty] public ThrustDirection HullUp = ThrustDirection.Up;
            [JsonProperty] public HullNoseSource HullNoseSource;
            /// <summary>True once the scanner has resolved the two above, so their defaults read as
            /// "belly-down hull" only when they were actually measured.</summary>
            [JsonProperty] public bool HasHullFrame;
            /// <summary>Relative mining-throughput score, summed across ship-drill blocks —
            /// see <see cref="Factions.Core.IPrefabCatalogue.DrillYieldScore"/>. 0 = no drills.</summary>
            [JsonProperty] public double DrillYieldScore;
            /// <summary>Set only for player-captured prefabs (`!factions ship capture`): the faction
            /// tag this prefab is restricted to, or null for a vanilla-scanned prefab / a capture
            /// scoped to "all" factions.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string OwnerFactionTag;
            /// <summary>The prefab's block multiset as <see cref="Factions.Core.PrefabFingerprint"/>
            /// states it — what a live hull is matched against when a player sends one to a node at
            /// the Services Terminal. Computed by <c>PrefabCatalogueScanner.ScanPrefab</c>'s
            /// grid-array body, so every entry gets one wherever it came from: a folder walk, a mod
            /// definition, or a capture. Absent (and omitted from JSON) only for a prefab whose
            /// blocks all failed to resolve, and no match may then accept it —
            /// <c>PrefabFingerprint.Matches</c> refuses an empty on either side.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Fingerprint;
            /// <summary>Weapon blocks on this prefab that <c>WeaponStatsHelper</c> recognised as
            /// weapons but could not price (zero Dps) — their capability is implemented outside the
            /// definition pipeline. Null (and omitted from JSON) when every weapon block scored, which
            /// is the overwhelmingly common case; an unarmed hull is null here, not zero.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? UnscoredWeaponBlocks;
            /// <summary>How many cube blocks the design resolved to across every grid, counted by
            /// <c>PrefabCatalogueScanner.ScanPrefab</c>. 0 for an entry written before the field
            /// existed, which reads as "not counted" rather than "empty grid".</summary>
            [JsonProperty] public int BlockCount;
            /// <summary>Largest-hull crew evidence. Null for catalogues written before the survey;
            /// no caller may interpret that absence as an automated drone.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public CrewLayout CrewLayout;
            /// <summary>What this entry is for: <c>"Ship"</c> (a fleet hull) or <c>"Station"</c> (a
            /// design a founded node can be rebuilt as). Every entry written before station designs
            /// existed loads as "Ship", which is what every one of them is.</summary>
            [JsonProperty] public string Kind = "Ship";
            /// <summary>Where a station design was measured standing — <c>"Surface"</c> or
            /// <c>"Space"</c>; see <see cref="Factions.Core.StationPrefabSite"/>. Null, and omitted
            /// from the JSON, for a ship hull.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Site;
            /// <summary>The design this entry is a published version of (see
            /// <see cref="Factions.Core.PrefabVersions"/>). Null, and omitted from the JSON, for an
            /// original.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Family;
            /// <summary>Which version of <see cref="Family"/> this is, from 2. 0 for an original,
            /// and omitted from the JSON.</summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Version;

            // ---- fuel manifest (docs/fuel-plan.md) ---------------------------------------------
            // The capacity side of the plan, beside ThrustBands' consumption side. Gas-tank litres
            // are GasTankLiters above; these are the electric store and the reactor's.

            /// <summary>Inventory key of the ingot this hull's reactors burn (<c>"Ingot:Uranium"</c>,
            /// or a modded reactor's fuel). Null — and absent from the JSON — for a hull with no
            /// reactor, which is most of them.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReactorFuelKey;
            /// <summary>Kilograms of <see cref="ReactorFuelKey"/> the hull's reactors hold together
            /// — see <c>PrefabCatalogueScanner.AccumulateReactorFuel</c> for the volume-to-mass
            /// conversion and for what a hull with two reactor fuels counts.</summary>
            [JsonProperty] public double ReactorFuelCapacityKg;
            /// <summary>Summed <c>MyBatteryBlockDefinition.MaxStoredPower</c>, MWh.</summary>
            [JsonProperty] public double BatteryMwh;
            /// <summary>True once the scan has resolved at least one block of this design, so the
            /// zeros above read as "no reactor and no battery" rather than "the fuel pass never
            /// ran". False for an entry written before the manifest existed —
            /// <see cref="Factions.Core.FuelManifest.HasFuelData"/> is what a range gate consults
            /// before refusing anything.</summary>
            [JsonProperty] public bool HasFuelData;
        }

        // ---- fields -----------------------------------------------------------------------------

        private static readonly IReadOnlyList<string> EmptyList = new List<string>().AsReadOnly();

        private readonly Dictionary<string, Entry> _entries;

        /// <summary>Family id → the ids of its published versions, the original excluded. Derived
        /// from the entries' <see cref="Entry.Family"/>, and kept in step by every registration
        /// and removal.</summary>
        private readonly Dictionary<string, List<string>> _versions =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _entries.Count; } }

        /// <summary>Every prefab id the catalogue holds. For callers selecting on a scanned property
        /// — a livery, a grid size — rather than looking one up by name.</summary>
        public IEnumerable<string> Ids { get { return _entries.Keys; } }

        // ---- construction -----------------------------------------------------------------------

        public SePrefabCatalogue(Dictionary<string, Entry> entries)
        {
            _entries = entries ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Entry> kv in _entries) IndexVersion(kv.Key, kv.Value);
        }

        /// <summary>Empty catalogue — used when the scanner cannot run (no SE session).</summary>
        public static readonly SePrefabCatalogue Empty =
            new SePrefabCatalogue(new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));

        // ---- runtime registration (player-captured prefabs, `!factions ship capture`) -----------

        /// <summary>Adds or replaces a prefab entry at runtime — unlike the vanilla scan (which only
        /// ever runs once, at session load), this makes a captured prefab immediately queryable by
        /// every existing lookup (<see cref="GetNamesForRole"/>, <see cref="Cost"/>, etc.) with no
        /// session restart. <paramref name="ownerFactionTag"/> is null/empty for a capture scoped
        /// to "all" factions.</summary>
        public void RegisterCustomPrefab(string prefabId, Entry entry, string ownerFactionTag)
        {
            entry.OwnerFactionTag = string.IsNullOrEmpty(ownerFactionTag) ? null : ownerFactionTag;
            Entry previous;
            if (_entries.TryGetValue(prefabId, out previous)) UnindexVersion(prefabId, previous);
            _entries[prefabId] = entry;
            IndexVersion(prefabId, entry);
            // A capture made mid-session is judged now; at boot the ceiling is not yet applied here
            // and ApplyPerformanceCeiling judges every entry after the last registration.
            JudgeAgainstCeiling(prefabId, entry);
        }

        /// <summary>Moves a held entry to <paramref name="ownerFactionTag"/> (null for every faction)
        /// under <paramref name="factionHint"/>. False when the catalogue does not hold it.</summary>
        public bool SetLivery(string prefabId, string factionHint, string ownerFactionTag)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e)) return false;
            e.FactionHint = string.IsNullOrEmpty(factionHint) ? PrefabFactionHint.Universal : factionHint;
            e.OwnerFactionTag = string.IsNullOrEmpty(ownerFactionTag) ? null : ownerFactionTag;
            return true;
        }

        /// <summary>The id as the catalogue holds it, matched without regard to case.</summary>
        public bool TryGetCanonicalId(string prefabId, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(prefabId) || !_entries.ContainsKey(prefabId)) return false;
            foreach (string key in _entries.Keys)
                if (string.Equals(key, prefabId, StringComparison.OrdinalIgnoreCase)) { canonical = key; return true; }
            return false;
        }

        /// <summary>Drops a captured design from the catalogue. False when it was not held.</summary>
        public bool RemovePrefab(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e)) return false;
            UnindexVersion(prefabId, e);
            _entries.Remove(prefabId);
            _overCeiling.Remove(prefabId);
            return true;
        }

        private void IndexVersion(string prefabId, Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Family)) return;
            List<string> ids;
            if (!_versions.TryGetValue(e.Family, out ids))
            {
                ids = new List<string>();
                _versions[e.Family] = ids;
            }
            if (!ids.Contains(prefabId, StringComparer.OrdinalIgnoreCase)) ids.Add(prefabId);
        }

        private void UnindexVersion(string prefabId, Entry e)
        {
            List<string> ids;
            if (e == null || string.IsNullOrEmpty(e.Family) || !_versions.TryGetValue(e.Family, out ids)) return;
            ids.RemoveAll(id => string.Equals(id, prefabId, StringComparison.OrdinalIgnoreCase));
            if (ids.Count == 0) _versions.Remove(e.Family);
        }

        // ---- IPrefabVersionCatalogue ------------------------------------------------------------

        public string FamilyOf(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null
                || string.IsNullOrEmpty(e.Family))
                return prefabId;
            return e.Family;
        }

        public int VersionOf(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null
                || string.IsNullOrEmpty(e.Family) || e.Version < 2)
                return 1;
            return e.Version;
        }

        public string LatestFor(string prefabId, string factionTag)
        {
            if (string.IsNullOrEmpty(prefabId)) return prefabId;
            List<string> ids;
            if (!_versions.TryGetValue(FamilyOf(prefabId), out ids)) return prefabId;
            return PrefabVersions.Latest(prefabId, factionTag, Members(prefabId, ids));
        }

        /// <summary>Every published version of <paramref name="family"/>, the original excluded, in
        /// no particular order. Empty when it has none.</summary>
        public IReadOnlyList<string> VersionsOf(string family)
        {
            List<string> ids;
            return !string.IsNullOrEmpty(family) && _versions.TryGetValue(family, out ids) ? ids.AsReadOnly() : EmptyList;
        }

        private IEnumerable<PrefabVersionMember> Members(string prefabId, List<string> ids)
        {
            foreach (string id in ids)
            {
                Entry e;
                if (!_entries.TryGetValue(id, out e) || e == null) continue;
                yield return new PrefabVersionMember { Id = id, Version = e.Version, OwnerFactionTag = e.OwnerFactionTag };
            }
            Entry self;
            if (_entries.TryGetValue(prefabId, out self) && self != null && string.IsNullOrEmpty(self.Family))
                yield return new PrefabVersionMember { Id = prefabId, Version = 1, OwnerFactionTag = self.OwnerFactionTag };
        }

        /// <summary>True if <paramref name="prefabId"/> is already known (vanilla-scanned or a
        /// previously-registered capture) — used to reject a capture name collision.</summary>
        public bool Contains(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _entries.ContainsKey(prefabId);
        }

        // ---- IOwnedPrefabCatalogue --------------------------------------------------------------

        public string OwnerFactionTag(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.OwnerFactionTag : null;
        }

        // ---- IStationPrefabCatalogue ------------------------------------------------------------

        /// <summary>True for an entry scanned as a station design. An unknown id is false, so a true
        /// answer is also proof the catalogue holds the prefab.</summary>
        public bool IsStationPrefab(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null)
                return false;
            return string.Equals(e.Kind, "Station", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The site a station design was measured at, or null for a ship hull / unknown
        /// id.</summary>
        public string Site(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null)
                return null;
            return e.Site;
        }

        // ---- IHullFramePrefabCatalogue -----------------------------------------------------------

        /// <summary>Kill switch for FAC-215's lift axis, set from
        /// <see cref="Factions.Core.FactionTuning.LiftFromHullFrame"/> at session load. False makes
        /// every hull report an unknown frame, which every lift gate reads as the belly-down hull it
        /// assumed before. Here rather than in Core because the 26 places that build a
        /// <c>RouteEnvironment</c> have no tuning in hand and the catalogue does.</summary>
        public bool LiftFromHullFrame = true;

        /// <summary>The attitude this hull flies. Never null: an unknown id, or one scanned before
        /// this existed, answers <see cref="Factions.Core.HullFrame.Unknown"/>, whose
        /// <c>Known</c> is false and whose lift bucket is <c>Down</c> — the belly-down hull every lift
        /// gate assumed before FAC-215.</summary>
        public Factions.Core.HullFrame HullFrame(string prefabId)
        {
            Entry e;
            if (!LiftFromHullFrame || string.IsNullOrEmpty(prefabId)
                || !_entries.TryGetValue(prefabId, out e) || e == null || !e.HasHullFrame)
                return Factions.Core.HullFrame.Unknown;
            return new Factions.Core.HullFrame
            {
                Nose   = e.HullNose,
                Up     = e.HullUp,
                Source = e.HullNoseSource,
                Known  = true,
            };
        }

        // ---- IMunitionPrefabCatalogue -----------------------------------------------------------

        /// <summary>The prefab's munition slots, or null for an unarmed or unknown prefab.</summary>
        public IReadOnlyList<MunitionSlot> MunitionSlots(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null) return null;
            return e.MunitionSlots;
        }

        // ---- IFuelPrefabCatalogue ---------------------------------------------------------------

        /// <summary>What this hull can hold of each fuel. Never null: an unknown id answers
        /// <see cref="Factions.Core.FuelManifest.Unknown"/>, whose
        /// <c>HasFuelData</c> is false, which every caller must read as "unknown, don't gate".
        /// A fresh instance per known prefab rather than one cached on the entry — the manifest's
        /// fields are writable, and a shared instance would let one caller's edit reach every
        /// other.</summary>
        public Factions.Core.FuelManifest FuelManifest(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null)
                return Factions.Core.FuelManifest.Unknown;
            return new Factions.Core.FuelManifest
            {
                ReactorFuelKey        = e.ReactorFuelKey,
                ReactorFuelCapacityKg = e.ReactorFuelCapacityKg,
                BatteryMwh            = e.BatteryMwh,
                HasFuelData           = e.HasFuelData,
            };
        }

        /// <summary>Blocks in the design, or 0 for an unknown id / an entry scanned before the
        /// count was recorded.</summary>
        public int BlockCount(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) && e != null
                ? e.BlockCount : 0;
        }

        public CrewLayout CrewLayout(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) && e != null
                ? e.CrewLayout : null;
        }

        /// <summary>True when this entry is a station design — the one predicate every ship-facing
        /// enumeration below excludes on, so a 1200-block static grid can never be dispatched as a
        /// hull however well its stats read.</summary>
        private static bool IsStation(Entry e)
        {
            return e != null && string.Equals(e.Kind, "Station", StringComparison.OrdinalIgnoreCase);
        }

        // ---- fingerprint lookup (Services Terminal "Send to Node") ------------------------------

        /// <summary>The stored block fingerprint for one prefab, or empty when it has none.</summary>
        public string Fingerprint(string prefabId)
        {
            Entry e;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e == null) return "";
            return e.Fingerprint ?? "";
        }

        /// <summary>
        /// Every prefab whose blocks are exactly <paramref name="fingerprint"/> and that
        /// <paramref name="factionTag"/> is allowed to use — a capture scoped to another faction is
        /// not a match however identical the hull, which is the whole reason the scope is consulted
        /// here rather than after the match.
        ///
        /// <para>A linear scan: the catalogue is in the low thousands and this runs once per button
        /// press, so a second index would be state to keep correct for no measurable gain.</para>
        /// </summary>
        public List<string> IdsWithFingerprint(string fingerprint, string factionTag)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(fingerprint)) return hits;
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (e == null || IsStation(e)) continue;
                if (!Factions.Core.PrefabFingerprint.Matches(e.Fingerprint, fingerprint)) continue;
                if (!string.IsNullOrEmpty(e.OwnerFactionTag)
                    && !string.Equals(e.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits.Add(kv.Key);
            }
            return hits;
        }

        // ---- IPrefabCatalogue -------------------------------------------------------------------

        public long CargoLiters(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.CargoLiters : 0;
        }

        private static readonly IReadOnlyDictionary<string, long> EmptyGasTanks
            = new Dictionary<string, long>();

        public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) && e.GasTankLiters != null
                ? (IReadOnlyDictionary<string, long>)e.GasTankLiters : EmptyGasTanks;
        }

        public long Cost(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.Cost : 0;
        }

        public string FactionHint(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.FactionHint : "All";
        }

        private static readonly List<WeaponBand> EmptyBands = new List<WeaponBand>();

        public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) && e.WeaponBands != null
                ? e.WeaponBands : EmptyBands;
        }

        public string GridSize(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.GridSize : "Unknown";
        }

        private static readonly IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> EmptyThrustBands
            = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();

        // Unlike IReadOnlyList<T>, IReadOnlyDictionary<TKey,TValue> is NOT variant in TValue in the
        // BCL, so Dictionary<ThrustDirection,List<ThrustBand>> has no implicit conversion to this
        // method's return type — a small explicit wrap is needed. Bounded by direction count (<=6),
        // negligible per call.
        public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
        {
            Entry e;
            Dictionary<ThrustDirection, List<ThrustBand>> bands;
            if (string.IsNullOrEmpty(prefabId) || !_entries.TryGetValue(prefabId, out e) || e.ThrustBands == null)
                return EmptyThrustBands;
            bands = e.ThrustBands;

            var result = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(bands.Count);
            foreach (var kv in bands) result[kv.Key] = kv.Value;
            return result;
        }

        public double DryMassKg(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.DryMassKg : 0;
        }

        public double DrillYieldScore(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e.DrillYieldScore : 0;
        }

        /// <summary>Ship prefabs over the performance ceiling (FAC-385). Excluded from every role
        /// pool; see <see cref="ApplyPerformanceCeiling"/>.</summary>
        private readonly HashSet<string> _overCeiling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One prefab the ceiling excluded, for the boot line.</summary>
        public struct CeilingExclusion
        {
            public string PrefabId;
            public int Blocks;
            public double PredictedMs;
        }

        /// <summary>Marks every ship prefab whose predicted awake cost is over the ceiling, so
        /// <see cref="GetNamesForRole"/> never offers it. Call once, after the last prefab has been
        /// registered, so captures are judged too. Static grids are exempt — station designs, and the
        /// encounter bases catalogued as <c>Kind: Ship</c> with a static grid (<c>GridSize:
        /// Station</c>, 93 of them live) — because a static grid is never integrated and the cost
        /// model is for an awake, moving hull. Returns what was excluded, costliest first.</summary>
        public List<CeilingExclusion> ApplyPerformanceCeiling(PrefabCeiling ceiling, out int shipsJudged)
        {
            _ceiling = ceiling;
            _overCeiling.Clear();
            shipsJudged = 0;
            var excluded = new List<CeilingExclusion>();
            if (ceiling == null) return excluded;
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (e == null || IsStation(e)
                    || string.Equals(e.GridSize, "Station", StringComparison.OrdinalIgnoreCase)) continue;
                shipsJudged++;
                if (!ceiling.IsOver(kv.Key, e.BlockCount)) continue;
                _overCeiling.Add(kv.Key);
                excluded.Add(new CeilingExclusion
                {
                    PrefabId = kv.Key,
                    Blocks = e.BlockCount,
                    PredictedMs = ceiling.PredictedAwakeMs(kv.Key, e.BlockCount),
                });
            }
            excluded.Sort((a, b) => b.PredictedMs.CompareTo(a.PredictedMs));
            return excluded;
        }

        /// <summary>The ceiling applied at boot, kept so a prefab captured later in the session is
        /// judged the moment it is registered rather than at the next restart.</summary>
        private PrefabCeiling _ceiling;

        private void JudgeAgainstCeiling(string prefabId, Entry e)
        {
            if (_ceiling == null || e == null || IsStation(e)
                || string.Equals(e.GridSize, "Station", StringComparison.OrdinalIgnoreCase)) return;
            if (_ceiling.IsOver(prefabId, e.BlockCount)) _overCeiling.Add(prefabId);
            else _overCeiling.Remove(prefabId);
        }

        /// <summary>One prefab whose side of the ceiling changed on a re-judge.</summary>
        public struct CeilingChange
        {
            public string PrefabId;
            public bool NowOver;
            public int Blocks;
            public double PredictedMs;
            public double FormulaMs;
            /// <summary>The learned cost behind the verdict; null when the formula decided.</summary>
            public PrefabCostLearner.Estimate Learned;
        }

        /// <summary>Judges every ship prefab again against the ceiling applied at boot, which now
        /// reads learned costs (<see cref="PrefabCeiling.Learned"/>), and returns what crossed it
        /// either way. A no-op before <see cref="ApplyPerformanceCeiling"/>.</summary>
        public List<CeilingChange> RejudgePerformanceCeiling()
        {
            var changes = new List<CeilingChange>();
            PrefabCeiling ceiling = _ceiling;
            if (ceiling == null) return changes;
            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (e == null || IsStation(e)
                    || string.Equals(e.GridSize, "Station", StringComparison.OrdinalIgnoreCase)) continue;
                bool over = ceiling.IsOver(kv.Key, e.BlockCount);
                bool was = _overCeiling.Contains(kv.Key);
                if (over == was) continue;
                if (over) _overCeiling.Add(kv.Key); else _overCeiling.Remove(kv.Key);
                changes.Add(new CeilingChange
                {
                    PrefabId = kv.Key,
                    NowOver = over,
                    Blocks = e.BlockCount,
                    PredictedMs = ceiling.PredictedAwakeMs(kv.Key, e.BlockCount),
                    FormulaMs = ceiling.PredictedAwakeMs(e.BlockCount),
                    Learned = ceiling.LearnedFor(kv.Key),
                });
            }
            changes.Sort((a, b) => string.Compare(a.PrefabId, b.PrefabId, StringComparison.OrdinalIgnoreCase));
            return changes;
        }

        public bool IsOverPerformanceCeiling(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _overCeiling.Contains(prefabId);
        }

        /// <summary>Design families an admin has banned (FAC-451), matched case-insensitively. Excluded
        /// from every role pool the way <see cref="_overCeiling"/> is; the ids stay in the catalogue,
        /// because existing hulls of a banned design still need their BOM, stats and family.</summary>
        private readonly HashSet<string> _bannedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Replaces the ban list. Takes effect on the next lookup.</summary>
        public void SetBannedFamilies(IEnumerable<string> families)
        {
            _bannedFamilies.Clear();
            if (families == null) return;
            foreach (string f in families)
                if (!string.IsNullOrEmpty(f)) _bannedFamilies.Add(f.Trim());
        }

        /// <summary>True when <paramref name="prefabId"/>, or the design family it is a version of, is
        /// banned.</summary>
        public bool IsBanned(string prefabId)
        {
            if (string.IsNullOrEmpty(prefabId) || _bannedFamilies.Count == 0) return false;
            return _bannedFamilies.Contains(prefabId) || _bannedFamilies.Contains(FamilyOf(prefabId) ?? prefabId);
        }

        public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
        {
            if (string.IsNullOrEmpty(role)) return EmptyList;

            // First pass: faction-specific matches.
            var specific = new List<string>();
            var universal = new List<string>();

            foreach (KeyValuePair<string, Entry> kv in _entries)
            {
                Entry e = kv.Value;
                if (e == null || e.Roles == null) continue;
                // A station design carries no fleet role of its own, but a folder-fixed encounter
                // token would still surface it here. Excluded by kind rather than by role so the
                // rule holds for whatever the entry happens to be tagged with.
                if (IsStation(e)) continue;
                if (_overCeiling.Contains(kv.Key)) continue;
                if (IsBanned(kv.Key)) continue;

                bool hasRole = false;
                foreach (string r in e.Roles)
                {
                    if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) { hasRole = true; break; }
                }
                if (!hasRole) continue;

                bool isAll = string.IsNullOrEmpty(e.FactionHint)
                          || string.Equals(e.FactionHint, "All", StringComparison.OrdinalIgnoreCase);
                bool matches = !string.IsNullOrEmpty(factionHint)
                            && string.Equals(e.FactionHint, factionHint, StringComparison.OrdinalIgnoreCase);

                if (matches)      specific.Add(kv.Key);
                else if (isAll)   universal.Add(kv.Key);
            }

            // Specific first, then universal -- the union, not an override. Order is the
            // preference: the ranker breaks cost ties by pool position, so a faction still flies
            // its own hulls wherever they compete and reaches a generic one only when that is
            // genuinely the better answer for the job. The rule itself lives in Core so it can be
            // tested without a host; see PrefabRolePool for why the override had to go.
            return PrefabRolePool.Combine(specific, universal);
        }

        // ---- JSON serialisation (for prefab_catalogue.json) ------------------------------------

        public string ToJson()
        {
            return JsonConvert.SerializeObject(_entries, Formatting.Indented);
        }

        public static SePrefabCatalogue FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return Empty;
            try
            {
                var entries = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(json,
                    new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.Default });
                if (entries == null) return Empty;
                // Ensure OrdinalIgnoreCase key comparison after deserialise.
                var normalised = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in entries) normalised[kv.Key] = kv.Value;
                return new SePrefabCatalogue(normalised);
            }
            catch
            {
                return Empty;
            }
        }
    }
}
