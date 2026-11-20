using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-347: a squadron draws from the whole estate, and a combat demand carries the
    /// faction's power floor wherever it was posted.
    ///
    /// <para>Measured live 2026-09-18 on v0.262.0, after FAC-343 shipped: every SPRT dispatch read
    /// <c>convoySize=1 gotDps=2500</c> against a <c>wantDps</c> of up to 1,354,423, while the faction
    /// held 5,046 Dps of combat power spread across an estate of 122 nodes. And a freed build slot at
    /// N130 re-queued a 229,659-unit <c>Naginata</c>, because the power floor is posted only at the
    /// emptiest yard while the dispatch-failure poster — which fires at every origin that could not
    /// fly — carried none at all.</para></summary>
    public class MultiOriginOffenceTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 18, 17, 0, 0, DateTimeKind.Utc);

        private static WorldModel Estate(out SpatialNode home, out SpatialNode depot, out SpatialNode target,
                                         ArmedCatalogue cat)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = cat;
            target = w.Graph.Add(new Vec3(4000, 0, 0), 0.6, "MARK");
            target.OwnerFactionTag = "B"; target.HoldStrength = 1.0; target.Radius = 40;
            target.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 12000, RangeM = 800 } };
            // HOME is nearest the target, so FindBestOriginNode picks it; DEPOT holds the fleet.
            home = w.Graph.Add(new Vec3(3000, 0, 0), 1.0, "HOME");
            home.OwnerFactionTag = "A"; home.HoldStrength = 1.0; home.StableStationId = 1; home.Radius = 40;
            depot = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "DEPOT");
            depot.OwnerFactionTag = "A"; depot.HoldStrength = 1.0; depot.StableStationId = 2; depot.Radius = 40;
            return w;
        }

        private static PendingFleetAction Dispatch(WorldModel w, SpatialNode target, FactionTuning t)
        {
            var st = new FactionState { Tag = "A", RngSeed = 1 };
            double defence = CombatStrength.OfNodeDefence(target, w.PrefabCatalogue, t);
            double want = CombatStrength.DpsForDamage(
                CombatStrength.DamageToDestroy(defence, t.RaidOverwhelmMargin), t);
            var intent = new FactionIntent
            {
                Kind = ActionKind.Raid, TargetNodeId = target.Id,
                TargetFactionTag = target.OwnerFactionTag, Cost = t.RaidCost,
            };
            PendingFleetAction fleet;
            Assert.True(OffensiveDispatch.Dispatch(st, intent, w, t, false, w.PrefabCatalogue,
                null, null, null, null, null, out fleet, null, want));
            return fleet;
        }

        /// <summary>The headline. The hulls are at the far node and the near one holds almost nothing;
        /// the squadron has to reach them. Before this it was as strong as the nearest node's pool
        /// however strong the faction was.</summary>
        [Fact]
        public void ASquadronDrawsFromTheEstate_NotJustTheNearestNode()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            SpatialNode home, depot, target;
            WorldModel w = Estate(out home, out depot, out target, cat);
            home.ShipStock["Fighter"] = 1;      // the near node is nearly empty
            depot.ShipStock["Fighter"] = 30;    // the fleet is at the far one

            PendingFleetAction fleet = Dispatch(w, target, t);

            Assert.Equal("HOME", fleet.OriginNodeId);          // it still departs from the near node
            Assert.True(fleet.ConvoySize > 1,
                "the far node's hulls must join the squadron; got " + fleet.ConvoySize);
            Assert.True(ShipPool.TotalDps(fleet.WeaponBands) > 1000,
                "and bring their Dps with them; got " + ShipPool.TotalDps(fleet.WeaponBands));
            Assert.True(depot.ShipStock["Fighter"] < 30, "drawn from the depot's own stock");
        }

        /// <summary>It stops at the target rather than emptying the estate, and it never exceeds the
        /// hull ceiling. A reinforcement pool with no bound is how one flashpoint drains a faction.</summary>
        [Fact]
        public void ItDrawsOnlyWhatTheTargetAsksFor()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            SpatialNode home, depot, target;
            WorldModel w = Estate(out home, out depot, out target, cat);
            home.ShipStock["Fighter"] = 1;
            depot.ShipStock["Fighter"] = 200;

            PendingFleetAction fleet = Dispatch(w, target, t);

            Assert.True(fleet.ConvoySize <= t.RaidSquadronMaxHulls,
                "bounded by the hull ceiling; got " + fleet.ConvoySize);
            Assert.True(depot.ShipStock["Fighter"] > 150,
                "the estate is not emptied into one raid; left " + depot.ShipStock["Fighter"]);
        }

        /// <summary>No target to size against — every caller that passes none — draws from one node
        /// exactly as it always did.</summary>
        [Fact]
        public void WithNoTarget_TheDrawIsSingleNodeAsBefore()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            SpatialNode home, depot, target;
            WorldModel w = Estate(out home, out depot, out target, cat);
            home.ShipStock["Fighter"] = 1;
            depot.ShipStock["Fighter"] = 30;

            var st = new FactionState { Tag = "A", RngSeed = 1 };
            var intent = new FactionIntent
            {
                Kind = ActionKind.Raid, TargetNodeId = target.Id,
                TargetFactionTag = "B", Cost = t.RaidCost,
            };
            PendingFleetAction fleet;
            Assert.True(OffensiveDispatch.Dispatch(st, intent, w, t, false, w.PrefabCatalogue,
                null, null, null, null, null, out fleet));   // no targetDps

            Assert.Equal(1, fleet.ConvoySize);
            Assert.Equal(30, depot.ShipStock["Fighter"]);
        }

        /// <summary>A combat demand posted by the dispatch-failure path carries the same power floor
        /// the proactive poster attaches. Without it that demand builds on the posture blend, which at
        /// a high posture takes the strongest hull in the pool — the flagship the floor exists to rule
        /// out — and this path posts at every origin that could not fly, so it outnumbers the
        /// floored one.</summary>
        [Fact]
        public void ADispatchFailureDemand_CarriesTheFactionsPowerFloor()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            WorldModel w = TestSupport.World();
            w.UtcNow = T0; w.PrefabCatalogue = cat; w.Tuning = t;
            SpatialNode yard = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "YARD");
            yard.OwnerFactionTag = "A"; yard.HoldStrength = 1.0; yard.StableStationId = 1;
            var st = new FactionState { Tag = "A", TerrorStandingDps = 500000 };

            OffensiveDispatch.PostShipDemand(w, "A", ActionKind.Raid, "YARD", null, st, t);

            List<DemandEntry> posted = w.Demand.SnapshotShipDemands("A");
            Assert.Single(posted);
            Assert.True(posted[0].MinDps > 0,
                "a combat demand must carry the floor wherever it was posted; got " + posted[0].MinDps);
        }

        /// <summary>...and a caller with no faction state in hand still posts the plain demand, so the
        /// non-combat and legacy paths are untouched.</summary>
        [Fact]
        public void WithoutFactionState_TheDemandIsPlain()
        {
            FactionTuning t = TestSupport.Tuning();
            WorldModel w = TestSupport.World();
            w.UtcNow = T0; w.PrefabCatalogue = new ArmedCatalogue().Add("Fighter", 1000, "Small"); w.Tuning = t;
            SpatialNode yard = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "YARD");
            yard.OwnerFactionTag = "A";

            OffensiveDispatch.PostShipDemand(w, "A", ActionKind.Raid, "YARD", null);

            List<DemandEntry> posted = w.Demand.SnapshotShipDemands("A");
            Assert.Single(posted);
            Assert.Equal(0, posted[0].MinDps);
        }
    }
}
