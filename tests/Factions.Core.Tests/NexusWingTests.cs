using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A wing command given on a Nexus agent: the call and its answer survive the wire, and
    /// the authority serves what the agent read only while it keeps being refreshed, and only to the
    /// commander it was read for.</summary>
    public class NexusWingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private static NexusWingCall Call(long antenna, long commander, long target)
        {
            var call = new NexusWingCall
            {
                Id = 5, Command = (int)WingCommand.FocusTarget, Via = (int)WingVia.Client, Steam = 7UL,
                Identity = commander, Tag = "EXP", Antenna = antenna, PresserLock = target,
                Targets = new List<NexusWingTarget>
                {
                    NexusWingTarget.From(new WingTargetReading { EntityId = target, Exists = true, Hostile = true, PosM = new Vec3(10, 20, 30) }),
                },
            };
            call.SetAntenna(new WingAntennaReading
            {
                Exists = true, Functional = true, Enabled = true, Powered = true, Broadcasting = true,
                RadiusM = 5000, OwnerIdentityId = commander, OwnerFactionTag = "EXP", PosM = new Vec3(1, 2, 3),
            }, 900);
            return call;
        }

        [Fact]
        public void ACallAndItsAnswerSurviveTheWire()
        {
            NexusWingCall back;
            Assert.True(NexusWingCall.TryDecode(Call(100, 42, 300).Encode(), out back));
            Assert.Equal((int)WingCommand.FocusTarget, back.Command);
            Assert.Equal("EXP", back.Tag);
            Assert.Equal(900, back.AntennaGrid);
            WingAntennaReading a = back.AntennaReading();
            Assert.True(a.Broadcasting);
            Assert.Equal(5000, a.RadiusM);
            Assert.Equal(42, a.OwnerIdentityId);
            Assert.Equal(3, a.PosM.Z);
            WingTargetReading t = Assert.Single(back.Targets).ToReading();
            Assert.Equal(300, t.EntityId);
            Assert.True(t.Hostile);
            Assert.Equal(20, t.PosM.Y);

            var r = new NexusWingResult { Id = 5, Ok = true, Message = "Wing: 3 hull(s) attacking your target.", Antenna = 100, Identity = 42 };
            NexusWingResult rb;
            Assert.True(NexusWingResult.TryDecode(r.Encode(), out rb));
            Assert.True(rb.Ok);
            Assert.False(rb.Ended);
            Assert.Equal(42, rb.Identity);
        }

        [Fact]
        public void AReadingIsServedOnlyWhileFresh()
        {
            var readings = new NexusWingReadings();
            readings.Put(Call(100, 42, 300), T0);
            WingAntennaReading a;
            long grid;
            Assert.True(readings.TryAntenna(100, T0.AddSeconds(4), out a, out grid));
            Assert.Equal(900, grid);
            Assert.False(readings.TryAntenna(100, T0.AddSeconds(6), out a, out grid));
            Assert.False(a.Exists);

            // A refresh renews it.
            readings.Put(Call(100, 42, 300), T0.AddSeconds(6));
            Assert.True(readings.TryAntenna(100, T0.AddSeconds(10), out a, out grid));
        }

        [Fact]
        public void ATargetIsReadForItsCommanderOnly()
        {
            var readings = new NexusWingReadings();
            readings.Put(Call(100, 42, 300), T0);
            WingTargetReading t;
            Assert.True(readings.TryTarget(300, 42, T0, out t));
            Assert.True(t.Hostile);
            Assert.False(readings.TryTarget(300, 43, T0, out t));
            Assert.False(readings.TryTarget(301, 42, T0, out t));
        }

        [Fact]
        public void PruneDropsWhatIsNoLongerServed()
        {
            var readings = new NexusWingReadings();
            readings.Put(Call(100, 42, 300), T0);
            readings.Put(Call(101, 43, 301), T0.AddSeconds(4));
            readings.Prune(T0.AddSeconds(6));
            Assert.Equal(1, readings.AntennaCount);
            WingTargetReading t;
            Assert.False(readings.TryTarget(300, 42, T0.AddSeconds(6), out t));
            Assert.True(readings.TryTarget(301, 43, T0.AddSeconds(6), out t));
        }

        [Fact]
        public void AStationsCallServesItsGridWhileFresh()
        {
            NexusWingCall call = Call(100, 42, 300);
            call.Command = (int)WingCommand.ProtectMe;
            call.Station = true;
            call.SStatic = true;
            call.SCombat = 300;
            call.SHostileNear = 301;
            call.SAntennas = new List<NexusWingAntenna>
            {
                NexusWingAntenna.From(101, new WingAntennaReading { Exists = true, Broadcasting = true, RadiusM = 2000, OwnerIdentityId = 42 }),
            };
            call.STargets = new List<NexusWingTarget>
            {
                NexusWingTarget.From(new WingTargetReading { EntityId = 301, Exists = true, Hostile = true }),
            };
            NexusWingCall back;
            Assert.True(NexusWingCall.TryDecode(call.Encode(), out back));

            var readings = new NexusWingReadings();
            readings.Put(back, T0);
            NexusWingReadings.StationGrid station;
            Assert.True(readings.TryStation(900, T0.AddSeconds(4), out station));
            Assert.True(station.Static);
            Assert.Equal(300, station.Combat);
            Assert.Equal(301, station.HostileNear);
            Assert.Contains(100L, station.Antennas);
            Assert.Contains(101L, station.Antennas);
            WingAntennaReading a;
            long grid;
            Assert.True(readings.TryAntenna(101, T0.AddSeconds(4), out a, out grid));
            Assert.Equal(900, grid);
            WingTargetReading t;
            Assert.True(readings.TryTarget(301, 42, T0.AddSeconds(4), out t));
            Assert.True(t.Hostile);

            Assert.False(readings.TryStation(900, T0.AddSeconds(6), out station));
            readings.Prune(T0.AddSeconds(6));
            Assert.Equal(0, readings.StationCount);
        }
    }
}
