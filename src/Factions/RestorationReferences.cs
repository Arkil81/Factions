using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;

namespace Factions
{
    /// <summary>The prefab a Factions spawn produced a grid from, and how that was found; null when
    /// nothing records one.</summary>
    internal delegate string SpawnedPrefabLookup(long gridEntityId, out string via);

    /// <summary>
    /// What a live grid is restored against, and the pattern match for one nothing marks. Built by
    /// the runtime on a standalone server and the Nexus authority, and by a Nexus agent over the
    /// data directory's captures and designs, which it reads and never writes: a design an agent's
    /// player registers goes to the authority (<see cref="AgentCaptures"/>).
    /// </summary>
    internal sealed partial class RestorationReferences
    {
        private readonly Func<CustomPrefabStore> _store;
        private readonly SpawnedPrefabLookup _spawnedPrefabOf;
        private readonly FactionTuning _tuning;

        public RestorationReferences(Func<CustomPrefabStore> store, SpawnedPrefabLookup spawnedPrefabOf, FactionTuning tuning)
        {
            _store = store;
            _spawnedPrefabOf = spawnedPrefabOf;
            _tuning = tuning;
        }

        private CustomPrefabStore _customPrefabStore { get { return _store != null ? _store() : null; } }

        /// <summary>Advances a pending match by one slice; call every frame.</summary>
        public void Pump() { PumpRestoreMatch(); }
    }

    public sealed partial class FactionRuntime
    {
        private RestorationReferences _restorationRefs;

        /// <summary>The runtime's restoration references, built on first use.</summary>
        internal RestorationReferences RestorationRefs
        {
            get
            {
                if (_restorationRefs == null)
                    _restorationRefs = new RestorationReferences(() => _customPrefabStore, SpawnedPrefabOf, _tuning);
                return _restorationRefs;
            }
        }

        internal bool TryRestorationTarget(Sandbox.Game.Entities.MyCubeGrid grid, string explicitPrefab, List<RestorationBlock> current,
                                           out RestorationTarget target, out bool unmarked, out string error)
        {
            return RestorationRefs.TryRestorationTarget(grid, explicitPrefab, current, out target, out unmarked, out error);
        }

        internal bool StartRestoreMatchFor(Sandbox.Game.Entities.MyCubeGrid grid, Action<RestorationTarget, string> done, out string refusal)
        {
            return RestorationRefs.StartRestoreMatchFor(grid, done, out refusal);
        }

        internal bool CheckRegistrable(Sandbox.Game.Entities.MyCubeGrid grid, bool force, out RestorationLiveGroup live, out string refusal)
        {
            return RestorationRefs.CheckRegistrable(grid, force, out live, out refusal);
        }

        internal bool CommitRegistration(RestorationLiveGroup live, long identityId, long fee, out string fingerprint)
        {
            return RestorationRefs.CommitRegistration(live, identityId, fee, out fingerprint);
        }

        internal string RegisteredFingerprint(long gridEntityId)
        {
            return RestorationRefs.RegisteredFingerprint(gridEntityId);
        }

        internal string BenchRestorePlan(string target, string prefabArg)
        {
            return RestorationRefs.BenchRestorePlan(target, prefabArg);
        }

        internal string BenchRegisterDesign(string target, bool force)
        {
            return RestorationRefs.BenchRegisterDesign(target, force);
        }

        /// <summary>The prefab a Factions spawn produced <paramref name="eid"/> from: this process's
        /// spawner record, else the grid's mark, whose fleet key names the fleet and — for a squadron
        /// hull — its slot (<c>key#i</c>). Null for a grid this plugin did not spawn.</summary>
        internal string SpawnedPrefabOf(long eid, out string via)
        {
            via = null;
            var vanilla = _spawner as VanillaFleetSpawner;
            string prefab = vanilla != null ? vanilla.GridPrefabId(eid) : null;
            if (!string.IsNullOrEmpty(prefab)) { via = "spawn"; return prefab; }

            SimGridRecord record;
            if (!SimGridMarks.TryGet(eid, out record) || record == null || string.IsNullOrEmpty(record.FleetKey)) return null;
            string recordKey = record.FleetKey;
            foreach (FactionState st in _states)
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    string key = FleetKey(fleet);
                    if (!FleetKeyCovers(key, recordKey)) continue;
                    via = "fleet";
                    int hash = recordKey.Length > key.Length ? key.Length : -1;
                    int slot;
                    if (hash >= 0 && int.TryParse(recordKey.Substring(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out slot))
                    {
                        IReadOnlyList<string> hulls = SpawnHulls(fleet);
                        if (hulls != null && slot < hulls.Count && !string.IsNullOrEmpty(hulls[slot])) return hulls[slot];
                    }
                    return string.IsNullOrEmpty(fleet.ShipPrefabId) ? null : fleet.ShipPrefabId;
                }
            return null;
        }

    }
}
