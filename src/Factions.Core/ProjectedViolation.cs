namespace Factions.Core
{
    /// <summary>
    /// One projected node sitting closer to a neighbour than the separation in force where it lands.
    ///
    /// <para><see cref="Zone"/> and <see cref="InsideFeature"/> are carried because they separate two
    /// very different faults that produce the same count. A node still inside its authored feature
    /// but too close to a sibling is a <i>packing</i> problem — the region is denser than the recipe
    /// thinks. A node that has fallen <b>outside</b> its feature is a <i>placement</i> problem: it
    /// inherits the deep-space floor, which it then breaches trivially, and the real defect is that
    /// it left the region at all.</para>
    /// </summary>
    public sealed class ProjectedViolation
    {
        /// <summary>Index into the list handed to
        /// <see cref="WorldModel.ProjectedSeparationDetail"/>, so the caller can name the owner.</summary>
        public int Index;

        public Vec3 PositionKm;

        /// <summary>Distance to the nearest other node, km.</summary>
        public double NearestKm;

        /// <summary>Separation required where this node landed, km.</summary>
        public double RequiredKm;

        /// <summary>Zone the position resolves to — the zone floor it inherits when no feature claims it.</summary>
        public NodeZone Zone;

        /// <summary>True when a world feature still claims this position.</summary>
        public bool InsideFeature;

        /// <summary>The feature's own separation when <see cref="InsideFeature"/>; otherwise 0.</summary>
        public double FeatureSeparationKm;
    }
}
