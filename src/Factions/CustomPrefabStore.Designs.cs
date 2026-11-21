using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// The restoration half of the capture store: what a live grid is restored against.
    ///
    /// <para><b>Registered designs.</b> A snapshot of a ship's mechanical group taken while it was
    /// whole — the selected grid first, then its rotor, hinge and piston subgrids — keyed to that
    /// grid's entity id — SE keeps an entity id across saves — and carrying the design's
    /// fingerprint (<see cref="Factions.Core.RestorationDiff.Fingerprint"/>) so the record names
    /// the shape it was taken of, not only the grid. Unlike a capture it is never a prefab: it
    /// enters no catalogue, no recipe registry and no shipyard, and lives in its own directory so
    /// nothing that walks the captures meets it.</para>
    ///
    /// <para><b>Sold ships.</b> Which prefab a grid bought from a Factions store was spawned from.
    /// SE's own purchase path names nothing but the prefab (see <see cref="StoreShipSaleMarks"/>),
    /// so this is the only record that survives the grid being renamed or the server restarting.</para>
    /// </summary>
    public sealed partial class CustomPrefabStore
    {
        private const string DesignDirName = "designs";
        private const string DesignManifestFileName = "registered.json";
        private const string SoldManifestFileName = "sold-ships.json";

        /// <summary>The most sold-ship records kept; the oldest go first. A record is a few dozen
        /// bytes and a grid that has been scrapped leaves its record behind.</summary>
        internal const int MaxSoldShips = 4000;

        public sealed class DesignRecord
        {
            [JsonProperty] public long EntityId;
            [JsonProperty] public string Fingerprint;
            /// <summary>Blocks in every grid of the design.</summary>
            [JsonProperty] public int Blocks;
            /// <summary>Grids in the design when it has subgrids; 0 (omitted) for one grid.</summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Grids;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string GridName;
            [JsonProperty] public long RegisteredByIdentityId;
            [JsonProperty] public DateTime RegisteredUtc;
        }

        public sealed class SoldShipRecord
        {
            [JsonProperty] public long EntityId;
            [JsonProperty] public string PrefabId;
            [JsonProperty] public long BuyerIdentityId;
            [JsonProperty] public DateTime SoldUtc;
        }

        private readonly Dictionary<long, DesignRecord> _designs = new Dictionary<long, DesignRecord>();
        private readonly Dictionary<long, MyObjectBuilder_CubeGrid[]> _designGrids = new Dictionary<long, MyObjectBuilder_CubeGrid[]>();
        private readonly List<SoldShipRecord> _sold = new List<SoldShipRecord>();

        private string DesignDir { get { return Path.Combine(_dir, DesignDirName); } }

        public int DesignCount { get { return _designs.Count; } }
        public int SoldShipCount { get { return _sold.Count; } }

        /// <summary>The design of entity <paramref name="entityId"/>: its grids, the registered grid
        /// first.</summary>
        public bool TryGetDesign(long entityId, out DesignRecord record, out MyObjectBuilder_CubeGrid[] grids)
        {
            grids = null;
            if (!_designs.TryGetValue(entityId, out record)) return false;
            return _designGrids.TryGetValue(entityId, out grids) && grids != null && grids.Length > 0 && grids[0] != null;
        }

        /// <summary>Every registered design's grids, by entity id ascending.</summary>
        public List<KeyValuePair<long, MyObjectBuilder_CubeGrid[]>> AllDesigns()
        {
            var list = new List<KeyValuePair<long, MyObjectBuilder_CubeGrid[]>>(_designGrids.Count);
            foreach (KeyValuePair<long, MyObjectBuilder_CubeGrid[]> kv in _designGrids)
                if (kv.Value != null && kv.Value.Length > 0 && kv.Value[0] != null && _designs.ContainsKey(kv.Key)) list.Add(kv);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        /// <summary>Keeps <paramref name="grids"/> — the registered grid first, then its subgrids — as
        /// the design of entity <paramref name="entityId"/>, replacing any earlier one, and writes it to
        /// disk. False when the write failed; the design is then usable until the next restart only.</summary>
        public bool RegisterDesign(long entityId, string fingerprint, MyObjectBuilder_CubeGrid[] grids, string gridName,
                                   long registeredByIdentityId, DateTime nowUtc)
        {
            if (!KeepDesign(entityId, fingerprint, grids, gridName, registeredByIdentityId, nowUtc)) return false;
            try
            {
                Directory.CreateDirectory(DesignDir);
                string name = DesignFileStem(entityId);
                MyPrefabManager.SavePrefabToPath(name, Path.Combine(DesignDir, name + ".sbc"),
                                                 new List<MyObjectBuilder_CubeGrid>(grids));
                WriteDesignManifest();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: failed to persist the design of grid " + entityId + " (usable until restart).");
                return false;
            }
        }

        /// <summary>As <see cref="RegisterDesign"/>, in memory only: a Nexus agent's copy of a design
        /// the authority writes, usable here until its next reload of the store brings the written one.</summary>
        public bool KeepDesign(long entityId, string fingerprint, MyObjectBuilder_CubeGrid[] grids, string gridName,
                               long registeredByIdentityId, DateTime nowUtc)
        {
            if (entityId == 0 || grids == null || grids.Length == 0 || grids[0] == null) return false;
            int blocks = 0;
            foreach (MyObjectBuilder_CubeGrid g in grids)
                if (g != null && g.CubeBlocks != null) blocks += g.CubeBlocks.Count;
            var record = new DesignRecord
            {
                EntityId               = entityId,
                Fingerprint            = fingerprint ?? "",
                Blocks                 = blocks,
                Grids                  = grids.Length > 1 ? grids.Length : 0,
                GridName               = string.IsNullOrEmpty(gridName) ? null : gridName,
                RegisteredByIdentityId = registeredByIdentityId,
                RegisteredUtc          = nowUtc,
            };
            _designs[entityId] = record;
            _designGrids[entityId] = grids;
            return true;
        }

        /// <summary>The prefab a Factions store sold as grid <paramref name="entityId"/>.</summary>
        public bool TryGetSoldShip(long entityId, out SoldShipRecord record)
        {
            for (int i = _sold.Count - 1; i >= 0; i--)
                if (_sold[i].EntityId == entityId) { record = _sold[i]; return true; }
            record = null;
            return false;
        }

        /// <summary>Every prefab a recorded sale names — a sold ship is restored against it, so it is
        /// a reference like any in a brain.</summary>
        public IEnumerable<string> SoldPrefabIds()
        {
            foreach (SoldShipRecord r in _sold) yield return r.PrefabId;
        }

        public void RecordSoldShip(long entityId, string prefabId, long buyerIdentityId, DateTime nowUtc)
        {
            if (entityId == 0 || string.IsNullOrEmpty(prefabId)) return;
            _sold.RemoveAll(r => r.EntityId == entityId);
            _sold.Add(new SoldShipRecord { EntityId = entityId, PrefabId = prefabId, BuyerIdentityId = buyerIdentityId, SoldUtc = nowUtc });
            if (_sold.Count > MaxSoldShips) _sold.RemoveRange(0, _sold.Count - MaxSoldShips);
            try
            {
                Directory.CreateDirectory(DesignDir);
                File.WriteAllText(Path.Combine(DesignDir, SoldManifestFileName), JsonConvert.SerializeObject(_sold, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CustomPrefabStore: failed to persist the sale record of grid " + entityId + ".");
            }
        }

        private static string DesignFileStem(long entityId)
        {
            return "design-" + entityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void WriteDesignManifest()
        {
            var list = new List<DesignRecord>(_designs.Values);
            list.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            File.WriteAllText(Path.Combine(DesignDir, DesignManifestFileName), JsonConvert.SerializeObject(list, Formatting.Indented));
        }

        /// <summary>Reads both manifests. A file that is missing, malformed or refused is a warning
        /// and a skip, like a capture's.</summary>
        private void LoadDesigns()
        {
            string manifest = Path.Combine(DesignDir, DesignManifestFileName);
            if (File.Exists(manifest))
            {
                try
                {
                    List<DesignRecord> records = JsonConvert.DeserializeObject<List<DesignRecord>>(File.ReadAllText(manifest))
                                                 ?? new List<DesignRecord>();
                    foreach (DesignRecord r in records)
                    {
                        if (r == null || r.EntityId == 0) continue;
                        string sbc = Path.Combine(DesignDir, DesignFileStem(r.EntityId) + ".sbc");
                        MyObjectBuilder_CubeGrid[] grids = null;
                        try { grids = ReadGrids(sbc); }
                        catch (Exception ex) { Log.Warn(ex, "CustomPrefabStore: could not load the design of grid " + r.EntityId + " — skipped."); }
                        if (grids == null || grids.Length == 0 || grids[0] == null) continue;
                        _designs[r.EntityId] = r;
                        _designGrids[r.EntityId] = grids;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "CustomPrefabStore: could not read " + manifest + " — no registered designs loaded.");
                }
            }

            string sold = Path.Combine(DesignDir, SoldManifestFileName);
            if (File.Exists(sold))
            {
                try
                {
                    List<SoldShipRecord> records = JsonConvert.DeserializeObject<List<SoldShipRecord>>(File.ReadAllText(sold));
                    if (records != null)
                        foreach (SoldShipRecord r in records)
                            if (r != null && r.EntityId != 0 && !string.IsNullOrEmpty(r.PrefabId)) _sold.Add(r);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "CustomPrefabStore: could not read " + sold + " — no sold-ship records loaded.");
                }
            }

            if (_designs.Count > 0 || _sold.Count > 0)
                Log.Info("CustomPrefabStore: loaded " + _designs.Count + " registered design(s) and "
                         + _sold.Count + " sold-ship record(s).");
        }
    }
}
