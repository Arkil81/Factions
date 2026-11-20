using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// <para><b>Scarcity is answered per inventory key, never per ItemKind.</b> There is deliberately
    /// no kind-level multiplier on this interface: a node's cheap bulk would otherwise answer for the
    /// one item it holds none of. A shipyard sitting on 107,094 stone/iron/silicon ingots read as
    /// oversupplied in <c>Ingot</c> and refused every gram of gold flown to it, 0 of 308 convoys over
    /// three days (FAC-286).</para>
    ///
    /// <para>There is no kind-level reference either. A target is what a node actually consumes of
    /// one key, and a kind has no consumption of its own — the per-kind credit budget that used to
    /// sit underneath this had no production caller left once the flow ranking replaced it.</para>
    /// </summary>
    public interface IPriceOracle
    {
        /// <summary>Scarcity multiplier for a concrete inventory key at <paramref name="nodeId"/>.
        /// 1.0 = at target stock. &lt;1 = oversupplied. &gt;1 = scarce. Range [ScarcityMin, ScarcityMax].</summary>
        double ScarcityMultiplier(string nodeId, string inventoryKey);

        /// <summary>Reference stock for a concrete inventory key at <paramref name="nodeId"/>.
        /// Returns false when the node has no reference need for that item or no longer exists.</summary>
        bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference);

        /// <summary>The same target, but only when a **measured** term set it — consumption here,
        /// sales from here, or a BOM owed here. Returns false when the target rests on the per-kind
        /// floor alone.
        ///
        /// <para>The floor is a bootstrap value sized to open the scarcity gate for a recipe that has
        /// never run, not a statement about how much the node should hold. A caller sizing an order
        /// off it asks for `ReferenceStockIngot` (60) of a key a node may consume by the thousand,
        /// which is why this is a separate question from
        /// <see cref="TryGetReferenceStock"/> rather than a flag on it.</para></summary>
        bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target);
    }

    /// <summary>
    /// What a station's store holds back from sale: the part of its target that serves the station
    /// itself. A separate interface rather than a member of <see cref="IPriceOracle"/> so an oracle
    /// without one keeps holding back against <see cref="IPriceOracle.TryGetReferenceStock"/>.
    ///
    /// <para><b>The sale term is not in it.</b> The target rises with what players buy here so the
    /// station restocks for them; holding that same stock back from the store meant the more a
    /// station sold, the less it offered. A player at N217 met it as hydrogen "kept for steady
    /// sales" that its refuel would not sell (operator, 2026-10-07).</para>
    /// </summary>
    public interface IOfferKeepBack
    {
        /// <summary>The largest of the owed, flow and qualifying floor terms. False when none is
        /// above zero, or the node or key is unknown.</summary>
        bool TryGetKeepBackStock(string nodeId, string inventoryKey, out long keepBack);
    }

    /// <summary>The scarcity multiplier as a function of stock alone, shared by every oracle so the
    /// staged production cycle and the live one price on one curve.
    ///
    /// <para><b>Short side, linear:</b> <c>1 + (max − 1) × (reference − stock) / reference</c>, so
    /// it reaches <paramref name="max"/> only at zero stock. The hyperbola it replaces,
    /// <c>reference / stock</c>, reached 3.0 at a third of the reference, and 857 of 930 contracts on
    /// 2026-08-28 sat at exactly that ceiling (FAC-161). <b>Glut side, unchanged:</b>
    /// <c>reference / stock</c> down to <paramref name="min"/>. The two meet at 1.0 at
    /// <c>stock == reference</c>.</para>
    ///
    /// <para>No time term. A price that moves with a clock rather than with stock lets a player drain
    /// a node, wait, and sell the same units back for more (operator, 2026-10-09).</para></summary>
    public static class ScarcityCurve
    {
        public static double At(long reference, long stock, double min, double max)
        {
            if (reference <= 0) return 1.0;
            if (stock < 0) stock = 0;
            if (stock >= reference) return MathUtil.Clamp((double)reference / stock, min, 1.0);
            return 1.0 + (max - 1.0) * (reference - stock) / reference;
        }
    }

    /// <summary>The multiplier at a stock level other than the node's current one, for pricing a row
    /// at the stock its trade would leave behind. A separate interface, like
    /// <see cref="IOfferKeepBack"/>, so an oracle without an opinion prices at the current stock.</summary>
    public interface IScarcityAtStock
    {
        double ScarcityAtStock(string nodeId, string inventoryKey, long stock);
    }

    /// <summary>Where on the curve a player-facing row is priced, so that buying a row and selling it
    /// straight back at the same node cannot profit (FAC-161).
    ///
    /// <para><b>An offer at its far end</b>, the stock left once the row is bought out. After that
    /// buy-out the current stock is the far end, so any order taken at that stock or above pays at
    /// most what the player paid. <b>An order or a contract at its midpoint</b>,
    /// <c>stock + units / 2</c>. Not the far end: order rows are sized by gross demand, which usually
    /// reaches the reference, so far-end orders would price at or below base almost everywhere. On a
    /// linear curve the midpoint is the row's average price, and it sits above the stock a buy-out
    /// leaves, so a round trip loses money.</para>
    ///
    /// <para>Units are inventory units: a gas row's kilolitres are converted back to litres by the
    /// caller.</para></summary>
    public static class RowScarcity
    {
        public static double ForOffer(IPriceOracle oracle, string nodeId, string invKey, long stock, long inventoryUnits)
        {
            return At(oracle, nodeId, invKey, stock - Math.Max(0L, inventoryUnits));
        }

        public static double ForOrder(IPriceOracle oracle, string nodeId, string invKey, long stock, long inventoryUnits)
        {
            return At(oracle, nodeId, invKey, Math.Max(0L, stock) + Math.Max(0L, inventoryUnits) / 2);
        }

        private static double At(IPriceOracle oracle, string nodeId, string invKey, long stock)
        {
            if (oracle == null) return 1.0;
            var at = oracle as IScarcityAtStock;
            return at != null
                ? at.ScarcityAtStock(nodeId, invKey, Math.Max(0L, stock))
                : oracle.ScarcityMultiplier(nodeId, invKey);
        }
    }

    public sealed class NullPriceOracle : IPriceOracle
    {
        public static readonly NullPriceOracle Instance = new NullPriceOracle();
        public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 1.0; }
        public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 0; return false; }
        public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
    }

    /// <summary>
    /// Computes per-node, per-ItemKind scarcity from live node inventory vs a capability-derived
    /// reference stock. Reference stock = Σ(CapabilityInputFor + CapabilityOutputFor) × buffer
    /// horizon across all installed capabilities, representing "how much of this item should be
    /// sitting on this node for one buffer cycle" — what it consumes to keep running, plus what it
    /// produces awaiting collection.
    ///
    /// <para>The reference stock is cached per node and invalidated when capabilities change.
    /// Call <see cref="InvalidateNode"/> after any Develop action, and <see cref="RecomputeAll"/>
    /// at Load time.</para>
    ///
    /// <para>Nodes with no capability input for a given kind return 1.0 (neutral — no opinion).
    /// Nodes absent from the graph return 1.0 and are evicted from the cache.</para>
    /// </summary>
    public sealed class PriceOracle : IPriceOracle, IOfferKeepBack, IScarcityAtStock
    {
        private readonly WorldModel _world;
        private readonly FactionTuning _tuning;

        // factionTag -> how many of its nodes have any installed capability, i.e. how many places a
        // faction-wide operational need is spread across. Cleared by InvalidateNode/RecomputeAll,
        // which is every point a capability set changes. An ownership transfer between two Develops
        // leaves it stale; the effect is bounded (it only scales the shared-need share) and it
        // self-corrects at the next Develop or load.
        private readonly Dictionary<string, int> _holderCountByTag = new Dictionary<string, int>();

        // (nodeId, invKey) -> operational need, memoised for one tick. The share below walks the
        // faction's nodes to find where its stock actually sits, and ScarcityMultiplier is called
        // per dest-node-per-kind by TradeManager and per recipe output by ProductionManager, so that
        // walk cannot run per query. Only items with a live need reach it at all.
        private readonly Dictionary<string, long> _needCache = new Dictionary<string, long>();

        private System.DateTime _needCacheStamp;
        private int _needCacheRevision = -1;

        // The inverted recipe graph, and the registry it was built from. Rebuilt when the host
        // swaps the registry (the sim and the tests both start on NullRecipeRegistry and replace it),
        // never per query: the graph is a property of the catalogue, not of a node or a tick.
        private ItemDownstreamIndex _downstream;
        private IRecipeRegistry _downstreamFrom;

        public PriceOracle(WorldModel world, FactionTuning tuning)
        {
            _world  = world;
            _tuning = tuning;
        }

        public double ScarcityMultiplier(string nodeId, string inventoryKey)
        {
            return ScarcityAtStock(nodeId, inventoryKey, _world.GetNodeInventory(nodeId, inventoryKey));
        }

        /// <summary><see cref="ScarcityCurve.At"/> for this node's reference stock of
        /// <paramref name="inventoryKey"/>, at <paramref name="stock"/> instead of what it holds.</summary>
        public double ScarcityAtStock(string nodeId, string inventoryKey, long stock)
        {
            long reference;
            if (!TryGetReferenceStock(nodeId, inventoryKey, out reference) || reference <= 0)
                return 1.0;
            return ScarcityCurve.At(reference, stock, _tuning.ScarcityMin, _tuning.ScarcityMax);
        }

        public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
        {
            reference = 0;
            if (string.IsNullOrEmpty(inventoryKey)) return false;

            if (!_world.Graph.Nodes.ContainsKey(nodeId)) return false;

            ItemKind kind;
            if (!_world.TryGetInventoryItemKind(inventoryKey, out kind)) return false;

            reference = FlowRankedTarget(nodeId, inventoryKey, kind);
            return reference > 0;
        }

        public bool TryGetKeepBackStock(string nodeId, string inventoryKey, out long keepBack)
        {
            keepBack = 0;
            if (string.IsNullOrEmpty(inventoryKey)) return false;
            if (!_world.Graph.Nodes.ContainsKey(nodeId)) return false;

            ItemKind kind;
            if (!_world.TryGetInventoryItemKind(inventoryKey, out kind)) return false;

            keepBack = Explain(nodeId, inventoryKey, kind).KeepBack;
            return keepBack > 0;
        }

        public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target)
        {
            target = 0;
            if (string.IsNullOrEmpty(inventoryKey)) return false;
            if (!_world.Graph.Nodes.ContainsKey(nodeId)) return false;

            ItemKind kind;
            if (!_world.TryGetInventoryItemKind(inventoryKey, out kind)) return false;

            TargetBreakdown b = Explain(nodeId, inventoryKey, kind);
            if (b.Target <= 0 || b.Winner == "floor" || b.Winner == "none") return false;
            target = b.Target;
            return true;
        }

        /// <summary>Drop what this node's target depended on. Call after any Develop action that
        /// changes its capability set: the target itself is read live from flow and demand, but the
        /// per-node caches over those are not, and the holder count a shared need is divided by
        /// moves whenever a capability set does.</summary>
        public void InvalidateNode(string nodeId)
        {
            _holderCountByTag.Clear();
            _needCacheRevision = -1;
        }

        /// <summary>Drop every cache. Call once at Load, after WorldModel registries are fully
        /// wired — the recipe graph the downstream term reads is built from one of them.</summary>
        public void RecomputeAll()
        {
            _holderCountByTag.Clear();
            _needCache.Clear();
            _needCacheRevision = -1;
            _downstream = null;
            _downstreamFrom = null;
        }

        /// <summary>
        /// What this node should hold of one item, ranked the way a warehouse manager would rather
        /// than derived from the hardware standing on it.
        ///
        /// <para>Four terms, each the largest claim of its own kind — the target is their max, not
        /// their sum, because they are four answers to one question and the biggest is the one that
        /// binds:</para>
        /// <list type="number">
        /// <item><b>Flow.</b> What this node actually consumes, per pass, times a coverage horizon,
        /// weighted by <see cref="ItemDownstreamIndex.Reach"/> — how many further refinement steps
        /// stand on the item and in what quantity. This is the term that carries the decision.</item>
        /// <item><b>Commitment.</b> <see cref="OperationalNeedFor"/> — ship and install BOMs owed
        /// here. A one-off capital claim, not throughput, and unchanged by this rule.</item>
        /// <item><b>Bootstrap.</b> The per-kind floor, applied only where a live <c>Item:</c> row
        /// wants the key. A recipe that has never run has no flow to read, so without this the node
        /// would never open the gate that would let it start — the FAC-164 shape, one level up.</item>
        /// <item><b>Sale.</b> Units really bought from this node per pass, over its own coverage
        /// horizon — <see cref="SpatialNode.SalesHistory"/>. Cross-faction trade and player store
        /// purchases only; the central bank is excluded because it buys a fraction of stock with no
        /// demand-side bound, which would make sales a function of stock and the curve unstable.
        /// No downstream weight: a terminal product has none by definition, and an item with both
        /// signals already takes the larger of the two through this <c>max</c>.</item>
        /// <item><b>Floor.</b> A glut has to stay visible to the central bank, whose buyout is the
        /// only non-debt money injection there is. Applied to a bank-buyable kind only once the node
        /// already holds that much, so it can pull a reading down into glut and never push one up
        /// into scarcity. This is not the sale term and does not track what the bank takes — it is a
        /// constant, which is what stops it feeding back.</item>
        /// </list>
        ///
        /// <para><b>Why this collapses the concrete loop and the capability formula did not.</b>
        /// Stone ore is the largest flow in the world, so its own flow term is large and stays
        /// large — but what decides whether the stone recipe runs is the target for its OUTPUT.
        /// <c>Ingot:Stone</c> is made far faster than it is used and nothing but concrete consumes
        /// it, so its flow term is near zero and its reach is near zero; the pull gate stops
        /// admitting the recipe, consumption stops, and stone's own EMA decays behind it. The loop
        /// unwinds from the top, which is the only end it can be unwound from.</para>
        ///
        /// <para><b>Why the sale term is bounded and the bank is not.</b> A cross-faction trade is
        /// capped by the buyer's own <c>SpendBudget</c> and inbound window, and a player purchase by
        /// the player's wallet — both outside this node, so neither grows just because this node
        /// holds more. The bank buys <c>CentralBankBuyFraction x stock</c> from any glut, so
        /// crediting it would close the loop on itself: more stock, more "sold", higher target, more
        /// production, more stock.</para>
        /// </summary>
        private long FlowRankedTarget(string nodeId, string inventoryKey, ItemKind kind)
        {
            return Explain(nodeId, inventoryKey, kind).Target;
        }

        /// <summary>One (node, item) target with every term that competed for it, and which one won.
        /// <para><see cref="FlowRankedTarget"/> is this method's <c>Target</c> field, so the
        /// operator-facing <c>factions target</c> breakdown and the number the world actually ranks
        /// on cannot disagree — there is one computation, not a second one written to explain the
        /// first.</para></summary>
        public struct TargetBreakdown
        {
            /// <summary>What the node should hold: the largest of the four terms below.</summary>
            public long Target;
            /// <summary>Which term produced <see cref="Target"/>: <c>flow</c>, <c>sold</c>,
            /// <c>owed</c>, <c>floor</c>, or <c>none</c> when every term was zero.</summary>
            public string Winner;
            /// <summary>Observed consumption over the coverage horizon, already weighted by
            /// <see cref="Reach"/>.</summary>
            public long Flow;
            /// <summary>Units really bought from this node, over the sale horizon.</summary>
            public long Sold;
            /// <summary>Ship and install BOMs owed here.</summary>
            public long Owed;
            /// <summary>The per-kind floor, 0 when this key did not qualify for one.</summary>
            public long Floor;
            /// <summary>Why the floor applied: <c>gas</c>, <c>wanted</c>, <c>bankglut</c>, or null.</summary>
            public string FloorReason;
            /// <summary>The item's normalised downstream reach, 0..1.</summary>
            public double Reach;
            /// <summary>Distinct keys whose recipes consume this one directly.</summary>
            public int Consumers;
            /// <summary>What the node is holding right now.</summary>
            public long Stock;
            /// <summary>The multiplier every consumer reads off this target.</summary>
            public double Scarcity;
            /// <summary>What a store holds back from sale: <see cref="Target"/> without the sale
            /// term (<see cref="IOfferKeepBack"/>). Never above <see cref="Target"/>.</summary>
            public long KeepBack;
            /// <summary>Which term set <see cref="KeepBack"/>: <c>owed</c>, <c>flow</c>, a floor
            /// reason (<c>gas</c>, <c>wanted</c>, <c>bankglut</c>), or <c>none</c>.</summary>
            public string KeepBackTerm;
        }

        /// <summary>The full decomposition of one (node, item) target. Returns <c>Target = 0</c> and
        /// <c>Winner = "none"</c> for a key the node has no opinion about, which is a real answer
        /// rather than an absent one.</summary>
        public TargetBreakdown ExplainTarget(string nodeId, string inventoryKey)
        {
            var empty = new TargetBreakdown { Winner = "none" };
            if (string.IsNullOrEmpty(inventoryKey) || !_world.Graph.Nodes.ContainsKey(nodeId)) return empty;
            ItemKind kind;
            if (!_world.TryGetInventoryItemKind(inventoryKey, out kind)) return empty;

            TargetBreakdown b = Explain(nodeId, inventoryKey, kind);
            b.Scarcity = ScarcityMultiplier(nodeId, inventoryKey);
            return b;
        }

        private TargetBreakdown Explain(string nodeId, string inventoryKey, ItemKind kind)
        {
            var b = new TargetBreakdown
            {
                Owed      = OperationalNeedFor(nodeId, inventoryKey),
                Flow      = FlowTarget(nodeId, inventoryKey),
                Sold      = SaleTarget(nodeId, inventoryKey),
                Reach     = Downstream.Reach(inventoryKey),
                Consumers = Downstream.ConsumerCount(inventoryKey),
                Stock     = _world.GetNodeInventory(nodeId, inventoryKey),
                Winner    = "none",
            };

            long target = 0;
            if (b.Owed > target) { target = b.Owed; b.Winner = "owed"; }
            if (b.Flow > target) { target = b.Flow; b.Winner = "flow"; }
            // The station's own need ends here; the sale term below only raises the target.
            long keepBack = target;
            string keepBackTerm = b.Winner;
            if (b.Sold > target) { target = b.Sold; b.Winner = "sold"; }

            long floor = _tuning.ReferenceStockFor(kind);
            if (floor > keepBack)
            {
                // Gas is the one chain whose bootstrap cannot come from a want, because the want is
                // downstream of the capability the want would ask for: no FuelDepot means no gas, no
                // gas means no Item:Gas: demand, and no demand means nothing ever asks for a
                // FuelDepot. FuelDraw credits the flow term only where hulls already depart, which
                // is the same circle one hop out. The floor is what breaks it.
                string reason = null;
                if (kind == ItemKind.Hydrogen || kind == ItemKind.Oxygen) reason = "gas";
                else if (AnythingWants(nodeId, inventoryKey)) reason = "wanted";
                else if (BankBuys(inventoryKey, kind) && b.Stock >= floor) reason = "bankglut";

                if (reason != null)
                {
                    keepBack = floor;
                    keepBackTerm = reason;
                    if (floor > target) { b.Floor = floor; b.FloorReason = reason; target = floor; b.Winner = "floor"; }
                }
            }

            b.Target = target;
            b.KeepBack = keepBack;
            b.KeepBackTerm = keepBackTerm;
            return b;
        }

        /// <summary>The flow term: observed consumption at this node over the coverage horizon,
        /// scaled by how far the item's usefulness reaches above it.
        /// <para><see cref="SpatialNode.DemandHistory"/> is credited in LITRES where a volume is
        /// known (<c>ProductionManager</c> weights the credit by
        /// <see cref="IItemVolumeCatalogue.VolumeLitersPerUnit"/> and falls back to raw units when it
        /// is not), so the same division has to be undone here or a target would be denominated in
        /// litres and compared against a unit stock.</para></summary>
        private long FlowTarget(string nodeId, string inventoryKey)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return 0;

            double weighted;
            if (!node.DemandHistory.TryGetValue(inventoryKey, out weighted) || weighted <= 0) return 0;

            double unitsPerPass = weighted;
            IItemVolumeCatalogue volumes = _world.ItemVolumes;
            if (volumes != null)
            {
                double litersPerUnit = volumes.VolumeLitersPerUnit(inventoryKey);
                if (litersPerUnit > 0) unitsPerPass = weighted / litersPerUnit;
            }
            if (unitsPerPass < _tuning.TargetStockFlowFloorUnits) return 0;

            double coverage = _tuning.TargetStockCoveragePeriods;
            if (coverage <= 0) return 0;

            double reach = Downstream.Reach(inventoryKey);
            double weight = 1.0 + (_tuning.TargetStockDownstreamWeight > 0
                ? _tuning.TargetStockDownstreamWeight * reach : 0.0);

            double target = unitsPerPass * coverage * weight;
            return target >= long.MaxValue ? long.MaxValue : (long)target;
        }

        /// <summary>The sale term: units really bought from this node per pass, over
        /// <see cref="FactionTuning.TargetStockSaleCoveragePeriods"/>.
        /// <para><see cref="SpatialNode.SalesHistory"/> is a decaying accumulator rather than an EMA
        /// (nothing stages a per-pass credit set for it), so a steady rate settles at
        /// <c>unitsPerPass / alpha</c>. Multiplying by the same <c>alpha</c> the fold decays it with
        /// returns it to units per pass — the denomination <see cref="FlowTarget"/> is in, so one
        /// coverage number means the same thing in both.</para></summary>
        private long SaleTarget(string nodeId, string inventoryKey)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return 0;

            double accumulated;
            if (!node.SalesHistory.TryGetValue(inventoryKey, out accumulated) || accumulated <= 0) return 0;

            double periods = _tuning.DemandHistoryEmaPeriods > 0 ? _tuning.DemandHistoryEmaPeriods : 1.0;
            double soldPerPass = accumulated * (2.0 / (periods + 1.0));
            if (soldPerPass < _tuning.TargetStockFlowFloorUnits) return 0;

            double coverage = _tuning.TargetStockSaleCoveragePeriods;
            if (coverage <= 0) return 0;

            double target = soldPerPass * coverage;
            return target >= long.MaxValue ? long.MaxValue : (long)target;
        }

        /// <summary>The inverted recipe graph, built once from the world's recipe registry. Lazy
        /// because the registry is wired after the oracle in both hosts.</summary>
        private ItemDownstreamIndex Downstream
        {
            get
            {
                IRecipeRegistry recipes = _world.Recipes;
                if (_downstream == null || !ReferenceEquals(_downstreamFrom, recipes))
                {
                    _downstream = ItemDownstreamIndex.Build(recipes);
                    _downstreamFrom = recipes;
                }
                return _downstream;
            }
        }

        /// <summary>Whether the central bank's asset-purchase pass would ever buy this key — the one
        /// sale channel that has to keep working, because its buyout is the money supply's only
        /// non-debt faucet. Mirrors <c>WorldModel.IsBuyableCentralBankProcessedGood</c>'s kind
        /// test rather than re-deriving a second rule.</summary>
        private bool BankBuys(string inventoryKey, ItemKind kind)
        {
            return kind == ItemKind.ProcessedGood && !string.IsNullOrEmpty(inventoryKey);
        }

        /// <summary>True when any live <c>Item:</c> row wants <paramref name="inventoryKey"/> at this
        /// node or anywhere in its owner's network. Both scopes count: which of a faction's stations a
        /// delivery lands at is logistics, so a faction-wide want is a reason for any of its nodes to
        /// hold value in the item — the same rule <see cref="StationBuyDemand"/> applies on the
        /// buy side.
        ///
        /// <para>Reads O(1) aggregates rather than <c>GetFor</c>, whose linear scan would walk every
        /// demand in the world on a path TradeManager already calls per dest-node-per-kind.</para></summary>
        private bool AnythingWants(string nodeId, string inventoryKey)
        {
            DemandRegister demand = _world.Demand;
            if (demand == null) return true;      // no register to consult: assume a want, never gate

            if (demand.ItemDemandRowsAtNode(nodeId, inventoryKey) > 0) return true;

            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return true;
            string tag = node.OwnerFactionTag;
            if (string.IsNullOrEmpty(tag)) return true;

            return demand.ItemDemandRowsFor(tag, inventoryKey) > 0;
        }

        /// <summary>What this node is committed to holding of <paramref name="inventoryKey"/>: every
        /// need pinned here in full, plus this node's share of the owning faction's unpinned needs.
        /// <para>The share is per capability-bearing node rather than per owned node — an outpost
        /// with no industry is not a place a faction gathers an install BOM. Both lookups are O(1)
        /// against aggregates <see cref="DemandRegister"/> maintains incrementally; this runs on the
        /// same path TradeManager walks per dest-node-per-kind.</para></summary>
        private long OperationalNeedFor(string nodeId, string inventoryKey)
        {
            DemandRegister demand = _world.Demand;
            if (demand == null) return 0;

            // Both, not just the clock: a need posted during the production cycle is read by the
            // income pass under the same UtcNow, so a clock-only cache would answer with the need as
            // it stood one pass ago.
            if (_needCacheStamp != _world.UtcNow || _needCacheRevision != demand.OperationalRevision)
            {
                _needCache.Clear();
                _needCacheStamp = _world.UtcNow;
                _needCacheRevision = demand.OperationalRevision;
            }
            string cacheKey = nodeId + "\u0001" + inventoryKey;
            long cached;
            if (_needCache.TryGetValue(cacheKey, out cached)) return cached;

            long need = ComputeOperationalNeed(demand, nodeId, inventoryKey);
            _needCache[cacheKey] = need;
            return need;
        }

        private long ComputeOperationalNeed(DemandRegister demand, string nodeId, string inventoryKey)
        {
            long pinned = demand.OperationalDemandAtNode(nodeId, inventoryKey);

            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return pinned;
            string tag = node.OwnerFactionTag;
            if (string.IsNullOrEmpty(tag)) return pinned;

            long shared = demand.OperationalDemandFor(tag, inventoryKey);
            if (shared <= 0) return pinned;

            return pinned + ShareOfFactionNeed(node, tag, inventoryKey, shared);
        }

        /// <summary>This node's claim on a need no single node owes — an install BOM, payable from
        /// wherever the faction holds the material. The rule itself is
        /// <see cref="FactionNeedShare.For"/>, shared with the demand chain's install roots so the
        /// two cannot disagree about what a node owes.</summary>
        private long ShareOfFactionNeed(SpatialNode node, string factionTag, string inventoryKey, long shared)
        {
            long here;
            node.Inventory.TryGetValue(inventoryKey, out here);
            return FactionNeedShare.For(shared, here,
                _world.GetFactionInventoryByKey(factionTag, inventoryKey), HolderCountFor(factionTag));
        }

        /// <summary>How many of <paramref name="factionTag"/>'s nodes have any installed capability,
        /// memoized per tag for the whole cache lifetime.</summary>
        private int HolderCountFor(string factionTag)
        {
            int count;
            if (_holderCountByTag.TryGetValue(factionTag, out count)) return count;

            count = FactionNeedShare.HolderCount(_world.Graph.OwnedBy(factionTag));
            _holderCountByTag[factionTag] = count;
            return count;
        }

    }
}
