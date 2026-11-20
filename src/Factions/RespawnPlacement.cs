using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Sandbox.Common.ObjectBuilders.Definitions;   // MyObjectBuilder_Datapad
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;   // MyGps
using Sandbox.Game.World;
using SpaceEngineers.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;   // IMyGps
using VRage.ObjectBuilders;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Places a respawn-ship spawn beside something worth arriving next to, and tells the arrival
    /// where the nearest welcoming station is.
    ///
    /// Scope is exactly SE's <c>MySpaceRespawnComponent.SpawnAtShip</c> — the respawn-screen options
    /// that hand out a pod or a rover. Respawning into a medical room or survival kit goes through
    /// the spawn point's own transform, and a suit-only respawn through <c>SpawnInSuit</c>; neither
    /// is touched.
    ///
    /// Vanilla already anchors to online faction mates, but at <c>OptimalSpawnDistance</c> (16 km on
    /// this world) and it never considers a station at all — with nobody online you get a random
    /// point on the sunlit hemisphere, or a random spot near a procedural asteroid in space. What is
    /// added here is a station anchor, a tighter mate radius, and the guarantee that a planet spawn
    /// stays on the planet the player picked.
    ///
    /// <see cref="RespawnPlacementPatches"/> drives this: it opens a spawn, lets SE compute its own
    /// answer, then offers it here for replacement.
    /// </summary>
    internal sealed class RespawnPlacement
    {
        internal static RespawnPlacement Instance;

        /// <summary>Set on a Nexus agent, where the patches would otherwise stand down on
        /// <see cref="SimulationGate.Deferred"/>: the players respawn there, so the placement does
        /// too, from the authority's snapshot.</summary>
        internal static volatile bool OnAgent;

        /// <summary>Whether the respawn patches act on this server.</summary>
        internal static bool Active { get { return !SimulationGate.Deferred || OnAgent; } }

        // Search budget for a placement that clears obstructions. Small: each attempt is a
        // FindFreePlace probe on the game thread inside a spawn the player is waiting on.
        private const int PlacementAttempts = 12;

        // Metres of surface-height spread tolerated across a candidate landing site's footprint.
        // Vanilla's own evenness test (TestPlanetLandingPosition) is private; this is the same idea
        // measured directly, and keeps a pod off a cliff face without demanding a billiard table.
        private const double MaxSurfaceSpreadM = 12.0;

        private readonly Func<SpatialGraph> _graph;
        private readonly Func<FactionTuning> _tuning;
        private readonly Func<WorldFeaturePlacement> _features;
        private readonly Func<string, string> _personalityOf;
        private readonly DecisionLog _log;

        private SpatialGraph Graph { get { return _graph != null ? _graph() : null; } }
        private FactionTuning Tuning { get { return _tuning != null ? _tuning() : null; } }
        private WorldFeaturePlacement Features
        {
            get { WorldFeaturePlacement f = _features != null ? _features() : null; return f ?? WorldFeaturePlacement.Empty; }
        }
        private bool Ready { get { return Graph != null && Tuning != null; } }

        // Seeded per spawn from the identity, so the same player retrying a spawn does not get the
        // same rejected direction over and over, while a test replay stays reproducible.
        private Random _rng = new Random(1);

        /// <summary>The spawn currently being placed, or null outside one.</summary>
        private PendingSpawn _pending;

        private sealed class PendingSpawn
        {
            public long IdentityId;
            public string FactionTag;         // null for a factionless player
            public long? ChosenPlanetId;      // respawn-screen planet choice, null for space
            public float CollisionRadiusM;
            public float PlanetDeployAltitudeM;
            public bool Applied;              // placement already decided for this spawn
            public SpawnAnchorKind AnchorKind;
            public string AnchorNodeId;
        }

        public RespawnPlacement(FactionRuntime runtime, DecisionLog log)
            : this(() => runtime != null && runtime.World != null ? runtime.World.Graph : null,
                   () => runtime != null && runtime.World != null ? runtime.World.Tuning : null,
                   () => runtime != null && runtime.World != null ? runtime.World.Features : null,
                   tag =>
                   {
                       FactionState owner = runtime != null ? runtime.FindFaction(tag) : null;
                       return owner != null ? owner.PersonalityId : null;
                   },
                   log)
        {
        }

        /// <summary>From explicit sources: on a Nexus agent, the authority's snapshot graph and
        /// factions, and this server's own tuning and authored features.</summary>
        public RespawnPlacement(Func<SpatialGraph> graph, Func<FactionTuning> tuning,
                                Func<WorldFeaturePlacement> features, Func<string, string> personalityOf,
                                DecisionLog log)
        {
            _graph = graph;
            _tuning = tuning;
            _features = features;
            _personalityOf = personalityOf;
            _log = log;
        }

        /// <summary>Subscribes to SE's post-spawn event so wayfinding runs once the character exists.</summary>
        public void Attach()
        {
            MySpaceRespawnComponent component = RespawnComponent();
            if (component == null)
            {
                FactionsPlugin.Log.Warn("RespawnPlacement: no MySpaceRespawnComponent; "
                    + "spawn placement is active but wayfinding is not.");
                return;
            }
            component.RespawnDoneEvent += OnRespawnDone;
        }

        public void Detach()
        {
            MySpaceRespawnComponent component = RespawnComponent();
            if (component != null) component.RespawnDoneEvent -= OnRespawnDone;
            _pending = null;
        }

        // Sync.Players.RespawnComponent is where SE itself registers the live instance, so it is
        // the one guaranteed to be the object SpawnAtShip is actually running on.
        private static MySpaceRespawnComponent RespawnComponent()
        {
            if (MySession.Static == null || MySession.Static.Players == null) return null;
            return MySession.Static.Players.RespawnComponent as MySpaceRespawnComponent;
        }

        // ---- spawn lifecycle, driven by the Harmony patches -------------------------------------

        /// <summary>Opens a respawn-ship spawn. Everything the placement needs that is only visible
        /// at <c>SpawnAtShip</c>'s own arguments is captured here.</summary>
        public void Begin(MyPlayer player, string respawnShipId, long? planetId)
        {
            _pending = null;
            if (player == null || player.Identity == null) return;

            MyRespawnShipDefinition ship = respawnShipId != null
                ? MyDefinitionManager.Static.GetRespawnShipDefinition(respawnShipId)
                : null;
            if (ship == null) return;

            long identityId = player.Identity.IdentityId;
            MyFaction faction = MySession.Static != null && MySession.Static.Factions != null
                ? MySession.Static.Factions.GetPlayerFaction(identityId)
                : null;

            _rng = new Random(identityId.GetHashCode());
            _pending = new PendingSpawn
            {
                IdentityId = identityId,
                FactionTag = faction != null ? faction.Tag : null,
                ChosenPlanetId = planetId,
                CollisionRadiusM = ship.Prefab != null ? ship.Prefab.BoundingSphere.Radius : 10f,
                PlanetDeployAltitudeM = ship.PlanetDeployAltitude
            };
        }

        /// <summary>Closes the spawn opened by <see cref="Begin"/>.</summary>
        public void End()
        {
            _pending = null;
        }

        // ---- vanilla's cockpit datapad, driven by RespawnDatapadPatches ----------------------------

        // Depth of SE's PutPlayerInRespawnGrid, the step that seats the arrival and, with economy on,
        // drops its own datapad into the cockpit. RespawnDoneEvent fires after it returns.
        private int _seatingDepth;

        // Whether vanilla's datapad was withheld during the seating that preceded the next
        // RespawnDoneEvent; read and cleared there.
        private bool _vanillaDatapadWithheld;

        public void BeginSeating()
        {
            _seatingDepth++;
            _vanillaDatapadWithheld = false;
        }

        public void EndSeating()
        {
            if (_seatingDepth > 0) _seatingDepth--;
        }

        /// <summary>
        /// True while a respawn ship is being boarded and wayfinding is on. Vanilla's datapad names the
        /// nearest procedural economy station within 150 km, with no zone tier and no hostility test,
        /// so it would be a second, possibly contradicting, pointer beside the one
        /// <see cref="OnRespawnDone"/> issues.
        /// </summary>
        public bool WithholdVanillaDatapad()
        {
            if (_seatingDepth <= 0) return false;
            // Not before there is a graph to point at: on an agent ahead of its first snapshot ours
            // would not be issued either, and the player would get none.
            FactionTuning t = Tuning;
            if (t == null || Graph == null || !t.RespawnWayfindingEnabled) return false;
            _vanillaDatapadWithheld = true;
            return true;
        }

        /// <summary>
        /// Offers SE's own chosen spawn position for replacement. Returns with
        /// <paramref name="position"/> untouched whenever nothing better is available — an unclaimed
        /// region, a planet with no welcoming surface station, or a placement that could not be
        /// cleared — so the fallback is always plain vanilla behaviour.
        /// </summary>
        public void Offer(ref Vector3D position, ref Vector3 forward, ref Vector3 up)
        {
            PendingSpawn pending = _pending;
            if (pending == null || pending.Applied) return;
            if (!Ready || MySession.Static == null) return;

            // Applied up front: a failure to improve on SE's answer is still a decision, and this
            // method is re-entered when GetSpawnPositionInSpace falls back to the planet path.
            pending.Applied = true;

            try
            {
                MyPlanet planet = PlanetFor(pending, position);
                Vec3 vanillaKm = ToKm(position);

                // A space spawn on a world that authors respawn regions may only land inside one:
                // mates and stations elsewhere are not anchors, and every landing spot is checked.
                WorldFeaturePlacement features = Features;
                WorldFeaturePlacement confineTo = planet == null && features.HasSpaceRespawnRegion ? features : null;
                Vec3 regionKm = default(Vec3);
                bool hasRegionPoint = confineTo != null && confineTo.TryNearestSpaceRespawnPoint(vanillaKm, out regionKm);

                var request = new SpawnAnchorRequest
                {
                    VanillaPositionKm = vanillaKm,
                    OnPlanet = planet != null,
                    PlanetId = planet != null ? planet.EntityId.ToString() : null,
                    SpaceRespawnRestricted = confineTo != null,
                    SpaceRespawnFallbackKm = hasRegionPoint ? regionKm : (Vec3?)null
                };

                FactionTuning tuning = Tuning;
                SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                    request,
                    MatePositionsKm(pending, planet, confineTo),
                    Candidates(pending),
                    tuning);

                if (choice.Kind == SpawnAnchorKind.None) return;

                Vector3D placed;
                Vector3 placedForward = forward;
                Vector3 placedUp = up;
                bool ok = planet != null
                    ? TryPlaceOnPlanet(pending, planet, choice, out placed, out placedForward, out placedUp)
                    : TryPlaceInSpace(pending, choice, confineTo, out placed);
                if (!ok)
                {
                    WriteBlocked(pending, choice);
                    // A mate or station that could not be cleared still leaves the region itself.
                    // Region fallback is None for a planet spawn or an unrestricted world.
                    choice = choice.Kind == SpawnAnchorKind.Region
                        ? SpawnAnchorChoice.None
                        : SpawnAnchorSelector.SelectRegionFallback(request, tuning);
                    if (choice.Kind == SpawnAnchorKind.None) return;
                    if (!TryPlaceInSpace(pending, choice, confineTo, out placed))
                    {
                        WriteBlocked(pending, choice);
                        return;
                    }
                }

                double movedKm = (placed - position).Length() / 1000.0;
                position = placed;
                forward = placedForward;
                up = placedUp;
                pending.AnchorKind = choice.Kind;
                pending.AnchorNodeId = choice.NodeId;

                Write("RESPAWN_PLACED identity=" + pending.IdentityId
                    + " anchor=" + choice.Kind + AnchorSuffix(choice)
                    + " radius=" + choice.RadiusM.ToString("0") + "m"
                    + " env=" + (planet != null ? "surface" : "space")
                    + " moved=" + movedKm.ToString("0.0") + "km");
            }
            catch (Exception ex)
            {
                // A spawn the player is waiting on must never be taken down by anchoring: SE's own
                // position is already in the out-parameter and remains valid.
                FactionsPlugin.Log.Warn(ex, "RespawnPlacement: placement threw; keeping SE's position.");
            }
        }

        // ---- placement ---------------------------------------------------------------------------

        /// <summary>Clears a spot near the anchor. With <paramref name="confineTo"/> set, a spot
        /// outside every respawn region is refused too: the random offset can carry a ring-edge
        /// anchor off the ring's face, and FindFreePlaceCustom can move the spot again.</summary>
        private bool TryPlaceInSpace(PendingSpawn pending, SpawnAnchorChoice choice,
            WorldFeaturePlacement confineTo, out Vector3D placed)
        {
            Vector3D anchor = ToWorld(choice.PositionKm);
            for (int i = 0; i < PlacementAttempts; i++)
            {
                Vector3D candidate = anchor + RandomUnit() * choice.RadiusM;
                // With rock shells on, rock away from every body is not spawned; the probe must see it.
                FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
                if (rock != null) rock.EnsureAround(ToKm(candidate), Tuning);
                // FindFreePlaceCustom rejects grids, voxels and safe zones alike, so this also keeps
                // the arrival outside the station's own safe zone rather than inside it.
                Vector3D? free = MyEntities.FindFreePlaceCustom(candidate, pending.CollisionRadiusM);
                if (!free.HasValue) continue;
                if (confineTo != null && !confineTo.AllowsSpaceRespawnContaining(ToKm(free.Value))) continue;
                placed = free.Value;
                return true;
            }
            placed = Vector3D.Zero;
            return false;
        }

        private bool TryPlaceOnPlanet(PendingSpawn pending, MyPlanet planet, SpawnAnchorChoice choice,
            out Vector3D placed, out Vector3 forward, out Vector3 up)
        {
            placed = Vector3D.Zero;
            forward = Vector3.Forward;
            up = Vector3.Up;

            Vector3D anchor = ToWorld(choice.PositionKm);
            Vector3D centre = planet.PositionComp.WorldVolume.Center;
            Vector3D radial = anchor - centre;
            if (radial.LengthSquared() < 1.0) return false;
            radial.Normalize();

            for (int i = 0; i < PlacementAttempts; i++)
            {
                // Offset along the surface, not through it: a straight-line offset from the anchor
                // would tilt into or out of the ground the moment the radius approaches planet scale.
                Vector3D tangent = Vector3D.Cross(radial, RandomUnit());
                // A random direction almost parallel to the radial leaves no usable tangent; take
                // another sample rather than normalising something that is numerically zero.
                if (tangent.LengthSquared() < 1e-6) continue;
                tangent.Normalize();

                Vector3D landing = planet.GetClosestSurfacePointGlobal(anchor + tangent * choice.RadiusM);
                if (!IsSurfaceEvenEnough(planet, landing, pending.CollisionRadiusM)) continue;

                // Mirrors GetShipOrientationForPlanetSpawn: deploy altitude along the local gravity
                // axis, hull upright, so the pod's own descent velocity carries it down as usual.
                Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(landing);
                Vector3D upAxis = Vector3.IsZero(gravity) ? Vector3D.Up : -Vector3D.Normalize(gravity);

                // FindFreePlaceCustom rather than the FindFreePlace vanilla's own
                // GetShipOrientationForPlanetSpawn uses: that overload is marked obsolete, and this
                // one additionally refuses a safe zone, so the drop never starts inside the
                // station's own bubble.
                Vector3D? free = MyEntities.FindFreePlaceCustom(
                    landing + upAxis * pending.PlanetDeployAltitudeM, pending.CollisionRadiusM);
                if (!free.HasValue) continue;

                placed = free.Value;
                up = upAxis;
                forward = Vector3.CalculatePerpendicularVector(upAxis);
                return true;
            }
            return false;
        }

        /// <summary>Rejects cliff faces and crevasses by sampling the true surface around the landing
        /// footprint and measuring the spread of their altitudes.</summary>
        private static bool IsSurfaceEvenEnough(MyPlanet planet, Vector3D landing, double footprintM)
        {
            Vector3D centre = planet.PositionComp.WorldVolume.Center;
            Vector3D radial = landing - centre;
            if (radial.LengthSquared() < 1.0) return false;
            radial.Normalize();

            Vector3D a = Vector3D.CalculatePerpendicularVector(radial);
            Vector3D b = Vector3D.Cross(radial, a);
            double reach = Math.Max(footprintM, 1.0);

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3D offset = (i == 0) ? a * reach
                                : (i == 1) ? a * -reach
                                : (i == 2) ? b * reach
                                           : b * -reach;
                Vector3D probe = planet.GetClosestSurfacePointGlobal(landing + offset);
                double altitude = Vector3D.Dot(probe - centre, radial);
                if (altitude < min) min = altitude;
                if (altitude > max) max = altitude;
            }
            return (max - min) <= MaxSurfaceSpreadM;
        }

        // ---- candidate assembly -------------------------------------------------------------------

        /// <summary>Every founded station the estate knows about, tagged with the two hostility
        /// signals. A node with no station has nothing to arrive next to and nothing to name.</summary>
        private List<SpawnAnchorCandidate> Candidates(PendingSpawn pending)
        {
            var list = new List<SpawnAnchorCandidate>();
            WorldFeaturePlacement features = Features;
            var xenophobiaByTag = new Dictionary<string, double>(StringComparer.Ordinal);
            var atWarByTag = new Dictionary<string, bool>(StringComparer.Ordinal);

            SpatialGraph graph = Graph;
            if (graph == null) return list;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || node.StableStationId == 0) continue;
                string tag = node.OwnerFactionTag;
                if (string.IsNullOrEmpty(tag)) continue;

                double xenophobia;
                if (!xenophobiaByTag.TryGetValue(tag, out xenophobia))
                {
                    // An unregistered or neutral holder gets the balanced profile's own xenophobia
                    // rather than a free pass -- "we know nothing about them" is not "they are safe".
                    xenophobia = Personalities.Get(_personalityOf != null ? _personalityOf(tag) : null).Traits.Xenophobia;
                    xenophobiaByTag[tag] = xenophobia;
                }

                bool atWar;
                if (!atWarByTag.TryGetValue(tag, out atWar))
                {
                    atWar = IsAtWar(pending.FactionTag, tag);
                    atWarByTag[tag] = atWar;
                }

                list.Add(new SpawnAnchorCandidate
                {
                    NodeId = node.Id,
                    DisplayName = !string.IsNullOrEmpty(node.DisplayName) ? node.DisplayName : node.Id,
                    PositionKm = node.Position,
                    Site = node.Site,
                    PlanetId = node.PlanetId,
                    OwnerTag = tag,
                    OwnerXenophobia = xenophobia,
                    AtWarWithSpawner = atWar,
                    SpaceRespawnAllowed = features.AllowsSpaceRespawnAt(node.Position)
                });
            }
            return list;
        }

        private static bool IsAtWar(string spawnerTag, string ownerTag)
        {
            if (string.IsNullOrEmpty(spawnerTag)) return false;   // factionless: at war with nobody
            if (string.Equals(spawnerTag, ownerTag, StringComparison.Ordinal)) return false;
            try
            {
                MyFactionCollection factions = MySession.Static.Factions;
                MyFaction mine = factions.TryGetFactionByTag(spawnerTag);
                MyFaction theirs = factions.TryGetFactionByTag(ownerTag);
                if (mine == null || theirs == null) return false;

                Tuple<MyRelationsBetweenFactions, int> relation =
                    factions.GetRelationBetweenFactions(mine.FactionId, theirs.FactionId);
                return relation != null && relation.Item1 == MyRelationsBetweenFactions.Enemies;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "RespawnPlacement: faction standing lookup failed for '"
                    + ownerTag + "'.");
                return false;
            }
        }

        /// <summary>
        /// Online faction mates that share the spawn's environment. SE's own friendly-position scan
        /// is not reused: it also returns the spawning player's own body and shuffles the result,
        /// and both would defeat a nearest-mate rule. With <paramref name="confineTo"/> set, a mate
        /// outside every respawn region is not one to spawn beside.
        /// </summary>
        private List<Vec3> MatePositionsKm(PendingSpawn pending, MyPlanet planet, WorldFeaturePlacement confineTo)
        {
            var mates = new List<Vec3>();
            if (string.IsNullOrEmpty(pending.FactionTag)) return mates;

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.Identity == null) continue;
                if (player.Identity.IdentityId == pending.IdentityId) continue;

                MyCharacter character = player.Character;
                if (character == null || character.IsDead || character.MarkedForClose) continue;

                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(player.Identity.IdentityId);
                if (faction == null || !string.Equals(faction.Tag, pending.FactionTag, StringComparison.Ordinal))
                    continue;

                Vector3D pos = character.PositionComp.GetPosition();
                // A planet choice is the player's own; a mate elsewhere never pulls the spawn off it,
                // and a mate standing on some planet never drags a space spawn down a gravity well.
                if (planet != null ? !IsInside(planet, pos) : IsInsideAnyPlanet(pos)) continue;
                Vec3 posKm = ToKm(pos);
                if (confineTo != null && !confineTo.AllowsSpaceRespawnContaining(posKm)) continue;
                mates.Add(posKm);
            }
            return mates;
        }

        private static bool IsInside(MyPlanet planet, Vector3D pos)
        {
            return planet != null
                && planet.PositionComp.WorldAABB.Contains(pos) != ContainmentType.Disjoint;
        }

        private static bool IsInsideAnyPlanet(Vector3D pos)
        {
            return IsInside(MyGamePruningStructure.GetClosestPlanet(pos), pos);
        }

        /// <summary>
        /// The planet a spawn is happening on, or null for a space spawn. The player's respawn-screen
        /// choice decides it when there is one; otherwise SE's own position does, which is what makes
        /// this correct on <c>GetSpawnPositionInSpace</c>'s planet fallback too.
        /// </summary>
        private static MyPlanet PlanetFor(PendingSpawn pending, Vector3D vanillaPosition)
        {
            if (pending.ChosenPlanetId.HasValue)
            {
                MyEntity entity;
                if (MyEntities.TryGetEntityById(pending.ChosenPlanetId.Value, out entity, false))
                {
                    var planet = entity as MyPlanet;
                    if (planet != null) return planet;
                }
            }
            MyPlanet closest = MyGamePruningStructure.GetClosestPlanet(vanillaPosition);
            return IsInside(closest, vanillaPosition) ? closest : null;
        }

        // ---- wayfinding ---------------------------------------------------------------------------

        private void OnRespawnDone(ulong steamId)
        {
            bool vanillaWithheld = _vanillaDatapadWithheld;
            _vanillaDatapadWithheld = false;
            try
            {
                if (!Ready || MySession.Static == null) return;
                if (!Tuning.RespawnWayfindingEnabled) return;

                MyPlayer player;
                if (!MySession.Static.Players.TryGetPlayerBySteamId(steamId, out player)) return;
                if (player == null || player.Identity == null || player.Character == null) return;

                long identityId = player.Identity.IdentityId;
                Vector3D pos = player.Character.PositionComp.GetPosition();
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(pos);
                bool onPlanet = IsInside(planet, pos);

                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
                var pending = new PendingSpawn
                {
                    IdentityId = identityId,
                    FactionTag = faction != null ? faction.Tag : null
                };

                var request = new SpawnAnchorRequest
                {
                    VanillaPositionKm = ToKm(pos),
                    OnPlanet = onPlanet,
                    PlanetId = onPlanet ? planet.EntityId.ToString() : null,
                    SpaceRespawnRestricted = !onPlanet && Features.HasSpaceRespawnRegion
                };

                SpawnAnchorCandidate target = SpawnAnchorSelector.SelectWayfindingTarget(
                    request, ToKm(pos), Candidates(pending), Tuning);
                if (target == null)
                {
                    Write("RESPAWN_WAYFIND_NONE identity=" + identityId
                        + " env=" + (onPlanet ? "surface" : "space"));
                    return;
                }

                Vector3D targetWorld = ToWorld(target.PositionKm);
                int retired = SendGps(identityId, target, targetWorld);
                bool datapad = TryGiveDatapad(player, target, targetWorld);

                Write("RESPAWN_WAYFIND identity=" + identityId
                    + " node=" + target.NodeId + " owner=" + target.OwnerTag
                    + " site=" + target.Site
                    + " dist=" + ((targetWorld - pos).Length() / 1000.0).ToString("0.0") + "km"
                    + " datapad=" + datapad
                    + " retired=" + retired
                    + " vanilla_datapad=" + (vanillaWithheld ? "withheld" : "none"));
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "RespawnPlacement: wayfinding failed for steam=" + steamId + ".");
            }
        }

        /// <summary>Sends the welcome GPS and deletes every earlier one, so a player carries exactly one:
        /// each respawn's marker names the station chosen for that arrival, and the ones before it
        /// point back at wherever they spawned last. Returns how many were deleted.</summary>
        private static int SendGps(long identityId, SpawnAnchorCandidate target, Vector3D world)
        {
            var gps = new MyGps
            {
                // The name is what SE's GPS list shows; the description is what marks it as ours.
                Name = target.DisplayName,
                DisplayName = target.DisplayName,
                Description = Factions.Wire.WelcomeGps.Description(target.DisplayName, target.OwnerTag),
                Coords = world,
                GPSColor = WelcomeGpsColour,
                ShowOnHud = true,
                AlwaysVisible = false,
                // Permanent, unlike the scanner's contacts: an arrival's one destination should not
                // quietly expire while they are still working out which way is up.
                DiscardAt = null
            };
            gps.UpdateHash();
            int retired = RetireWelcomeGpss(identityId, gps.Hash);
            MySession.Static.Gpss.SendAddGpsRequest(identityId, ref gps, 0L, playSoundOnCreation: false);
            return retired;
        }

        // A marker identical to the new one is kept rather than deleted and re-added: SE refuses an
        // add whose hash it already holds, so it simply stays.
        private static int RetireWelcomeGpss(long identityId, int keepHash)
        {
            var gpss = new List<IMyGps>();
            MySession.Static.Gpss.GetGpsList(identityId, gpss);
            int retired = 0;
            foreach (IMyGps old in gpss)
            {
                if (old == null || old.Hash == keepHash) continue;
                if (!Factions.Wire.WelcomeGps.IsWelcome(old.Name, old.Description)) continue;
                MySession.Static.Gpss.SendDeleteGpsRequest(identityId, old.Hash);
                retired++;
            }
            return retired;
        }

        /// <summary>
        /// Puts a datapad naming the station into the arrival's own inventory. Mirrors the flavour of
        /// vanilla's economy-station datapad (<c>MySessionComponentEconomy.TryFillStoryDatapad</c>),
        /// which resolves its station through <c>GetRandomStation</c> and so can name one anywhere in
        /// the system; this one names the station actually chosen for this arrival.
        /// </summary>
        private static bool TryGiveDatapad(MyPlayer player, SpawnAnchorCandidate target, Vector3D world)
        {
            try
            {
                var inventory = player.Character.GetInventory() as MyInventory;
                if (inventory == null) return false;

                var defId = new MyDefinitionId(typeof(MyObjectBuilder_Datapad), "Datapad");
                if (MyDefinitionManager.Static.GetPhysicalItemDefinition(defId) == null) return false;
                if (inventory.ComputeAmountThatFits(defId) < 1) return false;

                var datapad = MyObjectBuilderSerializer.CreateNewObject(defId) as MyObjectBuilder_Datapad;
                if (datapad == null) return false;

                datapad.Name = target.DisplayName;
                datapad.Data = target.DisplayName + " [" + target.OwnerTag + "]"
                    + Environment.NewLine + Environment.NewLine
                    + "The nearest station willing to trade with a stranger."
                    + Environment.NewLine
                    + GpsText(target.DisplayName, Vector3D.Round(world, 0), WelcomeGpsColour);

                inventory.AddItems((MyFixedPoint)1, datapad);
                return true;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "RespawnPlacement: could not issue the welcome datapad.");
                return false;
            }
        }

        // ---- helpers -------------------------------------------------------------------------------

        // MyGps' own SE-standard marker blue. Its ConvertToString is internal, so the wire format is
        // reproduced here instead: GPS:name:x:y:z:#AARRGGBB: with invariant numbers, exactly what a
        // player can paste back into their GPS list.
        private static readonly Color WelcomeGpsColour = new Color(117, 201, 241);

        private static string GpsText(string name, Vector3D coords, Color colour)
        {
            var rgba = new ColorDefinitionRGBA(colour.R, colour.G, colour.B, colour.A);
            return "GPS:" + name
                + ":" + coords.X.ToString(CultureInfo.InvariantCulture)
                + ":" + coords.Y.ToString(CultureInfo.InvariantCulture)
                + ":" + coords.Z.ToString(CultureInfo.InvariantCulture)
                + ":" + rgba.Hex + ":";
        }

        private void WriteBlocked(PendingSpawn pending, SpawnAnchorChoice choice)
        {
            Write("RESPAWN_PLACE_BLOCKED identity=" + pending.IdentityId
                + " anchor=" + choice.Kind + AnchorSuffix(choice)
                + " radius=" + choice.RadiusM.ToString("0") + "m");
        }

        private static string AnchorSuffix(SpawnAnchorChoice choice)
        {
            return choice.NodeId != null ? " node=" + choice.NodeId : string.Empty;
        }

        private void Write(string line)
        {
            if (_log != null) _log.Write("respawn " + line);
        }

        private Vector3D RandomUnit()
        {
            // Marsaglia: uniform on the sphere, matching VanillaFleetSpawner.PickSpawnPos.
            double cosP = _rng.NextDouble() * 2.0 - 1.0;
            double sinP = Math.Sqrt(Math.Max(0.0, 1.0 - cosP * cosP));
            double theta = _rng.NextDouble() * Math.PI * 2.0;
            return new Vector3D(sinP * Math.Cos(theta), sinP * Math.Sin(theta), cosP);
        }

        private static Vec3 ToKm(Vector3D world)
        {
            return new Vec3(world.X / 1000.0, world.Y / 1000.0, world.Z / 1000.0);
        }

        private static Vector3D ToWorld(Vec3 km)
        {
            return new Vector3D(km.X * 1000.0, km.Y * 1000.0, km.Z * 1000.0);
        }
    }
}
