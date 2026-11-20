using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>A grid-local block position. SE's <c>Vector3I</c> without the dependency.</summary>
    public struct GridCell : IEquatable<GridCell>
    {
        public readonly int X, Y, Z;

        public GridCell(int x, int y, int z) { X = x; Y = y; Z = z; }

        public static readonly GridCell Zero = new GridCell(0, 0, 0);

        public static GridCell operator +(GridCell a, GridCell b) { return new GridCell(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static GridCell operator -(GridCell a, GridCell b) { return new GridCell(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        public static GridCell Min(GridCell a, GridCell b) { return new GridCell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)); }
        public static GridCell Max(GridCell a, GridCell b) { return new GridCell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)); }
        public GridCell Abs() { return new GridCell(Math.Abs(X), Math.Abs(Y), Math.Abs(Z)); }

        public bool Equals(GridCell o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is GridCell && Equals((GridCell)obj); }
        public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
        public override string ToString()
        {
            return "(" + X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture)
                 + "," + Z.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>The six grid-local directions, in SE's <c>Base6Directions.Direction</c> order so the
    /// host converts a block orientation by casting.</summary>
    public enum GridDir : byte { Forward = 0, Backward = 1, Left = 2, Right = 3, Up = 4, Down = 5 }

    /// <summary>One of the 24 rotations that keep a grid on its lattice, stored as where it sends
    /// the three basis axes (+X to <see cref="XTo"/>, +Y to <see cref="YTo"/>, +Z to
    /// <see cref="ZTo"/>).
    ///
    /// <para>A block's orientation is itself such a rotation, built the way SE's
    /// <c>MatrixI(forward, up)</c> builds it: +X to Right (= Forward × Up), +Y to Up, +Z to
    /// Backward.</para></summary>
    public struct GridRotation : IEquatable<GridRotation>
    {
        public readonly GridDir XTo, YTo, ZTo;

        private GridRotation(GridDir x, GridDir y, GridDir z) { XTo = x; YTo = y; ZTo = z; }

        public static readonly GridRotation Identity = new GridRotation(GridDir.Right, GridDir.Up, GridDir.Backward);

        private static readonly GridCell[] Vectors =
        {
            new GridCell(0, 0, -1), new GridCell(0, 0, 1),
            new GridCell(-1, 0, 0), new GridCell(1, 0, 0),
            new GridCell(0, 1, 0),  new GridCell(0, -1, 0),
        };

        public static GridCell Vector(GridDir d) { return Vectors[(int)d]; }

        public static GridDir Flip(GridDir d) { return (GridDir)((int)d ^ 1); }

        private static bool TryDir(GridCell v, out GridDir d)
        {
            for (int i = 0; i < 6; i++)
                if (Vectors[i].Equals(v)) { d = (GridDir)i; return true; }
            d = GridDir.Forward;
            return false;
        }

        private static GridCell Cross(GridCell a, GridCell b)
        {
            return new GridCell(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        /// <summary>The rotation a block with this <paramref name="forward"/>/<paramref name="up"/>
        /// applies to its own frame. False when the two are parallel — no block carries that.</summary>
        public static bool TryFromOrientation(GridDir forward, GridDir up, out GridRotation rotation)
        {
            rotation = Identity;
            GridCell f = Vector(forward), u = Vector(up);
            GridCell r = Cross(f, u);
            GridDir right;
            if (!TryDir(r, out right)) return false;
            rotation = new GridRotation(right, up, Flip(forward));
            return true;
        }

        /// <summary>All 24, identity first, then in a fixed order — so a search over them is
        /// deterministic.</summary>
        public static readonly GridRotation[] All = BuildAll();

        private static GridRotation[] BuildAll()
        {
            var list = new List<GridRotation> { Identity };
            for (int f = 0; f < 6; f++)
                for (int u = 0; u < 6; u++)
                {
                    GridRotation r;
                    if (!TryFromOrientation((GridDir)f, (GridDir)u, out r)) continue;
                    if (!r.Equals(Identity)) list.Add(r);
                }
            return list.ToArray();
        }

        public GridCell Apply(GridCell v)
        {
            GridCell x = Vector(XTo), y = Vector(YTo), z = Vector(ZTo);
            return new GridCell(x.X * v.X + y.X * v.Y + z.X * v.Z,
                                x.Y * v.X + y.Y * v.Y + z.Y * v.Z,
                                x.Z * v.X + y.Z * v.Y + z.Z * v.Z);
        }

        public GridDir Apply(GridDir d)
        {
            GridDir result;
            TryDir(Apply(Vector(d)), out result);
            return result;
        }

        /// <summary>This rotation after <paramref name="first"/>: <c>(this ∘ first)(v) = this(first(v))</c>.</summary>
        public GridRotation After(GridRotation first)
        {
            return new GridRotation(Apply(first.XTo), Apply(first.YTo), Apply(first.ZTo));
        }

        public GridRotation Inverse()
        {
            // An orthonormal integer matrix inverts by transposition: the column of the inverse for
            // an axis is the row of this one for that axis.
            GridCell x = Vector(XTo), y = Vector(YTo), z = Vector(ZTo);
            GridDir ix, iy, iz;
            TryDir(new GridCell(x.X, y.X, z.X), out ix);
            TryDir(new GridCell(x.Y, y.Y, z.Y), out iy);
            TryDir(new GridCell(x.Z, y.Z, z.Z), out iz);
            return new GridRotation(ix, iy, iz);
        }

        public bool Equals(GridRotation o) { return XTo == o.XTo && YTo == o.YTo && ZTo == o.ZTo; }
        public override bool Equals(object obj) { return obj is GridRotation && Equals((GridRotation)obj); }
        public override int GetHashCode() { return ((int)XTo << 8) | ((int)YTo << 4) | (int)ZTo; }

        /// <summary>Where the rotation sends Forward and Up — the form a log line reads.</summary>
        public override string ToString() { return "fwd=" + Apply(GridDir.Forward) + " up=" + Apply(GridDir.Up); }
    }

    /// <summary>Reference grid coordinates to current grid coordinates: rotate, then offset.</summary>
    public struct GridTransform : IEquatable<GridTransform>
    {
        public readonly GridRotation Rotation;
        public readonly GridCell Offset;

        public GridTransform(GridRotation rotation, GridCell offset) { Rotation = rotation; Offset = offset; }

        public static readonly GridTransform Identity = new GridTransform(GridRotation.Identity, GridCell.Zero);

        public GridCell Apply(GridCell v) { return Rotation.Apply(v) + Offset; }

        public bool Equals(GridTransform o) { return Rotation.Equals(o.Rotation) && Offset.Equals(o.Offset); }
        public override bool Equals(object obj) { return obj is GridTransform && Equals((GridTransform)obj); }
        public override int GetHashCode() { return Rotation.GetHashCode() * 397 ^ Offset.GetHashCode(); }
        public override string ToString() { return Rotation + " offset=" + Offset; }
    }

    /// <summary>
    /// One block of a design or of a live grid. Identity is <see cref="DefinitionId"/> (type and
    /// subtype — SE reuses object-builder types across block families, so either alone collides)
    /// plus <see cref="Min"/> plus orientation.
    ///
    /// <para><see cref="Components"/> is the definition's full bill, set on reference blocks;
    /// <see cref="MissingComponents"/> is what a live block's stack lacks, set on current ones.
    /// Both map component subtype to count and are read, never written, by the diff, so a host may
    /// share one instance between every block of a definition.</para>
    /// </summary>
    public sealed class RestorationBlock
    {
        public string DefinitionId;
        /// <summary>The block's minimum grid cell — SE's <c>MySlimBlock.Min</c>.</summary>
        public GridCell Min;
        /// <summary>The definition's size in its own frame; (1,1,1) for a single-cell block.</summary>
        public GridCell Size = new GridCell(1, 1, 1);
        public GridDir Forward = GridDir.Forward;
        public GridDir Up = GridDir.Up;
        /// <summary>A ship controller (cockpit or remote control) — what a transform search anchors on.</summary>
        public bool IsAnchor;

        public float Integrity;
        public float MaxIntegrity;
        public bool Deformed;

        public IReadOnlyDictionary<string, int> Components;
        public IReadOnlyDictionary<string, int> MissingComponents;

        /// <summary>Below full integrity, or short of a component. Deformation alone is not.</summary>
        public bool BelowFullIntegrity
        {
            get
            {
                if (MissingComponents != null)
                    foreach (KeyValuePair<string, int> kv in MissingComponents)
                        if (kv.Value > 0) return true;
                return MaxIntegrity > 0f && Integrity < MaxIntegrity * (1f - RestorationDiff.IntegrityTolerance);
            }
        }

        /// <summary>Needs a welder: below full integrity or deformed.</summary>
        public bool NeedsWeld { get { return Deformed || BelowFullIntegrity; } }

        /// <summary>The far corner of the cells this block occupies, as SE's
        /// <c>MySlimBlock.ComputeMax</c> computes it: the size rotated into the grid and made
        /// positive.</summary>
        public GridCell Max
        {
            get
            {
                GridRotation r;
                if (!GridRotation.TryFromOrientation(Forward, Up, out r)) r = GridRotation.Identity;
                return Min + r.Apply(new GridCell(Size.X - 1, Size.Y - 1, Size.Z - 1)).Abs();
            }
        }
    }

    /// <summary>A reference block placed into the current grid's frame.</summary>
    public struct RestorationPlacement
    {
        public string DefinitionId;
        public GridCell Min;
        public GridCell Max;
        public GridDir Forward;
        public GridDir Up;
    }

    public enum RestorationStatus { Intact, Damaged, Missing, Blocked }

    /// <summary>One reference block's verdict.</summary>
    public sealed class RestorationEntry
    {
        public int ReferenceIndex;
        /// <summary>The live block matched (intact or damaged) or the first one in the way (blocked);
        /// -1 for a missing block.</summary>
        public int CurrentIndex = -1;
        public RestorationStatus Status;
        public RestorationPlacement Placement;
        /// <summary>Components this block costs to restore: the definition's full bill for a
        /// missing block, the stack's shortfall for a damaged one, empty otherwise.</summary>
        public Dictionary<string, int> Bill = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>What a grid needs to get back to its design.</summary>
    public sealed class RestorationPlan
    {
        public GridTransform Transform;
        public int ReferenceBlocks;
        public int CurrentBlocks;
        public int Intact;
        public readonly List<RestorationEntry> Damaged = new List<RestorationEntry>();
        public readonly List<RestorationEntry> Missing = new List<RestorationEntry>();
        public readonly List<RestorationEntry> Blocked = new List<RestorationEntry>();
        /// <summary>Live blocks the design does not have — the owner's additions. Never touched.</summary>
        public readonly List<int> Extra = new List<int>();
        /// <summary>Damaged blocks whose only fault is deformation: they weld for no components.</summary>
        public int DeformedOnly;
        /// <summary>Components for every missing and damaged block. Blocked blocks are not in it:
        /// they cannot be placed while the owner's block sits in their cells.</summary>
        public readonly Dictionary<string, long> Bill = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Live blocks that sit exactly on a design block (intact or damaged).</summary>
        public int Matched { get { return Intact + Damaged.Count; } }

        /// <summary>The share of the live grid the design accounts for. A transform off by a rotation
        /// reads near zero here.</summary>
        public double MatchedShareOfCurrent { get { return CurrentBlocks > 0 ? (double)Matched / CurrentBlocks : 0.0; } }

        /// <summary>The bill's lines, largest first, then by name.</summary>
        public List<KeyValuePair<string, long>> BillLines()
        {
            var lines = new List<KeyValuePair<string, long>>(Bill);
            lines.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            return lines;
        }
    }

    /// <summary>The transform a search settled on, and how well it fits.</summary>
    public struct RestorationFit
    {
        public GridTransform Transform;
        /// <summary>Live blocks that land exactly on a design block under <see cref="Transform"/>.</summary>
        public int Matched;
        public int Candidates;
        /// <summary>"identity", "anchor" or "shared" — which candidate set produced the winner.</summary>
        public string Via;
        /// <summary>More (live, design) pairs existed than <see cref="RestorationDiff.MaxAnchorPairs"/>;
        /// the pairs past the cap were not scored.</summary>
        public bool PairCapHit;
    }

    /// <summary>
    /// A live grid against its design: which design blocks are intact, damaged, missing, or cannot
    /// go back because a block the design does not have sits in their cells; which live blocks are
    /// the owner's additions; and the components the repair costs. Pure and deterministic: block
    /// lists in, plan out. See docs/ship-restoration.md.
    ///
    /// <para>The bill is in components, per block definition, the same numbers the shipyard's
    /// prefab bill sums. What those components cost in ingots is an assembler's business (and this
    /// world's assemblers divide their inputs by three); it is not applied here.</para>
    /// </summary>
    public static class RestorationDiff
    {
        /// <summary>Integrity within this fraction of the maximum reads as full. SE compares exactly;
        /// the tolerance only absorbs float noise from a serialised grid.</summary>
        public const float IntegrityTolerance = 1e-4f;

        /// <summary>The most (live anchor, design anchor) pairs a transform search scores.</summary>
        public const int MaxAnchorPairs = 64;

        private struct BlockKey : IEquatable<BlockKey>
        {
            public string Def;
            public GridCell Min;
            public GridDir Forward, Up;

            public bool Equals(BlockKey o)
            {
                return Min.Equals(o.Min) && Forward == o.Forward && Up == o.Up && string.Equals(Def, o.Def, StringComparison.Ordinal);
            }
            public override bool Equals(object obj) { return obj is BlockKey && Equals((BlockKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Min.GetHashCode();
                    h = h * 31 + (int)Forward * 6 + (int)Up;
                    return h * 31 + (Def != null ? StringComparer.Ordinal.GetHashCode(Def) : 0);
                }
            }
        }

        private static BlockKey KeyOf(RestorationBlock b)
        {
            return new BlockKey { Def = b.DefinitionId ?? "", Min = b.Min, Forward = b.Forward, Up = b.Up };
        }

        /// <summary>Where <paramref name="block"/> lands in the current grid under <paramref name="t"/>.
        /// Its footprint's corners are both mapped and re-sorted, because a rotation can swap which
        /// corner is the minimum.</summary>
        public static RestorationPlacement Place(RestorationBlock block, GridTransform t)
        {
            GridCell a = t.Apply(block.Min), b = t.Apply(block.Max);
            return new RestorationPlacement
            {
                DefinitionId = block.DefinitionId ?? "",
                Min = GridCell.Min(a, b),
                Max = GridCell.Max(a, b),
                Forward = t.Rotation.Apply(block.Forward),
                Up = t.Rotation.Apply(block.Up),
            };
        }

        private static Dictionary<BlockKey, int> IndexByKey(IReadOnlyList<RestorationBlock> blocks)
        {
            var index = new Dictionary<BlockKey, int>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] == null) continue;
                BlockKey k = KeyOf(blocks[i]);
                if (!index.ContainsKey(k)) index[k] = i;
            }
            return index;
        }

        /// <summary>Every cell a live block occupies, to the block occupying it.</summary>
        private static Dictionary<GridCell, int> Occupancy(IReadOnlyList<RestorationBlock> blocks)
        {
            var cells = new Dictionary<GridCell, int>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                RestorationBlock b = blocks[i];
                if (b == null) continue;
                GridCell lo = b.Min, hi = b.Max;
                for (int x = lo.X; x <= hi.X; x++)
                    for (int y = lo.Y; y <= hi.Y; y++)
                        for (int z = lo.Z; z <= hi.Z; z++)
                        {
                            var c = new GridCell(x, y, z);
                            if (!cells.ContainsKey(c)) cells[c] = i;
                        }
            }
            return cells;
        }

        /// <summary>The diff of <paramref name="current"/> against <paramref name="reference"/> placed
        /// by <paramref name="transform"/>. Entries keep the design's block order.</summary>
        public static RestorationPlan Diff(IReadOnlyList<RestorationBlock> reference, IReadOnlyList<RestorationBlock> current,
                                           GridTransform transform)
        {
            if (reference == null) throw new ArgumentNullException("reference");
            if (current == null) throw new ArgumentNullException("current");

            var plan = new RestorationPlan { Transform = transform, ReferenceBlocks = reference.Count, CurrentBlocks = current.Count };
            Dictionary<BlockKey, int> exact = IndexByKey(current);
            Dictionary<GridCell, int> occupied = Occupancy(current);
            var matched = new bool[current.Count];

            for (int ri = 0; ri < reference.Count; ri++)
            {
                RestorationBlock rb = reference[ri];
                if (rb == null) continue;
                RestorationPlacement p = Place(rb, transform);
                var entry = new RestorationEntry { ReferenceIndex = ri, Placement = p };

                int ci;
                var key = new BlockKey { Def = p.DefinitionId, Min = p.Min, Forward = p.Forward, Up = p.Up };
                if (exact.TryGetValue(key, out ci) && !matched[ci])
                {
                    matched[ci] = true;
                    entry.CurrentIndex = ci;
                    RestorationBlock cb = current[ci];
                    if (!cb.NeedsWeld) { plan.Intact++; continue; }
                    entry.Status = RestorationStatus.Damaged;
                    AddAll(entry.Bill, cb.MissingComponents);
                    if (entry.Bill.Count == 0 && !cb.BelowFullIntegrity) plan.DeformedOnly++;
                    plan.Damaged.Add(entry);
                    AddAll(plan.Bill, entry.Bill);
                    continue;
                }

                int blocker = FirstOccupant(occupied, p.Min, p.Max);
                if (blocker >= 0)
                {
                    entry.Status = RestorationStatus.Blocked;
                    entry.CurrentIndex = blocker;
                    plan.Blocked.Add(entry);
                    continue;
                }

                entry.Status = RestorationStatus.Missing;
                AddAll(entry.Bill, rb.Components);
                plan.Missing.Add(entry);
                AddAll(plan.Bill, entry.Bill);
            }

            for (int ci = 0; ci < current.Count; ci++)
                if (current[ci] != null && !matched[ci]) plan.Extra.Add(ci);
            return plan;
        }

        private static int FirstOccupant(Dictionary<GridCell, int> occupied, GridCell lo, GridCell hi)
        {
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                    {
                        int who;
                        if (occupied.TryGetValue(new GridCell(x, y, z), out who)) return who;
                    }
            return -1;
        }

        private static void AddAll(Dictionary<string, int> into, IReadOnlyDictionary<string, int> from)
        {
            if (from == null) return;
            foreach (KeyValuePair<string, int> kv in from)
            {
                if (kv.Value <= 0 || string.IsNullOrEmpty(kv.Key)) continue;
                int have;
                into.TryGetValue(kv.Key, out have);
                into[kv.Key] = have + kv.Value;
            }
        }

        private static void AddAll(Dictionary<string, long> into, Dictionary<string, int> from)
        {
            foreach (KeyValuePair<string, int> kv in from)
            {
                long have;
                into.TryGetValue(kv.Key, out have);
                into[kv.Key] = have + kv.Value;
            }
        }

        /// <summary>Live blocks that land exactly on a design block under <paramref name="t"/>.</summary>
        public static int Score(IReadOnlyList<RestorationBlock> reference, IReadOnlyList<RestorationBlock> current, GridTransform t)
        {
            return Score(reference, IndexByKey(current), t);
        }

        private static int Score(IReadOnlyList<RestorationBlock> reference, Dictionary<BlockKey, int> exact, GridTransform t)
        {
            int n = 0;
            for (int i = 0; i < reference.Count; i++)
            {
                RestorationBlock rb = reference[i];
                if (rb == null) continue;
                RestorationPlacement p = Place(rb, t);
                if (exact.ContainsKey(new BlockKey { Def = p.DefinitionId, Min = p.Min, Forward = p.Forward, Up = p.Up })) n++;
            }
            return n;
        }

        /// <summary>The transform that puts <paramref name="designBlock"/> exactly where
        /// <paramref name="liveBlock"/> is: the one rotation taking the design block's orientation
        /// to the live one's, and the offset that then lines up their footprints.</summary>
        public static GridTransform AlignOn(RestorationBlock designBlock, RestorationBlock liveBlock)
        {
            GridRotation from, to;
            if (!GridRotation.TryFromOrientation(designBlock.Forward, designBlock.Up, out from)) from = GridRotation.Identity;
            if (!GridRotation.TryFromOrientation(liveBlock.Forward, liveBlock.Up, out to)) to = GridRotation.Identity;
            GridRotation r = to.After(from.Inverse());
            RestorationPlacement placed = Place(designBlock, new GridTransform(r, GridCell.Zero));
            return new GridTransform(r, liveBlock.Min - placed.Min);
        }

        /// <summary>
        /// The transform under which the most live blocks land exactly on design blocks.
        ///
        /// <para>Identity is scored first — a grid spawned from its design, or registered from
        /// itself, keeps its block coordinates. Then each surviving anchor (cockpit or remote
        /// control) is aligned on each design block of the same definition; with no shared anchor,
        /// the shared definition with the fewest pairs stands in for one. Each pairing implies one
        /// transform, so the search is bounded by <see cref="MaxAnchorPairs"/>, not by 24 rotations
        /// times every offset. The first best candidate wins, so the result is deterministic.</para>
        /// </summary>
        public static RestorationFit FindTransform(IReadOnlyList<RestorationBlock> reference, IReadOnlyList<RestorationBlock> current)
        {
            if (reference == null) throw new ArgumentNullException("reference");
            if (current == null) throw new ArgumentNullException("current");

            Dictionary<BlockKey, int> exact = IndexByKey(current);
            var best = new RestorationFit { Transform = GridTransform.Identity, Via = "identity", Candidates = 1 };
            best.Matched = Score(reference, exact, GridTransform.Identity);
            if (best.Matched >= CountNonNull(current)) return best;

            string via;
            bool capped;
            List<KeyValuePair<int, int>> pairs = AnchorPairs(reference, current, out via, out capped);
            best.PairCapHit = capped;
            var tried = new HashSet<GridTransform> { GridTransform.Identity };
            for (int i = 0; i < pairs.Count; i++)
            {
                GridTransform t = AlignOn(reference[pairs[i].Value], current[pairs[i].Key]);
                if (!tried.Add(t)) continue;
                best.Candidates++;
                int s = Score(reference, exact, t);
                if (s > best.Matched) { best.Matched = s; best.Transform = t; best.Via = via; }
            }
            return best;
        }

        private static int CountNonNull(IReadOnlyList<RestorationBlock> blocks)
        {
            int n = 0;
            for (int i = 0; i < blocks.Count; i++) if (blocks[i] != null) n++;
            return n;
        }

        /// <summary>(live index, design index) pairs to align on, in a fixed order, at most
        /// <see cref="MaxAnchorPairs"/>; <paramref name="capped"/> when more existed.</summary>
        private static List<KeyValuePair<int, int>> AnchorPairs(IReadOnlyList<RestorationBlock> reference,
                                                               IReadOnlyList<RestorationBlock> current, out string via,
                                                               out bool capped)
        {
            capped = false;
            var refByDef = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < reference.Count; i++)
            {
                if (reference[i] == null) continue;
                string d = reference[i].DefinitionId ?? "";
                List<int> l;
                if (!refByDef.TryGetValue(d, out l)) refByDef[d] = l = new List<int>();
                l.Add(i);
            }

            var pairs = new List<KeyValuePair<int, int>>();
            via = "anchor";
            long possible = 0;
            for (int ci = 0; ci < current.Count; ci++)
            {
                RestorationBlock cb = current[ci];
                if (cb == null || !cb.IsAnchor) continue;
                List<int> refs;
                if (!refByDef.TryGetValue(cb.DefinitionId ?? "", out refs)) continue;
                possible += refs.Count;
                for (int k = 0; k < refs.Count && pairs.Count < MaxAnchorPairs; k++)
                    pairs.Add(new KeyValuePair<int, int>(ci, refs[k]));
            }
            if (pairs.Count > 0)
            {
                capped = possible > pairs.Count;
                return pairs;
            }

            // No anchor survived that the design has: the shared definition that implies the fewest
            // transforms. Ties go to the ordinal-first definition.
            via = "shared";
            var curByDef = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i] == null) continue;
                string d = current[i].DefinitionId ?? "";
                List<int> l;
                if (!curByDef.TryGetValue(d, out l)) curByDef[d] = l = new List<int>();
                l.Add(i);
            }
            string pick = null;
            long pickCost = long.MaxValue;
            foreach (KeyValuePair<string, List<int>> kv in curByDef)
            {
                List<int> refs;
                if (!refByDef.TryGetValue(kv.Key, out refs)) continue;
                long cost = (long)refs.Count * kv.Value.Count;
                if (cost < pickCost || (cost == pickCost && string.CompareOrdinal(kv.Key, pick) < 0))
                {
                    pick = kv.Key;
                    pickCost = cost;
                }
            }
            if (pick == null) return pairs;
            capped = pickCost > MaxAnchorPairs;
            List<int> live = curByDef[pick], design = refByDef[pick];
            for (int a = 0; a < live.Count && pairs.Count < MaxAnchorPairs; a++)
                for (int b = 0; b < design.Count && pairs.Count < MaxAnchorPairs; b++)
                    pairs.Add(new KeyValuePair<int, int>(live[a], design[b]));
            return pairs;
        }

        /// <summary>
        /// A stable name for a design's shape: FNV-1a 64 over its blocks' definition, orientation and
        /// position relative to the design's minimum corner, in a sorted order. The same blocks in any
        /// list order, or the whole design moved on its lattice, give the same fingerprint; a block
        /// added, removed, turned or swapped gives another. Integrity and components are not in it.
        /// </summary>
        public static string Fingerprint(IReadOnlyList<RestorationBlock> blocks)
        {
            if (blocks == null) return "";
            var keys = new List<BlockKey>(blocks.Count);
            bool any = false;
            GridCell corner = GridCell.Zero;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] == null) continue;
                BlockKey k = KeyOf(blocks[i]);
                corner = any ? GridCell.Min(corner, k.Min) : k.Min;
                any = true;
                keys.Add(k);
            }
            keys.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Def, b.Def);
                if (c != 0) return c;
                if (a.Min.X != b.Min.X) return a.Min.X.CompareTo(b.Min.X);
                if (a.Min.Y != b.Min.Y) return a.Min.Y.CompareTo(b.Min.Y);
                if (a.Min.Z != b.Min.Z) return a.Min.Z.CompareTo(b.Min.Z);
                if (a.Forward != b.Forward) return a.Forward.CompareTo(b.Forward);
                return a.Up.CompareTo(b.Up);
            });

            ulong h = 14695981039346656037UL;
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                BlockKey k = keys[i];
                GridCell m = k.Min - corner;
                sb.Length = 0;
                sb.Append(k.Def).Append('|').Append(m.ToString()).Append('|')
                  .Append((char)('0' + (int)k.Forward)).Append((char)('0' + (int)k.Up)).Append(';');
                string s = sb.ToString();
                for (int c = 0; c < s.Length; c++)
                {
                    h ^= s[c];
                    h *= 1099511628211UL;
                }
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>The first block exactly at <paramref name="p"/> — same definition, minimum cell and
        /// orientation — or -1.</summary>
        public static int IndexOf(IReadOnlyList<RestorationBlock> blocks, RestorationPlacement p)
        {
            if (blocks == null) return -1;
            var key = new BlockKey { Def = p.DefinitionId ?? "", Min = p.Min, Forward = p.Forward, Up = p.Up };
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i] != null && KeyOf(blocks[i]).Equals(key)) return i;
            return -1;
        }

        /// <summary>
        /// The fingerprint of a mechanical group: the first grid's alone when there is only one — so a
        /// single-grid design keeps the name <see cref="Fingerprint(IReadOnlyList{RestorationBlock})"/>
        /// gives it — else FNV-1a 64 over the first grid's fingerprint and the others' in ordinal
        /// order. A subgrid's own shape is in it; how far its rotor, hinge or piston has turned or
        /// travelled is not.
        /// </summary>
        public static string Fingerprint(IReadOnlyList<IReadOnlyList<RestorationBlock>> grids)
        {
            if (grids == null || grids.Count == 0) return Fingerprint((IReadOnlyList<RestorationBlock>)null);
            string first = Fingerprint(grids[0]);
            if (grids.Count == 1) return first;
            var rest = new List<string>(grids.Count - 1);
            for (int i = 1; i < grids.Count; i++) rest.Add(Fingerprint(grids[i]));
            rest.Sort(StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            string s = first + "|" + string.Join(",", rest.ToArray());
            for (int c = 0; c < s.Length; c++)
            {
                h ^= s[c];
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>Blocks below full integrity — what refuses a design registration. Deformation
        /// alone does not count.</summary>
        public static int CountBelowFullIntegrity(IReadOnlyList<RestorationBlock> blocks)
        {
            int n = 0;
            if (blocks == null) return 0;
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i] != null && blocks[i].BelowFullIntegrity) n++;
            return n;
        }
    }
}
