using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One ring around a gas giant, as Real Gas Giants configures it.</summary>
    public sealed class GasGiantRingSpec
    {
        /// <summary>Ring visual preset — <c>Default1</c>, <c>Default2</c>, … Also selects the band
        /// pattern RGG draws.</summary>
        public string Skin = "Default1";

        public Vec3 Normal = new Vec3(0, 1, 0);
        public Vec3 Color = new Vec3(255, 210, 190);

        /// <summary>Ring radii as multiples of the giant's own <see cref="GasGiantSpec.RadiusKm"/> —
        /// <b>both</b> of them, which is this recipe's convention and not RGG's. A 180 km body with
        /// scales 1.0 and 2.5 gives a 180–450 km ring. What the mod is handed is
        /// <see cref="RggInnerScale"/> / <see cref="RggOuterScale"/>, which is a different pair.</summary>
        public double InnerScale = 1.0;
        public double OuterScale = 2.5;

        /// <summary>Ring radii in km outright, overriding <see cref="InnerScale"/> and
        /// <see cref="OuterScale"/>. Null keeps the scales.
        /// <para>Here because a ring that has been moved to clear something — Cauldron's 195 km inner
        /// edge, which sits outside its 191 km atmosphere — is not a round multiple of the body, and
        /// 195/180 does not round-trip through a double. Real Gas Giants is still handed scales,
        /// derived back from these edges in its own convention, so the drawn ring and the mined one
        /// cannot part.</para></summary>
        public double? InnerRadiusKm;
        public double? OuterRadiusKm;

        public double LayerSpacingScale = 1.0;
        public double RotationPeriodSeconds = 7200.0;
        public double LightMult = 0.7;
        public double ShadowMult = 0.5;
        public bool ShadowOnRing = true;
        public bool ConstrainNearbyAsteroidsToRing;
        public bool EnabledDraw = true;
        public bool EnabledParticle = true;

        /// <summary>RGG's own collector field — what a grid parked in the ring harvests.</summary>
        public bool CollectResources = true;
        public string CollectResourceSubTypeId = "Ice";
        public int CollectResourceAmount = 100;

        /// <summary>
        /// Half-thickness of the matching <b>world feature</b>, in km. RGG has no thickness setting —
        /// its ring is a drawn disc — so this is ours, and it is what makes the ring a samplable
        /// volume for ore geography rather than an infinitely thin plane.
        /// </summary>
        public double FeatureHalfThicknessKm = 3.6;

        /// <summary>
        /// Sub-band boundaries as fractions of the ring's span, in pairs, matching the band pattern
        /// the chosen <see cref="Skin"/> draws. Empty authors a solid ring.
        ///
        /// <para>These are what turns a drawn ring into a feature with gaps: band inner =
        /// <c>inner + f × (outer − inner)</c>. <c>GasGiantRingType_Default2</c>'s eighteen fractions
        /// reproduce Cauldron's nine bands exactly.</para>
        /// </summary>
        public List<double> BandFractions = new List<double>();

        /// <summary>What each band is made of, one entry per pair in <see cref="BandFractions"/>, in
        /// the same order. Null, or an entry with null <see cref="RingBandOre.Materials"/>, inherits
        /// the ring's own <see cref="Materials"/>.
        ///
        /// <para>Separate from the fractions rather than merged into them because the two have
        /// different authors: the fractions are the skin's drawn pattern and are not ours to choose,
        /// while the ore is the whole design. Keeping them apart is also what lets the geography
        /// follow a ring that moves — the fractions rescale, and the ore rides along.</para>
        ///
        /// <para>Positional, so a mismatched count is a plan-time error rather than a silent shift of
        /// every band's ore by one.</para></summary>
        public List<RingBandOre> BandOre;

        /// <summary>What the ring is made of, for ore geography. Independent of
        /// <see cref="CollectResourceSubTypeId"/>, which is RGG's own collector.
        /// <para>Null is no ore opinion, an empty list is an authored barren ring — see
        /// <see cref="WorldFeature.Materials"/>.</para></summary>
        public List<FeatureMaterial> Materials;

        /// <summary>How densely the ring is packed with physical rock, 0..1. RGG's drawn ring is
        /// billboards, not voxels, so this is the only thing that puts anything in it to mine.
        /// See <see cref="WorldFeature.Density"/>.</summary>
        public double Density;

        /// <summary>The <see cref="NodeZone"/> nodes in this ring belong to. Null defers to gravity,
        /// which cuts a ring in half wherever the body's well happens to end — see
        /// <see cref="WorldFeature.Zone"/>.</summary>
        public NodeZone? Zone;

        /// <summary>Minimum km between two nodes in this ring. Absent falls back to the zone floor.
        /// See <see cref="WorldFeature.NodeSeparationKm"/>.</summary>
        public double? NodeSeparationKm;

        /// <summary>True when a respawn ship arriving in space may be placed in this ring. See
        /// <see cref="WorldFeature.SpaceRespawn"/>.</summary>
        public bool SpaceRespawn;

        /// <summary>The ring's inner edge in km: the authored radius, or the scale off the body.</summary>
        public double FeatureInnerKm(double bodyRadiusKm)
        {
            return InnerRadiusKm.HasValue ? InnerRadiusKm.Value : bodyRadiusKm * InnerScale;
        }

        /// <summary>The ring's outer edge in km: the authored radius, or the scale off the body.</summary>
        public double FeatureOuterKm(double bodyRadiusKm)
        {
            return OuterRadiusKm.HasValue ? OuterRadiusKm.Value : bodyRadiusKm * OuterScale;
        }

        /// <summary>What Real Gas Giants is told for the inner edge, derived from the same edge the
        /// feature uses so the drawn ring and the mined one describe one annulus. RGG multiplies this
        /// by the body radius: <c>radiusRingInner = radiusPlanet * RingInnerScale</c>.</summary>
        public double RggInnerScale(double bodyRadiusKm)
        {
            return bodyRadiusKm > 0 ? FeatureInnerKm(bodyRadiusKm) / bodyRadiusKm : InnerScale;
        }

        /// <summary>What Real Gas Giants is told for the outer edge. See <see cref="RggInnerScale"/>.
        /// <para>Measured against the ring's INNER edge, not the body: RGG computes
        /// <c>radiusRingOuter = radiusRingInner * RingOuterScale</c>, and reads its skin's band
        /// fractions over <c>1 -> RingOuterScale</c> in units of the inner radius. The two conventions
        /// agree only while <see cref="InnerScale"/> is exactly 1, which is why moving a ring outward
        /// carries its outer edge further than asked.</para></summary>
        public double RggOuterScale(double bodyRadiusKm)
        {
            double inner = FeatureInnerKm(bodyRadiusKm);
            return inner > 0 ? FeatureOuterKm(bodyRadiusKm) / inner : OuterScale;
        }
    }

    /// <summary>Ore for one band of a ring, positionally matched to a pair in
    /// <see cref="GasGiantRingSpec.BandFractions"/>. The geometry comes from the fractions; this is
    /// only what fills it.</summary>
    public sealed class RingBandOre
    {
        /// <summary>Packing for this band alone, 0..1. Null inherits the ring's.</summary>
        public double? Density;

        /// <summary>Composition of this band alone. Null inherits the ring's; an empty list is an
        /// authored barren band — the same distinction <see cref="FeatureBand.Materials"/> makes.</summary>
        public List<FeatureMaterial> Materials;
    }

    /// <summary>
    /// Real Gas Giants parameters for one body. RGG does not create planets — it decorates one whose
    /// <c>StorageName</c> matches an entry in its config — so these are both what the builder writes
    /// into that config and what the planner must use instead of the generator definition.
    ///
    /// <para>A gas giant's gravity comes from here, not from
    /// the generator definition: the voxel core is 9.5 km while the body
    /// players experience is a 180 km virtual sphere.</para>
    /// </summary>
    public sealed class GasGiantSpec
    {
        /// <summary>The virtual body's radius in km — what gravity and the rings are measured from,
        /// and nothing to do with the voxel planet's own size.</summary>
        public double RadiusKm = 180.0;

        public double GravityStrength = 2.0;
        public double GravityFalloff = 7.0;

        public string Skin = "Default1";
        public Vec3 Color = new Vec3(255, 210, 190);
        public double OxygenDensity;
        public double DayLengthSeconds = 7200.0;
        public bool SpinCounterClockwise = true;

        public bool AsteroidRemoval = true;
        public bool PressureDamagePlayers = true;
        public bool PressureDamageGrids = true;

        public bool CollectPlanetResources = true;
        public string CollectResourceUpperSubTypeId = "Ice";
        public int CollectResourceUpperAmount = 1000;
        public string CollectResourceLowerSubTypeId = "Ice";
        public int CollectResourceLowerAmount = 1000;

        public List<GasGiantRingSpec> Rings = new List<GasGiantRingSpec>();

        /// <summary>Where this body's natural gravity reaches, in km — RGG's numbers, on SE's own
        /// 0.05 g cutoff. Cauldron: 180 × (2 / 0.05)^(1/7) = 305 km, matching the live world.</summary>
        public double GravityLimitKm()
        {
            if (GravityFalloff <= 0 || RadiusKm <= 0) return Math.Max(0, RadiusKm);
            double strength = GravityStrength <= 0 ? 0 : GravityStrength;
            return RadiusKm * Math.Pow(strength / PlanetTypeSpec.GravityLimitStrength, 1.0 / GravityFalloff);
        }

        /// <summary>How far the widest ring reaches, in km. Zero when there are none.</summary>
        public double RingReachKm()
        {
            double reach = 0;
            if (Rings == null) return reach;
            foreach (GasGiantRingSpec r in Rings)
                if (r != null) reach = Math.Max(reach, r.FeatureOuterKm(RadiusKm));
            return reach;
        }

        public bool Validate(out string why)
        {
            if (RadiusKm <= 0) { why = "GasGiant.RadiusKm must be greater than zero."; return false; }
            if (GravityFalloff <= 0) { why = "GasGiant.GravityFalloff must be greater than zero."; return false; }
            if (Rings != null)
                foreach (GasGiantRingSpec r in Rings)
                {
                    if (r == null) { why = "GasGiant has a null ring."; return false; }
                    if (r.FeatureOuterKm(RadiusKm) <= r.FeatureInnerKm(RadiusKm))
                    { why = "a ring needs an outer edge beyond its inner one."; return false; }
                    if (r.FeatureInnerKm(RadiusKm) < 0) { why = "a ring's inner edge cannot be negative."; return false; }
                    if (r.FeatureHalfThicknessKm <= 0) { why = "a ring needs FeatureHalfThicknessKm greater than zero."; return false; }
                    if (r.BandFractions != null && r.BandFractions.Count % 2 != 0)
                    { why = "a ring's BandFractions must come in pairs."; return false; }
                    // Positional, so a miscount would silently shift every band's ore by one.
                    if (r.BandOre != null && r.BandFractions != null && r.BandOre.Count != r.BandFractions.Count / 2)
                    {
                        why = "a ring's BandOre must have one entry per BandFractions pair ("
                            + (r.BandFractions.Count / 2) + " expected, " + r.BandOre.Count + " given).";
                        return false;
                    }
                }
            why = null;
            return true;
        }
    }
}
