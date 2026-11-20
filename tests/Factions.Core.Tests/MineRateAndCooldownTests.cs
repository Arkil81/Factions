using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The two halves of the 4x-mining balance change, which move in opposite directions and
    /// reach an existing server by different routes.
    ///
    /// <para><see cref="FactionTuning.MineDutyCycle"/> is a new key no build has ever persisted,
    /// so its shipped value reaches a live tuning.json unaided.
    /// <see cref="FactionTuning.MineCooldownMultiplier"/> is not: every live host persisted 10.0, so
    /// it has to come through <c>ApplyVersionedContractDefaults</c> or the node lockout never
    /// shortens. Both claims are asserted here, each with the negative control that stops it passing
    /// for the wrong reason.</para></summary>
    public class MineRateAndCooldownTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            public bool HasPrefab(string prefabId) => true;
            public long CargoLiters(string prefabId) => 500_000;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public double Dps(string prefabId) => 0;
            public double DryMassKg(string prefabId) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => null;
            public string GridSize(string prefabId) => "Small";
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new string[0];
            public IEnumerable<string> AllPrefabIds => new[] { "Drill" };
            public double DrillYieldScore(string prefabId) => 1000;
            public IReadOnlyList<string> RolesFor(string prefabId) => new[] { "Mine", "Haul" };
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string subTypeId) => 1.0;
        }

        // ---- shipped defaults --------------------------------------------------------------------

        [Fact]
        public void ShippedDefaults_RestANodeExactlyAsLongAsItWasWorked()
        {
            Assert.Equal(1.0, TestSupport.Tuning().MineCooldownMultiplier, 6);
        }

        [Fact]
        public void ShippedDefaults_CarryTheHalfDutyCycle()
        {
            Assert.Equal(0.5, TestSupport.Tuning().MineDutyCycle, 6);
        }

        // ---- how each reaches an existing server -------------------------------------------------

        /// <summary>The cooldown's whole reason for being in the version gate: a host carrying the
        /// tenfold lockout every earlier build persisted must be migrated off it.</summary>
        [Fact]
        public void ExistingTuningAtTheTenfoldCooldown_IsMigrated()
        {
            FactionTuning t = TestSupport.TuningWithOverride(@"{
                ""TuneVersion"": 43,
                ""MineCooldownMultiplier"": 10.0
            }");

            Assert.Equal(1.0, t.MineCooldownMultiplier, 6);
        }

        /// <summary>The gate's other half: past the bump, an operator's own lockout must survive every
        /// boot. Without this the test above would also pass on an implementation that stamped the
        /// default unconditionally and ignored the version entirely.</summary>
        [Fact]
        public void TuningAlreadyAtTheNewVersion_KeepsAnOperatorsOwnCooldown()
        {
            FactionTuning t = TestSupport.TuningWithOverride(@"{
                ""TuneVersion"": " + TestSupport.Tuning().TuneVersion + @",
                ""MineCooldownMultiplier"": 6.0
            }");

            Assert.Equal(6.0, t.MineCooldownMultiplier, 6);
        }

        /// <summary>The duty cycle needs no gate, and this is the case that proves it: a tuning.json
        /// already AT the current version, so the gate does not fire at all, and no MineDutyCycle key
        /// in it — exactly the shape of a host that boots this build a second time. It still reads
        /// 0.5, from the shipped defaults file.</summary>
        [Fact]
        public void DutyCycle_ReachesAHostAlreadyPastTheBump_WithoutTheGate()
        {
            FactionTuning t = TestSupport.TuningWithOverride(@"{
                ""TuneVersion"": " + TestSupport.Tuning().TuneVersion + @",
                ""CentralBankGrantAmount"": 250000
            }");

            Assert.Equal(0.5, t.MineDutyCycle, 6);
        }

        [Fact]
        public void DutyCycle_IsAnOperatorOverrideLikeAnyOther()
        {
            FactionTuning t = TestSupport.TuningWithOverride(@"{
                ""TuneVersion"": " + TestSupport.Tuning().TuneVersion + @",
                ""MineDutyCycle"": 0.25
            }");

            Assert.Equal(0.25, t.MineDutyCycle, 6);
        }

        // ---- what the rate multiplier actually does ----------------------------------------------

        /// <summary>The behavioural claim the whole change rests on: the multiplier reaches the rate a
        /// real dispatch is priced at, so the same hold arrives in a quarter of the dwell. Asserted as
        /// a ratio between two dispatches differing ONLY in the multiplier — a mirror of the formula
        /// would pass whether or not <c>ResolveDrillRate</c> reads the field.
        ///
        /// <para>Trip size is deliberately not asserted to change: it is a hold
        /// (<c>ClampTargetLiters</c>), not a formula, and the cargo capacity is the same on both
        /// runs.</para></summary>
        [Fact]
        public void FourfoldDutyCycle_QuartersTheDwellForTheSameHold()
        {
            double dwellAtLow = DispatchedMineDwellSeconds(0.125, out double targetAtLow);
            double dwellAtHigh = DispatchedMineDwellSeconds(0.5, out double targetAtHigh);

            Assert.True(dwellAtLow > 0, "the low-duty fixture must actually dispatch a Mine");
            Assert.Equal(targetAtLow, targetAtHigh, 6);          // same hold, not a bigger trip
            Assert.Equal(4.0, dwellAtLow / dwellAtHigh, 6);
        }

        /// <summary>Cooldown is dwell-derived, so a faster rate shortens the lockout by the same
        /// factor on top of whatever <see cref="FactionTuning.MineCooldownMultiplier"/> says. Resolves
        /// the dispatched dwell into a real <c>MineCooldownUntilUtc</c> rather than restating the
        /// arithmetic.</summary>
        [Fact]
        public void FasterRate_ShortensTheNodeLockoutByTheSameFactor()
        {
            DateTime resolvedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            double lockoutAtLow = ResolvedCooldownSeconds(0.125, resolvedAt);
            double lockoutAtHigh = ResolvedCooldownSeconds(0.5, resolvedAt);

            Assert.True(lockoutAtLow > 0, "the low-duty fixture must actually set a cooldown");
            Assert.Equal(4.0, lockoutAtLow / lockoutAtHigh, 6);
        }

        // ---- fixture -----------------------------------------------------------------------------

        /// <summary>Dispatches one Mine at <paramref name="dutyCycle"/> and returns the dwell the
        /// dispatcher priced it at (the Mine leg's own duration — the ship is already parked at the
        /// deposit, so no travel is folded in).</summary>
        private static double DispatchedMineDwellSeconds(double dutyCycle, out double targetLiters)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new Catalogue();

            var st = new FactionState { Tag = "F" };

            SpatialNode ore = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Ore");
            ore.OwnerFactionTag = "F";
            ore.HoldStrength = 1.0;
            ore.Deposit = new NodeDeposit();
            ore.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            ore.Richness = 1.0;
            ore.ShipStock["Drill"] = 1;

            SpatialNode smelter = world.Graph.Add(new Vec3(1, 0, 0), 1.0, "Smelter");
            smelter.OwnerFactionTag = "F";
            smelter.HoldStrength = 1.0;
            // A measured draw far beyond the deposit's passive mint: the extraction planner opens an
            // order at the smelter, the only automatic Mine trigger since FAC-395 slice 2b.
            smelter.DemandHistory["Ore:Iron"] = 1e9;

            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineDutyCycle = dutyCycle;

            new OperationsManager(itemVolumes: new Volumes())
                .Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            // PendingFleetAction is a struct, so FirstOrDefault would hand back a zeroed one that
            // reads as a legitimate 0-second dwell -- index it instead.
            targetLiters = 0;
            for (int i = 0; i < st.PendingFleets.Count; i++)
            {
                PendingFleetAction f = st.PendingFleets[i];
                if (f.Kind != ActionKind.Mine) continue;
                targetLiters = f.MineTargetLiters;
                return (f.EstimatedArrivalUtc - f.DepartedUtc).TotalSeconds;
            }
            return 0;
        }

        /// <summary>Runs that same dispatch through <see cref="PendingFleetResolver"/> so the node's
        /// real <see cref="SpatialNode.MineCooldownUntilUtc"/> is what gets measured.</summary>
        private static double ResolvedCooldownSeconds(double dutyCycle, DateTime resolvedAt)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new Catalogue();

            var st = new FactionState { Tag = "F" };

            SpatialNode ore = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Ore");
            ore.OwnerFactionTag = "F";
            ore.HoldStrength = 1.0;
            ore.Deposit = new NodeDeposit();
            ore.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            ore.Richness = 1.0;
            ore.ShipStock["Drill"] = 1;

            SpatialNode smelter = world.Graph.Add(new Vec3(1, 0, 0), 1.0, "Smelter");
            smelter.OwnerFactionTag = "F";
            smelter.HoldStrength = 1.0;
            // A measured draw far beyond the deposit's passive mint: the extraction planner opens an
            // order at the smelter, the only automatic Mine trigger since FAC-395 slice 2b.
            smelter.DemandHistory["Ore:Iron"] = 1e9;

            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineDutyCycle = dutyCycle;

            var volumes = new Volumes();
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            // Jump the clock past the dwell so the Mine stop resolves on this pass.
            world.UtcNow = resolvedAt.AddYears(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, tuning,
                new SeededRandom(1), new List<string>(), null, null, null, volumes);

            return (ore.MineCooldownUntilUtc - world.UtcNow).TotalSeconds;
        }
    }
}
