using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-309 slice 1: a Guard is committed only when it can actually win the fight it is sent to
    /// (counting the node's own guns), and a refusal turns into a build order instead of silence.
    ///
    /// <para>The measurement these stand on: <c>ResolveShipDuel</c> gives each side an effective HP
    /// equal to its total weapon DPS, so "equal strength" is not a judgement call — it is the duel's
    /// own arithmetic, read ahead of time through <see cref="CombatStrength"/>.</para>
    /// </summary>
    public class GuardStrengthGateTests
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
            t.GuardCommitRatio = 1.2;         // the margin these were written against; the shipped default moved to 0.9 (TuneVersion 46)
            // These were written against a single convoy sized to the commit margin, which is the
            // composition rule they pin. The marshal's own knobs are held at that same premise so
            // they keep pinning it: one wave, sized to 1.2.
            t.GuardOverwhelmMargin = 1.2;
            t.GuardMaxConvoysPerThreat = 1;
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

        /// <summary>HOME holds <paramref name="hulls"/> defenders of <paramref name="hullDps"/> each;
        /// N002 is the threatened node. The raider's convoy DPS is <paramref name="raidDps"/> — set on
        /// the fleet's own bands, because that is what the gate and the duel both read.</summary>
        private static WorldModel MakeWorld(out FactionState defender, out FactionState raider,
                                            double raidDps, double hullDps, int hulls)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Gunship"] = (float)hullDps;
            w.PrefabCatalogue = cat;

            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            w.Graph.Connect("HOME", "N002");
            home.ShipStock["Gunship"] = hulls;

            defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetNodeId = "N002",
                OriginPosKm = new Vec3(500, 0, 0),
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

        private static List<string> Run(WorldModel w, FactionTuning t, FactionState defender, FactionState raider)
        {
            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);
            return log;
        }

        private static string Line(List<string> log, string token)
        {
            return log.FirstOrDefault(m => m.Contains(token));
        }

        // ---- the gate ----------------------------------------------------------------------------

        // The pair that is the whole ticket. Same world, same money, same stock, same raid -- only the
        // attacker's strength differs, so nothing but the gate can explain the two outcomes.
        [Fact]
        public void AWinnableFightIsTaken_AnUnwinnableOneIsRefused()
        {
            FactionTuning t = Tuning();
            FactionState d, r;

            // 1000 of defence against 500 of attack at ratio 1.2 -> 1000 >= 600. Sails.
            WorldModel win = MakeWorld(out d, out r, raidDps: 500, hullDps: 1000, hulls: 1);
            List<string> won = Run(win, t, d, r);
            Assert.NotNull(Line(won, "GUARD_DISPATCH"));
            Assert.Null(Line(won, "GUARD_OUTGUNNED"));

            // 100 of defence against 5000 of attack -> 100 >= 6000 is false. Stays home.
            WorldModel lose = MakeWorld(out d, out r, raidDps: 5000, hullDps: 100, hulls: 1);
            List<string> lost = Run(lose, t, d, r);
            Assert.Null(Line(lost, "GUARD_DISPATCH"));
            Assert.NotNull(Line(lost, "GUARD_OUTGUNNED"));
            Assert.Empty(d.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }


        // ---- FAC-360: reach, which a DPS total cannot see ----------------------------------------

        // The arithmetic of the defect, with no world plumbing in the way. One defence, one attacker,
        // two predicates, opposite answers -- and the duel resolves the way Destroys says, because it
        // IS the duel's arithmetic. Live, this shape declined 58 defences in an hour and then won the
        // one it finally sailed at ratio=0.05.
        [Fact]
        public void AReachAdvantage_LosesOnTotals_AndWinsTheDuel()
        {
            FactionTuning t = Tuning();                       // 3 turns at 2000/800/0, fraction 0.35
            var node = new SpatialNode { Id = "N", OwnerFactionTag = "DEF" };
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 1180, RangeM = 3000 } };
            var force = new List<WeaponBand> { new WeaponBand { Dps = 10, RangeM = 800 } };
            const double attackDps = 1000;                    // the raider, 800 m, no long bracket

            // Totals: 1180 + 10 = 1190, against 1000 x 1.2 = 1200. One short.
            Assert.Equal(1190, CombatStrength.OfDefence(force, node, t), 3);
            Assert.False(CombatStrength.Holds(CombatStrength.OfDefence(force, node, t), attackDps,
                                              t.GuardCommitRatio));

            // Brackets: the station fires in all three turns, the hull in two, the attacker cannot
            // answer the first at all. (1180 + 1190 + 1190) x 0.35 = 1246, against 1200 to destroy.
            Assert.Equal(1246, CombatStrength.DuelDamage(force, node, t), 3);
            Assert.True(CombatStrength.Destroys(force, node, t, attackDps, t.GuardOverwhelmMargin));
            Assert.False(CombatStrength.Survives(force, node, t, attackDps, t.GuardOverwhelmMargin));
        }

        // The same pair through the dispatcher: the gate must take the verdict that matches the duel.
        // Without the bracketed arm this logs GUARD_OUTGUNNED, which is what shipped.
        [Fact]
        public void AGuardThatLosesOnTotalsButKillsInTheDuel_IsStillSent()
        {
            FactionTuning t = Tuning();
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 1000, hullDps: 10, hulls: 1);
            SpatialNode node = w.Graph.Nodes["N002"];
            node.StationPrefabId = "Fort";
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 1180, RangeM = 3000 } };

            List<string> log = Run(w, t, d, r);

            Assert.NotNull(Line(log, "GUARD_DISPATCH"));
            Assert.Null(Line(log, "GUARD_OUTGUNNED"));
        }

        // The negative control, and the one that stops the widening becoming "always sail". The
        // station's REACH is cut to the attacker's, so there is no free turn, and it is weak enough
        // that the duel neither destroys the raider nor drives it off (641 of the 780 that would):
        // both predicates agree it is hopeless.
        [Fact]
        public void AWeakerDefenceWithoutTheReach_IsStillRefused()
        {
            FactionTuning t = Tuning();
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 1000, hullDps: 10, hulls: 1);
            SpatialNode node = w.Graph.Nodes["N002"];
            node.StationPrefabId = "Fort";
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 600, RangeM = 800 } };

            List<string> log = Run(w, t, d, r);

            Assert.NotNull(Line(log, "GUARD_OUTGUNNED"));
            Assert.Null(Line(log, "GUARD_DISPATCH"));
        }

        // The operator's rule: refuse only if the STATION cannot close the gap. The hull alone loses
        // in both halves; the node's own guns are what decides. Without this the gate would strand a
        // defender at home beside a fortress that was going to win anyway.
        [Fact]
        public void TheStationCountsTowardTheDefence_AndCanRescueAFightTheHullWouldLose()
        {
            FactionTuning t = Tuning();
            FactionState d, r;

            // Hull 100 vs attack 1000 (needs 1200): hopeless alone.
            WorldModel bare = MakeWorld(out d, out r, raidDps: 1000, hullDps: 100, hulls: 1);
            Assert.NotNull(Line(Run(bare, t, d, r), "GUARD_OUTGUNNED"));

            // Same numbers, but N002 is a station with real guns: 100 + 1500 >= 1200. Sails.
            WorldModel fortified = MakeWorld(out d, out r, raidDps: 1000, hullDps: 100, hulls: 1);
            SpatialNode node = fortified.Graph.Nodes["N002"];
            node.StationPrefabId = "Fort";
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 1500, RangeM = 800 } };
            Assert.NotNull(Line(Run(fortified, t, d, r), "GUARD_DISPATCH"));
        }

        // The flat station term is the other half of "the station counts", and it is the one a gate
        // reimplementing the duel would most easily drop -- it is not in the bands.
        [Fact]
        public void TheFlatStationDefenceCountsToo_AndOnlyForAFoundedStation()
        {
            FactionTuning t = Tuning();
            t.StationDefensePower = 5000;
            FactionState d, r;

            WorldModel claim = MakeWorld(out d, out r, raidDps: 1000, hullDps: 100, hulls: 1);
            Assert.Null(claim.Graph.Nodes["N002"].StationPrefabId);              // a bare claim
            Assert.NotNull(Line(Run(claim, t, d, r), "GUARD_OUTGUNNED"));

            WorldModel founded = MakeWorld(out d, out r, raidDps: 1000, hullDps: 100, hulls: 1);
            founded.Graph.Nodes["N002"].StationPrefabId = "Fort";                 // same node, founded
            Assert.NotNull(Line(Run(founded, t, d, r), "GUARD_DISPATCH"));
        }

        // A refusal has to buy something, or "do not send lemmings" becomes "never defend anything".
        // The fight becomes a build order sized at the DPS that would have won it.
        [Fact]
        public void ARefusalPostsAShipDemandCarryingTheMissingDps_AndReturnsTheHulls()
        {
            FactionTuning t = Tuning();
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 5000, hullDps: 100, hulls: 3);

            List<string> log = Run(w, t, d, r);

            Assert.NotNull(Line(log, "GUARD_OUTGUNNED"));
            Assert.Equal(3, ShipPool.Available(w.Graph.Nodes["HOME"], "Gunship"));   // nothing spent

            DemandEntry ship = w.Demand.SnapshotShipDemands("DEF").Single(e => e.Kind == "Ship:Patrol");
            // 5000 attack x 1.2, with no station to help: the whole 6000 is owed to the shipyard.
            Assert.Equal(6000, ship.MinDps, 0);
        }

        [Fact]
        public void ARefusalSpendsNoMoney()
        {
            FactionTuning t = Tuning();
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 5000, hullDps: 100, hulls: 1);
            long before = w.GetTreasury("DEF");

            Assert.NotNull(Line(Run(w, t, d, r), "GUARD_OUTGUNNED"));
            Assert.Equal(before, w.GetTreasury("DEF"));
        }

        // ---- sizing ------------------------------------------------------------------------------

        // Posture says how big a convoy this faction likes; it knows nothing about what is coming.
        // Four hulls of 200 beat an 800 attack at ratio 1.2 (960 needed); one would not.
        [Fact]
        public void TheConvoyIsSizedToBeatTheAttack_NotToTheBurstSize()
        {
            FactionTuning t = Tuning();
            Assert.Equal(1, t.GuardBurstSize);         // what posture alone would have sent
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 800, hullDps: 200, hulls: 6);

            Assert.NotNull(Line(Run(w, t, d, r), "GUARD_DISPATCH"));

            PendingFleetAction guard = d.PendingFleets.Single(f => f.Kind == ActionKind.Guard);
            Assert.Equal(5, guard.ConvoySize);         // ceil(960 / 200)
            Assert.Equal(1000f, guard.WeaponBands.Sum(b => b.Dps));
            Assert.Equal(1, ShipPool.Available(w.Graph.Nodes["HOME"], "Gunship"));   // and no more
        }

        // The other direction: a threat small enough for the burst size does not inflate the convoy.
        [Fact]
        public void ASmallThreatDoesNotInflateTheConvoy()
        {
            FactionTuning t = Tuning();
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 10, hullDps: 1000, hulls: 6);

            Assert.NotNull(Line(Run(w, t, d, r), "GUARD_DISPATCH"));
            Assert.Equal(1, d.PendingFleets.Single(f => f.Kind == ActionKind.Guard).ConvoySize);
        }

        // ---- what the gate must NOT refuse -------------------------------------------------------

        // A player siege carries no fleet and therefore no bands, and the feeblest hull in the world
        // must still sail: nothing about a player's strength is readable, so there is nothing to weigh.
        //
        // This asserts the OUTCOME only. It does not discriminate "unmeasurable" from "measured at
        // zero" -- both dispatch, because a defence of any size clears a zero bar. The distinction is
        // pinned by CombatStrength_AnAttackerWithNoBandsIsUnmeasurable_NotWeak below, which is the
        // test that fails if TryOfAttack starts reporting an empty band list as a weak attacker.
        [Fact]
        public void APlayerSiegeIsAlwaysAnswered_ItsStrengthBeingUnknowable()
        {
            FactionTuning t = Tuning();
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat(); cat.Dps["Gunship"] = 1f;          // the feeblest possible defender
            w.PrefabCatalogue = cat;
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            w.Graph.Connect("HOME", "N002");
            home.ShipStock["Gunship"] = 1;

            w.Tuning = t;
            target.SiegeStartedUtc = w.UtcNow;
            w.RegisterPlayerAggression("N002", t.PlayerAggressionMinDamage + 1);

            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            var log = new List<string>();
            new FleetManager().Tick(new[] { defender }, w, t, log);

            string line = Line(log, "GUARD_DISPATCH");
            Assert.NotNull(line);
            Assert.Contains("vs=player", line);
            Assert.Null(Line(log, "GUARD_OUTGUNNED"));
        }

        // The disable switch, and the negative control on every assertion above: at ratio 0 the gate
        // is inert and the hopeless fight is taken, exactly as it was before this existed.
        [Fact]
        public void RatioAtZeroDisablesTheGate()
        {
            FactionTuning t = Tuning();
            t.GuardCommitRatio = 0;
            FactionState d, r;
            WorldModel w = MakeWorld(out d, out r, raidDps: 5000, hullDps: 1, hulls: 1);

            List<string> log = Run(w, t, d, r);
            Assert.NotNull(Line(log, "GUARD_DISPATCH"));
            Assert.Null(Line(log, "GUARD_OUTGUNNED"));
        }

        // ---- the input has to survive a restart --------------------------------------------------

        /// <summary>Found live while building the gate: 13 of 13 raid-like fleets in flight on the
        /// box carried no weapon bands at all, because the bands are derived and nothing rebuilt them
        /// on load. That disarms the gate — but it also disarms the DUEL, which scores an empty band
        /// list as one point of effective HP and no ability to shoot back.</summary>
        [Fact]
        public void AFleetThatCameBackFromDiskUnarmed_IsRearmedFromTheHullItWasDispatchedWith()
        {
            var cat = new Cat();
            cat.Dps["Gunship"] = 250f;
            var st = new FactionState { Tag = "RVN" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetNodeId = "N002",
                ShipPrefabId = "Gunship", ConvoySize = 4,
                WeaponBands = null,                       // exactly as a brain load leaves it
            });

            Assert.Equal(1, PendingFleetResolver.RebuildMissingWeaponBands(new[] { st }, cat));

            PendingFleetAction fleet = st.PendingFleets[0];
            Assert.Equal(1000f, ShipPool.TotalDps(fleet.WeaponBands));   // 250 x 4, the convoy it flew with
        }

        [Fact]
        public void RearmingNeverOverwritesBandsAFleetAlreadyHas_AndSurvivesAnUnknownHull()
        {
            var cat = new Cat();
            cat.Dps["Gunship"] = 250f;                                   // "Ghost" is not in the catalogue
            var armed = new FactionState { Tag = "A" };
            armed.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ShipPrefabId = "Gunship", ConvoySize = 4,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 7, RangeM = 1 } },
            });
            var ghost = new FactionState { Tag = "B" };
            ghost.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ShipPrefabId = "Ghost", ConvoySize = 4, WeaponBands = null,
            });

            Assert.Equal(0, PendingFleetResolver.RebuildMissingWeaponBands(new[] { armed, ghost }, cat));

            Assert.Equal(7f, ShipPool.TotalDps(armed.PendingFleets[0].WeaponBands));  // untouched
            // An unknown hull is left empty rather than given a fabricated list: it is already empty,
            // and filling it would hide the catalogue gap behind a fleet that reads as armed.
            Assert.True(ghost.PendingFleets[0].WeaponBands == null
                        || ghost.PendingFleets[0].WeaponBands.Count == 0);
        }

        [Fact]
        public void ASquadronIsRearmedFromEveryMemberItStillHas()
        {
            var cat = new Cat();
            cat.Dps["Lead"] = 900f;
            cat.Dps["Escort"] = 100f;
            var st = new FactionState { Tag = "RVN" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Conquer, ShipPrefabId = "Lead", ConvoySize = 3,
                Squadron = new List<string> { "Lead", "Escort", "Escort" },
                WeaponBands = null,
            });

            Assert.Equal(1, PendingFleetResolver.RebuildMissingWeaponBands(new[] { st }, cat));
            Assert.Equal(1100f, ShipPool.TotalDps(st.PendingFleets[0].WeaponBands));   // 900 + 100 + 100
        }

        // ---- the strength model itself -----------------------------------------------------------

        [Fact]
        public void CombatStrength_ReadsTheDefenceTheWayTheDuelScoresIt()
        {
            FactionTuning t = Tuning();
            t.StationDefensePower = 250;
            WorldModel w = TestSupport.World();
            SpatialNode node = AddNode(w, "N", "DEF", new Vec3(0, 0, 0));

            var convoy = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 800 } };

            // A bare claim contributes nothing at all, flat term included.
            Assert.Equal(0, CombatStrength.OfStation(node, t));
            Assert.Equal(100, CombatStrength.OfDefence(convoy, node, t));

            node.StationPrefabId = "Fort";
            Assert.Equal(250, CombatStrength.StationFlat(node, t));
            Assert.Equal(250, CombatStrength.OfStation(node, t));

            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 400, RangeM = 800 } };
            Assert.Equal(650, CombatStrength.OfStation(node, t));
            Assert.Equal(750, CombatStrength.OfDefence(convoy, node, t));
        }

        [Fact]
        public void CombatStrength_AnAttackerWithNoBandsIsUnmeasurable_NotWeak()
        {
            double dps;
            Assert.False(CombatStrength.TryOfAttack(new PendingFleetAction(), out dps));
            Assert.False(CombatStrength.TryOfAttack(
                new PendingFleetAction { WeaponBands = new List<WeaponBand>() }, out dps));

            Assert.True(CombatStrength.TryOfAttack(
                new PendingFleetAction { WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 7 } } },
                out dps));
            Assert.Equal(7, dps);
        }

        [Fact]
        public void CombatStrength_ShortfallIsWhatTheStationCannotCover()
        {
            FactionTuning t = Tuning();
            WorldModel w = TestSupport.World();
            SpatialNode node = AddNode(w, "N", "DEF", new Vec3(0, 0, 0));
            node.StationPrefabId = "Fort";
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 400, RangeM = 800 } };

            Assert.Equal(800, CombatStrength.ShortfallFromShips(1000, node, t, 1.2), 3);  // 1200 - 400
            Assert.Equal(0, CombatStrength.ShortfallFromShips(100, node, t, 1.2), 3);     // station covers it
        }
    }
}
