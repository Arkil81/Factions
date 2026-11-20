using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// <see cref="IOreGeography"/> over authored <see cref="WorldFeature"/>s — the deep-space half of
    /// the model, and SE-free because a feature is geometry over authored data, not a world read.
    ///
    /// <para>A position inside no feature is <b>not samplable</b> rather than barren. That distinction
    /// is what lets the composite fall through to the planet reader, and what makes an empty catalogue
    /// mean "features have nothing to say" instead of "there is no ore anywhere".</para>
    ///
    /// <para>Anchors resolve once, at construction: a ring is defined relative to the body it hangs
    /// off, and re-resolving per query would put a planet lookup inside the node seeding loop.</para>
    ///
    /// <para>A feature that authors no composition at all is <b>transparent</b> here, not barren: it
    /// exists to govern placement (see <see cref="WorldFeaturePlacement"/>) and has no opinion about
    /// ore, so the reader beneath it answers. Only an explicitly empty material list authors
    /// emptiness. Without that distinction a placement-only feature over a planet would blank that
    /// planet's surface ore, since features are composed first — see
    /// <see cref="CompositeOreGeography"/>.</para>
    /// </summary>
    public sealed class WorldFeatureGeography : IOreGeography
    {
        private readonly List<WorldFeature> _features = new List<WorldFeature>();
        private readonly List<Vec3> _centers = new List<Vec3>();

        /// <summary>Features whose <see cref="WorldFeature.AnchorPlanetId"/> named a body that does not
        /// exist. Kept rather than dropped silently — an authored feature pointing at a missing planet
        /// is an operator error worth reporting, and it lands at its literal <c>Center</c> meanwhile.</summary>
        public readonly List<string> UnresolvedAnchors = new List<string>();

        /// <param name="features">Authored features. Null or empty is legitimate and normal — the
        /// shipped default is a blank catalogue.</param>
        /// <param name="planets">Used once, to resolve anchors. May be null.</param>
        /// <param name="yieldRatios">Bare ore subtype to the <c>MinedOreRatio</c> of the asteroid
        /// materials that carry it — what a feature rock of that ore actually gives up per cubic metre
        /// once it is spawned. An authored feature names an ore, not a material, and SE veins the rock
        /// with whichever materials carry it, so the host averages theirs. Null, or an ore it does not
        /// list, is reported as unknown (0).</param>
        public WorldFeatureGeography(IEnumerable<WorldFeature> features, IPlanetCatalogue planets,
            IDictionary<string, double> yieldRatios = null)
        {
            WorldFeatureAnchors.Resolve(features, planets, _features, _centers, UnresolvedAnchors);
            if (yieldRatios != null)
                foreach (var kv in yieldRatios)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) _yieldRatios[kv.Key] = kv.Value;
        }

        private readonly Dictionary<string, double> _yieldRatios = new Dictionary<string, double>(StringComparer.Ordinal);

        private double YieldRatioOf(string subTypeId)
        {
            double r;
            return _yieldRatios.TryGetValue(subTypeId, out r) ? r : 0.0;   // 0: unknown, see OreObservation
        }

        public int Count { get { return _features.Count; } }

        /// <summary>Points a feature sample reports. Arbitrary but fixed: <see cref="DepositModel"/>
        /// only reads hit counts relative to each other, so the absolute number just needs enough
        /// resolution to represent a small share without rounding it to zero.</summary>
        private const int FeatureSamplePoints = 1000;


        /// <summary>Feature containment, and nothing else: the innermost feature covering the position
        /// decides, exactly as it does in <see cref="TrySample"/>, so a placement loop and a real sample
        /// can never disagree about which feature owns a point.
        /// <para>A feature with no authored composition is transparent here, not empty — the same rule
        /// <see cref="TrySample"/> follows, and for the same reason: a placement-only region must not
        /// blank the body underneath it.</para></summary>
        public OreProspect CouldBearOre(Vec3 posKm)
        {
            int best = -1;
            double bestExtent = double.MaxValue;
            FeatureBand bestBand = null;
            for (int i = 0; i < _features.Count; i++)
            {
                FeatureBand band;
                if (!_features[i].TryResolve(posKm, _centers[i], out band)) continue;
                if (_features[i].MaterialsAt(band) == null) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; bestBand = band; }
            }
            if (best < 0) return OreProspect.Unknown;

            List<FeatureMaterial> materials = _features[best].MaterialsAt(bestBand);
            if (materials != null)
                for (int i = 0; i < materials.Count; i++)
                    if (materials[i] != null && materials[i].Share > 0) return OreProspect.Possible;

            // An explicitly empty material list authors emptiness, which is an answer -- not a
            // fall-through to the reader beneath.
            return OreProspect.None;
        }

        public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
        {
            sample = null;

            // Innermost containing feature wins, so a small rich pocket authored inside a broad belt
            // reads as the pocket rather than being averaged away by its surroundings.
            int best = -1;
            double bestExtent = double.MaxValue;
            FeatureBand bestBand = null;
            for (int i = 0; i < _features.Count; i++)
            {
                FeatureBand band;
                if (!_features[i].TryResolve(posKm, _centers[i], out band)) continue;
                // A feature with no composition anywhere is skipped rather than allowed to win and
                // then decline: otherwise a placement-only region authored inside an ore-bearing one
                // would shadow it, which is the very thing declining is meant to avoid.
                if (_features[i].MaterialsAt(band) == null) continue;
                double extent = _features[i].OuterRadiusKm;
                if (best < 0 || extent < bestExtent) { best = i; bestExtent = extent; bestBand = band; }
            }
            if (best < 0) return false;

            WorldFeature f = _features[best];
            // The band the point actually fell in decides composition, falling back to the feature's
            // own. A ring whose bands differ in composition is the ordinary case, not an exotic one.
            List<FeatureMaterial> materials = f.MaterialsAt(bestBand);
            var result = new OreSample { PointsExamined = FeatureSamplePoints };

            double shareSum = 0;
            if (materials != null)
                for (int i = 0; i < materials.Count; i++)
                    if (materials[i] != null && materials[i].Share > 0) shareSum += materials[i].Share;

            // An empty (not null) material list is a real, authored statement: this region is empty.
            // It reports barren rather than declining, so it still shadows whatever lies beneath it.
            // A null list never reaches here -- that feature was skipped as having no ore opinion.
            if (shareSum <= 0) { sample = result; return true; }

            for (int i = 0; i < materials.Count; i++)
            {
                FeatureMaterial m = materials[i];
                if (m == null || string.IsNullOrEmpty(m.SubTypeId) || m.Share <= 0) continue;

                int hits = (int)Math.Round(FeatureSamplePoints * (m.Share / shareSum));
                if (hits <= 0) continue;
                result.Ores.Add(new OreObservation(m.SubTypeId, hits, m.BandDepthM, m.OverburdenM,
                    YieldRatioOf(m.SubTypeId), OreSource.Band));
            }

            sample = result;
            return true;
        }

        /// <summary>A feature's composition is <b>authored</b>, not measured, so there is nothing for
        /// a survey to refine: the answer is already exact. This reports the same composition against
        /// the requested point budget so an accumulating deposit converges on it rather than stalling
        /// below its target forever.</summary>
        public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
        {
            if (!TrySample(posKm, radiusKm, out sample)) return false;
            if (sample == null || count <= 0) return sample != null;

            // Rescale the authored mix onto the requested budget; shares are what matter and they
            // are unchanged by the scaling.
            double factor = (double)count / Math.Max(1, sample.PointsExamined);
            for (int i = 0; i < sample.Ores.Count; i++)
            {
                OreObservation o = sample.Ores[i];
                int hits = (int)Math.Round(o.Hits * factor);
                sample.Ores[i] = new OreObservation(o.SubTypeId, hits < 1 ? 1 : hits, o.BandDepthM, o.OverburdenM,
                    o.YieldRatio, o.Source);
            }
            sample.PointsExamined = count;
            return true;
        }

        public IEnumerable<string> MintableOres
        {
            get
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < _features.Count; i++)
                {
                    // Band overrides included: an ore that exists only in one band of one ring is
                    // still mintable in this world, and omitting it would refuse every recipe
                    // downstream of it.
                    foreach (FeatureMaterial m in _features[i].AllMaterials())
                        if (!string.IsNullOrEmpty(m.SubTypeId) && seen.Add(m.SubTypeId))
                            yield return m.SubTypeId;
                }
            }
        }
    }

    /// <summary>
    /// Composes the two halves of the world's geography.
    ///
    /// <para><b>Features take precedence.</b> A feature is an explicit authored statement and a planet
    /// is a default, so where they overlap the author wins. This is not academic: the Cauldron ring
    /// sits 180-450 km out from a body whose gravity well reaches further than that, so a
    /// planet-first order would let a gas giant carrying no ore shadow the one deep-space deposit
    /// players can reach.</para>
    /// </summary>
    public sealed class CompositeOreGeography : IOreGeography
    {
        private readonly IOreGeography _features;
        private readonly IOreGeography _planets;

        public CompositeOreGeography(IOreGeography features, IOreGeography planets)
        {
            _features = features ?? NullOreGeography.Instance;
            _planets = planets ?? NullOreGeography.Instance;
        }

        /// <summary>Features first, then planets — the same precedence <see cref="TrySample"/> uses, so
        /// a feature that authors emptiness shadows the body under it here exactly as it does there.
        /// <see cref="OreProspect.Unknown"/> from both readers means nothing covers the position at
        /// all: open space, which can never bear ore.</summary>
        public OreProspect CouldBearOre(Vec3 posKm)
        {
            OreProspect fromFeatures = _features.CouldBearOre(posKm);
            if (fromFeatures != OreProspect.Unknown) return fromFeatures;
            return _planets.CouldBearOre(posKm);
        }

        public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
        {
            if (_features.TrySample(posKm, radiusKm, out sample)) return true;
            return _planets.TrySample(posKm, radiusKm, out sample);
        }

        public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
        {
            if (_features.TryResample(posKm, radiusKm, startIndex, count, out sample)) return true;
            return _planets.TryResample(posKm, radiusKm, startIndex, count, out sample);
        }

        public IEnumerable<string> MintableOres
        {
            get
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string o in _features.MintableOres) if (seen.Add(o)) yield return o;
                foreach (string o in _planets.MintableOres) if (seen.Add(o)) yield return o;
            }
        }
    }
}
