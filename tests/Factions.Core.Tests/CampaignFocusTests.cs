using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CampaignFocusTests
    {
        [Fact]
        public void CampaignFocusCollection_RoundTripsTwoIndependentFocuses()
        {
            DateTime firstSet = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
            DateTime secondSet = firstSet.AddMinutes(5);
            var state = new FactionState { Tag = "ACME", FactionId = 42, PersonalityId = "settler", RngSeed = 99 };
            state.CampaignFoci.Add(new CampaignFocus
            {
                Id = "N001",
                CenterNodeId = "N001",
                Priority = 0.75,
                SetAtUtc = firstSet,
                ExpiresAtUtc = firstSet.AddHours(1)
            });
            state.CampaignFoci.Add(new CampaignFocus
            {
                Id = "N002",
                CenterNodeId = "N002",
                Priority = 0.5,
                SetAtUtc = secondSet,
                ExpiresAtUtc = secondSet.AddHours(1)
            });

            BrainDoc doc = FactionStore.ToBrainDoc(state, new SpatialNode[0], FactionStore.DefaultLogCap);
            FactionState restored = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(2, restored.CampaignFoci.Count);
            Assert.Equal("N001", restored.CampaignFoci[0].Id);
            Assert.Equal("N001", restored.CampaignFoci[0].CenterNodeId);
            Assert.Equal(0.75, restored.CampaignFoci[0].Priority, 6);
            Assert.Equal(firstSet, restored.CampaignFoci[0].SetAtUtc);
            Assert.Equal(firstSet.AddHours(1), restored.CampaignFoci[0].ExpiresAtUtc);
            Assert.Equal("N002", restored.CampaignFoci[1].Id);
            Assert.Equal("N002", restored.CampaignFoci[1].CenterNodeId);
            Assert.Equal(0.5, restored.CampaignFoci[1].Priority, 6);
            Assert.Equal(secondSet, restored.CampaignFoci[1].SetAtUtc);
            Assert.Equal(secondSet.AddHours(1), restored.CampaignFoci[1].ExpiresAtUtc);
        }
        [Fact]
        public void CampaignFocusCapacity_ScalesFromBaselineAndStopsAtTuningMaximum()
        {
            var tuning = new FactionTuning
            {
                CampaignFocusBaseCapacity = 1,
                CampaignFocusCombatPowerPerAdditional = 100,
                CampaignFocusMaxConcurrent = 3
            };

            Assert.Equal(1, tuning.CampaignFocusCapacity(0));
            Assert.Equal(1, tuning.CampaignFocusCapacity(99));
            Assert.Equal(2, tuning.CampaignFocusCapacity(100));
            Assert.Equal(3, tuning.CampaignFocusCapacity(200));
            Assert.Equal(3, tuning.CampaignFocusCapacity(100000));
        }

        [Fact]
        public void CampaignFocusLifecycle_AllowsPartialOverlapButRejectsDuplicateAndFullySubsumedFocus()
        {
            var tuning = new FactionTuning
            {
                CampaignFocusBaseCapacity = 3,
                CampaignFocusMaxConcurrent = 3,
                CampaignFocusCombatPowerPerAdditional = 100,
                CampaignCooldown = TimeSpan.FromMinutes(10)
            };
            var state = new FactionState { Tag = "ACME" };
            DateTime now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N001",
                new System.Collections.Generic.HashSet<string> { "N001", "N002" }));
            Assert.True(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N003",
                new System.Collections.Generic.HashSet<string> { "N002", "N003" }));
            Assert.False(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N003",
                new System.Collections.Generic.HashSet<string> { "N003" }));
            Assert.False(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N002",
                new System.Collections.Generic.HashSet<string> { "N001", "N002" }));

            Assert.Equal(2, state.CampaignFoci.Count);
            Assert.All(state.CampaignFoci, focus =>
                Assert.Contains(focus.Objectives, objective => objective.Kind == CampaignObjectiveKind.DefendRegion));
            Assert.Equal(new[] { "N001", "N002", "N003" }, state.CampaignFocusRegionMembers.OrderBy(id => id));
        }

        private static CampaignFocus OffensiveFocus(string id, string enemyNode, DateTime now)
        {
            var f = new CampaignFocus { Id = id, CenterNodeId = enemyNode, SetAtUtc = now, ExpiresAtUtc = now.AddHours(2) };
            f.RegionMembers.Add(enemyNode);
            f.Objectives.Add(new CampaignObjective { Kind = CampaignObjectiveKind.Terrorise, TargetNodeId = enemyNode });
            return f;
        }

        /// <summary>Live 2026-10-08: SPRT held four terror foci, the defensive cap is three, and every
        /// defensive campaign it tried was refused, so it flew no patrols for days. An offence does not
        /// take a defensive slot (the rule OffensiveCampaigns states).</summary>
        [Fact]
        public void OffensiveFoci_DoNotTakeADefensiveSlot()
        {
            var tuning = new FactionTuning
            {
                CampaignFocusBaseCapacity = 3, CampaignFocusMaxConcurrent = 3,
                CampaignFocusCombatPowerPerAdditional = 100, CampaignCooldown = TimeSpan.FromMinutes(10)
            };
            DateTime now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "SPRT" };
            for (int i = 0; i < 4; i++) state.CampaignFoci.Add(OffensiveFocus("terror:" + i, "E" + i, now));

            Assert.True(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N010",
                new System.Collections.Generic.HashSet<string> { "N010", "N011" }));
            Assert.Equal(1, CampaignFocusLifecycle.DefensiveFocusCount(state));
            Assert.Equal(5, state.CampaignFoci.Count);
        }

        [Fact]
        public void DefensiveFoci_StillStopAtTheCapacity()
        {
            var tuning = new FactionTuning
            {
                CampaignFocusBaseCapacity = 2, CampaignFocusMaxConcurrent = 2,
                CampaignFocusCombatPowerPerAdditional = 100, CampaignCooldown = TimeSpan.FromMinutes(10)
            };
            DateTime now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "SPRT" };
            state.CampaignFoci.Add(OffensiveFocus("terror:0", "E0", now));
            Assert.True(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N001", new System.Collections.Generic.HashSet<string> { "N001" }));
            Assert.True(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N002", new System.Collections.Generic.HashSet<string> { "N002" }));

            Assert.False(CampaignFocusLifecycle.TryStart(state, tuning, 0, now, "N003", new System.Collections.Generic.HashSet<string> { "N003" }));
            Assert.Equal(2, CampaignFocusLifecycle.DefensiveFocusCount(state));
        }

        [Fact]
        public void LegacySingletonMigratesToDefensiveObjectiveAndNewSavesUseOnlyCollection()
        {
            DateTime legacySet = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
            var legacy = new BrainDoc
            {
                Tag = "ACME",
                CampaignFocusRegion = "N001",
                CampaignFocusPriority = 0.75,
                CampaignSetAtUtc = legacySet,
                CampaignFocusRegionMembers = new System.Collections.Generic.List<string> { "N001", "N002" }
            };

            FactionState state = FactionStore.ApplyBrainDoc(legacy, new SpatialGraph());

            CampaignFocus focus = Assert.Single(state.CampaignFoci);
            Assert.Equal("N001", focus.Id);
            Assert.Equal("N001", focus.CenterNodeId);
            Assert.Equal(legacySet.AddHours(1), focus.ExpiresAtUtc);
            Assert.Equal(new[] { "N001", "N002" }, focus.RegionMembers.OrderBy(id => id));
            CampaignObjective objective = Assert.Single(focus.Objectives);
            Assert.Equal(CampaignObjectiveKind.DefendRegion, objective.Kind);
            Assert.Equal("N001", objective.TargetNodeId);

            BrainDoc saved = FactionStore.ToBrainDoc(state, new SpatialNode[0], FactionStore.DefaultLogCap);
            Assert.Null(saved.CampaignFocusRegion);
            Assert.Null(saved.CampaignFocusPriority);
            Assert.Null(saved.CampaignSetAtUtc);
            Assert.Null(saved.CampaignFocusRegionMembers);
            Assert.Single(saved.CampaignFoci);
            Assert.Single(saved.CampaignFoci[0].Objectives);
            Assert.Equal("DefendRegion", saved.CampaignFoci[0].Objectives[0].Kind);

            // Current singleton consumers must still observe the migrated focus after a second
            // hydration, until FleetManager/Host/Sim are collection-aware.
            FactionState reloaded = FactionStore.ApplyBrainDoc(saved, new SpatialGraph());
            Assert.Equal("N001", reloaded.CampaignFocusRegion);
            Assert.Equal(0.75, reloaded.CampaignFocusPriority, 6);
            Assert.Equal(legacySet, reloaded.CampaignSetAtUtc);
            Assert.Equal(new[] { "N001", "N002" }, reloaded.CampaignFocusRegionMembers.OrderBy(id => id));
        }
    }
}
