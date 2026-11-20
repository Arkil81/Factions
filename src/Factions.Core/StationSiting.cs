namespace Factions.Core
{
    /// <summary>What kind of place a station stands in. Mirrors SE's own three-way
    /// <c>MyStationTypeEnum</c> split, named here so Core can answer the question without an SE
    /// reference.</summary>
    public enum StationSite
    {
        /// <summary>Free-floating, outside every well.
        /// <para><b>Zero deliberately.</b> <see cref="SpatialNode.Site"/> is derived and not
        /// persisted, so an unset value is a real state — a node loaded before its first
        /// <c>ReclassifyNodes</c>, or a fixture that forgot the field. Every consumer of this enum
        /// asks "is this on the ground", so the default must be the answer that grants nothing.
        /// <see cref="NodeZone.DeepSpace"/> is zero for the same reason.</para></summary>
        Space = 0,
        /// <summary>Off the ground but inside a body's gravity well.</summary>
        Orbital = 1,
        /// <summary>Standing on a body's terrain.</summary>
        Surface = 2,
    }

    /// <summary>
    /// What kind of station belongs at a position — asked of the world itself rather than read off a
    /// node's cached <see cref="NodeZone"/> label.
    ///
    /// <para><b>Why not <see cref="NodeZone"/>.</b> That label comes from relative gravity, which on a
    /// body whose atmosphere reaches twenty times its voxel radius reads as "surface" out to ~220 km.
    /// A node authored into a ring at 195 km is nowhere near any ground, and founding a surface
    /// outpost there produces a landed-looking grid floating in a debris disc. The question a station
    /// type actually asks is geometric — am I standing on terrain, am I in a well, or am I in open
    /// space — and geometry is what this answers. See docs/issues/FAC-096 stage 2.</para>
    ///
    /// <para><b>Only the Surface half changes.</b> Orbital-versus-Space is still exactly
    /// <c>GravityAt &gt; 0</c>, the same predicate <see cref="WorldModel.ClassifyZone"/> uses to
    /// separate Orbit from DeepSpace — computed directly instead of through the cached label, so a
    /// node already snapped to the orbit band keeps the station type it has today.</para>
    /// </summary>
    public static class StationSiting
    {
        /// <summary>How close to the terrain counts as standing on it, in km. The same 1 km
        /// <see cref="WorldModel.ReclassifyNodes"/> already treats as "on the surface sphere", so a
        /// node that pass leaves alone is a node this call reads as Surface.</summary>
        public const double GroundToleranceKm = 1.0;

        public static StationSite Classify(IPlanetCatalogue planets, Vec3 posKm)
        {
            if (planets == null) return StationSite.Space;

            PlanetRecord nearest = PlanetGeometry.NearestByCentre(planets, posKm);
            if (nearest == null) return StationSite.Space;   // no bodies at all

            // A body the world says cannot be landed on never yields a surface station, however close
            // the terrain is -- the same answer SurfaceNodePolicy gives placement, asked here of the
            // station type directly rather than inherited through a demoted NodeZone label.
            if (nearest.AllowsSurfaceStations && CouldBeNearTerrain(nearest, posKm))
            {
                // Inside the body is Surface as a matter of geometry, decided before any terrain
                // lookup -- mirrors ClassifyZone's own first rule. ReclassifyNodes lifts buried nodes
                // to the surface anyway; this keeps the answer right in the window before it does.
                if (nearest.SurfaceRadius > 0 && posKm.DistanceTo(nearest.Center) < nearest.SurfaceRadius)
                    return StationSite.Surface;

                Vec3 ground = planets.NearestSurfacePoint(posKm);
                if (ground.DistanceSquaredTo(posKm) <= GroundToleranceKm * GroundToleranceKm)
                    return StationSite.Surface;
            }

            // Not NearestByCentre: a point near a moon can sit outside the moon's small well while
            // every scrap of gravity there is the parent's, and that point is Orbital, not Space.
            return planets.GravityAt(posKm) > 0 ? StationSite.Orbital : StationSite.Space;
        }

        /// <summary>Whether <paramref name="posKm"/> lies inside the gravity well of a body a
        /// station may stand on — asked of every body rather than of the nearest one, because the
        /// well a point is in and the body nearest its centre are not always the same (a point out
        /// by a moon can be nearer the moon while every scrap of gravity there is the parent's).
        ///
        /// <para><b>What it is for.</b> "Off the ground but in a well" covers two unlike places. In
        /// low orbit over landable terrain there is a world below to supply and be supplied by; in
        /// the outer well of a body nothing can land on there is nothing at all, however strong the
        /// gravity reading. <see cref="Classify"/> answers what kind of station stands here and
        /// rightly calls both Orbital; this answers whether the site is worth anything, which is a
        /// different question and belongs to whoever asks it.</para>
        ///
        /// <para>Landability is <see cref="PlanetRecord.AllowsSurfaceStations"/> — the world's own
        /// authored answer, resolved by <see cref="SurfaceNodePolicy"/>, so a body the recipe says
        /// cannot be landed on yields nothing here either.</para></summary>
        public static bool InLandableWell(IPlanetCatalogue planets, Vec3 posKm)
        {
            if (planets == null) return false;
            foreach (PlanetRecord p in planets.Planets)
            {
                if (p == null || !p.AllowsSurfaceStations) continue;
                if (p.GravityRadius <= 0) continue;
                if (posKm.DistanceTo(p.Center) < p.GravityRadius) return true;
            }
            return false;
        }

        /// <summary>Whether <paramref name="posKm"/> is close enough to <paramref name="body"/> for a
        /// terrain lookup to be able to say Surface.
        ///
        /// <para><b>This is a cost guard, not a rule.</b> <see cref="IPlanetCatalogue
        /// .NearestSurfacePoint"/> is <c>MyPlanet.GetClosestSurfacePointGlobal</c> on the live host — a
        /// terrain-following lookup that can touch voxel data — and <see cref="Classify"/> is now
        /// called for every node on every boot (node kind derives from it as well as station type).
        /// A deep-space node would otherwise pay a terrain lookup against whichever planet happens to
        /// be nearest, to answer a question its own gravity reading already settles.</para>
        ///
        /// <para>Beyond a body's gravity limit the terrain is far further away than
        /// <see cref="GroundToleranceKm"/>, and nothing nearer could be a different body — that body
        /// would be the nearest one. A body reporting no gravity limit is not skipped: there is
        /// nothing to bound the guard with, so it pays the lookup as before.</para></summary>
        private static bool CouldBeNearTerrain(PlanetRecord body, Vec3 posKm)
        {
            if (body.GravityRadius <= body.SurfaceRadius + GroundToleranceKm) return true;
            return posKm.DistanceTo(body.Center) < body.GravityRadius;
        }
    }
}
