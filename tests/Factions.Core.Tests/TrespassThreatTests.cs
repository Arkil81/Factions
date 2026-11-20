using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 2 — a detected enemy ship in a faction's space marks the nearest owned node
    /// intruded: threat rises once per intrusion and the reactive Guard answers it, behind any real
    /// attack.
    /// </summary>
    public sealed class TrespassThreatTests
    {
        // ---- the mark --------------------------------------------------------------------------------

        [Fact]
        public void Mark_RaisesThreatOncePerIntrusionNotPerPass()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            SpatialNode n = w.Graph.Nodes["N002"];
            n.Threat = 0;

            Assert.True(w.MarkIntruder("N002"));
            for (int i = 0; i < 10; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(5);
                Assert.False(w.MarkIntruder("N002"));
            }
            Assert.Equal(TrespassPolicy.IntruderThreat, n.Threat, 6);

            // Gone past the memory, then back: a new intrusion, but inside the charge interval.
            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            Assert.False(w.IsIntruded("N002"));
            Assert.True(w.MarkIntruder("N002"));
            Assert.Equal(TrespassPolicy.IntruderThreat, n.Threat, 6);

            // Back after the interval: charged again.
            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderThreatInterval;
            Assert.True(w.MarkIntruder("N002"));
            Assert.Equal(2 * TrespassPolicy.IntruderThreat, n.Threat, 6);
        }

        // The characteristic failure: a pilot slipping in and out of a border every 61 s for three
        // hours, threat decaying on the 180 s global pass at the shipped rate. Trespass alone must
        // never hold the node above one charge, nor anywhere near a patrol's stand floor.
        [Fact]
        public void Loitering_CannotRatchetThreat()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = w.Graph.Nodes["N002"];
            n.Threat = 0;
            DateTime start = w.UtcNow, nextDecay = w.UtcNow.AddSeconds(180);
            double max = 0;
            for (DateTime at = start; at < start.AddHours(3); at = at.AddSeconds(5))
            {
                w.UtcNow = at;
                // In for 30 s of every 91: each return is past the 60 s memory, so it is fresh.
                if ((at - start).TotalSeconds % 91 < 30) w.MarkIntruder("N002");
                if (at >= nextDecay)
                {
                    w.DecayThreat(t.ThreatDecayForIntervalSeconds(180));
                    nextDecay = nextDecay.AddSeconds(180);
                }
                max = Math.Max(max, n.Threat);
            }
            Assert.True(max <= TrespassPolicy.IntruderThreat + 1e-9, "max threat " + max);
            Assert.True(max < t.PatrolStandThreatFloor);
        }

        [Fact]
        public void Mark_ExpiresAndTheGateClears()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            Assert.False(w.AnyIntruder);
            w.MarkIntruder("N002");
            Assert.True(w.AnyIntruder);
            Assert.True(w.IsIntruded("N002"));
            Assert.False(w.IsIntruded("HOME"));

            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            Assert.False(w.AnyIntruder);
            Assert.False(w.IsIntruded("N002"));
        }

        [Fact]
        public void Mark_RefusesUnknownAndNeutralNodes()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            SpatialNode n = w.Graph.Add(new Vec3(50, 0, 0), 0.5, "N009");
            n.OwnerFactionTag = NeutralFaction.Tag;
            Assert.False(w.MarkIntruder("N009"));
            Assert.False(w.MarkIntruder("NOPE"));
            Assert.False(w.AnyIntruder);
        }

        [Fact]
        public void OnlyTheEnemiesBandIsAnIntruder()
        {
            Assert.True(TrespassPolicy.IsIntruder(TrespassPolicy.ToneFor("Enemies", false)));
            Assert.False(TrespassPolicy.IsIntruder(TrespassPolicy.ToneFor("Neutral", false)));
            Assert.False(TrespassPolicy.IsIntruder(TrespassPolicy.ToneFor("Friends", false)));
            Assert.False(TrespassPolicy.IsIntruder(TrespassPolicy.ToneFor("Enemies", true)));
        }

        // ---- the reactive Guard ----------------------------------------------------------------------

        [Fact]
        public void AnIntrudedNode_DrawsOneReactiveGuard()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            FactionTuning t = StationCallTests.GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var fm = new FleetManager();

            var log = new List<string>();
            fm.Tick(new[] { def }, w, t, log);
            Assert.Empty(StationCallTests.GuardLines(log));   // control: nobody there

            w.MarkIntruder("N002");
            fm.Tick(new[] { def }, w, t, log);
            string line = Assert.Single(StationCallTests.GuardLines(log));
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=intruder", line);

            // Still there on the next passes: answered once, never reinforced.
            for (int i = 0; i < 3; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(30);
                w.MarkIntruder("N002");
                fm.Tick(new[] { def }, w, t, log);
            }
            Assert.Single(StationCallTests.GuardLines(log));
            Assert.Single(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        [Fact]
        public void AnExpiredMark_DrawsNothing()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            FactionTuning t = StationCallTests.GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };

            w.MarkIntruder("N002");
            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            var log = new List<string>();
            new FleetManager().Tick(new[] { def }, w, t, log);
            Assert.Empty(StationCallTests.GuardLines(log));
        }

        [Fact]
        public void AnInboundRaid_OutranksAnIntruderElsewhere()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            FactionTuning t = StationCallTests.GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            // The raid is minutes out; the intruder is at HOME now. The raid still wins the pick.
            FactionState rad = StationCallTests.Raider(w, "N002");
            w.MarkIntruder("HOME");

            var log = new List<string>();
            new FleetManager().Tick(new[] { def, rad }, w, t, log);
            string line = StationCallTests.GuardLines(log).First();
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=RAD", line);
        }

        [Fact]
        public void APactPartner_DoesNotAnswerAnIntruderAlone()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            FactionTuning t = StationCallTests.GuardTuning();
            // ALY owns no node with an intruder of its own; DEF's N002 has one. DEF has no hull, ALY has.
            SpatialNode home = w.Graph.Nodes["HOME"];
            home.ShipStock.Clear();
            SpatialNode aly = w.Graph.Add(new Vec3(20, 0, 0), 0.6, "ALY1");
            aly.OwnerFactionTag = "ALY"; aly.HoldStrength = 0.9; aly.Radius = 40;
            aly.ShipStock["Gunship"] = 2;
            w.Graph.Connect("ALY1", "N002");
            w.SetTreasury("ALY", 500000);
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            def.PactWith.Add("ALY");
            ally.PactWith.Add("DEF");

            w.MarkIntruder("N002");
            var log = new List<string>();
            new FleetManager().Tick(new[] { def, ally }, w, t, log);
            Assert.DoesNotContain(StationCallTests.GuardLines(log), l => l.Contains("ALY"));

            // Control: the same partner does answer a call at that node, so the path is open.
            w.SetStationCall("N002", null);
            new FleetManager().Tick(new[] { def, ally }, w, t, log);
            Assert.Contains(StationCallTests.GuardLines(log), l => l.Contains("ALY"));
        }
    }
}
