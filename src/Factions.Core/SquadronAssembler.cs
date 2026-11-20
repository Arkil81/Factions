using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Assembles a mixed-role Raid/Retaliate/Conquer squadron from a node's unified ship pool
    /// (<see cref="SpatialNode.ShipStock"/>) via <see cref="ShipPool"/>'s tag-based suitability —
    /// eligibility comes from each stocked ship's actual catalogue tags, not from a role bucket, so
    /// any cargo-capable ship in stock (built for any purpose) can crew a raid's cargo slot.
    /// </summary>
    public static class SquadronAssembler
    {
        /// <summary>Picks a squadron and consumes the chosen ships (never dipping into
        /// <see cref="SpatialNode.ListedForSale"/>-reserved stock — see <see cref="ShipPool.Available"/>).
        /// Preferred composition: 1 large-grid combat ship, up to <paramref name="maxFighters"/>
        /// small-grid combat ships, 1 cargo slot. Falls back to 1 combat-suitable ship of any grid
        /// size (e.g. a prefab the catalogue can classify as armed but not cleanly Large/Small) if
        /// none of the combat slots found anything. Returns an empty list whenever nothing available
        /// has any combat power at all — a raid squadron always needs at least one ship that can
        /// actually fight; a pure cargo hauler or other zero-Dps prefab is never pressed into service
        /// on its own.
        /// <paramref name="route"/> null (the default) applies no route gate. When supplied, every
        /// slot additionally requires <see cref="ShipPool.IsRouteFeasible"/> on it — the leg the
        /// assembled fleet flies — so a squadron never gets crewed with a ship that can't survive the
        /// trip to the raid's target.
        /// <para><b>One pool, not origin-first.</b> <paramref name="node"/> (the origin) and
        /// <paramref name="reinforcements"/> are drawn as a single pool: every slot takes the best
        /// hull wherever it is parked, ties going to the source listed first (the origin, then
        /// reinforcements in the order given) and then to the lower prefab id. Filling from the origin
        /// until it was empty spent the hull cap on the origin's weakest hulls while the estate's
        /// strongest sat elsewhere. A hull from a reinforcement joins the squadron at the origin and
        /// departs with it, so it must also clear <paramref name="musterRoute"/> — the leg from its
        /// own node to the origin — when that is supplied.</para>
        /// <para><paramref name="targetDps"/> above zero tops the roster up with the strongest
        /// remaining combat hulls until the squadron's summed Dps clears it or
        /// <paramref name="maxHulls"/> is reached — the offensive counterpart of
        /// <see cref="AssembleDefence"/>'s wave target, and the reason a swarm can be fielded at all
        /// where the fighter cap alone would refuse. At or below zero the roster is the preferred
        /// composition alone.</para>
        /// <para><b>The cargo slot prefers an armed hull.</b> It takes the strongest combat hull with a
        /// hold that brings the squadron's summed cargo to <paramref name="lootLiters"/> — the most
        /// the raid can carry off (see <see cref="WorldModel.LootableLiters"/>; a raid's loot is
        /// capped by the fleet's summed <see cref="PendingFleetAction.CargoCapacityLiters"/>). Only
        /// when no armed hull gets there does it take the biggest hold available, which is where a
        /// non-combat hauler comes in; a slot spent on a 0-Dps hull is a slot the defence does not
        /// have to answer.</para>
        /// <para><c>SourceNodeIds</c> names, per squadron member, the node it was drawn from, so a
        /// refusal can hand each hull back where it came from.
        /// <paramref name="contributingNodeIds"/>, when supplied, collects the reinforcement ids that
        /// gave hulls, for the dispatch line.</para></summary>
        public static (List<string> Squadron, bool MissingLargeCombat, bool MissingCargo, List<string> SourceNodeIds)
            Assemble(SpatialNode node, IPrefabCatalogue catalogue, int maxFighters, ShipPool.RouteEnvironment? route = null,
                     int maxHulls = 0, double targetDps = 0,
                     IReadOnlyList<SpatialNode> reinforcements = null, List<string> contributingNodeIds = null,
                     Func<SpatialNode, ShipPool.RouteEnvironment?> musterRoute = null, double lootLiters = 0)
        {
            var pool = new DrawPool(node, reinforcements, catalogue, route, musterRoute);
            var squadron = new List<string>();
            var sources = new List<string>();
            bool missingLargeCombat = true;
            bool missingCargo = true;

            // Combat slots first: a squadron with no fighting power at all is never assembled, so
            // the cargo slot (below) must not be the thing that makes squadron.Count > 0 on its own.
            Func<string, double> dpsScore = id => TotalDps(catalogue, id);
            if (pool.TakeBest(id => ShipPool.IsCombatSuitable(catalogue, id) && catalogue.GridSize(id) == "Large",
                              dpsScore, squadron, sources))
                missingLargeCombat = false;

            for (int i = 0; i < maxFighters; i++)
                if (!pool.TakeBest(id => ShipPool.IsCombatSuitable(catalogue, id) && catalogue.GridSize(id) == "Small",
                                   dpsScore, squadron, sources))
                    break;

            if (squadron.Count == 0)
            {
                // Guaranteed minimum: any combat-suitable ship regardless of grid classification.
                // Deliberately does NOT fall further back to "any remaining ship" — a pure cargo
                // hauler or other zero-Dps prefab has no business crewing a raid squadron on its
                // own; if nothing available can actually fight, the raid should block, not dispatch
                // a convoy with zero combat power.
                pool.TakeBest(id => ShipPool.IsCombatSuitable(catalogue, id), dpsScore, squadron, sources);
            }

            // Cargo slot: only worth adding once the squadron actually has something that can fight.
            if (squadron.Count > 0)
            {
                long hold = 0;
                for (int i = 0; i < squadron.Count; i++) hold += CargoOf(catalogue, squadron[i]);
                bool took = pool.TakeBest(
                    id => ShipPool.IsCombatSuitable(catalogue, id) && TotalDps(catalogue, id) > 0
                          && ShipPool.IsCargoSuitable(catalogue, id)
                          && hold + CargoOf(catalogue, id) >= lootLiters,
                    dpsScore, squadron, sources)
                    || pool.TakeBest(id => ShipPool.IsCargoSuitable(catalogue, id),
                                     id => CargoOf(catalogue, id), squadron, sources);
                if (took) missingCargo = false;
            }

            // Sized against the defence, not against the roster. The composition above says what a
            // squadron LIKES to look like and knows nothing about what is standing at the target, so
            // it is a floor here rather than the size -- the same relationship posture has to
            // AssembleDefence's wave target. Strongest hull first, because a squadron's effective HP
            // in the duel is its own summed Dps: every hull added buys damage and survivability at
            // once, so there is no reason to draw a weak one while a strong one is in the pool.
            //
            // Reinforcements join THIS squadron rather than flying as convoys of their own. The
            // defensive marshal sends several convoys because the duel merges every defender standing
            // at a node into one force; there is no attacker-side equivalent -- ResolveShipDuel reads
            // one fleet's WeaponBands -- so several raids at one target would duel and die one at a
            // time, which is worse than the single squadron.
            if (targetDps > 0 && squadron.Count > 0 && maxHulls > squadron.Count)
            {
                double dps = 0;
                for (int i = 0; i < squadron.Count; i++) dps += TotalDps(catalogue, squadron[i]);
                while (squadron.Count < maxHulls && dps < targetDps)
                {
                    if (!pool.TakeBest(id => ShipPool.IsCombatSuitable(catalogue, id) && TotalDps(catalogue, id) > 0,
                                       dpsScore, squadron, sources))
                        break;
                    dps += TotalDps(catalogue, squadron[squadron.Count - 1]);
                }
            }

            if (contributingNodeIds != null) pool.CollectContributors(contributingNodeIds);
            return (squadron, missingLargeCombat, missingCargo, sources);
        }

        /// <summary>The stock a raid squadron draws from: the origin first, then each reinforcement,
        /// each with the route gates its hulls must clear. Picks are deterministic — best score, then
        /// the earlier source, then the lower prefab id — whatever order a node's stock dictionary
        /// happens to enumerate in.</summary>
        private sealed class DrawPool
        {
            private readonly List<SpatialNode> _nodes = new List<SpatialNode>();
            private readonly List<List<string>> _prefabs = new List<List<string>>();
            private readonly List<Dictionary<string, int>> _remaining = new List<Dictionary<string, int>>();
            private readonly List<bool> _gave = new List<bool>();
            private readonly Dictionary<string, bool> _feasible = new Dictionary<string, bool>(StringComparer.Ordinal);
            private readonly List<ShipPool.RouteEnvironment?> _muster = new List<ShipPool.RouteEnvironment?>();
            private readonly IPrefabCatalogue _catalogue;
            private readonly ShipPool.RouteEnvironment? _route;

            public DrawPool(SpatialNode origin, IReadOnlyList<SpatialNode> reinforcements, IPrefabCatalogue catalogue,
                ShipPool.RouteEnvironment? route, Func<SpatialNode, ShipPool.RouteEnvironment?> musterRoute)
            {
                _catalogue = catalogue;
                _route = route;
                Add(origin, null);
                if (reinforcements != null)
                    for (int i = 0; i < reinforcements.Count; i++)
                    {
                        SpatialNode from = reinforcements[i];
                        if (from == null || from == origin || _nodes.Contains(from)) continue;
                        Add(from, musterRoute != null ? musterRoute(from) : null);
                    }
            }

            private void Add(SpatialNode n, ShipPool.RouteEnvironment? muster)
            {
                if (n == null) return;
                var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in n.ShipStock)
                {
                    int available = ShipPool.Available(n, kv.Key);
                    if (available > 0) remaining[kv.Key] = available;
                }
                var prefabs = new List<string>(remaining.Keys);
                prefabs.Sort(StringComparer.Ordinal);
                _nodes.Add(n);
                _prefabs.Add(prefabs);
                _remaining.Add(remaining);
                _gave.Add(false);
                _muster.Add(muster);
            }

            private bool Feasible(int source, string prefabId)
            {
                string key = source.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + prefabId;
                bool ok;
                if (_feasible.TryGetValue(key, out ok)) return ok;
                ok = ShipPool.IsRouteFeasible(_catalogue, prefabId, _route)
                     && (source == 0 || ShipPool.IsRouteFeasible(_catalogue, prefabId, _muster[source]));
                _feasible[key] = ok;
                return ok;
            }

            /// <summary>Takes the best eligible hull in the pool into <paramref name="squadron"/>,
            /// recording its node in <paramref name="sources"/>. False when nothing is eligible.</summary>
            public bool TakeBest(Func<string, bool> eligible, Func<string, double> score,
                List<string> squadron, List<string> sources)
            {
                int bestSource = -1;
                string best = null;
                double bestScore = double.NegativeInfinity;
                for (int si = 0; si < _nodes.Count; si++)
                {
                    List<string> prefabs = _prefabs[si];
                    Dictionary<string, int> remaining = _remaining[si];
                    for (int pi = 0; pi < prefabs.Count; pi++)
                    {
                        string id = prefabs[pi];
                        int left;
                        if (!remaining.TryGetValue(id, out left) || left <= 0) continue;
                        if (!eligible(id) || !Feasible(si, id)) continue;
                        double sc = score(id);
                        // Strictly greater: sources and prefabs are walked in tie-break order, so
                        // the first of equals is the one the tie-break names.
                        if (best == null || sc > bestScore) { best = id; bestScore = sc; bestSource = si; }
                    }
                }
                if (best == null) return false;
                Take(_nodes[bestSource], _remaining[bestSource], squadron, best);
                sources.Add(_nodes[bestSource].Id);
                _gave[bestSource] = true;
                return true;
            }

            /// <summary>The reinforcement nodes that gave at least one hull, in source order.</summary>
            public void CollectContributors(List<string> into)
            {
                for (int si = 1; si < _nodes.Count; si++)
                    if (_gave[si]) into.Add(_nodes[si].Id);
            }
        }

        private static long CargoOf(IPrefabCatalogue catalogue, string prefabId)
        {
            return catalogue != null ? catalogue.CargoLiters(prefabId) : 0;
        }

        /// <summary>Draws a defensive squadron — Guard or Patrol — from <paramref name="node"/>'s
        /// pool and consumes it, the way <see cref="Assemble"/> does for a raid but with the shape a
        /// defence wants: a large-grid lead when one is available, then the strongest remaining
        /// hulls, and it <b>stops when the summed DPS clears <paramref name="targetDps"/></b> rather
        /// than filling a fixed roster. At least <paramref name="minHulls"/> are taken when the pool
        /// has them (what posture would have sent), never more than <paramref name="maxHulls"/>, and
        /// with <paramref name="targetDps"/> at or below zero exactly <paramref name="minHulls"/> — a
        /// Patrol has no attack to size against. <paramref name="eligible"/> is the kind's own
        /// suitability rule (<see cref="ShipPool.IsSuitableFor"/> for Guard,
        /// <see cref="ShipPool.IsSuitableForRole"/> for Patrol), route-gated here exactly as a raid
        /// squadron is. Empty when nothing eligible has any combat power.
        /// <para>Provenance: FAC-312 — a Guard was one prefab times a count, so a node holding one
        /// gunship and fourteen fighters could field either, never both.</para></summary>
        public static List<string> AssembleDefence(SpatialNode node, IPrefabCatalogue catalogue,
            Func<string, bool> eligible, ShipPool.RouteEnvironment? route,
            int minHulls, int maxHulls, double targetDps)
        {
            var squadron = new List<string>();
            if (node == null || catalogue == null || eligible == null) return squadron;
            var remaining = new Dictionary<string, int>();
            foreach (var kv in node.ShipStock)
            {
                int available = ShipPool.Available(node, kv.Key);
                if (available > 0) remaining[kv.Key] = available;
            }
            Func<string, bool> fit = id => eligible(id) && TotalDps(catalogue, id) > 0
                                           && ShipPool.IsRouteFeasible(catalogue, id, route);
            if (minHulls < 1) minHulls = 1;
            if (maxHulls < minHulls) maxHulls = minHulls;

            double dps = 0;
            string lead = BestPrefab(remaining, catalogue,
                id => fit(id) && catalogue.GridSize(id) == "Large",
                id => TotalDps(catalogue, id));
            if (lead != null) { Take(node, remaining, squadron, lead); dps += TotalDps(catalogue, lead); }

            while (squadron.Count < maxHulls
                   && (squadron.Count < minHulls || (targetDps > 0 && dps < targetDps)))
            {
                string next = BestPrefab(remaining, catalogue, fit, id => TotalDps(catalogue, id));
                if (next == null) break;
                Take(node, remaining, squadron, next);
                dps += TotalDps(catalogue, next);
            }
            return squadron;
        }

        /// <summary>The bands a squadron brings to a duel: every member's own bands, side by side,
        /// so each range bracket sees the hulls that reach it. Empty for an empty squadron.</summary>
        public static List<WeaponBand> WeaponBandsOf(IPrefabCatalogue catalogue, IReadOnlyList<string> squadron)
        {
            var bands = new List<WeaponBand>();
            if (catalogue == null || squadron == null) return bands;
            // Per distinct prefab, scaled by how many of it fly -- the shape ConvoyWeaponBands has
            // always given a single-prefab convoy, so a stack of one hull reads exactly as before.
            var counts = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < squadron.Count; i++)
            {
                int at = -1;
                for (int j = 0; j < counts.Count; j++) if (counts[j].Key == squadron[i]) { at = j; break; }
                if (at < 0) counts.Add(new KeyValuePair<string, int>(squadron[i], 1));
                else counts[at] = new KeyValuePair<string, int>(counts[at].Key, counts[at].Value + 1);
            }
            for (int i = 0; i < counts.Count; i++)
                bands.AddRange(ShipPool.ConvoyWeaponBands(catalogue, counts[i].Key, counts[i].Value));
            return bands;
        }

        /// <summary>Hands every member of <paramref name="squadron"/> back to <paramref name="node"/>'s
        /// pool — a refusal that drew before it decided.</summary>
        public static void ReturnAll(SpatialNode node, IReadOnlyList<string> squadron)
        {
            if (node == null || squadron == null) return;
            for (int i = 0; i < squadron.Count; i++) ShipPool.Return(node, squadron[i], 1);
        }

        /// <summary>Hands each member of <paramref name="squadron"/> back to the node
        /// <paramref name="sourceNodeIds"/> names for it — a refusal of a squadron drawn from several
        /// nodes. A member whose source is missing or no longer in the graph goes to
        /// <paramref name="fallback"/>, so a hull is never lost by a handback.</summary>
        public static void ReturnToSources(WorldModel world, IReadOnlyList<string> squadron,
            IReadOnlyList<string> sourceNodeIds, SpatialNode fallback)
        {
            if (squadron == null) return;
            for (int i = 0; i < squadron.Count; i++)
            {
                SpatialNode home = null;
                string id = sourceNodeIds != null && i < sourceNodeIds.Count ? sourceNodeIds[i] : null;
                if (id == null || world == null || !world.Graph.Nodes.TryGetValue(id, out home)) home = fallback;
                if (home != null) ShipPool.Return(home, squadron[i], 1);
            }
        }

        /// <summary>The comma-joined composition for a dispatch line.</summary>
        public static string Describe(IReadOnlyList<string> squadron)
        {
            return squadron == null ? "" : string.Join(",", squadron);
        }

        /// <summary>Draws the squadron that costs least to fly, from <paramref name="node"/>'s pool,
        /// and consumes it. <paramref name="costOf"/> is one hull's cost for the whole job (fuel
        /// credits, say); a negative cost is unknown and ranks after every priced hull without being
        /// refused.
        /// <list type="bullet">
        ///   <item><paramref name="targetDps"/> above zero: the cheapest set whose summed DPS meets it,
        ///   at most <paramref name="maxHulls"/> — the cheaper of the cheapest single hull that meets
        ///   it alone, and the cheapest-per-DPS set trimmed of every hull it can spare. When no set
        ///   within <paramref name="maxHulls"/> meets it, the strongest hulls go instead: an
        ///   under-strength squadron is the one that has to be as strong as it can be.</item>
        ///   <item>At or below zero: exactly <paramref name="minHulls"/> hulls, the lowest cost per
        ///   DPS first — the same size as before, as cheap as the pool allows.</item>
        /// </list>
        /// Eligibility and the route gate are <see cref="AssembleDefence"/>'s. Empty when nothing
        /// eligible has any combat power. Ties break on prefab id, so the choice is deterministic.
        /// <para>Provenance: FAC-423 — Patrol took the highest-DPS hulls and burned 80-94% of three
        /// small factions' hydrogen on them.</para></summary>
        public static List<string> AssembleByCost(SpatialNode node, IPrefabCatalogue catalogue,
            Func<string, bool> eligible, ShipPool.RouteEnvironment? route,
            int minHulls, int maxHulls, double targetDps, Func<string, double> costOf)
        {
            var squadron = new List<string>();
            if (node == null || catalogue == null || eligible == null || costOf == null) return squadron;
            if (minHulls < 1) minHulls = 1;
            if (maxHulls < minHulls) maxHulls = minHulls;

            // One entry per hull the pool can give, priced once per prefab.
            var hulls = new List<PricedHull>();
            var pricedByPrefab = new Dictionary<string, PricedHull>(StringComparer.Ordinal);
            foreach (var kv in node.ShipStock)
            {
                int available = ShipPool.Available(node, kv.Key);
                if (available <= 0) continue;
                if (!eligible(kv.Key) || !ShipPool.IsRouteFeasible(catalogue, kv.Key, route)) continue;
                double dps = TotalDps(catalogue, kv.Key);
                if (dps <= 0) continue;
                PricedHull h;
                if (!pricedByPrefab.TryGetValue(kv.Key, out h))
                {
                    double cost = costOf(kv.Key);
                    h = new PricedHull { PrefabId = kv.Key, Dps = dps, Cost = cost >= 0 ? cost : UnknownCost };
                    pricedByPrefab[kv.Key] = h;
                }
                for (int i = 0; i < available; i++) hulls.Add(h);
            }
            if (hulls.Count == 0) return squadron;

            // Cheapest per point of DPS first, the unknown last. Among equals -- every hull flying on
            // free power ties at 0 -- the stronger first.
            hulls.Sort((x, y) =>
            {
                int c = (x.Cost / x.Dps).CompareTo(y.Cost / y.Dps);
                if (c != 0) return c;
                c = y.Dps.CompareTo(x.Dps);
                return c != 0 ? c : string.CompareOrdinal(x.PrefabId, y.PrefabId);
            });

            List<PricedHull> plan;
            if (targetDps <= 0)
            {
                plan = hulls.GetRange(0, Math.Min(minHulls, hulls.Count));
            }
            else
            {
                plan = CheapestMeeting(hulls, targetDps, maxHulls);
                if (plan == null)
                {
                    var strongest = new List<PricedHull>(hulls);
                    strongest.Sort((x, y) =>
                    {
                        int c = y.Dps.CompareTo(x.Dps);
                        return c != 0 ? c : string.CompareOrdinal(x.PrefabId, y.PrefabId);
                    });
                    plan = strongest.GetRange(0, Math.Min(maxHulls, strongest.Count));
                }
            }

            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in node.ShipStock) remaining[kv.Key] = ShipPool.Available(node, kv.Key);
            for (int i = 0; i < plan.Count; i++) Take(node, remaining, squadron, plan[i].PrefabId);
            return squadron;
        }

        /// <summary>A priced hull's cost when its price is unknown: after every priced hull.</summary>
        private const double UnknownCost = 1e18;

        private sealed class PricedHull
        {
            public string PrefabId;
            public double Dps;
            public double Cost;
        }

        /// <summary>The cheaper of two plans that each meet <paramref name="targetDps"/>: the
        /// cheapest single hull strong enough alone, and the cheapest-per-DPS greedy set trimmed of
        /// every hull it can spare. Null when neither meets the target within
        /// <paramref name="maxHulls"/>. <paramref name="byCostPerDps"/> is already sorted.</summary>
        private static List<PricedHull> CheapestMeeting(List<PricedHull> byCostPerDps, double targetDps, int maxHulls)
        {
            PricedHull single = null;
            for (int i = 0; i < byCostPerDps.Count; i++)
            {
                PricedHull h = byCostPerDps[i];
                if (h.Dps < targetDps) continue;
                if (single == null || h.Cost < single.Cost
                    || (h.Cost == single.Cost && string.CompareOrdinal(h.PrefabId, single.PrefabId) < 0))
                    single = h;
            }

            List<PricedHull> greedy = null;
            double dps = 0;
            var set = new List<PricedHull>();
            for (int i = 0; i < byCostPerDps.Count && set.Count < maxHulls; i++)
            {
                set.Add(byCostPerDps[i]);
                dps += byCostPerDps[i].Dps;
                if (dps >= targetDps) { greedy = set; break; }
            }
            if (greedy != null)
            {
                // Drop the dearest hulls the set can do without.
                var byCostDesc = new List<PricedHull>(greedy);
                byCostDesc.Sort((x, y) => y.Cost.CompareTo(x.Cost));
                for (int i = 0; i < byCostDesc.Count && greedy.Count > 1; i++)
                {
                    if (dps - byCostDesc[i].Dps < targetDps) continue;
                    greedy.Remove(byCostDesc[i]);
                    dps -= byCostDesc[i].Dps;
                }
            }

            if (single == null) return greedy;
            if (greedy == null) return new List<PricedHull> { single };
            double greedyCost = 0;
            for (int i = 0; i < greedy.Count; i++) greedyCost += greedy[i].Cost;
            return single.Cost <= greedyCost ? new List<PricedHull> { single } : greedy;
        }

        private static string BestPrefab(Dictionary<string, int> remaining, IPrefabCatalogue catalogue,
            Func<string, bool> eligible, Func<string, double> score)
        {
            string best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var kv in remaining)
            {
                if (kv.Value <= 0 || !eligible(kv.Key)) continue;
                double s = score(kv.Key);
                if (best == null || s > bestScore) { best = kv.Key; bestScore = s; }
            }
            return best;
        }

        private static void Take(SpatialNode node, Dictionary<string, int> remaining, List<string> squadron, string prefabId)
        {
            squadron.Add(prefabId);

            int left;
            remaining.TryGetValue(prefabId, out left);
            left -= 1;
            if (left > 0) remaining[prefabId] = left;
            else remaining.Remove(prefabId);

            int stock;
            node.ShipStock.TryGetValue(prefabId, out stock);
            node.ShipStock[prefabId] = stock > 0 ? stock - 1 : 0;
        }

        private static double TotalDps(IPrefabCatalogue catalogue, string prefabId)
        {
            if (catalogue == null) return 0;
            double sum = 0;
            IReadOnlyList<WeaponBand> bands = catalogue.WeaponBands(prefabId);
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }
    }
}
