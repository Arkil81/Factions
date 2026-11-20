using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The hold rule the host's observation loop reads: which arrived fleets keep a
    /// materialised grid on station instead of completing the leg, and until when.</summary>
    public class FleetHoldsStationTests
    {
        private static FactionTuning Tuning() { return TestSupport.Tuning(); }

        private static PendingFleetAction Fleet(ActionKind kind, DateTime arrival, bool resolved)
        {
            return new PendingFleetAction
            {
                Kind = kind,
                ActorTag = "MERC",
                TargetTag = "NONE",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = arrival.AddMinutes(-1),
                EstimatedArrivalUtc = arrival,
                Resolved = resolved,
                Intercepted = false,
                LastResolvedSegment = -1,
            };
        }

        [Fact]
        public void GuardHoldsStationForItsDutyWindowAfterArrival()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction guard = Fleet(ActionKind.Guard, arrival, resolved: true);

            Assert.True(guard.HoldsStation(arrival.AddSeconds(1), t));
            Assert.True(guard.HoldsStation(arrival.Add(t.GuardDuration).AddSeconds(-1), t));
            Assert.Equal(arrival.Add(t.GuardDuration), guard.HoldEndsUtc(t));
        }

        [Fact]
        public void GuardHoldEndsWithTheDutyWindow()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction guard = Fleet(ActionKind.Guard, arrival, resolved: true);

            Assert.False(guard.HoldsStation(arrival.Add(t.GuardDuration), t));
        }

        [Fact]
        public void GuardInTransitDoesNotHoldYet()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction guard = Fleet(ActionKind.Guard, arrival, resolved: false);

            Assert.False(guard.HoldsStation(arrival.AddSeconds(-30), t));
        }

        [Fact]
        public void InterceptedGuardDoesNotHold()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction guard = Fleet(ActionKind.Guard, arrival, resolved: true);
            guard.Intercepted = true;

            Assert.False(guard.HoldsStation(arrival.AddSeconds(1), t));
        }

        [Fact]
        public void EngageAndInterceptDwellsHoldUntilTheLegArrives()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction raid = Fleet(ActionKind.Raid, arrival, resolved: false);
            raid.CurrentStopKind = FleetStopKind.Engage;
            PendingFleetAction convoy = Fleet(ActionKind.Trade, arrival, resolved: false);
            convoy.CurrentStopKind = FleetStopKind.Intercept;

            Assert.True(raid.HoldsStation(arrival.AddSeconds(-60), t));
            Assert.True(convoy.HoldsStation(arrival.AddSeconds(-60), t));
            Assert.Equal(arrival, raid.HoldEndsUtc(t));
            Assert.Equal(arrival, convoy.HoldEndsUtc(t));
        }

        [Fact]
        public void AnArrivedTradeOrPatrolDoesNotHold()
        {
            FactionTuning t = Tuning();
            DateTime arrival = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            PendingFleetAction trade = Fleet(ActionKind.Trade, arrival, resolved: true);
            PendingFleetAction patrol = Fleet(ActionKind.Patrol, arrival, resolved: true);

            Assert.False(trade.HoldsStation(arrival.AddSeconds(1), t));
            Assert.False(patrol.HoldsStation(arrival.AddSeconds(1), t));
        }
    }
}
