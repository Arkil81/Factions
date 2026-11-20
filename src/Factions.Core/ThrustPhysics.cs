using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Pure, SE-free thrust/atmosphere math shared by the prefab catalogue's route-feasibility
    /// checks. <see cref="ForceMultiplier"/> is a direct port of the real SE thrust-effectiveness
    /// curve (decompiled <c>Sandbox.Game.EntityComponents.MyThrusterBlockThrustComponent
    /// .CalculateForceMultiplier</c>), so this class reproduces vanilla's own Atmospheric/Hydrogen/
    /// Ion behaviour exactly while staying correct for arbitrary modded <see cref="ThrustBand"/>
    /// curves scanned off real block definitions (see <c>ThrustStatsHelper</c> in the host project).
    /// </summary>
    public static class ThrustPhysics
    {
        public const double StandardGravityMs2 = 9.81;

        /// <summary>Direct port of <c>MyThrusterBlockThrustComponent.CalculateForceMultiplier</c>.
        /// <paramref name="airDensity"/> is SE's planetary-influence value, which SE reads as the air
        /// density at the thruster (<c>MyPlanet.GetAirDensity</c>; 0 in vacuum, 1 at a planet's
        /// average radius) — see <see cref="AirDensity(PlanetRecord, double)"/>. It is not gravity:
        /// 3.65 km up on a 30 km body with a 4.86 km atmosphere, gravity is 0.6 g and the air 0.25,
        /// which leaves an atmospheric thruster at zero.</summary>
        public static float ForceMultiplier(ThrustBand band, double airDensity, bool inAtmosphere)
        {
            if (band.NeedsAtmosphereForInfluence && !inAtmosphere)
                return band.EffectivenessAtMinInfluence;

            if (band.MaxPlanetaryInfluence == band.MinPlanetaryInfluence)
                return 1f;

            double t = (airDensity - band.MinPlanetaryInfluence)
                     / (band.MaxPlanetaryInfluence - band.MinPlanetaryInfluence);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return (float)(band.EffectivenessAtMinInfluence
                + (band.EffectivenessAtMaxInfluence - band.EffectivenessAtMinInfluence) * t);
        }

        /// <summary>Sum(ForceMagnitudeN * ForceMultiplier(...)) across one direction's bands.
        /// Direction-agnostic — the caller picks which <see cref="ThrustDirection"/>'s band list
        /// to pass in (e.g. the <c>Down</c> bucket for lift, <c>Backward</c> for cruise).</summary>
        public static double ForceN(IReadOnlyList<ThrustBand> bandsForDirection, double airDensity,
            bool inAtmosphere)
        {
            if (bandsForDirection == null) return 0;
            double total = 0;
            for (int i = 0; i < bandsForDirection.Count; i++)
            {
                ThrustBand band = bandsForDirection[i];
                total += band.ForceMagnitudeN * ForceMultiplier(band, airDensity, inAtmosphere);
            }
            return total;
        }

        /// <summary>Dry (no-cargo) thrust-to-weight ratio along the <c>Down</c> direction (lift),
        /// weight from <paramref name="gravityG"/> and thrust effectiveness from
        /// <paramref name="airDensity"/>. Missing the <c>Down</c> key is treated as zero force
        /// available, same as an empty list. Zero-weight edge case (deep space): +Infinity when any
        /// real lift force exists (nothing to fight — trivially feasible), 0 when the prefab has no
        /// working Down-facing thrust at all (never feasible, anywhere).</summary>
        public static double DryTwr(IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
            double dryMassKg, double gravityG, double airDensity, bool inAtmosphere)
        {
            return DryTwr(bandsByDirection, dryMassKg, gravityG, airDensity, inAtmosphere, ThrustDirection.Down);
        }

        /// <summary>Dry thrust-to-weight along <paramref name="liftBand"/>, the bucket that pushes this
        /// hull toward its own up (<see cref="HullFrame.LiftBand"/>). <c>Down</c> is the belly-down
        /// hull; a hull whose author put the cockpit on another axis flies, and is held up, on a
        /// different bank.</summary>
        public static double DryTwr(IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
            double dryMassKg, double gravityG, double airDensity, bool inAtmosphere, ThrustDirection liftBand)
        {
            IReadOnlyList<ThrustBand> liftBands;
            if (bandsByDirection == null || !bandsByDirection.TryGetValue(liftBand, out liftBands))
                liftBands = null;

            double forceN = ForceN(liftBands, airDensity, inAtmosphere);
            double weightN = dryMassKg * gravityG * StandardGravityMs2;

            if (weightN <= 0)
                return forceN > 0 ? double.PositiveInfinity : 0;

            return forceN / weightN;
        }

        /// <summary>SE's air density <paramref name="radiusKm"/> from <paramref name="body"/>'s
        /// centre: <c>MyPlanet.GetAirDensity</c>, falling linearly from 1 at the average radius
        /// (<see cref="PlanetRecord.SurfaceRadius"/>) to 0 at <see cref="PlanetRecord.AtmosphereRadius"/>.
        /// The generator's own density factor is not catalogued and is taken as 1.</summary>
        public static double AirDensity(PlanetRecord body, double radiusKm)
        {
            if (body == null || !body.HasAtmosphere) return 0;
            double depthKm = body.AtmosphereRadius - body.SurfaceRadius;
            if (depthKm <= 0) return 0;
            double density = 1.0 - (radiusKm - body.SurfaceRadius) / depthKm;
            return density < 0 ? 0 : (density > 1 ? 1 : density);
        }

        /// <summary><see cref="AirDensity(PlanetRecord, double)"/> at <paramref name="posKm"/>, against
        /// the same nearest body <see cref="InAtmosphere"/> reads.</summary>
        public static double AirDensity(IPlanetCatalogue planets, Vec3 posKm)
        {
            PlanetRecord nearest = NearestBody(planets, posKm);
            return nearest == null ? 0 : AirDensity(nearest, posKm.DistanceTo(nearest.Center));
        }

        /// <summary>Maximum additional cargo mass (kg), on top of <paramref name="dryMassKg"/>, a
        /// prefab can carry at <paramref name="gravityG"/>/<paramref name="airDensity"/> while still
        /// clearing <paramref name="minDryTwr"/> along the <c>Down</c> direction — the loaded-cargo
        /// counterpart to <see cref="DryTwr(IReadOnlyDictionary{ThrustDirection, IReadOnlyList{ThrustBand}}, double, double, double, bool)"/>'s
        /// empty-hull check. <paramref name="gravityFloorG"/> (pass <c>tuning.OrbitGravityMin</c>)
        /// floors the gravity the weight term uses: real deep-space gravity commonly samples as
        /// exactly 0 in this sim (far outside every planet's gravity radius), and DryTwr's own
        /// +Infinity-on-zero-weight shortcut is correct for a fixed-mass binary gate but would give a
        /// mass-growing cargo cap no teeth at all on a genuinely space-only leg — flooring treats deep
        /// space as at least orbit-grade gravity for cargo-capacity purposes specifically. Thrust is
        /// read at the real air density, never floored. Returns +Infinity ("don't constrain") when
        /// the prefab is unscanned (<paramref name="dryMassKg"/> &lt;= 0) or the gate is disabled
        /// (<paramref name="minDryTwr"/> &lt;= 0). Returns 0 (never negative) if the dry hull alone
        /// can't already clear the floor here — shouldn't happen for a route whose dry-TWR gate
        /// already accepted this prefab, but defensive.</summary>
        public static double MaxCargoMassKg(IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
            double dryMassKg, double gravityG, double airDensity, bool inAtmosphere, double minDryTwr, double gravityFloorG)
        {
            return MaxCargoMassKg(bandsByDirection, dryMassKg, gravityG, airDensity, inAtmosphere,
                                  minDryTwr, gravityFloorG, ThrustDirection.Down);
        }

        /// <summary>As above, along <paramref name="liftBand"/> — the bucket that pushes this hull
        /// toward its own up (<see cref="HullFrame.LiftBand"/>).</summary>
        public static double MaxCargoMassKg(IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
            double dryMassKg, double gravityG, double airDensity, bool inAtmosphere, double minDryTwr, double gravityFloorG,
            ThrustDirection liftBand)
        {
            if (dryMassKg <= 0 || minDryTwr <= 0) return double.PositiveInfinity;

            double effectiveG = gravityG > gravityFloorG ? gravityG : gravityFloorG;
            if (effectiveG <= 0) return double.PositiveInfinity; // no floor configured and truly zero gravity — nothing to weigh cargo against

            IReadOnlyList<ThrustBand> liftBands;
            if (bandsByDirection == null || !bandsByDirection.TryGetValue(liftBand, out liftBands))
                liftBands = null;
            double forceN = ForceN(liftBands, airDensity, inAtmosphere);

            double weightPerKgN = effectiveG * StandardGravityMs2;
            double maxTotalMassKg = forceN / (minDryTwr * weightPerKgN);
            double maxCargoMassKg = maxTotalMassKg - dryMassKg;
            return maxCargoMassKg > 0 ? maxCargoMassKg : 0;
        }

        /// <summary>True when <paramref name="posKm"/> is inside the atmosphere radius of its
        /// nearest planet (and that planet actually has an atmosphere). Reads
        /// <see cref="IPlanetCatalogue.Planets"/> directly — no new interface member needed, since
        /// every <see cref="PlanetRecord"/> already carries <c>Center</c>/<c>AtmosphereRadius</c>/
        /// <c>HasAtmosphere</c> on both the host and offline-sim implementations.</summary>
        public static bool InAtmosphere(IPlanetCatalogue planets, Vec3 posKm)
        {
            PlanetRecord atmosphere = AtmosphereAt(planets, posKm);
            return atmosphere != null;
        }

        /// <summary>The body whose atmosphere <paramref name="posKm"/> is in, by the same nearest-body
        /// rule as <see cref="InAtmosphere"/>; null in vacuum.</summary>
        public static PlanetRecord AtmosphereAt(IPlanetCatalogue planets, Vec3 posKm)
        {
            PlanetRecord nearest = NearestBody(planets, posKm);
            if (nearest == null || !nearest.HasAtmosphere) return null;
            return posKm.DistanceTo(nearest.Center) <= nearest.AtmosphereRadius ? nearest : null;
        }

        private static PlanetRecord NearestBody(IPlanetCatalogue planets, Vec3 posKm)
        {
            if (planets == null) return null;

            PlanetRecord nearest = null;
            double nearestDist = double.MaxValue;
            foreach (PlanetRecord p in planets.Planets)
            {
                double dist = posKm.DistanceTo(p.Center);
                if (dist < nearestDist) { nearest = p; nearestDist = dist; }
            }
            return nearest;
        }
    }
}
