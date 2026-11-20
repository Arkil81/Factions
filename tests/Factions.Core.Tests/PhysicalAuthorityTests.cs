using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A dwell fight the grids take part in: decided by what the grids measured while every
    /// fleet of it is in the world, and handed to and from the duel as they leave and arrive, without
    /// counting any damage twice.</summary>
    public class PhysicalAuthorityTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public readonly Dictionary<string, float> RangeM = new Dictionary<string, float>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float dps, range;
                if (!Dps.TryGetValue(prefabId, out dps)) return new WeaponBand[0];
                if (!RangeM.TryGetValue(prefabId, out range)) range = 2000;
                return new[] { new WeaponBand { Dps = dps, RangeM = range } };
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

        private static Catalogue Cat()
        {
            var cat = new Catalogue();
            cat.Dps["Gunship"] = 5000;
            cat.Dps["Scout"] = 10;
            cat.Dps["Ram"] = 5000;
            cat.RangeM["Ram"] = 0;
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

        /// <summary>A bench fight: SPRT attacks IMPR, both sides in the world unless
        /// <paramref name="materialised"/> is false.</summary>
        private static List<FactionState> Bench(WorldModel w, Catalogue cat, string[] atk, string[] def,
                                                bool materialised = true, BenchFlags flags = BenchFlags.None)
        {
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            string node = BenchScenario.FightNodeId("f1");
            PendingFleetAction a = BenchScenario.Attacker("f1", "atk", "SPRT", "IMPR", node, new Vec3(0, 0, 0),
                atk, cat, T0, 300, flags);
            PendingFleetAction d = BenchScenario.Defender("f1", "def", "IMPR", node, new Vec3(0.6, 0, 0),
                def, cat, T0, 300, w.Tuning, flags);
            if (materialised)
            {
                a.MaterialisedEntityId = 101;
                if (atk.Length > 1)
                {
                    a.EscortEntityIds = new List<long>();
                    for (int i = 1; i < atk.Length; i++) a.EscortEntityIds.Add(101 + i);
                }
                d.MaterialisedEntityId = 201;
            }
            sprt.PendingFleets.Add(a);
            impr.PendingFleets.Add(d);
            return new List<FactionState> { sprt, impr };
        }

        private static void Resolve(List<FactionState> states, WorldModel w, List<string> log, int atSeconds)
        {
            w.UtcNow = T0.AddSeconds(atSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
        }

        // ---- when the grids decide -------------------------------------------------------------

        [Fact]
        public void AnUndecidedFightRepelsTheAttacker()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            Resolve(states, w, log, 10);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            // The grids: the raid's second hull was shot to half its integrity.
            PendingFleetAction a = states[0].PendingFleets[0];
            a.SetIntegrity(1, 0.5);
            states[0].PendingFleets[0] = a;
            Resolve(states, w, log, 301);

            // The grids say 7500 of 10000, and the Scout still stands at the dwell's end: undecided,
            // which repels the attacker.
            string meeting = Line(log, "RAID_REPELLED [b.f1.atk] SPRT vs [b.f1.def] IMPR @bench:f1 ratio=0.75 outcome=repelled");
            Assert.NotNull(meeting);
            Assert.Contains(" src=physical (grids T1-T3 atk=0.75 def=1.00 defence_standing", meeting);
            Assert.Contains("hulls_lost atk=0/2 def=0/1", meeting);
            Assert.Null(Line(log, "RAID_BREAKS_GUARD"));
            // The hulls already carry what the grids did: nothing is applied on top.
            Assert.Null(Line(log, "HULL_DAMAGED [b.f1.atk]"));
            string bench = Line(log, "BENCH_RESOLVED f1 [b.f1.atk] SPRT");
            Assert.Contains("outcome=repelled ratio=0.75 cause=physical", bench);
            Assert.Contains("[b.f1.def] IMPR outcome=stood", bench);
        }

        [Fact]
        public void AHullLostInTheWorldIsGoneFromTheRatio()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship", "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            Resolve(states, w, log, 10);

            PendingFleetAction a = states[0].PendingFleets[0];
            a.MarkHullLostAt(3);
            a.EscortEntityIds = new List<long> { 102, 103 };
            states[0].PendingFleets[0] = a;
            Resolve(states, w, log, 301);

            string meeting = Line(log, "RAID_REPELLED [b.f1.atk] SPRT");
            Assert.NotNull(meeting);
            Assert.Contains("ratio=0.75 outcome=repelled", meeting);
            Assert.Contains("hulls_lost atk=1/4", meeting);
        }

        [Fact]
        public void ARaidTheGridsBrokeLeavesWithTheHullsItStillHas()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship", "Gunship" }, new[] { "Scout" });
            var log = new List<string>();
            Resolve(states, w, log, 10);

            // One hull lost and the other two at a quarter: 2500 of 15000, below every break-off
            // fraction.
            PendingFleetAction a = states[0].PendingFleets[0];
            a.MarkHullLostAt(2);
            a.SetIntegrity(0, 0.25);
            a.SetIntegrity(1, 0.25);
            a.EscortEntityIds = new List<long> { 102 };
            states[0].PendingFleets[0] = a;
            Resolve(states, w, log, 301);

            // Below its break-off fraction with the Scout still standing: repelled, not written down.
            string repelled = Line(log, "RAID_REPELLED [b.f1.atk] SPRT");
            Assert.NotNull(repelled);
            Assert.Contains("ratio=0.17 outcome=repelled src=physical", repelled);
            Assert.Contains("broke_off=atk defence_standing", repelled);
            // The hull the grids destroyed is counted once, and nothing else is written off.
            Assert.Contains("hulls_lost atk=1/3", repelled);
            Assert.Null(Line(log, "SHIP_LOST"));
        }

        [Fact]
        public void AGuardDestroyedInTheWorldNoLongerDefends()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Scout" }, new[] { "Gunship" }, materialised: false);
            PendingFleetAction d = states[1].PendingFleets[0];
            d.MaterialisedEntityId = -1;
            d.ForcedRatio = 0;
            d.ForcedRatioCause = "grid_destroyed";
            states[1].PendingFleets[0] = d;
            Assert.False(d.IsActiveGuard(T0.AddSeconds(10), w.Tuning));
            Assert.False(d.StandsAsDefender(T0.AddSeconds(10), w.Tuning));

            var log = new List<string>();
            Resolve(states, w, log, 301);
            // The Gunship Guard would have broken a Scout raid; with it gone the raid meets no Guard.
            Assert.Null(Line(log, "GUARD_INTERCEPT"));
            Assert.NotNull(Line(log, "STATION_DEFENSE [b.f1.atk] SPRT"));
        }

        // ---- handoffs --------------------------------------------------------------------------

        [Fact]
        public void LeavingMidDwellHandsTheRestToTheDuelFromTheGridsState()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship", "Gunship" }, new[] { "Gunship" });
            var log = new List<string>();
            Resolve(states, w, log, 10);

            // Half way through turn 2 the grids have destroyed one raid hull, and then everyone leaves.
            w.UtcNow = T0.AddSeconds(150);
            PendingFleetAction a = states[0].PendingFleets[0];
            a.MarkHullLostAt(2);
            a.MaterialisedEntityId = 0;
            a.EscortEntityIds = null;
            states[0].PendingFleets[0] = a;
            PendingFleetAction d = states[1].PendingFleets[0];
            d.MaterialisedEntityId = 0;
            states[1].PendingFleets[0] = d;
            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);

            string handoff = Line(log, "FIGHT_HANDOFF [b.f1.atk] SPRT dir=abstract");
            Assert.NotNull(handoff);
            Assert.Contains("turns_fought=2/3", handoff);
            Assert.Contains("atk=0.67", handoff);
            Assert.False(states[0].PendingFleets[0].Fight.Physical);

            Resolve(states, w, log, 301);
            // Only the last turn is left: point blank, 10000 raid DPS against 5000. The raid brings
            // 10000 of the 15000 it began with and loses 1750 of it: 8250 of 15000.
            string meeting = Line(log, "RAID_BREAKS_GUARD [b.f1.atk] SPRT ratio=0.55");
            Assert.NotNull(meeting);
            Assert.Contains(" src=handoff (T3(", meeting);
            Assert.DoesNotContain("T1(", meeting);
            // Each surviving hull loses what the last turn took from what it brought into it (8250 of
            // 10000), not the whole fight's ratio a second time.
            string damaged = Line(log, "HULL_DAMAGED [b.f1.atk] SPRT cause=guard_duel");
            Assert.NotNull(damaged);
            Assert.Contains("surviving=0.8", damaged);
        }

        [Fact]
        public void ArrivingMidDwellFightsTheElapsedTurnsFirstAndSpawnsAtTheBracket()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            w.Graph.Add(new Vec3(0, 0, 0), 0.6, "N1").OwnerFactionTag = "IMPR";
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            var squadron = new List<string> { "Gunship", "Gunship" };
            sprt.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetTag = "IMPR", TargetNodeId = "N1",
                CurrentStopKind = FleetStopKind.Engage, DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(300),
                ShipPrefabId = "Gunship", ConvoySize = 2, Squadron = squadron,
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, squadron), FleetId = "r1", LastResolvedSegment = -1,
            });
            var guards = new List<string> { "Gunship", "Gunship" };
            impr.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "IMPR", TargetNodeId = "N1", CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = T0.AddSeconds(-10), EstimatedArrivalUtc = T0.AddSeconds(-10), Resolved = true,
                ShipPrefabId = "Gunship", ConvoySize = 2, Squadron = guards,
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, guards), FleetId = "g1",
                LastResolvedSegment = -1,
            });
            var states = new List<FactionState> { sprt, impr };
            var log = new List<string>();

            // A player arrives half way through turn 2: turn 1 is fought by the duel first. The two
            // sides are equal, so neither falls to its break-off fraction in one turn.
            w.UtcNow = T0.AddSeconds(150);
            // Every gun here reaches 2000 m, so the contested schedule holds both sides there.
            MeetingSpawn spawn = PendingFleetResolver.BeforeMaterialise(states, w, w.Tuning, 0, 0, new SeededRandom(3), log);
            Assert.True(spawn.InMeeting);
            Assert.Equal(1, spawn.OpponentSi);
            Assert.Equal(2000.0, spawn.SeparationM);
            // Turn 1 at 2000 m: each side takes 3500 of its 10000; the hulls appear carrying it.
            Assert.Equal(0.65, states[0].PendingFleets[0].IntegrityOf(0), 3);
            Assert.Equal(0.65, states[0].PendingFleets[0].IntegrityOf(1), 3);
            Assert.Equal(0.65, states[1].PendingFleets[0].IntegrityOf(0), 3);
            Assert.Equal(0.65, states[1].PendingFleets[0].IntegrityOf(1), 3);
            Assert.NotNull(Line(log, "HULL_DAMAGED [r1] SPRT cause=duel_prefix"));
            Assert.Equal(1, states[0].PendingFleets[0].Fight.TurnsFought);

            // A second fleet of the meeting spawning moments later fights nothing again.
            MeetingSpawn guardSpawn = PendingFleetResolver.BeforeMaterialise(states, w, w.Tuning, 1, 0, new SeededRandom(3), log);
            Assert.Equal(0, guardSpawn.OpponentSi);
            Assert.Equal(0.65, states[1].PendingFleets[0].IntegrityOf(0), 3);

            // Both in the world: the grids take over from turn 2.
            PendingFleetAction r = states[0].PendingFleets[0];
            r.MaterialisedEntityId = 11;
            states[0].PendingFleets[0] = r;
            PendingFleetAction g = states[1].PendingFleets[0];
            g.MaterialisedEntityId = 21;
            states[1].PendingFleets[0] = g;
            w.UtcNow = T0.AddSeconds(160);
            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);
            string handoff = Line(log, "FIGHT_HANDOFF [r1] SPRT dir=physical");
            Assert.NotNull(handoff);
            Assert.Contains("turns_fought=1/3 bracket=2000m", handoff);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            // Part of it was abstract, so the Guard still standing at the end repels nothing: the
            // handoff path is decided by the ratio as the duel's is.
            w.UtcNow = T0.AddSeconds(301);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.Contains(" src=handoff (grids T2-T3", Line(log, "RAID_BREAKS_GUARD [r1] SPRT"));
            Assert.Null(Line(log, "RAID_REPELLED"));
        }

        [Fact]
        public void APointBlankBracketStillSpawnsClearOfTheOpponent()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            w.Tuning.CombatBaseDamageFraction = 0.01;
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            sprt.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetNodeId = "N1", CurrentStopKind = FleetStopKind.Engage,
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(300), ShipPrefabId = "Ram", ConvoySize = 1,
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, new List<string> { "Ram" }), FleetId = "r1",
                LastResolvedSegment = -1,
            });
            var states = new List<FactionState> { sprt, impr };
            // A raid whose only guns reach 0 m, against nothing that moves, fights point blank.
            w.UtcNow = T0.AddSeconds(250);
            MeetingSpawn spawn = PendingFleetResolver.BeforeMaterialise(states, w, w.Tuning, 0, 0, new SeededRandom(3), new List<string>());
            Assert.True(spawn.InMeeting);
            Assert.Equal(MeetingSpawn.MinSeparationM, spawn.SeparationM);
            Assert.Equal(-1, spawn.OpponentSi);
        }

        [Fact]
        public void AnElapsedStretchThatEndsTheFightEndsTheDwell()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" }, materialised: false);
            var log = new List<string>();

            w.UtcNow = T0.AddSeconds(250);
            PendingFleetResolver.BeforeMaterialise(states, w, w.Tuning, 0, 0, new SeededRandom(3), log);
            Assert.True(states[0].PendingFleets[0].Fight.Decided.HasValue);
            Assert.Equal(T0.AddSeconds(250), states[0].PendingFleets[0].EstimatedArrivalUtc);

            Resolve(states, w, log, 250);
            string meeting = Line(log, "RAID_BREAKS_GUARD [b.f1.atk] SPRT ratio=1.00");
            Assert.NotNull(meeting);
            Assert.Contains(" src=duel (T1(", meeting);
            Assert.Contains("[b.f1.def] IMPR outcome=destroyed", Line(log, "BENCH_RESOLVED f1"));
        }

        [Fact]
        public void AFightTheGridsNeverTookPartInIsTheOrdinaryDuel()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" }, materialised: false);
            var log = new List<string>();
            Resolve(states, w, log, 301);
            Assert.Contains(" src=duel (T1(", Line(log, "RAID_BREAKS_GUARD [b.f1.atk] SPRT"));
            Assert.Null(Line(log, "FIGHT_HANDOFF"));
        }

        [Fact]
        public void ARealRaidInTheWorldIsDecidedByItsGrids()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            w.Graph.Add(new Vec3(0, 0, 0), 0.6, "N1").OwnerFactionTag = "IMPR";
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            sprt.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetNodeId = "N1", CurrentStopKind = FleetStopKind.Engage,
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(300), ShipPrefabId = "Gunship", ConvoySize = 1,
                FleetId = "r1", MaterialisedEntityId = 11, LastResolvedSegment = -1,
            });
            var states = new List<FactionState> { sprt, impr };
            var log = new List<string>();
            w.UtcNow = T0.AddSeconds(10);

            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);
        }

        // ---- the hulls the grids kept come home ----------------------------------------------------

        /// <summary>A real (non-bench) raid at N1 with a Scout Guard, both in the world.</summary>
        private static List<FactionState> RealMeeting(WorldModel w, Catalogue cat, int gunships)
        {
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "H1");
            home.OwnerFactionTag = "SPRT"; home.HoldStrength = 1.0;
            SpatialNode target = w.Graph.Add(new Vec3(5, 0, 0), 1.0, "N1");
            target.OwnerFactionTag = "IMPR"; target.HoldStrength = 1.0;
            var squadron = new List<string>();
            for (int i = 0; i < gunships; i++) squadron.Add("Gunship");
            FactionState sprt = State("SPRT"), impr = State("IMPR");
            sprt.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetTag = "IMPR", TargetNodeId = "N1", OriginNodeId = "H1",
                ReturnNodeId = "H1", OriginPosKm = target.Position, TargetPosKm = target.Position,
                CurrentStopKind = FleetStopKind.Engage, DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(300),
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "H1", Kind = FleetStopKind.Return } },
                ShipPrefabId = "Gunship", ConvoySize = gunships, Squadron = squadron,
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, squadron), FleetId = "r1",
                MaterialisedEntityId = 11, LastResolvedSegment = -1,
            });
            impr.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "IMPR", TargetNodeId = "N1", CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = T0.AddSeconds(-10), EstimatedArrivalUtc = T0.AddSeconds(-10), Resolved = true,
                ShipPrefabId = "Scout", ConvoySize = 1, TargetPosKm = target.Position,
                WeaponBands = SquadronAssembler.WeaponBandsOf(cat, new List<string> { "Scout" }), FleetId = "g1",
                MaterialisedEntityId = 21, LastResolvedSegment = -1,
            });
            return new List<FactionState> { sprt, impr };
        }

        [Fact]
        public void HullsTheGridsKeptComeHomeWhateverTheRatio()
        {
            foreach (bool gridsDecide in new[] { true, false })
            {
                Catalogue cat = Cat();
                WorldModel w = World(cat);
                List<FactionState> states = RealMeeting(w, cat, 3);
                var log = new List<string>();
                Resolve(states, w, log, 10);

                // One hull destroyed, the other two at 0.6: 6000 of 15000.
                PendingFleetAction a = states[0].PendingFleets[0];
                a.MarkHullLostAt(2);
                a.SetIntegrity(0, 0.6);
                a.SetIntegrity(1, 0.6);
                states[0].PendingFleets[0] = a;
                if (gridsDecide)
                {
                    // The defence was destroyed in the world, so the raid lands at its ratio.
                    PendingFleetAction dead = states[1].PendingFleets[0];
                    dead.MaterialisedEntityId = -1;
                    dead.ForcedRatio = 0;
                    dead.ForcedRatioCause = "grid_destroyed";
                    states[1].PendingFleets[0] = dead;
                }
                else
                {
                    // The control: the Guard leaves in turn 2 and the duel fights turn 3 (a handoff).
                    PendingFleetAction g = states[1].PendingFleets[0];
                    g.MaterialisedEntityId = 0;
                    states[1].PendingFleets[0] = g;
                    w.UtcNow = T0.AddSeconds(150);
                    PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);
                }
                Resolve(states, w, log, 301);
                Assert.Equal(gridsDecide, states[0].PendingFleets[0].HullsMeasured);
                Assert.Equal(FleetStopKind.Return, states[0].PendingFleets[0].CurrentStopKind);

                // The grids fly home out of range; the return leg resolves abstractly.
                PendingFleetAction home1 = states[0].PendingFleets[0];
                if (!gridsDecide)
                {
                    // The duel had the last word; the same 0.40 is forced for the comparison.
                    Assert.Contains("src=handoff", Line(log, "RAID_BREAKS_GUARD [r1] SPRT"));
                    home1.ForcedRatio = 0.4;
                }
                home1.MaterialisedEntityId = 0;
                home1.EscortEntityIds = null;
                states[0].PendingFleets[0] = home1;
                Resolve(states, w, log, 3600);
                string returned = Line(log, "FLEET_RETURNED [r1] SPRT");
                Assert.NotNull(returned);
                SpatialNode home = w.Graph.Nodes["H1"];
                if (gridsDecide)
                {
                    // At 0.40 the raid still did what 0.40 does, but both hulls still flying land.
                    Assert.Contains("STATION_DEFENSE [r1] SPRT ratio=0.40", string.Join("\n", log));
                    Assert.Contains("ratio=0.40 ship_returned hulls_returned=2/3", returned);
                    // No recipe in this world, so the damaged hulls land in stock (REPAIR_SKIPPED).
                    Assert.Equal(2, home.ShipStock["Gunship"]);
                }
                else
                {
                    // The duel's turn spent none of the raid's capacity (the grids had taken it:
                    // surviving=1.00), so the capacity write-off takes no hull and both land. Before
                    // FAC-172 the threshold wrote off both at 0.40.
                    Assert.Contains("surviving=1.00", Line(log, "HULL_DAMAGED [r1] SPRT cause=guard_duel"));
                    Assert.Contains("ratio=0.40 ship_returned hulls_returned=2/3", returned);
                    Assert.Null(Line(log, "SHIP_LOST [r1]"));
                    Assert.Equal(2, home.ShipStock["Gunship"]);
                }
            }
        }

        [Fact]
        public void ARepelledRaidLeavesAtOnceWithNoEffectAndBringsItsHullsHome()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = RealMeeting(w, cat, 3);
            var log = new List<string>();
            Resolve(states, w, log, 10);
            PendingFleetAction a = states[0].PendingFleets[0];
            a.MarkHullLostAt(2);
            a.SetIntegrity(0, 0.6);
            states[0].PendingFleets[0] = a;
            Resolve(states, w, log, 301);

            Assert.Contains("ratio=0.53 outcome=repelled src=physical", Line(log, "RAID_REPELLED [r1] SPRT vs [g1] IMPR @N1"));
            Assert.NotNull(Line(log, "FLEET_RETREATED [r1] SPRT kind=Raid @N1 reason=repelled"));
            Assert.Null(Line(log, "RAID_RESOLVED"));
            // No effect on the node, the attack filed as repelled on both sides and as an incident.
            Assert.Equal(1.0, w.Graph.Nodes["N1"].HoldStrength, 6);
            RaidIncident incident = Assert.Single(w.Incidents);
            Assert.True(incident.Repelled);
            Assert.Equal("SPRT", incident.Attacker);
            Assert.Single(states[0].Fights);
            Assert.Single(states[1].Fights);
            // Straight onto the way home, no dwell left.
            PendingFleetAction home1 = states[0].PendingFleets[0];
            Assert.Equal(FleetStopKind.Return, home1.CurrentStopKind);
            Assert.Equal("repelled", home1.RetreatReason);
            Assert.True(home1.HullsMeasured);

            home1.MaterialisedEntityId = 0;
            home1.EscortEntityIds = null;
            states[0].PendingFleets[0] = home1;
            Resolve(states, w, log, 3600);
            Assert.Contains("ship_returned hulls_returned=2/3", Line(log, "FLEET_RETURNED [r1] SPRT"));
            Assert.Equal(2, w.Graph.Nodes["H1"].ShipStock["Gunship"]);
        }

        [Fact]
        public void AStationWhoseTurretsStillWorkRepelsTheRaidWhateverItsZone()
        {
            foreach (double stationDps in new[] { 500.0, 0.0 })
            {
                Catalogue cat = Cat();
                WorldModel w = World(cat);
                List<FactionState> states = RealMeeting(w, cat, 2);
                // The station's grid read from the world: working turrets, or none left.
                w.StationWeaponReading = id => id == "N1" ? (double?)stationDps : null;
                var log = new List<string>();
                Resolve(states, w, log, 10);
                // The raid's grids destroyed it, so the hook credited them with damage.
                PendingFleetAction shooter = states[0].PendingFleets[0];
                shooter.FightDamageDealt = 4000;
                states[0].PendingFleets[0] = shooter;
                // No Guard left standing: its grid was destroyed in the world.
                PendingFleetAction dead = states[1].PendingFleets[0];
                dead.MaterialisedEntityId = -1;
                dead.ForcedRatio = 0;
                dead.ForcedRatioCause = "grid_destroyed";
                states[1].PendingFleets[0] = dead;
                Resolve(states, w, log, 301);

                if (stationDps > 0)
                {
                    string line = Line(log, "RAID_REPELLED [r1] SPRT vs station @N1 ratio=1.00 outcome=repelled src=physical");
                    Assert.NotNull(line);
                    Assert.Contains("defence_standing station_dps=500", line);
                    Assert.Null(Line(log, "STATION_DEFENSE"));
                    Assert.Equal(1.0, w.Graph.Nodes["N1"].HoldStrength, 6);
                    Assert.True(Assert.Single(w.Incidents).Repelled);
                    Assert.Equal("repelled", states[0].PendingFleets[0].RetreatReason);
                }
                else
                {
                    Assert.Contains("src=physical", Line(log, "STATION_DEFENSE [r1] SPRT ratio=1.00"));
                    Assert.Null(Line(log, "RAID_REPELLED"));
                }
            }
        }

        /// <summary>A raid on its dwell at N1, which has a station and no Guard, materialised from the
        /// start so the grids decide it.</summary>
        private static List<FactionState> StationOnlyMeeting(WorldModel w, Catalogue cat, string prefab)
        {
            List<FactionState> states = RealMeeting(w, cat, 1);
            PendingFleetAction raid = states[0].PendingFleets[0];
            raid.ShipPrefabId = prefab;
            raid.Squadron = new List<string> { prefab };
            raid.WeaponBands = SquadronAssembler.WeaponBandsOf(cat, raid.Squadron);
            states[0].PendingFleets[0] = raid;
            states[1].PendingFleets.Clear();
            // A station with no turrets: only the flat floor (StationDefensePower) defends it.
            w.Graph.Nodes["N1"].StationPrefabId = "Outpost";
            w.StationWeaponReading = id => id == "N1" ? (double?)0.0 : null;
            return states;
        }

        /// <summary>FAC-489: the grids fought all of the dwell and the raid hit nothing — not the
        /// station, not its zone. It did not get through: no hold strip, no loot, no zone down.</summary>
        [Fact]
        public void ARaidWhoseGridsHitNothingIsRepelledByAnUnarmedStation()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = StationOnlyMeeting(w, cat, "Gunship");
            var log = new List<string>();
            Resolve(states, w, log, 10);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);
            Resolve(states, w, log, 301);

            string repelled = Line(log, "RAID_REPELLED [r1] SPRT vs station @N1");
            Assert.NotNull(repelled);
            Assert.Contains("outcome=repelled src=physical (grids T1-T3 atk=1.00 def=1.00 no_hits", repelled);
            Assert.Null(Line(log, "STATION_DEFENSE"));
            Assert.Null(Line(log, "RAID_RESOLVED"));
            SpatialNode n = w.Graph.Nodes["N1"];
            Assert.Equal(1.0, n.HoldStrength, 6);
            Assert.Equal(default(DateTime), n.SafeZoneDownUntilUtc);
            Assert.True(Assert.Single(w.Incidents).Repelled);
        }

        /// <summary>FAC-489: what the zone absorbed counts as an attack, and so does damage. Either one
        /// lets the same raid land, with the zone knocked down as a landed raid does.</summary>
        [Fact]
        public void ARaidThatHitTheZoneOrTheStationLands()
        {
            foreach (bool zone in new[] { true, false })
            {
                Catalogue cat = Cat();
                WorldModel w = World(cat);
                List<FactionState> states = StationOnlyMeeting(w, cat, "Gunship");
                var log = new List<string>();
                Resolve(states, w, log, 10);
                PendingFleetAction a = states[0].PendingFleets[0];
                if (zone) a.FightZoneHits = 2500;
                else a.FightDamageDealt = 2500;
                states[0].PendingFleets[0] = a;
                Resolve(states, w, log, 301);

                string landed = Line(log, "STATION_DEFENSE [r1] SPRT ratio=1.00 @N1 src=physical");
                Assert.NotNull(landed);
                Assert.DoesNotContain("no_hits", landed);
                if (zone) Assert.Contains("zone atk=2500", landed);
                else Assert.Contains("hit atk=2500", landed);
                Assert.Null(Line(log, "RAID_REPELLED"));
                Assert.True(w.Graph.Nodes["N1"].SafeZoneDownUntilUtc > w.UtcNow);
            }
        }

        /// <summary>FAC-488: a waved raid whose grid is gone mid-dwell — the host closed its site and
        /// despawned the hull — is handed to the duel for the turns left, not judged by grids that are
        /// no longer there. The station's floor out-fights an unarmed hull, so it does not land.</summary>
        [Fact]
        public void AWavedRaidWhoseGridIsGoneMidDwellIsDecidedByTheDuel()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = StationOnlyMeeting(w, cat, "Unarmed");
            w.WavedMeetings.Add("r1");
            var log = new List<string>();
            Resolve(states, w, log, 10);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            // The launch gave up: the site closes and the grid is despawned.
            w.UtcNow = T0.AddSeconds(150);
            w.WavedMeetings.Remove("r1");
            PendingFleetAction a = states[0].PendingFleets[0];
            a.MaterialisedEntityId = 0;
            states[0].PendingFleets[0] = a;
            PendingFleetResolver.ObserveFights(states, w, w.Tuning, new SeededRandom(3), log);
            Assert.NotNull(Line(log, "FIGHT_HANDOFF [r1] SPRT dir=abstract"));
            Assert.False(states[0].PendingFleets[0].Fight.Physical);

            Resolve(states, w, log, 301);
            string outcome = Line(log, "COMBAT_OUTCOME [r1] SPRT vs station IMPR @N1");
            Assert.NotNull(outcome);
            Assert.Contains("src=handoff", outcome);
            Assert.DoesNotContain("attack=Landed", outcome);
            Assert.Null(Line(log, "STATION_DEFENSE"));
            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Equal(default(DateTime), w.Graph.Nodes["N1"].SafeZoneDownUntilUtc);
        }

        /// <summary>FAC-492: a waved raid on a station alone whose last hull was written off is decided by
        /// the site at once, not left to sit out its dwell: it resolves on the next pass, repelled with
        /// its hull lost, and takes nothing.</summary>
        [Fact]
        public void AWavedRaidThatLostItsLastHullIsDecidedAtOnceAndTakesNothing()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = StationOnlyMeeting(w, cat, "Gunship");
            w.WavedMeetings.Add("r1");
            var log = new List<string>();
            Resolve(states, w, log, 10);
            Assert.True(states[0].PendingFleets[0].Fight.Physical);

            // 60 s in, the site writes the only hull off and decides with the attacker not standing.
            w.UtcNow = T0.AddSeconds(60);
            PendingFleetAction a = states[0].PendingFleets[0];
            DisabledHull.WriteOffHull(ref a, 0, "Gunship", cat);
            a.MaterialisedEntityId = 0;
            states[0].PendingFleets[0] = a;
            Assert.True(PendingFleetResolver.DecideWaved(states, w, w.Tuning, 0, 0, false, log));
            Resolve(states, w, log, 61);

            string repelled = Line(log, "RAID_REPELLED [r1] SPRT vs station @N1");
            Assert.NotNull(repelled);
            Assert.Contains("waves attack_spent", repelled);
            Assert.Contains("hulls_lost atk=1/1", repelled);
            Assert.Null(Line(log, "STATION_DEFENSE"));
            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Equal(1.0, w.Graph.Nodes["N1"].HoldStrength, 6);
            Assert.Equal(default(DateTime), w.Graph.Nodes["N1"].SafeZoneDownUntilUtc);
        }

        [Fact]
        public void ADefenceThatBrokeOffInTheWorldLetsTheRaidLand()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Gunship" });
            var log = new List<string>();
            Resolve(states, w, log, 10);
            // The Guard's hull shot to a tenth: below every break-off fraction.
            PendingFleetAction d = states[1].PendingFleets[0];
            d.SetIntegrity(0, 0.1);
            states[1].PendingFleets[0] = d;
            Resolve(states, w, log, 301);

            Assert.Contains(" src=physical (grids T1-T3 atk=1.00 def=0.10 broke_off=def",
                            Line(log, "RAID_BREAKS_GUARD [b.f1.atk] SPRT ratio=1.00"));
            Assert.NotNull(Line(log, "GUARD_BROKE_OFF [b.f1.def] IMPR"));
            Assert.Null(Line(log, "RAID_REPELLED"));
            Assert.Contains("outcome=landed ratio=1.00 cause=physical", Line(log, "BENCH_RESOLVED f1 [b.f1.atk] SPRT"));
        }

        [Fact]
        public void AMeasuredFleetWhoseGridDiedLaterLandsNothingItLost()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "H1");
            home.OwnerFactionTag = "SPRT"; home.HoldStrength = 1.0;
            var st = State("SPRT");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", TargetNodeId = "H1", OriginNodeId = "H1",
                CurrentStopKind = FleetStopKind.Return, DepartedUtc = T0, EstimatedArrivalUtc = T0,
                ShipPrefabId = "Gunship", ConvoySize = 1, FleetId = "r2", HullsMeasured = true,
                ForcedRatio = 0, ForcedRatioCause = "grid_destroyed", LastResolvedSegment = -1,
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);
            Assert.Contains("ship_lost", Line(log, "FLEET_RETURNED [r2]"));
            Assert.Contains("why=forced:grid_destroyed", Line(log, "SHIP_LOST [r2] SPRT"));
            Assert.False(home.ShipStock.ContainsKey("Gunship"));
        }

        /// <summary>A return leg the hull-return threshold writes off names what it lost, as the
        /// one-way terminal does: the stall that destroyed ISBT's only hull logged nothing but
        /// <c>FLEET_RETURNED … ratio=0.00 ship_lost</c>.</summary>
        [Fact]
        public void AStalledHullWrittenOffOnItsReturnLegIsLoggedAndRecorded()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "H1");
            home.OwnerFactionTag = "ISBT"; home.HoldStrength = 1.0;
            var st = State("ISBT");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "ISBT", TargetNodeId = "H1", OriginNodeId = "N9",
                CurrentStopKind = FleetStopKind.Return, DepartedUtc = T0, EstimatedArrivalUtc = T0,
                ShipPrefabId = "Gunship", ConvoySize = 1, FleetId = "s1",
                ForcedRatio = 0, ForcedRatioCause = "grid_stalled", LastResolvedSegment = -1,
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);

            Assert.Contains("ratio=0.00 ship_lost", Line(log, "FLEET_RETURNED [s1] ISBT"));
            Assert.Contains("prefab=Gunship count=1 from=N9 to=H1 ratio=0.00 why=forced:grid_stalled",
                            Line(log, "SHIP_LOST [s1] ISBT"));
            FactionEvent lost = Assert.Single(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost);
            Assert.Equal("ISBT", lost.ActorTag);
            Assert.Equal(1, lost.Amount);
            Assert.False(home.ShipStock.ContainsKey("Gunship"));
        }

        private static PendingFleetAction DuelRaid(string id, List<string> squadron, string prefab, int convoy)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "FCTM", TargetTag = "IGSL", TargetNodeId = "N68", OriginNodeId = "H1",
                ReturnNodeId = "H1", CurrentStopKind = FleetStopKind.Engage, DepartedUtc = T0, EstimatedArrivalUtc = T0,
                ShipPrefabId = prefab, ConvoySize = convoy, Squadron = squadron, FleetId = id,
                ForcedRatioCause = PendingFleetResolver.SourceDuel, LastResolvedSegment = -1,
            };
        }

        /// <summary>Lands <paramref name="fleet"/> at H1 through its return leg and answers the log.</summary>
        private static List<string> LandHome(WorldModel w, PendingFleetAction fleet, double ratio)
        {
            fleet.ForcedRatio = ratio;
            fleet.CurrentStopKind = FleetStopKind.Return;
            fleet.TargetNodeId = "H1";
            var st = State("FCTM");
            st.PendingFleets.Add(fleet);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);
            return log;
        }

        private static SpatialNode Home(WorldModel w)
        {
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "H1");
            home.OwnerFactionTag = "FCTM"; home.HoldStrength = 1.0;
            return home;
        }

        /// <summary>Under the threshold a duel writes hulls off by the capacity it spent, as a
        /// broken-off attack does, and the rest come home: 15,010 DPS at 0.46 spends 8,105, which takes
        /// the Scout and one Gunship (5,010) and spreads the other 3,095 over the two Gunships left.
        /// Before, the return leg wrote off all four.</summary>
        [Fact]
        public void UnderTheThreshold_ADuelWritesHullsOffByCapacityAndTheRestComeHome()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode home = Home(w);
            PendingFleetAction raid = DuelRaid("q2", new List<string> { "Gunship", "Scout", "Gunship", "Gunship" }, "Gunship", 0);
            var log = new List<string>();

            int lost = PendingFleetResolver.SettleDuelHulls(ref raid, 0.46, 0.46, PendingFleetResolver.SourceDuel,
                                                            "repelled", w, log);

            Assert.Equal(2, lost);
            Assert.True(raid.HullsSettled);
            Assert.Equal(new[] { "Gunship", "Gunship" }, raid.HullsToReturn);
            Assert.Equal(1.0 - 3095.4 / 10000.0, raid.IntegrityOf(0), 4);
            Assert.Equal(1.0 - 3095.4 / 10000.0, raid.IntegrityOf(1), 4);
            Assert.Contains("count=2 from=H1 to=N68 ratio=0.46 why=repelled hulls=Scout,Gunship", Line(log, "SHIP_LOST [q2] FCTM"));
            FactionEvent written = Assert.Single(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost);
            Assert.Equal(2, written.Amount);

            List<string> back = LandHome(w, raid, 0.46);
            Assert.Contains("ratio=0.46 ship_returned hulls_returned=2/4", Line(back, "FLEET_RETURNED [q2] FCTM"));
            Assert.Null(Line(back, "SHIP_LOST"));
            Assert.Equal(2, home.ShipStock["Gunship"]);
        }

        /// <summary>Above the threshold nothing changes: every hull keeps the duel's attrition and
        /// none is written off, so the terminal lands them on the ratio as it always did.</summary>
        [Fact]
        public void AboveTheThreshold_EveryHullKeepsTheDuelsAttritionAndNoneIsWrittenOff()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            PendingFleetAction raid = DuelRaid("q3", new List<string> { "Gunship", "Scout", "Gunship" }, "Gunship", 0);
            var log = new List<string>();

            Assert.Equal(0, PendingFleetResolver.SettleDuelHulls(ref raid, 0.6, 0.6, PendingFleetResolver.SourceDuel,
                                                                 "guard_duel", w, log));
            Assert.False(raid.HullsSettled);
            Assert.Equal(3, raid.HullsToReturn.Count);
            Assert.Equal(0.6, raid.IntegrityOf(0), 6);
            Assert.Null(Line(log, "SHIP_LOST"));
        }

        /// <summary>A meeting the grids decided is settled by what they measured, never twice.</summary>
        [Fact]
        public void APhysicalResultIsNotWrittenOffAgain()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            PendingFleetAction raid = DuelRaid("q4", new List<string> { "Gunship", "Gunship" }, "Gunship", 0);
            var log = new List<string>();

            Assert.Equal(0, PendingFleetResolver.SettleDuelHulls(ref raid, 0.3, 0.3, PendingFleetResolver.SourcePhysical,
                                                                 "repelled", w, log));
            Assert.False(raid.HullsSettled);
            Assert.Equal(2, raid.HullsToReturn.Count);
        }

        /// <summary>A convoy has no roster: its count shrinks by what the capacity takes, and the
        /// one-hull floor never brings a written-off hull back.</summary>
        [Fact]
        public void AConvoyLosesPartOfItsCount_AndATotalWriteOffKeepsTheThreshold()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode home = Home(w);
            // 20,000 at 0.46 spends 10,800: two hulls, and the other 800 over the two left.
            PendingFleetAction convoy = DuelRaid("c1", null, "Gunship", 4);
            var log = new List<string>();
            Assert.Equal(2, PendingFleetResolver.SettleDuelHulls(ref convoy, 0.46, 0.46, PendingFleetResolver.SourceDuel,
                                                                 "station_duel", w, log));
            Assert.Equal(2, convoy.ConvoySize);
            Assert.True(convoy.HullsSettled);
            Assert.Equal(0.92, convoy.IntegrityOf(0), 6);
            List<string> back = LandHome(w, convoy, 0.46);
            Assert.Contains("ship_returned", Line(back, "FLEET_RETURNED [c1] FCTM"));
            Assert.Equal(2, home.ShipStock["Gunship"]);

            // Nothing left of the capacity takes every hull: the convoy is left to the threshold whole.
            PendingFleetAction gone = DuelRaid("c2", null, "Gunship", 4);
            Assert.Equal(0, PendingFleetResolver.SettleDuelHulls(ref gone, 0.0, 0.0, PendingFleetResolver.SourceDuel,
                                                                 "station_duel", w, log));
            Assert.Equal(4, gone.ConvoySize);
            Assert.False(gone.HullsSettled);
        }

        /// <summary>A duel-repelled squadron at or under the threshold that was never settled hull by
        /// hull — persisted before <see cref="PendingFleetAction.HullsSettled"/> existed — is still
        /// written off whole, by one line and one event that count its hulls.</summary>
        [Fact]
        public void ADuelRepelledSquadronWrittenOffOnItsReturnLegCountsItsHulls()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "H1");
            home.OwnerFactionTag = "FCTM"; home.HoldStrength = 1.0;
            var st = State("FCTM");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "FCTM", TargetNodeId = "H1", OriginNodeId = "N68",
                CurrentStopKind = FleetStopKind.Return, DepartedUtc = T0, EstimatedArrivalUtc = T0,
                ShipPrefabId = "Gunship", Squadron = new List<string> { "Gunship", "Gunship", "Gunship" },
                FleetId = "q1", RetreatReason = "repelled",
                ForcedRatio = 0.46, ForcedRatioCause = PendingFleetResolver.SourceDuel, LastResolvedSegment = -1,
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);

            Assert.Contains("ratio=0.46 ship_lost hulls_returned=0/3", Line(log, "FLEET_RETURNED [q1] FCTM"));
            Assert.Contains("why=forced:duel hulls_returned=0/3", Line(log, "SHIP_LOST [q1] FCTM"));
            FactionEvent lost = Assert.Single(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost);
            Assert.Equal(3, lost.Amount);
            Assert.False(home.ShipStock.ContainsKey("Gunship"));
        }

        // ---- what the learner gets ---------------------------------------------------------------

        [Fact]
        public void TheLearnerReadsTheHookWhenTheGridsFoughtItAll()
        {
            var o = new PhysicalFightOutcome
            {
                Source = PendingFleetResolver.SourcePhysical,
                AttackerDamageDealt = 900, AttackerDamageTaken = 300,
                DefenderDamageDealt = 300, DefenderDamageTaken = 900,
                AttackerCapacityDealt = 1, DefenderCapacityDealt = 2,
            };
            double dealt, taken;
            PendingFleetResolver.MeasuredDamage(o, true, out dealt, out taken);
            Assert.Equal(900, dealt);
            Assert.Equal(300, taken);
            PendingFleetResolver.MeasuredDamage(o, false, out dealt, out taken);
            Assert.Equal(300, dealt);
            Assert.Equal(900, taken);

            // Part of it abstract: the capacity figures, which cover the duel's turns too.
            o.Source = PendingFleetResolver.SourceHandoff;
            PendingFleetResolver.MeasuredDamage(o, true, out dealt, out taken);
            Assert.Equal(1, dealt);
            Assert.Equal(2, taken);
        }

        [Fact]
        public void APhysicalFightTeachesBothSidesOnlyWhenTheBenchFilesIt()
        {
            foreach (bool ledger in new[] { false, true })
            {
                Catalogue cat = Cat();
                WorldModel w = World(cat);
                List<FactionState> states = Bench(w, cat, new[] { "Gunship", "Gunship" }, new[] { "Scout" },
                                                  flags: ledger ? BenchFlags.Ledger : BenchFlags.None);
                var log = new List<string>();
                Resolve(states, w, log, 10);
                PendingFleetAction a = states[0].PendingFleets[0];
                a.FightDamageDealt = 4000;
                a.FightDamageTaken = 100;
                states[0].PendingFleets[0] = a;
                Resolve(states, w, log, 301);
                Assert.Equal(ledger ? 1L : 0L, states[0].TacticMemory.Updates);
                Assert.Equal(ledger ? 1L : 0L, states[1].TacticMemory.Updates);
                // Filed once when stopped, not again when its leg ends.
                Assert.Equal(ledger ? 1 : 0, states[0].Fights.Count);
                // Repelled, and still learned from: the damage share is the reward either way.
                Assert.Contains("hit atk=4000 def=0", Line(log, "RAID_REPELLED [b.f1.atk] SPRT"));
            }
        }

        [Fact]
        public void AMaterialisedLeadFliesItsFactionsLearnedPick()
        {
            Catalogue cat = Cat();
            WorldModel w = World(cat);
            List<FactionState> states = Bench(w, cat, new[] { "Gunship" }, new[] { "Scout" });
            TacticArm arm;
            BreakOffLevel breakOff;
            Assert.True(PendingFleetResolver.LearnedStanceFor(states, w, w.Tuning, 0, 0, out arm, out breakOff));
            Assert.True(PendingFleetResolver.LearnedStanceFor(states, w, w.Tuning, 1, 0, out arm, out breakOff));

            TacticOrder order = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = 5, AnchorEntityId = 7,
                Learned = new TacticOrder { Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High },
            });
            Assert.Equal(TacticArm.Kite, order.Arm);
            Assert.Equal(BreakOffLevel.High, order.BreakOff);
            Assert.Equal(5L, order.TargetEntityId);
            Assert.Equal(TacticSource.Learned, order.Source);
            Assert.Equal(TacticResolver.DefaultArm, TacticResolver.Resolve(new TacticRequest()).Arm);

            // A fleet in no dwell fight, or a faction that learns nothing there, keeps the default.
            w.TacticLearning = false;
            var real = new PendingFleetAction { Kind = ActionKind.Raid, ActorTag = "SPRT", TargetNodeId = "N9",
                                                CurrentStopKind = FleetStopKind.Engage, EstimatedArrivalUtc = T0.AddSeconds(300) };
            states[0].PendingFleets.Add(real);
            Assert.False(PendingFleetResolver.LearnedStanceFor(states, w, w.Tuning, 0, 1, out arm, out breakOff));
        }

        // ---- geometry and persistence ----------------------------------------------------------

        [Fact]
        public void ASpawnPointSitsAtTheBracketFromItsOpponent()
        {
            var spawn = new MeetingSpawn { InMeeting = true, SeparationM = 800, FallbackDirection = new Vec3(0, 0, 2) };
            Vec3 p = spawn.PositionKm(new Vec3(3, 4, 0), new Vec3(0, 0, 0));
            Assert.Equal(0.48, p.X, 6);
            Assert.Equal(0.64, p.Y, 6);
            Assert.Equal(0.0, p.Z, 6);

            // On top of the opponent: out along the fallback.
            Vec3 q = spawn.PositionKm(new Vec3(1, 1, 1), new Vec3(1, 1, 1));
            Assert.Equal(1.8, q.Z, 6);
            Assert.Equal(1.0, q.X, 6);
        }

        [Fact]
        public void TheFightRecordRoundTripsAndIsOmittedWhenAbsent()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "A";
            var st = new FactionState { Tag = "A" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", OriginNodeId = "H1", TargetNodeId = "E1",
                CurrentStopKind = FleetStopKind.Engage,
                Fight = new FightProgress
                {
                    TurnsFought = 2, AttackerBase = 15000, DefenderBase = 5020, StationShare = 0.4,
                    AttackerHulls = 3, DefenderHulls = 1, FoughtPhysically = true, FoughtAbstractly = true,
                    Decided = new ShipDuel.Result { AttackerRatio = 0.5 },
                },
                HullsMeasured = true,
                HullsSettled = true,
            });
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Raid, ActorTag = "A", OriginNodeId = "H1", TargetNodeId = "E1" });

            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, graph.OwnedBy("A"), FactionStore.DefaultLogCap),
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            Assert.Equal(1, CountOf(json, "\"Fight\""));
            Assert.DoesNotContain("Decided", json, StringComparison.Ordinal);

            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());
            FightProgress p = back.PendingFleets[0].Fight;
            Assert.Equal(2, p.TurnsFought);
            Assert.Equal(15000, p.AttackerBase);
            Assert.Equal(5020, p.DefenderBase);
            Assert.Equal(0.4, p.StationShare, 6);
            Assert.Equal(3, p.AttackerHulls);
            Assert.True(p.FoughtPhysically && p.FoughtAbstractly && !p.Physical);
            Assert.False(p.Decided.HasValue);
            Assert.Null(back.PendingFleets[1].Fight);
            Assert.True(back.PendingFleets[0].HullsMeasured);
            Assert.False(back.PendingFleets[1].HullsMeasured);
            Assert.Equal(1, CountOf(json, "HullsMeasured"));
            Assert.True(back.PendingFleets[0].HullsSettled);
            Assert.False(back.PendingFleets[1].HullsSettled);
            Assert.Equal(1, CountOf(json, "HullsSettled"));
        }

        [Fact]
        public void AReadingDiffersOnlyWhenAHullMovedOrWentMissing()
        {
            var f = new PendingFleetAction { Squadron = new List<string> { "Gunship", "Gunship" } };
            Assert.False(HullDamage.Differs(null, 2, f));
            f.SetIntegrity(1, 0.5);
            Assert.True(HullDamage.Differs(null, 2, f));
            Assert.False(HullDamage.Differs(new List<double> { 1.0, 0.5 }, 2, f));
            // A whole hull written off leaves every remaining reading as it was: only the count moved.
            var whole = new PendingFleetAction { Squadron = new List<string> { "Gunship", "Gunship" } };
            whole.MarkHullLostAt(1);
            Assert.True(HullDamage.Differs(null, 2, whole));
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
