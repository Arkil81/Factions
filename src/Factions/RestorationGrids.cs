using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// SE grids into <see cref="RestorationBlock"/> lists: a live <see cref="MyCubeGrid"/> as it
    /// stands, and a design's <see cref="MyObjectBuilder_CubeGrid"/> as it was saved. The block
    /// identity both sides produce is the definition id's string form (type and subtype), the
    /// block's Min and its Forward/Up — the same on either side for the same block.
    ///
    /// <para>Component bills come from the block definition, summed per component subtype exactly
    /// as <see cref="SeRecipeRegistry.RegisterPrefabRecipe"/> sums a prefab's, and are shared
    /// between every block of a definition. Game thread only.</para>
    /// </summary>
    internal static class RestorationGrids
    {
        private static readonly Dictionary<MyDefinitionId, IReadOnlyDictionary<string, int>> BomByDef =
            new Dictionary<MyDefinitionId, IReadOnlyDictionary<string, int>>();

        private static readonly Dictionary<string, int> _missingScratch = new Dictionary<string, int>();

        /// <summary>A live grid, integrity and stack shortfall included.</summary>
        public static List<RestorationBlock> FromGrid(MyCubeGrid grid)
        {
            return FromGrid(grid, null);
        }

        /// <summary>As <see cref="FromGrid(MyCubeGrid)"/>, and <paramref name="slims"/> (when not
        /// null) receives each returned block's slim block at the same index.</summary>
        public static List<RestorationBlock> FromGrid(MyCubeGrid grid, List<MySlimBlock> slims)
        {
            var list = new List<RestorationBlock>();
            if (grid == null) return list;
            foreach (MySlimBlock slim in grid.CubeBlocks)
            {
                RestorationBlock b = FromSlim(slim);
                if (b == null) continue;
                list.Add(b);
                if (slims != null) slims.Add(slim);
            }
            return list;
        }

        /// <summary>One live block; null for a block with no definition.</summary>
        public static RestorationBlock FromSlim(MySlimBlock slim)
        {
            if (slim == null || slim.BlockDefinition == null) return null;
            MyCubeBlockDefinition def = slim.BlockDefinition;
            var b = new RestorationBlock
            {
                DefinitionId = def.Id.ToString(),
                Min          = new GridCell(slim.Min.X, slim.Min.Y, slim.Min.Z),
                Size         = new GridCell(def.Size.X, def.Size.Y, def.Size.Z),
                Forward      = (GridDir)(byte)slim.Orientation.Forward,
                Up           = (GridDir)(byte)slim.Orientation.Up,
                IsAnchor     = slim.FatBlock is MyShipController,
                Integrity    = slim.Integrity,
                MaxIntegrity = slim.MaxIntegrity,
                Deformed     = slim.HasDeformation,
                Components   = BomOf(def),
            };
            _missingScratch.Clear();
            slim.GetMissingComponents(_missingScratch);
            if (_missingScratch.Count > 0) b.MissingComponents = new Dictionary<string, int>(_missingScratch);
            return b;
        }

        /// <summary>The grid of <paramref name="grid"/>'s mechanical group that vanilla's Repair tab
        /// lists the group under: the one with the most blocks, the first of equals in group order.</summary>
        public static MyCubeGrid MainOf(MyCubeGrid grid)
        {
            if (grid == null) return null;
            MyCubeGrid main = null;
            foreach (MyCubeGrid g in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid))
                if (g != null && (main == null || g.CubeBlocks.Count > main.CubeBlocks.Count)) main = g;
            return main ?? grid;
        }

        /// <summary><paramref name="root"/>'s mechanical group as it stands: the root first, then the
        /// rest by entity id, each grid's blocks, and the joints between them — every base holding a
        /// top on another grid of the group. Connector and merge links are not followed.</summary>
        public static RestorationLiveGroup LiveGroup(MyCubeGrid root)
        {
            var group = new RestorationLiveGroup();
            if (root == null) return group;
            var others = new List<MyCubeGrid>();
            foreach (MyCubeGrid g in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(root))
                if (g != null && g != root && !g.Closed && !g.MarkedForClose) others.Add(g);
            others.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            group.Grids.Add(root);
            group.Grids.AddRange(others);

            var at = new Dictionary<MySlimBlock, KeyValuePair<int, int>>();
            for (int gi = 0; gi < group.Grids.Count; gi++)
            {
                var slims = new List<MySlimBlock>();
                group.Assembly.Grids.Add(FromGrid(group.Grids[gi], slims));
                group.Slims.Add(slims);
                for (int i = 0; i < slims.Count; i++) at[slims[i]] = new KeyValuePair<int, int>(gi, i);
            }
            for (int gi = 0; gi < group.Grids.Count; gi++)
            {
                foreach (MyMechanicalConnectionBlockBase b in group.Grids[gi].GetFatBlocks<MyMechanicalConnectionBlockBase>())
                {
                    if (b == null || b.TopBlock == null) continue;
                    KeyValuePair<int, int> baseAt, topAt;
                    if (!at.TryGetValue(b.SlimBlock, out baseAt) || !at.TryGetValue(b.TopBlock.SlimBlock, out topAt)) continue;
                    if (baseAt.Key == topAt.Key) continue;
                    group.Assembly.Joints.Add(new RestorationJoint
                    {
                        BaseGrid = baseAt.Key, BaseBlock = baseAt.Value, TopGrid = topAt.Key, TopBlock = topAt.Value,
                    });
                }
            }
            return group;
        }

        /// <summary>A design's grids and the joints between them: a base names its top by the top
        /// block's entity id (<c>TopBlockId</c>, or an old save's <c>RotorEntityId</c>), and a top
        /// names its base (<c>ParentEntityId</c>). Every grid is kept, a grid of no known block as an
        /// empty list, so indices stay the design's.</summary>
        public static RestorationDesign Design(MyObjectBuilder_CubeGrid[] grids)
        {
            var design = new RestorationDesign();
            if (grids == null) return design;
            var byId = new Dictionary<long, KeyValuePair<int, int>>();
            for (int gi = 0; gi < grids.Length; gi++)
            {
                int unknown;
                var sources = new List<MyObjectBuilder_CubeBlock>();
                design.Assembly.Grids.Add(FromBuilder(grids[gi], out unknown, sources));
                design.Sources.Add(sources);
                design.Unknown += unknown;
                for (int i = 0; i < sources.Count; i++)
                    if (sources[i].EntityId != 0) byId[sources[i].EntityId] = new KeyValuePair<int, int>(gi, i);
            }

            var joined = new HashSet<long>();
            for (int gi = 0; gi < design.Sources.Count; gi++)
            {
                List<MyObjectBuilder_CubeBlock> sources = design.Sources[gi];
                for (int i = 0; i < sources.Count; i++)
                {
                    var mc = sources[i] as MyObjectBuilder_MechanicalConnectionBlock;
                    if (mc == null) continue;
                    long top = mc.TopBlockId.HasValue ? mc.TopBlockId.Value : 0;
                    var motor = mc as MyObjectBuilder_MotorBase;
                    if (top == 0 && motor != null && motor.RotorEntityId.HasValue) top = motor.RotorEntityId.Value;
                    if (top == 0) continue;
                    KeyValuePair<int, int> topAt;
                    if (!byId.TryGetValue(top, out topAt) || topAt.Key == gi)
                    {
                        design.Assembly.UnresolvedJoints++;
                        continue;
                    }
                    if (!joined.Add(top)) continue;
                    design.Assembly.Joints.Add(new RestorationJoint { BaseGrid = gi, BaseBlock = i, TopGrid = topAt.Key, TopBlock = topAt.Value });
                }
            }
            for (int gi = 0; gi < design.Sources.Count; gi++)
            {
                List<MyObjectBuilder_CubeBlock> sources = design.Sources[gi];
                for (int i = 0; i < sources.Count; i++)
                {
                    var top = sources[i] as MyObjectBuilder_AttachableTopBlockBase;
                    if (top == null || top.ParentEntityId == 0 || top.EntityId == 0 || joined.Contains(top.EntityId)) continue;
                    KeyValuePair<int, int> baseAt;
                    if (!byId.TryGetValue(top.ParentEntityId, out baseAt) || baseAt.Key == gi) continue;
                    if (!(design.Sources[baseAt.Key][baseAt.Value] is MyObjectBuilder_MechanicalConnectionBlock)) continue;
                    joined.Add(top.EntityId);
                    design.Assembly.Joints.Add(new RestorationJoint { BaseGrid = baseAt.Key, BaseBlock = baseAt.Value, TopGrid = gi, TopBlock = i });
                }
            }
            return design;
        }

        /// <summary>A saved design. Blocks whose definition this world does not have are skipped and
        /// counted in <paramref name="unknown"/> — a removed mod's blocks cannot be placed back.</summary>
        public static List<RestorationBlock> FromBuilder(MyObjectBuilder_CubeGrid grid, out int unknown)
        {
            return FromBuilder(grid, out unknown, null);
        }

        /// <summary>As <see cref="FromBuilder(MyObjectBuilder_CubeGrid, out int)"/>, and
        /// <paramref name="sources"/> (when not null) receives each returned block's own object
        /// builder at the same index — what a placement clones.</summary>
        public static List<RestorationBlock> FromBuilder(MyObjectBuilder_CubeGrid grid, out int unknown,
                                                         List<MyObjectBuilder_CubeBlock> sources)
        {
            unknown = 0;
            var list = new List<RestorationBlock>();
            if (grid == null || grid.CubeBlocks == null || MyDefinitionManager.Static == null) return list;
            foreach (MyObjectBuilder_CubeBlock ob in grid.CubeBlocks)
            {
                if (ob == null) continue;
                MyCubeBlockDefinition def;
                if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(ob.GetId(), out def) || def == null)
                {
                    unknown++;
                    continue;
                }
                SerializableBlockOrientation o = ob.BlockOrientation;
                list.Add(new RestorationBlock
                {
                    DefinitionId = def.Id.ToString(),
                    Min          = new GridCell(ob.Min.X, ob.Min.Y, ob.Min.Z),
                    Size         = new GridCell(def.Size.X, def.Size.Y, def.Size.Z),
                    Forward      = (GridDir)(byte)o.Forward,
                    Up           = (GridDir)(byte)o.Up,
                    IsAnchor     = ob is MyObjectBuilder_ShipController,
                    Integrity    = def.MaxIntegrity,
                    MaxIntegrity = def.MaxIntegrity,
                    Components   = BomOf(def),
                });
                if (sources != null) sources.Add(ob);
            }
            return list;
        }

        private static IReadOnlyDictionary<string, int> BomOf(MyCubeBlockDefinition def)
        {
            IReadOnlyDictionary<string, int> bom;
            if (BomByDef.TryGetValue(def.Id, out bom)) return bom;
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (def.Components != null)
                foreach (MyCubeBlockDefinition.Component c in def.Components)
                {
                    if (c.Definition == null) continue;
                    string sub = c.Definition.Id.SubtypeName;
                    if (string.IsNullOrEmpty(sub)) continue;
                    int have;
                    d.TryGetValue(sub, out have);
                    d[sub] = have + c.Count;
                }
            BomByDef[def.Id] = d;
            return d;
        }
    }

    /// <summary>A live ship's mechanical group (<see cref="RestorationGrids.LiveGroup"/>): the grids,
    /// root first, each grid's slim blocks at the indices of its block list, and the assembly.</summary>
    internal sealed class RestorationLiveGroup
    {
        public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();
        public readonly List<List<MySlimBlock>> Slims = new List<List<MySlimBlock>>();
        public readonly RestorationAssembly Assembly = new RestorationAssembly();

        public List<RestorationBlock> Root { get { return (List<RestorationBlock>)Assembly.Grids[0]; } }

        public int BlockCount
        {
            get
            {
                int n = 0;
                foreach (IReadOnlyList<RestorationBlock> g in Assembly.Grids) n += g.Count;
                return n;
            }
        }

        public IEnumerable<RestorationBlock> AllBlocks()
        {
            foreach (IReadOnlyList<RestorationBlock> g in Assembly.Grids)
                foreach (RestorationBlock b in g) yield return b;
        }
    }

    /// <summary>A design's grids as block lists and joints (<see cref="RestorationGrids.Design"/>),
    /// with each block's own object builder at the same index — what a placement clones.</summary>
    internal sealed class RestorationDesign
    {
        public readonly RestorationAssembly Assembly = new RestorationAssembly();
        public readonly List<List<MyObjectBuilder_CubeBlock>> Sources = new List<List<MyObjectBuilder_CubeBlock>>();
        /// <summary>Blocks of a definition this world lacks, left out.</summary>
        public int Unknown;
    }
}
