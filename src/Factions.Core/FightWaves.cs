using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>The constants and pure rules the host flies a fight in waves by (docs/fight-control.md,
    /// FAC-425 slice 3). The bookkeeping is <see cref="FightSite"/>; this is what feeds it.</summary>
    public static class FightWaves
    {
        /// <summary>The most hulls a combat fleet puts in the world while it is not at a fight site.
        /// Without it a 24-hull raid materialises whole on its approach and the site then sheds most of
        /// it the moment the dwell begins.</summary>
        public const int TransitMaxHulls = 4;

        /// <summary>The field budget, ms/frame of predicted combat cost, until the governor sizes it
        /// (slice 4). With <see cref="DefaultHullCostMs"/> it holds eight hulls.</summary>
        public const double DefaultFieldMs = 8.0;

        /// <summary>A hull's cost when nothing prices it.</summary>
        public const double DefaultHullCostMs = 1.0;

        /// <summary>How far a hull leaving the field flies from the site before it is closed.</summary>
        public const double LeaveClearM = 2000.0;

        /// <summary>A hull leaving the field is closed after this long however far it got.</summary>
        public const double LeaveTimeoutSeconds = 60.0;

        /// <summary>How often the host reviews its sites: exits, admissions, freezes.</summary>
        public const double ReviewSeconds = 1.0;

        /// <summary>The field budget one site gets (docs/fight-control.md, Governor): the Factions fleet
        /// budget (<see cref="TrafficBudget.FactionsBudgetMs"/>, NaN before the first window) split
        /// between the live sites, cut 25% a governor step, and nothing — one hull a side — at step 3 or
        /// while the site's own sim floor is tripped. The governor sizes the field; it never freezes a
        /// fight already open.</summary>
        public static double FieldMs(double factionsBudgetMs, int liveSites, int factionsStep, bool simFloorTripped)
        {
            if (simFloorTripped || factionsStep >= 3) return 0.0;
            double budget = double.IsNaN(factionsBudgetMs) ? DefaultFieldMs : Math.Max(0.0, factionsBudgetMs);
            double share = budget / Math.Max(1, liveSites);
            return share * (1.0 - 0.25 * Math.Max(0, factionsStep));
        }

        /// <summary>The hulls a combat fleet spawns outside a site: the first
        /// <see cref="TransitMaxHulls"/> of <paramref name="hulls"/>, lead first as the squadron orders
        /// them. A fleet at or under the cap spawns as it always has.</summary>
        public static IReadOnlyList<string> TransitHulls(IReadOnlyList<string> hulls)
        {
            if (hulls == null || hulls.Count <= TransitMaxHulls) return hulls;
            var first = new List<string>(TransitMaxHulls);
            for (int i = 0; i < TransitMaxHulls; i++) first.Add(hulls[i]);
            return first;
        }

        /// <summary>One hull slot of <paramref name="fleet"/> as the site sees it: its prefab, its side,
        /// its DPS at its integrity, and its predicted cost.</summary>
        public static FightHull HullOf(PendingFleetAction fleet, int slot, string side, IPrefabCatalogue catalogue,
                                       Func<string, double> costMs)
        {
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            string prefab = slot >= 0 && slot < hulls.Count ? hulls[slot] : fleet.ShipPrefabId;
            double dps = catalogue != null && !string.IsNullOrEmpty(prefab) ? ShipPool.TotalDps(catalogue.WeaponBands(prefab)) : 0;
            double cost = costMs != null && !string.IsNullOrEmpty(prefab) ? costMs(prefab) : DefaultHullCostMs;
            return new FightHull
            {
                FleetId = fleet.FleetId,
                Member = slot,
                Prefab = prefab,
                Side = side,
                Dps = dps * fleet.IntegrityOf(slot),
                CostMs = cost > 0 ? cost : DefaultHullCostMs,
            };
        }

        /// <summary>Slots of a fleet with <paramref name="slotCount"/> hulls that are not in
        /// <paramref name="taken"/>, in slot order — what joins the queue when a fleet joins a site.</summary>
        public static List<int> FreeSlots(int slotCount, ICollection<int> taken)
        {
            var free = new List<int>();
            for (int s = 0; s < slotCount; s++)
                if (taken == null || !taken.Contains(s)) free.Add(s);
            return free;
        }
    }

    /// <summary>The fight's own sim-ratio watch: trips after <see cref="TripSeconds"/> continuously below
    /// <see cref="Floor"/>, clears after <see cref="ClearSeconds"/> continuously at or above
    /// <see cref="ClearAt"/>. Read every review, so it acts in seconds where the governor's interval is
    /// 180 s.</summary>
    public sealed class SimFloorWatch
    {
        public const double Floor = 0.8;
        public const double ClearAt = 0.9;
        public const double TripSeconds = 5.0;
        public const double ClearSeconds = 10.0;

        private DateTime _belowSince = DateTime.MinValue;
        private DateTime _aboveSince = DateTime.MinValue;

        public bool Tripped { get; private set; }

        public bool Update(double simRatio, DateTime nowUtc)
        {
            if (simRatio < Floor)
            {
                _aboveSince = DateTime.MinValue;
                if (_belowSince == DateTime.MinValue) _belowSince = nowUtc;
                if ((nowUtc - _belowSince).TotalSeconds >= TripSeconds) Tripped = true;
            }
            else
            {
                _belowSince = DateTime.MinValue;
                if (simRatio >= ClearAt)
                {
                    if (_aboveSince == DateTime.MinValue) _aboveSince = nowUtc;
                    if ((nowUtc - _aboveSince).TotalSeconds >= ClearSeconds) Tripped = false;
                }
                else _aboveSince = DateTime.MinValue;
            }
            return Tripped;
        }
    }
}
