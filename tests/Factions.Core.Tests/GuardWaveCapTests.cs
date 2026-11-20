using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-452: a defensive wave carries as many hulls as one raid squadron.
    ///
    /// <para>Live 2026-10-07, 16:54:32 box-local: <c>EXP GUARD_OUTGUNNED N257 -> N257 vs=IMPR
    /// attack=1686483 defence=1409479 duelDmg=1347377 needDmg=1370268 station=808 hulls=15 waves=3</c>.
    /// N257 held 144 warships; the marshal could sail three waves of five against a 23-hull raid,
    /// missed the bar by 1.7%, and the raid met the station alone. The wave was still bounded by the
    /// old fighter roster (<c>RaidSquadronMaxFighters + 1</c>) after the raid side moved to
    /// <c>RaidSquadronMaxHulls</c>.</para>
    ///
    /// <para>The world here is N257's shape at small numbers: the threatened node is its own
    /// depot, 60 parked 200-DPS gunships, no station, against a 4000-DPS raider. Fifteen of them
    /// (3000 DPS) neither hold at <c>GuardCommitRatio</c> 0.9 nor win the duel; the roster-sized
    /// marshal could reach no more.</para></summary>
    public class GuardWaveCapTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return prefabId == "Gunship"
                    ? new[] { new WeaponBand { Dps = 200f, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>The shipped wave knobs (3 waves, 4 fighters, 24-hull squadron) with posture and
        /// budget taken out of the way.</summary>
        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GuardCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            t.MusterConvoyScaleMax = 1.0;
            t.GuardBurstSize = 1;
            t.StationDefensePower = 0;
            t.GuardCommitRatio = 0.9;
            t.GuardOverwhelmMargin = 1.25;
            t.GuardMaxConvoysPerThreat = 3;
            t.RaidSquadronMaxFighters = 4;
            t.RaidSquadronMaxHulls = 24;
            return t;
        }

        private static WorldModel MakeWorld(int parked, double raidDps, out FactionState defender, out FactionState raider)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            w.PrefabCatalogue = new Cat();

            SpatialNode depot = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N257");
            depot.OwnerFactionTag = "DEF";
            depot.HoldStrength = 0.9;
            depot.Radius = 40;
            depot.ShipStock["Gunship"] = parked;

            defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetNodeId = "N257",
                OriginPosKm = new Vec3(5000, 0, 0),
                TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(30),
                FleetId = "RAIDN257",
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = (float)raidDps, RangeM = 800 } },
                LastResolvedSegment = -1,
            });
            return w;
        }

        private static List<string> Run(WorldModel w, FactionTuning t, params FactionState[] states)
        {
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            return log;
        }

        private static int HullsSent(FactionState defender)
        {
            return defender.PendingFleets.Where(f => f.Kind == ActionKind.Guard).Sum(f => f.ConvoySize);
        }

        [Fact]
        public void AWave_CarriesOneRaidSquadronsWorthOfHulls()
        {
            FactionTuning t = Tuning();
            Assert.Equal(24, CombatStrength.GuardWaveMaxHulls(t, 0.0));
            Assert.Equal(72, CombatStrength.FieldableHullCap(t));

            // With the raid top-up off, the roster bounds both sides, as it did before the top-up.
            t.RaidSquadronMaxHulls = 0;
            Assert.Equal(5, CombatStrength.GuardWaveMaxHulls(t, 0.0));
        }

        /// <summary>The live failure, reproduced: the depot can beat the raider with what is parked
        /// on it, and does.</summary>
        [Fact]
        public void ADepotThatCanWin_SailsEnoughToWin()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(60, 4000, out d, out r);

            List<string> log = Run(w, Tuning(), d, r);

            Assert.DoesNotContain(log, l => l.Contains("GUARD_OUTGUNNED"));
            int sent = HullsSent(d);
            Assert.True(sent > 15, "sent " + sent + " hulls; the roster-sized marshal reached 15");
            // Topped up to the bar, not emptied into the fight.
            Assert.True(sent < 60, "sent all " + sent + " parked hulls");
            Assert.Equal(60 - sent, w.Graph.Nodes["N257"].ShipStock["Gunship"]);
        }

        /// <summary>Control: the same world under the roster-sized wave is the logged refusal. If this
        /// stops refusing, the test above no longer proves the cap is what changed.</summary>
        [Fact]
        public void UnderTheRosterSizedWave_TheSameDepotRefusesWithFifteen()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(60, 4000, out d, out r);
            FactionTuning t = Tuning();
            t.RaidSquadronMaxHulls = 0;

            List<string> log = Run(w, t, d, r);

            string refusal = Assert.Single(log, l => l.Contains("GUARD_OUTGUNNED"));
            Assert.Contains("hulls=15 waves=3", refusal);
            Assert.Equal(0, HullsSent(d));
        }

        /// <summary>A defence that still loses with every hull it can sail keeps refusing: the cap
        /// is the bug, falling back is not.</summary>
        [Fact]
        public void ADepotThatCannotWinEvenAtTheNewCap_StillRefuses()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(100, 20000, out d, out r);

            List<string> log = Run(w, Tuning(), d, r);

            string refusal = Assert.Single(log, l => l.Contains("GUARD_OUTGUNNED"));
            Assert.Contains("hulls=72 waves=3", refusal);
            Assert.Equal(0, HullsSent(d));
            Assert.Equal(100, w.Graph.Nodes["N257"].ShipStock["Gunship"]);
        }
    }
}
