using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Why no owned node could host an <see cref="InvestmentRequest"/>'s capability.
    /// Reported by <see cref="InvestmentTargeting"/> so an unresolvable request is distinguishable
    /// from one that simply hasn't been enacted yet.</summary>
    public enum InvestmentTargetBlock
    {
        /// <summary>A node was found; no gate blocked the request.</summary>
        None = 0,
        /// <summary>The faction owns no nodes at all.</summary>
        NoOwnedNodes,
        /// <summary>Owned nodes exist but none is a Founded station (capabilities are station sub-modules).</summary>
        NoFoundedStation,
        /// <summary>Founded stations exist but the faction's influence control at each is at or below 0.5.</summary>
        ControlTooLow,
        /// <summary>The capability's prerequisite capability is not installed anywhere in the network.</summary>
        PrereqMissing,
        /// <summary>Every eligible node already has this capability at its maximum level.</summary>
        AtMaxLevel,
        /// <summary>The faction owns no node at all in the region the request named — the body and
        /// the half of it, ground or well, or the feature, the leg that asked actually runs in. Reported instead of
        /// the gate ladder, never alongside it: with no node in the region there is no gate any node
        /// in it could have failed.</summary>
        OutOfRegion,
        /// <summary>Eligible nodes exist but none has non-zero capability affinity for this capability.</summary>
        NoAffinity,
    }

    /// <summary>The owned node an <see cref="InvestmentRequest"/> is aiming at, or the gate that
    /// stopped it. Resolution is a pure function of the current graph, so both the brain's
    /// <c>Develop</c> candidate generation and read-only observers (dashboard snapshot, chat
    /// commands) can derive the same answer without storing it on the request.</summary>
    public struct InvestmentTarget
    {
        /// <summary>Owned node id the request resolves to, or null when <see cref="Block"/> explains why not.</summary>
        public string NodeId;
        /// <summary>Capability affinity of the winning node, 0 when unresolved.</summary>
        public double Fit;
        /// <summary>True when a region-scoped request resolved inside the region it named. False when
        /// it was never region-scoped — and, since there is no off-region fallback, false on every
        /// unresolved target too.</summary>
        public bool OnTargetPlanet;

        public InvestmentTargetBlock Block;

        public bool Resolved { get { return NodeId != null; } }
    }

    /// <summary>An active <see cref="InvestmentRequest"/> paired with the node it resolves to —
    /// the shape read-only observers (dashboard snapshot, <c>station info</c>) report.</summary>
    public struct InvestmentSignal
    {
        public InvestmentRequest Request;
        public InvestmentTarget Target;
        /// <summary>False when the faction's treasury is below the Develop floor, i.e. the brain
        /// won't generate a Develop candidate for this request at all yet.</summary>
        public bool Affordable;
        /// <summary>The install's material gate, per key — the other half of <see cref="Affordable"/>.
        /// Empty for a capability that costs no items. See <see cref="InstallMaterials"/>.</summary>
        public List<InstallMaterialRow> Materials;

        /// <summary>True when every material row is covered, i.e. only the treasury and the target
        /// gates can still be holding this install up.</summary>
        public bool MaterialsMet
        {
            get
            {
                if (Materials == null) return true;
                foreach (InstallMaterialRow r in Materials)
                    if (r.Shortfall > 0) return false;
                return true;
            }
        }
    }

    /// <summary>Resolves an <see cref="InvestmentRequest"/> (a capability-level need posted by a
    /// manager) to the owned node that should receive it. Managers post network- or planet-scoped
    /// needs and never name a node; the node choice lives here so
    /// <see cref="FactionBrain"/> and observers agree on it.</summary>
    public static class InvestmentTargeting
    {
        /// <summary>Per-capability count of owned nodes with that capability installed at level >= 1.
        /// Indexed by <c>(int)NodeCapabilityKind</c>, so sized to <see cref="FactionTuning.CapabilityKindCount"/>
        /// — not to <see cref="FactionTuning.AllCapabilities"/>, which omits kinds and would drop the
        /// highest ordinals from the count.</summary>
        public static int[] InstalledCounts(IEnumerable<SpatialNode> owned)
        {
            int[] installed = new int[FactionTuning.CapabilityKindCount];
            if (owned == null) return installed;
            foreach (SpatialNode n in owned)
                foreach (KeyValuePair<NodeCapabilityKind, int> kv in n.Capabilities)
                    if ((int)kv.Key < installed.Length && kv.Value >= 1) installed[(int)kv.Key]++;
            return installed;
        }

        /// <summary>True when the faction can currently fund a Develop action. The brain skips
        /// Develop candidate generation entirely below this line, so an unaffordable faction's
        /// resolved target is "where this would go", not "where this is going".</summary>
        public static bool CanAffordDevelop(IWorldView world, string factionTag, FactionTuning t)
        {
            return world.GetTreasury(factionTag) > t.InsolvencyFloor + t.DevelopCost;
        }

        /// <summary>All active requests for a faction, each resolved to its target node. Owned nodes,
        /// installed counts and the affordability check are computed once for the whole snapshot.</summary>
        public static List<InvestmentSignal> SnapshotSignals(IWorldView world, string factionTag, FactionTuning t)
        {
            List<InvestmentRequest> requests = world.InvestmentRegister.SnapshotFor(factionTag);
            var signals = new List<InvestmentSignal>(requests.Count);
            if (requests.Count == 0) return signals;

            var owned = new List<SpatialNode>(world.Graph.OwnedBy(factionTag));
            int[] installed = InstalledCounts(owned);
            bool affordable = CanAffordDevelop(world, factionTag, t);

            foreach (InvestmentRequest req in requests)
                signals.Add(new InvestmentSignal
                {
                    Request    = req,
                    Target     = Resolve(world, factionTag, owned, installed, req, t),
                    Affordable = affordable,
                    Materials  = InstallMaterials.For(world, factionTag, req.Cap, t),
                });
            return signals;
        }

        /// <summary>Convenience overload that reads owned nodes and installed counts from the world.
        /// Callers resolving several requests for one faction should compute both once and use the
        /// primary overload instead.</summary>
        public static InvestmentTarget Resolve(IWorldView world, string factionTag,
            InvestmentRequest req, FactionTuning t)
        {
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(factionTag));
            return Resolve(world, factionTag, owned, InstalledCounts(owned), req, t);
        }

        /// <summary>
        /// A region-scoped request (a local Shipyard needed because a same-body leg has no
        /// route-feasible ship) resolves <b>only</b> inside the region it names — the body and the
        /// half of it, ground or well, that <see cref="InvestmentRequest.TargetSite"/> carries. A
        /// Shipyard anywhere else cannot serve the leg that asked, and cannot satisfy the poster's own
        /// coverage check either, so an install placed outside the region leaves the request standing
        /// and the next tick spends another install on the next node.
        ///
        /// <para><b>There is deliberately no unrestricted fallback.</b> It existed to avoid "silently
        /// dropping the investment", but the drop is not silent: an unresolved target carries its
        /// <see cref="InvestmentTargetBlock"/> to the dashboard and to <c>station info</c>, and
        /// <c>FactionBrain.AddDevelopCandidates</c> emits no Develop candidate for it — so an
        /// unplaceable request stops outbidding ordinary development instead of consuming every
        /// Develop tick the faction can afford. See FAC-236.</para></summary>
        public static InvestmentTarget Resolve(IWorldView world, string factionTag,
            List<SpatialNode> owned, int[] installed, InvestmentRequest req, FactionTuning t)
        {
            NodeRegion region = NodeRegion.Of(req);
            if (!region.IsNone)
            {
                InvestmentTarget inRegion = BestNode(world, factionTag, owned, installed, req, t, region);
                inRegion.OnTargetPlanet = inRegion.Resolved;
                return inRegion;
            }
            return BestNode(world, factionTag, owned, installed, req, t, NodeRegion.None);
        }

        /// <summary>Best-fit owned node for one pass — same prerequisite/control/max-level gates
        /// either way. When <paramref name="region"/> is not <see cref="NodeRegion.None"/>, nodes
        /// outside it (see <see cref="ShipyardCoverage.InRegion"/>) are dropped BEFORE the ladder, so
        /// they neither win nor colour the reported reason; a region holding no owned node reports
        /// <see cref="InvestmentTargetBlock.OutOfRegion"/> rather than a gate no node in it ever
        /// reached.</summary>
        private static InvestmentTarget BestNode(IWorldView world, string factionTag,
            List<SpatialNode> owned, int[] installed, InvestmentRequest req, FactionTuning t,
            NodeRegion region)
        {
            bool restrictToTargetRegion = !region.IsNone;
            var result = new InvestmentTarget();
            if (owned == null || owned.Count == 0)
            {
                result.Block = InvestmentTargetBlock.NoOwnedNodes;
                return result;
            }

            SpatialNode best = null;
            double bestFit = 0;
            // Each passed gate advances the reason to the next one that could still apply, so the
            // block finally reported is the furthest gate any owned node actually reached.
            InvestmentTargetBlock block = InvestmentTargetBlock.NoFoundedStation;
            int inRegion = 0;

            foreach (SpatialNode n in owned)
            {
                // The region is a PRECONDITION, not a gate, so it is tested before the ladder and an
                // out-of-region node advances nothing. Ordered the other way it decided the reported
                // reason for requests it had nothing to do with: any owned node at max level, however
                // far away, pinned the answer at AtMaxLevel and the region test was never reached.
                // FAC-238 — a faction owning no station in the region read "already at max level".
                if (restrictToTargetRegion && !ShipyardCoverage.InRegion(n, region)) continue;
                inRegion++;

                if (n.StableStationId == 0) continue;              // capabilities are station sub-modules; need a Founded node
                block = Furthest(block, InvestmentTargetBlock.ControlTooLow);

                double control = world.Influence != null ? world.Influence.ControlAt(n.Id, factionTag) : 1.0;
                if (control <= 0.5) continue;
                block = Furthest(block, InvestmentTargetBlock.PrereqMissing);

                NodeCapabilityKind? reqPrereq = CapabilityPrerequisites.For(req.Cap);
                if (reqPrereq.HasValue && installed[(int)reqPrereq.Value] < 1) continue;
                block = Furthest(block, InvestmentTargetBlock.AtMaxLevel);

                int level; n.Capabilities.TryGetValue(req.Cap, out level);
                if (level >= t.MaxLevelFor(req.Cap)) continue;     // already at max (or Shipyard/FuelDepot capped at 1)
                block = Furthest(block, InvestmentTargetBlock.NoAffinity);

                double fit = NodeContent.CapabilityAffinityFor(n, req.Cap, t);
                // A Farm grows any crop it has a seed for; native crops are free seeds, so they are a
                // preference and not a gate (FarmCrops.SitingPreference).
                if (req.Cap == NodeCapabilityKind.Farm)
                    fit *= FarmCrops.SitingPreference(n, world.Planets, req.Crop);
                if (fit > bestFit) { bestFit = fit; best = n; }
            }

            if (best != null)
            {
                result.NodeId = best.Id;
                result.Fit = bestFit;
                result.Block = InvestmentTargetBlock.None;
                return result;
            }

            // Nothing in the region at all is its own answer, and a truer one than whatever the
            // ladder would have said about nodes elsewhere -- there were none to say it about.
            result.Block = restrictToTargetRegion && inRegion == 0
                ? InvestmentTargetBlock.OutOfRegion
                : block;
            return result;
        }

        /// <summary>Blocks are ordered by how far a node got before failing; the reported reason is
        /// the furthest any node reached.</summary>
        private static InvestmentTargetBlock Furthest(InvestmentTargetBlock a, InvestmentTargetBlock b)
        {
            return (int)b > (int)a ? b : a;
        }

        /// <summary>Short human-readable reason, for logs / chat / dashboard payloads.</summary>
        public static string Describe(InvestmentTargetBlock block)
        {
            switch (block)
            {
                case InvestmentTargetBlock.NoOwnedNodes:     return "no owned nodes";
                case InvestmentTargetBlock.NoFoundedStation: return "no founded station";
                case InvestmentTargetBlock.ControlTooLow:    return "control too low";
                case InvestmentTargetBlock.PrereqMissing:    return "prerequisite capability missing";
                case InvestmentTargetBlock.AtMaxLevel:       return "already at max level";
                case InvestmentTargetBlock.OutOfRegion:      return "no owned node in the region asked for";
                case InvestmentTargetBlock.NoAffinity:       return "no suitable node";
                default:                                     return "";
            }
        }
    }
}
