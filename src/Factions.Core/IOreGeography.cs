using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which of SE's systems put an ore where a sample found it. Each answers "how much
    /// rock per unit of ground" in its own way, and the deposit model treats them alike except where
    /// noted on each value.</summary>
    public enum OreSource
    {
        /// <summary>A band of a planet's ore map, or an authored feature's material: a continuous
        /// layer of known thickness under the point.</summary>
        Band = 0,
        /// <summary>A layer of a planet's material rule — lake ice, a sand sea — credited at its own
        /// thickness, with the layers above it as overburden.</summary>
        Layer = 1,
        /// <summary>Small voxel maps the planet's environment scatters over its surface (rock
        /// pillars and the like). Expressed as the expected volume per square metre of ground, which
        /// is far below any band's thickness, so <see cref="DepositModel.MinBandDepthM"/> does not
        /// apply: its reason — a band too thin for SE to voxelise — is about bands.</summary>
        Scatter = 2,
    }

    /// <summary>One ore found by an area sample, with the figures the deposit model needs.
    /// <para><see cref="SubTypeId"/> is the bare SE ore subtype ("Iron", "Ice") with no
    /// <c>Ore:</c>/<c>RareOre:</c> prefix — the same form <c>AsteroidDepositCandidate.OreName</c>
    /// uses, and the form <see cref="ItemCatalogue"/> keys ore by.</para></summary>
    public struct OreObservation
    {
        /// <summary>Bare SE ore subtype, unprefixed.</summary>
        public string SubTypeId;

        /// <summary>Sample points that found this ore. Relative, not absolute — meaningful only
        /// against <see cref="OreSample.PointsExamined"/> and the other observations in the same
        /// sample.</summary>
        public int Hits;

        /// <summary>Thickness of the ore band in metres. Ore per unit of surface area, so it scales
        /// richness alongside <see cref="Hits"/>.</summary>
        public double BandDepthM;

        /// <summary>Metres of waste rock above the band. Overburden — how much has to be stripped
        /// before any ore is reachable. Carried through the seam but not yet consumed: see
        /// docs/ore-geography.md, "Start is overburden, and is deliberately not wired yet".</summary>
        public double OverburdenM;

        /// <summary>Ore a cubic metre of this rock gives up when drilled: SE's
        /// <c>MinedOreRatio</c> for the voxel <b>material</b> the ore was found in. Per material, not
        /// per ore — the same ore comes out of a 5x ice sheet and a 1x snowfield, or a 1x silver vein
        /// and 0.002x black sand. 0 when the reader cannot say, which the deposit model reads as
        /// <see cref="DepositModel.ReferenceYieldRatio"/>. A material that truly yields nothing is
        /// the reader's to leave out, not to report as 0.</summary>
        public double YieldRatio;

        /// <summary>Which SE system produced this observation.</summary>
        public OreSource Source;

        public OreObservation(string subTypeId, int hits, double bandDepthM, double overburdenM)
            : this(subTypeId, hits, bandDepthM, overburdenM, 0.0, OreSource.Band)
        {
        }

        public OreObservation(string subTypeId, int hits, double bandDepthM, double overburdenM,
            double yieldRatio, OreSource source)
        {
            SubTypeId = subTypeId;
            Hits = hits;
            BandDepthM = bandDepthM;
            OverburdenM = overburdenM;
            YieldRatio = yieldRatio;
            Source = source;
        }
    }

    /// <summary>What an area sample around one position actually found. An empty
    /// <see cref="Ores"/> with a nonzero <see cref="PointsExamined"/> is a real, meaningful answer —
    /// barren ground — and is distinct from the seam declining to sample at all.</summary>
    public sealed class OreSample
    {
        /// <summary>Total sample points examined, hits and misses alike. Zero means nothing was
        /// looked at, which is not the same as finding nothing.</summary>
        public int PointsExamined;

        public readonly List<OreObservation> Ores = new List<OreObservation>();

        public bool IsBarren { get { return Ores.Count == 0; } }
    }

    /// <summary>Whether ore could be under a position at all, answered from geometry and authored
    /// data alone — no probing. Three states, because "this reader has nothing to say" and "this
    /// ground carries nothing" are different answers and a composite reader has to tell them apart.
    /// <para>The same distinction <see cref="DepositResolution"/> draws between <c>Declined</c> and
    /// <c>Barren</c>, one level earlier: this is asked <b>before</b> a position is committed to, so
    /// it must cost nothing.</para></summary>
    public enum OreProspect
    {
        /// <summary>Nothing this reader knows covers the position. In a composite, ask the next
        /// reader; when every reader says this, the position is open space and can never bear ore.</summary>
        Unknown = 0,
        /// <summary>Covered, and carries no mineable ore anywhere — a gas giant, or a feature that
        /// authors emptiness. Decisive: sampling here would come back barren.</summary>
        None = 1,
        /// <summary>Covered by a body or feature that carries mineable ore. Says nothing about how
        /// much, or whether any of it is under this particular point — see
        /// <see cref="IOreGeography.CouldBearOre"/> for why that is the most this can honestly claim.</summary>
        Possible = 2,
    }

    /// <summary>
    /// Ore-geography seam: what minerals are actually present at a place in the world.
    /// <para>The host answers this from SE itself — for a planet, by sampling the body's ore cubemap
    /// (already decoded and pinned at planet construction, so no voxel load and no allocation); for
    /// deep space, from the authored world features, since there is no cubemap to read. The offline
    /// sim wires <see cref="NullOreGeography"/> and gets a barren world.</para>
    /// <para><b>This seam measures; it does not decide.</b> It reports raw observations and Core
    /// turns them into a deposit (<see cref="DepositModel"/>), so node-kind propensity, weighting and
    /// richness policy stay SE-free and unit-testable. See docs/ore-geography.md.</para>
    /// </summary>
    public interface IOreGeography
    {
        /// <summary>Area-sample the ore present within <paramref name="radiusKm"/> of
        /// <paramref name="posKm"/>.
        /// <para>Area, not point: only a small fraction of a planet's ore map carries ore at all, so
        /// a single point sample answers "nothing here" almost everywhere regardless of what the
        /// region holds. The radius is the node's catchment, not its footprint.</para>
        /// <para>Returns false when this position cannot be sampled at all — no body and no feature
        /// covers it — which the caller must not confuse with a sample that came back barren.</para></summary>
        bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample);

        /// <summary>Continues a survey: samples <paramref name="count"/> more points of the same
        /// deterministic sequence, starting at <paramref name="startIndex"/>.
        /// <para>Point <c>i</c> must be a <b>fixed</b> place — independent of how many points are
        /// ever taken — or accumulating across calls is meaningless. That contract is the whole
        /// reason this exists beside <see cref="TrySample"/> rather than being folded into it. See
        /// FAC-101 and <see cref="ProgressiveLattice"/>.</para></summary>
        bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample);

        /// <summary>Could there be ore under <paramref name="posKm"/> at all — is it inside a body or
        /// an authored feature that carries any? Geometry and authored data only: <b>no probing, no
        /// sampling, no allocation</b>, so it is cheap enough to ask of every candidate position in a
        /// placement loop, which <see cref="TrySample"/> is not.
        ///
        /// <para><b>Presence of possibility, never composition.</b> Only ~3% of a planet's ore texels
        /// carry anything, so no cheap per-position answer can rank two points on the same body — that
        /// is what the survey exists for. What this <i>can</i> settle is the case that costs a faction
        /// most: open space outside every body and feature, where no amount of surveying will ever
        /// find anything. See docs/ore-geography.md.</para>
        ///
        /// <para>Not <c>IPlanetOreLookup</c>. That is region-keyed — "what does this <i>body</i>
        /// carry" — and answers which region to colonise. This is position-keyed and answers whether
        /// a coordinate is worth standing on. Same world, different questions; collapsing them would
        /// give the colonisation ranker a point sample and the placement loop a whole planet.</para></summary>
        OreProspect CouldBearOre(Vec3 posKm);

        /// <summary>Every ore subtype obtainable anywhere in this world, from any body or feature.
        /// <para>World-scoped, deliberately: recipe reachability asks what the world can ever supply,
        /// not what one node holds, and answering it per-node would wrongly exclude a recipe whose
        /// input exists on a planet no faction currently owns.</para></summary>
        IEnumerable<string> MintableOres { get; }
    }

    /// <summary>Offline-sim and unit-test geography: nothing is samplable and nothing is mintable.
    /// A world with no ore anywhere, which callers must handle rather than assume away.</summary>
    public static class NullOreGeography
    {
        public static readonly IOreGeography Instance = new NullImpl();

        private sealed class NullImpl : IOreGeography
        {
            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                sample = null;
                return false;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                sample = null;
                return false;
            }

            public OreProspect CouldBearOre(Vec3 posKm)
            {
                return OreProspect.Unknown;
            }

            public IEnumerable<string> MintableOres
            {
                get { yield break; }
            }
        }
    }
}
