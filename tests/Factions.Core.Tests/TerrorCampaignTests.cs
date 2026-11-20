using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The antagonist's offensive shape (FAC-268): an implacable faction that from time to time picks
    /// a region of space and enacts a terror campaign on it — raids only, never conquest — carrying
    /// a fleet requirement the yard builds toward, and reaching the region by decision rather than
    /// by influence.
    /// </summary>
    public class TerrorCampaignTests
    {
        private static Personality Implacable(bool implacable = true)
        {
            var p = new Personality
            {
                Temperature = 0.2,
                Traits = new Traits { Xenophobia = 1.0, Aggression = 0.9, Expansionism = 0.1, Mercantilism = 0.0, Implacable = implacable },
            };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static SpatialNode Node(FakeWorld w, string id, string owner, string feature, Vec3 pos, double value = 0.5, double hold = 0.5)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.FeatureKey = feature;
            n.StableStationId = 1;
            return n;
        }

        /// <summary>FCTM's shape, small: a home nest, and two rival nests -- a rich one of three
        /// nodes and a poor one of three -- plus a lone rival outpost too small to campaign on.</summary>
        private static FakeWorld World(out FactionState fctm)
        {
            var w = new FakeWorld { Treasury = 5_000_000 };
            Node(w, "HOME", "FCTM", "Cinder Nest", new Vec3(0, 0, 0));
            Node(w, "HOME2", "FCTM", "Cinder Nest", new Vec3(100, 0, 0)).ShipStock["Raider"] = 2;
            Node(w, "R1", "IGSL", "Rich Belt", new Vec3(3000, 0, 0), value: 0.9);
            Node(w, "R2", "IGSL", "Rich Belt", new Vec3(3100, 0, 0), value: 0.9);
            Node(w, "R3", "IGSL", "Rich Belt", new Vec3(3200, 0, 0), value: 0.9);
            Node(w, "P1", "SPRT", "Poor Belt", new Vec3(-2000, 0, 0), value: 0.1);
            Node(w, "P2", "SPRT", "Poor Belt", new Vec3(-2100, 0, 0), value: 0.1);
            Node(w, "P3", "SPRT", "Poor Belt", new Vec3(-2200, 0, 0), value: 0.1);
            Node(w, "LONE", "IMPR", "Lone Rock", new Vec3(500, 0, 0), value: 1.0);
            fctm = new FactionState { Tag = "FCTM", PersonalityId = "factorum", RngSeed = 7 };
            foreach (string rival in new[] { "IGSL", "SPRT", "IMPR" }) fctm.Relations[rival] = -1.0;
            fctm.Drives[Drive.Rivalry] = 0.8;
            fctm.Drives[Drive.Security] = 0.6;
            return w;
        }

        private static Decision Think(FakeWorld w, FactionState s, Personality p, FactionTuning t)
        {
            return new FactionBrain().Think(s, p, w, new SeededRandom(1), t);
        }

        // ---- choosing where to go ------------------------------------------------------------

        [Fact]
        public void ChooseTarget_PicksTheRichestRegionWithEnoughNodes_AndStagesFromTheNearestOwnNode()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();

            TerrorCampaigns.Target target = TerrorCampaigns.ChooseTarget(s, w, t);

            Assert.True(target.Found);
            Assert.Equal(NodeRegion.Feature("Rich Belt"), target.Region);
            Assert.Equal("HOME2", target.StagingNodeId);       // 2900 km away, not HOME at 3000
            Assert.Equal(3, target.Nodes);
        }

        [Fact]
        public void ChooseTarget_RotatesAwayFromTheLastRegion_AndIgnoresRegionsTooSmallToCampaignOn()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();
            s.LastTerrorRegionKey = NodeRegion.Feature("Rich Belt").ToKey();

            TerrorCampaigns.Target target = TerrorCampaigns.ChooseTarget(s, w, t);

            // The lone rock is worth more per node than the poor belt but holds one node: a raid,
            // not a campaign. The poor belt is the only other region with three.
            Assert.Equal(NodeRegion.Feature("Poor Belt"), target.Region);
            Assert.Equal("HOME", target.StagingNodeId);
        }

        [Fact]
        public void ChooseTarget_IgnoresAlliesAndTheNeutralFaction()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();
            s.Relations["IGSL"] = 0.5;                            // not hostile: the rich belt is off the table
            foreach (string id in new[] { "P1", "P2", "P3" }) w.Graph.Nodes[id].OwnerFactionTag = NeutralFaction.Tag;

            Assert.False(TerrorCampaigns.ChooseTarget(s, w, t).Found);
        }

        // ---- the brain nominates it, on a cadence, only for the implacable ----------------------

        [Fact]
        public void AnImplacableFaction_NominatesATerrorCampaign_AndAnOrdinaryOneDoesNot()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();

            Decision d = Think(w, s, Implacable(), t);
            Candidate campaign = d.Scored.Find(c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetRegion != null);
            Assert.NotNull(campaign);
            Assert.Equal(NodeRegion.Feature("Rich Belt").ToKey(), campaign.Intent.TargetRegion);
            Assert.Equal("HOME2", campaign.Intent.TargetNodeId);

            FactionState plain; FakeWorld w2 = World(out plain);
            Decision d2 = Think(w2, plain, Implacable(implacable: false), t);
            Assert.DoesNotContain(d2.Scored, c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetRegion != null);
        }

        [Fact]
        public void TheCadence_OneAtATime_ThenTheCooldown()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();
            Assert.True(TerrorCampaigns.CanStart(s, t, w.UtcNow));

            Assert.True(TerrorCampaigns.TryStart(s, t, w.UtcNow, "HOME2", NodeRegion.Feature("Rich Belt")));
            Assert.False(TerrorCampaigns.CanStart(s, t, w.UtcNow));            // one active
            Assert.False(TerrorCampaigns.TryStart(s, t, w.UtcNow, "HOME", NodeRegion.Feature("Poor Belt")));
            Assert.DoesNotContain(Think(w, s, Implacable(), t).Scored,
                c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetRegion != null);

            // The focus expires with the duration; the cooldown still holds the next one back.
            DateTime later = w.UtcNow + t.TerrorCampaignDuration + TimeSpan.FromMinutes(1);
            CampaignFocusLifecycle.Maintain(s, later, id => true, t.CampaignCooldown);
            Assert.Null(TerrorCampaigns.Active(s));
            Assert.False(TerrorCampaigns.CanStart(s, t, later));
            Assert.True(TerrorCampaigns.CanStart(s, t, w.UtcNow + t.TerrorCampaignCooldown));
        }

        [Fact]
        public void TryStart_WritesTheFocusTheObjectiveAndTheClock()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();

            Assert.True(TerrorCampaigns.TryStart(s, t, w.UtcNow, "HOME2", NodeRegion.Feature("Rich Belt")));

            CampaignFocus focus = Assert.Single(s.CampaignFoci);
            Assert.Equal("terror:feature:Rich Belt", focus.Id);
            Assert.Equal("HOME2", focus.CenterNodeId);
            Assert.Equal(new HashSet<string> { "HOME2" }, focus.RegionMembers);   // nothing foreign for the defensive readers
            Assert.Equal(w.UtcNow + t.TerrorCampaignDuration, focus.ExpiresAtUtc);
            CampaignObjective o = Assert.Single(focus.Objectives);
            Assert.Equal(CampaignObjectiveKind.Terrorise, o.Kind);
            Assert.Equal(PlacementRegionKind.Feature, o.TargetRegionKind);
            Assert.Equal("Rich Belt", o.TargetRegionKey);
            Assert.Equal(t.TerrorCampaignHulls, o.WantedCombatHulls);
            Assert.Equal(w.UtcNow, s.LastTerrorCampaignUtc);
            Assert.Equal("feature:Rich Belt", s.LastTerrorRegionKey);
        }

        // ---- the campaign reaches its region by decision, and only ever raids ---------------------

        [Fact]
        public void WithACampaignActive_TheRegionIsARaidTargetWithoutInfluenceReach()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();
            // ZeroField: FCTM projects nothing anywhere, so without the campaign the belt's nodes are
            // nominated by nobody -- the reach gate names none of them.
            Decision before = Think(w, s, Implacable(), t);
            foreach (string beltNode in new[] { "R1", "R2", "R3" })
                Assert.Null(CampaignNomination.Raid(before, w, beltNode));

            Assert.True(TerrorCampaigns.TryStart(s, t, w.UtcNow, "HOME2", NodeRegion.Feature("Rich Belt")));
            Decision d = Think(w, s, Implacable(), t);

            // The campaign's own region is reachable by decision: Think ranks its nodes into the
            // objective's candidate list, which is what FleetManager dispatches through.
            CampaignObjective objective = TerrorCampaigns.Active(s);
            Assert.NotNull(objective);
            Assert.NotEmpty(objective.Candidates);
            foreach (string candidate in objective.Candidates)
                Assert.Contains(candidate, new[] { "R1", "R2", "R3" });
            Assert.Empty(CampaignNomination.AllConquests(d));
        }

        [Fact]
        public void AnImplacableFaction_NeverConquers_EvenAReachableBrokenNode()
        {
            FactionState s; FakeWorld w = World(out s);
            var t = TestSupport.Tuning();
            ((ZeroField)w.Influence).OverrideSample = 1.0;                   // full reach everywhere
            w.Graph.Nodes["LONE"].HoldStrength = 0.05;                        // broken: a conquest target for anyone else

            Decision implacable = Think(w, s, Implacable(), t);
            Assert.Empty(CampaignNomination.AllConquests(implacable));
            Assert.NotEmpty(CampaignNomination.AllRaids(implacable));   // it still raids

            FactionState plain; FakeWorld w2 = World(out plain);
            ((ZeroField)w2.Influence).OverrideSample = 1.0;
            w2.Graph.Nodes["LONE"].HoldStrength = 0.05;
            Assert.NotEmpty(CampaignNomination.AllConquests(Think(w2, plain, Implacable(implacable: false), t)));
        }

        // ---- the fleet requirement ------------------------------------------------------------

        [Fact]
        public void ACampaignRaisesTheCombatTarget_SoTheYardBuildsTowardIt()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode yard = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "YARD");
            yard.OwnerFactionTag = "F"; yard.HoldStrength = 1.0; yard.StableStationId = 1;
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            yard.ShipStock["Trade"] = 1;
            yard.ShipStock["Raid"] = 1;                                       // meets the posture-0 baseline of 1
            world.PrefabCatalogue = new RoleNamedCatalogue();
            var state = new FactionState { Tag = "F" };
            var t = world.Tuning;

            var quiet = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { state }, world, t, quiet);
            Assert.DoesNotContain(quiet, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid"));

            Assert.True(TerrorCampaigns.TryStart(state, t, world.UtcNow, "YARD", NodeRegion.Feature("Rich Belt")));
            var loud = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { state }, world, t, loud);
            Assert.Contains(loud, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid"));
        }

        /// <summary>Classifies stock by its literal role name, the way ShipyardManagerTests' own
        /// catalogue does: "Trade" is cargo-only, "Raid" is combat-only.</summary>
        private sealed class RoleNamedCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return prefabId == "Trade" ? 1000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => prefabId == "Raid" ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        // ---- it survives a restart --------------------------------------------------------------

        [Fact]
        public void ATerrorObjectiveAndTheCadenceClock_RoundTripThroughTheBrainFile()
        {
            var s = new FactionState { Tag = "FCTM", FactionId = 1, PersonalityId = "factorum", RngSeed = 3 };
            var t = TestSupport.Tuning();
            DateTime at = new DateTime(2026, 9, 5, 18, 0, 0, DateTimeKind.Utc);
            Assert.True(TerrorCampaigns.TryStart(s, t, at, "N036", NodeRegion.Body("Kor", StationSite.Orbital)));

            BrainDoc doc = FactionStore.ToBrainDoc(s, new SpatialNode[0], FactionStore.DefaultLogCap);
            FactionState restored = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            CampaignObjective o = TerrorCampaigns.Active(restored);
            Assert.NotNull(o);
            Assert.Equal(NodeRegion.Body("Kor", StationSite.Orbital), NodeRegion.FromObjective(o));
            Assert.Equal(t.TerrorCampaignHulls, o.WantedCombatHulls);
            Assert.Equal(at, restored.LastTerrorCampaignUtc);
            Assert.Equal("body:Kor:Orbital", restored.LastTerrorRegionKey);
            Assert.Equal("terror:body:Kor:Orbital", Assert.Single(restored.CampaignFoci).Id);
        }

        [Fact]
        public void RegionKeys_RoundTripEveryKind()
        {
            NodeRegion r;
            Assert.True(NodeRegion.TryFromKey(NodeRegion.Feature("Far Nest Umbra").ToKey(), out r));
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), r);
            Assert.True(NodeRegion.TryFromKey(NodeRegion.Body("72111577199174151", StationSite.Surface).ToKey(), out r));
            Assert.Equal(NodeRegion.Body("72111577199174151", StationSite.Surface), r);
            Assert.False(NodeRegion.TryFromKey(null, out r));
            Assert.False(NodeRegion.TryFromKey("nonsense", out r));
            Assert.Null(NodeRegion.None.ToKey());
        }
    }
}
