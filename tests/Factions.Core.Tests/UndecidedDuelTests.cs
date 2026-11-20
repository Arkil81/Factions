using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Undecided repels the attacker wherever the duel had a hand in the meeting (<c>src=duel</c>,
    /// <c>handoff</c>), not only where the grids decided it; and a side's range preference only names
    /// brackets its own weapons reach (<see cref="RangeSchedule.InReach"/>). The measured case: a
    /// one-hull Reinforce held a 24-hull raid at 2000 m for all three turns, neither side hurt the
    /// other, and the raid landed at ratio 1.00.
    /// </summary>
    public class UndecidedDuelTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>Jitter() = 1 exactly.</summary>
        private static IRandom NoJitter() { return new FixedRandom(0.5); }

        private static List<WeaponBand> Bands(params float[] dpsThenRange)
        {
            var bands = new List<WeaponBand>();
            for (int i = 0; i + 1 < dpsThenRange.Length; i += 2)
                bands.Add(new WeaponBand { Dps = dpsThenRange[i], RangeM = dpsThenRange[i + 1] });
            return bands;
        }

        /// <summary>Hulls with a dry mass and one thrust direction, so each side has a mobility.</summary>
        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _mass = new Dictionary<string, double>();
            private readonly Dictionary<string, float> _thrust = new Dictionary<string, float>();

            public void Add(string id, double massKg, float thrustN) { _mass[id] = massKg; _thrust[id] = thrustN; }

            public long CargoLiters(string prefabId) { return 0; }
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var d = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                float n;
                if (prefabId != null && _thrust.TryGetValue(prefabId, out n))
                    d[ThrustDirection.Backward] = new List<ThrustBand>
                    {
                        new ThrustBand { ThrusterType = "Hydrogen", ForceMagnitudeN = n,
                                         EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f },
                    };
                return d;
            }
            public double DryMassKg(string prefabId)
            {
                double m;
                return prefabId != null && _mass.TryGetValue(prefabId, out m) ? m : 0;
            }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>Brick: a slow raid hull (0.1 m/s²). Picket: a fast defender (100 m/s²).</summary>
        private static Catalogue Cat()
        {
            var cat = new Catalogue();
            cat.Add("Brick", 1e6, 1e5f);
            cat.Add("Picket", 1e4, 1e6f);
            cat.Add("Barge", 1e6, 2e5f);
            return cat;
        }

        private static WorldModel World(bool station)
        {
            WorldModel w = TestSupport.World(1);
            w.PrefabCatalogue = Cat();
            w.Tuning.CombatVariance = 0;
            w.Tuning.StationDefensePower = 20;
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "RVN1");
            home.OwnerFactionTag = "RVN";
            home.Radius = 40;
            SpatialNode n = w.Graph.Add(new Vec3(100, 0, 0), 0.6, "MERC1");
            n.OwnerFactionTag = "MERC";
            n.HoldStrength = 0.8;
            n.Radius = 40;
            if (station) n.StationPrefabId = "Outpost";
            return w;
        }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log) if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        /// <summary>A raid dispatched at MERC1 and carried to the end of its first dwell; the Guard (if
        /// any) is standing there when the dwell ends. Returns the states, raider first.</summary>
        private static List<FactionState> RaidToDwellEnd(WorldModel w, List<WeaponBand> raidBands, string raidPrefab,
            int raidHulls, List<WeaponBand> guardBands, string guardPrefab, List<string> log)
        {
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var states = new List<FactionState> { raider, merc };
            PendingFleetAction raid = PendingFleetResolver.CreatePending(ActionKind.Raid, "RVN",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "MERC1", Cost = 0 }, w, w.Tuning, immediate: false);
            raid.WeaponBands = raidBands;
            raid.ShipPrefabId = raidPrefab;
            raid.ConvoySize = raidHulls;
            raider.PendingFleets.Add(raid);

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            Assert.Equal(FleetStopKind.Engage, raider.PendingFleets[0].CurrentStopKind);

            DateTime dwellEnd = raider.PendingFleets[0].EstimatedArrivalUtc;
            if (guardBands != null)
                merc.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1", FleetId = "G00001",
                    DepartedUtc = dwellEnd.AddMinutes(-2), EstimatedArrivalUtc = dwellEnd.AddMinutes(-1),
                    Resolved = true, WeaponBands = guardBands, ShipPrefabId = guardPrefab, ConvoySize = 1,
                });
            w.UtcNow = dwellEnd;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            return states;
        }

        // ---- the range schedule is reach-aware ------------------------------------------------------

        [Fact]
        public void Standoff_DoesNotHoldABracketItsGunsCannotReach_EvenWhenTheFloorLandsThere()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            double[] brackets = RangeSchedule.Brackets(tuning);
            // The N157 shape: a defence whose ship reaches 800 m, a station floor of 20, and a raid
            // with no reach at 2000 m. At 2000 m only the floor lands, and the raid lands nothing, so the
            // ratio there is infinite — but the ship itself cannot shoot there.
            List<WeaponBand> guard = Bands(3150, 800);
            List<WeaponBand> raid = N157Raid();

            Assert.Equal(800, RangeSchedule.StandoffBracket(guard, 20, raid, 0, brackets));
        }

        [Fact]
        public void Standoff_StillHoldsALongBracketWhereItsGunsLand()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            double[] brackets = RangeSchedule.Brackets(tuning);
            // Six DPS of long guns and nothing else: every bracket is in reach, and 2000 m, where the raid
            // cannot reply, is best.
            Assert.Equal(2000, RangeSchedule.StandoffBracket(Bands(6, 2000), 0, Bands(156000, 800), 0, brackets));
        }

        [Theory]
        [InlineData(11f, false)]   // 11 of 111: under a tenth of the best bracket
        [InlineData(12f, true)]    // 12 of 112: over it
        public void ABracketIsInReach_AtATenthOfTheBestBracket(float longDps, bool inReach)
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            double[] brackets = RangeSchedule.Brackets(tuning);   // 0, 800, 2000
            bool[] reach = RangeSchedule.InReach(Bands(100, 0, longDps, 2000), brackets);
            Assert.True(reach[0]);
            Assert.Equal(inReach, reach[2]);
            Assert.Equal(inReach ? 2000 : 800,
                         RangeSchedule.StandoffBracket(Bands(100, 800, longDps, 2000), 0, Bands(1000, 800), 0, brackets));
        }

        [Fact]
        public void ASideWithNoGunsKeepsEveryBracket()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            double[] brackets = RangeSchedule.Brackets(tuning);
            Assert.True(RangeSchedule.InReach(null, brackets).All(r => r));
            // A station with only its floor still wants the range the enemy cannot reach.
            Assert.Equal(2000, RangeSchedule.StandoffBracket(null, 20, Bands(100, 800), 0, brackets));
            Assert.Equal(2000, RangeSchedule.LongestReach(null, 20, brackets));
            Assert.Equal(0, RangeSchedule.LongestReach(null, 0, brackets));
        }

        [Fact]
        public void Kite_OpensOnlyToTheLongestBracketItsGunsReach()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            double[] brackets = RangeSchedule.Brackets(tuning);
            // A token 5 DPS at 2000 m beside 100 at 800 m: the floor and the token gun both land at
            // 2000 m, but the guns that matter stop at 800.
            Assert.Equal(800, RangeSchedule.LongestReach(Bands(100, 800, 5, 2000), 20, brackets));
            Assert.Equal(2000, RangeSchedule.LongestReach(Bands(100, 800, 50, 2000), 20, brackets));
        }

        [Fact]
        public void TheAttacker_WhoSetsEveryTurnAgainstAStation_PicksWhereItsGunsLand()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            // A token 10 DPS at 2000 m against a station that reaches only 800 m: 2000 m used to be
            // "best" (the station cannot reply), though the raid lands a five-hundredth of its fire there.
            DuelTurn[] plan = RangeSchedule.Plan(Bands(5000, 800, 10, 2000), Bands(300, 800), 0,
                new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = 5 },
                new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = CombatMobility.Station },
                tuning);
            foreach (DuelTurn t in plan)
            {
                Assert.NotEqual(DuelSetter.Defender, t.SetBy);
                Assert.NotEqual(2000, t.RangeM);
            }
        }

        [Fact]
        public void TheCommitGate_PredictsTheReachAwareSchedule()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            tuning.CombatVariance = 0;
            List<WeaponBand> guard = Bands(3150, 800);
            List<WeaponBand> raid = N157Raid();
            var atk = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = 0.1 };
            var def = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = 100 };
            ShipDuel.Result r = ShipDuel.Fight(raid, guard, 20, tuning, NoJitter(), null, null, true, atk, def);
            DuelTurn[] plan = RangeSchedule.Plan(raid, guard, 20, atk, def, tuning);
            Assert.Equal(plan.Select(t => (int)t.RangeM).Take(r.BracketsFought.Length), r.BracketsFought);
            // Both now want 800 m; neither asks for the 2000 m only the station floor reaches.
            Assert.StartsWith("T1(800m set=both)", r.TurnSummary);
            Assert.DoesNotContain("2000m", r.TurnSummary);
        }

        // ---- undecided repels the attacker in the duel ---------------------------------------------

        [Fact]
        public void TheMeasuredShape_AnUntouchedGuardRepelsTheRaid_NotRatioOneBreakthrough()
        {
            WorldModel w = World(station: true);
            var log = new List<string>();
            // The N157 raid against a Reinforce that, unlike the Clipper, carries six DPS of guns that
            // reach 2000 m: it holds 2000 m every turn, neither side hurts the other, the defence stands.
            List<FactionState> states = RaidToDwellEnd(w, N157Raid(), "Brick", 24, Bands(6, 2000), "Picket", log);

            string repelled = Line(log, "RAID_REPELLED ");
            Assert.NotNull(repelled);
            Assert.Contains(" RVN vs [G00001] MERC @MERC1 ratio=1.00 outcome=repelled src=duel (T1(2000m set=def) atk=0 def=", repelled);
            Assert.Contains("T3(2000m set=def) atk=0", repelled);
            Assert.Contains("| defence_standing", repelled);
            Assert.Null(Line(log, "RAID_BREAKS_GUARD"));
            Assert.Null(Line(log, "RAID_RESOLVED"));

            string outcome = Line(log, "COMBAT_OUTCOME ");
            Assert.Contains("meeting=Guard attack=Repelled defence=Stood next=withdraw", outcome);
            Assert.Contains("src=duel", outcome);

            // Filed as the physical case files it: the retreat, the event, no effect on the node.
            Assert.NotNull(Line(log, "FLEET_RETREATED "));
            Assert.Contains("reason=repelled", Line(log, "FLEET_RETREATED "));
            PendingFleetAction raid = states[0].PendingFleets[0];
            Assert.Equal("repelled", raid.RetreatReason);
            Assert.Equal("duel", raid.ForcedRatioCause);
            Assert.False(raid.Intercepted);
            Assert.Equal(FleetStopKind.Return, raid.CurrentStopKind);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.RaidRepelled && e.ActorTag == "RVN");
            // Every hull went home: the event says repelled, not destroyed.
            FactionEvent ev = w.PendingEvents.Single(e => e.Kind == FactionEventKind.RaidRepelled);
            Assert.Equal(0, ev.Amount);
            Assert.Equal(0.0, ev.Ratio);
        }

        /// <summary>The N157 raid's bands as the catalogue gives them: nine Wakizashi (12100 DPS to
        /// 800 m, 1270 to 1400 m) and fourteen DS_Pirate_Scimitar (2500 to 800 m) — 155329 DPS, the
        /// dispatch's <c>gotDps</c> — and nothing that reaches 2000 m.</summary>
        private static List<WeaponBand> N157Raid()
        {
            var bands = new List<WeaponBand>();
            for (int i = 0; i < 9; i++) bands.AddRange(Bands(12100, 800, 1269.84131f, 1400));
            for (int i = 0; i < 14; i++) bands.AddRange(Bands(2500, 800));
            return bands;
        }

        [Fact]
        public void TheN157Meeting_TheClipperClosesToWhereItCanShoot_AndTheRaidBreaksIt()
        {
            WorldModel w = World(station: true);
            var log = new List<string>();
            // The Reinforce was one Clipper: 3150 DPS to 800 m and nothing beyond. Its def=6/7/6 at
            // 2000 m was the station floor (20 x 0.35); the ship itself landed nothing there. Reach-aware,
            // neither side asks for 2000 m, and a raid fifty times its size destroys it.
            RaidToDwellEnd(w, N157Raid(), "Brick", 24, Bands(3150, 800), "Picket", log);

            string broke = Line(log, "RAID_BREAKS_GUARD ");
            Assert.NotNull(broke);
            Assert.Contains("(T1(800m set=both)", broke);
            Assert.DoesNotContain("2000m", broke);
            Assert.Null(Line(log, "RAID_REPELLED"));
            Assert.Contains("attack=Landed defence=Destroyed", Line(log, "COMBAT_OUTCOME "));
        }

        [Fact]
        public void ARaidThatHurtsAStandingDefence_StillBreaksThrough()
        {
            WorldModel w = World(station: false);
            var log = new List<string>();
            // The raid is the fast side and holds 2000 m, where the Guard's guns do not reach: it takes
            // 42% of the Guard's capacity, the Guard stands above its break-off, and the raid lands.
            RaidToDwellEnd(w, Bands(400, 2000), "Picket", 1, Bands(1000, 800), "Brick", log);

            string broke = Line(log, "RAID_BREAKS_GUARD ");
            Assert.NotNull(broke);
            Assert.Contains("ratio=1.00", broke);
            Assert.Null(Line(log, "RAID_REPELLED"));
            Assert.Contains("attack=Landed defence=Stood", Line(log, "COMBAT_OUTCOME "));
        }

        [Fact]
        public void ARaidThatLostTheLargerShare_IsRepelledByADefenceThatStood()
        {
            WorldModel w = World(station: false);
            var log = new List<string>();
            // The Guard holds 2000 m on two turns, where only its 400 DPS of long guns reach; the raid
            // gets one turn at 800 m. The raid takes 350 of the Guard's 2400 (15%) and loses 420 of its
            // 1000 (42%): the defence stood and was winning, so the raid is repelled, not landed.
            RaidToDwellEnd(w, Bands(1000, 800), "Brick", 1, Bands(400, 2000, 2000, 0), "Barge", log);

            string repelled = Line(log, "RAID_REPELLED ");
            Assert.NotNull(repelled);
            Assert.Contains("ratio=0.58 outcome=repelled src=duel (T1(2000m set=def) atk=0 def=140 | T2(800m set=atk) atk=350 def=140 | T3(2000m set=def) atk=0 def=140 | defence_standing)", repelled);
            Assert.Null(Line(log, "RAID_BREAKS_GUARD"));
            Assert.Contains("meeting=Guard attack=Repelled defence=Stood next=withdraw", Line(log, "COMBAT_OUTCOME "));
            Assert.Contains("reason=repelled", Line(log, "FLEET_RETREATED "));
        }

        [Fact]
        public void AStationTheRaidDestroys_IsUnchanged()
        {
            WorldModel w = World(station: true);
            w.Graph.Nodes["MERC1"].StationWeapons = Bands(50, 800);
            var log = new List<string>();
            RaidToDwellEnd(w, Bands(5000, 2000), "Brick", 3, null, null, log);

            Assert.StartsWith("STATION_DEFENSE ", Line(log, "STATION_DEFENSE "));
            Assert.Contains("src=duel", Line(log, "STATION_DEFENSE "));
            Assert.Null(Line(log, "RAID_REPELLED"));
            Assert.Contains("meeting=Station attack=Landed defence=Destroyed", Line(log, "COMBAT_OUTCOME "));
        }

        [Fact]
        public void AStationTheRaidBarelyScratches_RepelsIt()
        {
            WorldModel w = World(station: true);
            w.Graph.Nodes["MERC1"].StationWeapons = Bands(1000, 800);
            var log = new List<string>();
            // The raid sets every turn at 2000 m, where only the floor answers it: it survives at 0.58
            // and takes about 5% of a 1020-DPS station, which stands.
            RaidToDwellEnd(w, Bands(50, 2000), "Brick", 1, null, null, log);

            string repelled = Line(log, "RAID_REPELLED ");
            Assert.NotNull(repelled);
            Assert.Contains(" RVN vs station @MERC1 ratio=", repelled);
            Assert.Contains("outcome=repelled src=duel", repelled);
            Assert.Contains("meeting=Station attack=Repelled defence=Stood", Line(log, "COMBAT_OUTCOME "));
        }

        [Fact]
        public void ABareNode_RepelsNothing()
        {
            WorldModel w = World(station: false);
            var log = new List<string>();
            RaidToDwellEnd(w, null, "Brick", 3, null, null, log);

            Assert.Null(Line(log, "RAID_REPELLED"));
            Assert.NotNull(Line(log, "STATION_DEFENSE "));
        }

        [Fact]
        public void ARaidWithNoDwellToLeave_IsSettledOnTheSpot()
        {
            WorldModel w = World(station: false);
            DateTime now = w.UtcNow;
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 1 };
            merc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1", FleetId = "G00001",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = Bands(6, 2000), ShipPrefabId = "Picket", ConvoySize = 1,
            });
            rvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", FleetId = "R00001",
                OriginNodeId = "RVN1", OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(100, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = N157Raid(), LastResolvedSegment = -1,
                ShipPrefabId = "Brick", ConvoySize = 24,
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { merc, rvn }, w, w.Tuning, NoJitter(), log);

            Assert.Contains("outcome=repelled src=duel", Line(log, "RAID_REPELLED "));
            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
        }

        [Fact]
        public void TheBenchDuel_CountsTheRunsAMeetingWouldRepel()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            var atk = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = 0.1 };
            var def = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid, Mobility = 100 };
            BenchDuelReport untouched = BenchScenario.Duel(N157Raid(), Bands(6, 2000), 20, tuning, 20, 7, true, atk, def);
            Assert.Equal(20, untouched.Repelled);
            Assert.Contains(" repelled=20", untouched.Format());

            BenchDuelReport closed = BenchScenario.Duel(N157Raid(), Bands(3150, 800), 20, tuning, 20, 7, true, atk, def);
            Assert.Equal(0, closed.Repelled);
            Assert.Equal(20, closed.AttackerWins);
        }

        // ---- a side asks only for a range its loaded guns reach ------------------------------------

        /// <summary>The live catalogue's Naginata, Tanto and Wakizashi: bands and munition slots.</summary>
        private sealed class LiveArmed : IPrefabCatalogue, IMunitionPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return BandsOf(prefabId); }

            public static List<WeaponBand> BandsOf(string prefabId)
            {
                switch (prefabId)
                {
                    case "Naginata": return Bands(84634.93f, 2000, 6000.00049f, 1400);
                    case "Tanto": return Bands(6400, 800, 1269.84131f, 1400);
                    case "Wakizashi": return Bands(12100, 800, 1269.84131f, 1400);
                    default: return new List<WeaponBand>();
                }
            }

            public IReadOnlyList<MunitionSlot> MunitionSlots(string prefabId)
            {
                switch (prefabId)
                {
                    case "Naginata": return new[]
                    {
                        new MunitionSlot { MagazineKey = "LargeCalibreAmmo", RangeM = 2000, Dps = 41777.79f, MagazinesPerSecond = 2.45751619f, FullLoad = 99 },
                        new MunitionSlot { MagazineKey = "LargeRailgunAmmo", RangeM = 2000, Dps = 42857.15f, MagazinesPerSecond = 0.857142866f, FullLoad = 36 },
                        new MunitionSlot { MagazineKey = "MediumCalibreAmmo", RangeM = 1400, Dps = 6000.00049f, MagazinesPerSecond = 1.5f, FullLoad = 30 },
                    };
                    case "Tanto": return new[]
                    {
                        new MunitionSlot { MagazineKey = "NATO_25x184mm", RangeM = 800, Dps = 5400, MagazinesPerSecond = 0.428571433f, FullLoad = 24 },
                        new MunitionSlot { MagazineKey = "Missile200mm", RangeM = 800, Dps = 1000, MagazinesPerSecond = 2, FullLoad = 8 },
                        new MunitionSlot { MagazineKey = "MediumCalibreAmmo", RangeM = 1400, Dps = 1269.84131f, MagazinesPerSecond = 0.317460328f, FullLoad = 6 },
                    };
                    case "Wakizashi": return new[]
                    {
                        new MunitionSlot { MagazineKey = "NATO_25x184mm", RangeM = 800, Dps = 11100, MagazinesPerSecond = 0.880952537f, FullLoad = 55 },
                        new MunitionSlot { MagazineKey = "Missile200mm", RangeM = 800, Dps = 1000, MagazinesPerSecond = 2, FullLoad = 8 },
                        new MunitionSlot { MagazineKey = "MediumCalibreAmmo", RangeM = 1400, Dps = 1269.84131f, MagazinesPerSecond = 0.317460328f, FullLoad = 6 },
                    };
                    default: return null;
                }
            }
        }

        private static PendingFleetAction ArmedFleet(ActionKind kind, string tag, string prefab, int hulls,
                                                     Dictionary<string, long> munitions)
        {
            var bands = new List<WeaponBand>();
            for (int i = 0; i < hulls; i++) bands.AddRange(LiveArmed.BandsOf(prefab));
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = tag, TargetNodeId = "N157", ShipPrefabId = prefab, ConvoySize = hulls,
                WeaponBands = bands, MunitionsArmed = true, Munitions = munitions,
            };
        }

        [Fact]
        public void TheN157Triple_ADefenceWhoseLongGunsAreEmpty_DoesNotAskFor2000m()
        {
            // 21:31:36: EXP's lead Guard was a Naginata that had spent its artillery and railgun
            // magazines at 21:23, beside Tanto squadrons and the station floor. By its bands it still
            // reached 2000 m, so the defence set T2 there — where its guns fired nothing and only the
            // floor (def=8) landed. Read from what it can fire, 2000 m is out of its reach.
            WorldModel w = TestSupport.World(1);
            w.PrefabCatalogue = new LiveArmed();
            w.Tuning.CombatVariance = 0;
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            var sprt = new FactionState { Tag = "SPRT" };
            var exp = new FactionState { Tag = "EXP" };
            sprt.PendingFleets.Add(ArmedFleet(ActionKind.Raid, "SPRT", "Wakizashi", 17, new Dictionary<string, long>
                { { "NATO_25x184mm", 17 * 55 }, { "Missile200mm", 17 * 8 }, { "MediumCalibreAmmo", 17 * 6 } }));
            exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Naginata", 1, new Dictionary<string, long>
                { { "LargeCalibreAmmo", 0 }, { "LargeRailgunAmmo", 0 }, { "MediumCalibreAmmo", 0 } }));
            for (int g = 0; g < 6; g++)
                exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Tanto", 5, new Dictionary<string, long>
                    { { "NATO_25x184mm", 5 * 24 }, { "Missile200mm", 5 * 8 }, { "MediumCalibreAmmo", 5 * 6 } }));
            var states = new List<FactionState> { sprt, exp };
            var defence = new List<(int si, int fi)>();
            var defenceBands = new List<WeaponBand>();
            for (int i = 0; i < exp.PendingFleets.Count; i++)
            {
                defence.Add((1, i));
                defenceBands.AddRange(exp.PendingFleets[i].WeaponBands);
            }
            DuelMunitions atkMun = DuelMunitions.ForSide(states, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);
            DuelMunitions defMun = DuelMunitions.ForSide(states, defence, f => f.WeaponBands, null, 1.0, w, w.Tuning);
            // The stances chosen live: SPRT AttackRun/Mid, EXP Standoff/Low; SPRT sets turns 1 and 3.
            var atk = new DuelStance { Arm = TacticArm.AttackRun, BreakOff = BreakOffLevel.Mid, Mobility = 6 };
            var def = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Low, Mobility = 4 };

            // By bands alone the defence still wants 2000 m on turn 2: the defect as it was live.
            DuelTurn[] byBands = RangeSchedule.Plan(sprt.PendingFleets[0].WeaponBands, defenceBands, 20, atk, def, w.Tuning);
            Assert.Equal(2000, byBands[1].RangeM);
            Assert.Equal(DuelSetter.Defender, byBands[1].SetBy);

            ShipDuel.Result r = ShipDuel.Fight(sprt.PendingFleets[0].WeaponBands, defenceBands, 20, w.Tuning, NoJitter(),
                atkMun, defMun, true, atk, def);
            Assert.StartsWith("T1(0m set=atk)", r.TurnSummary);
            Assert.DoesNotContain("2000m", r.TurnSummary);
            Assert.Contains("T2(800m set=", r.TurnSummary);
        }

        [Fact]
        public void TheN268Guard_WithALastMagazineForItsLongGuns_ClosesWhereItsLoadedGunsFire()
        {
            // 21:36:16: the Guard that held 2000 m at 21:30 (def=48287) came back with its long-gun
            // magazines nearly spent, and set T2 at 2000 m again for def=454 against 64369 at 0 m on T1.
            // Fire does not fall with damage in the duel, so only its magazines explain that. Here the
            // Naginata keeps one magazine of each long type (about 9% of what the Tantos still fire):
            // by bands it wants 2000 m, by what it can fire it closes.
            WorldModel w = TestSupport.World(1);
            w.PrefabCatalogue = new LiveArmed();
            w.Tuning.CombatVariance = 0;
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            var sprt = new FactionState { Tag = "SPRT" };
            var exp = new FactionState { Tag = "EXP" };
            sprt.PendingFleets.Add(ArmedFleet(ActionKind.Raid, "SPRT", "Wakizashi", 10, new Dictionary<string, long>
                { { "NATO_25x184mm", 10 * 55 }, { "Missile200mm", 10 * 8 }, { "MediumCalibreAmmo", 10 * 6 } }));
            exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Naginata", 1, new Dictionary<string, long>
                { { "LargeCalibreAmmo", 1 }, { "LargeRailgunAmmo", 1 }, { "MediumCalibreAmmo", 0 } }));
            exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Tanto", 4, new Dictionary<string, long>
                { { "NATO_25x184mm", 4 * 48 }, { "Missile200mm", 4 * 16 }, { "MediumCalibreAmmo", 0 } }));
            var states = new List<FactionState> { sprt, exp };
            var defenceBands = new List<WeaponBand>(exp.PendingFleets[0].WeaponBands);
            defenceBands.AddRange(exp.PendingFleets[1].WeaponBands);
            var atk = new DuelStance { Arm = TacticArm.AttackRun, BreakOff = BreakOffLevel.Mid, Mobility = 6 };
            var def = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Low, Mobility = 4 };

            DuelTurn[] byBands = RangeSchedule.Plan(sprt.PendingFleets[0].WeaponBands, defenceBands, 0, atk, def, w.Tuning);
            Assert.Equal(2000, byBands[1].RangeM);

            ShipDuel.Result r = ShipDuel.Fight(sprt.PendingFleets[0].WeaponBands, defenceBands, 0, w.Tuning, NoJitter(),
                DuelMunitions.ForSide(states, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning),
                DuelMunitions.ForSide(states, new[] { (1, 0), (1, 1) }, f => f.WeaponBands, null, 1.0, w, w.Tuning),
                true, atk, def);
            Assert.DoesNotContain("2000m", r.TurnSummary);
        }

        [Fact]
        public void TheN157SecondCycle_ARaidOutOfMagazinesFiresNothing_AndTheDefenceStillClosesToFire()
        {
            // 21:44:46, the raid's second dwell cycle: `T1(800m set=atk) atk=28115 | T2(2000m set=def)
            // def=7 | T3(800m set=atk) atk=0 def=34373`. The raid left N445 with 4321 of 13271 magazines
            // (MUNITIONS_SHORT) and spent most in its first cycle, so it empties on T1 and lands nothing
            // on T3 at the bracket it hit on T1 — its fire is its magazines, its strength its hulls.
            // The defence's T2 at 2000 m is the empty-Naginata defect; read from loaded guns it closes.
            WorldModel w = TestSupport.World(1);
            w.PrefabCatalogue = new LiveArmed();
            w.Tuning.CombatVariance = 0;
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            var sprt = new FactionState { Tag = "SPRT" };
            var exp = new FactionState { Tag = "EXP" };
            sprt.PendingFleets.Add(ArmedFleet(ActionKind.Raid, "SPRT", "Wakizashi", 24, new Dictionary<string, long>
                { { "NATO_25x184mm", 24 * 10 }, { "Missile200mm", 24 * 20 }, { "MediumCalibreAmmo", 0 } }));
            exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Naginata", 1, new Dictionary<string, long>
                { { "LargeCalibreAmmo", 0 }, { "LargeRailgunAmmo", 0 }, { "MediumCalibreAmmo", 0 } }));
            for (int g = 0; g < 3; g++)
                exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Tanto", 5, new Dictionary<string, long>
                    { { "NATO_25x184mm", 5 * 24 }, { "Missile200mm", 5 * 8 }, { "MediumCalibreAmmo", 5 * 6 } }));
            var states = new List<FactionState> { sprt, exp };
            var defence = new List<(int si, int fi)>();
            var defenceBands = new List<WeaponBand>();
            for (int i = 0; i < exp.PendingFleets.Count; i++)
            {
                defence.Add((1, i));
                defenceBands.AddRange(exp.PendingFleets[i].WeaponBands);
            }
            var atk = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.High, Mobility = 6 };
            var def = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Low, Mobility = 4 };
            Assert.Equal(2000, RangeSchedule.Plan(sprt.PendingFleets[0].WeaponBands, defenceBands, 20, atk, def, w.Tuning)[1].RangeM);

            ShipDuel.Result r = ShipDuel.Fight(sprt.PendingFleets[0].WeaponBands, defenceBands, 20, w.Tuning, NoJitter(),
                DuelMunitions.ForSide(states, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning),
                DuelMunitions.ForSide(states, defence, f => f.WeaponBands, null, 1.0, w, w.Tuning),
                true, atk, def);

            string[] turns = r.TurnSummary.Split('|');
            Assert.StartsWith("T1(800m set=", turns[0].Trim());
            Assert.DoesNotContain("atk=0 ", turns[0]);
            Assert.DoesNotContain("2000m set=def", r.TurnSummary);
            Assert.Contains("atk=0 ", turns[2]);                   // magazines spent: nothing to fire
            Assert.False(r.AttackerDestroyed);
            Assert.True(r.AttackerRatio > 0.5);                    // strength is hulls, not rounds
        }

        [Fact]
        public void LoadedBands_DropASlotWhoseMagazinesAreSpent_AndScaleAShortOne()
        {
            WorldModel w = TestSupport.World(1);
            w.PrefabCatalogue = new LiveArmed();
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            w.Tuning.CombatTurnFireSeconds = 30;
            var exp = new FactionState { Tag = "EXP" };
            // Artillery empty; railgun holds 12.86 of the 25.71 magazines a 30 s turn takes.
            exp.PendingFleets.Add(ArmedFleet(ActionKind.Guard, "EXP", "Naginata", 1, new Dictionary<string, long>
                { { "LargeCalibreAmmo", 0 }, { "LargeRailgunAmmo", 13 }, { "MediumCalibreAmmo", 90 } }));
            DuelMunitions m = DuelMunitions.ForSide(new List<FactionState> { exp }, new[] { (0, 0) }, f => f.WeaponBands,
                null, 1.0, w, w.Tuning);
            List<WeaponBand> loaded = m.LoadedBands();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(42857.15 * 13 / (0.857142866 * 30), loaded[0].Dps, 0);
            Assert.Equal(2000, loaded[0].RangeM);
            Assert.Equal(6000, loaded[1].Dps, 0);
            // Nothing was spent reading them.
            Assert.Equal(CombatStrength.InRangeDps(loaded, 2000), CombatStrength.InRangeDps(m.LoadedBands(), 2000), 6);
        }

        [Fact]
        public void ThePredicate_ComparesLostShares_WithTheDefenceStanding()
        {
            // 21:44:46 N157: the raid at 0.65, the defence at 0.90 and standing — repelled on lost share.
            Assert.True(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.65, DefenderRatio = 0.90 }, 10));
            // The raid lost the smaller share: it lands.
            Assert.False(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.80, DefenderRatio = 0.70 }, 10));
            // A defence that broke off or was destroyed lets it land whatever the shares.
            Assert.False(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.40, DefenderRatio = 0.30, DefenderBrokeOff = true }, 10));
            Assert.False(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.40, DefenderRatio = 0, DefenderDestroyed = true }, 10));
            // 21:58:54 N157 (v0.401.0 repelled it): the raid at 0.98, the defence at 0.91 — the raid lost
            // the smaller share, so it lands.
            Assert.False(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.98, DefenderRatio = 0.91 }, 10));
            // Equal shares land; nothing lost on either side is undecided, which repels.
            Assert.False(ShipDuel.StandingDefenceRepels(new ShipDuel.Result { AttackerRatio = 0.9, DefenderRatio = 0.9 }, 10));
            var r = new ShipDuel.Result { AttackerRatio = 1, DefenderRatio = 1 };
            Assert.True(ShipDuel.StandingDefenceRepels(r, 10));
            Assert.False(ShipDuel.StandingDefenceRepels(r, 0));                        // a bare node
            r.DefenderBrokeOff = true;
            Assert.False(ShipDuel.StandingDefenceRepels(r, 10));
            r.DefenderBrokeOff = false;
            r.AttackerBrokeOff = true;
            Assert.False(ShipDuel.StandingDefenceRepels(r, 10));
        }
    }
}
