using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A dwell leg is exactly stationary, and that exactness is load-bearing.
    ///
    /// <para>The map window identifies a raid that is fighting -- as opposed to one still flying at
    /// its target -- from the velocity the host samples for it, because the wire carries no field
    /// for the current leg. That works only because <c>AdvanceToNextStop</c> gives a dwell leg the
    /// same point for its origin and its target, so <see cref="PendingFleetAction.CurrentPosKm"/>
    /// returns an identical vector at every timestamp and the host's finite difference cancels to
    /// exactly zero. It is a structural property of dwell legs, not a floating-point coincidence.</para>
    ///
    /// <para>These tests exist so that property cannot be removed silently. Nothing in
    /// <c>FleetResolution</c> states it, the client that depends on it is in another assembly, and
    /// the failure it would cause -- a raid marker that never appears, or appears on the wrong
    /// fleets -- is invisible server-side. See <c>MapWorld.MapFleet.IsEngaging</c>.</para>
    /// </summary>
    public class DwellStationarityTests
    {
        /// <summary>The host's own velocity sample, mirrored: a finite difference over the live
        /// acceleration profile. Kept identical to <c>FactionRuntime.SampleFleetVelocity</c> --
        /// including its 0.1-minute step -- because a test of a different sample would prove
        /// nothing about the one the client actually receives.</summary>
        private static Vec3 SampleVelocity(PendingFleetAction fleet, FactionTuning tuning, DateTime now)
        {
            const double sampleMinutes = 0.1;
            Vec3 here = fleet.CurrentPosKm(now, tuning);
            Vec3 next = fleet.CurrentPosKm(now.AddMinutes(sampleMinutes), tuning);
            return new Vec3(
                (next.X - here.X) / sampleMinutes,
                (next.Y - here.Y) / sampleMinutes,
                (next.Z - here.Z) / sampleMinutes);
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            n.Radius = 40;
            return n;
        }

        private sealed class FixedRandom : IRandom
        {
            public double NextDouble() { return 0.5; }
        }

        /// <summary>A raid 100 km from its victim, dispatched and in transit.</summary>
        private static FactionState DispatchRaid(WorldModel w, out List<FactionState> states)
        {
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            states = new List<FactionState> { raider, new FactionState { Tag = "MERC", RngSeed = 1 } };
            raider.PendingFleets.Add(PendingFleetResolver.CreatePending(
                ActionKind.Raid, "RVN",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "MERC1", Cost = 0 },
                w, w.Tuning, immediate: false));
            return raider;
        }

        /// <summary>Flies the raid to its target so it pops the queued Engage stop and begins the
        /// dwell.</summary>
        private static PendingFleetAction Engaging(WorldModel w, FactionState raider, List<FactionState> states)
        {
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(
                states, w, w.Tuning, new FixedRandom(), new List<string>(), null);
            PendingFleetAction f = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Engage, f.CurrentStopKind);   // the arrangement, not the claim
            return f;
        }

        [Fact]
        public void EngageLeg_HasTheSamePointForItsOriginAndItsTarget()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states;
            PendingFleetAction f = Engaging(w, DispatchRaid(w, out states), states);

            // Exact equality is the point. An Engage leg is not "nearly" stationary.
            Assert.Equal(f.TargetPosKm.X, f.OriginPosKm.X);
            Assert.Equal(f.TargetPosKm.Y, f.OriginPosKm.Y);
            Assert.Equal(f.TargetPosKm.Z, f.OriginPosKm.Z);
        }

        [Fact]
        public void EngageLeg_SamplesExactlyZeroVelocityForTheWholeDwell()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states;
            PendingFleetAction f = Engaging(w, DispatchRaid(w, out states), states);

            // Across the dwell, not at one instant: the marker must hold for the whole window a
            // defender has to react in, not flicker as the progress ratio moves.
            double dwell = w.Tuning.RaidDwellSeconds;
            foreach (double fraction in new[] { 0.0, 0.01, 0.25, 0.5, 0.75, 0.99 })
            {
                Vec3 v = SampleVelocity(f, w.Tuning, f.DepartedUtc.AddSeconds(dwell * fraction));
                Assert.Equal(0.0, v.X);
                Assert.Equal(0.0, v.Y);
                Assert.Equal(0.0, v.Z);
            }
        }

        [Fact]
        public void TransitLeg_NeverSamplesZero_SoTheDiscriminatorCanFail()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states;
            FactionState raider = DispatchRaid(w, out states);
            PendingFleetAction f = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, f.CurrentStopKind);   // still flying

            // The other arm. Without this, "velocity is zero" could be true of every fleet and the
            // test above would confirm nothing.
            double legSeconds = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalSeconds;
            foreach (double fraction in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
            {
                Vec3 v = SampleVelocity(f, w.Tuning, f.DepartedUtc.AddSeconds(legSeconds * fraction));
                Assert.True(Math.Abs(v.X) > 0.0,
                    "a fleet in transit must not read as stationary at " + fraction + " of its leg");
            }
        }

        [Fact]
        public void MineDwell_IsNotStationary_BecauseTheMinerWorksARing()
        {
            WorldModel w = TestSupport.World(1);

            // Unlike Engage, a Mine dwell is NOT stationary: the miner flies a ring around its
            // deposit for the whole dwell (MineWander). So zero velocity now singles out an
            // engagement among dwells, and MapFleet.IsEngaging's kind filter is a second guard
            // rather than the only one. Constructed directly; AdvanceToNextStop is exercised by
            // MineWanderTests.
            var mining = new PendingFleetAction
            {
                Kind = ActionKind.Mine,
                CurrentStopKind = FleetStopKind.Mine,
                FleetId = "M1",
                OriginPosKm = new Vec3(40, -12, 7),
                TargetPosKm = new Vec3(40, -12, 7),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddSeconds(900),
            };

            Vec3 v = SampleVelocity(mining, w.Tuning, w.UtcNow.AddSeconds(120));
            Assert.True(Math.Abs(v.X) + Math.Abs(v.Y) + Math.Abs(v.Z) > 0.0,
                "a miner on its dwell must read as moving");
        }
    }
}
