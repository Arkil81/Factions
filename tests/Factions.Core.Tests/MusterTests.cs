using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Tests for <see cref="ActionKind.Muster"/> — the deterrence fleet build-up action — and the
    /// fleet-power-relative border-pressure dampening (<c>FactionBrain.PowerDampening</c>/
    /// <c>DampedRivalPressure</c>) that feeds both Muster's own trigger and the Security/Rivalry
    /// drives generally. Modeled on <c>ConquerTests.cs</c>'s brain-candidate-generation coverage.
    /// </summary>
    public class MusterTests
    {
        private static Personality NeutralXenophobe()
        {
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Xenophobia = 1.0 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            return p;
        }

        private static Personality AggressorPersona(double aggression = 0.9)
        {
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = aggression, Xenophobia = 0.8 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 2.0;
            return p;
        }

        // ========================================================================================
        // PowerDampening / DampedRivalPressure (observed via Sense()'s Security/Rivalry drives)
        // ========================================================================================

        [Fact]
        public void Sense_BorderPressure_DampensByRelativeFleetPower()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            double SecurityAt(long ourPower, long theirPower)
            {
                var w = new FakeWorld { Treasury = 500_000 };
                var zf = (ZeroField)w.Influence;
                zf.OverridePressure = 0.8;
                zf.OverrideRivals.Add("B");
                w.FleetPower["A"] = ourPower;
                w.FleetPower["B"] = theirPower;

                // No owned nodes: isolates Security to the friction/dampening term alone (no
                // PerceivedThreat or hold-atrophy contribution to muddy the comparison).
                var s = new FactionState { Tag = "A", RngSeed = 1 };
                brain.Think(s, NeutralXenophobe(), w, new SeededRandom(1), t);
                return s.Drives[Drive.Security];
            }

            double outclassed = SecurityAt(10, 10000);
            double parity      = SecurityAt(1000, 1000);
            double dominant    = SecurityAt(10000, 10);

            Assert.True(outclassed > parity,
                "an outclassed fleet should read the same territorial pressure as more threatening than at parity");
            Assert.True(parity > dominant,
                "parity should still read as more threatening than dominance — never a stable cold-war zero");
        }

        [Fact]
        public void Sense_BorderPressure_NoRivals_ProducesZeroSecurity()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 500_000 }; // ZeroField.OverrideRivals stays empty

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            brain.Think(s, NeutralXenophobe(), w, new SeededRandom(1), t);

            Assert.Equal(0.0, s.Drives[Drive.Security]);
        }

        // ========================================================================================
        // Muster candidate generation: Security-driven (untargeted, "para bellum")
        // ========================================================================================

        [Fact]
        public void GenerateForDrive_HighSecurity_GeneratesUntargetedMusterCandidate()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 500_000 };
            var zf = (ZeroField)w.Influence;
            zf.OverridePressure = 0.9;
            zf.OverrideRivals.Add("B");
            w.FleetPower["A"] = 1;
            w.FleetPower["B"] = 100_000; // badly outclassed -> weakness ceiling amplifies friction into Security

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            Decision d = brain.Think(s, NeutralXenophobe(), w, new SeededRandom(1), t);

            Assert.True(s.Drives[Drive.Security] > 0.4,
                "test setup should cross the Muster Security threshold; got " + s.Drives[Drive.Security]);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Muster && c.Intent.TargetFactionTag == null);
        }

        // ========================================================================================
        // Muster candidate generation: Rivalry-driven (targeted at the worst dampened-pressure rival)
        // ========================================================================================

        [Fact]
        public void GenerateForDrive_HighRivalry_GeneratesTargetedMusterCandidate_AtWorstDampenedRival()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 500_000 };
            var zf = (ZeroField)w.Influence;
            zf.OverridePressure = 0.9;
            zf.OverrideRivals.Add("B");
            w.FleetPower["A"] = 1;
            w.FleetPower["B"] = 100_000;

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            Decision d = brain.Think(s, AggressorPersona(), w, new SeededRandom(1), t);

            Assert.True(s.Drives[Drive.Rivalry] > 0.4,
                "test setup should cross the Muster Rivalry threshold; got " + s.Drives[Drive.Rivalry]);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Muster && c.Intent.TargetFactionTag == "B");
        }

        [Fact]
        public void GenerateForDrive_HighRivalry_NoTerritorialRivals_NoTargetedMusterCandidate()
        {
            // Rivalry can run high purely from relations (enmity) with no territorial overlap at
            // all — RivalTags is then empty, so DampedRivalPressure has no rival to point at and
            // the targeted firing mode must not fire (only the untargeted mode ever could here).
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 500_000 }; // ZeroField.OverrideRivals stays empty

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.9;

            Decision d = brain.Think(s, AggressorPersona(), w, new SeededRandom(1), t);

            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Muster && c.Intent.TargetFactionTag != null);
        }

        // ========================================================================================
        // Posture headroom: Muster must stop competing once MilitaryPosture is already near the
        // 1.0 ceiling — Enact clamps posture at 1.0, so firing again past that point buys nothing.
        // ========================================================================================

        [Fact]
        public void GenerateForDrive_MusterUtility_ShrinksAsPostureApproachesCeiling()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            double MusterUtilityAt(double posture)
            {
                var w = new FakeWorld { Treasury = 500_000 };
                var zf = (ZeroField)w.Influence;
                zf.OverridePressure = 0.9;
                zf.OverrideRivals.Add("B");
                w.FleetPower["A"] = 1;
                w.FleetPower["B"] = 100_000; // badly outclassed -> Security stays high regardless of posture

                var s = new FactionState { Tag = "A", RngSeed = 1, MilitaryPosture = posture };
                Decision d = brain.Think(s, NeutralXenophobe(), w, new SeededRandom(1), t);
                Candidate c = d.Scored.FirstOrDefault(x => x.Intent.Kind == ActionKind.Muster && x.Intent.TargetFactionTag == null);
                return c != null ? c.Utility : 0.0;
            }

            double lowPostureUtility  = MusterUtilityAt(0.0);
            double highPostureUtility = MusterUtilityAt(0.999);

            Assert.True(lowPostureUtility > 0,
                "sanity check: Muster should have nonzero utility at posture=0; got " + lowPostureUtility);
            Assert.True(highPostureUtility < lowPostureUtility * 0.1,
                "Muster utility should collapse toward zero as posture approaches the 1.0 ceiling "
                + "(same Security-driven setup otherwise) — low=" + lowPostureUtility + " high=" + highPostureUtility);
        }
    }
}
