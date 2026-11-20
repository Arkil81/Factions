using System;

namespace Factions.Core
{
    /// <summary>A planet in the catalogue. Positions and radii are in km (world-space).</summary>
    public sealed class PlanetRecord
    {
        public string Id;            // stable host/world identity
        public string Name;          // operator-facing display name
        public string Generator;     // SE generator/subtype, e.g. EarthLike
        public Vec3   Center;        // km, world-space
        public double SurfaceRadius; // km — average terrain radius
        public double GravityRadius; // km — outer edge of detectable gravity
        public double AtmosphereRadius; // km — zero for airless bodies
        public bool   HasAtmosphere;
        /// <summary>The oxygen level of this body's air at the surface, before thinning with altitude:
        /// SE's <c>Atmosphere.Density × Atmosphere.OxygenDensity</c>, or zero when the atmosphere is
        /// not <c>Breathable</c>. What <see cref="WorkerUpkeep.BreathesAmbient"/> reads.</summary>
        public double BreathableOxygen;

        /// <summary>Whether a station may stand on this body's terrain. Resolved by
        /// <see cref="SurfaceNodePolicy"/>: the recipe's authored value wins, and SE's own
        /// <c>MyPlanetGeneratorDefinition.AllowSurfaceEconomyStations</c> is only the fallback.
        /// <para><b>SE's flag alone discriminates nothing on this world</b> — it defaults to true,
        /// vanilla declares it true on all eight bodies that mention it, and neither planet mod
        /// declares it at all, gas giant included. Verified against the live definition set
        /// 2026-08-25; see <see cref="SurfaceNodePolicy"/>.</para>
        /// <para>Defaults to <c>true</c> so a catalogue that does not report the flag stays
        /// permissive and behaves exactly as it did before the field existed.</para></summary>
        public bool AllowsSurfaceStations = true;

        /// <summary>Minimum distance between faction nodes on this body's terrain, in km. Null defers
        /// to <see cref="FactionTuning.NodeSeparationSurfaceKm"/>. Resolved by the host from the
        /// recipe, the same way <see cref="AllowsSurfaceStations"/> is.</summary>
        public double? NodeSeparationKm;

        /// <summary>What this body blocks a sightline at, in km. Null occludes at
        /// <see cref="SurfaceRadius"/>, which is the terrain and is what a real body's radius means.
        /// Resolved by the host from the recipe, the same way <see cref="NodeSeparationKm"/> is.
        /// <para>Authored because SE's radius is not always the body. Real Gas Giants reports a
        /// 9.5 km voxel core for Cauldron while the giant players fly around is a 180 km virtual
        /// sphere, so deferring to the terrain would leave the largest object in the system
        /// transparent to every sensor in it.</para></summary>
        public double? SensorOcclusionRadiusKm;

        /// <summary>Whether a station may sit in this body's orbit
        /// (<c>MyPlanetGeneratorDefinition.AllowOrbitalEconomyStations</c>). Same default and
        /// reasoning as <see cref="AllowsSurfaceStations"/>.</summary>
        public bool AllowsOrbitalStations = true;

        /// <summary>Crops an NPC Farm on this body grows with no seed stock, as SE seed/crop subtypes
        /// (<c>Fruit</c>, <c>Grain</c>, <c>Mushrooms</c>, <c>Vegetables</c>). Stamped from
        /// <see cref="RecipePlanet.NativeCrops"/> by <see cref="SurfaceNodePolicy.ApplyTo"/>; null
        /// means nothing grows wild here.</summary>
        public string[] NativeCrops;

        /// <summary>Whether a Habitat on this body may recruit workers from a civilian pool. Stamped
        /// from <see cref="RecipePlanet.Recruits"/> by <see cref="SurfaceNodePolicy.ApplyTo"/>.
        /// FAC-493.</summary>
        public bool Recruits;
    }

    /// <summary>
    /// Planet catalogue seam. The SE host wires a real adapter; the offline sim uses
    /// <see cref="NullPlanetCatalogue"/> which reports no planets and zero gravity everywhere.
    /// <para>All positions are in km (world-space). Gravity is in g (0 = none, 1.0 = full surface).</para>
    /// </summary>
    public interface IPlanetCatalogue
    {
        System.Collections.Generic.IEnumerable<PlanetRecord> Planets { get; }

        /// <summary>Gravity multiplier at <paramref name="posKm"/> (0..1+, where 1.0 = 1 g at planet surface).</summary>
        double GravityAt(Vec3 posKm);

        /// <summary>Closest surface point on the nearest planet (km, world-space).
        /// Returns <paramref name="posKm"/> unchanged when no planet is nearby.</summary>
        Vec3 NearestSurfacePoint(Vec3 posKm);

        /// <summary>Outward surface normal at <paramref name="posKm"/> on the nearest planet.
        /// Returns <c>(0,1,0)</c> when no planet is nearby.</summary>
        Vec3 SurfaceNormal(Vec3 posKm);
    }

    public static class NullPlanetCatalogue
    {
        public static readonly IPlanetCatalogue Instance = new NullImpl();

        private sealed class NullImpl : IPlanetCatalogue
        {
            public System.Collections.Generic.IEnumerable<PlanetRecord> Planets
            {
                get { yield break; }
            }
            public double GravityAt(Vec3 posKm) { return 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }
    }

    /// <summary>
    /// SE economy hook (treasury realization). The host wires a real adapter backed by
    /// MyBankingSystem; the sim and dry-run pass null (abstract ledger only, no SE side-effects).
    /// </summary>
    public interface IEconomyGateway
    {
        /// <summary>Credit (+) or debit (−) an SE wallet by <paramref name="amount"/>.
        ///
        /// <para><paramref name="ownerIdentityId"/> is a faction id <b>or</b> a player identity id.
        /// SE keeps one account dictionary keyed by a plain owner identifier —
        /// <c>MyFactionCollection</c> creates entries under a <c>FactionId</c> and
        /// <c>MyPlayerCollection</c> under an <c>IdentityId</c>, into the same table — so charging a
        /// person needs no seam of its own, and the method's name is narrower than what it takes.</para>
        ///
        /// <para>Returns false when the write did not happen: the SE economy system is unavailable
        /// (disabled, not loaded), the identifier holds no account, or the debit would take the
        /// balance below zero — SE refuses such a subtraction outright rather than clamping it.
        /// The refusal is authoritative and atomic, so a caller charging a wallet as a precondition
        /// charges first and commits on true, rather than reading a balance and then writing.</para></summary>
        bool ChangeFactionBalance(long ownerIdentityId, long amount);

        /// <summary>Read the faction's current SE wallet balance. Returns false when the SE economy
        /// system is unavailable or the faction holds no account -- distinct from a genuine zero
        /// balance, which returns true with <paramref name="balance"/> 0. <see cref="ITreasuryMirror"/>
        /// computes a delta against this, so conflating the two would credit a full treasury on top of
        /// a balance that was merely unreadable at the time.</summary>
        bool TryGetFactionBalance(long factionId, out long balance);
    }

    /// <summary>
    /// Mirrors the sim ledger into the host's player-visible wallet. Authority stays with
    /// <see cref="WorldModel"/>'s treasury (DESIGN decision 6); this only keeps the copy from
    /// drifting, so the number a player reads in game is the number the brain decides on.
    ///
    /// Keyed by faction <em>tag</em> because Core has no SE faction id -- the tag-to-id mapping and
    /// the SE call both live host-side. The value pushed is the <em>absolute</em> treasury, not a
    /// delta: that makes the one-time rebase of a seeded 1e9 account and every subsequent push the
    /// same operation, so a missed or failed push heals on the next one instead of accumulating.
    /// </summary>
    public interface ITreasuryMirror
    {
        /// <summary>Bring <paramref name="factionTag"/>'s host-visible wallet to
        /// <paramref name="treasury"/>. Returns false if the push did not land (economy unavailable,
        /// tag not resolvable to an SE faction) so the caller can retry; a false return must never
        /// roll back the sim-side change that triggered it.</summary>
        bool MirrorTreasury(string factionTag, long treasury);
    }

    /// <summary>No-op mirror: the offline sim and any host session without a live SE economy keep a
    /// purely abstract ledger. The default for <see cref="WorldModel.TreasuryMirror"/>.</summary>
    public static class NullTreasuryMirror
    {
        public static readonly ITreasuryMirror Instance = new NullImpl();

        private sealed class NullImpl : ITreasuryMirror
        {
            public bool MirrorTreasury(string factionTag, long treasury) { return true; }
        }
    }

    /// <summary>
    /// Fleet materialisation hook. When MES is available the host wires a real adapter that calls
    /// CustomSpawnRequest; otherwise the NullFleetSpawner no-ops (dry-run / abstract simulation).
    /// GridExists is always wired on the host so reboot reconciliation can clean up dead entity IDs.
    /// </summary>
    public interface IFleetSpawner
    {
        /// <summary>True when this spawner performs no actual MES materialisation (NullFleetSpawner).
        /// FactionRuntime uses this to skip transit time and resolve pending fleets immediately.</summary>
        bool SkipsTransit { get; }

        /// <summary>Attempt to spawn a raiding fleet for <paramref name="factionTag"/> at the target
        /// position (km, world-space). <paramref name="fleetKey"/> is a stable unique key for this
        /// fleet event (used for async dedup — the spawner may return 0 on the first call while the
        /// prefab load is in flight, then return the entity ID on the next call for the same key).
        /// <paramref name="prefabId"/> is the prefab SubtypeId already gated and consumed at dispatch
        /// time (<see cref="PendingFleetAction.ShipPrefabId"/>), so the spawned grid is the hull the
        /// economy actually paid for. Pass "" for a free-built ship with no BOM; the spawner falls back
        /// to its own role-based catalogue lookup in that case. Every spawn takes it as a required
        /// argument rather than reading it back out of the fleet, so a new kind cannot reach a
        /// materialisation path without deciding what it flies.
        /// Returns the spawned entity ID, or 0 if the spawn did not fire / is still pending.</summary>
        long SpawnRaid(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId);

        /// <summary>Attempt to spawn a trade convoy for <paramref name="factionTag"/>. See
        /// <see cref="SpawnRaid"/> for the <paramref name="fleetKey"/> and <paramref name="prefabId"/>
        /// contracts.</summary>
        long SpawnTrade(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId);

        /// <summary>Attempt to spawn a patrol for <paramref name="factionTag"/>. See
        /// <see cref="SpawnRaid"/> for the <paramref name="fleetKey"/> and <paramref name="prefabId"/>
        /// contracts. <paramref name="centerKm"/> is the node position to loop around.</summary>
        long SpawnPatrol(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 centerKm, string prefabId);

        /// <summary>Attempt to spawn a haul convoy for <paramref name="factionTag"/>. See
        /// <see cref="SpawnRaid"/> for the <paramref name="fleetKey"/> and <paramref name="prefabId"/>
        /// contracts.</summary>
        long SpawnHaul(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId);

        /// <summary>Attempt to spawn a mining ship for <paramref name="factionTag"/>. See
        /// <see cref="SpawnRaid"/> for the <paramref name="fleetKey"/> and <paramref name="prefabId"/>
        /// contracts. On its dwell <paramref name="targetKm"/> is a waypoint of the ring it works
        /// (<see cref="MineWander"/>), not the deposit.</summary>
        long SpawnMine(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId);

        /// <summary>Attempt to spawn a gift convoy for <paramref name="factionTag"/>. See
        /// <see cref="SpawnRaid"/> for the <paramref name="fleetKey"/> and <paramref name="prefabId"/>
        /// contracts.</summary>
        long SpawnGift(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId);

        /// <summary>Attempt to spawn every hull of a multi-ship squadron for
        /// <paramref name="factionTag"/> under one <paramref name="fleetKey"/>. Returns the lead's
        /// entity ID once every hull's spawn has completed (or failed), filling
        /// <paramref name="escortsOut"/> with the other live IDs in <paramref name="prefabIds"/>
        /// order; returns 0 while any hull is still pending, leaving <paramref name="escortsOut"/>
        /// untouched, so a caller polls the same key again exactly as it does for the single-hull
        /// spawns. The lead is the first hull that spawned successfully, and
        /// <paramref name="prefabIds"/>[0] is the preferred one. <paramref name="kind"/> selects the
        /// per-kind wiring (turret arming and the like) that <see cref="SpawnRaid"/> and its
        /// siblings each take from their own method identity. Every prefab is a required argument
        /// for the same reason the single-hull spawns take theirs: the hulls were gated and consumed
        /// at dispatch, and the spawner must not pick its own.</summary>
        long SpawnSquadron(string fleetKey, ActionKind kind, string factionTag, long factionId,
                           Vec3 posKm, Vec3 targetKm,
                           System.Collections.Generic.IReadOnlyList<string> prefabIds,
                           System.Collections.Generic.List<long> escortsOut);

        /// <summary>Check whether a previously spawned grid still exists in the loaded world.
        /// Called during reboot reconciliation and the observation loop.</summary>
        bool GridExists(long entityId);

        /// <summary>UTC time this grid was last seen taking damage, or null when it has taken none
        /// or is not tracked (it was materialised by a previous process, or this spawner does not
        /// materialise at all). The observation loop uses it to tell a grid destroyed in combat from
        /// one that merely vanished — an admin deleting it, an engine cleanup, or a reload that did
        /// not restore it. Only the first costs the faction its hull; a null answer must never be
        /// read as a loss.</summary>
        DateTime? LastDamageUtc(long entityId);

        /// <summary>True when this spawner itself closed <paramref name="entityId"/> -- as the grid it
        /// was asked to despawn, or as part of that grid's assembly or fleet-mates. A grid we closed
        /// is never read as lost, whatever damage it took first (<see cref="GridLossRule"/>).</summary>
        bool ClosedByUs(long entityId);

        /// <summary>Despawn a materialised fleet grid. Called when all players have moved out of
        /// observation range; the abstract event continues in-flight and will re-materialise on the
        /// next approach.
        ///
        /// <para>Returns how many grids were closed, which is more than one whenever the hull carries
        /// subgrids: a rotor-mounted turret is its own grid and closing only the hull leaves it in
        /// the world with the hull's velocity. Split fragments and released ordnance recorded against
        /// the same fleet go with it. <c>0</c> means nothing was closed — the entity was already gone
        /// — and for a spawner that never materialises anything that is every call, so a caller must
        /// read it as "nothing was closed", not as a failure.</para></summary>
        int Despawn(long entityId);

        /// <summary>The prefab SubtypeId a materialised grid was spawned from, or null when this
        /// spawner does not track it (it never materialises, or the grid came from a previous
        /// process). Lets a loss be attributed to the hull that died, which for a squadron escort is
        /// the only way to say which member left the pool.</summary>
        string GridPrefabId(long entityId);

        /// <summary>Returns the current world position of a materialised fleet grid in km, or null
        /// if the entity no longer exists. Used to detect drift between the physical grid and the
        /// brain's simulated position so grids that have fallen far behind can be despawned.</summary>
        Vec3? GetGridPositionKm(long entityId);

        /// <summary>Returns the current speed of a materialised fleet grid in km/min, or -1 if the
        /// entity does not exist. Returns 0 if the grid has no physics body. Used to detect crashed
        /// or stuck grids.</summary>
        double GetGridSpeedKmPerMin(long entityId);

        /// <summary>Returns the SE faction tag of the grid's primary owner, or null if the grid has
        /// no owners or the owner's faction cannot be resolved. Used to detect player capture.</summary>
        string GetGridOwnerFactionTag(long entityId);

        /// <summary>Rewires an already-materialised grid's autopilot toward a new target without
        /// despawning/respawning it. Used when an in-flight fleet completes one leg of a multi-leg
        /// itinerary (e.g. Pickup-then-deliver) while its physical grid is still alive. Returns
        /// false if the entity no longer exists or no autopilot mechanism could be found/rewired —
        /// the caller falls back to a fresh spawn in that case.</summary>
        bool Retarget(long entityId, Vec3 newTargetKm);

        /// <summary>Advances every grid currently under native flight control. Called once per host
        /// frame; a no-op for spawners that never materialise anything.</summary>
        void TickFlight();

        /// <summary>Hands a materialised grid from the cruise autopilot to the native flight
        /// controller for its terminal approach to <paramref name="targetKm"/>. Idempotent — calling
        /// it again for a grid already manoeuvring toward the same target just refreshes the target.
        /// Returns false if the entity is gone or has no usable thrust/gyro systems, in which case
        /// the caller leaves it on the cruise autopilot.</summary>
        bool BeginManoeuvre(long entityId, Vec3 targetKm);

        /// <summary>Departure bookend of <see cref="BeginManoeuvre"/>: hands a grid that has not got
        /// moving yet to the native controller so it accelerates off its origin under command, and
        /// returns whether it is now under control. Idempotent and self-limiting — it declines a grid
        /// that is already up to speed or already close enough for the approach profile. When the
        /// launch finishes it releases the grid and re-wires its cruise autopilot itself, then
        /// returns false. <paramref name="flyThrough"/> says no approach will be asked for on this
        /// leg, so the launch must not leave the grid to one.</summary>
        bool BeginLaunch(long entityId, Vec3 targetKm, bool flyThrough);

        /// <summary>Whether the launch ladder has exhausted its retries on a grid and given up on
        /// flying it off its origin. The caller's answer is to despawn it and let the leg resolve
        /// abstractly — the outcome a player leaving range already produces, which costs the faction
        /// nothing. Reading it clears it, so the caller acts once.</summary>
        bool ConsumeLaunchGiveUp(long entityId);

        /// <summary>Whether a grid has had no thrusters, or no owner, for long enough that nothing of
        /// its faction can fly it again. The caller writes the hull off when hostile fire did it and
        /// otherwise despawns it and lets the leg resolve abstractly, as for a launch give-up. True
        /// once per unbroken run; asked every tick.</summary>
        bool ConsumeThrustLost(long entityId);

        /// <summary>Releases a grid from native flight control, restoring the grid-side state the
        /// controller changed (AI-block activation); the caller is responsible for re-wiring an
        /// autopilot if the grid keeps flying (see <see cref="Retarget"/>). Safe to call for an
        /// entity that was never controlled.</summary>
        void ReleaseControl(long entityId);

        /// <summary>True while a grid is under native flight control, or handed back from a launch
        /// with the check that the cruise autopilot took it still pending. The observation loop uses
        /// this to exempt the grid from the stall detector, which would otherwise force-fail it as
        /// crashed debris.</summary>
        bool IsManoeuvring(long entityId);

        /// <summary>True while a grid under native flight control has reached its manoeuvre's rest
        /// point and is holding there — at rest within tolerance, slow enough to count as stopped.
        /// The observation loop accepts this as leg arrival alongside the distance-to-node test,
        /// because a clearance-adjusted stop point may rest outside the node's arrival radius (see
        /// <c>ApproachClearance</c>). Always false for a grid not under control.</summary>
        bool IsManoeuvreComplete(long entityId);
    }
}
