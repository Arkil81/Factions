using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Hull-independent <see cref="LegImpulseProfile"/>s, keyed on the geometry that
    /// produced them. Impulse per kilogram depends on the polyline and the gravity along it and on
    /// nothing else, so the same node pair is solved once and every later dispatch over it
    /// multiplies the cached scalars by its own mass and bands.
    ///
    /// <para><b>Why this exists.</b> Live traffic runs at roughly 1.6 dispatches a second inside
    /// the Torch update tick. A sampled build walks the polyline at
    /// <see cref="LegImpulseProfile.SampleStepKm"/> and queries gravity at every step, which on the
    /// host is a call into SE. The cache turns the common repeat into a dictionary lookup, and
    /// <see cref="IsDeepSpace"/> turns the common first sight — a leg that never comes near a
    /// gravity well — into a few sphere tests.</para>
    ///
    /// <para>Bounded by clearing at <see cref="Capacity"/> rather than by eviction: the working set
    /// is node pairs, which is small and stable, and a clear costs one rebuild of it. The cache is
    /// invalidated wholesale when the planet catalogue instance or either tuning input changes,
    /// because every entry was solved against those.</para></summary>
    public sealed class LegProfileCache
    {
        /// <summary>Entries held before the whole table is dropped.</summary>
        public const int Capacity = 4096;

        /// <summary>Position quantisation, in reciprocal kilometres: 0.1 km. Finer than any node
        /// pair this world distinguishes and coarse enough that floating-point drift in a
        /// recomputed position still lands on the same key.</summary>
        public const double KeyScalePerKm = 10.0;

        private readonly Dictionary<ProfileKey, LegImpulseProfile> _entries = new Dictionary<ProfileKey, LegImpulseProfile>();
        private IPlanetCatalogue _planets;
        private double _dropoutG;
        private double _cruiseMs;
        private double _brakeMs;
        private double _transitMs;

        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Count { get { return _entries.Count; } }

        public void ResetCounters() { Hits = 0; Misses = 0; }

        public void Clear() { _entries.Clear(); }

        /// <summary>The profile for <paramref name="polylineKm"/>, cached on its endpoints and
        /// vertex count. <paramref name="cacheHit"/> reports which side of the lookup the answer
        /// came from, for the ledger.</summary>
        public LegImpulseProfile Get(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm,
                                     out bool cacheHit)
        {
            return Get(planets, tuning, polylineKm, 0, out cacheHit);
        }

        /// <summary>The same lookup, priced at <paramref name="thrusterSpeedMs"/> instead of the
        /// tuning default. At or below 0 means the default.
        ///
        /// <para>The entry is still solved and stored once per geometry: speed is applied on the way
        /// out by <see cref="LegImpulseProfile.AtSpeed"/>, which is exact and O(1). So a fleet-speed
        /// model that varies speed per hull, per environment or per leg costs this cache nothing —
        /// it neither multiplies the entries nor invalidates them.</para></summary>
        public LegImpulseProfile Get(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm,
                                     double thrusterSpeedMs, out bool cacheHit)
        {
            cacheHit = false;
            if (tuning == null || polylineKm == null || polylineKm.Count < 2) return new LegImpulseProfile();

            if (!ReferenceEquals(planets, _planets) || _dropoutG != tuning.FsdGravityDropoutG
                || _cruiseMs != tuning.ThrusterCruiseSpeedMs
                || _brakeMs != LegImpulseProfile.ThrusterBrakeSpeedMs(tuning)
                || _transitMs != LegImpulseProfile.InGravityTransitSpeedMs(tuning))
            {
                _entries.Clear();
                _planets = planets;
                _dropoutG = tuning.FsdGravityDropoutG;
                _cruiseMs = tuning.ThrusterCruiseSpeedMs;
                _brakeMs = LegImpulseProfile.ThrusterBrakeSpeedMs(tuning);
                _transitMs = LegImpulseProfile.InGravityTransitSpeedMs(tuning);
            }

            ProfileKey key = ProfileKey.For(polylineKm);
            LegImpulseProfile profile;
            if (_entries.TryGetValue(key, out profile))
            {
                Hits++; cacheHit = true;
                return profile.AtSpeed(thrusterSpeedMs);
            }

            Misses++;
            profile = Build(planets, tuning, polylineKm);
            if (_entries.Count >= Capacity) _entries.Clear();
            _entries[key] = profile;
            return profile.AtSpeed(thrusterSpeedMs);
        }

        /// <summary>The profile for one polyline, taking the deep-space shortcut when the whole
        /// path provably stays outside every gravity well. Public so the equivalence of the two
        /// paths can be asserted directly.</summary>
        public static LegImpulseProfile Build(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm)
        {
            if (IsDeepSpace(planets, polylineKm)) return DeepSpaceProfile(tuning, polylineKm);
            return LegImpulseProfile.Build(planets, tuning, polylineKm);
        }

        /// <summary>True when no point of <paramref name="polylineKm"/> lies inside any body's
        /// <see cref="PlanetRecord.GravityRadius"/>. A segment-versus-sphere test per body, which
        /// is a handful of arithmetic against the sampled walk's hundreds of gravity queries.
        ///
        /// <para>Conservative in the only direction that matters: a path that grazes a well is
        /// reported as not deep space and takes the sampled path, so the shortcut can never
        /// under-charge a leg that enters gravity between two samples.</para></summary>
        public static bool IsDeepSpace(IPlanetCatalogue planets, IList<Vec3> polylineKm)
        {
            if (planets == null || polylineKm == null || polylineKm.Count < 2) return false;
            foreach (PlanetRecord planet in planets.Planets)
            {
                if (planet == null) continue;
                double radiusKm = planet.GravityRadius;
                if (radiusKm <= 0) continue;
                for (int i = 0; i < polylineKm.Count - 1; i++)
                    if (SegmentDistanceKm(polylineKm[i], polylineKm[i + 1], planet.Center) < radiusKm)
                        return false;
            }
            return true;
        }

        /// <summary>What the sampled walk produces for a path entirely outside every well, without
        /// sampling it: no lift impulse, no in-well turn charge, one FSD stretch over the whole
        /// length, and the one accelerate/brake pair every leg owes for its undock and its dock.</summary>
        private static LegImpulseProfile DeepSpaceProfile(FactionTuning tuning, IList<Vec3> polylineKm)
        {
            var profile = new LegImpulseProfile();
            if (tuning == null || polylineKm == null || polylineKm.Count < 2) return profile;
            profile.HasGeometry = true;

            double lengthKm = 0;
            for (int i = 0; i < polylineKm.Count - 1; i++) lengthKm += polylineKm[i].DistanceTo(polylineKm[i + 1]);
            if (lengthKm > 0)
            {
                profile.FsdDistanceKm = lengthKm;
                profile.FsdEngagements = 1;
            }
            profile.ThrusterStretches = 1;

            profile.Ends = ThrusterStretchEnds.AccelerateAndBrake;
            profile.ApplySpeeds(tuning.ThrusterCruiseSpeedMs, LegImpulseProfile.ThrusterBrakeSpeedMs(tuning));
            return profile;
        }

        /// <summary>Shortest distance from <paramref name="centre"/> to the segment a→b, in km.</summary>
        private static double SegmentDistanceKm(Vec3 a, Vec3 b, Vec3 centre)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double lengthSq = abx * abx + aby * aby + abz * abz;
            double acx = centre.X - a.X, acy = centre.Y - a.Y, acz = centre.Z - a.Z;
            if (lengthSq <= 1e-12) return Math.Sqrt(acx * acx + acy * acy + acz * acz);

            double t = (acx * abx + acy * aby + acz * abz) / lengthSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = acx - abx * t, dy = acy - aby * t, dz = acz - abz * t;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Every vertex, quantised to <see cref="KeyScalePerKm"/> and folded into a hash.
        /// <para><b>Endpoints and a vertex count are not enough.</b> <c>RouteChooser</c> costs several
        /// routes between one pair of endpoints, and two of them clearing the same body at different
        /// radii have the same vertex count — so a key made of the ends and the count hands the
        /// second candidate the first one's answer, and the comparison silently reads one route
        /// twice. Hashing the vertices costs a few dozen doubles against the sampled walk it
        /// avoids.</para></summary>
        private struct ProfileKey : IEquatable<ProfileKey>
        {
            private readonly long _ax, _ay, _az, _bx, _by, _bz;
            private readonly int _vertices;
            private readonly int _shape;   // hash of every interior vertex, in order

            private ProfileKey(long ax, long ay, long az, long bx, long by, long bz, int vertices, int shape)
            {
                _ax = ax; _ay = ay; _az = az; _bx = bx; _by = by; _bz = bz;
                _vertices = vertices; _shape = shape;
            }

            public static ProfileKey For(IList<Vec3> polylineKm)
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
                return new ProfileKey(Q(a.X), Q(a.Y), Q(a.Z), Q(b.X), Q(b.Y), Q(b.Z), polylineKm.Count, shape);
            }

            private static long Q(double km) { return (long)Math.Round(km * KeyScalePerKm); }

            public bool Equals(ProfileKey other)
            {
                return _ax == other._ax && _ay == other._ay && _az == other._az
                    && _bx == other._bx && _by == other._by && _bz == other._bz
                    && _vertices == other._vertices && _shape == other._shape;
            }

            public override bool Equals(object obj) { return obj is ProfileKey && Equals((ProfileKey)obj); }

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
