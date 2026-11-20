using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions.Sim
{
    /// <summary>The slice-0 fuel balance report of docs/fuel-plan.md:
    /// <c>dotnet run --project src/Factions.Sim -- fuel &lt;dataDir&gt; [days]</c>.
    ///
    /// <para>Runs the real server brains, catalogue and planet set offline for a few simulated days,
    /// prices every fleet itinerary the run creates with <see cref="LegImpulseProfile"/> and
    /// <see cref="FuelBurn"/>, and prints what the model asks each faction to burn per day beside
    /// what its FuelDepot capability could make. That pair is the plan's first two ratios, and they
    /// are what <see cref="FactionTuning.FuelBurnScale"/> is set from — a ratio above 1 on a faction
    /// sitting on ice means the formula is wrong, not the scale.</para>
    ///
    /// <para><b>This report infers; Core never does.</b> The live catalogue was written before the
    /// consumption fields existed, so a band whose <see cref="ThrustBand.MaxPowerMw"/> is 0 is
    /// filled here from the vanilla per-newton constants
    /// (<see cref="SimFuelCatalogue.InferBand"/>) and every number it produces is labelled
    /// <c>inferred</c>. The fuel manifest is not inferred: a hull with no scanned battery or reactor
    /// keeps its electric demand in <see cref="LegBurn.ElectricMwh"/> and the report calls it
    /// uncovered MWh.</para></summary>
    internal static class FuelScenario
    {
        /// <summary>One FuelDepot level: 6 gas generators at 25 L/s of ice, 20 L of hydrogen per
        /// litre of ice — docs/fuel-plan.md's depot figure, 259.2 ML/day.</summary>
        internal const double DepotLitresPerLevelPerDay = 6.0 * 25.0 * 20.0 * 86400.0;

        internal static int Run(string dataDir, int days) { return Run(dataDir, days, 1); }

        /// <summary><paramref name="minutesPerTick"/> trades resolution for wall time. The manager
        /// sweeps run off a simulated-seconds schedule, so their total work over a simulated day is
        /// the same whatever it is set to; what it divides is the per-tick work — the brain's think
        /// pass and the arrival sweep. A tick coarser than the shortest manager interval collapses
        /// two sweeps into one, so 3 minutes is the floor at the live intervals of 180 s.</summary>
        internal static int Run(string dataDir, int days, int minutesPerTick)
        {
            if (minutesPerTick <= 0) minutesPerTick = 1;
            if (string.IsNullOrEmpty(dataDir))
            {
                Console.Error.WriteLine("Usage: fuel <dataDir> [days]");
                return 2;
            }
            if (days <= 0) days = 3;

            var store = new FactionStore(dataDir);
            List<IndexEntry> entries = store.LoadIndex();
            if (entries.Count == 0)
            {
                Console.Error.WriteLine("No faction index found at: " + dataDir);
                return 1;
            }

            FactionTuning tuning = Program.LoadSimTuning(dataDir);

            CatalogueLoadStats catStats;
            SimFuelCatalogue catalogue = SimFuelCatalogue.Load(
                Path.Combine(dataDir, "prefab_catalogue.json"), out catStats);

            PlanetLoadStats planetStats;
            SimAbsolutePlanetCatalogue planets = SimAbsolutePlanetCatalogue.Load(
                Path.Combine(dataDir, "planets.json"), out planetStats);

            var world = new WorldModel(new SeededRandom(20260916))
            {
                Tuning = tuning,
                PrefabCatalogue = catalogue,
                Planets = planets,
            };
            world.UtcNow = DateTime.UtcNow;

            var states = new List<FactionState>();
            Dictionary<string, Personality> personalities = Program.BuildPersonalities();
            foreach (IndexEntry e in entries)
            {
                BrainDoc doc = store.LoadBrain(e.Tag);
                if (doc == null) continue;
                FactionState st = FactionStore.ApplyBrainDoc(doc, world.Graph, tuning.CampaignCooldown);
                long treasury = Math.Max(doc.Treasury, tuning.InsolvencyFloor);
                bool neverFunded = doc.Treasury == 0
                    && (doc.Sources == null || doc.Sources.Count == 0)
                    && st.DormantUntilUtc == DateTime.MinValue;
                world.SetTreasury(st.Tag, neverFunded ? tuning.SeedTreasury : treasury);
                states.Add(st);
            }

            // Same two corrections RunLive makes to a loaded brain: LastReinforcedUtc is not
            // persisted, so every node would start decaying immediately, and the server's real
            // cooldowns would idle most factions for the whole run.
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.LastReinforcedUtc == DateTime.MinValue) n.LastReinforcedUtc = world.UtcNow;
            foreach (FactionState st in states) st.Cooldowns.Clear();

            {
                var buf = new List<string>();
                PendingFleetResolver.ReconcileAndResolveOnLoad(states, world, tuning,
                    new SeededRandom(20260916), buf, null);
            }

            var spawner = new SimTransitSpawner();
            var brain = new FactionBrain();
            var ops = new OperationsManager();
            var shipyard = new ShipyardManager();
            var trade = new TradeManager();
            var fleets = new FleetManager();

            var report = new FuelReport(catalogue, planets, tuning, world);
            var seenFleets = new HashSet<string>(StringComparer.Ordinal);
            // Everything already in flight on load is pre-existing, not created by this run.
            foreach (FactionState st in states)
                foreach (PendingFleetAction f in st.PendingFleets) seenFleets.Add(FleetKey(st, f));

            int ticks = days * 24 * 60 / minutesPerTick;
            double opsInterval = tuning.OpsIntervalSeconds > 0 ? tuning.OpsIntervalSeconds : 10.0;
            double yardInterval = tuning.ShipyardIntervalSeconds > 0 ? tuning.ShipyardIntervalSeconds : 60.0;
            double tradeInterval = tuning.TradeIntervalSeconds > 0 ? tuning.TradeIntervalSeconds : 60.0;
            double fleetInterval = tuning.FleetIntervalSeconds > 0 ? tuning.FleetIntervalSeconds : 60.0;
            double tickSeconds = minutesPerTick * 60.0;

            double opsDue = 0, yardDue = 0, tradeDue = 0, fleetDue = 0;
            var log = new List<string>();
            DateTime startedWall = DateTime.UtcNow;
            // Where the wall time goes, so a slow run is a measurement rather than a guess.
            var swArrive = new System.Diagnostics.Stopwatch();
            var swThink = new System.Diagnostics.Stopwatch();
            var swOps = new System.Diagnostics.Stopwatch();
            var swYard = new System.Diagnostics.Stopwatch();
            var swTrade = new System.Diagnostics.Stopwatch();
            var swFleet = new System.Diagnostics.Stopwatch();
            var swPrice = new System.Diagnostics.Stopwatch();

            for (int tick = 0; tick < ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(minutesPerTick);
                world.AccrueIncome();

                var arrivals = new List<string>();
                swArrive.Start();
                PendingFleetResolver.ResolveArrivals(states, world, tuning,
                    new SeededRandom(unchecked(tick * 1103515245)), arrivals, null);
                swArrive.Stop();

                swThink.Start();
                foreach (FactionState st in states)
                {
                    if (FactionLifecycle.IsDormant(st, world.UtcNow)) continue;
                    Personality p;
                    if (!personalities.TryGetValue(st.PersonalityId ?? "default", out p)) p = personalities["default"];
                    Decision d = brain.Think(st, p, world, new SeededRandom(unchecked(st.RngSeed + tick)), tuning);
                    Program.EnactLive(world, tuning, st, d, spawner);
                }
                swThink.Stop();

                log.Clear();
                opsDue -= tickSeconds; yardDue -= tickSeconds; tradeDue -= tickSeconds; fleetDue -= tickSeconds;
                if (opsDue <= 0) { swOps.Start(); ops.Tick(states, world, tuning, log); swOps.Stop(); opsDue = opsInterval; }
                if (yardDue <= 0) { swYard.Start(); shipyard.Tick(states, world, tuning, log); swYard.Stop(); yardDue = yardInterval; }
                if (tradeDue <= 0) { swTrade.Start(); trade.Tick(states, world, tuning, log); swTrade.Stop(); tradeDue = tradeInterval; }
                if (fleetDue <= 0) { swFleet.Start(); fleets.Tick(states, world, tuning, log); swFleet.Stop(); fleetDue = fleetInterval; }

                world.DecayInfluence(0.02);
                FactionLifecycle.Step(states, world, tuning, null);

                swPrice.Start();
                foreach (FactionState st in states)
                {
                    for (int i = 0; i < st.PendingFleets.Count; i++)
                    {
                        PendingFleetAction fleet = st.PendingFleets[i];
                        string key = FleetKey(st, fleet);
                        if (!seenFleets.Add(key)) continue;
                        report.PriceItinerary(st.Tag, fleet, world, tuning);
                    }
                }
                swPrice.Stop();

                // Progress on stderr, so a redirected report stays clean. A run with no sign of
                // life is indistinguishable from a hung one, and the manager sweeps are not cheap.
                if ((tick + 1) % Math.Max(1, 60 / minutesPerTick) == 0)
                    Console.Error.WriteLine("fuel: " + ((tick + 1) * minutesPerTick / 60) + "h of " + (days * 24)
                        + "h, legs " + report.LegsPriced
                        + ", " + ((DateTime.UtcNow - startedWall).TotalSeconds).ToString("0", CultureInfo.InvariantCulture)
                        + "s  [think " + swThink.ElapsedMilliseconds / 1000 + "s ops " + swOps.ElapsedMilliseconds / 1000
                        + "s trade " + swTrade.ElapsedMilliseconds / 1000 + "s yard " + swYard.ElapsedMilliseconds / 1000
                        + "s fleet " + swFleet.ElapsedMilliseconds / 1000 + "s arrive " + swArrive.ElapsedMilliseconds / 1000
                        + "s price " + swPrice.ElapsedMilliseconds / 1000 + "s]");
            }

            Console.Write(report.Render(dataDir, days, ticks, minutesPerTick, states, world, tuning, catStats, planetStats));
            return 0;
        }

        private static string FleetKey(FactionState st, PendingFleetAction fleet)
        {
            return st.Tag + "/" + (fleet.FleetId ?? (fleet.OriginNodeId + ">" + fleet.TargetNodeId
                + "@" + fleet.DepartedUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // ---- prefab catalogue ------------------------------------------------------------------------

    /// <summary>How much of the catalogue's burn data was scanned and how much this report made up.
    /// Every count here is a caveat printed at the top of the report.</summary>
    internal sealed class CatalogueLoadStats
    {
        public int Entries;
        public int BandsScanned;      // MaxPowerMw already present
        public int BandsInferred;     // filled from the vanilla per-newton constants
        public int BandsNoData;       // neither — a modded thruster type this report cannot price
        public int HullsWithFuelData; // Entry.HasFuelData, i.e. a scanned battery/reactor manifest
    }

    /// <summary>The live <c>prefab_catalogue.json</c> as an <see cref="IPrefabCatalogue"/> +
    /// <see cref="IFuelPrefabCatalogue"/>, the way <c>DoctrineScenario.ScenarioCatalogue</c> fakes
    /// the interface — except that this one's entries are the real ones the host wrote.
    ///
    /// <para>The catalogue in the live data directory predates the consumption fields, so
    /// <see cref="InferBand"/> fills a zero <see cref="ThrustBand.MaxPowerMw"/> from the vanilla
    /// per-newton constants. That inference exists <b>only here</b>: Core reads a zero as "unknown,
    /// no burn" and must keep doing so, because a modded thruster has no vanilla constant to borrow
    /// and a fabricated burn would gate a hull on a number nobody measured.</para></summary>
    internal sealed class SimFuelCatalogue : IPrefabCatalogue, IFuelPrefabCatalogue
    {
        /// <summary>Large-grid vanilla large hydrogen thruster: 7.5 MW-equivalent for 7.2 MN, H2 at
        /// 0.001556 MWh/L and efficiency 1 — 4,820 L/s at full thrust.</summary>
        private const double HydrogenMwPerNewton = 7.5 / 7.2e6;
        private const double HydrogenEnergyDensityMwhPerL = 0.001556;
        private const string HydrogenFuelId = "Gas:Hydrogen";
        /// <summary>Large ion: 33.6 MW for 4.32 MN, electric.</summary>
        private const double IonMwPerNewton = 33.6 / 4.32e6;
        /// <summary>Large atmospheric: 16.8 MW for 6.48 MN, electric.</summary>
        private const double AtmosphericMwPerNewton = 16.8 / 6.48e6;

        // JSON-only DTO: every field below is assigned by the deserialiser, never in code.
#pragma warning disable 649
        internal sealed class Entry
        {
            public long Cost;
            public string FactionHint = "All";
            public List<string> Roles;
            public List<WeaponBand> WeaponBands;
            public long CargoLiters;
            public Dictionary<string, long> GasTankLiters;
            public string GridSize = "Large";
            public Dictionary<ThrustDirection, List<ThrustBand>> ThrustBands;
            public double DryMassKg;
            public double DrillYieldScore;
            public string Kind = "Ship";
            public string ReactorFuelKey;
            public double ReactorFuelCapacityKg;
            public double BatteryMwh;
            public bool HasFuelData;

            [JsonIgnore] public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> Bands;
            [JsonIgnore] public IReadOnlyDictionary<string, long> Tanks;
            /// <summary>True when at least one of this hull's bands had to be inferred.</summary>
            [JsonIgnore] public bool AnyInferredBand;
            /// <summary>True when no band of this hull resolved to a burn rate at all.</summary>
            [JsonIgnore] public bool NoBurnData;
        }
#pragma warning restore 649

        private static readonly IReadOnlyList<string> EmptyNames = new List<string>().AsReadOnly();
        private static readonly IReadOnlyList<WeaponBand> EmptyWeapons = new List<WeaponBand>().AsReadOnly();
        private static readonly IReadOnlyDictionary<string, long> EmptyTanks = new Dictionary<string, long>();
        private static readonly IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> EmptyBands
            = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();

        private readonly Dictionary<string, Entry> _entries;
        private readonly Dictionary<string, List<string>> _byRole =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        internal int Count { get { return _entries.Count; } }
        internal IEnumerable<KeyValuePair<string, Entry>> All { get { return _entries; } }

        private SimFuelCatalogue(Dictionary<string, Entry> entries)
        {
            _entries = entries;
            foreach (KeyValuePair<string, Entry> pair in entries)
            {
                if (pair.Value == null || pair.Value.Roles == null) continue;
                for (int i = 0; i < pair.Value.Roles.Count; i++)
                {
                    List<string> names;
                    if (!_byRole.TryGetValue(pair.Value.Roles[i], out names))
                        _byRole[pair.Value.Roles[i]] = names = new List<string>();
                    names.Add(pair.Key);
                }
            }
        }

        internal static SimFuelCatalogue Load(string path, out CatalogueLoadStats stats)
        {
            stats = new CatalogueLoadStats();
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (loaded != null)
                    foreach (KeyValuePair<string, Entry> pair in loaded)
                        if (pair.Value != null) entries[pair.Key] = pair.Value;
            }
            else
            {
                Console.Error.WriteLine("prefab_catalogue.json not found at: " + path);
            }

            foreach (KeyValuePair<string, Entry> pair in entries)
            {
                Entry e = pair.Value;
                stats.Entries++;
                if (e.HasFuelData) stats.HullsWithFuelData++;

                var bands = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                bool anyPriced = false;
                if (e.ThrustBands != null)
                {
                    foreach (KeyValuePair<ThrustDirection, List<ThrustBand>> dir in e.ThrustBands)
                    {
                        if (dir.Value == null) continue;
                        var list = new List<ThrustBand>(dir.Value.Count);
                        for (int i = 0; i < dir.Value.Count; i++)
                        {
                            ThrustBand band = dir.Value[i];
                            if (band.MaxPowerMw > 0)
                            {
                                stats.BandsScanned++;
                                anyPriced = true;
                            }
                            else if (InferBand(ref band))
                            {
                                stats.BandsInferred++;
                                e.AnyInferredBand = true;
                                anyPriced = true;
                            }
                            else
                            {
                                stats.BandsNoData++;
                            }
                            list.Add(band);
                        }
                        bands[dir.Key] = list.AsReadOnly();
                    }
                }
                e.Bands = bands;
                e.NoBurnData = !anyPriced;
                e.Tanks = e.GasTankLiters != null
                    ? (IReadOnlyDictionary<string, long>)e.GasTankLiters
                    : EmptyTanks;
            }

            return new SimFuelCatalogue(entries);
        }

        /// <summary>Fills a band scanned before the consumption fields existed from the vanilla
        /// per-newton constants, scaled by the band's own <see cref="ThrustBand.ForceMagnitudeN"/>.
        /// Only the three vanilla thruster types are inferable; anything else stays at zero and is
        /// reported as a band this run could not price. Returns true when the band was filled.</summary>
        internal static bool InferBand(ref ThrustBand band)
        {
            if (band.ForceMagnitudeN <= 0 || string.IsNullOrEmpty(band.ThrusterType)) return false;

            if (string.Equals(band.ThrusterType, "Hydrogen", StringComparison.Ordinal))
            {
                band.MaxPowerMw = (float)(band.ForceMagnitudeN * HydrogenMwPerNewton);
                band.FuelId = HydrogenFuelId;
                band.FuelEfficiency = 1f;
                band.FuelEnergyDensityMwhPerL = (float)HydrogenEnergyDensityMwhPerL;
                return true;
            }
            if (string.Equals(band.ThrusterType, "Ion", StringComparison.Ordinal))
            {
                band.MaxPowerMw = (float)(band.ForceMagnitudeN * IonMwPerNewton);
                band.FuelId = null;
                return true;
            }
            if (string.Equals(band.ThrusterType, "Atmospheric", StringComparison.Ordinal))
            {
                band.MaxPowerMw = (float)(band.ForceMagnitudeN * AtmosphericMwPerNewton);
                band.FuelId = null;
                return true;
            }
            return false;
        }

        internal Entry Get(string prefabId)
        {
            Entry e;
            return !string.IsNullOrEmpty(prefabId) && _entries.TryGetValue(prefabId, out e) ? e : null;
        }

        // ---- IPrefabCatalogue -------------------------------------------------------------------

        public long CargoLiters(string prefabId) { Entry e = Get(prefabId); return e != null ? e.CargoLiters : 0; }
        public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
        { Entry e = Get(prefabId); return e != null ? e.Tanks : EmptyTanks; }
        public long Cost(string prefabId) { Entry e = Get(prefabId); return e != null ? e.Cost : 0; }
        public string FactionHint(string prefabId) { Entry e = Get(prefabId); return e != null ? e.FactionHint : "All"; }
        public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
        { Entry e = Get(prefabId); return e != null && e.WeaponBands != null ? e.WeaponBands : EmptyWeapons; }
        public string GridSize(string prefabId) { Entry e = Get(prefabId); return e != null ? e.GridSize : "Unknown"; }
        public double DryMassKg(string prefabId) { Entry e = Get(prefabId); return e != null ? e.DryMassKg : 0; }
        public double DrillYieldScore(string prefabId) { Entry e = Get(prefabId); return e != null ? e.DrillYieldScore : 0; }
        public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
        { Entry e = Get(prefabId); return e != null && e.Bands != null ? e.Bands : EmptyBands; }

        public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
        {
            List<string> names;
            if (string.IsNullOrEmpty(role) || !_byRole.TryGetValue(role, out names)) return EmptyNames;

            var exact = new List<string>();
            var universal = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                Entry e = Get(names[i]);
                if (e == null || !string.Equals(e.Kind, "Ship", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(e.FactionHint, factionHint, StringComparison.OrdinalIgnoreCase)) exact.Add(names[i]);
                else if (string.Equals(e.FactionHint, "All", StringComparison.OrdinalIgnoreCase)) universal.Add(names[i]);
            }
            return exact.Count > 0 ? exact.AsReadOnly() : universal.AsReadOnly();
        }

        // ---- IFuelPrefabCatalogue ---------------------------------------------------------------

        public FuelManifest FuelManifest(string prefabId)
        {
            Entry e = Get(prefabId);
            if (e == null) return Factions.Core.FuelManifest.Unknown;
            return new FuelManifest
            {
                ReactorFuelKey = e.ReactorFuelKey,
                ReactorFuelCapacityKg = e.ReactorFuelCapacityKg,
                BatteryMwh = e.BatteryMwh,
                HasFuelData = e.HasFuelData,
            };
        }
    }

    // ---- planets ---------------------------------------------------------------------------------

    internal sealed class PlanetLoadStats
    {
        public int Planets;
        public int SpecMatched;
        public int SpecFallback;
        public readonly List<string> FallbackGenerators = new List<string>();
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>The live <c>planets.json</c> as an <see cref="IPlanetCatalogue"/> reporting
    /// <b>absolute</b> g, which is the unit the host reports and the unit
    /// <see cref="FactionTuning.FsdGravityDropoutG"/> is written in.
    /// <see cref="StaticPlanetCatalogue"/> cannot be used for this: its gravity is a proxy reading
    /// 1.0 anywhere at or inside the surface whatever the body's real surface gravity, so a leg
    /// through a light moon and a leg through a 2 g giant would be billed the same weight.
    ///
    /// <para>The curve is SE's own,
    /// <c>MySphericalNaturalGravityComponent.GetGravityMultiplier</c>:
    /// <c>g = intensity</c> inside <c>maxRadius</c>, <c>intensity·(d/maxRadius)^-falloff</c> outside
    /// it, and 0 past the gravity limit. <c>intensity</c> and <c>falloff</c> come from the
    /// generator's <see cref="PlanetTypeSpec"/>; <c>maxRadius</c> is inverted out of the record's
    /// own <see cref="PlanetRecord.GravityRadius"/> rather than read from
    /// <see cref="PlanetRecord.SurfaceRadius"/>, because the RGG gas giant reports a 9.5 km voxel
    /// core for a body whose gravity component uses 180 km. The inversion reproduces that 180 km
    /// from the recorded 304.9 km limit exactly, which is the check that the generator constants are
    /// the right ones.</para></summary>
    internal sealed class SimAbsolutePlanetCatalogue : IPlanetCatalogue
    {
        private sealed class Body
        {
            public PlanetRecord Record;
            public double IntensityG;
            public double Falloff;
            public double MaxRadiusKm;
        }

        // JSON-only DTO, as SimFuelCatalogue.Entry is.
#pragma warning disable 649
        private sealed class PlanetsDoc
        {
            public List<Row> Planets;
            public sealed class Row
            {
                public string Id, Name, Generator;
                public double X, Y, Z;
                public double SurfaceRadius, GravityRadius, AtmosphereRadius;
                public bool HasAtmosphere;
            }
        }
#pragma warning restore 649

        /// <summary>Generator constants this report knows. Only the gas giant's differ from the
        /// vanilla-shaped default, and its pair is the Real Gas Giants config the world was built
        /// from (<c>Radius 180, GravityStrength 2, GravityFalloff 7</c>, docs/world-builder.md).
        /// Anything else falls back to 1 g and SE's own falloff default of 7, and the report says
        /// which generators that happened for.</summary>
        private static StaticPlanetTypeCatalogue Specs()
        {
            return new StaticPlanetTypeCatalogue(new[]
            {
                new PlanetTypeSpec { DefinitionName = "RealGasGiant", SurfaceGravity = 2.0, GravityFalloffPower = 7.0 },
            });
        }

        private readonly Body[] _bodies;
        private readonly PlanetRecord[] _records;

        private SimAbsolutePlanetCatalogue(Body[] bodies)
        {
            _bodies = bodies;
            _records = new PlanetRecord[bodies.Length];
            for (int i = 0; i < bodies.Length; i++) _records[i] = bodies[i].Record;
        }

        internal static SimAbsolutePlanetCatalogue Load(string path, out PlanetLoadStats stats)
        {
            stats = new PlanetLoadStats();
            var bodies = new List<Body>();
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("planets.json not found at: " + path + " — every leg will be billed as deep space.");
                return new SimAbsolutePlanetCatalogue(bodies.ToArray());
            }

            PlanetsDoc doc = JsonConvert.DeserializeObject<PlanetsDoc>(File.ReadAllText(path));
            IPlanetTypeCatalogue specs = Specs();
            if (doc != null && doc.Planets != null)
            {
                foreach (PlanetsDoc.Row row in doc.Planets)
                {
                    if (row == null) continue;
                    var record = new PlanetRecord
                    {
                        Id = row.Id,
                        Name = row.Name,
                        Generator = row.Generator,
                        Center = new Vec3(row.X, row.Y, row.Z),
                        SurfaceRadius = row.SurfaceRadius,
                        GravityRadius = row.GravityRadius,
                        AtmosphereRadius = row.AtmosphereRadius,
                        HasAtmosphere = row.HasAtmosphere,
                    };

                    PlanetTypeSpec spec;
                    bool matched = specs.TryGet(row.Generator ?? "", out spec) && spec != null;
                    double intensity = matched ? spec.SurfaceGravity : 1.0;
                    double falloff = matched && spec.GravityFalloffPower > 0 ? spec.GravityFalloffPower : 7.0;
                    if (matched) stats.SpecMatched++;
                    else { stats.SpecFallback++; stats.FallbackGenerators.Add(row.Generator ?? "(unnamed)"); }

                    // gravityLimit = maxRadius × (intensity / 0.05) ^ (1/falloff), inverted.
                    double maxRadius = record.GravityRadius > 0
                        ? record.GravityRadius / Math.Pow(intensity / PlanetTypeSpec.GravityLimitStrength, 1.0 / falloff)
                        : record.SurfaceRadius;

                    bodies.Add(new Body
                    {
                        Record = record,
                        IntensityG = intensity,
                        Falloff = falloff,
                        MaxRadiusKm = maxRadius,
                    });
                    stats.Planets++;
                    stats.Lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} — surface g {1:F2} {2}, falloff {3:F0}, body radius {4:F1} km (inverted from the {5:F1} km limit), voxel radius {6:F1} km",
                        row.Generator, intensity, matched ? "(spec)" : "(fallback)", falloff,
                        maxRadius, record.GravityRadius, record.SurfaceRadius));
                }
            }
            return new SimAbsolutePlanetCatalogue(bodies.ToArray());
        }

        public IEnumerable<PlanetRecord> Planets { get { return _records; } }

        public double GravityAt(Vec3 posKm)
        {
            double strongest = 0;
            for (int i = 0; i < _bodies.Length; i++)
            {
                Body b = _bodies[i];
                double d = b.Record.Center.DistanceTo(posKm);
                if (b.Record.GravityRadius > 0 && d >= b.Record.GravityRadius) continue;
                double g = d <= b.MaxRadiusKm
                    ? b.IntensityG
                    : b.IntensityG * Math.Pow(d / b.MaxRadiusKm, -b.Falloff);
                if (g > strongest) strongest = g;
            }
            return strongest;
        }

        public Vec3 NearestSurfacePoint(Vec3 posKm)
        {
            PlanetRecord p = Nearest(posKm);
            if (p == null) return posKm;
            Vec3 n = Normal(p, posKm);
            return new Vec3(p.Center.X + n.X * p.SurfaceRadius,
                            p.Center.Y + n.Y * p.SurfaceRadius,
                            p.Center.Z + n.Z * p.SurfaceRadius);
        }

        public Vec3 SurfaceNormal(Vec3 posKm)
        {
            PlanetRecord p = Nearest(posKm);
            return p == null ? new Vec3(0, 1, 0) : Normal(p, posKm);
        }

        private PlanetRecord Nearest(Vec3 x)
        {
            PlanetRecord best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _records.Length; i++)
            {
                double d = _records[i].Center.DistanceTo(x) - _records[i].SurfaceRadius;
                if (d < bestDist) { bestDist = d; best = _records[i]; }
            }
            return best;
        }

        private static Vec3 Normal(PlanetRecord p, Vec3 x)
        {
            double dx = x.X - p.Center.X, dy = x.Y - p.Center.Y, dz = x.Z - p.Center.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return len > 1e-6 ? new Vec3(dx / len, dy / len, dz / len) : new Vec3(0, 1, 0);
        }
    }

    // ---- aggregation -----------------------------------------------------------------------------

    /// <summary>Running totals for one grouping key (a faction, an action kind, the whole run).</summary>
    internal sealed class FuelTotals
    {
        public int Legs;
        public int LegsWithoutBurnData;
        public int LegsWithInferredBands;
        public readonly Dictionary<string, double> GasLitres = new Dictionary<string, double>(StringComparer.Ordinal);
        public double ReactorFuelKg;
        public double BatteryMwh;
        public double UncoveredMwh;
        public double HaulLitres;

        // phase split, before the electric demand is resolved against a battery or reactor
        public double HoverGasLitres, DeltaVGasLitres;
        public double HoverMwh, DeltaVMwh, FsdMwh;

        public double TotalGasLitres
        {
            get { double t = 0; foreach (double v in GasLitres.Values) t += v; return t; }
        }

        public double Hydrogen
        {
            get { double v; return GasLitres.TryGetValue("Gas:Hydrogen", out v) ? v : 0; }
        }

        public void AddGas(string key, double litres)
        {
            if (litres <= 0) return;
            double existing;
            GasLitres.TryGetValue(key ?? "", out existing);
            GasLitres[key ?? ""] = existing + litres;
        }

        public void Absorb(LegBurn burn)
        {
            foreach (KeyValuePair<string, double> pair in burn.GasLitres) AddGas(pair.Key, pair.Value);
            ReactorFuelKg += burn.ReactorFuelKg;
            BatteryMwh += burn.BatteryMwh;
            UncoveredMwh += burn.ElectricMwh;
        }
    }

    /// <summary>Prices every itinerary the run creates and renders the markdown report.</summary>
    internal sealed class FuelReport
    {
        private readonly SimFuelCatalogue _catalogue;
        private readonly IPlanetCatalogue _planets;
        private readonly FactionTuning _tuning;

        private readonly Dictionary<string, FuelTotals> _byFaction = new Dictionary<string, FuelTotals>(StringComparer.Ordinal);
        private readonly Dictionary<ActionKind, FuelTotals> _byKind = new Dictionary<ActionKind, FuelTotals>();
        private readonly FuelTotals _all = new FuelTotals();
        private readonly List<double> _legLitres = new List<double>();
        private int _legsPriced;
        private int _legsWithGeometry;

        internal int LegsPriced { get { return _legsPriced; } }

        internal FuelReport(SimFuelCatalogue catalogue, IPlanetCatalogue planets, FactionTuning tuning, WorldModel world)
        {
            _catalogue = catalogue;
            _planets = planets;
            _tuning = tuning;
        }

        private FuelTotals Faction(string tag)
        {
            FuelTotals t;
            if (!_byFaction.TryGetValue(tag, out t)) _byFaction[tag] = t = new FuelTotals();
            return t;
        }

        private FuelTotals Kind(ActionKind kind)
        {
            FuelTotals t;
            if (!_byKind.TryGetValue(kind, out t)) _byKind[kind] = t = new FuelTotals();
            return t;
        }

        /// <summary>Prices a newly created fleet's whole itinerary once: the current leg
        /// (origin → target) followed by every remaining stop in order, as one polyline. A
        /// clearance chain and a multi-stop convoy are therefore one charge, which is what
        /// docs/fuel-plan.md says a draw is.</summary>
        internal void PriceItinerary(string tag, PendingFleetAction fleet, WorldModel world, FactionTuning tuning)
        {
            List<Vec3> polyline = Polyline(fleet, world);
            FuelTotals faction = Faction(tag);
            FuelTotals kind = Kind(fleet.Kind);
            faction.Legs++;
            kind.Legs++;
            _all.Legs++;
            _legsPriced++;

            if (fleet.CargoCapacityLiters > 0)
            {
                faction.HaulLitres += fleet.CargoCapacityLiters;
                kind.HaulLitres += fleet.CargoCapacityLiters;
                _all.HaulLitres += fleet.CargoCapacityLiters;
            }

            LegImpulseProfile profile = LegImpulseProfile.Build(_planets, tuning, polyline);
            if (!profile.HasGeometry)
            {
                faction.LegsWithoutBurnData++;
                kind.LegsWithoutBurnData++;
                _all.LegsWithoutBurnData++;
                return;
            }
            _legsWithGeometry++;

            var legTotal = new LegBurn();
            bool anyBurnData = false, anyInferred = false;
            double hoverGas = 0, deltaVGas = 0, hoverMwh = 0, deltaVMwh = 0, fsdMwh = 0;

            foreach (string prefabId in Hulls(fleet))
            {
                SimFuelCatalogue.Entry entry = _catalogue.Get(prefabId);
                double massKg = entry != null ? entry.DryMassKg : 0;
                if (massKg <= 0) continue;   // unscanned hull: unknown, not free
                if (entry.AnyInferredBand) anyInferred = true;

                IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = entry.Bands;
                FuelManifest manifest = _catalogue.FuelManifest(prefabId);

                LegBurn hullBurn = FuelBurn.ForLeg(profile, massKg, bands, manifest, tuning);
                legTotal.Add(hullBurn);
                if (hullBurn.HasBurnData) anyBurnData = true;

                // The same three charges again, unresolved, so the phase shares can be read. ForLeg
                // folds them together and then splits the electric side across the hull's store, and
                // a share table has to be taken before that split.
                double air = profile.RepresentativeAirDensity;
                bool atmo = profile.RepresentativeInAtmosphere;
                LegBurn down = FuelBurn.ForImpulse(bands, ThrustDirection.Down, massKg * profile.DownImpulsePerKg, air, atmo);
                LegBurn back = FuelBurn.ForImpulse(bands, ThrustDirection.Backward,
                                                   massKg * profile.AccelerateDeltaVMs, air, atmo);
                LegBurn fwd = FuelBurn.ForImpulse(bands, ThrustDirection.Forward,
                                                  massKg * profile.BrakeDeltaVMs, air, atmo);

                hoverGas += down.TotalGasLitres * tuning.FuelBurnScale;
                hoverMwh += down.ElectricMwh * tuning.FuelBurnScale;
                deltaVGas += (back.TotalGasLitres + fwd.TotalGasLitres) * tuning.FuelBurnScale;
                deltaVMwh += (back.ElectricMwh + fwd.ElectricMwh) * tuning.FuelBurnScale;
                fsdMwh += FuelBurn.FsdEnergyMwh(profile, massKg, tuning) * tuning.FuelBurnScale;
            }

            if (!anyBurnData)
            {
                faction.LegsWithoutBurnData++;
                kind.LegsWithoutBurnData++;
                _all.LegsWithoutBurnData++;
            }
            if (anyInferred)
            {
                faction.LegsWithInferredBands++;
                kind.LegsWithInferredBands++;
                _all.LegsWithInferredBands++;
            }

            faction.Absorb(legTotal); kind.Absorb(legTotal); _all.Absorb(legTotal);
            faction.HoverGasLitres += hoverGas; kind.HoverGasLitres += hoverGas; _all.HoverGasLitres += hoverGas;
            faction.DeltaVGasLitres += deltaVGas; kind.DeltaVGasLitres += deltaVGas; _all.DeltaVGasLitres += deltaVGas;
            faction.HoverMwh += hoverMwh; kind.HoverMwh += hoverMwh; _all.HoverMwh += hoverMwh;
            faction.DeltaVMwh += deltaVMwh; kind.DeltaVMwh += deltaVMwh; _all.DeltaVMwh += deltaVMwh;
            faction.FsdMwh += fsdMwh; kind.FsdMwh += fsdMwh; _all.FsdMwh += fsdMwh;

            _legLitres.Add(legTotal.TotalGasLitres);
        }

        /// <summary>Every hull flying this fleet: the squadron member by member when it has one, and
        /// <see cref="PendingFleetAction.ShipPrefabId"/> repeated <see cref="PendingFleetAction.HullCount"/>
        /// times otherwise. A convoy burns per hull, so a report that priced one would understate a
        /// six-ship dispatch six-fold.</summary>
        private static IEnumerable<string> Hulls(PendingFleetAction fleet)
        {
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                for (int i = 0; i < fleet.Squadron.Count; i++) yield return fleet.Squadron[i];
                yield break;
            }
            if (string.IsNullOrEmpty(fleet.ShipPrefabId)) yield break;
            int hulls = fleet.HullCount;
            for (int i = 0; i < hulls; i++) yield return fleet.ShipPrefabId;
        }

        private static List<Vec3> Polyline(PendingFleetAction fleet, WorldModel world)
        {
            var points = new List<Vec3>();
            AddPoint(points, fleet.OriginPosKm);
            AddPoint(points, fleet.TargetPosKm);
            if (fleet.RemainingStops != null)
            {
                for (int i = 0; i < fleet.RemainingStops.Count; i++)
                {
                    FleetStop stop = fleet.RemainingStops[i];
                    if (stop.HasPosition) { AddPoint(points, stop.PositionKm); continue; }
                    SpatialNode node;
                    if (!string.IsNullOrEmpty(stop.NodeId) && world.Graph.Nodes.TryGetValue(stop.NodeId, out node))
                        AddPoint(points, node.Position);
                }
            }
            return points;
        }

        private static void AddPoint(List<Vec3> points, Vec3 p)
        {
            if (points.Count > 0 && points[points.Count - 1].DistanceTo(p) < 1e-6) return;
            points.Add(p);
        }

        // ---- rendering --------------------------------------------------------------------------

        internal string Render(string dataDir, int days, int ticks, int minutesPerTick,
                               List<FactionState> states, WorldModel world,
                               FactionTuning tuning, CatalogueLoadStats catStats, PlanetLoadStats planetStats)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine("# Fuel balance — slice 0 model over the live catalogue");
            sb.AppendLine();
            sb.AppendLine("data dir: `" + dataDir + "`");
            sb.AppendLine("run: **" + days + " simulated days**, " + ticks.ToString(ci) + " ticks of "
                          + minutesPerTick + " minute" + (minutesPerTick == 1 ? "" : "s") + ", "
                          + states.Count + " factions, " + world.Graph.Nodes.Count + " nodes. The brain thinks once "
                          + "per tick, against the live server's FactionTickIntervalSeconds of "
                          + tuning.FactionTickIntervalSeconds.ToString("0", ci) + " s; the manager sweeps keep their "
                          + "own simulated-seconds schedule and so run at the live cadence either way.");
            sb.AppendLine("tuning: FuelBurnScale " + tuning.FuelBurnScale.ToString("0.###", ci)
                          + ", FsdGravityDropoutG " + tuning.FsdGravityDropoutG.ToString("0.####", ci)
                          + " (absolute g), thruster speed " + tuning.ThrusterCruiseSpeedMs.ToString("0.##", ci)
                          + " m/s, FSD cruise " + tuning.FleetSpeedKmPerMin.ToString("0.##", ci) + " km/min");
            sb.AppendLine();

            sb.AppendLine("## What is measured and what is inferred");
            sb.AppendLine();
            sb.AppendLine("- catalogue: **" + catStats.Entries + " entries**, bands **scanned "
                          + catStats.BandsScanned + "**, **inferred " + catStats.BandsInferred
                          + "**, unpriceable " + catStats.BandsNoData + ".");
            sb.AppendLine(catStats.BandsScanned == 0
                ? "- **every band below is inferred.** The scanner that fills `MaxPowerMw`/`FuelId` had not run "
                  + "when this snapshot was written, so a hydrogen band is priced at the vanilla large thruster's "
                  + "7.5 MW per 7.2 MN, an ion band at 33.6 MW per 4.32 MN and an atmospheric one at 16.8 MW per "
                  + "6.48 MN, each scaled by the band's own force. A modded thruster type has no vanilla constant "
                  + "to borrow and is left unpriced."
                : "- a band scanned before the consumption fields existed is inferred from the vanilla per-newton "
                  + "constants, scaled by its own force; a modded thruster type has none to borrow and is left unpriced.");
            sb.AppendLine("- fuel manifests: **" + catStats.HullsWithFuelData + " of " + catStats.Entries
                          + " hulls** carry a scanned battery/reactor manifest. Nothing is inferred for the manifest — "
                          + "a hull with none keeps its electric demand as **uncovered MWh**.");
            sb.AppendLine("- planets: " + planetStats.Planets + ", generator specs matched " + planetStats.SpecMatched
                          + ", fallback to 1 g " + planetStats.SpecFallback
                          + (planetStats.FallbackGenerators.Count > 0
                             ? " (" + string.Join(", ", planetStats.FallbackGenerators.ToArray()) + ")"
                             : ""));
            for (int i = 0; i < planetStats.Lines.Count; i++) sb.AppendLine("  - " + planetStats.Lines[i]);
            sb.AppendLine();
            sb.AppendLine("| body | voxel radius km | gravity limit km | FSD boundary km | g just inside the limit |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (PlanetRecord p in _planets.Planets)
            {
                double boundary = PlanetGeometry.GravityBoundaryRadius(_planets, p, tuning.FsdGravityDropoutG, new Vec3(1, 0, 0));
                double gAtLimit = _planets.GravityAt(new Vec3(p.Center.X + p.GravityRadius * 0.999, p.Center.Y, p.Center.Z));
                sb.AppendLine("| " + p.Generator
                    + " | " + p.SurfaceRadius.ToString("0.#", ci)
                    + " | " + p.GravityRadius.ToString("0.#", ci)
                    + " | " + boundary.ToString("0.#", ci)
                    + " | " + gAtLimit.ToString("0.###", ci) + " |");
            }
            sb.AppendLine();
            sb.AppendLine("The FSD boundary and the gravity limit are the **same surface**, and that is not a rounding "
                + "artefact: SE cuts natural gravity to 0 at 0.05 g "
                + "(`MySphericalNaturalGravityComponent`'s `GRAVITY_LIMIT_STRENGTH`), so no point in the world ever "
                + "reads a gravity between 0 and 0.05. `FsdGravityDropoutG` 0.01 therefore cannot name a shell inside "
                + "the well — every metre of a body's gravity radius is billed as thruster flight, including the outer "
                + "shell where the hull weighs a twentieth of its surface weight. Any value at or below 0.05 g gives "
                + "the same boundary.");
            sb.AppendLine("- legs priced: " + _legsPriced + ", of which " + _legsWithGeometry
                          + " had usable geometry and " + _all.LegsWithoutBurnData + " produced no burn at all.");
            sb.AppendLine("- hull mass is **dry mass only**: the snapshot carries no item mass table, so a loaded "
                          + "outbound leg is billed as an empty one. Cargo litres are the dispatched convoy's "
                          + "`CargoCapacityLiters`, i.e. capacity offered rather than a manifest weighed.");
            sb.AppendLine();

            // ---- per faction ----
            sb.AppendLine("## Per faction, per simulated day");
            sb.AppendLine();
            sb.AppendLine("| faction | legs | no burn data | H2 L/day | U kg/day | batt MWh/day | uncovered MWh/day | depot lv | depot ML/day | burn÷depot | fuel÷haul |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

            var tags = new List<string>();
            foreach (FactionState st in states) tags.Add(st.Tag);
            tags.Sort(delegate (string a, string b)
            {
                FuelTotals ta = _byFaction.ContainsKey(a) ? _byFaction[a] : null;
                FuelTotals tb = _byFaction.ContainsKey(b) ? _byFaction[b] : null;
                double va = ta != null ? ta.Hydrogen : 0, vb = tb != null ? tb.Hydrogen : 0;
                return vb.CompareTo(va);
            });

            foreach (string tag in tags)
            {
                FuelTotals t;
                if (!_byFaction.TryGetValue(tag, out t)) t = new FuelTotals();
                int depotLevels = DepotLevels(world, tag);
                double depotPerDay = depotLevels * FuelScenario.DepotLitresPerLevelPerDay;
                double h2PerDay = t.Hydrogen / days;
                double burnOverDepot = depotPerDay > 0 ? h2PerDay / depotPerDay : double.NaN;
                double fuelOverHaul = t.HaulLitres > 0 ? t.TotalGasLitres / t.HaulLitres : double.NaN;

                sb.AppendLine("| " + tag
                    + " | " + t.Legs
                    + " | " + t.LegsWithoutBurnData
                    + " | " + Sci(h2PerDay)
                    + " | " + Sci(t.ReactorFuelKg / days)
                    + " | " + Sci(t.BatteryMwh / days)
                    + " | " + Sci(t.UncoveredMwh / days)
                    + " | " + depotLevels
                    + " | " + (depotPerDay / 1e6).ToString("0.#", ci)
                    + " | " + Ratio(burnOverDepot)
                    + " | " + Ratio(fuelOverHaul)
                    + " |");
            }
            sb.AppendLine();
            sb.AppendLine("`burn÷depot` is hydrogen only, against 259.2 ML/day per FuelDepot level "
                          + "(6 generators × 25 L/s ice × 20 L/L). `n/a` = the faction holds no depot, "
                          + "so the ratio is division by zero rather than a pass.");
            sb.AppendLine("`fuel÷haul` is every gas litre burned over every cargo litre dispatched. "
                          + "The third ratio of docs/fuel-plan.md, imported ÷ drawn, is **n/a until slice 1** — "
                          + "nothing is drawn or imported yet.");
            sb.AppendLine();

            // ---- per kind ----
            sb.AppendLine("## Per action kind, whole run");
            sb.AppendLine();
            sb.AppendLine("| kind | legs | no burn data | H2 L | U kg | batt MWh | uncovered MWh | mean H2 L/leg |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            var kinds = new List<ActionKind>(_byKind.Keys);
            kinds.Sort(delegate (ActionKind a, ActionKind b) { return _byKind[b].Hydrogen.CompareTo(_byKind[a].Hydrogen); });
            foreach (ActionKind k in kinds)
            {
                FuelTotals t = _byKind[k];
                sb.AppendLine("| " + k
                    + " | " + t.Legs
                    + " | " + t.LegsWithoutBurnData
                    + " | " + Sci(t.Hydrogen)
                    + " | " + Sci(t.ReactorFuelKg)
                    + " | " + Sci(t.BatteryMwh)
                    + " | " + Sci(t.UncoveredMwh)
                    + " | " + Sci(t.Legs > 0 ? t.Hydrogen / t.Legs : 0)
                    + " |");
            }
            sb.AppendLine();

            // ---- phases ----
            sb.AppendLine("## Phase shares");
            sb.AppendLine();
            double gasTotal = _all.HoverGasLitres + _all.DeltaVGasLitres;
            double mwhTotal = _all.HoverMwh + _all.DeltaVMwh + _all.FsdMwh;
            sb.AppendLine("| phase | gas L | % of gas | electric MWh | % of electric |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            sb.AppendLine("| hover / climb (Down) | " + Sci(_all.HoverGasLitres) + " | " + Pct(_all.HoverGasLitres, gasTotal)
                          + " | " + Sci(_all.HoverMwh) + " | " + Pct(_all.HoverMwh, mwhTotal) + " |");
            sb.AppendLine("| Δv (Backward + Forward) | " + Sci(_all.DeltaVGasLitres) + " | " + Pct(_all.DeltaVGasLitres, gasTotal)
                          + " | " + Sci(_all.DeltaVMwh) + " | " + Pct(_all.DeltaVMwh, mwhTotal) + " |");
            sb.AppendLine("| FSD cruise + charge | 0 | 0% | " + Sci(_all.FsdMwh) + " | " + Pct(_all.FsdMwh, mwhTotal) + " |");
            sb.AppendLine();
            sb.AppendLine("Electric here is the **unresolved** demand, before a battery or reactor covers it — "
                          + "the split has to be taken before the resolution, or a hull with a big battery would "
                          + "read as having spent nothing.");
            sb.AppendLine();

            // ---- distribution ----
            sb.AppendLine("## Per-leg gas distribution");
            sb.AppendLine();
            _legLitres.Sort();
            sb.AppendLine("| n | p50 L | p90 L | max L | mean L |");
            sb.AppendLine("|---:|---:|---:|---:|---:|");
            double mean = 0;
            for (int i = 0; i < _legLitres.Count; i++) mean += _legLitres[i];
            if (_legLitres.Count > 0) mean /= _legLitres.Count;
            sb.AppendLine("| " + _legLitres.Count
                + " | " + Sci(Percentile(_legLitres, 0.50))
                + " | " + Sci(Percentile(_legLitres, 0.90))
                + " | " + Sci(_legLitres.Count > 0 ? _legLitres[_legLitres.Count - 1] : 0)
                + " | " + Sci(mean) + " |");
            sb.AppendLine();

            sb.AppendLine("## Run total");
            sb.AppendLine();
            sb.AppendLine("legs " + _all.Legs + " (" + _all.LegsWithoutBurnData + " with no burn data, "
                          + _all.LegsWithInferredBands + " carrying at least one inferred band); "
                          + "gas " + Sci(_all.TotalGasLitres) + " L; reactor fuel " + Sci(_all.ReactorFuelKg)
                          + " kg; battery " + Sci(_all.BatteryMwh) + " MWh; uncovered " + Sci(_all.UncoveredMwh)
                          + " MWh; cargo dispatched " + Sci(_all.HaulLitres) + " L.");
            foreach (KeyValuePair<string, double> pair in _all.GasLitres)
                sb.AppendLine("- " + (pair.Key.Length == 0 ? "(unkeyed gas)" : pair.Key) + ": " + Sci(pair.Value) + " L");
            sb.AppendLine();
            return sb.ToString();
        }

        private static int DepotLevels(WorldModel world, string tag)
        {
            int levels = 0;
            foreach (SpatialNode n in world.Graph.OwnedBy(tag))
            {
                int lv;
                if (n.Capabilities.TryGetValue(NodeCapabilityKind.FuelDepot, out lv)) levels += lv;
            }
            return levels;
        }

        private static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0) return 0;
            int idx = (int)Math.Floor(q * (sorted.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        private static string Sci(double v)
        {
            if (v == 0) return "0";
            if (Math.Abs(v) >= 1e6 || Math.Abs(v) < 0.01)
                return v.ToString("0.00e+0", CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Ratio(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "n/a";
            return v >= 100 ? v.ToString("0.00e+0", CultureInfo.InvariantCulture)
                            : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Pct(double part, double whole)
        {
            if (whole <= 0) return "n/a";
            return (100.0 * part / whole).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
    }
}
