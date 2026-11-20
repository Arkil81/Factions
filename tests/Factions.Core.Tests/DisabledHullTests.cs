using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A hull that loses its thrust under hostile fire is disabled: written off at once, out
    /// of the meeting's capacity, never rematerialised; one that stalls unshot is not.</summary>
    public class DisabledHullTests
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
                if (!Dps.TryGetValue(prefabId, out dps)) return new WeaponBand[0];
                return new[] { new WeaponBand { Dps = dps, RangeM = 2000 } };
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

        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 22, 13, 0, DateTimeKind.Utc);

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

        private static FactionState State(string tag) { return new FactionState { Tag = tag, RngSeed = 1 }; }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log)
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        /// <summary>A bench fight, SPRT attacking IMPR, every grid in the world: the attacker's lead is
        /// 101 and its escorts 102.., the defender 201.</summary>
        private static List<FactionState> Bench(WorldModel w, Catalogue cat, string[] atk, string[] def)
        {
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            string node = BenchScenario.FightNodeId("f1");
            PendingFleetAction a = BenchScenario.Attacker("f1", "atk", "SPRT", "IMPR", node, new Vec3(0, 0, 0),
                atk, cat, T0, 300, BenchFlags.None);
            PendingFleetAction d = BenchScenario.Defender("f1", "def", "IMPR", node, new Vec3(0.6, 0, 0),
                def, cat, T0, 300, w.Tuning, BenchFlags.None);
            a.MaterialisedEntityId = 101;
            if (atk.Length > 1)
            {
                a.EscortEntityIds = new List<long>();
                for (int i = 1; i < atk.Length; i++) a.EscortEntityIds.Add(101 + i);
            }
            d.MaterialisedEntityId = 201;
            sprt.PendingFleets.Add(a);
            impr.PendingFleets.Add(d);
            return new List<FactionState> { sprt, impr };
        }

        private static void Resolve(List<FactionState> states, WorldModel w, List<string> log, int atSeconds)
        {
            w.UtcNow = T0.AddSeconds(atSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
        }

        // ---- disabled or stalled --------------------------------------------------------------------

        [Fact]
        public void AThrustLossUnderRecentHostileFireIsDisabled()
        {
            DateTime now = T0.AddSeconds(60);
            Assert.True(DisabledHull.IsDisabled(true, now.AddSeconds(-6), now));
            Assert.True(DisabledHull.IsDisabled(true, now - DisabledHull.HostileHitWindow, now));
        }

        [Fact]
        public void AThrustLossWithNoHostileFireOnRecordIsAStall_NotADisabling()
        {
            DateTime now = T0.AddSeconds(60);
            // Nobody shot it: the thrust-loss dematerialise keeps it.
            Assert.False(DisabledHull.IsDisabled(true, null, now));
            // Shot long before it lost its thrust: the loss is not the shot's doing.
            Assert.False(DisabledHull.IsDisabled(true, now - DisabledHull.HostileHitWindow - TimeSpan.FromSeconds(1), now));
            // Recent fire with thrust intact is a fight, not a disabling.
            Assert.False(DisabledHull.IsDisabled(false, now.AddSeconds(-2), now));
        }

        [Fact]
        public void TheLastHostileHitSkipsFireFromTheHullsOwnSide()
        {
            var fire = new IncomingFire();
            fire.Record(101, 900, 5000, T0);                 // the player's ship
            fire.Record(101, 102, 20, T0.AddSeconds(3));     // an escort's stray round, later
            long attacker;
            DateTime hit;

            Assert.True(fire.TryLastHit(101, g => g == 102, out attacker, out hit));
            Assert.Equal(900, attacker);
            Assert.Equal(T0, hit);

            Assert.True(fire.TryLastHit(101, null, out attacker, out hit));
            Assert.Equal(102, attacker);

            Assert.False(fire.TryLastHit(101, g => true, out attacker, out hit));
            Assert.False(fire.TryLastHit(555, null, out attacker, out hit));
        }

        [Fact]
        public void TheLastHostileHitIsNotBoundByTheShortWhoIsShootingMeWindow()
        {
            var fire = new IncomingFire();
            fire.Record(101, 900, 5000, T0);
            long attacker;
            DateTime hit;
            DateTime later = T0 + IncomingFire.RecentWindow + TimeSpan.FromSeconds(5);
            Assert.False(fire.HitRecently(101, later));
            Assert.True(fire.TryLastHit(101, null, out attacker, out hit));
            Assert.True(DisabledHull.IsDisabled(true, hit, later));
        }

        // ---- a lead is written off and never comes back ---------------------------------------------

        [Fact]
        public void ADisabledLeadTakesTheDestroyedPathAndIsNeverRematerialised()
        {
            Catalogue cat = Cat();
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "SPRT", TargetNodeId = "N165", FleetId = "FB7180",
                DepartedUtc = T0.AddMinutes(-5), EstimatedArrivalUtc = T0.AddMinutes(-4),
                ShipPrefabId = "Gunship", ConvoySize = 2, Squadron = new List<string> { "Gunship", "Gunship" },
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, new List<string> { "Gunship", "Gunship" }),
                MaterialisedEntityId = 140045217994094467, EscortEntityIds = new List<long> { 7 },
                LastResolvedSegment = -1,
            };
            DateTime now = T0.AddSeconds(24);

            DisabledHull.WriteOffLead(ref fleet, 0, "Gunship", cat, now);

            // The destroyed sentinel, not 0: 0 is what the host rematerialises.
            Assert.Equal(-1, fleet.MaterialisedEntityId);
            Assert.NotEqual(0, fleet.MaterialisedEntityId);
            Assert.Null(fleet.EscortEntityIds);
            Assert.Equal(DisabledHull.Cause, fleet.ForcedRatioCause);
            Assert.True(fleet.ForcedRatio.HasValue);
            Assert.Equal(now, fleet.EstimatedArrivalUtc);
            Assert.True(fleet.IsPhysicalLoss);
            // The lead's hull is gone from what the fleet can credit back; the escort's is not.
            Assert.Equal(new List<string> { "Gunship" }, fleet.SurvivingHulls);
            Assert.Equal(5000, (double)fleet.WeaponBands[0].Dps, 3);
            // Out of the node's defence at once.
            Assert.False(fleet.IsActiveGuard(now, TestSupport.Tuning()));
        }

        [Fact]
        public void AGuardDisabledInTheWorldNoLongerDefends()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Scout" }, new[] { "Gunship" });
            PendingFleetAction a = states[0].PendingFleets[0];
            a.MaterialisedEntityId = 0;
            states[0].PendingFleets[0] = a;
            PendingFleetAction d = states[1].PendingFleets[0];
            d.MaterialisedEntityId = 0;
            DisabledHull.WriteOffLead(ref d, 0, "Gunship", cat, T0.AddSeconds(5));
            states[1].PendingFleets[0] = d;
            Assert.False(d.StandsAsDefender(T0.AddSeconds(10), w.Tuning));

            var log = new List<string>();
            Resolve(states, w, log, 301);
            // The Gunship Guard would have broken a Scout raid; disabled, it meets no Guard.
            Assert.Null(Line(log, "GUARD_INTERCEPT"));
            Assert.NotNull(Line(log, "STATION_DEFENSE [b.f1.atk] SPRT"));
        }

        // ---- an escort is written off and the squadron fights on --------------------------------------

        [Fact]
        public void ADisabledEscortLeavesOnlyItsOwnHullAndTheLeadFliesOn()
        {
            Catalogue cat = Cat();
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", FleetId = "r1",
                ShipPrefabId = "Gunship", ConvoySize = 3, Squadron = new List<string> { "Gunship", "Scout", "Gunship" },
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, new List<string> { "Gunship", "Scout", "Gunship" }),
                MaterialisedEntityId = 101, EscortEntityIds = new List<long> { 102, 103 },
            };

            DisabledHull.WriteOffEscort(ref fleet, 103, 2, "Gunship", cat);

            Assert.Equal(101, fleet.MaterialisedEntityId);
            Assert.Null(fleet.ForcedRatio);
            Assert.False(fleet.IsPhysicalLoss);
            Assert.Equal(new List<long> { 102 }, fleet.EscortEntityIds);
            Assert.Equal(new List<string> { "Gunship", "Scout" }, fleet.SurvivingHulls);
            double dps = 0;
            foreach (WeaponBand b in fleet.WeaponBands) dps += b.Dps;
            Assert.Equal(5010, dps, 3);

            // The last escort out leaves no empty list behind.
            DisabledHull.WriteOffEscort(ref fleet, 102, 1, "Scout", cat);
            Assert.Null(fleet.EscortEntityIds);
            Assert.Equal(new List<string> { "Gunship" }, fleet.SurvivingHulls);
        }

        [Fact]
        public void AnEscortDisabledInTheWorldIsGoneFromTheMeetingsCapacity()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship", "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            Resolve(states, w, log, 10);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            PendingFleetAction a = states[0].PendingFleets[0];
            DisabledHull.WriteOffEscort(ref a, 104, 3, "Gunship", cat);
            states[0].PendingFleets[0] = a;
            Resolve(states, w, log, 301);

            string meeting = Line(log, "RAID_REPELLED [b.f1.atk] SPRT");
            Assert.NotNull(meeting);
            Assert.Contains("ratio=0.75 outcome=repelled", meeting);
            Assert.Contains("hulls_lost atk=1/4", meeting);
        }

        // ---- the wreck's delay ----------------------------------------------------------------------

        [Fact]
        public void AWreckIsDueOnlyAfterItsDelay()
        {
            var q = new DisabledHullQueue();
            q.Add(11, "[FB7180]", "SPRT", T0);
            q.Add(11, "[FB7180]", "SPRT", T0.AddSeconds(20));   // a second report keeps the first due time
            q.Add(12, "[FB7180]", "SPRT", T0.AddSeconds(10));
            Assert.True(q.Contains(11));

            var due = new List<DisabledHullQueue.Entry>();
            Assert.Equal(0, q.TakeDue(T0 + DisabledHull.DespawnDelay - TimeSpan.FromSeconds(1), due));
            Assert.Equal(1, q.TakeDue(T0 + DisabledHull.DespawnDelay, due));
            Assert.Equal(11, due[0].EntityId);
            Assert.False(q.Contains(11));
            Assert.True(q.Contains(12));
            Assert.Equal(1, q.TakeDue(T0.AddSeconds(10) + DisabledHull.DespawnDelay, due));
            Assert.Equal(0, q.Count);
        }

        [Fact]
        public void TheKillConfirmationNamesTheHullAndWhere()
        {
            Assert.Equal("Kill confirmed: [SPRT] guard DS_Pirate_Scimitar disabled at N165.",
                         DisabledHull.KillSentence("SPRT", ActionKind.Guard, "DS_Pirate_Scimitar", "N165"));
            Assert.Equal("Kill confirmed: [EXP] raid hull disabled.",
                         DisabledHull.KillSentence("EXP", ActionKind.Raid, null, null));
        }
    }
}
