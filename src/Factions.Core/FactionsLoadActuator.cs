using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What the governor's Factions step does to a fleet a player has come near.</summary>
    public enum MaterialiseDecision
    {
        /// <summary>Materialise as usual.</summary>
        Materialise = 0,
        /// <summary>Stay abstract and resolve as if unobserved. Traffic only.</summary>
        ResolveOffScreen = 1,
        /// <summary>Stay abstract with the schedule frozen: neither arriving nor resolving, until
        /// the hold lifts or its bound runs out. Combat only.</summary>
        Wait = 2,
    }

    /// <summary>
    /// What the load governor's Factions step does to fleet materialisation (docs/load-governor.md,
    /// build step 5). Factions is cut first, cheapest cut first.
    ///
    /// <para><b>Step 1 and above: traffic resolves off-screen.</b> A Haul, Trade, Mine, Gift or
    /// Ferry fleet that would materialise because a player came within range stays abstract and
    /// resolves as it would with nobody near — the path every unobserved traffic fleet already
    /// takes, so no outcome changes, only whether its grid is simulated.</para>
    ///
    /// <para><b>Step 2 and above: combat waits</b> (operator, 2026-09-24). A Raid, Retaliate,
    /// Conquer, Guard, Patrol or Reinforce fleet that would materialise is held where it stands
    /// with its schedule frozen, then materialises with its whole squadron when the step drops or
    /// the wait reaches its bound (<see cref="CombatHoldTracker"/>). Not waves: a squadron's lead
    /// stays in the fight until it resolves, so escorts held back would never arrive as a wave.</para>
    ///
    /// <para>A fleet already materialised is never touched: despawning in front of a player would
    /// be visible.</para>
    /// </summary>
    public static class FactionsLoadActuator
    {
        public const int TrafficStep = 1;
        public const int CombatStep = 2;

        public static bool IsTraffic(ActionKind kind)
        {
            return kind == ActionKind.Haul || kind == ActionKind.Trade || kind == ActionKind.Mine
                || kind == ActionKind.Gift || kind == ActionKind.Ferry;
        }

        public static bool IsCombat(ActionKind kind)
        {
            return kind == ActionKind.Raid || kind == ActionKind.Retaliate || kind == ActionKind.Conquer
                || kind == ActionKind.Guard || kind == ActionKind.Patrol || kind == ActionKind.Reinforce;
        }

        public static MaterialiseDecision Decide(ActionKind kind, int factionsStep)
        {
            if (factionsStep >= TrafficStep && IsTraffic(kind)) return MaterialiseDecision.ResolveOffScreen;
            if (factionsStep >= CombatStep && IsCombat(kind)) return MaterialiseDecision.Wait;
            return MaterialiseDecision.Materialise;
        }

        /// <summary>Whether a fleet of this kind may start materialising at this step.</summary>
        public static bool MayMaterialise(ActionKind kind, int factionsStep)
        {
            return Decide(kind, factionsStep) == MaterialiseDecision.Materialise;
        }

        /// <summary>A held fleet's schedule moved forward by <paramref name="held"/>: its position
        /// and arrival are computed from these clocks, so shifting them freezes it where it
        /// stands. <c>RouteDepartedUtc</c> is the clock of a routed leg and moves with them when
        /// set.</summary>
        public static PendingFleetAction Freeze(PendingFleetAction fleet, TimeSpan held)
        {
            if (held <= TimeSpan.Zero) return fleet;
            fleet.DepartedUtc = fleet.DepartedUtc + held;
            fleet.EstimatedArrivalUtc = fleet.EstimatedArrivalUtc + held;
            if (fleet.RouteDepartedUtc != default(DateTime)) fleet.RouteDepartedUtc = fleet.RouteDepartedUtc + held;
            return fleet;
        }
    }

    /// <summary>
    /// How long each combat fleet has been held, so a wait is bounded (operator, 2026-09-24: 30
    /// minutes, then it materialises anyway — the player sees the fight late but always sees it).
    ///
    /// <para><b>Only continuous holding counts, and only it is frozen.</b> The observation loop
    /// calls <see cref="TryHold"/> every frame the fleet is held. A gap longer than
    /// <see cref="GapSeconds"/> — the player left range, or the step dropped for a while — ends a
    /// segment: the time across the gap is neither frozen nor counted, because the fleet was flying
    /// normally through it.</para>
    /// </summary>
    public sealed class CombatHoldTracker
    {
        public const double GapSeconds = 5.0;

        private sealed class Entry
        {
            public TimeSpan Held;
            public DateTime Last;
            public bool Expired;
        }

        private readonly Dictionary<string, Entry> _holds = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count { get { return _holds.Count; } }

        /// <summary>Holds the fleet for this frame. Returns true with the time to freeze by, or false
        /// once its accumulated hold has reached <paramref name="maxWait"/> — from then on it is
        /// released and should materialise. <paramref name="started"/> is true on the first frame
        /// of a hold, for a once-per-hold log line.</summary>
        public bool TryHold(string fleetId, DateTime now, TimeSpan maxWait, out TimeSpan freezeBy, out bool started)
        {
            freezeBy = TimeSpan.Zero;
            started = false;
            string key = fleetId ?? "";
            Entry e;
            if (!_holds.TryGetValue(key, out e))
            {
                e = new Entry { Last = now };
                _holds[key] = e;
                started = true;
            }
            if (e.Expired) return false;

            TimeSpan step = now - e.Last;
            if (step < TimeSpan.Zero || step.TotalSeconds > GapSeconds) step = TimeSpan.Zero;
            e.Last = now;
            e.Held += step;
            if (maxWait > TimeSpan.Zero && e.Held >= maxWait)
            {
                e.Expired = true;
                return false;
            }
            freezeBy = step;
            return true;
        }

        public TimeSpan HeldFor(string fleetId)
        {
            Entry e;
            return _holds.TryGetValue(fleetId ?? "", out e) ? e.Held : TimeSpan.Zero;
        }

        /// <summary>Whether the fleet was held within the last <see cref="GapSeconds"/> and is not
        /// released: frozen where it stands, so it must not be reported as moving.</summary>
        public bool IsHolding(string fleetId, DateTime now)
        {
            Entry e;
            if (!_holds.TryGetValue(fleetId ?? "", out e) || e.Expired) return false;
            TimeSpan since = now - e.Last;
            return since >= TimeSpan.Zero && since.TotalSeconds <= GapSeconds;
        }

        /// <summary>The fleet materialised or resolved; forget it.</summary>
        public void Release(string fleetId)
        {
            _holds.Remove(fleetId ?? "");
        }

        /// <summary>Drops holds not touched for <paramref name="idle"/>, so fleets that left range
        /// or resolved elsewhere do not accumulate.</summary>
        public void Prune(DateTime now, TimeSpan idle)
        {
            List<string> stale = null;
            foreach (KeyValuePair<string, Entry> kv in _holds)
                if (now - kv.Value.Last > idle) (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale != null) foreach (string k in stale) _holds.Remove(k);
        }
    }
}
