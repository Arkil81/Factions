using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Realises a <see cref="WorldPlan"/>: creates the planned bodies and, on request, emits the
    /// authored <c>world_features.json</c> whose anchors point at them.
    ///
    /// <para><b>Apply fails closed.</b> A planet's voxel storage is baked into the save the moment it
    /// is created, so there is no un-spawn that leaves the save as it was. The first failure stops
    /// the run and says the world must be discarded and rebuilt from a fresh one — it does not
    /// continue, and there is no resume. Operator decision, 2026-08-24.</para>
    ///
    /// <para>Every method here touches SE entities and definitions: game thread only. Bridge verbs
    /// already run there, from the plugin's own <c>Update()</c>.</para>
    /// </summary>
    internal static class WorldBuilder
    {
        /// <summary>How far a realised centre may sit from its planned one before the run stops.
        /// The min-corner conversion is exact arithmetic, so any drift at all means SE's storage
        /// sizing has moved out from under the plan.</summary>
        private const double CentreToleranceM = 1.0;

        /// <summary><c>MyGuiScreenDebugSpawnMenu</c> is internal, so its public replication helper
        /// needs reflection. Absent, bodies still exist server-side but a connected client never
        /// builds them.</summary>
        private static readonly MethodInfo SpawnPlanetClientModApi = ResolveClientSpawn();

        private static MethodInfo ResolveClientSpawn()
        {
            try
            {
                Type t = typeof(MyPlanet).Assembly.GetType("Sandbox.Game.Gui.MyGuiScreenDebugSpawnMenu");
                return t == null ? null : t.GetMethod("SpawnPlanetClientModApi",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "WorldBuilder: could not resolve SpawnPlanetClientModApi.");
                return null;
            }
        }

        // ---- reading the world ---------------------------------------------------------------

        /// <summary>Live bodies, by name. Stage 1's only precondition is that this is empty.
        /// <para>Copied out of <see cref="MyPlanets"/>' own list rather than filtered out of
        /// <c>MyEntities.GetEntities()</c>: the latter walks every entity in the world -- thousands
        /// of asteroid voxel maps -- to reject all but a handful (FAC-265). Copied, not returned by
        /// reference, because callers hold the result while SE keeps mutating its list.</para>
        /// </summary>
        public static List<MyPlanet> LivePlanets()
        {
            var planets = new List<MyPlanet>();
            List<MyPlanet> live = MyPlanets.GetPlanets();
            if (live == null) return planets;
            for (int i = 0; i < live.Count; i++)
                if (live[i] != null) planets.Add(live[i]);
            return planets;
        }

        /// <summary>
        /// Everything already in the sector that a new body must not be created on top of. Today that
        /// is faction station records — including unmaterialized ones, which is the case that matters:
        /// a body placed over one collides with nothing and instead wraps a planet around a station
        /// that materializes inside it the first time a player goes near.
        /// </summary>
        public static List<PlacementObstacle> CollectObstacles()
        {
            var obstacles = new List<PlacementObstacle>();
            if (MySession.Static == null || MySession.Static.Factions == null) return obstacles;

            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction faction = kv.Value;
                if (faction == null) continue;
                foreach (var station in faction.Stations)
                {
                    if (station == null) continue;
                    Vector3D p = station.Position;
                    obstacles.Add(new PlacementObstacle(
                        faction.Tag + " " + (string.IsNullOrEmpty(station.Name) ? "station" : station.Name),
                        new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0)));
                }
            }
            return obstacles;
        }

        // ---- applying --------------------------------------------------------------------------

        public sealed class ApplyResult
        {
            public readonly List<string> Created = new List<string>();
            public string Failure;
            public bool Aborted { get { return !string.IsNullOrEmpty(Failure); } }
        }

        /// <summary>
        /// Creates every body in <paramref name="plan"/>, verifying each landed where it was planned.
        /// Stops at the first failure without touching the rest.
        /// </summary>
        public static ApplyResult Apply(WorldPlan plan, IPlanetTypeCatalogue types)
        {
            var result = new ApplyResult();
            if (plan == null) { result.Failure = "no plan."; return result; }
            if (!plan.IsApplicable) { result.Failure = "the plan is not applicable; nothing was created."; return result; }

            foreach (PlannedPlanet pp in plan.Planets)
            {
                string failure;
                if (!CreateOne(pp, out failure))
                {
                    // Fail closed: everything before this exists and cannot be undone.
                    result.Failure = pp.Name + ": " + failure
                        + (result.Created.Count > 0
                            ? " -- " + result.Created.Count + " body/bodies were already created ("
                              + string.Join(", ", result.Created.ToArray())
                              + "). This world is half-built: discard it and apply to a fresh one."
                            : " -- nothing was created.");
                    return result;
                }
                result.Created.Add(pp.Name);
            }
            return result;
        }

        private static bool CreateOne(PlannedPlanet pp, out string failure)
        {
            var minCornerM = new Vector3D(pp.MinCornerKm.X * 1000.0, pp.MinCornerKm.Y * 1000.0, pp.MinCornerKm.Z * 1000.0);
            float sizeM = (float)(pp.DiameterKm * 1000.0);

            MyPlanet planet;
            try
            {
                planet = MyWorldGenerator.AddPlanet(pp.StorageName, pp.Name, pp.Definition,
                    minCornerM, pp.Seed, sizeM, false, 0L, false, false);
            }
            catch (Exception ex)
            {
                failure = "AddPlanet threw -- " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            if (planet == null)
            {
                // The only signal SE gives. MaxPlanets is validated at plan time precisely because
                // there is nothing to inspect once this has happened.
                failure = "AddPlanet returned null (definition '" + pp.Definition + "', size "
                        + pp.DiameterKm.ToString("0.#") + " km). Check MaxPlanets against the plan's "
                        + "distinct type count, and that the definition resolves.";
                return false;
            }

            // The min-corner offset is a power-of-two storage cube, not half the diameter. The
            // arithmetic is exact, so any drift means SE's sizing has moved -- and a body that is
            // 16 km off takes every feature anchored to it along.
            Vector3D realised = planet.PositionComp.GetPosition();
            var realisedKm = new Vec3(realised.X / 1000.0, realised.Y / 1000.0, realised.Z / 1000.0);
            double driftM = realisedKm.DistanceTo(pp.CenterKm) * 1000.0;
            if (driftM > CentreToleranceM)
            {
                failure = "landed " + driftM.ToString("0.##") + " m from its planned centre (planned "
                        + pp.CenterKm + " km, realised " + realisedKm + " km). The storage-size "
                        + "arithmetic no longer matches SE's.";
                return false;
            }

            pp.RealisedEntityId = planet.EntityId;
            ReplicateToClients(pp, minCornerM, sizeM);

            FactionsPlugin.Log.Info("WorldBuilder: created " + pp.Name + " (" + pp.Definition + ", "
                + pp.DiameterKm.ToString("0.#") + " km, seed " + pp.Seed + ") at " + realisedKm
                + " km, entityId=" + planet.EntityId + ".");
            failure = null;
            return true;
        }

        /// <summary><c>AddPlanet</c> is server-side only; SE's own ModAPI path follows it with this
        /// event so connected clients build the same body.</summary>
        private static void ReplicateToClients(PlannedPlanet pp, Vector3D minCornerM, float sizeM)
        {
            if (SpawnPlanetClientModApi == null)
            {
                FactionsPlugin.Log.Warn("WorldBuilder: no client replication for " + pp.Name
                    + " -- anyone connected will not see it until they reconnect.");
                return;
            }
            try
            {
                SpawnPlanetClientModApi.Invoke(null, new object[]
                {
                    pp.Name, pp.StorageName, sizeM, pp.Seed, minCornerM, pp.RealisedEntityId
                });
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "WorldBuilder: client replication failed for " + pp.Name + ".");
            }
        }

        // ---- emitting the features file --------------------------------------------------------

        /// <summary>
        /// Writes <c>world_features.json</c> with every anchor resolved to the entity id of the body
        /// just created, and <c>Center</c> set to that body's realised centre so the loader's
        /// no-anchor fallback lands on it rather than at the world origin.
        /// <para>A planet's orbit band (<see cref="PlannedFeature.OrbitRegionWidthKm"/>) takes its
        /// inner radius from <paramref name="gravityRadiusKm"/>, the live body's gravity radius by
        /// entity id. A band whose body has none is left out and named in the result: written at
        /// radius 0 it would claim the planet's whole well.</para>
        /// </summary>
        public static string EmitFeatures(WorldPlan plan, string dataDir, string instanceDir,
                                          Func<long, double> gravityRadiusKm)
        {
            if (plan == null || plan.Features.Count == 0) return "features: nothing to emit.";

            string conflict;
            if (HasConflictingCopy(dataDir, instanceDir, out conflict)) return conflict;

            var byName = new Dictionary<string, PlannedPlanet>(StringComparer.OrdinalIgnoreCase);
            foreach (PlannedPlanet pp in plan.Planets) byName[pp.Name] = pp;

            var doc = new FeatureDocument();
            int anchored = 0;
            var skipped = new List<string>();
            foreach (PlannedFeature pf in plan.Features)
            {
                PlannedPlanet body;
                double gravityKm = !string.IsNullOrEmpty(pf.AnchorPlanetName)
                                   && byName.TryGetValue(pf.AnchorPlanetName, out body)
                                   && body.RealisedEntityId != 0L && gravityRadiusKm != null
                    ? gravityRadiusKm(body.RealisedEntityId) : 0;
                double innerKm, outerKm;
                if (!pf.TryResolveRadii(gravityKm, out innerKm, out outerKm)) { skipped.Add(pf.Name); continue; }

                var feature = new WorldFeature
                {
                    Name = pf.Name,
                    Shape = pf.Shape,
                    Center = pf.CenterKm,
                    Normal = pf.Normal,
                    InnerRadiusKm = innerKm,
                    OuterRadiusKm = outerKm,
                    HalfThicknessKm = pf.HalfThicknessKm,
                    Density = pf.Density,
                    NodeSeparationKm = pf.NodeSeparationKm,
                    Bands = pf.Bands,
                    Materials = pf.Materials,
                    Zone = pf.Zone,
                    SpaceRespawn = pf.SpaceRespawn,
                    GivesUpHole = pf.GivesUpHole
                };
                PlannedPlanet anchor;
                if (!string.IsNullOrEmpty(pf.AnchorPlanetName) && byName.TryGetValue(pf.AnchorPlanetName, out anchor)
                    && anchor.RealisedEntityId != 0L)
                {
                    feature.AnchorPlanetId = anchor.RealisedEntityId.ToString();
                    anchored++;
                }
                doc.Features.Add(feature);
            }

            string path = Path.Combine(dataDir, WorldFeatureFile.FileName);
            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(doc, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                return "features: write FAILED -- " + ex.GetType().Name + ": " + ex.Message;
            }

            return "features: wrote " + doc.Features.Count + " (" + anchored + " anchored) to " + path
                 + (skipped.Count > 0
                     ? "; LEFT OUT " + skipped.Count + " orbit band(s) whose body has no live gravity radius: "
                       + string.Join(", ", skipped.ToArray())
                     : "");
        }

        /// <summary>
        /// A features file in the directory the loader will NOT read is going to either silently win
        /// or silently lose, because <see cref="DataDirectory.ResolveInput"/> falls back only when the
        /// primary is absent. Neither is a state worth writing into.
        /// </summary>
        private static bool HasConflictingCopy(string dataDir, string instanceDir, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(instanceDir) || string.IsNullOrEmpty(dataDir)) return false;
            if (string.Equals(dataDir, instanceDir, StringComparison.OrdinalIgnoreCase)) return false;

            string other = Path.Combine(instanceDir, WorldFeatureFile.FileName);
            if (!File.Exists(other)) return false;

            why = "features: REFUSED -- a " + WorldFeatureFile.FileName + " already exists at " + other
                + ", which is not the directory the loader reads (" + dataDir + "). It would silently "
                + "win or silently lose. Remove it, then emit again.";
            return true;
        }

        private sealed class FeatureDocument
        {
            public List<WorldFeature> Features = new List<WorldFeature>();
        }

        // ---- reporting -------------------------------------------------------------------------

        /// <summary>Renders a plan as the diff it is: what would be created, where, and every
        /// constraint that had to be retried or could not be met.</summary>
        public static string Describe(WorldPlan plan, int livePlanetCount, int obstacleCount)
        {
            if (plan == null) return "plan: nothing to describe.";
            var sb = new StringBuilder();
            sb.Append("plan: ").Append(plan.Planets.Count).Append(" planet(s), ")
              .Append(plan.Features.Count).Append(" feature(s), ")
              .Append(plan.DistinctDefinitionCount).Append(" distinct type(s); checked against ")
              .Append(obstacleCount).Append(" existing object(s); world holds ")
              .Append(livePlanetCount).Append(" planet(s).");

            foreach (PlannedPlanet pp in plan.Planets)
            {
                sb.Append("\n  ").Append(pp.Placed ? "+" : "!").Append(' ').Append(pp.Name)
                  .Append(" [").Append(pp.Definition).Append("] ")
                  .Append(pp.DiameterKm.ToString("0.#")).Append(" km seed=").Append(pp.Seed);
                if (pp.Placed)
                    sb.Append(" centre=").Append(pp.CenterKm).Append(" km gravity=")
                      .Append(pp.GravityLimitKm.ToString("0.#")).Append(" km exclusion=")
                      .Append(pp.ExclusionRadiusKm.ToString("0.#")).Append(" km")
                      .Append(pp.CenterWasAuthored ? " (authored)" : " (placed in " + pp.Attempts + " try)");
                else
                    sb.Append(" UNPLACED -- ").Append(pp.Rejection);
            }

            foreach (PlannedFeature pf in plan.Features)
                sb.Append("\n  ~ ").Append(pf.Name).Append(' ').Append(pf.Shape)
                  .Append(string.IsNullOrEmpty(pf.AnchorPlanetName) ? " (free)" : " -> " + pf.AnchorPlanetName)
                  .Append(' ').Append(pf.OrbitRegionWidthKm > 0
                      ? "gravity radius + " + pf.OrbitRegionWidthKm.ToString("0.#") + " km (read at emit)"
                      : pf.OuterRadiusKm.ToString("0.#") + " km");

            foreach (string note in plan.Notes) sb.Append("\n  . ").Append(note);
            foreach (string error in plan.Errors) sb.Append("\n  ERROR ").Append(error);
            return sb.ToString();
        }
    }
}
