using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>Integer coordinate of one cubic cell of the rock grid. Cells are the unit of
    /// spawning, despawning and determinism: a cell's contents derive entirely from
    /// <c>(worldSeed, feature, cellCoord)</c>, so one can be built or torn down without consulting
    /// any other, and without a global object list to keep in step.</summary>
    public struct CellCoord : IEquatable<CellCoord>
    {
        public readonly int X, Y, Z;
        public CellCoord(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(CellCoord o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object o) { return o is CellCoord && Equals((CellCoord)o); }
        public override int GetHashCode() { unchecked { return ((X * 397) ^ Y) * 397 ^ Z; } }
        public override string ToString()
        {
            return X.ToString(CultureInfo.InvariantCulture) + "_"
                 + Y.ToString(CultureInfo.InvariantCulture) + "_"
                 + Z.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One physical object a feature asks for. Everything here is derived, never stored:
    /// the same inputs reproduce it exactly, which is what lets a despawned object come back
    /// identical rather than merely similar.</summary>
    public struct FeatureRockObject
    {
        /// <summary>Stable, unique voxel storage name — the handle the host finds it by later.</summary>
        public string StorageName;
        public Vec3 PositionKm;
        public double SizeM;
        /// <summary>Bare SE subtype of the material, e.g. "Ice". Resolved to a voxel material
        /// definition host-side.</summary>
        public string MaterialSubTypeId;
        /// <summary>Seed for SE's own asteroid shape generator.</summary>
        public int ShapeSeed;

        /// <summary>Authored pocket-count multiplier, or null to inherit the world's setting.
        /// See <see cref="FeatureMaterial.DepositCountCoefficient"/>.</summary>
        public double? DepositCountCoefficient;

        /// <summary>Authored pocket-size denominator, or null to inherit the world's setting.
        /// See <see cref="FeatureMaterial.DepositSizeDenominator"/>.</summary>
        public double? DepositSizeDenominator;
    }

    /// <summary>
    /// Deterministic value source. Not a general-purpose RNG: it exists so a draw depends on
    /// <b>what it is for</b> rather than on how many draws came before it.
    ///
    /// <para>Every stream is seeded from an identity — <c>(worldSeed, feature, cell, index)</c> —
    /// so object 7 of a cell is the same whether or not objects 0..6 were ever generated. A shared
    /// stream would make that false, and a lazy player-driven materialiser generates objects in
    /// whatever order players happen to fly, which is precisely when stream position stops being
    /// reproducible.</para>
    /// </summary>
    public struct DeterministicRng
    {
        private uint _state;

        public DeterministicRng(int seed)
        {
            _state = (uint)seed;
            if (_state == 0) _state = 0x9E3779B9; // xorshift is absorbing at zero
        }

        /// <summary>xorshift32 — small, fast, and identical on every runtime, which
        /// <see cref="Random"/> is explicitly not guaranteed to be.</summary>
        public uint NextUInt()
        {
            unchecked
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return _state;
            }
        }

        /// <summary>Uniform in [0,1).</summary>
        public double NextDouble()
        {
            return (NextUInt() >> 8) * (1.0 / 16777216.0); // 24 bits of mantissa
        }

        public double Range(double min, double max) { return min + (max - min) * NextDouble(); }
    }

    /// <summary>
    /// Turns an authored <see cref="WorldFeature"/> into the physical objects it asks for, one cell
    /// at a time. Pure and SE-free: the host materialises what this returns and decides nothing
    /// about what should be there.
    ///
    /// <para><b>Cells, not a global index.</b> A feature the size of Cauldron's ring asks for tens of
    /// thousands of objects, and no per-frame loop can walk that. Cells bound the work to the
    /// player's neighbourhood, let one region be torn down without touching another, and — because a
    /// cell's contents are a pure function of its coordinate — remove the need to persist any
    /// per-object record at all. Vanilla's own
    /// <c>MyProceduralAsteroidCellGenerator</c> has the same shape, deliberately.</para>
    ///
    /// <para><b>Density is honoured per band via rejection.</b> Candidates are drawn at the feature's
    /// peak density and each is then accepted with probability <c>bandDensity / peak</c>. That keeps
    /// each band's own density exact even where a cell straddles several of them, which a per-cell
    /// count could not express.</para>
    /// </summary>
    public static class FeatureRockPlanner
    {
        /// <summary>Objects a cell of <paramref name="cellSizeKm"/> holds for one feature.
        /// Empty — the common case — allocates nothing beyond the iterator.</summary>
        /// <param name="resolvedCenterKm">The feature's anchor-resolved centre, passed in so anchor
        /// lookup stays out of the per-cell path.</param>
        public static IEnumerable<FeatureRockObject> ObjectsInCell(
            WorldFeature feature, Vec3 resolvedCenterKm, CellCoord cell,
            int worldSeed, double maxDensityPer1000Km3, double cellSizeKm)
        {
            if (feature == null || cellSizeKm <= 0 || maxDensityPer1000Km3 <= 0) yield break;

            double peak = PeakDensity(feature);
            if (peak <= 0) yield break;

            double cellVolumeKm3 = cellSizeKm * cellSizeKm * cellSizeKm;
            double expected = peak * (maxDensityPer1000Km3 / 1000.0) * cellVolumeKm3;
            if (expected <= 0) yield break;

            int featureHash = WorldRecipe.StableHash(feature.Name ?? string.Empty);
            int cellSeed = Combine(Combine(worldSeed, featureHash), cell.GetHashCode());

            // Fractional carry: a cell holding 0.35 expected objects must sometimes hold one and
            // usually none. Rounding would give every cell zero and empty the whole feature.
            var counter = new DeterministicRng(Combine(cellSeed, 0x436F756E)); // "Coun"
            int count = (int)Math.Floor(expected);
            if (counter.NextDouble() < expected - count) count++;
            if (count <= 0) yield break;

            double originX = cell.X * cellSizeKm;
            double originY = cell.Y * cellSizeKm;
            double originZ = cell.Z * cellSizeKm;

            for (int i = 0; i < count; i++)
            {
                var rng = new DeterministicRng(Combine(cellSeed, i + 1));

                var posKm = new Vec3(
                    originX + rng.NextDouble() * cellSizeKm,
                    originY + rng.NextDouble() * cellSizeKm,
                    originZ + rng.NextDouble() * cellSizeKm);

                // The feature's real shape emerges by rejection, so a cell straddling the ring's
                // edge thins out on its own rather than needing the geometry clipped per cell.
                FeatureBand band;
                if (!feature.TryResolve(posKm, resolvedCenterKm, out band)) continue;

                double bandDensity = MathUtil.Clamp01(feature.DensityAt(band));
                if (bandDensity <= 0) continue;
                if (bandDensity < peak && rng.NextDouble() >= bandDensity / peak) continue;

                FeatureMaterial material = PickMaterial(feature.MaterialsAt(band), ref rng);
                if (material == null) continue; // a barren band: authored to hold nothing

                SizeRange size = material.ResolvedSizeRange();

                yield return new FeatureRockObject
                {
                    StorageName = StorageName(featureHash, cell, i, material.SubTypeId),
                    PositionKm = posKm,
                    SizeM = rng.Range(size.MinM, size.MaxM),
                    MaterialSubTypeId = material.SubTypeId,
                    ShapeSeed = Combine(cellSeed, i + 1),
                    DepositCountCoefficient = material.DepositCountCoefficient,
                    DepositSizeDenominator = material.DepositSizeDenominator,
                };
            }
        }

        /// <summary>The highest density anywhere in the feature — the rate candidates are drawn at
        /// before per-band rejection thins them back down.</summary>
        public static double PeakDensity(WorldFeature feature)
        {
            if (feature == null) return 0;
            double peak = MathUtil.Clamp01(feature.Density);
            if (feature.Bands != null)
                foreach (FeatureBand b in feature.Bands)
                {
                    if (b == null) continue;
                    double d = MathUtil.Clamp01(feature.DensityAt(b));
                    if (d > peak) peak = d;
                }
            return peak;
        }

        /// <summary>Every cell touching the ball of <paramref name="radiusKm"/> about
        /// <paramref name="centreKm"/>. Coarse — a cube shell, not a sphere — because the caller
        /// re-tests each object's real position anyway and an exact enumeration would cost more than
        /// the few extra empty cells it saves.</summary>
        public static IEnumerable<CellCoord> CellsWithin(Vec3 centreKm, double radiusKm, double cellSizeKm)
        {
            if (cellSizeKm <= 0 || radiusKm < 0) yield break;

            int minX = FloorDiv(centreKm.X - radiusKm, cellSizeKm);
            int maxX = FloorDiv(centreKm.X + radiusKm, cellSizeKm);
            int minY = FloorDiv(centreKm.Y - radiusKm, cellSizeKm);
            int maxY = FloorDiv(centreKm.Y + radiusKm, cellSizeKm);
            int minZ = FloorDiv(centreKm.Z - radiusKm, cellSizeKm);
            int maxZ = FloorDiv(centreKm.Z + radiusKm, cellSizeKm);

            for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
            for (int z = minZ; z <= maxZ; z++)
                yield return new CellCoord(x, y, z);
        }

        /// <summary>Whether a cell can hold any of this feature at all — an AABB reject that keeps
        /// the per-cell generator off the great majority of cells near a player.</summary>
        public static bool CellCouldTouch(WorldFeature feature, Vec3 resolvedCenterKm, CellCoord cell, double cellSizeKm)
        {
            if (feature == null || cellSizeKm <= 0) return false;

            double r = feature.OuterRadiusKm;
            double loX = cell.X * cellSizeKm, hiX = loX + cellSizeKm;
            double loY = cell.Y * cellSizeKm, hiY = loY + cellSizeKm;
            double loZ = cell.Z * cellSizeKm, hiZ = loZ + cellSizeKm;

            // Nearest point of the cell to the feature centre.
            double dx = resolvedCenterKm.X < loX ? loX - resolvedCenterKm.X : (resolvedCenterKm.X > hiX ? resolvedCenterKm.X - hiX : 0);
            double dy = resolvedCenterKm.Y < loY ? loY - resolvedCenterKm.Y : (resolvedCenterKm.Y > hiY ? resolvedCenterKm.Y - hiY : 0);
            double dz = resolvedCenterKm.Z < loZ ? loZ - resolvedCenterKm.Z : (resolvedCenterKm.Z > hiZ ? resolvedCenterKm.Z - hiZ : 0);

            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        /// <summary>Cell containing a position.</summary>
        public static CellCoord CellOf(Vec3 posKm, double cellSizeKm)
        {
            return new CellCoord(
                FloorDiv(posKm.X, cellSizeKm),
                FloorDiv(posKm.Y, cellSizeKm),
                FloorDiv(posKm.Z, cellSizeKm));
        }

        private static FeatureMaterial PickMaterial(List<FeatureMaterial> materials, ref DeterministicRng rng)
        {
            if (materials == null || materials.Count == 0) return null;

            double total = 0;
            for (int i = 0; i < materials.Count; i++)
            {
                FeatureMaterial m = materials[i];
                if (m != null && !string.IsNullOrEmpty(m.SubTypeId) && m.Share > 0) total += m.Share;
            }
            if (total <= 0) return null;

            double roll = rng.NextDouble() * total;
            for (int i = 0; i < materials.Count; i++)
            {
                FeatureMaterial m = materials[i];
                if (m == null || string.IsNullOrEmpty(m.SubTypeId) || m.Share <= 0) continue;
                if (roll < m.Share) return m;
                roll -= m.Share;
            }
            return null;
        }

        /// <summary>
        /// Stable and unique across features, cells and indices. The host uses it to find an object
        /// again, so it must survive a restart unchanged — hence the process-stable
        /// <see cref="WorldRecipe.StableHash"/> rather than <see cref="string.GetHashCode"/>, and
        /// invariant formatting rather than the ambient culture's.
        ///
        /// <para><b>The material is part of the name, and that is load-bearing.</b> SE caches voxel
        /// bytes per client in an LRU keyed on the storage name
        /// (<c>MyMultiplayer.Static.VoxelMapData</c>), which assumes a name identifies immutable
        /// content. Ours names a <i>cell</i>, and a cell's content changes whenever its authored
        /// composition does — so without this a client kept serving pre-change bytes forever while
        /// the server held the new rock, and mining disagreed with what was on screen. Measured live
        /// 2026-08-25. Folding the material in means changed composition yields a new name, which no
        /// cache can hit.</para>
        /// </summary>
        public static string StorageName(int featureHash, CellCoord cell, int index, string materialSubTypeId)
        {
            int contentTag = WorldRecipe.StableHash(materialSubTypeId ?? string.Empty);
            return "fr" + featureHash.ToString("x8", CultureInfo.InvariantCulture)
                 + "_" + cell
                 + "_" + index.ToString(CultureInfo.InvariantCulture)
                 + "_" + (contentTag & 0xFFF).ToString("x3", CultureInfo.InvariantCulture);
        }

        private static int FloorDiv(double value, double size)
        {
            return (int)Math.Floor(value / size);
        }

        /// <summary>Order-independent mixing of two identity components. FNV-style so it stays
        /// stable across runtimes for the same reason <see cref="WorldRecipe.StableHash"/> does.</summary>
        public static int Combine(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a ^ 2166136261;
                h *= 16777619;
                h ^= (uint)b;
                h *= 16777619;
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}
