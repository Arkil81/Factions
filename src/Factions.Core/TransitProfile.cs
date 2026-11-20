using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>The speed tier one stretch of a leg is flown in. Ordered slowest to fastest, and
    /// persisted as its integer value, so a new tier must be appended rather than inserted.</summary>
    public enum TransitTier
    {
        /// <summary>Inside a body's gravity well, where the FSD refuses and a hull is on its own
        /// thrusters. Capped at <see cref="FactionTuning.FleetSpeedMinKmPerMin"/>, the world's own
        /// large-grid speed limit.</summary>
        Gravity = 0,

        /// <summary>An authored region outside every well — a ring, a belt, anything the world
        /// declares <see cref="NodeZone.Orbit"/>. Capped at
        /// <see cref="FactionTuning.FleetSpeedOrbitKmPerMin"/>.</summary>
        Orbit = 1,

        /// <summary>Open space. Capped at <see cref="FactionTuning.FleetSpeedKmPerMin"/>.</summary>
        DeepSpace = 2,
    }

    /// <summary>Which tier each stretch of one already-planned polyline falls in, as a run-length
    /// encoding in path order. Geometry decides it and nothing else, so it is the same for every
    /// fleet and every speed, and is meant to be built once per node pair and cached.
    ///
    /// <para><b>Why the tiers are not the speeds.</b> A run table solved against the world survives
    /// a retune, while a table of minutes does not. The speeds enter only in
    /// <see cref="TransitSchedule"/>, which integrates this against the live tuning on demand — the
    /// same split <see cref="LegImpulseProfile.AtSpeed"/> makes for the burn model, and for the same
    /// reason: the walk that costs the time is speed-free.</para>
    ///
    /// <para><b>Precedence: gravity outranks the authored world.</b> This is the inverse of the
    /// placement rule, where an authored region wins over a gravity sample. It has to be: an
    /// authored ring is a statement about where things belong, and it cannot make a drive run where
    /// the drive refuses. Cauldron's ring straddles its own gravity limit, so the inboard half of
    /// that ring is <see cref="TransitTier.Gravity"/> and only the outboard half is
    /// <see cref="TransitTier.Orbit"/>.</para></summary>
    public sealed class LegZoneProfile
    {
        /// <summary>Length of each run, in km, in path order. Parallel to <see cref="RunTiers"/>.</summary>
        public readonly List<double> RunKm = new List<double>();

        /// <summary>The tier each run is flown in. Parallel to <see cref="RunKm"/>.</summary>
        public readonly List<TransitTier> RunTiers = new List<TransitTier>();

        /// <summary>Summed <see cref="RunKm"/> — the flown length of the polyline.</summary>
        public double TotalKm;

        /// <summary>False when the polyline had fewer than two points, so nothing was sampled. The
        /// run table is then empty, which means "unknown", not "all deep space".</summary>
        public bool HasGeometry;

        // The schedule built from this profile, and the tuning values it was built under
        // (TransitSchedule.TryGet). Not persisted; Add drops it. The schedule is a pure function of the
        // run table and those four numbers.
        internal TransitSchedule CachedSchedule;
        internal bool CachedScheduleValid;
        internal double CachedMin, CachedMax, CachedOrbit, CachedAccel;

        /// <summary>True when the whole leg is one <see cref="TransitTier.DeepSpace"/> run, i.e. the
        /// tiers change nothing and the schedule is the plain profile a bare distance produces.
        /// The common case on any world, and what lets the run table be omitted from persistence.</summary>
        public bool IsPlainDeepSpace
        {
            get { return RunTiers.Count == 1 && RunTiers[0] == TransitTier.DeepSpace; }
        }

        /// <summary>Path sampling step, matching <see cref="LegImpulseProfile.SampleStepKm"/> so the
        /// two walks resolve a boundary to the same precision.</summary>
        public const double SampleStepKm = LegImpulseProfile.SampleStepKm;

        /// <summary>Cap on sub-segments per polyline segment, as <see cref="LegImpulseProfile"/>.</summary>
        public const int MaxSamplesPerSegment = LegImpulseProfile.MaxSamplesPerSegment;

        /// <summary>Ceiling on runs in one table. A path alternating tiers more often than this is
        /// merged into its neighbours rather than growing the table without bound — it is persisted
        /// per in-flight fleet, and the schedule walks it per query.</summary>
        public const int MaxRuns = 32;

        /// <summary>Appends <paramref name="km"/> of <paramref name="tier"/>, extending the last run
        /// when the tier is unchanged.</summary>
        public void Add(TransitTier tier, double km)
        {
            if (km <= 0) return;
            CachedScheduleValid = false;
            CachedSchedule = null;
            TotalKm += km;
            int last = RunTiers.Count - 1;
            if (last >= 0 && RunTiers[last] == tier) { RunKm[last] = RunKm[last] + km; return; }
            RunKm.Add(km);
            RunTiers.Add(tier);
        }

        /// <summary>Kilometres in one tier. A report figure, and what sizes a tier against a world.</summary>
        public double KmIn(TransitTier tier)
        {
            double total = 0;
            for (int i = 0; i < RunTiers.Count; i++) if (RunTiers[i] == tier) total += RunKm[i];
            return total;
        }

        /// <summary>Collapses the shortest runs until at most <see cref="MaxRuns"/> remain, folding
        /// each into whichever neighbour is slower so a merge can never make a leg faster than the
        /// ground under it allows.</summary>
        public void Bound()
        {
            while (RunTiers.Count > MaxRuns)
            {
                int smallest = 0;
                for (int i = 1; i < RunKm.Count; i++) if (RunKm[i] < RunKm[smallest]) smallest = i;

                int into = smallest > 0 ? smallest - 1 : 1;
                if (smallest > 0 && smallest + 1 < RunTiers.Count && RunTiers[smallest + 1] < RunTiers[into])
                    into = smallest + 1;

                RunKm[into] = RunKm[into] + RunKm[smallest];
                RunKm.RemoveAt(smallest);
                RunTiers.RemoveAt(smallest);

                // The removal can leave two equal-tier runs adjacent; fold them so the bound makes
                // progress rather than shuffling the same count.
                for (int i = RunTiers.Count - 1; i > 0; i--)
                {
                    if (RunTiers[i] != RunTiers[i - 1]) continue;
                    RunKm[i - 1] = RunKm[i - 1] + RunKm[i];
                    RunKm.RemoveAt(i);
                    RunTiers.RemoveAt(i);
                }
            }
        }

        /// <summary>The table for a leg of <paramref name="distKm"/> that is entirely open space —
        /// what a caller with no geometry gets, and exactly the profile a bare distance has always
        /// produced.</summary>
        public static LegZoneProfile PlainDeepSpace(double distKm)
        {
            var profile = new LegZoneProfile();
            if (distKm <= 0) return profile;
            profile.HasGeometry = true;
            profile.Add(TransitTier.DeepSpace, distKm);
            return profile;
        }

        /// <summary>Walks <paramref name="polylineKm"/> end to end and records which tier each
        /// sampled sub-segment falls in.
        ///
        /// <para>Gravity is tested first and wins: a sample inside any body's
        /// <see cref="PlanetRecord.GravityRadius"/> whose gravity reads above
        /// <see cref="FactionTuning.FsdGravityDropoutG"/> is <see cref="TransitTier.Gravity"/>
        /// whatever the authored world says about that point. Everything else asks
        /// <paramref name="features"/> for an authored zone, using containment — a ring's
        /// <see cref="WorldFeature.HalfThicknessKm"/> is enforced, so a path passing high over a
        /// ring's pole is open space rather than ring traffic.</para></summary>
        public static LegZoneProfile Build(IPlanetCatalogue planets, WorldFeaturePlacement features,
                                           FactionTuning tuning, IList<Vec3> polylineKm)
        {
            var profile = new LegZoneProfile();
            if (tuning == null || polylineKm == null || polylineKm.Count < 2) return profile;
            profile.HasGeometry = true;

            double dropoutG = tuning.FsdGravityDropoutG;

            for (int segment = 0; segment < polylineKm.Count - 1; segment++)
            {
                Vec3 from = polylineKm[segment], to = polylineKm[segment + 1];
                double segmentKm = from.DistanceTo(to);
                if (segmentKm <= 0) continue;

                int steps = (int)Math.Ceiling(segmentKm / SampleStepKm);
                if (steps < 1) steps = 1;
                if (steps > MaxSamplesPerSegment) steps = MaxSamplesPerSegment;
                double stepKm = segmentKm / steps;

                for (int step = 0; step < steps; step++)
                {
                    double t = (step + 0.5) / steps;
                    Vec3 sample = Lerp(from, to, t);
                    profile.Add(TierAt(planets, features, sample, dropoutG), stepKm);
                }
            }

            if (profile.TotalKm <= 0) return profile;
            profile.Bound();
            return profile;
        }

        /// <summary>A bounded table for a straight leg, from the two endpoints alone: no path walk,
        /// no gravity sampling beyond the ends, no cache. What a dispatch scoring loop reads when it
        /// asks "can this fleet get there in time" about many candidates per tick, originating from
        /// an interpolated position that would miss a geometry cache every time.
        ///
        /// <para>An endpoint inside a gravity well contributes the chord its own well cuts from the
        /// leg, solved analytically, so the common shape — a leg leaving or arriving at a station in
        /// gravity — is priced correctly. What it cannot see is a well crossed <i>between</i> the
        /// ends, so it is optimistic there; <see cref="Build"/> is the form that reads the whole
        /// path.</para>
        ///
        /// <para><b>An authored region at an endpoint is deliberately not modelled here.</b> The
        /// cheap form has only a feature's bounding sphere to work with, and for the shape this is
        /// actually for — a ring 3.6 km thick inside a 465 km sphere — that chord over-states the
        /// crossing by two orders of magnitude. Ignoring it is the smaller error, and the sampled
        /// build is where a ring is priced.</para></summary>
        public static LegZoneProfile FromEndpoints(IPlanetCatalogue planets, FactionTuning tuning,
                                                   Vec3 fromKm, Vec3 toKm)
        {
            double distKm = fromKm.DistanceTo(toKm);
            if (tuning == null || distKm <= 0) return new LegZoneProfile();

            double dropoutG = tuning.FsdGravityDropoutG;
            double head = GravityChordKm(planets, fromKm, toKm, dropoutG);
            double tail = GravityChordKm(planets, toKm, fromKm, dropoutG);

            if (head <= 0 && tail <= 0) return PlainDeepSpace(distKm);

            // Neither end may claim more than its half, so two wells cannot between them claim more
            // than the leg -- which a short leg with both ends in gravity would otherwise do.
            double half = distKm * 0.5;
            if (head > half) head = half;
            if (tail > half) tail = half;

            var profile = new LegZoneProfile();
            profile.HasGeometry = true;
            profile.Add(TransitTier.Gravity, head);
            profile.Add(TransitTier.DeepSpace, distKm - head - tail);
            profile.Add(TransitTier.Gravity, tail);
            return profile;
        }

        /// <summary>Kilometres from <paramref name="endKm"/> toward <paramref name="otherKm"/> that
        /// lie inside the well containing <paramref name="endKm"/>, or 0 when it is not in one.
        /// The segment-versus-sphere root, taken on the endpoint's side.</summary>
        private static double GravityChordKm(IPlanetCatalogue planets, Vec3 endKm, Vec3 otherKm, double dropoutG)
        {
            if (!InGravity(planets, endKm, dropoutG)) return 0;

            PlanetRecord well = PlanetGeometry.GravityWellPlanet(planets, endKm);
            if (well == null || well.GravityRadius <= 0) return 0;

            double dx = otherKm.X - endKm.X, dy = otherKm.Y - endKm.Y, dz = otherKm.Z - endKm.Z;
            double lengthSq = dx * dx + dy * dy + dz * dz;
            if (lengthSq <= 1e-12) return 0;

            double ox = endKm.X - well.Center.X, oy = endKm.Y - well.Center.Y, oz = endKm.Z - well.Center.Z;
            double b = ox * dx + oy * dy + oz * dz;
            double c = ox * ox + oy * oy + oz * oz - well.GravityRadius * well.GravityRadius;

            double discriminant = b * b - lengthSq * c;
            if (discriminant <= 0) return 0;

            // c < 0 because the endpoint is inside, so the two roots straddle 0 and the exit is the
            // positive one.
            double t = (-b + Math.Sqrt(discriminant)) / lengthSq;
            if (t <= 0) return 0;
            if (t > 1) t = 1;
            return t * Math.Sqrt(lengthSq);
        }

        /// <summary>The tier one point falls in. Public because it is also the cheap form: a caller
        /// scoring many candidates classifies the two endpoints rather than walking a path.</summary>
        public static TransitTier TierAt(IPlanetCatalogue planets, WorldFeaturePlacement features,
                                         Vec3 posKm, double dropoutG)
        {
            if (InGravity(planets, posKm, dropoutG)) return TransitTier.Gravity;

            NodeZone zone;
            if (features != null && features.TryZoneContaining(posKm, out zone) && zone != NodeZone.DeepSpace)
                return zone == NodeZone.Surface ? TransitTier.Gravity : TransitTier.Orbit;

            return TransitTier.DeepSpace;
        }

        /// <summary>True when the drive refuses at <paramref name="posKm"/>. The containment test
        /// comes first because it is arithmetic while <see cref="IPlanetCatalogue.GravityAt"/> is a
        /// call into the host.</summary>
        private static bool InGravity(IPlanetCatalogue planets, Vec3 posKm, double dropoutG)
        {
            if (planets == null || dropoutG <= 0) return false;

            PlanetRecord well = PlanetGeometry.GravityWellPlanet(planets, posKm);
            if (well == null || well.GravityRadius <= 0) return false;
            if (posKm.DistanceTo(well.Center) >= well.GravityRadius) return false;

            return planets.GravityAt(posKm) > dropoutG;
        }

        private static Vec3 Lerp(Vec3 a, Vec3 b, double t)
        {
            return new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }
    }
}
