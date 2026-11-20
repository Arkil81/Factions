using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Verifies the arrival-time Trade settlement mechanic (2026-08-02 push-economy fix):
    ///   - Items settle in destination-scarcity order, capped by the recipient's real-time
    ///     affordability (never at dispatch time).
    ///   - A recipient is never pushed below InsolvencyFloor by one convoy.
    ///   - A windowed, income-relative inbound budget (shared across every sender) additionally
    ///     protects a recipient's spending capacity from a trade flood.
    ///   - Unsold cargo (scarcity-gated or budget-exhausted) returns to the convoy's origin node.
    ///   - A tracked recipient (FactionState present) has the buyer-side charge threaded into
    ///     GetNetFlow via WorldModel.Spend, symmetric with the seller's existing Earn.
    ///   - An untracked recipient (no FactionState) keeps the old unconditional free-delivery
    ///     behaviour verbatim.
    /// </summary>
    public class TradeSettlementTests
    {
        // ---- helpers -----------------------------------------------------------------------

        private static WorldModel MakeWorld() => TestSupport.World();

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
            double hold = 0.9, double value = 0.5)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionState MakeState(string tag) => new FactionState { Tag = tag, RngSeed = 1 };

        private static PendingFleetAction MakeTradeFleet(string actor, string targetTag, string targetNodeId,
            string originNodeId, DateTime now, Dictionary<string, long> cargo)
            => new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                ActorTag = actor,
                TargetTag = targetTag,
                TargetNodeId = targetNodeId,
                OriginNodeId = originNodeId,
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                Cargo = cargo,
            };

        /// <summary>Scarcity per inventory key. The settlement gate reads the concrete key, so a
        /// fake that answered only by kind — and a flat 1.0 per key, as this one did — would leave
        /// every scarcity assertion below passing on a value the production path never consults.
        /// Kinds are accepted at construction for readability and expanded to their subtypes here.</summary>
        private sealed class FixedScarcityOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private static readonly Dictionary<ItemKind, string[]> KindSubTypes =
                new Dictionary<ItemKind, string[]>
                {
                    { ItemKind.Ore,           new[] { "Iron", "Nickel", "Cobalt", "Silicon", "Magnesium", "Stone", "Ice" } },
                    { ItemKind.ProcessedGood, new[] { "SteelPlate", "Construction", "Computer", "MetalGrid" } },
                };

            private readonly Dictionary<string, double> _byKey =
                new Dictionary<string, double>(StringComparer.Ordinal);

            public FixedScarcityOracle(Dictionary<ItemKind, double> byKind)
            {
                foreach (var kv in byKind)
                {
                    string[] subTypes;
                    if (!KindSubTypes.TryGetValue(kv.Key, out subTypes)) continue;
                    foreach (string s in subTypes) _byKey[s] = kv.Value;
                }
            }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            {
                reference = 0;
                return false;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double v;
                return _byKey.TryGetValue(inventoryKey, out v) ? v : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                reference = 0;
                return false;
            }
        }

        // ---- floor-headroom gating ----------------------------------------------------------

        [Fact]
        public void Trade_RecipientNearFloor_OnlyAffordablePortionSells_RestReturnsToOrigin()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            // Floor is -50000 by default; give B only 3000 of headroom above it.
            w.SetTreasury("B", w.Tuning.InsolvencyFloor + 3000);

            var cargo = new Dictionary<string, long> { { "SteelPlate", 1000 } }; // ProcessedGood, fallback 10cr/unit
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            // 3000 headroom / 10cr = 300 units sold; 700 returned.
            Assert.Equal(300L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));
            Assert.Equal(700L, w.GetNodeInventory("A1", ItemKind.ProcessedGood));
            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("B"));
            Assert.Contains("settled=3000", string.Join("\n", log));
            Assert.Contains("returned=7000", string.Join("\n", log));
        }

        // ---- the sale term ---------------------------------------------------------------------

        [Fact]
        public void Trade_SoldUnitsAreCreditedToTheSellersNode_NotTheBuyers()
        {
            // "How much is being bought FROM us" -- it is the seller's shelf whose target should
            // rise, so a settlement must credit the origin. Tested at the settlement itself rather
            // than against WorldModel.RecordUnitsSold: a direct call on the helper cannot tell the
            // two nodes apart, and swapping the argument here reds nothing without this.
            WorldModel w = MakeWorld();
            SpatialNode seller = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            SpatialNode buyer  = AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000);

            var cargo = new Dictionary<string, long> { { "SteelPlate", 1000 } };
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", w.UtcNow, cargo));

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { stA, stB }, w, w.Tuning,
                new SeededRandom(1), new List<string>());

            // The discriminator: the trade has to have actually settled, or an empty SalesHistory on
            // the buyer is a pass on a sale that never happened.
            Assert.Equal(1000L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));

            double sold;
            Assert.True(seller.SalesHistory.TryGetValue("SteelPlate", out sold),
                "the seller's node must carry the sale");
            Assert.Equal(1000.0, sold);
            Assert.Empty(buyer.SalesHistory);
        }

        // ---- scarcity gating ------------------------------------------------------------------

        [Fact]
        public void Trade_ItemBelowScarcityFloor_NeverSold_ReturnsToOrigin()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000); // ample — floor headroom is not the constraint here

            var oracle = new FixedScarcityOracle(new Dictionary<ItemKind, double> { { ItemKind.Ore, 0.3 } }); // below default 0.5 floor

            var cargo = new Dictionary<string, long> { { "Iron", 500 } }; // Ore
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, oracle: oracle);

            Assert.Equal(0L, w.GetNodeInventory("B1", ItemKind.Ore));
            Assert.Equal(500L, w.GetNodeInventory("A1", ItemKind.Ore));
            Assert.Equal(1_000_000L, w.GetTreasury("B"));
            Assert.Contains("settled=0", string.Join("\n", log));
        }

        [Fact]
        public void Trade_MixedScarcity_HigherScarcitySellsFirst_PartialOnSecondWhenBudgetLimited()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            // Headroom: 100 (Iron, full) + 500 (half of SteelPlate) = 600.
            w.SetTreasury("B", w.Tuning.InsolvencyFloor + 600);

            var oracle = new FixedScarcityOracle(new Dictionary<ItemKind, double>
            {
                { ItemKind.Ore, 2.0 },          // most scarce — sells first
                { ItemKind.ProcessedGood, 1.0 }, // still above the 0.5 floor, sells second
            });

            var cargo = new Dictionary<string, long> { { "Iron", 100 }, { "SteelPlate", 100 } };
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, oracle: oracle);

            Assert.Equal(100L, w.GetNodeInventory("B1", ItemKind.Ore));         // fully sold, higher scarcity
            Assert.Equal(50L, w.GetNodeInventory("B1", ItemKind.ProcessedGood)); // partial — budget ran out
            Assert.Equal(50L, w.GetNodeInventory("A1", ItemKind.ProcessedGood)); // remainder returned
            Assert.Equal(w.Tuning.InsolvencyFloor, w.GetTreasury("B"));
        }

        // ---- windowed inbound budget -----------------------------------------------------------

        [Fact]
        public void Trade_WindowedInboundBudget_BindsBeforeFloorHeadroom()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 10_000_000); // floor headroom is not the constraint
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 300; // small, income-independent window budget

            var budget = new SpendBudget();
            var cargo = new Dictionary<string, long> { { "SteelPlate", 100 } }; // value 1000 at fallback 10cr
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, tradeInboundBudget: budget);

            Assert.Equal(30L, w.GetNodeInventory("B1", ItemKind.ProcessedGood)); // 300 / 10cr
            Assert.Equal(70L, w.GetNodeInventory("A1", ItemKind.ProcessedGood));
            Assert.Equal(10_000_000L - 300L, w.GetTreasury("B"));
        }

        [Fact]
        public void Trade_WindowedInboundBudget_AggregatesAcrossSenders()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "C1", "C", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stC = MakeState("C");
            var stB = MakeState("B");
            w.SetTreasury("B", 10_000_000);
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 1000; // exactly one convoy's worth

            var budget = new SpendBudget();
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 100 } })); // value 1000
            stC.PendingFleets.Add(MakeTradeFleet("C", "B", "B1", "C1", now,
                new Dictionary<string, long> { { "SteelPlate", 100 } })); // value 1000

            var states = new List<FactionState> { stA, stC, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, tradeInboundBudget: budget);

            // Only one of the two convoys' worth of value can clear the shared 1000-credit window —
            // the first sender processed gets through in full, the second is entirely throttled back
            // to its own origin, proving the budget is keyed on the recipient, not per-sender.
            long delivered = w.GetNodeInventory("B1", ItemKind.ProcessedGood);
            Assert.Equal(100L, delivered);
            long returnedToA = w.GetNodeInventory("A1", ItemKind.ProcessedGood);
            long returnedToC = w.GetNodeInventory("C1", ItemKind.ProcessedGood);
            Assert.Equal(100L, returnedToA + returnedToC);
            Assert.Equal(10_000_000L - 1000L, w.GetTreasury("B"));
        }

        // ---- untracked recipient regression -----------------------------------------------------

        [Fact]
        public void Trade_RecipientStateNull_FullUnconditionalDeliveryPreserved()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0)); // node exists, but no FactionState "B" in states

            var stA = MakeState("A");
            var cargo = new Dictionary<string, long> { { "SteelPlate", 100 } };
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA }; // no "B" FactionState at all
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            // Full delivery, no charge — matches the pre-existing behaviour for an untracked recipient.
            Assert.Equal(100L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));
            Assert.Equal(0L, w.GetTreasury("B"));
            Assert.Equal(0L, w.GetTreasury("A"));
        }

        // ---- buyer-side expense visibility ------------------------------------------------------

        [Fact]
        public void Trade_BuyerSideCharge_VisibleInRecipientNetFlow()
        {
            WorldModel w = MakeWorld();
            w.Tuning.UpkeepPerSource = 0; // isolate the trade charge from station upkeep
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000);

            var cargo = new Dictionary<string, long> { { "SteelPlate", 100 } }; // value 1000
            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now, cargo));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            w.AccrueIncome(states);

            Assert.Equal(-1000L, w.GetNetFlow("B"));
        }

        // ---- no-cargo flat-fee fallback path (regression) ---------------------------------------

        [Fact]
        public void Trade_NoCargoManifest_FlatFeeStillSettles_ViaSpend()
        {
            WorldModel w = MakeWorld();
            w.Tuning.UpkeepPerSource = 0;
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("A", 10000);
            w.SetTreasury("B", 5000);

            DateTime now = w.UtcNow;
            var fleet = MakeTradeFleet("A", "B", "B1", "A1", now, null);
            fleet.CargoValue = 4000;
            stA.PendingFleets.Add(fleet);

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            Assert.Equal(14000L, w.GetTreasury("A"));
            Assert.Equal(1000L, w.GetTreasury("B"));

            w.AccrueIncome(states);
            Assert.Equal(-4000L, w.GetNetFlow("B"));
        }
        // ---- the dock reads the item, not its kind (FAC-286) -----------------------------------

        /// <summary>Scarcity per inventory key, with no kind fallback — the shape every consumer
        /// now reads.</summary>
        private sealed class KeyScarcityOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private readonly Dictionary<string, double> _byKey;
            public KeyScarcityOracle(Dictionary<string, double> byKey) { _byKey = byKey; }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double v;
                return _byKey.TryGetValue(inventoryKey, out v) ? v : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            { reference = 0; return false; }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            { reference = 0; return false; }
        }

        /// <summary>One convoy, two subtypes of one ItemKind, opposite verdicts: the destination is
        /// short of cobalt and glutted in stone, and both are <c>ItemKind.Ore</c>. No kind-level read
        /// can produce this — it has one number for both — which is why EXP's shipyard, sitting on
        /// 107,094 stone/iron/silicon ingots, refused every gram of gold flown to it: 0 of 308
        /// convoys over three days while its sibling nodes accepted freely (FAC-286).</summary>
        [Fact]
        public void Trade_TwoSubtypesOfOneKind_SettleIndependently()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000);   // ample: no budget gate can be the refuser here

            var oracle = new KeyScarcityOracle(new Dictionary<string, double>
            {
                { "Cobalt", 2.5 },   // short of it
                { "Stone",  0.3 },   // glutted in it, on the oversupply floor
            });

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "Cobalt", 100 }, { "Stone", 200 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, oracle: oracle);

            Assert.Equal(100L, w.GetNodeInventory("B1", "Cobalt"));   // wanted: delivered
            Assert.Equal(0L,   w.GetNodeInventory("A1", "Cobalt"));
            Assert.Equal(0L,   w.GetNodeInventory("B1", "Stone"));    // glutted: refused
            Assert.Equal(200L, w.GetNodeInventory("A1", "Stone"));
            Assert.Contains("refused_floor=200", string.Join("\n", log));
        }

        // ---- the inbound window can draw on treasury above the reserve --------------------------

        /// <summary>A cash-rich recipient clears an order its income alone could never pay for. The
        /// inbound budget was the only one of the five windowed budgets whose call sites passed no
        /// wealth term at all, so a faction holding billions committed nothing but income to goods it
        /// had itself asked for — 138 of 235 settlements refused on the window (FAC-286).</summary>
        [Fact]
        public void Trade_RichRecipient_DrawsOnTreasurySurplus_AndClearsTheWholeOrder()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 300;          // income + floor buys 30 of 1000 units
            w.Tuning.ReserveTreasury = 1_000_000;
            w.Tuning.TradeInboundWealthFractionPerWindow = 0.01;
            // Surplus 9,000,000 x 0.01 = 90,000, far more than the 10,000 the order costs.
            w.SetTreasury("B", 10_000_000);

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 1000 } }));   // 10cr each

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log,
                tradeInboundBudget: new SpendBudget());

            Assert.Equal(1000L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));
            Assert.Equal(0L, w.GetNodeInventory("A1", ItemKind.ProcessedGood));
            Assert.DoesNotContain("refused_budget=", string.Join("\n", log));
        }

        /// <summary>The mirror, and the reason the term is anchored on the surplus rather than on
        /// treasury: a faction at or below its operational reserve gets no wealth allowance at all
        /// and is refused exactly as before. Without this the test above would pass for a poor
        /// faction too, which is the failure mode the reserve exists to prevent.</summary>
        [Fact]
        public void Trade_RecipientBelowTheReserve_GetsNoWealthAllowance()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 300;
            w.Tuning.ReserveTreasury = 1_000_000;
            w.Tuning.TradeInboundWealthFractionPerWindow = 0.01;
            w.SetTreasury("B", 900_000);                      // below the reserve: surplus is 0

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 1000 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log,
                tradeInboundBudget: new SpendBudget());

            Assert.Equal(30L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));   // the 300 floor only
            Assert.Contains("refused_budget=970", string.Join("\n", log));
        }

        /// <summary>Zero switches the term off, so the pre-change behaviour is still reachable and
        /// the two tests above cannot be passing on something other than the wealth term.</summary>
        [Fact]
        public void Trade_WealthFractionZero_RestoresTheIncomeOnlyWindow()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 300;
            w.Tuning.ReserveTreasury = 1_000_000;
            w.Tuning.TradeInboundWealthFractionPerWindow = 0;   // off
            w.SetTreasury("B", 10_000_000);                     // rich, and it must not matter

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 1000 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log,
                tradeInboundBudget: new SpendBudget());

            Assert.Equal(30L, w.GetNodeInventory("B1", ItemKind.ProcessedGood));
            Assert.Contains("refused_budget=970", string.Join("\n", log));
        }

        // ---- refusal attribution on the resolve line (FAC-286) ----------------------------------

        // The defect these exist for: `settled=0 returned=N` was emitted identically whether the
        // dock refused on the oversupply floor or ran out of money, so three days of live logs could
        // not say which gate ate the cargo. Each test below asserts the label AND the absence of the
        // other labels -- a counter that fires for every refusal would pass the first half alone.

        [Fact]
        public void Trade_FloorRefusal_IsLabelledFloor_NotBudget()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000);   // ample: neither budget gate can be the refuser

            var oracle = new FixedScarcityOracle(new Dictionary<ItemKind, double> { { ItemKind.Ore, 0.3 } });

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "Iron", 500 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, oracle: oracle);

            string line = string.Join("\n", log);
            Assert.Contains("settled=0", line);
            Assert.Contains("refused_floor=500", line);
            Assert.DoesNotContain("refused_budget=", line);
            Assert.DoesNotContain("refused_headroom=", line);
            Assert.DoesNotContain("refused_other=", line);
        }

        [Fact]
        public void Trade_HeadroomRefusal_IsLabelledHeadroom_NotFloor()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", w.Tuning.InsolvencyFloor + 3000);   // 300 of 1000 units affordable

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 1000 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            string line = string.Join("\n", log);
            Assert.Contains("returned=7000", line);
            Assert.Contains("refused_headroom=700", line);
            Assert.DoesNotContain("refused_floor=", line);
            Assert.DoesNotContain("refused_other=", line);
        }

        [Fact]
        public void Trade_WindowRefusal_IsLabelledBudget_NotHeadroom()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 10_000_000);   // headroom is not the constraint; the window is
            stB.CentralBankRecentIncome = 0;
            w.Tuning.TradeInboundMaxIncomeFractionPerWindow = 0;
            w.Tuning.TradeInboundBudgetFloor = 300;

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 100 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log,
                tradeInboundBudget: new SpendBudget());

            string line = string.Join("\n", log);
            Assert.Contains("refused_budget=70", line);
            Assert.DoesNotContain("refused_headroom=", line);
            Assert.DoesNotContain("refused_floor=", line);
            Assert.DoesNotContain("refused_other=", line);
        }

        [Fact]
        public void Trade_FloorStop_AttributesTheItemsBehindItToTheFloorToo()
        {
            // The walk stops at the first item on the floor and everything sorted after it rides
            // home. Those units belong to the floor, not to whatever gate is checked next.
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 1_000_000);

            var oracle = new FixedScarcityOracle(new Dictionary<ItemKind, double>
            {
                { ItemKind.Ore, 0.3 },            // on the floor, stops the walk
                { ItemKind.ProcessedGood, 0.4 },  // also on the floor, sorted behind it
            });

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "Iron", 500 }, { "SteelPlate", 200 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log, oracle: oracle);

            string line = string.Join("\n", log);
            Assert.Contains("refused_floor=700", line);   // 500 + 200, both attributed to the floor
            Assert.DoesNotContain("refused_other=", line);
        }

        [Fact]
        public void Trade_FullySettledConvoy_CarriesNoRefusalCounters()
        {
            // The negative control: if any counter fired on a clean settlement, every assertion of
            // absence above would be passing for the wrong reason.
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var stA = MakeState("A");
            var stB = MakeState("B");
            w.SetTreasury("B", 10_000_000);

            DateTime now = w.UtcNow;
            stA.PendingFleets.Add(MakeTradeFleet("A", "B", "B1", "A1", now,
                new Dictionary<string, long> { { "SteelPlate", 10 } }));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), log);

            string line = string.Join("\n", log);
            Assert.Contains("settled=100", line);
            Assert.DoesNotContain("refused_", line);
            Assert.DoesNotContain("unpriced=", line);
        }
    }
}
