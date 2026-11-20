using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the contested range schedule (<see cref="RangeSchedule"/>), tactic arms and break-off
    /// in <see cref="ShipDuel"/>, the commit-gate prediction that reads the same schedule
    /// (<see cref="CombatStrength.DuelDamage(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,DuelMatchup)"/>),
    /// and the uncontested duel <c>factions bench duel</c> runs as a baseline, which must be the
    /// fixed closing order exactly.
    /// </summary>
    public class ContestedRangeTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>Jitter() = 1 exactly, so damage is hand-computable.</summary>
        private static IRandom NoJitter() { return new FixedRandom(0.5); }

        private static List<WeaponBand> Bands(float dps, float rangeM)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
        }

        private static DuelStance Stance(TacticArm arm, BreakOffLevel level, double mobility)
        {
            return new DuelStance { Arm = arm, BreakOff = level, Mobility = mobility };
        }

        /// <summary>Hulls with a dry mass, one full-effectiveness thrust direction and a loadout.</summary>
        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _mass = new Dictionary<string, double>();
            private readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> _thrust =
                new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();
            private readonly Dictionary<string, List<WeaponBand>> _weapons = new Dictionary<string, List<WeaponBand>>();

            public void Add(string id, double massKg, float backwardForceN, List<WeaponBand> weapons,
                            float forwardForceN = 0)
            {
                _mass[id] = massKg;
                var dirs = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                if (backwardForceN > 0) dirs[ThrustDirection.Backward] = new List<ThrustBand> { Hydrogen(backwardForceN) };
                if (forwardForceN > 0) dirs[ThrustDirection.Forward] = new List<ThrustBand> { Hydrogen(forwardForceN) };
                _thrust[id] = dirs;
                _weapons[id] = weapons;
            }

            private static ThrustBand Hydrogen(float n)
            {
                return new ThrustBand
                {
                    ThrusterType = "Hydrogen", ForceMagnitudeN = n,
                    EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                };
            }

            public long CargoLiters(string prefabId) { return 0; }
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                List<WeaponBand> w;
                return prefabId != null && _weapons.TryGetValue(prefabId, out w) ? w : new List<WeaponBand>();
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> d;
                return prefabId != null && _thrust.TryGetValue(prefabId, out d)
                    ? d : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId)
            {
                double m;
                return prefabId != null && _mass.TryGetValue(prefabId, out m) ? m : 0;
            }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        // ---- the ticket's two cases ----------------------------------------------------------------

        [Fact]
        public void FasterKite_ThatOutrangesASlowerBrawler_WinsTheDuelItLosesOnTheFixedOrder()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            List<WeaponBand> kiter = Bands(100, 2000);
            List<WeaponBand> brawler = Bands(250, 800);
            DuelStance atk = Stance(TacticArm.Kite, BreakOffLevel.High, 10);
            DuelStance def = Stance(TacticArm.Brawl, BreakOffLevel.Low, 2);

            ShipDuel.Result fixedOrder = ShipDuel.Fight(kiter, brawler, 0, tuning, NoJitter(), null, null, false, atk, def);
            ShipDuel.Result contested = ShipDuel.Fight(kiter, brawler, 0, tuning, NoJitter(), null, null, true, atk, def);

            // Fixed order: one free turn at 2000 m, then two at 800 m and 0 m where the brawler lands
            // 87.5 a turn against 100 HP.
            Assert.True(fixedOrder.AttackerDestroyed);
            // Contested: the kiter holds 5/6 of the mobility and sets every turn at 2000 m, where the
            // brawler cannot reply.
            Assert.False(contested.AttackerDestroyed);
            Assert.False(contested.AttackerBrokeOff);
            Assert.Equal(1.0, contested.AttackerRatio, 6);
            Assert.Equal("T1(2000m set=atk) atk=35 def=0 | T2(2000m set=atk) atk=35 def=0 | T3(2000m set=atk) atk=35 def=0",
                         contested.TurnSummary);
        }

        [Fact]
        public void AStation_CannotBeOutKited_ByAnAttackerItOutranges()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            List<WeaponBand> attacker = Bands(100, 800);
            List<WeaponBand> station = Bands(60, 2000);
            long flat = tuning.StationDefensePower;
            DuelStance stationStance = Stance(TacticArm.Standoff, BreakOffLevel.Mid, CombatMobility.Station);

            ShipDuel.Result kited = ShipDuel.Fight(attacker, station, flat, tuning, NoJitter(), null, null, true,
                Stance(TacticArm.Kite, BreakOffLevel.Low, 50), stationStance);
            ShipDuel.Result brawled = ShipDuel.Fight(attacker, station, flat, tuning, NoJitter(), null, null, true,
                Stance(TacticArm.Brawl, BreakOffLevel.Low, 50), stationStance);

            // The attacker sets every turn (a station cannot move), yet no range it can choose is
            // outside the station's reach: the station fires every turn, and kiting buys nothing a
            // brawl does not.
            foreach (string turn in kited.TurnSummary.Split('|'))
            {
                Assert.DoesNotContain("set=def", turn);
                Assert.DoesNotContain("def=0", turn);
            }
            Assert.Equal(brawled.AttackerRatio, kited.AttackerRatio, 9);
            Assert.Equal(brawled.DefenderRatio, kited.DefenderRatio, 9);
        }

        // ---- allocation --------------------------------------------------------------------------

        [Fact]
        public void Allocation_ASevenTenthsShare_SetsSevenTurnsInTen_TheSameEveryTime()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            tuning.CombatTurns = 10;
            tuning.CombatRangeBracketsM = new double[] { 2000, 2000, 2000, 2000, 2000, 800, 800, 800, 0, 0 };
            // The attacker brawls (wants 0 m); the defender outranges it and wants 2000 m.
            DuelStance atk = Stance(TacticArm.Brawl, BreakOffLevel.Mid, 7);
            DuelStance def = Stance(TacticArm.Standoff, BreakOffLevel.Mid, 3);
            DuelTurn[] a = RangeSchedule.Plan(Bands(100, 800), Bands(100, 2000), 0, atk, def, tuning);
            DuelTurn[] b = RangeSchedule.Plan(Bands(100, 800), Bands(100, 2000), 0, atk, def, tuning);

            Assert.Equal(7, a.Count(t => t.SetBy == DuelSetter.Attacker && t.RangeM == 0));
            Assert.Equal(3, a.Count(t => t.SetBy == DuelSetter.Defender && t.RangeM == 2000));
            // Spread, not front-loaded: the defender's first turn is the second one.
            Assert.Equal(DuelSetter.Attacker, a[0].SetBy);
            Assert.Equal(DuelSetter.Defender, a[1].SetBy);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(a[i].RangeM, b[i].RangeM);
                Assert.Equal(a[i].SetBy, b[i].SetBy);
            }
        }

        [Fact]
        public void AttackerShare_StationIsZero_UnknownSplitsEvenly()
        {
            Assert.Equal(1.0, RangeSchedule.AttackerShare(3, CombatMobility.Station));
            Assert.Equal(1.0, RangeSchedule.AttackerShare(CombatMobility.Unknown, CombatMobility.Station));
            Assert.Equal(0.0, RangeSchedule.AttackerShare(CombatMobility.Station, 3));
            Assert.Equal(0.5, RangeSchedule.AttackerShare(CombatMobility.Unknown, 3));
            Assert.Equal(0.75, RangeSchedule.AttackerShare(3, 1), 9);
        }

        [Fact]
        public void Mobility_IsTheSlowestScannedHull_AStrongestFace_AndAStationIsZero()
        {
            var cat = new Catalogue();
            cat.Add("fast", 1000, 20000, Bands(1, 1));                         // 20 m/s^2
            cat.Add("slow", 1000, 3000, Bands(1, 1), forwardForceN: 5000);     // strongest face 5 m/s^2
            Assert.Equal(20.0, CombatMobility.OfHull(cat, "fast", 0, false), 6);
            Assert.Equal(5.0, CombatMobility.OfHull(cat, "slow", 0, false), 6);
            Assert.Equal(5.0, CombatMobility.OfHulls(cat, new[] { "fast", "slow", "unscanned" }, 0, false), 6);
            Assert.Equal(CombatMobility.Unknown, CombatMobility.OfHulls(cat, new[] { "unscanned" }, 0, false));
            Assert.Equal(CombatMobility.Station, CombatMobility.OfDefence(cat, new string[0], false, 0, false));
        }

        // ---- break-off -----------------------------------------------------------------------------

        [Fact]
        public void BreakOff_ASideBelowItsFraction_LeavesAndTheDuelStops()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            // Equal 100/100 at 800 m: both lose 35 a turn, so both are at 0.30 after two turns. The
            // attacker leaves below 0.35 (Mid); the defender stays until 0.2 (Low).
            ShipDuel.Result r = ShipDuel.Fight(Bands(100, 800), Bands(100, 800), 0, tuning, NoJitter(), null, null, true,
                Stance(TacticArm.Brawl, BreakOffLevel.Mid, 5), Stance(TacticArm.Brawl, BreakOffLevel.Low, 5));

            Assert.True(r.AttackerBrokeOff);
            Assert.False(r.DefenderBrokeOff);
            Assert.False(r.AttackerDestroyed);
            Assert.Equal(0.30, r.AttackerRatio, 6);
            Assert.EndsWith("| broke_off=atk", r.TurnSummary);
            Assert.DoesNotContain("T3(", r.TurnSummary);
        }

        [Fact]
        public void BreakOff_AStationNeverLeaves()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            ShipDuel.Result r = ShipDuel.Fight(Bands(100, 800), Bands(100, 800), 0, tuning, NoJitter(), null, null, true,
                Stance(TacticArm.Brawl, BreakOffLevel.Low, 5), Stance(TacticArm.Brawl, BreakOffLevel.High, CombatMobility.Station));
            Assert.False(r.DefenderBrokeOff);
            Assert.Contains("T3(", r.TurnSummary);
        }

        // ---- the gates predict what the duel fights -----------------------------------------------

        [Fact]
        public void Gate_PredictsTheContestedSchedule_WhereTheFixedOrderWouldMispredict()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            List<WeaponBand> kiter = Bands(100, 2000);
            List<WeaponBand> brawler = Bands(250, 800);
            var matchup = new DuelMatchup
            {
                AttackerBands = kiter,
                Attacker = Stance(TacticArm.Kite, BreakOffLevel.High, 10),
                Defender = Stance(TacticArm.Brawl, BreakOffLevel.Low, 2),
            };

            // Fixed order: the brawler "destroys" the kiter (175 >= 100). Contested: it never lands
            // a shot, and the duel agrees.
            Assert.True(CombatStrength.Destroys(brawler, null, tuning, 100, 1.0));
            Assert.False(CombatStrength.Destroys(brawler, null, tuning, 100, 1.0, matchup));
            Assert.Equal(0.0, CombatStrength.DuelDamage(brawler, null, tuning, matchup), 9);
            ShipDuel.Result duel = ShipDuel.Fight(kiter, brawler, 0, tuning, NoJitter(), null, null, true,
                                                  matchup.Attacker, matchup.Defender);
            Assert.False(duel.AttackerDestroyed || duel.AttackerBrokeOff);
        }

        [Fact]
        public void Gate_DuelDamage_EqualsWhatTheDuelDeals_WhenNeitherSideLeavesEarly()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            tuning.CombatBaseDamageFraction = 0.02;   // small, so no side is destroyed or leaves
            var node = new SpatialNode { Id = "N", StationPrefabId = "st", StationWeapons = Bands(40, 2000) };
            List<WeaponBand> attacker = new List<WeaponBand>
            {
                new WeaponBand { Dps = 300, RangeM = 800 }, new WeaponBand { Dps = 50, RangeM = 2000 },
            };
            List<WeaponBand> convoy = Bands(120, 800);
            var matchup = new DuelMatchup
            {
                AttackerBands = attacker,
                Attacker = Stance(TacticArm.AttackRun, BreakOffLevel.Mid, 4),
                Defender = Stance(TacticArm.Standoff, BreakOffLevel.Mid, 6),
            };
            var defenderBands = new List<WeaponBand>(convoy);
            defenderBands.AddRange(node.StationWeapons);
            long flat = CombatStrength.StationFlat(node, tuning);

            ShipDuel.Result duel = ShipDuel.Fight(attacker, defenderBands, flat, tuning, NoJitter(), null, null, true,
                                                  matchup.Attacker, matchup.Defender);
            double attackerMax = 350;
            double dealt = attackerMax * (1 - duel.AttackerRatio);
            Assert.False(duel.AttackerBrokeOff || duel.DefenderBrokeOff);
            Assert.Equal(dealt, CombatStrength.DuelDamage(convoy, node, tuning, matchup), 6);
        }

        [Fact]
        public void Gate_Defeat_CountsTheAttackersOwnBreakOff()
        {
            var matchup = new DuelMatchup { Attacker = Stance(TacticArm.Standoff, BreakOffLevel.Mid, 3) };
            Assert.Equal(1.0 - TacticChoice.BreakOffMidFraction, CombatStrength.DefeatShare(matchup), 9);
            Assert.Equal(1.0, CombatStrength.DefeatShare(null), 9);
        }

        private static DuelMatchup Brawls(List<WeaponBand> attacker)
        {
            return new DuelMatchup
            {
                AttackerBands = attacker,
                Attacker = Stance(TacticArm.Brawl, BreakOffLevel.Mid, 5),
                Defender = Stance(TacticArm.Brawl, BreakOffLevel.Mid, 5),
            };
        }

        [Fact]
        public void RaidGate_ARaidThatWouldOnlyBreakOff_Commits()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            List<WeaponBand> raid = Bands(100, 800);
            List<WeaponBand> defence = Bands(100, 800);
            // 35 a turn against 100 HP: 65, then 30 -- below 0.35, so it leaves with HP left.
            // Summed over three turns the defence deals 105, which a whole-duel reading calls fatal.
            Assert.Equal(105.0, CombatStrength.DuelDamage(defence, null, tuning, Brawls(raid)), 6);
            Assert.True(CombatStrength.Survives(defence, null, tuning, 100, 1.0, Brawls(raid)));
            // The defender's question is unchanged: a raider driven off has been stopped.
            Assert.True(CombatStrength.Destroys(defence, null, tuning, 100, 1.0, Brawls(raid)));
        }

        [Fact]
        public void RaidGate_ARaidDestroyedBeforeItCanLeave_IsStillRefused()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            List<WeaponBand> raid = Bands(100, 800);
            // 105 on the first turn against 100 HP: destroyed from full strength, never below 0.35 alive.
            Assert.False(CombatStrength.Survives(Bands(300, 800), null, tuning, 100, 1.0, Brawls(raid)));
            // The margin scales the HP the defence has to take, as it does uncontested: 0.8 x 100 = 80
            // against 87.5 in one turn.
            Assert.True(CombatStrength.Survives(Bands(250, 800), null, tuning, 100, 1.0, Brawls(raid)));
            Assert.False(CombatStrength.Survives(Bands(250, 800), null, tuning, 100, 0.8, Brawls(raid)));
        }

        // ---- uncontested is the fixed order exactly --------------------------------------------------

        /// <summary>The duel on the fixed closing order — the reference the uncontested duel has to
        /// reproduce.</summary>
        private static void LegacyDuel(List<WeaponBand> atkBands, List<WeaponBand> defBands, long flat,
            FactionTuning tuning, IRandom rng, out double atkRatio, out double defRatio,
            out bool atkDead, out bool defDead, out string summaryOut)
        {
            double atkMax = Math.Max(1, atkBands.Sum(b => (double)b.Dps));
            double defMax = Math.Max(1, defBands.Sum(b => (double)b.Dps) + flat);
            double atkHp = atkMax, defHp = defMax;
            int turns = tuning.CombatTurns > 0 ? tuning.CombatTurns : 1;
            double[] brackets = (tuning.CombatRangeBracketsM != null && tuning.CombatRangeBracketsM.Length == turns)
                ? tuning.CombatRangeBracketsM : new double[turns];
            var summary = new System.Text.StringBuilder();
            for (int t = 0; t < turns; t++)
            {
                double bracket = brackets[t];
                double a = atkHp > 0 ? CombatStrength.InRangeDps(atkBands, bracket) : 0;
                double d = defHp > 0 ? CombatStrength.InRangeDps(defBands, bracket) + flat : 0;
                double ja = tuning.CombatVariance <= 0 ? 1 : 1 + (rng.NextDouble() * 2 - 1) * tuning.CombatVariance;
                double atkDmg = a * tuning.CombatBaseDamageFraction * ja;
                double jd = tuning.CombatVariance <= 0 ? 1 : 1 + (rng.NextDouble() * 2 - 1) * tuning.CombatVariance;
                double defDmg = d * tuning.CombatBaseDamageFraction * jd;
                defHp = Math.Max(0, defHp - atkDmg);
                atkHp = Math.Max(0, atkHp - defDmg);
                if (summary.Length > 0) summary.Append(" | ");
                summary.Append("T").Append(t + 1).Append("(").Append(bracket.ToString("0", CultureInfo.InvariantCulture))
                       .Append("m) atk=").Append(atkDmg.ToString("0", CultureInfo.InvariantCulture))
                       .Append(" def=").Append(defDmg.ToString("0", CultureInfo.InvariantCulture));
            }
            atkRatio = Math.Min(1, Math.Max(0, atkHp / atkMax));
            defRatio = Math.Min(1, Math.Max(0, defHp / defMax));
            atkDead = atkHp <= 0;
            defDead = defHp <= 0;
            summaryOut = summary.ToString();
        }

        private static List<WeaponBand> RandomBands(SeededRandom rng)
        {
            float[] ranges = { 0, 100, 800, 1500, 2000, 3000 };
            var bands = new List<WeaponBand>();
            int n = 1 + (int)(rng.NextDouble() * 3);
            for (int i = 0; i < n; i++)
                bands.Add(new WeaponBand { Dps = (float)(rng.NextDouble() * 400), RangeM = ranges[(int)(rng.NextDouble() * ranges.Length)] });
            return bands;
        }

        [Fact]
        public void Uncontested_IsTheFixedOrderExactly_WhateverTheStances()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            var gen = new SeededRandom(4000);
            TacticArm[] arms = { TacticArm.Brawl, TacticArm.Standoff, TacticArm.Kite, TacticArm.AttackRun };
            for (int i = 0; i < 400; i++)
            {
                List<WeaponBand> atk = RandomBands(gen), def = RandomBands(gen);
                long flat = gen.NextDouble() < 0.5 ? 0 : 20;
                DuelStance sa = Stance(arms[i % 4], (BreakOffLevel)(i % 3), gen.NextDouble() * 10);
                DuelStance sd = Stance(arms[(i / 4) % 4], (BreakOffLevel)((i / 3) % 3), gen.NextDouble() * 10);

                double ar, dr; bool ad, dd; string sum;
                LegacyDuel(atk, def, flat, tuning, new SeededRandom(i), out ar, out dr, out ad, out dd, out sum);
                ShipDuel.Result r = ShipDuel.Fight(atk, def, flat, tuning, new SeededRandom(i), null, null, false, sa, sd);

                Assert.Equal(ar, r.AttackerRatio);
                Assert.Equal(dr, r.DefenderRatio);
                Assert.Equal(ad, r.AttackerDestroyed);
                Assert.Equal(dd, r.DefenderDestroyed);
                Assert.Equal(sum, r.TurnSummary);
                Assert.False(r.AttackerBrokeOff || r.DefenderBrokeOff);

                Assert.Equal(CombatStrength.DuelDamage(def, null, tuning),
                             CombatStrength.DuelDamage(def, null, tuning, null));
            }
        }

        // ---- through the resolver ------------------------------------------------------------------

        private static WorldModel ResolverWorld(Catalogue cat)
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            foreach (var spec in new[] { new { Id = "MERC1", Owner = "MERC", X = 0.0 }, new { Id = "RVN1", Owner = "RVN", X = 500.0 } })
            {
                SpatialNode n = w.Graph.Add(new Vec3(spec.X, 0, 0), 0.6, spec.Id);
                n.OwnerFactionTag = spec.Owner;
                n.HoldStrength = 0.8;
                n.Radius = 40;
            }
            return w;
        }

        private static List<FactionState> GuardAndRaid(WorldModel w, string guardPersonality, string guardHull,
            List<WeaponBand> guardBands, string raidPersonality, string raidHull, List<WeaponBand> raidBands)
        {
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC", PersonalityId = guardPersonality };
            var atk = new FactionState { Tag = "RVN", PersonalityId = raidPersonality };
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1", ShipPrefabId = guardHull, ConvoySize = 1,
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = guardBands,
            });
            atk.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginNodeId = "RVN1", ShipPrefabId = raidHull, ConvoySize = 1,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = raidBands,
            });
            return new List<FactionState> { def, atk };
        }

        [Fact]
        public void Resolver_AMercantileKiter_BreaksAFactorumBrawlerGuard()
        {
            // On the fixed closing order the brawler intercepts the kiter; contested, the kiter holds
            // the range where only it can fire.
            var cat = new Catalogue();
            cat.Add("kiter", 1000, 10000, Bands(100, 2000));    // 10 m/s^2
            cat.Add("brawler", 1000, 2000, Bands(250, 800));    //  2 m/s^2

            WorldModel w = ResolverWorld(cat);
            List<FactionState> states = GuardAndRaid(w, "factorum", "brawler", Bands(250, 800),
                                                     "mercantile", "kiter", Bands(100, 2000));
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            string all = string.Join("\n", log);
            Assert.Contains("RAID_BREAKS_GUARD", all);
            Assert.Contains("ratio=1.00", all);
            Assert.Contains("set=atk", all);
        }

        [Fact]
        public void Resolver_ARaidThatBreaksOff_ForfeitsTheRaidAndKeepsItsHull()
        {
            var cat = new Catalogue();
            cat.Add("gunboat", 1000, 5000, Bands(100, 800));
            WorldModel w = ResolverWorld(cat);
            // raider: AttackRun, leaves below 0.35. factorum: Brawl, stays until 0.2. Equal hulls, so
            // both stand at 0.30 after two turns and only the raid leaves.
            List<FactionState> states = GuardAndRaid(w, "factorum", "gunboat", Bands(100, 800),
                                                     "raider", "gunboat", Bands(100, 800));
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            string all = string.Join("\n", log);

            Assert.Contains("RAID_BROKE_OFF", all);
            Assert.Contains("ratio=0.30 hulls_lost=0/1", all);
            Assert.Contains("broke_off=atk", all);
            Assert.DoesNotContain("RAID_RESOLVED", all);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
            int stock;
            w.Graph.Nodes["RVN1"].ShipStock.TryGetValue("gunboat", out stock);
            Assert.Equal(1, stock);
            Assert.Empty(states[1].PendingFleets);
        }

        [Fact]
        public void Resolver_AGuardThatBreaksOff_LeavesTheNodeToTheRaid()
        {
            var cat = new Catalogue();
            cat.Add("gunboat", 1000, 5000, Bands(100, 800));
            WorldModel w = ResolverWorld(cat);
            // mercantile Guard leaves below 0.5; the factorum raid stays until 0.2.
            List<FactionState> states = GuardAndRaid(w, "mercantile", "gunboat", Bands(100, 800),
                                                     "factorum", "gunboat", Bands(100, 800));
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            string all = string.Join("\n", log);

            Assert.Contains("GUARD_BROKE_OFF", all);
            Assert.Contains("RAID_BREAKS_GUARD", all);
            Assert.Contains("broke_off=def", all);
            Assert.True(w.Graph.Nodes["MERC1"].HoldStrength < 0.8);
        }

        [Fact]
        public void Resolver_ABenchRaidThatBreaksOff_IsWrittenUpAsBrokenOff()
        {
            var cat = new Catalogue();
            cat.Add("gunboat", 1000, 5000, Bands(100, 800));
            WorldModel w = ResolverWorld(cat);
            List<FactionState> states = GuardAndRaid(w, "factorum", "gunboat", Bands(100, 800),
                                                     "raider", "gunboat", Bands(100, 800));
            PendingFleetAction raid = states[1].PendingFleets[0];
            raid.BenchLabel = "k1";
            raid.FleetId = "b.k1.raid";
            raid.CurrentStopKind = FleetStopKind.Engage;
            states[1].PendingFleets[0] = raid;
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string all = string.Join("\n", log);
            Assert.Contains("RAID_BROKE_OFF [b.k1.raid] RVN", all);
            string resolved = log.Single(l => l.StartsWith("BENCH_RESOLVED k1", StringComparison.Ordinal));
            Assert.Contains("[b.k1.raid] RVN outcome=broke_off ratio=0.30 cause=duel", resolved);
        }

        [Fact]
        public void Resolver_EachSidesBreakOff_IsTheOutcomeItWrites()
        {
            var cat = new Catalogue();
            cat.Add("gunboat", 1000, 5000, Bands(100, 800));

            WorldModel w = ResolverWorld(cat);
            List<FactionState> states = GuardAndRaid(w, "factorum", "gunboat", Bands(100, 800),
                                                     "raider", "gunboat", Bands(100, 800));
            PendingFleetAction raid = states[1].PendingFleets[0];
            raid.MaterialisedEntityId = 55;
            raid.CurrentStopKind = FleetStopKind.Engage;
            states[1].PendingFleets[0] = raid;
            int leaving = -1;
            PendingFleetAction stopped = default(PendingFleetAction);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null, eid =>
            {
                foreach (PendingFleetAction f in states[1].PendingFleets)
                    if (f.MaterialisedEntityId == eid) { leaving = f.DepartingHulls().Count; stopped = f; }
            });
            // The raid that left keeps its one hull, and that hull's grid is handed over to fly out,
            // named as broken off at the ratio it left at.
            Assert.Equal(1, leaving);
            Assert.Equal("broke_off", stopped.StoppedHow);
            Assert.Equal(0.30, stopped.StoppedRatio, 6);
            Assert.Equal("duel", stopped.StoppedSource);
            CombatOutcome raidLeft = Assert.Single(w.PendingCombatOutcomes);
            Assert.Equal(CombatAttackResult.BrokeOff, raidLeft.Attack);
            Assert.Equal(CombatDefenceResult.Stood, raidLeft.Defence);
            Assert.Equal(0, raidLeft.AttackerHullsLost);
            Assert.Equal("withdraw", raidLeft.NextStep);
            Assert.Equal(CombatTone.Win, raidLeft.ToneFor("MERC"));

            w = ResolverWorld(cat);
            states = GuardAndRaid(w, "mercantile", "gunboat", Bands(100, 800),
                                  "factorum", "gunboat", Bands(100, 800));
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null);
            CombatOutcome guardLeft = Assert.Single(w.PendingCombatOutcomes);
            Assert.Equal(CombatAttackResult.Landed, guardLeft.Attack);
            Assert.Equal(CombatDefenceResult.BrokeOff, guardLeft.Defence);
            Assert.Equal(CombatTone.Loss, guardLeft.ToneFor("MERC"));
            Assert.Contains("broke through; the defenders withdrew", guardLeft.Sentence());
        }
    }
}
