using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>Geometry of a world feature. Deliberately more than a centre and a radius: a ring is
    /// a torus bound to a body, and a scalar radius would have to be thrown away the moment one is
    /// expressed.</summary>
    public enum WorldFeatureShape
    {
        /// <summary>A ball around <see cref="WorldFeature.Center"/> of <see cref="WorldFeature.OuterRadiusKm"/>.</summary>
        Sphere = 0,
        /// <summary>A flat annulus in the plane whose normal is <see cref="WorldFeature.Normal"/>,
        /// between inner and outer radius and within <see cref="WorldFeature.HalfThicknessKm"/> of
        /// that plane. Optionally subdivided into concentric bands with gaps between them.</summary>
        Ring = 1,
        /// <summary>A spherical shell between inner and outer radius — a cloud around a body rather
        /// than a disc.</summary>
        Shell = 2,
    }

    /// <summary>How large the physical objects spawned for one material are, in metres. Read as the
    /// <c>size</c> a voxel asteroid is generated at, so it is a diameter-ish extent rather than a
    /// radius.
    /// <para>Per material rather than per feature because size is a property of what the rock is: ice
    /// chunks and metal-rich bodies are not the same size, and a band holding both should show
    /// both.</para></summary>
    public sealed class SizeRange
    {
        public double MinM = 64.0;
        public double MaxM = 256.0;

        public SizeRange() { }
        public SizeRange(double minM, double maxM) { MinM = minM; MaxM = maxM; }

        /// <summary>What an unauthored <see cref="FeatureMaterial.SizeRange"/> means. Matches the
        /// incumbent <c>AsteroidField</c> profile, so a feature that says nothing
        /// about size produces rock the same scale as a node's field already does.</summary>
        public static SizeRange Default { get { return new SizeRange(64.0, 256.0); } }
    }

    /// <summary>One material a feature is made of, how thick its deposits run, and how large the
    /// objects made of it are. Shares across a feature need not sum to 1; they are relative.</summary>
    public sealed class FeatureMaterial
    {
        /// <summary>Bare SE subtype, unprefixed — "Iron", "Ice", and gases once those are minted.</summary>
        public string SubTypeId;
        public double Share = 1.0;
        /// <summary>Deposit thickness in metres, the same quantity a planet's ore band carries. Feeds
        /// richness through <see cref="DepositModel"/>, so a feature and a planet are scored on one
        /// scale.</summary>
        public double BandDepthM = 10.0;
        /// <summary>Metres of waste above the deposit. Carried for parity with planet bands; not yet
        /// consumed — see docs/ore-geography.md on overburden.</summary>
        public double OverburdenM;

        /// <summary>Size of the individual objects made of this material. Null means
        /// <see cref="SizeRange.Default"/> — absence is inheritance, not zero, because a zero-size
        /// asteroid is not a thing an author would ever mean.</summary>
        public SizeRange SizeRange;

        /// <summary>
        /// How many ore pockets a rock of this material carries, as a multiplier on SE's own count.
        /// Null inherits the world's <c>DepositsCountCoefficient</c>.
        ///
        /// <para><b>Deliberately SE's own knob, named as SE names it.</b> On generator 4 a rock's
        /// pocket count is <c>DepositsCountCoefficient x n</c>, where n is 1..5 by size — so at the
        /// stock value of 1 a 256 m rock gets three pockets and measures ~99% stone. That is correct
        /// for an asteroid rather than a defect, but it was not something an author could say
        /// anything about: a rich seam and a barren rock were the same rock. Raising this is the
        /// direct lever.</para>
        ///
        /// <para>Applied by scoping SE's session setting around this rock's generation, so it varies
        /// per material rather than per world. Like every other composition input it must be applied
        /// identically on the client, or the two ends disagree about deposit volume exactly as they
        /// once did about identity — see docs/ore-geography.md.</para>
        /// </summary>
        public double? DepositCountCoefficient;

        /// <summary>
        /// How large each pocket is, as SE's own <c>DepositSizeDenominator</c>. Null inherits the
        /// world's value (60 on this world).
        ///
        /// <para><b>Smaller means bigger deposits</b>, because SE computes the pocket radius as
        /// <c>size / denominator + 8</c>. Inverted and unintuitive, and named as SE names it anyway:
        /// an author who reads SE's own world settings should find the same word meaning the same
        /// thing, and a friendlier name here would be a second vocabulary to keep in step.</para>
        /// </summary>
        public double? DepositSizeDenominator;

        /// <summary>This material's size range, or the default when unauthored.</summary>
        public SizeRange ResolvedSizeRange()
        {
            return SizeRange ?? Factions.Core.SizeRange.Default;
        }
    }

    /// <summary>One concentric band of a <see cref="WorldFeatureShape.Ring"/>, in km from the centre.
    /// A ring with no bands is solid between its inner and outer radius.
    /// <para>A band may override the feature's <see cref="Density"/> and <see cref="Materials"/>: real
    /// rings are not uniform, and the alternative — one feature per band — would repeat the whole
    /// geometry nine times to vary one number.</para></summary>
    public sealed class FeatureBand
    {
        public double InnerRadiusKm;
        public double OuterRadiusKm;

        /// <summary>Packing for this band alone, 0..1. Null inherits the feature's.</summary>
        public double? Density;

        /// <summary>Composition of this band alone. Null inherits the feature's; an <b>empty list is
        /// an authored statement</b> that this band is barren, exactly as it is on a feature. That
        /// distinction is why this defaults to null rather than to an empty list.</summary>
        public List<FeatureMaterial> Materials;
    }

    /// <summary>
    /// A named region of deep space that contains material. **Features are the only places deep-space
    /// minerals exist: no feature, no ore.**
    ///
    /// <para>Authored, never rolled. Planets are read from SE because SE already decided what is in
    /// them; deep space has no such source of truth, so we are it. Note the symmetry — on a planet the
    /// world is authored by SE and Factions reads it; in space Factions authors it and SE's content
    /// conforms. Same principle, opposite ends. See docs/ore-geography.md.</para>
    /// </summary>
    public sealed class WorldFeature
    {
        public string Name;
        public WorldFeatureShape Shape = WorldFeatureShape.Sphere;

        /// <summary>Centre in km. Ignored when <see cref="AnchorPlanetId"/> resolves — a ring is
        /// defined relative to the body it hangs off, not by an absolute position that silently rots
        /// if the body moves or the world is regenerated.</summary>
        public Vec3 Center;

        /// <summary><see cref="PlanetRecord.Id"/> this feature is centred on, when it hangs off a
        /// body. Null for a free-floating feature.</summary>
        public string AnchorPlanetId;

        /// <summary>Disc normal for <see cref="WorldFeatureShape.Ring"/>. Normalised on resolve;
        /// a zero vector falls back to +Y.</summary>
        public Vec3 Normal = new Vec3(0, 1, 0);

        public double InnerRadiusKm;
        public double OuterRadiusKm;
        /// <summary>Half the ring's vertical extent, in km.</summary>
        public double HalfThicknessKm;

        /// <summary>
        /// How densely this feature is packed with physical objects, 0..1 — <b>the same 0..1 range as
        /// SE's own <c>ProceduralDensity</c> slider, deliberately.</b> An author coming from vanilla
        /// reaches for the same kind of number in the same span, which is what the parity is for. Do
        /// not "improve" this into an absolute per-volume figure.
        ///
        /// <para><b>The range matches; the absolute scale need not.</b> Vanilla generator 4 places
        /// one candidate per <c>SubcellSize</c>³ = 6.144 km, i.e. 4.31 objects per 1000 km³ at its own
        /// density 1.0 (~8.6 counting clusters). <see cref="FactionTuning.FeatureMaxDensityPer1000Km3"/>
        /// is free to sit above that, and does — a ring is meant to read as a belt rather than as
        /// vanilla's scattered debris. So 0.35 here is 35% of <i>this world's</i> ceiling, not a claim
        /// that it reproduces vanilla's count at 0.35.</para>
        ///
        /// <para>The ceiling it scales against is
        /// <see cref="FactionTuning.FeatureMaxDensityPer1000Km3"/>, so the whole world retunes from one
        /// key rather than by re-authoring every feature.</para>
        ///
        /// <para><b>0 is the default and means no physical objects.</b> A feature at density 0 still
        /// carries composition — it remains a real statement about what minerals are in that region —
        /// so this is additive: every feature authored before this field existed keeps its economic
        /// meaning and gains no rock.</para>
        /// </summary>
        public double Density;

        /// <summary>Minimum km between two nodes inside this region. Null falls back to the zone
        /// floor — 500 km in deep space, which is what makes open space open.
        ///
        /// <para>Authored per region rather than per zone because a zone is three numbers for a whole
        /// world. The Cauldron ring straddles its own gravity limit, so before this it took the 15 km
        /// surface floor across bands 1-3 and the 500 km deep-space floor across bands 5-9, with band
        /// 4 changing rule partway across its own width: 494 sites inside and 0.6 outside, i.e. 50 km
        /// of authored ore-bearing ring that was structurally uninhabitable (FAC-105).</para></summary>
        public double? NodeSeparationKm;

        /// <summary>
        /// The <see cref="NodeZone"/> every node inside this region belongs to. Null defers to
        /// <see cref="WorldModel.ClassifyZone"/>'s gravity reading, so this is additive: an
        /// unauthored feature classifies exactly as it did before the field existed.
        ///
        /// <para><b>Zone's remaining job is separation</b> (<see cref="FactionTuning.NodeSeparationFor"/>),
        /// so authoring it is really authoring how densely a region may be settled. It is spelled as a
        /// zone rather than as a distance so the answer stays consistent for the consumers still
        /// reading the label while they are moved off it — see docs/issues/FAC-096 stage 2.</para>
        ///
        /// <para><b>Why a region has to be able to say this.</b> Gravity cuts an authored feature
        /// wherever a well happens to end. Cauldron's well ends at 304.89 km and its ring spans
        /// 195–465 km, so without this the inner bands are Orbit (30 km apart) and the outer bands
        /// DeepSpace (500 km apart) — one authored feature, two settlement densities, split by a
        /// boundary that has nothing to do with the ring, and the split falls through the middle of a
        /// single band.</para>
        /// </summary>
        public NodeZone? Zone;

        /// <summary>
        /// True when a respawn ship arriving in space may be placed in this region. On a world that
        /// authors it on any feature, a space spawn is only ever set down inside a region that does —
        /// beside a welcoming station there, beside a faction mate there, or at the nearest point of
        /// one — and never in open space or in a region that does not. See
        /// <see cref="SpawnAnchorSelector"/>.
        ///
        /// <para><b>Opt-in, and the world's statement rather than code's.</b> Which regions are safe
        /// for a fresh arrival is a fact about the world: on the genesis world it is Cauldron's ring,
        /// and the Factorum nests are exactly the places it is not. Authored per feature in
        /// <c>world_recipe.json</c> so a world with a different shape says so in its own recipe. A
        /// world that authors it nowhere keeps the unrestricted behaviour.</para>
        /// </summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool SpaceRespawn;

        /// <summary>True when a <see cref="WorldFeatureShape.Shell"/> leaves the ball inside its inner
        /// radius to whatever lies there, as a ring always does. A planet's orbit band sets it: the
        /// band starts where the planet's gravity ends, and the well beneath it is Surface, placed on
        /// the ground -- not pulled out into the band. False keeps a shell's hole, the behaviour every
        /// shell had before this field (see <see cref="ClaimsForPlacement"/>). FAC-537.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool GivesUpHole;

        /// <summary>Concentric sub-annuli with gaps between them. Empty = solid.</summary>
        public List<FeatureBand> Bands = new List<FeatureBand>();

        /// <summary>Composition of this feature. <b>Null and empty mean different things.</b> Null is
        /// no ore opinion — the feature is transparent to the ore seam and whatever lies beneath it
        /// answers instead, which is what lets a feature exist purely to govern placement. An empty
        /// list is an authored statement that the region is barren, and it shadows the planet reader
        /// underneath. Same distinction <see cref="FeatureBand.Materials"/> already draws per band.</summary>
        public List<FeatureMaterial> Materials;

        /// <summary>Density for a band, falling back to the feature's. Null band = the feature.</summary>
        public double DensityAt(FeatureBand band)
        {
            return band != null && band.Density.HasValue ? band.Density.Value : Density;
        }

        /// <summary>Composition for a band, falling back to the feature's. A band with an explicitly
        /// empty (not null) list is barren and returns that empty list rather than inheriting.
        /// <para>Null here — both band and feature unauthored — is "no ore opinion", not "barren":
        /// see <see cref="Materials"/>.</para></summary>
        public List<FeatureMaterial> MaterialsAt(FeatureBand band)
        {
            return band != null && band.Materials != null ? band.Materials : Materials;
        }

        /// <summary>Appends a material, starting the list if this feature had no ore opinion yet.
        /// Going from "no opinion" to "these ores" is what authoring one material means, and doing it
        /// through the field would need every caller to know about the null default.</summary>
        public void AddMaterial(FeatureMaterial material)
        {
            if (material == null) return;
            if (Materials == null) Materials = new List<FeatureMaterial>();
            Materials.Add(material);
        }

        /// <summary>True when this feature says anything at all about composition, anywhere in its
        /// volume. False makes it transparent to the ore seam.</summary>
        public bool HasOreOpinion()
        {
            if (Materials != null) return true;
            if (Bands == null) return false;
            foreach (FeatureBand b in Bands)
                if (b != null && b.Materials != null) return true;
            return false;
        }

        /// <summary>Every material this feature can produce anywhere in its volume — the feature's own
        /// plus each band's override. Used where the question is "what could exist here at all"
        /// rather than "what is at this point".</summary>
        public IEnumerable<FeatureMaterial> AllMaterials()
        {
            if (Materials != null)
                foreach (FeatureMaterial m in Materials)
                    if (m != null) yield return m;

            if (Bands == null) yield break;
            foreach (FeatureBand b in Bands)
            {
                if (b == null || b.Materials == null) continue;
                foreach (FeatureMaterial m in b.Materials)
                    if (m != null) yield return m;
            }
        }

        /// <summary>True when <paramref name="posKm"/> lies inside this feature's volume.
        /// <para><paramref name="resolvedCenter"/> is passed rather than read from
        /// <see cref="Center"/> so anchor resolution happens once per catalogue build instead of once
        /// per query — this runs per candidate feature per node.</para></summary>
        public bool Contains(Vec3 posKm, Vec3 resolvedCenter)
        {
            FeatureBand ignored;
            return TryResolve(posKm, resolvedCenter, out ignored);
        }

        /// <summary>As <see cref="Contains"/>, and reports the band the point fell in.
        /// <paramref name="band"/> is null for a hit on a solid ring, a sphere or a shell — none of
        /// which have bands — so a null band means "use the feature's own density and materials",
        /// not "missed".</summary>
        public bool TryResolve(Vec3 posKm, Vec3 resolvedCenter, out FeatureBand band)
        {
            band = null;
            double dx = posKm.X - resolvedCenter.X;
            double dy = posKm.Y - resolvedCenter.Y;
            double dz = posKm.Z - resolvedCenter.Z;

            switch (Shape)
            {
                case WorldFeatureShape.Sphere:
                {
                    double r2 = dx * dx + dy * dy + dz * dz;
                    return r2 <= OuterRadiusKm * OuterRadiusKm;
                }

                case WorldFeatureShape.Shell:
                {
                    double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    return r >= InnerRadiusKm && r <= OuterRadiusKm;
                }

                case WorldFeatureShape.Ring:
                {
                    Vec3 n = NormalisedNormal();
                    double height = dx * n.X + dy * n.Y + dz * n.Z;
                    if (Math.Abs(height) > HalfThicknessKm) return false;

                    // Distance from the axis, i.e. within the disc's plane.
                    double px = dx - height * n.X;
                    double py = dy - height * n.Y;
                    double pz = dz - height * n.Z;
                    double radial = Math.Sqrt(px * px + py * py + pz * pz);

                    if (radial < InnerRadiusKm || radial > OuterRadiusKm) return false;
                    if (Bands == null || Bands.Count == 0) return true;

                    for (int i = 0; i < Bands.Count; i++)
                    {
                        FeatureBand b = Bands[i];
                        if (b != null && radial >= b.InnerRadiusKm && radial <= b.OuterRadiusKm)
                        {
                            band = b;
                            return true;
                        }
                    }
                    return false; // in the annulus but in a gap between bands
                }
            }
            return false;
        }

        // ---- placement -------------------------------------------------------------------------
        //
        // A feature that describes a region of space also decides where a node in that region sits.
        // The alternative is a gravity heuristic built for ordinary planets deciding it for us, which
        // on a gas giant reads a 190 km atmosphere shell as "surface" and drags a ring node down onto
        // a 9.5 km voxel core. See docs/issues/FAC-096.

        /// <summary>True when this feature is the placement authority for <paramref name="posKm"/>.
        ///
        /// <para>The claim is the feature's own outer radius — the ball it fits in — because that is
        /// the extent the author actually described, and nothing outside it should be pulled in. A
        /// <see cref="WorldFeatureShape.Ring"/> additionally gives up the hole in its middle, so a
        /// node near the body a ring hangs off still belongs to that body rather than being flung out
        /// to the innermost band. A <see cref="WorldFeatureShape.Shell"/> keeps its hole unless it says
        /// <see cref="GivesUpHole"/>: an authored shell around a planet is meant to compete with that
        /// planet's own placement, and <see cref="WorldModel"/> settles the two by displacement
        /// wherever the planet's candidate is its surface. It is the surface snap that competes; the
        /// orbit snap yields to any feature claiming the point — see <c>WorldModel.PlaceNode</c>. A
        /// planet's orbit band gives its hole up, because the well beneath it is the planet's.</para></summary>
        public bool ClaimsForPlacement(Vec3 posKm, Vec3 resolvedCenter)
        {
            double dx = posKm.X - resolvedCenter.X;
            double dy = posKm.Y - resolvedCenter.Y;
            double dz = posKm.Z - resolvedCenter.Z;
            double r2 = dx * dx + dy * dy + dz * dz;
            if (r2 > OuterRadiusKm * OuterRadiusKm) return false;
            if (Shape == WorldFeatureShape.Shell) return !GivesUpHole || r2 >= InnerRadiusKm * InnerRadiusKm;
            if (Shape != WorldFeatureShape.Ring) return true;

            Vec3 n = NormalisedNormal();
            double height = dx * n.X + dy * n.Y + dz * n.Z;
            double px = dx - height * n.X, py = dy - height * n.Y, pz = dz - height * n.Z;
            return px * px + py * py + pz * pz >= InnerRadiusKm * InnerRadiusKm;
        }

        /// <summary>The nearest point inside this feature's authored volume. Returns
        /// <paramref name="posKm"/> unchanged when it is already inside, so a node that is where the
        /// recipe wants it moves zero and stops being re-placed on every boot.
        ///
        /// <para>Per shape: a ring clamps height into <see cref="HalfThicknessKm"/> and radial
        /// distance into the nearest band, preserving azimuth so radial structure survives; a shell
        /// clamps radius into <c>[Inner, Outer]</c>, preserving direction; a sphere clamps radius to
        /// <see cref="OuterRadiusKm"/>.</para>
        ///
        /// <para><b>A point outside lands <see cref="ContainmentInsetKm"/> inside the face, not on
        /// it.</b> The centre a feature is measured from is a live body's position, and that moves:
        /// the gas giant Cauldron's ring hangs off read 0.0015 km on 2026-08-31 and 0.0035 km on
        /// 2026-10-09, which left every node clamped onto the slab face 2 m outside it and its ore
        /// unreadable. A point already inside is still returned unchanged, so the inset costs a node
        /// that is where the recipe wants it nothing. See docs/issues/FAC-213.</para></summary>
        public Vec3 NearestPointInside(Vec3 posKm, Vec3 resolvedCenter)
        {
            double dx = posKm.X - resolvedCenter.X;
            double dy = posKm.Y - resolvedCenter.Y;
            double dz = posKm.Z - resolvedCenter.Z;

            if (Shape == WorldFeatureShape.Ring)
            {
                Vec3 n = NormalisedNormal();
                double height = dx * n.X + dy * n.Y + dz * n.Z;
                double px = dx - height * n.X, py = dy - height * n.Y, pz = dz - height * n.Z;
                double radial = Math.Sqrt(px * px + py * py + pz * pz);

                double ux, uy, uz;
                if (radial > 1e-9) { ux = px / radial; uy = py / radial; uz = pz / radial; }
                else { Vec3 perp = AnyPerpendicular(n); ux = perp.X; uy = perp.Y; uz = perp.Z; }

                double targetRadial = ClampIntoNearestBand(radial);
                double targetHeight = ClampInset(height, -HalfThicknessKm, HalfThicknessKm);

                return new Vec3(
                    resolvedCenter.X + ux * targetRadial + n.X * targetHeight,
                    resolvedCenter.Y + uy * targetRadial + n.Y * targetHeight,
                    resolvedCenter.Z + uz * targetRadial + n.Z * targetHeight);
            }

            double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double lo = Shape == WorldFeatureShape.Sphere ? 0.0 : InnerRadiusKm;
            if (r >= lo && r <= OuterRadiusKm) return posKm;

            double targetR = ClampInset(r, lo, OuterRadiusKm);
            if (r <= 1e-9)
            {
                // At the exact centre there is no radial direction; +Y, the same fallback
                // NormalisedNormal uses, keeps the answer deterministic.
                return new Vec3(resolvedCenter.X, resolvedCenter.Y + targetR, resolvedCenter.Z);
            }
            return new Vec3(
                resolvedCenter.X + dx / r * targetR,
                resolvedCenter.Y + dy / r * targetR,
                resolvedCenter.Z + dz / r * targetR);
        }

        /// <summary>The furthest <see cref="NearestPointInside"/> can move a point this feature
        /// claims. Bounds the drift sweep's "could a legitimate reposition have moved it this far",
        /// which would otherwise refuse to re-anchor a station whose node a feature legitimately
        /// placed — see <see cref="WorldModel.MaxPlausibleSnapDistanceKm"/>.</summary>
        public double MaxPlacementDisplacementKm()
        {
            switch (Shape)
            {
                // The claim is exactly the volume, so a claimed point is always already inside.
                case WorldFeatureShape.Sphere: return 0.0;
                // Worst case is the centre of the hole, pushed out to the inner surface. A shell that
                // gives its hole up claims only its own volume, like a sphere.
                case WorldFeatureShape.Shell: return GivesUpHole ? 0.0 : InnerRadiusKm;
                case WorldFeatureShape.Ring:
                {
                    // Radial travel is bounded by the annulus width, height travel by how far off the
                    // plane the claim ball reaches at the inner radius. Summing the squares of two
                    // separate worst cases over-counts, which is the safe direction for a fence.
                    double span = OuterRadiusKm - InnerRadiusKm;
                    double heightReachSq = OuterRadiusKm * OuterRadiusKm - InnerRadiusKm * InnerRadiusKm;
                    if (heightReachSq < 0) heightReachSq = 0;
                    return Math.Sqrt(span * span + heightReachSq);
                }
            }
            return 0.0;
        }

        /// <summary>Radial distance clamped into whichever band is nearest, or into the whole annulus
        /// when the ring is solid. A radius sitting in a gap between bands goes to the closer band
        /// edge rather than staying in the gap.</summary>
        private double ClampIntoNearestBand(double radial)
        {
            if (Bands != null && Bands.Count > 0)
            {
                double bestTarget = 0;
                double bestGap = double.MaxValue;
                for (int i = 0; i < Bands.Count; i++)
                {
                    FeatureBand b = Bands[i];
                    if (b == null || b.OuterRadiusKm < b.InnerRadiusKm) continue;
                    double target = ClampInset(radial, b.InnerRadiusKm, b.OuterRadiusKm);
                    double gap = Math.Abs(target - radial);
                    if (gap < bestGap) { bestGap = gap; bestTarget = target; }
                }
                if (bestGap < double.MaxValue) return bestTarget;
            }
            return ClampInset(radial, InnerRadiusKm, OuterRadiusKm);
        }

        /// <summary>How far inside a face <see cref="NearestPointInside"/> puts a point it had to
        /// move: 10 m, five times the largest anchor movement measured live, and far below anything
        /// the ore seam or separation can tell apart.</summary>
        public const double ContainmentInsetKm = 0.01;

        /// <summary><paramref name="value"/> unchanged when it lies in <c>[lo, hi]</c>; otherwise the
        /// nearer end moved <see cref="ContainmentInsetKm"/> inward, capped at a quarter of the range
        /// so a thin band or slab is never crossed.</summary>
        private static double ClampInset(double value, double lo, double hi)
        {
            if (value >= lo && value <= hi) return value;
            double inset = Math.Min(ContainmentInsetKm, (hi - lo) * 0.25);
            if (inset < 0) inset = 0;
            return value < lo ? lo + inset : hi - inset;
        }

        /// <summary>Any unit vector perpendicular to <paramref name="n"/>. Only reached at the ring's
        /// exact axis, where azimuth is undefined and some deterministic choice has to be made.</summary>
        private static Vec3 AnyPerpendicular(Vec3 n)
        {
            Vec3 axis = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            double cx = n.Y * axis.Z - n.Z * axis.Y;
            double cy = n.Z * axis.X - n.X * axis.Z;
            double cz = n.X * axis.Y - n.Y * axis.X;
            double len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (len <= 1e-9) return new Vec3(1, 0, 0);
            return new Vec3(cx / len, cy / len, cz / len);
        }

        /// <summary>Volume of the whole feature in km³, ignoring band subdivision — the volume a
        /// solid feature of this shape occupies.</summary>
        public double VolumeKm3()
        {
            switch (Shape)
            {
                case WorldFeatureShape.Sphere:
                    return (4.0 / 3.0) * Math.PI * OuterRadiusKm * OuterRadiusKm * OuterRadiusKm;

                case WorldFeatureShape.Shell:
                    return (4.0 / 3.0) * Math.PI
                         * (OuterRadiusKm * OuterRadiusKm * OuterRadiusKm - InnerRadiusKm * InnerRadiusKm * InnerRadiusKm);

                case WorldFeatureShape.Ring:
                    return AnnulusVolumeKm3(InnerRadiusKm, OuterRadiusKm);
            }
            return 0.0;
        }

        /// <summary>Volume of one ring band in km³.</summary>
        public double BandVolumeKm3(FeatureBand band)
        {
            if (band == null || Shape != WorldFeatureShape.Ring) return 0.0;
            return AnnulusVolumeKm3(band.InnerRadiusKm, band.OuterRadiusKm);
        }

        private double AnnulusVolumeKm3(double innerKm, double outerKm)
        {
            if (outerKm <= innerKm) return 0.0;
            return Math.PI * (outerKm * outerKm - innerKm * innerKm) * 2.0 * HalfThicknessKm;
        }

        /// <summary>
        /// How many physical objects this feature's density asks for, given the world's packing
        /// ceiling in objects per 1000 km³.
        ///
        /// <para>Banded rings are summed band by band rather than measured across the whole annulus:
        /// the gaps between bands are genuinely empty, and charging them volume would put rock in
        /// them or thin out the bands to pay for them.</para>
        ///
        /// <para>Returns a count, not a schedule. Spawning it is
        /// <see href="FAC-074">asteroid authorship</see>'s problem, and this number is large enough
        /// that it needs to be — a 180–450 km ring at density 1.0 asks for tens of thousands.</para>
        /// </summary>
        public long ObjectCount(double maxDensityPer1000Km3)
        {
            if (maxDensityPer1000Km3 <= 0) return 0;

            double perKm3 = maxDensityPer1000Km3 / 1000.0;
            double total = 0;

            if (Shape == WorldFeatureShape.Ring && Bands != null && Bands.Count > 0)
            {
                foreach (FeatureBand b in Bands)
                {
                    if (b == null) continue;
                    double d = MathUtil.Clamp01(DensityAt(b));
                    if (d <= 0) continue;
                    total += BandVolumeKm3(b) * d * perKm3;
                }
            }
            else
            {
                double d = MathUtil.Clamp01(Density);
                if (d > 0) total = VolumeKm3() * d * perKm3;
            }

            if (total <= 0) return 0;
            return (long)Math.Round(total);
        }

        private Vec3 NormalisedNormal()
        {
            double len = Math.Sqrt(Normal.X * Normal.X + Normal.Y * Normal.Y + Normal.Z * Normal.Z);
            if (len <= 1e-9) return new Vec3(0, 1, 0);
            return new Vec3(Normal.X / len, Normal.Y / len, Normal.Z / len);
        }
    }
}
