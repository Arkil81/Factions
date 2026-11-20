using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The detected tier for fleets — the half of the two-tier model that is derived rather than
    /// persisted, and the one the map's fleet fog reads (FAC-120).
    /// </summary>
    public class FleetSensingTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _mass = new Dictionary<string, double>();
            private readonly HashSet<string> _armed = new HashSet<string>();

            public Catalogue Add(string prefabId, double dryMassKg, bool armed)
            {
                _mass[prefabId] = dryMassKg;
                if (armed) _armed.Add(prefabId);
                return this;
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return _mass.TryGetValue(prefabId, out v) ? v : 0;
            }

            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return _armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = 100, RangeM = 800 } }
                    : new WeaponBand[0];
            }

            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litres = new Dictionary<string, double>();
            public Volumes Add(string invKey, double litersPerUnit) { _litres[invKey] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _litres.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        private static PendingFleetAction Fleet(
            string prefabId, int convoySize = 1, long capacityLiters = 0,
            Dictionary<string, long> cargo = null)
        {
            return new PendingFleetAction
            {
                ShipPrefabId = prefabId,
                ConvoySize = convoySize,
                CargoCapacityLiters = capacityLiters,
                Cargo = cargo,
            };
        }

        // ---- signature ---------------------------------------------------------------------------

        [Fact]
        public void AnUnscannedPrefabIsStillDetectable_AtTheReferenceSignature()
        {
            // "Unknown, do not gate" — the same convention DryMassKg documents for route feasibility.
            // The failure that would matter here is a fleet turning invisible because nobody scanned
            // its prefab, so this asserts the fallback is 1.0 and not 0.
            Assert.Equal(1.0, FleetSensing.SignatureOf(Fleet("never-scanned"), new Catalogue(), null, null, default(DateTime)));
        }

        [Fact]
        public void ALadenArmedConvoyIsLouderThanAnEmptyUnarmedScout()
        {
            var prefabs = new Catalogue()
                .Add("scout", 40000, false)
                .Add("hauler", 900000, true);
            var volumes = new Volumes().Add("Ore:Iron", 0.37);

            double scout = FleetSensing.SignatureOf(Fleet("scout"), prefabs, volumes, null, default(DateTime));
            double convoy = FleetSensing.SignatureOf(
                Fleet("hauler", convoySize: 4, capacityLiters: 100000,
                      cargo: new Dictionary<string, long> { { "Ore:Iron", 270270 } }),
                prefabs, volumes, null, default(DateTime));

            Assert.True(scout < 1.0, "a small empty scout should be quieter than the reference");
            Assert.True(convoy > scout * 2, "loud=" + convoy + " quiet=" + scout);
            // No ship ceiling any more; what matters is that the loud one is loud.
            Assert.True(convoy > 1.0, "convoy=" + convoy);
        }

        [Fact]
        public void CargoFillIsMeasuredInLitres_NotItemCount()
        {
            // An item count would make a hold of ore and a hold of components read the same. This is
            // the same volume-aware accounting the cargo loader itself uses.
            var volumes = new Volumes().Add("Ore:Iron", 0.37).Add("Comp:SteelPlate", 3.0);
            PendingFleetAction ore = Fleet("h", capacityLiters: 30000,
                cargo: new Dictionary<string, long> { { "Ore:Iron", 10000 } });
            PendingFleetAction plates = Fleet("h", capacityLiters: 30000,
                cargo: new Dictionary<string, long> { { "Comp:SteelPlate", 10000 } });

            Assert.Equal(0.1233, FleetSensing.CargoFill(ore, volumes), 4);
            Assert.Equal(1.0, FleetSensing.CargoFill(plates, volumes));   // clamped: 30000 L over capacity
        }

        [Fact]
        public void CargoFillIsZeroWithoutAVolumeCatalogue_RatherThanThrowing()
        {
            PendingFleetAction laden = Fleet("h", capacityLiters: 30000,
                cargo: new Dictionary<string, long> { { "Ore:Iron", 10000 } });
            Assert.Equal(0.0, FleetSensing.CargoFill(laden, null));
            Assert.Equal(0.0, FleetSensing.CargoFill(Fleet("h"), new Volumes()));
        }

        // ---- sensors -----------------------------------------------------------------------------

        private static SpatialNode Node(string id, double x, string owner)
        {
            return new SpatialNode { Id = id, Position = new Vec3(x, 0, 0), OwnerFactionTag = owner };
        }

        private static SpatialGraph GraphOf(params SpatialNode[] nodes)
        {
            var g = new SpatialGraph();
            foreach (SpatialNode n in nodes) g.Nodes[n.Id] = n;
            return g;
        }

        [Fact]
        public void OnlyTheObserversOwnNodesAreItsSensors()
        {
            SpatialGraph g = GraphOf(Node("A", 0, "MERC"), Node("B", 50, "RVN"), Node("C", 90, "MERC"));
            var sensors = new List<SensorSource>();
            FleetSensing.CollectNodeSensors(g, "MERC", TestSupport.Tuning(), sensors);

            Assert.Equal(2, sensors.Count);
            Assert.DoesNotContain(sensors, s => s.PositionKm.X == 50);
        }

        [Fact]
        public void ASensorArrayWidensTheEnvelopeItContributes()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode bare = Node("A", 0, "MERC");
            SpatialNode equipped = Node("B", 500, "MERC");
            equipped.Capabilities[NodeCapabilityKind.SensorArray] = 2;

            var sensors = new List<SensorSource>();
            FleetSensing.CollectNodeSensors(GraphOf(bare, equipped), "MERC", t, sensors);

            Assert.Equal(t.SensorNodePassiveRangeKm, sensors[0].StrengthKm);
            Assert.Equal(t.SensorNodePassiveRangeKm + 2 * t.SensorArrayRangeKmPerLevel, sensors[1].StrengthKm);
        }

        [Fact]
        public void CollectAppendsRatherThanClears_SoAGridStacksOnTheEstate()
        {
            var sensors = new List<SensorSource> { new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 100 } };
            FleetSensing.CollectNodeSensors(GraphOf(Node("A", 0, "MERC")), "MERC", TestSupport.Tuning(), sensors);
            Assert.Equal(2, sensors.Count);
        }

        // ---- detection ---------------------------------------------------------------------------

        [Fact]
        public void RangeScalesWithSignature_SoAQuietScoutSlipsPastWhatCatchesAConvoy()
        {
            // The whole point of signature-versus-strength: same sensor, same distance, different
            // outcome by what is flying. A flat radius could not produce this.
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 50 },
            };
            var at60Km = new Vec3(60, 0, 0);

            Assert.False(FleetSensing.IsDetectedBy(sensors, at60Km, 0.5));   // 25 km reach
            Assert.False(FleetSensing.IsDetectedBy(sensors, at60Km, 1.0));   // 50 km reach
            Assert.True(FleetSensing.IsDetectedBy(sensors, at60Km, 2.0));    // 100 km reach
        }

        [Fact]
        public void AnObserverWithNoSensorsDetectsNothing()
        {
            // The floor the fog needs to be able to reach: a player on foot with no faction. If this
            // ever returns true, the seam is open and every other test here is decoration.
            Assert.False(FleetSensing.IsDetectedBy(new List<SensorSource>(), new Vec3(0, 0, 0), Signature.Max));
            Assert.False(FleetSensing.IsDetectedBy(null, new Vec3(0, 0, 0), Signature.Max));
        }

        [Fact]
        public void AnyOneSensorIsEnough()
        {
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 50 },
                new SensorSource { PositionKm = new Vec3(1000, 0, 0), StrengthKm = 50 },
            };
            Assert.True(FleetSensing.IsDetectedBy(sensors, new Vec3(1020, 0, 0), 1.0));
        }

        // ---- FAC-136 slice 5: what an abstract convoy is DOING ------------------------------------

        private static FactionTuning FleetTuning()
        {
            var t = TestSupport.Tuning();
            t.SignatureWeightFleetDeparture = 1.20;
            t.SignatureDecayFleetDepartureSeconds = 120.0;
            t.SignatureWeightFleetMining = 0.80;
            return t;
        }

        [Fact]
        public void AFreshDepartureIsConspicuousAndFades()
        {
            var t = FleetTuning();
            DateTime dep = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            PendingFleetAction f = Fleet("scout");
            f.DepartedUtc = dep;

            Assert.Equal(2.20, FleetSensing.ExcitationOf(f, t, dep), 6);                  // just left
            Assert.Equal(1.60, FleetSensing.ExcitationOf(f, t, dep.AddSeconds(60)), 6);   // halfway
            Assert.Equal(1.00, FleetSensing.ExcitationOf(f, t, dep.AddSeconds(120)), 6);  // settled
            Assert.Equal(1.00, FleetSensing.ExcitationOf(f, t, dep.AddHours(3)), 6);      // and stays
        }

        [Fact]
        public void DrillingIsSustainedForAsLongAsTheDwellLasts()
        {
            var t = FleetTuning();
            DateTime dep = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            PendingFleetAction f = Fleet("scout");
            f.DepartedUtc = dep;
            f.CurrentStopKind = FleetStopKind.Mine;

            // Long after the departure burst has gone, the drills are still the loud thing.
            Assert.Equal(1.80, FleetSensing.ExcitationOf(f, t, dep.AddHours(2)), 6);
        }

        [Fact]
        public void ACruisingConvoyIsAtRest_SoTheTermDiscriminates()
        {
            // The exemption rule as an assertion: a term that is true of every convoy in transit
            // would raise the floor for all of them and separate none. Only a fresh departure or a
            // drilling dwell moves this.
            var t = FleetTuning();
            DateTime dep = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            PendingFleetAction f = Fleet("scout");
            f.DepartedUtc = dep;
            f.CurrentStopKind = FleetStopKind.Service;

            Assert.Equal(1.0, FleetSensing.ExcitationOf(f, t, dep.AddHours(1)), 6);
        }

        [Fact]
        public void NoTuning_MeansBaseSignatureOnly()
        {
            DateTime dep = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            PendingFleetAction f = Fleet("scout");
            f.DepartedUtc = dep;
            f.CurrentStopKind = FleetStopKind.Mine;

            Assert.Equal(1.0, FleetSensing.ExcitationOf(f, null, dep), 9);
        }

        [Fact]
        public void ActivityActuallyReachesTheConvoysSignature()
        {
            // End to end: the excitation is not merely computed, it changes what a sensor sees.
            var t = FleetTuning();
            var prefabs = new Catalogue().Add("hauler", 900000, true);
            DateTime dep = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            PendingFleetAction f = Fleet("hauler");
            f.DepartedUtc = dep;

            double justLeft = FleetSensing.SignatureOf(f, prefabs, null, t, dep);
            double settled  = FleetSensing.SignatureOf(f, prefabs, null, t, dep.AddHours(1));

            Assert.True(justLeft > settled * 2, "justLeft=" + justLeft + " settled=" + settled);
        }
    }
}
