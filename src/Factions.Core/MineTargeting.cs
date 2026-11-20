using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Composition and richness math for the volume-targeted Mine dwell mechanic. Shared by the
    /// automatic (demand-driven) and player-triggered Mine dispatch paths in
    /// <see cref="OperationsManager"/> so both use exactly one formula. See docs/managers.md's Mine
    /// section for the full model.
    /// </summary>
    public static class MineTargeting
    {
        /// <summary>Per-drill-block contribution to a prefab's <see cref="IPrefabCatalogue.DrillYieldScore"/>:
        /// the volume of one cut, <c>CutOutRadius^3</c>, in m^3. SE removes a SPHERE of that radius on
        /// every cut (<c>MyDrillCutOut</c>, <c>MyDrillBase.Drill</c>), so what a drill clears scales
        /// with the cube of its radius; the <c>(4/3)*pi</c> the sphere formula also carries lives in
        /// <see cref="FactionTuning.MineDrillLitersPerScorePoint"/> beside the cut cadence, keeping
        /// this function a pure geometric term.
        ///
        /// <para>This was <c>CutOutRadius^2</c> before FAC-228, on the reasoning that a moving drill
        /// sweeps a cylinder and so clears by cross-section. SE does not model it that way: the cut
        /// is a sphere at a fixed cadence regardless of motion. The squared form also compressed the
        /// range between hulls -- it rated a large drill 2.14x a small one where SE's geometry says
        /// 3.12x, underrating big drills by 1.46x.</para>
        ///
        /// <para><c>MyShipDrillDefinition.Speed</c> is deliberately not a factor: it drives the drill
        /// head's visual spin animation (decompiled SE <c>MyShipDrill.Render.UpdateSpeed</c>) and
        /// carries no throughput meaning.</para></summary>
        public static double DrillBlockScore(double cutOutRadius)
        {
            if (cutOutRadius <= 0) return 0;
            return cutOutRadius * cutOutRadius * cutOutRadius;
        }

        /// <summary>Resolves which ores a Mine dwell will actually target and in what ratio.
        /// <paramref name="requestedWeights"/> (SubTypeId -&gt; a percentage/weight, not necessarily
        /// normalized) drives the ACTUAL OUTPUT composition when given -- it is not merely a
        /// selection filter over the node's natural mix. Ores the node doesn't yield at all are
        /// dropped from the request; the surviving subset is renormalized to sum to 1.0.
        /// <paramref name="requestedWeights"/> null/empty falls back to the node's own natural
        /// composition (today's full-mix default), computed from each entry's true relative
        /// abundance -- <c>ItemYieldFor(kind) * FreeMintScaleFor(node, kind) * entry.Weight</c> --
        /// which correctly weighs an Ore entry against a RareOre entry on a mixed node (their
        /// <see cref="NodeYieldEntry.Weight"/> columns are independently normalized per ItemKind, so a
        /// naive pure-Weight renormalization across kinds would misrepresent their relative
        /// abundance). Returns an empty dictionary if nothing qualifies (node yields nothing, or the
        /// entire requested set was rejected) -- callers decide how to handle that (reject vs. fall
        /// back).</summary>
        public static Dictionary<string, double> ComputeTargetWeights(SpatialNode node, FactionTuning tuning,
            Dictionary<string, double> requestedWeights, IItemRegistry registry = null)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (node == null || tuning == null) return result;
            NodeYieldEntry[] table = NodeYieldResolver.EntriesFor(node, tuning, registry);
            if (table.Length == 0) return result;

            if (requestedWeights != null && requestedWeights.Count > 0)
            {
                var yieldable = new HashSet<string>(StringComparer.Ordinal);
                foreach (NodeYieldEntry e in table)
                {
                    ItemKind k;
                    if (!Enum.TryParse(e.ItemKind, out k)) continue;
                    if ((k == ItemKind.Ore || k == ItemKind.RareOre) && e.Weight > 0)
                        yieldable.Add(e.SubTypeId);
                }

                double requestedSum = 0;
                foreach (var kv in requestedWeights)
                {
                    if (kv.Value <= 0 || !yieldable.Contains(kv.Key)) continue;
                    result[kv.Key] = kv.Value;
                    requestedSum += kv.Value;
                }
                if (requestedSum <= 0) { result.Clear(); return result; }
                var keys = new List<string>(result.Keys);
                foreach (string k in keys) result[k] /= requestedSum;
                return result;
            }

            double naturalSum = 0;
            foreach (NodeYieldEntry e in table)
            {
                ItemKind k;
                if (!Enum.TryParse(e.ItemKind, out k)) continue;
                if (k != ItemKind.Ore && k != ItemKind.RareOre) continue;
                if (e.Weight <= 0) continue;
                double magnitude = tuning.ItemYieldFor(k) * NodeContent.FreeMintScaleFor(node, k, tuning) * e.Weight;
                if (magnitude <= 0) continue;
                double cur;
                result.TryGetValue(e.SubTypeId, out cur);
                result[e.SubTypeId] = cur + magnitude;
                naturalSum += magnitude;
            }
            if (naturalSum <= 0) { result.Clear(); return result; }
            var natKeys = new List<string>(result.Keys);
            foreach (string k in natKeys) result[k] /= naturalSum;
            return result;
        }

        /// <summary>The composition-weighted average of each targeted ore's own
        /// <see cref="NodeContent.FreeMintScaleFor"/> -- how well this node serves the specific basket
        /// being asked for, before the node's own Richness/HoldStrength/control factors apply.
        ///
        /// <para><b>What discriminates is the ground, not a scale.</b> The mint scale is one global
        /// row since FAC-102, so it separates ItemKinds and not nodes. The separation comes from
        /// <see cref="TryGetEntryItemKind"/> keying on each ore's OWN entry in this node's yield
        /// table -- which the deposit supplies. An ore the node does not hold contributes nothing, so
        /// a node whose ground is all ice scores zero for an iron basket rather than inheriting a
        /// generous Ore scale it cannot spend.</para>
        ///
        /// <para>Shared by the dwell-time factor below and the dispatch-time source selection in
        /// <see cref="OperationsManager"/>, so both rank a candidate by the same number.</para></summary>
        public static double WeightedYieldScale(SpatialNode node, FactionTuning tuning,
            Dictionary<string, double> targetWeights, IItemRegistry registry = null)
        {
            if (node == null || tuning == null || targetWeights == null || targetWeights.Count == 0) return 0;
            NodeYieldEntry[] table = NodeYieldResolver.EntriesFor(node, tuning, registry);
            if (table.Length == 0) return 0;

            double weightedScale = 0;
            foreach (var kv in targetWeights)
            {
                ItemKind kind;
                if (!TryGetEntryItemKind(table, kv.Key, out kind)) continue;
                weightedScale += kv.Value * NodeContent.FreeMintScaleFor(node, kind, tuning);
            }
            return weightedScale;
        }

        /// <summary>Composition-weighted litres of ore per cubic metre of rock for what this dwell is
        /// targeting -- SE's <c>MinedOreRatio</c>, averaged over <paramref name="targetWeights"/>.
        /// This is the extraction rate's composition term (FAC-228); <see cref="WeightedYieldScale"/>
        /// remains the SELECTION term, and the two are deliberately different questions: how fast the
        /// rock gives up ore, versus which deposit is worth sending a hull to.
        ///
        /// <para>Returns 0 when the node yields none of the requested ores, which is what makes
        /// <see cref="NodeRichnessFactor"/> refuse an impossible dwell -- the same eligibility
        /// semantics the mint-scale term used to carry here, kept deliberately. An ore the node DOES
        /// hold never contributes 0, because <see cref="FactionTuning.OreDensityRatioFor"/> falls back
        /// to a positive default.</para></summary>
        public static double WeightedOreDensity(SpatialNode node, FactionTuning tuning,
            Dictionary<string, double> targetWeights, IItemRegistry registry = null)
        {
            if (node == null || tuning == null || targetWeights == null || targetWeights.Count == 0) return 0;
            NodeYieldEntry[] table = NodeYieldResolver.EntriesFor(node, tuning, registry);
            if (table.Length == 0) return 0;

            double weighted = 0;
            foreach (var kv in targetWeights)
            {
                ItemKind kind;
                if (!TryGetEntryItemKind(table, kv.Key, out kind)) continue;   // node does not hold it
                weighted += kv.Value * OreDensityAt(node, tuning, kv.Key);
            }
            return weighted;
        }

        /// <summary>Litres of <paramref name="invKey"/> per cubic metre of this node's rock: what the
        /// survey measured for the materials it is actually in, else the
        /// <see cref="FactionTuning.OreDensityRatio"/> table.
        /// <para>The measurement wins because the table is per ore and the world is per material.
        /// A BetterStone mineral gives up as much as 8.8 where the table's default says 1, and
        /// Agni's silver comes out of black sand at 0.002 where a silver vein gives 1.</para></summary>
        public static double OreDensityAt(SpatialNode node, FactionTuning tuning, string invKey)
        {
            double measured;
            if (node != null && node.Deposit != null
                && node.Deposit.TryYieldRatioOf(WorldModel.StripInvKeyPrefix(invKey), out measured)
                && measured > 0)
                return measured;
            return tuning.OreDensityRatioFor(invKey);
        }

        /// <summary>How well <paramref name="node"/> would serve <paramref name="requestedWeights"/>,
        /// for ranking candidate deposits at dispatch time. Zero means "cannot serve this basket at
        /// all" -- either the node yields none of the requested ores, or every one it does yield has
        /// a zero <see cref="NodeContent.FreeMintScaleFor"/>, which would leave
        /// <c>ComputeMineDwellSeconds</c> with a zero extraction rate and fail the dispatch anyway.
        ///
        /// <para>Deliberately <see cref="NodeRichnessFactor"/> MINUS its influence-control term.
        /// <c>InfluenceControl</c> is a per-pair O(sources) query, and selection evaluates every
        /// candidate in the search radius rather than the one node that won -- see the performance
        /// rule in CLAUDE.md. Control still applies in full where it decides the real dwell.</para>
        ///
        /// <para><paramref name="requestedWeights"/> null/empty scores the node's own natural mix,
        /// matching what an unweighted dispatch would actually mine.</para></summary>
        public static double SelectionScore(SpatialNode node, FactionTuning tuning,
            Dictionary<string, double> requestedWeights, IItemRegistry registry = null)
        {
            if (node == null || tuning == null) return 0;
            double weightedScale = WeightedYieldScale(node, tuning,
                ComputeTargetWeights(node, tuning, requestedWeights, registry), registry);
            if (weightedScale <= 0) return 0;

            return MathUtil.Clamp01(node.Richness) * MathUtil.Clamp01(node.HoldStrength) * weightedScale;
        }

        /// <summary>Whether <paramref name="node"/> is one of the deposits a Mine dispatch anchored at
        /// <paramref name="anchorPos"/> could ever choose: mineable, and inside
        /// <see cref="FactionTuning.MineSearchRadiusKm"/> (a radius of 0 or less means unbounded).
        /// Membership only — cooldown and yield decide eligibility separately, and both are
        /// conditions a hull outlives.</summary>
        public static bool IsInCatchment(SpatialNode node, Vec3 anchorPos, FactionTuning tuning)
        {
            if (node == null || tuning == null) return false;
            if (!NodeContent.IsMineable(node)) return false;
            return tuning.MineSearchRadiusKm <= 0
                || node.Position.DistanceTo(anchorPos) <= tuning.MineSearchRadiusKm;
        }

        /// <summary>The deposit in <paramref name="incumbent"/>'s catchment that is hardest to lift
        /// off — the gravity a hull built for this dispatch actually has to clear, since Mine sends
        /// the same hull to a rotating set of deposits over its life rather than to one fixed target.
        ///
        /// <para><paramref name="incumbent"/> — the deposit this dispatch chose — is returned unless
        /// some other catchment member has <i>strictly</i> greater gravity. So where gravity does not
        /// differentiate the candidates at all (a wholly deep-space estate, every sample 0) this
        /// reduces exactly to the chosen-source rule it replaced, and the answer only moves when
        /// there is a real thrust reason for it to.</para>
        ///
        /// <para>Ranked over the whole catchment (<see cref="IsInCatchment"/>), not over the subset
        /// eligible right now: a deposit on cooldown or scoring zero for today's basket is still a
        /// place this hull gets sent later. Sizing against only today's winner is what let a hull
        /// with a 0.785 g ceiling be built for a catchment reaching 1.008 g.</para>
        ///
        /// <para>Gravity is sampled from position, never from <see cref="SpatialNode.Zone"/> — a node
        /// inside an authored ring feature carries that feature's label whatever its real gravity.
        /// Ties among challengers break on node id, so a catchment with several equally-hard deposits
        /// names the same one every cycle instead of whichever the graph happened to yield first.</para></summary>
        public static SpatialNode HardestGravityDeposit(IEnumerable<SpatialNode> ownedNodes, Vec3 anchorPos,
            IPlanetCatalogue planets, FactionTuning tuning, SpatialNode incumbent)
        {
            if (incumbent == null || ownedNodes == null || planets == null || tuning == null) return incumbent;
            double hardestG;
            SpatialNode hardest = HardestInCatchment(ownedNodes, anchorPos, planets, tuning, out hardestG);
            return ChallengeIncumbent(hardest, hardestG, incumbent, planets);
        }

        /// <summary>The first half of <see cref="HardestGravityDeposit"/>, which does not depend on
        /// the incumbent: the catchment member with the greatest gravity, the lowest node id among
        /// equals, and that gravity. Null when the catchment is empty. A caller asking for many
        /// incumbents around one anchor computes it once.</summary>
        public static SpatialNode HardestInCatchment(IEnumerable<SpatialNode> ownedNodes, Vec3 anchorPos,
            IPlanetCatalogue planets, FactionTuning tuning, out double hardestG)
        {
            hardestG = double.NegativeInfinity;
            SpatialNode hardest = null;
            if (ownedNodes == null || planets == null || tuning == null) return null;
            foreach (SpatialNode n in ownedNodes)
            {
                if (!IsInCatchment(n, anchorPos, tuning)) continue;
                double g = planets.GravityAt(n.Position);
                if (double.IsNaN(g)) continue;
                if (hardest == null || g > hardestG
                    || (g == hardestG && string.CompareOrdinal(n.Id, hardest.Id) < 0))
                {
                    hardest = n;
                    hardestG = g;
                }
            }
            return hardest;
        }

        /// <summary>The second half of <see cref="HardestGravityDeposit"/>: the catchment's hardest
        /// member only when its gravity is strictly greater than the incumbent's, the incumbent
        /// otherwise.</summary>
        public static SpatialNode ChallengeIncumbent(SpatialNode hardest, double hardestG, SpatialNode incumbent,
            IPlanetCatalogue planets)
        {
            if (incumbent == null || hardest == null || planets == null) return incumbent;
            return hardestG > planets.GravityAt(incumbent.Position) ? hardest : incumbent;
        }

        /// <summary>The node's multiplicative factor on the drill's own physical extraction rate --
        /// NOT a composition-split weight (that's <see cref="ComputeTargetWeights"/>'s job). A richer
        /// node lets the same drill gather a target volume FASTER, not "more ore for the same time."
        ///
        /// <para>Exactly two terms, and both are properties of the rock (FAC-228):
        /// <see cref="SpatialNode.Richness"/>, how much ore is in this ground, and
        /// <see cref="WeightedOreDensity"/>, how much ore a cubic metre of it gives up for what is
        /// being targeted.</para>
        ///
        /// <para><b>Three terms were removed and none of them belonged.</b>
        /// <see cref="FactionTuning.FreeMintScale"/> is the passive-income mint weight, so raising the
        /// ore faucet made every ship physically drill faster, and its <c>RareOre 0.1</c> was a 20x
        /// penalty where SE's own density says 5x -- <see cref="WeightedOreDensity"/> replaces it with
        /// the real number. <see cref="SpatialNode.HoldStrength"/> is a territorial claim, not a
        /// property of rock (it also read 1.0 on every live node measured, so it was inert as well as
        /// wrong). Influence control is <c>mine / (mine + bestRival)</c>, a diplomatic standing, and
        /// it made a faction with a neighbour drill 1.7x slower through identical stone with nothing
        /// a player could observe to explain it. Contested ground is already costly through hold
        /// decay, raids and influence; it does not also need to slow physics.</para>
        ///
        /// <para>Reads <see cref="SpatialNode.Richness"/> rather than
        /// <see cref="SpatialNode.ResourceValue"/>: this is a physical rate, and worth is
        /// price-weighted, so a cheap-ore node used to drill slowly because its product was cheap.
        /// See docs/issues/FAC-219.</para></summary>
        public static double NodeRichnessFactor(SpatialNode node, FactionTuning tuning,
            Dictionary<string, double> targetWeights, IItemRegistry registry = null)
        {
            if (node == null) return 0;
            double density = WeightedOreDensity(node, tuning, targetWeights, registry);
            if (density <= 0) return 0;   // node holds none of what was asked for

            // Richness is yield-weighted and density is the yield, so the rock factor takes the
            // yield back out of richness first; counting it in both would square it.
            double rock = node.Deposit != null ? node.Deposit.RockPerRichness : 1.0;
            return MathUtil.Clamp01(node.Richness) * rock * density;
        }

        private static bool TryGetEntryItemKind(NodeYieldEntry[] table, string subTypeId, out ItemKind kind)
        {
            kind = default(ItemKind);
            foreach (NodeYieldEntry e in table)
            {
                if (!string.Equals(e.SubTypeId, subTypeId, StringComparison.Ordinal)) continue;
                if (Enum.TryParse(e.ItemKind, out kind)) return true;
            }
            return false;
        }
    }

    /// <summary>Parses a player-supplied ore-weight specification like <c>"Iron:50,Nickel:50"</c> into
    /// a normalized SubTypeId -&gt; fraction map. Pure string parsing only -- no node-yield validation
    /// here (that needs a <see cref="SpatialNode"/>, done separately via
    /// <see cref="MineTargeting.ComputeTargetWeights"/> at the call site), so this stays independently
    /// testable and reusable regardless of which node the weights end up applied to.</summary>
    public static class OreWeightSpec
    {
        /// <summary>Attempts to parse <paramref name="spec"/>. Each comma-separated token must be
        /// <c>oreName:percentage</c> with a positive numeric percentage; <paramref name="weights"/> is
        /// keyed by the token's ore name prefixed "Ore:" (matching <see cref="NodeYieldEntry.SubTypeId"/>'s
        /// convention) and normalized to sum to 1.0. Returns false with a human-readable
        /// <paramref name="error"/> on any malformed/non-positive token; a null/empty/whitespace-only
        /// <paramref name="spec"/> is treated as "no weights requested" (returns true, empty map) so
        /// callers can pass an omitted argument straight through without a separate null check.</summary>
        public static bool TryParse(string spec, out Dictionary<string, double> weights, out string error)
        {
            weights = new Dictionary<string, double>(StringComparer.Ordinal);
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) return true;

            double sum = 0;
            foreach (string rawToken in spec.Split(','))
            {
                string token = rawToken.Trim();
                if (token.Length == 0) continue;

                int i = token.IndexOf(':');
                if (i <= 0 || i == token.Length - 1)
                {
                    error = "Malformed ore weight '" + token + "' -- expected <oreName>:<percentage>.";
                    weights = null;
                    return false;
                }

                string oreName = token.Substring(0, i).Trim();
                string pctText = token.Substring(i + 1).Trim();
                double pct;
                if (oreName.Length == 0 || !double.TryParse(pctText, out pct) || pct <= 0)
                {
                    error = "Malformed ore weight '" + token + "' -- expected <oreName>:<positive number>.";
                    weights = null;
                    return false;
                }

                string subTypeId = oreName.IndexOf(':') >= 0 ? oreName : "Ore:" + oreName;
                double cur;
                weights.TryGetValue(subTypeId, out cur);
                weights[subTypeId] = cur + pct;
                sum += pct;
            }

            if (weights.Count == 0)
            {
                error = "No valid ore weights found in '" + spec + "'.";
                weights = null;
                return false;
            }

            var keys = new List<string>(weights.Keys);
            foreach (string k in keys) weights[k] /= sum;
            return true;
        }
    }
}
