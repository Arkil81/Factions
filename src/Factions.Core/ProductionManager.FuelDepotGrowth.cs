using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>The gas stage's measurements for one region of an estate (<see cref="NodeRegion"/>):
    /// where the fuel is burned, where the depots stand, and where the ice is. The faction totals in
    /// <see cref="ProductionManager.GasStageRates"/> say whether the estate needs another level; this
    /// says where it should go.</summary>
    internal sealed class GasRegionLedger
    {
        /// <summary>Gas burned by legs departing the region's nodes, units per minute.</summary>
        public double BurnPerMin;
        /// <summary>FuelDepot levels installed in the region.</summary>
        public int Levels;
        /// <summary>Levels an install could still add in the region (see
        /// <see cref="ProductionManager.GasStageRates.Headroom"/>).</summary>
        public int Headroom;
        /// <summary><c>Ore:Ice</c> held at the region's owned nodes, depots and deposits alike.</summary>
        public double IceUnits;
        /// <summary>Ice an hour of the Mine trips bound for the region's ice hubs delivers.</summary>
        public double IceInFlight;
    }

    public sealed partial class ProductionManager
    {
        /// <summary>Last FuelDepot growth verdict per faction tag, so a hold is logged when it starts
        /// rather than every pass. In memory only.</summary>
        private readonly Dictionary<string, string> _fuelDepotVerdictLast =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static GasRegionLedger GasRegionFor(Dictionary<NodeRegion, GasRegionLedger> regions, NodeRegion region)
        {
            GasRegionLedger entry;
            if (!regions.TryGetValue(region, out entry))
            {
                entry = new GasRegionLedger();
                regions[region] = entry;
            }
            return entry;
        }

        /// <summary>The in-flight ice rate of each of <paramref name="st"/>'s ice extraction orders,
        /// keyed by hub, and whether any of them is open. Null when the faction has no ice order.
        /// Reads the plan the last committed Operations pass published; a player faction has none.</summary>
        private static Dictionary<string, double> IceInFlightByHub(FactionState st, string iceKey, ref GasStageRates rates)
        {
            if (st == null || st.ExtractionOrders.Count == 0) return null;
            Dictionary<string, double> byHub = null;
            foreach (StandingExtractionOrder order in st.ExtractionOrders)
            {
                if (order == null || order.HubNodeId == null
                    || !string.Equals(order.OreKey, iceKey, StringComparison.Ordinal)) continue;
                if (!order.Paused && order.TargetPerHour > 0) rates.IceOrderOpen = true;
                if (order.InFlightPerHour <= 0) continue;
                if (byHub == null) byHub = new Dictionary<string, double>(StringComparer.Ordinal);
                double prior;
                byHub.TryGetValue(order.HubNodeId, out prior);
                byHub[order.HubNodeId] = prior + order.InFlightPerHour;
            }
            return byHub;
        }

        /// <summary>
        /// Lever (b) of FAC-109: a faction whose measured fleet burn exceeds what its installed
        /// FuelDepot levels make asks for more levels, sized to the gap, sited where the burn is, and
        /// only when the ice to run them is there or on its way.
        ///
        /// <para><b>Trigger.</b> <c>burn &gt; output</c>, where burn is the gas every departing leg
        /// needed, met or not (<see cref="SpatialNode.FuelBurnEma"/>, credited by
        /// <see cref="FuelDraw"/>), and output is <c>levels × LevelPerMin</c>. The request is sized
        /// to the gap: <c>want = ceil(gap ÷ LevelPerMin)</c>, capped by the levels the estate can
        /// still take (<see cref="GasStageRates.Headroom"/>), and its urgency is the gap over output
        /// (<see cref="ImbalanceUrgency"/>) times the ice cover. The register holds one request per
        /// (faction, capability, region) and each Develop installs one level, so a pass never asks
        /// for more than one at a time; the request stands until the gap closes.</para>
        ///
        /// <para><b>Siting.</b> The region (<see cref="NodeRegion"/>) with the largest
        /// <c>burn − output</c> that still has headroom is the one asked for: the request is
        /// region-scoped, so <see cref="InvestmentTargeting"/> picks the node inside it by
        /// <see cref="NodeContent.CapabilityAffinityFor"/> — an existing depot below the cap, or a
        /// new site. Open-space nodes, and a faction whose short regions are all full, get the
        /// unscoped request, which resolves anywhere.</para>
        ///
        /// <para><b>Ice.</b> The cover is the ice held in the chosen region plus an hour of the Mine
        /// trips bound for its ice hubs, over one level-hour of ice, capped at 1 — ice on another body
        /// no longer counts as cover for this one. The unscoped request reads the faction's ice. When
        /// that cover is below <see cref="FactionTuning.FuelDepotOrderedIceCover"/> and the faction
        /// has an open ice extraction order, the order's share is used instead (<c>ice_src=order</c>).
        /// With neither, the stage waits and says so once
        /// (<c>THROUGHPUT_HOLD FuelDepot reason=no-ice</c>).</para>
        ///
        /// <para>Never for a player faction that has not opted the capability in
        /// (<see cref="FactionLifecycle.CanPostInvestmentFor"/>): <c>reason=opt-in</c>.</para>
        /// </summary>
        private void DecideFuelDepotGrowth(FactionTuning tuning, FactionState st, IProductionExecutionContext ctx,
            GasStageRates gas)
        {
            if (st == null || st.Tag == null) return;
            if (gas.BurnPerMin <= gas.CapacityPerMin)
            {
                _fuelDepotVerdictLast.Remove(st.Tag);
                return;
            }

            double gap = gas.BurnPerMin - gas.CapacityPerMin;
            NodeRegion scope;
            GasRegionLedger target = ChooseDepotRegion(gas, out scope);
            int headroom = target != null ? target.Headroom : gas.Headroom;

            double iceHeld = target != null ? target.IceUnits + target.IceInFlight : gas.IceUnits + gas.IceInFlight;
            double levelHourIce = gas.IcePerLevelPerMin * 60.0;
            double iceCover = levelHourIce > 0 ? Math.Min(1.0, iceHeld / levelHourIce) : 0.0;
            string iceSrc = iceCover > 0 ? "held" : "none";
            if (gas.IceOrderOpen && iceCover < tuning.FuelDepotOrderedIceCover)
            {
                iceCover = Math.Min(1.0, tuning.FuelDepotOrderedIceCover);
                iceSrc = "order";
            }

            int want = 0;
            if (gas.LevelPerMin > 0)
            {
                double levels = Math.Ceiling(gap / gas.LevelPerMin);
                want = levels >= headroom ? headroom : (int)levels;
            }

            string reason = FuelDepotGrowthReason(gas, iceCover, gap, want, iceSrc, scope);

            string hold = null;
            if (!FactionLifecycle.CanPostInvestmentFor(st, NodeCapabilityKind.FuelDepot)) hold = "opt-in";
            else if (want <= 0) hold = "at-cap";
            else if (iceCover <= 0) hold = "no-ice";

            if (hold != null)
            {
                string was;
                if (!_fuelDepotVerdictLast.TryGetValue(st.Tag, out was) || was != hold)
                {
                    _fuelDepotVerdictLast[st.Tag] = hold;
                    ctx.AddLog(st.Tag + " THROUGHPUT_HOLD FuelDepot reason=" + hold + " (" + reason + ")");
                }
                return;
            }

            _fuelDepotVerdictLast[st.Tag] = "posted";
            double urgency = ImbalanceUrgency(gas.BurnPerMin, gas.CapacityPerMin) * iceCover;
            PostThroughputInvestment(tuning, st, NodeCapabilityKind.FuelDepot, ctx, urgency, reason, scope);
        }

        /// <summary>The region a new level should go to: the one burning furthest past its own
        /// output that still has headroom. <paramref name="scope"/> is the region to post the request
        /// for, <see cref="NodeRegion.None"/> for the unscoped request. Returns that region's ledger,
        /// or null for the unscoped request, whose measurements are the faction's. Open space is a
        /// candidate like any other and, when it wins, posts unscoped.</summary>
        private static GasRegionLedger ChooseDepotRegion(GasStageRates gas, out NodeRegion scope)
        {
            scope = NodeRegion.None;
            if (gas.Regions == null) return null;

            GasRegionLedger best = null;
            NodeRegion bestRegion = NodeRegion.None;
            double bestGap = 0;
            foreach (KeyValuePair<NodeRegion, GasRegionLedger> kv in gas.Regions)
            {
                GasRegionLedger r = kv.Value;
                if (r.Headroom <= 0) continue;
                double regionGap = r.BurnPerMin - r.Levels * gas.LevelPerMin;
                // Strictly greater: a tie keeps the region met first, and the owned-node order the
                // regions were filled in is stable from pass to pass.
                if (regionGap <= bestGap) continue;
                best = r;
                bestRegion = kv.Key;
                bestGap = regionGap;
            }

            if (best == null || bestRegion.IsNone) return null;
            scope = bestRegion;
            return best;
        }

        /// <summary>The gas stage's reason string with the growth fields appended, so the line an
        /// operator greps keeps its existing prefix.</summary>
        private static string FuelDepotGrowthReason(GasStageRates gas, double iceCover, double gap, int want,
            string iceSrc, NodeRegion scope)
        {
            return GasStageReason(gas, iceCover)
                + " levels=" + gas.Levels.ToString(CultureInfo.InvariantCulture)
                + " output=" + gas.CapacityPerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " gap=" + gap.ToString("F1", CultureInfo.InvariantCulture)
                + " want=" + want.ToString(CultureInfo.InvariantCulture)
                + " ice_src=" + iceSrc
                + " " + scope.Describe();
        }
    }
}
