using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-422: the raid commit gate prices a defence by what it can field, and the squadron
    /// is drawn from the estate's best rather than the origin's first.
    ///
    /// <para>Live 2026-09-29, every minute from 19:40:21 box-local:
    /// <c>EXP RAID_REFUSED kind=Raid origin=N011 targetNode=N165 targetTag=SPRT haveDps=287160
    /// defenceDps=1041220 duelDmg=364441 surviveDmg=229728</c>. The defence was 416 parked
    /// <c>DS_Pirate_Scimitar</c> the node could never sail more than fifteen of; the squadron was the
    /// origin's weakest hulls while 39 <c>Naginata</c> sat at N257/N268. The world below is the one
    /// read from the live brains at 19:46 — positions, stock and weapon bands as scanned.</para></summary>
    public class RaidCensusFieldableTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 16, 40, 0, DateTimeKind.Utc);

        /// <summary>The live prefab catalogue's figures for the hulls in play.</summary>
        private sealed class LiveCatalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, WeaponBand[]> _bands = new Dictionary<string, WeaponBand[]>();
            private readonly Dictionary<string, string> _grid = new Dictionary<string, string>();
            private readonly Dictionary<string, long> _cargo = new Dictionary<string, long>();

            public LiveCatalogue()
            {
                Add("Naginata", "Large", 453125, Band(84634.93f, 2000), Band(6000.00049f, 1400));
                Add("Angurvadal", "Large", 484375, Band(15111.1123f, 2000), Band(2400.00024f, 1400), Band(43200f, 800));
                Add("Laevateinn", "Large", 47125, Band(9132.407f, 800));
                Add("Clipper", "Large", 140625, Band(3150f, 800));
                Add("Tanto", "Small", 0, Band(6400f, 800), Band(1269.84131f, 1400));
                Add("DS_Pirate_Scimitar", "Small", 0, Band(2500f, 800));
                Add("Eco_Ship_HullBreaker", "Small", 0);
                Add("WHI-24Drill", "Small", 375000);
                Add("Spadi-Space", "Small", 3375);
                Add("Eco_Ship_KiteMiner", "Small", 6750);
            }

            private static WeaponBand Band(float dps, float rangeM) { return new WeaponBand { Dps = dps, RangeM = rangeM }; }

            public LiveCatalogue Add(string id, string grid, long cargo, params WeaponBand[] bands)
            {
                _bands[id] = bands; _grid[id] = grid; _cargo[id] = cargo; return this;
            }

            public long CargoLiters(string prefabId) { long v; return _cargo.TryGetValue(prefabId, out v) ? v : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                WeaponBand[] b;
                return _bands.TryGetValue(prefabId, out b) ? b : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { string g; return _grid.TryGetValue(prefabId, out g) ? g : "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static SpatialNode Node(WorldModel w, string id, string owner, double x, double y, double z,
            string station, params object[] stock)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, y, z), 1.0, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 1.0; n.Radius = 40;
            n.StationPrefabId = station;
            for (int i = 0; i + 1 < stock.Length; i += 2) n.ShipStock[(string)stock[i]] = (int)stock[i + 1];
            return n;
        }

        /// <summary>EXP's estate and SPRT's N165 as the brains read them at 19:46, with EXP holding a
        /// standing campaign on N165.</summary>
        private static WorldModel Live(out FactionState exp, out SpatialNode n165)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = new LiveCatalogue();
            w.SetTreasury("EXP", 10_000_000_000);

            n165 = Node(w, "N165", "SPRT", 4.430324640275909, -1.7985, -340.76972166096374,
                        "Economy_SpaceStation_14_Pirate", "DS_Pirate_Scimitar", 416, "Eco_Ship_HullBreaker", 2);
            n165.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 1200.00012f, RangeM = 1400 } };

            Node(w, "N011", "EXP", 35.363991794604736, -0.42631578947368415, -317.23186312971745, "Economy_SpaceStation_15_Pirate",
                 "Clipper", 29, "DS_Pirate_Scimitar", 2, "Eco_Ship_KiteMiner", 1, "Laevateinn", 24, "WHI-24Drill", 5,
                 "Naginata", 1, "Spadi-Space", 2, "Tanto", 10);
            Node(w, "N257", "EXP", 39.81105921922904, -1.7965, -359.25665795641896, "Economy_SpaceStation_15_Pirate",
                 "Clipper", 18, "Naginata", 16, "Laevateinn", 17, "Eco_Ship_KiteMiner", 1);
            Node(w, "N268", "EXP", 75.97881562020885, 0.07949789631086462, -333.48965205707003, "Economy_SpaceStation_11_Pirate",
                 "Clipper", 3, "Naginata", 23, "Tanto", 6, "Angurvadal", 3, "Laevateinn", 4, "WHI-24Drill", 2);
            Node(w, "N157", "EXP", 75.1582925713139, -1.7965, -228.87325101146203, "Economy_SpaceStation_14_Pirate",
                 "Tanto", 111, "Laevateinn", 5, "Clipper", 3);
            SpatialNode n012 = Node(w, "N012", "EXP", 116.47182876910867, -1.1842105263157896, -234.39473554358653,
                 "Economy_SpaceStation_14_Pirate", "Clipper", 6, "Tanto", 5, "WHI-24Drill", 1);
            n012.ListedForSale["Clipper"] = 2;
            Node(w, "N765", "EXP", 186.59563530855206, -0.19040787255840694, -299.79741127290816, "Economy_SpaceStation_15",
                 "Tanto", 17, "WHI-24Drill", 2, "Spadi-Space", 1);

            exp = new FactionState { Tag = "EXP", RngSeed = 1 };
            var focus = new CampaignFocus
            {
                Id = "player:conquest:node:N165", CenterNodeId = "N011",
                SetAtUtc = T0, ExpiresAtUtc = T0.AddHours(3),
            };
            focus.RegionMembers.Add("N011");
            var objective = new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise, Cause = CampaignCause.Grudge, TargetNodeId = "N165",
            };
            objective.Candidates.Add("N165");
            focus.Objectives.Add(objective);
            exp.CampaignFoci.Add(focus);
            return w;
        }

        private static int Stock(WorldModel w, string node, string prefab)
        {
            int v;
            return w.Graph.Nodes[node].ShipStock.TryGetValue(prefab, out v) ? v : 0;
        }

        // ---- (a) the census counts what the defence can field -----------------------------------

        /// <summary>One rule for both sides: the census caps parked stock at the marshal's own wave
        /// count times its own largest wave. At the shipped tuning that is 3 waves of 24 hulls — one
        /// raid squadron's worth each (FAC-452).</summary>
        [Fact]
        public void TheFieldableCap_IsTheMarshalsWavesTimesItsLargestWave()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(CombatStrength.GuardMaxWaves(t) * CombatStrength.GuardWaveMaxHulls(t, 1.0),
                         CombatStrength.FieldableHullCap(t));
            Assert.Equal(72, CombatStrength.FieldableHullCap(t));
            // Posture only ever raises the wave, so the attacker prices the largest one.
            Assert.True(CombatStrength.GuardWaveMaxHulls(t, 0.0) <= CombatStrength.GuardWaveMaxHulls(t, 1.0));
        }

        /// <summary>N165's defence is 72 Scimitars, its turret and its floor — ~181k, not the
        /// 1,041,220 the whole stockpile read as.</summary>
        [Fact]
        public void N165sDefence_IsWhatItCanSail_NotEverythingParkedThere()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            FactionTuning t = w.Tuning;

            double fieldable = CombatStrength.OfNodeDefence(n165, w.PrefabCatalogue, t);
            Assert.Equal(72 * 2500 + 1200.00012 + 20, fieldable, 1);

            // Bands and the scalar agree about which hulls are present (bands carry no flat floor).
            Assert.Equal(fieldable - 20,
                         ShipPool.TotalDps(CombatStrength.NodeDefenceBands(n165, w.PrefabCatalogue, t, null))
                         + 1200.00012, 1);
            // Parked stock is untouched by being counted.
            Assert.Equal(416, n165.ShipStock["DS_Pirate_Scimitar"]);
        }

        /// <summary>Strongest first: the defence the attacker prices is the best the marshal could
        /// send, so a depot of weak hulls with a few strong ones counts the strong ones.</summary>
        [Fact]
        public void TheCensus_TakesTheStrongestHullsUpToTheCap()
        {
            WorldModel w = TestSupport.World();
            var cat = new LiveCatalogue();
            SpatialNode n = Node(w, "D", "B", 0, 0, 0, null, "DS_Pirate_Scimitar", 100, "Naginata", 3);
            List<KeyValuePair<string, int>> fielded = CombatStrength.FieldableParkedStock(n, cat, w.Tuning);

            Assert.Equal("Naginata", fielded[0].Key);
            Assert.Equal(3, fielded[0].Value);
            Assert.Equal("DS_Pirate_Scimitar", fielded[1].Key);
            Assert.Equal(69, fielded[1].Value);
        }

        /// <summary>The campaign budget reads the same census, so a region's stockpile cannot ask the
        /// yard for a fleet the raid gate would never need.</summary>
        [Fact]
        public void TheRegionCensus_ReadsTheSameFieldableFigure()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            n165.FeatureKey = "Belt";
            long census = TerrorCampaigns.RegionCensusDps(w, w.PrefabCatalogue, NodeRegion.Feature("Belt"), "EXP", w.Tuning);
            Assert.Equal((long)(72 * 2500 + 1200.00012), census);
        }

        // ---- the live case end to end -----------------------------------------------------------

        /// <summary>The live refusal, reproduced from its own numbers: the squadron it logged
        /// (haveDps 287,160, surviveDmg 229,728) against the whole stockpile is destroyed; against
        /// what N165 can field it lives.</summary>
        [Fact]
        public void TheLoggedSquadron_DiesToTheStockpile_AndLivesAgainstTheFieldableDefence()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            FactionTuning t = w.Tuning;

            var stockpile = new List<WeaponBand>(ShipPool.ConvoyWeaponBands(w.PrefabCatalogue, "DS_Pirate_Scimitar", 416));
            Assert.Equal(229728, CombatStrength.DamageToDestroy(287160, t.RaidSurvivalMargin), 0);
            Assert.False(CombatStrength.Survives(stockpile, n165, t, 287160, t.RaidSurvivalMargin));
            Assert.True(CombatStrength.Survives(CombatStrength.NodeDefenceBands(n165, w.PrefabCatalogue, t, null),
                                                n165, t, 287160, t.RaidSurvivalMargin));
        }

        /// <summary>The headline: the same world that refused every minute now commits, and the
        /// squadron is drawn from more than one node. Its strength is sized to what N165 can field,
        /// not to the whole stockpile.</summary>
        [Fact]
        public void EXPsCampaignOnN165_Commits_WithHullsFromMoreThanOneNode()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { exp }, w, w.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("RAID_REFUSED"));
            PendingFleetAction fleet = Assert.Single(exp.PendingFleets);
            Assert.Equal("N011", fleet.OriginNodeId);
            string dispatch = Assert.Single(log, l => l.Contains("FLEET_DISPATCH EXP"));
            Assert.Contains("drewFrom=", dispatch);
            Assert.Contains("drewFrom=N257", dispatch);
            // The cargo slot is an armed hull with a hold -- a Naginata from N257 -- not a drill ship,
            // and the squadron is sized against the ~181k N165 can field (three 24-hull waves of
            // Scimitars, FAC-452), not the whole stockpile.
            Assert.DoesNotContain("WHI-24Drill", fleet.Squadron);
            Assert.Equal(3, fleet.Squadron.Count(p => p == "Naginata"));
            Assert.Equal(14, Stock(w, "N257", "Naginata"));
            Assert.Equal(5, Stock(w, "N011", "WHI-24Drill"));
        }

        /// <summary>Negative control: a defence that can field more than the best squadron is still
        /// refused, and the line reads the fieldable figure. The gate must not simply open.</summary>
        [Fact]
        public void ADefenceWhoseFieldableForceWins_IsStillRefused()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            // The estate shrinks to one Tanto; N165 keeps its 416 Scimitars.
            foreach (SpatialNode n in w.Graph.OwnedBy("EXP").ToList()) n.ShipStock.Clear();
            w.Graph.Nodes["N011"].ShipStock["Tanto"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { exp }, w, w.Tuning, log);

            Assert.Empty(exp.PendingFleets);
            Assert.Contains(log, l => l.Contains("RAID_REFUSED") && l.Contains("defenceDps=181220"));
            Assert.Equal(1, Stock(w, "N011", "Tanto"));
        }

        // ---- (b) one pool, (d) each hull goes home ----------------------------------------------

        private static bool Dispatch(WorldModel w, FactionState st, SpatialNode target, double targetDps,
            Func<PendingFleetAction, bool> commit, out PendingFleetAction fleet)
        {
            var intent = new FactionIntent
            {
                Kind = ActionKind.Raid, TargetNodeId = target.Id,
                TargetFactionTag = target.OwnerFactionTag, Cost = w.Tuning.RaidCost,
            };
            return OffensiveDispatch.Dispatch(st, intent, w, w.Tuning, false, w.PrefabCatalogue,
                null, null, null, null, null, out fleet, null, targetDps, commit);
        }

        /// <summary>The lead slot takes the estate's strongest large hull, not the origin's: N011
        /// holds one Naginata and the lead is that one (the tie goes to the origin), and every
        /// further Naginata comes from N257 before N268 (nearer the target).</summary>
        [Fact]
        public void TheSquadron_TakesTheEstatesStrongestHulls_TiesToTheEarlierSource()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            PendingFleetAction fleet;
            Assert.True(Dispatch(w, exp, n165, 1_500_000, null, out fleet));

            Assert.Equal("Naginata", fleet.Squadron[0]);
            Assert.Equal(0, Stock(w, "N011", "Naginata"));
            int naginatas = fleet.Squadron.Count(p => p == "Naginata");
            Assert.True(naginatas > 1, "the estate's Naginatas join; got " + naginatas);
            Assert.Equal(16 - Math.Min(16, naginatas - 1), Stock(w, "N257", "Naginata"));
            // Origin-first would have spent the cap on N011's 24 Laevateinn.
            Assert.DoesNotContain("Laevateinn", fleet.Squadron);
        }

        /// <summary>A refused squadron drawn from several nodes hands each hull back to the node it
        /// came from. Nothing departed, so nothing may move.</summary>
        [Fact]
        public void ARefusal_ReturnsEachHullToItsOwnNode()
        {
            FactionState exp; SpatialNode n165;
            WorldModel w = Live(out exp, out n165);
            var before = new Dictionary<string, Dictionary<string, int>>();
            foreach (SpatialNode n in w.Graph.Nodes.Values)
                before[n.Id] = new Dictionary<string, int>(n.ShipStock);

            PendingFleetAction fleet;
            List<string> drawn = null;
            Assert.False(Dispatch(w, exp, n165, 1_500_000, f => { drawn = new List<string>(f.Squadron); return false; },
                                  out fleet));

            Assert.NotNull(drawn);
            Assert.True(drawn.Count > 1);
            foreach (SpatialNode n in w.Graph.Nodes.Values)
                foreach (var kv in before[n.Id])
                    Assert.True(kv.Value == Stock(w, n.Id, kv.Key),
                                n.Id + " " + kv.Key + ": had " + kv.Value + ", now " + Stock(w, n.Id, kv.Key));
        }

        /// <summary>The draw is the same whatever order a node's stock dictionary enumerates in.</summary>
        [Fact]
        public void TheDraw_IsDeterministic_AcrossStockInsertionOrder()
        {
            var cat = new LiveCatalogue()
                .Add("B-Gun", "Large", 0, new WeaponBand { Dps = 5000, RangeM = 800 })
                .Add("A-Gun", "Large", 0, new WeaponBand { Dps = 5000, RangeM = 800 });
            WorldModel w1 = TestSupport.World();
            SpatialNode a = Node(w1, "O", "F", 0, 0, 0, null, "B-Gun", 1, "A-Gun", 1);
            WorldModel w2 = TestSupport.World();
            SpatialNode b = Node(w2, "O", "F", 0, 0, 0, null, "A-Gun", 1, "B-Gun", 1);

            var r1 = SquadronAssembler.Assemble(a, cat, maxFighters: 0);
            var r2 = SquadronAssembler.Assemble(b, cat, maxFighters: 0);
            Assert.Equal("A-Gun", r1.Squadron[0]);
            Assert.Equal(r1.Squadron, r2.Squadron);
        }
    }
}
