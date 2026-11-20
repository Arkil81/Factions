using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions.Sim
{
    /// <summary>
    /// Offline faction simulation.
    ///   default : text decision log    — dotnet run --project src/Factions.Sim -- [seed] [ticks]
    ///   heatmap : animated colour map  — dotnet run --project src/Factions.Sim -- heatmap [seed] [frames] [delayMs] [ascii]
    ///   persist : index+brain round-trip + bloat check — dotnet run --project src/Factions.Sim -- persist [seed] [ticks]
    ///   money   : lone-faction income bootstrap — dotnet run --project src/Factions.Sim -- money [seed] [ticks] [personality]
    ///   budget  : reinforce spend-budget balancing (wealth term + claim throttle vs. hold decay)
    ///             — ... -- budget [seed] [ticks] [reinforceWealthFraction] [claimHoldHealthWeight]
    ///   borders : besieged-outpost / control-gating demo — dotnet run --project src/Factions.Sim -- borders [seed] [ticks]
    ///   war     : two raiders contest the same space -> relations sour -> raids — ... -- war [seed] [ticks]
    ///   world   : full animated dashboard (map + cash/flow/ctl/action) — ... -- world [seed] [frames] [delayMs] [ascii]
    ///   collapse: insolvency lifecycle — bleed -> floor -> force-shed -> conquered/dormant — ... -- collapse [seed] [ticks]
    ///   truce   : war fatigue -> a negotiated truce resolves a stalemate — ... -- truce [seed] [ticks]
    ///   trade   : peaceful trade model — peaceful pair warm together; opposing pair trades+raids — ... -- trade [seed] [ticks]
    ///   knowledge: fog-of-war growth + the economy control run slice C is measured against
    ///             — dotnet run --project src/Factions.Sim -- knowledge [seed|brainsPath] [ticks] [factions] [gated] [probefleets] [birthright=<km>]
    ///               named arguments go AFTER the positional ones — they are matched by name, and
    ///               one sitting in a positional slot silently costs that slot its value
    ///   convoy  : fleet convoy mechanic — pending fleets, transit, meetings (Patrol/Raid/Trade), resolution — ... -- convoy [seed]
    ///   doctrine: fleet doctrine — a player faction builds only the hull its line names, holds at its
    ///             target under pressure, and counts hulls in flight — ... -- doctrine [seed] [ticks]
    ///   fuel    : slice-0 fuel balance report over the live catalogue, brains and planets
    ///             — dotnet run --project src/Factions.Sim -- fuel &lt;dataDir&gt; [days] [minutesPerTick]
    ///   live    : load real server brains + run with transit-enabled spawner so convoys are visible mid-route
    ///             — dotnet run --project src/Factions.Sim -- live [path] [frames] [delayMs]
    ///             path defaults to C:\Torch\torch-server\Instance\Factions
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "heatmap")
                return RunHeatmap(ParseOr(args, 1, 1234), ParseOr(args, 2, 40), ParseOr(args, 3, 400),
                                  !(args.Length > 4 && args[4] == "ascii"));
            if (args.Length > 0 && args[0] == "persist")
                return RunPersist(ParseOr(args, 1, 1234), ParseOr(args, 2, 60));
            if (args.Length > 0 && args[0] == "money")
                return RunMoneyDemo(ParseOr(args, 1, 1234), ParseOr(args, 2, 30), args.Length > 3 ? args[3] : "settler");
            if (args.Length > 0 && args[0] == "budget")
                return RunBudget(ParseOr(args, 1, 1234), ParseOr(args, 2, 240),
                                 ParseDoubleOrNull(args, 3), ParseDoubleOrNull(args, 4));
            if (args.Length > 0 && args[0] == "borders")
                return RunBorders(ParseOr(args, 1, 1234), ParseOr(args, 2, 16));
            if (args.Length > 0 && args[0] == "war")
                return RunWar(ParseOr(args, 1, 1234), ParseOr(args, 2, 30));
            if (args.Length > 0 && args[0] == "world")
                return RunWorld(ParseOr(args, 1, 1234), ParseOr(args, 2, 40), ParseOr(args, 3, 400),
                                !(args.Length > 4 && args[4] == "ascii"));
            if (args.Length > 0 && args[0] == "collapse")
                return RunCollapse(ParseOr(args, 1, 1234), ParseOr(args, 2, 30));
            if (args.Length > 0 && args[0] == "truce")
                return RunTruce(ParseOr(args, 1, 1234), ParseOr(args, 2, 60));
            if (args.Length > 0 && args[0] == "trade")
                return RunTrade(ParseOr(args, 1, 1234), ParseOr(args, 2, 40));
            if (args.Length > 0 && args[0] == "knowledge")
            {
                // knowledge [seed|brainsPath] [ticks] [factions]
                int probe;
                bool arg1IsSeed = args.Length <= 1 || int.TryParse(args[1], out probe);
                string brainsPath = arg1IsSeed ? null : args[1];
                foreach (string a in args)
                {
                    if (a == "probefleets") _probeFleets = true;
                    if (a == "gated") _gated = true;
                    if (a != null && a.StartsWith("birthright=", StringComparison.Ordinal))
                    {
                        double km;
                        if (double.TryParse(a.Substring("birthright=".Length),
                                            NumberStyles.Float, CultureInfo.InvariantCulture, out km)
                            && km >= 0)
                        {
                            _birthrightKm = km;
                        }
                        else
                        {
                            Console.Error.WriteLine("knowledge: could not read '" + a
                                + "'. Expected birthright=<km>, e.g. birthright=50.");
                            return 2;
                        }
                    }
                }
                return RunKnowledge(ParseOr(args, 1, 1234), ParseOr(args, 2, 480), ParseOr(args, 3, 6), brainsPath);
            }
            if (args.Length > 0 && args[0] == "convoy")
                return RunConvoy(ParseOr(args, 1, 1234));
            if (args.Length > 1 && args[0] == "tactics" && args[1] == "learn")
                return TacticsScenario.RunLearning(ParseOr(args, 2, 1234), ParseOr(args, 3, 60));
            if (args.Length > 0 && args[0] == "tactics")
                return TacticsScenario.Run(ParseOr(args, 1, 1234), ParseOr(args, 2, 2000));
            if (args.Length > 0 && args[0] == "doctrine")
                return DoctrineScenario.Run(ParseOr(args, 1, 1234), ParseOr(args, 2, 48));
            if (args.Length > 0 && args[0] == "production")
            {
                // production [modelPath] [hours] [orePerMinute]
                // modelPath is optional; if args[1] parses as an integer it is treated as hours.
                int dummy;
                bool arg1IsInt = args.Length > 1 && int.TryParse(args[1], out dummy);
                string modelPath = arg1IsInt ? null : (args.Length > 1 ? args[1] : null);
                int offset = modelPath != null ? 1 : 0;
                return RunProduction(modelPath, ParseOr(args, 1 + offset, 24), ParseOr(args, 2 + offset, 5000));
            }
            if (args.Length > 0 && args[0] == "fuel")
            {
                // fuel <dataDir> [days]
                return FuelScenario.Run(args.Length > 1 ? args[1] : null, ParseOr(args, 2, 3), ParseOr(args, 3, 1));
            }
            if (args.Length > 0 && args[0] == "live")
            {
                // live [path] [frames] [delayMs]
                // path is optional; if args[1] is an integer it is treated as frames (path omitted).
                int dummy;
                bool arg1IsInt = args.Length > 1 && int.TryParse(args[1], out dummy);
                string livePath = arg1IsInt ? null : (args.Length > 1 ? args[1] : null);
                int pathOffset = livePath != null ? 1 : 0;
                return RunLive(livePath, ParseOr(args, 1 + pathOffset, 120), ParseOr(args, 2 + pathOffset, 500));
            }
            return RunTextDemo(ParseOr(args, 0, 1234), ParseOr(args, 1, 24));
        }

        private static int ParseOr(string[] a, int i, int def) { int v; return a.Length > i && int.TryParse(a[i], out v) ? v : def; }

        private static double? ParseDoubleOrNull(string[] a, int i)
        {
            double v;
            return a.Length > i && double.TryParse(a[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        internal static FactionTuning LoadSimTuning(string baseDir = null)
        {
            var tuning = new FactionTuning();
            string dir = baseDir ?? FindDefaultTuningDir();
            if (string.IsNullOrEmpty(dir)) return tuning;

            string tempDir = Path.Combine(Path.GetTempPath(), "factions-sim-tuning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string defaultsSrc = Path.Combine(dir, "tuning_defaults.json");
            string tuningSrc = Path.Combine(dir, "tuning.json");
            if (File.Exists(defaultsSrc)) File.Copy(defaultsSrc, Path.Combine(tempDir, "tuning_defaults.json"), true);
            if (File.Exists(tuningSrc)) File.Copy(tuningSrc, Path.Combine(tempDir, "tuning.json"), true);

            new FactionStore(tempDir).LoadTuning(tuning);
            return tuning;
        }

        private static string FindDefaultTuningDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                string instanceDir = Path.Combine(dir, "instance");
                if (File.Exists(Path.Combine(instanceDir, "tuning_defaults.json")))
                    return instanceDir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }

        // ---- text decision log -----------------------------------------------------------------
        private static int RunTextDemo(int seed, int ticks)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var personalities = BuildPersonalities();
            var factions = new List<(FactionState st, Personality p)>
            {
                Seed(world, personalities, "MERC", "mercantile", 300000, new Vec3(0, 0, 0),   seed + 1),
                Seed(world, personalities, "RVN",  "raider",     250000, new Vec3(8000, 0, 0), seed + 2),
                Seed(world, personalities, "STL",  "settler",    280000, new Vec3(0, 0, 8000), seed + 3),
            };

            Console.WriteLine("=== Factions sim — seed " + seed + ", " + ticks + " ticks ===");
            Console.WriteLine("MERC=mercantile/open  RVN=raider/xenophobic  STL=settler/expansionist");
            PrintState(world, factions);
            Console.WriteLine();

            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();   // minted territorial yield -> private ledger (see Economy)
                ApplyEvents(world, tick);
                foreach (var f in factions)
                {
                    var faRng = new SeededRandom(f.st.RngSeed + tick);
                    Decision d = brain.Think(f.st, f.p, world, faRng, tuning);
                    Enact(world, tuning, f.st, d);
                    Console.WriteLine("t" + tick.ToString("00") + " " + f.st.Tag.PadRight(4) + " " + d.Explanation);
                }
                ApplyDiplomacy(world, factions, tuning);
                world.DecayThreat(tuning.ThreatDecayForIntervalSeconds(3600.0));
            }

            Console.WriteLine();
            PrintState(world, factions);
            return 0;
        }

        // ---- minted-income money loop (single-faction bootstrap) -------------------------------
        private static int RunMoneyDemo(int seed, int ticks, string persId)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // a lone faction with NO territory and a little seed capital — the "single ship in a random spot".
            var personalities = BuildPersonalities();
            if (!personalities.ContainsKey(persId)) persId = "settler";
            Personality p = personalities[persId];
            var st = new FactionState { Tag = "PIO", PersonalityId = persId, RngSeed = seed + 1 };
            world.SetTreasury("PIO", 80000);

            Console.WriteLine("=== money loop — seed " + seed + ", " + ticks + " ticks: lone " + persId + " faction bootstraps from 80k seed ===");
            Console.WriteLine("knobs: BaseYield=" + tuning.BaseYield + "/src  Upkeep=" + tuning.UpkeepPerSource +
                              "/src  Expand=" + tuning.ExpandCost + "  Reinforce=" + tuning.ReinforceCost + "  decay=0.03/tick");
            Console.WriteLine("tick  goal/intent                    treasury   netflow  src  hold   money   target      gap");
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();                                                            // 1) sources pay
                Decision d = brain.Think(st, p, world, new SeededRandom(st.RngSeed + tick), tuning); // 2) decide
                Enact(world, tuning, st, d);                                                     // 3) spend
                world.DecayInfluence(0.03);                                                      // 4) hold decays

                int src = world.Graph.OwnedBy("PIO").Count();
                double hold = world.Graph.OwnedBy("PIO").Sum(n => n.HoldStrength);
                CentralBankSnapshot bank = world.SnapshotCentralBank();
                Console.WriteLine("t" + tick.ToString("00") + "   " + (d.Goal + " -> " + d.Intent).PadRight(30) +
                                  world.GetTreasury("PIO").ToString().PadLeft(8) + "  " +
                                  world.GetNetFlow("PIO").ToString().PadLeft(8) + "  " +
                                  src.ToString().PadLeft(3) + "  " + hold.ToString("0.0").PadLeft(4) + "  " +
                                  bank.CurrentMoneySupply.ToString().PadLeft(6) + "  " +
                                  bank.TargetMoneySupply.ToString().PadLeft(7) + "  " +
                                  bank.MoneySupplyGap.ToString().PadLeft(8));
            }
            return 0;
        }

        // ---- budget mode: reinforce spend budget vs. hold decay at estate scale ----------------
        /// <summary>
        /// Balancing harness for the operational spend-budget model (SpendBudget wealth term +
        /// claim hold-health throttle). Three estates run the REAL FleetManager hold-maintenance
        /// path (no other sim mode does) against live-equivalent decay and a ~5-min manager visit
        /// cadence:
        ///   RICH — cash mountain, zero income, large estate: bleeds under the income-only budget,
        ///          must stabilize once the wealth term can draw on treasury surplus.
        ///   MID  — treasury below ReserveTreasury: must behave identically with or without the
        ///          wealth term (the reserve gate).
        ///   POOR — small estate, small cash: the income floor alone must keep it whole.
        /// Args: budget [seed] [ticks] [reinforceWealthFraction] [claimHoldHealthWeight] — the two
        /// optional doubles override tuning for A/B runs (pass 0 for the income-only control).
        /// </summary>
        private static int RunBudget(int seed, int ticks, double? wealthFractionOverride, double? claimWeightOverride)
        {
            var tuning = LoadSimTuning();
            if (wealthFractionOverride.HasValue) tuning.ReinforceWealthFractionPerWindow = wealthFractionOverride.Value;
            if (claimWeightOverride.HasValue) tuning.ClaimHoldHealthWeight = claimWeightOverride.Value;

            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            var fleets = new FleetManager();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();
            var estateRng = new SeededRandom(seed + 99);

            SeedEstate(world, "RICH", 80, new Vec3(0, 0, 0), estateRng);
            SeedEstate(world, "MID", 15, new Vec3(3000, 0, 0), estateRng);
            SeedEstate(world, "POOR", 6, new Vec3(0, 0, 3000), estateRng);
            world.SetTreasury("RICH", 2_000_000_000);
            world.SetTreasury("MID", 30_000_000);   // below ReserveTreasury -> wealth term must stay inert
            world.SetTreasury("POOR", 1_000_000);

            string[] tags = { "RICH", "MID", "POOR" };
            var states = new List<FactionState>();
            var factions = new List<(FactionState st, Personality p)>();
            foreach (string tag in tags)
            {
                var st = new FactionState { Tag = tag, PersonalityId = "settler", RngSeed = seed + tag.Length };
                states.Add(st);
                factions.Add((st, ps["settler"]));
            }

            var lostCum = new Dictionary<string, int>();
            var reinfCum = new Dictionary<string, int>();
            var claimCum = new Dictionary<string, int>();
            foreach (string tag in tags) { lostCum[tag] = 0; reinfCum[tag] = 0; claimCum[tag] = 0; }
            int evacuatedHulls = 0, strandedHulls = 0;   // FAC-278: hulls moved off / left on abandoned nodes

            Console.WriteLine("=== budget — seed " + seed + ", " + ticks + " ticks (1 min each) ===");
            Console.WriteLine("knobs: ReinforceWealthFraction=" + tuning.ReinforceWealthFractionPerWindow.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                              + "  ClaimHoldHealthWeight=" + tuning.ClaimHoldHealthWeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                              + "  ReserveTreasury=" + tuning.ReserveTreasury
                              + "  decay=" + tuning.HoldDecayPerHour + "/h  ReinforceCost=" + tuning.ReinforceCost
                              + "  manager visit every 5 ticks");
            Console.WriteLine("tick  tag    nodes underH  lost  reinf claims   treasury      netflow");

            var mgrLog = new List<string>();
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome(states);

                foreach (var f in factions)
                {
                    Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                    Enact(world, tuning, f.st, d);
                    if (d.Intent.Kind == ActionKind.Claim || d.Intent.Kind == ActionKind.Expand)
                        claimCum[f.st.Tag]++;
                }

                if (tick % 5 == 0)
                {
                    mgrLog.Clear();
                    fleets.Tick(states, world, tuning, mgrLog);
                    foreach (string line in mgrLog)
                        if (line.Contains(" REINFORCE cost="))
                            foreach (string tag in tags)
                                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) { reinfCum[tag]++; break; }
                }

                // Same four-argument form the host uses: the traffic offset is part of the
                // decay rule, so the sim must run it or it models a different world.
                world.DecayInfluence(tuning.HoldDecayForIntervalSeconds(60.0),
                                     tuning.PlayerHoldDecayForIntervalSeconds(60.0), states, 60.0);
                foreach (AbandonedNode lost in world.AbandonedNodes)
                {
                    int cur;
                    if (lostCum.TryGetValue(lost.PriorOwnerTag, out cur)) lostCum[lost.PriorOwnerTag] = cur + 1;
                }
                world.ClearAbandonedNodes();
                foreach (ShipEvacuation moved in world.ShipEvacuations)
                {
                    if (moved.ToNodeId != null) evacuatedHulls += moved.Count;
                    else strandedHulls += moved.Count;
                }
                world.ClearShipEvacuations();

                if (tick % 10 == 0 || tick == ticks)
                {
                    foreach (string tag in tags)
                    {
                        int nodes = 0, underHold = 0;
                        foreach (SpatialNode n in world.Graph.OwnedBy(tag))
                        {
                            nodes++;
                            if (n.HoldStrength < tuning.HoldMaintenanceThreshold) underHold++;
                        }
                        Console.WriteLine("t" + tick.ToString("000") + "  " + tag.PadRight(5)
                                          + nodes.ToString().PadLeft(6) + underHold.ToString().PadLeft(7)
                                          + lostCum[tag].ToString().PadLeft(6) + reinfCum[tag].ToString().PadLeft(7)
                                          + claimCum[tag].ToString().PadLeft(7)
                                          + world.GetTreasury(tag).ToString().PadLeft(11)
                                          + world.GetNetFlow(tag).ToString().PadLeft(13));
                    }
                }
            }
            Console.WriteLine("hulls evacuated on abandonment: " + evacuatedHulls
                              + "   stranded (no owned node reachable): " + strandedHulls);
            return 0;
        }

        /// <summary>Seeds one faction's estate for budget mode: count nodes scattered around home,
        /// mixed hold 0.35–1.0, never reinforced (no grace), plain N-ids so decay can revert them.
        /// Nodes are chained by graph edges plus a few UNKN frontier neighbours, so brains see a
        /// claimable frontier (the claim→decay→re-claim treadmill needs connectivity to appear).</summary>
        private static void SeedEstate(WorldModel w, string tag, int count, Vec3 home, IRandom rng)
        {
            SpatialNode prev = null;
            SpatialNode first = null;
            for (int i = 0; i < count; i++)
            {
                var pos = new Vec3(home.X + (rng.NextDouble() - 0.5) * 400,
                                   home.Y,
                                   home.Z + (rng.NextDouble() - 0.5) * 400);
                SpatialNode n = w.Graph.Add(pos, 0.4 + 0.4 * rng.NextDouble());
                n.OwnerFactionTag = tag;
                n.HoldStrength = 0.35 + 0.65 * rng.NextDouble();
                n.Radius = 30;
                if (prev != null) w.Graph.Connect(prev.Id, n.Id);
                if (first == null) first = n;
                prev = n;
            }

            int frontierCount = count / 10 + 2;
            for (int i = 0; i < frontierCount; i++)
            {
                var pos = new Vec3(home.X + (rng.NextDouble() - 0.5) * 500,
                                   home.Y,
                                   home.Z + (rng.NextDouble() - 0.5) * 500);
                SpatialNode n = w.Graph.Add(pos, 0.4 + 0.4 * rng.NextDouble());
                n.OwnerFactionTag = NeutralFaction.Tag;
                n.Radius = 30;
                w.Graph.Connect(first.Id, n.Id);
            }
        }

        // ---- contested border: a besieged outpost + control-gated income -----------------------
        private static int RunBorders(int seed, int ticks)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();

            // RVN: a strong, aggressive cluster that dominates the region around the origin.
            AddSource(world, "RVN", new Vec3(0, 0, 0), 0.9, 0.6);
            AddSource(world, "RVN", new Vec3(10, 0, 5), 0.9, 0.6);
            AddSource(world, "RVN", new Vec3(-10, 0, 10), 0.9, 0.6);
            AddSource(world, "RVN", new Vec3(5, 0, -15), 0.9, 0.6);
            world.SetTreasury("RVN", 200000);

            // MERC: a safe, profitable home far away + one outpost planted INSIDE RVN space (besieged at birth).
            AddSource(world, "MERC", new Vec3(4000, 0, 0), 0.8, 0.7);            // safe home — uncontested, pays well
            SpatialNode outpost = AddSource(world, "MERC", new Vec3(5, 0, 5), 0.5, 0.4); // besieged outpost
            world.SetTreasury("MERC", 120000);

            var factions = new List<(FactionState st, Personality p)>
            {
                (new FactionState { Tag = "RVN",  PersonalityId = "raider",     RngSeed = seed + 1 }, ps["raider"]),
                (new FactionState { Tag = "MERC", PersonalityId = "mercantile", RngSeed = seed + 2 }, ps["mercantile"]),
            };

            Console.WriteLine("=== borders — seed " + seed + ", " + ticks + " ticks: a MERC outpost besieged inside an RVN cluster ===");
            Console.WriteLine("RVN = raider cluster @origin   MERC = mercantile (safe home @4000km + besieged outpost @origin)");
            Console.WriteLine("watch the outpost's MERC-control collapse -> its yield goes net-negative -> MERC sheds it.");
            PrintOutpost(world, tuning, outpost, 0);
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                foreach (var f in factions)
                {
                    Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                    Enact(world, tuning, f.st, d);
                    Console.WriteLine("t" + tick.ToString("00") + " " + f.st.Tag.PadRight(4) + " " + d.Explanation);
                }
                ApplyDiplomacy(world, factions, tuning);
                world.DecayInfluence(0.02);
                PrintOutpost(world, tuning, outpost, tick);
            }
            return 0;
        }

        private static SpatialNode AddSource(WorldModel w, string tag, Vec3 pos, double hold, double value)
        {
            SpatialNode n = w.Graph.Add(pos, value);
            n.OwnerFactionTag = tag; n.HoldStrength = hold; n.Radius = 30;
            return n;
        }

        private static void PrintOutpost(WorldModel w, FactionTuning t, SpatialNode outpost, int tick)
        {
            string owner = outpost.OwnerFactionTag ?? "(abandoned)";
            double ctl = w.Influence.Control(outpost.Position, "MERC");
            long net = outpost.OwnerFactionTag == null ? 0 : Economy.SourceNet(outpost, ctl, t);
            Console.WriteLine("     t" + tick.ToString("00") + " outpost " + outpost.Id + ": owner=" + owner.PadRight(11) +
                              " MERC-control=" + ctl.ToString("0.00") + " net=" + net.ToString().PadLeft(6) +
                              "   |  MERC nf=" + w.GetNetFlow("MERC").ToString().PadLeft(6) +
                              "  RVN nf=" + w.GetNetFlow("RVN").ToString().PadLeft(6));
        }

        // ---- conflict: two aggressive factions contest the same space -> relations sour -> war -----
        private static int RunWar(int seed, int ticks)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();

            // two raiders interleaved around the origin -> high mutual border pressure from the start
            AddSource(world, "RED", new Vec3(0, 0, 0), 0.85, 0.6);
            AddSource(world, "RED", new Vec3(50, 0, 10), 0.85, 0.6);
            AddSource(world, "RED", new Vec3(15, 0, 50), 0.85, 0.6);
            AddSource(world, "BLU", new Vec3(30, 0, 30), 0.85, 0.6);
            AddSource(world, "BLU", new Vec3(60, 0, 55), 0.85, 0.6);
            AddSource(world, "BLU", new Vec3(-10, 0, 25), 0.85, 0.6);
            world.SetTreasury("RED", 300000);
            world.SetTreasury("BLU", 300000);

            var factions = new List<(FactionState st, Personality p)>
            {
                (new FactionState { Tag = "RED", PersonalityId = "raider", RngSeed = seed + 1 }, ps["raider"]),
                (new FactionState { Tag = "BLU", PersonalityId = "raider", RngSeed = seed + 2 }, ps["raider"]),
            };
            FactionState red = factions[0].st, blu = factions[1].st;

            Console.WriteLine("=== war — seed " + seed + ", " + ticks + " ticks: two raiders contest the same space ===");
            Console.WriteLine("watch RED<->BLU standing sour under border pressure, then raids fly and sources fall.");
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                foreach (var f in factions)
                {
                    Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                    Enact(world, tuning, f.st, d);
                    Console.WriteLine("t" + tick.ToString("00") + " " + f.st.Tag + " " + d.Explanation);
                }
                ApplyDiplomacy(world, factions, tuning);
                world.DecayInfluence(0.02);
                Console.WriteLine("     standing RED->BLU " + red.RelationTo("BLU").ToString("0.00") +
                                  "  BLU->RED " + blu.RelationTo("RED").ToString("0.00") +
                                  "   src RED " + world.Graph.OwnedBy("RED").Count() +
                                  " BLU " + world.Graph.OwnedBy("BLU").Count());
            }
            return 0;
        }

        // ---- fog of war: how fast knowledge grows, and the control run for slice C -------------

        /// <summary>
        /// Runs a station-only world -- the shape a fresh `factions init` + `factions scan` produces
        /// -- with the knowledge sweep active and nothing gated on it, which is exactly what ships
        /// today.
        ///
        /// <para>It answers two questions that are otherwise guesses. **How fast does knowledge
        /// grow?** -- the sweep and fleet traffic are the only channels, and if the curve is flat
        /// then slice C needs a Scout action before it is viable, not after. **What does the economy
        /// do while knowledge is merely recorded?** -- these numbers are the control. When slice C
        /// gates the brains, the same seed and tick count must be run again and compared against
        /// this run; "did the economy stall" is otherwise an argument rather than a measurement.</para>
        /// </summary>
        private static int RunKnowledge(int seed, int ticks, int factionCount, string brainsPath)
        {
            FactionTuning tuning = LoadSimTuning(brainsPath);
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Dictionary<string, Personality> ps = BuildPersonalities();

            if (!string.IsNullOrEmpty(brainsPath))
                return RunKnowledgeOverBrains(brainsPath, ticks, tuning, world, brain, ps);

            if (factionCount < 2) factionCount = 2;
            if (factionCount > 12) factionCount = 12;
            string[] persIds = { "mercantile", "settler", "raider", "factorum", "mercantile", "default" };
            var rng = new SeededRandom(seed ^ 0x5EED);
            var factions = new List<(FactionState st, Personality p)>();
            long stationSeq = 0;

            // Stations scattered in a shell, several per faction, the way scanned economy stations
            // sit: clustered enough that some rivals are neighbours and some are not. No edges here
            // -- the densify below is what scan itself now does.
            for (int f = 0; f < factionCount; f++)
            {
                string tag = "F" + (f + 1).ToString("00");
                string persId = persIds[f % persIds.Length];
                // Clusters deliberately OVERLAP. Territories that do not interleave put every rival
                // beyond both the birthright range and the sensor envelope, so birthright == owned
                // and learned stays 0 -- the run then exercises nothing, which is what a first pass
                // at 900 km separation did. Calibrated against the live world, where 33% of
                // scan-backed stations have a foreign station inside the passive sensor's effective
                // 50 km and 41% inside 62.5 km.
                double angle = 2.0 * Math.PI * f / factionCount;
                var centre = new Vec3(Math.Cos(angle) * 260.0, 0, Math.Sin(angle) * 260.0);
                int stations = 3 + (int)(rng.NextDouble() * 3);
                for (int i = 0; i < stations; i++)
                {
                    var pos = new Vec3(
                        centre.X + (rng.NextDouble() - 0.5) * 320.0,
                        centre.Y + (rng.NextDouble() - 0.5) * 120.0,
                        centre.Z + (rng.NextDouble() - 0.5) * 320.0);
                    // E### ids on purpose: a fresh scan produces SE economy stations, and
                    // SourceMapping.IsScanBacked exempts those from use-it-or-lose-it hold decay
                    // (floored at 0.1). With engine-invented N### ids instead, every faction decays
                    // to zero nodes inside 200 ticks and the run measures a dying world rather than
                    // a fog-of-war question -- which is exactly what the first version of this
                    // scenario did.
                    stationSeq++;
                    SpatialNode n = world.Graph.Add(pos, 0.4 + rng.NextDouble() * 0.5,
                                                    "E" + (900000000000000000L + stationSeq));
                    n.OwnerFactionTag = tag;
                    n.HoldStrength = 0.85;
                    n.StableStationId = 900000000000000000L + stationSeq;
                }
                world.SetTreasury(tag, 400000);
                factions.Add((new FactionState { Tag = tag, PersonalityId = persId, RngSeed = seed + f + 1 },
                              ps[persId]));
            }

            return RunKnowledgeLoop(tuning, world, brain, factions, ticks, seed, "synthetic");
        }

        /// <summary>
        /// The same run over a REAL brains directory -- the live server's, or the standalone's once it
        /// is initialised. This is the variant that produces a control run worth comparing against.
        ///
        /// <para>The synthetic world above is a smoke test and openly not a valid baseline: hand-built
        /// faction geometry idles ~94% of decisions and contracts, so slice C could not make it worse
        /// and a comparison would prove nothing. Balancing a toy economy is months of work that the
        /// real brains already embody.</para>
        /// </summary>
        private static int RunKnowledgeOverBrains(
            string path, int ticks, FactionTuning tuning, WorldModel world, FactionBrain brain,
            Dictionary<string, Personality> ps)
        {
            var store = new FactionStore(path);
            List<IndexEntry> entries = store.LoadIndex();
            if (entries.Count == 0)
            {
                Console.Error.WriteLine("No faction index found at: " + path);
                Console.Error.WriteLine("Usage: knowledge [seed|brainsPath] [ticks] [factions]");
                return 1;
            }

            world.UtcNow = DateTime.UtcNow;
            var factions = new List<(FactionState st, Personality p)>();
            foreach (IndexEntry e in entries)
            {
                BrainDoc doc = store.LoadBrain(e.Tag);
                if (doc == null) continue;
                FactionState st = FactionStore.ApplyBrainDoc(doc, world.Graph, tuning.CampaignCooldown);
                long treasury = Math.Max(doc.Treasury, tuning.InsolvencyFloor);
                world.SetTreasury(st.Tag, treasury);
                Personality p;
                if (!ps.TryGetValue(st.PersonalityId ?? "default", out p)) p = ps["default"];
                factions.Add((st, p));
            }

            // LastReinforcedUtc is not persisted, so loaded nodes would otherwise bypass the grace
            // period and decay immediately. The server was maintaining them moments ago.
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.LastReinforcedUtc == DateTime.MinValue) n.LastReinforcedUtc = world.UtcNow;

            var buf = new List<string>();
            var loadRng = new SeededRandom(20260824);
            var loadStates = new List<FactionState>();
            foreach (var f in factions) loadStates.Add(f.st);
            PendingFleetResolver.ReconcileAndResolveOnLoad(loadStates, world, tuning, loadRng, buf, null);

            return RunKnowledgeLoop(tuning, world, brain, factions, ticks, 20260824, path);
        }

        /// <summary>Set by the `probefleets` argument. Dispatches a long-range fleet per faction on a
        /// cadence, regardless of what the brains want.
        ///
        /// <para>This is a deliberate probe of one mechanism, not organic traffic, and it is labelled
        /// as such in the output. The channel under test is <see cref="KnowledgeSweep.SweepFleetSegment"/>
        /// -- whether a fleet in transit detects nodes outside the birthright floor. That is a
        /// property of geometry and range, not of whether a peaceful world's brains happen to feel
        /// like raiding: the first real run chose zero fleet actions in 48 ticks, which left the
        /// channel untested rather than shown redundant.</para></summary>
        private static bool _probeFleets;

        /// <summary>Set by the `gated` argument: turns <see cref="FactionTuning.KnowledgeGatedBrains"/>
        /// on for the run. The whole purpose of this scenario is that the same seed, world and tick
        /// count can be run both ways and the columns compared -- a stall is a shape in those numbers,
        /// not an error in a log.</summary>
        private static bool _gated;

        /// <summary>Set by the <c>birthright=&lt;km&gt;</c> argument: overrides
        /// <see cref="FactionTuning.BirthrightRangeKm"/> for the run. Negative leaves the tuning value
        /// alone, which is the default.
        ///
        /// <para>The birthright is what <c>hidden&amp;reachable</c> is measured against rather than a
        /// detail of the gate, so sweeping it is how that column is turned into a curve. The header
        /// echoes whatever is in force, so a run whose override did not apply says so in its own first
        /// lines instead of quietly reporting the default.</para></summary>
        private static double _birthrightKm = -1;

        private static int RunKnowledgeLoop(
            FactionTuning tuning, WorldModel world, FactionBrain brain,
            List<(FactionState st, Personality p)> factions, int ticks, int seed, string label)
        {
            tuning.KnowledgeGatedBrains = _gated;
            if (_birthrightKm >= 0) tuning.BirthrightRangeKm = _birthrightKm;
            int scanLinks = world.DensifyTopology(true).LinksAdded;
            NodeProximityIndex index = NodeProximityIndex.Build(
                world.Graph, Math.Max(50.0, tuning.SensorNodePassiveRangeKm * Signature.Max));

            Console.WriteLine("=== knowledge — " + label + ", seed " + seed + ", " + ticks + " ticks, " + factions.Count + " factions ===");
            Console.WriteLine("A station-only world, as a fresh init+scan produces. " + world.Graph.Nodes.Count
                              + " nodes, " + scanLinks + " topology links from the post-scan densify.");
            Console.WriteLine("tick = " + (int)TimeSpan.FromMinutes(30).TotalMinutes + " min, so "
                              + ticks + " ticks = " + (ticks * 30.0 / 1440.0).ToString("0.0") + " days of sim time.");
            Console.WriteLine("horizon=" + (int)tuning.DiscoveryHorizon.TotalDays + "d  birthright="
                              + tuning.BirthrightRangeKm + "km  nodeSensor=" + tuning.SensorNodePassiveRangeKm
                              + "km  fleetSensor=" + tuning.SensorFleetRangeKm + "km");
            if (_gated)
            {
                Console.WriteLine("KnowledgeGatedBrains=TRUE. Brains generate candidates only for nodes they know, and");
                Console.WriteLine("read a rival node's owner/hold/value as of last detection. Compare against the same");
                Console.WriteLine("seed and tick count with the flag off.");
            }
            else
            {
                Console.WriteLine("Nothing is gated on knowledge: this is the CONTROL run. Re-run it with `gated`,");
                Console.WriteLine("same seed and ticks, and compare the economy columns.");
            }
            if (_probeFleets)
            {
                Console.WriteLine();
                Console.WriteLine("PROBE FLEETS ON: one 800km+ Patrol per faction every 4 ticks, regardless of what the");
                Console.WriteLine("brains want. This is a deliberate probe of SweepFleetSegment, not organic traffic --");
                Console.WriteLine("read `outside` (learned entries NOT already in the birthright) as the answer.");
            }
            if (label == "synthetic")
            {
                Console.WriteLine();
                Console.WriteLine("!! SMOKE TEST, NOT A VALID BASELINE. The synthetic world idles ~94% of decisions");
                Console.WriteLine("   and contracts, so slice C cannot make it worse and a comparison proves nothing.");
                Console.WriteLine("   For a real control run, point this at a brains directory:");
                Console.WriteLine("     knowledge <path-to-Instance/Factions> [ticks]");
            }
            Console.WriteLine();

            int genesisKnown = 0, genesisBirthright = 0;
            int dispatches = 0;
            // Which actions the brains actually chose. This is the sharpest control column of the
            // lot: slice C does not change the world directly, it changes what the brain can see to
            // choose from, so a shift in this histogram IS the effect being measured.
            var actionTally = new Dictionary<ActionKind, int>();
            // What the gate actually removes is options, not outcomes. On a world whose economy is
            // already at the floor for unrelated reasons (FAC-121), the action histogram cannot move
            // much -- but the number of candidates a brain could see to choose from moves directly,
            // and so does the trade-partner list, which is gate question 1 measured rather than argued.
            long candidateTotal = 0;
            long thinkCount = 0;
            var candidateKinds = new Dictionary<ActionKind, int>();
            // Counting candidates is not enough on its own: Raid, Conquer, Gift and ProposePact each
            // emit at most ONE candidate -- the best target they could find -- so the gate can change
            // WHICH node a faction goes after without moving any count at all. The distinct target
            // sets below are what actually separates a gated run from its control.
            var proposedNodes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var proposedFactions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var f0 in factions)
            {
                proposedNodes[f0.st.Tag] = new HashSet<string>(StringComparer.Ordinal);
                proposedFactions[f0.st.Tag] = new HashSet<string>(StringComparer.Ordinal);
            }

            // Half an hour per tick, not a minute. Both mechanisms this run measures live on a scale
            // a one-minute tick cannot reach: real action cooldowns are hours to days (Expand 2 days,
            // Develop 12h, Reinforce 1-2h), and DiscoveryHorizon is 30 days. At a minute a tick, 200
            // ticks is 3.3 hours -- nothing comes off cooldown, brains idle ~94% of decisions, and
            // the run measures a frozen world. `live` mode hit the same wall and clears cooldowns
            // instead; advancing the clock is the more faithful fix here, because knowledge expiry
            // is itself time-based and clearing cooldowns would not move it.
            TimeSpan tickSpan = TimeSpan.FromMinutes(30);
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.Add(tickSpan);
                world.AccrueIncome();

                foreach (var f in factions)
                {
                    Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                    thinkCount++;
                    if (d.Scored != null)
                    {
                        candidateTotal += d.Scored.Count;
                        for (int c = 0; c < d.Scored.Count; c++)
                        {
                            FactionIntent intent = d.Scored[c].Intent;
                            int ct;
                            candidateKinds.TryGetValue(intent.Kind, out ct);
                            candidateKinds[intent.Kind] = ct + 1;
                            if (!string.IsNullOrEmpty(intent.TargetNodeId))
                                proposedNodes[f.st.Tag].Add(intent.TargetNodeId);
                            if (!string.IsNullOrEmpty(intent.TargetFactionTag))
                                proposedFactions[f.st.Tag].Add(intent.TargetFactionTag);
                        }
                    }
                    int tally;
                    actionTally.TryGetValue(d.Intent.Kind, out tally);
                    actionTally[d.Intent.Kind] = tally + 1;
                    // The sim's shared Enact applies instant world effects and never creates a
                    // PendingFleetAction, so a scenario built on it alone can never exercise the
                    // fleet-as-sensor channel -- half of how knowledge grows. Route the fleet-bearing
                    // kinds through the real resolver instead, and let everything else enact as usual.
                    if (IsFleetAction(d.Intent.Kind) && !string.IsNullOrEmpty(d.Intent.TargetNodeId)
                        && d.Intent.Cost <= world.GetTreasury(f.st.Tag))
                    {
                        world.Spend(f.st.Tag, d.Intent.Cost);
                        f.st.PendingFleets.Add(PendingFleetResolver.CreatePending(
                            d.Intent.Kind, f.st.Tag, d.Intent, world, tuning, immediate: false));
                        dispatches++;
                    }
                    else Enact(world, tuning, f.st, d);
                }
                if (_probeFleets && tick % 4 == 1) DispatchProbeFleets(world, tuning, factions, tick, ref dispatches);

                // Sweep BEFORE arrivals resolve: resolution drops the fleet from PendingFleets, and
                // at these speeds most legs begin and end inside one tick, so sweeping afterwards
                // would see an empty list and measure nothing.
                SweepFleetKnowledge(world, index, tuning, factions, tickSpan);

                var arrivalStates = new List<FactionState>();
                foreach (var f in factions) arrivalStates.Add(f.st);
                PendingFleetResolver.ResolveArrivals(
                    arrivalStates, world, tuning, new SeededRandom(seed + tick), new List<string>(), null);
                ApplyDiplomacy(world, factions, tuning);
                // Territory is held CONSTANT, deliberately. The live server keeps hold up through the
                // manager layer (Reinforce, presence, standing orders) that this sim does not run, so
                // decay here is unopposed: at 30 minutes a tick and HoldDecayPerHour 0.24, an
                // unreinforced node reaches zero in ~6 ticks. A first pass without this watched DVSR
                // fall from 138 nodes to 7 and UNKN take 2940 of 3164 — measuring the sim's missing
                // manager layer, not anything about knowledge. Stamping the grace clock each tick is
                // the existing mechanism for "someone is maintaining this", and it keeps the baseline
                // stable so a slice-C run diverges against a fixed background rather than a collapsing
                // one. ReinforceGracePeriod is 1800s, exactly one tick.
                foreach (SpatialNode maintained in world.Graph.Nodes.Values)
                    if (maintained != null && maintained.OwnerFactionTag != null
                        && maintained.OwnerFactionTag != NeutralFaction.Tag)
                        maintained.LastReinforcedUtc = world.UtcNow;
                world.DecayInfluence(tuning.HoldDecayForIntervalSeconds(tickSpan.TotalSeconds),
                                     tuning.PlayerHoldDecayForIntervalSeconds(tickSpan.TotalSeconds),
                                     null, tickSpan.TotalSeconds);
                world.DecayThreat(tuning.ThreatDecayForIntervalSeconds(tickSpan.TotalSeconds));

                // Expand adds nodes, so the broad-phase has to notice. Same staleness signal the host
                // uses; ownership drift alone never invalidates it.
                if (index.IsStaleFor(world.Graph))
                    index = NodeProximityIndex.Build(
                        world.Graph, Math.Max(50.0, tuning.SensorNodePassiveRangeKm * Signature.Max));

                // The same bodies the host blocks sightlines with, so the sim and the server discover
                // the same nodes. A planetless sim world yields SensorOcclusion.None.
                SensorOcclusion occlusion = SensorOcclusion.From(world.Planets, tuning);
                foreach (var f in factions)
                    KnowledgeSweep.SweepOwnedNodes(world.Graph, index, f.st, tuning, world.UtcNow, occlusion);

                bool firstTick = tick == 1;
                if (firstTick || tick % Math.Max(1, ticks / 8) == 0 || tick == ticks)
                {
                    int totalKnown = 0, totalBirthright = 0;
                    Console.WriteLine("t" + tick.ToString("000") + "  graph=" + world.Graph.Nodes.Count);
                    foreach (var f in factions)
                    {
                        KnowledgeLine line = MeasureKnowledge(world, tuning, f.st);
                        totalKnown += line.Known;
                        totalBirthright += line.Birthright;
                        Console.WriteLine("      " + f.st.Tag
                            + "  owned=" + line.Owned.ToString().PadLeft(3)
                            + "  birthright=" + line.Birthright.ToString().PadLeft(3)
                            + "  learned=" + line.Learned.ToString().PadLeft(4)
                            + "  outside=" + line.LearnedOutside.ToString().PadLeft(4)
                            + "  known=" + line.Known.ToString().PadLeft(3)
                            + " (" + line.PercentOfGraph.ToString("0.0").PadLeft(5) + "% of graph)"
                            + "   treasury=" + world.GetTreasury(f.st.Tag).ToString().PadLeft(9));
                    }
                    if (firstTick) { genesisKnown = totalKnown; genesisBirthright = totalBirthright; }
                }
            }

            Console.WriteLine();
            Console.WriteLine("--- growth ---");
            int endKnown = 0, endLearned = 0;
            foreach (var f in factions)
            {
                KnowledgeLine line = MeasureKnowledge(world, tuning, f.st);
                endKnown += line.Known;
                endLearned += line.Learned;
            }
            int endOutside = 0;
            foreach (var f in factions) endOutside += MeasureKnowledge(world, tuning, f.st).LearnedOutside;
            Console.WriteLine("known (summed over factions): genesis " + genesisKnown + " -> " + endKnown
                              + "   learned by detection: " + endLearned);
            Console.WriteLine("learned OUTSIDE the birthright floor: " + endOutside
                              + "   <- the only number that shows detection contributing anything");
            if (endLearned == 0)
                Console.WriteLine("  !! learned=0 after " + ticks + " ticks. Knowledge is frozen at its floor:");
            Console.WriteLine("     birthright at genesis was " + genesisBirthright
                              + " -- anything above that is what sensors and traffic actually found.");
            Console.WriteLine();
            Console.WriteLine("--- economy control (compare a slice-C run against these) ---");
            Console.WriteLine("  fleet dispatches : " + dispatches);
            var ordered = new List<KeyValuePair<ActionKind, int>>(actionTally);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            var actions = new StringBuilder();
            foreach (KeyValuePair<ActionKind, int> kv in ordered)
            {
                if (actions.Length > 0) actions.Append("  ");
                actions.Append(kv.Key).Append('=').Append(kv.Value);
            }
            Console.WriteLine("  actions chosen   : " + actions);
            Console.WriteLine("  candidates       : " + candidateTotal + " over " + thinkCount + " thinks ("
                              + (thinkCount > 0 ? (candidateTotal / (double)thinkCount) : 0).ToString("0.00")
                              + " per think)");
            var orderedKinds = new List<KeyValuePair<ActionKind, int>>(candidateKinds);
            orderedKinds.Sort((a, b) => b.Value.CompareTo(a.Value));
            var kinds = new StringBuilder();
            foreach (KeyValuePair<ActionKind, int> kv in orderedKinds)
            {
                if (kinds.Length > 0) kinds.Append("  ");
                kinds.Append(kv.Key).Append('=').Append(kv.Value);
            }
            Console.WriteLine("  candidate kinds  : " + kinds);
            var partners = new StringBuilder();
            int partnerTotal = 0;
            foreach (var f in factions)
            {
                if (partners.Length > 0) partners.Append("  ");
                partners.Append(f.st.Tag).Append('=').Append(f.st.TradePriority.Count);
                partnerTotal += f.st.TradePriority.Count;
            }
            Console.WriteLine("  trade partners   : " + partnerTotal + " total -- " + partners);
            var targets = new StringBuilder();
            int targetTotal = 0, targetFactionTotal = 0;
            foreach (var f in factions)
            {
                int nodes = proposedNodes[f.st.Tag].Count, tags = proposedFactions[f.st.Tag].Count;
                targetTotal += nodes;
                targetFactionTotal += tags;
                if (targets.Length > 0) targets.Append("  ");
                targets.Append(f.st.Tag).Append('=').Append(nodes).Append('/').Append(tags);
            }
            Console.WriteLine("  distinct targets : " + targetTotal + " nodes, " + targetFactionTotal
                              + " factions (per faction nodes/factions) -- " + targets);
            ReportGateEffect(world, tuning, factions);
            Console.WriteLine("  nodes in graph   : " + world.Graph.Nodes.Count);
            long treasury = 0;
            foreach (var f in factions) treasury += world.GetTreasury(f.st.Tag);
            Console.WriteLine("  total treasury   : " + treasury);
            Console.WriteLine();
            Console.WriteLine("A slice-C run that stalls shows up here as dispatches collapsing toward zero and");
            Console.WriteLine("the node count flat, NOT as an error. That is the whole reason this run exists.");
            return 0;
        }

        /// <summary>What the gate actually takes away, measured rather than assumed.
        ///
        /// <para>A candidate count cannot answer this: Raid, Conquer, Gift and ProposePact each emit
        /// only their single best target, so hiding two thirds of the map moves no count and may move
        /// no target either -- if everything hidden was unusable anyway. The number that decides it is
        /// <c>hidden&amp;reachable</c>: foreign nodes the gate removes that the faction could actually
        /// have projected force to. Zero there means the gate is a no-op on this world, and says so
        /// out loud instead of leaving two identical runs to be read as a broken flag.</para></summary>
        private static void ReportGateEffect(
            WorldModel world, FactionTuning tuning, List<(FactionState st, Personality p)> factions)
        {
            if (world.Influence == null) return;
            DateTime now = world.UtcNow;
            Console.WriteLine();
            Console.WriteLine("--- what the gate removes (foreign nodes; reachable = influence >= RaidReachThreshold) ---");
            int totHidden = 0, totHiddenReachable = 0;
            foreach (var f in factions)
            {
                int foreign = 0, hidden = 0, reachable = 0, hiddenReachable = 0;
                foreach (SpatialNode n in world.Graph.Nodes.Values)
                {
                    if (n == null || n.OwnerFactionTag == f.st.Tag) continue;
                    foreign++;
                    bool canReach = world.Influence.SampleAt(n.Id, f.st.Tag) >= tuning.RaidReachThreshold;
                    if (canReach) reachable++;
                    NodeBelief belief;
                    bool visible = KnowledgeGate.TryBelief(
                        world.Graph, f.st.Tag, f.st.Knowledge, n, now,
                        tuning.DiscoveryHorizon, tuning.BirthrightRangeKm, out belief);
                    if (visible) continue;
                    hidden++;
                    if (canReach) hiddenReachable++;
                }
                totHidden += hidden;
                totHiddenReachable += hiddenReachable;
                Console.WriteLine("      " + f.st.Tag
                    + "  foreign=" + foreign.ToString().PadLeft(3)
                    + "  reachable=" + reachable.ToString().PadLeft(3)
                    + "  hidden=" + hidden.ToString().PadLeft(3)
                    + "  hidden&reachable=" + hiddenReachable.ToString().PadLeft(3));
            }
            Console.WriteLine("  hidden " + totHidden + ", of which reachable " + totHiddenReachable
                              + (totHiddenReachable == 0
                                 ? "  <- the gate changes NOTHING here: everything it hides was out of reach anyway"
                                 : "  <- this is what a gated run loses"));
        }

        /// <summary>Action kinds the live host dispatches as a fleet rather than applying instantly.</summary>
        private static bool IsFleetAction(ActionKind kind)
        {
            return kind == ActionKind.Raid || kind == ActionKind.Retaliate
                || kind == ActionKind.Patrol || kind == ActionKind.Guard
                || kind == ActionKind.Reinforce;
        }

        /// <summary>One long-range fleet per faction, origin = an owned node, target = a node at
        /// least MinProbeRangeKm away. The distance is the point: a short hop stays inside the
        /// birthright neighbourhood the fleet departed from and could not detect anything new even
        /// if the channel works perfectly.</summary>
        /// <summary>Fleet half of the sweep, hoisted so it can run before arrivals are resolved.
        /// An arrived fleet is included on purpose -- see the host's matching comment.</summary>
        private static void SweepFleetKnowledge(
            WorldModel world, NodeProximityIndex index, FactionTuning tuning,
            List<(FactionState st, Personality p)> factions, TimeSpan tickSpan)
        {
            SensorOcclusion occlusion = SensorOcclusion.From(world.Planets, tuning);
            foreach (var f in factions)
            {
                for (int i = 0; i < f.st.PendingFleets.Count; i++)
                {
                    PendingFleetAction fleet = f.st.PendingFleets[i];
                    if (fleet.Intercepted || fleet.Resolved) continue;
                    KnowledgeSweep.SweepFleetSegment(world.Graph, index, f.st,
                        fleet.CurrentPosKm(world.UtcNow - tickSpan, tuning),
                        fleet.CurrentPosKm(world.UtcNow, tuning), tuning, world.UtcNow,
                        null, null, occlusion);
                }
            }
        }

        private static void DispatchProbeFleets(
            WorldModel world, FactionTuning tuning,
            List<(FactionState st, Personality p)> factions, int tick, ref int dispatches)
        {
            const double MinProbeRangeKm = 800.0;
            var nodes = new List<SpatialNode>(world.Graph.Nodes.Values);
            if (nodes.Count == 0) return;

            for (int f = 0; f < factions.Count; f++)
            {
                FactionState st = factions[f].st;
                SpatialNode origin = null;
                foreach (SpatialNode n in nodes)
                    if (n != null && n.OwnerFactionTag == st.Tag) { origin = n; break; }
                if (origin == null) continue;

                SpatialNode target = null;
                int start = (tick * 7919 + f * 104729) % nodes.Count;
                for (int i = 0; i < nodes.Count; i++)
                {
                    SpatialNode candidate = nodes[(start + i) % nodes.Count];
                    if (candidate == null || candidate.Id == origin.Id) continue;
                    if (Sensing.DistanceSquared(origin.Position, candidate.Position)
                        < MinProbeRangeKm * MinProbeRangeKm) continue;
                    target = candidate;
                    break;
                }
                if (target == null) continue;

                var intent = new FactionIntent
                {
                    Kind = ActionKind.Patrol,
                    TargetNodeId = target.Id,
                    Cost = 0,
                };
                st.PendingFleets.Add(PendingFleetResolver.CreatePending(
                    ActionKind.Patrol, st.Tag, intent, world, tuning, immediate: false));
                dispatches++;
            }
        }

        private struct KnowledgeLine
        {
            public int Owned, Birthright, Learned, Known;
            /// <summary>Live learned entries that are NOT in the birthright set. This is the only
            /// column that answers "is detection contributing anything at all" -- `learned` alone
            /// cannot, because an entry already inside the floor changes no answer. Should have been
            /// here from the first version; its absence is why the first run's reading needed
            /// hand-checking against `known == birthright`.</summary>
            public int LearnedOutside;
            public double PercentOfGraph;
        }

        private static KnowledgeLine MeasureKnowledge(WorldModel world, FactionTuning tuning, FactionState st)
        {
            DateTime now = world.UtcNow;
            TimeSpan horizon = tuning.DiscoveryHorizon;
            double range = tuning.BirthrightRangeKm;

            var line = new KnowledgeLine();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n != null && n.OwnerFactionTag == st.Tag) line.Owned++;

            HashSet<string> birthrightSet = KnowledgeView.KnownNodeIds(world.Graph, st.Tag, null, now, horizon, range);
            line.Birthright = birthrightSet.Count;
            line.Known = KnowledgeView.KnownNodeIds(world.Graph, st.Tag, st.Knowledge, now, horizon, range).Count;
            foreach (KeyValuePair<string, DateTime> kv in st.Knowledge.Entries)
            {
                if (horizon > TimeSpan.Zero && now - kv.Value >= horizon) continue;
                line.Learned++;
                if (!birthrightSet.Contains(kv.Key)) line.LearnedOutside++;
            }
            int graph = world.Graph.Nodes.Count;
            line.PercentOfGraph = graph > 0 ? 100.0 * line.Known / graph : 0.0;
            return line;
        }

        // ---- full animated dashboard: every behaviour at once ----------------------------------
        private static int RunWorld(int seed, int frames, int delayMs, bool color)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();

            // three young factions seeded apart; they grow on minted income, expand, collide and contest.
            var factions = new List<(FactionState st, Personality p)>
            {
                SeedYoung(world, ps, "MERC", "mercantile", 90000, new Vec3(-90, 0, -40), seed + 1),
                SeedYoung(world, ps, "RVN",  "raider",     90000, new Vec3(110, 0,   0), seed + 2),
                SeedYoung(world, ps, "STL",  "settler",    90000, new Vec3(0,   0, 120), seed + 3),
            };
            // a weak MERC outpost planted next to RVN's core — besieged at birth; watch MERC shed it.
            AddSource(world, "MERC", new Vec3(116, 0, 6), 0.4, 0.3);

            var last = new Dictionary<string, string>();
            if (color) { HeatmapRenderer.EnableAnsi(); try { Console.Clear(); } catch { } }
            for (int tick = 0; tick <= frames; tick++)   // tick 0 = the seed state (besieged *M visible before MERC acts)
            {
                if (tick > 0)
                {
                    world.UtcNow = world.UtcNow.AddMinutes(1);
                    world.AccrueIncome();                               // 1) minted income -> ledgers
                    foreach (var f in factions)
                    {
                        Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                        Enact(world, tuning, f.st, d);                  // 2) expand/claim/reinforce/develop/withdraw
                        last[f.st.Tag] = d.Goal + " " + d.Intent;
                    }
                    ApplyDiplomacy(world, factions, tuning);            // relations drift + raid fallout
                    world.DecayInfluence(0.025);                        // 3) hold decays — use it or lose it
                }
                string frame = HeatmapRenderer.Frame(world, factions, tick, 40, 20, color, last);
                if (color) { Console.Write(HeatmapRenderer.Home()); Console.Write(frame); Console.Write(Legend()); Thread.Sleep(delayMs); }
                else { Console.WriteLine(frame + Legend()); }
            }
            if (color) Console.Write(HeatmapRenderer.Reset() + "\n");
            return 0;
        }

        private static (FactionState, Personality) SeedYoung(WorldModel w, Dictionary<string, Personality> ps,
            string tag, string persId, long treasury, Vec3 home, int rngSeed)
        {
            SpatialNode core = w.Graph.Add(home, 0.55); core.OwnerFactionTag = tag; core.HoldStrength = 0.6; core.Radius = 30;
            SpatialNode f1 = w.Graph.Add(new Vec3(home.X + 30, home.Y, home.Z + 12), 0.7); f1.Radius = 30;
            SpatialNode f2 = w.Graph.Add(new Vec3(home.X - 12, home.Y, home.Z + 30), 0.5); f2.Radius = 30;
            w.Graph.Connect(core.Id, f1.Id); w.Graph.Connect(core.Id, f2.Id);
            w.SetTreasury(tag, treasury);
            return (new FactionState { Tag = tag, PersonalityId = persId, RngSeed = rngSeed }, ps[persId]);
        }

        private static string Legend()
        {
            return "  map: UPPER=strong infl  lower=weak  ><=contested border  .=empty\n" +
                   "  watch: cash climbs (income) | src climbs (expand/claim) | ctl<1 = contested | MERC sheds its besieged outpost (Withdraw)\n";
        }

        // ---- animated influence heatmap --------------------------------------------------------
        private static int RunHeatmap(int seed, int frames, int delayMs, bool color)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var personalities = BuildPersonalities();
            // a tight triangle, so the spheres actually meet
            var factions = new List<(FactionState st, Personality p)>
            {
                SeedSource(world, personalities, "MERC", "mercantile", 300000, new Vec3(0,   0, 0),   seed + 1),
                SeedSource(world, personalities, "RVN",  "raider",     250000, new Vec3(150, 0, 0),   seed + 2),
                SeedSource(world, personalities, "STL",  "settler",    280000, new Vec3(75,  0, 130), seed + 3),
            };

            if (color) { HeatmapRenderer.EnableAnsi(); try { Console.Clear(); } catch { } }

            for (int tick = 1; tick <= frames; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                foreach (var f in factions)
                {
                    var faRng = new SeededRandom(f.st.RngSeed + tick);
                    Decision d = brain.Think(f.st, f.p, world, faRng, tuning);
                    Enact(world, tuning, f.st, d);
                }
                world.DecayInfluence(0.03); // linear decay

                string frame = HeatmapRenderer.Frame(world, factions, tick, 40, 20, color);
                if (color) { Console.Write("[H"); Console.Write(frame); Thread.Sleep(delayMs); }
                else { Console.WriteLine(frame); }
            }
            if (color) Console.Write("[0m\n");
            return 0;
        }

        // ---- persistence smoke test ------------------------------------------------------------
        private static int RunPersist(int seed, int ticks)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var personalities = BuildPersonalities();
            var factions = new List<(FactionState st, Personality p)>
            {
                SeedSource(world, personalities, "MERC", "mercantile", 300000, new Vec3(0,   0, 0),   seed + 1),
                SeedSource(world, personalities, "RVN",  "raider",     250000, new Vec3(150, 0, 0),   seed + 2),
                SeedSource(world, personalities, "STL",  "settler",    280000, new Vec3(75,  0, 130), seed + 3),
            };

            Console.WriteLine("=== persistence smoke test — seed " + seed + ", " + ticks + " ticks ===");
            Simulate(world, brain, tuning, factions, ticks);

            string dir = Path.Combine(Path.GetTempPath(), "factions-persist-" + seed);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            SaveAll(new FactionStore(dir), world, factions);
            long total = ReportSizes(new FactionStore(dir), "saved to " + dir + " :");

            // reload into a fresh world (sources only; heatmap is rebuilt on demand, never stored)
            var store2 = new FactionStore(dir);
            var graph2 = new SpatialGraph();
            var states2 = new List<FactionState>();
            foreach (IndexEntry e in store2.LoadIndex())
                states2.Add(FactionStore.ApplyBrainDoc(store2.LoadBrain(e.Tag), graph2, tuning.CampaignCooldown));

            bool ok = states2.Count == factions.Count;
            Console.WriteLine();
            Console.Write("round-trip: " + states2.Count + " factions reloaded; sources(after/before) ");
            foreach (var f in factions)
            {
                int before = world.Graph.OwnedBy(f.st.Tag).Count();
                int after = graph2.OwnedBy(f.st.Tag).Count();
                if (before != after) ok = false;
                Console.Write(f.st.Tag + " " + after + "/" + before + "  ");
            }
            Console.WriteLine(ok ? "-> OK" : "-> MISMATCH");

            // bloat check: keep playing (4x more ticks) and re-save; size must track territory, not time
            Simulate(world, brain, tuning, factions, ticks * 4);
            string dirLong = dir + "-long";
            if (Directory.Exists(dirLong)) Directory.Delete(dirLong, true);
            SaveAll(new FactionStore(dirLong), world, factions);
            long totalLong = TotalSize(new FactionStore(dirLong));
            int maxLog = 0;
            foreach (var f in factions) maxLog = Math.Max(maxLog, f.st.Log.Count);

            Console.WriteLine();
            Console.WriteLine("bloat check (played 5x longer, " + (ticks * 5) + " ticks total):");
            Console.WriteLine("  total bytes " + total + " -> " + totalLong + "   (tracks live sources/territory, not playtime)");
            Console.WriteLine("  decision log capped at " + FactionStore.DefaultLogCap + " per brain (max now " + maxLog + ") -> constant vs playtime");
            Console.WriteLine("  heatmap/octree: not persisted (derived from sources on load)");
            return ok ? 0 : 1;
        }

        private static void Simulate(WorldModel world, FactionBrain brain, FactionTuning tuning, List<(FactionState st, Personality p)> factions, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                foreach (var f in factions)
                {
                    var faRng = new SeededRandom(f.st.RngSeed + i);
                    Decision d = brain.Think(f.st, f.p, world, faRng, tuning);
                    Enact(world, tuning, f.st, d);
                    f.st.Log.Add(world.UtcNow.ToString("HH:mm") + " " + d.Intent);
                    if (f.st.Log.Count > FactionStore.DefaultLogCap) f.st.Log.RemoveAt(0);
                }
                world.DecayInfluence(0.03);
            }
        }

        private static void SaveAll(FactionStore store, WorldModel world, List<(FactionState st, Personality p)> factions)
        {
            var index = new List<IndexEntry>();
            foreach (var f in factions)
            {
                store.SaveBrain(FactionStore.ToBrainDoc(f.st, world.Graph.OwnedBy(f.st.Tag), FactionStore.DefaultLogCap));
                index.Add(FactionStore.ToIndexEntry(f.st, world.UtcNow.AddMinutes(1)));
            }
            store.SaveIndex(index);
        }

        private static long ReportSizes(FactionStore store, string header)
        {
            Console.WriteLine(header);
            long total = 0;
            var idxFi = new FileInfo(store.IndexPath);
            Console.WriteLine("  index.json".PadRight(24) + idxFi.Length + " bytes  (" + store.LoadIndex().Count + " factions)");
            total += idxFi.Length;
            foreach (string tag in store.ListBrainTags())
            {
                var fi = new FileInfo(store.BrainPath(tag));
                BrainDoc doc = store.LoadBrain(tag);
                int srcs = doc.Sources != null ? doc.Sources.Count : 0;
                int log = doc.Log != null ? doc.Log.Count : 0;
                Console.WriteLine(("  brains/" + tag + ".json").PadRight(24) + fi.Length + " bytes  (" + srcs + " sources, log " + log + ")");
                total += fi.Length;
            }
            Console.WriteLine("  total".PadRight(24) + total + " bytes");
            return total;
        }

        private static long TotalSize(FactionStore store)
        {
            long total = new FileInfo(store.IndexPath).Length;
            foreach (string tag in store.ListBrainTags()) total += new FileInfo(store.BrainPath(tag)).Length;
            return total;
        }

        // ---- insolvency lifecycle: bleed -> floor -> force-shed -> conquered/dormant ----------------
        private static int RunCollapse(int seed, int ticks)
        {
            var tuning = LoadSimTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // A static rival floods influence over the same space (the besieger; we never run its brain).
            SpatialNode hege = world.Graph.Add(new Vec3(0, 0, 0), 0.8, "HEGE_CORE");
            hege.OwnerFactionTag = "HEGE"; hege.HoldStrength = 2.5; hege.Radius = 200;  // dominates control here
            world.SetTreasury("HEGE", 1000000);

            // The victim: a zealot fixated on the threat (Security-dominant), so it NEVER runs the Wealth
            // consolidation branch and will not abandon its money-losing outposts itself. Its three outposts use
            // scan-backed (G###) ids — real assets, decay-exempt — so ONLY insolvency force-shed can reclaim them.
            var zealot = new Personality { Id = "zealot", Temperature = 0.2,
                Traits = new Traits { Xenophobia = 1.0, Mercantilism = 0.0, Aggression = 1.0, Expansionism = 0.3 } };
            zealot.Weights[Drive.Security] = 1.6; zealot.Weights[Drive.Rivalry] = 1.4; zealot.Weights[Drive.Growth] = 0.5;
            zealot.Weights[Drive.Wealth] = 0.1; zealot.Weights[Drive.Caution] = 0.3;   // Wealth barely weighted -> won't self-shed
            var doom = new FactionState { Tag = "DOOM", PersonalityId = "zealot", RngSeed = seed + 7 };
            for (int i = 0; i < 3; i++)
            {
                SpatialNode n = world.Graph.Add(new Vec3(20 * i, 0, 10 * i), 0.10, "G00" + (i + 1)); // low value -> a drain
                n.OwnerFactionTag = "DOOM"; n.HoldStrength = 0.5; n.Radius = 24;
            }
            world.SetTreasury("DOOM", 9000);

            Console.WriteLine("=== collapse — seed " + seed + ", " + ticks + " ticks ===");
            Console.WriteLine("DOOM holds 3 besieged, money-losing outposts inside rival HEGE's influence and is too");
            Console.WriteLine("zealous (Security-fixated) to abandon them: floor=" + tuning.InsolvencyFloor +
                              "  -> bleeds to the floor, the world force-sheds its drains, and it is conquered (dormant).");
            Console.WriteLine("tick  goal/intent                    treasury   netflow  src  state     money   target      gap");

            var roster = new List<FactionState> { doom };
            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();   // mint income; floor clamp + force-shed the insolvent (single-sourced in Core)

                string intent = "(asleep)";
                if (!FactionLifecycle.IsDormant(doom, world.UtcNow))
                {
                    Decision d = brain.Think(doom, zealot, world, new SeededRandom(doom.RngSeed + tick), tuning);
                    Enact(world, tuning, doom, d);
                    intent = (d.Goal + " -> " + d.Intent).ToString();
                }

                string transition = null;
                FactionLifecycle.Step(roster, world, tuning, tag => transition = "CONQUERED -> dormant");

                int src = world.Graph.OwnedBy("DOOM").Count();
                string state = transition ?? (FactionLifecycle.IsDormant(doom, world.UtcNow) ? "dormant" : "active");
                CentralBankSnapshot bank = world.SnapshotCentralBank();
                Console.WriteLine("t" + tick.ToString("00") + "   " + intent.PadRight(30) +
                                  world.GetTreasury("DOOM").ToString().PadLeft(8) + "  " +
                                  world.GetNetFlow("DOOM").ToString().PadLeft(8) + "  " +
                                  src.ToString().PadLeft(3) + "  " + state.PadRight(11) + " " +
                                  bank.CurrentMoneySupply.ToString().PadLeft(6) + "  " +
                                  bank.TargetMoneySupply.ToString().PadLeft(7) + "  " +
                                  bank.MoneySupplyGap.ToString().PadLeft(8));
            }
            return 0;
        }

        // ---- peaceful trade model: two scenarios in one run ----------------------------------------
        // Scenario 1 (MERC + STL): two peaceful factions at moderate range — both enter Growth/Wealth
        //   phases and trade. Trade boosts slow the drift-based souring from border overlap.
        // Scenario 2 (MERC + RVN): opposing motivations — RVN starts far enough that border friction is low,
        //   so Growth dominates early (raider trades briefly before expansion closes the distance). Once
        //   pressure rises RVN raids; MERC keeps trading to slow the sour. The asymmetry is visible: each
        //   MERC Trade nudges +TradeRelationBoost; each RVN Raid slams −RaidRelationHit (~10× larger).
        private static int RunTrade(int seed, int ticks)
        {
            var tuning = new FactionTuning();
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();

            // Triangle: MERC@origin(merc pair with STL), RVN 70 km east (low initial friction -> Growth
            // phase), STL between them so RVN can reach STL as a trade target while raiding MERC.
            // RVN-STL distance ~35 km → RVN influence at STL is high enough for Trade to beat Idle.
            var merc = SeedSource(world, ps, "MERC", "mercantile", 200000, new Vec3(0,  0,  0),  seed + 1);
            var stl  = SeedSource(world, ps, "STL",  "settler",    200000, new Vec3(50, 0, 30),  seed + 2);
            var rvn  = SeedSource(world, ps, "RVN",  "raider",     200000, new Vec3(70, 0,  0),  seed + 3);
            var factions = new List<(FactionState st, Personality p)> { merc, stl, rvn };
            FactionState ms = merc.Item1, ss = stl.Item1, rs = rvn.Item1;

            Console.WriteLine("=== trade — seed " + seed + ", " + ticks + " ticks ===");
            Console.WriteLine("MERC=mercantile  STL=settler  RVN=raider");
            Console.WriteLine("scenario 1: MERC+STL (peaceful pair) -> both enter Growth; Trade boosts slow the drift sour");
            Console.WriteLine("scenario 2: MERC+RVN (opposing) -> RVN Growth-dominant early (trades); then raids as pressure rises; MERC keeps trading");
            Console.WriteLine("asymmetry: Trade=+" + tuning.TradeRelationBoost.ToString("0.00") +
                              "  Raid=-" + tuning.RaidRelationHit.ToString("0.00") + " (negative actions remembered ~10x longer)");
            Console.WriteLine("tick  MERC goal->intent          STL goal->intent          RVN goal->intent");
            Console.WriteLine("      MERC->STL  STL->MERC  |  MERC->RVN  RVN->MERC");

            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                foreach (var f in factions)
                {
                    Decision d = brain.Think(f.st, f.p, world, new SeededRandom(f.st.RngSeed + tick), tuning);
                    Enact(world, tuning, f.st, d);
                    Console.Write("t" + tick.ToString("00") + " " + f.st.Tag.PadRight(4) + " " +
                                  (d.Goal + "->" + d.Intent).PadRight(24));
                }
                Console.WriteLine();
                ApplyDiplomacy(world, factions, tuning);
                world.DecayInfluence(0.02);

                Console.WriteLine("     " +
                    "MERC->STL " + ms.RelationTo("STL").ToString("+0.00;-0.00") + "  " +
                    "STL->MERC " + ss.RelationTo("MERC").ToString("+0.00;-0.00") + "  |  " +
                    "MERC->RVN " + ms.RelationTo("RVN").ToString("+0.00;-0.00") + "  " +
                    "RVN->MERC " + rs.RelationTo("MERC").ToString("+0.00;-0.00"));
            }
            return 0;
        }

        // ---- war fatigue -> a negotiated truce resolves a stalemate --------------------------------
        private static int RunTruce(int seed, int ticks)
        {
            // demo-tuned fatigue so peace arrives snappily (and before either side is conquered by attrition)
            var tuning = new FactionTuning { WarFatigueRate = 0.05, RaidFatigue = 0.25 };
            var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
            var brain = new FactionBrain();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ps = BuildPersonalities();

            // Two evenly-matched neighbours with heavily overlapping influence -> constant border pressure -> war.
            // Balanced temperament: aggressive enough to raid, but they tire and ultimately sue for peace.
            var az = SeedSource(world, ps, "AZUR", "default", 300000, new Vec3(0, 0, 0), seed + 1);
            var cr = SeedSource(world, ps, "CRMS", "default", 300000, new Vec3(35, 0, 0), seed + 2);
            var factions = new List<(FactionState st, Personality p)> { az, cr };

            Console.WriteLine("=== truce — seed " + seed + ", " + ticks + " ticks: two neighbours fight, tire, and make peace ===");
            Console.WriteLine("knobs: fatigue +" + tuning.WarFatigueRate + "/hostile tick, +" + tuning.RaidFatigue +
                              "/raid; sue@" + tuning.PeaceThreshold + " accept@" + tuning.AcceptPeaceThreshold +
                              "; truce window " + tuning.TruceDuration.TotalMinutes + " ticks");
            Console.WriteLine("tick  AZUR goal->intent         rel    fat  |  CRMS goal->intent         rel    fat");

            for (int tick = 1; tick <= ticks; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();
                Decision da = brain.Think(az.Item1, az.Item2, world, new SeededRandom(az.Item1.RngSeed + tick), tuning);
                Enact(world, tuning, az.Item1, da);
                Decision db = brain.Think(cr.Item1, cr.Item2, world, new SeededRandom(cr.Item1.RngSeed + tick), tuning);
                Enact(world, tuning, cr.Item1, db);
                ApplyDiplomacy(world, factions, tuning);     // accrues fatigue, applies raids, prints "* TRUCE ..." on peace

                Console.WriteLine("t" + tick.ToString("00") + "   " +
                    (da.Goal + "->" + da.Intent).PadRight(21) +
                    az.Item1.RelationTo("CRMS").ToString("+0.00;-0.00") + "  " + az.Item1.FatigueTo("CRMS").ToString("0.00") + "  |  " +
                    (db.Goal + "->" + db.Intent).PadRight(21) +
                    cr.Item1.RelationTo("AZUR").ToString("+0.00;-0.00") + "  " + cr.Item1.FatigueTo("AZUR").ToString("0.00"));
            }
            return 0;
        }

        // ---- convoy: PendingFleetAction transit, meetings, resolution verification ----------------
        /// <summary>
        /// Exercises the full PendingFleetResolver pipeline without SE dependencies. Demonstrates:
        ///   1. Pending actions appear after Enact and effects are absent during transit.
        ///   2. Effects apply at arrival (immediate fast-path and distance-based).
        ///   3. Patrol intercepts Raid at the same node.
        ///   4. Raid intercepts Trade at the same node (high chance, loot).
        ///   5. Triple encounter: Patrol vs Raid + Trade.
        ///   6. Load-time reconciliation: overdue fleets resolve at full ratio.
        /// </summary>
        private static int RunConvoy(int seed)
        {
            Console.WriteLine("=== convoy mode — PendingFleetAction verification (seed " + seed + ") ===");
            Console.WriteLine();

            var rng = new SeededRandom(seed);
            var tuning = new FactionTuning { FleetSpeedKmPerMin = 10.0 }; // slow transit for demo (real-time minutes)

            // ---- SCENARIO 1: Immediate fast-path ---
            Console.WriteLine("--- S1: Immediate fast-path (EstimatedArrivalUtc = DepartedUtc) ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var nodeB = world.Graph.Add(new Vec3(100, 0, 0), 0.7, "B1"); nodeB.OwnerFactionTag = "B"; nodeB.HoldStrength = 0.8; nodeB.Radius = 40;
                world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1").OwnerFactionTag = "A";

                var stA = new FactionState { Tag = "A" };
                var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 12000 };
                var fleet  = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, world, tuning, immediate: true);
                stA.PendingFleets.Add(fleet);

                Console.WriteLine("  Created Raid A -> B1, immediate=" + fleet.HasArrived(world.UtcNow));
                Console.WriteLine("  Hold before: " + nodeB.HoldStrength.ToString("0.000"));

                var states = new List<FactionState> { stA, new FactionState { Tag = "B" } };
                var log    = new List<string>();
                PendingFleetResolver.ResolveArrivals(states, world, tuning, rng, log, null);

                Console.WriteLine("  Hold after:  " + nodeB.HoldStrength.ToString("0.000") + "  (should be < 0.8)");
                Console.WriteLine("  Pending fleets remaining: " + stA.PendingFleets.Count + "  (should be 0)");
                Console.WriteLine("  Log: " + (log.Count > 0 ? log[0] : "(none)"));
                Console.WriteLine();
            }

            // ---- SCENARIO 2: Distance-based transit (no immediate) ---
            Console.WriteLine("--- S2: Distance-based transit (100 km / 10 km/min = 10 ticks) ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var nodeB = world.Graph.Add(new Vec3(100, 0, 0), 0.7, "B1"); nodeB.OwnerFactionTag = "B"; nodeB.HoldStrength = 0.8; nodeB.Radius = 40;
                world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1").OwnerFactionTag = "A";

                var stA = new FactionState { Tag = "A" };
                var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 12000 };
                var fleet  = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, world, tuning, immediate: false);
                stA.PendingFleets.Add(fleet);

                double transitMins = (fleet.EstimatedArrivalUtc - fleet.DepartedUtc).TotalMinutes;
                Console.WriteLine("  Transit time: " + transitMins.ToString("0.0") + " min  (expect 10.0)");

                var states = new List<FactionState> { stA, new FactionState { Tag = "B" } };

                // Advance tick by tick; effects should be absent until arrival
                for (int tick = 1; tick <= 15; tick++)
                {
                    world.UtcNow = world.UtcNow.AddMinutes(1);
                    double ratio = fleet.CompletionRatio(world.UtcNow);
                    var logBuf = new List<string>();
                    PendingFleetResolver.ResolveArrivals(states, world, tuning, rng, logBuf, null);
                    string tag = logBuf.Count > 0 ? " <- RESOLVED" : "";
                    Console.WriteLine("  t" + tick.ToString("00") + "  ratio=" + ratio.ToString("0.00")
                                      + "  hold=" + nodeB.HoldStrength.ToString("0.000")
                                      + "  fleets=" + stA.PendingFleets.Count + tag);
                    if (stA.PendingFleets.Count == 0) break;
                }
                Console.WriteLine();
            }

            // ---- SCENARIO 3: Patrol vs Raid at same node ---
            Console.WriteLine("--- S3: Patrol intercepts Raid at same node ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var node = world.Graph.Add(new Vec3(0, 0, 0), 0.7, "MERC1"); node.OwnerFactionTag = "MERC"; node.HoldStrength = 0.8; node.Radius = 40;

                DateTime now = world.UtcNow;
                var stMerc = new FactionState { Tag = "MERC" };
                var stRvn  = new FactionState { Tag = "RVN" };

                // Add a recently-arrived, still-active patrol from MERC
                stMerc.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                    DepartedUtc = now.AddMinutes(-10), EstimatedArrivalUtc = now.AddMinutes(-5),
                    Resolved = true,
                });

                // Add an immediate-arrival Raid from RVN
                stRvn.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                    OriginPosKm = new Vec3(50, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 0
                });

                var states = new List<FactionState> { stMerc, stRvn };
                var log    = new List<string>();
                PendingFleetResolver.ResolveArrivals(states, world, tuning, rng, log, null);

                Console.WriteLine("  Hold after encounter: " + node.HoldStrength.ToString("0.000"));
                foreach (string l in log) Console.WriteLine("  " + l);
                Console.WriteLine();
            }

            // ---- SCENARIO 4: Raid + Trade at same node ---
            Console.WriteLine("--- S4: Raid intercepts Trade at destination ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                world.Graph.Add(new Vec3(0, 0, 0), 0.7, "DEST").OwnerFactionTag = "B";
                world.SetTreasury("RVN", 50000);

                DateTime now = world.UtcNow;
                var stRvn  = new FactionState { Tag = "RVN" };
                var stMerc = new FactionState { Tag = "MERC" };
                var stB    = new FactionState { Tag = "B" };
                const long cargo = 5000;

                stRvn.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "B", TargetNodeId = "DEST",
                    OriginPosKm = new Vec3(50, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 0
                });
                stMerc.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "B", TargetNodeId = "DEST",
                    OriginPosKm = new Vec3(-30, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = cargo
                });

                var states = new List<FactionState> { stRvn, stMerc, stB };
                var log    = new List<string>();
                double relBefore = stMerc.RelationTo("B");
                long treasBefore = world.GetTreasury("RVN");

                PendingFleetResolver.ResolveArrivals(states, world, tuning, rng, log, null);

                Console.WriteLine("  MERC relation before=" + relBefore.ToString("0.000")
                                  + "  after=" + stMerc.RelationTo("B").ToString("0.000"));
                Console.WriteLine("  RVN treasury before=" + treasBefore + "  after=" + world.GetTreasury("RVN"));
                foreach (string l in log) Console.WriteLine("  " + l);
                Console.WriteLine();
            }

            // ---- SCENARIO 5: Triple encounter ---
            Console.WriteLine("--- S5: Triple encounter (Patrol + Raid + Trade at same node) ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var node = world.Graph.Add(new Vec3(0, 0, 0), 0.7, "PORT"); node.OwnerFactionTag = "MERC"; node.HoldStrength = 0.8; node.Radius = 40;
                world.SetTreasury("RVN", 50000);

                DateTime now = world.UtcNow;
                var stMerc = new FactionState { Tag = "MERC" };
                var stRvn  = new FactionState { Tag = "RVN" };
                var stStl  = new FactionState { Tag = "STL" };

                // Active patrol
                stMerc.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "PORT",
                    DepartedUtc = now.AddMinutes(-10), EstimatedArrivalUtc = now.AddMinutes(-5),
                    Resolved = true,
                });
                // Arriving Raid
                stRvn.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "PORT",
                    OriginPosKm = new Vec3(80, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 0
                });
                // Arriving Trade
                stStl.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Trade, ActorTag = "STL", TargetTag = "MERC", TargetNodeId = "PORT",
                    OriginPosKm = new Vec3(-40, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 4000
                });

                var states = new List<FactionState> { stMerc, stRvn, stStl };
                var log    = new List<string>();
                PendingFleetResolver.ResolveArrivals(states, world, tuning, rng, log, null);

                Console.WriteLine("  Hold: " + node.HoldStrength.ToString("0.000")
                                  + "  STL->MERC rel: " + stStl.RelationTo("MERC").ToString("+0.000;-0.000"));
                foreach (string l in log) Console.WriteLine("  " + l);
                Console.WriteLine();
            }

            // ---- SCENARIO 6: Load-time reconciliation ---
            Console.WriteLine("--- S6: ReconcileAndResolveOnLoad (overdue fleet resolves at full ratio) ---");
            {
                var world = new WorldModel(new SeededRandom(seed)) { Tuning = tuning };
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var node = world.Graph.Add(new Vec3(0, 0, 0), 0.7, "B1"); node.OwnerFactionTag = "B"; node.HoldStrength = 0.8; node.Radius = 40;

                DateTime now = world.UtcNow;
                var stA = new FactionState { Tag = "A" };
                stA.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                    OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now.AddMinutes(-50), EstimatedArrivalUtc = now.AddMinutes(-10),
                    CargoValue = 0, Resolved = false
                });

                Console.WriteLine("  Hold before reconcile: " + node.HoldStrength.ToString("0.000") + "  (fleet was overdue)");
                var states = new List<FactionState> { stA, new FactionState { Tag = "B" } };
                var log    = new List<string>();
                PendingFleetResolver.ReconcileAndResolveOnLoad(states, world, tuning, rng, log, null);

                Console.WriteLine("  Hold after reconcile:  " + node.HoldStrength.ToString("0.000") + "  (should show full raid damage)");
                Console.WriteLine("  Log: " + (log.Count > 0 ? log[0] : "(none)"));
                Console.WriteLine();
            }

            Console.WriteLine("=== convoy mode complete ===");
            return 0;
        }

        // ---- live mode: load real server brains, run with transit-enabled spawner, render convoys ------
        /// <summary>
        /// Loads faction brains from the server instance path (read-only), then runs the brain
        /// tick loop with a <see cref="SimTransitSpawner"/> (SkipsTransit = false) so factions
        /// generate real transit-time convoys that are visible mid-route on the animated map.
        /// Never saves back — purely observational.
        /// </summary>
        private static int RunLive(string path, int frames, int delayMs)
        {
            const string DefaultPath = @"C:\Torch\torch-server\Instance\Factions";
            if (string.IsNullOrEmpty(path)) path = DefaultPath;

            var store = new FactionStore(path);
            List<IndexEntry> entries = store.LoadIndex();
            if (entries.Count == 0)
            {
                Console.Error.WriteLine("No faction index found at: " + path);
                Console.Error.WriteLine("Usage: live [path] [frames] [delayMs]");
                return 1;
            }

            var tuning = LoadSimTuning(path);
            var world = new WorldModel(new SeededRandom(20260607)) { Tuning = tuning };
            world.UtcNow = DateTime.UtcNow;
            var states = new List<FactionState>();
            var personalities = BuildPersonalities();

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

            // LastReinforcedUtc is not persisted in SourceDoc, so loaded nodes have DateTime.MinValue,
            // which bypasses the grace period and causes immediate decay. Stamp them as just-reinforced
            // so the sim starts from a stable state (the server was maintaining them moments ago).
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.LastReinforcedUtc == DateTime.MinValue) n.LastReinforcedUtc = world.UtcNow;

            // Cooldowns from the real server (Expand = 2 days, Develop = 12h, Reinforce = 1-2h) would
            // cause all factions to idle for most or all of a 120-tick run. Clear them so we see what
            // factions actually want to do — this is an observational visualizer, not a server replay.
            foreach (FactionState st in states)
                st.Cooldowns.Clear();

            // Resolve any overdue in-flight fleets from the persisted state before we start ticking.
            // This mirrors what FactionRuntime.Load() does on server boot.
            {
                var buf = new List<string>();
                PendingFleetResolver.ReconcileAndResolveOnLoad(states, world, tuning, new SeededRandom(20260607), buf, null);
            }

            var factions = new List<(FactionState st, Personality p)>();
            foreach (FactionState st in states)
            {
                Personality p;
                if (!personalities.TryGetValue(st.PersonalityId ?? "default", out p)) p = personalities["default"];
                factions.Add((st, p));
            }

            // SimTransitSpawner.SkipsTransit = false → CreatePending produces real transit time + BFS path
            var spawner = new SimTransitSpawner();
            var brain = new FactionBrain();
            var eventLog = new List<string>();
            var lastAction = new Dictionary<string, string>();

            HeatmapRenderer.EnableAnsi();
            try { Console.Clear(); } catch { }
            Console.WriteLine("=== Factions live mode — " + entries.Count + " factions from: " + path);
            Console.WriteLine("    " + frames + " ticks, " + delayMs + " ms/frame  (convoys: !! = raid  ~~ = trade  oo = patrol)");
            Thread.Sleep(900);

            for (int tick = 0; tick < frames; tick++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                world.AccrueIncome();

                // Resolve arriving fleets before new decisions (mirrors FactionRuntime.StepOnce)
                {
                    var arrivals = new List<string>();
                    PendingFleetResolver.ResolveArrivals(states, world, tuning,
                        new SeededRandom(unchecked(tick * 1103515245)), arrivals, null);
                    foreach (string msg in arrivals) eventLog.Add("t" + tick + " " + msg);
                }

                // Think and enact each active faction
                foreach (FactionState st in states)
                {
                    if (FactionLifecycle.IsDormant(st, world.UtcNow)) continue;
                    Personality p;
                    if (!personalities.TryGetValue(st.PersonalityId ?? "default", out p)) p = personalities["default"];
                    Decision d = brain.Think(st, p, world, new SeededRandom(unchecked(st.RngSeed + tick)), tuning);
                    EnactLive(world, tuning, st, d, spawner);
                    if (d.Intent.Kind != ActionKind.Idle)
                        lastAction[st.Tag] = d.Goal + " " + d.Intent.Kind;
                }

                ApplyDiplomacy(world, factions, tuning);
                world.DecayInfluence(0.02);
                FactionLifecycle.Step(states, world, tuning,
                    tag => eventLog.Add("t" + tick + " CONQUERED " + tag + " -> dormant"));

                // Collect all in-flight (not yet arrived, not intercepted) convoys for the renderer
                var convoys = new List<PendingFleetAction>();
                foreach (FactionState st in states)
                    foreach (PendingFleetAction fleet in st.PendingFleets)
                        if (!fleet.HasArrived(world.UtcNow) && !fleet.Intercepted)
                            convoys.Add(fleet);

                while (eventLog.Count > 12) eventLog.RemoveAt(0);

                string frame = HeatmapRenderer.Frame(world, factions, tick, 50, 22, true, lastAction, convoys, eventLog);
                Console.Write(HeatmapRenderer.Home());
                Console.Write(frame);
                Thread.Sleep(delayMs);
            }

            Console.Write(HeatmapRenderer.Reset() + "\n");
            return 0;
        }

        // Enact variant for the live mode: Trade/Raid/Patrol create PendingFleetActions with real
        // transit time (spawner.SkipsTransit = false) instead of applying effects directly.
        internal static void EnactLive(WorldModel world, FactionTuning t, FactionState st, Decision d, IFleetSpawner spawner)
        {
            FactionIntent it = d.Intent;
            if (it.Kind == ActionKind.Idle) return;
            if (it.Cost > world.GetTreasury(st.Tag)) return;
            bool enacted = true;
            switch (it.Kind)
            {
                case ActionKind.Expand:
                    // A refusal (no legal position) creates and charges nothing but still spends the
                    // cooldown, as in the host, so the brain does not re-roll the draw every tick.
                    if (world.ExpandFrom(st.Tag, it.TargetNodeId) == null)
                    {
                        st.Cooldowns[ActionKind.Expand] = world.UtcNow + t.CooldownFor(ActionKind.Expand);
                        return;
                    }
                    break;
                case ActionKind.Claim:     world.Claim(st.Tag, it.TargetNodeId); break;
                case ActionKind.Reinforce: world.Reinforce(st.Tag, it.TargetNodeId); break;
                case ActionKind.Develop:   if (it.TargetCapability.HasValue) world.Develop(st.Tag, it.TargetNodeId, it.TargetCapability.Value); break;
                case ActionKind.Withdraw:  world.Withdraw(st, it.TargetNodeId); DrainEvacuations(world); break;
                case ActionKind.Negotiate: Diplomacy.OfferPeace(st, it.TargetFactionTag, world.UtcNow); break;
                case ActionKind.ProposePact:
                    Diplomacy.Propose(st, it.TargetFactionTag,
                        new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, world.UtcNow);
                    break;
                case ActionKind.Campaign:
                {
                    NodeRegion campaignRegion = NodeRegion.None;
                    bool namesRegion = it.TargetRegion != null && NodeRegion.TryFromKey(it.TargetRegion, out campaignRegion);
                    if (it.Cause.HasValue && it.Cause.Value == CampaignCause.Terror && namesRegion)
                    {
                        enacted = TerrorCampaigns.TryStart(st, t, world.UtcNow, it.TargetNodeId, campaignRegion, world, world.PrefabCatalogue);
                        break;
                    }
                    if (it.ObjectiveKind.HasValue && (namesRegion || it.ObjectiveNodeId != null))
                    {
                        CampaignObjective startedObjective;
                        enacted = OffensiveCampaigns.TryStart(st, t, world.UtcNow, it.TargetNodeId,
                            campaignRegion, it.ObjectiveNodeId, it.ObjectiveKind.Value,
                            it.Cause.HasValue ? it.Cause.Value : CampaignCause.Predation,
                            world, world.PrefabCatalogue, out startedObjective);
                        break;
                    }
                    HashSet<string> members = world.ComputeCampaignRegionMembers(st.Tag, it.TargetNodeId, t.CampaignRegionTraverseDepth);
                    enacted = CampaignFocusLifecycle.TryStart(st, t, world.GetFleetCombatPower(st.Tag),
                        world.UtcNow, it.TargetNodeId, members);
                    break;
                }
                case ActionKind.Trade:
                case ActionKind.Guard:
                case ActionKind.Patrol:
                {
                    bool immediate = spawner.SkipsTransit;   // false for SimTransitSpawner
                    PendingFleetAction fleet = PendingFleetResolver.CreatePending(it.Kind, st.Tag, it, world, t, immediate);
                    st.PendingFleets.Add(fleet);
                    break;
                }
                case ActionKind.Found:
                case ActionKind.FinishBuild: break; // no-op in sim
            }
            if (!enacted) return;
            world.Spend(st.Tag, it.Cost);
            if (it.Kind != ActionKind.Campaign)
                st.Cooldowns[it.Kind] = world.UtcNow + t.CooldownFor(it.Kind);
        }

        /// <summary>Empties <see cref="WorldModel.ShipEvacuations"/> after a Withdraw. These modes
        /// have no log to write them to, and the list is only ever cleared by a drain -- an undrained
        /// one grows for the lifetime of the process.
        /// <para>Provenance: FAC-278.</para></summary>
        private static void DrainEvacuations(WorldModel world)
        {
            world.ClearShipEvacuations();
        }

        // ---- shared --------------------------------------------------------------------------------
        private static void Enact(WorldModel world, FactionTuning t, FactionState st, Decision d)
        {
            FactionIntent intent = d.Intent;
            if (intent.Kind == ActionKind.Idle) return;
            if (intent.Cost > world.GetTreasury(st.Tag)) return;
            bool enacted = true;
            switch (intent.Kind)
            {
                case ActionKind.Expand:
                    if (world.ExpandFrom(st.Tag, intent.TargetNodeId) == null)
                    {
                        st.Cooldowns[ActionKind.Expand] = world.UtcNow + t.CooldownFor(ActionKind.Expand);
                        return;
                    }
                    break;
                case ActionKind.Claim: world.Claim(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Reinforce: world.Reinforce(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Develop: if (intent.TargetCapability.HasValue) world.Develop(st.Tag, intent.TargetNodeId, intent.TargetCapability.Value); break;
                case ActionKind.Raid:
                case ActionKind.Retaliate: world.Raid(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Conquer: world.Conquer(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Guard:  world.Guard(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Patrol: world.Patrol(st.Tag, intent.TargetNodeId); break;
                case ActionKind.Withdraw: world.Withdraw(st, intent.TargetNodeId); DrainEvacuations(world); break;
                case ActionKind.Negotiate: Diplomacy.OfferPeace(st, intent.TargetFactionTag, world.UtcNow); break;
                case ActionKind.ProposePact:
                    Diplomacy.Propose(st, intent.TargetFactionTag,
                        new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, world.UtcNow);
                    break;
                case ActionKind.Campaign:
                {
                    NodeRegion terrorRegion = NodeRegion.None;
                    bool namesRegion = intent.TargetRegion != null && NodeRegion.TryFromKey(intent.TargetRegion, out terrorRegion);
                    if (intent.Cause.HasValue && intent.Cause.Value == CampaignCause.Terror && namesRegion)
                    {
                        enacted = TerrorCampaigns.TryStart(st, t, world.UtcNow, intent.TargetNodeId, terrorRegion, world, world.PrefabCatalogue);
                        break;
                    }
                    if (intent.ObjectiveKind.HasValue && (namesRegion || intent.ObjectiveNodeId != null))
                    {
                        CampaignObjective startedObjective;
                        enacted = OffensiveCampaigns.TryStart(st, t, world.UtcNow, intent.TargetNodeId,
                            terrorRegion, intent.ObjectiveNodeId, intent.ObjectiveKind.Value,
                            intent.Cause.HasValue ? intent.Cause.Value : CampaignCause.Predation,
                            world, world.PrefabCatalogue, out startedObjective);
                        break;
                    }
                    HashSet<string> members = world.ComputeCampaignRegionMembers(st.Tag, intent.TargetNodeId, t.CampaignRegionTraverseDepth);
                    enacted = CampaignFocusLifecycle.TryStart(st, t, world.GetFleetCombatPower(st.Tag),
                        world.UtcNow, intent.TargetNodeId, members);
                    break;
                }
                case ActionKind.Trade: Diplomacy.LiftRelation(st, intent.TargetFactionTag, t); break;
                case ActionKind.Found:
                case ActionKind.FinishBuild: break; // no-op in sim
            }
            if (!enacted) return;
            world.Spend(st.Tag, intent.Cost);
            if (intent.Kind != ActionKind.Campaign)
                st.Cooldowns[intent.Kind] = world.UtcNow + t.CooldownFor(intent.Kind);
        }

        // Relations drift + war fatigue; raids sour the victims and tire both sides; then truces conclude.
        // Run once per tick after enact (mirrors the host's ApplyDiplomacy).
        private static void ApplyDiplomacy(WorldModel world, List<(FactionState st, Personality p)> factions, FactionTuning tuning)
        {
            foreach (var f in factions) { Diplomacy.Drift(f.st, f.p, world, tuning); Diplomacy.AccrueFatigue(f.st, world, tuning); }
            // The incident drain, from Core, so the host and the sim share one rule for who sours
            // and who tires.
            Diplomacy.ApplyIncidents(factions.Select(f => f.st), world, tuning);

            // Same drain the host performs, at the same point in the tick: the sim keeps identical
            // faction histories, so a ring bug shows up offline rather than only on a live server.
            FactionEventLog.Drain(factions.Select(f => f.st).ToList(), world, tuning.RecentEventCap);

            Diplomacy.ProcessProposals(factions.Select(f => f.st), world, tuning,
                onTruce: (a, b) => Console.WriteLine("  * TRUCE " + a + " <-> " + b + " (war fatigue -> peace)"),
                onPact:  (a, b) => Console.WriteLine("  * PACT  " + a + " <-> " + b + " (formal alliance)"));
        }

        private static void ApplyEvents(WorldModel world, int tick)
        {
            if (tick == 5)
            {
                AddPresenceAtHome(world, "MERC", new Presence { ActorId = "PLAYER_Zed", Kind = ActorKind.Player });
                AddPresenceAtHome(world, "RVN", new Presence { ActorId = "PLAYER_Zed", Kind = ActorKind.Player });
                Console.WriteLine("  * t05 EVENT: neutral player 'Zed' enters MERC and RVN space (same act, different reading)");
            }
            if (tick == 12)
            {
                SpatialNode home = world.Graph.OwnedBy("STL").FirstOrDefault();
                if (home != null)
                {
                    home.Threat = 0.9;
                    home.Presences.Add(new Presence { ActorId = "RAIDERS", Kind = ActorKind.Faction, Hostile = true });
                }
                Console.WriteLine("  * t12 EVENT: hostile raid hits STL home (threat 0.9)");
            }
        }

        private static void AddPresenceAtHome(WorldModel world, string tag, Presence pr)
        {
            SpatialNode home = world.Graph.OwnedBy(tag).FirstOrDefault();
            if (home != null) home.Presences.Add(pr);
        }

        internal static Dictionary<string, Personality> BuildPersonalities()
        {
            return Personalities.All();   // single-sourced in Factions.Core (shared with the host)
        }

        private static (FactionState, Personality) Seed(WorldModel w, Dictionary<string, Personality> ps,
            string tag, string persId, long treasury, Vec3 home, int rngSeed)
        {
            SpatialNode hp = w.Graph.Add(home, 0.5);
            hp.OwnerFactionTag = tag; hp.HoldStrength = 0.6;
            SpatialNode a = w.Graph.Add(new Vec3(home.X + 1200, home.Y, home.Z + 300), 0.7);
            SpatialNode b = w.Graph.Add(new Vec3(home.X - 300, home.Y, home.Z + 1200), 0.35);
            w.Graph.Connect(hp.Id, a.Id); w.Graph.Connect(hp.Id, b.Id);
            w.SetTreasury(tag, treasury);
            return (new FactionState { Tag = tag, PersonalityId = persId, RngSeed = rngSeed }, ps[persId]);
        }

        private static (FactionState, Personality) SeedSource(WorldModel w, Dictionary<string, Personality> ps,
            string tag, string persId, long treasury, Vec3 home, int rngSeed)
        {
            SpatialNode hp = w.Graph.Add(home, 0.5);
            hp.OwnerFactionTag = tag; hp.HoldStrength = 0.9; hp.Radius = 30;            // bright core
            SpatialNode a = w.Graph.Add(new Vec3(home.X + 40, home.Y, home.Z + 15), 0.7); a.Radius = 24;
            SpatialNode b = w.Graph.Add(new Vec3(home.X - 15, home.Y, home.Z + 40), 0.35); b.Radius = 24;
            w.Graph.Connect(hp.Id, a.Id); w.Graph.Connect(hp.Id, b.Id);
            w.SetTreasury(tag, treasury);
            return (new FactionState { Tag = tag, PersonalityId = persId, RngSeed = rngSeed }, ps[persId]);
        }

        // ---- production scenario ------------------------------------------------------------

        /// <summary>Ore-to-ingot recipes as the live server resolved them (item_recipe_cache.json,
        /// 2026-08-19). Used when no production_model.json is supplied.</summary>
        private static readonly string[,] s_oreRecipes =
        {
            // blueprint,            ore,         baseProductionTimeSecs, ingotPerOre
            { "IronOreToIngot",      "Iron",      "0.05", "0.699999988079071"   },
            { "NickelOreToIngot",    "Nickel",    "0.66", "0.40000000596046448" },
            { "CobaltOreToIngot",    "Cobalt",    "3.0",  "0.30000001192092896" },
            { "MagnesiumOreToIngot", "Magnesium", "0.5",  "0.0070000002160668373" },
            { "SiliconOreToIngot",   "Silicon",   "0.6",  "0.699999988079071"   },
            { "SilverOreToIngot",    "Silver",    "1.0",  "0.10000000149011612" },
            { "GoldOreToIngot",      "Gold",      "0.4",  "0.0099999997764825821" },
            { "PlatinumOreToIngot",  "Platinum",  "3.0",  "0.004999999888241291" },
            { "UraniumOreToIngot",   "Uranium",   "4.0",  "0.0099999997764825821" },
        };

        private sealed class ProductionModelDoc
        {
            public double ProductivityBonus { get; set; }
            public Dictionary<string, MachineProfile> Profiles { get; set; }
            public Dictionary<string, List<ConversionRecipe>> Recipes { get; set; }
        }

        /// <summary>
        /// Drives a real <see cref="ProductionManager"/> offline against a fixed ore supply, so the
        /// corrected SE machine math can be measured over a long horizon before it reaches a live
        /// world. Reports, per ore, what the bank could consume versus what it was actually fed —
        /// the distinction that decides whether faster machines change anything at all.
        ///
        /// <para>With no model path it uses the live server's own ore-to-ingot constants and the
        /// live world multipliers. Point it at an <c>Instance/Factions/production_model.json</c>
        /// dump to run the exact resolved model instead.</para>
        /// </summary>
        private static int RunProduction(string modelPath, int hours, long orePerMinute)
        {
            if (hours <= 0) hours = 24;
            if (orePerMinute <= 0) orePerMinute = 5000;

            ProductionModelDoc model = LoadProductionModel(modelPath);
            Console.WriteLine("Production scenario — " + hours + " simulated hours, "
                + orePerMinute + " ore/min delivered per type"
                + (modelPath != null ? ", model: " + modelPath : ", model: live SE constants"));
            Console.WriteLine();

            long baseline  = RunProductionScenario("BASELINE  (no world multipliers, no upgrade modules)",
                model, hours, orePerMinute, neutral: true);
            Console.WriteLine();
            long corrected = RunProductionScenario("CORRECTED (live world multipliers + 4 speed modules)",
                model, hours, orePerMinute, neutral: false);

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "corrected / baseline ore throughput: {0:F2}x  — the machine-speed headline is an upper bound;"
                + " ore supply and the bank's machine count decide how much of it is reachable.",
                baseline > 0 ? (double)corrected / baseline : 0.0));
            return 0;
        }

        private static ProductionModelDoc LoadProductionModel(string modelPath)
        {
            if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
            {
                try
                {
                    var loaded = JsonConvert.DeserializeObject<ProductionModelDoc>(File.ReadAllText(modelPath));
                    if (loaded != null && loaded.Recipes != null && loaded.Recipes.Count > 0) return loaded;
                    Console.Error.WriteLine("production_model.json had no recipes; using built-in SE constants.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not read " + modelPath + ": " + ex.Message);
                }
            }

            var recipes = new List<ConversionRecipe>();
            for (int i = 0; i < s_oreRecipes.GetLength(0); i++)
            {
                recipes.Add(new ConversionRecipe
                {
                    BlueprintId = s_oreRecipes[i, 0],
                    TimeSecs    = float.Parse(s_oreRecipes[i, 2], CultureInfo.InvariantCulture),
                    Inputs      = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = s_oreRecipes[i, 1], Amount = 1.0 } },
                    Outputs     = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = s_oreRecipes[i, 1],
                                                          Amount = double.Parse(s_oreRecipes[i, 3], CultureInfo.InvariantCulture) } },
                });
            }

            MachineProfile refineryII = MachinePhysics.DefaultProfile(NodeCapabilityKind.RefineryII);
            refineryII.WorldSpeedMultiplier = 3.0;   // live Sandbox.sbc RefinerySpeedMultiplier
            refineryII.SourceBlockSubtypeId = "LargeRefinery";

            return new ProductionModelDoc
            {
                ProductivityBonus = 4.0,
                Profiles = new Dictionary<string, MachineProfile> { { "RefineryII", refineryII } },
                Recipes  = new Dictionary<string, List<ConversionRecipe>> { { "RefineryII", recipes } },
            };
        }

        private static long RunProductionScenario(string label, ProductionModelDoc model,
            int hours, long orePerMinute, bool neutral)
        {
            const double PassSeconds = 30.0;

            FactionTuning tuning = LoadSimTuning();
            tuning.ProductionIntervalSeconds = PassSeconds;
            tuning.MaxRefineryUpgrades = 1;              // hold the bank at one installed level
            tuning.ProductionProductivityBonus = neutral ? 0.0 : model.ProductivityBonus;
            tuning.MachineProfiles = new Dictionary<NodeCapabilityKind, MachineProfile>();
            if (!neutral && model.Profiles != null)
                foreach (KeyValuePair<string, MachineProfile> kv in model.Profiles)
                {
                    NodeCapabilityKind cap;
                    if (Enum.TryParse(kv.Key, out cap)) tuning.MachineProfiles[cap] = kv.Value;
                }

            var world = new WorldModel(new SeededRandom(20260819)) { Tuning = tuning };
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "REFINERY");
            node.OwnerFactionTag = "SIM";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>();
            foreach (KeyValuePair<string, List<ConversionRecipe>> kv in model.Recipes)
            {
                NodeCapabilityKind cap;
                if (Enum.TryParse(kv.Key, out cap)) capRecipes[cap] = kv.Value;
            }
            List<ConversionRecipe> refinery = capRecipes[NodeCapabilityKind.RefineryII];

            var manager = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "SIM" } };

            long delivered = (long)(orePerMinute * PassSeconds / 60.0);
            var totalDelivered = new Dictionary<string, long>();
            int passes = (int)(hours * 3600.0 / PassSeconds);

            manager.Tick(states, world, tuning, new List<string>());   // prime the slots

            for (int pass = 0; pass < passes; pass++)
            {
                foreach (ConversionRecipe r in refinery)
                {
                    string oreKey = "Ore:" + r.Inputs[0].SubTypeId;
                    long have;
                    node.Inventory.TryGetValue(oreKey, out have);
                    node.Inventory[oreKey] = have + delivered;
                    long sum;
                    totalDelivered.TryGetValue(oreKey, out sum);
                    totalDelivered[oreKey] = sum + delivered;

                    // Standing demand for the ingot keeps the pull gate open, the way a real
                    // network's downstream consumers do.
                    world.Demand.Post(new DemandEntry
                    {
                        FactionTag = "SIM", Kind = "Item:Ingot:" + r.Outputs[0].SubTypeId, NodeId = node.Id,
                        Quantity = 1_000_000_000, ExpiresUtc = world.UtcNow.AddDays(365),
                    });
                }

                world.UtcNow = world.UtcNow.AddSeconds(PassSeconds);
                manager.Tick(states, world, tuning, new List<string>());
            }

            Console.WriteLine(label);
            Console.WriteLine(new string('-', 104));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-10} {1,12} {2,12} {3,10} {4,12} {5,12} {6,10} {7,8}",
                "ore", "delivered", "refined", "used%", "ingots", "expected", "loss%", "slots"));

            long totalRefined = 0;
            int idleRecipes = 0;
            var throughput = manager.SnapshotThroughput("SIM");
            foreach (ConversionRecipe r in refinery)
            {
                string oreKey   = "Ore:" + r.Inputs[0].SubTypeId;
                string ingotKey = "Ingot:" + r.Outputs[0].SubTypeId;
                long deliveredOre, oreLeft, ingots;
                totalDelivered.TryGetValue(oreKey, out deliveredOre);
                node.Inventory.TryGetValue(oreKey, out oreLeft);
                node.Inventory.TryGetValue(ingotKey, out ingots);
                long consumed = deliveredOre - oreLeft;
                long expected = (long)Math.Floor(consumed * r.Outputs[0].Amount);

                int slots = 0;
                foreach (ThroughputEntry t in throughput)
                    if (t.PrimaryOutputId == ingotKey) slots += t.SlotCount;

                double usedPct = deliveredOre > 0 ? 100.0 * consumed / deliveredOre : 0.0;
                double lossPct = expected > 0 ? 100.0 * (expected - ingots) / expected : 0.0;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-10} {1,12} {2,12} {3,9:F1}% {4,12} {5,12} {6,9:F3}% {7,8}",
                    r.Inputs[0].SubTypeId, deliveredOre, consumed, usedPct, ingots, expected, lossPct, slots));
                totalRefined += consumed;
                if (consumed == 0) idleRecipes++;
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "total ore refined: {0:N0}" + (idleRecipes > 0
                    ? "   ({1} recipe(s) never ran: more recipes than the bank has machines)" : ""),
                totalRefined, idleRecipes));
            return totalRefined;
        }

        private static void PrintState(WorldModel w, List<(FactionState st, Personality p)> fs)
        {
            Console.WriteLine("-- world: " + w.Graph.Nodes.Count + " nodes --");
            Console.WriteLine("   bank " + FormatCentralBankSnapshot(
                w.SnapshotCentralBank(fs.Select(f => f.st).ToList())));
            foreach (var f in fs)
            {
                List<string> owned = w.Graph.OwnedBy(f.st.Tag).Select(n => n.Id).ToList();
                int infrastructureCount = w.Graph.OwnedBy(f.st.Tag).Count(Economy.HasDevelopedInfrastructure);
                int stationCount = w.Graph.OwnedBy(f.st.Tag).Count(Economy.HasPhysicalStation);
                Console.WriteLine("   " + f.st.Tag.PadRight(4) + " treasury=" + w.GetTreasury(f.st.Tag).ToString().PadLeft(9) +
                                  "  debt=" + w.GetDebt(f.st.Tag).ToString().PadLeft(7) +
                                  "  infra=" + infrastructureCount +
                                  "  legacy=" + Math.Max(0, stationCount - infrastructureCount) +
                                  "  nodes[" + owned.Count + "]: " + string.Join(",", owned));
            }
        }

        private static string FormatCentralBankSnapshot(CentralBankSnapshot snap)
        {
            return "money=" + snap.CurrentMoneySupply
                   + " target=" + snap.TargetMoneySupply
                   + " gap=" + snap.MoneySupplyGap
                   + " coll=" + snap.WorldCollateralValue
                   + " anchor=" + snap.AnchorCollateralValue
                   + " policy=" + (string.IsNullOrEmpty(snap.LastPolicyMode) ? "none" : snap.LastPolicyMode)
                   + " levied=" + snap.LastPolicyLevied
                   + " rescued=" + snap.LastPolicyRescued
                   + " rr=" + snap.ReserveRatio.ToString("0.###")
                   + " terr=" + snap.TerritoryNodeCount
                   + " founded=" + snap.FoundedNodeCount
                   + " cap=" + snap.CapabilityLevelCount
                   + " ships=" + snap.ShipCount
                   + " (idle=" + snap.IdleShipCount + " flight=" + snap.InFlightShipCount + ")";
        }
    }
}
