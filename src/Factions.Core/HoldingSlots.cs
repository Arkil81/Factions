using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Where a queued arrival waits (docs/traffic-control.md, T4): a holding slot around its station,
    /// spaced by what the ship costs against what its neighbourhood may spend, so a player who flies
    /// out to the holding ships sees about as many as the budget allows at once and no more.
    ///
    /// <para><b>Spacing</b> is the distance at which one ship per <c>π·ViewRadiusKm²</c> of holding
    /// area adds up to the share: <c>ViewRadiusKm · sqrt(π · cost / share)</c>, clamped. Cheap ships
    /// and a generous share pack close; a costly ship on a tight share stands well apart.</para>
    ///
    /// <para><b>Placement</b> starts on the ship's own approach line, <see cref="InnerKm"/> out from the
    /// station, and steps outward and sideways on a lattice until it finds a point that is
    /// <c>spacing</c> clear of every slot already taken. Near a planet a slot is never lower than the
    /// station plus <see cref="PlanetMarginKm"/>, nor within <see cref="PlanetMarginKm"/> of the
    /// surface radius, and the straight run from slot to station must clear the body. A station
    /// outside every gravity well has no planet test.</para>
    /// </summary>
    public static class HoldingSlots
    {
        public const double ViewRadiusKm = 5.0;
        public const double MinSpacingKm = 0.5;
        public const double MaxSpacingKm = 5.0;
        public const double InnerKm = 3.0;
        public const double PlanetMarginKm = 1.0;
        private const int Rings = 12;
        private const int Lateral = 2;

        public static double SpacingKm(double costMs, double shareMs)
        {
            double share = Math.Max(0.1, shareMs);
            double d = ViewRadiusKm * Math.Sqrt(Math.PI * Math.Max(0.0, costMs) / share);
            return Math.Max(MinSpacingKm, Math.Min(MaxSpacingKm, d));
        }

        /// <summary>The first free slot, or null when none on the lattice qualifies.</summary>
        public static Vec3? Choose(Vec3 stationKm, Vec3 fromKm, double spacingKm, IList<Vec3> taken, IPlanetCatalogue planets)
        {
            return Choose(stationKm, fromKm, spacingKm, taken, planets, InnerKm);
        }

        /// <param name="innerKm">How far out the first ring sits. The runtime puts it just past the
        /// radius a player at the station sees, so holding ships wait unseen unless someone flies out
        /// to them, and do not take the share the station's own queue needs.</param>
        public static Vec3? Choose(Vec3 stationKm, Vec3 fromKm, double spacingKm, IList<Vec3> taken, IPlanetCatalogue planets,
                                   double innerKm)
        {
            Vec3 dir = Normalise(Sub(fromKm, stationKm), new Vec3(1, 0, 0));
            Vec3 u = Normalise(Cross(dir, Math.Abs(dir.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0)), new Vec3(0, 1, 0));
            Vec3 v = Cross(dir, u);
            PlanetRecord body = planets != null ? PlanetGeometry.GravityWellPlanet(planets, stationKm) : null;
            if (body != null && body.Center.DistanceTo(stationKm) > body.GravityRadius) body = null;

            for (int k = 0; k < Rings; k++)
            {
                double r = innerKm + k * spacingKm;
                for (int ring = 0; ring <= Lateral; ring++)
                    for (int a = -ring; a <= ring; a++)
                        for (int b = -ring; b <= ring; b++)
                        {
                            if (Math.Max(Math.Abs(a), Math.Abs(b)) != ring) continue;
                            Vec3 p = Add(Add(Add(stationKm, Scale(dir, r)), Scale(u, a * spacingKm)), Scale(v, b * spacingKm));
                            if (!Clear(p, spacingKm, taken)) continue;
                            if (body != null && !ClearOfPlanet(p, stationKm, body)) continue;
                            return p;
                        }
            }
            return null;
        }

        /// <summary>Moves an unmaterialised fleet's leg to start at <paramref name="slotKm"/> now,
        /// keeping its target: a straight leg from the slot, timed on its own zone profile. Held with
        /// <see cref="FactionsLoadActuator.Freeze"/> it stays at the slot; released, it flies on.</summary>
        public static PendingFleetAction Reorigin(PendingFleetAction fleet, Vec3 slotKm, WorldModel world,
                                                  FactionTuning tuning, DateTime now)
        {
            fleet.OriginPosKm = slotKm;
            fleet.LegZones = world != null ? world.ZonesFor(slotKm, fleet.TargetPosKm) : fleet.LegZones;
            double mins = world != null ? tuning.TransitTimeMins(fleet.LegZones) : tuning.TransitTimeMins(slotKm.DistanceTo(fleet.TargetPosKm));
            fleet.DepartedUtc = now;
            fleet.EstimatedArrivalUtc = now.AddMinutes(Math.Max(0.0, mins));
            fleet.PathNodeIds = null;
            fleet.LastResolvedSegment = -1;
            fleet.RouteDistanceKm = 0;
            fleet.RouteCoveredKm = 0;
            fleet.RouteDepartedUtc = default(DateTime);
            return fleet;
        }

        private static bool Clear(Vec3 p, double spacingKm, IList<Vec3> taken)
        {
            if (taken == null) return true;
            for (int i = 0; i < taken.Count; i++)
                if (taken[i].DistanceTo(p) < spacingKm) return false;
            return true;
        }

        private static bool ClearOfPlanet(Vec3 p, Vec3 stationKm, PlanetRecord body)
        {
            double floor = Math.Max(body.Center.DistanceTo(stationKm), body.SurfaceRadius) + PlanetMarginKm;
            if (body.Center.DistanceTo(p) < floor) return false;
            return PlanetRoutePlanner.SegmentClear(p, stationKm, body, 0.0);
        }

        private static Vec3 Normalise(Vec3 v, Vec3 fallback)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return len < 1e-9 ? fallback : new Vec3(v.X / len, v.Y / len, v.Z / len);
        }

        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        private static Vec3 Scale(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
