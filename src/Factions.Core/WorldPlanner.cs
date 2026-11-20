using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Turns a <see cref="WorldRecipe"/> into a <see cref="WorldPlan"/>: validates it, resolves every
    /// derived number, and places the bodies that were not given a centre.
    ///
    /// <para>Pure and SE-free. It needs nothing from a live session because every physical constant
    /// it uses comes from <see cref="IPlanetTypeCatalogue"/>, so a plan can be produced, reviewed and
    /// unit-tested long before a world exists to apply it to.</para>
    /// </summary>
    public static class WorldPlanner
    {
        /// <summary>SE's own hard cap, <c>MyPlanets.MAX_PLANET_SIZE</c> — 8 000 000 m of diameter.</summary>
        public const double MaxPlanetDiameterKm = 8000.0;

        /// <summary>Real Gas Giants replaces any planet on its generator whose terrain radius exceeds
        /// this, so a gas giant's voxel core has to stay under it. The body players see is
        /// <see cref="GasGiantSpec.RadiusKm"/>, which is unrelated.</summary>
        public const double GasGiantMaxTerrainRadiusKm = 10.0;

        /// <summary>
        /// Plans <paramref name="recipe"/>.
        /// </summary>
        /// <param name="maxPlanetTypes">The session's <c>MaxPlanets</c>, which caps distinct planet
        /// <b>types</b>. Zero means "not known here" and skips the check — the host passes the live
        /// value so the plan fails now rather than getting a null back from SE later.</param>
        /// <param name="obstacles">Things already in the sector that a new body must not be created
        /// on top of. Optional, and its absence is reported rather than assumed: with these supplied,
        /// running a destructive cleanup before planning becomes a preference rather than a
        /// correctness requirement.</param>
        public static WorldPlan Plan(WorldRecipe recipe, IPlanetTypeCatalogue types, int maxPlanetTypes = 0,
            IEnumerable<PlacementObstacle> obstacles = null)
        {
            var plan = new WorldPlan();
            if (recipe == null) { plan.Errors.Add("no recipe."); return plan; }
            plan.WorldSeed = recipe.WorldSeed;

            if (types == null) { plan.Errors.Add("no planet-type catalogue -- cannot resolve any definition."); return plan; }
            if (recipe.Planets.Count == 0) plan.Notes.Add("recipe declares no planets.");

            WorldVolume volume = recipe.Volume ?? new WorldVolume();
            WorldPlacementRules rules = recipe.Placement ?? new WorldPlacementRules();

            var hazards = new List<PlacementObstacle>();
            if (obstacles != null)
                foreach (PlacementObstacle o in obstacles) if (o != null) hazards.Add(o);
            plan.ObstaclesConsidered = hazards.Count;
            if (volume.ExtentKm <= 0)
                plan.Errors.Add("Volume.ExtentKm must be greater than zero -- it is the parameter that says how big the world is.");

            WorldLayout layout = recipe.Layout;
            if (layout != null)
            {
                string layoutWhy;
                if (!layout.Validate(out layoutWhy)) { plan.Errors.Add("Layout: " + layoutWhy); layout = null; }
            }
            else plan.Notes.Add("no Layout -- unplaced bodies are scattered uniformly in the volume rather than orbiting.");

            // ---- planets: validate and resolve ------------------------------------------------
            var specs = new Dictionary<string, PlanetTypeSpec>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolved = new List<PlannedPlanet>();

            foreach (RecipePlanet rp in recipe.Planets)
            {
                if (rp == null) { plan.Errors.Add("null planet entry."); continue; }
                string label = string.IsNullOrEmpty(rp.Name) ? "(unnamed)" : rp.Name;

                if (string.IsNullOrEmpty(rp.Name)) { plan.Errors.Add("a planet has no Name."); continue; }
                if (!names.Add(rp.Name)) { plan.Errors.Add("duplicate planet name '" + rp.Name + "' -- names are how features anchor."); continue; }

                PlanetTypeSpec spec;
                if (!types.TryGet(rp.Definition, out spec))
                {
                    plan.Errors.Add(label + ": unknown planet definition '" + (rp.Definition ?? "(none)")
                                    + "' -- SE answers an unknown definition with a bare null at spawn time.");
                    continue;
                }
                if (rp.DiameterKm <= 0) { plan.Errors.Add(label + ": DiameterKm must be greater than zero."); continue; }
                if (rp.DiameterKm > MaxPlanetDiameterKm)
                {
                    plan.Errors.Add(label + ": DiameterKm " + rp.DiameterKm.ToString("0.#") + " exceeds SE's cap of "
                                    + MaxPlanetDiameterKm.ToString("0") + ".");
                    continue;
                }

                specs[rp.Name] = spec;
                definitions.Add(spec.DefinitionName ?? rp.Definition);

                int seed = WorldRecipe.ResolveSeed(recipe.WorldSeed, rp);
                string storage = WorldRecipe.StorageName(rp, seed);
                if (!storageNames.Add(storage))
                    plan.Errors.Add(label + ": storage name '" + storage + "' collides with another body.");

                if (rp.GasGiant != null)
                {
                    string ggWhy;
                    if (!rp.GasGiant.Validate(out ggWhy)) { plan.Errors.Add(label + ": " + ggWhy); continue; }

                    // Real Gas Giants closes any planet on its own generator whose MaximumRadius
                    // exceeds 10 km and re-spawns a 19 km one in its place, at a position it derives
                    // with an offset that does not cancel the storage cube. A body over the limit is
                    // therefore not merely resized -- it is deleted and moved.
                    double terrainKm = spec.MaxRadiusKm(rp.DiameterKm);
                    if (terrainKm > GasGiantMaxTerrainRadiusKm)
                    {
                        plan.Errors.Add(label + ": a gas giant's voxel body must stay within "
                            + GasGiantMaxTerrainRadiusKm.ToString("0.#") + " km of terrain radius (this one is "
                            + terrainKm.ToString("0.##") + " km). Real Gas Giants closes anything larger and "
                            + "re-spawns its own 19 km body at a position of its choosing. Size the body you "
                            + "actually want with GasGiant.RadiusKm, which is the virtual sphere, not this.");
                        continue;
                    }
                }

                var pp = new PlannedPlanet
                {
                    Name = rp.Name,
                    Definition = spec.DefinitionName ?? rp.Definition,
                    StorageName = storage,
                    Seed = seed,
                    DiameterKm = rp.DiameterKm,
                    StorageSizeKm = spec.StorageSizeKm(rp.DiameterKm),
                    TerrainRadiusKm = spec.MaxRadiusKm(rp.DiameterKm),
                    AllowSurfaceEconomyStations = spec.AllowSurfaceEconomyStations,
                    AllowOrbitalEconomyStations = spec.AllowOrbitalEconomyStations,
                    CenterWasAuthored = rp.CenterKm.HasValue,
                    AddGps = rp.AddGps,
                    GasGiant = rp.GasGiant,
                    Orbit = rp.Orbit
                };

                // A gas giant's gravity is the mod's, not the generator definition's: the voxel core
                // is 9.5 km while the body players fly around is a 180 km virtual sphere reaching
                // 305 km. Reading it off the definition would understate the well by 25x.
                pp.GravityLimitKm = rp.GasGiant != null
                    ? rp.GasGiant.GravityLimitKm()
                    : spec.GravityLimitKm(rp.DiameterKm);
                pp.ExclusionRadiusKm = pp.GravityLimitKm;
                if (rp.GasGiant != null)
                {
                    double ringReach = rp.GasGiant.RingReachKm();
                    if (ringReach > pp.ExclusionRadiusKm) pp.ExclusionRadiusKm = ringReach;
                }
                if (rp.CenterKm.HasValue) pp.CenterKm = rp.CenterKm.Value;
                resolved.Add(pp);
            }

            plan.DistinctDefinitionCount = definitions.Count;
            if (maxPlanetTypes > 0 && definitions.Count > maxPlanetTypes)
                plan.Errors.Add("recipe uses " + definitions.Count + " distinct planet type(s) but the session's MaxPlanets is "
                                + maxPlanetTypes + ". MaxPlanets caps distinct TYPES, not bodies -- and SE rejects the excess "
                                + "with a bare null, so this must be raised in the world config before applying.");

            // ---- features: resolve anchors, and widen the exclusion radius they imply ----------
            var byName = new Dictionary<string, PlannedPlanet>(StringComparer.OrdinalIgnoreCase);
            foreach (PlannedPlanet pp in resolved) byName[pp.Name] = pp;

            // Each gas-giant ring becomes a world feature derived from the SAME radius and scales
            // the mod's own config gets, so the two cannot drift apart.
            foreach (PlannedPlanet pp in resolved)
            {
                if (pp.GasGiant == null || pp.GasGiant.Rings == null) continue;
                int index = 0;
                foreach (GasGiantRingSpec ring in pp.GasGiant.Rings)
                {
                    index++;
                    if (ring == null) continue;
                    var derived = new PlannedFeature
                    {
                        Name = pp.Name + (index > 1 ? " Ring " + index : " Ring"),
                        Shape = WorldFeatureShape.Ring,
                        AnchorPlanetName = pp.Name,
                        Normal = ring.Normal,
                        InnerRadiusKm = ring.FeatureInnerKm(pp.GasGiant.RadiusKm),
                        OuterRadiusKm = ring.FeatureOuterKm(pp.GasGiant.RadiusKm),
                        HalfThicknessKm = ring.FeatureHalfThicknessKm,
                        Density = ring.Density,
                        Materials = ring.Materials,   // null stays null: no ore opinion
                        Zone = ring.Zone,
                        NodeSeparationKm = ring.NodeSeparationKm,
                        SpaceRespawn = ring.SpaceRespawn,
                        DerivedFromGasGiant = true
                    };
                    derived.Bands = BandsFromFractions(derived.InnerRadiusKm, derived.OuterRadiusKm,
                        ring.BandFractions, ring.BandOre);
                    plan.Features.Add(derived);
                }
            }

            foreach (RecipeFeature rf in recipe.Features)
            {
                if (rf == null) { plan.Errors.Add("null feature entry."); continue; }
                string label = string.IsNullOrEmpty(rf.Name) ? "(unnamed feature)" : rf.Name;

                var pf = new PlannedFeature
                {
                    Name = rf.Name,
                    Shape = rf.Shape,
                    Normal = rf.Normal,
                    InnerRadiusKm = rf.InnerRadiusKm,
                    OuterRadiusKm = rf.OuterRadiusKm,
                    HalfThicknessKm = rf.HalfThicknessKm,
                    Density = rf.Density,
                    Bands = rf.Bands ?? new List<FeatureBand>(),
                    Materials = rf.Materials,   // null stays null: no ore opinion
                    Zone = rf.Zone,
                    NodeSeparationKm = rf.NodeSeparationKm,
                    SpaceRespawn = rf.SpaceRespawn,
                    CenterKm = rf.Center
                };

                if (!string.IsNullOrEmpty(rf.AnchorPlanet))
                {
                    PlannedPlanet anchor;
                    if (!byName.TryGetValue(rf.AnchorPlanet, out anchor))
                    {
                        // The whole reason anchoring happens here: at boot this is a WARN and the
                        // feature quietly lands on its literal Center. Here it can refuse.
                        plan.Errors.Add(label + ": AnchorPlanet '" + rf.AnchorPlanet + "' is not a planet in this recipe.");
                        continue;
                    }
                    pf.AnchorPlanetName = anchor.Name;
                    // A ring can reach far past its body -- 450 km off a 9.5 km core, in the case
                    // this rule exists for. Sizing separation off the planet would be off by 50x.
                    if (rf.OuterRadiusKm > anchor.ExclusionRadiusKm)
                        anchor.ExclusionRadiusKm = rf.OuterRadiusKm;
                }

                string why;
                if (!ValidateFeatureGeometry(pf, out why)) { plan.Errors.Add(label + ": " + why); continue; }
                plan.Features.Add(pf);
            }

            // ---- placement --------------------------------------------------------------------
            var placed = new List<PlannedPlanet>();

            // Authored centres are fixed points, so they go down first and everything else works
            // around them. The rest go largest-exclusion-first: the hardest body to fit is the one
            // that should get the emptiest world.
            foreach (PlannedPlanet pp in resolved)
                if (pp.CenterWasAuthored) PlaceAuthored(pp, placed, hazards, volume, rules, plan);

            // Resolve the system centre BEFORE choosing what still needs placing: the central body
            // anchors the system, so it must not also be distributed across the bands it defines.
            Vec3 systemCentre = SystemCentre(layout, byName, plan);
            foreach (PlannedPlanet pp in resolved)
                if (pp.Placed && !placed.Contains(pp)) placed.Add(pp);

            var pending = new List<PlannedPlanet>();
            foreach (PlannedPlanet pp in resolved) if (!pp.Placed) pending.Add(pp);
            pending.Sort(delegate (PlannedPlanet a, PlannedPlanet b)
            {
                int byRadius = b.ExclusionRadiusKm.CompareTo(a.ExclusionRadiusKm);
                return byRadius != 0 ? byRadius : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            var rng = new SeededRandom(recipe.WorldSeed);
            int bandCursor = 0;
            foreach (PlannedPlanet pp in pending)
            {
                if (layout != null)
                    PlaceInOrbit(pp, placed, hazards, layout, systemCentre, rules, rng, plan, ref bandCursor);
                else
                    PlaceRandom(pp, placed, hazards, volume, rules, specs, rng, plan);
            }

            // Min corners last, so they are derived from whatever centre each body ended up with.
            foreach (PlannedPlanet pp in resolved)
            {
                PlanetTypeSpec spec = specs[pp.Name];
                pp.MinCornerKm = spec.MinCornerFromCenterKm(pp.CenterKm, pp.DiameterKm);
            }

            // Anchored features inherit their body's final centre, so the loader's no-anchor
            // fallback lands on the body instead of at the world origin.
            foreach (PlannedFeature pf in plan.Features)
            {
                if (string.IsNullOrEmpty(pf.AnchorPlanetName)) continue;
                PlannedPlanet anchor;
                if (byName.TryGetValue(pf.AnchorPlanetName, out anchor)) pf.CenterKm = anchor.CenterKm;
            }

            plan.Planets.AddRange(resolved);
            return plan;
        }

        /// <summary>Where the bands are measured from: the named central body's centre, or the
        /// world origin.</summary>
        private static Vec3 SystemCentre(WorldLayout layout, Dictionary<string, PlannedPlanet> byName, WorldPlan plan)
        {
            var origin = new Vec3(0, 0, 0);
            if (layout == null || string.IsNullOrEmpty(layout.CentralBody)) return origin;

            PlannedPlanet central;
            if (!byName.TryGetValue(layout.CentralBody, out central))
            {
                plan.Errors.Add("Layout.CentralBody '" + layout.CentralBody + "' is not a planet in this recipe.");
                return origin;
            }
            // An unauthored central body anchors the system at the origin rather than being
            // distributed like everything else -- being the centre is the whole of its position.
            if (!central.CenterWasAuthored)
            {
                central.CenterKm = origin;
                central.CenterWasAuthored = true;
                central.Placed = true;
                plan.Notes.Add(central.Name + ": central body, placed at the system centre.");
            }
            return central.CenterKm;
        }

        /// <summary>Sub-bands of a ring, from fraction pairs of its span, each carrying the ore
        /// authored for it. Empty fractions leave the ring solid; absent or short
        /// <paramref name="bandOre"/> leaves a band inheriting the ring's own materials.</summary>
        internal static List<FeatureBand> BandsFromFractions(double innerKm, double outerKm,
            List<double> fractions, List<RingBandOre> bandOre)
        {
            var bands = new List<FeatureBand>();
            if (fractions == null || fractions.Count < 2) return bands;
            double span = outerKm - innerKm;
            for (int i = 0; i + 1 < fractions.Count; i += 2)
            {
                int index = i / 2;
                RingBandOre ore = bandOre != null && index < bandOre.Count ? bandOre[index] : null;
                bands.Add(new FeatureBand
                {
                    InnerRadiusKm = innerKm + fractions[i] * span,
                    OuterRadiusKm = innerKm + fractions[i + 1] * span,
                    Density = ore == null ? null : ore.Density,
                    Materials = ore == null ? null : ore.Materials
                });
            }
            return bands;
        }

        private static void PlaceInOrbit(PlannedPlanet pp, List<PlannedPlanet> placed, List<PlacementObstacle> hazards,
            WorldLayout layout, Vec3 centre, WorldPlacementRules rules, IRandom rng, WorldPlan plan, ref int bandCursor)
        {
            OrbitBand band = layout.FindBand(pp.Orbit);
            if (band == null && !string.IsNullOrEmpty(pp.Orbit))
            {
                pp.Placed = false;
                pp.Rejection = "Orbit '" + pp.Orbit + "' names no band in the layout.";
                return;
            }
            // Unpinned bodies take bands in turn rather than at random: one per band reads as a
            // system, where a random draw piles two into one orbit and leaves another empty.
            if (band == null)
            {
                band = layout.Bands[bandCursor % layout.Bands.Count];
                bandCursor++;
            }

            int attempts = Math.Max(1, rules.MaxAttemptsPerPlanet);
            string lastWhy = "no candidate position was tried.";
            for (int i = 0; i < attempts; i++)
            {
                pp.Attempts++;
                Vec3 candidate = OrbitSampler.Sample(centre, band, layout.PlaneNormal, layout.InclinationDeg, rng);
                string why;
                if (Accepts(pp, candidate, placed, hazards, null, rules, out why))
                {
                    pp.CenterKm = candidate;
                    pp.Placed = true;
                    pp.OrbitBandName = string.IsNullOrEmpty(band.Name) ? null : band.Name;
                    pp.OrbitRadiusKm = candidate.DistanceTo(centre);
                    placed.Add(pp);
                    if (pp.Attempts > 1) plan.Notes.Add(pp.Name + ": placed after " + pp.Attempts + " attempt(s).");
                    return;
                }
                lastWhy = why;
            }

            pp.Placed = false;
            pp.Rejection = "no position found on band "
                         + (string.IsNullOrEmpty(band.Name) ? band.InnerRadiusKm.ToString("0.#") + "-" + band.OuterRadiusKm.ToString("0.#") + " km" : "'" + band.Name + "'")
                         + " in " + pp.Attempts + " attempt(s); last refusal: " + lastWhy;
        }

        private static void PlaceAuthored(PlannedPlanet pp, List<PlannedPlanet> placed,
            List<PlacementObstacle> hazards, WorldVolume volume, WorldPlacementRules rules, WorldPlan plan)
        {
            string why;
            if (Accepts(pp, pp.CenterKm, placed, hazards, volume, rules, out why))
            {
                pp.Placed = true;
                placed.Add(pp);
                return;
            }
            // An authored centre is an instruction, not a suggestion: honour it and say what it
            // violates, rather than silently moving a body the operator placed on purpose.
            pp.Placed = true;
            placed.Add(pp);
            plan.Notes.Add(pp.Name + ": authored centre " + pp.CenterKm + " km violates " + why
                           + " -- kept as authored.");
        }

        private static void PlaceRandom(PlannedPlanet pp, List<PlannedPlanet> placed,
            List<PlacementObstacle> hazards, WorldVolume volume, WorldPlacementRules rules,
            Dictionary<string, PlanetTypeSpec> specs, IRandom rng, WorldPlan plan)
        {
            double reach = volume.ExtentKm - pp.ExclusionRadiusKm;
            if (reach <= 0)
            {
                pp.Placed = false;
                pp.Rejection = "the volume (extent " + volume.ExtentKm.ToString("0.#") + " km) is smaller than this body's own "
                             + "exclusion radius (" + pp.ExclusionRadiusKm.ToString("0.#") + " km).";
                return;
            }

            int attempts = Math.Max(1, rules.MaxAttemptsPerPlanet);
            string lastWhy = "no candidate position was tried.";
            for (int i = 0; i < attempts; i++)
            {
                pp.Attempts++;
                Vec3 candidate = Sample(volume, reach, rng);
                string why;
                if (Accepts(pp, candidate, placed, hazards, volume, rules, out why))
                {
                    pp.CenterKm = candidate;
                    pp.Placed = true;
                    placed.Add(pp);
                    if (pp.Attempts > 1)
                        plan.Notes.Add(pp.Name + ": placed after " + pp.Attempts + " attempt(s).");
                    return;
                }
                lastWhy = why;
            }

            pp.Placed = false;
            pp.Rejection = "no position found in " + pp.Attempts + " attempt(s); last refusal: " + lastWhy;
        }

        /// <summary>Uniform inside the volume, shrunk by <paramref name="reach"/> so the body and its
        /// exclusion sphere stay inside the extent the operator asked for.</summary>
        private static Vec3 Sample(WorldVolume volume, double reach, IRandom rng)
        {
            if (volume.Shape == WorldVolumeShape.Sphere)
            {
                // Rejection sampling in the bounding cube: correct with nothing but NextDouble(),
                // and it terminates quickly (a ball fills 52% of its cube).
                for (int i = 0; i < 64; i++)
                {
                    Vec3 c = Cube(reach, rng);
                    if (c.X * c.X + c.Y * c.Y + c.Z * c.Z <= reach * reach) return c;
                }
                return new Vec3(0, 0, 0);
            }
            return Cube(reach, rng);
        }

        private static Vec3 Cube(double halfExtent, IRandom rng)
        {
            return new Vec3((rng.NextDouble() * 2.0 - 1.0) * halfExtent,
                            (rng.NextDouble() * 2.0 - 1.0) * halfExtent,
                            (rng.NextDouble() * 2.0 - 1.0) * halfExtent);
        }

        /// <summary>The three placement constraints, in the order that makes the cheapest one fail first.</summary>
        private static bool Accepts(PlannedPlanet pp, Vec3 center, List<PlannedPlanet> placed,
            List<PlacementObstacle> hazards, WorldVolume volume, WorldPlacementRules rules, out string why)
        {
            double originDistance = Math.Sqrt(center.X * center.X + center.Y * center.Y + center.Z * center.Z);

            if (volume != null && volume.OriginKeepOutKm > 0 && originDistance < volume.OriginKeepOutKm + pp.ExclusionRadiusKm)
            {
                why = "the origin keep-out (needs " + (volume.OriginKeepOutKm + pp.ExclusionRadiusKm).ToString("0.#")
                    + " km from origin, is " + originDistance.ToString("0.#") + " km)";
                return false;
            }

            foreach (PlannedPlanet other in placed)
            {
                double needed = pp.ExclusionRadiusKm + other.ExclusionRadiusKm + rules.SeparationMarginKm;
                double actual = center.DistanceTo(other.CenterKm);
                if (actual < needed)
                {
                    why = "clearance from " + other.Name + " (needs " + needed.ToString("0.#")
                        + " km, is " + actual.ToString("0.#") + " km)";
                    return false;
                }
            }

            // Measured off the TERRAIN radius, not the exclusion radius. A station inside a gravity
            // well is ordinary -- a station inside the voxel is not, and forbidding the former would
            // also forbid a station sitting in the ring it was authored for.
            if (hazards != null && hazards.Count > 0)
            {
                double terrainKm = pp.TerrainRadiusKm;
                foreach (PlacementObstacle hazard in hazards)
                {
                    double needed = terrainKm + hazard.RadiusKm + rules.ObstacleMarginKm;
                    double actual = center.DistanceTo(hazard.PositionKm);
                    if (actual < needed)
                    {
                        why = "clearance from " + (string.IsNullOrEmpty(hazard.Label) ? "an existing object" : hazard.Label)
                            + " (needs " + needed.ToString("0.#") + " km, is " + actual.ToString("0.#") + " km)";
                        return false;
                    }
                }
            }

            why = null;
            return true;
        }

        private static bool ValidateFeatureGeometry(PlannedFeature f, out string why)
        {
            if (string.IsNullOrEmpty(f.Name)) { why = "no Name"; return false; }
            if (f.OuterRadiusKm <= 0) { why = "needs OuterRadiusKm > 0"; return false; }
            if (f.Shape != WorldFeatureShape.Sphere)
            {
                if (f.InnerRadiusKm < 0) { why = "InnerRadiusKm cannot be negative"; return false; }
                if (f.InnerRadiusKm >= f.OuterRadiusKm) { why = "InnerRadiusKm must be less than OuterRadiusKm"; return false; }
                if (f.Shape == WorldFeatureShape.Ring && f.HalfThicknessKm <= 0) { why = "Ring needs HalfThicknessKm > 0"; return false; }
            }
            why = null;
            return true;
        }
    }
}
