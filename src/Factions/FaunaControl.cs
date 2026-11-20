using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using SpaceEngineers.Game.AI;
using Sandbox.Definitions;
using VRage.Game;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Applies the recipe's authored animal spawn rules (<see cref="FaunaPolicy"/>) in place of a
    /// generator's own <c>AnimalSpawnInfo</c>, at the one method SE asks.
    ///
    /// <para><b>Two hooks, and they are applied as a pair.</b>
    /// <c>MySpaceBotFactory.GetDayOrNightAnimalSpawnInfo(MyPlanet, Vector3D)</c> is the only reader of
    /// a body's spawn lists that matters: every wave, spawn delay, spawn distance and kill delay
    /// <c>MySpaceFaunaComponent</c> uses comes through it, handed the player's position. But that
    /// component registers a body for spawning only if the private <c>PlanetHasFauna</c> finds a
    /// non-empty <b>day</b> list on its generator — once when the planet is added, and again on every
    /// update. A Cauldron body with no native animals therefore needs the second hook to be visited at
    /// all. The registration hook is installed only once the spawn-info hook is bound: the component
    /// dereferences the spawn info without a null check (<c>SpawnInfo</c>'s constructor), so a body
    /// registered here that vanilla then answers with null would throw on every fauna update.</para>
    ///
    /// <para><b>Never hands SE a null or an empty list to spawn from.</b> An authored body where no rule
    /// matches and the generator has nothing gets <see cref="Silent"/>: an empty animal list and a wave
    /// of zero. SE draws <c>GetRandomInt(0, 0)</c> bots from it — none — and never indexes the list.
    /// The same object is what a rule with an empty <see cref="FaunaRule.Animals"/> means.</para>
    ///
    /// <para><b>Server-side only.</b> The component returns early on a client, and bots replicate; a
    /// client needs the creature mods for their models and nothing from Factions.</para>
    ///
    /// <para>Provenance: FAC-471; design in docs/world-builder.md, "Fauna authored in the recipe".</para>
    /// </summary>
    internal static class FaunaControl
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static FaunaPolicy _policy;

        /// <summary>One SE spawn info per authored rule, built on first use: the generator's numbers
        /// are its base, and only an authored value replaces one.</summary>
        private static readonly Dictionary<FaunaRule, MyPlanetAnimalSpawnInfo> _built =
            new Dictionary<FaunaRule, MyPlanetAnimalSpawnInfo>();

        private static MyPlanetAnimalSpawnInfo _silent;

        private static FieldInfo _storageData;
        private static FieldInfo _storageSeed;

        /// <summary>Toggled by <c>factions debug fauna</c>. Per-process, default off.</summary>
        internal static bool DebugEnabled;

        /// <summary>The spawn-info hook is in place.</summary>
        internal static bool Bound { get; private set; }

        /// <summary>The registration hook is in place.</summary>
        internal static bool RegistrationBound { get; private set; }

        internal static string InactiveReason { get; private set; }

        /// <summary>The reason given when there is nothing to hook: not a fault.</summary>
        internal const string NothingAuthored = "no fauna authored";

        /// <summary>Spawn-info asks answered from an authored rule, from the generator's own lists, and
        /// with <see cref="Silent"/>, this process. Asks also come from spider pathing, so these count
        /// questions, not waves.</summary>
        internal static int Authored { get; private set; }
        internal static int Native { get; private set; }
        internal static int Silenced { get; private set; }

        /// <summary>Bodies this process registered for spawning that their generator would not have.
        /// </summary>
        internal static int Registered { get; private set; }

        /// <summary>Hands the authored policy to the hooks. Called from plugin <c>Init</c>, before the
        /// world loads: SE registers a body for spawning when the planet entity is added.</summary>
        internal static void Load(FaunaPolicy policy)
        {
            _policy = policy;
            _built.Clear();
        }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) { InactiveReason = "no Harmony instance"; return; }
            if (_policy == null || _policy.Count == 0) { InactiveReason = NothingAuthored; return; }
            try
            {
                MethodInfo spawnInfo = AccessTools.Method(typeof(MySpaceBotFactory), "GetDayOrNightAnimalSpawnInfo",
                    new[] { typeof(MyPlanet), typeof(Vector3D) });
                if (spawnInfo == null)
                {
                    InactiveReason = "MySpaceBotFactory.GetDayOrNightAnimalSpawnInfo(MyPlanet, Vector3D) did not resolve";
                    return;
                }
                harmony.Patch(spawnInfo,
                    prefix: new HarmonyMethod(typeof(FaunaControl).GetMethod("SpawnInfoPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(FaunaControl).GetMethod("SpawnInfoPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                Bound = true;

                MethodInfo hasFauna = AccessTools.Method(typeof(MySpaceFaunaComponent), "PlanetHasFauna",
                    new[] { typeof(MyPlanet) });
                if (hasFauna == null)
                {
                    InactiveReason = "MySpaceFaunaComponent.PlanetHasFauna(MyPlanet) did not resolve; a body whose "
                                   + "generator has no day animals spawns nothing";
                    return;
                }
                harmony.Patch(hasFauna,
                    postfix: new HarmonyMethod(typeof(FaunaControl).GetMethod("HasFaunaPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                RegistrationBound = true;
            }
            catch (Exception ex)
            {
                InactiveReason = "patch threw: " + ex.Message;
            }
        }

        /// <summary>Answers from the first authored rule that holds at the player's position, or lets
        /// SE answer from the generator.</summary>
        private static bool SpawnInfoPrefix(MyPlanet planet, Vector3D position, ref MyPlanetAnimalSpawnInfo __result,
                                            out bool __state)
        {
            // True while SE answers an authored body itself, so the postfix can tell a native answer
            // from a null it must replace.
            __state = false;
            try
            {
                FaunaPolicy.Entry body = BodyOf(planet);
                if (body == null) return true;

                FaunaSite site = SiteAt(planet, position, body);
                FaunaRule rule = FaunaPolicy.Match(body, site);
                if (rule == null)
                {
                    __state = true;
                    Trace(body, site, "native", null);
                    return true;
                }

                __result = rule.Animals.Count == 0 ? Silent() : Build(rule, planet.Generator);
                if (rule.Animals.Count == 0) Silenced++; else Authored++;
                Trace(body, site, rule.Animals.Count == 0 ? "silent" : "authored", rule);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Fauna control: spawn-info prefix failed; the generator's own animals apply.");
                return true;
            }
        }

        /// <summary>An authored body is never answered with null: it may be registered only because it
        /// is authored, and SE does not null-check the answer. Runs after a skipped original too, which
        /// is why it reads <c>__state</c> rather than the result alone.</summary>
        private static void SpawnInfoPostfix(ref MyPlanetAnimalSpawnInfo __result, bool __state)
        {
            try
            {
                if (!__state) return;
                if (__result != null) { Native++; return; }
                __result = Silent();
                Silenced++;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Fauna control: spawn-info postfix failed.");
            }
        }

        private static void HasFaunaPostfix(MyPlanet planet, ref bool __result)
        {
            try
            {
                if (__result || !Bound) return;
                FaunaPolicy.Entry body = BodyOf(planet);
                if (body == null || !FaunaPolicy.HasAnimals(body)) return;
                __result = true;
                // Asked once at registration and then every fauna update; count the registration only.
                if (_registeredIds.Add(planet.EntityId))
                {
                    Registered++;
                    Log.Info("Fauna: body registered for spawning from the recipe: " + body.Name
                           + " generator=" + body.Generator + " rules=" + body.Rules.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Fauna control: registration postfix failed.");
            }
        }

        private static readonly HashSet<long> _registeredIds = new HashSet<long>();

        private static FaunaPolicy.Entry BodyOf(MyPlanet planet)
        {
            if (_policy == null || _policy.Count == 0 || planet == null || planet.Generator == null) return null;
            FaunaPolicy.Entry body;
            return _policy.TryGetBody(planet.Generator.Id.SubtypeName, SeedOf(planet), out body) ? body : null;
        }

        /// <summary>Gathers only what the body's rules consult: a night test, a surface read, a scan of
        /// the node graph.</summary>
        private static FaunaSite SiteAt(MyPlanet planet, Vector3D position, FaunaPolicy.Entry body)
        {
            var site = new FaunaSite();
            if (FaunaPolicy.AsksNight(body))
                site.IsNight = MySectorWeatherComponent.IsThereNight(planet, ref position);

            if (FaunaPolicy.AsksTerrain(body))
            {
                // SE's own latitude is the Y component of the unit direction from the centre (the
                // material rules compare its sine); bodies are not rotated, so world Y is the axis.
                Vector3D centre = planet.PositionComp.GetPosition();
                Vector3D dir = position - centre;
                double len = dir.Length();
                if (len > 1e-3)
                {
                    double sinLat = Math.Max(-1.0, Math.Min(1.0, dir.Y / len));
                    site.LatitudeDeg = Math.Asin(sinLat) * 180.0 / Math.PI;
                }
                Vector3D ground = planet.GetClosestSurfacePointGlobal(ref position);
                site.AltitudeM = Math.Max(0.0, (ground - centre).Length() - planet.MinimumRadius);
            }

            if (FaunaPolicy.AsksNodes(body))
                site.NearestNodeKm = NearestNodeKm(position);
            return site;
        }

        private static double? NearestNodeKm(Vector3D position)
        {
            WorldModel world = FactionsPlugin.Instance == null || FactionsPlugin.Instance.Runtime == null
                ? null : FactionsPlugin.Instance.Runtime.World;
            if (world == null || world.Graph == null || world.Graph.Nodes == null) return null;

            var here = new Vec3(position.X / 1000.0, position.Y / 1000.0, position.Z / 1000.0);
            double best = double.MaxValue;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                double d2 = n.Position.DistanceSquaredTo(here);
                if (d2 < best) best = d2;
            }
            return best == double.MaxValue ? (double?)null : Math.Sqrt(best);
        }

        private static MyPlanetAnimalSpawnInfo Build(FaunaRule rule, MyPlanetGeneratorDefinition generator)
        {
            MyPlanetAnimalSpawnInfo info;
            if (_built.TryGetValue(rule, out info)) return info;

            MyPlanetAnimalSpawnInfo native = generator == null ? null
                : (rule.When == FaunaTime.Night ? generator.NightAnimalSpawnInfo ?? generator.AnimalSpawnInfo
                                                : generator.AnimalSpawnInfo ?? generator.NightAnimalSpawnInfo);
            info = new MyPlanetAnimalSpawnInfo();
            if (native != null)
            {
                info.SpawnDelayMin = native.SpawnDelayMin;
                info.SpawnDelayMax = native.SpawnDelayMax;
                info.SpawnDistMin  = native.SpawnDistMin;
                info.SpawnDistMax  = native.SpawnDistMax;
                info.KillDelay     = native.KillDelay;
                info.WaveCountMin  = native.WaveCountMin;
                info.WaveCountMax  = native.WaveCountMax;
            }

            var animals = new MyPlanetAnimal[rule.Animals.Count];
            for (int i = 0; i < animals.Length; i++) animals[i] = new MyPlanetAnimal { AnimalType = rule.Animals[i] };
            info.Animals = animals;

            if (rule.DelaySeconds != null)
            {
                info.SpawnDelayMin = Ms(rule.DelaySeconds[0]);
                info.SpawnDelayMax = Ms(rule.DelaySeconds[1]);
            }
            if (rule.DistanceM != null)
            {
                info.SpawnDistMin = (float)rule.DistanceM[0];
                info.SpawnDistMax = (float)rule.DistanceM[1];
            }
            if (rule.Wave != null)
            {
                // SE's GetRandomInt excludes its maximum; the recipe's range is inclusive.
                info.WaveCountMin = rule.Wave[0];
                info.WaveCountMax = rule.Wave[1] + 1;
            }
            if (rule.KillDelaySeconds.HasValue) info.KillDelay = Ms(rule.KillDelaySeconds.Value);

            _built[rule] = info;
            return info;
        }

        /// <summary>What an authored "nothing spawns here" is handed to SE as: no animals, a wave of
        /// zero, and SE's own default delays so its spawn-point bookkeeping runs as for any body.</summary>
        private static MyPlanetAnimalSpawnInfo Silent()
        {
            if (_silent == null)
                _silent = new MyPlanetAnimalSpawnInfo
                {
                    Animals = new MyPlanetAnimal[0],
                    WaveCountMin = 0,
                    WaveCountMax = 0,
                };
            return _silent;
        }

        private static int Ms(double seconds)
        {
            return (int)Math.Round(Math.Min(seconds * 1000.0, int.MaxValue));
        }

        /// <summary>The terrain seed, read only when two authored bodies share a generator — the one
        /// case <see cref="FaunaPolicy.TryGetBody"/> consults it. The provider keeps it in a private
        /// struct.</summary>
        private static int SeedOf(MyPlanet planet)
        {
            try
            {
                MyPlanetStorageProvider provider = planet.Provider;
                if (provider == null) return 0;
                if (_storageData == null)
                {
                    _storageData = AccessTools.Field(typeof(MyPlanetStorageProvider), "m_data");
                    if (_storageData == null) return 0;
                    _storageSeed = AccessTools.Field(_storageData.FieldType, "Seed");
                }
                if (_storageSeed == null) return 0;
                object data = _storageData.GetValue(provider);
                return unchecked((int)(long)_storageSeed.GetValue(data));
            }
            catch
            {
                return 0;
            }
        }

        private static void Trace(FaunaPolicy.Entry body, FaunaSite site, string outcome, FaunaRule rule)
        {
            if (!DebugEnabled) return;
            Log.Info("FAUNA_ASK body=" + body.Name + " outcome=" + outcome
                   + " night=" + (site.IsNight ? "1" : "0")
                   + " lat=" + site.LatitudeDeg.ToString("0.0", CultureInfo.InvariantCulture)
                   + " alt=" + site.AltitudeM.ToString("0", CultureInfo.InvariantCulture)
                   + " nodeKm=" + (site.NearestNodeKm.HasValue
                        ? site.NearestNodeKm.Value.ToString("0.0", CultureInfo.InvariantCulture) : "-")
                   + (rule == null ? "" : " animals=" + string.Join(",", rule.Animals.ToArray())));
        }

        /// <summary>The one-line readback for <c>factions debug fauna</c>.</summary>
        internal static string Describe()
        {
            return "spawnInfo=" + (Bound ? "bound" : "UNBOUND")
                 + " registration=" + (RegistrationBound ? "bound" : "UNBOUND")
                 + (InactiveReason == null ? "" : " (" + InactiveReason + ")")
                 + " authored=" + Authored + " native=" + Native + " silent=" + Silenced
                 + " registered=" + Registered;
        }
    }
}
