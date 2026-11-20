using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A node's defence is marshalled: several convoys converge on one threatened node until the
    /// duel's own arithmetic says the attacker dies, drawn from whatever sources can reach it in
    /// time — and a player faction's estate is defended by the same rule an NPC's is.
    ///
    /// <para>The figures below are the duel's, not invented ones. Weapons at 800 m miss the first
    /// range bracket (2000 m) and fire on the other two, so a defence of D total DPS deals
    /// <c>2 x D x CombatBaseDamageFraction</c> over a duel, and an attacker of A DPS has A effective
    /// HP.</para>
    /// </summary>
    public class GuardMarshalTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float d;
                return Dps.TryGetValue(prefabId ?? "", out d) && d > 0
                    ? new[] { new WeaponBand { Dps = d, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GuardCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            t.MusterConvoyScaleMax = 1.0;     // isolate sizing from the posture multiplier
            t.GuardBurstSize = 1;
            t.StationDefensePower = 0;        // a bare claim unless a test says otherwise
            t.GuardCommitRatio = 0.9;
            t.GuardOverwhelmMargin = 1.25;
            t.GuardMaxConvoysPerThreat = 3;
            return t;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            n.Radius = 40;
            return n;
        }

        /// <summary>N002 is the threatened node. Every other entry in <paramref name="sources"/> is
        /// an owned node holding that many 200-DPS Gunships, at the distance given.</summary>
        private static WorldModel MakeWorld(out FactionState defender, out FactionState raider,
                                            double raidDps, params (string Id, int Hulls, double Km)[] sources)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Gunship"] = 200f;
            w.PrefabCatalogue = cat;

            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            foreach ((string Id, int Hulls, double Km) src in sources)
            {
                SpatialNode n = AddNode(w, src.Id, "DEF", new Vec3(10 + src.Km, 0, 0));
                w.Graph.Connect(src.Id, "N002");
                if (src.Hulls > 0) n.ShipStock["Gunship"] = src.Hulls;
            }

            defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetNodeId = "N002",
                OriginPosKm = new Vec3(5000, 0, 0),
                TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(30),
                FleetId = "RAIDN002",
                WeaponBands = raidDps > 0
                    ? new List<WeaponBand> { new WeaponBand { Dps = (float)raidDps, RangeM = 800 } }
                    : null,
                LastResolvedSegment = -1,
            });
            return w;
        }

        private static List<string> Run(WorldModel w, FactionTuning t, params FactionState[] states)
        {
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            return log;
        }

        private static List<string> Dispatches(List<string> log)
        {
            return log.Where(m => m.Contains("GUARD_DISPATCH")).ToList();
        }

        // ---- the marshal ---------------------------------------------------------------------

        // One node's pool cannot carry the whole defence: a convoy is capped at
        // RaidSquadronMaxFighters + 1 hulls, so five 200-DPS Gunships (1000 DPS, 700 duel damage) fall
        // short of the 1250 that kills a 1000-DPS raider. The second source is what closes the gap.
        [Fact]
        public void WhenOneNodeCannotFieldEnough_TheNextNearestSourceIsDrawnToo()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 1000,
                                     sources: new[] { ("HOME", 5, 20.0), ("ALT", 5, 40.0) });

            List<string> dispatched = Dispatches(Run(w, Tuning(), d, r));

            Assert.Equal(2, dispatched.Count);
            Assert.Contains("HOME -> N002", dispatched[0]);
            Assert.Contains("wave=1/2", dispatched[0]);
            Assert.Contains("ALT -> N002", dispatched[1]);      // nearest first, then the next one out
            Assert.Contains("wave=2/2", dispatched[1]);
            Assert.Equal(2, d.PendingFleets.Count(f => f.Kind == ActionKind.Guard));
            // 1250 is what destroys a 1000-DPS attacker at margin 1.25; the marshal says so on the line.
            Assert.Contains("duel=", dispatched[0]);
            Assert.EndsWith("/1250", dispatched[0].Split(new[] { "duel=" }, StringSplitOptions.None)[1].Split(' ')[0]);
        }

        // The other direction, and the control for the test above: one node that can already destroy
        // the attacker sends one convoy and stops. Without this, "marshals a second wave" would pass
        // on an implementation that always sent every convoy it could.
        [Fact]
        public void WhenOneNodeCanDestroyTheAttackerAlone_OnlyOneConvoyGoes()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 400,      // 500 duel damage needed; HOME's five hulls deal 700
                                     sources: new[] { ("HOME", 5, 20.0), ("ALT", 5, 40.0) });

            List<string> dispatched = Dispatches(Run(w, Tuning(), d, r));

            Assert.Single(dispatched);
            Assert.Contains("wave=1/1", dispatched[0]);
            Assert.DoesNotContain(dispatched, m => m.Contains("ALT -> N002"));
        }

        // The cap is what stops one flashpoint emptying an estate.
        [Fact]
        public void TheMarshalNeverExceedsTheConvoyCap()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 4000,     // far past anything these nodes can field
                                     sources: new[] { ("HOME", 5, 20.0), ("ALT", 5, 40.0),
                                                      ("FAR", 5, 60.0), ("FARTHER", 5, 80.0) });
            FactionTuning t = Tuning();
            t.GuardCommitRatio = 0;                                   // isolate the cap from the floor

            List<string> dispatched = Dispatches(Run(w, t, d, r));

            Assert.Equal(t.GuardMaxConvoysPerThreat, dispatched.Count);
            Assert.DoesNotContain(dispatched, m => m.Contains("FARTHER -> N002"));
        }

        // The floor is judged on the whole marshal, not convoy by convoy: nothing may be spent while
        // the total still loses, and every hull drawn on the way to that verdict goes back.
        [Fact]
        public void AMarshalThatStillLosesSpendsNothingAndReturnsEveryHull()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 9000,
                                     sources: new[] { ("HOME", 5, 20.0), ("ALT", 5, 40.0) });
            long before = w.GetTreasury("DEF");

            List<string> log = Run(w, Tuning(), d, r);

            Assert.Empty(Dispatches(log));
            Assert.NotNull(log.FirstOrDefault(m => m.Contains("GUARD_OUTGUNNED")));
            Assert.Equal(before, w.GetTreasury("DEF"));
            Assert.Equal(5, w.Graph.Nodes["HOME"].ShipStock["Gunship"]);
            Assert.Equal(5, w.Graph.Nodes["ALT"].ShipStock["Gunship"]);
            Assert.Empty(d.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // An attacker whose strength cannot be read -- a player siege, or a fleet dispatched before
        // its bands were recorded -- is answered, but never marshalled against: there is no figure to
        // size a second convoy by.
        [Fact]
        public void AnUnmeasurableAttackerIsAnsweredWithOneConvoy()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 0,
                                     sources: new[] { ("HOME", 5, 20.0), ("ALT", 5, 40.0) });

            List<string> dispatched = Dispatches(Run(w, Tuning(), d, r));

            Assert.Single(dispatched);
            Assert.DoesNotContain("duel=", dispatched[0]);
        }

        // ---- coverage ------------------------------------------------------------------------

        // A node with a defence already standing is topped up while that defence only survives its
        // attacker, and left alone once it kills it. The pair is the point: either assertion alone
        // passes on an implementation that always tops up, or on one that never does.
        [Fact]
        public void AStandingDefenceIsToppedUpOnlyWhileItFallsShortOfDestroying()
        {
            foreach (bool sufficient in new[] { false, true })
            {
                FactionState d, r;
                WorldModel w = MakeWorld(out d, out r, raidDps: 1000,
                                         sources: new[] { ("HOME", 5, 20.0) });
                // 1250 duel damage destroys this attacker; each 200-DPS hull brings 140.
                float standingDps = sufficient ? 1800f : 200f;
                d.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "DEF", TargetNodeId = "N002",
                    OriginPosKm = new Vec3(30, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                    DepartedUtc = w.UtcNow.AddMinutes(-5), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-4),
                    Resolved = true, LastResolvedSegment = -1,
                    WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = standingDps, RangeM = 800 } },
                });

                List<string> dispatched = Dispatches(Run(w, Tuning(), d, r));

                if (sufficient) Assert.Empty(dispatched);
                else Assert.NotEmpty(dispatched);
            }
        }

        // ---- the player faction ----------------------------------------------------------------

        // The hulls a player parks at a node were never going to answer a raid any other way: the
        // Defend block ran for NPCs only, so a player faction's estate was defended by its station
        // guns alone however large its fleet.
        [Fact]
        public void APlayerFactionDefendsItsOwnEstate()
        {
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 400,
                                     sources: new[] { ("HOME", 5, 20.0) });
            d.IsPlayerFaction = true;

            List<string> dispatched = Dispatches(Run(w, Tuning(), d, r));

            Assert.Single(dispatched);
            Assert.Contains("HOME -> N002", dispatched[0]);
        }
    }
}
