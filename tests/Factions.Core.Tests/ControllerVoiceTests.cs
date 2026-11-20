using System.Collections.Generic;
using System.Linq;
using Factions;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 voice: a faction's stations and hails speak in one recorded voice of five, assigned so
    /// all five are heard, carried on the traffic callout, and a hail travels as a callout to the one
    /// pilot it addresses.
    /// </summary>
    public sealed class ControllerVoiceTests
    {
        private static readonly string[] LiveTags =
            { "SPRT", "FCTM", "UNKN", "IMPR", "MEMN", "METC", "IGSL", "UVCO", "TFEG", "TFCS", "STCG", "ISBT", "UNGD", "EXP" };

        private static List<FactionState> Roster(params string[] tags)
        {
            return tags.Select(t => new FactionState { Tag = t, RngSeed = 1 }).ToList();
        }

        [Fact]
        public void TheLiveRoster_HearsEveryVoice_AndNoVoiceTakesMoreThanItsShare()
        {
            List<FactionState> roster = Roster(LiveTags);
            Assert.Equal(14, ControllerVoice.Assign(roster));

            int[] used = new int[ControllerVoice.Speakers];
            foreach (FactionState s in roster) used[s.ControllerSpeaker]++;
            Assert.All(used, n => Assert.InRange(n, 2, 3));
            Assert.Equal(0, roster.Single(s => s.Tag == "EXP").ControllerSpeaker);   // first in tag order
        }

        [Fact]
        public void AnAssignedVoice_IsKept_AndANewFactionTakesTheLeastUsed()
        {
            List<FactionState> roster = Roster("AAA", "BBB", "CCC");
            roster[0].ControllerSpeaker = 4;
            ControllerVoice.Assign(roster);
            Assert.Equal(4, roster[0].ControllerSpeaker);
            Assert.Equal(0, roster[1].ControllerSpeaker);
            Assert.Equal(1, roster[2].ControllerSpeaker);

            roster.Add(new FactionState { Tag = "ZZZ", RngSeed = 1 });
            Assert.Equal(2, ControllerVoice.SpeakerOf(roster, "zzz"));
            Assert.Equal(0, ControllerVoice.SpeakerOf(roster, "NOPE"));
            Assert.Equal(4, roster[0].ControllerSpeaker);
        }

        [Fact]
        public void TheVoice_SurvivesTheBrainFile_AndAnUnassignedOneIsOmitted()
        {
            var graph = new SpatialGraph();
            var s = new FactionState { Tag = "METC", RngSeed = 1, ControllerSpeaker = 3 };
            BrainDoc doc = FactionStore.ToBrainDoc(s, new SpatialNode[0], 10);
            Assert.Equal(3, doc.ControllerSpeaker);
            Assert.Equal(3, FactionStore.ApplyBrainDoc(doc, graph).ControllerSpeaker);

            BrainDoc none = FactionStore.ToBrainDoc(new FactionState { Tag = "EXP", RngSeed = 1 }, new SpatialNode[0], 10);
            Assert.Null(none.ControllerSpeaker);
            Assert.Equal(-1, FactionStore.ApplyBrainDoc(none, graph).ControllerSpeaker);
        }

        [Fact]
        public void TheSpeakerAndTheHailEvents_RoundTripOnTheCallout()
        {
            foreach (WireTrafficEvent ev in new[] { WireTrafficEvent.Arrive, WireTrafficEvent.HailWelcome,
                         WireTrafficEvent.HailNotice, WireTrafficEvent.HailWarning, WireTrafficEvent.HailIntercept,
                         WireTrafficEvent.HailOccupation, WireTrafficEvent.HailOccupationHeld })
            {
                var c = new WireTrafficCallout
                {
                    Event = ev, Voice = WireTrafficVoice.Hostile, ActorTag = "METC", NodeId = "N093", Speaker = 3,
                };
                WireTrafficCallout back;
                Assert.True(FactionsWire.TryReadTrafficCallout(FactionsWire.WriteTrafficCallout(c), out back));
                Assert.Equal(ev, back.Event);
                Assert.Equal(3, back.Speaker);
                Assert.Equal(FactionsWire.IsHail(ev), ev != WireTrafficEvent.Arrive);
            }
        }

        [Fact]
        public void AHail_IsAlwaysHeard_FaintAtTheEdgeWhenThePilotIsFarOff()
        {
            FactionTuning t = TestSupport.Tuning();
            var node = new SpatialNode { Id = "N093", Position = new Vec3(0, 0, 0) };
            var radio = new RadioReceiver { Seated = true };
            double reach = RadioPath.StationRangeKm(radio, t);

            var near = new TrafficListener { SteamId = 7, PosKm = new Vec3(reach * 0.1, 0, 0), Radio = radio };
            WireTrafficCallout close = TrafficVoiceMap.Hail(WireTrafficEvent.HailWarning, "METC", 2, node, "Wharf", near, t);
            Assert.Equal(WireTrafficEvent.HailWarning, close.Event);
            Assert.Equal(WireTrafficVoice.Hostile, close.Voice);
            Assert.Equal("METC", close.ActorTag);
            Assert.Equal(2, close.Speaker);
            Assert.Equal((float)reach, close.Aim.OmniReachKm, 3);
            Assert.True(close.DistanceShare < 64);

            var far = new TrafficListener { SteamId = 7, PosKm = new Vec3(reach * 5, 0, 0), Radio = radio };
            WireTrafficCallout faint = TrafficVoiceMap.Hail(WireTrafficEvent.HailWelcome, "METC", 2, node, "Wharf", far, t);
            Assert.Equal(WireTrafficVoice.Patrol, faint.Voice);
            Assert.True(faint.Aim.OmniReachKm > reach * 5, "a hail past the radio's reach must still be in reach");
            Assert.Equal(TrafficVoiceMap.Share(reach * 5, reach * 5 / TrafficVoiceMap.HailEdgeShare), faint.DistanceShare);
            Assert.True(faint.DistanceShare > 200, "and heard at the edge, where the static is heaviest");
        }
    }
}
