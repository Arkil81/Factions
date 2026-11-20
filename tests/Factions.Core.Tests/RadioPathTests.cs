using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-419 slice 5: the pilot radio's physics. Values pinned against the formulas and
    /// against the operator's radio physics bench readouts (2026-09-29).</summary>
    public class RadioPathTests
    {
        [Fact]
        public void Approaching_raises_the_voice_and_receding_lowers_it()
        {
            Assert.True(RadioPath.DopplerHz(450, -100) > 0);
            Assert.True(RadioPath.DopplerHz(450, 100) < 0);
            Assert.Equal(150.1, RadioPath.DopplerHz(450, -100), 1);   // 450 MHz x 100 m/s / c
        }

        [Fact]
        public void Flutter_is_speed_over_wavelength()
        {
            Assert.Equal(0.6662, RadioPath.WavelengthM(450), 4);
            Assert.Equal(450.3, RadioPath.FlutterHz(450, 300), 1);     // the bench read 450.31 Hz
            Assert.Equal(0.0, RadioPath.FlutterHz(450, 0));
        }

        [Fact]
        public void Signal_to_noise_falls_six_db_per_doubling()
        {
            Assert.Equal(35.0, RadioPath.SnrDb(35, 1, 0), 6);
            Assert.Equal(35.0 - 6.0206, RadioPath.SnrDb(35, 2, 0), 3);
            Assert.Equal(12.5, RadioPath.SnrDb(35, 6, 6.9), 1);        // the bench: 6 km, grazing -> 12.5 dB
            Assert.Equal(RadioPath.SnrDb(35, 0.05, 0), RadioPath.SnrDb(35, 0.001, 0));
        }

        [Theory]
        [InlineData(-1.0, 0.0)]
        [InlineData(0.0, 6.03)]
        [InlineData(0.1, 6.9)]
        [InlineData(1.0, 13.93)]
        [InlineData(3.0, 22.42)]
        public void Knife_edge_loss_follows_ITU_R_P526(double nu, double db)
        {
            Assert.Equal(db, RadioPath.KnifeEdgeLossDb(nu), 1);
        }

        [Fact]
        public void The_muffle_opens_with_no_loss_and_reaches_the_floor_at_the_set_loss()
        {
            Assert.Equal(3400.0, RadioPath.MuffleCutoffHz(0, 700, 20));
            Assert.Equal(1971, RadioPath.MuffleCutoffHz(6.9, 700, 20), 0);   // the bench read 1971 Hz
            Assert.Equal(700.0, RadioPath.MuffleCutoffHz(20, 700, 20), 6);
            Assert.Equal(700.0, RadioPath.MuffleCutoffHz(60, 700, 20), 6);
        }

        [Fact]
        public void A_line_well_clear_of_a_planet_is_not_diffracted()
        {
            // 60 km planet at the origin; a 20 km line passing 70 km from its centre.
            double nu = RadioPath.SphereNu(new Vec3(-10, 70, 0), new Vec3(10, 70, 0), new Vec3(0, 0, 0), 60, 0.666);
            Assert.True(nu < -0.78, "nu " + nu);
            Assert.Equal(0.0, RadioPath.KnifeEdgeLossDb(nu));
        }

        [Fact]
        public void A_line_just_inside_the_limb_is_deep_in_shadow()
        {
            // Passing 100 m inside the surface over 10 km each way: a real planet does not muffle, it cuts.
            double nu = RadioPath.SphereNu(new Vec3(-10, 59.9, 0), new Vec3(10, 59.9, 0), new Vec3(0, 0, 0), 60, 0.666);
            Assert.True(nu > 2, "nu " + nu);
            Assert.True(RadioPath.KnifeEdgeLossDb(nu) > 20);
        }

        [Fact]
        public void A_planet_behind_one_end_is_not_in_the_way()
        {
            // A surface station talking outward: the planet's centre projects behind the station.
            double nu = RadioPath.SphereNu(new Vec3(0, 60.5, 0), new Vec3(0, 90, 0), new Vec3(0, 0, 0), 60, 0.666);
            Assert.Equal(double.NegativeInfinity, nu);
        }

        [Fact]
        public void Occlusion_reports_the_worst_body_on_the_line()
        {
            var planets = new List<PlanetRecord>
            {
                new PlanetRecord { Center = new Vec3(0, 0, 0), SurfaceRadius = 60 },
            };
            SensorOcclusion occ = new SensorOcclusion(planets, 0);
            Assert.Equal(1, occ.Count);
            double through = occ.WorstDiffractionNu(new Vec3(-100, 0, 0), new Vec3(100, 0, 0), 0.666);
            double clear = occ.WorstDiffractionNu(new Vec3(-100, 200, 0), new Vec3(100, 200, 0), 0.666);
            Assert.True(through > 100, "through " + through);
            Assert.True(clear < -0.78, "clear " + clear);
            Assert.Equal(double.NegativeInfinity, SensorOcclusion.None.WorstDiffractionNu(new Vec3(-100, 0, 0), new Vec3(100, 0, 0), 0.666));
        }

        [Fact]
        public void An_arrival_calls_in_five_km_out_at_no_more_than_grid_speed()
        {
            WorldModel w = TestSupport.World(1);
            w.Graph.Add(new Vec3(0, 0, 0), 1.0, "A").OwnerFactionTag = "HAUL";
            w.Graph.Add(new Vec3(200, 0, 0), 1.0, "B").OwnerFactionTag = "HAUL";
            var st = new FactionState { Tag = "HAUL" };
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);   // seed
            // 200 km in two minutes: the sim's abstract cruise, far above any grid speed.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "HAUL", FleetId = "H8", OriginNodeId = "A", TargetNodeId = "B",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(200, 0, 0), CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = w.UtcNow.AddMinutes(-2), EstimatedArrivalUtc = w.UtcNow, LastResolvedSegment = -1,
            });
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);
            TrafficEvent a = w.TakeTrafficEvents().Find(e => e.Kind == TrafficEventKind.Arrive);
            Assert.NotNull(a);
            Assert.InRange(a.PilotPosKm.DistanceTo(new Vec3(200, 0, 0)), 4.9, 5.1);
            double speed = Math.Sqrt(a.PilotVelMs.X * a.PilotVelMs.X + a.PilotVelMs.Y * a.PilotVelMs.Y + a.PilotVelMs.Z * a.PilotVelMs.Z);
            Assert.InRange(speed, 99.0, 100.0 + 1e-6);   // capped, direction kept
            Assert.True(a.PilotVelMs.X > 0);
        }

        [Fact]
        public void The_radio_speed_cap_follows_the_server_grid_limit()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GridMaxSpeedLarge = GridSpeedPolicy.NoOverride; t.GridMaxSpeedSmall = GridSpeedPolicy.NoOverride;
            Assert.Equal(100.0, TrafficWatch.RadioSpeedCapMs(t));
            t.GridMaxSpeedLarge = 300; t.GridMaxSpeedSmall = 400;
            Assert.Equal(400.0, TrafficWatch.RadioSpeedCapMs(t));
        }

        [Fact]
        public void A_departure_carries_the_pilot_moving_away_from_the_stop()
        {
            WorldModel w = TestSupport.World(1);
            w.Graph.Add(new Vec3(0, 0, 0), 1.0, "A").OwnerFactionTag = "HAUL";
            w.Graph.Add(new Vec3(200, 0, 0), 1.0, "B").OwnerFactionTag = "HAUL";
            var st = new FactionState { Tag = "HAUL" };
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);   // seed
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "HAUL", FleetId = "H9", OriginNodeId = "A", TargetNodeId = "B",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(200, 0, 0), CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10), LastResolvedSegment = -1,
            });
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);
            TrafficEvent d = Assert.Single(w.TakeTrafficEvents());
            Assert.Equal(TrafficEventKind.Depart, d.Kind);
            Assert.True(d.PilotVelMs.X > 0, "moving toward B, vx " + d.PilotVelMs.X);
            Assert.Equal(0.0, d.PilotVelMs.Y, 6);
            Assert.True(d.PilotPosKm.X < 1, "calls from the stop, x " + d.PilotPosKm.X);
        }
        private static AntennaReading ReferenceAntenna(FactionTuning t)
        {
            return new AntennaReading { VolumeM3 = t.SensorAntennaReferenceVolumeM3, RadiusM = t.SensorAntennaReferenceM, Broadcasting = true };
        }

        private static RadioReceiver Seat(FactionTuning t, int antennas)
        {
            var list = new List<AntennaReading>();
            for (int i = 0; i < antennas; i++) list.Add(ReferenceAntenna(t));
            return new RadioReceiver { Seated = true, AntennaCount = antennas, StrengthKm = ShipSensor.StrengthKm(list, t) };
        }

        [Fact]
        public void A_suit_hears_its_own_short_range_whatever_the_ship_carries()
        {
            FactionTuning t = TestSupport.Tuning();
            var suit = default(RadioReceiver);
            Assert.Equal(10.0, RadioPath.StationRangeKm(suit, t));
            Assert.Equal(10.0, RadioPath.PilotRangeKm(suit, 3.0, 50.0, t));   // loud, broadcasting: still 10
            // Stood up out of a well-fitted ship: the strength is the ship's, and the suit does not use it.
            RadioReceiver standing = Seat(t, 3); standing.Seated = false;
            Assert.Equal(10.0, RadioPath.StationRangeKm(standing, t));
        }

        [Fact]
        public void A_seat_with_no_antenna_hears_the_ship_default_and_nothing_further()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver bare = Seat(t, 0);
            Assert.Equal(30.0, RadioPath.StationRangeKm(bare, t));
            Assert.Equal(30.0, RadioPath.PilotRangeKm(bare, 3.0, 50.0, t));   // no antenna, no broadcast channel
        }

        [Fact]
        public void Antennas_hear_what_the_scanner_sees_and_more_antennas_hear_further()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver one = Seat(t, 1), two = Seat(t, 2), four = Seat(t, 4);
            Assert.Equal(ShipSensor.ReferenceKm(t), RadioPath.StationRangeKm(one, t), 6);
            Assert.True(RadioPath.StationRangeKm(two, t) > RadioPath.StationRangeKm(one, t));
            // Root sum of squares: four reference antennas are worth two of one's antenna term.
            double term = ShipSensor.ReferenceKm(t) - t.SensorFloorKm;
            Assert.Equal(t.SensorFloorKm + 2 * term, RadioPath.StationRangeKm(four, t), 6);
            // The pilot is heard as far as the scanner would see the hull: strength x signature.
            Assert.Equal(one.StrengthKm * 2.0, RadioPath.PilotRangeKm(one, 2.0, 0, t), 6);
            Assert.Equal(Sensing.DetectionRangeKm(one.StrengthKm, 2.0, 0), RadioPath.PilotRangeKm(one, 2.0, 0, t), 6);
        }

        [Fact]
        public void Fitting_an_antenna_never_shortens_the_reach()
        {
            FactionTuning t = TestSupport.Tuning();
            // A token antenna: the sensor is barely over its floor, below the bare-seat default.
            var tiny = new RadioReceiver { Seated = true, AntennaCount = 1, StrengthKm = t.SensorFloorKm + 0.5 };
            Assert.True(tiny.StrengthKm < t.AtcRadioRangeKm);
            Assert.Equal(30.0, RadioPath.StationRangeKm(tiny, t));
            Assert.Equal(30.0, RadioPath.PilotRangeKm(tiny, 0.2, 0, t));   // a quiet hull
        }

        [Fact]
        public void A_hull_broadcasting_further_than_it_is_seen_is_heard_at_its_broadcast()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver one = Seat(t, 1);
            double seen = RadioPath.PilotRangeKm(one, 1.0, 0, t);
            Assert.Equal(seen + 40, RadioPath.PilotRangeKm(one, 1.0, seen + 40, t), 6);
        }

        [Fact]
        public void Every_reach_sounds_the_same_at_its_own_edge()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(0.0, RadioPath.AntennaGainDb(30, t), 6);
            Assert.Equal(-9.54, RadioPath.AntennaGainDb(10, t), 2);
            double suitEdge = RadioPath.SnrDb(t.AtcSnrAt1kmDb, 10, 0) + RadioPath.AntennaGainDb(10, t);
            double shipEdge = RadioPath.SnrDb(t.AtcSnrAt1kmDb, 30, 0) + RadioPath.AntennaGainDb(30, t);
            double arrayEdge = RadioPath.SnrDb(t.AtcSnrAt1kmDb, 300, 0) + RadioPath.AntennaGainDb(300, t);
            Assert.Equal(shipEdge, suitEdge, 6);
            Assert.Equal(shipEdge, arrayEdge, 6);
        }

        [Fact]
        public void A_traffic_event_carries_the_pilots_signature()
        {
            WorldModel w = TestSupport.World(1);
            w.Graph.Add(new Vec3(0, 0, 0), 1.0, "A").OwnerFactionTag = "HAUL";
            w.Graph.Add(new Vec3(200, 0, 0), 1.0, "B").OwnerFactionTag = "HAUL";
            var st = new FactionState { Tag = "HAUL" };
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);   // seed
            var f = new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "HAUL", FleetId = "H7", OriginNodeId = "A", TargetNodeId = "B",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(200, 0, 0), CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10), LastResolvedSegment = -1,
                MaterialisedEntityId = 4242,
            };
            st.PendingFleets.Add(f);
            TrafficWatch.Scan(new List<FactionState> { st }, w, w.Tuning);
            TrafficEvent d = Assert.Single(w.TakeTrafficEvents());
            Assert.Equal(FleetSensing.SignatureOf(f, w.PrefabCatalogue, null, w.Tuning, w.UtcNow), d.PilotSignature, 9);
            Assert.True(d.PilotSignature > 1.0, "a fresh departure is excited, sig " + d.PilotSignature);
            Assert.Equal(4242, d.PilotEntityId);
        }
        private static RadioReceiver DishAlong(FactionTuning t, params Vec3[] axes)
        {
            RadioReceiver r = Seat(t, 1);
            r.Dishes = new RadioDish[axes.Length];
            for (int i = 0; i < axes.Length; i++) r.Dishes[i] = new RadioDish { Kind = RadioDishKind.Dish, Axis = axes[i] };
            return r;
        }

        [Theory]
        [InlineData(0.0, 8.0)]       // on axis: its own peak
        [InlineData(22.5, 5.0)]      // half its 45 deg beam: 3 dB down
        [InlineData(36.7, 0.0)]      // 45 x sqrt(8/12): the parabola reaches 0
        [InlineData(90.0, 0.0)]
        public void A_parabolic_antenna_has_a_weaker_wider_beam(double deg, double db)
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = Seat(t, 1);
            r.Dishes = new[] { new RadioDish { Kind = RadioDishKind.Parabolic, Axis = new Vec3(1, 0, 0) } };
            Assert.Equal(db, RadioPath.DirectionalGainDb(r, new Vec3(0, 0, 0), At(deg, 50), t), 1);
        }

        [Fact]
        public void A_dish_and_a_parabolic_antenna_each_use_their_own_pattern()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = Seat(t, 2);
            r.Dishes = new[]
            {
                new RadioDish { Kind = RadioDishKind.Dish, Axis = new Vec3(1, 0, 0) },
                new RadioDish { Kind = RadioDishKind.Parabolic, Axis = new Vec3(0, 1, 0) },
            };
            Vec3 o = new Vec3(0, 0, 0);
            Assert.Equal(12.0, RadioPath.DirectionalGainDb(r, o, At(0, 50), t), 6);
            Assert.Equal(8.0, RadioPath.DirectionalGainDb(r, o, At(90, 50), t), 6);
            // 25 deg off the dish is outside its beam (12 - 12 x (25/30)^2 = 3.7), and 65 deg off the
            // parabolic is outside its own; the better of the two wins.
            Assert.Equal(12.0 - 12.0 * (25.0 / 30) * (25.0 / 30), RadioPath.DirectionalGainDb(r, o, At(25, 50), t), 6);
        }

        private static Vec3 At(double deg, double km)
        {
            double rad = deg * Math.PI / 180.0;
            return new Vec3(km * Math.Cos(rad), km * Math.Sin(rad), 0);
        }

        [Theory]
        [InlineData(0.0, 12.0)]     // on axis: the peak
        [InlineData(15.0, 9.0)]     // half the beamwidth off: 3 dB down
        [InlineData(30.0, 0.0)]     // a whole beamwidth off: parabola 12 - 12 = 0
        [InlineData(90.0, 0.0)]     // beside it: no worse than omni
        [InlineData(180.0, 0.0)]    // behind it
        public void A_dish_gains_on_its_axis_and_falls_off_a_parabola(double deg, double db)
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = DishAlong(t, new Vec3(1, 0, 0));
            Assert.Equal(db, RadioPath.DirectionalGainDb(r, new Vec3(0, 0, 0), At(deg, 50), t), 6);
        }

        [Fact]
        public void Pointing_a_dish_at_a_station_lengthens_the_reach_that_way_only()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = DishAlong(t, new Vec3(1, 0, 0));
            double omni = RadioPath.StationRangeKm(r, t);
            Assert.Equal(omni * Math.Pow(10, 12.0 / 20), RadioPath.StationRangeKm(r, t, new Vec3(0, 0, 0), At(0, 200)), 6);
            Assert.Equal(omni, RadioPath.StationRangeKm(r, t, new Vec3(0, 0, 0), At(120, 200)), 6);
            // A station 300 km out along the axis is in reach; the same station abeam is not.
            Assert.True(RadioPath.StationRangeKm(r, t, new Vec3(0, 0, 0), At(0, 300)) > 300);
            Assert.True(RadioPath.StationRangeKm(r, t, new Vec3(0, 0, 0), At(90, 300)) < 300);
        }

        [Fact]
        public void Pointing_a_dish_at_a_pilot_lengthens_that_reach_too()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = DishAlong(t, new Vec3(0, 0, 1));
            double omni = RadioPath.PilotRangeKm(r, 2.0, 0, t);
            Assert.Equal(omni * Math.Pow(10, 12.0 / 20), RadioPath.PilotRangeKm(r, 2.0, 0, t, new Vec3(5, 5, 5), new Vec3(5, 5, 90)), 6);
        }

        [Fact]
        public void The_best_aimed_dish_wins()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = DishAlong(t, new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(-1, 0, 0));
            Assert.Equal(12.0, RadioPath.DirectionalGainDb(r, new Vec3(0, 0, 0), At(0, 50), t), 6);
        }

        [Fact]
        public void No_dish_no_seat_or_no_gain_means_no_direction()
        {
            FactionTuning t = TestSupport.Tuning();
            Vec3 o = new Vec3(0, 0, 0), ahead = At(0, 50);
            Assert.Equal(0.0, RadioPath.DirectionalGainDb(Seat(t, 2), o, ahead, t));
            RadioReceiver standing = DishAlong(t, new Vec3(1, 0, 0)); standing.Seated = false;
            Assert.Equal(0.0, RadioPath.DirectionalGainDb(standing, o, ahead, t));
            Assert.Equal(10.0, RadioPath.StationRangeKm(standing, t, o, ahead));   // the suit's reach, unchanged
            t.AtcDishGainDb = 0;
            Assert.Equal(0.0, RadioPath.DirectionalGainDb(DishAlong(t, new Vec3(1, 0, 0)), o, ahead, t));
        }

        [Fact]
        public void A_dish_reach_still_sounds_the_same_at_its_edge()
        {
            FactionTuning t = TestSupport.Tuning();
            RadioReceiver r = DishAlong(t, new Vec3(1, 0, 0));
            double reach = RadioPath.StationRangeKm(r, t, new Vec3(0, 0, 0), At(0, 100));
            double dishEdge = RadioPath.SnrDb(t.AtcSnrAt1kmDb, reach, 0) + RadioPath.AntennaGainDb(reach, t);
            double shipEdge = RadioPath.SnrDb(t.AtcSnrAt1kmDb, 30, 0) + RadioPath.AntennaGainDb(30, t);
            Assert.Equal(shipEdge, dishEdge, 6);
        }
    }
}
