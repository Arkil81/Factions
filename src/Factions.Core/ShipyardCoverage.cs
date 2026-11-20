using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The part of an estate a local need is scoped to. Three kinds, one value: a body's ground, a
    /// body's well, or an authored feature in open space. Anything outside all three is no region
    /// at all (<see cref="IsNone"/>), and no coverage question is asked there — open deep space is
    /// connective tissue, not somewhere a faction is asked to keep a yard or a refinery. First
    /// written for Shipyard coverage; the throughput stage's per-region ore ledger
    /// (<see cref="ProductionManager"/>) reads the same value, which is why it is not named for
    /// yards.
    ///
    /// <para><b>Planet wins over feature.</b> A feature anchored inside a well (a ring around the gas
    /// giant) is authored to place nodes; the nodes it places still carry the well's
    /// <see cref="SpatialNode.PlanetId"/>, and the well is the region a leg between them runs in.
    /// The feature kind exists for the nodes no body claims — <see cref="SpatialNode.PlanetId"/>
    /// null, <see cref="SpatialNode.FeatureKey"/> set — which before this was every deep-space node
    /// reading as one undifferentiated place.</para>
    /// </summary>
    public struct NodeRegion : IEquatable<NodeRegion>
    {
        /// <summary><see cref="SpatialNode.PlanetId"/> for a body region; null for a feature region.</summary>
        public readonly string PlanetId;
        /// <summary>Which half of the body: <see cref="StationSite.Surface"/> or, for everything
        /// else inside the well, <see cref="StationSite.Orbital"/>. Meaningless when
        /// <see cref="PlanetId"/> is null.</summary>
        public readonly StationSite Site;
        /// <summary><see cref="SpatialNode.FeatureKey"/> for a feature region; null for a body region.</summary>
        public readonly string FeatureKey;

        private NodeRegion(string planetId, StationSite site, string featureKey)
        {
            PlanetId = planetId;
            Site = site;
            FeatureKey = featureKey;
        }

        public static NodeRegion Body(string planetId, StationSite site)
        {
            return new NodeRegion(planetId, site == StationSite.Surface ? StationSite.Surface : StationSite.Orbital, null);
        }

        public static NodeRegion Feature(string featureKey)
        {
            return new NodeRegion(null, StationSite.Space, featureKey);
        }

        public static NodeRegion None { get { return new NodeRegion(null, StationSite.Space, null); } }

        public bool IsBody    { get { return PlanetId != null; } }
        public bool IsFeature { get { return PlanetId == null && FeatureKey != null; } }
        public bool IsNone    { get { return PlanetId == null && FeatureKey == null; } }

        /// <summary>The region <paramref name="req"/> names, or <see cref="None"/> for an unscoped
        /// request. A request naming a body without a site is read as its ground — the shape every
        /// planet-scoped poster wrote before <see cref="InvestmentRequest.TargetSite"/> existed.</summary>
        public static NodeRegion Of(InvestmentRequest req)
        {
            if (req.TargetPlanetId != null)
                return Body(req.TargetPlanetId, req.TargetSite.HasValue ? req.TargetSite.Value : StationSite.Surface);
            if (req.TargetFeature != null) return Feature(req.TargetFeature);
            return None;
        }

        /// <summary>Stamp this region onto <paramref name="req"/>'s scoping fields.</summary>
        public InvestmentRequest Scope(InvestmentRequest req)
        {
            req.TargetPlanetId = PlanetId;
            req.TargetSite     = IsBody ? (StationSite?)Site : null;
            req.TargetFeature  = IsFeature ? FeatureKey : null;
            return req;
        }

        /// <summary>The log form: <c>planet=X site=Y</c> or <c>feature=Name</c>, so a request can be
        /// grepped by the region it named whichever kind it is.</summary>
        public string Describe()
        {
            if (IsBody) return "planet=" + PlanetId + " site=" + Site;
            if (IsFeature) return "feature=" + FeatureKey;
            return "region=none";
        }

        public bool Equals(NodeRegion other)
        {
            return string.Equals(PlanetId, other.PlanetId, StringComparison.Ordinal)
                && Site == other.Site
                && string.Equals(FeatureKey, other.FeatureKey, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return obj is NodeRegion && Equals((NodeRegion)obj); }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PlanetId == null ? 0 : StringComparer.Ordinal.GetHashCode(PlanetId);
                hash = (hash * 397) ^ (int)Site;
                hash = (hash * 397) ^ (FeatureKey == null ? 0 : StringComparer.Ordinal.GetHashCode(FeatureKey));
                return hash;
            }
        }

        public override string ToString() { return Describe(); }

        /// <summary>A stable, parseable key — <c>feature:Name</c>, <c>body:PlanetId:Surface</c>,
        /// <c>body:PlanetId:Orbital</c> — for carrying a region through an intent or a log line.
        /// Null for <see cref="None"/>.</summary>
        public string ToKey()
        {
            if (IsFeature) return "feature:" + FeatureKey;
            if (IsBody) return "body:" + PlanetId + ":" + Site;
            return null;
        }

        public static bool TryFromKey(string key, out NodeRegion region)
        {
            region = None;
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith("feature:", StringComparison.Ordinal))
            {
                string name = key.Substring("feature:".Length);
                if (name.Length == 0) return false;
                region = Feature(name);
                return true;
            }
            if (key.StartsWith("body:", StringComparison.Ordinal))
            {
                int cut = key.LastIndexOf(':');
                if (cut <= "body:".Length) return false;
                string planetId = key.Substring("body:".Length, cut - "body:".Length);
                StationSite site;
                if (planetId.Length == 0 || !Enum.TryParse(key.Substring(cut + 1), out site)) return false;
                region = Body(planetId, site);
                return true;
            }
            return false;
        }

        /// <summary>The region a campaign objective names, in the objective's own vocabulary
        /// (<see cref="PlacementRegionKind"/> + key + planet id), or <see cref="None"/>.</summary>
        public static NodeRegion FromObjective(CampaignObjective o)
        {
            if (o == null || !o.TargetRegionKind.HasValue) return None;
            switch (o.TargetRegionKind.Value)
            {
                case PlacementRegionKind.Feature:
                    return string.IsNullOrEmpty(o.TargetRegionKey) ? None : Feature(o.TargetRegionKey);
                case PlacementRegionKind.PlanetSurface:
                    return string.IsNullOrEmpty(o.TargetPlanetId) ? None : Body(o.TargetPlanetId, StationSite.Surface);
                case PlacementRegionKind.PlanetOrbit:
                    return string.IsNullOrEmpty(o.TargetPlanetId) ? None : Body(o.TargetPlanetId, StationSite.Orbital);
                default:
                    return None;
            }
        }

        /// <summary>Stamp this region onto an objective's region fields.</summary>
        public void ApplyTo(CampaignObjective o)
        {
            if (o == null) return;
            if (IsFeature)
            {
                o.TargetRegionKind = PlacementRegionKind.Feature;
                o.TargetRegionKey = FeatureKey;
                o.TargetPlanetId = null;
            }
            else if (IsBody)
            {
                o.TargetRegionKind = Site == StationSite.Surface ? PlacementRegionKind.PlanetSurface : PlacementRegionKind.PlanetOrbit;
                o.TargetRegionKey = PlanetId;
                o.TargetPlanetId = PlanetId;
            }
            else
            {
                o.TargetRegionKind = null;
                o.TargetRegionKey = null;
                o.TargetPlanetId = null;
            }
        }

        /// <summary>The region <paramref name="n"/> stands in. A body claims a node before a feature
        /// does; anything in a well not standing on terrain is in the well, including a node whose
        /// <see cref="SpatialNode.Site"/> has not been derived yet — the answer that grants no ground
        /// capacity is the safe default, the same reasoning <see cref="StationSite.Space"/> is zero
        /// for. Outside every body and feature: <see cref="None"/>.</summary>
        public static NodeRegion Of(SpatialNode n)
        {
            if (n == null) return None;
            if (n.PlanetId != null) return Body(n.PlanetId, n.Site);
            if (n.FeatureKey != null) return Feature(n.FeatureKey);
            return None;
        }

        /// <summary>True when <paramref name="n"/> stands in this region. Never true for
        /// <see cref="None"/>: no node is "in" the absence of a region.</summary>
        public bool Contains(SpatialNode n)
        {
            if (n == null || IsNone) return false;
            return Of(n).Equals(this);
        }
    }

    /// <summary>What one region holds, as far as a Shipyard ask is concerned. Reported so the log
    /// line carries the census the decision was made on.</summary>
    public struct ShipyardCensus
    {
        /// <summary>Owned nodes standing in the region.</summary>
        public int Nodes;
        /// <summary>Owned Shipyards in the region.</summary>
        public int Yards;
        /// <summary>Those yards currently absorbing components (<see cref="ShipyardCoverage.IsWorking"/>).</summary>
        public int Working;
        /// <summary>Whether any founded station in the region can still receive a Shipyard.</summary>
        public bool Hostable;
    }

    /// <summary>
    /// Whether a faction's existing Shipyards already answer a local build need, and where another
    /// one would have to stand to answer it.
    ///
    /// <para><b>One predicate, shared by the poster and the placement.</b>
    /// <see cref="ShipPool.MaybePostLocalShipyardInvestment"/> and the coverage sweep in
    /// <see cref="ShipyardManager"/> decide whether to ask, and <see cref="InvestmentTargeting"/>
    /// decides where the install lands. When those two read different rules the request survives
    /// its own remedy: the ask is re-posted every tick, the install goes somewhere the ask cannot
    /// see, and since a Shipyard caps at level 1 the next tick simply picks the next node. All of
    /// them call in here so the question and the answer stay the same question.</para>
    ///
    /// <para><b>The region is a body's ground, a body's well, or an authored feature</b> — see
    /// <see cref="NodeRegion"/>. Not <see cref="NodeZone"/>, the relative-gravity label FAC-096
    /// stage 2 replaced. Not <see cref="PlacementRegion"/> either: its planet key is the generator
    /// subtype, deliberately, so two different bodies of one generator would collide. A leg between
    /// two nodes in the same well is served by a yard in that well; a leg across a body's terrain
    /// needs one on that terrain, which is what <see cref="StationSite"/> separates; and a leg
    /// between two nodes of one nest is served by a yard in that nest.</para>
    /// </summary>
    public static class ShipyardCoverage
    {
        /// <summary>The region <paramref name="n"/> stands in. A body claims a node before a feature
        /// does; anything in a well not standing on terrain is in the well, including a node whose
        /// <see cref="SpatialNode.Site"/> has not been derived yet — the answer that grants no ground
        /// capacity is the safe default, the same reasoning <see cref="StationSite.Space"/> is zero
        /// for. Outside every body and feature: <see cref="NodeRegion.None"/>.</summary>
        public static NodeRegion RegionOf(SpatialNode n) { return NodeRegion.Of(n); }

        /// <summary>True when <paramref name="n"/> stands in <paramref name="region"/>. Never true
        /// for <see cref="NodeRegion.None"/>: no node is "in" the absence of a region.</summary>
        public static bool InRegion(SpatialNode n, NodeRegion region) { return region.Contains(n); }

        /// <summary>
        /// Whether a Shipyard in this region is currently turning components into hulls.
        ///
        /// <para>A station refit (<see cref="ShipyardManager.IsStationRefit"/>) is not counted:
        /// it occupies the slot but produces no hull, so a yard rebuilding its own station reads as
        /// idle here rather than as saturated capacity.</para>
        ///
        /// <para>"Busy" alone is not a rate: a slot held by an entry that has absorbed nothing for
        /// hours reads as occupied forever. <see cref="BuildQueueEntry.StalledSeconds"/> is reset to
        /// zero the moment any component lands, so it is the one measure of whether the yard is being
        /// fed — and a yard that is not being fed does not get more capacity beside it to split the
        /// same starved component stream.</para>
        ///
        /// <para><b>The question is saturation, not activity</b>, which is why this counts rather than
        /// returning on the first fed entry. A yard runs <see cref="SpatialNode.BuildSlotCount"/>
        /// builds at once; one whose every slot is fed has nothing spare, while one feeding two of its
        /// three has room for the work another yard would take. Same argument as the throughput
        /// stage's utilisation term, and it is what keeps the anti-sprawl gate meaning the same thing
        /// after a Shipyard can be upgraded: a level-3 yard with one build running is not full.</para>
        /// </summary>
        public static bool IsWorking(SpatialNode yard, FactionTuning t)
        {
            if (yard == null || yard.ShipyardPaused) return false;
            double stallCeiling = t == null ? 0.0 : t.ShipyardWorkingStallSeconds;
            int fed = 0;
            foreach (BuildQueueEntry e in yard.BuildQueue)
            {
                // A station refit holds a slot and is fed the same way, but it is the yard's own
                // rebuild rather than hull throughput: counting it would have a faction ask for
                // capacity beside a yard that is not building ships at all.
                if (ShipyardManager.IsStationRefit(e)) continue;
                if (!yard.IsActiveBuild(e)) continue;
                if (e.StalledSeconds <= stallCeiling) fed++;
            }
            return fed >= yard.BuildSlotCount();
        }

        /// <summary>One pass over <paramref name="owned"/>, bucketed by region. Nodes in no region
        /// are not counted anywhere. This is the census every coverage question reads, whether it
        /// asks about one region (<see cref="WantsAnotherYard"/>) or all of them (the sweep).</summary>
        public static Dictionary<NodeRegion, ShipyardCensus> Census(IEnumerable<SpatialNode> owned, FactionTuning t)
        {
            var census = new Dictionary<NodeRegion, ShipyardCensus>();
            if (owned == null) return census;
            int maxLevel = t == null ? 1 : t.MaxLevelFor(NodeCapabilityKind.Shipyard);
            foreach (SpatialNode n in owned)
            {
                NodeRegion region = RegionOf(n);
                if (region.IsNone) continue;
                ShipyardCensus c;
                census.TryGetValue(region, out c);
                c.Nodes++;
                int level;
                bool has = n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) && level >= 1;
                // Capabilities are station sub-modules, so only a Founded node can ever receive one.
                if (n.StableStationId != 0 && level < maxLevel) c.Hostable = true;
                if (has)
                {
                    c.Yards++;
                    if (IsWorking(n, t)) c.Working++;
                }
                census[region] = c;
            }
            return census;
        }

        /// <summary>
        /// Whether this faction should ask for a Shipyard in <paramref name="region"/>.
        ///
        /// <para><b>The coverage arms.</b> A region holding no yard of this faction's says yes
        /// outright — that is the coverage the ask exists for. A region that already holds one says
        /// yes only when <em>every</em> yard in it is working: another yard earns its install cost
        /// when the ones present are saturated, and nothing when they are idle. Same argument as the
        /// throughput stage's utilisation term — adding capacity cannot raise output when the
        /// constraint is input arrival.</para>
        ///
        /// <para><b>And two refusals, because an ask nobody can answer is not free.</b>
        /// <see cref="ProductionManager"/>'s install reservation walks the raw request list and never
        /// asks whether a request resolves to a node, so a standing request no install can satisfy
        /// holds its whole BOM out of routable inventory for as long as it stands. Measured live
        /// 2026-09-02: IGSL 7,180 and IMPR 6,913 component units reserved against their only
        /// investment request, which resolved to nothing in both cases. So this refuses to ask when
        /// the region contains no owned <b>founded station</b> below the Shipyard level cap (nowhere
        /// to put one — IMPR's case, every station in the region already at max, and IGSL's, whose
        /// leg ran between unfounded nodes), and when the Shipyard's prerequisite capability is
        /// installed nowhere in the network (nothing could be installed yet). Both read the same
        /// helpers <see cref="InvestmentTargeting"/> resolves with, rather than a second copy of its
        /// ladder. See FAC-238.</para>
        ///
        /// <para><b>Influence control is deliberately NOT checked here</b>, though the placement
        /// gates on it. It is the one gate that is genuinely transient — control recovers on its own,
        /// and a request that waits out a contested node is doing its job. The two above are stable
        /// states that no amount of waiting resolves.</para>
        ///
        /// <para><paramref name="yards"/> and <paramref name="working"/> are reported so the log line
        /// carries the measurement the decision was made on.</para>
        /// </summary>
        public static bool WantsAnotherYard(IEnumerable<SpatialNode> owned, NodeRegion region,
            FactionTuning t, out int yards, out int working)
        {
            yards = 0;
            working = 0;
            if (owned == null || region.IsNone) return false;
            var ownedList = owned as List<SpatialNode> ?? new List<SpatialNode>(owned);
            ShipyardCensus c;
            if (!Census(ownedList, t).TryGetValue(region, out c)) return false;
            yards = c.Yards;
            working = c.Working;
            return Wants(c, ownedList);
        }

        /// <summary>The decision <see cref="WantsAnotherYard"/> documents, on a census already taken.
        /// <paramref name="owned"/> is read only for the network-wide prerequisite check.</summary>
        public static bool Wants(ShipyardCensus c, IEnumerable<SpatialNode> owned)
        {
            if (!c.Hostable) return false;
            // The prerequisite is read from the map InvestmentTargeting resolves against, never named
            // here, so the two cannot answer "can a Shipyard be installed at all" differently.
            NodeCapabilityKind? prereq = CapabilityPrerequisites.For(NodeCapabilityKind.Shipyard);
            if (prereq.HasValue && !CapabilityPrerequisites.IsInstalledAnywhere(owned, prereq.Value)) return false;
            return c.Yards == 0 || c.Working == c.Yards;
        }
    }
}
