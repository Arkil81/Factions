using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 3c — at Intercept, a Guard sent at an intruder pursues it past its node's leash
    /// until the intruder leaves the faction's space.
    /// </summary>
    public sealed class TrespassPursuitTests
    {
        private const long Grid = 777;
        private static readonly Vec3 InDefSpace = new Vec3(20, 0, 0);
        private static readonly Vec3 OpenSpace = new Vec3(1000, 0, 0);

        private static InfluenceField Field(WorldModel w)
        {
            FactionTuning t = TestSupport.Tuning();
            return new InfluenceField(w.Graph, () => t);
        }

        private static PendingFleetAction Guard(bool againstIntruder = true)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = "DEF",
                TargetNodeId = "N002",
                AgainstIntruder = againstIntruder,
            };
        }

        private static long Pursuit(WorldModel w, PendingFleetAction g, Vec3 at)
        {
            return TrespassLadder.PursuitTarget(g, w, Field(w), at, 0.03);
        }

        [Fact]
        public void AtIntercept_TheGuardPursuesWhileTheIntruderIsInItsSpace()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            w.MarkIntruder("N002", TrespassRung.Intercept, Grid);

            Assert.Equal(Grid, Pursuit(w, Guard(), InDefSpace));
        }

        [Fact]
        public void LeavingTheSpaceEndsThePursuit()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            w.MarkIntruder("N002", TrespassRung.Intercept, Grid);

            Assert.Equal(0, Pursuit(w, Guard(), OpenSpace));

            // Into another faction's space is out of this one's.
            SpatialNode other = w.Graph.Add(new Vec3(300, 0, 0), 0.6, "OTH1");
            other.OwnerFactionTag = "OTH"; other.HoldStrength = 0.9; other.Radius = 40;
            Assert.Equal(0, Pursuit(w, Guard(), new Vec3(300, 0, 0)));
        }

        [Fact]
        public void NoPursuitWhileShadowing_OrForAnOrdinaryGuard_OrWithoutAGrid()
        {
            WorldModel shadow = StationCallTests.DefenderWorld();
            shadow.MarkIntruder("N002", TrespassRung.Shadow, Grid);
            Assert.Equal(0, Pursuit(shadow, Guard(), InDefSpace));

            WorldModel ordinary = StationCallTests.DefenderWorld();
            ordinary.MarkIntruder("N002", TrespassRung.Intercept, Grid);
            Assert.Equal(0, Pursuit(ordinary, Guard(againstIntruder: false), InDefSpace));

            WorldModel unnamed = StationCallTests.DefenderWorld();
            unnamed.MarkIntruder("N002", TrespassRung.Intercept, 0);
            Assert.Equal(0, Pursuit(unnamed, Guard(), InDefSpace));
        }

        [Fact]
        public void AnExpiredMark_EndsThePursuit()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            w.MarkIntruder("N002", TrespassRung.Intercept, Grid);
            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);

            Assert.Equal(0, Pursuit(w, Guard(), InDefSpace));
        }

        [Fact]
        public void ReMarkingTheGuardsNode_KeepsThePursuitAndTheDutyAlive()
        {
            // What the host does each second of a chase: the node stays intruded at Intercept, so the
            // Guard is neither released nor dropped back to Shadow while the intruder runs.
            WorldModel w = StationCallTests.DefenderWorld();
            w.MarkIntruder("N002", TrespassRung.Intercept, Grid);
            PendingFleetAction g = Guard();
            for (int i = 0; i < 30; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(5);
                long p = Pursuit(w, g, InDefSpace);
                Assert.Equal(Grid, p);
                w.MarkIntruder("N002", TrespassRung.Intercept, p);
            }
            Assert.False(TrespassLadder.GuardDutyOver(g, w));
            Assert.False(TrespassLadder.GuardHoldsFire(g, w, new FactionState[0]));
        }
    }
}
