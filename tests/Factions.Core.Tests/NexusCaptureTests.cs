using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A capture made on a Nexus agent and registered on the authority: the upload and its
    /// answer survive the wire, an answer is taken once, and an unanswered scan lapses.</summary>
    public class NexusCaptureTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AnUploadAndItsAnswerSurviveTheWire()
        {
            string xml = "<Definitions>" + new string('x', 200000) + "</Definitions>";
            var up = new NexusCaptureUpload
            {
                Id = 42, Kind = (int)NexusCaptureKind.Station, Surface = (int)NexusCaptureSurface.TerminalStation,
                Steam = 76561198000000001UL, Identity = 1234, Owner = "EXP", Scope = "own", Name = "Kestrel",
                Blocks = 812, Px = 1.5, Py = -2, Pz = 3, AdoptNode = "N7", GridsXml = xml,
            };
            byte[] frame = up.Encode();
            Assert.True(frame.Length < xml.Length / 10);
            NexusCaptureUpload back;
            Assert.True(NexusCaptureUpload.TryDecode(frame, out back));
            Assert.Equal(42, back.Id);
            Assert.Equal(NexusCaptureKind.Station, (NexusCaptureKind)back.Kind);
            Assert.Equal(76561198000000001UL, back.Steam);
            Assert.Equal("N7", back.AdoptNode);
            Assert.Equal(812, back.Blocks);
            Assert.Equal(xml, back.GridsXml);

            var r = new NexusCaptureResult { Id = 42, Ok = true, Message = "Scanned station 'Kestrel'", Node = "N7" };
            NexusCaptureResult rb;
            Assert.True(NexusCaptureResult.TryDecode(r.Encode(), out rb));
            Assert.True(rb.Ok);
            Assert.Equal("N7", rb.Node);
        }

        [Fact]
        public void ADesignRegistrationCarriesItsKeyAndFee()
        {
            var up = new NexusCaptureUpload
            {
                Id = -T0.Ticks, Kind = (int)NexusCaptureKind.Design, Surface = (int)NexusCaptureSurface.TerminalShip,
                Steam = 7UL, Identity = 99, Owner = "IMPR", AdoptNode = "N3", EntityId = 144115188075855873L,
                Fingerprint = "a1b2c3", Fee = 25000, DisplayName = "Wren", GridsXml = "<Definitions/>",
            };
            NexusCaptureUpload back;
            Assert.True(NexusCaptureUpload.TryDecode(up.Encode(), out back));
            Assert.Equal(NexusCaptureKind.Design, (NexusCaptureKind)back.Kind);
            Assert.Equal(144115188075855873L, back.EntityId);
            Assert.Equal("a1b2c3", back.Fingerprint);
            Assert.Equal(25000, back.Fee);
            Assert.Equal("N3", back.AdoptNode);
            Assert.True(back.Id < 0);

            // Its answer matches no pending scan: scan ids are positive.
            var pending = new NexusCapturePending<string>();
            Assert.True(pending.Add("scan", T0) > 0);
            Assert.Null(pending.Take(back.Id));
        }

        [Fact]
        public void AnAnswerIsTakenOnce()
        {
            var p = new NexusCapturePending<string>();
            long a = p.Add("a", T0), b = p.Add("b", T0);
            Assert.NotEqual(a, b);
            Assert.Equal("a", p.Take(a));
            Assert.Null(p.Take(a));
            Assert.Null(p.Take(999));
            Assert.Equal(1, p.Count);
        }

        [Fact]
        public void AnUnansweredScanLapsesOnce()
        {
            var p = new NexusCapturePending<string>();
            long a = p.Add("a", T0);
            Assert.Null(p.Lapse(T0 + NexusCapturePending<string>.Timeout));
            List<string> lapsed = p.Lapse(T0 + NexusCapturePending<string>.Timeout + TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { "a" }, lapsed);
            Assert.Null(p.Take(a));
            Assert.Null(p.Lapse(T0.AddMinutes(5)));
        }

        [Fact]
        public void TheSnapshotCarriesTheCaptureGeneration()
        {
            NexusNodeSnapshot s = NexusNodeSnapshot.Build(null, new List<FactionState>(), 1);
            s.Captures = 3;
            NexusNodeSnapshot back;
            Assert.True(NexusNodeSnapshot.TryDecode(s.Encode(), out back));
            Assert.Equal(3, back.Captures);
        }
    }
}
