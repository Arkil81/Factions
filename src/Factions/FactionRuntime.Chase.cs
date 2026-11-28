using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// A player can chase a fleet and make it spawn (FAC-539).
    ///
    /// <para><b>The brake.</b> An abstract fleet near a player slows along <see cref="ChaseBrake"/>'s
    /// curve, so one cruising at deep-space speed stays inside the observation radius long enough to
    /// spawn instead of crossing it in three seconds.</para>
    ///
    /// <para><b>The force radius.</b> Inside <see cref="FactionTuning.ForceSpawnRadiusKm"/> the holds
    /// that keep a fleet abstract near a player (the station queue, the traffic presence budget, the
    /// load governor) no longer apply, and the spawn goes to the head of the spawn queue. A held fleet
    /// is therefore not refused for good: the player has to come closer.</para>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Set by the host: distance in km from a point to the nearest player whose presence
        /// can spawn a fleet (the same set <c>isPlayerWithin</c> counts). Positive infinity when there
        /// is none. Null leaves the brake off.</summary>
        public Func<Vec3, double> NearestPlayerKm { get; set; }

        private readonly Dictionary<string, DateTime> _chaseBrakedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private DateTime _chaseBrakePruneUtc;

        /// <summary>Whether a player is within the force radius of <paramref name="posKm"/>.</summary>
        private bool ForcesSpawn(Vec3 posKm, Func<Vec3, double, bool> isPlayerWithin)
        {
            return _tuning.ForceSpawnRadiusKm > 0 && isPlayerWithin(posKm, _tuning.ForceSpawnRadiusKm);
        }

        /// <summary>Slows the abstract fleet at <paramref name="fi"/> for the time since it was last
        /// braked, if it is moving faster than the cap at its distance from the nearest player. True
        /// when its schedule was pushed back, and the caller rereads it.</summary>
        private bool ApplyChaseBrake(FactionState st, int fi, PendingFleetAction fleet, Vec3 posKm, DateTime now)
        {
            Func<Vec3, double> nearest = NearestPlayerKm;
            if (nearest == null || fleet.FleetId == null || fleet.IsBench || _tuning.ChaseBrakeStartKm <= 0) return false;

            double cap = ChaseBrake.SpeedCapKmPerMin(nearest(posKm), _tuning);
            if (double.IsInfinity(cap)) { _chaseBrakedAt.Remove(fleet.FleetId); return false; }

            DateTime last;
            bool had = _chaseBrakedAt.TryGetValue(fleet.FleetId, out last);
            _chaseBrakedAt[fleet.FleetId] = now;
            if (!had || HeldElsewhere(fleet.FleetId, now)) return false;

            TimeSpan gap = now - last;
            if (gap <= TimeSpan.Zero || gap.TotalSeconds > CombatHoldTracker.GapSeconds) return false;
            TimeSpan freeze = ChaseBrake.FreezeFor(ChaseBrake.NominalSpeedKmPerMin(fleet, now, _tuning), cap, gap);
            if (freeze <= TimeSpan.Zero) return false;
            st.PendingFleets[fi] = FactionsLoadActuator.Freeze(fleet, freeze);
            return true;
        }

        /// <summary>Whether another hold already froze this fleet's schedule in the last few seconds.
        /// Each of them stops it outright, so a brake on top would move it backwards.</summary>
        private bool HeldElsewhere(string fleetId, DateTime now)
        {
            if (_combatHolds.IsHolding(fleetId, now) || _waveSiteOfFleet.ContainsKey(fleetId)) return true;
            DateTime at;
            if (_queueHeldAt.TryGetValue(fleetId, out at) && Recent(at, now)) return true;
            return _combatHeldAt.TryGetValue(fleetId, out at) && Recent(at, now);
        }

        private static bool Recent(DateTime at, DateTime now)
        {
            TimeSpan since = now - at;
            return since >= TimeSpan.Zero && since.TotalSeconds <= CombatHoldTracker.GapSeconds;
        }

        /// <summary>Drops brake stamps for fleets that resolved or materialised while braked.</summary>
        private void PruneChaseBrake(DateTime now)
        {
            if (_chaseBrakedAt.Count == 0 || now < _chaseBrakePruneUtc) return;
            _chaseBrakePruneUtc = now.AddMinutes(1);
            List<string> stale = null;
            foreach (KeyValuePair<string, DateTime> e in _chaseBrakedAt)
                if (!Recent(e.Value, now)) (stale ?? (stale = new List<string>())).Add(e.Key);
            if (stale != null) foreach (string id in stale) _chaseBrakedAt.Remove(id);
        }
    }
}
