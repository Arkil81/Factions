using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-425 slice 3: what Core does for a fight the host flies in waves.</summary>
    public class FightWavesTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float dps;
                return Dps.TryGetValue(prefabId, out dps) ? new[] { new WeaponBand { Dps = dps, RangeM = 2000 } } : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static readonly DateTime T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static Catalogue Cat()
        {
            var cat = new Catalogue();
            cat.Dps["Gunship"] = 5000;
            cat.Dps["Scout"] = 10;
            return cat;
        }

        private static WorldModel World(Catalogue cat)
        {
            WorldModel w = TestSupport.World(1);
            w.UtcNow = T0;
            w.PrefabCatalogue = cat;
            w.Tuning.CombatVariance = 0;
            return w;
        }

        private static List<FactionState> Bench(WorldModel w, Catalogue cat, string[] atk, string[] def, bool materialised)
        {
            var sprt = new FactionState { Tag = "SPRT", RngSeed = 1 };
            var impr = new FactionState { Tag = "IMPR", RngSeed = 1 };
            string node = BenchScenario.FightNodeId("f1");
            PendingFleetAction a = BenchScenario.Attacker("f1", "atk", "SPRT", "IMPR", node, new Vec3(0, 0, 0), atk, cat, T0, 300, BenchFlags.None);
            PendingFleetAction d = BenchScenario.Defender("f1", "def", "IMPR", node, new Vec3(0.6, 0, 0), def, cat, T0, 300, w.Tuning, BenchFlags.None);
            if (materialised) { a.MaterialisedEntityId = 101; d.MaterialisedEntityId = 201; }
            sprt.PendingFleets.Add(a);
            impr.PendingFleets.Add(d);
            return new List<FactionState> { sprt, impr };
        }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log) if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        /// <summary>Taken into the grids, then both sides' grids leave the world. A waved meeting stays the
        /// grids' (its hulls are queued at the site, not gone); an ordinary one hands off to the duel.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AWavedMeeting_StaysPhysical_WithNoGridOnTheField(bool waved)
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Gunship" }, true);
            var log = new List<string>();
            w.UtcNow = T0.AddSeconds(10);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            if (waved) w.WavedMeetings.Add(states[0].PendingFleets[0].FleetId);
            for (int s = 0; s < 2; s++)
            {
                PendingFleetAction f = states[s].PendingFleets[0];
                f.MaterialisedEntityId = 0;
                f.EscortEntityIds = null;
                states[s].PendingFleets[0] = f;
            }
            w.UtcNow = T0.AddSeconds(100);
            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.Equal(waved, states[0].PendingFleets[0].Fight.Physical);
            Assert.Equal(waved, Line(log, "FIGHT_HANDOFF") == null);
        }

        [Fact]
        public void AWavedMeetingThatNeverHadGrids_IsTakenIntoTheGrids()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship" }, new[] { "Gunship" }, false);
            w.WavedMeetings.Add(states[0].PendingFleets[0].FleetId);
            var log = new List<string>();
            w.UtcNow = T0.AddSeconds(10);
            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.NotNull(states[0].PendingFleets[0].Fight);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);
        }

        [Fact]
        public void MeetingOf_NamesTheRaidThenItsDefence_AndNothingForADefender()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship" }, new[] { "Scout" }, false);
            w.UtcNow = T0.AddSeconds(10);

            List<(int si, int fi)> meeting = PendingFleetResolver.MeetingOf(states, w, w.Tuning, 0, 0);
            Assert.Equal(new[] { (0, 0), (1, 0) }, meeting.ToArray());
            Assert.Empty(PendingFleetResolver.MeetingOf(states, w, w.Tuning, 1, 0));
        }

        // ---- the site's slot bookkeeping ---------------------------------------------------------

        [Fact]
        public void ALostSlotRenumbersTheFleetsHullsAboveIt_AndDropsItsOwn()
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            for (int i = 0; i < 4; i++) site.Enqueue(new FightHull { Side = "A", FleetId = "F", Member = i, CostMs = 1, Dps = 1 }, T0);
            site.Enqueue(new FightHull { Side = "A", FleetId = "G", Member = 2, CostMs = 1, Dps = 1 }, T0);
            site.Admit(2.0, T0);   // F#0 and F#1 on the field

            site.OnSlotRemoved("F", 1);

            Assert.Equal(new[] { 0 }, site.FieldOfFleet("F").Select(h => h.Member).ToArray());
            Assert.Equal(new[] { 1, 2 }, site.QueuedOf("F").Select(h => h.Member).ToArray());
            Assert.Equal(2, site.QueuedOf("G").Single().Member);   // another fleet's slots do not move
        }

        [Fact]
        public void AnAdoptedGrid_IsOnTheField_AndShedWhenOverBudget()
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            for (int i = 0; i < 6; i++) site.PlaceOnField(new FightHull { Side = "A", FleetId = "R", Member = i, CostMs = 1, Dps = 1 }, T0);
            site.Enqueue(new FightHull { Side = "B", FleetId = "G", Member = 0, CostMs = 1, Dps = 1 }, T0);
            site.PlaceOnField(new FightHull { Side = "A", FleetId = "R", Member = 0, CostMs = 1, Dps = 1 }, T0);   // idempotent

            Assert.Equal(6, site.OnField("A"));
            List<FightExit> shed = site.Review(T0.AddSeconds(1), h => 1.0, 3.0);
            Assert.Equal(3, shed.Count(e => e.Cause == FightExitCause.Shed));
        }

        // ---- the rules that feed it -------------------------------------------------------------

        [Fact]
        public void InTransit_ACombatFleetPutsAtMostFourHullsInTheWorld_LeadFirst()
        {
            var hulls = new List<string> { "Lead", "A", "B", "C", "D", "E" };
            Assert.Equal(new[] { "Lead", "A", "B", "C" }, FightWaves.TransitHulls(hulls).ToArray());
            var few = new List<string> { "Lead", "A" };
            Assert.Same(few, FightWaves.TransitHulls(few));
        }

        [Fact]
        public void AHullsStrengthIsItsDpsAtItsIntegrity_AndItsCostIsPriced()
        {
            Catalogue cat = Cat();
            var fleet = new PendingFleetAction { FleetId = "F", Squadron = new List<string> { "Gunship", "Scout" } };
            fleet.SetIntegrity(0, 0.5);

            FightHull h = FightWaves.HullOf(fleet, 0, "SPRT", cat, p => p == "Gunship" ? 2.5 : 0.1);
            Assert.Equal("Gunship", h.Prefab);
            Assert.Equal(2500, h.Dps, 3);
            Assert.Equal(2.5, h.CostMs, 6);
            Assert.Equal(FightWaves.DefaultHullCostMs, FightWaves.HullOf(fleet, 1, "SPRT", cat, null).CostMs, 6);
            Assert.Equal(new[] { 0, 2 }, FightWaves.FreeSlots(3, new HashSet<int> { 1 }).ToArray());
        }

        // ---- the governor's part ----------------------------------------------------------------

        [Fact]
        public void TheFieldIsAShareOfTheBudget_CutByStep_AndNothingAtStepThreeOrUnderTheSimFloor()
        {
            Assert.Equal(6.0, FightWaves.FieldMs(12.0, 2, 0, false), 6);
            Assert.Equal(4.5, FightWaves.FieldMs(12.0, 2, 1, false), 6);
            Assert.Equal(3.0, FightWaves.FieldMs(12.0, 2, 2, false), 6);
            Assert.Equal(0.0, FightWaves.FieldMs(12.0, 2, 3, false), 6);
            Assert.Equal(0.0, FightWaves.FieldMs(12.0, 1, 0, true), 6);
            Assert.Equal(FightWaves.DefaultFieldMs, FightWaves.FieldMs(double.NaN, 1, 0, false), 6);
            Assert.Equal(0.0, FightWaves.FieldMs(-3.0, 1, 0, false), 6);
        }

        [Fact]
        public void AZeroField_StillShowsOneHullASide()
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            for (int i = 0; i < 3; i++) site.Enqueue(new FightHull { Side = "A", FleetId = "R", Member = i, CostMs = 1, Dps = 1 }, T0);
            for (int i = 0; i < 3; i++) site.Enqueue(new FightHull { Side = "B", FleetId = "G", Member = i, CostMs = 1, Dps = 1 }, T0);
            site.Admit(FightWaves.FieldMs(10, 1, 3, false), T0);
            Assert.Equal(1, site.OnField("A"));
            Assert.Equal(1, site.OnField("B"));
        }

        [Fact]
        public void TheSimWatchTripsOnASustainedDrop_AndClearsOnASustainedRecovery()
        {
            var w = new SimFloorWatch();
            Assert.False(w.Update(0.5, T0));
            Assert.False(w.Update(0.5, T0.AddSeconds(4)));
            Assert.True(w.Update(0.5, T0.AddSeconds(5)));

            // A recovery that does not hold does not clear it.
            Assert.True(w.Update(0.95, T0.AddSeconds(6)));
            Assert.True(w.Update(0.85, T0.AddSeconds(10)));
            Assert.True(w.Update(0.95, T0.AddSeconds(11)));
            Assert.True(w.Update(0.95, T0.AddSeconds(20)));
            Assert.False(w.Update(0.95, T0.AddSeconds(21)));

            // A dip shorter than the trip time does not trip it.
            Assert.False(w.Update(0.5, T0.AddSeconds(30)));
            Assert.False(w.Update(0.9, T0.AddSeconds(33)));
            Assert.False(w.Update(0.5, T0.AddSeconds(34)));
            Assert.False(w.Update(0.5, T0.AddSeconds(38)));
        }

        // ---- the site's verdict goes to Core ----------------------------------------------------

        /// <summary>Every Guard hull retreated (integrity 0.3 each, capacity left), so the site reads the
        /// raid as the side standing. Capacity alone would read the Guard as still standing and repel.</summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void TheSitesVerdict_IsTheResult(bool attackerStands, bool repelled)
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Gunship", "Gunship" }, true);
            w.WavedMeetings.Add(states[0].PendingFleets[0].FleetId);
            var log = new List<string>();
            w.UtcNow = T0.AddSeconds(10);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            PendingFleetAction loser = states[attackerStands ? 1 : 0].PendingFleets[0];
            loser.SetIntegrity(0, 0.3);
            loser.SetIntegrity(1, 0.3);
            states[attackerStands ? 1 : 0].PendingFleets[0] = loser;

            Assert.True(PendingFleetResolver.DecideWaved(states, w, w.Tuning, 0, 0, attackerStands, log));
            FightProgress p = states[0].PendingFleets[0].Fight;
            Assert.True(p.Decided.HasValue);
            Assert.True(p.DecidedByGrids);
            Assert.Equal(repelled, p.Decided.Value.AttackerRepelled);
            Assert.Equal(attackerStands, p.Decided.Value.DefenderBrokeOff);
            Assert.True(states[0].PendingFleets[0].EstimatedArrivalUtc <= w.UtcNow);   // the dwell ends now
        }

        [Fact]
        public void ADecisionIsNotTakenForAFleetThatIsNotADwellFight()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship" }, new[] { "Gunship" }, true);
            Assert.False(PendingFleetResolver.DecideWaved(states, w, w.Tuning, 1, 0, true, new List<string>()));
        }

        [Fact]
        public void AQueuedHullTooDamagedToStay_LeavesFromTheQueue_InsteadOfBeingSpawned()
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            site.Enqueue(new FightHull { Side = "A", FleetId = "R", Member = 0, CostMs = 1, Dps = 1 }, T0);
            site.Enqueue(new FightHull { Side = "A", FleetId = "R", Member = 1, CostMs = 1, Dps = 1 }, T0);
            List<FightHull> cleared = site.Admit(10, T0, h => h.Member == 0 ? 0.3 : 0.9);
            Assert.Equal(1, Assert.Single(cleared).Member);
            Assert.Equal(1, site.RetreatedFrom("A"));
            Assert.Equal(0, site.Queued("A"));
        }
    }
}
