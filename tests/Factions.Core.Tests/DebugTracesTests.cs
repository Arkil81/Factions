using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The manager log filters after they stopped being tuning keys (eight at the move, sixteen now). The load-bearing property
    /// is not the toggling — it is that the defaults are the values they replaced.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public sealed class DebugTracesTests : IDisposable
    {
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        [Fact]
        public void DefaultsAreTheLiveValuesTheyReplaced_NotAllOff()
        {
            // Read off the running server's tuning.json on 2026-09-02, the day they moved:
            // Produced/ItemXfer/UpkeepIncome/CentralBankDenials false, the other four TRUE.
            // Defaulting the four to false would have silently darkened channels the operator had,
            // and the symptom of that is silence -- indistinguishable from the thing you were
            // watching having stopped.
            DebugTraces.ResetToDefaults();

            Assert.False(DebugTraces.Produced);
            Assert.False(DebugTraces.ItemXfer);
            Assert.False(DebugTraces.UpkeepIncome);
            Assert.False(DebugTraces.CentralBankDenials);
            Assert.True(DebugTraces.ItemDemand);
            Assert.True(DebugTraces.ShipEvents);
            Assert.True(DebugTraces.Investment);
            Assert.True(DebugTraces.BrainDecisions);
            // The new ones are the exception: they did not exist before. SlotGate is per bank per
            // tick; Diplomacy is the per-pair detail behind an unconditional per-pass summary.
            Assert.False(DebugTraces.SlotGate);
            Assert.False(DebugTraces.Diplomacy);
            Assert.False(DebugTraces.Fuel);
            Assert.False(DebugTraces.Sensing);
            // Contracts and EconomyProfiler gate lines that shipped unconditional: the contract
            // board's generation path was 5,431 of 20,239 lines of Torch-2026-09-21.log, and the
            // profiler is instrumentation for one stall investigation. Both off.
            Assert.False(DebugTraces.Contracts);
            Assert.False(DebugTraces.EconomyProfiler);
            Assert.False(DebugTraces.Governor);
            Assert.False(DebugTraces.StoreBar);   // one line per refused click
            Assert.False(DebugTraces.Extraction); // one line per (hub, ore) per plan
            Assert.False(DebugTraces.Tactics);
            Assert.False(DebugTraces.Landing);    // a raycast and a voxel read per gear event
        }

        [Theory]
        [InlineData("produced")]
        [InlineData("itemxfer")]
        [InlineData("upkeep")]
        [InlineData("itemdemand")]
        [InlineData("shipevents")]
        [InlineData("investment")]
        [InlineData("bankdenials")]
        [InlineData("braindecisions")]
        [InlineData("slots")]
        [InlineData("diplomacy")]
        [InlineData("grids")]
        [InlineData("fuel")]
        [InlineData("sensing")]
        [InlineData("buildrate")]
        [InlineData("contracts")]
        [InlineData("econtick")]
        [InlineData("governor")]
        [InlineData("munitions")]
        [InlineData("storebar")]
        [InlineData("extraction")]
        [InlineData("tactics")]
        [InlineData("landing")]
        [InlineData("trespass")]
        public void EveryTraceIsReachableByName_AndReportsWhatItDid(string name)
        {
            DebugTraces.ResetToDefaults();
            var before = new Dictionary<string, bool>();
            foreach (var kv in DebugTraces.All()) before[kv.Key] = kv.Value;

            bool nowOn; string description;
            Assert.True(DebugTraces.TryToggle(name, out nowOn, out description));
            Assert.False(string.IsNullOrEmpty(description));   // the operator is told what they turned on
            Assert.NotEqual(before[name], nowOn);

            // ...and exactly one moved. A copy-paste in the switch would flip a neighbour instead.
            int moved = 0;
            foreach (var kv in DebugTraces.All()) if (kv.Value != before[kv.Key]) moved++;
            Assert.Equal(1, moved);
        }

        [Fact]
        public void AnUnknownNameIsRefused_SoTheCallerCanFallThrough()
        {
            // `factions debug <kind>` has other owners -- trade, uplink, rock. Claiming an unknown
            // word here would swallow theirs.
            bool nowOn; string description;
            Assert.False(DebugTraces.TryToggle("uplink", out nowOn, out description));
            Assert.False(DebugTraces.TryToggle("", out nowOn, out description));
            Assert.False(DebugTraces.TryToggle(null, out nowOn, out description));
        }

        [Fact]
        public void AllNamesAreListedForTheStartupStateLine()
        {
            // LogDebugToggleState enumerates this; a trace missing from it is on with no way to know.
            var names = new List<string>();
            foreach (var kv in DebugTraces.All()) names.Add(kv.Key);
            Assert.Equal(32, names.Count);
            foreach (string n in new[] { "produced", "itemxfer", "upkeep", "itemdemand", "shipevents",
                                         "investment", "bankdenials", "braindecisions", "slots",
                                         "diplomacy", "grids", "fuel", "buildrate", "sensing",
                                         "contracts", "econtick", "governor", "munitions", "consent",
                                         "storebar", "extraction", "tactics", "restore", "admission", "physics", "alloc", "landing", "trespass", "asminput", "relation", "seats" })
                Assert.Contains(n, names);
        }
    }
}
