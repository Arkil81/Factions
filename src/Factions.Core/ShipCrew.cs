using System;

namespace Factions.Core
{
    public enum CrewHullClass { Unknown, Drone, PilotOnly, CrewedShip, Station }

    /// <summary>Seat evidence from the largest hull, not its docked craft or turret subgrids.
    /// A seat is an accommodation cue, not proof that its exit or another room is reachable.</summary>
    public sealed class CrewLayout
    {
        public bool Known;
        public bool IsStation;
        public int PilotStations;
        public int CrewSeats;
        public int CryoChambers;
        public int SpawnPoints;
        public int AuthoredCrew;
    }

    public sealed class CrewEstimate
    {
        public CrewHullClass Class;
        public int Desired;
        /// <summary>Native non-control seating or authored spawn positions; reachability still
        /// needs a physical observation. Does not include ship-control seats or cryo chambers.</summary>
        public int PresenceSlots;
        public bool Authored;
    }

    /// <summary>FAC-440's first estimate: one occupant per human station or non-cryo crew seat,
    /// unless the author supplied a complement. Mass, cargo and weapon size cannot establish
    /// habitable space. Runtime presence is limited separately, so missing spawn slots do not
    /// erase a ship's desired complement.</summary>
    public static class ShipCrew
    {
        public static CrewEstimate Estimate(CrewLayout layout)
        {
            if (layout == null || !layout.Known) return new CrewEstimate();
            int pilots = Math.Max(0, layout.PilotStations);
            int seats = Math.Max(0, layout.CrewSeats);
            int points = Math.Max(0, layout.SpawnPoints);
            bool authored = layout.AuthoredCrew > 0;
            int desired = authored ? layout.AuthoredCrew : Math.Max(pilots + seats, points);
            return new CrewEstimate
            {
                Class = layout.IsStation ? CrewHullClass.Station
                    : seats > 0 || points > 0 || (authored && desired > pilots) ? CrewHullClass.CrewedShip
                    : pilots > 0 ? CrewHullClass.PilotOnly : CrewHullClass.Drone,
                Desired = desired,
                PresenceSlots = points > 0 ? points : seats,
                Authored = authored,
            };
        }

        /// <summary>The unarmed movement trial uses native passive seats, even when the design
        /// carries authored spawn points. It tests one hull with at most eight characters.</summary>
        public static int TrialCount(CrewLayout layout, int emptyCrewSeats)
        {
            CrewEstimate estimate = Estimate(layout);
            if (estimate.Class != CrewHullClass.CrewedShip) return 0;
            return Math.Min(8, Math.Min(estimate.Desired, Math.Max(0, emptyCrewSeats)));
        }
    }
}
