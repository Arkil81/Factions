using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Resolves each feature's centre against the body it hangs off. Shared by the two
    /// things that read features — the ore seam and placement — so a ring cannot be in one place for
    /// composition and another for geometry.
    /// <para>Resolution happens once, at construction: re-resolving per query would put a planet
    /// lookup inside the node seeding loop.</para></summary>
    internal static class WorldFeatureAnchors
    {
        public static void Resolve(IEnumerable<WorldFeature> features, IPlanetCatalogue planets,
                                   List<WorldFeature> resolvedFeatures, List<Vec3> resolvedCenters,
                                   List<string> unresolvedAnchors)
        {
            Dictionary<string, Vec3> byId = null;
            if (planets != null)
            {
                byId = new Dictionary<string, Vec3>(StringComparer.Ordinal);
                foreach (PlanetRecord p in planets.Planets)
                    if (p != null && !string.IsNullOrEmpty(p.Id)) byId[p.Id] = p.Center;
            }

            if (features == null) return;
            foreach (WorldFeature f in features)
            {
                if (f == null) continue;
                Vec3 center = f.Center;
                if (!string.IsNullOrEmpty(f.AnchorPlanetId))
                {
                    Vec3 anchored;
                    if (byId != null && byId.TryGetValue(f.AnchorPlanetId, out anchored)) center = anchored;
                    else unresolvedAnchors.Add((f.Name ?? "(unnamed)") + " -> " + f.AnchorPlanetId);
                }
                resolvedFeatures.Add(f);
                resolvedCenters.Add(center);
            }
        }
    }

    /// <summary>
    /// Where the authored world says a node in its regions belongs — the placement half of the
    /// feature model, and the counterpart to <see cref="WorldFeatureGeography"/>'s composition half.
    ///
    /// <para><b>Why this exists.</b> Placement used to be a three-bucket gravity heuristic
    /// (<see cref="NodeZone"/>) that labelled a node and repositioned it in the same pass. On a body
    /// whose atmosphere reaches twenty times its voxel radius that heuristic reads a 190 km shell as
    /// surface, and the reposition then drags a node authored into a ring 180 km out down onto a
    /// 9.5 km core. An authored region is a statement about the world; a gravity sample is a guess
    /// about it, so where the two disagree inside an authored region, the author wins. See
    /// docs/issues/FAC-096.</para>
    ///
    /// <para>This class only answers where a claimed point goes. Whether the feature or the planet
    /// gets to place a node at all is <see cref="WorldModel"/>'s call: the feature outranks the orbit
    /// snap outright and competes with the surface snap by displacement — see
    /// <c>WorldModel.PlaceNode</c>.</para>
    /// </summary>
    public sealed class WorldFeaturePlacement
    {
        private readonly List<WorldFeature> _features = new List<WorldFeature>();
        private readonly List<Vec3> _centers = new List<Vec3>();

        /// <summary>Features whose <see cref="WorldFeature.AnchorPlanetId"/> named a body that does
        /// not exist, in the same form <see cref="WorldFeatureGeography.UnresolvedAnchors"/> reports.</summary>
        public readonly List<string> UnresolvedAnchors = new List<string>();

        /// <summary>An unauthored world: nothing is claimed, so every planet rule applies unchanged.
        /// This is what <see cref="WorldModel"/> starts with, which is why the whole model is additive
        /// per world rather than a change to worlds nobody has authored features for.</summary>
        public static readonly WorldFeaturePlacement Empty = new WorldFeaturePlacement(null, null);

        public WorldFeaturePlacement(IEnumerable<WorldFeature> features, IPlanetCatalogue planets)
        {
            WorldFeatureAnchors.Resolve(features, planets, _features, _centers, UnresolvedAnchors);
        }

        public int Count { get { return _features.Count; } }

        /// <summary>Where the authored world puts a node at <paramref name="posKm"/>, or false when
        /// no feature claims that point.
        /// <para>The innermost claiming feature wins — the same tie-break the ore seam uses — so a
        /// small region authored inside a broad one governs its own interior.</para></summary>
        public bool TryPlace(Vec3 posKm, out Vec3 placedKm)
        {
            placedKm = posKm;
            int best = FindClaimant(posKm);
            if (best < 0) return false;
            placedKm = _features[best].NearestPointInside(posKm, _centers[best]);
            return true;
        }

        /// <summary>The <see cref="NodeZone"/> the authored world assigns at <paramref name="posKm"/>,
        /// or false when no feature claiming that point authors one.
        /// <para>The innermost claiming feature wins, the same tie-break placement and the ore seam
        /// use — but only among features that actually author a zone, so a broad region's opinion is
        /// not silently cancelled by a small placement-only one inside it that has none.</para></summary>
        public bool TryZoneAt(Vec3 posKm, out NodeZone zone)
        {
            zone = NodeZone.DeepSpace;
            int best = -1;
            double bestExtent = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].Zone.HasValue) continue;
                if (!_features[i].ClaimsForPlacement(posKm, _centers[i])) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; }
            }
            if (best < 0) return false;
            zone = _features[best].Zone.Value;
            return true;
        }

        /// <summary>The <see cref="NodeZone"/> the authored world assigns at <paramref name="posKm"/>
        /// by <b>containment</b>, or false when no feature actually containing that point authors one.
        ///
        /// <para>The counterpart to <see cref="TryZoneAt"/>, and the one a question about a point in
        /// transit must ask. <see cref="TryZoneAt"/> goes through
        /// <see cref="WorldFeature.ClaimsForPlacement"/>, which tests only the outer sphere and the
        /// inner radius — deliberately, so a node authored near a ring is pulled into it rather than
        /// left beside it. Applied to a path that would make the whole sphere out to
        /// <see cref="WorldFeature.OuterRadiusKm"/> read as ring, including the empty space far off
        /// its plane. <see cref="WorldFeature.Contains"/> enforces
        /// <see cref="WorldFeature.HalfThicknessKm"/>, so a path high over a ring's pole is the open
        /// space it flies through.</para></summary>
        public bool TryZoneContaining(Vec3 posKm, out NodeZone zone)
        {
            zone = NodeZone.DeepSpace;
            int best = -1;
            double bestExtent = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].Zone.HasValue) continue;
                if (!_features[i].Contains(posKm, _centers[i])) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; }
            }
            if (best < 0) return false;
            zone = _features[best].Zone.Value;
            return true;
        }

        /// <summary>True when any zone-authoring feature's outer sphere comes within reach of the
        /// segment a-b, so a path along it has to be sampled. False means no authored zone can
        /// possibly be crossed, which is the cheap answer for the great majority of legs.
        ///
        /// <para>Conservative in the one direction that matters: the test is against the outer
        /// sphere rather than the shape, so a path that grazes a ring's bounding sphere without
        /// entering the ring is sampled and found to be open space. A path this returns false for
        /// cannot be in a feature at any point along it.</para></summary>
        public bool MightClaimZone(Vec3 aKm, Vec3 bKm)
        {
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].Zone.HasValue) continue;
                double radiusKm = _features[i].OuterRadiusKm;
                if (radiusKm <= 0) continue;
                if (SegmentDistanceKm(aKm, bKm, _centers[i]) < radiusKm) return true;
            }
            return false;
        }

        /// <summary>Shortest distance from <paramref name="centre"/> to the segment a-b, in km.</summary>
        private static double SegmentDistanceKm(Vec3 a, Vec3 b, Vec3 centre)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double lengthSq = abx * abx + aby * aby + abz * abz;
            double acx = centre.X - a.X, acy = centre.Y - a.Y, acz = centre.Z - a.Z;
            if (lengthSq <= 1e-12) return Math.Sqrt(acx * acx + acy * acy + acz * acz);

            double t = (acx * abx + acy * aby + acz * abz) / lengthSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = acx - abx * t, dy = acy - aby * t, dz = acz - abz * t;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>The minimum node separation the authored world sets at <paramref name="posKm"/>,
        /// or false when no feature claiming that point authors one. Innermost claiming feature wins,
        /// counting only features that actually author a separation.</summary>
        public bool TrySeparationAt(Vec3 posKm, out double separationKm)
        {
            separationKm = 0;
            int best = -1;
            double bestExtent = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].NodeSeparationKm.HasValue) continue;
                if (!_features[i].ClaimsForPlacement(posKm, _centers[i])) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; }
            }
            if (best < 0) return false;
            separationKm = _features[best].NodeSeparationKm.Value;
            return true;
        }

        /// <summary>The largest separation any feature authors, or 0 when none do. Bounds the
        /// spatial bucket <see cref="WorldModel.DensifyTopology"/> sizes its scan with — a bucket
        /// smaller than the reach it is searching would silently miss pairs.</summary>
        public double MaxAuthoredSeparationKm()
        {
            double max = 0;
            for (int i = 0; i < _features.Count; i++)
            {
                double? sep = _features[i].NodeSeparationKm;
                if (sep.HasValue && sep.Value > max) max = sep.Value;
            }
            return max;
        }

        /// <summary>The furthest any feature claiming <paramref name="posKm"/> could have moved a
        /// node to get there. 0 when nothing claims it.</summary>
        public double MaxPlacementDisplacementKm(Vec3 posKm)
        {
            double max = 0;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].ClaimsForPlacement(posKm, _centers[i])) continue;
                double d = _features[i].MaxPlacementDisplacementKm();
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>The name of the feature that claims <paramref name="posKm"/>, or false when none
        /// does. Innermost claimant wins, exactly as <see cref="TryPlace"/> decides — so the feature a
        /// node is placed by and the feature it is recorded as standing in are always the same one.
        /// This is the one lookup behind <see cref="SpatialNode.FeatureKey"/>.</summary>
        public bool TryFeatureAt(Vec3 posKm, out string name)
        {
            name = null;
            int best = FindClaimant(posKm);
            if (best < 0) return false;
            name = _features[best].Name;
            return !string.IsNullOrEmpty(name);
        }

        /// <summary>True when any feature authors <see cref="WorldFeature.SpaceRespawn"/>. False keeps
        /// space spawns unrestricted, so the rule is additive per world.</summary>
        public bool HasSpaceRespawnRegion
        {
            get
            {
                for (int i = 0; i < _features.Count; i++)
                    if (_features[i].SpaceRespawn) return true;
                return false;
            }
        }

        /// <summary>Whether the feature a node at <paramref name="posKm"/> stands in admits a space
        /// respawn. Decided by the placement claim, the same innermost claimant
        /// <see cref="TryFeatureAt"/> records as the node's feature, so a station counts as in the
        /// region it was placed by.</summary>
        public bool AllowsSpaceRespawnAt(Vec3 posKm)
        {
            int best = FindClaimant(posKm);
            return best >= 0 && _features[best].SpaceRespawn;
        }

        /// <summary>Whether <paramref name="posKm"/> lies inside a region that admits a space respawn,
        /// by containment: the innermost feature actually containing the point decides. The question
        /// for an arbitrary point — a faction mate, or a candidate landing spot — where the claim ball
        /// would read empty space far off a ring's plane as ring.</summary>
        public bool AllowsSpaceRespawnContaining(Vec3 posKm)
        {
            int best = -1;
            double bestExtent = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].Contains(posKm, _centers[i])) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; }
            }
            return best >= 0 && _features[best].SpaceRespawn;
        }

        /// <summary>The nearest point to <paramref name="fromKm"/> inside any region that admits a
        /// space respawn, or false when none is authored. A point already inside one comes back
        /// unchanged.</summary>
        public bool TryNearestSpaceRespawnPoint(Vec3 fromKm, out Vec3 pointKm)
        {
            pointKm = fromKm;
            bool found = false;
            double bestDistSq = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].SpaceRespawn) continue;
                Vec3 p = _features[i].NearestPointInside(fromKm, _centers[i]);
                double d = fromKm.DistanceSquaredTo(p);
                if (d >= bestDistSq) continue;
                bestDistSq = d;
                pointKm = p;
                found = true;
            }
            return found;
        }

        private int FindClaimant(Vec3 posKm)
        {
            int best = -1;
            double bestExtent = double.MaxValue;
            for (int i = 0; i < _features.Count; i++)
            {
                if (!_features[i].ClaimsForPlacement(posKm, _centers[i])) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; }
            }
            return best;
        }
    }
}
