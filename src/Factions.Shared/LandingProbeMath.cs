namespace Factions
{
    /// <summary>
    /// The SE-free half of <see cref="LandingProbe"/>: which voxel box a probe reads, and how its
    /// content bytes are reduced to a checksum. Both ends of a gear lock must pick the same box and
    /// reduce it the same way, or a differing checksum would mean nothing.
    ///
    /// <para>The box is snapped to a <see cref="Cell"/>-voxel lattice rather than centred on the
    /// probe point, so a server and a client whose pivots differ by a fraction of a metre still read
    /// the identical box. It is <see cref="Span"/> voxels on a side and keeps the point at least
    /// <see cref="Margin"/> voxels from every face. Two lines whose <c>box=</c> differ were snapped
    /// to neighbouring cells and their checksums are not comparable.</para>
    /// </summary>
    public static class LandingProbeMath
    {
        public const int Cell = 16;
        public const int Span = 32;
        public const int Margin = (Span - Cell) / 2;

        /// <summary>Minimum corner, on one axis, of the box holding voxel coordinate
        /// <paramref name="coord"/>. The maximum corner is this plus <see cref="Span"/> - 1.</summary>
        public static int BoxMin(int coord)
        {
            return FloorDiv(coord, Cell) * Cell - Margin;
        }

        /// <summary>FNV-1a, 32-bit, over the first <paramref name="count"/> bytes.</summary>
        public static uint Fnv1a(byte[] data, int count)
        {
            uint hash = 2166136261u;
            if (data == null) return hash;
            if (count > data.Length) count = data.Length;
            for (int i = 0; i < count; i++)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }
            return hash;
        }

        /// <summary>Sum of the first <paramref name="count"/> bytes: the box's total content, so two
        /// differing checksums also say how much material one side has that the other lacks.</summary>
        public static long Sum(byte[] data, int count)
        {
            long sum = 0;
            if (data == null) return 0;
            if (count > data.Length) count = data.Length;
            for (int i = 0; i < count; i++) sum += data[i];
            return sum;
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
    }
}
