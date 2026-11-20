using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Role-derived build rank. A shipyard works <see cref="SpatialNode.BuildSlots"/> (one)
    /// of <see cref="SpatialNode.OrderedBuildQueue"/> at a time, so before this every entry sat at
    /// rank 0 and pure creation order decided which hull a starved yard actually built.
    ///
    /// <para>Live 2026-08-31: ISBT's N007 held five entries — Raid, StoreSmall, StoreLarge, Trade,
    /// Mine — all at rank 0, and the Mine hull that would have unblocked its iron chain sat behind
    /// two shop hulls nothing was waiting on. See FAC-204.</para></summary>
    public sealed class BuildRolePriorityTests
    {
        private static WorldModel MakeWorld()
        {
            var w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return w;
        }

        private static BuildQueueEntry Entry(string role, WorldModel w, int seq)
        {
            var e = new BuildQueueEntry
            {
                Id = role + ":" + seq.ToString("D3"), Role = role, PrefabId = role + "Hull",
                CreatedUtc = w.UtcNow, ExpiresUtc = w.UtcNow.AddHours(24),
                Priority = ShipyardManager.RolePriority(role, w.Tuning),
            };
            e.Bom["Comp:SteelPlate"] = 100;
            return e;
        }

        [Fact]
        public void TheRankedOrderIsMineTradePatrolRaidStoreSmallStoreLarge()
        {
            FactionTuning t = TestSupport.Tuning();
            int[] ranks =
            {
                ShipyardManager.RolePriority("Mine", t),
                ShipyardManager.RolePriority("Trade", t),
                ShipyardManager.RolePriority("Patrol", t),
                ShipyardManager.RolePriority("Raid", t),
                ShipyardManager.RolePriority("StoreSmall", t),
                ShipyardManager.RolePriority("StoreLarge", t),
            };

            for (int i = 1; i < ranks.Length; i++)
                Assert.True(ranks[i - 1] > ranks[i],
                    "rank at " + (i - 1) + " (" + ranks[i - 1] + ") must outrank rank at " + i + " (" + ranks[i] + ")");
            // Every buildable role is ranked above the 0 an unranked one keeps, so the table is
            // complete rather than merely ordered.
            Assert.All(ranks, r => Assert.True(r > 0));
        }

        [Fact]
        public void EveryBuildableRoleHasARank_SoNoneSilentlyFallsToZero()
        {
            FactionTuning t = TestSupport.Tuning();
            foreach (string role in ShipyardManager.PlayerQueueRoles)
                Assert.True(ShipyardManager.RolePriority(role, t) > 0,
                    "buildable role '" + role + "' has no rank and would sit at 0");
        }

        [Fact]
        public void AnUnknownRoleAndAbsentTuning_BothReadAsZero_NotAsAGuess()
        {
            Assert.Equal(0, ShipyardManager.RolePriority("Gift", TestSupport.Tuning()));
            Assert.Equal(0, ShipyardManager.RolePriority("Mine", null));
            Assert.Equal(0, ShipyardManager.RolePriority("Mine", new FactionTuning { BuildRolePriority = null }));
        }

        /// <summary>A build named by hull carries no role. A player or a doctrine line asked for that
        /// hull by name, so it starts with the highest role rather than below every automatic build.</summary>
        [Fact]
        public void ABuildNamedByHull_StartsAtTheHighestRoleRank()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.True(t.HighestBuildRolePriority() > 0);
            Assert.Equal(t.HighestBuildRolePriority(), ShipyardManager.RolePriority(null, t));
            Assert.Equal(t.StoreShipBuildPriority, ShipyardManager.RolePriority(null, t, destinedForSale: true));
            Assert.Equal(0, ShipyardManager.RolePriority(null, new FactionTuning { BuildRolePriority = null }));
        }

        /// <summary>The behaviour that matters: a yard works one build, and it is the Mine hull even
        /// when every shop hull was queued first.</summary>
        [Fact]
        public void MineHullTakesTheOneWorkingSlot_EvenWhenQueuedLast()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N007");
            node.OwnerFactionTag = "F";

            // ISBT/N007's real queue order on 2026-08-31.
            node.BuildQueue.Add(Entry("Raid", w, 1));
            node.BuildQueue.Add(Entry("StoreSmall", w, 2));
            node.BuildQueue.Add(Entry("StoreLarge", w, 3));
            node.BuildQueue.Add(Entry("Trade", w, 4));
            node.BuildQueue.Add(Entry("Mine", w, 5));

            List<BuildQueueEntry> ordered = node.OrderedBuildQueue();
            Assert.Equal(new[] { "Mine", "Trade", "Raid", "StoreSmall", "StoreLarge" },
                         ordered.Select(e => e.Role).ToArray());

            BuildQueueEntry working = ordered[0];
            Assert.Equal("Mine", working.Role);
            Assert.True(node.IsActiveBuild(working));
            // And the shop hulls are not merely behind it — they are not being worked at all.
            Assert.False(node.IsActiveBuild(ordered.Single(e => e.Role == "StoreLarge")));
            Assert.False(node.IsActiveBuild(ordered.Single(e => e.Role == "StoreSmall")));
        }

        /// <summary>The discriminator: at the old all-zero default the same queue is decided by
        /// creation order, and the Mine hull queued last is the one that never runs.</summary>
        [Fact]
        public void AtTheOldAllZeroDefault_CreationOrderDecided_AndMineLost()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N007");
            node.OwnerFactionTag = "F";

            foreach (string role in new[] { "Raid", "StoreSmall", "StoreLarge", "Trade", "Mine" })
            {
                var e = new BuildQueueEntry
                {
                    Id = role + ":x", Role = role, PrefabId = role + "Hull",
                    CreatedUtc = w.UtcNow, ExpiresUtc = w.UtcNow.AddHours(24),
                    // Priority deliberately left at the struct default -- the pre-change world.
                };
                e.Bom["Comp:SteelPlate"] = 100;
                node.BuildQueue.Add(e);
            }

            Assert.Equal("Raid", node.OrderedBuildQueue()[0].Role);
            Assert.False(node.IsActiveBuild(node.BuildQueue.Single(e => e.Role == "Mine")));
        }

        /// <summary>A rank set by hand afterwards survives: the role table is a starting rank, not a
        /// policy that re-derives itself.</summary>
        [Fact]
        public void APlayerSetRankIsNotOverwritten_ByTheRoleTable()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N007");
            node.OwnerFactionTag = "F";
            node.BuildQueue.Add(Entry("Mine", w, 1));
            node.BuildQueue.Add(Entry("StoreLarge", w, 2));

            var log = new List<string>();
            PlayerQueueResult result = new ShipyardManager()
                .SetBuildPriority(w, "F", "N007", "StoreLarge:002", 99, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            Assert.Equal("StoreLarge", node.OrderedBuildQueue()[0].Role);
            Assert.Contains(log, l => l.Contains("BUILD_PRIORITY_BY_PLAYER") && l.Contains("from=1 to=99"));
        }

        /// <summary>The install lever keeps its meaning: `develop &lt;cap&gt; priority 1` still
        /// outranks an ordinary build, rather than silently losing to a role rank the operator
        /// cannot see.</summary>
        [Fact]
        public void ASetInstallRankIsLiftedClearOfEveryBuildRank()
        {
            FactionTuning t = TestSupport.Tuning();
            var st = new FactionState { Tag = "F" };
            st.InstallPriority[NodeCapabilityKind.RefineryII] = 1;

            int install = st.InstallRankFor(NodeCapabilityKind.RefineryII, t);
            int highestBuild = ShipyardManager.PlayerQueueRoles.Max(r => ShipyardManager.RolePriority(r, t));

            // A build blocks an install at Priority >= installRank, so clearing the highest build
            // rank is what "priority 1 wins" requires.
            Assert.True(install > highestBuild,
                "install rank " + install + " must clear the highest build rank " + highestBuild);
        }

        [Fact]
        public void AnUnsetInstallStaysAtZero_SoItStillLosesToEveryBuild()
        {
            FactionTuning t = TestSupport.Tuning();
            var st = new FactionState { Tag = "F" };

            // Lifting an unset capability would have an install nobody ranked start beating builds --
            // the opposite of the all-default behaviour this preserves.
            Assert.Equal(0, st.InstallRankFor(NodeCapabilityKind.RefineryII, t));

            // An explicit 0 reads as "do not prioritise" and is left alone too.
            st.InstallPriority[NodeCapabilityKind.RefineryII] = 0;
            Assert.Equal(0, st.InstallRankFor(NodeCapabilityKind.RefineryII, t));
        }

        [Fact]
        public void WithoutTuning_TheRankIsUnliftedSoOlderCallersAreUnaffected()
        {
            var st = new FactionState { Tag = "F" };
            st.InstallPriority[NodeCapabilityKind.RefineryII] = 3;
            Assert.Equal(3, st.InstallRankFor(NodeCapabilityKind.RefineryII));
        }

        [Fact]
        public void HighestBuildRolePriority_TracksTheTable_AndIsZeroWhenAbsent()
        {
            Assert.Equal(6, TestSupport.Tuning().HighestBuildRolePriority());
            Assert.Equal(0, new FactionTuning { BuildRolePriority = null }.HighestBuildRolePriority());
            Assert.Equal(0, new FactionTuning { BuildRolePriority = new Dictionary<string, int>() }.HighestBuildRolePriority());
        }

        /// <summary>The ranks stay in single digits so the install lever remains reachable: an
        /// install that wants to outrank the highest build needs one step above it, not a number
        /// nobody would think to try.</summary>
        [Fact]
        public void RanksStayLowEnoughThatInstallPriorityCanStillWin()
        {
            FactionTuning t = TestSupport.Tuning();
            int highest = ShipyardManager.PlayerQueueRoles.Max(r => ShipyardManager.RolePriority(r, t));
            Assert.True(highest < 10, "highest build rank " + highest + " pushes InstallPriority out of single digits");
        }
    }
}
