using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which ends of a thruster stretch a profile is charged for. A stretch that starts
    /// and ends at rest pays both; the two one-ended cases are the manoeuvres a materialised grid
    /// is actually observed flying, where the controller hands the grid over at speed or takes it
    /// over already moving.</summary>
    public enum ThrusterStretchEnds
    {
        /// <summary>Accelerate at the start, brake at the end. What an abstract leg does, and the
        /// default.</summary>
        AccelerateAndBrake = 0,

        /// <summary>Accelerate only: a launch, which hands the grid back at speed and never brakes
        /// it. The whole charge lands on the <c>Backward</c> bands.</summary>
        AccelerateOnly = 1,

        /// <summary>Brake only: an approach that engages on a grid already at cruise. The whole
        /// charge lands on the <c>Forward</c> bands.</summary>
        BrakeOnly = 2,
    }

    /// <summary>The hull-independent half of a leg's fuel cost: impulse per kilogram, plus the FSD
    /// stretches, for one already-planned polyline. Geometry and gravity decide it, so it is the
    /// same for every hull and is meant to be built once per node pair and cached; a hull turns it
    /// into items by multiplying by its mass and its bands' specific consumption
    /// (<see cref="FuelBurn.ForLeg"/>). That split is what lets sourcing rank candidates before a
    /// hull is chosen.
    ///
    /// <para>The model is Newtonian, from docs/fuel-plan.md: outside a gravity well coasting is
    /// free and only the ends of a thruster stretch cost Δv, while inside one the <c>Down</c> bands
    /// carry the hull's weight for every second it is there. The boundary between the two is where
    /// natural gravity falls to <see cref="FactionTuning.FsdGravityDropoutG"/>, which is where the
    /// FSD refuses to run.</para>
    ///
    /// <para>Thruster phases are clocked at <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> — the
    /// speed a hull flies under thrusters, which is well under the world's grid speed limit. Hover
    /// seconds are a stretch's length divided by it and a stretch's Δv is charged at it, so the two
    /// terms move in opposite directions when it changes.</para></summary>
    public sealed class LegImpulseProfile
    {
        /// <summary>Newton-seconds per kilogram the <c>Down</c> bands owe for holding the hull up:
        /// <c>∫ g·9.81 ds / v</c> over the in-well stretches, <c>v</c> being
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/>.</summary>
        public double DownImpulsePerKg;

        /// <summary>Total thruster Δv in m/s — the sum of <see cref="AccelerateDeltaVMs"/> and
        /// <see cref="BrakeDeltaVMs"/>. A report figure; the charge is made from the two halves,
        /// because the bands that accelerate a hull are not the ones that stop it.</summary>
        public double ThrustDeltaVMs;

        /// <summary>Δv in m/s owed to the <c>Backward</c> bands: one
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> per contiguous thruster stretch that is
        /// charged for its start, plus half of every in-well vertex's <c>2·v·sin(θ/2)</c>, since a
        /// turn is a brake followed by an accelerate.</summary>
        public double AccelerateDeltaVMs;

        /// <summary>Δv in m/s owed to the <c>Forward</c> bands, the braking counterpart of
        /// <see cref="AccelerateDeltaVMs"/>.</summary>
        public double BrakeDeltaVMs;

        /// <summary>Kilometres flown outside the gravity boundary, under the FSD.</summary>
        public double FsdDistanceKm;

        /// <summary>Contiguous FSD stretches, each of which pays one charge burst.</summary>
        public int FsdEngagements;

        /// <summary>Contiguous thruster stretches. At least 1 for any real leg: a leg that never
        /// enters a well still undocks and docks.</summary>
        public int ThrusterStretches;

        /// <summary>Kilometres flown on thrusters, inside the boundary. Not itself a charge —
        /// coasting is free — but the length the hover term was integrated over.</summary>
        public double ThrusterDistanceKm;

        /// <summary>Gravity the thruster phases are charged at, for the band effectiveness curve:
        /// the length-weighted mean over the in-well samples, 0 when the leg never enters a well.
        /// One representative for the whole leg rather than one per stretch — the profile is a
        /// cached scalar set, and the alternative is a per-stretch list every consumer would have
        /// to walk.</summary>
        public double RepresentativeG;

        /// <summary>Whether the thruster phases are charged as in-atmosphere: true when more than
        /// half the in-well sampled length sits inside an atmosphere. Selects the atmospheric bands'
        /// effectiveness, the same way <see cref="RepresentativeG"/> selects the curve position.</summary>
        public bool RepresentativeInAtmosphere;

        /// <summary>Length-weighted mean air density over the in-well sampled stretches — what sets
        /// thrust effectiveness on those stretches, as <see cref="RepresentativeG"/> sets weight.
        /// Gravity is not a stand-in for it: on the upper slopes of an atmosphere the two differ by
        /// more than half.</summary>
        public double RepresentativeAirDensity;

        /// <summary>False when the polyline had fewer than two points, so nothing was sampled and
        /// every term above is a zero that means "unknown", not "free".</summary>
        public bool HasGeometry;

        /// <summary>∫ g·9.81 ds over the in-well stretches, in m²/s². Speed-free: this is the whole
        /// cost of the sampled gravity walk, and dividing it by a speed gives the hover impulse per
        /// kilogram at that speed. Stored so a cached geometry can be priced at any speed.</summary>
        public double GravityImpulseIntegralPerKg;

        /// <summary>Σ 2·sin(θ/2) over the interior vertices that count, dimensionless. Speed-free,
        /// for the same reason: multiplying by a speed gives the turn Δv at that speed.</summary>
        public double TurnSineSum;

        /// <summary>Which ends of a stretch this profile charges. Held because it decides how the
        /// hover integral is apportioned between the two speeds.</summary>
        public ThrusterStretchEnds Ends;

        /// <summary>Speed the accelerating end is flown at, m/s — a departure climbing out of a
        /// well, capped by the flight controller's launch handoff.</summary>
        public double AccelerateSpeedMs;

        /// <summary>Speed the braking end is flown at, m/s. Measured at about 4x the launch speed:
        /// nothing caps a descent until braking starts (docs/fuel-plan.md, the in-well speed
        /// table).</summary>
        public double BrakeSpeedMs;

        /// <summary>Speed the part of an in-well stretch that is neither end is flown at, m/s — the
        /// world's own grid limit, which is what caps a hull transiting a well rather than entering
        /// or leaving it. 0 means the stretch is charged entirely at its ends' speeds, which is how
        /// every calibration build and every host whose grid limit is at or below
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> prices.</summary>
        public double TransitSpeedMs;

        /// <summary>True once <see cref="RetimeHover"/> has replaced the derived hover duration with
        /// a measured one. The hover term then describes a specific manoeuvre rather than a speed,
        /// so <see cref="AtSpeed"/> will not rescale it.</summary>
        public bool HoverIsMeasured;

        /// <summary>The same geometry priced at a different thruster speed, in O(1).
        ///
        /// <para>Exact, not an approximation, and that is the point: the gravity walk that costs the
        /// time produces <c>Σ g·9.81·ds</c> and a vertex count, neither of which depends on speed.
        /// Hover impulse is that integral divided by speed and every Δv term is a multiple of it, so
        /// one solved geometry serves every speed a fleet might fly. A cache keyed on geometry alone
        /// therefore survives a per-hull or per-environment speed model without degenerating.</para>
        ///
        /// <para>Returns <c>this</c> when the speed already matches, when there is no geometry, or
        /// when <see cref="HoverIsMeasured"/> — a measured hover is not a function of speed and
        /// rescaling it would be a fiction.</para></summary>
        public LegImpulseProfile AtSpeed(double cruiseMs)
        {
            return WithSpeeds(cruiseMs, cruiseMs);
        }

        /// <summary>The same geometry priced at an accelerating-end and a braking-end speed, in O(1).
        ///
        /// <para>A hull does not fly a well at one speed. Live on v0.256.0, at g ≥ 0.5 over moves
        /// longer than 50 m, launches peak at 100.1 m/s (n=291) because the controller's launch
        /// handoff caps them there, while approaches peak at 420.1 m/s (n=18) because nothing caps a
        /// descent until braking starts. Hover goes as 1/v, so pricing the descent at the launch
        /// speed over-charges it about fourfold. In deep space the same approach peaks at 100.3
        /// (n=157), so a leg with no gravity on it brakes at the accelerating speed.</para>
        ///
        /// <para>The hover integral is apportioned by <see cref="Ends"/>: a stretch that both
        /// accelerates and brakes spends half its length at each speed, and a one-ended stretch
        /// spends all of it at that end's. Returns <c>this</c> when nothing would change, when there
        /// is no geometry, or when <see cref="HoverIsMeasured"/> — a measured hover describes one
        /// manoeuvre's seconds, not a speed.</para></summary>
        public LegImpulseProfile WithSpeeds(double accelerateMs, double brakeMs)
        {
            if (accelerateMs <= 0 || brakeMs <= 0) return this;
            if (AccelerateSpeedMs <= 0 || BrakeSpeedMs <= 0) return this;
            if (accelerateMs == AccelerateSpeedMs && brakeMs == BrakeSpeedMs) return this;
            if (HoverIsMeasured) return this;

            var scaled = new LegImpulseProfile
            {
                GravityImpulseIntegralPerKg = GravityImpulseIntegralPerKg,
                TurnSineSum = TurnSineSum,
                Ends = Ends,
                FsdDistanceKm = FsdDistanceKm,
                FsdEngagements = FsdEngagements,
                ThrusterStretches = ThrusterStretches,
                ThrusterDistanceKm = ThrusterDistanceKm,
                RepresentativeG = RepresentativeG,
                RepresentativeInAtmosphere = RepresentativeInAtmosphere,
                RepresentativeAirDensity = RepresentativeAirDensity,
                HasGeometry = HasGeometry,
            };
            scaled.ApplySpeeds(accelerateMs, brakeMs, TransitSpeedMs);
            return scaled;
        }

        /// <summary>Seconds the accelerating end of an in-well stretch lasts — a launch, which the
        /// flight controller's handoff caps. Measured live on v0.256.0 at g ≥ 0.5 over moves longer
        /// than 50 m: 6.8 s, median of 291. At <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> of
        /// 100 m/s that is 680 m of the stretch.</summary>
        public const double LaunchEndSeconds = 6.8;

        /// <summary>Seconds the braking end of an in-well stretch lasts — a descent, from the same
        /// window: 48.5 s, median of 18. At <see cref="FactionTuning.ThrusterApproachSpeedMs"/> of
        /// 420 m/s that is 20.4 km of the stretch.</summary>
        public const double ApproachEndSeconds = 48.5;

        /// <summary>The speed the transiting part of an in-well stretch is flown at: the world's own
        /// large-grid limit (<see cref="FactionTuning.FleetSpeedMinKmPerMin"/>, overridden at session
        /// load from SE's <c>LargeShipMaxSpeed</c>), which is what a hull crossing a well is capped
        /// by once it is neither climbing out nor settling in.
        ///
        /// <para>0 — no transit portion, charge the whole stretch at its ends' speeds — whenever the
        /// limit is unset or is not faster than <see cref="FactionTuning.ThrusterCruiseSpeedMs"/>.
        /// A cap below the speed the ends are already flown at cannot describe a transit, and
        /// applying it would raise hover rather than lower it.</para></summary>
        public static double InGravityTransitSpeedMs(FactionTuning tuning)
        {
            if (tuning == null || tuning.FleetSpeedMinKmPerMin <= 0) return 0;
            double ms = tuning.FleetSpeedMinKmPerMin * 1000.0 / 60.0;
            return ms > tuning.ThrusterCruiseSpeedMs ? ms : 0;
        }

        /// <summary>Fills the speed-dependent terms from the speed-free integrals. The one place the
        /// two speeds are turned into impulse and Δv, so <see cref="Build"/> and
        /// <see cref="WithSpeeds"/> cannot disagree about how.</summary>
        internal void ApplySpeeds(double accelerateMs, double brakeMs)
        {
            ApplySpeeds(accelerateMs, brakeMs, 0);
        }

        /// <summary>The same, with a transit speed for the part of the in-well length that is
        /// neither end. See <see cref="InGravityTransitSpeedMs"/>; 0 charges the whole stretch at
        /// its ends' speeds.</summary>
        internal void ApplySpeeds(double accelerateMs, double brakeMs, double transitMs)
        {
            // The fast brake is a gravity phenomenon, not a property of braking. Measured live on
            // v0.256.0 over moves longer than 50 m: an approach inside a well peaks at 420.1 m/s
            // (p50, n=18) while one in deep space peaks at 100.3 (n=157) -- the cruise speed. A leg
            // that never meets gravity therefore brakes at the accelerating speed.
            if (GravityImpulseIntegralPerKg <= 0) brakeMs = accelerateMs;

            AccelerateSpeedMs = accelerateMs;
            BrakeSpeedMs = brakeMs;
            TransitSpeedMs = transitMs;
            if (accelerateMs <= 0 || brakeMs <= 0) return;

            double accelShare, brakeShare, transitShare;
            Apportion(accelerateMs, brakeMs, transitMs, out accelShare, out brakeShare, out transitShare);

            DownImpulsePerKg = GravityImpulseIntegralPerKg
                             * (accelShare / accelerateMs + brakeShare / brakeMs
                                + (transitShare > 0 ? transitShare / transitMs : 0.0));

            double stretches = ThrusterStretches;
            AccelerateDeltaVMs = (Ends == ThrusterStretchEnds.BrakeOnly ? 0.0 : stretches * accelerateMs)
                               + TurnSineSum * accelerateMs * 0.5;
            BrakeDeltaVMs = (Ends == ThrusterStretchEnds.AccelerateOnly ? 0.0 : stretches * brakeMs)
                          + TurnSineSum * brakeMs * 0.5;
            ThrustDeltaVMs = AccelerateDeltaVMs + BrakeDeltaVMs;
        }

        /// <summary>How the in-well length — and so the gravity integral, which is length-weighted —
        /// divides between the accelerating end, the braking end and the transit between them.
        ///
        /// <para>The ends are manoeuvres of a measured duration, so each is
        /// <c>seconds × that end's speed</c> metres long: 680 m for a launch at 100 m/s and 20.4 km
        /// for a descent at 420. A stretch longer than the two of them spends the remainder merely
        /// crossing the well, which the world's grid limit caps and nothing else does — and that
        /// remainder is most of the traffic, so pricing it at the launch speed over-charges hover
        /// several-fold on the legs that dominate the total.</para>
        ///
        /// <para>A stretch shorter than its own ends has no transit part and is split exactly as it
        /// was before: half its length at each end, or all of it at one end's speed for a one-ended
        /// stretch. Every observed manoeuvre is that case, which is why the calibration readings are
        /// unaffected.</para></summary>
        private void Apportion(double accelerateMs, double brakeMs, double transitMs,
                               out double accelShare, out double brakeShare, out double transitShare)
        {
            if (Ends == ThrusterStretchEnds.AccelerateOnly) { accelShare = 1.0; brakeShare = 0.0; }
            else if (Ends == ThrusterStretchEnds.BrakeOnly) { accelShare = 0.0; brakeShare = 1.0; }
            else { accelShare = 0.5; brakeShare = 0.5; }
            transitShare = 0.0;

            if (transitMs <= 0) return;
            double inWellM = ThrusterDistanceKm * 1000.0;
            if (inWellM <= 0) return;

            double accelEndM = Ends == ThrusterStretchEnds.BrakeOnly ? 0.0 : LaunchEndSeconds * accelerateMs;
            double brakeEndM = Ends == ThrusterStretchEnds.AccelerateOnly ? 0.0 : ApproachEndSeconds * brakeMs;
            if (accelEndM + brakeEndM >= inWellM) return;

            accelShare = accelEndM / inWellM;
            brakeShare = brakeEndM / inWellM;
            transitShare = 1.0 - accelShare - brakeShare;
        }

        /// <summary>Replaces the hover term's derived duration with a measured one:
        /// <see cref="RepresentativeG"/> held for <paramref name="observedSeconds"/>. For a profile
        /// built <c>thrustersOnly</c> over a manoeuvre that was timed, where length over speed
        /// understates the seconds a hull that accelerates from rest, or settles onto a stop point,
        /// actually spends holding itself up. A profile that never met gravity is left alone.</summary>
        public void RetimeHover(double observedSeconds)
        {
            if (observedSeconds <= 0 || RepresentativeG <= 0) return;
            DownImpulsePerKg = RepresentativeG * ThrustPhysics.StandardGravityMs2 * observedSeconds;
            HoverIsMeasured = true;
        }

        /// <summary>Path sampling step. Fine enough that a well's boundary lands within a few km on
        /// an entry leg, coarse enough that a long deep-space segment is a handful of gravity
        /// queries rather than hundreds.</summary>
        public const double SampleStepKm = 5.0;

        /// <summary>Cap on sub-segments per polyline segment, so one very long segment cannot make
        /// the walk unbounded. A segment longer than <c>SampleStepKm × this</c> is sampled coarser.</summary>
        public const int MaxSamplesPerSegment = 256;

        /// <summary>Walks <paramref name="polylineKm"/> end to end, classifies each sampled
        /// sub-segment as inside or outside the containing well's FSD boundary, and accumulates the
        /// per-kilogram impulse and the FSD stretches.
        ///
        /// <para>The boundary is the radius at which
        /// <see cref="IPlanetCatalogue.GravityAt"/> reads <see cref="FactionTuning.FsdGravityDropoutG"/>,
        /// solved by <see cref="PlanetGeometry.GravityBoundaryRadius"/> along the radial through the
        /// first sample that resolved to that body and then reused for the rest of the walk — the
        /// same one-shell-per-body shortcut <c>PlanetRoutePlanner</c> takes, and for the same
        /// reason. The tuning value is read in whatever unit the catalogue reports: absolute g on
        /// the host, where <c>SePlanetCatalogue</c> returns SE's natural-gravity multiplier, and a
        /// surface-relative proxy under <see cref="StaticPlanetCatalogue"/>, where 1.0 means "at or
        /// inside the surface" whatever the body's real surface gravity. The two coincide on a 1 g
        /// body and diverge on any other, so an offline balance run reads a wider well than the
        /// live server does on a light moon.</para></summary>
        public static LegImpulseProfile Build(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm)
        {
            return Build(planets, tuning, polylineKm, false);
        }

        /// <summary>The same walk, with <paramref name="thrustersOnly"/> classifying every sample as
        /// thruster work at its own sampled gravity whatever the boundary says: no FSD distance, no
        /// engagement, one contiguous stretch, and a turn charged at every interior vertex.
        ///
        /// <para>For the one case where the drive is known to be absent rather than assumed present:
        /// a materialised NPC grid carries no FSD block, so the manoeuvre it actually flies is
        /// thrusters end to end. Zero gravity still costs no lift impulse and the ends still cost
        /// their Δv, so a deep-space manoeuvre under this flag differs from the default build only
        /// in where the distance is attributed.</para></summary>
        public static LegImpulseProfile Build(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm,
                                              bool thrustersOnly)
        {
            return Build(planets, tuning, polylineKm, thrustersOnly, 0, ThrusterStretchEnds.AccelerateAndBrake);
        }

        /// <summary>The same walk again, at a caller-supplied thruster speed and charging only the
        /// ends of a stretch the manoeuvre actually flew.
        ///
        /// <para>For the calibration line, which has to compare like with like: a materialised grid
        /// flies at whatever its flight controller granted it that leg, not at
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/>, and a launch never brakes while an
        /// approach that engages at cruise never accelerates. <paramref name="thrusterSpeedMs"/> at
        /// or below 0 falls back to the tuning field, so Core's own paths keep one speed.</para></summary>
        public static LegImpulseProfile Build(IPlanetCatalogue planets, FactionTuning tuning, IList<Vec3> polylineKm,
                                              bool thrustersOnly, double thrusterSpeedMs, ThrusterStretchEnds ends)
        {
            var profile = new LegImpulseProfile();
            if (tuning == null || polylineKm == null || polylineKm.Count < 2) return profile;
            profile.HasGeometry = true;

            double dropoutG = tuning.FsdGravityDropoutG;
            // An explicit speed means "price this manoeuvre at the speed it was observed to fly",
            // so it governs both ends. Only the tuning path distinguishes them.
            double accelerateMs = thrusterSpeedMs > 0 ? thrusterSpeedMs : tuning.ThrusterCruiseSpeedMs;
            double brakeMs = thrusterSpeedMs > 0 ? thrusterSpeedMs : ThrusterBrakeSpeedMs(tuning);
            double cruiseMs = accelerateMs;
            profile.Ends = ends;

            // One-entry memo for the bisected boundary: in-well samples are contiguous per body
            // along a path, so a body is solved once per leg in every case but a route threading
            // two wells alternately.
            string memoKey = null;
            double memoRadiusKm = 0;

            double inWellLengthKm = 0, inWellAtmoLengthKm = 0, inWellGravityLengthKm = 0, inWellAirLengthKm = 0;
            bool haveState = false, previousInWell = false;

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

                    double sampleG;
                    bool inWell;
                    if (thrustersOnly)
                    {
                        sampleG = planets != null ? planets.GravityAt(sample) : 0.0;
                        inWell = true;
                    }
                    else inWell = IsInWell(planets, sample, dropoutG, ref memoKey, ref memoRadiusKm, out sampleG);

                    if (inWell != previousInWell || !haveState)
                    {
                        if (inWell) profile.ThrusterStretches++;
                        else profile.FsdEngagements++;
                        previousInWell = inWell;
                        haveState = true;
                    }

                    if (inWell)
                    {
                        profile.ThrusterDistanceKm += stepKm;
                        inWellLengthKm += stepKm;
                        inWellGravityLengthKm += sampleG * stepKm;
                        if (ThrustPhysics.InAtmosphere(planets, sample)) inWellAtmoLengthKm += stepKm;
                        inWellAirLengthKm += ThrustPhysics.AirDensity(planets, sample) * stepKm;
                        profile.GravityImpulseIntegralPerKg += sampleG * ThrustPhysics.StandardGravityMs2
                                                             * (stepKm * 1000.0);
                    }
                    else
                    {
                        profile.FsdDistanceKm += stepKm;
                    }
                }
            }

            if (inWellLengthKm > 0)
            {
                profile.RepresentativeG = inWellGravityLengthKm / inWellLengthKm;
                profile.RepresentativeInAtmosphere = inWellAtmoLengthKm * 2.0 > inWellLengthKm;
                profile.RepresentativeAirDensity = inWellAirLengthKm / inWellLengthKm;
            }

            // A leg that never enters a well is still charged one accelerate/brake pair, for the
            // undock at its origin and the dock at its destination.
            if (profile.ThrusterStretches < 1) profile.ThrusterStretches = 1;

            if (cruiseMs > 0)
            {
                // A turn is a brake into the new heading and an accelerate out of it, whatever the
                // stretch's own ends were; the sine sum is the speed-free half of it.
                profile.TurnSineSum = SumTurnSines(planets, polylineKm, dropoutG,
                                                  thrustersOnly, ref memoKey, ref memoRadiusKm);
                // The transit cap describes an abstract leg crossing a well. A caller that named a
                // speed is pricing one observed manoeuvre at the speed it was seen to fly, and
                // `thrustersOnly` is the materialised-grid path under the native flight controller:
                // neither has a transit part to charge, so both keep the ends-only split.
                double transitMs = thrustersOnly || thrusterSpeedMs > 0 ? 0 : InGravityTransitSpeedMs(tuning);
                profile.ApplySpeeds(accelerateMs, brakeMs, transitMs);
            }

            return profile;
        }

        /// <summary>Δv spent changing direction at the polyline's interior vertices. A turn holding
        /// speed <c>v</c> through <c>θ</c> costs <c>2·v·sin(θ/2)</c>. Only vertices inside a well are
        /// charged: a course change under the FSD is free, which is why a deep-space clearance arc
        /// costs nothing but its endpoints. Under <paramref name="thrustersOnly"/> every vertex is
        /// charged, because nothing on that path is under the FSD.</summary>
        /// <summary>The braking-end speed the tuning describes, falling back to the accelerating
        /// one when it is unset so a host that has never seen the field prices as it did before.</summary>
        public static double ThrusterBrakeSpeedMs(FactionTuning tuning)
        {
            if (tuning == null) return 0;
            return tuning.ThrusterApproachSpeedMs > 0 ? tuning.ThrusterApproachSpeedMs : tuning.ThrusterCruiseSpeedMs;
        }

        private static double SumTurnSines(IPlanetCatalogue planets, IList<Vec3> polylineKm, double dropoutG,
                                          bool thrustersOnly,
                                          ref string memoKey, ref double memoRadiusKm)
        {
            double total = 0;
            for (int i = 1; i < polylineKm.Count - 1; i++)
            {
                Vec3 vertex = polylineKm[i];
                double ignoredG;
                if (!thrustersOnly
                    && !IsInWell(planets, vertex, dropoutG, ref memoKey, ref memoRadiusKm, out ignoredG)) continue;

                double angle = TurnAngle(polylineKm[i - 1], vertex, polylineKm[i + 1]);
                if (angle <= 0) continue;
                total += 2.0 * Math.Sin(angle * 0.5);
            }
            return total;
        }

        /// <summary>Angle between the incoming and outgoing directions at <paramref name="vertex"/>,
        /// in radians. 0 when either leg is degenerate.</summary>
        private static double TurnAngle(Vec3 previous, Vec3 vertex, Vec3 next)
        {
            double ax = vertex.X - previous.X, ay = vertex.Y - previous.Y, az = vertex.Z - previous.Z;
            double bx = next.X - vertex.X, by = next.Y - vertex.Y, bz = next.Z - vertex.Z;
            double aLen = Math.Sqrt(ax * ax + ay * ay + az * az);
            double bLen = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (aLen < 1e-9 || bLen < 1e-9) return 0;

            double cos = (ax * bx + ay * by + az * bz) / (aLen * bLen);
            if (cos > 1) cos = 1; else if (cos < -1) cos = -1;
            return Math.Acos(cos);
        }

        /// <summary>True when <paramref name="posKm"/> sits inside the FSD dropout boundary of the
        /// body whose well contains it, so the hull there is on thrusters. Also reports the gravity
        /// sample, which is 0 outside every well. Falls back to comparing the sample against
        /// <paramref name="dropoutG"/> directly when the body's radii will not support a bisection.</summary>
        private static bool IsInWell(IPlanetCatalogue planets, Vec3 posKm, double dropoutG,
                                     ref string memoKey, ref double memoRadiusKm, out double gravity)
        {
            gravity = 0;
            if (planets == null || dropoutG <= 0) return false;

            PlanetRecord well = PlanetGeometry.GravityWellPlanet(planets, posKm);
            if (well == null || well.GravityRadius <= 0) return false;

            double distanceKm = posKm.DistanceTo(well.Center);
            if (distanceKm >= well.GravityRadius) return false; // the nearest-by-centre fallback, not a container

            gravity = planets.GravityAt(posKm);

            string key = well.Id ?? well.Name ?? well.Center.ToString();
            if (!string.Equals(key, memoKey, StringComparison.Ordinal))
            {
                memoKey = key;
                memoRadiusKm = PlanetGeometry.GravityBoundaryRadius(planets, well, dropoutG,
                                                                    PlanetGeometry.RadialUnit(well, posKm));
            }
            if (memoRadiusKm <= 0) return gravity > dropoutG;
            return distanceKm < memoRadiusKm;
        }

        private static Vec3 Lerp(Vec3 a, Vec3 b, double t)
        {
            return new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }
    }
}
