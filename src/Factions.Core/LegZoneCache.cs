using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Tier run tables (<see cref="LegZoneProfile"/>) keyed on the geometry that produced
    /// them, on the same terms and for the same reason as <see cref="LegProfileCache"/>: the table
    /// depends on the polyline, the planets and the authored features and on nothing else, so one
    /// node pair is solved once and every later dispatch over it is a dictionary lookup.
    ///
    /// <para>Speeds are deliberately not part of the key. A retune changes what the table costs, not
    /// what it says, so the cache survives one — which is what lets the transit model be read on
    /// every dispatch without a walk.</para></summary>
    public sealed class LegZoneCache
    {
        /// <summary>Entries held before the whole table is dropped.</summary>
        public const int Capacity = 4096;

        /// <summary>Position quantisation, in reciprocal kilometres, as <see cref="LegProfileCache"/>.</summary>
        public const double KeyScalePerKm = 10.0;

        private readonly Dictionary<ZoneKey, LegZoneProfile> _entries = new Dictionary<ZoneKey, LegZoneProfile>();
        private IPlanetCatalogue _planets;
        private WorldFeaturePlacement _features;
        private double _dropoutG;

        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Count { get { return _entries.Count; } }

        public void ResetCounters() { Hits = 0; Misses = 0; }

        public void Clear() { _entries.Clear(); }

        /// <summary>The tier table for <paramref name="polylineKm"/>.</summary>
        public LegZoneProfile Get(IPlanetCatalogue planets, WorldFeaturePlacement features,
                                  FactionTuning tuning, IList<Vec3> polylineKm, out bool cacheHit)
        {
            cacheHit = false;
            if (tuning == null || polylineKm == null || polylineKm.Count < 2) return new LegZoneProfile();

            if (!ReferenceEquals(planets, _planets) || !ReferenceEquals(features, _features)
                || _dropoutG != tuning.FsdGravityDropoutG)
            {
                _entries.Clear();
                _planets = planets;
                _features = features;
                _dropoutG = tuning.FsdGravityDropoutG;
            }

            ZoneKey key = ZoneKey.For(polylineKm);
            LegZoneProfile profile;
            if (_entries.TryGetValue(key, out profile)) { Hits++; cacheHit = true; return profile; }

            Misses++;
            profile = Build(planets, features, tuning, polylineKm);
            if (_entries.Count >= Capacity) _entries.Clear();
            _entries[key] = profile;
            return profile;
        }

        /// <summary>The table for one polyline, taking the open-space shortcut when the path
        /// provably meets neither a gravity well nor a zone-authoring feature. Public so the
        /// equivalence of the two paths can be asserted directly.</summary>
        public static LegZoneProfile Build(IPlanetCatalogue planets, WorldFeaturePlacement features,
                                           FactionTuning tuning, IList<Vec3> polylineKm)
        {
            if (IsPlainDeepSpace(planets, features, polylineKm))
                return LegZoneProfile.PlainDeepSpace(LengthKm(polylineKm));
            return LegZoneProfile.Build(planets, features, tuning, polylineKm);
        }

        /// <summary>True when no point of <paramref name="polylineKm"/> can be in a gravity well or
        /// inside a zone-authoring feature — a handful of segment-versus-sphere tests against the
        /// sampled walk's hundreds of gravity queries.</summary>
        public static bool IsPlainDeepSpace(IPlanetCatalogue planets, WorldFeaturePlacement features,
                                            IList<Vec3> polylineKm)
        {
            if (!LegProfileCache.IsDeepSpace(planets, polylineKm)) return false;
            if (features == null) return true;

            for (int i = 0; i < polylineKm.Count - 1; i++)
                if (features.MightClaimZone(polylineKm[i], polylineKm[i + 1])) return false;
            return true;
        }

        private static double LengthKm(IList<Vec3> polylineKm)
        {
            double total = 0;
            for (int i = 0; i < polylineKm.Count - 1; i++) total += polylineKm[i].DistanceTo(polylineKm[i + 1]);
            return total;
        }

        /// <summary>Every vertex, quantised to <see cref="KeyScalePerKm"/> and folded into a hash.
        /// <para><b>Endpoints and a vertex count are not enough.</b> <c>RouteChooser</c> costs several
        /// routes between one pair of endpoints, and two of them clearing the same body at different
        /// radii have the same vertex count — so a key made of the ends and the count hands the
        /// second candidate the first one's answer, and the comparison silently reads one route
        /// twice. Hashing the vertices costs a few dozen doubles against the sampled walk it
        /// avoids.</para></summary>
        private struct ZoneKey : IEquatable<ZoneKey>
        {
            private readonly long _ax, _ay, _az, _bx, _by, _bz;
            private readonly int _vertices;
            private readonly int _shape;   // hash of every interior vertex, in order

            private ZoneKey(long ax, long ay, long az, long bx, long by, long bz, int vertices, int shape)
            {
                _ax = ax; _ay = ay; _az = az; _bx = bx; _by = by; _bz = bz;
                _vertices = vertices; _shape = shape;
            }

            public static ZoneKey For(IList<Vec3> polylineKm)
            {
                Vec3 a = polylineKm[0], b = polylineKm[polylineKm.Count - 1];
                int shape = 17;
                unchecked
                {
                    for (int i = 1; i < polylineKm.Count - 1; i++)
                    {
                        shape = shape * 31 + Q(polylineKm[i].X).GetHashCode();
                        shape = shape * 31 + Q(polylineKm[i].Y).GetHashCode();
                        shape = shape * 31 + Q(polylineKm[i].Z).GetHashCode();
                    }
                }
                return new ZoneKey(Q(a.X), Q(a.Y), Q(a.Z), Q(b.X), Q(b.Y), Q(b.Z), polylineKm.Count, shape);
            }

            private static long Q(double km) { return (long)Math.Round(km * KeyScalePerKm); }

            public bool Equals(ZoneKey other)
            {
                return _ax == other._ax && _ay == other._ay && _az == other._az
                    && _bx == other._bx && _by == other._by && _bz == other._bz
                    && _vertices == other._vertices && _shape == other._shape;
            }

            public override bool Equals(object obj) { return obj is ZoneKey && Equals((ZoneKey)obj); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + _ax.GetHashCode();
                    hash = hash * 31 + _ay.GetHashCode();
                    hash = hash * 31 + _az.GetHashCode();
                    hash = hash * 31 + _bx.GetHashCode();
                    hash = hash * 31 + _by.GetHashCode();
                    hash = hash * 31 + _bz.GetHashCode();
                    hash = hash * 31 + _vertices;
                    hash = hash * 31 + _shape;
                    return hash;
                }
            }
        }
    }
}
