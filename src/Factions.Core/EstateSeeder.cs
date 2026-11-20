using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What seeding did, or would do.</summary>
    public sealed class EstateSeedResult
    {
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public int FactionsSeeded;
        public int NodesSeeded;

        /// <summary>Regions asked for more than they hold. Seeding is refused while any remain —
        /// see <see cref="EstateSeeder"/>.</summary>
        public bool IsOverSubscribed;

        /// <summary>
        /// Pairs of would-be nodes closer together than the separation in force where they sit,
        /// measured <b>after</b> <see cref="WorldModel.PlaceNode"/> has moved them. Counted on a dry
        /// run as well as an apply, because it is the number worth knowing before committing.
        /// <para>The lattice is violation-free by construction, so a count taken on the lattice
        /// would always be zero and would mean nothing. Placement is where the guarantee can be
        /// lost: a surface point is projected onto real terrain and an orbital one is snapped to a
        /// band, neither of which preserves the spacing the lattice was built with. Existing graph
        /// nodes are included — a seeded node is not checked against them anywhere else.</para>
        /// </summary>
        public int SeparationViolations;

        /// <summary>Closest pair observed, km, or 0 when fewer than two positions were measured.
        /// Reported next to the count because one 9.9 km pair at a 10 km floor and one 0.2 km pair
        /// are the same count and very different problems.</summary>
        public double MinSpacingKm;

        /// <summary>The separation required at the closest pair, km — what <see cref="MinSpacingKm"/>
        /// should be read against.</summary>
        public double MinSpacingRequiredKm;

        /// <summary>Node ids the recipe asks to start founded, in the order they were placed. Empty
        /// on a dry run — a node has no id until it is committed, so there is nothing to name.
        /// The host founds these; Core cannot, since a station is an SE entity.</summary>
        public readonly List<string> FoundNodeIds = new List<string>();

        /// <summary>Starting hulls, resolved to concrete prefabs and the nodes they park at. Empty
        /// on a dry run for the same reason <see cref="FoundNodeIds"/> is — the selection is still
        /// made and reported in <see cref="Lines"/>, it just has no node id to bind to yet.
        ///
        /// <para>Applied by the host rather than written here, so a grant can be withheld when the
        /// station it belongs to failed to found. A hull parked at an unfounded node is exactly the
        /// stranded stock <c>WorldModel.AuditStrandedShipStock</c> reports.</para></summary>
        public readonly List<ShipGrant> ShipGrants = new List<ShipGrant>();

        /// <summary>Capabilities installed on the seeded estate, in the order they were installed.
        /// A REPORT, not an instruction: unlike a station or a hull these are applied in Core as the
        /// plan is built, because each one changes what the next should choose (see
        /// <see cref="EstateSeeder"/>'s capability notes). Empty on a dry run.</summary>
        public readonly List<CapabilityGrant> CapabilityGrants = new List<CapabilityGrant>();
    }

    /// <summary>One capability install: which node got it, and to what level.</summary>
    public struct CapabilityGrant
    {
        public string NodeId;
        public NodeCapabilityKind Kind;
        public int Level;

        /// <summary>The node's affinity for this capability at the moment it was chosen, for the
        /// report. A capability that landed at 0.5 everywhere was placed by tie-break, not by fit,
        /// which is worth being able to see.</summary>
        public double Affinity;
    }

    /// <summary>One authored hull, resolved: which prefab, at which node.</summary>
    public struct ShipGrant
    {
        public string NodeId;
        public string PrefabId;
        public int Count;

        /// <summary>The authored role this was resolved from, for the report. A prefab id alone
        /// cannot say what the recipe asked for.</summary>
        public string Role;
    }

    /// <summary>
    /// Turns an authored <see cref="FactionRecipe"/> into real nodes.
    ///
    /// <para><b>The bootstrap.</b> Every territorial action a brain can emit closes over nodes the
    /// faction already owns — <c>Expand</c> needs an origin from <c>OwnedBy</c>, <c>Claim</c> reads a
    /// frontier derived from it, <c>Found</c> iterates it. On an empty graph all three are shut, so
    /// `factions init` alone leaves every faction permanently landless. `factions scan` used to
    /// supply the first nodes from SE's economy stations; this replaces it (FAC-105).</para>
    ///
    /// <para><b>Refuses before it places, warns before it refuses.</b> Placement commits an
    /// over-subscribed node rather than failing, so the only useful check happens first. A region
    /// asked for more than <see cref="PlacementCapacity"/> reports is a refusal, and the same
    /// condition is surfaced as a warning at load so an unbuildable recipe is visible without anyone
    /// running the verb.</para>
    /// </summary>
    public static class EstateSeeder
    {
        /// <summary>Map the authored regions onto resolved placement keys. A recipe entry names a
        /// body or a feature; the planner wants one discriminated key.</summary>
        public static List<PlacementAssignment> ToAssignments(FactionRecipe recipe)
        {
            var list = new List<PlacementAssignment>();
            if (recipe == null || recipe.Factions == null) return list;

            foreach (FactionEstate estate in recipe.Factions)
            {
                if (estate == null || string.IsNullOrEmpty(estate.Tag) || estate.Regions == null) continue;
                foreach (EstateRegion region in estate.Regions)
                {
                    if (region == null) continue;
                    bool isFeature = !string.IsNullOrEmpty(region.Feature);
                    list.Add(new PlacementAssignment
                    {
                        Tag = estate.Tag,
                        Region = new PlacementRegion
                        {
                            Kind = isFeature ? PlacementRegionKind.Feature : PlacementRegionKind.PlanetSurface,
                            Key = isFeature ? region.Feature : region.Body,
                        },
                        Nodes = region.Nodes,
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// Seed every authored estate. <paramref name="apply"/> false plans and reports without
        /// touching the world.
        ///
        /// <para>Positions come from one lattice per region, carved per faction by proximity to a
        /// deterministic home — so separation holds <b>across</b> factions sharing a body, not merely
        /// within each one. Two independently sampled estates would have no guarantee between them.</para>
        ///
        /// <para><paramref name="orphanStationRecords"/> is how many station records the host holds
        /// that no node points at. Core cannot count them — a station record is an SE entity and
        /// outlives the graph — so the host measures and Core decides. Required rather than optional:
        /// "the caller could not count" and "the caller counted zero" must not read the same, which
        /// is the same rule the null roster above is refused under.</para>
        /// </summary>
        public static EstateSeedResult Seed(FactionRecipe recipe, WorldModel world, int worldSeed, bool apply,
            Func<PlacementRegion, PlacementRegionGeometry> geometryFor,
            Func<PlacementRegion, double> separationFor,
            ICollection<string> registeredTags,
            int orphanStationRecords)
        {
            var result = new EstateSeedResult();
            List<PlacementAssignment> assignments = ToAssignments(recipe);

            // The roster, before the geometry. A recipe naming a faction this world does not have
            // places real nodes for an owner that cannot act on them, and every later stage succeeds:
            // the planner validates regions, not tags. Found the hard way -- faction_recipe.json was
            // authored against a previous world's factions, and 45 of 65 nodes would have gone to
            // owners that do not exist, reported as a clean apply.
            //
            // A null roster REFUSES rather than skipping. "Caller supplied nothing" and "caller
            // supplied a matching roster" must not produce the same silence.
            if (registeredTags == null)
            {
                result.Warnings.Add("no faction roster supplied -- refusing rather than seeding "
                                  + "territory that may belong to nobody.");
                result.IsOverSubscribed = true;
                return result;
            }

            // A second apply would commit a second copy of every node -- SeedNodeAt does not dedupe,
            // and the placement is deterministic, so the world would silently double. Refuse instead.
            if (apply && world != null)
            {
                var alreadyHeld = new List<string>();
                foreach (PlacementAssignment a in assignments)
                {
                    if (string.IsNullOrEmpty(a.Tag) || alreadyHeld.Contains(a.Tag)) continue;
                    foreach (SpatialNode n in world.Graph.OwnedBy(a.Tag)) { alreadyHeld.Add(a.Tag); break; }
                }
                if (alreadyHeld.Count > 0)
                {
                    alreadyHeld.Sort(StringComparer.Ordinal);
                    result.Warnings.Add(alreadyHeld.Count + " faction(s) already hold territory: "
                        + string.Join(", ", alreadyHeld.ToArray())
                        + ". Seeding again would place a SECOND copy of every node, not reconcile "
                        + "with the first. Clear the estate before re-seeding.");
                    result.IsOverSubscribed = true;
                    return result;
                }
            }

            // Orphan station records from a previous generation. The territory guard above reads the
            // GRAPH, and a station record outlives it: clear the graph and re-roll, and every record
            // the last roll minted is stranded with nothing pointing at it while a fresh generation
            // is minted on top. That is the whole of FAC-129 -- 68 records against 56 live nodes,
            // three grids stacked on one position, and an SE contract issued from a station no node
            // knew about. Refused on an apply, said out loud on a dry run so it is learned before the
            // commit rather than at it.
            //
            // Counted across every faction, not only the recipe's: 'world purge orphans' is
            // all-or-nothing by design and never touches node-backed estate, so there is no reading
            // under which some orphans are worth keeping.
            if (orphanStationRecords > 0)
            {
                result.Warnings.Add(orphanStationRecords + " orphan station record(s) already exist -- "
                    + "stations the host holds that no node points at. Seeding stacks a new generation "
                    + "on the old one and strands these permanently. Run 'factions world purge orphans "
                    + "apply' first; it removes exactly these and leaves live estate alone."
                    + (apply ? "" : " An apply will REFUSE while they remain."));
                if (apply)
                {
                    result.IsOverSubscribed = true;
                    return result;
                }
            }

            var known = new HashSet<string>(registeredTags, StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            foreach (PlacementAssignment a in assignments)
                if (!string.IsNullOrEmpty(a.Tag) && !known.Contains(a.Tag) && !unknown.Contains(a.Tag))
                    unknown.Add(a.Tag);

            if (unknown.Count > 0)
            {
                unknown.Sort(StringComparer.Ordinal);
                result.Warnings.Add("recipe names " + unknown.Count + " faction(s) this world does not "
                    + "have: " + string.Join(", ", unknown.ToArray())
                    + ". Their territory would belong to nobody.");
                result.IsOverSubscribed = true;      // same refusal channel: nothing is placed
                return result;
            }

            // Not a refusal: a registered faction with no authored estate is a legitimate choice --
            // UNKN is a graveyard by design, and FCTM waits on features nobody has authored yet.
            // Said out loud because "deliberately landless" and "forgotten" look identical.
            var landless = new List<string>();
            foreach (string tag in known)
            {
                bool claimed = false;
                foreach (PlacementAssignment a in assignments)
                    if (string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)) { claimed = true; break; }
                if (!claimed) landless.Add(tag);
            }
            if (landless.Count > 0)
            {
                landless.Sort(StringComparer.Ordinal);
                result.Lines.Add("no estate authored for: " + string.Join(", ", landless.ToArray()));
            }

            // How many stations each faction starts with standing. Counted down as nodes commit.
            var foundBudget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (FactionEstate fe in recipe.Factions ?? new List<FactionEstate>())
            {
                if (fe == null || string.IsNullOrEmpty(fe.Tag) || fe.Founded <= 0) continue;
                int owned = 0;
                foreach (PlacementAssignment a in assignments)
                    if (string.Equals(a.Tag, fe.Tag, StringComparison.OrdinalIgnoreCase)) owned += a.Nodes;
                if (fe.Founded > owned)
                    result.Warnings.Add(fe.Tag + ": Founded=" + fe.Founded + " but it holds only "
                        + owned + " node(s); every one will be founded.");
                foundBudget[fe.Tag] = fe.Founded;
            }

            // Ships, checked BEFORE anything commits -- the same discipline the placement plan
            // follows. A ship fault is a recipe fault: it is answerable from the recipe and the
            // catalogue alone, without a single node on the ground, and refusing after the estate
            // is already placed would leave half a world behind for a file that could have been
            // read first.
            if (!ShipsAreSatisfiable(recipe, foundBudget, world, result)) return result;

            // Every projected position, and who asked for it, for the spacing measure after the
            // loop. The labels are the whole point: a bare count cannot be acted on.
            var placed = new List<Vec3>();
            var placedBy = new List<string>();
            // The nodes that will carry a station, in the order they were chosen. Ships hang off
            // this: a hull parked where nothing was founded has no station to belong to.
            var willFound = new List<PlannedStation>();
            // Snapshot the graph BEFORE seeding: on an apply the nodes we are about to commit land in
            // it, and measuring against a live read pairs each position with its own committed twin.
            List<Vec3> preExisting = world != null ? world.ExistingNodePositions() : new List<Vec3>();

            PlacementPlan plan = PlacementPlanner.Plan(assignments, geometryFor, separationFor);
            foreach (string refusal in plan.Refusals) result.Warnings.Add(refusal);
            result.IsOverSubscribed = !plan.IsFeasible;
            if (!plan.IsFeasible) return result;   // never place into a region that cannot hold it

            // Group by region so one lattice serves every claimant on that body.
            var byRegion = new Dictionary<string, List<PlacementAssignment>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (PlacementAssignment a in assignments)
            {
                string key = a.Region.Kind + "|" + (a.Region.Key ?? "");
                List<PlacementAssignment> bucket;
                if (!byRegion.TryGetValue(key, out bucket))
                {
                    bucket = new List<PlacementAssignment>();
                    byRegion[key] = bucket;
                    order.Add(key);
                }
                bucket.Add(a);
            }

            var seededTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in order)
            {
                List<PlacementAssignment> claimants = byRegion[key];
                PlacementRegion region = claimants[0].Region;
                PlacementRegionGeometry geometry = geometryFor(region);
                int demand = 0;
                foreach (PlacementAssignment a in claimants) demand += a.Nodes;

                // One lattice for the region's whole demand. Sized to the demand, not the capacity,
                // so an estate that uses half a body still spreads across all of it.
                List<Vec3> lattice = SampleRegion(region, geometry,
                    RegionSampler.SeedFor(worldSeed, "", region.Key), demand, world);
                if (lattice.Count == 0)
                {
                    result.Warnings.Add(region + ": no sampleable geometry; " + demand + " node(s) not placed.");
                    continue;
                }

                var remaining = new List<Vec3>(lattice);
                // Deterministic claimant order: the recipe's order is an authoring accident.
                claimants.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

                foreach (PlacementAssignment a in claimants)
                {
                    // Each faction's home is its own lattice point, chosen by its own seed, so one
                    // faction's share cannot move because another's count changed.
                    int seed = RegionSampler.SeedFor(worldSeed, a.Tag, region.Key);
                    int homeIndex = (int)(RegionSampler.SeededUnit(seed) * remaining.Count);
                    if (homeIndex >= remaining.Count) homeIndex = remaining.Count - 1;
                    if (homeIndex < 0) homeIndex = 0;

                    List<Vec3> mine = RegionSampler.NearestTo(remaining, remaining[homeIndex], a.Nodes);
                    foreach (Vec3 p in mine)
                    {
                        remaining.RemoveAll(q => q.DistanceSquaredTo(p) < 1e-12);
                        // Projected on a dry run too, so the spacing measured below is the spacing
                        // the world would really get -- not the lattice's, which cannot violate.
                        //
                        // The SAME projected point is what the ship plan is evaluated against. A
                        // lattice point is not where the node ends up: SeedNodeAt commits at
                        // ProjectSeedPosition, which lifts a surface point onto terrain and pulls an
                        // orbital one radially to OrbitGravityMin. Sampling gravity before that ran
                        // parked a hull at 1.04 TWR against a 1.05 floor on the genesis re-seed
                        // (FAC-224) -- the seeder's own filter reading a position the node does not
                        // occupy.
                        Vec3 placedAt = p;
                        if (world != null)
                        {
                            placedAt = world.ProjectSeedPosition(p);
                            placed.Add(placedAt);
                            placedBy.Add(a.Tag + " " + region);
                        }
                        if (world != null)
                        {
                            SpatialNode seeded = apply ? world.SeedNodeAt(a.Tag, p) : null;
                            // Regions are walked in authoring order, so a faction's power base is
                            // founded before its forward presence.
                            //
                            // Chosen on a dry run as well, because the ship plan below hangs off it:
                            // a dry run that could not say which nodes get hulls would be reporting
                            // a different plan from the one an apply commits.
                            int wanted;
                            if ((seeded != null || !apply)
                                && foundBudget.TryGetValue(a.Tag, out wanted) && wanted > 0)
                            {
                                if (seeded != null) result.FoundNodeIds.Add(seeded.Id);
                                willFound.Add(new PlannedStation
                                {
                                    Tag      = a.Tag,
                                    NodeId   = seeded != null ? seeded.Id : null,
                                    // The committed node's own position on an apply, and the
                                    // projection of the same point on a dry run -- SeedNodeAt derives
                                    // one from the other, so the two modes cannot disagree about
                                    // where a hull is being asked to hold altitude.
                                    PositionKm = seeded != null ? seeded.Position : placedAt,
                                });
                                foundBudget[a.Tag] = wanted - 1;
                            }
                        }
                        result.NodesSeeded++;
                    }
                    if (seededTags.Add(a.Tag)) result.FactionsSeeded++;
                    result.Lines.Add(a.Tag + " " + region + " nodes=" + mine.Count
                                     + (mine.Count < a.Nodes ? " (SHORT of " + a.Nodes + ")" : ""));
                    if (mine.Count < a.Nodes)
                        result.Warnings.Add(a.Tag + " " + region + ": asked for " + a.Nodes
                                            + ", lattice yielded " + mine.Count + ".");
                }
            }
            if (world != null && placed.Count > 0)
            {
                double closestOverall;
                IList<ProjectedViolation> bad = world.ProjectedSeparationDetail(placed, preExisting, out closestOverall);
                result.SeparationViolations = bad.Count;

                if (bad.Count == 0)
                {
                    result.MinSpacingKm = closestOverall < double.MaxValue ? closestOverall : 0;
                    result.MinSpacingRequiredKm = 0;
                }
                else
                {
                    ProjectedViolation worst = bad[0];
                    foreach (ProjectedViolation v in bad)
                        if (v.RequiredKm - v.NearestKm > worst.RequiredKm - worst.NearestKm) worst = v;
                    result.MinSpacingKm = worst.NearestKm;
                    result.MinSpacingRequiredKm = worst.RequiredKm;

                    foreach (ProjectedViolation v in bad)
                    {
                        string who = v.Index < placedBy.Count ? placedBy[v.Index] : "node " + v.Index;
                        // Say which fault this is. Inside its feature and too close is a packing
                        // problem; outside it is a placement problem wearing the deep-space floor.
                        string why = v.InsideFeature
                            ? "inside its feature, too close"
                            : "LEFT its region -- now " + v.Zone + ", which is why it wants "
                              + v.RequiredKm.ToString("0.##") + " km";
                        // The position is here for the placement fault specifically: "it left the
                        // region" is only actionable if you can see WHERE it went.
                        result.Warnings.Add(who + ": " + v.NearestKm.ToString("0.##")
                            + " km from its nearest neighbour, needs " + v.RequiredKm.ToString("0.##")
                            + " km (" + why + ") at ("
                            + v.PositionKm.X.ToString("0.###") + ", "
                            + v.PositionKm.Y.ToString("0.###") + ", "
                            + v.PositionKm.Z.ToString("0.###") + ").");
                    }
                }
            }

            PlanShips(recipe, willFound, world, result);
            PlanCapabilities(recipe, willFound, world, apply, result);

            return result;
        }

        /// <summary>The node a station is planned for, before the host has founded it. Carries the
        /// position as well as the id because the id is null on a dry run and the position is what
        /// route feasibility is actually decided against.
        ///
        /// <para><see cref="PositionKm"/> is the <b>placed</b> position, never the lattice point the
        /// recipe's sampler produced. <c>WorldModel.SeedNodeAt</c> commits at
        /// <c>ProjectSeedPosition</c>, and for an orbital node that is a deliberate radial move to
        /// <c>OrbitGravityMin</c> -- so the two points differ by design, and gravity at them differs
        /// with it (FAC-224).</para></summary>
        private struct PlannedStation
        {
            public string Tag;
            public string NodeId;
            public Vec3 PositionKm;
        }

        /// <summary>
        /// Whether every authored <see cref="FactionEstate.Ships"/> entry can be honoured at all —
        /// answered from the recipe and the catalogue, before anything is placed.
        ///
        /// <para>Two faults, both the author's: a faction that asks for hulls but founds nowhere to
        /// park them, and a role no prefab in this world can take. Neither depends on where a node
        /// lands, which is why both are answerable here; whether a <b>particular</b> node can hold a
        /// <b>particular</b> hull is a question about gravity, and <see cref="PlanShips"/> asks it
        /// once positions exist.</para>
        ///
        /// <para>No catalogue means no opinion, not a refusal: a <see cref="WorldModel"/> without one
        /// is every unit test that does not care about ships, and refusing there would make the
        /// recipe unusable in exactly the setting it is easiest to test.</para>
        /// </summary>
        private static bool ShipsAreSatisfiable(FactionRecipe recipe, Dictionary<string, int> foundBudget,
            WorldModel world, EstateSeedResult result)
        {
            IPrefabCatalogue catalogue = world != null ? world.PrefabCatalogue : null;
            bool ok = true;

            foreach (FactionEstate fe in recipe.Factions ?? new List<FactionEstate>())
            {
                if (fe == null || fe.Ships == null || fe.Ships.Count == 0) continue;

                int wanted;
                if (!foundBudget.TryGetValue(fe.Tag ?? "", out wanted) || wanted <= 0)
                {
                    result.Warnings.Add(fe.Tag + " authors ships but Founded=0. A hull parks at a "
                        + "station; with none founded there is nowhere to put it.");
                    ok = false;
                    continue;
                }

                if (catalogue == null) continue;

                foreach (EstateShips s in fe.Ships)
                {
                    if (s == null) continue;
                    IReadOnlyList<string> pool = catalogue.GetNamesForRole(s.Role, "All");
                    if (pool == null || pool.Count == 0)
                    {
                        result.Warnings.Add(fe.Tag + " Ships[" + s.Role + "]: no prefab in this "
                            + "world's catalogue carries that role, so the count cannot be filled.");
                        ok = false;
                    }
                }
            }

            if (catalogue == null && HasAnyShips(recipe))
                result.Lines.Add("no prefab catalogue: ship grants planned as counts only.");

            if (!ok) result.IsOverSubscribed = true;   // same refusal channel: nothing is placed
            return ok;
        }

        private static bool HasAnyShips(FactionRecipe recipe)
        {
            foreach (FactionEstate fe in recipe.Factions ?? new List<FactionEstate>())
                if (fe != null && fe.Ships != null && fe.Ships.Count > 0) return true;
            return false;
        }

        /// <summary>
        /// Resolve each authored hull to a concrete prefab and a founded node.
        ///
        /// <para><b>Route feasibility is a filter, not a preference.</b> A hull that cannot hold
        /// altitude where it is parked never leaves, occupies the node's stock forever, and
        /// re-blocks the dispatch that wanted it — the stranded-stock failure
        /// <c>ShipPool.IsRouteFeasible</c> exists to prevent. Authoring one into a genesis world is
        /// the same defect, committed deliberately, so a role with no feasible hull <b>at that
        /// node</b> is skipped with a warning naming the node rather than filled with the nearest
        /// thing.</para>
        ///
        /// <para>Hulls round-robin across the faction's founded nodes: its power base is founded
        /// first (regions are walked in authoring order), so a fleet spread this way is weighted
        /// toward the power base without any rule saying so.</para>
        /// </summary>
        private static void PlanShips(FactionRecipe recipe, List<PlannedStation> willFound,
            WorldModel world, EstateSeedResult result)
        {
            if (world == null || world.PrefabCatalogue == null || !HasAnyShips(recipe)) return;

            IPrefabCatalogue catalogue = world.PrefabCatalogue;
            double minDryTwr = world.Tuning != null ? world.Tuning.MinDryTwr : 0;

            foreach (FactionEstate fe in recipe.Factions ?? new List<FactionEstate>())
            {
                if (fe == null || fe.Ships == null || fe.Ships.Count == 0) continue;

                var stations = new List<PlannedStation>();
                foreach (PlannedStation ps in willFound)
                    if (string.Equals(ps.Tag, fe.Tag, StringComparison.OrdinalIgnoreCase)) stations.Add(ps);
                if (stations.Count == 0)
                {
                    result.Warnings.Add(fe.Tag + ": no station was placed, so its "
                        + CountShips(fe) + " authored hull(s) have nowhere to park.");
                    continue;
                }

                int granted = 0, skipped = 0, cursor = 0;
                foreach (EstateShips s in fe.Ships)
                {
                    if (s == null) continue;
                    IReadOnlyList<string> pool = catalogue.GetNamesForRole(s.Role, "All");

                    for (int i = 0; i < s.Count; i++)
                    {
                        PlannedStation at = stations[cursor % stations.Count];
                        cursor++;

                        // Origin and destination are the same point: the question is whether the
                        // hull can hold itself up where it is parked, not whether it can fly a leg.
                        ShipPool.RouteEnvironment parked = ShipPool.RouteEnvironment.From(
                            world.Planets, at.PositionKm, at.PositionKm, minDryTwr);

                        string chosen = PickHull(pool, catalogue, parked);
                        if (chosen == null)
                        {
                            result.Warnings.Add(fe.Tag + " Ships[" + s.Role + "]: no hull can hold "
                                + "altitude at " + (at.NodeId ?? "a planned node") + " ("
                                + world.Planets.GravityAt(at.PositionKm).ToString("0.###")
                                + " g); 1 hull not granted rather than stranded there.");
                            skipped++;
                            continue;
                        }

                        result.ShipGrants.Add(new ShipGrant
                        {
                            NodeId = at.NodeId, PrefabId = chosen, Count = 1, Role = s.Role,
                        });
                        granted++;
                    }
                }

                result.Lines.Add(fe.Tag + " ships=" + granted + " across " + stations.Count
                                 + " station(s)" + (skipped > 0 ? " (" + skipped + " SKIPPED)" : ""));
            }
        }

        /// <summary>
        /// Install each faction's authored capabilities on the founded nodes that suit them best.
        ///
        /// <para><b>Applied here rather than handed to the host, unlike stations and hulls.</b> A
        /// capability is Core state — <c>WorldModel.Develop</c> takes no SE entity — and, more to the
        /// point, each install changes what the next one should choose:
        /// <c>NodeContent.CapabilityAffinityFor</c> scores a Shipyard by <c>HasIndustry</c>, which is
        /// true only once something that refines or assembles already stands there. Planning the
        /// whole set against a frozen world would score every node identically for the one capability
        /// whose placement most depends on the others.</para>
        ///
        /// <para><b>Prerequisite order, derived rather than listed.</b> The install order comes from
        /// <see cref="CapabilityPrerequisites.For"/>, so it cannot drift from the gate it exists to
        /// satisfy. It also produces the clustering above for free: Refinery and Manufactory land
        /// first, and the Shipyard then finds a node that already has industry on it.</para>
        ///
        /// <para><b>Dry runs report totals, not placement, and that is a real limit.</b> Affinity is
        /// read off a committed node's deposit and body — neither exists before <c>SeedNodeAt</c>
        /// runs — so unlike the ship plan there is nothing to rank on a dry run. What a dry run does
        /// still answer is every question the recipe can be wrong about on its own: unknown kinds,
        /// missing prerequisites, counts larger than the estate, and levels above the ceiling.</para>
        /// </summary>
        private static void PlanCapabilities(FactionRecipe recipe, List<PlannedStation> willFound,
            WorldModel world, bool apply, EstateSeedResult result)
        {
            if (world == null) return;
            FactionTuning tuning = world.Tuning ?? new FactionTuning();

            foreach (FactionEstate fe in recipe.Factions ?? new List<FactionEstate>())
            {
                if (fe == null || fe.Capabilities == null || fe.Capabilities.Count == 0) continue;

                var stations = new List<PlannedStation>();
                foreach (PlannedStation ps in willFound)
                    if (string.Equals(ps.Tag, fe.Tag, StringComparison.OrdinalIgnoreCase)) stations.Add(ps);

                int authored = 0;
                foreach (EstateCapability c in fe.Capabilities) if (c != null) authored += c.Count;

                if (stations.Count == 0)
                {
                    result.Warnings.Add(fe.Tag + ": no station was placed, so its " + authored
                        + " authored capability install(s) have nowhere to go.");
                    continue;
                }

                foreach (EstateCapability c in fe.Capabilities)
                {
                    if (c == null) continue;
                    if (c.Count > stations.Count)
                        result.Warnings.Add(fe.Tag + " Capabilities[" + c.Kind + "]: asks for "
                            + c.Count + " node(s) but only " + stations.Count + " are founded; every "
                            + "founded node will get it.");
                }

                if (!apply)
                {
                    result.Lines.Add(fe.Tag + " capabilities=" + authored + " across "
                                     + stations.Count + " station(s) (placement decided on apply)");
                    continue;
                }

                int installed = 0;
                foreach (EstateCapability c in InstallOrder(fe.Capabilities))
                {
                    NodeCapabilityKind kind;
                    if (!TryParseCapability(c.Kind, out kind)) continue;

                    int level = c.EffectiveLevel;
                    int ceiling = tuning.MaxLevelFor(kind);
                    if (level > ceiling)
                    {
                        result.Warnings.Add(fe.Tag + " Capabilities[" + c.Kind + "]: Level "
                            + level + " exceeds this capability's ceiling of " + ceiling
                            + "; installed at " + ceiling + ".");
                        level = ceiling;
                    }
                    if (level < 1) level = 1;

                    foreach (PlannedStation at in BestNodesFor(stations, kind, c.Count, world, tuning))
                    {
                        SpatialNode node;
                        if (at.NodeId == null || !world.Graph.Nodes.TryGetValue(at.NodeId, out node)) continue;

                        double affinity = NodeContent.CapabilityAffinityFor(node, kind, tuning);
                        // Develop adds one level per call and does NOT clamp itself -- its own
                        // contract says the caller owns the ceiling, which is why level is capped
                        // above rather than here.
                        for (int i = 0; i < level; i++) world.Develop(fe.Tag, at.NodeId, kind);

                        result.CapabilityGrants.Add(new CapabilityGrant
                        {
                            NodeId = at.NodeId, Kind = kind, Level = level, Affinity = affinity,
                        });
                        installed++;
                    }
                }
                result.Lines.Add(fe.Tag + " capabilities=" + installed + " across "
                                 + stations.Count + " station(s)");
            }
        }

        /// <summary>The authored capabilities, ordered so nothing is installed before the capability
        /// it depends on. Derived from <see cref="CapabilityPrerequisites.For"/> rather than listed,
        /// so a new prerequisite edge cannot leave a stale order behind.</summary>
        private static List<EstateCapability> InstallOrder(List<EstateCapability> authored)
        {
            var remaining = new List<EstateCapability>();
            foreach (EstateCapability c in authored) if (c != null) remaining.Add(c);

            var ordered = new List<EstateCapability>();
            var placed = new HashSet<NodeCapabilityKind>();

            // Bounded by the list length: each pass places at least one entry unless the authored
            // set has a prerequisite it does not contain, which Validate already refuses -- and if
            // one somehow reaches here, the tail is appended rather than dropped.
            // Forward, so entries whose prerequisites are equally satisfied keep the order they were
            // authored in. Walking backwards reversed every independent pair -- a recipe listing
            // Refinery then FuelDepot installed the depot first -- which is invisible until a
            // capability's affinity depends on what is already installed, and then decides placement.
            while (remaining.Count > 0)
            {
                bool progress = false;
                for (int i = 0; i < remaining.Count; )
                {
                    NodeCapabilityKind kind;
                    if (!TryParseCapability(remaining[i].Kind, out kind)) { remaining.RemoveAt(i); progress = true; continue; }
                    NodeCapabilityKind? prereq = CapabilityPrerequisites.For(kind);
                    if (prereq.HasValue && !placed.Contains(prereq.Value)) { i++; continue; }

                    ordered.Add(remaining[i]);
                    placed.Add(kind);
                    remaining.RemoveAt(i);
                    progress = true;
                }
                if (!progress) { ordered.AddRange(remaining); break; }
            }
            return ordered;
        }

        /// <summary>The <paramref name="count"/> founded nodes that suit <paramref name="kind"/>
        /// best, highest affinity first. Ties break on node id so a run is reproducible; affinity is
        /// frequently tied, since a capability with no matching condition scores every node the same
        /// <c>NodeContent.DefaultAffinity</c>.</summary>
        private static List<PlannedStation> BestNodesFor(List<PlannedStation> stations,
            NodeCapabilityKind kind, int count, WorldModel world, FactionTuning tuning)
        {
            var scored = new List<PlannedStation>(stations);
            scored.Sort((a, b) =>
            {
                double sa = AffinityOf(a, kind, world, tuning);
                double sb = AffinityOf(b, kind, world, tuning);
                if (sb != sa) return sb.CompareTo(sa);
                return string.CompareOrdinal(a.NodeId ?? "", b.NodeId ?? "");
            });
            if (count < scored.Count) scored.RemoveRange(count, scored.Count - count);
            return scored;
        }

        private static double AffinityOf(PlannedStation ps, NodeCapabilityKind kind,
            WorldModel world, FactionTuning tuning)
        {
            SpatialNode node;
            if (ps.NodeId == null || !world.Graph.Nodes.TryGetValue(ps.NodeId, out node))
                return NodeContent.DefaultAffinity;
            return NodeContent.CapabilityAffinityFor(node, kind, tuning);
        }

        /// <summary>Parse against the <see cref="NodeCapabilityKind"/> enum, deliberately NOT against
        /// <c>FactionTuning.AllCapabilities</c> — that array lists six of the enum's seven and omits
        /// <c>SensorArray</c>, so authoring one through it would be impossible. A numeric string
        /// parses too, hence the <c>IsDefined</c> check.</summary>
        private static bool TryParseCapability(string name, out NodeCapabilityKind kind)
        {
            kind = default(NodeCapabilityKind);
            if (string.IsNullOrEmpty(name)) return false;
            NodeCapabilityKind parsed;
            if (!Enum.TryParse(name, true, out parsed)) return false;
            if (!Enum.IsDefined(typeof(NodeCapabilityKind), parsed)) return false;
            kind = parsed;
            return true;
        }

        /// <summary>Cheapest route-feasible hull for the role at this node, or null when none is.
        /// Cheapest rather than best, matching <c>ShipyardManager.SelectPrefabBom</c>'s own ranking:
        /// a starting fleet should look like what the faction would have built first, not like a
        /// gift of its best hull.</summary>
        private static string PickHull(IReadOnlyList<string> pool, IPrefabCatalogue catalogue,
            ShipPool.RouteEnvironment parked)
        {
            string best = null;
            long bestCost = long.MaxValue;
            if (pool == null) return null;

            foreach (string id in pool)
            {
                if (!ShipPool.IsRouteFeasible(catalogue, id, parked)) continue;
                long cost = catalogue.Cost(id);
                // A prefab with no resolved cost sorts last rather than first: zero is "unpriced",
                // not "free", and letting it win would hand every faction the same unscanned hull.
                if (cost <= 0) cost = long.MaxValue - 1;
                if (cost < bestCost || (cost == bestCost && string.CompareOrdinal(id, best) < 0))
                {
                    bestCost = cost;
                    best = id;
                }
            }
            return best;
        }

        private static int CountShips(FactionEstate fe)
        {
            int n = 0;
            foreach (EstateShips s in fe.Ships ?? new List<EstateShips>())
                if (s != null) n += s.Count;
            return n;
        }

        /// <summary>
        /// Resolve a region to its live geometry. Planets match on <b>generator subtype</b> and
        /// features on <b>name</b> — the two key spaces the recipe deliberately keeps separate,
        /// because a planet generated by <c>Kor</c> and a feature called <c>Kor</c> could both exist.
        /// Returns null for a region this world does not contain, which the planner reports as a
        /// refusal rather than skipping.
        /// <para><paramref name="features"/> must be the <b>resolved</b> list — the one whose anchors
        /// have already been pointed at real bodies — or a ring will be measured around the origin.</para>
        /// </summary>
        public static Func<PlacementRegion, PlacementRegionGeometry> GeometryFrom(
            IPlanetCatalogue planets, IEnumerable<WorldFeature> features)
        {
            return region =>
            {
                if (region == null || string.IsNullOrEmpty(region.Key)) return null;

                if (region.Kind == PlacementRegionKind.Feature)
                {
                    if (features == null) return null;
                    foreach (WorldFeature f in features)
                    {
                        if (f == null || !string.Equals(f.Name, region.Key, StringComparison.OrdinalIgnoreCase)) continue;
                        return new PlacementRegionGeometry
                        {
                            CentreKm = f.Center,
                            InnerRadiusKm = f.InnerRadiusKm,
                            OuterRadiusKm = f.OuterRadiusKm,
                            HalfThicknessKm = f.HalfThicknessKm,
                        };
                    }
                    return null;
                }

                if (planets == null) return null;
                foreach (PlanetRecord p in planets.Planets)
                {
                    if (p == null || !string.Equals(p.Generator, region.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    return new PlacementRegionGeometry
                    {
                        CentreKm = p.Center,
                        SurfaceRadiusKm = p.SurfaceRadius,
                        AllowsSurface = p.AllowsSurfaceStations,
                    };
                }
                return null;
            };
        }

        /// <summary>
        /// The separation floor a region places at: the authored value where there is one, else the
        /// global per-zone floor. Same precedence as <see cref="WorldModel.SeparationAtKm"/> — an
        /// authored feature wins, then an authored body, then the zone.
        /// </summary>
        public static Func<PlacementRegion, double> SeparationFrom(
            IPlanetCatalogue planets, IEnumerable<WorldFeature> features, FactionTuning tuning)
        {
            return region =>
            {
                if (region == null || tuning == null) return 0;

                if (region.Kind == PlacementRegionKind.Feature)
                {
                    if (features != null)
                        foreach (WorldFeature f in features)
                        {
                            if (f == null || !string.Equals(f.Name, region.Key, StringComparison.OrdinalIgnoreCase)) continue;
                            if (f.NodeSeparationKm.HasValue && f.NodeSeparationKm.Value > 0)
                                return f.NodeSeparationKm.Value;
                            return tuning.NodeSeparationFor(f.Zone.HasValue ? f.Zone.Value : NodeZone.DeepSpace);
                        }
                    return tuning.NodeSeparationFor(NodeZone.DeepSpace);
                }

                if (planets != null)
                    foreach (PlanetRecord p in planets.Planets)
                    {
                        if (p == null || !string.Equals(p.Generator, region.Key, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.NodeSeparationKm.HasValue && p.NodeSeparationKm.Value > 0)
                            return p.NodeSeparationKm.Value;
                        break;
                    }
                return tuning.NodeSeparationFor(NodeZone.Surface);
            };
        }

        private static List<Vec3> SampleRegion(PlacementRegion region, PlacementRegionGeometry g,
                                               int seed, int count, WorldModel world)
        {
            if (g == null) return new List<Vec3>();
            if (region.Kind == PlacementRegionKind.PlanetSurface)
                return RegionSampler.OnSphere(g.CentreKm, g.SurfaceRadiusKm, count, seed);

            // The feature's own claim, not a re-derivation of it: TrySeparationAt is what
            // SeparationAtKm consults, so a point this accepts is a point that keeps the region's
            // separation instead of falling through to the deep-space floor.
            Func<Vec3, bool> claims = null;
            if (world != null)
                claims = pos =>
                {
                    double ignored;
                    return world.Features.TrySeparationAt(pos, out ignored);
                };

            return RegionSampler.InAnnulus(g.CentreKm, g.InnerRadiusKm, g.OuterRadiusKm,
                g.HalfThicknessKm > 0 ? g.HalfThicknessKm : g.OuterRadiusKm, count, seed, claims);
        }
    }
}
