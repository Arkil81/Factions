using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-539: a player can catch a cruising fleet. It brakes gradually as it nears them, and
    /// a spawn inside the force radius goes to the head of the spawn queue.</summary>
    public class ChaseSpawnTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        private const byte Main = 2;

        /// <summary>Production's speeds: 500 km/min cruise, the world's 30 km/min ship limit, and the
        /// brake's 25 km/min floor under it.</summary>
        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                FleetSpeedKmPerMin = 500, FleetSpeedMinKmPerMin = 30, FleetAccelKmPerMin2 = 4608,
                ObservationRadiusKm = 15, ChaseBrakeStartKm = 100, ChaseBrakeFloorKmPerMin = 25, ForceSpawnRadiusKm = 3,
            };
        }

        [Fact]
        public void TheCapIsTheFloorInsideTheObservationRadius_AndOffPastTheBrakeStart()
        {
            FactionTuning t = Tuning();
            Assert.Equal(25, ChaseBrake.SpeedCapKmPerMin(0, t), 6);
            Assert.Equal(25, ChaseBrake.SpeedCapKmPerMin(15, t), 6);
            Assert.True(double.IsPositiveInfinity(ChaseBrake.SpeedCapKmPerMin(100, t)));
            Assert.True(double.IsPositiveInfinity(ChaseBrake.SpeedCapKmPerMin(double.PositiveInfinity, t)));
            // Constant deceleration: v^2 is linear in distance, so halfway it is the mean of the squares.
            Assert.Equal(Math.Sqrt((25 * 25 + 500 * 500) / 2.0), ChaseBrake.SpeedCapKmPerMin(57.5, t), 6);

            // No floor of its own: the world's ship limit.
            t.ChaseBrakeFloorKmPerMin = 0;
            Assert.Equal(30, ChaseBrake.SpeedCapKmPerMin(15, t), 6);

            t.ChaseBrakeStartKm = 0;
            Assert.True(double.IsPositiveInfinity(ChaseBrake.SpeedCapKmPerMin(5, t)));
        }

        [Fact]
        public void TheFreezeLeavesTheFleetMovingAtTheCap()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(940), ChaseBrake.FreezeFor(500, 30, TimeSpan.FromSeconds(1)));
            Assert.Equal(TimeSpan.Zero, ChaseBrake.FreezeFor(20, 30, TimeSpan.FromSeconds(1)));
            Assert.Equal(TimeSpan.Zero, ChaseBrake.FreezeFor(500, double.PositiveInfinity, TimeSpan.FromSeconds(1)));
        }

        /// <summary>The production case: a haul cruising past a player 5 km off its line. Unbraked it is
        /// inside 15 km for 3.4 s, less than a spawn waits in the pacer. Braked it slows smoothly, stays
        /// inside for close to a minute, and speeds up again once past.</summary>
        [Fact]
        public void ACruisingFleetBrakesGraduallyPastAPlayer_AndStaysInRangeLongEnoughToSpawn()
        {
            FactionTuning t = Tuning();
            var fleet = new PendingFleetAction
            {
                FleetId = "f1", OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(2000, 0, 0),
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddMinutes(4.1),
            };
            DateTime originalEta = fleet.EstimatedArrivalUtc;
            var player = new Vec3(1000, 5, 0);
            TimeSpan dt = TimeSpan.FromMilliseconds(100);

            double inRangeSeconds = 0, maxDropPerFrame = 0, speedAt50Km = double.NaN, prevSpeed = double.NaN;
            double minSpeedPastPlayer = double.PositiveInfinity, lastSpeed = 0;
            Vec3 prevPos = fleet.CurrentPosKm(T0, t);
            for (DateTime now = T0 + dt; now < T0.AddMinutes(10); now += dt)
            {
                Vec3 pos = fleet.CurrentPosKm(now, t);
                double cap = ChaseBrake.SpeedCapKmPerMin(pos.DistanceTo(player), t);
                TimeSpan freeze = ChaseBrake.FreezeFor(ChaseBrake.NominalSpeedKmPerMin(fleet, now, t), cap, dt);
                if (freeze > TimeSpan.Zero)
                {
                    fleet = FactionsLoadActuator.Freeze(fleet, freeze);
                    pos = fleet.CurrentPosKm(now, t);
                }

                double speed = prevPos.DistanceTo(pos) / dt.TotalMinutes;
                double dist = pos.DistanceTo(player);
                if (dist <= 15) inRangeSeconds += dt.TotalSeconds;
                if (double.IsNaN(speedAt50Km) && dist <= 50 && pos.X < player.X) speedAt50Km = speed;
                if (!double.IsNaN(prevSpeed) && pos.X < player.X) maxDropPerFrame = Math.Max(maxDropPerFrame, prevSpeed - speed);
                if (pos.X > player.X && dist <= 15) minSpeedPastPlayer = Math.Min(minSpeedPastPlayer, speed);
                prevSpeed = speed; prevPos = pos; lastSpeed = speed;
                if (pos.X > 1300) break;
            }

            Assert.InRange(inRangeSeconds, 60, 80);
            // Halfway down the curve at 50 km: sqrt(25^2 + 2a*35), a = (500^2-25^2)/(2*85).
            Assert.InRange(speedAt50Km, 300, 345);
            // The steepest step between 0.1 s frames is the deceleration's, about 2.4 km/min: no jump.
            Assert.True(maxDropPerFrame < 6, "largest drop " + maxDropPerFrame);
            Assert.True(minSpeedPastPlayer >= 20, "past the player " + minSpeedPastPlayer);
            Assert.True(lastSpeed > 400, "leaving at " + lastSpeed);
            Assert.True(fleet.EstimatedArrivalUtc > originalEta.AddSeconds(45));
        }

        [Fact]
        public void APrioritisedSpawnGoesAheadOfTheQueue_BehindAnEarlierOne_AndStillWaitsForTheGap()
        {
            var p = new SpawnPacer(10.0);
            double waited;
            Assert.True(p.TryBegin("a", T0, out waited));
            Assert.False(p.TryBegin("b", T0.AddSeconds(1), out waited));
            Assert.False(p.TryBegin("c", T0.AddSeconds(1), out waited));
            Assert.False(p.TryBegin("d", T0.AddSeconds(1), out waited));

            p.Prioritise("c", T0.AddSeconds(2));
            p.Prioritise("d", T0.AddSeconds(2));
            p.Prioritise("c", T0.AddSeconds(3));   // again: stays where it is
            Assert.Equal(2, p.Prioritised);

            // Still behind the spawn in flight and the gap after it.
            Assert.False(p.TryBegin("c", T0.AddSeconds(3), out waited));
            p.Landed("a", T0.AddSeconds(4));
            Assert.False(p.TryBegin("c", T0.AddSeconds(8), out waited));
            Assert.False(p.TryBegin("b", T0.AddSeconds(14.1), out waited));
            Assert.False(p.TryBegin("d", T0.AddSeconds(14.1), out waited));
            Assert.True(p.TryBegin("c", T0.AddSeconds(14.1), out waited));
            p.Landed("c", T0.AddSeconds(15));
            Assert.False(p.TryBegin("d", T0.AddSeconds(20), out waited));   // still polling, so not dropped as gone
            Assert.False(p.TryBegin("b", T0.AddSeconds(25.1), out waited));
            Assert.True(p.TryBegin("d", T0.AddSeconds(25.1), out waited));
        }

        [Fact]
        public void PrioritisingTheSpawnInFlightOrAWithdrawnOne_DoesNotQueueIt()
        {
            var p = new SpawnPacer(1.0);
            double waited;
            Assert.True(p.TryBegin("a", T0, out waited));
            p.Prioritise("a", T0);
            Assert.False(p.IsWaiting("a"));

            Assert.False(p.TryBegin("b", T0, out waited));
            p.Prioritise("b", T0);
            Assert.True(p.Withdraw("b"));
            Assert.False(p.IsWaiting("b"));
            Assert.Equal(0, p.QueueLength);
        }

        [Fact]
        public void TheRegistryMarksAWaitingRemoteSpawnOnce_AsARespotWhereItStands()
        {
            var r = new NexusFleetRegistry();
            r.Apply(Main, "s1", true, null, null, null, 0, T0);
            r.Spawn(Main, new NexusFleetOrder { Key = "k1", Kind = (int)ActionKind.Haul, Tag = "SPRT", Prefab = "p", X = 100, Tx = 150 }, T0);

            Assert.True(r.Prioritise("k1", T0.AddSeconds(1)));
            Assert.False(r.Prioritise("k1", T0.AddSeconds(2)));
            Assert.False(r.Prioritise("nobody", T0.AddSeconds(2)));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Main, T0.AddSeconds(2), out orders, out asks, out held);
            Assert.Equal(new[] { NexusFleetOp.Spawn, NexusFleetOp.Respot }, orders.ConvertAll(o => o.Op).ToArray());
            Assert.True(orders[1].Priority);
            Assert.Equal(100, orders[1].X);
            Assert.Equal(150, orders[1].Tx);
        }

        [Fact]
        public void PriorityRoundTripsOnTheWire_AndAnOlderOrderReadsAsOrdinary()
        {
            var o = new NexusFleetOrder { Op = NexusFleetOp.Respot, Key = "k1", Priority = true };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(o);
            Assert.True(Newtonsoft.Json.JsonConvert.DeserializeObject<NexusFleetOrder>(json).Priority);
            Assert.False(Newtonsoft.Json.JsonConvert.DeserializeObject<NexusFleetOrder>("{\"Op\":11,\"Key\":\"k1\"}").Priority);
        }
    }
}
