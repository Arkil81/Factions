using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The shared store rule. These pin the behaviour the live store block had before it was
    /// extracted here, because the extraction is exactly the kind of change that can move a number
    /// while every aggregate still looks plausible.
    /// </summary>
    public class StationListingsTests
    {
        private const string Steel = "Comp:SteelPlate";
        private const string Thrust = "Comp:Thrust";
        private const string IronOre = "Ore:Iron";

        private sealed class Oracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public readonly Dictionary<string, long> Reference = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> Scarcity = new Dictionary<string, double>(StringComparer.Ordinal);

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference) { reference = 0; return false; }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double v;
                return Scarcity.TryGetValue(inventoryKey, out v) ? v : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                return Reference.TryGetValue(inventoryKey, out reference) && reference > 0;
            }
        }

        /// <summary>An oracle that also answers the store's keep-back, as the real ones do.</summary>
        private sealed class KeepBackOracle : IPriceOracle, IOfferKeepBack
        {
            public readonly Dictionary<string, long> Reference = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> KeepBack = new Dictionary<string, long>(StringComparer.Ordinal);
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 1.0; }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                return Reference.TryGetValue(inventoryKey, out reference) && reference > 0;
            }
            public bool TryGetKeepBackStock(string nodeId, string inventoryKey, out long keepBack)
            {
                return KeepBack.TryGetValue(inventoryKey, out keepBack) && keepBack > 0;
            }
        }

        /// <summary>A reference-only oracle on the real curve, answering at any stock as the real ones do.</summary>
        private sealed class CurveOracle : IPriceOracle, IOfferKeepBack, IScarcityAtStock
        {
            public readonly Dictionary<string, long> Reference = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> KeepBack = new Dictionary<string, long>(StringComparer.Ordinal);
            public SpatialNode Node;
            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                long stock; Node.Inventory.TryGetValue(inventoryKey, out stock);
                return ScarcityAtStock(nodeId, inventoryKey, stock);
            }
            public double ScarcityAtStock(string nodeId, string inventoryKey, long stock)
            {
                long reference;
                return Reference.TryGetValue(inventoryKey, out reference)
                    ? ScarcityCurve.At(reference, stock, 0.5, 3.0) : 1.0;
            }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                return Reference.TryGetValue(inventoryKey, out reference) && reference > 0;
            }
            public bool TryGetKeepBackStock(string nodeId, string inventoryKey, out long keepBack)
            {
                return KeepBack.TryGetValue(inventoryKey, out keepBack) && keepBack > 0;
            }
        }

        [Fact]
        public void AnOfferRow_IsPricedAtTheStockItLeaves()
        {
            // FAC-161: priced at the pre-sale stock, a buy-out moved the node from glut to short at one
            // price, and the next refresh ordered the same units back at up to 3x.
            var node = Node();
            var prices = new Prices(); var policy = new Policy();
            prices.Map[Thrust] = 100;
            node.Inventory[Thrust] = 3000;
            var oracle = new CurveOracle { Node = node };
            oracle.Reference[Thrust] = 1000;
            oracle.KeepBack[Thrust] = 500;

            StationListing offered = RunWeighted(node, oracle, prices, policy)
                .Find(r => r.InvKey == Thrust && r.Side == StationListingSide.Offer);

            Assert.True(offered.Quantity > 0);
            long expected = (long)Math.Round(100 * ScarcityCurve.At(1000, 3000 - offered.Quantity, 0.5, 3.0));
            Assert.Equal(expected, offered.UnitPrice);
            Assert.NotEqual(50L, offered.UnitPrice);   // 0.5x: the pre-sale stock's price
        }

        [Fact]
        public void AnOrderRow_IsPricedAtItsMidpoint()
        {
            var node = Node();
            var prices = new Prices(); var policy = new Policy();
            prices.Map[Thrust] = 100;
            node.Inventory[Thrust] = 0;
            var oracle = new CurveOracle { Node = node };
            oracle.Reference[Thrust] = 1000;

            int noDemand;
            StationListing ordered = Run(node, new List<string> { Thrust }, oracle, prices,
                    DemandFor(Thrust, 800), policy, out noDemand)
                .Find(r => r.InvKey == Thrust && r.Side == StationListingSide.Order);

            Assert.Equal(800L, ordered.Quantity);
            Assert.Equal(220L, ordered.UnitPrice);      // the curve at 400 of 1000: 1 + 2 x 0.6
        }

        private static List<StationListing> RunWeighted(SpatialNode node, IPriceOracle oracle, Prices prices, Policy policy)
        {
            var into = new List<StationListing>();
            int noDemand, trimmed, noHeadroom, offList;
            StationListings.Build(node, null, oracle, prices, new DemandRegister(), policy, Now, into,
                out noDemand, StationListings.Uncapped, true, false, out trimmed, out noHeadroom, out offList);
            return into;
        }

        /// <summary>The N217 hydrogen (operator, 2026-10-07): a target raised by what players bought
        /// there used to hold the stock back from those players. Held against the keep-back, the
        /// whole holding is offered; the same reference read as the keep-back offers nothing.</summary>
        [Fact]
        public void AnOffer_IsHeldBackAgainstTheKeepBack_NotTheSalesRaisedReference()
        {
            var node = Node();
            var prices = new Prices(); var policy = new Policy();
            prices.Map[Thrust] = 100;
            node.Inventory[Thrust] = 1000;

            var withKeepBack = new KeepBackOracle();
            withKeepBack.Reference[Thrust] = 10_000;   // set by sales
            StationListing offered = RunWeighted(node, withKeepBack, prices, policy)
                .Find(r => r.InvKey == Thrust && r.Side == StationListingSide.Offer);
            Assert.Equal(1000L, offered.Quantity);

            var without = new Oracle();
            without.Reference[Thrust] = 10_000;
            Assert.DoesNotContain(RunWeighted(node, without, prices, policy),
                r => r.InvKey == Thrust && r.Side == StationListingSide.Offer);
        }

        [Fact]
        public void AnOffer_UnderItsKeepBack_KeepsTheShortfallsShare()
        {
            var node = Node();
            var prices = new Prices(); var policy = new Policy();
            prices.Map[Thrust] = 100;
            node.Inventory[Thrust] = 1000;

            var oracle = new KeepBackOracle();
            oracle.Reference[Thrust] = 10_000;
            oracle.KeepBack[Thrust] = 2000;            // half covered: half held back
            StationListing offered = RunWeighted(node, oracle, prices, policy)
                .Find(r => r.InvKey == Thrust && r.Side == StationListingSide.Offer);
            Assert.Equal(500L, offered.Quantity);
        }

        private sealed class Prices : IItemPriceCatalogue
        {
            public readonly Dictionary<string, long> Map = new Dictionary<string, long>(StringComparer.Ordinal);
            public long BasePrice(string subTypeId)
            {
                long v;
                return Map.TryGetValue(subTypeId ?? "", out v) ? v : 0;
            }
        }

        private sealed class Policy : IStationListingPolicy
        {
            public readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.Ordinal);
            public long Cap = 1000000;
            public bool CanList(string invKey) { return !Blocked.Contains(invKey); }
            public long ListingCap(string invKey) { return Cap; }
        }

        private static SpatialNode Node()
        {
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "ACME" };
            return n;
        }

        private static DemandRegister DemandFor(string invKey, long qty, string nodeId = "N1")
        {
            var d = new DemandRegister();
            d.Post(new DemandEntry
            {
                FactionTag = "ACME", Kind = "Item:" + invKey, NodeId = nodeId,
                Quantity = qty, ExpiresUtc = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            return d;
        }

        private static readonly DateTime Now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static List<StationListing> Run(
            SpatialNode node, IReadOnlyList<string> orderKeys, IPriceOracle oracle, Prices prices,
            DemandRegister demand, Policy policy, out int noDemand)
        {
            int trimmed;
            return Run(node, orderKeys, oracle, prices, demand, policy, out noDemand,
                StationListings.Uncapped, out trimmed);
        }

        private static List<StationListing> Run(
            SpatialNode node, IReadOnlyList<string> orderKeys, IPriceOracle oracle, Prices prices,
            DemandRegister demand, Policy policy, out int noDemand,
            long buyBudget, out int budgetTrimmed)
        {
            int noHeadroom;
            return Run(node, orderKeys, oracle, prices, demand, policy, out noDemand,
                buyBudget, out budgetTrimmed, out noHeadroom);
        }

        private static List<StationListing> Run(
            SpatialNode node, IReadOnlyList<string> orderKeys, IPriceOracle oracle, Prices prices,
            DemandRegister demand, Policy policy, out int noDemand,
            long buyBudget, out int budgetTrimmed, out int noHeadroom, bool needWeighted = false,
            bool orePriority = false, IList<StationBudgetRefusal> refusals = null)
        {
            int offList;
            return Run(node, orderKeys, oracle, prices, demand, policy, out noDemand,
                buyBudget, out budgetTrimmed, out noHeadroom, out offList, needWeighted, orePriority, refusals);
        }

        private static List<StationListing> Run(
            SpatialNode node, IReadOnlyList<string> orderKeys, IPriceOracle oracle, Prices prices,
            DemandRegister demand, Policy policy, out int noDemand,
            long buyBudget, out int budgetTrimmed, out int noHeadroom, out int offList,
            bool needWeighted = false, bool orePriority = false, IList<StationBudgetRefusal> refusals = null)
        {
            var into = new List<StationListing>();
            StationListings.Build(node, orderKeys, oracle, prices, demand, policy, Now, into,
                out noDemand, buyBudget, needWeighted, orePriority,
                out budgetTrimmed, out noHeadroom, out offList, refusals);
            return into;
        }

        private static List<StationListing> RunCountingOffList(
            SpatialNode node, IReadOnlyList<string> orderKeys, Prices prices, DemandRegister demand,
            out int noDemand, out int offList)
        {
            int trimmed, noHeadroom;
            return Run(node, orderKeys, new Oracle(), prices, demand, new Policy(), out noDemand,
                StationListings.Uncapped, out trimmed, out noHeadroom, out offList);
        }

        private const string Seed = "Seed:Grain";

        private static void Post(DemandRegister d, string invKey, long qty, string nodeId,
            DateTime? expiresUtc = null)
        {
            d.Post(new DemandEntry
            {
                FactionTag = "ACME", Kind = "Item:" + invKey, NodeId = nodeId, Quantity = qty,
                ExpiresUtc = expiresUtc ?? new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        private static StationListing? OrderFor(List<StationListing> rows, string invKey)
        {
            foreach (StationListing r in rows)
                if (r.InvKey == invKey && r.Side == StationListingSide.Order) return r;
            return null;
        }

        /// <summary>FAC-486: the Farm's missing seed is on no faction type's OrdersList. The list is
        /// flavour, demand is need, so the demanded seed lists anyway.</summary>
        [Fact]
        public void ADemandedItemOffTheOrdersList_StillGetsABuyOrder()
        {
            var node = Node();
            var prices = new Prices();
            prices.Map[Steel] = 10; prices.Map[Seed] = 5;
            var demand = new DemandRegister();
            Post(demand, Seed, 1, "N1");

            int noDemand, offList;
            List<StationListing> rows = RunCountingOffList(node, new List<string> { Steel }, prices, demand,
                out noDemand, out offList);

            StationListing? seed = OrderFor(rows, Seed);
            Assert.True(seed.HasValue);
            Assert.Equal(1L, seed.Value.Quantity);
            Assert.Equal(1, offList);
            Assert.Null(OrderFor(rows, Steel));      // on the list, no demand: still not bought
            Assert.Equal(1, noDemand);
        }

        /// <summary>The demand gate is not loosened by the union: an OrdersList key with no demand
        /// lists nothing, and an off-list key with no demand is not even a candidate.</summary>
        [Fact]
        public void WithoutDemand_NothingIsBought_OnOrOffTheList()
        {
            var node = Node();
            var prices = new Prices();
            prices.Map[Steel] = 10; prices.Map[Seed] = 5;

            int noDemand, offList;
            List<StationListing> rows = RunCountingOffList(node, new List<string> { Steel }, prices,
                new DemandRegister(), out noDemand, out offList);

            Assert.DoesNotContain(rows, r => r.Side == StationListingSide.Order);
            Assert.Equal(0, offList);
        }

        /// <summary>A need owed at another node is not this station's to buy, and an expired one is
        /// nobody's: neither nominates a candidate. A faction-wide need does.</summary>
        [Fact]
        public void OnlyANeedThisStationCanAnswer_Nominates()
        {
            var node = Node();
            var prices = new Prices();
            prices.Map[Seed] = 5; prices.Map[Thrust] = 100; prices.Map[IronOre] = 2;
            var demand = new DemandRegister();
            Post(demand, Seed, 1, "N2");
            Post(demand, Thrust, 4, null, Now.AddMinutes(-1));
            Post(demand, IronOre, 300, null);

            int noDemand, offList;
            List<StationListing> rows = RunCountingOffList(node, new List<string> { Steel }, prices, demand,
                out noDemand, out offList);

            Assert.Null(OrderFor(rows, Seed));
            Assert.Null(OrderFor(rows, Thrust));
            Assert.Equal(300L, OrderFor(rows, IronOre).Value.Quantity);
            Assert.Equal(1, offList);
            Assert.Equal(0, noDemand);      // a need owed elsewhere is not even a candidate here
        }

        /// <summary>A world with no faction-type catalogue still buys a need the node has never
        /// held: the inventory-threshold fallback cannot see a key that is not in the inventory.</summary>
        [Fact]
        public void NoCatalogue_StillBuysANeedTheNodeDoesNotHold()
        {
            var node = Node();
            var prices = new Prices();
            prices.Map[Seed] = 5;
            var demand = new DemandRegister();
            Post(demand, Seed, 1, "N1");

            int noDemand, offList;
            List<StationListing> rows = RunCountingOffList(node, null, prices, demand, out noDemand, out offList);

            Assert.Equal(1L, OrderFor(rows, Seed).Value.Quantity);
            Assert.Equal(1, offList);
        }

        [Fact]
        public void AGasOffer_ReachesTheRows_OnceThePolicyAdmitsIt()
        {
            // Gas failed CanList before FAC-174 -- the host's def-id cache is built from physical
            // item definitions and gas is not one -- so it was dropped before any price, threshold or
            // demand test ran. Nothing else in the builder needed changing; this pins that.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            const string gas = "Gas:Hydrogen";
            prices.Map[gas] = 300;            // SE's own MinimumHydrogenPrice, per kilolitre
            policy.Cap = 10_000_000;
            oracle.Reference[gas] = 10_000_000;
            node.Inventory[gas] = 4_000_000;  // above the 0.2 x reference offer threshold

            int noDemand;
            List<StationListing> rows = Run(node, null, oracle, prices, new DemandRegister(), policy, out noDemand);

            StationListing row = rows.Find(r => r.InvKey == gas && r.Side == StationListingSide.Offer);
            Assert.True(row.Quantity > 0);
            // A listing is a store row, and SE settles gas in kilolitres: 4,000,000 litres of stock
            // is a row of 4,000 at 300 apiece. A row of 4,000,000 at 300 is the litre count reaching
            // the shelf, which SE would then multiply by a thousand again on delivery.
            Assert.Equal(4000, row.Quantity);
            Assert.Equal(300, row.UnitPrice);
        }

        [Fact]
        public void AnUncappedGasOffer_CarriesTheWholeStock_InOneRow()
        {
            // A large-grid large hydrogen tank holds 15,000,000 L. A fixed row size below that makes a
            // player trade several times to fill one tank from a node that holds plenty.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices();
            var policy = new Policy { Cap = StationListings.Uncapped };

            const string gas = "Gas:Hydrogen";
            prices.Map[gas] = 300;
            oracle.Reference[gas] = 10_000_000;
            node.Inventory[gas] = 40_000_000;

            int noDemand, trimmed, noHeadroom;
            List<StationListing> rows = Run(node, null, oracle, prices, new DemandRegister(), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);

            StationListing row = rows.Find(r => r.InvKey == gas && r.Side == StationListingSide.Offer);
            Assert.Equal(40_000, row.Quantity);
        }

        [Fact]
        public void AnUncappedGasOrder_IsSizedByDemand()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices();
            var policy = new Policy { Cap = StationListings.Uncapped };

            const string gas = "Gas:Hydrogen";
            prices.Map[gas] = 300;
            node.Inventory[gas] = 2_000_000;

            int noDemand;
            List<StationListing> rows = Run(node, new List<string> { gas }, oracle, prices,
                DemandFor(gas, 3_000_000), policy, out noDemand);

            StationListing row = rows.Find(r => r.InvKey == gas && r.Side == StationListingSide.Order);
            Assert.Equal(3_000, row.Quantity);
        }

        [Fact]
        public void AGasHoldingUnderOneKilolitreIsNotOffered()
        {
            // SE would take the row's price for an Amount the buyer cannot draw a unit from.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            const string gas = "Gas:Hydrogen";
            prices.Map[gas] = 300;
            oracle.Reference[gas] = 1000;
            node.Inventory[gas] = 900;        // above the offer threshold, under one kilolitre

            int noDemand;
            List<StationListing> rows = Run(node, null, oracle, prices, new DemandRegister(), policy, out noDemand);

            Assert.DoesNotContain(rows, r => r.InvKey == gas);
        }

        [Fact]
        public void AGasOffer_IsWithheldWhenTheNodeIsShortOfItsOwnReference()
        {
            // FAC-162's need-weighting is not gas-aware and does not need to be, but a 10,000,000 L
            // reference makes it bite far harder on gas than on ore -- worth pinning so nobody reads
            // a small gas row as a bug.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            const string gas = "Gas:Hydrogen";
            prices.Map[gas] = 3;
            oracle.Reference[gas] = 10_000_000;
            node.Inventory[gas] = 4_000_000;

            int noDemand, trimmed, noHeadroom;
            List<StationListing> rows = Run(node, null, oracle, prices, new DemandRegister(), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);

            StationListing row = rows.Find(r => r.InvKey == gas && r.Side == StationListingSide.Offer);
            Assert.NotNull(row);
            Assert.True(row.Quantity < 4_000_000,
                "a node holding under half its reference must not offer the whole shelf");
        }

        [Fact]
        public void AGasKeyTheHostCannotList_IsDroppedLikeAnyOther()
        {
            // Water passes nothing: SE marks it without CanPlayerOffer, so the host never registers
            // it and the policy answers false. The builder must not special-case gas around that.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            const string water = "Gas:Water";
            policy.Blocked.Add(water);
            prices.Map[water] = 50;
            oracle.Reference[water] = 1000;
            node.Inventory[water] = 500_000;

            int noDemand;
            List<StationListing> rows = Run(node, null, oracle, prices, new DemandRegister(), policy, out noDemand);

            Assert.DoesNotContain(rows, r => r.InvKey == water);
        }

        [Fact]
        public void AnOreOrderIsOnTheShelfWhetherOrNotTheTierIsOn()
        {
            // Operator, 2026-08-29: selling ore is what a player can do from the moment they spawn,
            // so a station holding an ore need must surface it. This needed a funding tier when rows
            // competed for one pot -- a component's reference stock makes its shortfall score large
            // while ore sits mid-range, so ore lost. Since rows are sized independently there is
            // nothing left for it to lose to, and the guarantee holds with the tier OFF as well,
            // which is what this now pins.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            prices.Map[Steel] = 100;
            prices.Map[IronOre] = 100;
            // Steel outscores ore on need: the node holds none of a referenced item (score 1.0),
            // while the oracle has no opinion on ore at all (NeutralScore 0.5).
            oracle.Reference[Steel] = 1000;
            node.Inventory[Steel] = 0;

            var demand = new DemandRegister();
            foreach (string key in new[] { Steel, IronOre })
                demand.Post(new DemandEntry
                {
                    FactionTag = "ACME", Kind = "Item:" + key, NodeId = "N1",
                    Quantity = 50, ExpiresUtc = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                });

            var keys = new List<string> { Steel, IronOre };
            int noDemand, trimmed, noHeadroom;

            // 5,000 credits buys 50 units of EACH of them now, not of one of them: steel outscores
            // ore (score 1.0 against NeutralScore 0.5) and no longer takes anything by doing so.
            List<StationListing> without = Run(node, keys, oracle, prices, demand, policy,
                out noDemand, 5000, out trimmed, out noHeadroom, false, false);
            StationListing oreUntiered = Assert.Single(without,
                r => r.Side == StationListingSide.Order && r.InvKey == IronOre);
            Assert.Equal(50, oreUntiered.Quantity);

            List<StationListing> with = Run(node, keys, oracle, prices, demand, policy,
                out noDemand, 5000, out trimmed, out noHeadroom, false, true);
            StationListing ore = Assert.Single(with,
                r => r.Side == StationListingSide.Order && r.InvKey == IronOre);
            Assert.Equal(50, ore.Quantity);
            Assert.Equal(0, trimmed);                       // neither row was shortened by the other
        }

        [Fact]
        public void OrePriority_ReadsFirst_EvenWhenTheNodeIsNearlyStockedOnIt()
        {
            // The tier must outrank need, not blend with it: ore the node is nearly stocked on still
            // reads first, because a well-supplied ore is the one a player is most likely to be
            // carrying. Asserts POSITION, not presence -- presence stopped being able to fail on
            // 2026-08-30, when rows stopped competing, and a check that cannot fail is not a check.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();

            prices.Map[Steel] = 100;
            prices.Map[IronOre] = 100;
            oracle.Reference[Steel] = 1000;   node.Inventory[Steel] = 0;      // score 1.0
            oracle.Reference[IronOre] = 1000; node.Inventory[IronOre] = 900;  // score 0.1

            var demand = new DemandRegister();
            foreach (string key in new[] { Steel, IronOre })
                demand.Post(new DemandEntry
                {
                    FactionTag = "ACME", Kind = "Item:" + key, NodeId = "N1",
                    Quantity = 50, ExpiresUtc = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                });

            int noDemand, trimmed, noHeadroom;
            List<StationListing> with = Run(node, new List<string> { Steel, IronOre },
                oracle, prices, demand, policy, out noDemand, 5000, out trimmed, out noHeadroom,
                false, true);

            List<StationListing> orders = with.FindAll(
                delegate (StationListing r) { return r.Side == StationListingSide.Order; });
            Assert.Equal(2, orders.Count);
            Assert.Equal(IronOre, orders[0].InvKey);        // ahead of the higher-scoring steel
        }

        [Fact]
        public void Offer_AppearsOnlyAboveTheItemsOwnReferenceThreshold()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 3003;
            oracle.Reference[Steel] = 1000;          // threshold = 1000 * 0.2 = 200

            node.Inventory[Steel] = 200;             // exactly at it -> not offered
            int nd;
            Assert.Empty(Run(node, new List<string>(), oracle, prices, null, policy, out nd));

            node.Inventory[Steel] = 201;
            List<StationListing> rows = Run(node, new List<string>(), oracle, prices, null, policy, out nd);
            StationListing row = Assert.Single(rows);
            Assert.Equal(StationListingSide.Offer, row.Side);
            Assert.Equal(201, row.Quantity);
        }

        [Fact]
        public void Offer_PriceIsBaseTimesThatItemsOwnScarcity()
        {
            // The FAC-130 half: two components of one kind must not share a multiplier.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 100; prices.Map[Thrust] = 100;
            oracle.Scarcity[Steel] = 0.5; oracle.Scarcity[Thrust] = 3.0;
            node.Inventory[Steel] = 500; node.Inventory[Thrust] = 500;

            int nd;
            List<StationListing> rows = Run(node, new List<string>(), oracle, prices, null, policy, out nd);
            Assert.Equal(2, rows.Count);
            Assert.Equal(50, rows.Find(r => r.InvKey == Steel).UnitPrice);
            Assert.Equal(300, rows.Find(r => r.InvKey == Thrust).UnitPrice);
        }

        [Fact]
        public void Offer_QuantityIsCappedByTheListingCap()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices();
            var policy = new Policy { Cap = 250 };
            prices.Map[Steel] = 10;
            node.Inventory[Steel] = 9999;

            int nd;
            Assert.Equal(250, Assert.Single(Run(node, new List<string>(), oracle, prices, null, policy, out nd)).Quantity);
        }

        [Fact]
        public void UnlistableItems_NeverReachTheRows()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            policy.Blocked.Add(Steel);
            prices.Map[Steel] = 10; prices.Map[Thrust] = 10;
            node.Inventory[Steel] = 500; node.Inventory[Thrust] = 500;

            int nd;
            List<StationListing> rows = Run(node, new List<string>(), oracle, prices, null, policy, out nd);
            Assert.Equal(Thrust, Assert.Single(rows).InvKey);
        }

        [Fact]
        public void Order_RequiresLiveDemand_AndCountsWhatItRejected()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 3003; prices.Map[Thrust] = 62290;
            var orderKeys = new List<string> { Steel, Thrust };

            int nd;
            List<StationListing> rows = Run(node, orderKeys, oracle, prices, DemandFor(Steel, 400), policy, out nd);

            StationListing row = Assert.Single(rows);
            Assert.Equal(Steel, row.InvKey);
            Assert.Equal(StationListingSide.Order, row.Side);
            Assert.Equal(400, row.Quantity);
            // Thrust was a candidate with no demand -- this is the counter STORE_LIST reports, and
            // it is structurally 0 if the filter is ever bypassed.
            Assert.Equal(1, nd);
        }

        [Fact]
        public void Order_QuantityIsDemandMinusWhatIsAlreadyHeld_AgainstTheCap()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices();
            var policy = new Policy { Cap = 500 };
            prices.Map[Steel] = 10;
            node.Inventory[Steel] = 450;

            int nd;
            List<StationListing> rows = Run(node, new List<string> { Steel }, oracle, prices, DemandFor(Steel, 400), policy, out nd);
            // Stock also clears the offer threshold (no reference means "no opinion", which lists),
            // so assert on the order side rather than on the row count.
            StationListing order = Assert.Single(rows.FindAll(r => r.Side == StationListingSide.Order));
            Assert.Equal(50, order.Quantity);   // min(demand 400, cap 500 - held 450)
        }

        [Fact]
        public void Order_IsDroppedWhenTheNodeIsAlreadyAtTheCap()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices();
            var policy = new Policy { Cap = 500 };
            prices.Map[Steel] = 10;
            node.Inventory[Steel] = 500;

            int nd;
            List<StationListing> rows = Run(node, new List<string> { Steel }, oracle, prices, DemandFor(Steel, 400), policy, out nd);
            Assert.Empty(rows.FindAll(r => r.Side == StationListingSide.Order));
        }

        [Fact]
        public void NoCatalogue_FallsBackToInventoryThreshold_AndStillGatesOnDemand()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 10; prices.Map[Thrust] = 10;
            oracle.Reference[Steel] = 1000;      // 300 < 1000*0.8 -> short, so a buy candidate
            oracle.Reference[Thrust] = 1000;
            node.Inventory[Steel] = 300;
            node.Inventory[Thrust] = 300;

            int nd;
            // null orderKeys selects the fallback. Only Steel has demand.
            List<StationListing> rows = Run(node, null, oracle, prices, DemandFor(Steel, 250), policy, out nd);

            StationListing order = Assert.Single(rows.FindAll(r => r.Side == StationListingSide.Order));
            Assert.Equal(Steel, order.InvKey);
            Assert.Equal(250, order.Quantity);       // min(reference-held 700, demand 250, cap)
            Assert.Equal(1, nd);                     // Thrust rejected for want of demand
        }

        [Fact]
        public void AnItemWithNoBasePrice_IsNotListedOnEitherSide()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            node.Inventory[Steel] = 5000;            // priced 0 -> below MinBasePrice

            int nd;
            Assert.Empty(Run(node, new List<string> { Steel }, oracle, prices, DemandFor(Steel, 400), policy, out nd));
            Assert.Equal(0, nd);                     // rejected on price, not on demand
        }
        // ---- the buy budget (FAC-159) -----------------------------------------------------------

        [Fact]
        public void BuyOrderIsTrimmedToWhatTheFactionCanPayFor()
        {
            // The live shape: ISBT N007 advertised 999,487 Ore:Stone at 30 -- 30,000,000 credits
            // against a 350,000 treasury -- because no balance was read anywhere in the path.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 10;
            oracle.Scarcity[Steel] = 3.0;            // unit price 30
            policy.Cap = 1000000;
            var demand = DemandFor(Steel, 3013200);

            int noDemand, trimmed;
            var rows = Run(node, new[] { Steel }, oracle, prices, demand, policy,
                out noDemand, 300000, out trimmed);

            var order = rows.Find(r => r.Side == StationListingSide.Order);
            Assert.Equal(30, order.UnitPrice);
            Assert.Equal(10000, order.Quantity);                 // 300000 / 30, not 1,000,000
            Assert.Equal(1, trimmed);
            Assert.True(order.Quantity * order.UnitPrice <= 300000);
        }

        [Fact]
        public void TheBudgetBoundsEachRowAndDeliberatelyNotTheWholeOrderSet()
        {
            // REVERSED 2026-08-30 by operator decision, and the reversal is the point of the test:
            // this asserted `committed <= budget`, i.e. that three affordable orders must be
            // collectively affordable too. Rows are advertisements, not reservations -- "individual
            // rows don't have to be treated as linked... if a player exhausts the budget on one row,
            // the other rows will simply fail". So the sum MAY exceed the window; no single row may.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            policy.Cap = 1000;
            var demand = new DemandRegister();
            string[] keys = { "Comp:A", "Comp:B", "Comp:C" };
            foreach (string k in keys)
            {
                prices.Map[k] = 10;
                demand.Post(new DemandEntry
                {
                    FactionTag = "ACME", Kind = "Item:" + k, NodeId = node.Id,
                    Quantity = 1000,
                    ExpiresUtc = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                });
            }

            int noDemand, trimmed;
            var rows = Run(node, keys, oracle, prices, demand, policy, out noDemand, 15000, out trimmed);

            long committed = 0;
            int orderRows = 0;
            foreach (StationListing r in rows)
                if (r.Side == StationListingSide.Order)
                {
                    orderRows++;
                    committed += r.Quantity * r.UnitPrice;
                    Assert.True(r.Quantity * r.UnitPrice <= 15000,
                        r.InvKey + " alone committed " + (r.Quantity * r.UnitPrice) + " of a 15000 budget");
                }

            // Three rows, each capped at 1,000 units by the listing cap = 10,000 credits apiece:
            // under the budget individually, 30,000 together against a 15,000 window. The old rule
            // produced one full row and two starved ones from these same inputs, so this is not a
            // weaker assertion than the one it replaces, it is the opposite one.
            Assert.Equal(3, orderRows);
            Assert.Equal(30000, committed);
            Assert.True(committed > 15000, "the point of the reversal: the set may exceed the window");
            Assert.Equal(0, trimmed);                       // nothing was shortened: each got its want
        }

        [Fact]
        public void AZeroBudgetListsNoOrdersButStillListsOffers()
        {
            // Selling earns credits rather than spending them, so an insolvent station must still be
            // able to sell what it holds -- otherwise the bound would deepen the hole it is in.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 10;
            oracle.Reference[Steel] = 10;
            node.Inventory[Steel] = 5000;            // well above the offer threshold
            var demand = DemandFor(Steel, 1000);

            int noDemand, trimmed;
            var rows = Run(node, new[] { Steel }, oracle, prices, demand, policy, out noDemand, 0, out trimmed);

            Assert.DoesNotContain(rows, r => r.Side == StationListingSide.Order);
            Assert.Contains(rows, r => r.Side == StationListingSide.Offer);
            Assert.Equal(1, trimmed);
        }

        [Fact]
        public void UncappedIsTheOldBehaviourAndReportsNoTrim()
        {
            // budgetTrimmed carries the same contract as noDemand: structurally 0 when the bound is
            // bypassed, so a live 0 means "nothing was trimmed", never "the bound is not running".
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 10;
            oracle.Scarcity[Steel] = 3.0;
            policy.Cap = 1000000;
            var demand = DemandFor(Steel, 3013200);

            int noDemand, trimmed;
            var rows = Run(node, new[] { Steel }, oracle, prices, demand, policy,
                out noDemand, StationListings.Uncapped, out trimmed);

            var order = rows.Find(r => r.Side == StationListingSide.Order);
            Assert.Equal(1000000, order.Quantity);
            Assert.Equal(0, trimmed);
        }

        // ---- offers weighed against the node's own need (FAC-162) --------------------------------

        [Fact]
        public void AShortNodeDoesNotOfferItsWholeStock()
        {
            // ISBT N007 offered all 558 kg of the Ore:Iron its own refinery needed, against a
            // reference of 1,049, while holding zero Ingot:Iron and posting REFINE_DEMAND for it.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 143;
            oracle.Reference[Steel] = 1049;
            node.Inventory[Steel] = 558;

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);

            var offer = rows.Find(r => r.Side == StationListingSide.Offer);
            Assert.True(offer.Quantity > 0, "some sales is acceptable -- it must not refuse outright");
            Assert.True(offer.Quantity < 558, "it offered its whole stock: " + offer.Quantity);
            Assert.Equal(296, offer.Quantity);          // 558 x (1 - 0.468 shortfall)
        }

        [Fact]
        public void AGluttedNodeStillOffersEverything()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 50;
            oracle.Reference[Steel] = 1000;
            node.Inventory[Steel] = 188589;            // ice-shaped: far above target

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);

            var offer = rows.Find(r => r.Side == StationListingSide.Offer);
            Assert.Equal(188589, offer.Quantity);
        }

        [Fact]
        public void TheWeightingChangesQuantityAndLeavesThePriceAlone()
        {
            // The price already carried the need signal -- base x scarcity -- and must not be
            // touched: that half was working and is what makes a needed item expensive.
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 143;
            oracle.Reference[Steel] = 1049;
            oracle.Scarcity[Steel] = 1.881;
            node.Inventory[Steel] = 558;

            int noDemand, trimmed, noHeadroom;
            var weighted = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);
            var unweighted = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: false);

            var a = weighted.Find(r => r.Side == StationListingSide.Offer);
            var b = unweighted.Find(r => r.Side == StationListingSide.Offer);
            Assert.Equal(b.UnitPrice, a.UnitPrice);     // 143 x 1.881 = 269, either way
            Assert.Equal(269, a.UnitPrice);
            Assert.True(a.Quantity < b.Quantity);
        }

        [Fact]
        public void ANodeHoldingNoneOfWhatItNeedsOffersNone()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 143;
            oracle.Reference[Steel] = 1049;
            node.Inventory[Steel] = 1;                  // effectively nothing against a 1,049 target

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: true);

            Assert.DoesNotContain(rows, r => r.Side == StationListingSide.Offer);
        }

        [Fact]
        public void WeightingOffIsTheOldWholeStockBehaviour()
        {
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[Steel] = 143;
            oracle.Reference[Steel] = 1049;
            node.Inventory[Steel] = 558;

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new string[0], oracle, prices, DemandFor(Steel, 0), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom, needWeighted: false);

            Assert.Equal(558, rows.Find(r => r.Side == StationListingSide.Offer).Quantity);
        }

    
        // ---- per-row sizing against the whole budget (FAC-176) ---------------------------------
        // These carry forward the coverage the retired BudgetAllocation tests held, re-expressed
        // against the rule that replaced them.

        private static DemandRegister DemandForAll(params string[] keys)
        {
            var d = new DemandRegister();
            foreach (string k in keys)
                d.Post(new DemandEntry
                {
                    FactionTag = "ACME", Kind = "Item:" + k, NodeId = "N1",
                    Quantity = 1000, ExpiresUtc = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                });
            return d;
        }

        [Fact]
        public void EveryRowIsSizedAgainstTheWholeBudget_NotAShareOfIt()
        {
            // The FAC-176 regression, at ISBT's live numbers. Budget 162,238 = income 374,127 x 0.3
            // plus the 50,000 floor. Under the retired shared pot a 0.25 reserve left the ordinary
            // tier 40,559 and the 0.5 per-row cap was measured on the whole budget, so it could not
            // bind there: one row took 77,220 and the other two got nothing at all.
            const string grid = "Comp:MetalGrid", tube = "Comp:LargeTube", plate = "Comp:SteelPlate";
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[grid] = 17898; prices.Map[tube] = 12870; prices.Map[plate] = 9009;
            foreach (string k in new[] { grid, tube, plate }) oracle.Reference[k] = 1000;

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new[] { grid, tube, plate }, oracle, prices,
                DemandForAll(grid, tube, plate), policy, out noDemand,
                162238, out trimmed, out noHeadroom);

            Assert.Equal(3, rows.Count);
            // Each row sized against the FULL budget, independently -- 9, 12 and 18 units, not the
            // 6/0/0 the shared pot produced.
            Assert.Equal(162238 / 17898, Quantity(rows, grid));
            Assert.Equal(162238 / 12870, Quantity(rows, tube));
            Assert.Equal(162238 /  9009, Quantity(rows, plate));
        }

        [Fact]
        public void NoSingleRowMayExceedTheBudget()
        {
            // The half of FAC-159 that is load-bearing and must survive the allocator's retirement:
            // ISBT N007 advertised 999,487 Ore:Stone at 30 -- 30,000,000 credits against a 350,000
            // treasury. Bounding each row is what fixed that; dividing the bound between rows was
            // never the part that did the work.
            const string stone = "Ore:Stone";
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[stone] = 30;
            oracle.Reference[stone] = 10000000;

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new[] { stone }, oracle, prices,
                DemandFor(stone, 999487), policy, out noDemand, 350000, out trimmed, out noHeadroom);

            Assert.Equal(350000 / 30, Quantity(rows, stone));
            Assert.Equal(1, trimmed);                       // shortened, so it is counted
        }

        [Fact]
        public void ARowThatCannotAffordOneUnitIsRefusedAndNamed()
        {
            // Lever 3 of FAC-176: budgettrimmed is a bare count, and reconstructing which items a
            // station refused took a world-save read and a price table. The refusal names itself.
            const string grid = "Comp:MetalGrid", plate = "Comp:SteelPlate";
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[grid] = 17898; prices.Map[plate] = 9009;
            oracle.Reference[grid] = 1000; oracle.Reference[plate] = 1000;

            var refusals = new List<StationBudgetRefusal>();
            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new[] { grid, plate }, oracle, prices,
                DemandForAll(grid, plate), policy, out noDemand, 10000, out trimmed, out noHeadroom,
                false, false, refusals);

            Assert.Single(rows);                            // only the plate is affordable
            Assert.Equal(1, Quantity(rows, plate));
            var refused = Assert.Single(refusals);
            Assert.Equal(grid, refused.InvKey);
            Assert.Equal(17898, refused.UnitPrice);         // the price is what refused it
            Assert.True(refused.WantUnits > 0);
        }

        [Fact]
        public void OrePriorityOrdersTheShelfWithoutTakingAnyoneElsesUnits()
        {
            // Ore was a funding tier and is now presentation only. It must still read first -- and
            // must no longer cost the component anything, which is what FAC-168's reserve existed
            // to compensate for.
            const string stone = "Ore:Stone", plate = "Comp:SteelPlate";
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[stone] = 30; prices.Map[plate] = 9009;
            oracle.Reference[stone] = 100000; oracle.Reference[plate] = 1000;

            int noDemand, trimmed, noHeadroom;
            var ordered = Run(node, new[] { plate, stone }, oracle, prices,
                DemandForAll(stone, plate), policy, out noDemand, 162238, out trimmed, out noHeadroom,
                false, true);
            var unordered = Run(node, new[] { plate, stone }, oracle, prices,
                DemandForAll(stone, plate), policy, out noDemand, 162238, out trimmed, out noHeadroom,
                false, false);

            var orders = ordered.FindAll(delegate (StationListing r) { return r.Side == StationListingSide.Order; });
            Assert.Equal(stone, orders[0].InvKey);          // ore reads first
            // ...and every quantity is identical either way: ordering is not funding.
            Assert.Equal(Quantity(unordered, stone), Quantity(ordered, stone));
            Assert.Equal(Quantity(unordered, plate), Quantity(ordered, plate));
        }

        [Fact]
        public void AnUncappedBudgetLeavesEveryRowAtItsFullWant()
        {
            const string plate = "Comp:SteelPlate";
            var node = Node();
            var oracle = new Oracle(); var prices = new Prices(); var policy = new Policy();
            prices.Map[plate] = 9009;
            oracle.Reference[plate] = 1000;

            int noDemand, trimmed, noHeadroom;
            var rows = Run(node, new[] { plate }, oracle, prices, DemandFor(plate, 640), policy,
                out noDemand, StationListings.Uncapped, out trimmed, out noHeadroom);

            Assert.Equal(640, Quantity(rows, plate));
            Assert.Equal(0, trimmed);                       // structurally 0 when the bound is off
        }

        private static long Quantity(List<StationListing> rows, string invKey)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].InvKey == invKey && rows[i].Side == StationListingSide.Order)
                    return rows[i].Quantity;
            return 0;
        }
}
}
