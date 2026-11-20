using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The antagonist's offensive shape: an implacable faction that from time to time picks a region
    /// of space and enacts a terror campaign on it — raids only, never conquest (operator ruling,
    /// 2026-09-05, overriding FAC-104's lane framing; FAC-268).
    ///
    /// <para><b>Why a campaign objective and not a new concept.</b> <see cref="CampaignFocus"/> is
    /// already the persisted answer to "where is this faction concentrating", and FAC-043/FAC-047
    /// both warn against a second one. A terror campaign is a focus whose centre is the faction's
    /// own <em>staging</em> node and whose objective names a region it does not hold.</para>
    ///
    /// <para><b>Why it carries a fleet requirement.</b> The only combat-ship demand channel scales
    /// with <see cref="FactionState.MilitaryPosture"/>, which only Muster raises, and FCTM never once
    /// chose Muster in two days of logs. <see cref="CampaignObjective.WantedCombatHulls"/> is read by
    /// <c>ShipyardManager</c> on top of the posture target, so the yard builds toward the campaign
    /// before the first raid can enact.</para>
    ///
    /// <para><b>Why the raid path skips the influence gate for it.</b> <c>FactionBrain.AddRaids</c>
    /// only names a target the faction projects <see cref="FactionTuning.RaidReachThreshold"/> of
    /// influence onto. A deep-space faction projects nothing onto a planet, so its −1 standing to
    /// everyone produced no raid candidates at all. Within a terror campaign the objective's region
    /// is reachable by decision; the distance penalty still applies, so a nearer region is still
    /// preferred where the objective allows.</para>
    ///
    /// <para><b>Why a campaign remembers how it went (FAC-271).</b> The first shape had no memory:
    /// a fixed two-hour window, a hull-count requirement that died with it, and nothing about the
    /// outcome reaching the next one. Now the requirement is a <b>DPS budget</b>
    /// (<see cref="Budget"/>) sized from a census of the target region's defenders and from the
    /// opposition the last campaign actually met, times an escalation multiplier; a campaign is
    /// <b>scored at close</b> (<see cref="Maintain"/>) from the faction's own
    /// <see cref="FactionState.Fights"/>; a repelled campaign escalates and comes back to the same
    /// region, a successful one de-escalates, and one left alone calms down with time. Between
    /// campaigns an escalated faction keeps <see cref="FactionState.TerrorStandingDps"/> stocked.
    /// Operator rulings, 2026-09-06, in the ticket.</para>
    /// </summary>
    public static class TerrorCampaigns
    {
        /// <summary>The focus id prefix, so a terror focus is distinguishable from a defensive one
        /// centred on the same node.</summary>
        public const string FocusIdPrefix = "terror:";

        /// <summary>The active <b>terror</b> objective, or null. A raid campaign nominated for an
        /// ordinary motive shares the <see cref="CampaignObjectiveKind.Terrorise"/> kind, so the
        /// antagonist's own campaign is the one whose <see cref="CampaignObjective.Cause"/> says so —
        /// the cadence, escalation and revenge below are all about that one.</summary>
        public static CampaignObjective Active(FactionState s)
        {
            if (s == null) return null;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && o.Kind == CampaignObjectiveKind.Terrorise && o.Cause == CampaignCause.Terror)
                        return o;
            }
            return null;
        }

        /// <summary>Combat hulls every active offensive objective wants, summed.</summary>
        public static int WantedHulls(FactionState s)
        {
            int total = 0;
            if (s == null) return 0;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && OffensiveCampaigns.IsOffensive(o.Kind) && o.WantedCombatHulls > 0)
                        total += o.WantedCombatHulls;
            }
            return total;
        }

        /// <summary>Combat DPS the yard should keep stocked for the offence: the active offensive
        /// objectives' budgets while any campaign stands, else the standing requirement an escalated
        /// antagonist carries between campaigns (<see cref="FactionState.TerrorStandingDps"/>). Every
        /// offensive objective carries a budget, so a faction running a conquest builds toward it the
        /// same way the antagonist builds toward a terror campaign.</summary>
        public static long WantedDps(FactionState s)
        {
            if (s == null) return 0;
            long total = 0;
            bool any = false;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && OffensiveCampaigns.IsOffensive(o.Kind))
                    {
                        any = true;
                        if (o.WantedCombatDps > 0) total += o.WantedCombatDps;
                    }
            }
            return any ? total : Math.Max(0, s.TerrorStandingDps);
        }

        /// <summary>
        /// The defender census of <paramref name="region"/>: the strongest defence any one node there,
        /// owned by a faction other than <paramref name="ourTag"/>, can put up — its parked hulls the
        /// defence can field (<see cref="CombatStrength.FieldableParkedStock"/>, the same census a
        /// raid's commit gate prices) plus the station's own weapon bands. What a raid into the region
        /// can expect to be met by, read from the world rather than assumed: stations here are
        /// typically unarmed, so the fleets parked beside them are the yardstick, not the walls.
        ///
        /// <para><b>The strongest node, not the sum (FAC-467).</b> A raid lands on one node and meets
        /// that node's defence; help from elsewhere is what <see cref="FactionState.TerrorMetDps"/>
        /// learns. Summed over a region holding another antagonist's depots, the census read 42.7M
        /// for a campaign that fielded three hulls, and the budget built from it was beyond any squadron.</para>
        /// 0 without a catalogue for the ship half; the station half needs none.
        /// </summary>
        public static long RegionCensusDps(IWorldView world, IPrefabCatalogue catalogue, NodeRegion region, string ourTag,
            FactionTuning tuning)
        {
            if (world == null || region.IsNone) return 0;
            long strongest = 0;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n == null || n.OwnerFactionTag == null || n.OwnerFactionTag == ourTag) continue;
                if (!region.Contains(n)) continue;
                long here = 0;
                if (n.StationWeapons != null)
                    for (int i = 0; i < n.StationWeapons.Count; i++) here += (long)n.StationWeapons[i].Dps;
                if (catalogue != null)
                    here += (long)ShipPool.TotalDps(CombatStrength.ParkedDefenceBands(n, catalogue, tuning));
                if (here > strongest) strongest = here;
            }
            return strongest;
        }

        /// <summary>
        /// A campaign's DPS budget: the larger of the census (× <see cref="FactionTuning.TerrorBudgetRatio"/>)
        /// and the opposition the last campaign actually met (<see cref="FactionState.TerrorMetDps"/>),
        /// times <c>1 + TerrorEscalationStep × TerrorEscalation</c>. The census is what the faction
        /// can see; the met figure is what it learned — a Guard that answered from outside the region
        /// is in the second and not the first.
        /// </summary>
        public static long Budget(FactionState s, FactionTuning t, long censusDps)
        {
            if (s == null || t == null) return 0;
            double ratio = t.TerrorBudgetRatio > 0 ? t.TerrorBudgetRatio : 1.0;
            double baseDps = Math.Max(Math.Max(0, censusDps) * ratio, Math.Max(0, s.TerrorMetDps));
            double multiplier = 1.0 + Math.Max(0, t.TerrorEscalationStep) * Math.Max(0, s.TerrorEscalation);
            return (long)Math.Round(baseDps * multiplier);
        }

        /// <summary>Whether raiding <paramref name="node"/> advances <paramref name="objective"/>:
        /// the node is not ours and either <b>is</b> the node the objective names
        /// (<see cref="CampaignObjective.TargetNodeId"/>) or stands in the region it names.
        /// <para>The node-scoped form is what a campaign against a target in open deep space takes:
        /// a node belonging to no body and no authored feature stands in no
        /// <see cref="NodeRegion"/> at all, and naming it directly is the only way to name it.</para></summary>
        public static bool Advances(CampaignObjective objective, SpatialNode node, string factionTag)
        {
            if (objective == null || node == null || objective.Kind != CampaignObjectiveKind.Terrorise) return false;
            if (node.OwnerFactionTag == factionTag) return false;
            if (!string.IsNullOrEmpty(objective.TargetNodeId))
                return string.Equals(objective.TargetNodeId, node.Id, StringComparison.Ordinal);
            NodeRegion region = NodeRegion.FromObjective(objective);
            return !region.IsNone && region.Contains(node);
        }

        /// <summary>Quality boost on a Raid against <paramref name="node"/> from the faction's active
        /// terror objectives. A weighting, never a filter — the same rule
        /// <see cref="SeizeObjectives.QualityBoost"/> follows.</summary>
        public static double QualityBoost(FactionState state, SpatialNode node, double boostPerObjective)
        {
            if (state == null || node == null || boostPerObjective <= 0) return 0;
            double total = 0;
            foreach (CampaignFocus f in state.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (Advances(o, node, state.Tag)) total += boostPerObjective;
            }
            return total;
        }

        /// <summary>Whether a hostile-owned node is a legitimate raid target for <paramref name="s"/>:
        /// somebody else's, not neutral, standing below the hostile threshold, no pact, no truce, no
        /// open peace offer — the same gates <c>FactionBrain.AddRaids</c> applies.</summary>
        public static bool IsRaidable(FactionState s, SpatialNode n, FactionTuning t, DateTime nowUtc)
        {
            if (s == null || n == null) return false;
            string owner = n.OwnerFactionTag;
            if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) return false;
            if (s.RelationTo(owner) >= t.HostileRelationThreshold) return false;
            if (Diplomacy.InPactWith(s, owner)) return false;
            if (Diplomacy.UnderTruce(s, owner, nowUtc)) return false;
            if (s.Proposals.ContainsKey(owner)) return false;
            return true;
        }

        /// <summary>A candidate region for the next campaign.</summary>
        public struct Target
        {
            public NodeRegion Region;
            public string StagingNodeId;
            public int Nodes;
            public double Score;
            public bool Found { get { return StagingNodeId != null; } }
        }

        /// <summary>
        /// Pick the next region to terrorise: the richest region (by summed node worth) holding at
        /// least <see cref="FactionTuning.TerrorCampaignMinRegionNodes"/> raidable nodes, other than
        /// the one targeted last time — so campaigns rotate rather than grind one place. Staging is
        /// the faction's own node nearest the region. Deterministic: same world, same answer, which
        /// is what makes it testable and what the log line can be checked against.
        /// </summary>
        public static Target ChooseTarget(FactionState s, IWorldView world, FactionTuning t)
        {
            var result = new Target();
            if (s == null || world == null || t == null) return result;

            var owned = new List<SpatialNode>();
            var byRegion = new Dictionary<NodeRegion, List<SpatialNode>>();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag == s.Tag) { owned.Add(n); continue; }
                if (!IsRaidable(s, n, t, world.UtcNow)) continue;
                NodeRegion region = NodeRegion.Of(n);
                if (region.IsNone) continue;
                List<SpatialNode> list;
                if (!byRegion.TryGetValue(region, out list)) byRegion[region] = list = new List<SpatialNode>();
                list.Add(n);
            }
            if (owned.Count == 0) return result;

            NodeRegion bestRegion = NodeRegion.None;
            List<SpatialNode> bestNodes = null;
            double bestScore = 0;
            // Revenge (FAC-271, ruling 2): a repelled campaign goes back to the same region rather
            // than rotating away — if that region still holds enough raidable nodes. Otherwise the
            // ordinary rotation below applies and the flag lapses with the next start.
            bool revenge = false;
            if (s.TerrorRevenge && s.LastTerrorRegionKey != null)
            {
                foreach (KeyValuePair<NodeRegion, List<SpatialNode>> kv in byRegion)
                {
                    if (kv.Value.Count < t.TerrorCampaignMinRegionNodes) continue;
                    if (!string.Equals(kv.Key.ToKey(), s.LastTerrorRegionKey, StringComparison.Ordinal)) continue;
                    bestRegion = kv.Key;
                    bestNodes = kv.Value;
                    foreach (SpatialNode n in kv.Value) bestScore += 0.4 + 0.6 * n.ResourceValue;
                    revenge = true;
                    break;
                }
            }
            foreach (KeyValuePair<NodeRegion, List<SpatialNode>> kv in byRegion)
            {
                if (revenge) break;
                if (kv.Value.Count < t.TerrorCampaignMinRegionNodes) continue;
                string key = kv.Key.ToKey();
                if (s.LastTerrorRegionKey != null && string.Equals(key, s.LastTerrorRegionKey, StringComparison.Ordinal)
                    && byRegion.Count > 1) continue;
                double score = 0;
                foreach (SpatialNode n in kv.Value) score += 0.4 + 0.6 * n.ResourceValue;
                // Deterministic tie-break on the key so two equal regions do not alternate by
                // dictionary order.
                if (score > bestScore || (score == bestScore && bestNodes != null
                    && string.CompareOrdinal(key, bestRegion.ToKey()) < 0))
                {
                    bestScore = score;
                    bestRegion = kv.Key;
                    bestNodes = kv.Value;
                }
            }
            if (bestNodes == null) return result;

            string staging = null;
            double stagingDist = double.MaxValue;
            foreach (SpatialNode mine in owned)
                foreach (SpatialNode theirs in bestNodes)
                {
                    double d = mine.Position.DistanceTo(theirs.Position);
                    if (d < stagingDist || (d == stagingDist && string.CompareOrdinal(mine.Id, staging) < 0))
                    {
                        stagingDist = d;
                        staging = mine.Id;
                    }
                }

            result.Region = bestRegion;
            result.StagingNodeId = staging;
            result.Nodes = bestNodes.Count;
            result.Score = bestScore;
            return result;
        }

        /// <summary>Whether the cadence allows a new campaign: none active, and the cooldown since
        /// the last one has passed.</summary>
        public static bool CanStart(FactionState s, FactionTuning t, DateTime nowUtc)
        {
            if (s == null || t == null) return false;
            if (Active(s) != null) return false;
            if (s.LastTerrorCampaignUtc != DateTime.MinValue && nowUtc - s.LastTerrorCampaignUtc < t.TerrorCampaignCooldown) return false;
            return true;
        }

        /// <summary>
        /// Start a terror campaign: a focus centred on the staging node (its only region member, so
        /// the defensive readers that bias Reinforce/Patrol toward region members see nothing
        /// foreign), carrying one <see cref="CampaignObjectiveKind.Terrorise"/> objective. Does not
        /// count against the defensive focus capacity — it is not a defence — and refuses while one
        /// is already active. Stamps the cadence clock and the region key, sizes the DPS budget from
        /// the region's defender census (<paramref name="world"/> and <paramref name="catalogue"/>
        /// null = no census, budget from escalation and the last opposition met alone), and spends
        /// the revenge flag — it bought this target.
        /// </summary>
        public static bool TryStart(FactionState s, FactionTuning t, DateTime nowUtc, string stagingNodeId, NodeRegion region,
            IWorldView world = null, IPrefabCatalogue catalogue = null)
        {
            if (s == null || t == null || string.IsNullOrEmpty(stagingNodeId) || region.IsNone) return false;
            if (Active(s) != null) return false;

            long census = RegionCensusDps(world, catalogue, region, s.Tag, t);
            var objective = new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise,
                Cause = CampaignCause.Terror,
                WantedCombatHulls = Math.Max(0, t.TerrorCampaignHulls),
                WantedCombatDps = Budget(s, t, census),
            };
            region.ApplyTo(objective);

            var focus = new CampaignFocus
            {
                Id = FocusIdPrefix + region.ToKey(),
                CenterNodeId = stagingNodeId,
                Priority = s.Drives[Drive.Rivalry],
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc + t.TerrorCampaignDuration,
            };
            focus.RegionMembers.Add(stagingNodeId);
            focus.Objectives.Add(objective);
            s.CampaignFoci.Add(focus);
            s.LastTerrorCampaignUtc = nowUtc;
            s.LastTerrorRegionKey = region.ToKey();
            s.TerrorRevenge = false;
            CampaignFocusLifecycle.RefreshCompatibilityView(s);
            return true;
        }

        /// <summary>The antagonist's view of the one offensive close — see
        /// <see cref="OffensiveCampaigns.Maintain"/>, which owns it. Kept as the name the manager and
        /// the terror tests call so there is a single code path and not a second one beside it: idle
        /// escalation decay, then every offensive focus that has expired, lost its staging node or
        /// (for a conquest) taken its ground, scored and removed.</summary>
        public static int Maintain(FactionState s, FactionTuning t, DateTime nowUtc, Func<string, bool> ownsCenter,
            IWorldView world, IPrefabCatalogue catalogue, IList<string> log)
        {
            return OffensiveCampaigns.Maintain(s, t, nowUtc, ownsCenter, world, catalogue, log);
        }
    }
}
