namespace Factions.Core
{
    /// <summary>
    /// What would happen if a node were created at one position, decided without creating one.
    ///
    /// <para>The read-only half of <see cref="WorldModel.SeedNodeAt"/>: it runs the same snap
    /// (<see cref="WorldModel.ProjectSeedPosition"/>), reads separation from the same authority
    /// (<see cref="WorldModel.SeparationAtKm"/>) and applies the same "is anything inside my required
    /// radius" rule the graph is audited against. One rule, two entry points — a caller that computed
    /// any of this itself would be a second implementation of placement, which is exactly how the
    /// authored separation once stayed inert for a day (FAC-105).</para>
    ///
    /// <para><b>The verdict is about the snapped point, not the requested one.</b> A point over a
    /// planet lands on terrain, one inside a ring lands in a band, and the separation in force is
    /// whatever applies <i>where it ends up</i>. Quoting the requested position would be quoting a
    /// place no node will ever occupy.</para>
    ///
    /// <para><see cref="NearestNodeKm"/> is deliberately NOT for players. Distance to the blocking
    /// node is a bearing-free but real disclosure of where somebody else's territory is, and the
    /// player-facing surface says only "occupied" (docs/economy-territory.md → Expanding). It is here
    /// for the server's own log line and for the admin verbs.</para>
    /// </summary>
    public sealed class NodePlacementProbe
    {
        /// <summary>The position asked about, km.</summary>
        public Vec3 RequestedKm;

        /// <summary>Where a node created here would actually come to rest, km — the value a player
        /// has to see before paying, because it can be a long way from where they are sitting.</summary>
        public Vec3 PlacedKm;

        /// <summary>Zone of <see cref="PlacedKm"/>, re-derived after the snap: lifting a point to a
        /// surface or into a ring band can move it across a region boundary.</summary>
        public NodeZone Zone;

        /// <summary>Separation in force at <see cref="PlacedKm"/>, km — authored feature, then
        /// authored body, then the per-zone floor.</summary>
        public double SeparationKm;

        /// <summary>Distance from <see cref="PlacedKm"/> to the nearest node already in the graph, km;
        /// <see cref="double.MaxValue"/> on an empty graph. Server-side only — see the type remarks.</summary>
        public double NearestNodeKm;

        /// <summary>True when nothing sits inside <see cref="SeparationKm"/> of <see cref="PlacedKm"/>.
        /// A zero or negative separation never blocks: it is the world declaring no rule here, not a
        /// radius that everything breaches.</summary>
        public bool Clear;

        /// <summary>True when the point is over a planet and the surface snap could not find the
        /// ground under it, so no node can be created here (<see cref="WorldModel.SeedNodeAt"/>
        /// refuses it). Never <see cref="Clear"/>.</summary>
        public bool NoGround;

        /// <summary>How far the snap moved the point, km. Zero for a site that is already legal
        /// ground; large for one over a planet, where the node lands on terrain below.</summary>
        public double SnapDistanceKm { get { return RequestedKm.DistanceTo(PlacedKm); } }
    }
}
