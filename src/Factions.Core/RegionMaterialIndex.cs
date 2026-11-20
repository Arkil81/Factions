using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which ores a body's terrain yields. The host seam: planet ore lives in SE's own generator
    /// definitions (<c>MyPlanetGeneratorDefinition.OreMappings</c>), which Core cannot read.
    ///
    /// <para>Features need no equivalent — their composition is authored in the world files and is
    /// already a Core type.</para>
    /// </summary>
    public interface IPlanetOreLookup
    {
        /// <summary>Relative shares by bare subtype (<c>Platinum</c>) for the body whose
        /// <see cref="PlanetRecord.Id"/> is given. Empty when the body carries no ore mapping — the
        /// gas giant's case, and a legitimate answer rather than a failure.</summary>
        IEnumerable<KeyValuePair<string, double>> SurfaceOreShares(string planetId);
    }

    /// <summary>
    /// The region-keyed view of the world that a colonisation decision needs: what each region is made
    /// of, how full it is, and how far away.
    ///
    /// <para><b>Built once, queried many times.</b> Assigning nodes to regions is a pass over the whole
    /// graph and testing feature containment is per-node work; doing it per query would put a graph
    /// scan inside a scoring loop. Build it at the top of a pass and throw it away at the end.</para>
    ///
    /// <para><b>A planet's surface yields its ore; its orbit yields nothing.</b> Orbit is a placement
    /// region, not an ore region, so it never appears in <see cref="RegionsYielding"/> — only in
    /// <see cref="RegionsNear"/>, as somewhere to stand. That is exactly what a beachhead is: it is not
    /// there to mine.</para>
    ///
    /// <para><b>Occupancy counts every owner.</b> A body full of somebody else's nodes is just as full,
    /// and that is the case colonisation most needs to notice.</para>
    /// </summary>
    public sealed class RegionMaterialIndex : IRegionMaterialIndex
    {
        private readonly List<ColonisationCandidate> _all = new List<ColonisationCandidate>();
        private readonly Dictionary<string, List<ColonisationCandidate>> _byMaterial =
            new Dictionary<string, List<ColonisationCandidate>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vec3> _centreByRegion =
            new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);

        private RegionMaterialIndex() { }

        /// <summary>
        /// Build the index for one decision.
        ///
        /// <para><paramref name="featuresWithCentres"/> pairs each feature with its **resolved** centre.
        /// Passed in rather than resolved here because anchor resolution already lives in
        /// <see cref="WorldFeatureGeography"/>, and two implementations of it would drift.</para>
        ///
        /// <para><paramref name="anchor"/> is where the deciding faction is measuring from — its own
        /// territory. Distance is real cost: transit, and interception exposure on every leg after.</para>
        /// </summary>
        public static RegionMaterialIndex Build(
            IPlanetCatalogue planets,
            IEnumerable<KeyValuePair<WorldFeature, Vec3>> featuresWithCentres,
            IEnumerable<SpatialNode> nodes,
            IPlanetOreLookup planetOre,
            FactionTuning tuning,
            Vec3 anchor)
        {
            var index = new RegionMaterialIndex();

            var surfaceByPlanet = new Dictionary<string, ColonisationCandidate>(StringComparer.Ordinal);
            var orbitByPlanet = new Dictionary<string, ColonisationCandidate>(StringComparer.Ordinal);

            if (planets != null && planets.Planets != null)
            {
                foreach (PlanetRecord p in planets.Planets)
                {
                    if (p == null || string.IsNullOrEmpty(p.Id)) continue;

                    double surfaceSep = p.NodeSeparationKm.HasValue && p.NodeSeparationKm.Value > 0
                        ? p.NodeSeparationKm.Value
                        : SeparationFallback(tuning, NodeZone.Surface);

                    ColonisationCandidate surface = new ColonisationCandidate
                    {
                        Region = new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = p.Generator },
                        Geometry = new PlacementRegionGeometry
                        {
                            SurfaceRadiusKm = p.SurfaceRadius,
                            AllowsSurface = p.AllowsSurfaceStations
                        },
                        SeparationKm = surfaceSep,
                        DistanceKm = anchor.DistanceTo(p.Center),
                    };
                    surfaceByPlanet[p.Id] = surface;
                    index.Add(surface, p.Center);

                    ColonisationCandidate orbit = new ColonisationCandidate
                    {
                        Region = new PlacementRegion { Kind = PlacementRegionKind.PlanetOrbit, Key = p.Generator },
                        Geometry = new PlacementRegionGeometry
                        {
                            InnerRadiusKm = p.SurfaceRadius,
                            OuterRadiusKm = p.GravityRadius > p.SurfaceRadius ? p.GravityRadius : p.SurfaceRadius * 2.0,
                            HalfThicknessKm = 0,
                            AllowsSurface = false
                        },
                        SeparationKm = SeparationFallback(tuning, NodeZone.Orbit),
                        DistanceKm = anchor.DistanceTo(p.Center),
                    };
                    orbitByPlanet[p.Id] = orbit;
                    index.Add(orbit, p.Center);

                    // The body's own ore belongs to its terrain. An unlandable body still gets a surface
                    // candidate, with zero capacity -- so it reads as full rather than as absent, and a
                    // decision diverts to its orbit instead of silently ignoring the ore beneath it.
                    if (planetOre != null)
                        foreach (KeyValuePair<string, double> kv in planetOre.SurfaceOreShares(p.Id))
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                                index.Offer(kv.Key, surface, kv.Value);
                }
            }

            var featureList = new List<KeyValuePair<WorldFeature, Vec3>>();
            if (featuresWithCentres != null)
                foreach (KeyValuePair<WorldFeature, Vec3> kv in featuresWithCentres)
                    if (kv.Key != null && !string.IsNullOrEmpty(kv.Key.Name)) featureList.Add(kv);

            var featureCandidates = new List<ColonisationCandidate>();
            foreach (KeyValuePair<WorldFeature, Vec3> kv in featureList)
            {
                WorldFeature f = kv.Key;
                ColonisationCandidate c = new ColonisationCandidate
                {
                    Region = new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = f.Name },
                    Geometry = new PlacementRegionGeometry
                    {
                        InnerRadiusKm = f.InnerRadiusKm,
                        OuterRadiusKm = f.OuterRadiusKm,
                        HalfThicknessKm = f.HalfThicknessKm,
                        AllowsSurface = false
                    },
                    SeparationKm = f.NodeSeparationKm.HasValue && f.NodeSeparationKm.Value > 0
                        ? f.NodeSeparationKm.Value
                        : SeparationFallback(tuning, f.Zone.HasValue ? f.Zone.Value : NodeZone.DeepSpace),
                    DistanceKm = anchor.DistanceTo(kv.Value),
                };
                featureCandidates.Add(c);
                index.Add(c, kv.Value);

                foreach (KeyValuePair<string, double> m in SharesOf(f))
                    index.Offer(m.Key, c, m.Value);
            }

            // One pass over the graph for occupancy. Every owner counts.
            if (nodes != null)
            {
                foreach (SpatialNode n in nodes)
                {
                    if (n == null) continue;

                    if (!string.IsNullOrEmpty(n.PlanetId))
                    {
                        ColonisationCandidate hit;
                        if (n.Zone == NodeZone.Surface && surfaceByPlanet.TryGetValue(n.PlanetId, out hit))
                        { hit.OccupiedNodes++; continue; }
                        if (n.Zone == NodeZone.Orbit && orbitByPlanet.TryGetValue(n.PlanetId, out hit))
                        { hit.OccupiedNodes++; continue; }
                    }

                    for (int i = 0; i < featureList.Count; i++)
                    {
                        if (!featureList[i].Key.Contains(n.Position, featureList[i].Value)) continue;
                        featureCandidates[i].OccupiedNodes++;
                        break;   // regions do not overlap by construction; first containment wins
                    }
                }
            }

            return index;
        }

        public IEnumerable<ColonisationCandidate> RegionsYielding(string subTypeId)
        {
            List<ColonisationCandidate> l;
            if (!string.IsNullOrEmpty(subTypeId) && _byMaterial.TryGetValue(subTypeId, out l)) return l;
            return new List<ColonisationCandidate>();
        }

        /// <summary>Regions worth diverting to when <paramref name="full"/> has no room, **nearest
        /// first** — the ordering <see cref="ColonisationPlanner"/> relies on.
        /// <para>A full surface's own orbit sits at distance zero from it, so it comes first without
        /// being special-cased: it is genuinely the closest region to the one that would not take
        /// another node, and it is what puts a faction's influence within reach of it.</para></summary>
        public IEnumerable<ColonisationCandidate> RegionsNear(PlacementRegion full)
        {
            var ordered = new List<ColonisationCandidate>();
            if (full == null) return ordered;

            Vec3 from;
            if (!_centreByRegion.TryGetValue(KeyOf(full), out from)) return ordered;

            string exclude = KeyOf(full);
            foreach (ColonisationCandidate c in _all)
            {
                if (c == null || c.Region == null) continue;
                if (string.Equals(KeyOf(c.Region), exclude, StringComparison.OrdinalIgnoreCase)) continue;
                ordered.Add(c);
            }

            ordered.Sort(delegate (ColonisationCandidate a, ColonisationCandidate b)
            {
                double da = DistanceFrom(from, a), db = DistanceFrom(from, b);
                int byDistance = da.CompareTo(db);
                return byDistance != 0 ? byDistance
                     : string.CompareOrdinal(KeyOf(a.Region), KeyOf(b.Region));   // stable, not hash-ordered
            });
            return ordered;
        }

        // ---- internals -----------------------------------------------------------------------

        private void Add(ColonisationCandidate c, Vec3 centre)
        {
            _all.Add(c);
            _centreByRegion[KeyOf(c.Region)] = centre;
        }

        /// <summary>Records that a region yields a material. A region can offer several, and the same
        /// candidate object is shared across them so occupancy is counted once.</summary>
        private void Offer(string subTypeId, ColonisationCandidate region, double share)
        {
            List<ColonisationCandidate> l;
            if (!_byMaterial.TryGetValue(subTypeId, out l))
            {
                l = new List<ColonisationCandidate>();
                _byMaterial[subTypeId] = l;
            }
            // Share belongs to the (region, material) pair, and ColonisationCandidate carries one
            // Share -- so a region yielding two materials needs one entry per material.
            l.Add(new ColonisationCandidate
            {
                Region = region.Region,
                Geometry = region.Geometry,
                SeparationKm = region.SeparationKm,
                DistanceKm = region.DistanceKm,
                Share = share,
                OccupiedNodes = 0,
                Occupancy = region,   // occupancy lives on the shared region entry
            });
        }

        private double DistanceFrom(Vec3 from, ColonisationCandidate c)
        {
            Vec3 centre;
            if (c == null || c.Region == null || !_centreByRegion.TryGetValue(KeyOf(c.Region), out centre))
                return double.MaxValue;
            return from.DistanceTo(centre);
        }

        private static string KeyOf(PlacementRegion r)
        {
            return r == null ? "" : ((int)r.Kind).ToString() + ":" + (r.Key ?? "");
        }

        private static double SeparationFallback(FactionTuning tuning, NodeZone zone)
        {
            return tuning != null ? tuning.NodeSeparationFor(zone) : 0;
        }

        /// <summary>A feature's share of each material it carries.
        /// <para><b>An approximation, and only ever used for ranking.</b> It takes the largest share a
        /// material reaches in any one band rather than a volume-weighted average across the whole
        /// region — enough to say "the ring is a platinum source and Tellus is not", which is the
        /// question. It is not a yield prediction and must not be used as one.</para></summary>
        private static IEnumerable<KeyValuePair<string, double>> SharesOf(WorldFeature f)
        {
            var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Accumulate(best, f.Materials);
            if (f.Bands != null)
                foreach (FeatureBand b in f.Bands)
                    if (b != null) Accumulate(best, f.MaterialsAt(b));
            return best;
        }

        private static void Accumulate(Dictionary<string, double> best, List<FeatureMaterial> materials)
        {
            if (materials == null) return;
            foreach (FeatureMaterial m in materials)
            {
                if (m == null || string.IsNullOrEmpty(m.SubTypeId) || m.Share <= 0) continue;
                double existing;
                if (!best.TryGetValue(m.SubTypeId, out existing) || m.Share > existing)
                    best[m.SubTypeId] = m.Share;
            }
        }
    }
}
