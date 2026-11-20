using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using NLog;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// In-memory + on-disk store for player-captured ship prefabs (`!factions ship capture`).
    /// Decoupled from the SE prefab-definition system entirely — the in-memory dictionary here is
    /// what makes a capture immediately spawnable with no session restart (consulted by
    /// <see cref="CustomPrefabSpawner"/>); disk persistence (<see cref="Register"/>'s save side,
    /// <see cref="LoadAll"/>) exists only so captures survive a Torch restart, and is otherwise
    /// decoupled from that immediate-availability path.
    /// </summary>
    public sealed partial class CustomPrefabStore
    {
        private const string ManifestFileName = "index.json";

        private sealed class ManifestEntry
        {
            [JsonProperty] public string PrefabId;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string OwnerFactionTag;
            /// <summary>The livery the design is built under (<c>SePrefabCatalogue.Entry.FactionHint</c>).
            /// Absent for the universal livery, which is every capture a player makes.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string FactionHint;
            [JsonProperty] public long CapturedByIdentityId;
            [JsonProperty] public string CapturedByFactionTag;
            [JsonProperty] public DateTime CapturedUtc;
            /// <summary>"Ship" or "Station" — which registration the boot replay runs for this
            /// capture. Absent in every manifest written before station designs existed, and every
            /// one of those is a ship, which is why <see cref="KindOf"/> reads an absent value as
            /// "Ship" rather than refusing the entry.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Kind;
            /// <summary>Where a station design was measured standing ("Surface"/"Space"). Null for a
            /// ship, and for a station capture whose site could not be classified — which no refit
            /// accepts, so the design is inert rather than mis-sited.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Site;
            /// <summary>The design this capture is a published version of; null for an original.
            /// See <see cref="Factions.Core.PrefabVersions"/>.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Family;
            /// <summary>Version number within <see cref="Family"/>, from 2; 0 for an original.</summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Version;
        }

        /// <summary>The kind a manifest entry declares, defaulting an absent value to "Ship".</summary>
        private static string KindOf(ManifestEntry entry)
        {
            return entry != null && !string.IsNullOrEmpty(entry.Kind) ? entry.Kind : "Ship";
        }

        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private readonly string _dir;
        private readonly Dictionary<string, MyObjectBuilder_CubeGrid[]> _grids =
            new Dictionary<string, MyObjectBuilder_CubeGrid[]>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ManifestEntry> _manifest = new List<ManifestEntry>();

        private CustomPrefabStore(string dir) { _dir = dir; }

        public bool TryGetGrids(string prefabId, out MyObjectBuilder_CubeGrid[] grids)
        {
            grids = null;
            return !string.IsNullOrEmpty(prefabId) && _grids.TryGetValue(prefabId, out grids);
        }

        /// <summary>Registers a freshly-captured prefab: keeps its grids in memory (immediately
        /// spawnable) and writes both the <c>.sbc</c> and a manifest entry to disk (restart-durable).
        /// Ownership on <paramref name="grids"/> doesn't matter here — <see cref="MyPrefabManager
        /// .SavePrefabToPath"/> zeroes it on write regardless, and <see cref="CustomPrefabSpawner"/>
        /// always re-stamps ownership to the dispatching faction at spawn time.</summary>
        public void Register(string prefabId, MyObjectBuilder_CubeGrid[] grids, string ownerFactionTag,
            long capturedByIdentityId, string capturedByFactionTag, string kind = null, string site = null,
            string family = null, int version = 0, string factionHint = null)
        {
            _grids[prefabId] = grids;

            var entry = new ManifestEntry
            {
                PrefabId             = prefabId,
                OwnerFactionTag      = string.IsNullOrEmpty(ownerFactionTag) ? null : ownerFactionTag,
                CapturedByIdentityId = capturedByIdentityId,
                CapturedByFactionTag = capturedByFactionTag,
                CapturedUtc          = DateTime.UtcNow,
                Kind                 = string.IsNullOrEmpty(kind) ? null : kind,
                Site                 = string.IsNullOrEmpty(site) ? null : site,
                Family               = string.IsNullOrEmpty(family) ? null : family,
                Version              = string.IsNullOrEmpty(family) ? 0 : version,
                FactionHint          = UniversalAsNull(factionHint),
            };
            _manifest.RemoveAll(e => string.Equals(e.PrefabId, prefabId, StringComparison.OrdinalIgnoreCase));
            _manifest.Add(entry);

            SaveToDisk(prefabId, grids);
        }

        private void SaveToDisk(string prefabId, MyObjectBuilder_CubeGrid[] grids)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string sbcPath = Path.Combine(_dir, prefabId + ".sbc");
                MyPrefabManager.SavePrefabToPath(prefabId, sbcPath, new List<MyObjectBuilder_CubeGrid>(grids));
                WriteManifest();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: failed to persist '" + prefabId + "' to disk (still usable this session).");
            }
        }

        private void WriteManifest()
        {
            File.WriteAllText(Path.Combine(_dir, ManifestFileName), JsonConvert.SerializeObject(_manifest, Formatting.Indented));
        }

        /// <summary>Moves a capture to <paramref name="ownerFactionTag"/> under
        /// <paramref name="factionHint"/>, and writes the manifest. False when the store does not hold
        /// it or the write failed.</summary>
        public bool SetLivery(string prefabId, string ownerFactionTag, string factionHint)
        {
            ManifestEntry entry = _manifest.Find(e => string.Equals(e.PrefabId, prefabId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            entry.OwnerFactionTag = string.IsNullOrEmpty(ownerFactionTag) ? null : ownerFactionTag;
            entry.FactionHint = UniversalAsNull(factionHint);
            try { WriteManifest(); return true; }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: failed to write the livery of '" + prefabId + "'.");
                return false;
            }
        }

        private static string UniversalAsNull(string factionHint)
        {
            return string.IsNullOrEmpty(factionHint)
                || string.Equals(factionHint, PrefabFactionHint.Universal, StringComparison.OrdinalIgnoreCase)
                ? null : factionHint;
        }

        /// <summary>Deletes a capture: its grids, its manifest entry and its <c>.sbc</c>. False when
        /// the store did not hold it. The catalogue and recipe registry are the caller's.</summary>
        public bool Remove(string prefabId)
        {
            if (string.IsNullOrEmpty(prefabId) || !_grids.Remove(prefabId)) return false;
            _manifest.RemoveAll(e => string.Equals(e.PrefabId, prefabId, StringComparison.OrdinalIgnoreCase));
            try
            {
                WriteManifest();
                string sbcPath = Path.Combine(_dir, prefabId + ".sbc");
                if (File.Exists(sbcPath)) File.Delete(sbcPath);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: failed to delete '" + prefabId + "' from disk.");
            }
            return true;
        }

        /// <summary>The faction that published a captured family: the capturer of its original,
        /// or — once the original has been deleted — the publisher of its versions, which is the
        /// same faction. Null when this store holds nothing of the family. The caller decides first
        /// whether the family is a content prefab, whose versions are per-faction lines and whose
        /// publishers differ.</summary>
        public string CapturedFamilyPublisher(string family)
        {
            if (string.IsNullOrEmpty(family)) return null;
            string fromVersion = null;
            foreach (ManifestEntry m in _manifest)
            {
                if (string.IsNullOrEmpty(m.Family) && string.Equals(m.PrefabId, family, StringComparison.OrdinalIgnoreCase))
                    return m.CapturedByFactionTag ?? "";
                if (fromVersion == null && string.Equals(m.Family, family, StringComparison.OrdinalIgnoreCase))
                    fromVersion = m.CapturedByFactionTag ?? "";
            }
            return fromVersion;
        }

        /// <summary>Loads every previously-captured prefab's raw grids back into memory from disk.
        /// Caller (<c>FactionsPlugin</c>) is responsible for re-registering each into the prefab
        /// catalogue and recipe registry via <see cref="AllEntries"/> — this class only owns the grid
        /// data and the ownership-scope manifest.</summary>
        public static CustomPrefabStore LoadAll(string dir)
        {
            var store = new CustomPrefabStore(dir);
            store.LoadDesigns();
            string manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath)) return store;

            List<ManifestEntry> entries;
            try
            {
                entries = JsonConvert.DeserializeObject<List<ManifestEntry>>(File.ReadAllText(manifestPath)) ?? new List<ManifestEntry>();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: could not read " + manifestPath + " — no custom prefabs loaded.");
                return store;
            }

            foreach (ManifestEntry entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.PrefabId)) continue;
                string sbcPath = Path.Combine(dir, entry.PrefabId + ".sbc");

                // One file can never take session load down: anything it does -- missing, malformed,
                // refused -- is a warning and a skip, and every other capture still loads.
                MyObjectBuilder_CubeGrid[] grids = null;
                try
                {
                    grids = ReadGrids(sbcPath);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "CustomPrefabStore: could not load '" + entry.PrefabId + "' from " + sbcPath + " — skipped.");
                }
                if (grids == null)
                {
                    Log.Warn("CustomPrefabStore: could not load '" + entry.PrefabId + "' from " + sbcPath + " — skipped.");
                    continue;
                }

                store._grids[entry.PrefabId] = grids;
                store._manifest.Add(entry);
            }

            Log.Info("CustomPrefabStore: loaded " + store._grids.Count + " captured prefab(s) from disk.");
            return store;
        }

        /// <summary>The saved grids of one capture, or null when the file does not hold a prefab.
        /// The bytes are read here and handed to the serializer's byte overload: the path overloads
        /// run SE's mod-path check, which refuses any directory outside the game's own, and the
        /// data directory this store lives in is one. The writer never had that check, so a capture
        /// saved fine and only its first read back at boot was refused.</summary>
        private static MyObjectBuilder_CubeGrid[] ReadGrids(string sbcPath)
        {
            if (!File.Exists(sbcPath)) return null;
            byte[] xml = File.ReadAllBytes(sbcPath);
            MyObjectBuilder_Definitions defs;
            ulong size;
            if (!MyObjectBuilderSerializer.DeserializeXML(xml, out defs, out size) || defs == null
                || defs.Prefabs == null || defs.Prefabs.Length == 0)
                return null;
            return defs.Prefabs[0].CubeGrids;
        }

        /// <summary>Every loaded/registered entry — consumed by <c>FactionsPlugin</c> to re-populate
        /// the prefab catalogue and recipe registry on session load, without either of those needing
        /// to know about this class.</summary>
        public IEnumerable<(string PrefabId, MyObjectBuilder_CubeGrid[] Grids, string OwnerFactionTag,
                            string Kind, string Site, string Family, int Version, string FactionHint)> AllEntries
        {
            get
            {
                foreach (ManifestEntry m in _manifest)
                {
                    MyObjectBuilder_CubeGrid[] grids;
                    if (_grids.TryGetValue(m.PrefabId, out grids))
                        yield return (m.PrefabId, grids, m.OwnerFactionTag, KindOf(m), m.Site, m.Family, m.Version,
                                      m.FactionHint ?? PrefabFactionHint.Universal);
                }
            }
        }

        /// <summary>True when this id names a design a player scanned, as opposed to a prefab the
        /// content walk found. The store is the only place that distinction survives — a capture
        /// scoped to "all" factions carries no owner tag, so the catalogue entry alone cannot tell
        /// the two apart.</summary>
        public bool IsCapture(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _grids.ContainsKey(prefabId);
        }
    }
}
