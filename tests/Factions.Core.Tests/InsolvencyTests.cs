using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The insolvency lifecycle: the private ledger has a hard floor (a negative credit limit), a
    /// faction pinned at the floor while still bleeding has its worst drain reclaimed by the world
    /// (force-shed), and a fully-collapsed faction sleeps then is reborn with seed capital — never an
    /// unrecoverable runaway negative. The economic half lives in <see cref="WorldModel.AccrueIncome"/>; the
    /// dormancy/rebirth half in <see cref="FactionLifecycle"/>. Both are single-sourced so sim and host agree.</summary>
    public class InsolvencyTests
    {
        private static SpatialNode Owned(WorldModel w, string id, double value, double hold, double x = 0)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), value, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = hold;
            n.StableStationId = id.GetHashCode();
            return n;
        }

        // The bleed can never run away: a faction holding only a money-losing source is clamped at the floor.
        [Fact]
        public void Floor_BoundsTheBleed_NeverRunsAway()
        {
            var w = TestSupport.World();
            w.Tuning.CentralBankGrantAmount = 0;
            Owned(w, "N001", 0.01, 0.5);          // value ~0 -> yield < upkeep -> a net drain
            w.SetTreasury("F", 1000);

            long min = long.MaxValue;
            for (int i = 0; i < 200; i++) { w.AccrueIncome(); min = Math.Min(min, w.GetTreasury("F")); }

            Assert.True(min >= w.Tuning.InsolvencyFloor, "treasury must never fall below the floor");
            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("F")); // pinned at the floor once the drain is shed
        }

        // Force-shed reclaims the SINGLE worst (most-negative) drain, not just any drain, and leaves the rest.
        [Fact]
        public void ForceShed_RemovesTheMostNegativeDrain()
        {
            var w = TestSupport.World();
            w.Tuning.CentralBankGrantAmount = 0;
            Owned(w, "N001", 0.05, 0.5, 0);       // a mild drain (yield ~400 - 1500)
            Owned(w, "N002", 0.00, 0.5, 5000);    // the worst drain (yield 0 - 1500), far enough to stand alone

            string shed = w.ForceShedWorstDrain("F");

            Assert.Equal("N002", shed);
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N002"].OwnerFactionTag); // withdrawn -> handed to UNKN
            Assert.Equal("F", w.Graph.Nodes["N001"].OwnerFactionTag);       // the milder drain is left for next tick
        }

        // A profitable holding is never force-shed (nothing is net-negative).
        [Fact]
        public void ForceShed_NoOp_WhenAllSourcesPay()
        {
            var w = TestSupport.World();
            w.Tuning.CentralBankGrantAmount = 0;
            Owned(w, "N001", 0.9, 0.9);           // valuable, well-held, uncontested -> pays its upkeep and more
            Assert.Null(w.ForceShedWorstDrain("F"));
            Assert.Equal("F", w.Graph.Nodes["N001"].OwnerFactionTag);
        }

        // AccrueIncome integrates floor + force-shed: a faction with several drains is whittled to nothing
        // without ever crossing the floor.
        [Fact]
        public void AccrueIncome_FloorsAndForceSheds_UntilSolventOrEmpty()
        {
            var w = TestSupport.World();
            w.Tuning.CentralBankGrantAmount = 0;
            Owned(w, "N001", 0.0, 0.5, 0);
            Owned(w, "N002", 0.0, 0.5, 5000);
            Owned(w, "N003", 0.0, 0.5, 10000);
            w.SetTreasury("F", 2000);

            long min = long.MaxValue;
            for (int i = 0; i < 30; i++) { w.AccrueIncome(); min = Math.Min(min, w.GetTreasury("F")); }

            Assert.True(min >= w.Tuning.InsolvencyFloor, "the floor holds while the drains are being shed");
            Assert.Empty(w.Graph.OwnedBy("F"));                 // all three drains reclaimed
            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("F"));
        }

        [Fact]
        public void AccrueIncome_DoesNotForceShed_WhenBelowFloorButLedgerStillPositive()
        {
            var w = TestSupport.World();
            w.Tuning.UpkeepPerSource = 1000;
            w.Tuning.DebtInterestRate = 0;
            w.Tuning.LoanAmount = 0;
            w.Tuning.CentralBankReserveRatio = 0;
            w.Tuning.CentralBankGrantAmount = 0;
            w.Tuning.CentralBankSupportLoanAmount = 0;
            w.Tuning.OreYield = 0;
            w.Tuning.IngotYield = 0;
            w.Tuning.ProcessedGoodYield = 0;
            w.Tuning.UpkeepOre = 0;
            w.Tuning.UpkeepIngot = 0;
            w.Tuning.UpkeepProcessedGood = 0;

            var node = Owned(w, "N001", 0.0, 0.5);
            node.Inventory["SteelPlate"] = 2000; // market value keeps the faction asset-solvent
            w.SetTreasury("F", w.Tuning.InsolvencyFloor - 10000);

            w.AccrueIncome();

            Assert.True(w.GetTreasury("F") >= w.Tuning.InsolvencyFloor);
            Assert.Equal("F", node.OwnerFactionTag);
            Assert.True(w.GetLedger("F") > w.Tuning.InsolvencyFloor);
        }

        [Fact]
        public void AccrueIncome_ForceSheds_WhenBelowFloorAndLedgerIsSpent()
        {
            var w = TestSupport.World();
            w.Tuning.UpkeepPerSource = 1000;
            w.Tuning.DebtInterestRate = 0;
            w.Tuning.LoanAmount = 0;
            w.Tuning.CentralBankReserveRatio = 0;
            w.Tuning.CentralBankGrantAmount = 0;
            w.Tuning.CentralBankSupportLoanAmount = 0;

            var node = Owned(w, "N001", 0.0, 0.5);
            w.SetTreasury("F", w.Tuning.InsolvencyFloor - 10000);

            w.AccrueIncome();

            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("F"));
            Assert.Equal(NeutralFaction.Tag, node.OwnerFactionTag); // withdrawn -> handed to UNKN
            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetLedger("F"));
        }

        // Collapse: a landless faction pinned at the floor is conquered -> dormant (indefinitely; not bailed out).
        [Fact]
        public void AccrueIncome_PunitiveAutoBorrow_IsRemoved()
        {
            var w = TestSupport.World();
            w.Tuning.UpkeepPerSource = 1000;
            w.Tuning.DebtInterestRate = 0;
            w.Tuning.LoanAmount = 50000;
            w.Tuning.DebtRatio = 1.0;
            w.Tuning.CentralBankReserveRatio = 0;
            w.Tuning.CentralBankGrantAmount = 0;
            w.Tuning.CentralBankSupportLoanAmount = 0;
            w.Tuning.CentralBankGrantAmount = 0;

            var node = Owned(w, "N001", 0.0, 0.5);
            node.Inventory["Ore:Iron"] = 100000;
            w.SetTreasury("F", w.Tuning.InsolvencyFloor - 10);
            w.SetDebt("F", 1000);

            w.AccrueIncome();

            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("F"));
            Assert.Equal(1000, w.GetDebt("F"));
            Assert.DoesNotContain(w.SnapshotDebtEvents(), e => e.Type == "auto-borrow" && e.FactionTag == "F");
        }

        [Fact]
        public void Collapse_LandlessAtFloor_GoesDormant()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { Tuning = t, UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var s = new FactionState { Tag = "F" };
            w.SetTreasury("F", t.InsolvencyFloor);              // out of credit, owns nothing

            FactionLifecycle.Step(new[] { s }, w, t);

            Assert.True(FactionLifecycle.IsDormant(s, w.UtcNow));
            Assert.Equal(DateTime.MaxValue, s.DormantUntilUtc); // conquered = dormant indefinitely (revival deferred)
        }

        // A faction is NOT put to sleep while it is solvent, or while it still holds territory.
        [Fact]
        public void NotDormant_WhenSolvent_OrStillLanded()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { Tuning = t, UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

            var solvent = new FactionState { Tag = "F" };
            w.SetTreasury("F", t.InsolvencyFloor + 1);          // one credit above the floor
            FactionLifecycle.Step(new[] { solvent }, w, t);
            Assert.False(FactionLifecycle.IsDormant(solvent, w.UtcNow));

            var landed = new FactionState { Tag = "F" };        // at the floor BUT still owns a source
            Owned(w, "N001", 0.0, 0.5);
            w.SetTreasury("F", t.InsolvencyFloor);
            FactionLifecycle.Step(new[] { landed }, w, t);
            Assert.False(FactionLifecycle.IsDormant(landed, w.UtcNow));
        }

        // A conquered faction stays dormant: it is not auto-revived on later ticks (revival is deferred).
        [Fact]
        public void Conquered_StaysDormant_AcrossTicks()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { Tuning = t, UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var s = new FactionState { Tag = "F" };
            w.SetTreasury("F", t.InsolvencyFloor);

            FactionLifecycle.Step(new[] { s }, w, t);           // -> conquered/dormant
            for (int i = 0; i < 500; i++) { w.UtcNow = w.UtcNow.AddMinutes(1); FactionLifecycle.Step(new[] { s }, w, t); }

            Assert.True(FactionLifecycle.IsDormant(s, w.UtcNow));      // still asleep far in the future
            Assert.Equal(t.InsolvencyFloor, w.GetTreasury("F"));      // not bailed out
        }

        // The dormancy marker must survive a reboot: it round-trips through the brain doc (and is omitted for
        // an active faction, so JSON stays lean).
        [Fact]
        public void DormancyTimer_RoundTripsThroughPersistence()
        {
            var dormant = new FactionState { Tag = "F", DormantUntilUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc) };
            BrainDoc doc = FactionStore.ToBrainDoc(dormant, Enumerable.Empty<SpatialNode>(), 10);
            Assert.Equal(dormant.DormantUntilUtc, doc.DormantUntilUtc);
            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(dormant.DormantUntilUtc, back.DormantUntilUtc);

            var active = new FactionState { Tag = "G" };        // never dormant -> omitted from the doc
            Assert.Null(FactionStore.ToBrainDoc(active, Enumerable.Empty<SpatialNode>(), 10).DormantUntilUtc);
            Assert.Equal(DateTime.MinValue, FactionStore.ApplyBrainDoc(new BrainDoc { Tag = "G" }, new SpatialGraph()).DormantUntilUtc);
        }
    }
}
