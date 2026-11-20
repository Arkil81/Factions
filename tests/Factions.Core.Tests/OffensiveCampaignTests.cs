using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Slice B of the campaign delegation (docs/campaigns.md): the brain nominates a region with a
    /// cause and ranks the nodes worth attacking under it; <see cref="FleetManager"/>'s offensive
    /// block decides when to strike, which of them, and with what; and one close scores every
    /// offensive objective.
    /// </summary>
    public class OffensiveCampaignTests
    {
        // ---- fixtures ----------------------------------------------------------------------

        private sealed class Cat : IPrefabCatalogue
        {
            public readonly HashSet<string> Armed = new HashSet<string>();
            public float DpsPerShip = 500f;
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return Armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = DpsPerShip, RangeM = 800 } }
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

        /// <summary>Tuning for the manager tests: every OTHER spender's budget is switched off so a
        /// refusal can only have come from the offensive purse, and the offensive one is a plain
        /// floor with no income term.</summary>
        private static FactionTuning ManagerTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.FleetSpeedKmPerMin = 1200.0;
            t.RaidCost = 1000;
            t.ConquerCost = 2000;
            t.ConquerHoldCeiling = 0.15;
            t.OffensiveTempo = TimeSpan.FromMinutes(10);
            t.RaidMaxIncomeFractionPerWindow = 0;
            t.RaidBudgetFloor = 1000000;
            t.RaidWealthFractionPerWindow = 0;
            t.SpendBudgetWindowSeconds = 3600;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            return t;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos, double hold = 0.9)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        /// <summary>A faction with one stocked home node and two hostile targets to campaign against.</summary>
        private static WorldModel CampaignWorld(out FactionState st, out Cat cat)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("US", 5000000);
            SpatialNode home = AddNode(w, "HOME", "US", new Vec3(0, 0, 0));
            AddNode(w, "T1", "THEM", new Vec3(10, 0, 0));
            AddNode(w, "T2", "THEM", new Vec3(20, 0, 0));
            w.Graph.Connect("HOME", "T1");
            w.Graph.Connect("HOME", "T2");
            cat = new Cat();
            cat.Armed.Add("Gunship");
            w.PrefabCatalogue = cat;
            home.ShipStock["Gunship"] = 4;
            st = new FactionState { Tag = "US", RngSeed = 1 };
            st.Relations["THEM"] = -0.9;
            return w;
        }

        /// <summary>An offensive focus of <paramref name="kind"/> staged on HOME, carrying the ranked
        /// candidate list the brain would have written.</summary>
        private static CampaignObjective GiveObjective(FactionState st, CampaignObjectiveKind kind,
            CampaignCause cause, DateTime nowUtc, params string[] candidates)
        {
            var objective = new CampaignObjective { Kind = kind, Cause = cause };
            foreach (string c in candidates) objective.Candidates.Add(c);
            var focus = new CampaignFocus
            {
                Id = (kind == CampaignObjectiveKind.SeizeRegion ? "conquest:" : "raid:") + "test",
                CenterNodeId = "HOME",
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddHours(2),
            };
            focus.RegionMembers.Add("HOME");
            focus.Objectives.Add(objective);
            st.CampaignFoci.Add(focus);
            return objective;
        }

        private static string DispatchLine(List<string> log)
        {
            return log.FirstOrDefault(m => m.Contains("FLEET_DISPATCH"));
        }

        // ---- the nominators ------------------------------------------------------------------

        /// <summary>A body region, so the collapse under test is a real region collapse and not the
        /// node-scoped fallback a region-less node takes.</summary>
        private static FakeWorld TwoRegionWorld()
        {
            var w = new FakeWorld { Treasury = 500000 };
            var field = (ZeroField)w.Influence;
            field.OverrideSample = 1.0;
            // Border pressure, so Security and Rivalry are non-zero and the conflict branch is
            // actually generated for a faction with no standing quarrel.
            field.OverridePressure = 1.0;
            field.OverrideRivals.Add("THEM");
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "US";
            home.HoldStrength = 0.9;
            home.ShipStock["Raid"] = 1;

            // Rich body: two weak, valuable nodes. Poor body: one well-held, low-value node.
            // Every target sits exactly 100 km from home, so the distance discount is identical for
            // all three and cannot be what ranks them -- value and weakness have to do it alone.
            var richPositions = new[] { new Vec3(100, 0, 0), new Vec3(0, 0, 100) };
            for (int i = 0; i < 2; i++)
            {
                SpatialNode n = w.Graph.Add(richPositions[i], 0.95, "RICH" + i);
                n.OwnerFactionTag = "THEM";
                n.HoldStrength = 0.1;
                n.PlanetId = "PRICH";
                n.Site = StationSite.Surface;
            }
            SpatialNode poor = w.Graph.Add(new Vec3(0, 100, 0), 0.1, "POOR0");
            poor.OwnerFactionTag = "THEM";
            poor.HoldStrength = 0.95;
            poor.PlanetId = "PPOOR";
            poor.Site = StationSite.Surface;
            return w;
        }

        private static Personality Raider(bool implacable = false)
        {
            var p = new Personality
            {
                Temperature = 0.01,
                Traits = new Traits { Aggression = 0.95, Implacable = implacable }
            };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 3.0;
            return p;
        }

        // The headline of the brain half: a raider names a PLACE, with a reason, and never a fleet.
        [Fact]
        public void Nominator_EmitsARaidCampaignForTheRichWeakRegion_AndNeverARaidIntent()
        {
            FakeWorld w = TwoRegionWorld();
            var t = TestSupport.Tuning();
            t.RaidReachThreshold = 0;
            var s = new FactionState { Tag = "US", RngSeed = 1 };
            s.Relations["THEM"] = -0.9;

            Decision d = new FactionBrain().Think(s, Raider(), w, new SeededRandom(1), t);

            Candidate rich = CampaignNomination.Raid(d, w, "RICH0");
            Candidate poor = CampaignNomination.Raid(d, w, "POOR0");
            Assert.NotNull(rich);
            Assert.NotNull(poor);   // the loser competes: value and weakness decide the ranking
            Assert.True(rich.Quality > poor.Quality,
                "the rich, weakly held body must outrank the poor, well-held one");
            Assert.Equal(NodeRegion.Body("PRICH", StationSite.Surface).ToKey(), rich.Intent.TargetRegion);
            Assert.Equal(CampaignObjectiveKind.Terrorise, rich.Intent.ObjectiveKind);
            Assert.Equal(CampaignCause.Predation, rich.Intent.Cause);
            Assert.Equal("H", rich.Intent.TargetNodeId);   // the staging base, not the target
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Raid
                                              || c.Intent.Kind == ActionKind.Retaliate
                                              || c.Intent.Kind == ActionKind.Conquer);
        }

        // The cause a fight ledger produces. The attacker's standing is deliberately ABOVE the
        // hostility threshold, so nothing but the record can have created this candidate.
        [Fact]
        public void Nominator_SetsRetaliationCause_FromRecentAttackers()
        {
            FakeWorld w = TwoRegionWorld();
            var t = TestSupport.Tuning();
            t.RaidReachThreshold = 0;
            var s = new FactionState { Tag = "US", RngSeed = 1 };
            s.Relations["THEM"] = 0.0;
            Assert.True(s.RelationTo("THEM") > t.HostileRelationThreshold,
                        "the standing has to be above the hostility gate or the record proves nothing");
            FightLedger.Append(s, new FightRecord
            {
                Utc = w.UtcNow.AddMinutes(-5),
                Kind = ActionKind.Raid,
                Attacking = false,
                OpponentTag = "THEM",
                NodeId = "H",
            }, 32);

            Decision d = new FactionBrain().Think(s, Raider(), w, new SeededRandom(1), t);

            Candidate answer = Assert.Single(CampaignNomination.AllRetaliations(d));
            Assert.Equal(CampaignCause.Retaliation, answer.Intent.Cause);
            Assert.Equal(CampaignObjectiveKind.Terrorise, answer.Intent.ObjectiveKind);
        }

        // The antagonist looms, it does not settle: no conquest campaign, ever.
        [Fact]
        public void Nominator_ImplacableFaction_NominatesNoConquestCampaign()
        {
            FakeWorld w = TwoRegionWorld();
            var t = TestSupport.Tuning();
            t.RaidReachThreshold = 0;
            var s = new FactionState { Tag = "US", RngSeed = 1 };
            s.Relations["THEM"] = -0.9;

            Decision implacable = new FactionBrain().Think(s, Raider(implacable: true), w, new SeededRandom(1), t);
            Assert.Empty(CampaignNomination.AllConquests(implacable));

            // The same world and the same standings for an ordinary raider DO produce one, so the
            // trait is what decided it and not some other gate this fixture happens to trip.
            var ordinary = new FactionState { Tag = "US", RngSeed = 1 };
            ordinary.Relations["THEM"] = -0.9;
            Decision plain = new FactionBrain().Think(ordinary, Raider(), TwoRegionWorld(), new SeededRandom(1), t);
            Assert.NotEmpty(CampaignNomination.AllConquests(plain));
        }

        // The manager's whole input: a ranked list, rebuilt from scratch every think.
        [Fact]
        public void Candidates_AreRankedByWorth_AndRebuiltEveryThink()
        {
            FakeWorld w = TwoRegionWorld();
            var t = TestSupport.Tuning();
            t.RaidReachThreshold = 0;
            var s = new FactionState { Tag = "US", RngSeed = 1 };
            s.Relations["THEM"] = -0.9;

            var objective = new CampaignObjective { Kind = CampaignObjectiveKind.Terrorise };
            NodeRegion.Body("PRICH", StationSite.Surface).ApplyTo(objective);
            objective.Candidates.Add("STALE");            // a leftover the rebuild must clear
            var focus = new CampaignFocus { Id = "raid:test", CenterNodeId = "H", SetAtUtc = w.UtcNow };
            focus.RegionMembers.Add("H");
            focus.Objectives.Add(objective);
            s.CampaignFoci.Add(focus);

            // RICH1 is made the better target, and it is offered SECOND, so arrival order cannot be
            // what puts it first.
            w.Graph.Nodes["RICH0"].ResourceValue = 0.2;
            w.Graph.Nodes["RICH1"].ResourceValue = 0.95;

            new FactionBrain().Think(s, Raider(), w, new SeededRandom(1), t);

            Assert.Equal(new[] { "RICH1", "RICH0" }, objective.Candidates.ToArray());
            Assert.DoesNotContain("STALE", objective.Candidates);
            Assert.DoesNotContain("POOR0", objective.Candidates);   // outside the objective's region
        }

        // ---- the manager's offensive block -----------------------------------------------------

        [Fact]
        public void OffensiveBlock_DispatchesTheTopCandidate_AndNamesTheObjective()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            CampaignObjective objective = GiveObjective(st, CampaignObjectiveKind.Terrorise,
                CampaignCause.Predation, w.UtcNow, "T2", "T1");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            PendingFleetAction fleet = Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));
            Assert.Equal("T2", fleet.TargetNodeId);                       // the top of the list, not the nearest
            Assert.Equal(w.UtcNow, objective.LastDispatchUtc);
            string line = DispatchLine(log);
            Assert.NotNull(line);
            Assert.Contains("objective=raid:test", line);
            Assert.Equal(5000000 - t.RaidCost, w.GetTreasury("US"));
        }

        // A retaliation campaign keeps the Retaliate fleet kind, so every magnitude, fight record and
        // resolver path downstream is the one it always was.
        [Fact]
        public void OffensiveBlock_RetaliationCause_DispatchesARetaliateFleet()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Retaliation, w.UtcNow, "T1");

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Retaliate));
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));
        }

        [Fact]
        public void OffensiveBlock_Tempo_HoldsTheSecondDispatchBack()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Predation, w.UtcNow, "T1", "T2");
            var manager = new FleetManager();

            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));

            w.UtcNow = w.UtcNow.AddMinutes(1);                            // inside OffensiveTempo
            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));

            w.UtcNow = w.UtcNow.AddMinutes(20);                           // past it
            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Raid));
        }

        [Fact]
        public void OffensiveBlock_SpendBudget_RefusesOnceTheWindowIsSpent()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            t.RaidBudgetFloor = t.RaidCost;            // exactly one raid per window
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Predation, w.UtcNow, "T1", "T2");
            var manager = new FleetManager();

            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));

            w.UtcNow = w.UtcNow.AddMinutes(20);        // past the tempo, still inside the spend window
            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));

            w.UtcNow = w.UtcNow.AddSeconds(t.SpendBudgetWindowSeconds);   // fresh window
            manager.Tick(new[] { st }, w, t, new List<string>());
            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Raid));
        }

        // The DPS budget is what the yard builds toward, not a bar the manager must clear before it
        // fights: a census-sized budget (a whole region's defenders) sits far above what one node
        // can field, and gating on it silenced every raider on the first live run of slice B.
        [Fact]
        public void OffensiveBlock_DispatchesWhileTheSquadronIsUnderTheBudget()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            CampaignObjective objective = GiveObjective(st, CampaignObjectiveKind.Terrorise,
                CampaignCause.Predation, w.UtcNow, "T1");
            long fieldable = ShipPool.BestSquadron(w.Graph.OwnedBy("US"), w.PrefabCatalogue,
                                                   t.RaidSquadronMaxFighters).Dps;
            Assert.True(fieldable > 0, "the yard has to be able to field something or this proves nothing");

            objective.WantedCombatDps = fieldable * 20 + 1;
            new FleetManager().Tick(new[] { st }, w, t, new List<string>());
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));
            Assert.Equal(fieldable * 20 + 1, objective.WantedCombatDps);   // the yard's target is untouched
        }

        // The gate that used to sit on the brain's candidate: with nothing to fly, nothing flies —
        // and no cost is charged and no tempo stamped, so the next tick can try again.
        [Fact]
        public void OffensiveBlock_IsBlocked_WhenNoShipStockExists()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            w.Graph.Nodes["HOME"].ShipStock.Clear();
            CampaignObjective objective = GiveObjective(st, CampaignObjectiveKind.Terrorise,
                CampaignCause.Predation, w.UtcNow, "T1");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));
            Assert.Equal(DateTime.MinValue, objective.LastDispatchUtc);
            Assert.Equal(5000000, w.GetTreasury("US"));
            Assert.Contains(log, m => m.Contains("FLEET_BLOCKED"));
        }

        [Fact]
        public void OffensiveBlock_SkipsACandidateWithAFleetAlreadyInbound()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Predation, w.UtcNow, "T1", "T2");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "US", TargetTag = "THEM", TargetNodeId = "T1",
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10),
                FleetId = "INBOUND", LastResolvedSegment = -1,
            });

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid && f.TargetNodeId == "T2"));
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid && f.TargetNodeId == "T1"));
        }

        // A raid flying a planet-clearance detour carries no TargetNodeId: PlanFleetLeg nulls it
        // and requeues the real destination behind the navigation stops. The inbound skip must
        // see through that, or every tick sends another squadron at the same node.
        [Fact]
        public void OffensiveBlock_SkipsACandidateWithAFleetInboundOnADetour()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Predation, w.UtcNow, "T1", "T2");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "US", TargetTag = null, TargetNodeId = null,
                CurrentStopKind = FleetStopKind.Navigation,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "T1", Kind = FleetStopKind.Engage },
                },
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10),
                FleetId = "DETOUR", LastResolvedSegment = -1,
            });

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            Assert.DoesNotContain(st.PendingFleets, f => f.FleetId != "DETOUR"
                && PendingFleetResolver.SemanticTargetOf(f) == "T1");
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid
                && PendingFleetResolver.SemanticTargetOf(f) == "T2"));
        }

        // Soften, then finish. Both halves in one test because the ceiling is the only thing that
        // differs between them.
        [Fact]
        public void Conquest_SendsRaidAboveTheHoldCeiling_AndConquerAtOrUnderIt()
        {
            FactionState hardSt; Cat hardCat;
            WorldModel hard = CampaignWorld(out hardSt, out hardCat);
            FactionTuning t = ManagerTuning();
            hard.Graph.Nodes["T1"].HoldStrength = t.ConquerHoldCeiling + 0.1;
            GiveObjective(hardSt, CampaignObjectiveKind.SeizeRegion, CampaignCause.Predation, hard.UtcNow, "T1");
            new FleetManager().Tick(new[] { hardSt }, hard, t, new List<string>());
            Assert.Single(hardSt.PendingFleets.Where(f => f.Kind == ActionKind.Raid && f.TargetNodeId == "T1"));
            Assert.Empty(hardSt.PendingFleets.Where(f => f.Kind == ActionKind.Conquer));

            FactionState softSt; Cat softCat;
            WorldModel soft = CampaignWorld(out softSt, out softCat);
            soft.Graph.Nodes["T1"].HoldStrength = t.ConquerHoldCeiling;
            GiveObjective(softSt, CampaignObjectiveKind.SeizeRegion, CampaignCause.Predation, soft.UtcNow, "T1");
            new FleetManager().Tick(new[] { softSt }, soft, t, new List<string>());
            Assert.Single(softSt.PendingFleets.Where(f => f.Kind == ActionKind.Conquer && f.TargetNodeId == "T1"));
        }

        // ---- the close --------------------------------------------------------------------------

        [Fact]
        public void Conquest_ClosesSucceeded_WhenItsNodeIsTaken()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            CampaignObjective objective = GiveObjective(st, CampaignObjectiveKind.SeizeRegion,
                CampaignCause.Predation, w.UtcNow, "T1");
            objective.TargetNodeId = "T1";               // node-scoped: this node IS the objective

            var stillTheirs = new List<string>();
            Assert.Equal(0, OffensiveCampaigns.Maintain(st, t, w.UtcNow, id => true, w, w.PrefabCatalogue, stillTheirs));
            Assert.Single(st.CampaignFoci);

            w.Graph.Nodes["T1"].OwnerFactionTag = "US";  // taken
            var log = new List<string>();
            Assert.Equal(1, OffensiveCampaigns.Maintain(st, t, w.UtcNow, id => true, w, w.PrefabCatalogue, log));
            Assert.Empty(st.CampaignFoci);
            string closed = Assert.Single(log.Where(m => m.StartsWith("CAMPAIGN_OBJECTIVE CLOSED")));
            Assert.Contains("kind=conquest", closed);
            Assert.Contains("node=T1", closed);
            Assert.Contains("verdict=succeeded", closed);
        }

        [Fact]
        public void RaidCampaign_ClosesOnExpiry_WithItsOwnLine()
        {
            FactionState st; Cat cat;
            WorldModel w = CampaignWorld(out st, out cat);
            FactionTuning t = ManagerTuning();
            GiveObjective(st, CampaignObjectiveKind.Terrorise, CampaignCause.Grudge, w.UtcNow, "T1");

            var log = new List<string>();
            Assert.Equal(1, OffensiveCampaigns.Maintain(st, t, w.UtcNow.AddHours(3), id => true,
                                                        w, w.PrefabCatalogue, log));
            string closed = Assert.Single(log.Where(m => m.StartsWith("CAMPAIGN_OBJECTIVE CLOSED")));
            Assert.Contains("kind=raid", closed);
            Assert.Contains("cause=Grudge", closed);
            Assert.Contains("verdict=idle", closed);      // nothing was fought under it
            // The antagonist's line is a different one and must not be produced for an ordinary
            // faction's campaign.
            Assert.DoesNotContain(log, m => m.Contains("TERROR_CAMPAIGN CLOSED"));
        }

        // ---- persistence -------------------------------------------------------------------------

        // The tempo stamp is a cooldown, so it has to survive a restart; the candidate list is a
        // cache of a ranking over a world that has moved, so it must not.
        [Fact]
        public void CauseAndTempoStampSurviveARoundTrip_ButCandidatesDoNot()
        {
            var st = new FactionState { Tag = "US" };
            DateTime now = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            CampaignObjective objective = GiveObjective(st, CampaignObjectiveKind.SeizeRegion,
                CampaignCause.Retaliation, now, "T1", "T2");
            objective.TargetNodeId = "T1";
            objective.LastDispatchUtc = now.AddMinutes(-7);

            FactionState back = FactionStore.ApplyBrainDoc(
                FactionStore.ToBrainDoc(st, new SpatialNode[0], 32), new SpatialGraph());

            CampaignObjective loaded = Assert.Single(Assert.Single(back.CampaignFoci).Objectives);
            Assert.Equal(CampaignObjectiveKind.SeizeRegion, loaded.Kind);
            Assert.Equal(CampaignCause.Retaliation, loaded.Cause);
            Assert.Equal(now.AddMinutes(-7), loaded.LastDispatchUtc);
            Assert.Empty(loaded.Candidates);
        }
    }
}
