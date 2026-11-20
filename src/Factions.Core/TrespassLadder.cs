using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How far a faction has escalated against an intruder at one node.</summary>
    public enum TrespassRung
    {
        None = 0,
        /// <summary>A Guard watches and holds fire.</summary>
        Shadow = 1,
        /// <summary>A Guard engages.</summary>
        Intercept = 2
    }

    /// <summary>
    /// The escalation ladder against an enemy ship in a faction's space: a Guard shadows it with
    /// its weapons held, then intercepts once the ship has stayed long enough. How long is the
    /// faction's to say, through its aggression, how deep in its space the ship is, and how bad
    /// the standing is. SE-free; the host supplies the standing and the clock.
    ///
    /// <para>Provenance: FAC-483 slice 3. Design: docs/fleet-events.md, "Trespass: a hostile ship in
    /// our space".</para>
    /// </summary>
    public static class TrespassLadder
    {
        /// <summary>How long a ship may stay at the edge of a faction's space, under a faction of
        /// the default personality and at the edge of the Enemies band, before it is intercepted.</summary>
        public const double BaseShadowSeconds = 180.0;

        /// <summary>The aggression <see cref="BaseShadowSeconds"/> is quoted at: the default
        /// personality's.</summary>
        public const double BaselineAggression = 0.4;

        /// <summary>How far a shadowing Guard holds from the intruder: outside the reach of vanilla
        /// gatlings and missiles (800 m), close enough to be seen doing it.</summary>
        public const double ShadowStandoffM = 1500.0;

        /// <summary>How far from its node a Guard follows an intruder while it shadows. Past this
        /// it returns to the node; pursuit beyond it is Intercept's (slice 3c).</summary>
        public const double ShadowLeashM = 10000.0;

        /// <summary>Below this speed a ship is lingering, not passing through: parked, mining or
        /// hovering. A ship cruising across a faction's space, on a trade run or anything else, is
        /// above it.</summary>
        public const double LingerSpeedMs = 20.0;

        /// <summary>Lingering time, within one stay, that costs one step of reputation.</summary>
        public const double LingerStepSeconds = 600.0;

        /// <summary>Reputation one step costs.</summary>
        public const int LingerStepReputation = 100;

        /// <summary>Whether a pass counts toward the reputation cost of a stay: the ship was told to
        /// leave and stayed (<see cref="TrespassRung.Intercept"/>), it is slow, and it is not docked
        /// by connector. Docked is never lingering, so a ship trading at a station costs nothing,
        /// and neither does one passing through at speed.</summary>
        public static bool IsLingering(TrespassRung rung, double speedMs, bool docked)
        {
            return rung == TrespassRung.Intercept && !docked && speedMs < LingerSpeedMs;
        }

        /// <summary>The time before Intercept. Every factor only shortens or lengthens the base:
        /// <list type="bullet">
        /// <item>aggression: a third at 0.9 (raider), about 1.5x at 0.2 (mercantile);</item>
        /// <item>depth: <paramref name="control"/> is the faction's share of influence where the ship
        /// is, 0.5 at the edge of its space and 1 deep inside; the edge keeps the base, deep inside
        /// halves it;</item>
        /// <item>standing: the edge of the Enemies band (-500) keeps the base, -1500 halves it.</item>
        /// </list>
        /// Zero for a faction that <paramref name="startsHostile"/>: the ladder starts at Intercept.</summary>
        public static double ShadowSeconds(double aggression, double control, int rep, bool startsHostile)
        {
            if (startsHostile) return 0;
            double byAggression = Math.Pow(3.0, (BaselineAggression - aggression) / 0.5);
            double byDepth = Clamp(1.5 - control, 0.5, 1.0);
            double byStanding = 1.0 - Clamp((-500.0 - rep) / 2000.0, 0.0, 0.5);
            return BaseShadowSeconds * byAggression * byDepth * byStanding;
        }

        /// <summary>The rung a ship seen for <paramref name="dwellSeconds"/> has reached.</summary>
        public static TrespassRung RungFor(double dwellSeconds, double shadowSeconds)
        {
            return dwellSeconds < shadowSeconds ? TrespassRung.Shadow : TrespassRung.Intercept;
        }

        /// <summary>What a faction says when its patrol is cleared to engage.</summary>
        public static string InterceptWarning(string factionName)
        {
            string name = string.IsNullOrEmpty(factionName) ? "this faction's" : factionName;
            return "You were told to leave " + name + " space. Our patrol is cleared to engage.";
        }

        /// <summary>Whether a faction starts the ladder at Intercept: one SE seeds hostile to every
        /// player. An implacable faction (FCTM), or one whose SE type is <c>Pirate</c> (SPRT).</summary>
        public static bool StartsHostile(Personality personality, string seFactionType)
        {
            if (personality != null && personality.Traits.Implacable) return true;
            return string.Equals(seFactionType, "Pirate", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a Guard holding at its node keeps its weapons held: it was sent against
        /// an intruder, the node is still at <see cref="TrespassRung.Shadow"/>, and nothing there has
        /// done anything a Guard answers by fighting — no player damage, no station call, and no
        /// raid dwelling on the node.</summary>
        public static bool GuardHoldsFire(PendingFleetAction guard, WorldModel world, IList<FactionState> states)
        {
            if (!guard.AgainstIntruder || world == null || guard.TargetNodeId == null) return false;
            if (world.IntruderRungAt(guard.TargetNodeId) != TrespassRung.Shadow) return false;
            SpatialNode node;
            if (world.Graph.Nodes.TryGetValue(guard.TargetNodeId, out node) && world.IsUnderPlayerAttack(node)) return false;
            string ignored;
            if (world.IsStationCalling(guard.TargetNodeId, out ignored)) return false;
            return !RaidDwellsAt(guard.TargetNodeId, states, world.UtcNow);
        }

        /// <summary>The intruder a Guard holding at its node pursues past the node's leash, or 0: it
        /// was sent at an intruder, its node has reached <see cref="TrespassRung.Intercept"/>, and the
        /// intruder's grid, at <paramref name="intruderPosKm"/>, is still in the Guard's faction's
        /// space. Leaving that space is what ends a pursuit.</summary>
        public static long PursuitTarget(PendingFleetAction guard, WorldModel world, InfluenceField field,
                                         Vec3 intruderPosKm, double minInfluence)
        {
            if (!guard.AgainstIntruder || world == null || guard.TargetNodeId == null) return 0;
            if (world.IntruderRungAt(guard.TargetNodeId) != TrespassRung.Intercept) return 0;
            long grid = world.IntruderGridAt(guard.TargetNodeId);
            if (grid == 0) return 0;
            string space = TrespassPolicy.SpaceOwnerAt(field, intruderPosKm, minInfluence);
            return string.Equals(space, guard.ActorTag, StringComparison.Ordinal) ? grid : 0;
        }

        /// <summary>Whether a Guard sent against an intruder has nothing left to do at its node: the
        /// intruder is gone and nothing else threatens it.</summary>
        public static bool GuardDutyOver(PendingFleetAction guard, WorldModel world)
        {
            if (!guard.AgainstIntruder || world == null || guard.TargetNodeId == null) return false;
            if (world.IsIntruded(guard.TargetNodeId)) return false;
            SpatialNode node;
            if (world.Graph.Nodes.TryGetValue(guard.TargetNodeId, out node) && world.IsUnderPlayerAttack(node)) return false;
            string ignored;
            return !world.IsStationCalling(guard.TargetNodeId, out ignored);
        }

        private static bool RaidDwellsAt(string nodeId, IList<FactionState> states, DateTime now)
        {
            if (states == null) return false;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState s = states[si];
                if (s == null) continue;
                for (int fi = 0; fi < s.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = s.PendingFleets[fi];
                    if (f.TargetNodeId != nodeId || f.Resolved || f.Intercepted) continue;
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer) continue;
                    if (f.HasArrived(now)) return true;
                }
            }
            return false;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }

    /// <summary>
    /// How long each pilot has been seen in each faction's space, without a gap longer than
    /// <see cref="TrespassPolicy.IntruderMemory"/>. A gap starts the clock again: a ship that left
    /// and came back is shadowed afresh.
    /// </summary>
    public sealed class TrespassDwellBook
    {
        private struct Seen
        {
            public DateTime First;
            public DateTime Last;
            public TrespassRung Rung;
            public DateTime LastLingerRead;
            public double LingerSeconds;
            public int StepsCharged;
        }

        /// <summary>The longest gap between two passes counted as lingering: a missed pass is
        /// bridged, a long one is not.</summary>
        public const double MaxLingerGapSeconds = 10.0;

        private readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>(StringComparer.Ordinal);

        /// <summary>Records a sighting and returns the seconds since this stay began.</summary>
        public double Observe(long identityId, string tag, DateTime nowUtc)
        {
            return Observe(Subject(identityId), tag, nowUtc);
        }

        /// <summary><see cref="Observe(long, string, DateTime)"/> for any subject: a pilot's identity
        /// or a fleet (<see cref="FleetTrespass.Subject"/>).</summary>
        public double Observe(string subject, string tag, DateTime nowUtc)
        {
            string key = Key(subject, tag);
            Seen s;
            if (!_seen.TryGetValue(key, out s) || nowUtc - s.Last > TrespassPolicy.IntruderMemory)
                s = new Seen { First = nowUtc, Rung = TrespassRung.None };
            s.Last = nowUtc;
            _seen[key] = s;
            return (nowUtc - s.First).TotalSeconds;
        }

        /// <summary>Adds the time since this stay's previous read when <paramref name="lingering"/>
        /// (<see cref="TrespassLadder.IsLingering"/>), and returns how many reputation steps
        /// (<see cref="TrespassLadder.LingerStepReputation"/> each) are newly due: one per full
        /// <see cref="TrespassLadder.LingerStepSeconds"/> of lingering in this stay. A stay with less
        /// lingering than that costs nothing, and a new stay starts from zero. Call after
        /// <see cref="Observe"/>.</summary>
        public int Linger(long identityId, string tag, DateTime nowUtc, bool lingering)
        {
            string key = Key(identityId, tag);
            Seen s;
            if (!_seen.TryGetValue(key, out s)) return 0;
            if (lingering && s.LastLingerRead != default(DateTime))
            {
                double dt = (nowUtc - s.LastLingerRead).TotalSeconds;
                if (dt > 0) s.LingerSeconds += dt < MaxLingerGapSeconds ? dt : MaxLingerGapSeconds;
            }
            s.LastLingerRead = nowUtc;
            int due = (int)Math.Floor(s.LingerSeconds / TrespassLadder.LingerStepSeconds) - s.StepsCharged;
            if (due < 0) due = 0;
            s.StepsCharged += due;
            _seen[key] = s;
            return due;
        }

        /// <summary>Seconds this stay has spent lingering, for the log.</summary>
        public double LingerSeconds(long identityId, string tag)
        {
            Seen s;
            return _seen.TryGetValue(Key(identityId, tag), out s) ? s.LingerSeconds : 0;
        }

        /// <summary>Records the rung this stay has reached. True when it is higher than before: the
        /// moment a step-up is announced.</summary>
        public bool StepTo(long identityId, string tag, TrespassRung rung)
        {
            return StepTo(Subject(identityId), tag, rung);
        }

        /// <summary><see cref="StepTo(long, string, TrespassRung)"/> for any subject.</summary>
        public bool StepTo(string subject, string tag, TrespassRung rung)
        {
            string key = Key(subject, tag);
            Seen s;
            if (!_seen.TryGetValue(key, out s) || rung <= s.Rung) return false;
            s.Rung = rung;
            _seen[key] = s;
            return true;
        }

        /// <summary>Drops stays not seen within <see cref="TrespassPolicy.IntruderMemory"/>.</summary>
        public void Prune(DateTime nowUtc)
        {
            if (_seen.Count == 0) return;
            var stale = new List<string>();
            foreach (KeyValuePair<string, Seen> kv in _seen)
                if (nowUtc - kv.Value.Last > TrespassPolicy.IntruderMemory) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) _seen.Remove(stale[i]);
        }

        public int Count { get { return _seen.Count; } }

        private static string Key(long identityId, string tag)
        {
            return Key(Subject(identityId), tag);
        }

        private static string Key(string subject, string tag)
        {
            return subject + "|" + tag;
        }

        private static string Subject(long identityId)
        {
            return identityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
