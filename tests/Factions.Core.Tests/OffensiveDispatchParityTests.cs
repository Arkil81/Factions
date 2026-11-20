using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="OffensiveDispatch"/> is the one dispatch body the Torch host and the offline sim
    /// both run. They wire it differently — the host hands it the prefab and item catalogues it
    /// holds, a blocked-report throttle, a log sink and the live event feed; the sim has only
    /// <see cref="WorldModel.PrefabCatalogue"/> and passes the rest as null — so the fleet the two
    /// produce from one intent has to come out the same, or "host and sim run the same mechanics"
    /// is not true.
    /// </summary>
    public class OffensiveDispatchParityTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return prefabId == "Hauler" ? 30000 : 500; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 1000; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                if (prefabId == "Cruiser") return new[] { new WeaponBand { Dps = 900, RangeM = 1200 } };
                if (prefabId == "Fighter") return new[] { new WeaponBand { Dps = 300, RangeM = 700 } };
                return new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return prefabId == "Fighter" ? "Small" : "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey) { return 1.0; }
        }

        private sealed class Masses : IItemMassCatalogue
        {
            public double MassKgPerUnit(string invKey) { return 1.0; }
        }

        /// <summary>One raid-capable faction at O, a rival at T, and a mixed pool at O so squadron
        /// assembly has a large combat hull, a fighter and a cargo hull to choose between. Both
        /// arms of the parity test build this from scratch, since a dispatch consumes the stock.</summary>
        private static WorldModel BuildWorld(out FactionState actor, out FactionIntent intent, out Cat cat)
        {
            WorldModel world = TestSupport.World(7);
            cat = new Cat();
            world.PrefabCatalogue = cat;

            SpatialNode origin = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "O");
            origin.OwnerFactionTag = "AAA";
            origin.HoldStrength = 0.9;
            origin.Radius = 40;
            origin.ShipStock["Cruiser"] = 1;
            origin.ShipStock["Fighter"] = 3;
            origin.ShipStock["Hauler"] = 1;

            SpatialNode target = world.Graph.Add(new Vec3(4000, 0, 0), 0.6, "T");
            target.OwnerFactionTag = "BBB";
            target.HoldStrength = 0.5;
            target.Radius = 40;

            actor = new FactionState
            {
                Tag = "AAA",
                RngSeed = 7,
                MilitaryPosture = 0.75,     // exercises the posture convoy scale
            };
            intent = new FactionIntent
            {
                Kind = ActionKind.Raid,
                TargetNodeId = "T",
                TargetFactionTag = "BBB",
            };
            return world;
        }

        [Fact]
        public void HostAndSimWiringDispatchTheSameFleet()
        {
            FactionState hostActor, simActor;
            FactionIntent hostIntent, simIntent;
            Cat hostCat, simCat;
            WorldModel hostWorld = BuildWorld(out hostActor, out hostIntent, out hostCat);
            WorldModel simWorld = BuildWorld(out simActor, out simIntent, out simCat);

            var hostLog = new List<string>();
            PendingFleetAction hostFleet;
            bool hostDispatched = OffensiveDispatch.Dispatch(
                hostActor, hostIntent, hostWorld, hostWorld.Tuning, immediate: false,
                prefabCatalogue: hostCat, itemVolumes: new Volumes(), itemMasses: new Masses(),
                blockedReports: new OffensiveDispatch.BlockedFleetReports(),
                log: hostLog.Add, eventWriter: null, fleet: out hostFleet);

            PendingFleetAction simFleet;
            bool simDispatched = OffensiveDispatch.Dispatch(
                simActor, simIntent, simWorld, simWorld.Tuning, immediate: false,
                prefabCatalogue: simWorld.PrefabCatalogue, itemVolumes: null, itemMasses: null,
                blockedReports: null, log: null, eventWriter: null, fleet: out simFleet);

            Assert.True(hostDispatched);
            Assert.True(simDispatched);

            Assert.Equal(hostFleet.Kind, simFleet.Kind);
            Assert.Equal(hostFleet.ActorTag, simFleet.ActorTag);
            Assert.Equal(hostFleet.OriginNodeId, simFleet.OriginNodeId);
            Assert.Equal(hostFleet.TargetNodeId, simFleet.TargetNodeId);
            Assert.Equal(hostFleet.TargetTag, simFleet.TargetTag);
            Assert.Equal(hostFleet.FleetId, simFleet.FleetId);
            Assert.Equal(hostFleet.ConvoySize, simFleet.ConvoySize);
            Assert.Equal(hostFleet.ShipPrefabId, simFleet.ShipPrefabId);
            Assert.Equal(hostFleet.CargoCapacityLiters, simFleet.CargoCapacityLiters);
            Assert.Equal(hostFleet.Squadron, simFleet.Squadron);
            Assert.Equal(hostFleet.WeaponBands.Count, simFleet.WeaponBands.Count);
            Assert.Equal(hostFleet.EstimatedArrivalUtc, simFleet.EstimatedArrivalUtc);

            // The comparison would be vacuous against an empty squadron or a zero-ship convoy:
            // both sides have to have actually assembled something out of the pool.
            Assert.True(hostFleet.ConvoySize > 0);
            Assert.NotEmpty(hostFleet.Squadron);
            Assert.NotEmpty(hostFleet.WeaponBands);

            // Both sides pulled the same ships out of their own copy of the pool.
            SpatialNode hostOrigin = hostWorld.Graph.Nodes["O"];
            SpatialNode simOrigin = simWorld.Graph.Nodes["O"];
            foreach (KeyValuePair<string, int> kv in hostOrigin.ShipStock)
            {
                int simRemaining;
                simOrigin.ShipStock.TryGetValue(kv.Key, out simRemaining);
                Assert.Equal(kv.Value, simRemaining);
            }

            // Each side filed its own fleet, and the host's wiring is what writes the log.
            Assert.Single(hostActor.PendingFleets);
            Assert.Single(simActor.PendingFleets);
            Assert.Contains(hostLog, line => line.StartsWith("FLEET_DISPATCH AAA kind=Raid ", StringComparison.Ordinal));
        }

        /// <summary>The dispatch lines carry no caller-specific prefix: the host prepends its
        /// simulation tick to whatever the sink receives, so the sink must be handed the bare line.</summary>
        [Fact]
        public void DispatchLinesArriveUnprefixed()
        {
            FactionState actor;
            FactionIntent intent;
            Cat cat;
            WorldModel world = BuildWorld(out actor, out intent, out cat);

            var log = new List<string>();
            PendingFleetAction fleet;
            Assert.True(OffensiveDispatch.Dispatch(
                actor, intent, world, world.Tuning, immediate: false,
                prefabCatalogue: cat, itemVolumes: null, itemMasses: null,
                blockedReports: new OffensiveDispatch.BlockedFleetReports(),
                log: log.Add, eventWriter: null, fleet: out fleet));

            string dispatch = log.Find(line => line.StartsWith("FLEET_DISPATCH", StringComparison.Ordinal));
            Assert.NotNull(dispatch);
            Assert.Equal("FLEET_DISPATCH AAA kind=Raid origin=O targetNode=T targetTag=BBB"
                         + " fleetId=" + fleet.FleetId
                         + " convoySize=" + fleet.ConvoySize
                         + " etaUtc=" + fleet.EstimatedArrivalUtc.ToUniversalTime().ToString("o")
                         + OffensiveDispatch.SquadronLogSuffix(fleet.Squadron),
                         dispatch);
        }

        /// <summary>An origin with no combat stock blocks the dispatch instead of filing a fleet,
        /// and says so once — the caller reads the false return as "abort, charge nothing".</summary>
        [Fact]
        public void EmptyPoolBlocksAndReportsOnce()
        {
            FactionState actor;
            FactionIntent intent;
            Cat cat;
            WorldModel world = BuildWorld(out actor, out intent, out cat);
            world.Graph.Nodes["O"].ShipStock.Clear();

            var log = new List<string>();
            var reports = new OffensiveDispatch.BlockedFleetReports();
            PendingFleetAction fleet;

            Assert.False(OffensiveDispatch.Dispatch(
                actor, intent, world, world.Tuning, immediate: false,
                prefabCatalogue: cat, itemVolumes: null, itemMasses: null,
                blockedReports: reports, log: log.Add, eventWriter: null, fleet: out fleet));
            Assert.False(OffensiveDispatch.Dispatch(
                actor, intent, world, world.Tuning, immediate: false,
                prefabCatalogue: cat, itemVolumes: null, itemMasses: null,
                blockedReports: reports, log: log.Add, eventWriter: null, fleet: out fleet));

            Assert.Empty(actor.PendingFleets);
            Assert.Equal(
                new List<string>
                {
                    "FLEET_BLOCKED AAA kind=Raid origin=O targetNode=T targetTag=BBB"
                    + " reason=missing_eligible_ship_stock"
                },
                log);
        }
    }
}
