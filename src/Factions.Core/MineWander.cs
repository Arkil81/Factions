using System;

namespace Factions.Core
{
    /// <summary>
    /// Where a Mine fleet is while it works its deposit. The dwell is not spent parked on the node:
    /// the ship heads out to a ring around it and flies random legs on that ring until the dwell
    /// ends, then leaves for its delivery stop from wherever it is. The ring sits outside the node's
    /// visual range, so a miner is met out in the field, not at the station.
    ///
    /// <para>The path is a pure function of the fleet's dwell leg (its actor, deposit, origin node,
    /// hull and departure time, its origin and target positions, and
    /// <see cref="PendingFleetAction.MineWanderUp"/>), so it needs no state of its own and reads the
    /// same after a restart. <see cref="PendingFleetAction.FleetId"/> is deliberately not an input: it
    /// is minted from <see cref="string.GetHashCode"/>, which differs between processes on .NET Core.
    /// Two miners of one faction leaving one deposit in the same tick on the same hull fly the same
    /// ring. Waypoint <c>i</c> lies on a ring of radius
    /// <see cref="RingMinKm"/>..<see cref="RingMaxKm"/> at angle <c>i·step + jitter(i)</c>, with a
    /// small offset off the ring's plane. The step and jitter bound the angle between consecutive
    /// waypoints to [<see cref="StepDeg"/> - 2·<see cref="StepJitterDeg"/>,
    /// <see cref="StepDeg"/> + 2·<see cref="StepJitterDeg"/>], which keeps every ring leg at least
    /// <see cref="MinLegKm"/> long and never closer than <see cref="ClearanceKm"/> to the deposit.
    /// Only the outbound leg, from the deposit to waypoint 0, crosses that clearance.</para>
    ///
    /// <para>Deep space: the ring's plane has a random tilt and its waypoints sit up to
    /// <see cref="SpaceTiltKm"/> either side of it. In gravity the plane is tangent to the planet at
    /// the deposit and every waypoint is lifted <see cref="GravityLiftKm"/> or more above it, so a
    /// ring never runs below the node it circles.</para>
    ///
    /// <para>Abstract time: the outbound leg takes its own distance at
    /// <see cref="FactionTuning.FleetSpeedKmPerMin"/>, and every ring leg takes
    /// <see cref="NominalLegKm"/> at that speed whatever its real length, so the current leg is found
    /// in O(1) however long the dwell.</para>
    /// </summary>
    public static class MineWander
    {
        /// <summary>No ring leg comes closer to the deposit than this.</summary>
        public const double ClearanceKm = 20.0;

        /// <summary>No ring leg is shorter than this.</summary>
        public const double MinLegKm = 10.0;

        public const double RingMinKm = 24.0;
        public const double RingMaxKm = 28.0;

        /// <summary>Mean angle between consecutive waypoints, degrees.</summary>
        public const double StepDeg = 45.0;

        /// <summary>Per-waypoint angular jitter, ± degrees.</summary>
        public const double StepJitterDeg = 8.0;

        /// <summary>Deep space: half-width of the off-plane band the waypoints sit in.</summary>
        public const double SpaceTiltKm = 3.0;

        /// <summary>Gravity: the least height of a waypoint above the deposit's tangent plane, and
        /// how much higher one may sit.</summary>
        public const double GravityLiftKm = 2.0;
        public const double GravityLiftJitterKm = 2.0;

        /// <summary>The distance every ring leg is timed as in abstract flight.</summary>
        public const double NominalLegKm = 20.0;

        /// <summary>A materialised miner this close to its waypoint turns for the next one, so it
        /// flies the ring instead of stopping at every corner.</summary>
        public const double ArriveKm = 1.5;

        /// <summary>Whether <paramref name="fleet"/> is on its mining dwell and therefore wandering.</summary>
        public static bool Applies(PendingFleetAction fleet)
        {
            return fleet.Kind == ActionKind.Mine && fleet.CurrentStopKind == FleetStopKind.Mine && !fleet.Resolved;
        }

        /// <summary>The up axis to store on a dwell leg starting at <paramref name="depositKm"/>:
        /// the planet's surface normal when the deposit is in gravity, null in deep space.</summary>
        public static Vec3? UpAt(IPlanetCatalogue planets, Vec3 depositKm)
        {
            if (planets == null || planets.GravityAt(depositKm) <= 0) return null;
            Vec3 n = Normalize(planets.SurfaceNormal(depositKm));
            return Length(n) > 0 ? n : (Vec3?)null;
        }

        /// <summary>The ring <paramref name="fleet"/>'s dwell leg flies, fixed for the leg.</summary>
        public struct Ring
        {
            public Vec3 Center;
            public Vec3 E1, E2, N;
            public double RadiusKm;
            public bool InGravity;
            public uint Seed;
            public double PhaseRad;
            public double StepRad;   // signed: the direction the ring is flown
        }

        public static Ring RingOf(PendingFleetAction fleet)
        {
            uint seed = Seed(fleet.ActorTag, fleet.TargetNodeId, fleet.OriginNodeId, fleet.ShipPrefabId,
                             fleet.DepartedUtc.Ticks);
            var r = new Ring { Center = fleet.TargetPosKm, Seed = seed };
            Vec3 n;
            if (fleet.MineWanderUp.HasValue && Length(fleet.MineWanderUp.Value) > 0)
            {
                n = Normalize(fleet.MineWanderUp.Value);
                r.InGravity = true;
            }
            else
            {
                // A uniformly random plane normal.
                double z = 2 * Unit(seed, 0x51ED) - 1;
                double a = 2 * Math.PI * Unit(seed, 0x0A11);
                double s = Math.Sqrt(Math.Max(0, 1 - z * z));
                n = new Vec3(s * Math.Cos(a), s * Math.Sin(a), z);
            }
            Vec3 any = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            r.N = n;
            r.E1 = Normalize(Cross(n, any));
            r.E2 = Cross(n, r.E1);
            r.RadiusKm = RingMinKm + (RingMaxKm - RingMinKm) * Unit(seed, 0x0BAD);
            r.PhaseRad = 2 * Math.PI * Unit(seed, 0xFACE);
            r.StepRad = StepDeg * Math.PI / 180.0 * (Unit(seed, 0xD1CE) < 0.5 ? -1 : 1);
            return r;
        }

        /// <summary>Waypoint <paramref name="i"/> (0 = where the outbound leg goes).</summary>
        public static Vec3 Waypoint(Ring r, int i)
        {
            uint k = (uint)i * 2654435761u;
            double jitter = (2 * Unit(r.Seed ^ k, 0x7177) - 1) * StepJitterDeg * Math.PI / 180.0;
            double angle = r.PhaseRad + i * r.StepRad + jitter;
            double off = r.InGravity
                ? GravityLiftKm + GravityLiftJitterKm * Unit(r.Seed ^ k, 0x1F7)
                : (2 * Unit(r.Seed ^ k, 0x7117) - 1) * SpaceTiltKm;
            double c = Math.Cos(angle) * r.RadiusKm, s = Math.Sin(angle) * r.RadiusKm;
            return new Vec3(
                r.Center.X + r.E1.X * c + r.E2.X * s + r.N.X * off,
                r.Center.Y + r.E1.Y * c + r.E2.Y * s + r.N.Y * off,
                r.Center.Z + r.E1.Z * c + r.E2.Z * s + r.N.Z * off);
        }

        /// <summary>The leg the abstract fleet is on at <paramref name="utcNow"/>: 0 is the outbound
        /// leg to waypoint 0, leg <c>L ≥ 1</c> flies waypoint <c>L-1</c> to waypoint <c>L</c>. So
        /// the leg's number is also the waypoint it is flying to. <paramref name="fraction"/> is how
        /// far along it the fleet is.</summary>
        public static int LegAt(PendingFleetAction fleet, Ring r, DateTime utcNow, FactionTuning tuning,
                                out double fraction)
        {
            fraction = 0;
            double kmPerSec = tuning != null ? tuning.FleetSpeedKmPerMin / 60.0 : 0;
            if (kmPerSec <= 0) return 0;
            double elapsed = (utcNow - fleet.DepartedUtc).TotalSeconds;
            if (elapsed <= 0) return 0;
            double outbound = fleet.OriginPosKm.DistanceTo(Waypoint(r, 0)) / kmPerSec;
            if (elapsed < outbound)
            {
                fraction = outbound > 0 ? elapsed / outbound : 1;
                return 0;
            }
            double legSec = NominalLegKm / kmPerSec;
            double legs = (elapsed - outbound) / legSec;
            if (legs >= int.MaxValue - 1) legs = int.MaxValue - 2;
            int whole = (int)Math.Floor(legs);
            fraction = legs - whole;
            return whole + 1;
        }

        /// <summary>The abstract position of a wandering fleet at <paramref name="utcNow"/>.</summary>
        public static Vec3 PositionAt(PendingFleetAction fleet, DateTime utcNow, FactionTuning tuning)
        {
            Ring r = RingOf(fleet);
            double t;
            int leg = LegAt(fleet, r, utcNow, tuning, out t);
            if (tuning == null || tuning.FleetSpeedKmPerMin <= 0) return fleet.OriginPosKm;
            Vec3 from = leg == 0 ? fleet.OriginPosKm : Waypoint(r, leg - 1);
            Vec3 to = Waypoint(r, leg);
            return new Vec3(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, from.Z + (to.Z - from.Z) * t);
        }

        /// <summary>FNV-1a over the leg's identifying strings and its departure ticks: a hash that is
        /// the same in every process, which <see cref="string.GetHashCode"/> is not.</summary>
        internal static uint Seed(string actor, string deposit, string origin, string prefab, long ticks)
        {
            uint h = 2166136261u;
            h = Fnv(h, actor);
            h = Fnv(h, deposit);
            h = Fnv(h, origin);
            h = Fnv(h, prefab);
            for (int i = 0; i < 8; i++) { h ^= (uint)((ticks >> (8 * i)) & 0xFF); h *= 16777619u; }
            return h;
        }

        private static uint Fnv(uint h, string s)
        {
            if (s != null)
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            h ^= 0xFF; h *= 16777619u;   // separator, so "AB"+"C" and "A"+"BC" differ
            return h;
        }

        /// <summary>A uniform [0,1) draw from <paramref name="seed"/> and a per-use salt.</summary>
        private static double Unit(uint seed, uint salt)
        {
            uint x = seed ^ (salt * 0x9E3779B9u);
            x ^= x >> 16; x *= 0x7FEB352Du;
            x ^= x >> 15; x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x / 4294967296.0;
        }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }

        private static Vec3 Normalize(Vec3 v)
        {
            double l = Length(v);
            return l > 0 ? new Vec3(v.X / l, v.Y / l, v.Z / l) : new Vec3(0, 0, 0);
        }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
