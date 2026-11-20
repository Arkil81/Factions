using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-137 — a faction detecting players. The geometry is <see cref="Sensing.Detects"/> and is
    /// already covered by <c>KnowledgeTests</c>; what these pin is the new target type, the sensor
    /// collection, and the conventions that would otherwise fail silently.
    /// </summary>
    public sealed class PlayerSensingTests
    {
        private static SensorSource Sensor(double x, double strengthKm)
        {
            return new SensorSource { PositionKm = new Vec3(x, 0, 0), StrengthKm = strengthKm };
        }

        private static PlayerContact Contact(long id, double x, double signature)
        {
            return new PlayerContact { IdentityId = id, PositionKm = new Vec3(x, 0, 0), Signature = signature };
        }

        [Fact]
        public void Detect_SeesAContactInsideRangeAndMissesOneOutside()
        {
            var sensors = new List<SensorSource> { Sensor(0, 100) };
            var contacts = new List<PlayerContact> { Contact(1, 50, 1.0), Contact(2, 150, 1.0) };
            var into = new List<PlayerContact>();

            Assert.Equal(1, PlayerSensing.Detect(sensors, contacts, into));
            Assert.Equal(1L, into[0].IdentityId);
        }

        [Fact]
        public void Detect_RangeScalesWithSignature_SoTheSameDistanceCanGoEitherWay()
        {
            // One sensor, one distance, two players. The only difference is how loud they are, which
            // is the entire point of routing this through the shared model.
            var sensors = new List<SensorSource> { Sensor(0, 100) };
            var contacts = new List<PlayerContact>
            {
                Contact(1, 150, 2.0),   // 200 km reach -> seen
                Contact(2, 150, 0.5)    //  50 km reach -> missed
            };
            var into = new List<PlayerContact>();

            Assert.Equal(1, PlayerSensing.Detect(sensors, contacts, into));
            Assert.Equal(1L, into[0].IdentityId);
        }

        [Fact]
        public void Detect_UnsuppliedSignature_ReadsAsReferenceNotAsSilence()
        {
            // Signature left at its default. Signature.Clamp(0) is 0.25, not 0, so a caller that
            // forgot to measure a grid would otherwise quarter every player's detection range --
            // plausible, silent and wrong. Zero must mean "unknown, do not gate".
            var sensors = new List<SensorSource> { Sensor(0, 100) };
            var contacts = new List<PlayerContact>
            {
                new PlayerContact { IdentityId = 1, PositionKm = new Vec3(99, 0, 0) },
                new PlayerContact { IdentityId = 2, PositionKm = new Vec3(101, 0, 0) }
            };
            var into = new List<PlayerContact>();

            Assert.Equal(1, PlayerSensing.Detect(sensors, contacts, into));
            Assert.Equal(1L, into[0].IdentityId);
        }

        [Fact]
        public void Detect_NoSensors_SeesNobody()
        {
            var into = new List<PlayerContact>();
            Assert.Equal(0, PlayerSensing.Detect(new List<SensorSource>(), new List<PlayerContact> { Contact(1, 0, 1.0) }, into));
            Assert.Empty(into);
        }

        [Fact]
        public void Detect_ClearsPriorResult_SoALostSensorLosesTheContact()
        {
            // The failure this prevents: a faction that loses every sensor keeping its last answer
            // forever, which would look exactly like working detection.
            var into = new List<PlayerContact>();
            PlayerSensing.Detect(new List<SensorSource> { Sensor(0, 100) },
                                 new List<PlayerContact> { Contact(1, 10, 1.0) }, into);
            Assert.Single(into);

            PlayerSensing.Detect(new List<SensorSource>(), new List<PlayerContact> { Contact(1, 10, 1.0) }, into);
            Assert.Empty(into);
        }

        [Fact]
        public void CollectFactionSensors_TakesNodesAndInFlightFleets_AtTheirOwnStrengths()
        {
            var tuning = TestSupport.Tuning();
            tuning.SensorNodePassiveRangeKm = 25;
            tuning.SensorFleetRangeKm = 15;

            var graph = new SpatialGraph();
            graph.Nodes["N1"] = new SpatialNode { Id = "N1", OwnerFactionTag = "ACME", Position = new Vec3(0, 0, 0) };
            graph.Nodes["N2"] = new SpatialNode { Id = "N2", OwnerFactionTag = "OTHER", Position = new Vec3(10, 0, 0) };

            DateTime now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "ACME" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "ACME",
                OriginPosKm = new Vec3(500, 0, 0), TargetPosKm = new Vec3(500, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now.AddHours(1)
            });

            var sensors = new List<SensorSource>();
            PlayerSensing.CollectFactionSensors(graph, state, tuning, now, sensors);

            // The rival's node is not a sensor for us; our node and our convoy both are.
            Assert.Equal(2, sensors.Count);
            Assert.Contains(sensors, s => s.StrengthKm == 25);
            Assert.Contains(sensors, s => s.StrengthKm == 15);
        }

        [Fact]
        public void CollectFactionSensors_SkipsResolvedAndInterceptedFleets()
        {
            var tuning = TestSupport.Tuning();
            tuning.SensorFleetRangeKm = 15;
            DateTime now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var state = new FactionState { Tag = "ACME" };
            state.PendingFleets.Add(new PendingFleetAction { ActorTag = "ACME", DepartedUtc = now, EstimatedArrivalUtc = now.AddHours(1), Resolved = true });
            state.PendingFleets.Add(new PendingFleetAction { ActorTag = "ACME", DepartedUtc = now, EstimatedArrivalUtc = now.AddHours(1), Intercepted = true });

            var sensors = new List<SensorSource>();
            PlayerSensing.CollectFactionSensors(new SpatialGraph(), state, tuning, now, sensors);

            Assert.Empty(sensors);
        }

        [Fact]
        public void CollectFactionSensors_SensorArrayWidensTheEstate()
        {
            var tuning = TestSupport.Tuning();
            tuning.SensorNodePassiveRangeKm = 25;
            tuning.SensorArrayRangeKmPerLevel = 75;
            tuning.SensorFleetRangeKm = 0;      // isolate the node half

            var graph = new SpatialGraph();
            var node = new SpatialNode { Id = "N1", OwnerFactionTag = "ACME", Position = new Vec3(0, 0, 0) };
            node.Capabilities[NodeCapabilityKind.SensorArray] = 1;
            graph.Nodes["N1"] = node;

            var sensors = new List<SensorSource>();
            PlayerSensing.CollectFactionSensors(graph, new FactionState { Tag = "ACME" }, tuning,
                                                new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc), sensors);

            Assert.Single(sensors);
            Assert.Equal(100, sensors[0].StrengthKm);   // 25 passive + 75 per level
        }

        [Fact]
        public void Sees_AnswersByIdentityAndIsFalseForAnEmptySet()
        {
            var sensed = new List<PlayerContact> { Contact(7, 0, 1.0) };
            Assert.True(PlayerSensing.Sees(sensed, 7));
            Assert.False(PlayerSensing.Sees(sensed, 8));
            Assert.False(PlayerSensing.Sees(new List<PlayerContact>(), 7));
            Assert.False(PlayerSensing.Sees(null, 7));
        }

        [Fact]
        public void SensedPlayers_IsNotPersisted_SoAReloadedBrainStartsBlind()
        {
            // The rule this guards is the one KnowledgeSweep states for fleets: a remembered position
            // of a moving thing is worse than none. A round trip through the persistence layer must
            // therefore drop the set entirely rather than carry a stale one.
            var state = new FactionState { Tag = "ACME" };
            state.SensedPlayers.Add(Contact(1, 0, 1.0));

            BrainDoc doc = FactionStore.ToBrainDoc(state, new List<SpatialNode>(), 50);
            FactionState reloaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph(), TimeSpan.FromHours(1));

            Assert.Empty(reloaded.SensedPlayers);
        }
    
        // ---- FAC-463: a contact is a GRID, and a transition is keyed on one -------------------

        [Fact]
        public void SeesGrid_DistinguishesTwoHullsOfOneOwner()
        {
            var sensed = new List<PlayerContact>
            {
                new PlayerContact { IdentityId = 7, GridEntityId = 100, Signature = 1.0 },
                new PlayerContact { IdentityId = 7, GridEntityId = 200, Signature = 1.0 }
            };

            // Identity-keyed, both hulls read as one thing -- which is exactly the diff that would
            // swallow one of them arriving or leaving.
            Assert.True(PlayerSensing.Sees(sensed, 7));

            Assert.True(PlayerSensing.SeesGrid(sensed, 100));
            Assert.True(PlayerSensing.SeesGrid(sensed, 200));
            Assert.False(PlayerSensing.SeesGrid(sensed, 300));
            Assert.False(PlayerSensing.SeesGrid(sensed, 0));
            Assert.False(PlayerSensing.SeesGrid(null, 100));
        }

        [Fact]
        public void OneOwnersSecondHullIsItsOwnContact()
        {
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 25.0 }
            };
            var contacts = new List<PlayerContact>
            {
                new PlayerContact { IdentityId = 7, GridEntityId = 100,
                                    PositionKm = new Vec3(5, 0, 0), Signature = 1.0 },
                new PlayerContact { IdentityId = 7, GridEntityId = 200,
                                    PositionKm = new Vec3(10, 0, 0), Signature = 1.0 }
            };
            var into = new List<PlayerContact>();

            Assert.Equal(2, PlayerSensing.Detect(sensors, contacts, into));
            Assert.Equal(100, into[0].GridEntityId);
            Assert.Equal(200, into[1].GridEntityId);
        }
}
}
