using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The SE production formulae and the host-stamped machine profile that feeds them.
    /// Reference values come from decompiled SE source (MyRefinery.ProcessQueueItems,
    /// MyAssembler.GetEfficiencyMultiplierForBlueprint) and the live SBCs.
    /// </summary>
    public class MachineProfileTests
    {
        // The live world's four modelled machines with a four-speed-module fit (8 upgrade ports
        // x 0.5 = +4.0 Productivity) and the live world multipliers (all 3.0).
        private static MachineProfile LiveProfile(NodeCapabilityKind cap)
        {
            MachineProfile p = MachinePhysics.DefaultProfile(cap);
            p.WorldSpeedMultiplier = 3.0;
            if (cap == NodeCapabilityKind.Refinery) p.MaterialEfficiency = 0.70;   // Blast Furnace
            if (cap == NodeCapabilityKind.Manufactory || cap == NodeCapabilityKind.ManufactoryII)
                p.InputEfficiencyDivisor = 3.0;                                    // AssemblerEfficiencyMultiplier
            return p;
        }

        [Fact]
        public void EffectiveSpeed_MatchesSeFormula_ForTheFourModelledMachines()
        {
            // (BaseSpeed + Productivity) * WorldSpeedMultiplier
            Assert.Equal(13.95, MachinePhysics.EffectiveSpeed(LiveProfile(NodeCapabilityKind.Refinery), 4.0), 6);
            Assert.Equal(14.10, MachinePhysics.EffectiveSpeed(LiveProfile(NodeCapabilityKind.Manufactory), 4.0), 6);
            Assert.Equal(15.00, MachinePhysics.EffectiveSpeed(LiveProfile(NodeCapabilityKind.ManufactoryII), 4.0), 6);
            Assert.Equal(15.90, MachinePhysics.EffectiveSpeed(LiveProfile(NodeCapabilityKind.RefineryII), 4.0), 6);
        }

        [Fact]
        public void UnstampedDefaults_ReproduceThePreProfileConstants()
        {
            // No world multiplier, no upgrades: exactly the SBC speeds the manager used before
            // profiles existed, so an unstamped host (simulator, tests) is unaffected.
            Assert.Equal(0.65, MachinePhysics.EffectiveSpeed(MachinePhysics.DefaultProfile(NodeCapabilityKind.Refinery), 0.0), 6);
            Assert.Equal(0.70, MachinePhysics.EffectiveSpeed(MachinePhysics.DefaultProfile(NodeCapabilityKind.Manufactory), 0.0), 6);
            Assert.Equal(1.00, MachinePhysics.EffectiveSpeed(MachinePhysics.DefaultProfile(NodeCapabilityKind.ManufactoryII), 0.0), 6);
            Assert.Equal(1.30, MachinePhysics.EffectiveSpeed(MachinePhysics.DefaultProfile(NodeCapabilityKind.RefineryII), 0.0), 6);

            foreach (NodeCapabilityKind cap in new[] { NodeCapabilityKind.Refinery, NodeCapabilityKind.Manufactory,
                                                       NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII })
            {
                MachineProfile p = MachinePhysics.DefaultProfile(cap);
                Assert.Equal(6, p.MachineCount);
                Assert.Equal(1.0, p.MaterialEfficiency, 6);
                Assert.Equal(1.0, p.InputEfficiencyDivisor, 6);
            }
        }

        [Fact]
        public void NonProducingCapabilities_ResolveToZeroMachines()
        {
            // Shipyard alone. FuelDepot was here too until it gained the ice-to-gas recipes
            // (FAC-038); a zero here is what made TickOneBank skip its bank outright, so the two
            // have to move together and this is the assertion that says so.
            Assert.Equal(0, MachinePhysics.DefaultProfile(NodeCapabilityKind.Shipyard).MachineCount);
            Assert.Equal(0.0, MachinePhysics.EffectiveSpeed(MachinePhysics.DefaultProfile(NodeCapabilityKind.Shipyard), 4.0), 6);
        }

        [Fact]
        public void InputAmount_DividesByEfficiency_UnlessTheBlueprintOptsOut()
        {
            MachineModel model = MachinePhysics.Resolve(LiveProfile(NodeCapabilityKind.ManufactoryII), 4.0);

            // SE: prerequisites are multiplied by 1 / AssemblerEfficiencyMultiplier ...
            Assert.Equal(3.0, MachinePhysics.InputAmount(9.0, model, false), 6);
            // ... except for a blueprint carrying IgnoreEfficiencyMultiplier (the hand tools and
            // weapons ManufactoryII builds), which is charged in full.
            Assert.Equal(9.0, MachinePhysics.InputAmount(9.0, model, true), 6);
        }

        [Fact]
        public void OutputScale_IsMaterialEfficiency()
        {
            Assert.Equal(0.70, MachinePhysics.OutputScale(LiveProfile(NodeCapabilityKind.Refinery)), 6);
            Assert.Equal(1.00, MachinePhysics.OutputScale(LiveProfile(NodeCapabilityKind.RefineryII)), 6);
        }

        [Fact]
        public void MachineProfileFor_FallsBackToDefault_WhenUnstampedOrIncomplete()
        {
            var tuning = new FactionTuning();
            Assert.Equal(1.30, tuning.MachineProfileFor(NodeCapabilityKind.RefineryII).BaseSpeed, 6);

            // An incomplete stamp (host resolved no block definition) must not zero the model out.
            tuning.MachineProfiles[NodeCapabilityKind.RefineryII] = new MachineProfile { BaseSpeed = 0, MachineCount = 0 };
            Assert.Equal(1.30, tuning.MachineProfileFor(NodeCapabilityKind.RefineryII).BaseSpeed, 6);

            tuning.MachineProfiles[NodeCapabilityKind.RefineryII] =
                new MachineProfile { BaseSpeed = 2.5, MachineCount = 4, WorldSpeedMultiplier = 3.0 };
            Assert.Equal(2.5, tuning.MachineProfileFor(NodeCapabilityKind.RefineryII).BaseSpeed, 6);
        }

        [Fact]
        public void ResolveAll_AppliesTheTuningProductivityBonusToEveryCapability()
        {
            var tuning = new FactionTuning { ProductionProductivityBonus = 4.0 };
            tuning.MachineProfiles[NodeCapabilityKind.RefineryII] = LiveProfile(NodeCapabilityKind.RefineryII);

            MachineModel[] models = MachinePhysics.ResolveAll(tuning);
            Assert.Equal(15.90, models[(int)NodeCapabilityKind.RefineryII].Speed, 6);
            // Unstamped kinds still get the bonus, on their default (unmultiplied) base speed.
            Assert.Equal(4.65, models[(int)NodeCapabilityKind.Refinery].Speed, 6);
        }
    }
}
