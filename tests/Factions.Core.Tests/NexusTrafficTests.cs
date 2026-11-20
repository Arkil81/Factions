using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Station traffic carried from the authority to an agent's players.</summary>
    public class NexusTrafficTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        private static TrafficEvent Event(string node = "n1")
        {
            return new TrafficEvent
            {
                Kind = TrafficEventKind.Arrive, NodeId = node, NodePosKm = new Vec3(1, 2, 3), ActorTag = "ABC",
                FleetKind = ActionKind.Haul, StopKind = FleetStopKind.Pickup, FleetKey = "k", FleetId = "ABC-7",
                QueuePosition = 2, Departure = true, DivertNodeId = "n2", DivertNodeName = "Two", Utc = T0,
                PilotPosKm = new Vec3(4, 5, 6), PilotVelMs = new Vec3(7, 8, 9), PilotSignature = 12.5, PilotEntityId = 99,
            };
        }

        [Fact]
        public void ACallCarriesTheWholeEventThroughTheReply()
        {
            var reply = new NexusStationReply();
            reply.Traffic = new List<NexusTrafficCall> { NexusTrafficCall.From(Event(), "One", "OWN", 3) };
            Assert.False(reply.IsEmpty);
            NexusStationReply back;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
            Assert.Single(back.Traffic);
            NexusTrafficCall c = back.Traffic[0];
            Assert.Equal("One", c.NodeName);
            Assert.Equal("OWN", c.Owner);
            Assert.Equal(3, c.Speaker);
            TrafficEvent e = c.ToEvent();
            Assert.Equal(TrafficEventKind.Arrive, e.Kind);
            Assert.Equal("n1", e.NodeId);
            Assert.Equal(3.0, e.NodePosKm.Z, 6);
            Assert.Equal(ActionKind.Haul, e.FleetKind);
            Assert.Equal(FleetStopKind.Pickup, e.StopKind);
            Assert.Equal("ABC-7", e.FleetId);
            Assert.Equal(2, e.QueuePosition);
            Assert.True(e.Departure);
            Assert.Equal("Two", e.DivertNodeName);
            Assert.Equal(6.0, e.PilotPosKm.Z, 6);
            Assert.Equal(9.0, e.PilotVelMs.Z, 6);
            Assert.Equal(12.5, e.PilotSignature, 6);
            Assert.Equal(99, e.PilotEntityId);
        }

        [Fact]
        public void EachAgentIsHandedEachCallOnce()
        {
            var relay = new NexusTrafficRelay();
            relay.Add(NexusTrafficCall.From(Event("a"), null, null, 0), T0);
            relay.Add(NexusTrafficCall.From(Event("b"), null, null, 0), T0);
            Assert.Equal(2, relay.Take(2, T0).Count);
            Assert.Null(relay.Take(2, T0.AddSeconds(1)));
            Assert.Equal(2, relay.Take(4, T0.AddSeconds(1)).Count);
            relay.Add(NexusTrafficCall.From(Event("c"), null, null, 0), T0.AddSeconds(2));
            List<NexusTrafficCall> next = relay.Take(2, T0.AddSeconds(2));
            Assert.Single(next);
            Assert.Equal("c", next[0].Node);
        }

        /// <summary>A call heard late is wrong, not late: an agent that has not taken any for a while
        /// gets only what is still fresh.</summary>
        [Fact]
        public void AStaleCallIsNeverHanded()
        {
            var relay = new NexusTrafficRelay();
            relay.Add(NexusTrafficCall.From(Event("old"), null, null, 0), T0);
            relay.Add(NexusTrafficCall.From(Event("new"), null, null, 0), T0.AddSeconds(4));
            List<NexusTrafficCall> got = relay.Take(2, T0.AddSeconds(6));
            Assert.Single(got);
            Assert.Equal("new", got[0].Node);
        }

        /// <summary>An agent with nobody connected is skipped, so a player joining later does not
        /// hear the calls made before they were there.</summary>
        [Fact]
        public void ASkippedAgentIsNotHandedWhatCameBefore()
        {
            var relay = new NexusTrafficRelay();
            relay.Add(NexusTrafficCall.From(Event("a"), null, null, 0), T0);
            relay.Skip(2);
            Assert.Null(relay.Take(2, T0.AddSeconds(1)));
        }

        [Fact]
        public void TheQueueIsBounded()
        {
            var relay = new NexusTrafficRelay();
            for (int i = 0; i < NexusTrafficRelay.Capacity + 50; i++)
                relay.Add(NexusTrafficCall.From(Event("n" + i), null, null, 0), T0);
            Assert.Equal(NexusTrafficRelay.Capacity, relay.Count);
            List<NexusTrafficCall> got = relay.Take(2, T0);
            Assert.Equal("n50", got[0].Node);
        }

        /// <summary>A hail is for the one pilot it addresses: only the agent they are on is handed it.</summary>
        [Fact]
        public void AHailGoesOnlyToThePilotsAgent()
        {
            var relay = new NexusTrafficRelay();
            relay.Add(NexusTrafficCall.HailTo(77UL, 40, "ABC", 2, "n1", new Vec3(1, 2, 3), "One", T0), T0, 4);
            relay.Add(NexusTrafficCall.From(Event("all"), null, null, 0), T0);
            List<NexusTrafficCall> main = relay.Take(2, T0);
            Assert.Single(main);
            Assert.Equal("all", main[0].Node);
            List<NexusTrafficCall> day = relay.Take(4, T0);
            Assert.Equal(2, day.Count);
            Assert.Equal(77UL, day[0].To);
        }

        [Fact]
        public void AHailCarriesItsPilotAndNodeThroughTheReply()
        {
            var reply = new NexusStationReply
            {
                Traffic = new List<NexusTrafficCall> { NexusTrafficCall.HailTo(76561198000000001UL, 40, "ABC", 2, "n1", new Vec3(1, 2, 3), "One", T0) },
            };
            NexusStationReply back;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
            NexusTrafficCall c = back.Traffic[0];
            Assert.Equal(76561198000000001UL, c.To);
            Assert.Equal(40, c.Hail);
            Assert.Equal("ABC", c.Owner);
            Assert.Equal(2, c.Speaker);
            Assert.Equal("n1", c.Node);
            Assert.Equal("One", c.NodeName);
        }

        [Fact]
        public void TheReportCarriesDockedSeatsAndANoticeItsAuthor()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport { Session = "s", Docked = new List<long> { 11, 12 } }.Encode(), out back));
            Assert.Equal(new long[] { 11, 12 }, back.Docked.ToArray());
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport { Session = "s" }.Encode(), out back));
            Assert.Null(back.Docked);

            var reply = new NexusStationReply
            {
                Notices = new List<NexusNotice> { new NexusNotice { Seq = 1, Identity = 5, Text = "t", Chat = true, Offer = true, Author = "Kor control" } },
            };
            NexusStationReply r;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out r));
            Assert.Equal("Kor control", r.Notices[0].Author);
            Assert.True(r.Notices[0].Offer);
        }

        [Fact]
        public void AReplyWithoutTrafficCarriesNone()
        {
            NexusStationReply back;
            Assert.True(NexusStationReply.TryDecode(new NexusStationReply().Encode(), out back));
            Assert.Null(back.Traffic);
        }
    }
}
