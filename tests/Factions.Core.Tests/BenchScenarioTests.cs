using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Bench scenarios: real fleets staged by <c>factions bench</c> that run the ordinary
    /// meeting and dwell resolution but land no world effect, are never persisted, and end as one
    /// scenario. Plus the abstract duel run on demand.</summary>
    public class BenchScenarioTests
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

        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static WorldModel World(Catalogue cat)
        {
            WorldModel w = TestSupport.World(1);
            w.UtcNow = T0;
            w.PrefabCatalogue = cat;
            return w;
        }

        private static Catalogue Cat()
        {
            var cat = new Catalogue();
            cat.Dps["Gunship"] = 5000;
            cat.Dps["Scout"] = 10;
            return cat;
        }

        private static FactionState State(string tag) { return new FactionState { Tag = tag, RngSeed = 1 }; }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log)
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        private static bool AnyLineContains(IEnumerable<string> log, string text)
        {
            foreach (string l in log) if (l.Contains(text)) return true;
            return false;
        }

        /// <summary>SPRT attacks IMPR at a bench point: attacker with a lead and one escort grid,
        /// defender with one grid.</summary>
        private static List<FactionState> Fight(WorldModel w, Catalogue cat, string[] atk, string[] def,
                                                BenchFlags flags = BenchFlags.None)
        {
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            string node = BenchScenario.FightNodeId("f1");
            PendingFleetAction a = BenchScenario.Attacker("f1", "atk", "SPRT", "IMPR", node, new Vec3(0, 0, 0),
                atk, cat, T0, 300, flags);
            a.MaterialisedEntityId = 101;
            a.EscortEntityIds = new List<long> { 102 };
            PendingFleetAction d = BenchScenario.Defender("f1", "def", "IMPR", node, new Vec3(0.6, 0, 0),
                def, cat, T0, 300, w.Tuning, flags);
            d.MaterialisedEntityId = 201;
            sprt.PendingFleets.Add(a);
            impr.PendingFleets.Add(d);
            return new List<FactionState> { sprt, impr };
        }

        [Fact]
        public void PredicatesReadTheMarkerAndItsFlags()
        {
            var real = new PendingFleetAction { Kind = ActionKind.Raid };
            var bench = new PendingFleetAction { Kind = ActionKind.Raid, BenchLabel = "x" };
            var ledger = new PendingFleetAction { Kind = ActionKind.Raid, BenchLabel = "x", BenchFlags = BenchFlags.Ledger };
            var react = new PendingFleetAction { Kind = ActionKind.Raid, BenchLabel = "x", BenchFlags = BenchFlags.React };

            Assert.False(real.IsBench);
            Assert.True(bench.IsBench);
            Assert.True(BenchScenario.FilesFight(real));
            Assert.False(BenchScenario.FilesFight(bench));
            Assert.True(BenchScenario.FilesFight(ledger));
            Assert.True(BenchScenario.SeenAsThreat(real));
            Assert.False(BenchScenario.SeenAsThreat(bench));
            Assert.True(BenchScenario.SeenAsThreat(react));
        }

        [Fact]
        public void LabelsAreShortAndPlain()
        {
            Assert.True(BenchScenario.IsValidLabel("fight-1_a"));
            Assert.False(BenchScenario.IsValidLabel(""));
            Assert.False(BenchScenario.IsValidLabel("a b"));
            Assert.False(BenchScenario.IsValidLabel("a:b"));
            Assert.False(BenchScenario.IsValidLabel(new string('a', BenchScenario.MaxLabelLength + 1)));
        }

        [Fact]
        public void FactoriesStageBothSidesOnStation()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" });
            PendingFleetAction a = states[0].PendingFleets[0];
            PendingFleetAction d = states[1].PendingFleets[0];

            Assert.Equal(FleetStopKind.Engage, a.CurrentStopKind);
            Assert.True(a.HoldsStation(T0.AddSeconds(1), w.Tuning));
            Assert.Equal(2, a.Squadron.Count);
            Assert.Equal(10000, a.WeaponBands[0].Dps + (a.WeaponBands.Count > 1 ? a.WeaponBands[1].Dps : 0), 3);
            Assert.Equal("b.f1.atk", a.FleetId);
            Assert.Null(a.RemainingStops);

            Assert.True(d.IsActiveGuard(T0.AddSeconds(1), w.Tuning));
            Assert.True(d.StandsAsDefender(T0.AddSeconds(299), w.Tuning));
            Assert.True(d.Resolved);
            Assert.Null(d.Squadron);
        }

        [Fact]
        public void TheDwellRunsTheMeetingDuelAndTheAttackerLeaves()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            var closed = new List<long>();

            // Both sides are in the world from the start, so the grids hold the fight.
            w.UtcNow = T0.AddSeconds(10);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            Assert.Single(states[0].PendingFleets);
            Assert.Single(states[1].PendingFleets);
            Assert.Null(Line(log, "BENCH_RESOLVED"));
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);

            // The ordinary meeting ran, decided by the grids — which took nothing off either side, so
            // the Guard still stands and the attacker is repelled — and names the scenario.
            string meeting = Line(log, "RAID_REPELLED [b.f1.atk] SPRT vs [b.f1.def] IMPR");
            Assert.NotNull(meeting);
            Assert.Contains("ratio=1.00 outcome=repelled src=physical (grids T1-T3 atk=1.00 def=1.00", meeting);
            Assert.Equal("COMBAT_OUTCOME [b.f1.atk] SPRT vs [b.f1.def] IMPR @bench:f1 kind=Raid meeting=Guard"
                         + " attack=Repelled defence=Stood next=withdraw hulls_lost atk=0/2 def=0/1 src=physical bench=f1",
                         Line(log, "COMBAT_OUTCOME"));
            string resolved = Line(log, "BENCH_RESOLVED f1 [b.f1.atk] SPRT");
            Assert.NotNull(resolved);
            Assert.Contains("outcome=repelled", resolved);
            Assert.Contains("cause=physical", resolved);
            Assert.Contains("[b.f1.def] IMPR outcome=stood", resolved);
            Assert.Contains("@bench:f1", resolved);

            // Nobody vanishes at the resolution: the attacker is on a leg away from the fight, still
            // marked repelled so the host breaks it away first, and the defender holds briefly.
            Assert.Empty(closed);
            PendingFleetAction a = states[0].PendingFleets.Single();
            Assert.Equal(FleetStopKind.Return, a.CurrentStopKind);
            Assert.Equal("repelled", a.RetreatReason);
            Assert.False(a.Resolved);
            Assert.True(BenchScenario.InAftermath(a));
            Assert.Equal(0.0, a.OriginPosKm.X, 9);
            Assert.Equal(-BenchScenario.AftermathLeaveKm, a.TargetPosKm.X, 9);
            Assert.Equal(T0.AddSeconds(301 + BenchScenario.AftermathCapSeconds), a.EstimatedArrivalUtc);
            PendingFleetAction d = states[1].PendingFleets.Single();
            Assert.Equal(T0.AddSeconds(301 + BenchScenario.DefenderHoldSeconds), d.EstimatedArrivalUtc + w.Tuning.GuardDuration);

            // The defender's hold runs out and its grid goes; the write-up is not repeated.
            w.UtcNow = T0.AddSeconds(301 + BenchScenario.DefenderHoldSeconds + 1);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            Assert.Empty(states[1].PendingFleets);
            Assert.Equal(new List<long> { 201 }, closed);
            Assert.Single(states[0].PendingFleets);
            Assert.Single(log.Where(l => l.StartsWith("BENCH_RESOLVED", StringComparison.Ordinal)));
            Assert.Null(Line(log, "BENCH_CONCLUDED"));

            // The host closed the attacker's grids where its leg away ended: the scenario is done.
            PendingFleetAction gone = states[0].PendingFleets[0];
            gone.MaterialisedEntityId = 0;
            gone.EscortEntityIds = null;
            states[0].PendingFleets[0] = gone;
            w.UtcNow = w.UtcNow.AddSeconds(5);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            Assert.Equal("BENCH_CONCLUDED f1 reason=grids_left after=36s", Line(log, "BENCH_CONCLUDED"));
            Assert.Empty(states[0].PendingFleets);
            Assert.False(BenchScenario.IsActive(states, "f1"));
        }

        [Fact]
        public void AScenarioStillInTheWorldIsRetiredAtTheCap()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            var closed = new List<long>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            w.UtcNow = T0.AddSeconds(301 + BenchScenario.AftermathCapSeconds - 1);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            Assert.Single(states[0].PendingFleets);
            Assert.DoesNotContain(101L, closed);

            // Its grids never reached the end of the leg: the cap closes them where they are.
            w.UtcNow = T0.AddSeconds(301 + BenchScenario.AftermathCapSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log, onGridExpired: closed.Add);
            Assert.Equal("BENCH_CONCLUDED f1 reason=cap after=150s", Line(log, "BENCH_CONCLUDED"));
            Assert.Empty(states[0].PendingFleets);
            Assert.Contains(101L, closed);
            Assert.Contains(102L, closed);
        }

        [Fact]
        public void AScenarioWithNoGridsEndsAtItsResolution()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" });
            for (int si = 0; si < 2; si++)
            {
                PendingFleetAction f = states[si].PendingFleets[0];
                f.MaterialisedEntityId = 0;
                f.EscortEntityIds = null;
                states[si].PendingFleets[0] = f;
            }
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.NotNull(Line(log, "BENCH_RESOLVED f1"));
            Assert.Equal("BENCH_CONCLUDED f1 reason=grids_left after=0s", Line(log, "BENCH_CONCLUDED"));
            Assert.Empty(states[0].PendingFleets);
            Assert.Empty(states[1].PendingFleets);
        }

        [Fact]
        public void ALandedBenchAttackerLeavesToo()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode n = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N1");
            n.OwnerFactionTag = "IMPR";
            FactionState sprt = State("SPRT");
            var states = new List<FactionState> { sprt, State("IMPR") };
            PendingFleetAction raid = BenchScenario.Attacker("r1", "raid", "SPRT", "IMPR", "N1", new Vec3(10, 2, 0),
                new[] { "Gunship" }, cat, T0, 300, BenchFlags.None);
            raid.MaterialisedEntityId = 301;
            sprt.PendingFleets.Add(raid);
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.Contains("outcome=landed", Line(log, "BENCH_RESOLVED r1"));
            PendingFleetAction a = sprt.PendingFleets.Single();
            Assert.Equal(FleetStopKind.Return, a.CurrentStopKind);
            Assert.Null(a.RetreatReason);
            // Straight out from the node it attacked, which it was standing 2 km off.
            Assert.Equal(10.0, a.TargetPosKm.X, 9);
            Assert.Equal(2 + BenchScenario.AftermathLeaveKm, a.TargetPosKm.Y, 9);

            // Its leg away ends with the grid still flying: the defer holds it; the cap ends it.
            w.UtcNow = T0.AddSeconds(301 + BenchScenario.AftermathCapSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.Equal("BENCH_CONCLUDED r1 reason=cap after=150s", Line(log, "BENCH_CONCLUDED"));
            Assert.Empty(sprt.PendingFleets);
            Assert.Null(Line(log, "RAID_RESOLVED"));
        }

        [Fact]
        public void AScenarioLandsNoWorldEffect()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Scout" }, new[] { "Gunship" });
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            string resolved = Line(log, "BENCH_RESOLVED f1");
            Assert.NotNull(resolved);
            Assert.Contains("outcome=repelled", resolved);
            Assert.True(AnyLineContains(log, "GUARD_INTERCEPT [b.f1.def] IMPR defeated [b.f1.atk] SPRT"));
            // No hull written off or credited home for either side.
            Assert.Null(Line(log, "SHIP_LOST"));
            Assert.Null(Line(log, "SHIP_RETURNED"));
            // Nothing filed: no fight record, no incident, no event.
            Assert.Empty(states[0].Fights);
            Assert.Empty(states[1].Fights);
            Assert.Empty(w.Incidents);
            Assert.Empty(w.PendingEvents);
            Assert.Empty(states[0].PendingFleets);
            // The defender, still in the world, stands its short hold and then goes.
            Assert.Single(states[1].PendingFleets);
            w.UtcNow = T0.AddSeconds(301 + BenchScenario.DefenderHoldSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.Empty(states[1].PendingFleets);
        }

        [Fact]
        public void ABenchDefenderThatSurvivesCarriesItsDamage()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship" }, new[] { "Gunship", "Gunship", "Gunship" });
            PendingFleetAction before = states[1].PendingFleets[0];

            w.UtcNow = T0.AddSeconds(301);
            // Recorded on the defender in the duel's own pass, before the scenario retires it.
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            // The switch is off (no gate set), and the bench defender records its damage anyway.
            Assert.Null(before.HullIntegrity);
            Assert.True(Line(log, "HULL_DAMAGED [b.f1.def] IMPR cause=guard_duel") != null, string.Join("\n", log));
            Assert.NotNull(Line(log, "BENCH_RESOLVED f1"));
            Assert.NotNull(states[1].PendingFleets.Single().HullIntegrity);
        }

        [Fact]
        public void LedgerFlagFilesTheFight()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Fight(w, cat, new[] { "Gunship" }, new[] { "Scout" }, BenchFlags.Ledger);

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), new List<string>());

            Assert.Single(states[0].Fights);
            Assert.True(states[0].Fights[0].Attacking);
            Assert.Equal("IMPR", states[0].Fights[0].OpponentTag);
            Assert.Single(states[1].Fights);
            Assert.Empty(w.Incidents);
        }

        [Fact]
        public void ABenchRaidOnARealNodeLeavesTheNodeAsItWas()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode n = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N1");
            n.OwnerFactionTag = "IMPR";
            n.HoldStrength = 0.8;
            n.StationPrefabId = "Station";
            n.ShipStock["Gunship"] = 2;
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            var states = new List<FactionState> { sprt, impr };
            sprt.PendingFleets.Add(BenchScenario.Attacker("r1", "raid", "SPRT", "IMPR", "N1", n.Position,
                new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.True(AnyLineContains(log, "STATION_DEFENSE [b.r1.raid] SPRT"));
            string resolved = Line(log, "BENCH_RESOLVED r1 [b.r1.raid] SPRT");
            Assert.NotNull(resolved);
            Assert.Contains("outcome=landed", resolved);
            Assert.Contains(" vs IMPR @N1", resolved);
            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Equal(0.8, n.HoldStrength, 6);
            Assert.Equal("IMPR", n.OwnerFactionTag);
            Assert.Equal(2, n.ShipStock["Gunship"]);
            Assert.Empty(w.Incidents);
            Assert.Empty(sprt.Fights);
            Assert.Empty(sprt.PendingFleets);
        }

        [Fact]
        public void ABenchRaidRobsNoConvoy()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode n = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N1");
            n.OwnerFactionTag = "IMPR";
            n.HoldStrength = 0.8;
            FactionState sprt = State("SPRT"), impr = State("IMPR"), merc = State("MERC");
            var states = new List<FactionState> { sprt, impr, merc };
            sprt.PendingFleets.Add(BenchScenario.Attacker("r1", "raid", "SPRT", "IMPR", "N1", n.Position,
                new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));
            merc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "IMPR", TargetNodeId = "N1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = n.Position, FleetId = "C0FFEE",
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(301), ConvoySize = 1,
                Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } }, CargoValue = 1000,
                LastResolvedSegment = -1,
            });
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);

            Assert.NotNull(Line(log, "BENCH_RESOLVED r1"));
            Assert.False(AnyLineContains(log, "INTERCEPT_WINDOW"));
            Assert.False(AnyLineContains(log, "RAID_LOOTS"));
            foreach (PendingFleetAction f in merc.PendingFleets) Assert.Null(f.InterceptedByFleetId);
        }

        [Fact]
        public void ABenchRaidIsNoThreatUnlessAsked()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode n = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N1");
            n.OwnerFactionTag = "IMPR";
            FactionState sprt = State("SPRT");
            var states = new List<FactionState> { sprt, State("IMPR") };
            sprt.PendingFleets.Add(BenchScenario.Attacker("r1", "raid", "SPRT", "IMPR", "N1", n.Position,
                new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));
            PendingFleetAction found;

            Assert.False(PendingFleetResolver.HasIncomingRaid(states, w, "N1"));
            Assert.False(PendingFleetResolver.TryFindIncomingThreat(states, "N1", out found));

            PendingFleetAction f = sprt.PendingFleets[0];
            f.BenchFlags = BenchFlags.React;
            sprt.PendingFleets[0] = f;
            Assert.True(PendingFleetResolver.HasIncomingRaid(states, w, "N1"));
            Assert.True(PendingFleetResolver.TryFindIncomingThreat(states, "N1", out found));
        }

        [Fact]
        public void BenchFleetsAreNeverWritten()
        {
            Catalogue cat = Cat();
            FactionState sprt = State("SPRT");
            sprt.PendingFleets.Add(BenchScenario.Attacker("f1", "atk", "SPRT", "IMPR", BenchScenario.FightNodeId("f1"),
                new Vec3(0, 0, 0), new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));

            BrainDoc onlyBench = FactionStore.ToBrainDoc(sprt, new List<SpatialNode>(), FactionStore.DefaultLogCap);
            Assert.Null(onlyBench.PendingFleets);

            sprt.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetNodeId = "N1", FleetId = "ABC123",
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddMinutes(1), LastResolvedSegment = -1,
            });
            BrainDoc mixed = FactionStore.ToBrainDoc(sprt, new List<SpatialNode>(), FactionStore.DefaultLogCap);
            Assert.Single(mixed.PendingFleets);
        }

        [Fact]
        public void RemoveTakesOnlyTheNamedScenario()
        {
            Catalogue cat = Cat();
            FactionState sprt = State("SPRT");
            var states = new List<FactionState> { sprt };
            sprt.PendingFleets.Add(BenchScenario.Attacker("a", "atk", "SPRT", "IMPR", BenchScenario.FightNodeId("a"),
                new Vec3(0, 0, 0), new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));
            sprt.PendingFleets.Add(BenchScenario.Attacker("b", "atk", "SPRT", "IMPR", BenchScenario.FightNodeId("b"),
                new Vec3(0, 0, 0), new[] { "Gunship" }, cat, T0, 300, BenchFlags.None));
            sprt.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Trade, ActorTag = "SPRT" });

            Assert.True(BenchScenario.IsActive(states, "A"));
            Assert.Single(BenchScenario.Remove(states, "a"));
            Assert.False(BenchScenario.IsActive(states, "a"));
            Assert.Single(BenchScenario.Remove(states, null));
            Assert.Single(sprt.PendingFleets);
            Assert.False(sprt.PendingFleets[0].IsBench);
        }

        // ---- the duel ------------------------------------------------------------------------------

        private static FactionTuning ExactTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.CombatTurns = 3;
            t.CombatRangeBracketsM = new double[] { 2000, 800, 0 };
            t.CombatBaseDamageFraction = 0.2;
            t.CombatVariance = 0;
            return t;
        }

        [Fact]
        public void DuelIsTheResolversDuel()
        {
            // Attacker 100 dps reaching 2000 m, defender 50 dps reaching 800 m, no jitter:
            // T1 atk 20 def 0, T2 atk 20 def 10, T3 atk 20 (def at 10 -> 0) def 10.
            var atk = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 2000 } };
            var def = new List<WeaponBand> { new WeaponBand { Dps = 50, RangeM = 800 } };
            BenchDuelReport r = BenchScenario.Duel(atk, def, 0, ExactTuning(), 5, 1);

            Assert.Equal(5, r.Runs);
            Assert.Equal(5, r.AttackerWins);
            Assert.Equal(0, r.DefenderWins + r.Mutual + r.Standoff);
            Assert.Equal(0.8, BenchDuelReport.Percentile(r.AttackerRatios, 0.5), 6);
            Assert.Equal(0.0, BenchDuelReport.Percentile(r.DefenderRatios, 1), 6);
            Assert.Equal("T1(2000m) atk=20 def=0 | T2(800m) atk=20 def=10 | T3(0m) atk=20 def=10", r.FirstTurnSummary);
        }

        [Fact]
        public void DuelIsDeterministicForASeed()
        {
            Catalogue cat = Cat();
            FactionTuning t = TestSupport.Tuning();
            t.CombatVariance = 0.25;
            BenchDuelReport a = BenchScenario.Duel(cat, new[] { "Gunship" }, new[] { "Gunship", "Scout" }, 0, t, 50, 7);
            BenchDuelReport b = BenchScenario.Duel(cat, new[] { "Gunship" }, new[] { "Gunship", "Scout" }, 0, t, 50, 7);
            BenchDuelReport c = BenchScenario.Duel(cat, new[] { "Gunship" }, new[] { "Gunship", "Scout" }, 0, t, 50, 8);

            Assert.Equal(a.AttackerRatios, b.AttackerRatios);
            Assert.Equal(a.Format(), b.Format());
            Assert.NotEqual(a.AttackerRatios, c.AttackerRatios);
            Assert.Equal(50, a.AttackerWins + a.DefenderWins + a.Mutual + a.Standoff);
            Assert.Equal(5000, a.AttackerDps, 3);
            Assert.Equal(5010, a.DefenderDps, 3);
        }

        [Fact]
        public void ADuelCanBeContestedWithChosenArms()
        {
            Catalogue cat = Cat();
            FactionTuning t = TestSupport.Tuning();
            DuelStance kite = BenchScenario.Stance(cat, new[] { "Gunship" }, TacticArm.Kite, BreakOffLevel.High);
            DuelStance brawl = BenchScenario.Stance(cat, new[] { "Scout" }, TacticArm.Brawl, null);
            Assert.Equal(TacticArm.Kite, kite.Arm);
            Assert.Equal(BreakOffLevel.High, kite.BreakOff);
            Assert.Equal(TacticChoice.DefaultFor(null).BreakOff, brawl.BreakOff);

            BenchDuelReport c = BenchScenario.Duel(cat, new[] { "Gunship" }, new[] { "Scout" }, 0, t, 10, 3, kite, brawl);
            BenchDuelReport u = BenchScenario.Duel(cat, new[] { "Gunship" }, new[] { "Scout" }, 0, t, 10, 3);

            Assert.True(c.Contested);
            Assert.Contains(" set=", c.FirstTurnSummary);
            Assert.Contains("contested atk=Kite/High", c.Format());
            Assert.False(u.Contested);
            Assert.DoesNotContain(" set=", u.FirstTurnSummary);
        }

        [Fact]
        public void PercentileIsNearestRank()
        {
            var sorted = new List<double> { 0.1, 0.2, 0.3, 0.4 };
            Assert.Equal(0.1, BenchDuelReport.Percentile(sorted, 0));
            Assert.Equal(0.2, BenchDuelReport.Percentile(sorted, 0.5));
            Assert.Equal(0.4, BenchDuelReport.Percentile(sorted, 1));
            Assert.Equal(0, BenchDuelReport.Percentile(new List<double>(), 0.5));
        }
    }
}
