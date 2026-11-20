using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Placement and motion of bench phantom players (<c>factions bench phantom</c>): stand-ins the
    /// host counts as players so per-player load can be measured at n players. Everything here is
    /// in kilometres and metres per second, and SE-free; the host owns the identities, hulls and
    /// characters.
    /// </summary>
    public static class PhantomPlan
    {
        /// <summary>Phantom steam ids are this plus a serial. Real Steam ids start 0x0110..., so the
        /// range cannot meet one.</summary>
        public const ulong SteamIdBase = 0x0FAC000000000000UL;
        private const ulong SteamIdMask = 0xFFFF000000000000UL;

        /// <summary>Every phantom identity's display name starts with this, which is also how a boot
        /// finds the ones a save kept.</summary>
        public const string IdentityPrefix = "phantom-";

        public const int MaxPhantoms = 32;
        public const double DefaultStandoffKm = 3.0;

        /// <summary>A parked body further than this from its anchor is put back.</summary>
        public const double HoldKm = 0.1;

        /// <summary>A parked body moving faster than this is stopped; slower, it is left to sleep.</summary>
        public const double HoldSpeedMs = 0.5;

        public static bool IsPhantomSteamId(ulong steamId)
        {
            return (steamId & SteamIdMask) == SteamIdBase;
        }

        /// <summary>The owned nodes, in id order: where materialisation happens, and a stable order
        /// for a seed to pick from.</summary>
        public static List<SpatialNode> Candidates(IEnumerable<SpatialNode> nodes)
        {
            var result = new List<SpatialNode>();
            foreach (SpatialNode n in nodes)
                if (n != null && !string.IsNullOrEmpty(n.OwnerFactionTag)) result.Add(n);
            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return result;
        }

        /// <summary>The candidates within <paramref name="radiusKm"/> of <paramref name="centreKm"/>, in the
        /// same order: confines a patrol to one area (<c>near=</c>/<c>within=</c>) instead of the whole
        /// world, whose legs run hundreds of kilometres.</summary>
        public static List<SpatialNode> Within(List<SpatialNode> candidates, Vec3 centreKm, double radiusKm)
        {
            var result = new List<SpatialNode>();
            foreach (SpatialNode n in candidates)
                if (n.Position.DistanceTo(centreKm) <= radiusKm) result.Add(n);
            return result;
        }

        /// <summary><paramref name="count"/> nodes from a seeded shuffle of the candidates, all
        /// distinct until the candidates run out, then repeating the shuffle.</summary>
        public static List<SpatialNode> Spread(List<SpatialNode> candidates, int count, int seed)
        {
            var result = new List<SpatialNode>(count);
            if (candidates.Count == 0) return result;
            List<SpatialNode> order = Shuffled(candidates, new Random(seed));
            for (int i = 0; i < count; i++) result.Add(order[i % order.Count]);
            return result;
        }

        /// <summary>A patrol route of <paramref name="hops"/> nodes, never the same node twice in a
        /// row when there are two to choose from, and with three or more, not the loop back from
        /// the last node to the first either.</summary>
        public static List<SpatialNode> Route(List<SpatialNode> candidates, int hops, Random rng)
        {
            var route = new List<SpatialNode>(hops);
            if (candidates.Count == 0) return route;
            for (int i = 0; i < hops; i++)
            {
                SpatialNode pick = candidates[rng.Next(candidates.Count)];
                if (candidates.Count > 1)
                {
                    SpatialNode prev = route.Count > 0 ? route[route.Count - 1] : null;
                    SpatialNode first = candidates.Count > 2 && i == hops - 1 && route.Count > 0 ? route[0] : null;
                    while (pick == prev || pick == first)
                        pick = candidates[rng.Next(candidates.Count)];
                }
                route.Add(pick);
            }
            return route;
        }

        /// <summary>The point a phantom holds near a node: <paramref name="standoffKm"/> out, so it
        /// is well inside the observation radius but clear of the station. Straight up from the
        /// planet when the node has one under it, so the point is never underground; otherwise in
        /// a random direction.</summary>
        public static Vec3 Standoff(Vec3 nodeKm, Vec3? planetCentreKm, double standoffKm, Random rng)
        {
            Vec3 dir = default(Vec3);
            if (planetCentreKm.HasValue) dir = Unit(Sub(nodeKm, planetCentreKm.Value));
            if (Length(dir) < 0.5) dir = RandomUnit(rng);
            return Add(nodeKm, Scale(dir, standoffKm));
        }

        /// <summary>What to do with a parked body once a second: put it back when it has wandered
        /// past <see cref="HoldKm"/>, stop it when it is moving, otherwise leave it to sleep.</summary>
        public static void Hold(Vec3 bodyKm, Vec3 anchorKm, double speedMs, out bool teleport, out bool stop)
        {
            teleport = bodyKm.DistanceTo(anchorKm) > HoldKm;
            stop = !teleport && speedMs > HoldSpeedMs;
        }

        internal static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        internal static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        internal static Vec3 Scale(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        internal static double Length(Vec3 a) { return Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z); }

        internal static Vec3 Unit(Vec3 a)
        {
            double l = Length(a);
            return l > 1e-12 ? Scale(a, 1.0 / l) : default(Vec3);
        }

        private static Vec3 RandomUnit(Random rng)
        {
            while (true)
            {
                var v = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
                double l = Length(v);
                if (l > 0.1 && l <= 1.0) return Scale(v, 1.0 / l);
            }
        }

        private static List<SpatialNode> Shuffled(List<SpatialNode> items, Random rng)
        {
            var copy = new List<SpatialNode>(items);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                SpatialNode t = copy[i]; copy[i] = copy[j]; copy[j] = t;
            }
            return copy;
        }
    }

    /// <summary>
    /// One phantom's patrol: a loop of waypoints flown at a fixed speed, advanced once a second. A
    /// body that makes no progress for <see cref="StuckSeconds"/> — a planet or a station in the
    /// straight line — is put on the waypoint instead.
    /// </summary>
    public sealed class PhantomPatrol
    {
        public const int StuckSeconds = 10;
        private const double ArriveFloorKm = 0.2;

        public readonly List<Vec3> WaypointsKm;
        public readonly double SpeedMs;
        public int Next { get; private set; }
        public int Laps { get; private set; }

        private double _bestKm = double.MaxValue;
        private int _stalledSeconds;

        public struct Step
        {
            public Vec3 VelocityMs;
            public bool Teleport;
            public Vec3 TeleportToKm;
            public bool Advanced;
        }

        public PhantomPatrol(List<Vec3> waypointsKm, double speedMs)
        {
            if (waypointsKm == null || waypointsKm.Count == 0) throw new ArgumentException("a patrol needs a waypoint");
            WaypointsKm = waypointsKm;
            SpeedMs = speedMs;
        }

        public Vec3 Target { get { return WaypointsKm[Next]; } }

        /// <summary>Close enough to count as there: a second and a half of flight, at least 200 m.</summary>
        private double ArriveKm { get { return Math.Max(ArriveFloorKm, SpeedMs * 1.5 / 1000.0); } }

        /// <summary>A body that moves under physics: the velocity to give it for the next second.</summary>
        public Step Advance(Vec3 bodyKm)
        {
            var step = new Step();
            if (bodyKm.DistanceTo(Target) <= ArriveKm)
            {
                MoveOn();
                step.Advanced = true;
            }

            double d = bodyKm.DistanceTo(Target);
            double progressKm = Math.Max(0.01, SpeedMs * 0.25 / 1000.0);
            if (d < _bestKm - progressKm) { _bestKm = d; _stalledSeconds = 0; }
            else if (++_stalledSeconds >= StuckSeconds)
            {
                step.Teleport = true;
                step.TeleportToKm = Target;
                _bestKm = double.MaxValue;
                _stalledSeconds = 0;
                return step;
            }
            step.VelocityMs = PhantomPlan.Scale(PhantomPlan.Unit(PhantomPlan.Sub(Target, bodyKm)), SpeedMs);
            return step;
        }

        /// <summary>No body at all: the presence point itself moves, <paramref name="seconds"/> of
        /// flight toward the waypoint, moving on when it arrives.</summary>
        public Vec3 AdvanceVirtual(Vec3 posKm, double seconds)
        {
            double travelKm = SpeedMs * seconds / 1000.0;
            double d = posKm.DistanceTo(Target);
            if (d <= travelKm)
            {
                Vec3 at = Target;
                MoveOn();
                return at;
            }
            return PhantomPlan.Add(posKm, PhantomPlan.Scale(PhantomPlan.Unit(PhantomPlan.Sub(Target, posKm)), travelKm));
        }

        private void MoveOn()
        {
            Next = (Next + 1) % WaypointsKm.Count;
            if (Next == 0) Laps++;
            _bestKm = double.MaxValue;
            _stalledSeconds = 0;
        }
    }
}
