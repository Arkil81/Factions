using System;
using System.Collections.Generic;
using System.Diagnostics;
using Factions.Core;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Every SE prefab grid as a <see cref="RestorationCatalogueEntry"/> — per-definition block
    /// counts, not block lists — for pattern matching a grid nothing marks
    /// (<see cref="RestorationMatch"/>). Built on the first request that needs it, a few prefabs a
    /// frame under a time budget, and kept for the process: prefab definitions do not change while
    /// a session runs. Game thread only; an entry's <see cref="RestorationCatalogueEntry.Tag"/> is
    /// its <see cref="Handle"/>.
    /// </summary>
    internal sealed class RestorationCatalogueIndex
    {
        /// <summary>Where an entry's grid is read from when a match selects it.</summary>
        internal sealed class Handle
        {
            public MyObjectBuilder_CubeGrid Grid;
            /// <summary>Every grid of the prefab (or design) the entry is one of; <see cref="Grid"/> is
            /// one of them. Its subgrids are restored with it.</summary>
            public MyObjectBuilder_CubeGrid[] Grids;
            public int GridCount { get { return Grids != null ? Grids.Length : 1; } }
        }

        private readonly List<RestorationCatalogueEntry> _entries = new List<RestorationCatalogueEntry>();
        private readonly Dictionary<MyDefinitionId, string> _idText = new Dictionary<MyDefinitionId, string>();
        private List<string> _pending;
        private int _next;
        private long _elapsedTicks;

        public bool Ready { get; private set; }
        public int Prefabs { get; private set; }
        public int Blocks { get; private set; }
        public int Frames { get; private set; }
        public double ElapsedMs { get { return _elapsedTicks * 1000.0 / Stopwatch.Frequency; } }
        public IReadOnlyList<RestorationCatalogueEntry> Entries { get { return _entries; } }
        public int Definitions { get { return _idText.Count; } }

        /// <summary>Indexes prefabs, in ordinal id order, until <paramref name="deadlineTimestamp"/>
        /// (a <see cref="Stopwatch"/> timestamp) passes; always at least one. True once every prefab
        /// is indexed.</summary>
        public bool Step(long deadlineTimestamp)
        {
            if (Ready) return true;
            long start = Stopwatch.GetTimestamp();
            Frames++;
            if (_pending == null)
            {
                _pending = new List<string>();
                if (MyDefinitionManager.Static != null)
                    foreach (KeyValuePair<string, MyPrefabDefinition> kv in MyDefinitionManager.Static.GetPrefabDefinitions())
                        if (!string.IsNullOrEmpty(kv.Key)) _pending.Add(kv.Key);
                _pending.Sort(StringComparer.Ordinal);
            }
            do
            {
                if (_next >= _pending.Count) break;
                IndexPrefab(_pending[_next++]);
            }
            while (Stopwatch.GetTimestamp() < deadlineTimestamp);

            _elapsedTicks += Stopwatch.GetTimestamp() - start;
            if (_next >= _pending.Count)
            {
                Ready = true;
                _pending = null;
            }
            return Ready;
        }

        private void IndexPrefab(string id)
        {
            MyPrefabDefinition def;
            try { def = MyDefinitionManager.Static.GetPrefabDefinition(id); }
            catch { def = null; }
            MyObjectBuilder_CubeGrid[] grids = def != null ? def.CubeGrids : null;
            if (grids == null || grids.Length == 0) return;
            Prefabs++;
            for (int i = 0; i < grids.Length; i++)
            {
                RestorationCatalogueEntry e = EntryOf(id, i, grids);
                if (e == null) continue;
                Blocks += e.Blocks;
                _entries.Add(e);
            }
        }

        /// <summary>The entry of one grid; null for a grid with no known block. Definition ids are the
        /// type-and-subtype text <see cref="RestorationGrids.FromBuilder"/> gives a block, one string
        /// per id, and a block this world has no definition for is skipped as it skips it.</summary>
        public RestorationCatalogueEntry EntryOf(string id, int gridIndex, MyObjectBuilder_CubeGrid[] grids)
        {
            MyObjectBuilder_CubeGrid grid = grids != null && gridIndex >= 0 && gridIndex < grids.Length ? grids[gridIndex] : null;
            if (grid == null || grid.CubeBlocks == null || grid.CubeBlocks.Count == 0 || MyDefinitionManager.Static == null) return null;
            var e = new RestorationCatalogueEntry
            {
                Id = id,
                GridIndex = gridIndex,
                Tag = new Handle { Grid = grid, Grids = grids },
            };
            foreach (MyObjectBuilder_CubeBlock ob in grid.CubeBlocks)
            {
                if (ob == null) continue;
                MyDefinitionId defId = ob.GetId();
                string text;
                if (!_idText.TryGetValue(defId, out text))
                {
                    MyCubeBlockDefinition def;
                    text = MyDefinitionManager.Static.TryGetCubeBlockDefinition(defId, out def) && def != null ? def.Id.ToString() : null;
                    _idText[defId] = text;
                }
                if (text != null) e.Count(text);
            }
            return e.Blocks > 0 ? e : null;
        }
    }
}
