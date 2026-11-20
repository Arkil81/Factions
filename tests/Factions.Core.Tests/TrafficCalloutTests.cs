using System;
using Factions;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-419 slice 3: the traffic callout on the wire, and which fleets the controller
    /// talks about.</summary>
    public class TrafficCalloutTests
    {
        private static WireTrafficCallout Sample()
        {
            return new WireTrafficCallout
            {
                Event = WireTrafficEvent.Arrive, Voice = WireTrafficVoice.Haul, ActorTag = "METC",
                NodeId = "N093", NodeName = "Wayfarer Wharf", NodeOwnerTag = "METC", DistanceShare = 200,
            };
        }

        [Fact]
        public void A_callout_round_trips()
        {
            WireTrafficCallout back;
            Assert.True(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteTrafficCallout(Sample()), out back));
            Assert.Equal(WireTrafficEvent.Arrive, back.Event);
            Assert.Equal(WireTrafficVoice.Haul, back.Voice);
            Assert.Equal("METC", back.ActorTag);
            Assert.Equal("N093", back.NodeId);
            Assert.Equal("Wayfarer Wharf", back.NodeName);
            Assert.Equal("METC", back.NodeOwnerTag);
            Assert.Equal(200, back.DistanceShare);
        }

        [Fact]
        public void A_queue_callout_round_trips_with_its_fleet_id()
        {
            WireTrafficCallout c = Sample();
            c.Event = WireTrafficEvent.Diverted;
            c.FleetId = "646CEA";
            c.QueuePosition = 4;
            c.Departure = true;
            c.DivertNodeId = "N765";
            c.DivertNodeName = "Wayfarer Wharf";
            WireTrafficCallout back;
            Assert.True(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteTrafficCallout(c), out back));
            Assert.Equal(WireTrafficEvent.Diverted, back.Event);
            Assert.Equal("646CEA", back.FleetId);
            Assert.Equal(4, back.QueuePosition);
            Assert.True(back.Departure);
            Assert.Equal("N765", back.DivertNodeId);
            Assert.Equal("Wayfarer Wharf", back.DivertNodeName);
        }

        [Theory]
        [InlineData(TrafficEventKind.Queued)]
        [InlineData(TrafficEventKind.Held)]
        [InlineData(TrafficEventKind.Delayed)]
        [InlineData(TrafficEventKind.Diverted)]
        [InlineData(TrafficEventKind.Cleared)]
        public void Queue_events_are_voiced_for_civilian_traffic_and_keep_their_wire_value(TrafficEventKind kind)
        {
            WireTrafficVoice voice;
            Assert.True(TrafficVoiceMap.TryVoice(Event(kind, ActionKind.Haul), out voice));
            Assert.Equal(WireTrafficVoice.Haul, voice);
            Assert.Equal((byte)kind, (byte)(WireTrafficEvent)(byte)kind);
            Assert.True(Enum.IsDefined(typeof(WireTrafficEvent), (byte)kind));
        }

        [Fact]
        public void Null_strings_travel_as_empty()
        {
            WireTrafficCallout c = Sample();
            c.NodeName = null;
            c.NodeOwnerTag = null;
            WireTrafficCallout back;
            Assert.True(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteTrafficCallout(c), out back));
            Assert.Equal("", back.NodeName);
            Assert.Equal("", back.NodeOwnerTag);
        }

        [Fact]
        public void The_reader_refuses_what_it_does_not_know()
        {
            byte[] data = FactionsWire.WriteTrafficCallout(Sample());
            // After the header: event, voice, four length-prefixed strings ("METC", "N093",
            // "Wayfarer Wharf", "METC": 5 + 5 + 15 + 5 bytes), the share byte and the aim's eight
            // floats -- 33 + 32 bytes -- then the queue tail: empty fleet id, position, departure,
            // two empty divert strings -- 5 bytes.
            int eventAt = data.Length - 33 - 32 - 5;
            WireTrafficCallout back;

            byte[] badEvent = (byte[])data.Clone();
            badEvent[eventAt] = 9;
            Assert.False(FactionsWire.TryReadTrafficCallout(badEvent, out back));

            byte[] badVoice = (byte[])data.Clone();
            badVoice[eventAt + 1] = 0;
            Assert.False(FactionsWire.TryReadTrafficCallout(badVoice, out back));

            byte[] truncated = new byte[data.Length - 3];
            Array.Copy(data, truncated, truncated.Length);
            Assert.False(FactionsWire.TryReadTrafficCallout(truncated, out back));

            Assert.False(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteCombatOutcomeNotice(WireCombatTone.Win, "x"), out back));
        }

        private static TrafficEvent Event(TrafficEventKind kind, ActionKind fleet)
        {
            return new TrafficEvent { Kind = kind, FleetKind = fleet, NodeId = "N1", ActorTag = "T" };
        }

        [Theory]
        [InlineData(ActionKind.Haul, WireTrafficVoice.Haul)]
        [InlineData(ActionKind.Ferry, WireTrafficVoice.Haul)]
        [InlineData(ActionKind.Trade, WireTrafficVoice.Trade)]
        [InlineData(ActionKind.Gift, WireTrafficVoice.Trade)]
        [InlineData(ActionKind.Mine, WireTrafficVoice.Mine)]
        [InlineData(ActionKind.Reinforce, WireTrafficVoice.Relief)]
        [InlineData(ActionKind.Guard, WireTrafficVoice.Patrol)]
        [InlineData(ActionKind.Patrol, WireTrafficVoice.Patrol)]
        public void Traffic_gets_its_line_family(ActionKind fleet, WireTrafficVoice expected)
        {
            WireTrafficVoice voice;
            Assert.True(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.Arrive, fleet), out voice));
            Assert.Equal(expected, voice);
            Assert.True(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.Depart, fleet), out voice));
        }

        [Theory]
        [InlineData(ActionKind.Raid)]
        [InlineData(ActionKind.Conquer)]
        [InlineData(ActionKind.Retaliate)]
        public void An_attacker_gets_only_the_hostile_warning(ActionKind fleet)
        {
            WireTrafficVoice voice;
            Assert.False(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.Arrive, fleet), out voice));
            Assert.False(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.Depart, fleet), out voice));
            Assert.True(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.HostileInbound, fleet), out voice));
            Assert.Equal(WireTrafficVoice.Hostile, voice);
        }

        [Theory]
        [InlineData(ActionKind.Develop)]
        [InlineData(ActionKind.Muster)]
        [InlineData(ActionKind.Idle)]
        public void Other_kinds_are_not_called(ActionKind fleet)
        {
            WireTrafficVoice voice;
            Assert.False(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.Arrive, fleet), out voice));
            Assert.False(TrafficVoiceMap.TryVoice(Event(TrafficEventKind.HostileInbound, fleet), out voice));
        }

        private static TrafficEvent PilotAt(Vec3 posKm, Vec3 velMs)
        {
            return new TrafficEvent { Kind = TrafficEventKind.Arrive, FleetKind = ActionKind.Haul, NodeId = "N1",
                                      PilotPosKm = posKm, PilotVelMs = velMs };
        }

        private static TrafficListener Still(Vec3 posKm)
        {
            return new TrafficListener { SteamId = 1, PosKm = posKm, VelMs = new Vec3(0, 0, 0) };
        }

        [Fact]
        public void A_pilot_2_km_away_in_clear_sky_is_6_db_below_the_1_km_figure()
        {
            FactionTuning t = TestSupport.Tuning();
            WireTrafficPilotCall c = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(2, 0, 0), new Vec3(0, 0, 0)),
                                                               Still(new Vec3(0, 0, 0)), double.NegativeInfinity, t.AtcRadioRangeKm, t);
            Assert.Equal((short)Math.Round((t.AtcSnrAt1kmDb - 6.0206) * 10), c.SnrDeciDb);
            Assert.Equal(3400, c.CutoffHz);
            Assert.Equal(0, c.DopplerHz);
            Assert.Equal(0, c.FlutterHz);
            Assert.Equal((sbyte)t.AtcPilotLevelDb, c.LevelDb);
            Assert.Equal((sbyte)(t.AtcPilotLevelDb - t.AtcSnrAt1kmDb), c.NoiseDb);
        }

        [Fact]
        public void Closing_on_the_listener_raises_the_voice_whichever_end_moves()
        {
            FactionTuning t = TestSupport.Tuning();
            // Pilot 5 km out on +X flying toward the listener at 100 m/s.
            WireTrafficPilotCall pilotMoves = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(5, 0, 0), new Vec3(-100, 0, 0)),
                                                                        Still(new Vec3(0, 0, 0)), double.NegativeInfinity, t.AtcRadioRangeKm, t);
            // Listener flying toward a still pilot at the same speed.
            var movingListener = new TrafficListener { SteamId = 1, PosKm = new Vec3(0, 0, 0), VelMs = new Vec3(100, 0, 0) };
            WireTrafficPilotCall listenerMoves = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(5, 0, 0), new Vec3(0, 0, 0)),
                                                                           movingListener, double.NegativeInfinity, t.AtcRadioRangeKm, t);
            Assert.Equal((short)Math.Round(RadioPath.DopplerHz(t.AtcCarrierMHz, -100)), pilotMoves.DopplerHz);
            Assert.True(pilotMoves.DopplerHz > 0);
            Assert.Equal(pilotMoves.DopplerHz, listenerMoves.DopplerHz);
            Assert.Equal((ushort)Math.Round(RadioPath.FlutterHz(t.AtcCarrierMHz, 100)), pilotMoves.FlutterHz);
        }

        [Fact]
        public void Crossing_at_right_angles_flutters_without_a_doppler_offset()
        {
            FactionTuning t = TestSupport.Tuning();
            WireTrafficPilotCall c = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(5, 0, 0), new Vec3(0, 200, 0)),
                                                               Still(new Vec3(0, 0, 0)), double.NegativeInfinity, t.AtcRadioRangeKm, t);
            Assert.Equal(0, c.DopplerHz);
            Assert.True(c.FlutterHz > 0);
        }

        [Fact]
        public void A_grazing_planet_muffles_and_costs_signal()
        {
            FactionTuning t = TestSupport.Tuning();
            WireTrafficPilotCall clear = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(6, 0, 0), new Vec3(0, 0, 0)),
                                                                   Still(new Vec3(0, 0, 0)), double.NegativeInfinity, t.AtcRadioRangeKm, t);
            WireTrafficPilotCall grazing = TrafficVoiceMap.PilotCall(PilotAt(new Vec3(6, 0, 0), new Vec3(0, 0, 0)),
                                                                     Still(new Vec3(0, 0, 0)), 0.1, t.AtcRadioRangeKm, t);
            Assert.InRange(grazing.CutoffHz, 1970, 1972);   // the bench read 1971 Hz at nu 0.1
            Assert.InRange(clear.SnrDeciDb - grazing.SnrDeciDb, 68, 70);   // 6.9 dB of diffraction loss
        }

        [Fact]
        public void A_pilot_call_and_the_voice_preference_round_trip()
        {
            var c = new WireTrafficPilotCall { Event = WireTrafficEvent.Depart, NodeId = "N7", DopplerHz = -150, FlutterHz = 450,
                                               SnrDeciDb = -35, CutoffHz = 1971, Reflections = 77, SquelchDb = 3, LevelDb = -10, NoiseDb = -45 };
            WireTrafficPilotCall back;
            Assert.True(FactionsWire.TryReadTrafficPilotCall(FactionsWire.WriteTrafficPilotCall(c), out back));
            Assert.Equal(c.Event, back.Event); Assert.Equal("N7", back.NodeId); Assert.Equal(-150, back.DopplerHz);
            Assert.Equal(450, back.FlutterHz); Assert.Equal(-35, back.SnrDeciDb); Assert.Equal(1971, back.CutoffHz);
            Assert.Equal(77, back.Reflections); Assert.Equal(3, back.SquelchDb); Assert.Equal(-10, back.LevelDb); Assert.Equal(-45, back.NoiseDb);
            Assert.False(FactionsWire.TryReadTrafficPilotCall(FactionsWire.WriteTrafficCallout(Sample()), out back));

            bool enabled;
            Assert.True(FactionsWire.TryReadTrafficVoicePreference(FactionsWire.WriteTrafficVoicePreference(false), out enabled));
            Assert.False(enabled);
            Assert.True(FactionsWire.TryReadTrafficVoicePreference(FactionsWire.WriteTrafficVoicePreference(true), out enabled));
            Assert.True(enabled);
        }

        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(15.0, 128)]
        [InlineData(30.0, 255)]
        [InlineData(45.0, 255)]
        public void Distance_is_a_share_of_the_radio_range(double km, int expected)
        {
            Assert.Equal(expected, TrafficVoiceMap.Share(km, 30.0));
        }
        [Fact]
        public void A_longer_reach_hears_the_same_pilot_cleaner()
        {
            FactionTuning t = TestSupport.Tuning();
            TrafficEvent e = PilotAt(new Vec3(5, 0, 0), new Vec3(0, 0, 0));
            WireTrafficPilotCall suit = TrafficVoiceMap.PilotCall(e, Still(new Vec3(0, 0, 0)), double.NegativeInfinity, 10, t);
            WireTrafficPilotCall ship = TrafficVoiceMap.PilotCall(e, Still(new Vec3(0, 0, 0)), double.NegativeInfinity, 30, t);
            WireTrafficPilotCall array = TrafficVoiceMap.PilotCall(e, Still(new Vec3(0, 0, 0)), double.NegativeInfinity, 300, t);
            Assert.InRange(ship.SnrDeciDb - suit.SnrDeciDb, 95, 96);     // 9.54 dB
            Assert.InRange(array.SnrDeciDb - ship.SnrDeciDb, 199, 201);  // 20 dB
        }
        private static WireRadioAim SampleAim()
        {
            return new WireRadioAim { SourceX = 1234.5f, SourceY = -67.25f, SourceZ = 8.0f, OmniReachKm = 131.2f,
                                      DishGainDb = 12, DishBeamwidthDeg = 30, ParabolicGainDb = 8, ParabolicBeamwidthDeg = 45 };
        }

        [Fact]
        public void Both_traffic_messages_carry_the_aim()
        {
            WireTrafficCallout c = Sample();
            c.Aim = SampleAim();
            WireTrafficCallout cb;
            Assert.True(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteTrafficCallout(c), out cb));
            Assert.Equal(1234.5f, cb.Aim.SourceX); Assert.Equal(-67.25f, cb.Aim.SourceY); Assert.Equal(8.0f, cb.Aim.SourceZ);
            Assert.Equal(131.2f, cb.Aim.OmniReachKm);
            Assert.Equal(12f, cb.Aim.DishGainDb); Assert.Equal(30f, cb.Aim.DishBeamwidthDeg);
            Assert.Equal(8f, cb.Aim.ParabolicGainDb); Assert.Equal(45f, cb.Aim.ParabolicBeamwidthDeg);
            Assert.Equal(200, cb.DistanceShare);

            var p = new WireTrafficPilotCall { Event = WireTrafficEvent.Arrive, NodeId = "N1", SnrDeciDb = 55, Aim = SampleAim() };
            WireTrafficPilotCall pb;
            Assert.True(FactionsWire.TryReadTrafficPilotCall(FactionsWire.WriteTrafficPilotCall(p), out pb));
            Assert.Equal(55, pb.SnrDeciDb);
            Assert.Equal(131.2f, pb.Aim.OmniReachKm);
            Assert.Equal(45f, pb.Aim.ParabolicBeamwidthDeg);
        }

        [Theory]
        [InlineData("LargeBlockRadioAntennaDish", true, WireReflector.Dish)]
        [InlineData("LargeBlockCompactRadioAntennaReskin", true, WireReflector.Parabolic)]
        [InlineData("SmallBlockCompactRadioAntennaReskin", true, WireReflector.Parabolic)]
        [InlineData("LargeBlockCompactRadioAntenna", false, WireReflector.Dish)]
        [InlineData("LargeBlockRadioAntenna", false, WireReflector.Dish)]
        [InlineData("", false, WireReflector.Dish)]
        public void The_client_and_server_agree_on_which_blocks_are_reflectors(string subtype, bool reflector, WireReflector kind)
        {
            WireReflector got;
            Assert.Equal(reflector, RadioAim.TryKindOf(subtype, out got));
            if (reflector) Assert.Equal(kind, got);
        }

        [Fact]
        public void The_clients_pattern_is_cores_pattern()
        {
            FactionTuning t = TestSupport.Tuning();
            WireRadioAim aim = TrafficVoiceMap.Aim(new Vec3(0, 0, 0), 30, t);
            var rng = new Random(7);
            for (int n = 0; n < 500; n++)
            {
                var axis = new Vec3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                var src = new Vec3(rng.NextDouble() * 200 - 100, rng.NextDouble() * 200 - 100, rng.NextDouble() * 200 - 100);
                foreach (RadioDishKind kind in new[] { RadioDishKind.Dish, RadioDishKind.Parabolic })
                {
                    var r = new RadioReceiver { Seated = true, AntennaCount = 1, StrengthKm = 30,
                                                Dishes = new[] { new RadioDish { Kind = kind, Axis = axis } } };
                    double core = RadioPath.DirectionalGainDb(r, new Vec3(0, 0, 0), src, t);
                    double peak, beam;
                    RadioAim.PatternOf((WireReflector)(byte)kind, aim, out peak, out beam);
                    double wire = RadioAim.GainDb(peak, beam, axis.X, axis.Y, axis.Z, src.X, src.Y, src.Z);
                    Assert.Equal(core, wire, 4);
                }
            }
        }

        private static TrafficListener SeatedWithDish(Vec3 axis)
        {
            return new TrafficListener
            {
                SteamId = 1, PosKm = new Vec3(0, 0, 0), VelMs = new Vec3(0, 0, 0),
                Radio = new RadioReceiver { Seated = true, AntennaCount = 1, StrengthKm = 30,
                                            Dishes = new[] { new RadioDish { Kind = RadioDishKind.Dish, Axis = axis } } },
            };
        }

        private static TrafficEvent At(Vec3 nodeKm)
        {
            return new TrafficEvent { Kind = TrafficEventKind.Arrive, FleetKind = ActionKind.Haul, NodeId = "N1",
                                      NodePosKm = nodeKm, PilotPosKm = nodeKm, PilotSignature = 1.0 };
        }

        [Fact]
        public void A_station_behind_the_dish_could_be_heard_by_turning_but_is_not_heard_now()
        {
            FactionTuning t = TestSupport.Tuning();
            TrafficEvent e = At(new Vec3(-80, 0, 0));   // 80 km out, behind a dish pointing +X
            TrafficHearing h = TrafficVoiceMap.Hear(e, SeatedWithDish(new Vec3(1, 0, 0)), 0, t);
            Assert.False(h.StationNow);
            Assert.True(h.StationCould);                 // 30 km x 10^(12/20) = 119 km
            Assert.False(h.PilotNow);
            Assert.True(h.PilotCould);
            Assert.Equal(0.0, h.DishDb);
            // Turned onto it, it is heard now.
            TrafficHearing aimed = TrafficVoiceMap.Hear(e, SeatedWithDish(new Vec3(-1, 0, 0)), 0, t);
            Assert.True(aimed.StationNow);
            Assert.Equal(12.0, aimed.DishDb, 6);
        }

        [Fact]
        public void Beyond_the_best_aim_nothing_is_sent_and_without_a_reflector_could_is_now()
        {
            FactionTuning t = TestSupport.Tuning();
            TrafficHearing far = TrafficVoiceMap.Hear(At(new Vec3(130, 0, 0)), SeatedWithDish(new Vec3(1, 0, 0)), 0, t);
            Assert.False(far.StationCould);
            var bare = new TrafficListener { SteamId = 1, PosKm = new Vec3(0, 0, 0),
                                             Radio = new RadioReceiver { Seated = true } };
            foreach (double x in new[] { 10.0, 29.0, 31.0, 60.0 })
            {
                TrafficHearing h = TrafficVoiceMap.Hear(At(new Vec3(x, 0, 0)), bare, 0, t);
                Assert.Equal(h.StationNow, h.StationCould);
                Assert.Equal(h.PilotNow, h.PilotCould);
            }
        }

        [Fact]
        public void A_pilot_calls_snr_leaves_the_reflector_gain_to_the_client()
        {
            FactionTuning t = TestSupport.Tuning();
            TrafficEvent e = At(new Vec3(40, 0, 0));
            TrafficListener aimed = SeatedWithDish(new Vec3(1, 0, 0)), away = SeatedWithDish(new Vec3(-1, 0, 0));
            TrafficHearing ha = TrafficVoiceMap.Hear(e, aimed, 0, t), hw = TrafficVoiceMap.Hear(e, away, 0, t);
            WireTrafficPilotCall ca = TrafficVoiceMap.PilotCall(e, aimed, double.NegativeInfinity, ha.PilotOmniKm, t);
            WireTrafficPilotCall cw = TrafficVoiceMap.PilotCall(e, away, double.NegativeInfinity, hw.PilotOmniKm, t);
            Assert.Equal(cw.SnrDeciDb, ca.SnrDeciDb);
            Assert.Equal(40f, ca.Aim.SourceX);
            Assert.Equal((float)ha.PilotOmniKm, ca.Aim.OmniReachKm);
        }
    }
}
