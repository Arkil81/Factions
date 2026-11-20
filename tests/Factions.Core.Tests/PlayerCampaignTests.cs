using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Slice C of the campaign delegation (docs/campaigns.md -> A player faction): a player places a
    /// raid or conquest directive on a node, and their faction's managers serve it with the brain
    /// still switched off.
    /// </summary>
    public class PlayerCampaignTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly HashSet<string> Armed = new HashSet<string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return Armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = 500f, RangeM = 800 } }
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

        private static FactionTuning PlayerTuning()
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
            t.PlayerCampaignCap = 2;
            t.PlayerCampaignDuration = TimeSpan.FromHours(6);
            t.BirthrightRangeKm = 100;
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

        /// <summary>A player faction with one stocked home node, one more node of its own, two
        /// hostile-owned neighbours inside its birthright range and one far beyond it.</summary>
        private static WorldModel PlayerWorld(out FactionState st)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("EXP", 5000000);
            SpatialNode home = AddNode(w, "HOME", "EXP", new Vec3(0, 0, 0));
            AddNode(w, "MINE", "EXP", new Vec3(5, 0, 0));
            AddNode(w, "T1", "THEM", new Vec3(10, 0, 0));
            AddNode(w, "T2", "THEM", new Vec3(20, 0, 0));
            AddNode(w, "FAR", "THEM", new Vec3(1000, 0, 0));
            w.Graph.Connect("HOME", "T1");
            w.Graph.Connect("HOME", "T2");
            var cat = new Cat();
            cat.Armed.Add("Gunship");
            w.PrefabCatalogue = cat;
            home.ShipStock["Gunship"] = 4;
            st = new FactionState { Tag = "EXP", RngSeed = 1, IsPlayerFaction = true };
            return w;
        }

        private static IList<FactionKnowledge> Known(FactionState st)
        {
            return new[] { st.Knowledge };
        }

        private static OffensiveCampaigns.PlayerResult Place(FactionState st, WorldModel w, FactionTuning t,
            string nodeId, CampaignObjectiveKind kind, out CampaignFocus placed)
        {
            return OffensiveCampaigns.TryStartPlayer(st, t, w.UtcNow, w, nodeId, kind, false, Known(st), out placed);
        }

        // ---- placement -------------------------------------------------------------------------

        [Fact]
        public void Place_RefusesTheFactionsOwnNode()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.OwnNode,
                Place(st, w, t, "MINE", CampaignObjectiveKind.Terrorise, out placed));
            Assert.Null(placed);
            Assert.Empty(st.CampaignFoci);
        }

        [Fact]
        public void Place_RefusesANodeTheFactionDoesNotKnow_AndAcceptsItOnceSeen()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.NotKnown,
                Place(st, w, t, "FAR", CampaignObjectiveKind.Terrorise, out placed));

            st.Knowledge.Touch(w.Graph.Nodes["FAR"], w.UtcNow);
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok,
                Place(st, w, t, "FAR", CampaignObjectiveKind.Terrorise, out placed));
            Assert.True(placed.PlayerIssued);
            Assert.Equal(CampaignCause.Directive, placed.Objectives[0].Cause);
            Assert.Equal("FAR", placed.Objectives[0].TargetNodeId);
            Assert.Equal(0, placed.Objectives[0].WantedCombatDps);
        }

        [Fact]
        public void Place_RefusesAPactPartnersNode()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            st.PactWith.Add("THEM");
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.PactPartner,
                Place(st, w, t, "T1", CampaignObjectiveKind.SeizeRegion, out placed));
        }

        [Fact]
        public void Place_RefusesADuplicate_AndTheCap()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.Terrorise, out placed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.Duplicate, Place(st, w, t, "T1", CampaignObjectiveKind.Terrorise, out placed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T2", CampaignObjectiveKind.Terrorise, out placed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.CapReached, Place(st, w, t, "T1", CampaignObjectiveKind.SeizeRegion, out placed));
            Assert.Equal(2, OffensiveCampaigns.ActivePlayer(st).Count);
        }

        [Fact]
        public void Cancel_ReachesOnlyThePlayersOwnFocus()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.Terrorise, out placed));
            var nominated = new CampaignFocus { Id = "raid:brain", CenterNodeId = "HOME", SetAtUtc = w.UtcNow, ExpiresAtUtc = w.UtcNow.AddHours(2) };
            nominated.Objectives.Add(new CampaignObjective { Kind = CampaignObjectiveKind.Terrorise, Cause = CampaignCause.Predation, TargetNodeId = "T2" });
            st.CampaignFoci.Add(nominated);

            CampaignFocus removed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.NotFound, OffensiveCampaigns.CancelPlayer(st, "raid:brain", out removed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, OffensiveCampaigns.CancelPlayer(st, placed.Id, out removed));
            Assert.Same(placed, removed);
            Assert.Single(st.CampaignFoci);
        }

        // ---- the manager serves it ---------------------------------------------------------------

        [Fact]
        public void Candidates_AreProjectedForAPlayerDirective_AndEmptyOnceTheGroundIsTaken()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.SeizeRegion, out placed));

            FactionLifecycle.ProjectPlayerCampaignCandidates(st, w, t);
            Assert.Equal(new[] { "T1" }, placed.Objectives[0].Candidates);

            w.Graph.Nodes["T1"].OwnerFactionTag = "EXP";
            FactionLifecycle.ProjectPlayerCampaignCandidates(st, w, t);
            Assert.Empty(placed.Objectives[0].Candidates);
        }

        [Fact]
        public void PlayerFaction_DispatchesUnderItsConquestDirective_RaidAboveTheCeilingConquerAtOrUnder()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.SeizeRegion, out placed));

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            PendingFleetAction raid = Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Raid));
            Assert.Equal("T1", raid.TargetNodeId);
            Assert.Contains(log, m => m.Contains("FLEET_DISPATCH EXP kind=Raid") && m.Contains("objective=" + placed.Id));
            Assert.Equal(5000000 - t.RaidCost, w.GetTreasury("EXP"));

            // A second directive on a broken node finishes it with Conquer.
            w.Graph.Nodes["T2"].HoldStrength = 0.1;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T2", CampaignObjectiveKind.SeizeRegion, out placed));
            new FleetManager().Tick(new[] { st }, w, t, log);
            PendingFleetAction conquer = Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Conquer));
            Assert.Equal("T2", conquer.TargetNodeId);
        }

        /// <summary>The paired negative: a focus of any other provenance on a player faction is a
        /// leftover, not an order, and dispatches nothing — the brain that would have nominated it
        /// never ran for this faction.</summary>
        [Fact]
        public void PlayerFaction_WithoutADirective_DispatchesNothing()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            var nominated = new CampaignFocus { Id = "raid:brain", CenterNodeId = "HOME", SetAtUtc = w.UtcNow, ExpiresAtUtc = w.UtcNow.AddHours(2) };
            nominated.RegionMembers.Add("HOME");
            var objective = new CampaignObjective { Kind = CampaignObjectiveKind.Terrorise, Cause = CampaignCause.Predation, TargetNodeId = "T1" };
            objective.Candidates.Add("T1");
            nominated.Objectives.Add(objective);
            st.CampaignFoci.Add(nominated);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Empty(st.PendingFleets);
            Assert.DoesNotContain(log, m => m.Contains("FLEET_DISPATCH"));
            Assert.Equal(5000000, w.GetTreasury("EXP"));
        }

        [Fact]
        public void Expiry_ClosesAPlayerDirective()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.Terrorise, out placed));
            w.Graph.Nodes["HOME"].ShipStock.Clear();   // nothing flies; only the close is under test

            w.UtcNow = w.UtcNow + t.PlayerCampaignDuration + TimeSpan.FromMinutes(1);
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Empty(st.CampaignFoci);
            // No raid was fought under it, so the verdict is the idle one; what is under test is
            // that a player faction's directive reaches the close at all.
            Assert.Contains(log, m => m.Contains("CAMPAIGN_OBJECTIVE CLOSED EXP kind=raid cause=Directive")
                                      && m.Contains("node=T1"));
        }

        // ---- the defensive directive (FAC-309 slice 5) ----------------------------------------

        private static HashSet<string> Around(WorldModel w, string tag, string center)
        {
            return w.ComputeCampaignRegionMembers(tag, center, 2);
        }

        private static OffensiveCampaigns.PlayerResult Defend(FactionState st, WorldModel w, FactionTuning t,
            string nodeId, out CampaignFocus placed)
        {
            return CampaignFocusLifecycle.TryStartPlayer(st, t, w.UtcNow, w, nodeId, Around(w, st.Tag, nodeId), out placed);
        }

        [Fact]
        public void Defend_RefusesANodeTheFactionDoesNotOwn_AndAnUnknownOne()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.NotOwnNode, Defend(st, w, t, "T1", out placed));
            Assert.Null(placed);
            Assert.Equal(OffensiveCampaigns.PlayerResult.UnknownNode, Defend(st, w, t, "NOPE", out placed));
            Assert.Empty(st.CampaignFoci);
        }

        [Fact]
        public void Defend_MakesAPlayerIssuedDefensiveFocusOverTheNeighbourhood()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "HOME", out placed));
            Assert.True(placed.PlayerIssued);
            Assert.Equal("HOME", placed.CenterNodeId);
            Assert.Equal("player:defend:node:HOME", placed.Id);
            Assert.Equal(w.UtcNow + t.PlayerCampaignDuration, placed.ExpiresAtUtc);
            CampaignObjective objective = Assert.Single(placed.Objectives);
            Assert.Equal(CampaignObjectiveKind.DefendRegion, objective.Kind);
            Assert.Equal(CampaignCause.Directive, objective.Cause);
            Assert.Equal("HOME", objective.TargetNodeId);
            // The region is the brain's own BFS: HOME plus the owned neighbour 5 km away, not the
            // hostile ones.
            Assert.Contains("HOME", placed.RegionMembers);
            Assert.Contains("MINE", placed.RegionMembers);
            Assert.DoesNotContain("T1", placed.RegionMembers);
            // It is a directive, so it is not an offensive entry and does take a cap slot.
            Assert.Empty(OffensiveCampaigns.ActivePlayer(st));
            Assert.Equal(1, OffensiveCampaigns.PlayerDirectiveCount(st));
        }

        [Fact]
        public void Defend_RefusesADuplicateCentre_ANeighbourhoodAlreadyCovered_AndSharesTheCap()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "HOME", out placed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.Duplicate, Defend(st, w, t, "HOME", out placed));
            // MINE is already inside HOME's circuit, so a focus centred there adds no coverage.
            Assert.Equal(OffensiveCampaigns.PlayerResult.Duplicate, Defend(st, w, t, "MINE", out placed));
            // One raid fills the cap of 2 -- a defensive focus and an offensive one share it.
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.Terrorise, out placed));
            Assert.Equal(OffensiveCampaigns.PlayerResult.CapReached, Place(st, w, t, "T2", CampaignObjectiveKind.Terrorise, out placed));
            AddNode(w, "OUT", "EXP", new Vec3(0, 500, 0));
            Assert.Equal(OffensiveCampaigns.PlayerResult.CapReached, Defend(st, w, t, "OUT", out placed));
        }

        /// <summary>The whole point: a player faction, brain off, patrols the region its player
        /// asked it to. The paired negative is PatrolCircuitTests.APlayerFactionPatrolsOnlyAPlayerIssuedFocus.</summary>
        [Fact]
        public void PlayerFaction_PatrolsUnderItsDefensiveDirective()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            t.PatrolCost = 1000;
            t.PatrolBurstSize = 1;
            t.MusterConvoyScaleMax = 1.0;
            t.PatrolCircuitMaxStops = 4;
            t.PatrolNodeCooldown = TimeSpan.FromSeconds(900);
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "MINE", out placed));

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            PendingFleetAction patrol = Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Patrol));
            Assert.Equal("HOME", patrol.OriginNodeId);   // flown from where the hulls are, not from the centre
            string line = Assert.Single(log.Where(m => m.Contains("PATROL_DISPATCH")));
            Assert.Contains("focus=MINE", line);
            Assert.Equal(w.UtcNow, placed.Objectives[0].LastDispatchUtc);
            Assert.Equal(5000000 - t.PatrolCost, w.GetTreasury("EXP"));
        }

        [Fact]
        public void Defend_ClosesOnExpiry_AndOnLosingItsCentre()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            w.Graph.Nodes["HOME"].ShipStock.Clear();   // nothing flies; only the close is under test
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "MINE", out placed));

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Single(st.CampaignFoci);   // well inside its duration, centre still ours

            w.Graph.Nodes["MINE"].OwnerFactionTag = "THEM";
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Empty(st.CampaignFoci);
            Assert.Contains(log, m => m.Contains("CAMPAIGN_OBJECTIVE CLOSED EXP kind=defend cause=Directive")
                                      && m.Contains("node=MINE") && m.Contains("reason=center_lost"));

            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "HOME", out placed));
            w.UtcNow = w.UtcNow + t.PlayerCampaignDuration + TimeSpan.FromMinutes(1);
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Empty(st.CampaignFoci);
            Assert.Contains(log, m => m.Contains("kind=defend") && m.Contains("node=HOME") && m.Contains("reason=expired"));
        }

        [Fact]
        public void Defend_CanBeCancelled_AndRoundTripsPersistence()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Defend(st, w, t, "HOME", out placed));

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap);
            FactionState restored = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            CampaignFocus focus = Assert.Single(restored.CampaignFoci);
            Assert.True(focus.PlayerIssued);
            Assert.Equal(placed.Id, focus.Id);
            Assert.Equal(CampaignObjectiveKind.DefendRegion, focus.Objectives[0].Kind);
            Assert.Equal(CampaignCause.Directive, focus.Objectives[0].Cause);
            Assert.Equal(placed.RegionMembers.Count, focus.RegionMembers.Count);

            CampaignFocus removed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, OffensiveCampaigns.CancelPlayer(st, placed.Id, out removed));
            Assert.Same(placed, removed);
            Assert.Empty(st.CampaignFoci);
        }

        [Fact]
        public void Persistence_RoundTripsPlayerIssuedAndTheDirectiveCause()
        {
            FactionState st; WorldModel w = PlayerWorld(out st); FactionTuning t = PlayerTuning();
            CampaignFocus placed;
            Assert.Equal(OffensiveCampaigns.PlayerResult.Ok, Place(st, w, t, "T1", CampaignObjectiveKind.SeizeRegion, out placed));

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap);
            FactionState restored = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            CampaignFocus focus = Assert.Single(restored.CampaignFoci);
            Assert.True(focus.PlayerIssued);
            Assert.Equal(placed.Id, focus.Id);
            Assert.Equal(CampaignCause.Directive, focus.Objectives[0].Cause);
            Assert.Equal("T1", focus.Objectives[0].TargetNodeId);
        }
    }
}
