using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Factions.Core;

namespace Factions.Sim
{
    /// <summary>The contested-range balance pass: <c>dotnet run --project src/Factions.Sim -- tactics
    /// [seed] [encounters]</c>.
    ///
    /// <para>Draws one seeded population of raid-on-node encounters and runs it twice — gated, where
    /// a raid flies and a guard stands only if its commit gate admits it, and ungated, where every
    /// encounter is fought — through the real pieces: the raid commit predicate
    /// (<see cref="CombatStrength.Survives(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,double,double,DuelMatchup)"/>),
    /// the guard commit predicate (<see cref="CombatStrength.Holds"/> or
    /// <see cref="CombatStrength.Destroys(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,double,double,DuelMatchup)"/>),
    /// and <see cref="PendingFleetResolver.ResolveArrivals"/> for the duel and its outcome.</para>
    ///
    /// <para>What it does not run: <c>FleetManager</c> itself. Squadrons are drawn from a fixed hull
    /// roster rather than assembled from stock, so the counts are per encounter, not per campaign.
    /// Hull attrition counts the duel's own losses plus, for a raid that breaks through at 0.5 or
    /// less, the hulls <c>FinalizeReturn</c> writes off when it lands.</para></summary>
    internal static class TacticsScenario
    {
        private struct Hull
        {
            public string Id;
            public double MassKg;
            public float ThrustN;
            public WeaponBand[] Weapons;
        }

        private static readonly Hull[] Roster =
        {
            // id, dry mass, strongest-face thrust, loadout
            new Hull { Id = "brawler",     MassKg = 400000, ThrustN = 1600000, Weapons = new[] { W(420, 800) } },
            new Hull { Id = "gunship",     MassKg = 300000, ThrustN = 1800000, Weapons = new[] { W(240, 1500), W(120, 800) } },
            new Hull { Id = "sniper",      MassKg = 250000, ThrustN = 1250000, Weapons = new[] { W(200, 2000) } },
            new Hull { Id = "interceptor", MassKg = 100000, ThrustN = 1400000, Weapons = new[] { W(150, 800) } },
            new Hull { Id = "kiter",       MassKg = 150000, ThrustN = 1800000, Weapons = new[] { W(150, 2000) } },
            new Hull { Id = "frigate",     MassKg = 600000, ThrustN = 1800000, Weapons = new[] { W(250, 2000), W(300, 100) } },
        };

        private static readonly string[] Personalities = { "mercantile", "raider", "settler", "factorum", "default" };

        private static WeaponBand W(float dps, float rangeM) { return new WeaponBand { Dps = dps, RangeM = rangeM }; }

        private sealed class Encounter
        {
            public string AttackerPersonality, DefenderPersonality;
            public List<string> Raid = new List<string>();
            public List<string> Guard = new List<string>();
            public bool HasStation;
            public List<WeaponBand> StationWeapons = new List<WeaponBand>();
            public int DuelSeed;
        }

        private sealed class Tally
        {
            public int Encounters, RaidRefused, RaidsFlown, RaidsLanded, GuardOffered, GuardOutgunned, GuardsSent;
            public double LandedRatioSum;
            public int AttackerHulls, AttackerHullsLost, DefenderHulls, DefenderHullsLost;
            public int RaidBrokeOff, GuardBrokeOff, Intercepted, RaidRepelled;
        }

        internal static int Run(int seed, int encounters)
        {
            FactionTuning tuning = Program.LoadSimTuning();
            List<Encounter> population = Draw(seed, encounters);

            Tally gated = Pass(population, tuning, true);
            Tally all = Pass(population, tuning, false);

            Console.WriteLine("=== tactics — seed " + seed + ", " + encounters + " encounters, contested range ===");
            Console.WriteLine("-- gated: a raid flies only if the raid commit gate admits it; a guard stands only if the guard gate does --");
            Console.WriteLine("-- ungated: every encounter is fought, the guard squadron present when there is one --");
            Print(gated, all);
            return 0;
        }

        private static void Print(Tally gated, Tally all)
        {
            Console.WriteLine("| metric | gated | ungated |");
            Console.WriteLine("|---|---|---|");
            Row("RAID_REFUSED (raid commit gate refuses)", gated.RaidRefused, all.RaidRefused, gated.Encounters);
            Row("raids flown", gated.RaidsFlown, all.RaidsFlown, gated.Encounters);
            Row("raid success (lands, of raids flown)", gated.RaidsLanded, all.RaidsLanded, gated.RaidsFlown, all.RaidsFlown);
            Console.WriteLine("| mean landed ratio | " + Mean(gated.LandedRatioSum, gated.RaidsLanded) + " | " + Mean(all.LandedRatioSum, all.RaidsLanded) + " |");
            Row("raid intercepted (destroyed in the duel)", gated.Intercepted, all.Intercepted, gated.RaidsFlown, all.RaidsFlown);
            Row("RAID_BROKE_OFF", gated.RaidBrokeOff, all.RaidBrokeOff, gated.RaidsFlown, all.RaidsFlown);
            Row("RAID_REPELLED (defence stood, ahead on lost share)", gated.RaidRepelled, all.RaidRepelled, gated.RaidsFlown, all.RaidsFlown);
            Row("GUARD_OUTGUNNED (guard commit gate refuses)", gated.GuardOutgunned, all.GuardOutgunned, gated.GuardOffered);
            Row("guard commit rate", gated.GuardsSent, all.GuardsSent, gated.GuardOffered);
            Row("GUARD_BROKE_OFF", gated.GuardBrokeOff, all.GuardBrokeOff, gated.GuardsSent, all.GuardsSent);
            Row("attacker hull attrition", gated.AttackerHullsLost, all.AttackerHullsLost, gated.AttackerHulls, all.AttackerHulls);
            Row("defender hull attrition", gated.DefenderHullsLost, all.DefenderHullsLost, gated.DefenderHulls, all.DefenderHulls);
        }

        private static void Row(string name, int a, int b, int of) { Row(name, a, b, of, of); }

        private static void Row(string name, int a, int b, int ofA, int ofB)
        {
            Console.WriteLine("| " + name + " | " + Pct(a, ofA) + " | " + Pct(b, ofB) + " |");
        }

        private static string Pct(int n, int of)
        {
            return n + "/" + of + " (" + (of > 0 ? (100.0 * n / of).ToString("0.0", CultureInfo.InvariantCulture) : "-") + "%)";
        }

        private static string Mean(double sum, int n)
        {
            return n > 0 ? (sum / n).ToString("0.00", CultureInfo.InvariantCulture) : "-";
        }

        private static List<Encounter> Draw(int seed, int count)
        {
            var rng = new SeededRandom(seed);
            var list = new List<Encounter>();
            for (int i = 0; i < count; i++)
            {
                var e = new Encounter
                {
                    AttackerPersonality = Personalities[Pick(rng, Personalities.Length)],
                    DefenderPersonality = Personalities[Pick(rng, Personalities.Length)],
                    HasStation = rng.NextDouble() < 0.7,
                    DuelSeed = seed * 7919 + i,
                };
                int raidSize = 1 + Pick(rng, 4);
                for (int k = 0; k < raidSize; k++) e.Raid.Add(Roster[Pick(rng, Roster.Length)].Id);
                int guardSize = Pick(rng, 5);
                for (int k = 0; k < guardSize; k++) e.Guard.Add(Roster[Pick(rng, Roster.Length)].Id);
                if (e.HasStation)
                {
                    int turrets = Pick(rng, 3);
                    for (int k = 0; k < turrets; k++)
                        e.StationWeapons.Add(rng.NextDouble() < 0.5 ? W(120, 2000) : W(200, 800));
                }
                list.Add(e);
            }
            return list;
        }

        private static int Pick(SeededRandom rng, int n) { return Math.Min(n - 1, (int)(rng.NextDouble() * n)); }

        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, Hull> _hulls = new Dictionary<string, Hull>();
            public Catalogue() { foreach (Hull h in Roster) _hulls[h.Id] = h; }
            public long CargoLiters(string prefabId) { return 0; }
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                Hull h;
                return prefabId != null && _hulls.TryGetValue(prefabId, out h) ? h.Weapons : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var d = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                Hull h;
                if (prefabId != null && _hulls.TryGetValue(prefabId, out h))
                    d[ThrustDirection.Backward] = new List<ThrustBand>
                    {
                        new ThrustBand { ThrusterType = "Hydrogen", ForceMagnitudeN = h.ThrustN,
                                         EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f },
                    };
                return d;
            }
            public double DryMassKg(string prefabId)
            {
                Hull h;
                return prefabId != null && _hulls.TryGetValue(prefabId, out h) ? h.MassKg : 0;
            }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static List<WeaponBand> BandsOf(Catalogue cat, List<string> hulls)
        {
            var bands = new List<WeaponBand>();
            foreach (string id in hulls) bands.AddRange(cat.WeaponBands(id));
            return bands;
        }

        private static readonly Regex HullsLost = new Regex(@"hulls_lost=(\d+)/(\d+)");
        private static readonly Regex Ratio = new Regex(@" ratio=([0-9.]+)");

        private static Tally Pass(List<Encounter> population, FactionTuning tuning, bool gated)
        {
            var cat = new Catalogue();
            var tally = new Tally();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (Encounter e in population)
            {
                tally.Encounters++;
                var world = new WorldModel(new SeededRandom(e.DuelSeed)) { Tuning = tuning, UtcNow = now, PrefabCatalogue = cat };
                world.TacticLearning = true;

                SpatialNode target = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "DEF1");
                target.OwnerFactionTag = "DEF";
                target.HoldStrength = 0.8;
                target.Radius = 40;
                if (e.HasStation) { target.StationPrefabId = "station"; target.StationWeapons = e.StationWeapons; }
                SpatialNode home = world.Graph.Add(new Vec3(500, 0, 0), 0.6, "ATK1");
                home.OwnerFactionTag = "ATK";
                home.HoldStrength = 0.8;

                var def = new FactionState { Tag = "DEF", PersonalityId = e.DefenderPersonality };
                var atk = new FactionState { Tag = "ATK", PersonalityId = e.AttackerPersonality };
                var states = new List<FactionState> { def, atk };

                List<WeaponBand> raidBands = BandsOf(cat, e.Raid);
                List<WeaponBand> guardBands = BandsOf(cat, e.Guard);
                double attackDps = ShipPool.TotalDps(raidBands);
                double density; bool air;
                CombatMobility.EnvironmentAt(world.Planets, target, out density, out air);
                double raidMobility = CombatMobility.OfHulls(cat, e.Raid, density, air);

                // Guard commit: the defender decides first, as the marshal does before the raid lands.
                bool guardSent = false;
                if (e.Guard.Count > 0)
                {
                    tally.GuardOffered++;
                    var gm = new DuelMatchup
                    {
                        AttackerBands = raidBands,
                        Attacker = TacticChoice.For(atk, raidMobility),
                        Defender = TacticChoice.For(def, CombatMobility.OfDefence(cat, e.Guard, true, density, air)),
                    };
                    bool worth = CombatStrength.Holds(CombatStrength.OfDefence(guardBands, target, tuning), attackDps, tuning.GuardCommitRatio);
                    bool kills = CombatStrength.Destroys(guardBands, target, tuning, attackDps, tuning.GuardOverwhelmMargin, gm);
                    guardSent = worth || kills || !gated;
                    if (guardSent) tally.GuardsSent++; else tally.GuardOutgunned++;
                }

                // Raid commit: the attacker sees what stands at the node (the parked census the gate
                // reads is the guard squadron here, sent or not).
                var rm = new DuelMatchup
                {
                    AttackerBands = raidBands,
                    Attacker = TacticChoice.For(atk, raidMobility),
                    Defender = TacticChoice.For(def, CombatMobility.OfDefence(cat, e.Guard, e.Guard.Count > 0, density, air)),
                };
                if (gated && !CombatStrength.Survives(guardBands, target, tuning, attackDps, tuning.RaidSurvivalMargin, rm))
                {
                    tally.RaidRefused++;
                    continue;
                }
                tally.RaidsFlown++;
                tally.AttackerHulls += e.Raid.Count;
                if (guardSent) tally.DefenderHulls += e.Guard.Count;

                if (guardSent)
                    def.PendingFleets.Add(new PendingFleetAction
                    {
                        Kind = ActionKind.Guard, ActorTag = "DEF", TargetNodeId = "DEF1", FleetId = "G1",
                        ShipPrefabId = e.Guard[0], ConvoySize = e.Guard.Count,
                        Squadron = e.Guard.Count > 1 ? new List<string>(e.Guard) : null,
                        DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                        Resolved = true, WeaponBands = guardBands,
                    });
                atk.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "ATK", TargetTag = "DEF", TargetNodeId = "DEF1", FleetId = "R1",
                    OriginNodeId = "ATK1", ShipPrefabId = e.Raid[0], ConvoySize = e.Raid.Count,
                    Squadron = e.Raid.Count > 1 ? new List<string>(e.Raid) : null,
                    OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = raidBands,
                });

                var log = new List<string>();
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(e.DuelSeed), log, null);
                Score(log, e, guardSent, def, tally);
            }
            return tally;
        }

        // ---- the learner over time ------------------------------------------------------------------

        private sealed class Window
        {
            public int Fights, Landed, AttackerHulls, AttackerHullsLost, TopArmCount;
            public double ShareSum;
            public string TopArm;
        }

        /// <summary>
        /// <c>tactics learn [seed] [fights]</c>: one attacker raids one defender's Guard
        /// <paramref name="fights"/> times, one fight an hour; halfway the defender refits and two
        /// half-lives of <see cref="TacticMemory.HalfLifeDays"/> pass. Run twice on the same seeds —
        /// fighting the personality default, and picking from the attacker's and defender's
        /// <see cref="TacticMemory"/> — and tabulated in windows of ten fights: the attacker's damage
        /// share (dealt ÷ dealt + taken, read off the duel's capacity pools), raids landed, attacker
        /// hulls lost, and the arm the attacker picked most.
        /// </summary>
        internal static int RunLearning(int seed, int fights)
        {
            FactionTuning tuning = Program.LoadSimTuning();
            var cat = new Catalogue();
            string[] raid = { "sniper", "brawler" };
            string[] before = { "brawler", "brawler" };
            string[] after = { "sniper", "sniper", "sniper" };
            List<Window> byDefault = Learn(tuning, cat, seed, fights, raid, before, after, false);
            List<Window> learned = Learn(tuning, cat, seed, fights, raid, before, after, true);

            Console.WriteLine("=== tactics learn — seed " + seed + ", " + fights + " fights, factorum raid ("
                              + string.Join(",", raid) + ") on a settler Guard ("
                              + string.Join(",", before) + " -> " + string.Join(",", after) + " at fight "
                              + (fights / 2 + 1) + ") ===");
            Console.WriteLine("| fights | default: share / landed / hulls lost / arm | learned: share / landed / hulls lost / arm |");
            Console.WriteLine("|---|---|---|");
            for (int i = 0; i < byDefault.Count; i++)
            {
                Window d = byDefault[i], l = learned[i];
                int from = i * 10 + 1;
                Console.WriteLine("| " + from + "-" + (from + d.Fights - 1) + " | " + Cell(d) + " | " + Cell(l) + " |");
            }
            return 0;
        }

        private static string Cell(Window w)
        {
            return (w.ShareSum / Math.Max(1, w.Fights)).ToString("0.00", CultureInfo.InvariantCulture)
                 + " / " + w.Landed + "/" + w.Fights
                 + " / " + w.AttackerHullsLost + "/" + w.AttackerHulls
                 + " / " + w.TopArm + " " + w.TopArmCount + "/" + w.Fights;
        }

        private static List<Window> Learn(FactionTuning tuning, Catalogue cat, int seed, int fights,
            string[] raid, string[] before, string[] after, bool learning)
        {
            var def = new FactionState { Tag = "DEF", PersonalityId = "settler" };
            var atk = new FactionState { Tag = "ATK", PersonalityId = "factorum" };
            var windows = new List<Window>();
            var arms = new Dictionary<TacticArm, int>();
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < fights; i++)
            {
                bool refit = i >= fights / 2;
                DateTime now = t0.AddHours(i).AddDays(refit ? 2 * TacticMemory.HalfLifeDays : 0);
                var world = new WorldModel(new SeededRandom(seed * 7919 + i)) { Tuning = tuning, UtcNow = now, PrefabCatalogue = cat };
                world.TacticLearning = learning;
                SpatialNode target = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "DEF1");
                target.OwnerFactionTag = "DEF";
                target.HoldStrength = 0.8;
                target.Radius = 40;
                SpatialNode home = world.Graph.Add(new Vec3(500, 0, 0), 0.6, "ATK1");
                home.OwnerFactionTag = "ATK";
                home.HoldStrength = 0.8;

                var guard = new List<string>(refit ? after : before);
                var squad = new List<string>(raid);
                def.PendingFleets.Clear();
                atk.PendingFleets.Clear();
                atk.Fights.Clear();
                def.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "DEF", TargetNodeId = "DEF1", FleetId = "G1",
                    ShipPrefabId = guard[0], ConvoySize = guard.Count, Squadron = guard,
                    DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                    Resolved = true, WeaponBands = BandsOf(cat, guard),
                });
                atk.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "ATK", TargetTag = "DEF", TargetNodeId = "DEF1", FleetId = "R1",
                    OriginNodeId = "ATK1", ShipPrefabId = squad[0], ConvoySize = squad.Count, Squadron = squad,
                    OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = BandsOf(cat, squad),
                });
                var log = new List<string>();
                PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, world, tuning,
                                                     new SeededRandom(seed * 7919 + i), log, null);

                if (i % 10 == 0) { windows.Add(new Window()); arms.Clear(); }
                Window w = windows[windows.Count - 1];
                w.Fights++;
                w.AttackerHulls += squad.Count;
                double atkCap = 0, defCap = 0;
                foreach (WeaponBand b in BandsOf(cat, squad)) atkCap += b.Dps;
                foreach (WeaponBand b in BandsOf(cat, guard)) defCap += b.Dps;
                foreach (string line in log)
                {
                    if (line.Contains("RAID_RESOLVED")) w.Landed++;
                    Match m = HullsLost.Match(line);
                    if (m.Success && (line.StartsWith("RAID_BROKE_OFF", StringComparison.Ordinal)
                                      || line.StartsWith("GUARD_INTERCEPT", StringComparison.Ordinal)))
                        w.AttackerHullsLost += int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    Match r = Ratio.Match(line);
                    if (r.Success && line.StartsWith("RAID_BREAKS_GUARD", StringComparison.Ordinal)
                        && double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) <= 0.5)
                        w.AttackerHullsLost += squad.Count;
                }
                if (atk.Fights.Count > 0 && atk.Fights[0].Tactic != null)
                {
                    FightRecord f = atk.Fights[0];
                    TacticArm arm = f.Tactic.AttackerArm;
                    int c; arms.TryGetValue(arm, out c); arms[arm] = c + 1;
                    if (c + 1 > w.TopArmCount) { w.TopArmCount = c + 1; w.TopArm = arm.ToString(); }
                }
                w.ShareSum += DuelShare(log, atkCap, defCap);
            }
            return windows;
        }

        private static readonly Regex TurnDamage = new Regex(@"T\d+\([^)]*\) atk=(\d+) def=(\d+)");

        /// <summary>The attacker's damage share, summed from the duel's turn summary.</summary>
        private static double DuelShare(List<string> log, double atkCap, double defCap)
        {
            foreach (string line in log)
            {
                MatchCollection turns = TurnDamage.Matches(line);
                if (turns.Count == 0) continue;
                double dealt = 0, taken = 0;
                foreach (Match t in turns)
                {
                    dealt += double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                    taken += double.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                dealt = Math.Min(dealt, defCap);
                taken = Math.Min(taken, atkCap);
                return dealt + taken > 0 ? dealt / (dealt + taken) : 0.5;
            }
            return 0.5;
        }

        private static void Score(List<string> log, Encounter e, bool guardSent, FactionState def, Tally tally)
        {
            bool guardBrokeOff = false;
            foreach (string line in log)
            {
                if (line.Contains("GUARD_BROKE_OFF")) guardBrokeOff = true;
                if (line.Contains("RAID_RESOLVED"))
                {
                    tally.RaidsLanded++;
                    Match r = Ratio.Match(line);
                    if (r.Success) tally.LandedRatioSum += double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                bool broke = line.StartsWith("RAID_BROKE_OFF", StringComparison.Ordinal);
                bool stopped = line.StartsWith("GUARD_INTERCEPT", StringComparison.Ordinal)
                            || line.StartsWith("STATION_INTERCEPT", StringComparison.Ordinal);
                if (broke || stopped)
                {
                    if (broke) tally.RaidBrokeOff++; else tally.Intercepted++;
                    Match m = HullsLost.Match(line);
                    if (m.Success) tally.AttackerHullsLost += int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                if (line.StartsWith("RAID_REPELLED", StringComparison.Ordinal)) tally.RaidRepelled++;
                if (line.StartsWith("RAID_BREAKS_GUARD", StringComparison.Ordinal)
                    || line.StartsWith("STATION_DEFENSE", StringComparison.Ordinal)
                    || line.StartsWith("RAID_REPELLED", StringComparison.Ordinal))
                {
                    Match r = Ratio.Match(line);
                    // FinalizeReturn writes off every hull of a raid that comes home at 0.5 or less.
                    if (r.Success && double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) <= 0.5)
                        tally.AttackerHullsLost += e.Raid.Count;
                }
            }
            if (guardBrokeOff) tally.GuardBrokeOff++;
            if (!guardSent || guardBrokeOff) return;
            // A Guard force the duel destroyed is marked intercepted and removed with every hull.
            bool standing = false;
            foreach (PendingFleetAction f in def.PendingFleets)
                if (f.FleetId == "G1" && !f.Intercepted) standing = true;
            if (!standing) tally.DefenderHullsLost += e.Guard.Count;
        }
    }
}
