using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-188: the defects the Magnate registration path inherits from defaulting everything the
    /// NPC path sets. None of these had fired live — there is no player faction on this world — so
    /// they are a first-Magnate ambush, and unit tests are the only place they can be caught.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public class PlayerFactionCreationTests : IDisposable
    {
        /// <summary>These traces are per-process statics, so a test that flips one and throws would
        /// leak it into whatever xUnit runs next. Dispose runs after every test in this class.</summary>
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private static WorldModel World(FactionTuning t)
        {
            return new WorldModel { Tuning = t, UtcNow = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc) };
        }

        // ---- 1. dormancy is unrecoverable, and it must not reach a player ----------------------

        // The pair is the point. An NPC in this exact state SHOULD collapse — that is the designed
        // terminal state — so asserting only the player half would pass just as well against a Step
        // that had stopped collapsing anything at all.
        [Fact]
        public void Collapse_TakesAnNpc_ButNotAPlayerFaction()
        {
            var t = TestSupport.Tuning();
            var w = World(t);

            var npc = new FactionState { Tag = "NPC" };
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            w.SetTreasury("NPC", t.InsolvencyFloor);      // landless and out of credit
            w.SetTreasury("EXP", t.InsolvencyFloor);      // identical situation

            FactionLifecycle.Step(new[] { npc, player }, w, t);

            Assert.True(FactionLifecycle.IsDormant(npc, w.UtcNow),
                "an NPC landless at the floor still collapses -- that half is unchanged");
            Assert.False(FactionLifecycle.IsDormant(player, w.UtcNow),
                "a player faction must not be deregistered for the same condition");
            Assert.Equal(DateTime.MinValue, player.DormantUntilUtc);
        }

        // Why the exemption has to be at the write and not at a revival: there is no revival. Nothing
        // in the tree clears DormantUntilUtc, and re-claiming does not re-register a known tag, so a
        // player faction that ever took this write would be gone permanently.
        [Fact]
        public void APlayerFactionNeverAccumulatesDormancy_OverRepeatedSteps()
        {
            var t = TestSupport.Tuning();
            var w = World(t);
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            w.SetTreasury("EXP", t.InsolvencyFloor);

            for (int i = 0; i < 25; i++) FactionLifecycle.Step(new[] { player }, w, t);

            Assert.False(FactionLifecycle.IsDormant(player, w.UtcNow));
        }

        // ---- 2. the grant gate states the rule it enforces --------------------------------------

        // FAC-188 said the protection today is incidental -- collateral summing over owned nodes and
        // returning 0. It is incidental, but that is not the mechanism: a landless faction never
        // reaches the grant path AT ALL, because the income pass walks `pass.Owners`, which is built
        // per node owner. So it is refused one layer earlier and more absolutely than the ticket
        // describes, and the explicit gate added alongside is defence-in-depth rather than a live
        // branch.
        //
        // This test therefore pins the protection that actually runs, and fails the day the premise
        // changes -- which is exactly the day the explicit gate starts mattering.
        [Fact]
        public void ALandlessFactionNeverReachesTheGrantPath_WhileItsLandedPeerIsGranted()
        {
            var world = TestSupport.World();
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            var npc = new FactionState { Tag = "NPC" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantAmount = 40000;
            world.Tuning.CentralBankGrantTreasuryBuffer = 50000;
            DebugTraces.CentralBankDenials = true;   // per-process static; Dispose restores it
            world.Tuning.CentralBankSupportLoanAmount = 0;

            // The node belongs to NPC. EXP is a freshly registered Magnate before its first claim.
            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "NPC";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;
            node.StationBookValue = 2000000;

            Assert.False(world.OwnsAnyNode("EXP"));
            world.SetTreasury("EXP", -10000);
            world.SetTreasury("NPC", -10000);

            world.AccrueIncome(new[] { player, npc });

            // Positive control first. Without it, "EXP got nothing" is indistinguishable from a bank
            // that did nothing at all -- which is precisely how an earlier cut of this test passed
            // against an empty graph while proving nothing.
            Assert.Contains(world.SnapshotCentralBankEvents(),
                e => e.Type == "grant" && e.FactionTag == "NPC");

            // EXP produces NO central-bank event of any kind: not a grant, and not a denial either,
            // because it was never considered. A denial would mean it had entered the pass.
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(), e => e.FactionTag == "EXP");
            Assert.Equal(-10000, world.GetTreasury("EXP"));
        }

        // The gate itself, tested directly, since the branch above it is unreachable. It is about
        // territory and not about being a player: an NPC owning nothing fails it identically.
        [Fact]
        public void OwnsAnyNode_TracksTerritory_NotFactionKind()
        {
            var world = TestSupport.World();
            Assert.False(world.OwnsAnyNode("EXP"));
            Assert.False(world.OwnsAnyNode("NPC"));
            Assert.False(world.OwnsAnyNode(null));

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 0.5;

            Assert.True(world.OwnsAnyNode("EXP"));
            Assert.False(world.OwnsAnyNode("NPC"));
        }

        // ---- 3. the RNG seed is derived, and derived in one place --------------------------------

        [Fact]
        public void DeriveRngSeed_IsStableAndDistinct_AndNonZeroForRealIds()
        {
            long a = 144115188075855881L, b = 144115188075855882L;

            Assert.Equal(FactionLifecycle.DeriveRngSeed(a), FactionLifecycle.DeriveRngSeed(a));
            Assert.NotEqual(FactionLifecycle.DeriveRngSeed(a), FactionLifecycle.DeriveRngSeed(b));
            Assert.NotEqual(0, FactionLifecycle.DeriveRngSeed(a));
        }
    }
}
