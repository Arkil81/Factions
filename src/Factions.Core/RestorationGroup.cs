using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>A mechanical joint: the base block (rotor stator, hinge, piston, suspension) on one
    /// grid of an assembly holding the top block (rotor head, hinge top, piston top, wheel) on
    /// another. Indices are into <see cref="RestorationAssembly.Grids"/> and that grid's block list.</summary>
    public sealed class RestorationJoint
    {
        public int BaseGrid;
        public int BaseBlock;
        public int TopGrid;
        public int TopBlock;
    }

    /// <summary>Grids joined by mechanical joints: a design (a prefab's or a registered design's
    /// grids) or a live ship's mechanical group. Connector and merge-block links are not joints.</summary>
    public sealed class RestorationAssembly
    {
        public readonly List<IReadOnlyList<RestorationBlock>> Grids = new List<IReadOnlyList<RestorationBlock>>();
        public readonly List<RestorationJoint> Joints = new List<RestorationJoint>();
        /// <summary>A design's bases that name a top no grid of the design holds; their subgrid, if
        /// any, cannot be found.</summary>
        public int UnresolvedJoints;
    }

    /// <summary>One design grid of a restore and the live grid it is restored onto.</summary>
    public sealed class RestorationGridPlan
    {
        public int DesignGrid;
        /// <summary>The live grid, an index into the live assembly; -1 for a grid built on a top part
        /// the restore attaches (its base is there, or is placed, but the top is gone).</summary>
        public int LiveGrid = -1;
        /// <summary>Design to live coordinates. Identity for an attached grid: its placements are in
        /// design coordinates until the top exists and the host can align on it.</summary>
        public GridTransform Transform;
        /// <summary>The design joint this grid was reached through; -1 for the root.</summary>
        public int Joint = -1;
        /// <summary>The grid on the joint's other side, an index into
        /// <see cref="RestorationGroupPlan.Grids"/>; -1 for the root.</summary>
        public int Parent = -1;
        public RestorationPlan Plan;
        public RestorationSequence Sequence;
        /// <summary>For an attached grid, the step that attaches its top: the design's top block, in
        /// design coordinates, billed its full components. Null otherwise.</summary>
        public RestorationStep AttachStep;

        public bool Attach { get { return LiveGrid < 0; } }
    }

    /// <summary>A design subgrid the restore cannot pair with a live grid, and why. It is left out;
    /// it is never guessed at.</summary>
    public sealed class RestorationUnpaired
    {
        public int DesignGrid;
        public int Joint;
        public string Reason;
    }

    /// <summary>What a ship's mechanical group needs to get back to its design.</summary>
    public sealed class RestorationGroupPlan
    {
        /// <summary>The root first, then each subgrid in the order the joints reached it.</summary>
        public readonly List<RestorationGridPlan> Grids = new List<RestorationGridPlan>();
        public readonly List<RestorationUnpaired> Unpaired = new List<RestorationUnpaired>();
        /// <summary>Live grids of the group no design grid pairs with: the owner's own subgrids, left alone.</summary>
        public int ExtraLiveGrids;
        public int UnresolvedJoints;
        /// <summary>Every grid's steps: all welds first, then, grid by grid, a missing top's attach
        /// followed by that grid's placements.</summary>
        public RestorationSequence Sequence;

        public RestorationGridPlan Root { get { return Grids.Count > 0 ? Grids[0] : null; } }

        /// <summary>Live blocks of the paired grids the design does not have.</summary>
        public int Extra
        {
            get
            {
                int n = 0;
                foreach (RestorationGridPlan g in Grids) n += g.Plan.Extra.Count;
                return n;
            }
        }

        /// <summary>Components for every missing, damaged and attached block.</summary>
        public long BillTotal
        {
            get
            {
                long n = 0;
                foreach (KeyValuePair<string, long> kv in Sequence.Need()) n += kv.Value;
                return n;
            }
        }

        /// <summary>Per-grid step counts, <c>design grid:welds/places[+attach]</c> joined by commas,
        /// for a log line. Only grids with steps appear, then each unpaired grid as
        /// <c>design grid:reason</c>.</summary>
        public string GridsField()
        {
            var sb = new StringBuilder();
            foreach (RestorationGridPlan g in Grids)
            {
                RestorationSequence s = g.Sequence;
                if (s.Welds == 0 && s.Places == 0 && !g.Attach) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(g.DesignGrid.ToString(CultureInfo.InvariantCulture)).Append(':')
                  .Append(s.Welds.ToString(CultureInfo.InvariantCulture)).Append('/')
                  .Append(s.Places.ToString(CultureInfo.InvariantCulture));
                if (g.Attach) sb.Append("+attach");
            }
            foreach (RestorationUnpaired u in Unpaired)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(u.DesignGrid.ToString(CultureInfo.InvariantCulture)).Append(':').Append(u.Reason);
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }
    }

    /// <summary>
    /// A ship's mechanical group against its design. The root pair — the selected grid and the
    /// design grid a mark or a match named, with the transform between them — is given; every other
    /// design grid is paired by walking the design's joints outward from it:
    /// <list type="bullet">
    ///   <item>from a paired grid holding a joint's <b>base</b>: the base's live block sits where the
    ///   design puts it, and the top it holds is the subgrid's; that grid's transform is the one that
    ///   lines the design's top block up on the live one (<see cref="RestorationDiff.AlignOn"/>), not
    ///   a second match. A base whose top is gone — or a base the restore itself places — gets an
    ///   attached top, and the subgrid is built on it from nothing but its top;</item>
    ///   <item>from a paired grid holding a joint's <b>top</b>: the live top's base, the same way.</item>
    /// </list>
    /// A live top of another definition than the design's, a missing base the restore cannot place,
    /// and a base whose grid is gone leave that subgrid, and everything beyond it, unpaired and
    /// reported. Pure and deterministic.
    /// </summary>
    public static class RestorationGroup
    {
        public const string TopDiffers = "top-differs";
        public const string BaseNotRestorable = "base-not-restorable";
        public const string BaseGone = "base-gone";
        public const string LiveGridTaken = "live-grid-taken";
        public const string ParentUnpaired = "parent-unpaired";

        public static RestorationGroupPlan Plan(RestorationAssembly design, int designRoot, RestorationAssembly live, int liveRoot,
                                                GridTransform rootTransform)
        {
            if (design == null) throw new ArgumentNullException("design");
            if (live == null) throw new ArgumentNullException("live");
            if (designRoot < 0 || designRoot >= design.Grids.Count) throw new ArgumentOutOfRangeException("designRoot");
            if (liveRoot < 0 || liveRoot >= live.Grids.Count) throw new ArgumentOutOfRangeException("liveRoot");

            var result = new RestorationGroupPlan { UnresolvedJoints = design.UnresolvedJoints };
            var designDone = new bool[design.Grids.Count];
            var liveTaken = new bool[live.Grids.Count];

            Add(result, design, live, new RestorationGridPlan { DesignGrid = designRoot, LiveGrid = liveRoot, Transform = rootTransform });
            designDone[designRoot] = true;
            liveTaken[liveRoot] = true;

            for (int gi = 0; gi < result.Grids.Count; gi++)
            {
                RestorationGridPlan p = result.Grids[gi];
                for (int j = 0; j < design.Joints.Count; j++)
                {
                    RestorationJoint dj = design.Joints[j];
                    bool holdsBase = dj.BaseGrid == p.DesignGrid;
                    bool holdsTop = dj.TopGrid == p.DesignGrid;
                    if (holdsBase == holdsTop) continue;
                    int q = holdsBase ? dj.TopGrid : dj.BaseGrid;
                    if (designDone[q]) continue;
                    designDone[q] = true;

                    string reason;
                    RestorationGridPlan child = holdsBase
                        ? FromBase(design, live, p, gi, j, liveTaken, out reason)
                        : FromTop(design, live, p, gi, j, liveTaken, out reason);
                    if (child == null)
                    {
                        result.Unpaired.Add(new RestorationUnpaired { DesignGrid = q, Joint = j, Reason = reason });
                        continue;
                    }
                    if (child.LiveGrid >= 0) liveTaken[child.LiveGrid] = true;
                    Add(result, design, live, child);
                }
            }

            ReportBeyondUnpaired(result, design, designDone);
            for (int i = 0; i < liveTaken.Length; i++) if (!liveTaken[i]) result.ExtraLiveGrids++;
            result.Sequence = Combine(result);
            return result;
        }

        private static RestorationGridPlan FromBase(RestorationAssembly design, RestorationAssembly live, RestorationGridPlan p,
                                                    int parentIndex, int j, bool[] liveTaken, out string reason)
        {
            reason = null;
            RestorationJoint dj = design.Joints[j];
            RestorationBlock designBase = design.Grids[dj.BaseGrid][dj.BaseBlock];
            RestorationBlock designTop = design.Grids[dj.TopGrid][dj.TopBlock];

            if (!p.Attach)
            {
                IReadOnlyList<RestorationBlock> liveBlocks = live.Grids[p.LiveGrid];
                int liveBase = RestorationDiff.IndexOf(liveBlocks, RestorationDiff.Place(designBase, p.Transform));
                if (liveBase >= 0)
                {
                    RestorationJoint lj = LiveJointAtBase(live, p.LiveGrid, liveBase);
                    if (lj == null) return AttachPlan(dj, j, parentIndex);
                    RestorationBlock liveTop = live.Grids[lj.TopGrid][lj.TopBlock];
                    if (!string.Equals(liveTop.DefinitionId, designTop.DefinitionId, StringComparison.Ordinal))
                    {
                        reason = TopDiffers;
                        return null;
                    }
                    if (liveTaken[lj.TopGrid])
                    {
                        reason = LiveGridTaken;
                        return null;
                    }
                    return new RestorationGridPlan
                    {
                        DesignGrid = dj.TopGrid,
                        LiveGrid = lj.TopGrid,
                        Transform = RestorationDiff.AlignOn(designTop, liveTop),
                        Joint = j,
                        Parent = parentIndex,
                    };
                }
            }

            // The base is not there: the restore places it, then attaches its top.
            if (Places(p.Sequence, dj.BaseBlock)) return AttachPlan(dj, j, parentIndex);
            reason = BaseNotRestorable;
            return null;
        }

        private static RestorationGridPlan FromTop(RestorationAssembly design, RestorationAssembly live, RestorationGridPlan p,
                                                   int parentIndex, int j, bool[] liveTaken, out string reason)
        {
            reason = BaseGone;
            if (p.Attach) return null;
            RestorationJoint dj = design.Joints[j];
            RestorationBlock designTop = design.Grids[dj.TopGrid][dj.TopBlock];
            RestorationBlock designBase = design.Grids[dj.BaseGrid][dj.BaseBlock];
            int liveTop = RestorationDiff.IndexOf(live.Grids[p.LiveGrid], RestorationDiff.Place(designTop, p.Transform));
            if (liveTop < 0) return null;
            RestorationJoint lj = null;
            foreach (RestorationJoint k in live.Joints)
                if (k.TopGrid == p.LiveGrid && k.TopBlock == liveTop) { lj = k; break; }
            if (lj == null) return null;
            RestorationBlock liveBase = live.Grids[lj.BaseGrid][lj.BaseBlock];
            if (!string.Equals(liveBase.DefinitionId, designBase.DefinitionId, StringComparison.Ordinal))
            {
                reason = TopDiffers;
                return null;
            }
            if (liveTaken[lj.BaseGrid])
            {
                reason = LiveGridTaken;
                return null;
            }
            reason = null;
            return new RestorationGridPlan
            {
                DesignGrid = dj.BaseGrid,
                LiveGrid = lj.BaseGrid,
                Transform = RestorationDiff.AlignOn(designBase, liveBase),
                Joint = j,
                Parent = parentIndex,
            };
        }

        private static RestorationGridPlan AttachPlan(RestorationJoint dj, int j, int parentIndex)
        {
            return new RestorationGridPlan
            {
                DesignGrid = dj.TopGrid,
                LiveGrid = -1,
                Transform = GridTransform.Identity,
                Joint = j,
                Parent = parentIndex,
            };
        }

        private static RestorationJoint LiveJointAtBase(RestorationAssembly live, int grid, int block)
        {
            foreach (RestorationJoint k in live.Joints)
                if (k.BaseGrid == grid && k.BaseBlock == block) return k;
            return null;
        }

        private static bool Places(RestorationSequence seq, int referenceIndex)
        {
            foreach (RestorationStep s in seq.Steps)
                if (s.Kind == RestorationStepKind.Place && s.ReferenceIndex == referenceIndex) return true;
            return false;
        }

        /// <summary>Diffs a paired grid, or an attached one against nothing but its top, and adds it.</summary>
        private static void Add(RestorationGroupPlan result, RestorationAssembly design, RestorationAssembly live, RestorationGridPlan g)
        {
            IReadOnlyList<RestorationBlock> reference = design.Grids[g.DesignGrid];
            IReadOnlyList<RestorationBlock> current;
            int index = result.Grids.Count;
            if (g.Attach)
            {
                RestorationJoint dj = design.Joints[g.Joint];
                RestorationBlock top = reference[dj.TopBlock];
                current = new List<RestorationBlock> { top };
                g.AttachStep = new RestorationStep
                {
                    Kind = RestorationStepKind.AttachTop,
                    Grid = index,
                    ReferenceIndex = dj.TopBlock,
                    Placement = RestorationDiff.Place(top, GridTransform.Identity),
                };
                if (top.Components != null)
                    foreach (KeyValuePair<string, int> kv in top.Components)
                        if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key)) g.AttachStep.Bill[kv.Key] = kv.Value;
            }
            else current = live.Grids[g.LiveGrid];

            g.Plan = RestorationDiff.Diff(reference, current, g.Transform);
            g.Sequence = RestorationOrder.Build(g.Plan, current);
            foreach (RestorationStep s in g.Sequence.Steps) s.Grid = index;
            result.Grids.Add(g);
        }

        /// <summary>Design grids beyond an unpaired one, reachable from the root only through it.</summary>
        private static void ReportBeyondUnpaired(RestorationGroupPlan result, RestorationAssembly design, bool[] designDone)
        {
            var frontier = new Queue<int>();
            foreach (RestorationUnpaired u in result.Unpaired) frontier.Enqueue(u.DesignGrid);
            while (frontier.Count > 0)
            {
                int g = frontier.Dequeue();
                for (int j = 0; j < design.Joints.Count; j++)
                {
                    RestorationJoint dj = design.Joints[j];
                    int q = dj.BaseGrid == g ? dj.TopGrid : dj.TopGrid == g ? dj.BaseGrid : -1;
                    if (q < 0 || designDone[q]) continue;
                    designDone[q] = true;
                    result.Unpaired.Add(new RestorationUnpaired { DesignGrid = q, Joint = j, Reason = ParentUnpaired });
                    frontier.Enqueue(q);
                }
            }
        }

        private static RestorationSequence Combine(RestorationGroupPlan result)
        {
            var seq = new RestorationSequence();
            foreach (RestorationGridPlan g in result.Grids)
            {
                foreach (RestorationStep s in g.Sequence.Steps)
                    if (s.Kind == RestorationStepKind.Weld) seq.Steps.Add(s);
                seq.Welds += g.Sequence.Welds;
                seq.Places += g.Sequence.Places;
                seq.Blocked += g.Sequence.Blocked;
                seq.Unreachable += g.Sequence.Unreachable;
            }
            foreach (RestorationGridPlan g in result.Grids)
            {
                if (g.AttachStep != null)
                {
                    seq.Steps.Add(g.AttachStep);
                    seq.Attaches++;
                }
                foreach (RestorationStep s in g.Sequence.Steps)
                    if (s.Kind == RestorationStepKind.Place) seq.Steps.Add(s);
            }
            return seq;
        }
    }
}
