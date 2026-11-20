using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A terror campaign remembers how it went (FAC-271): the requirement is a DPS budget sized
    /// from the target region's defenders and the opposition met last time; a campaign is scored
    /// at close from the faction's own fight ledger; repelled escalates and returns to the same
    /// region, succeeded de-escalates, left alone it calms down; and the yard closes the budget's
    /// gap in a squadron's shape — a swarm while the roster fills, a bigger hull once it is full.
    /// </summary>
    public class TerrorEscalationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        private static SpatialNode Node(FakeWorld w, string id, string owner, string feature, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 0.5; n.FeatureKey = feature; n.StableStationId = 1;
            return n;
        }

        /// <summary>FCTM at home; IGSL's three-node Rich Belt and SPRT's three-node Poor Belt.</summary>
        private static FakeWorld World(out FactionState fctm)
        {
            var w = new FakeWorld { Treasury = 5_000_000, UtcNow = T0 };
            Node(w, "HOME", "FCTM", "Cinder Nest", new Vec3(0, 0, 0)).ShipStock["Drone"] = 2;
            Node(w, "R1", "IGSL", "Rich Belt", new Vec3(3000, 0, 0));
            Node(w, "R2", "IGSL", "Rich Belt", new Vec3(3100, 0, 0));
            Node(w, "R3", "IGSL", "Rich Belt", new Vec3(3200, 0, 0));
            Node(w, "P1", "SPRT", "Poor Belt", new Vec3(-2000, 0, 0));
            Node(w, "P2", "SPRT", "Poor Belt", new Vec3(-2100, 0, 0));
            Node(w, "P3", "SPRT", "Poor Belt", new Vec3(-2200, 0, 0));
            fctm = new FactionState { Tag = "FCTM", PersonalityId = "factorum", RngSeed = 7 };
            foreach (string rival in new[] { "IGSL", "SPRT" }) fctm.Relations[rival] = -1.0;
            fctm.Drives[Drive.Rivalry] = 0.8;
            return w;
        }

        private static FightRecord Attack(string region, double ratio, long theirDps, DateTime at, int convoy = 3)
        {
            return new FightRecord
            {
                Utc = at, Kind = ActionKind.Raid, Attacking = true, OpponentTag = "IGSL", NodeId = "R1",
                RegionKey = region, AttackerDps = 1500, DefenderDps = theirDps, AttackerRatio = ratio,
                AttackerHullsLost = ratio > 0.5 ? 0 : convoy,
            };
        }

        private static readonly Func<string, bool> AlwaysOwned = id => true;

        // ---- the ledger ----------------------------------------------------------------------

        [Fact]
        public void AGuardIntercept_RecordsTheFightToBothSides_WithWhatEachBrought()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider = new FactionState { Tag = "RVN" };
            SpatialNode target = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "MERC1");
            target.OwnerFactionTag = "MERC"; target.HoldStrength = 0.8; target.Radius = 40; target.FeatureKey = "Belt";
            DateTime now = w.UtcNow;
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), Resolved = true,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 600, RangeM = 2000 } },
            });
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 3,
                DepartedUtc = now, EstimatedArrivalUtc = now,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 60, RangeM = 2000 } },
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defender, raider }, w, w.Tuning, new SeededRandom(1), log, null);

            Assert.Contains(log, l => l.StartsWith("GUARD_INTERCEPT"));
            FightRecord mine = Assert.Single(raider.Fights);
            Assert.True(mine.Attacking);
            Assert.False(mine.Won);
            Assert.Equal("MERC", mine.OpponentTag);
            Assert.Equal("feature:Belt", mine.RegionKey);
            Assert.Equal(60, mine.AttackerDps);
            Assert.Equal(600, mine.DefenderDps);
            Assert.Equal(0, mine.AttackerRatio);
            Assert.Equal(3, mine.AttackerHullsLost);
            FightRecord theirs = Assert.Single(defender.Fights);
            Assert.False(theirs.Attacking);
            Assert.True(theirs.Won);
            Assert.Equal("RVN", theirs.OpponentTag);
        }

        [Fact]
        public void ARaidThatLands_RecordsItsRatio_AndKeepsItsHulls()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider = new FactionState { Tag = "RVN" };
            SpatialNode target = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "MERC1");
            target.OwnerFactionTag = "MERC"; target.HoldStrength = 0.8; target.Radius = 40;
            DateTime now = w.UtcNow;
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 2,
                DepartedUtc = now, EstimatedArrivalUtc = now,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 900, RangeM = 2000 } },
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defender, raider }, w, w.Tuning, new SeededRandom(1), log, null);

            Assert.Contains(log, l => l.StartsWith("RAID_RESOLVED"));
            FightRecord mine = Assert.Single(raider.Fights);
            Assert.True(mine.Won);
            Assert.True(mine.AttackerRatio > 0.5);
            Assert.Equal(0, mine.AttackerHullsLost);
            Assert.True(mine.DefenderDps < 900); // the token station duel, not a real defence
            Assert.Single(defender.Fights);
        }

        [Fact]
        public void TheLedgerIsBounded_OldestFirstOut()
        {
            var s = new FactionState { Tag = "F" };
            for (int i = 0; i < 60; i++)
                FightLedger.Append(s, Attack("feature:X", 1.0, i, T0.AddMinutes(i)), 48);
            Assert.Equal(48, s.Fights.Count);
            Assert.Equal(12, s.Fights[0].DefenderDps);
            Assert.Equal(59, s.Fights[47].DefenderDps);
        }

        // ---- the budget -----------------------------------------------------------------------

        [Fact]
        public void TheCensus_IsTheStrongestForeignNodeInTheRegion_NotOursAndNotTheSum()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small").Add("Corvette", 2000, "Large").Add("Hauler", 0, "Large", 5000);
            w.Graph.Nodes["R1"].ShipStock["Corvette"] = 2;             // 4000
            w.Graph.Nodes["R2"].ShipStock["Hauler"] = 3;               // unarmed, nothing
            w.Graph.Nodes["R3"].StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 300, RangeM = 800 } };
            w.Graph.Nodes["P1"].ShipStock["Corvette"] = 5;             // another region
            Node(w, "R4", "FCTM", "Rich Belt", new Vec3(3300, 0, 0)).ShipStock["Corvette"] = 9; // ours, excluded

            // R1's two corvettes (4,000) beat R3's guns (300); the two are not added, and R4 is ours.
            Assert.Equal(4000, TerrorCampaigns.RegionCensusDps(w, cat, NodeRegion.Feature("Rich Belt"), "FCTM", TestSupport.Tuning()));
            Assert.Equal(10000, TerrorCampaigns.RegionCensusDps(w, cat, NodeRegion.Feature("Poor Belt"), "FCTM", TestSupport.Tuning()));
        }

        [Fact]
        public void TheBudget_TakesTheLargerOfCensusAndWhatWasMet_TimesEscalation()
        {
            var t = TestSupport.Tuning();
            var s = new FactionState { Tag = "FCTM" };
            Assert.Equal(4000, TerrorCampaigns.Budget(s, t, 4000));
            s.TerrorMetDps = 6000;
            Assert.Equal(6000, TerrorCampaigns.Budget(s, t, 4000));   // what it met beats what it sees
            s.TerrorEscalation = 2;
            Assert.Equal(12000, TerrorCampaigns.Budget(s, t, 4000));  // × (1 + 0.5 × 2)
            s.TerrorMetDps = 0;
            Assert.Equal(0, TerrorCampaigns.Budget(s, t, 0));         // nothing seen, nothing met: the hull floor alone
        }

        [Fact]
        public void TryStart_SizesTheObjectiveFromTheRegionCensus_AndSpendsTheRevengeFlag()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var cat = new ArmedCatalogue().Add("Corvette", 2000, "Large");
            w.Graph.Nodes["R1"].ShipStock["Corvette"] = 3;
            fctm.TerrorRevenge = true;
            var t = TestSupport.Tuning();

            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, cat));
            CampaignObjective o = TerrorCampaigns.Active(fctm);
            Assert.Equal(6000, o.WantedCombatDps);
            Assert.Equal(6000, TerrorCampaigns.WantedDps(fctm));
            Assert.False(fctm.TerrorRevenge);
        }

        // ---- the close -------------------------------------------------------------------------

        [Fact]
        public void ARepelledCampaign_Escalates_LearnsTheOpposition_AndComesBackToTheSameRegion()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small");
            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, cat));
            // Three raids under the campaign: one landed, two were destroyed -- a majority failed.
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.9, 1200, T0.AddMinutes(20)), t.FightHistoryCap);
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.0, 4000, T0.AddMinutes(40)), t.FightHistoryCap);
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.3, 2500, T0.AddMinutes(60)), t.FightHistoryCap);
            // A fight before the campaign and one in another region must not count.
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.0, 9000, T0.AddHours(-3)), t.FightHistoryCap);
            FightLedger.Append(fctm, Attack("feature:Poor Belt", 0.0, 9000, T0.AddMinutes(50)), t.FightHistoryCap);

            var log = new List<string>();
            DateTime after = T0 + t.TerrorCampaignDuration + TimeSpan.FromMinutes(1);
            Assert.Equal(0, TerrorCampaigns.Maintain(fctm, t, T0.AddMinutes(90), AlwaysOwned, w, cat, log)); // still running
            Assert.Equal(1, TerrorCampaigns.Maintain(fctm, t, after, AlwaysOwned, w, cat, log));

            Assert.Null(TerrorCampaigns.Active(fctm));
            Assert.Equal(1, fctm.TerrorEscalation);
            Assert.Equal(after, fctm.TerrorEscalationUtc);
            Assert.True(fctm.TerrorRevenge);
            Assert.Equal(4000, fctm.TerrorMetDps);
            Assert.Equal(6000, fctm.TerrorStandingDps);            // 4000 met × 1.5, no census in the belt
            Assert.Equal(6000, TerrorCampaigns.WantedDps(fctm));   // held between campaigns
            string closed = Assert.Single(log, l => l.StartsWith("TERROR_CAMPAIGN CLOSED FCTM"));
            Assert.Contains("feature=Rich Belt raids=3 won=1 lost=6 met_dps=4000 verdict=repelled escalation=1 standing_dps=6000", closed);

            // Revenge: the next target is the same region, which rotation alone would have skipped.
            w.UtcNow = T0 + t.TerrorCampaignCooldown + TimeSpan.FromMinutes(1);
            TerrorCampaigns.Target next = TerrorCampaigns.ChooseTarget(fctm, w, t);
            Assert.True(next.Found);
            Assert.Equal(NodeRegion.Feature("Rich Belt"), next.Region);
            fctm.TerrorRevenge = false;
            Assert.Equal(NodeRegion.Feature("Poor Belt"), TerrorCampaigns.ChooseTarget(fctm, w, t).Region);

            // And the escalated budget carries what it learned.
            Assert.True(TerrorCampaigns.TryStart(fctm, t, w.UtcNow, "HOME", next.Region, w, cat));
            Assert.Equal(6000, TerrorCampaigns.Active(fctm).WantedCombatDps);
        }

        [Fact]
        public void ASuccessfulCampaign_DeEscalates_AndRotatesAgain()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            fctm.TerrorEscalation = 2; fctm.TerrorRevenge = true; fctm.TerrorStandingDps = 9000;
            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, null));
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.9, 800, T0.AddMinutes(20)), t.FightHistoryCap);
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.2, 800, T0.AddMinutes(40)), t.FightHistoryCap);
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.7, 800, T0.AddMinutes(60)), t.FightHistoryCap);

            var log = new List<string>();
            TerrorCampaigns.Maintain(fctm, t, T0 + t.TerrorCampaignDuration, AlwaysOwned, w, null, log);
            Assert.Equal(1, fctm.TerrorEscalation);
            Assert.False(fctm.TerrorRevenge);
            Assert.Equal(800, fctm.TerrorMetDps);
            Assert.Equal(1200, fctm.TerrorStandingDps);            // 800 × 1.5 at escalation 1
            Assert.Contains(log, l => l.Contains("verdict=succeeded escalation=1"));
        }

        [Fact]
        public void ACampaignThatFoughtNothing_LearnsNothing()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            fctm.TerrorEscalation = 1; fctm.TerrorEscalationUtc = T0.AddHours(-1); fctm.TerrorMetDps = 3000; fctm.TerrorStandingDps = 4500;
            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, null));
            var log = new List<string>();
            TerrorCampaigns.Maintain(fctm, t, T0 + t.TerrorCampaignDuration, AlwaysOwned, w, null, log);
            Assert.Null(TerrorCampaigns.Active(fctm));
            Assert.Equal(1, fctm.TerrorEscalation);
            Assert.Equal(T0.AddHours(-1), fctm.TerrorEscalationUtc);
            Assert.Equal(3000, fctm.TerrorMetDps);
            Assert.Equal(4500, fctm.TerrorStandingDps);
            Assert.Contains(log, l => l.Contains("raids=0 won=0 lost=0 met_dps=0 verdict=idle escalation=1"));
        }

        [Fact]
        public void ACampaignWhoseStagingNodeIsLost_IsClosedAndScored_NotDroppedUnread()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, null));
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.0, 2000, T0.AddMinutes(20)), t.FightHistoryCap);
            var log = new List<string>();
            Assert.Equal(1, TerrorCampaigns.Maintain(fctm, t, T0.AddMinutes(30), id => false, w, null, log));
            Assert.Equal(1, fctm.TerrorEscalation);
            Assert.Contains(log, l => l.Contains("verdict=repelled staging_lost"));
        }

        [Fact]
        public void EscalationDecays_WhenLeftAloneForADay()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            fctm.TerrorEscalation = 2; fctm.TerrorEscalationUtc = T0; fctm.TerrorMetDps = 4000; fctm.TerrorStandingDps = 8000;
            var log = new List<string>();
            TerrorCampaigns.Maintain(fctm, t, T0.AddHours(23), AlwaysOwned, w, null, log);
            Assert.Equal(2, fctm.TerrorEscalation);
            TerrorCampaigns.Maintain(fctm, t, T0.AddHours(24), AlwaysOwned, w, null, log);
            Assert.Equal(1, fctm.TerrorEscalation);
            Assert.Equal(T0.AddHours(24), fctm.TerrorEscalationUtc);
            Assert.Equal(6000, fctm.TerrorStandingDps);
            Assert.Contains(log, l => l.StartsWith("TERROR_ESCALATION DECAYED FCTM escalation=1"));
            TerrorCampaigns.Maintain(fctm, t, T0.AddHours(48), AlwaysOwned, w, null, log);
            Assert.Equal(0, fctm.TerrorEscalation);
            Assert.Equal(0, fctm.TerrorStandingDps);
            TerrorCampaigns.Maintain(fctm, t, T0.AddHours(72), AlwaysOwned, w, null, log);
            Assert.Equal(0, fctm.TerrorEscalation);                  // does not go negative
        }

        [Fact]
        public void EscalationIsCapped()
        {
            FactionState fctm; FakeWorld w = World(out fctm);
            var t = TestSupport.Tuning();
            fctm.TerrorEscalation = t.TerrorEscalationCap;
            Assert.True(TerrorCampaigns.TryStart(fctm, t, T0, "HOME", NodeRegion.Feature("Rich Belt"), w, null));
            FightLedger.Append(fctm, Attack("feature:Rich Belt", 0.0, 2000, T0.AddMinutes(20)), t.FightHistoryCap);
            TerrorCampaigns.Maintain(fctm, t, T0 + t.TerrorCampaignDuration, AlwaysOwned, w, null, new List<string>());
            Assert.Equal(t.TerrorEscalationCap, fctm.TerrorEscalation);
        }

        // ---- the yard ----------------------------------------------------------------------------

        [Fact]
        public void BestSquadron_IsOneLargePlusTheBestFighters_FromOneNode()
        {
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small").Add("Interceptor", 900, "Small")
                .Add("Corvette", 2000, "Large").Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
            var a = new SpatialNode { Id = "A" }; a.ShipStock["Drone"] = 6; a.ShipStock["Interceptor"] = 2;
            a.ShipStock["Corvette"] = 1; a.ShipStock["Cruiser"] = 1; a.ShipStock["Hauler"] = 4;
            var b = new SpatialNode { Id = "B" }; b.ShipStock["Cruiser"] = 3;   // 18000 in stock, one large slot

            ShipPool.SquadronStrength best = ShipPool.BestSquadron(new[] { a, b }, cat, 4);
            Assert.Equal("A", best.NodeId);
            Assert.Equal(6000 + 900 + 900 + 500 + 500, best.Dps);
            Assert.Equal(5, best.Hulls);
            Assert.Equal(0, best.OpenSlots);
            Assert.Equal(500, best.WeakestDps);

            ShipPool.SquadronStrength onlyB = ShipPool.BestSquadron(new[] { b }, cat, 4);
            Assert.Equal(6000, onlyB.Dps);
            Assert.Equal(4, onlyB.OpenSlots);
        }

        [Fact]
        public void AnEmptyRoster_FillsWithASwarm_NotOneFlagship()
        {
            // Budget 4000 against one 500-DPS drone: four fighter slots open, so the gap is shared
            // across them (3500 / 4 = 875) and the cheapest hull clearing THAT share is built.
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small").Add("Interceptor", 1000, "Small")
                .Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
            var recipes = new CostRecipes(); recipes.CostByPrefab["Drone"] = 10; recipes.CostByPrefab["Interceptor"] = 30; recipes.CostByPrefab["Cruiser"] = 500;
            SpatialNode yard; WorldModel world = Yard(cat, recipes, out yard);
            yard.ShipStock["Drone"] = 1;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 4000 };

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Drone", "Interceptor", "Cruiser" } : new[] { "Hauler" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("minDps=875"));
            BuildQueueEntry entry = yard.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Interceptor", entry.PrefabId);
        }

        [Fact]
        public void AFullRoster_UpgradesItsWeakestSlot_WithAHullThatBeatsIt()
        {
            // One large and four fighters already: no slot is open, so the whole 5000 gap is the
            // floor, and only the cruiser clears it.
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small").Add("Gunboat", 1000, "Large")
                .Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
            var recipes = new CostRecipes(); recipes.CostByPrefab["Drone"] = 10; recipes.CostByPrefab["Gunboat"] = 40; recipes.CostByPrefab["Cruiser"] = 500;
            SpatialNode yard; WorldModel world = Yard(cat, recipes, out yard);
            yard.ShipStock["Drone"] = 4; yard.ShipStock["Gunboat"] = 1;   // squadron 3000
            var state = new FactionState { Tag = "F", TerrorStandingDps = 8000 };

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Drone", "Gunboat", "Cruiser" } : new[] { "Hauler" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("minDps=5000"));
            Assert.Equal("Cruiser", yard.BuildQueue.Find(e => e.Role == "Raid").PrefabId);
        }

        [Fact]
        public void AGapNoHullCanClose_BuildsTheBestValuePerUnit_AndSaysSo()
        {
            var cat = new ArmedCatalogue().Add("Drone", 500, "Small").Add("Gunboat", 1000, "Large").Add("Hauler", 0, "Large", 5000);
            var recipes = new CostRecipes(); recipes.CostByPrefab["Drone"] = 10; recipes.CostByPrefab["Gunboat"] = 40;
            SpatialNode yard; WorldModel world = Yard(cat, recipes, out yard);
            yard.ShipStock["Drone"] = 4; yard.ShipStock["Gunboat"] = 1;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 50000 };

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Drone", "Gunboat" } : new[] { "Hauler" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            // The floor is posted at the whole gap, NOT clamped to the strongest hull: a gap wider
            // than any one hull is what tells the chooser the answer is several of them.
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("minDps=47000"));

            // Gunboat is the strongest hull in the pool and Drone is the best value per unit built
            // (500/10 against 1000/40). Four Drones cost one Gunboat and bring twice its power, so
            // the swarm is what closes the gap while it still matters.
            Assert.Equal("Drone", yard.BuildQueue.Find(e => e.Role == "Raid").PrefabId);
            Assert.Contains(log, l => l.Contains("BOM_DPS_FLOOR_CLAMPED") && l.Contains("role=Raid"));
        }

        [Fact]
        public void ARosterAlreadyMadeOfTheStrongestHull_PostsNothing()
        {
            // One large and four fighters, every one the strongest hull the role can build: the
            // budget is unmet but no build can improve the squadron, so nothing is posted -- the
            // alternative is a yard building the same hull forever.
            var cat = new ArmedCatalogue().Add("Gunboat", 1000, "Large").Add("Fighter", 1000, "Small").Add("Hauler", 0, "Large", 5000);
            var recipes = new CostRecipes(); recipes.CostByPrefab["Gunboat"] = 40; recipes.CostByPrefab["Fighter"] = 30;
            SpatialNode yard; WorldModel world = Yard(cat, recipes, out yard);
            yard.ShipStock["Gunboat"] = 1; yard.ShipStock["Fighter"] = 4;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 50000 };

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Gunboat", "Fighter" } : new[] { "Hauler" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);
            Assert.DoesNotContain(log, l => l.Contains("role=Raid") && l.Contains("minDps="));

            // With the large slot open the same stock is NOT maxed: the gap is still posted, whole.
            yard.ShipStock["Gunboat"] = 0;
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);
            Assert.Contains(log, l => l.Contains("role=Raid") && l.Contains("minDps=46000"));
        }

        [Fact]
        public void ABudgetAlreadyMet_PostsNoPowerDemand()
        {
            var cat = new ArmedCatalogue().Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
            var recipes = new CostRecipes(); recipes.CostByPrefab["Cruiser"] = 500;
            SpatialNode yard; WorldModel world = Yard(cat, recipes, out yard);
            yard.ShipStock["Cruiser"] = 1;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 4000 };
            var log = new List<string>();
            new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Cruiser" } }.Tick(new List<FactionState> { state }, world, world.Tuning, log);
            Assert.DoesNotContain(log, l => l.Contains("role=Raid") && l.Contains("minDps="));
        }

        // ---- it survives a restart ---------------------------------------------------------------

        [Fact]
        public void EscalationTheLedgerAndTheBudget_RoundTripThroughTheBrainFile()
        {
            var s = new FactionState { Tag = "FCTM", FactionId = 1, PersonalityId = "factorum", RngSeed = 3 };
            var t = TestSupport.Tuning();
            s.TerrorEscalation = 2; s.TerrorEscalationUtc = T0; s.TerrorRevenge = true; s.TerrorMetDps = 4000; s.TerrorStandingDps = 8000;
            FightLedger.Append(s, Attack("feature:Rich Belt", 0.3, 2500, T0.AddMinutes(5)), t.FightHistoryCap);
            Assert.True(TerrorCampaigns.TryStart(s, t, T0.AddMinutes(10), "N036", NodeRegion.Feature("Rich Belt"), null, null));
            Assert.Equal(8000, TerrorCampaigns.Active(s).WantedCombatDps);   // 4000 met × 2.0
            Assert.False(s.TerrorRevenge);

            BrainDoc doc = FactionStore.ToBrainDoc(s, new SpatialNode[0], FactionStore.DefaultLogCap);
            FactionState restored = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(2, restored.TerrorEscalation);
            Assert.Equal(T0, restored.TerrorEscalationUtc);
            Assert.False(restored.TerrorRevenge);
            Assert.Equal(4000, restored.TerrorMetDps);
            Assert.Equal(8000, restored.TerrorStandingDps);
            Assert.Equal(8000, TerrorCampaigns.Active(restored).WantedCombatDps);
            FightRecord f = Assert.Single(restored.Fights);
            Assert.Equal(ActionKind.Raid, f.Kind);
            Assert.True(f.Attacking);
            Assert.Equal("IGSL", f.OpponentTag);
            Assert.Equal("feature:Rich Belt", f.RegionKey);
            Assert.Equal(1500, f.AttackerDps);
            Assert.Equal(2500, f.DefenderDps);
            Assert.Equal(0.3, f.AttackerRatio);
            Assert.Equal(3, f.AttackerHullsLost);
        }

        [Fact]
        public void AnUntouchedBrain_WritesNoneOfTheNewKeys()
        {
            var s = new FactionState { Tag = "FCTM", FactionId = 1, PersonalityId = "factorum", RngSeed = 3 };
            BrainDoc doc = FactionStore.ToBrainDoc(s, new SpatialNode[0], FactionStore.DefaultLogCap);
            Assert.Null(doc.TerrorEscalation);
            Assert.Null(doc.TerrorEscalationUtc);
            Assert.False(doc.TerrorRevenge);
            Assert.Null(doc.TerrorMetDps);
            Assert.Null(doc.TerrorStandingDps);
            Assert.Null(doc.Fights);
        }
    }
}
