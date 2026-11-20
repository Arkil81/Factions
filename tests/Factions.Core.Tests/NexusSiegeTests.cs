using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the siege half of the Nexus station path: fire reported from an agent is applied once
    /// however often it is re-sent, a restarted agent starts its count again, and a zone is sent to
    /// the agent only while it is off its default state.
    /// </summary>
    public class NexusSiegeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        private static NexusSiegeEvent E(long seq, double zone = 10)
        {
            return new NexusSiegeEvent { Seq = seq, Node = "N1", Attacker = 9, Zone = zone };
        }

        [Fact]
        public void ReSentEvents_AreAppliedOnce_AndAckedThroughTheNewest()
        {
            var inbox = new NexusSiegeInbox();
            long through;

            List<NexusSiegeEvent> first = inbox.Accept(4, "s1", new[] { E(1), E(2) }, out through);
            Assert.Equal(2, first.Count);
            Assert.Equal(2, through);

            List<NexusSiegeEvent> again = inbox.Accept(4, "s1", new[] { E(1), E(2), E(3) }, out through);
            Assert.Single(again);
            Assert.Equal(3, again[0].Seq);
            Assert.Equal(3, through);
        }

        [Fact]
        public void ANewSession_StartsTheCountAgain_AndHomesAreSeparate()
        {
            var inbox = new NexusSiegeInbox();
            long through;
            inbox.Accept(4, "s1", new[] { E(1), E(2) }, out through);

            Assert.Single(inbox.Accept(4, "s2", new[] { E(1) }, out through));
            Assert.Single(inbox.Accept(2, "s9", new[] { E(1) }, out through));
        }

        [Fact]
        public void NoEvents_AcksNothing()
        {
            var inbox = new NexusSiegeInbox();
            long through;
            inbox.Accept(4, "s1", new[] { E(5) }, out through);

            Assert.Empty(inbox.Accept(4, "s1", null, out through));
            Assert.Equal(0, through);
        }

        [Fact]
        public void ZoneState_IsSentOnlyOffTheDefault()
        {
            var t = new FactionTuning { RaidZoneIntegrityPool = 1000.0, RaidZoneIntegrityRegenSeconds = 30.0,
                                        RaidZoneRaidLapseSeconds = 30.0, RaidZoneSiegeDwellSeconds = 300.0 };
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "DEF", ZoneIntegrity = -1, StableStationId = 77 };

            Assert.Null(NexusZoneState.For(n, t, T0));

            PlayerSiege.AbsorbZoneDamage(n, t, T0, 400, "EXP");
            NexusZoneState hit = NexusZoneState.For(n, t, T0);
            Assert.False(hit.Off);
            Assert.Equal(0.6, hit.Fraction, 3);

            n.ZoneBreachedUtc = T0;
            Assert.True(NexusZoneState.For(n, t, T0).Off);
        }

        [Fact]
        public void Reply_CarriesZonesNoticesAndTheAck()
        {
            var reply = new NexusStationReply
            {
                SiegeThrough = 7,
                Zones = new List<NexusZoneState> { new NexusZoneState { Node = "N1", Off = true } },
                Notices = new List<NexusNotice> { new NexusNotice { Identity = 9, Text = "x", HudMs = 5200, Chat = true } },
            };
            NexusStationReply back;
            Assert.False(reply.IsEmpty);
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
            Assert.Equal(7, back.SiegeThrough);
            Assert.True(back.Zones[0].Off);
            Assert.Equal(5200, back.Notices[0].HudMs);
        }

        [Fact]
        public void Notices_AreHeldUntilShown_AndExpireUnsent()
        {
            var box = new NexusNoticeOutbox();
            TimeSpan hold = TimeSpan.FromSeconds(30);
            box.Add(4, new NexusNotice { Identity = 9, Text = "began" }, T0);
            box.Add(4, new NexusNotice { Identity = 9, Text = "breached" }, T0.AddSeconds(1));

            List<NexusNotice> first = box.Pending(4, "s1", 0, T0.AddSeconds(1), hold);
            Assert.Equal(2, first.Count);
            Assert.True(first[1].Seq > first[0].Seq);

            List<NexusNotice> after = box.Pending(4, "s1", first[0].Seq, T0.AddSeconds(2), hold);
            Assert.Single(after);
            Assert.Equal("breached", after[0].Text);

            Assert.Null(box.Pending(4, "s1", 0, T0.AddSeconds(40), hold));
            Assert.Null(box.Pending(2, "s1", 0, T0, hold));
        }

        [Fact]
        public void ANewAgentSession_DropsWhatWasHeldForTheOldOne()
        {
            var box = new NexusNoticeOutbox();
            TimeSpan hold = TimeSpan.FromSeconds(30);
            box.Add(4, new NexusNotice { Identity = 9, Text = "began" }, T0);
            Assert.Single(box.Pending(4, "s1", 0, T0, hold));

            Assert.Null(box.Pending(4, "s2", 0, T0.AddSeconds(1), hold));
        }

        [Fact]
        public void AStationRecord_AndTheObservedSet_SurviveTheCodec()
        {
            var snapshot = new NexusNodeSnapshot { Sequence = 3 };
            snapshot.Nodes.Add(new NexusSnapshotNode
            {
                Id = "N1", Station = 77, Owner = "DEF",
                Record = new NexusStationRecord { Se = "Outpost_A", Type = 2, Name = "Kettle", X = 1, Ux = 1, Fz = -1 },
            });
            NexusNodeSnapshot back;
            Assert.True(NexusNodeSnapshot.TryDecode(snapshot.Encode(), out back));
            Assert.Equal("Outpost_A", back.Nodes[0].Record.Se);
            Assert.Equal(2, back.Nodes[0].Record.Type);
            Assert.Equal(-1, back.Nodes[0].Record.Fz);

            var report = new NexusAgentReport { Session = "s1", Observed = new List<string> { "N1" } };
            NexusAgentReport r;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out r));
            Assert.Equal("N1", r.Observed[0]);
        }

        [Fact]
        public void AnAnswer_RidesTheEventStream_AndSurvivesTheCodec()
        {
            var report = new NexusAgentReport
            {
                Session = "s1",
                Siege = new List<NexusSiegeEvent>
                {
                    new NexusSiegeEvent { Seq = 4, Attacker = 9, Answer = NexusAnswer.Yes },
                },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Equal(NexusAnswer.Yes, back.Siege[0].Answer);
            Assert.Null(back.Siege[0].Node);

            var inbox = new NexusSiegeInbox();
            long through;
            Assert.Single(inbox.Accept(4, "s1", back.Siege, out through));
            Assert.Empty(inbox.Accept(4, "s1", back.Siege, out through));
        }
    }
}
