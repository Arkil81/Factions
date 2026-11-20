using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The rule the map draws a fight by. It lived in <c>MapWorld.MapFleet</c> and so ran only on a
    /// live client, where no test could reach it and no server check could see it fail -- the same
    /// gap <see cref="DeadReckon"/> had before it was shared, and closed the same way.
    ///
    /// <para>These tests are about the <b>conjunction</b>. That a dwell leg samples exactly zero is
    /// a property of Core, and <c>DwellStationarityTests</c> pins it there against the code that
    /// produces it; what is asserted here is that both halves of the rule are required and that
    /// neither alone is sufficient.</para>
    /// </summary>
    public class FleetEngagementTests
    {
        private static WireMapFleet Fleet(byte kind, double vx = 0.0, double vy = 0.0, double vz = 0.0)
        {
            var f = new WireMapFleet();
            f.Kind = kind;
            f.VelXKmPerMin = vx;
            f.VelYKmPerMin = vy;
            f.VelZKmPerMin = vz;
            return f;
        }

        // ---- The positive case ------------------------------------------------------------------

        [Theory]
        [InlineData(FleetEngagement.RaidKind)]
        [InlineData(FleetEngagement.RetaliateKind)]
        [InlineData(FleetEngagement.ConquerKind)]
        public void EveryCombatKind_HoldingStill_IsEngaging(byte kind)
        {
            Assert.True(FleetEngagement.IsEngaging(Fleet(kind)));
        }

        // ---- The kind half, which is the one that fails silently ---------------------------------

        /// <summary>The trap this rule exists to avoid. A Mine leg is stationary by the identical
        /// Core mechanism and runs longer than a raid's dwell, so a rule keyed on stillness alone
        /// reports every mining operation in sensor range as a raid in progress. Mine dwells are far
        /// more common than engagements, so this is the arm with a real population behind it.</summary>
        [Fact]
        public void AMineFleetHoldingStill_IsNotEngaging()
        {
            const byte mineKind = 22;
            Assert.True(FleetEngagement.IsEngaging(FleetEngagement.RaidKind, 0, 0, 0),
                "arrangement: stillness alone would admit this");
            Assert.False(FleetEngagement.IsEngaging(Fleet(mineKind)));
        }

        /// <summary>Every other kind that can sit still, for the same reason -- Guard parks on
        /// station, and a Trade or Haul convoy is momentarily still at a stop. None is a fight.</summary>
        [Theory]
        [InlineData((byte)4)]    // Guard
        [InlineData((byte)10)]   // Trade
        [InlineData((byte)13)]   // Patrol
        [InlineData((byte)21)]   // Haul
        [InlineData((byte)22)]   // Mine
        public void NoOtherKindHoldingStill_IsEngaging(byte kind)
        {
            Assert.False(FleetEngagement.IsEngaging(Fleet(kind)));
        }

        // ---- The velocity half -------------------------------------------------------------------

        /// <summary>A raid in transit is not a raid fighting, which is the whole distinction the map
        /// could not previously draw. One axis moving is enough to disqualify it -- a fleet flying
        /// along a single axis is the ordinary case, not a special one.</summary>
        [Theory]
        [InlineData(1.0, 0.0, 0.0)]
        [InlineData(0.0, 1.0, 0.0)]
        [InlineData(0.0, 0.0, 1.0)]
        [InlineData(-3.5, 0.0, 0.0)]
        public void ACombatFleetStillMoving_IsNotEngaging(double vx, double vy, double vz)
        {
            Assert.False(FleetEngagement.IsEngaging(Fleet(FleetEngagement.RaidKind, vx, vy, vz)));
        }

        /// <summary>Exactness is the mechanism, not a rounding artefact. A fleet crawling at a
        /// millionth of a km/min is drifting, not holding station, and an epsilon comparison
        /// introduced "for safety" would start calling it a fight.</summary>
        [Fact]
        public void AVanishinglySlowCombatFleet_IsNotEngaging()
        {
            Assert.False(FleetEngagement.IsEngaging(Fleet(FleetEngagement.RaidKind, 1e-9, 0, 0)));
        }

        // ---- The ordinals -------------------------------------------------------------------------

        /// <summary>The wire carries Core's <c>ActionKind</c> as a raw byte, so these three numbers
        /// are a contract with an enum in another assembly that this one deliberately cannot
        /// reference. Nothing but this assertion notices if that enum is reordered.</summary>
        [Fact]
        public void TheOrdinalsMatchCoreActionKind()
        {
            Assert.Equal((byte)ActionKind.Raid, FleetEngagement.RaidKind);
            Assert.Equal((byte)ActionKind.Retaliate, FleetEngagement.RetaliateKind);
            Assert.Equal((byte)ActionKind.Conquer, FleetEngagement.ConquerKind);
            // And the kind the trap above is built on, so that test cannot quietly stop testing it.
            Assert.Equal(22, (byte)ActionKind.Mine);
        }

        /// <summary>`EngagesOnArrival` must admit exactly the kinds Core's own dwell-queueing does.
        /// Enumerated rather than spot-checked: a kind added to Core without being added here is the
        /// failure this catches, and it cannot be caught by testing the three that already pass.</summary>
        [Fact]
        public void EngagesOnArrival_AdmitsExactlyTheCombatKinds()
        {
            for (int k = 0; k <= 22; k++)
            {
                bool expected = k == (int)ActionKind.Raid
                             || k == (int)ActionKind.Retaliate
                             || k == (int)ActionKind.Conquer;
                Assert.Equal(expected, FleetEngagement.EngagesOnArrival((byte)k));
            }
        }

        // ---- The other half of the dwell ---------------------------------------------------------

        /// <summary>A Reinforce holds the same leg by the same mechanism, and the map draws it
        /// differently on purpose. The pair of assertions is the point: one fleet must read as
        /// exactly one of the two, or a garrison standing at a station paints as an attack on
        /// it.</summary>
        [Fact]
        public void AReinforceHoldingStill_DefendsAndDoesNotEngage()
        {
            WireMapFleet f = Fleet(FleetEngagement.ReinforceKind);
            Assert.True(FleetEngagement.IsHoldingDefence(f));
            Assert.False(FleetEngagement.IsEngaging(f));
        }

        /// <summary>And the attack reads the other way round, so neither test can pass by the
        /// reading being stuck on one answer.</summary>
        [Fact]
        public void ARaidHoldingStill_EngagesAndDoesNotDefend()
        {
            WireMapFleet f = Fleet(FleetEngagement.RaidKind);
            Assert.True(FleetEngagement.IsEngaging(f));
            Assert.False(FleetEngagement.IsHoldingDefence(f));
        }

        /// <summary>The stillness half, on this reading too: a Reinforce still flying at the node it
        /// is going to shore up has not arrived, and a ring on it would claim a garrison that is not
        /// there yet.</summary>
        [Theory]
        [InlineData(0.0001, 0.0, 0.0)]
        [InlineData(0.0, -0.0001, 0.0)]
        [InlineData(0.0, 0.0, 12.0)]
        public void AReinforceStillFlying_IsNotHoldingDefence(double vx, double vy, double vz)
        {
            Assert.False(FleetEngagement.IsHoldingDefence(
                Fleet(FleetEngagement.ReinforceKind, vx, vy, vz)));
        }

        /// <summary>Defence is exactly one kind. Guard is the one most likely to be added here by
        /// mistake: it parks on station, but it stands a duty window after resolving rather than a
        /// dwell leg, so it never carries this state.</summary>
        [Fact]
        public void DefendsOnArrival_AdmitsExactlyReinforce()
        {
            for (int k = 0; k <= 22; k++)
            {
                Assert.Equal(k == (int)ActionKind.Reinforce,
                             FleetEngagement.DefendsOnArrival((byte)k));
            }
            Assert.Equal((byte)ActionKind.Reinforce, FleetEngagement.ReinforceKind);
        }
    }
}
