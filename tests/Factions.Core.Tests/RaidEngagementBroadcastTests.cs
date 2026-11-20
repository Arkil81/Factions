using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// How a raid fighting at its target is seen. The rule is the ordinary sensor rule — an
    /// engagement is not exempt from fog, it is <b>loud</b> — which is what keeps a better scanner
    /// worth more against a raid rather than bypassed by it.
    ///
    /// <para>The operator's distinction, and the reason this is a floor and not only a multiplier:
    /// <b>a stealthy approach is good, a stealthy raid is bad.</b> A small unarmed hull should be
    /// hard to see coming and impossible to miss once it opens fire.</para>
    /// </summary>
    public class RaidEngagementBroadcastTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        private static ScannerFleetCandidate Fleet(
            double xKm, string engagingAt = null, string key = "F1", double signature = 1.0)
            => new ScannerFleetCandidate
            {
                Key = key,
                PositionKm = new Vec3(xKm, 0, 0),
                OwnerTag = "RVN",
                Signature = signature,
                EngagementTargetNodeId = engagingAt
            };

        private static IList<ScannerContact> Resolve(
            IEnumerable<ScannerFleetCandidate> fleets, IEnumerable<SpatialNode> nodes = null,
            double scannerRangeKm = 100.0)
            => PlayerScanner.Resolve(1, Origin, scannerRangeKm, nodes, fleets);

        /// <summary>A genuinely quiet hull: 50 t, unarmed. That matters — passing no catalogue at
        /// all yields dry mass 0, which <c>Signature.ForShip</c> reads as the <em>reference</em> hull
        /// (1.0), and 1.0 x the 3.0 excitation lands exactly on the floor. A test written that way
        /// passes with the floor deleted, which is how the first draft of this file was written and
        /// what the mutation check caught.</summary>
        private sealed class QuietDrone : IPrefabCatalogue
        {
            public bool HasPrefab(string prefabId) => true;
            public long CargoLiters(string prefabId) => 0;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) => new Dictionary<string, long>();
            public double Dps(string prefabId) => 0;
            public double DryMassKg(string prefabId) => 50_000;     // 0.1x reference -> base 0.447
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => null;
            public string GridSize(string prefabId) => "Small";
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new string[0];
            public IEnumerable<string> AllPrefabIds => new[] { "Drone" };
            public double DrillYieldScore(string prefabId) => 0;
            public IReadOnlyList<string> RolesFor(string prefabId) => new[] { "Raid" };
        }

        /// <summary>Signature of a quiet drone on the given leg, well past the departure window so
        /// only the stop-kind terms are in play.</summary>
        private static double SignatureOf(FleetStopKind stop, FactionTuning t, int convoySize = 1)
        {
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                CurrentStopKind = stop,
                ConvoySize = convoySize,
                ShipPrefabId = "Drone",
                DepartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            DateTime now = fleet.DepartedUtc.AddSeconds(600);
            return FleetSensing.SignatureOf(fleet, new QuietDrone(), null, t, now);
        }

        [Fact]
        public void AQuietHullFighting_IsStillLoud_BecauseAStealthyRaidIsNotAThing()
        {
            FactionTuning t = TestSupport.Tuning();

            double approaching = SignatureOf(FleetStopKind.Service, t);
            double fighting    = SignatureOf(FleetStopKind.Engage, t);

            // The multiplier ALONE would leave this drone at ~1.34 — audibly quiet. The floor is the
            // only thing that lifts it, so this assertion fails if the floor is removed.
            Assert.True(approaching * (1.0 + t.SignatureWeightFleetEngagement) < t.SignatureEngagementFloor,
                "fixture must be quiet enough that the multiplier alone cannot reach the floor");
            Assert.Equal(t.SignatureEngagementFloor, fighting, 6);
            Assert.True(approaching < fighting,
                "a stealthy approach must stay possible: " + approaching + " vs " + fighting);
        }

        [Fact]
        public void ABiggerFightIsLouderThanASmallOne_SoTheFloorIsAFloorAndNotAFlatValue()
        {
            FactionTuning t = TestSupport.Tuning();

            double lone     = SignatureOf(FleetStopKind.Engage, t, convoySize: 1);
            double squadron = SignatureOf(FleetStopKind.Engage, t, convoySize: 9);

            Assert.True(squadron > lone,
                "a squadron engaging must outread a lone fighter engaging: " + squadron + " vs " + lone);
        }

        [Fact]
        public void ADwellDoesNotFireTheDepartureBurst_SoAFightDoesNotGetQuieterAsItRuns()
        {
            // AdvanceToNextStop stamps DepartedUtc on every leg, the dwell included. Without the
            // guard the departure term rides on top for the first 120 s and then decays out, so
            // detection range SHRINKS while the fight runs — backwards for a reaction window.
            FactionTuning t = TestSupport.Tuning();
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Raid, CurrentStopKind = FleetStopKind.Engage,
                DepartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            double atStart = FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc);
            double late    = FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc.AddSeconds(280));

            Assert.Equal(atStart, late, 6);
            Assert.Equal(1.0 + t.SignatureWeightFleetEngagement, atStart, 6);
        }

        [Fact]
        public void TheSameGuardAppliesToMine_WhichHadTheIdenticalInversion()
        {
            FactionTuning t = TestSupport.Tuning();
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Mine, CurrentStopKind = FleetStopKind.Mine,
                DepartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            Assert.Equal(FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc),
                         FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc.AddSeconds(60)), 6);
        }

        [Fact]
        public void AFleetInTransitStillGetsItsDepartureBurst()
        {
            // The guard must not widen: a fleet that really did just light its drives is still loud.
            FactionTuning t = TestSupport.Tuning();
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Trade, CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            Assert.True(FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc)
                      > FleetSensing.ExcitationOf(fleet, t, fleet.DepartedUtc.AddSeconds(280)),
                "a real departure must still decay");
        }

        [Fact]
        public void AnEngagingRaid_ReachesFurtherThanTheSameFleetInTransit_ThroughTheOrdinaryRule()
        {
            // The claim that replaced the flat-range assertions: no bypass, just a louder object.
            // Same fleet, same scanner, 250 km out — the stop kind is the only difference.
            FactionTuning t = TestSupport.Tuning();
            double transitSig = SignatureOf(FleetStopKind.Service, t);
            double fightSig   = SignatureOf(FleetStopKind.Engage, t);

            Assert.Empty(Resolve(new[] { Fleet(250, signature: transitSig) }));
            Assert.Single(Resolve(new[] { Fleet(250, engagingAt: "N008", signature: fightSig) }));
        }

        [Fact]
        public void ABetterScannerIsWorthMoreAgainstARaid_NotBypassedByIt()
        {
            // The property the flat range destroyed: detection range is sensor x signature, so an
            // antenna's advantage over the bare floor is multiplied by the fight's loudness rather
            // than flattened away by a special case.
            FactionTuning t = TestSupport.Tuning();
            double fightSig = SignatureOf(FleetStopKind.Engage, t);

            Assert.Empty(Resolve(new[] { Fleet(250, engagingAt: "N008", signature: fightSig) },
                                 scannerRangeKm: t.SensorFloorKm));
            Assert.Single(Resolve(new[] { Fleet(250, engagingAt: "N008", signature: fightSig) },
                                  scannerRangeKm: ShipSensor.ReferenceKm(t)));
        }

        [Fact]
        public void AnEngagementIsOneContact_AndReadsAsParked()
        {
            IList<ScannerContact> contacts =
                Resolve(new[] { Fleet(10, engagingAt: "N008", signature: 3.0) });

            Assert.Single(contacts);
            Assert.Equal(ScannerContactKind.RaidEngagement, contacts[0].Kind);
            Assert.False(contacts[0].Moving);
        }

        [Fact]
        public void ARaidStillInTransit_ReadsAsAnOrdinaryFleet()
        {
            IList<ScannerContact> contacts = Resolve(new[] { Fleet(10) });

            Assert.Single(contacts);
            Assert.Equal(ScannerContactKind.Fleet, contacts[0].Kind);
            Assert.True(contacts[0].Moving);
        }

        [Fact]
        public void TheLabelNamesThePlace_SoAViewerWhoHasNeverBeenThereKnowsWhereToGo()
        {
            var world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(400, 0, 0), 0.6, "N008");
            node.OwnerFactionTag = "MERC";
            node.DisplayName = "Ceres Depot";
            node.Radius = 40;

            IList<ScannerContact> contacts = Resolve(
                new[] { Fleet(10, engagingAt: "N008", signature: 3.0) }, world.Graph.Nodes.Values);

            ScannerContact raid = default(ScannerContact);
            foreach (ScannerContact c in contacts)
                if (c.Kind == ScannerContactKind.RaidEngagement) { raid = c; break; }
            Assert.Equal("Raid: Ceres Depot", raid.Label);
        }

        [Fact]
        public void ScannerlessPlayersGetNothing_HoweverLoudTheFight()
        {
            Assert.Empty(PlayerScanner.Resolve(0, Origin, 100.0, null,
                new[] { Fleet(10, engagingAt: "N008", signature: 6.0) }));
        }
    }
}
