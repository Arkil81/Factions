using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FleetMunitions"/> and <see cref="DuelMunitions"/> (FAC-386): a full load
    /// drawn from the departure node, combat fleets' priority on that stock, a short departure,
    /// the duel firing only what a fleet carries, and the unspent remainder coming home.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public class MunitionsTests : IDisposable
    {
        private const string Nato = "NATO_25x184mm";
        private const string Gunship = "Gunship";
        private const string Freighter = "Freighter";
        private const string Artillery = "Artillery";
        private const string Shell = "LargeCalibreAmmo";
        private const string BeamShip = "BeamShip";
        private const string BeamGunship = "BeamGunship";
        private const string EnergyCharge = "ANEWS_EnergyChargeMag";

        public MunitionsTests() { DebugTraces.ResetToDefaults(); }
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        // ---- fixtures -----------------------------------------------------------------------

        /// <summary>A gunship firing 24 magazines of NATO at 0.1 a second, and a freighter carrying
        /// one small gun on the same magazine. Anything else is unarmed.</summary>
        private sealed class ArmedCatalogue : IPrefabCatalogue, IMunitionPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return prefabId == Freighter ? 100000 : 0; }
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
            public double DryMassKg(string prefabId) { return 100000; }

            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                if (prefabId == Gunship) return new[] { new WeaponBand { Dps = 100, RangeM = 2000 } };
                if (prefabId == Freighter) return new[] { new WeaponBand { Dps = 10, RangeM = 2000 } };
                return new WeaponBand[0];
            }

            public IReadOnlyList<MunitionSlot> MunitionSlots(string prefabId)
            {
                if (prefabId == Gunship)
                    return new[] { new MunitionSlot { MagazineKey = Nato, RangeM = 2000, Dps = 100, MagazinesPerSecond = 0.1f, FullLoad = 24 } };
                if (prefabId == Freighter)
                    return new[] { new MunitionSlot { MagazineKey = Nato, RangeM = 2000, Dps = 10, MagazinesPerSecond = 0.01f, FullLoad = 8 } };
                if (prefabId == Artillery)   // the Naginata's artillery slot, as scanned live
                    return new[] { new MunitionSlot { MagazineKey = Shell, RangeM = 2000, Dps = 41777.79f, MagazinesPerSecond = 2.45751619f, FullLoad = 99 } };
                if (prefabId == BeamShip)    // an AnEWS laser: its magazine is generated from power
                    return new[] { EnergySlot() };
                if (prefabId == BeamGunship)
                    return new[]
                    {
                        new MunitionSlot { MagazineKey = Nato, RangeM = 2000, Dps = 100, MagazinesPerSecond = 0.1f, FullLoad = 24 },
                        EnergySlot(),
                    };
                return null;
            }
        }

        private static MunitionSlot EnergySlot()
        {
            return new MunitionSlot
            {
                MagazineKey = EnergyCharge, RangeM = 2000, Dps = 5000, MagazinesPerSecond = 6f, FullLoad = 10,
                SelfSupplied = true,
            };
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static WorldModel World(int mode = MunitionDrawModes.Draw)
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new ArmedCatalogue();
            w.Tuning.MunitionDrawMode = mode;
            w.Tuning.CombatTurnFireSeconds = 30.0;
            return w;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            n.Radius = 40;
            return n;
        }

        private static PendingFleetAction Fleet(ActionKind kind, string prefab, string origin, string tag = "RVN")
        {
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = tag, OriginNodeId = origin, ShipPrefabId = prefab, ConvoySize = 1,
                FleetId = "F" + kind,
            };
        }

        private static long Stock(WorldModel w, string node)
        {
            long have;
            w.Graph.Nodes[node].Inventory.TryGetValue(Nato, out have);
            return have;
        }

        // ---- arming ---------------------------------------------------------------------------

        [Fact]
        public void Arm_DrawsAFullLoadFromTheOwnedOrigin()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            var log = new List<string>();

            FleetMunitions.Arm(ref raid, w, w.Tuning, log);

            Assert.True(raid.MunitionsArmed);
            Assert.Equal(24, raid.Munitions[Nato]);
            Assert.Equal(76, Stock(w, "N1"));
            Assert.Empty(log);   // a full departure is routine and says nothing by default
        }

        [Fact]
        public void Arm_CombatFleetShortOfStock_DepartsShortAndSaysSo()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 10;
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");
            var log = new List<string>();

            FleetMunitions.Arm(ref guard, w, w.Tuning, log);

            Assert.True(guard.MunitionsArmed);             // departs anyway: never refused
            Assert.Equal(10, guard.Munitions[Nato]);       // with only what the node had
            Assert.Equal(0, Stock(w, "N1"));
            string line = Assert.Single(log);
            Assert.StartsWith("MUNITIONS_SHORT ", line);
            Assert.Contains("kind=Guard", line);
            Assert.Contains("loaded=10/24", line);
            Assert.Contains("short=" + Nato + "=10/24", line);
        }

        [Fact]
        public void Arm_NodeWithNothing_ArmsTheFleetWithAnEmptyLoad()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());

            // Armed, holding zero: it fires nothing. Not "unarmed", which would fire without limit.
            Assert.True(raid.MunitionsArmed);
            Assert.Equal(0, raid.Munitions[Nato]);
        }

        [Fact]
        public void Arm_NonCombatShortfall_IsSilentUnlessTheTraceIsOn()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 3;
            PendingFleetAction trade = Fleet(ActionKind.Trade, Freighter, "N1");
            var log = new List<string>();
            FleetMunitions.Arm(ref trade, w, w.Tuning, log);
            Assert.Equal(3, trade.Munitions[Nato]);
            Assert.Empty(log);

            DebugTraces.Munitions = true;
            PendingFleetAction second = Fleet(ActionKind.Trade, Freighter, "N1");
            FleetMunitions.Arm(ref second, w, w.Tuning, log);
            Assert.StartsWith("MUNITIONS_SHORT ", Assert.Single(log));
        }

        [Fact]
        public void CombatHasPriority_AConvoyDispatchedFirstLeavesTheGuardItsLoad()
        {
            // The node holds exactly one gunship's full load, and that gunship is parked there. An
            // armed convoy leaves first; the Guard that follows must still arm in full.
            WorldModel w = World();
            SpatialNode node = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            node.Inventory[Nato] = 24;
            node.ShipStock[Gunship] = 1;

            PendingFleetAction trade = Fleet(ActionKind.Trade, Freighter, "N1");
            FleetMunitions.Arm(ref trade, w, w.Tuning, new List<string>());
            Assert.True(trade.MunitionsArmed);
            Assert.Equal(0, trade.Munitions[Nato]);        // the convoy flies short

            node.ShipStock.Remove(Gunship);                // the dispatcher takes the hull
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");
            var log = new List<string>();
            FleetMunitions.Arm(ref guard, w, w.Tuning, log);
            Assert.Equal(24, guard.Munitions[Nato]);       // the Guard arms in full
            Assert.Empty(log);
        }

        [Fact]
        public void CombatPriority_StockAboveTheReserveIsTheConvoys()
        {
            WorldModel w = World();
            SpatialNode node = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            node.Inventory[Nato] = 30;
            node.ShipStock[Gunship] = 1;

            PendingFleetAction trade = Fleet(ActionKind.Trade, Freighter, "N1");
            FleetMunitions.Arm(ref trade, w, w.Tuning, new List<string>());
            Assert.Equal(6, trade.Munitions[Nato]);        // 30 - 24 reserved
            Assert.Equal(24, Stock(w, "N1"));
        }

        [Fact]
        public void Arm_ModeOff_TouchesNothing()
        {
            WorldModel w = World(MunitionDrawModes.Off);
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());

            Assert.False(raid.MunitionsArmed);
            Assert.Null(raid.Munitions);
            Assert.Equal(100, Stock(w, "N1"));
        }

        [Fact]
        public void Arm_UnarmedHull_IsNotArmed()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction haul = Fleet(ActionKind.Haul, "Barge", "N1");

            FleetMunitions.Arm(ref haul, w, w.Tuning, new List<string>());

            Assert.False(haul.MunitionsArmed);
            Assert.Equal(100, Stock(w, "N1"));
        }

        [Fact]
        public void Arm_Twice_DrawsOnce()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());
            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());

            Assert.Equal(76, Stock(w, "N1"));
        }

        [Fact]
        public void Arm_ForeignOrigin_DrawsNothing()
        {
            WorldModel w = World();
            AddNode(w, "N1", "MERC", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            var log = new List<string>();

            FleetMunitions.Arm(ref raid, w, w.Tuning, log);

            Assert.Equal(0, raid.Munitions[Nato]);
            Assert.Equal(100, Stock(w, "N1"));
            Assert.Contains("node_not_owned", Assert.Single(log));
        }

        [Fact]
        public void Arm_SquadronDrawsForEveryMember()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.Squadron = new List<string> { Gunship, Gunship, Freighter };

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());

            Assert.Equal(24 + 24 + 8, raid.Munitions[Nato]);
        }

        // ---- sortie sizing ----------------------------------------------------------------------

        private static MunitionSlot Slot(float perSecond, int inventory, float rangeM)
        {
            return new MunitionSlot { MagazineKey = Shell, RangeM = rangeM, Dps = 1, MagazinesPerSecond = perSecond, FullLoad = inventory };
        }

        [Fact]
        public void SortieLoad_AHeavyGunCarriesAFullDuelOfFire()
        {
            // The Naginata's artillery: 99 shells in its turrets, 2.4575 a second across them. At
            // 30 s a turn over the three brackets it reaches, one duel takes 222 shells.
            WorldModel w = World();
            Assert.Equal(222, FleetMunitions.SortieLoad(Slot(2.45751619f, 99, 2000), w.Tuning));
        }

        [Fact]
        public void SortieLoad_CountsOnlyTheTurnsTheSlotReaches()
        {
            // 1400 m reaches the 800 m and point-blank brackets, not 2000 m: 1.5 x 30 x 2.
            WorldModel w = World();
            Assert.Equal(90, FleetMunitions.SortieLoad(Slot(1.5f, 30, 1400), w.Tuning));
        }

        [Fact]
        public void SortieLoad_NeverBelowWhatTheWeaponsHold()
        {
            // A gatling's 24 magazines outlast a duel (0.1 x 90 = 9); it keeps the 24.
            WorldModel w = World();
            Assert.Equal(24, FleetMunitions.SortieLoad(Slot(0.1f, 24, 2000), w.Tuning));
        }

        [Fact]
        public void SortieLoad_ScalesWithDuels_AndZeroFallsBackToTheInventory()
        {
            WorldModel w = World();
            w.Tuning.MunitionSortieDuels = 2.0;
            Assert.Equal(443, FleetMunitions.SortieLoad(Slot(2.45751619f, 99, 2000), w.Tuning));
            w.Tuning.MunitionSortieDuels = 0;
            Assert.Equal(99, FleetMunitions.SortieLoad(Slot(2.45751619f, 99, 2000), w.Tuning));
        }

        [Fact]
        public void Arm_DrawsTheEstimatedSortieNotTheTurretInventory()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Shell] = 1000;
            PendingFleetAction guard = Fleet(ActionKind.Guard, Artillery, "N1");

            FleetMunitions.Arm(ref guard, w, w.Tuning, new List<string>());

            Assert.Equal(222, guard.Munitions[Shell]);
            Assert.Equal(1000 - 222, w.Graph.Nodes["N1"].Inventory[Shell]);
        }

        [Fact]
        public void Duel_AnArtilleryLoadLastsTheWholeDuel()
        {
            // With the old turret-inventory load (99) the artillery ran dry inside T2; sized for a
            // duel it fires at full Dps through all three turns.
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "RVN", ShipPrefabId = Artillery, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long> { { Shell, 222 } },
            });
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);

            Assert.Equal(41777.79, side.Fire(2000), 0);
            Assert.Equal(41777.79, side.Fire(800), 0);
            Assert.Equal(41777.79, side.Fire(0), 0);
        }

        // ---- siege sizing and retreat ---------------------------------------------------------

        private static List<FleetStop> Siege(int dwells, string target, string home)
        {
            var stops = new List<FleetStop>();
            for (int i = 0; i < dwells; i++) stops.Add(new FleetStop { NodeId = target, Kind = FleetStopKind.Engage, DurationSeconds = 300 });
            stops.Add(new FleetStop { NodeId = home, Kind = FleetStopKind.Return });
            return stops;
        }

        [Fact]
        public void FightsPlanned_CountsTheItinerarysEngageDwells()
        {
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.RemainingStops = Siege(3, "T", "N1");
            Assert.Equal(3, FleetMunitions.FightsPlanned(raid));

            raid.CurrentStopKind = FleetStopKind.Engage;
            raid.RemainingStops = Siege(2, "T", "N1");
            Assert.Equal(3, FleetMunitions.FightsPlanned(raid));   // the one it is on counts too

            Assert.Equal(1, FleetMunitions.FightsPlanned(Fleet(ActionKind.Guard, Gunship, "N1")));
        }

        [Fact]
        public void Arm_ARaidCarriesEverySiegeCycle_AGuardOneDuel()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Shell] = 5000;
            PendingFleetAction raid = Fleet(ActionKind.Raid, Artillery, "N1");
            raid.RemainingStops = Siege(3, "T", "N1");
            PendingFleetAction guard = Fleet(ActionKind.Guard, Artillery, "N1");

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());
            FleetMunitions.Arm(ref guard, w, w.Tuning, new List<string>());

            Assert.Equal(664, raid.Munitions[Shell]);    // 2.4575 x 30 x 3 turns x 3 dwells
            Assert.Equal(222, guard.Munitions[Shell]);   // one duel
        }

        [Fact]
        public void BeginRetreat_EndsTheDwellAndDropsTheSiege()
        {
            WorldModel w = World();
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.CurrentStopKind = FleetStopKind.Engage;
            raid.EstimatedArrivalUtc = w.UtcNow.AddMinutes(4);
            raid.RemainingStops = Siege(2, "T", "N1");

            Assert.True(PendingFleetResolver.BeginRetreat(ref raid, w.UtcNow, w.Tuning, "out_of_ammo"));

            Assert.Equal("out_of_ammo", raid.RetreatReason);
            Assert.Equal(w.UtcNow, raid.EstimatedArrivalUtc);
            Assert.Equal(FleetStopKind.Return, Assert.Single(raid.RemainingStops).Kind);
            Assert.False(PendingFleetResolver.BeginRetreat(ref raid, w.UtcNow, w.Tuning, "out_of_ammo"));   // once
        }

        [Fact]
        public void BeginRetreat_AGuardsDutyWindowEnds_AndATransitIsLeftAlone()
        {
            WorldModel w = World();
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");
            guard.DepartedUtc = w.UtcNow.AddMinutes(-2);
            guard.EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1);
            Assert.True(guard.IsActiveGuard(w.UtcNow, w.Tuning));

            Assert.True(PendingFleetResolver.BeginRetreat(ref guard, w.UtcNow, w.Tuning, "out_of_ammo"));
            Assert.False(guard.IsActiveGuard(w.UtcNow, w.Tuning));

            PendingFleetAction flying = Fleet(ActionKind.Raid, Gunship, "N1");
            flying.CurrentStopKind = FleetStopKind.Service;
            flying.EstimatedArrivalUtc = w.UtcNow.AddMinutes(3);
            Assert.False(PendingFleetResolver.BeginRetreat(ref flying, w.UtcNow, w.Tuning, "out_of_ammo"));
            Assert.Null(flying.RetreatReason);
        }

        private static (List<FactionState> states, List<string> log) EngageLegResolves(WorldModel w, PendingFleetAction raid, bool withGuard)
        {
            var stDefender = new FactionState { Tag = "MERC" };
            var stRaider = new FactionState { Tag = "RVN" };
            if (withGuard)
                stDefender.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                    DepartedUtc = w.UtcNow.AddMinutes(-5), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-5),
                    Resolved = true, WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 500, RangeM = 2000 } },
                });
            stRaider.PendingFleets.Add(raid);
            var states = new List<FactionState> { stDefender, stRaider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
            return (states, log);
        }

        private static PendingFleetAction RaidOnItsEngageLeg(WorldModel w, int moreDwells)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginNodeId = "RVN1", ReturnNodeId = "RVN1", FleetId = "RAID02",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-5), EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Engage, RemainingStops = Siege(moreDwells, "MERC1", "RVN1"),
                ShipPrefabId = Gunship, ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 2000 } },
            };
        }

        [Fact]
        public void ARetreatingRaid_ForfeitsItsFight_AndIsNotDuelledLeaving()
        {
            WorldModel w = World();
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = RaidOnItsEngageLeg(w, 0);
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 0 } };
            raid.RetreatReason = "out_of_ammo";

            var result = EngageLegResolves(w, raid, withGuard: true);
            string log = string.Join("\n", result.log);

            Assert.Contains("FLEET_RETREATED [RAID02] RVN kind=Raid @MERC1 reason=out_of_ammo", log);
            Assert.DoesNotContain("RAID_RESOLVED", log);
            Assert.DoesNotContain("GUARD_INTERCEPT", log);
            Assert.DoesNotContain("RAID_BREAKS_GUARD", log);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);   // nothing stripped
        }

        [Fact]
        public void ARaidThatEmptiedItsMagazines_BreaksOffTheSiege()
        {
            WorldModel w = World();
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = RaidOnItsEngageLeg(w, 2);
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 0 } };

            var result = EngageLegResolves(w, raid, withGuard: false);

            Assert.Contains("siege_broken_off dropped=2 out_of_ammo", string.Join("\n", result.log));
        }

        [Fact]
        public void ARaidWithAmmunitionLeft_HoldsTheSiege()
        {
            // The control: same raid, magazines to spare, keeps its remaining dwells.
            WorldModel w = World();
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = RaidOnItsEngageLeg(w, 2);
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 500 } };

            var result = EngageLegResolves(w, raid, withGuard: false);
            string log = string.Join("\n", result.log);

            Assert.DoesNotContain("siege_broken_off", log);
            Assert.Contains("siege_holds", log);
        }

        // ---- shortfall demand -------------------------------------------------------------------

        private static bool TryDemand(WorldModel w, string kind, string node, out DemandEntry entry)
        {
            return w.Demand.TryGet("RVN", kind, node, null, out entry);
        }

        [Fact]
        public void ShortCombatDeparture_AsksForTheMissingMagazinesAtItsNode()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 10;
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");
            var log = new List<string>();

            FleetMunitions.Arm(ref guard, w, w.Tuning, log);

            DemandEntry need, item;
            Assert.True(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out need));
            Assert.Equal(14, need.Quantity);                       // 24 wanted, 10 drawn
            Assert.Equal(DemandClass.Fuel, need.Class);
            Assert.True(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out item));
            Assert.Equal(24, item.Quantity);                       // gross: MatchVectors nets stock
            Assert.Equal(DemandClass.Fuel, item.Class);
            Assert.Equal(w.UtcNow.AddMinutes(60), item.ExpiresUtc);
            Assert.Contains(" demanded=1", Assert.Single(log));
        }

        [Fact]
        public void FullDeparture_AsksForNothing()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0)).Inventory[Nato] = 100;
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");

            FleetMunitions.Arm(ref guard, w, w.Tuning, new List<string>());

            DemandEntry any;
            Assert.False(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out any));
            Assert.False(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out any));
        }

        [Fact]
        public void ShortNonCombatDeparture_AsksForNothing()
        {
            // A convoy flies short whenever combat holds the stock; that is the priority working,
            // not a supply gap.
            WorldModel w = World();
            SpatialNode node = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            node.Inventory[Nato] = 24;
            node.ShipStock[Gunship] = 1;
            PendingFleetAction trade = Fleet(ActionKind.Trade, Freighter, "N1");

            FleetMunitions.Arm(ref trade, w, w.Tuning, new List<string>());

            DemandEntry any;
            Assert.False(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out any));
        }

        [Fact]
        public void ShortDepartureFromAForeignNode_AsksForNothing()
        {
            WorldModel w = World();
            AddNode(w, "N1", "MERC", new Vec3(0, 0, 0));
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");

            FleetMunitions.Arm(ref raid, w, w.Tuning, new List<string>());

            DemandEntry any;
            Assert.False(w.Demand.TryGet("RVN", DemandRegister.NeedKind(Nato), "N1", null, out any));
            Assert.False(w.Demand.TryGet("MERC", DemandRegister.NeedKind(Nato), "N1", null, out any));
        }

        [Fact]
        public void ShortfallDemand_NeverLowersARowAlreadyThere()
        {
            WorldModel w = World();
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            w.Demand.Post(new DemandEntry
            {
                FactionTag = "RVN", Kind = DemandRegister.ItemKind(Nato), NodeId = "N1",
                ExpiresUtc = w.UtcNow.AddHours(3), Quantity = 500, Class = DemandClass.Catalog,
            });
            w.Demand.Post(DemandEntry.ForNeed("RVN", Nato, "N1", w.UtcNow.AddMinutes(5), 40, DemandClass.Fuel));
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");

            FleetMunitions.Arm(ref guard, w, w.Tuning, new List<string>());

            DemandEntry item, need;
            Assert.True(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out item));
            Assert.Equal(500, item.Quantity);                     // untouched...
            Assert.Equal(DemandClass.Catalog, item.Class);        // ...and not restamped
            Assert.True(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out need));
            Assert.Equal(40, need.Quantity);                      // the larger need stands
            Assert.Equal(w.UtcNow.AddMinutes(60), need.ExpiresUtc);
        }

        [Fact]
        public void ShortfallDemand_ZeroMinutesPostsNothing()
        {
            WorldModel w = World();
            w.Tuning.MunitionShortfallDemandMinutes = 0;
            AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction guard = Fleet(ActionKind.Guard, Gunship, "N1");
            var log = new List<string>();

            FleetMunitions.Arm(ref guard, w, w.Tuning, log);

            DemandEntry any;
            Assert.False(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out any));
            Assert.DoesNotContain("demanded=", Assert.Single(log));
        }

        // ---- standing reserve -------------------------------------------------------------------

        private static int PostReserve(WorldModel w, SpatialNode node, IDictionary<string, DemandEntry> rows = null)
        {
            var ctx = new LiveProductionExecutionContext(w, new List<string>());
            return FleetMunitions.PostReserveForNode("RVN", node, w.PrefabCatalogue, ctx, w.Tuning,
                w.UtcNow.AddMinutes(10), rows, new Dictionary<string, Dictionary<string, long>>());
        }

        private static SpatialNode FoundedWithGunships(WorldModel w, int gunships, long stock)
        {
            SpatialNode node = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            node.StableStationId = 42;
            node.ShipStock[Gunship] = gunships;
            if (stock > 0) node.Inventory[Nato] = stock;
            return node;
        }

        [Fact]
        public void Reserve_AsksForAFullLoadPerParkedCombatHull()
        {
            WorldModel w = World();
            SpatialNode node = FoundedWithGunships(w, 2, 10);

            Assert.Equal(1, PostReserve(w, node));

            DemandEntry need, item;
            Assert.True(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out need));
            Assert.Equal(2 * 24 - 10, need.Quantity);
            Assert.Equal(DemandClass.Fuel, need.Class);
            Assert.True(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out item));
            Assert.Equal(2 * 24, item.Quantity);                   // gross
        }

        [Fact]
        public void Reserve_ScalesWithTheLoadsSetting()
        {
            WorldModel w = World();
            w.Tuning.MunitionReserveLoads = 2.0;
            SpatialNode node = FoundedWithGunships(w, 1, 0);

            PostReserve(w, node);

            DemandEntry item;
            Assert.True(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out item));
            Assert.Equal(48, item.Quantity);
        }

        [Fact]
        public void Reserve_ANodeHoldingItsTargetAsksNothing()
        {
            WorldModel w = World();
            SpatialNode node = FoundedWithGunships(w, 1, 24);
            Assert.Equal(0, PostReserve(w, node));
            DemandEntry any;
            Assert.False(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out any));
        }

        [Fact]
        public void Reserve_NothingForAnUnfoundedNode_ModeOff_OrZeroLoads()
        {
            WorldModel w = World();
            SpatialNode node = FoundedWithGunships(w, 1, 0);
            node.StableStationId = 0;
            Assert.Equal(0, PostReserve(w, node));

            node.StableStationId = 42;
            w.Tuning.MunitionReserveLoads = 0;
            Assert.Equal(0, PostReserve(w, node));

            w.Tuning.MunitionReserveLoads = 1.0;
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Off;
            Assert.Equal(0, PostReserve(w, node));

            DemandEntry any;
            Assert.False(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out any));
        }

        [Fact]
        public void Reserve_OnlyCombatHullsCount()
        {
            // An unarmed hauler parked at the node asks for no ammunition.
            WorldModel w = World();
            SpatialNode node = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            node.StableStationId = 42;
            node.ShipStock["Barge"] = 3;
            Assert.Equal(0, PostReserve(w, node));
        }

        [Fact]
        public void Reserve_LeavesACoveringItemRowAlone()
        {
            WorldModel w = World();
            SpatialNode node = FoundedWithGunships(w, 1, 0);
            var catalogRow = new DemandEntry
            {
                FactionTag = "RVN", Kind = DemandRegister.ItemKind(Nato), NodeId = "N1",
                ExpiresUtc = w.UtcNow.AddHours(3), Quantity = 500, Class = DemandClass.Catalog,
            };
            w.Demand.Post(catalogRow);
            var rows = new Dictionary<string, DemandEntry> { { "N1|" + catalogRow.Kind, catalogRow } };

            PostReserve(w, node, rows);

            DemandEntry item, need;
            Assert.True(TryDemand(w, DemandRegister.ItemKind(Nato), "N1", out item));
            Assert.Equal(500, item.Quantity);
            Assert.Equal(DemandClass.Catalog, item.Class);
            Assert.True(TryDemand(w, DemandRegister.NeedKind(Nato), "N1", out need));
            Assert.Equal(24, need.Quantity);                      // the need row still stands
        }

        // ---- returning ------------------------------------------------------------------------

        [Fact]
        public void Credit_ReturnsWhatIsAboard_OnceAndNeverMore()
        {
            WorldModel w = World();
            SpatialNode home = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 17 } };

            Assert.Equal(17, FleetMunitions.Credit(ref raid, home, 1, w, null));
            Assert.Equal(0, FleetMunitions.Credit(ref raid, home, 1, w, null));   // a second path returns nothing
            Assert.Equal(17, Stock(w, "N1"));
        }

        [Fact]
        public void Credit_PartOfASquadron_ReturnsOnlyTheLandedShare()
        {
            WorldModel w = World();
            SpatialNode home = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.Squadron = new List<string> { Gunship, Gunship, Gunship, Gunship };
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 40 } };

            FleetMunitions.Credit(ref raid, home, 2, w, null);

            Assert.Equal(20, Stock(w, "N1"));
        }

        [Fact]
        public void Credit_NeverArmed_ReturnsNothing()
        {
            WorldModel w = World();
            SpatialNode home = AddNode(w, "N1", "RVN", new Vec3(0, 0, 0));
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");

            Assert.Equal(0, FleetMunitions.Credit(ref raid, home, 1, w, null));
            Assert.Equal(0, Stock(w, "N1"));
        }

        [Fact]
        public void ApplyObserved_CannotRaiseTheLoad_AndAMissingKeyIsSpent()
        {
            // A prefab spawns with its own magazines; a read of the live grid that finds more than
            // the fleet drew must not hand the surplus back.
            PendingFleetAction raid = Fleet(ActionKind.Raid, Gunship, "N1");
            raid.MunitionsArmed = true;
            raid.Munitions = new Dictionary<string, long> { { Nato, 10 }, { "Missile200mm", 4 } };

            long spent = FleetMunitions.ApplyObserved(ref raid,
                new Dictionary<string, long> { { Nato, 50 } });

            Assert.Equal(10, raid.Munitions[Nato]);
            Assert.Equal(0, raid.Munitions["Missile200mm"]);
            Assert.Equal(4, spent);
        }

        // ---- the duel ---------------------------------------------------------------------------

        private static (List<FactionState> states, List<string> log) RaidOnWeakGuard(WorldModel w, Dictionary<string, long> raidLoad)
        {
            var stDefender = new FactionState { Tag = "MERC" };
            var stRaider = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 1, RangeM = 2000 } },
            });
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginNodeId = "RVN1", ReturnNodeId = "RVN1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                ShipPrefabId = Gunship, ConvoySize = 1, FleetId = "RAID01",
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 2000 } },
            };
            if (raidLoad != null)
            {
                raid.MunitionsArmed = true;
                raid.Munitions = raidLoad;
            }
            stRaider.PendingFleets.Add(raid);
            var states = new List<FactionState> { stDefender, stRaider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
            return (states, log);
        }

        [Fact]
        public void Duel_ArmedWithNothing_LandsNoDamage()
        {
            WorldModel w = World();
            var result = RaidOnWeakGuard(w, new Dictionary<string, long> { { Nato, 0 } });
            string log = string.Join("\n", result.log);

            // Every gun reaches 2000 m, so both sides hold the fight there every turn.
            Assert.Contains("T1(2000m set=both) atk=0 ", log);
            Assert.Contains("T3(2000m set=both) atk=0 ", log);
            Assert.DoesNotContain("GUARD_INTERCEPT", log);   // it cannot beat even a 1-Dps guard...
        }

        [Fact]
        public void Duel_NeverArmedRaid_FiresWithoutLimit_AsBefore()
        {
            WorldModel w = World();
            var result = RaidOnWeakGuard(w, null);
            Assert.Contains("T1(2000m set=both) atk=35", string.Join("\n", result.log));
        }

        [Fact]
        public void Duel_SpendsOnlyTheTurnsItFires_AndTheRestComesHome()
        {
            // 0.1 magazines/s x 30 s = 3 magazines a turn. The 1-Dps guard dies in T1, so a raid
            // that holds fire at a beaten enemy spends 3 of its 24 and brings 21 home.
            WorldModel w = World();
            var result = RaidOnWeakGuard(w, new Dictionary<string, long> { { Nato, 24 } });
            string log = string.Join("\n", result.log);

            Assert.Contains("T1(2000m set=both) atk=35", log);        // full Dps while it has rounds
            // Home already, or still aboard a raid holding its dwell: either way 21 are left.
            long aboard = result.states[1].PendingFleets
                .Where(f => f.Munitions != null).Sum(f => f.Munitions.Values.Sum());
            Assert.Equal(21, Stock(w, "RVN1") + aboard);
        }

        [Fact]
        public void Duel_RunsDryMidFight_AndFiresTheFractionItHas()
        {
            // 1.5 magazines against a 3-magazine turn: half a turn of fire, half the damage.
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", ShipPrefabId = Gunship, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long> { { Nato, 1 } },
            });
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);

            Assert.Equal(100.0 / 3.0, side.Fire(2000), 3);   // 1 of the 3 a turn needs
            Assert.Equal(0.0, side.Fire(2000), 6);           // then nothing
            Assert.Equal(1, side.Commit(fleets, w, null));
            Assert.Equal(0, fleets[0].PendingFleets[0].Munitions[Nato]);
        }

        [Fact]
        public void DuelSide_OutOfRangeSlotSpendsNothing()
        {
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", ShipPrefabId = Gunship, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long> { { Nato, 24 } },
            });
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);

            Assert.Equal(0.0, side.Fire(5000), 6);
            Assert.Equal(0, side.Commit(fleets, w, null));
        }

        [Fact]
        public void DuelSide_StationBandsFireWithoutLimit()
        {
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "MERC" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", ShipPrefabId = Gunship, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long> { { Nato, 0 } },
            });
            var station = new List<WeaponBand> { new WeaponBand { Dps = 40, RangeM = 2000 } };
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, station, 1.0, w, w.Tuning);

            Assert.Equal(40.0, side.Fire(2000), 6);
            Assert.Equal(40.0, side.Fire(2000), 6);
        }

        [Fact]
        public void DuelSide_NoArmedFleet_IsNull_SoTheDuelIsUnchanged()
        {
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(Fleet(ActionKind.Raid, Gunship, "N1"));
            Assert.Null(DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning));

            WorldModel off = World(MunitionDrawModes.Off);
            PendingFleetAction armed = Fleet(ActionKind.Raid, Gunship, "N1");
            armed.MunitionsArmed = true;
            armed.Munitions = new Dictionary<string, long> { { Nato, 0 } };
            fleets[0].PendingFleets[0] = armed;
            Assert.Null(DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, off, off.Tuning));
        }

        // ---- persistence ----------------------------------------------------------------------

        [Fact]
        public void Persistence_AnArmedFleetWithAnEmptyLoadStaysArmed()
        {
            var st = new FactionState { Tag = "RVN" };
            PendingFleetAction empty = Fleet(ActionKind.Raid, Gunship, "N1");
            empty.MunitionsArmed = true;
            empty.Munitions = new Dictionary<string, long> { { Nato, 0 } };
            PendingFleetAction loaded = Fleet(ActionKind.Guard, Gunship, "N1");
            loaded.MunitionsArmed = true;
            loaded.Munitions = new Dictionary<string, long> { { Nato, 12 } };
            loaded.RetreatReason = "out_of_ammo";
            st.PendingFleets.Add(empty);
            st.PendingFleets.Add(loaded);
            st.PendingFleets.Add(Fleet(ActionKind.Trade, Freighter, "N1"));

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialNode[0], 10);
            string json = JsonConvert.SerializeObject(doc);
            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            Assert.True(back.PendingFleets[0].MunitionsArmed);
            Assert.NotNull(back.PendingFleets[0].Munitions);
            Assert.False(back.PendingFleets[0].Munitions.ContainsKey(Nato));  // zero dropped; reads as empty
            Assert.Equal(12, back.PendingFleets[1].Munitions[Nato]);
            Assert.Equal("out_of_ammo", back.PendingFleets[1].RetreatReason);
            Assert.Null(back.PendingFleets[0].RetreatReason);
            Assert.False(back.PendingFleets[2].MunitionsArmed);
            Assert.Null(back.PendingFleets[2].Munitions);
        }

        [Fact]
        public void Persistence_AnUnarmedFleetWritesNoMunitionKeys()
        {
            var st = new FactionState { Tag = "RVN" };
            st.PendingFleets.Add(Fleet(ActionKind.Trade, Freighter, "N1"));
            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, new SpatialNode[0], 10));
            Assert.DoesNotContain("Munitions", json);
        }

        // ---- self-supplied magazines (FAC-478) ------------------------------------------------

        [Fact]
        public void SelfSupplied_AMagazineAWeaponMakesItselfIsNeverDrawn()
        {
            WorldModel w = World();
            Dictionary<string, long> load = FleetMunitions.FullLoad(w.PrefabCatalogue, new[] { BeamShip }, w.Tuning);

            Assert.Empty(load);
        }

        [Fact]
        public void SelfSupplied_AnEnergyHullFightsAtFullDpsWithAnEmptyHold()
        {
            // The defect: an armed fleet fights only through its slots, so a weapon whose magazine
            // the node never stocks scored zero.
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "RVN", ShipPrefabId = BeamShip, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long>(),
            });
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);

            Assert.Equal(5000, side.Fire(2000), 0);
            Assert.Equal(5000, side.Fire(2000), 0);
        }

        [Fact]
        public void SelfSupplied_AHauledSlotStillRunsDryBesideIt()
        {
            // The NATO gun has nothing loaded; the energy weapon beside it fires regardless, and
            // only the NATO magazine is drawn for a sortie.
            WorldModel w = World();
            var fleets = new List<FactionState> { new FactionState { Tag = "RVN" } };
            fleets[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "RVN", ShipPrefabId = BeamGunship, ConvoySize = 1,
                MunitionsArmed = true, Munitions = new Dictionary<string, long>(),
            });
            DuelMunitions side = DuelMunitions.ForSide(fleets, new[] { (0, 0) }, f => f.WeaponBands, null, 1.0, w, w.Tuning);
            Dictionary<string, long> load = FleetMunitions.FullLoad(w.PrefabCatalogue, new[] { BeamGunship }, w.Tuning);

            Assert.Equal(5000, side.Fire(2000), 0);
            Assert.Equal(new[] { Nato }, load.Keys);
        }
    }
}
