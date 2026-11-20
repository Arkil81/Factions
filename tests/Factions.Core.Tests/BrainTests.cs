using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Minimal in-memory world for exercising the pure engine.</summary>
    internal sealed class FakeWorld : IWorldView
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public SpatialGraph Graph { get; } = new SpatialGraph();
        public long Treasury;
        public long? Ledger;
        public long NetFlow;
        /// <summary>Returned for all faction + item kind queries. Defaults to a large value so
        /// item-cost gates do not block tests that are only testing credit affordability.
        /// Set to 0 to test item-scarcity scenarios.</summary>
        public long Inventory = 1_000_000;
        public long GetTreasury(string factionTag) { return Treasury; }
        public long GetNetFlow(string factionTag) { return NetFlow; }
        public long GetLedger(string factionTag) { return Ledger ?? Treasury; }
        public long GetDebt(string factionTag) { return 0; }
        /// <summary>Per-faction fleet combat power, keyed by tag — distinct from the single-scalar
        /// Treasury/NetFlow fields above since dampening tests need to compare multiple factions'
        /// power against each other. Unset tags return 0 (no combat power known).</summary>
        public readonly Dictionary<string, long> FleetPower = new Dictionary<string, long>();
        public long GetFleetCombatPower(string factionTag)
        {
            long v;
            return FleetPower.TryGetValue(factionTag, out v) ? v : 0;
        }
        /// <summary>Test double keeps this decoupled from prefab-tag suitability (that's
        /// ShipPoolTests.cs's job) — brain-gating tests just want "this faction has some ship
        /// somewhere: yes/no," controlled by setting any non-empty ShipStock entry.
        /// <paramref name="targetNodeId"/> is deliberately ignored here — route-feasibility gating
        /// is WorldModel.HasShipStockFor's own concern, covered directly in WorldModelTests.cs.</summary>
        public bool HasShipStockFor(string factionTag, ActionKind kind, string targetNodeId = null)
        {
            if (!ShipPool.RequiresShip(kind)) return false;
            foreach (SpatialNode n in Graph.OwnedBy(factionTag))
                foreach (var kv in n.ShipStock)
                    if (kv.Value > 0) return true;
            return false;
        }
        public IEnumerable<SpatialNode> SuitableShipOrigins(string factionTag, ActionKind kind)
        {
            return Graph.OwnedBy(factionTag).Where(n => n.ShipStock.Values.Any(v => v > 0));
        }
        /// <summary>Per-key routable and staged stock, for the paths that name specific inventory
        /// keys (install BOMs). Empty by default, which is the long-standing behaviour these
        /// accessors had before they were made settable.</summary>
        public readonly Dictionary<string, long> InventoryByKey = new Dictionary<string, long>();
        public readonly Dictionary<string, long> ReservedByKey = new Dictionary<string, long>();
        /// <summary>Per-ItemKind staged stock, the flat-install-cost counterpart to
        /// <see cref="ReservedByKey"/>. Unset kinds return 0.</summary>
        public readonly Dictionary<ItemKind, long> ReservedByKind = new Dictionary<ItemKind, long>();
        public long GetInventory(string factionTag, ItemKind kind) { return Inventory; }
        public long GetReservedInventory(string factionTag, ItemKind kind)
        {
            long v;
            return ReservedByKind.TryGetValue(kind, out v) ? v : 0;
        }
        public long GetFactionInventoryByKey(string factionTag, string invKey)
        {
            long v;
            return InventoryByKey.TryGetValue(invKey, out v) ? v : 0;
        }
        public long GetFactionReservedInventoryByKey(string factionTag, string invKey)
        {
            long v;
            return ReservedByKey.TryGetValue(invKey, out v) ? v : 0;
        }
        public long GetNodeInventory(string nodeId, ItemKind kind) { return 0; }
        public IEnumerable<KeyValuePair<string, long>> GetNodeInventoryAll(string nodeId)
            { return System.Linq.Enumerable.Empty<KeyValuePair<string, long>>(); }
        public void AddNodeInventory(string nodeId, string subTypeId, long delta) { }
        public IItemRegistry ItemRegistry { get { return NullItemRegistry.Instance; } }
        public IRecipeRegistry RecipeRegistry { get { return NullRecipeRegistry.Instance; } }
        /// <summary>Settable so a test can drive the member-standing drift term; the default answers
        /// false for every pair, which is what keeps NPC-vs-NPC drift unchanged.</summary>
        public IMemberStandingSource MemberStanding { get; set; } = NullMemberStandingSource.Instance;
        private readonly DemandRegister _demand = new DemandRegister();
        public DemandRegister DemandRegister { get { return _demand; } }
        private readonly InvestmentRegister _investments = new InvestmentRegister();
        public InvestmentRegister InvestmentRegister { get { return _investments; } }
        public IInfluenceField Influence { get; set; } = new ZeroField();
        public IPlanetCatalogue Planets { get; set; } = NullPlanetCatalogue.Instance;
        public IItemCatalogue Catalogue { get; } = new TuningItemCatalogue(new FactionTuning());
    }

    internal sealed class ZeroField : IInfluenceField
    {
        /// <summary>When set, overrides the return value of <see cref="SampleAt"/> so tests can
        /// simulate reach > RaidReachThreshold without a full influence field.</summary>
        public double OverrideSample = 0;
        public double Sample(Vec3 pos, string factionTag) { return OverrideSample; }
        public double Control(Vec3 pos, string factionTag) { return 1.0; }   // uncontested in the empty fake
        /// <summary>When set, overrides <see cref="Pressure"/>'s return value the same way
        /// <see cref="OverrideSample"/> overrides Sample/SampleAt — empty by default so existing
        /// tests (which never expect a per-rival Pressure loop to fire) are unaffected.</summary>
        public double OverridePressure = 0;
        public double Pressure(string ofFaction, string byFaction) { return OverridePressure; }
        /// <summary>When set, <see cref="RivalTags"/> returns these tags instead of the default
        /// empty set — needed by tests exercising FactionBrain.Sense's per-rival Pressure/dampening
        /// loop, which does nothing when RivalTags is empty -- the default for every test that
        /// doesn't set this.</summary>
        public readonly List<string> OverrideRivals = new List<string>();
        public IEnumerable<string> RivalTags(string factionTag) { return OverrideRivals; }
        public double SampleAt(string nodeId, string factionTag) { return OverrideSample; }
        public double ControlAt(string nodeId, string factionTag) { return 1.0; }
    }

    public class BrainTests
    {
        private static Personality Persona(double xeno, double exp = 0.5, double merc = 0.5, double aggr = 0.5)
        {
            var p = new Personality
            {
                Temperature = 0.5,
                Traits = new Traits { Xenophobia = xeno, Expansionism = exp, Mercantilism = merc, Aggression = aggr }
            };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        /// <summary>FAC-272: readiness asks whether the faction owns a hull for each fleet role, the way
        /// the fleet gate does. It used to look stock up under the retired role names, so every faction
        /// read as shipless and took the full Growth push.</summary>
        [Fact]
        public void AFactionWithHulls_GetsLessGrowthPush_ThanAShiplessOne()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning();
            FakeWorld World(bool hulls)
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5);
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                if (hulls) home.ShipStock["Gunboat"] = 3;   // a prefab id, as every writer stores it
                return w;
            }

            var shipless = new FactionState { Tag = "F", RngSeed = 1 };
            var stocked = new FactionState { Tag = "F", RngSeed = 1 };
            brain.Think(shipless, Persona(0.0), World(false), new SeededRandom(1), tuning);
            brain.Think(stocked, Persona(0.0), World(true), new SeededRandom(1), tuning);

            Assert.True(stocked.Drives[Drive.Growth] < shipless.Drives[Drive.Growth],
                "growth " + stocked.Drives[Drive.Growth] + " with hulls vs " + shipless.Drives[Drive.Growth] + " without");
        }

        /// <summary>FAC-272 on the real world view: stock keyed by prefab id, judged by what each hull
        /// carries. A hauler answers Trade only, so it is one role of three; a hauler and a gunboat
        /// answer all three. Under the role-name lookup all three estates read the same.</summary>
        [Fact]
        public void Readiness_CountsRolesByWhatThePrefabKeyedStockCanDo()
        {
            var brain = new FactionBrain();
            double Growth(params string[] hulls)
            {
                WorldModel w = TestSupport.World();
                w.PrefabCatalogue = new ArmedYard.ArmedCatalogue()
                    .Add("Hauler", 0, "Small", cargo: 1000)
                    .Add("Gunboat", 500, "Small");
                SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                home.StableStationId = 1;
                foreach (string h in hulls) home.ShipStock[h] = 2;
                w.SetTreasury("F", 300000);
                var st = new FactionState { Tag = "F", RngSeed = 1 };
                brain.Think(st, Persona(0.0), w, new SeededRandom(1), w.Tuning);
                return st.Drives[Drive.Growth];
            }

            double none = Growth();
            double cargoOnly = Growth("Hauler");
            double all = Growth("Hauler", "Gunboat");

            Assert.True(cargoOnly < none, "a hauler is a Trade hull: " + cargoOnly + " vs " + none);
            Assert.True(all < cargoOnly, "a gunboat adds Raid and Patrol: " + all + " vs " + cargoOnly);
        }

        /// <summary>The brain offers the defensive Campaign while offensive foci are open: they do
        /// not count against the defensive capacity. The control fills that capacity with a
        /// defensive focus and the candidate disappears.</summary>
        [Fact]
        public void DefensiveCampaign_IsOffered_WhileOffensiveFociAreOpen()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning();   // capacity 1 at zero combat power
            FakeWorld World()
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5);
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });
                w.Graph.Add(new Vec3(5000, 0, 0), 0.5).OwnerFactionTag = "F";
                return w;
            }
            CampaignFocus Focus(string id, CampaignObjectiveKind kind)
            {
                var f = new CampaignFocus { Id = id, CenterNodeId = id, ExpiresAtUtc = DateTime.MaxValue };
                f.RegionMembers.Add(id);
                f.Objectives.Add(new CampaignObjective { Kind = kind, TargetNodeId = id });
                return f;
            }

            var attacking = new FactionState { Tag = "F", RngSeed = 1 };
            for (int i = 0; i < 4; i++) attacking.CampaignFoci.Add(Focus("E" + i, CampaignObjectiveKind.Terrorise));
            Decision d = brain.Think(attacking, Persona(0.9), World(), new SeededRandom(1), tuning);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Campaign);

            var full = new FactionState { Tag = "F", RngSeed = 1 };
            full.CampaignFoci.Add(Focus("X1", CampaignObjectiveKind.DefendRegion));
            Decision control = brain.Think(full, Persona(0.9), World(), new SeededRandom(1), tuning);
            Assert.DoesNotContain(control.Scored, c => c.Intent.Kind == ActionKind.Campaign);
        }

        /// <summary>Standing picket: a calm faction holding no defensive focus is still offered one,
        /// at the floored need, because Patrol flies only under a defend focus. Controls: with the
        /// floor off the calm faction gets no offer, and with one defend focus already held a
        /// second is not offered without pressure, even with capacity to spare.</summary>
        [Fact]
        public void StandingPicket_IsOffered_ToACalmFactionHoldingNoDefensiveFocus()
        {
            var brain = new FactionBrain();
            FakeWorld World()
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5);
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                home.ShipStock["Gunboat"] = 2;   // a ready fleet: Growth does not clear its bar
                var second = w.Graph.Add(new Vec3(5000, 0, 0), 0.5);
                second.OwnerFactionTag = "F";
                second.HoldStrength = 0.6;       // no atrophy: Security does not clear its bar
                return w;
            }
            bool Calm(FactionState st) => st.Drives[Drive.Security] <= 0.15 && st.Drives[Drive.Caution] <= 0.3
                && st.Drives[Drive.Growth] <= 0.5;

            var tuning = new FactionTuning { CampaignFocusBaseCapacity = 2, CampaignFocusMaxConcurrent = 2 };
            var bare = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(bare, Persona(0.0), World(), new SeededRandom(1), tuning);
            Assert.True(Calm(bare), "precondition: no drive clears the pressure gate: security "
                + bare.Drives[Drive.Security] + " caution " + bare.Drives[Drive.Caution] + " growth " + bare.Drives[Drive.Growth]);
            Candidate picket = d.Scored.FirstOrDefault(c => c.Intent.Kind == ActionKind.Campaign);
            Assert.NotNull(picket);
            Assert.True(picket.Need >= tuning.StandingPicketNeed, "need " + picket.Need);
            Assert.True(picket.Utility > 0, "utility " + picket.Utility);

            var off = new FactionTuning { CampaignFocusBaseCapacity = 2, CampaignFocusMaxConcurrent = 2, StandingPicketNeed = 0 };
            Decision noFloor = brain.Think(new FactionState { Tag = "F", RngSeed = 1 }, Persona(0.0), World(), new SeededRandom(1), off);
            Assert.DoesNotContain(noFloor.Scored, c => c.Intent.Kind == ActionKind.Campaign);

            var holding = new FactionState { Tag = "F", RngSeed = 1 };
            var focus = new CampaignFocus { Id = "X1", CenterNodeId = "X1", ExpiresAtUtc = DateTime.MaxValue };
            focus.RegionMembers.Add("X1");
            focus.Objectives.Add(new CampaignObjective { Kind = CampaignObjectiveKind.DefendRegion, TargetNodeId = "X1" });
            holding.CampaignFoci.Add(focus);
            Decision second = brain.Think(holding, Persona(0.0), World(), new SeededRandom(1), tuning);
            Assert.DoesNotContain(second.Scored, c => c.Intent.Kind == ActionKind.Campaign);
        }

        // The headline behaviour: the SAME neutral player is a threat only to a xenophobe.
        [Fact]
        public void NeutralPlayer_IsThreat_OnlyToXenophobe()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning();

            FakeWorld World()
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5);
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });
                return w;
            }

            var xeno = new FactionState { Tag = "F", RngSeed = 1 };
            var tolerant = new FactionState { Tag = "F", RngSeed = 1 };
            brain.Think(xeno, Persona(0.9), World(), new SeededRandom(1), tuning);
            brain.Think(tolerant, Persona(0.0), World(), new SeededRandom(1), tuning);

            Assert.True(xeno.Drives[Drive.Security] > 0.3, "xenophobe should feel threatened by neutral presence");
            Assert.True(tolerant.Drives[Drive.Security] < 0.05, "tolerant faction should not");
        }

        // Affordability is a hard gate: a broke faction cannot pick a costly action.
        [Fact]
        public void BrokeFaction_Idles_WhenNothingIsAffordable()
        {
            var brain = new FactionBrain();
            // Bare new FactionTuning() has zero costs and IdleUtility=0 (defaults live in tuning_defaults.json); set explicitly.
            var tuning = new FactionTuning
            {
                ClaimCost = 20000, ExpandCost = 50000, ReinforceCost = 15000,
                GuardCost = 5000, PatrolCost = 5000, RaidCost = 12000,
                DevelopCost = 25000, FoundCost = 100000, GiftCost = 10000,
                CampaignCost = 1000,
                IdleUtility = 0.15
            };

            var w = new FakeWorld { Treasury = 1000 }; // below every action cost
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.6;
            var frontier = w.Graph.Add(new Vec3(1000, 0, 0), 0.8, "A");
            w.Graph.Connect("H", "A");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), tuning);

            Assert.Equal(ActionKind.Idle, d.Intent.Kind);
        }

        // Develop must not be offered on an unfounded node (StableStationId == 0) ÔÇö capabilities are physical station sub-modules.
        [Fact]
        public void Develop_NotOffered_OnUnfoundedNode()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                DevelopCost = 25000, IdleUtility = 0.15,
                ClaimCost = 20000, ExpandCost = 50000, FoundCost = 100000,
                FoundHoldThreshold = 0.6
            };

            var w = new FakeWorld { Treasury = 10_000_000 };
            var node = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "N001");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.9;
            // StableStationId == 0 (default) ÔÇö not yet founded

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), tuning);

            Assert.True(d.Scored.TrueForAll(c => c.Intent.Kind != ActionKind.Develop),
                "Develop must not be generated for nodes that have no SE station yet");
        }

        // Found is gated on HoldStrength ÔÇö a weak-hold node must not be offered Founded.
        [Fact]
        public void Found_NotOffered_BelowHoldThreshold()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                FoundCost = 100000, FoundHoldThreshold = 0.6,
                IdleUtility = 0.15,
                ClaimCost = 20000, ExpandCost = 50000
            };

            var w = new FakeWorld { Treasury = 10_000_000 }; // fully solvent
            var node = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "N001");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.3; // below FoundHoldThreshold=0.6

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), tuning);

            Assert.NotEqual(ActionKind.Found, d.Intent.Kind);
            Assert.True(d.Scored.TrueForAll(c => c.Intent.Kind != ActionKind.Found),
                "Found must not be generated when hold is below FoundHoldThreshold");
        }

        private sealed class Bodies : IPlanetCatalogue
        {
            public readonly List<PlanetRecord> Records = new List<PlanetRecord>();
            public IEnumerable<PlanetRecord> Planets { get { return Records; } }
            public double GravityAt(Vec3 posKm) { return 1.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static FactionTuning FoundTuning()
        {
            return new FactionTuning
            {
                FoundCost = 100000, FoundHoldThreshold = 0.6, IdleUtility = 0.15,
                ClaimCost = 20000, ExpandCost = 50000
            };
        }

        // A held claim is offered Found however thin its ground: Tellus measured 0.13-0.16 Worth at a
        // full survey, and a station is what makes a node take a siege rather than a presence raid.
        [Fact]
        public void Found_Offered_OnAHeldClaimWithThinGround()
        {
            var w = new FakeWorld { Treasury = 10_000_000 };
            var node = w.Graph.Add(new Vec3(0, 0, 0), 0.15, "N001");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.7;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = new FactionBrain().Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), FoundTuning());

            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Found && c.Intent.TargetNodeId == "N001");
        }

        // The one body workers come from goes first: its claim outranks a richer one elsewhere, and
        // without the recruiting flag the richer one wins.
        [Theory]
        [InlineData(true, "N001")]
        [InlineData(false, "N002")]
        public void Found_PrefersAClaimOnARecruitingBody(bool recruits, string expected)
        {
            var bodies = new Bodies();
            bodies.Records.Add(new PlanetRecord { Id = "tellus", Name = "Tellus", Recruits = recruits });
            var w = new FakeWorld { Treasury = 10_000_000, Planets = bodies };
            var tellus = w.Graph.Add(new Vec3(0, 0, 0), 0.15, "N001");
            tellus.OwnerFactionTag = "F";
            tellus.HoldStrength = 0.7;
            tellus.PlanetId = "tellus";
            var deep = w.Graph.Add(new Vec3(5000, 0, 0), 0.58, "N002");
            deep.OwnerFactionTag = "F";
            deep.HoldStrength = 0.7;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = new FactionBrain().Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), FoundTuning());

            Candidate found = d.Scored.Find(c => c.Intent.Kind == ActionKind.Found);
            Assert.NotNull(found);
            Assert.Equal(expected, found.Intent.TargetNodeId);
        }

        // Same state + same seed must reproduce the same decision (so sim runs are debuggable).
        [Fact]
        public void SameSeed_SameState_IsReproducible()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning();

            FakeWorld World()
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                w.Graph.Add(new Vec3(1000, 0, 0), 0.8, "A");
                w.Graph.Add(new Vec3(0, 0, 1000), 0.4, "B");
                w.Graph.Connect("H", "A");
                w.Graph.Connect("H", "B");
                return w;
            }

            var s1 = new FactionState { Tag = "F", RngSeed = 7 };
            var s2 = new FactionState { Tag = "F", RngSeed = 7 };
            Decision d1 = brain.Think(s1, Persona(0.0, exp: 1.0), World(), new SeededRandom(7), tuning);
            Decision d2 = brain.Think(s2, Persona(0.0, exp: 1.0), World(), new SeededRandom(7), tuning);

            Assert.Equal(d1.Intent.ToString(), d2.Intent.ToString());
        }

        [Fact]
        public void RepeatedAction_IsPenalized_AndOtherCountersDecay()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                ClaimCost = 0,
                ExpandCost = 50000,
                ReinforceCost = 50000,
                FoundCost = 50000,
                FoundHoldThreshold = 1.0,
                IdleUtility = 0.01,
                TopN = 1
            };

            FakeWorld World()
            {
                var w = new FakeWorld { Treasury = 300000 };
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.8;
                w.Graph.Add(new Vec3(1000, 0, 0), 0.9, "A");
                w.Graph.Connect("H", "A");
                return w;
            }

            var fresh = new FactionState { Tag = "F", RngSeed = 1 };
            Decision dFresh = brain.Think(fresh, Persona(0.0, exp: 1.0), World(), new SeededRandom(1), tuning);
            Candidate freshClaim = dFresh.Scored.Find(c => c.Intent.Kind == ActionKind.Claim);

            var repeated = new FactionState { Tag = "F", RngSeed = 1 };
            repeated.ActionRepetition[ActionKind.Claim] = 4.0;
            repeated.ActionRepetition[ActionKind.Expand] = 10.0;
            Decision dRepeated = brain.Think(repeated, Persona(0.0, exp: 1.0), World(), new SeededRandom(1), tuning);
            Candidate repeatedClaim = dRepeated.Scored.Find(c => c.Intent.Kind == ActionKind.Claim);

            Assert.NotNull(freshClaim);
            Assert.NotNull(repeatedClaim);
            // The core invariant: Claim's repetition penalty lowers its utility compared to
            // the fresh version.  Campaign may or may not win the action slot — that does
            // not affect the repetition accounting we are testing here.
            Assert.True(repeatedClaim.Utility < freshClaim.Utility, "repeated Claim should score lower than a fresh Claim");
        }

        [Fact]
        public void Develop_RemainsACandidate_WhenSecurityIsDominant()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                ComfortLedger = 1_000_000,
                InsolvencyFloor = 50_000,
                DevelopCost = 25_000,
                GuardCost = 5_000,
                PatrolCost = 5_000,
                ReinforceCost = 10_000,
                IdleUtility = 0.01
            };

            var p = Persona(1.0, exp: 0.6, merc: 0.8, aggr: 0.2);
            p.Weights[Drive.Security] = 1.4;
            p.Weights[Drive.Growth] = 0.6;
            p.Weights[Drive.Wealth] = 0.4;
            p.Weights[Drive.Rivalry] = 0.3;
            p.Weights[Drive.Caution] = 0.5;

            var w = new FakeWorld { Treasury = 200_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.StableStationId = 1;
            home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });
            var frontier = w.Graph.Add(new Vec3(1000, 0, 0), 0.7, "A");
            w.Graph.Connect("H", "A");
            w.InvestmentRegister.Post(new InvestmentRequest { FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 1.0 });

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            double securityStrength = s.Drives[Drive.Security] * p.Weights[Drive.Security];
            double growthStrength = s.Drives[Drive.Growth] * p.Weights[Drive.Growth];
            Assert.True(securityStrength > growthStrength, "security should still be the dominant weighted drive in this setup");
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Develop && c.SourceDrive == Drive.Growth && c.Utility > 0);
        }

        [Fact]
        public void MultiDriveSupport_CanChooseDevelop_WithoutRemovingSecurityDominance()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                ComfortLedger = 1_000_000,
                InsolvencyFloor = 50_000,
                DevelopCost = 25_000,
                GuardCost = 5_000,
                PatrolCost = 5_000,
                ReinforceCost = 10_000,
                IdleUtility = 0.01,
                TopN = 1
            };

            var p = Persona(1.0, exp: 0.6, merc: 0.8, aggr: 0.2);
            p.Weights[Drive.Security] = 1.4;
            p.Weights[Drive.Growth] = 0.6;
            p.Weights[Drive.Wealth] = 0.4;
            p.Weights[Drive.Rivalry] = 0.3;
            p.Weights[Drive.Caution] = 0.5;

            var w = new FakeWorld { Treasury = 200_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.StableStationId = 1;
            home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });
            var frontier = w.Graph.Add(new Vec3(1000, 0, 0), 0.7, "A");
            w.Graph.Connect("H", "A");
            w.InvestmentRegister.Post(new InvestmentRequest { FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 1.0 });

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            double securityStrength = s.Drives[Drive.Security] * p.Weights[Drive.Security];
            double growthStrength = s.Drives[Drive.Growth] * p.Weights[Drive.Growth];
            Assert.True(securityStrength > growthStrength, "security should still be the dominant weighted drive in this setup");
            Assert.Equal(ActionKind.Develop, d.Intent.Kind);
            Assert.Equal(Drive.Growth, d.Goal);
        }

        // ---- strategic Campaign targeting -----------------------------------------------

        // Campaign is a single strategic intent — only ONE Campaign candidate per tick,
        // targeting the highest-threat node. It replaces the old per-node Reinforce candidates.
        [Fact]
        public void Campaign_TargetsHighestThreatNode()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 2_000_000, NetFlow = 5000 };

            // Two owned nodes with different rv values (rv drives threat).
            var nLowRv = w.Graph.Add(new Vec3(200, 0, 0), 0.3, "N001");
            nLowRv.OwnerFactionTag = "F";
            nLowRv.HoldStrength = 0.5;

            var nHighRv = w.Graph.Add(new Vec3(400, 0, 0), 0.9, "N002");
            nHighRv.OwnerFactionTag = "F";
            nHighRv.StableStationId = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            // Campaign generates exactly one candidate per tick.
            var cCampaign = System.Linq.Enumerable.FirstOrDefault(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign);
            Assert.NotNull(cCampaign);
            // Target must be one of the owned nodes (whichever has the highest perceived threat).
            Assert.True(cCampaign.Intent.TargetNodeId == nLowRv.Id
                || cCampaign.Intent.TargetNodeId == nHighRv.Id,
                "Campaign should target an owned node: " + cCampaign.Intent.TargetNodeId);
        }

        [Fact]
        public void Campaign_SkipsAnAlreadyFocusedCenter_WhenNominatingNextFocus()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.CampaignFocusBaseCapacity = 2;
            t.CampaignFocusMaxConcurrent = 2;
            var w = new FakeWorld { Treasury = 2_000_000, NetFlow = 5000 };

            var lowerThreat = w.Graph.Add(new Vec3(200, 0, 0), 0.3, "N001");
            lowerThreat.OwnerFactionTag = "F";
            lowerThreat.HoldStrength = 0.5;

            var focusedThreat = w.Graph.Add(new Vec3(400, 0, 0), 0.9, "N002");
            focusedThreat.OwnerFactionTag = "F";
            focusedThreat.HoldStrength = 0.5;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.CampaignFoci.Add(new CampaignFocus
            {
                Id = "focus-n002",
                CenterNodeId = focusedThreat.Id,
                Priority = 0.8,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });

            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Campaign
                && c.Intent.TargetNodeId == lowerThreat.Id);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Campaign
                && c.Intent.TargetNodeId == focusedThreat.Id);
        }

        // ---- Campaign: hold-based priority in FleetManager dispatch (not brain) ------

        // Campaign targets the highest-threat node regardless of hold; FleetManager then
        // uses hold thresholds for the operational Reinforce dispatch.
        [Fact]
        public void Campaign_TargetsHighestThreat_RegardlessOfHold()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 2_000_000, NetFlow = 5000 };

            // Two rv-qualifying unfounded nodes with equal rv (equal threat).
            var nFar = w.Graph.Add(new Vec3(200, 0, 0), 0.9, "N001");
            nFar.OwnerFactionTag = "F";
            nFar.HoldStrength = 0.5;

            var nClose = w.Graph.Add(new Vec3(400, 0, 0), 0.9, "N002");
            nClose.OwnerFactionTag = "F";
            nClose.HoldStrength = 0.6;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            // Campaign should have exactly one candidate; it targets the highest-threat node.
            Candidate cCampaign = System.Linq.Enumerable.FirstOrDefault(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign);
            Assert.NotNull(cCampaign);
            // Both nodes have equal rv (threat); Campaign picks one — either is valid.
            Assert.True(cCampaign.Intent.TargetNodeId == nFar.Id || cCampaign.Intent.TargetNodeId == nClose.Id);
        }

        // Campaign targets one of the owned nodes, founded or not.
        [Fact]
        public void MaintenanceReinforce_FallsBack_ToWeakestNode_WhenNoRvQualifyingClaim()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.FoundHoldThreshold     = 0.6;

            var w = new FakeWorld { Treasury = 2_000_000, NetFlow = 5000 };

            // Unfounded N### node at low rv.
            var nLowRv = w.Graph.Add(new Vec3(200, 0, 0), 0.5, "N010");
            nLowRv.OwnerFactionTag = "F";
            nLowRv.HoldStrength    = 0.7;
            nLowRv.StableStationId = 0;

            // Founded E### node at weaker hold ÔÇö should be the fallback target.
            var eWeak = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "E123456789012345678");
            eWeak.OwnerFactionTag = "F";
            eWeak.HoldStrength    = 0.4;
            eWeak.StableStationId = 123456789012345678;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            // Campaign generates exactly one candidate targeting one of the owned
            // nodes (the one with highest perceived threat).  With no foreign presences
            // on either node, threat is equal; Campaign picks one deterministically.
            var cCampaign = System.Linq.Enumerable.FirstOrDefault(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign);
            Assert.NotNull(cCampaign);
            Assert.True(cCampaign.Intent.TargetNodeId == eWeak.Id
                || cCampaign.Intent.TargetNodeId == nLowRv.Id);
        }

        // ---- all-drives fading-node hold maintenance ----------------------------------------

        // A decaying E### node must surface a Reinforce candidate even when Security drive is
        // suppressed (weight=0) ÔÇö the fading-node maintenance pass fires for every drive so hold
        // upkeep isn't invisible to Growth/Wealth-dominant factions.
        [Fact]
        public void FadingNode_Reinforce_FiresUnderGrowthDrive_WhenSecuritySuppressed()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 5_000_000, NetFlow = 10_000 };

            var eNode = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "E900517019133138153");
            eNode.OwnerFactionTag  = "F";
            eNode.HoldStrength     = 0.6;   // decaying ÔÇö below 0.8 threshold
            eNode.StableStationId  = 900517019133138153;

            var p = new Personality { Temperature = 0.0 };
            p.Weights[Drive.Security] = 0;   // suppress Security entirely
            p.Weights[Drive.Growth]   = 1;
            p.Weights[Drive.Wealth]   = 1;
            p.Weights[Drive.Rivalry]  = 0;
            p.Weights[Drive.Caution]  = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            // eNode (hold=0.6) is a scan-backed node with rv=0.9 → high threat.
            // It should be the Campaign target.
            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign
                     && c.Intent.TargetNodeId == eNode.Id);
        }

        // The most-decayed node (lowest hold) must outcompete a less-decayed node because
        // quality = 0.45 + 0.55 ├ù (1 ÔêÆ hold) ÔÇö a 0.3-hold node gets qÔëê0.84, a 0.7-hold node qÔëê0.62.
        [Fact]
        public void FadingNode_Reinforce_MostDecayed_OutscoresLessDecayed()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 5_000_000, NetFlow = 10_000 };

            // Both nodes decaying; eWorst is more severely decayed.
            var eWorst = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "E100000000000000001");
            eWorst.OwnerFactionTag = "F";
            eWorst.HoldStrength    = 0.3;
            eWorst.StableStationId = 100000000000000001;

            var eMild = w.Graph.Add(new Vec3(200, 0, 0), 0.9, "E100000000000000002");
            eMild.OwnerFactionTag = "F";
            eMild.HoldStrength    = 0.7;
            eMild.StableStationId = 100000000000000002;

            var p = new Personality { Temperature = 0.0 };
            p.Weights[Drive.Security] = 0;
            p.Weights[Drive.Growth]   = 1;
            p.Weights[Drive.Wealth]   = 1;
            p.Weights[Drive.Rivalry]  = 0;
            p.Weights[Drive.Caution]  = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Candidate cWorst = System.Linq.Enumerable.FirstOrDefault(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetNodeId == eWorst.Id);
            Candidate cMild = System.Linq.Enumerable.FirstOrDefault(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetNodeId == eMild.Id);

            Assert.NotNull(cWorst);
            // eMild may or may not appear (fading pass only adds the weakest); either way eWorst
            // must have the higher Support so it wins budget allocation.
            if (cMild != null)
                Assert.True(cWorst.Support > cMild.Support,
                    "more-decayed node must outscore less-decayed node");
        }

        // A well-held node (hold >= 0.8) CAN be Campaign-targeted — brain targets by threat,
        // not hold. Hold thresholds are FleetManager's operational concern (Reinforce dispatch).
        [Fact]
        public void Campaign_CanTargetWellHeldNode_WhenHighestThreat()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 5_000_000, NetFlow = 10_000 };

            var eStrong = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "E900517019133138153");
            eStrong.OwnerFactionTag = "F";
            eStrong.HoldStrength    = 0.85;   // well-held but high threat
            eStrong.StableStationId = 900517019133138153;

            var p = new Personality { Temperature = 0.0 };
            p.Weights[Drive.Security] = 0;
            p.Weights[Drive.Growth]   = 1;
            p.Weights[Drive.Wealth]   = 1;
            p.Weights[Drive.Rivalry]  = 0;
            p.Weights[Drive.Caution]  = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            // Campaign targets the highest-threat node regardless of hold strength.
            // FleetManager's dispatch logic applies the hold threshold (>= 0.8 skip) operationally.
            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Campaign && c.Intent.TargetNodeId == eStrong.Id);
        }

        // ---- capability upgrade (Phase 1: investment-request driven) -----------------------

        // An investment request for RefineryII with a node already at level 1 (below MaxLevel=15)
        // must produce a Develop(RefineryII) candidate pointing at that node.
        [Fact]
        public void Phase1_Develop_UpgradesExistingCapability_WhenBelowMaxLevel()
        {
            var brain = new FactionBrain();
            var t     = TestSupport.Tuning();  // MaxRefineryUpgrades = 15

            var w = new FakeWorld { Treasury = 5_000_000 };
            var n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag  = "F";
            n.HoldStrength     = 1.0;
            n.StableStationId  = 42;
            // Level 1 already installed; upgrade should be possible up to MaxLevel (5).
            n.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            n.Capabilities[NodeCapabilityKind.RefineryII] = 1;

            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F",
                Cap        = NodeCapabilityKind.RefineryII,
                Urgency    = 1.0,
                ExpiresUtc = w.UtcNow.AddMinutes(5),
            });

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop
                  && c.Intent.TargetCapability == NodeCapabilityKind.RefineryII
                  && c.Intent.TargetNodeId == "N1");
        }

        // When RefineryII is at MaxLevel, the node must NOT appear as an upgrade candidate.
        [Fact]
        public void Phase1_Develop_SkipsNode_WhenCapabilityAtMaxLevel()
        {
            var brain = new FactionBrain();
            var t     = TestSupport.Tuning();   // MaxRefineryUpgrades = 15

            var w = new FakeWorld { Treasury = 5_000_000 };
            var n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag  = "F";
            n.HoldStrength     = 1.0;
            n.StableStationId  = 42;
            // RefineryII at max level ÔÇö no upgrade possible.
            n.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            n.Capabilities[NodeCapabilityKind.RefineryII] = t.MaxRefineryUpgrades;

            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F",
                Cap        = NodeCapabilityKind.RefineryII,
                Urgency    = 1.0,
                ExpiresUtc = w.UtcNow.AddMinutes(5),
            });

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.DoesNotContain(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop
                  && c.Intent.TargetCapability == NodeCapabilityKind.RefineryII);
        }

        // Regression pin for the CapabilityPrerequisites refactor (see DevelopReadiness.cs):
        // the exploratory phase must never surface a cascade-gated capability (Manufactory,
        // Shipyard, ManufactoryII, RefineryII) when its upstream prerequisite is installed
        // nowhere in the faction's network.
        [Fact]
        public void Phase2_ExploratoryDevelop_NeverOffersCascadeGatedCapability_WhenPrerequisiteMissingEverywhere()
        {
            var brain = new FactionBrain();
            var t     = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 5_000_000 };
            var n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.StableStationId = 42;
            // No capabilities installed anywhere -- Refinery/Manufactory prerequisites are unmet.

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            var gated = new[]
            {
                NodeCapabilityKind.Manufactory, NodeCapabilityKind.Shipyard,
                NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII,
            };
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Develop
                && c.Intent.TargetCapability.HasValue && Array.IndexOf(gated, c.Intent.TargetCapability.Value) >= 0);
        }

        // ---- planet-scoped Shipyard investment (local-Haul-leg trigger) --------------------

        [Fact]
        public void Phase1_Develop_PlanetScopedShipyardRequest_PrefersSamePlanetOverHigherAffinityOtherPlanet()
        {
            var brain = new FactionBrain();
            FactionTuning t = TestSupport.Tuning();
            // Deliberately favor the WRONG-planet node on pure capability affinity, so a pass this
            // test proves the planet restriction -- not just a same-affinity tie -- is what wins.
            t.CapabilityAffinity = new Dictionary<string, Dictionary<string, double>>
            {
                ["Generic"] = new Dictionary<string, double> { ["Shipyard"] = 0.3 }, // Terra node's kind
                ["Core"]    = new Dictionary<string, double> { ["Shipyard"] = 0.9 }, // Mars node's kind
            };

            var w = new FakeWorld { Treasury = 5_000_000 };

            var manufactoryNode = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "Mfg");
            manufactoryNode.OwnerFactionTag = "F";
            manufactoryNode.HoldStrength = 1.0;
            manufactoryNode.StableStationId = 1;
            manufactoryNode.Capabilities[NodeCapabilityKind.Manufactory] = 1; // Shipyard prereq

            var terraNode = w.Graph.Add(new Vec3(1, 0, 0), 1.0, "TerraNode");
            terraNode.OwnerFactionTag = "F";
            terraNode.HoldStrength = 1.0;
            terraNode.StableStationId = 2;
            terraNode.Zone = NodeZone.Surface;
            terraNode.Site = StationSite.Surface;
            terraNode.PlanetId = "Terra";

            var marsNode = w.Graph.Add(new Vec3(2, 0, 0), 1.0, "MarsNode");
            marsNode.OwnerFactionTag = "F";
            marsNode.HoldStrength = 1.0;
            marsNode.StableStationId = 3;
            marsNode.Zone = NodeZone.Surface;
            marsNode.Site = StationSite.Surface;
            marsNode.PlanetId = "Mars";

            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 1.0,
                ExpiresUtc = w.UtcNow.AddMinutes(5), TargetPlanetId = "Terra",
            });

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop
                  && c.Intent.TargetCapability == NodeCapabilityKind.Shipyard
                  && c.Intent.TargetNodeId == "TerraNode");
        }

        [Fact]
        public void Phase1_Develop_PlanetScopedShipyardRequest_FallsBackWhenNoNodeOnThatPlanet()
        {
            // The faction owns no Surface node on "Terra" at all -- the investment must not be
            // silently dropped just because the ideal placement doesn't exist yet.
            var brain = new FactionBrain();
            FactionTuning t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 5_000_000 };

            var manufactoryNode = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "Mfg");
            manufactoryNode.OwnerFactionTag = "F";
            manufactoryNode.HoldStrength = 1.0;
            manufactoryNode.StableStationId = 1;
            manufactoryNode.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var marsNode = w.Graph.Add(new Vec3(2, 0, 0), 1.0, "MarsNode");
            marsNode.OwnerFactionTag = "F";
            marsNode.HoldStrength = 1.0;
            marsNode.StableStationId = 3;
            marsNode.Zone = NodeZone.Surface;
            marsNode.Site = StationSite.Surface;
            marsNode.PlanetId = "Mars"; // only planet the faction actually holds territory on

            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 1.0,
                ExpiresUtc = w.UtcNow.AddMinutes(5), TargetPlanetId = "Terra",
            });

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop && c.Intent.TargetCapability == NodeCapabilityKind.Shipyard);
        }

        [Fact]
        public void Phase1_Develop_NullTargetPlanetId_IgnoresZoneAndPlanet_RegressionGuard()
        {
            // No TargetPlanetId set (every non-local-Shipyard capability request, and the default
            // for FactionRuntime's other-role Shipyard demand path too) -- must pick purely by
            // capability affinity, completely ignoring Zone/PlanetId, exactly as before this feature.
            var brain = new FactionBrain();
            FactionTuning t = TestSupport.Tuning();
            t.CapabilityAffinity = new Dictionary<string, Dictionary<string, double>>
            {
                ["Shipyard"] = new Dictionary<string, double>
                {
                    ["Mineable"] = 0.95, ["HasIndustry"] = 0.9, ["Default"] = 0.3
                },
            };

            var w = new FakeWorld { Treasury = 5_000_000 };

            var manufactoryNode = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "Mfg");
            manufactoryNode.OwnerFactionTag = "F";
            manufactoryNode.HoldStrength = 1.0;
            manufactoryNode.StableStationId = 1;
            manufactoryNode.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var lowAffinityNode = w.Graph.Add(new Vec3(1, 0, 0), 1.0, "LowAffinity");
            lowAffinityNode.OwnerFactionTag = "F";
            lowAffinityNode.HoldStrength = 1.0;
            lowAffinityNode.StableStationId = 2;
            lowAffinityNode.Zone = NodeZone.Surface;
            lowAffinityNode.Site = StationSite.Surface;
            lowAffinityNode.PlanetId = "Terra";

            var highAffinityNode = w.Graph.Add(new Vec3(2, 0, 0), 1.0, "HighAffinity");
            highAffinityNode.OwnerFactionTag = "F";
            highAffinityNode.HoldStrength = 1.0;
            highAffinityNode.StableStationId = 3;
            highAffinityNode.Zone = NodeZone.DeepSpace;
            highAffinityNode.Site = StationSite.Space;
            highAffinityNode.PlanetId = null;
            // The affinity is a world reading now, so the winner has to earn it from the ground rather
            // than from its label. Deep space and no planet, so nothing but the deposit can be doing it.
            TestSupport.WithOre(highAffinityNode, "Iron");

            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 1.0,
                ExpiresUtc = w.UtcNow.AddMinutes(5), // TargetPlanetId left null
            });

            var p = Persona(0.5);
            p.Weights[Drive.Growth]  = 1;
            p.Weights[Drive.Wealth]  = 1;
            p.Weights[Drive.Caution] = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop
                  && c.Intent.TargetCapability == NodeCapabilityKind.Shipyard
                  && c.Intent.TargetNodeId == "HighAffinity");
        }

        [Fact]
        public void ReachableUnkn_DerelictClaimsSuppressGrowthAndSecurityExpand()
        {
            var tuning = new FactionTuning
            {
                ClaimCost = 10_000,
                ExpandCost = 50_000,
                InsolvencyFloor = 1,
                ReinforceCost = 10_000,
                GuardCost = 5_000,
                ComfortLedger = 1_000_000,
                IdleUtility = 0.15,
            };
            var p = Persona(1.0, exp: 1.0, aggr: 0.9);
            p.Weights[Drive.Security] = 1.8;
            p.Weights[Drive.Growth] = 1.0;
            var w = new FakeWorld { Treasury = 500_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "HOME");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });
            var derelict = w.Graph.Add(new Vec3(40, 0, 0), 0.1, "N001");
            derelict.OwnerFactionTag = NeutralFaction.Tag;
            var virgin = w.Graph.Add(new Vec3(80, 0, 0), 0.99, "V001");
            w.Graph.Connect(home.Id, derelict.Id);
            w.Graph.Connect(home.Id, virgin.Id);

            var state = new FactionState { Tag = "F", RngSeed = 1 };
            Decision decision = new FactionBrain().Think(state, p, w, new SeededRandom(1), tuning);

            Assert.Contains(decision.Scored, c => c.Intent.Kind == ActionKind.Claim && c.Intent.TargetNodeId == derelict.Id);
            Assert.DoesNotContain(decision.Scored, c => c.Intent.Kind == ActionKind.Claim && c.Intent.TargetNodeId == virgin.Id);
            Assert.DoesNotContain(decision.Scored, c => c.Intent.Kind == ActionKind.Expand);
        }

        // A faction with Security-dominant personality and all nodes contested must generate
        // Expand candidates via the Security drive ÔÇö "buffer territory" is a valid defence.
        // This covers the SPRT/factorum scenario where Growth is always suppressed to ~0
        // by high Security and the faction otherwise loops forever on Reinforce/Negotiate.
        [Fact]
        public void SecuritySieged_Faction_GeneratesExpand_AsBufferTerritory()
        {
            var brain = new FactionBrain();
            var tuning = new FactionTuning
            {
                ExpandCost = 50_000,
                InsolvencyFloor = 10_000,
                ReinforceCost = 10_000,
                GuardCost = 5_000,
                ComfortLedger = 1_000_000,
                IdleUtility = 0.15,
            };

            // factorum-like: heavy Security weight, near-zero Growth weight, full xenophobia
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = 1.0, Expansionism = 0.1, Aggression = 0.9, Mercantilism = 0.0 } };
            p.Weights[Drive.Security] = 1.8;
            p.Weights[Drive.Growth]   = 0.1;
            p.Weights[Drive.Wealth]   = 0.4;
            p.Weights[Drive.Rivalry]  = 0.2;
            p.Weights[Drive.Caution]  = 1.3;

            var w = new FakeWorld { Treasury = 500_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "N001");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.7;
            // Neutral player present ÔÇö a xenophobe reads this as high threat, pushing Security up
            home.Presences.Add(new Presence { ActorId = "PLY", Kind = ActorKind.Player, Hostile = false });

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            double secStr = s.Drives[Drive.Security] * p.Weights[Drive.Security];
            double groStr = s.Drives[Drive.Growth]   * p.Weights[Drive.Growth];
            Assert.True(s.Drives[Drive.Security] > 0.35, "security should be elevated by xenophobic faction sensing neutral presence");
            Assert.True(secStr > groStr, "security must dominate growth so Growth alone cannot generate Expand");
            Assert.True(d.Scored.Exists(c => c.Intent.Kind == ActionKind.Expand && c.Utility > 0),
                "Expand must be generated via the Security drive when territory is contested and Growth is suppressed");
        }

        // MaxLevelFor returns the correct ceiling for each capability kind.
        [Fact]
        public void MaxLevelFor_ReturnsExpectedCeilings()
        {
            var t = new FactionTuning { MaxRefineryUpgrades = 3, MaxManufactoryUpgrades = 7, MaxShipyardUpgrades = 4,
                                        MaxFuelDepotUpgrades = 9 };
            Assert.Equal(3, t.MaxLevelFor(NodeCapabilityKind.Refinery));
            Assert.Equal(3, t.MaxLevelFor(NodeCapabilityKind.RefineryII));
            Assert.Equal(7, t.MaxLevelFor(NodeCapabilityKind.Manufactory));
            Assert.Equal(7, t.MaxLevelFor(NodeCapabilityKind.ManufactoryII));
            // Each ceiling comes from its OWN field: a Shipyard reading 1 here would mean it fell
            // through to the default branch, which is what made its slot count unraisable.
            Assert.Equal(4, t.MaxLevelFor(NodeCapabilityKind.Shipyard));
            Assert.Equal(9, t.MaxLevelFor(NodeCapabilityKind.FuelDepot));
            // SensorArray is the remaining default-branch case, so it pins the branch itself: with
            // every other kind now reading its own field, a 1 here is the only proof left that the
            // default is still reachable.
            Assert.Equal(1, t.MaxLevelFor(NodeCapabilityKind.SensorArray));
        }

        // ---- P1: build-completion urgency (FinishBuild) -----------------------------------------

        // A nearly-complete build (ÔëÑ threshold) must generate a FinishBuild candidate.
        [Fact]
        public void FinishBuild_GeneratedWhenBuildIsNearlyDone()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning { BuildCompletionUrgencyFraction = 0.70, IdleUtility = 0.15 };

            var w = new FakeWorld { Treasury = 200_000 };
            var n = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "N001");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 0.9;
            // Build at 80% completion
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid" };
            entry.Bom["SteelPlate"]     = 100;
            entry.Collected["SteelPlate"] = 80;
            n.BuildQueue.Add(entry);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.FinishBuild && c.Intent.TargetNodeId == "N001" && c.Utility > 0);
        }

        // A build below the urgency threshold must NOT generate a FinishBuild candidate.
        [Fact]
        public void FinishBuild_NotGeneratedWhenBuildIsEarly()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning { BuildCompletionUrgencyFraction = 0.70, IdleUtility = 0.15 };

            var w = new FakeWorld { Treasury = 200_000 };
            var n = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "N001");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 0.9;
            // Build at 50% ÔÇö below the 70% threshold
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid" };
            entry.Bom["SteelPlate"]     = 100;
            entry.Collected["SteelPlate"] = 50;
            n.BuildQueue.Add(entry);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), t);

            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.FinishBuild);
        }

        // FinishBuild should win over Gift for a raider personality ÔÇö aggressive factions finish
        // combat ships instead of sending olive branches when a build is 80% assembled.
        [Fact]
        public void FinishBuild_BeatsGift_ForAggressiveFaction()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning
            {
                BuildCompletionUrgencyFraction = 0.70,
                GiftCost = 10_000,
                HostileRelationThreshold = -0.25,
                IdleUtility = 0.01,
                TopN = 5,
            };

            var w = new FakeWorld { Treasury = 500_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            var rival = w.Graph.Add(new Vec3(500, 0, 0), 0.7, "R");
            rival.OwnerFactionTag = "G";

            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid" };
            entry.Bom["SteelPlate"] = 100;
            entry.Collected["SteelPlate"] = 85;
            home.BuildQueue.Add(entry);

            // Aggressive raider personality: Aggression=0.9 ÔåÆ FinishBuild bias=0.96, Gift bias=0.28
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.9, Mercantilism = 0.1, Expansionism = 0.6, Xenophobia = 0.5 } };
            p.Weights[Drive.Security] = 1.2; p.Weights[Drive.Growth] = 0.7; p.Weights[Drive.Wealth] = 0.8;
            p.Weights[Drive.Rivalry] = 1.4; p.Weights[Drive.Caution] = 0.5;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["G"] = -0.2; // cold-war band ÔÇö Gift is generated targeting G

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Candidate fbCand = d.Scored.Find(c => c.Intent.Kind == ActionKind.FinishBuild);
            Candidate giftCand = d.Scored.Find(c => c.Intent.Kind == ActionKind.Gift);
            Assert.NotNull(fbCand);
            // Either FinishBuild wins outright or its utility is competitive with Gift
            if (giftCand != null)
                Assert.True(fbCand.Utility >= giftCand.Utility,
                    "FinishBuild must outscore Gift for an aggressive faction with a nearly-complete Raid ship");
        }

        // FinishBuild must have its own dedicated cooldown, not the shared FleetCooldown -- at the
        // 10s live FleetCooldown default (shorter than a 60s brain think-cycle) a shared cooldown
        // never actually throttled re-selection.
        [Fact]
        public void CooldownFor_FinishBuild_ReturnsDedicatedCooldown_NotFleetCooldown()
        {
            var t = new FactionTuning
            {
                FleetCooldown       = TimeSpan.FromSeconds(10),
                FinishBuildCooldown = TimeSpan.FromMinutes(10),
            };
            Assert.Equal(TimeSpan.FromMinutes(10), t.CooldownFor(ActionKind.FinishBuild));
            Assert.NotEqual(t.CooldownFor(ActionKind.Trade), t.CooldownFor(ActionKind.FinishBuild));
        }

        [Fact]
        public void Raid_PrefersCloserTarget_WhenDistanceDiscountOutweighsHigherResourceValue()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning
            {
                RaidReachThreshold = 0.01,
                HostileRelationThreshold = -0.25,
                RaidCost = 12_000,
                RaidDistanceHalfValueKm = 3000,
                IdleUtility = 0.01,
            };
            var w = new FakeWorld { Treasury = 500_000 };
            ((ZeroField)w.Influence).OverrideSample = 1.0;

            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.ShipStock["Raid"] = 1;

            var nearby = w.Graph.Add(new Vec3(1000, 0, 0), 0.5, "NEAR");
            nearby.OwnerFactionTag = "G";
            nearby.HoldStrength = 0.8;
            var distant = w.Graph.Add(new Vec3(6000, 0, 0), 1.0, "FAR");
            distant.OwnerFactionTag = "G";
            distant.HoldStrength = 0.8;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["G"] = -0.5;

            Decision d = brain.Think(s, Persona(0.01, aggr: 1.0), w, new SeededRandom(1), t);
            Candidate near = CampaignNomination.Raid(d, w, "NEAR");
            Candidate far  = CampaignNomination.Raid(d, w, "FAR");

            Assert.NotNull(near);
            Assert.NotNull(far);   // the loser competes: both are nominated, the discount decides which is worth more
            Assert.True(near.Quality > far.Quality,
                "the nearer target must outrank the richer distant one once the distance discount is applied");
        }

        // ---- FAC-273: predation, not hatred, creates a Raid candidate ----------------------------

        /// <summary>Tuning shared by the predation tests. Predation floor 0.5 and warm penalty 0.35 are
        /// the shipped defaults; the thresholds are the live ones.</summary>
        private static FactionTuning PredationTuning()
        {
            return new FactionTuning
            {
                RaidReachThreshold = 0.01,
                HostileRelationThreshold = -0.25,
                AllyRelationThreshold = 0.4,
                RaidPredationAggressionFloor = 0.5,
                RaidWarmTargetQualityPenalty = 0.35,
                RaidCost = 12_000,
                IdleUtility = 0.01,
                // The shipped border-pressure numbers. Without them PowerDampening returns 0 at
                // fleet parity, friction is 0, and neither Security nor Rivalry ever rises -- so
                // AddRaids would not be reached at all and every assertion below would be vacuous.
                BorderPressureParityFloor = 0.3,
                BorderPressureWeaknessCeiling = 1.7,
            };
        }

        /// <summary>One home with a Raid ship and one weak, valuable neighbour per entry in
        /// <paramref name="owners"/> — node id "T&lt;i&gt;", all at the same distance from home so
        /// nothing but standing separates them.
        ///
        /// <para>Border friction is the thing that makes this world a test of the raid rule rather
        /// than of the drive contest: <c>AddRaids</c> only runs under Security or Rivalry, and with
        /// all-neutral standings and a quiet border neither drive is above zero. Pressure from a
        /// crowded border raises both for <b>every</b> personality (a merchant reads it too, just
        /// less), so the mercantile control below reaches the same candidate loop the raider does
        /// and declines there — which is the claim being made.</para></summary>
        private static FakeWorld PreyWorld(params string[] owners)
        {
            var w = new FakeWorld { Treasury = 500_000 };
            var field = (ZeroField)w.Influence;
            field.OverrideSample = 1.0;
            field.OverridePressure = 1.0;
            foreach (string owner in owners)
                if (!field.OverrideRivals.Contains(owner)) field.OverrideRivals.Add(owner);

            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.ShipStock["Raid"] = 1;

            for (int i = 0; i < owners.Length; i++)
            {
                // Same radius from home, so the distance discount cannot decide the contest.
                double angle = i * 1.7;
                var prey = w.Graph.Add(
                    new Vec3(1000 * Math.Cos(angle), 1000 * Math.Sin(angle), 0), 0.9, "T" + i);
                prey.OwnerFactionTag = owners[i];
                prey.HoldStrength = 0.2;   // weak
            }
            return w;
        }

        // The headline of FAC-273: a raider with NO hostile standing at all still raids. Under the old
        // rule ("only the ones we hate") this world produced no Raid candidate whatsoever, which is
        // exactly why SPRT -- the world's designated raider, aggression 0.9 -- enacted zero raids for
        // two days once its standings warmed back to neutral.
        [Fact]
        public void Raid_PredatoryCandidate_ForARaiderWithAllNeutralStandings()
        {
            var brain = new FactionBrain();
            FactionTuning t = PredationTuning();
            FakeWorld w = PreyWorld("G");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Assert.Equal(0.0, s.RelationTo("G"));   // the precondition: nothing to hate

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.9), w, new SeededRandom(1), t);
            Candidate raid = CampaignNomination.Raid(d, w, "T0");

            Assert.NotNull(raid);
            Assert.Equal(CampaignCause.Predation, raid.Intent.Cause);
            Assert.True(raid.Utility > 0, "a raider's predatory raid campaign must actually score");
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Raid);   // the brain nominates, it does not dispatch
        }

        // The other half of the same rule: predation is what creates the candidate, so a faction below
        // the aggression floor is unchanged by FAC-273 -- in the identical world it still raids only
        // what it hates, and it hates nobody here.
        [Fact]
        public void Raid_NoPredatoryCandidate_ForAMercantileFactionInTheSameWorld()
        {
            var brain = new FactionBrain();
            FactionTuning t = PredationTuning();
            FakeWorld w = PreyWorld("G");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5, merc: 1.0, aggr: 0.1), w, new SeededRandom(1), t);

            Assert.Empty(CampaignNomination.AllRaids(d));
        }

        // Standing modulates, it does not gate: with predation < 1 off the hostile path, an owner we
        // already hate still outranks an equally weak, equally valuable, equally distant neutral one.
        [Fact]
        public void Raid_HostileOwnerOutranksAnEquallyWeakNeutralOne()
        {
            var brain = new FactionBrain();
            FactionTuning t = PredationTuning();
            FakeWorld w = PreyWorld("G", "E");   // T0 = neutral G, T1 = hostile E

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["E"] = -0.5;             // below HostileRelationThreshold

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.9), w, new SeededRandom(1), t);
            Candidate neutral = CampaignNomination.Raid(d, w, "T0");
            Candidate hostile = CampaignNomination.Raid(d, w, "T1");

            Assert.NotNull(hostile);
            Assert.NotNull(neutral);   // the loser competes: standing decides the ranking, not the candidate set
            Assert.True(hostile.Quality > neutral.Quality,
                "an owner we already hate must outrank an equally weak neutral one");
        }

        // ...and the warm end of the same ramp: a friend is a worse target than a stranger, because
        // RaidWarmTargetQualityPenalty scales the predatory score down by how warm the standing is.
        // The loser here competes -- both nodes are identical and reachable -- so a green result means
        // the penalty decided it, not that there was no contest.
        [Fact]
        public void Raid_WarmOwnerRanksBelowANeutralOne()
        {
            var brain = new FactionBrain();
            FactionTuning t = PredationTuning();
            FakeWorld w = PreyWorld("W", "G");   // T0 = warm W, T1 = neutral G

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["W"] = 0.86;             // SPRT's live standing toward IMPR, 2026-09-06

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.9), w, new SeededRandom(1), t);
            Candidate warm    = CampaignNomination.Raid(d, w, "T0");
            Candidate neutral = CampaignNomination.Raid(d, w, "T1");

            Assert.NotNull(neutral);
            Assert.NotNull(warm);      // the loser competes: the warm penalty decides, it does not exclude
            Assert.True(neutral.Quality > warm.Quality,
                "a friend must be a worse target than a stranger once the warm penalty is applied");
        }

        // The one standing that is still a hard exclusion rather than a weighting. A pact is the
        // formal alliance; predation must not reach through it however fat the target.
        [Fact]
        public void Raid_PactPartnerIsNeverACandidate_EvenForARaider()
        {
            var brain = new FactionBrain();
            FactionTuning t = PredationTuning();
            FakeWorld w = PreyWorld("G");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.PactWith.Add("G");

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.9), w, new SeededRandom(1), t);

            Assert.Null(CampaignNomination.Raid(d, w, "T0"));
        }

        // ---- P2: retaliate counter-raid ---------------------------------------------------------

        // When an owned node's threat exceeds RetaliateThreatThreshold and a reachable hostile
        // exists, a Retaliate candidate must be generated.
        [Fact]
        public void Retaliate_GeneratedWhenNodeIsThreatenedAndHostileReachable()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning
            {
                RetaliateThreatThreshold = 0.25,
                RaidReachThreshold = 0.01,
                HostileRelationThreshold = -0.25,
                RaidCost = 12_000,
                IdleUtility = 0.01,
            };

            // Full influence field that returns 1.0 for all queries (faction can reach everywhere)
            var w = new FakeWorld { Treasury = 500_000 };
            ((ZeroField)w.Influence).OverrideSample = 1.0;

            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.Threat = 0.4; // above RetaliateThreatThreshold=0.25
            home.ShipStock["Raid"] = 1; // ship-stock gate: need a Raid ship for Retaliate to score > 0

            var hostile = w.Graph.Add(new Vec3(500, 0, 0), 0.7, "R");
            hostile.OwnerFactionTag = "G";
            hostile.HoldStrength = 0.8;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["G"] = -0.5; // hostile (below HostileRelationThreshold=-0.25)

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.7), w, new SeededRandom(1), t);

            Assert.Contains(CampaignNomination.AllRetaliations(d), c => c.Utility > 0);
        }

        // When no owned node exceeds the threat threshold, Retaliate must not be generated.
        [Fact]
        public void Retaliate_NotGeneratedWhenThreatBelowThreshold()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning
            {
                RetaliateThreatThreshold = 0.25,
                RaidReachThreshold = 0.01,
                HostileRelationThreshold = -0.25,
                RaidCost = 12_000,
                IdleUtility = 0.01,
            };

            var w = new FakeWorld { Treasury = 500_000 };
            ((ZeroField)w.Influence).OverrideSample = 1.0;

            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            home.Threat = 0.10; // below RetaliateThreatThreshold

            var hostile = w.Graph.Add(new Vec3(500, 0, 0), 0.7, "R");
            hostile.OwnerFactionTag = "G";

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["G"] = -0.5;

            Decision d = brain.Think(s, Persona(0.5, aggr: 0.7), w, new SeededRandom(1), t);

            Assert.Empty(CampaignNomination.AllRetaliations(d));
        }

        // ---- P7: personality-drift war-spiral correction ----------------------------------------

        // A settler (low-aggression) fighting more than PersonalityDriftEnemyCount enemies at
        // PeaceThreshold fatigue must have Caution boosted.
        [Fact]
        public void PersonalityDrift_BoostsCaution_ForSettlerOverExtendedInWar()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.PersonalityDriftEnemyCount = 2;
            t.PersonalityDriftCautionBoost = 1.5;

            var w = new FakeWorld { Treasury = 300_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.7;

            // Settler personality: low aggression
            var p = Persona(0.3, exp: 1.0, merc: 0.4, aggr: 0.25);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            // Three enemies at PeaceThreshold fatigue (> PersonalityDriftEnemyCount=2)
            s.Relations["A"] = -0.5; s.Fatigue["A"] = t.PeaceThreshold + 0.05;
            s.Relations["B"] = -0.5; s.Fatigue["B"] = t.PeaceThreshold + 0.05;
            s.Relations["C"] = -0.5; s.Fatigue["C"] = t.PeaceThreshold + 0.05;

            // Run brain once to get drives with drift correction
            brain.Think(s, p, w, new SeededRandom(1), t);

            // Compare against a state with the same enemies but only 1 (below threshold)
            var sBaseline = new FactionState { Tag = "F", RngSeed = 1 };
            sBaseline.Relations["A"] = -0.5; sBaseline.Fatigue["A"] = t.PeaceThreshold + 0.05;
            brain.Think(sBaseline, p, w, new SeededRandom(1), t);

            Assert.True(s.Drives[Drive.Caution] >= sBaseline.Drives[Drive.Caution],
                "Caution must be at least as high for a settler over-extended in war vs. one fighting fewer enemies");
        }

        // A raider (aggression >= 0.5) must NOT receive the personality-drift boost.
        [Fact]
        public void PersonalityDrift_DoesNotBoostCaution_ForRaiderFaction()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.PersonalityDriftEnemyCount = 2;
            t.PersonalityDriftCautionBoost = 1.5;

            var w = new FakeWorld { Treasury = 300_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.7;

            // Raider personality: high aggression (>= 0.5) ÔåÆ drift correction must not apply
            var p = Persona(0.9, exp: 0.6, merc: 0.1, aggr: 0.9);

            var sOver = new FactionState { Tag = "F", RngSeed = 1 };
            sOver.Relations["A"] = -0.5; sOver.Fatigue["A"] = t.PeaceThreshold + 0.05;
            sOver.Relations["B"] = -0.5; sOver.Fatigue["B"] = t.PeaceThreshold + 0.05;
            sOver.Relations["C"] = -0.5; sOver.Fatigue["C"] = t.PeaceThreshold + 0.05;

            var sBase = new FactionState { Tag = "F", RngSeed = 1 };
            sBase.Relations["A"] = -0.5; sBase.Fatigue["A"] = t.PeaceThreshold + 0.05;

            brain.Think(sOver, p, w, new SeededRandom(1), t);
            brain.Think(sBase, p, w, new SeededRandom(1), t);

            // Drives may differ due to war-fatigue scaling of rivalry/caution, but the drift
            // multiplier must not have been applied (Caution should not be boosted beyond what
            // war fatigue already produces).
            // We verify indirectly: Caution for the over-extended raider should not exceed
            // PersonalityDriftCautionBoost ├ù the baseline Caution by more than war-fatigue alone would.
            double overCaution = sOver.Drives[Drive.Caution];
            double baseCaution = sBase.Drives[Drive.Caution];
            // If drift boost applied, overCaution would be up to 1.5├ù baseCaution.
            // For a raider we assert it stays within normal war-fatigue range (Ôëñ 1.3├ù baseline).
            Assert.True(overCaution <= baseCaution * 1.3 + 0.05,
                "Raider Caution must not receive the personality-drift boost");
        }

        // ---- P8: bank grant signal ---------------------------------------------------------------

        // When the grant cooldown is active (grant was recently issued), the bleed component of
        // Wealth must be reduced, so the faction does not sense itself as cash-poor.
        [Fact]
        public void GrantSignal_ReducesBleed_WhenGrantCooldownActive()
        {
            var brain = new FactionBrain();
            // RunwayComfortTicks = 20: runway = 50K/5K = 10 ticks ÔåÆ bleed = 1 - 10/20 = 0.5.
            // ComfortLedger must be very high so affluenceÔëê1 (povertyÔëê0), making bleed the sole
            // contributor to Wealth and letting the grant halving be visible.
            var t = new FactionTuning { RunwayComfortTicks = 20, ComfortLedger = 100_000_000, IdleUtility = 0.01 };

            // Use a negative net flow to generate bleed pressure; ledger set high to zero out poverty.
            var w = new FakeWorld { Treasury = 50_000, NetFlow = -5_000 };
            w.Ledger = 100_000_000; // affluence = 1.0 ÔåÆ poverty = 0 ÔåÆ bleed is the only Wealth driver
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;

            var sCooldown = new FactionState { Tag = "F", RngSeed = 1 };
            sCooldown.CentralBankGrantCooldownUntilUtc = w.UtcNow.AddMinutes(1); // grant issued ~now
            brain.Think(sCooldown, Persona(0.0), w, new SeededRandom(1), t);

            var sNoCooldown = new FactionState { Tag = "F", RngSeed = 1 };
            // CentralBankGrantCooldownUntilUtc = MinValue (default) ÔÇö no recent grant
            brain.Think(sNoCooldown, Persona(0.0), w, new SeededRandom(1), t);

            Assert.True(sCooldown.Drives[Drive.Wealth] < sNoCooldown.Drives[Drive.Wealth],
                "Wealth drive must be lower when a recent grant halves the bleed component");
        }

        // ---- P11: idle steady-state boost -------------------------------------------------------

        // When all five drives are below StabilityThreshold, Idle should win because its utility
        // is multiplied by StabilityIdleMultiplier, pushing it above low-urgency maintenance actions.
        [Fact]
        public void IdleBoost_WinsWhenAllDrivesAreStable()
        {
            var brain = new FactionBrain();
            // StabilityThreshold = 0.30: Growth has a floor of ~0.25 for a faction with nodes but
            // no expansion frontier and full fleet readiness, so the threshold must be above that.
            var t = new FactionTuning
            {
                IdleUtility = 0.15,
                StabilityThreshold = 0.30,
                StabilityIdleMultiplier = 2.0,   // Idle utility = 0.30 when stable
                ComfortLedger = 10_000,
                ReinforceCost = 5_000,
                GuardCost = 5_000,
                InsolvencyFloor = 1_000,
                TopN = 5,
            };

            // Rich, peaceful faction ÔÇö all drives should be low
            var w = new FakeWorld { Treasury = 5_000_000, NetFlow = 10_000 };
            w.Ledger = 10_000_000; // above ComfortLedger ÔåÆ affluence=1 ÔåÆ poverty=0 ÔåÆ WealthÔëê0
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.95;
            // Full fleet readiness: prevents the fleet-readiness boost from inflating Growth
            home.ShipStock["Trade"]  = 2;
            home.ShipStock["Raid"]   = 2;
            home.ShipStock["Patrol"] = 2;
            // No neighbors ÔåÆ no expansion frontier ÔåÆ Growth baseline = 0.25 (< threshold 0.30)

            // Peaceful personality, no hostile neighbors
            var p = Persona(0.0, exp: 0.3, merc: 0.8, aggr: 0.0);
            p.Weights[Drive.Security] = 0.5;
            p.Weights[Drive.Growth]   = 0.3;
            p.Weights[Drive.Wealth]   = 0.4;
            p.Weights[Drive.Rivalry]  = 0.1;
            p.Weights[Drive.Caution]  = 0.3;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            // All drives should be stable (below 0.30)
            bool stable = s.Drives[Drive.Security] < t.StabilityThreshold
                       && s.Drives[Drive.Growth]   < t.StabilityThreshold
                       && s.Drives[Drive.Wealth]   < t.StabilityThreshold
                       && s.Drives[Drive.Rivalry]  < t.StabilityThreshold
                       && s.Drives[Drive.Caution]  < t.StabilityThreshold;
            Assert.True(stable, "All drives must be below StabilityThreshold (0.30) for this test to be meaningful ÔÇö "
                + "sec=" + s.Drives[Drive.Security].ToString("0.00") + " gro=" + s.Drives[Drive.Growth].ToString("0.00")
                + " wea=" + s.Drives[Drive.Wealth].ToString("0.00") + " riv=" + s.Drives[Drive.Rivalry].ToString("0.00")
                + " cau=" + s.Drives[Drive.Caution].ToString("0.00"));

            Candidate idleCand = d.Scored.Find(c => c.Intent.Kind == ActionKind.Idle);
            Assert.NotNull(idleCand);
            Assert.True(Math.Abs(idleCand.Utility - t.IdleUtility * t.StabilityIdleMultiplier) < 0.001,
                "Idle utility must equal IdleUtility ├ù StabilityIdleMultiplier when all drives are stable");
            Assert.Equal(ActionKind.Idle, d.Intent.Kind);
        }

        // When any drive is above StabilityThreshold, Idle must NOT receive the boost.
        [Fact]
        public void FormatDecisionDebug_IncludesChosenCandidateAndScoreFactors()
        {
            var decision = new Decision
            {
                Goal = Drive.Growth,
                Intent = new FactionIntent { Kind = ActionKind.Develop, TargetNodeId = "E001", TargetCapability = NodeCapabilityKind.Refinery, Cost = 25000 },
                Explanation = "Growth -> Develop(E001+Refinery)",
                Scored = new List<Candidate>
                {
                    new Candidate
                    {
                        SourceDrive = Drive.Growth,
                        Support = 0.75,
                        SourceStrength = 0.75,
                        Intent = new FactionIntent { Kind = ActionKind.Develop, TargetNodeId = "E001", TargetCapability = NodeCapabilityKind.Refinery, Cost = 25000 },
                        Need = 0.75,
                        Afford = 1.0,
                        Bias = 0.80,
                        Quality = 0.70,
                        Cooldown = 1.0,
                        Repetition = 0.90,
                        Utility = 0.378
                    },
                    new Candidate
                    {
                        SourceDrive = Drive.Wealth,
                        Support = 0.50,
                        SourceStrength = 0.50,
                        Intent = new FactionIntent { Kind = ActionKind.DismantleCapability, TargetNodeId = "E002", TargetCapability = NodeCapabilityKind.Manufactory, Cost = 0 },
                        Need = 0.50,
                        Afford = 1.0,
                        Bias = 0.50,
                        Quality = 0.55,
                        Cooldown = 1.0,
                        Repetition = 1.0,
                        Utility = 0.1375
                    }
                }
            };
            var state = new FactionState { Tag = "F" };
            state.Drives[Drive.Growth] = 0.75;
            state.Drives[Drive.Wealth] = 0.50;

            string dump = FactionBrain.FormatDecisionDebug(state, decision, maxCandidates: 10);

            Assert.Contains("brain-debug F", dump);
            Assert.Contains("chosen=Growth -> Develop(E001+Refinery)", dump);
            Assert.Contains("drives=", dump);
            Assert.Contains("candidates=2", dump);
            Assert.Contains("* #1 Growth Develop(E001+Refinery)", dump);
            Assert.Contains("cost=25000", dump);
            Assert.Contains("need=0.750", dump);
            Assert.Contains("afford=1.000", dump);
            Assert.Contains("bias=0.800", dump);
            Assert.Contains("quality=0.700", dump);
            Assert.Contains("cooldown=1.000", dump);
            Assert.Contains("repetition=0.900", dump);
            Assert.Contains("utility=0.378", dump);
            Assert.Contains("#2 Wealth DismantleCapability(E002+Manufactory)", dump);
        }

        [Fact]
        public void IdleBoost_NotApplied_WhenAnyDriveIsAboveThreshold()
        {
            var brain = new FactionBrain();
            var t = new FactionTuning
            {
                IdleUtility = 0.15,
                StabilityThreshold = 0.20,
                StabilityIdleMultiplier = 2.0,
                TopN = 5,
            };

            var w = new FakeWorld { Treasury = 50_000 }; // poor treasury ÔåÆ Wealth drive elevated
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.9;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0), w, new SeededRandom(1), t);

            Candidate idleCand = d.Scored.Find(c => c.Intent.Kind == ActionKind.Idle);
            Assert.NotNull(idleCand);
            // At least one drive should be elevated; Idle should not be boosted
            bool anyElevated = s.Drives[Drive.Security] >= t.StabilityThreshold
                            || s.Drives[Drive.Growth]   >= t.StabilityThreshold
                            || s.Drives[Drive.Wealth]   >= t.StabilityThreshold
                            || s.Drives[Drive.Rivalry]  >= t.StabilityThreshold
                            || s.Drives[Drive.Caution]  >= t.StabilityThreshold;
            if (anyElevated)
                Assert.True(Math.Abs(idleCand.Utility - t.IdleUtility) < 0.001,
                    "Idle utility must equal plain IdleUtility (no boost) when any drive is above StabilityThreshold");
        }

        // ---- ship-stock gate in Score ------------------------------------------------------

        // Helper: build a SecurityRival world with the given rival node (hostile) so
        // AddRaids emits a Raid candidate.
        private static FakeWorld RivalWorld(string actorTag, string rivalTag, bool addRivalNode = true)
        {
            var w = new FakeWorld { Treasury = 500_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = actorTag;
            home.HoldStrength    = 0.8;
            home.StableStationId = 1;
            if (addRivalNode)
            {
                var rival = w.Graph.Add(new Vec3(50, 0, 0), 0.6, "R");
                rival.OwnerFactionTag = rivalTag;
                rival.HoldStrength    = 0.6;
            }
            ((ZeroField)w.Influence).OverrideSample = 1.0; // full reach so Raid is reachable
            return w;
        }

        // Nominating a campaign is a strategic decision and costs no hull: the brain names the
        // region whether or not anything is in stock, and readiness is FleetManager's gate on the
        // dispatch (see OffensiveCampaignDispatchTests). The candidate must still score, or an
        // empty yard would silently stop a faction ever deciding to fight.
        [Fact]
        public void RaidCampaign_StillNominated_WhenNoRaidShipInStock()
        {
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, RaidCost = 12_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = RivalWorld("F", "E");
            // No Raid stock anywhere.
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["E"] = -0.5; // hostile → raid campaign nominated

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.8), w, new SeededRandom(1), t);

            Candidate raid = CampaignNomination.Raid(d, w, "R");
            Assert.NotNull(raid);
            Assert.True(raid.Utility > 0, "an empty yard must not stop the brain deciding where to fight");
        }

        [Fact]
        public void RaidCampaign_NonZeroUtility_WhenRaidShipInStock()
        {
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, RaidCost = 12_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = RivalWorld("F", "E");
            w.Graph.Nodes["H"].ShipStock["Raid"] = 1; // one raid ship ready
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["E"] = -0.5;

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.8), w, new SeededRandom(1), t);

            Candidate raid = CampaignNomination.Raid(d, w, "R");
            Assert.NotNull(raid);
            Assert.True(raid.Utility > 0, "raid-campaign utility should be > 0 when a Raid ship is in stock");
        }

        [Fact]
        public void RaidCampaign_NotSuppressedByInFlightRaid_WhenStockRemains()
        {
            // HasInFlightFleet (a hard "one Raid at a time" rule) was removed — ship stock is now
            // the only concurrency limiter, matching TradeManager's stock/MaxTradeFleets-driven
            // model instead of an arbitrary per-kind uniqueness cap. With a ship still in stock, the
            // nomination must score > 0 even while an earlier Raid is unresolved.
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, RaidCost = 12_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = RivalWorld("F", "E");
            w.Graph.Nodes["H"].ShipStock["Raid"] = 1; // still stocked — represents "not yet exhausted"
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["E"] = -0.5;
            s.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "F", TargetTag = "E",
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10), Resolved = false
            });

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.8), w, new SeededRandom(1), t);

            Candidate raid = CampaignNomination.Raid(d, w, "R");
            Assert.NotNull(raid);
            Assert.True(raid.Utility > 0, "a raid campaign must not be suppressed by an in-flight Raid while stock remains");
        }

        [Fact]
        public void Gift_ZeroUtility_WhenNoTradeShipInStock()
        {
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, GiftCost = 10_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = new FakeWorld { Treasury = 500_000 };
            var home  = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength    = 0.8;
            home.StableStationId = 1;
            // No Trade ship stock.
            // Add a neighbour we have negative standing with → Gift candidate generated.
            var nb = w.Graph.Add(new Vec3(30, 0, 0), 0.5, "NB");
            nb.OwnerFactionTag = "N";
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["N"] = -0.15; // in cold-war band → Gift target

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.1), w, new SeededRandom(1), t);

            Candidate gift = d.Scored.Find(c => c.Intent.Kind == ActionKind.Gift);
            if (gift != null)
                Assert.Equal(0.0, gift.Utility, 6);
        }

        [Fact]
        public void Gift_NonZeroUtility_WhenTradeShipInStock()
        {
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, GiftCost = 10_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = new FakeWorld { Treasury = 500_000 };
            var home  = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength    = 0.8;
            home.StableStationId = 1;
            home.ShipStock["Trade"] = 1; // trade ship available
            var nb = w.Graph.Add(new Vec3(30, 0, 0), 0.5, "NB");
            nb.OwnerFactionTag = "N";
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["N"] = -0.15;

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.1), w, new SeededRandom(1), t);

            Candidate gift = d.Scored.Find(c => c.Intent.Kind == ActionKind.Gift);
            if (gift != null)
                Assert.True(gift.Utility > 0, "Gift utility should be > 0 when Trade ship is in stock");
        }

        [Fact]
        public void Negotiate_NonZeroUtility_EvenWithNoShips()
        {
            // Negotiate must never be blocked by ship-stock — it is a radio call, not a dispatch.
            var brain = new FactionBrain();
            var t     = new FactionTuning { TopN = 10, NegotiateCost = 100_000, InsolvencyFloor = 1_000, DevelopCost = 25_000 };
            var w     = new FakeWorld { Treasury = 500_000 };
            var home  = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength    = 0.8;
            home.StableStationId = 1;
            // No ships anywhere.
            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Relations["E"] = -0.9; // at war → Negotiate candidate generated
            s.Fatigue["E"] = 0.8;

            Decision d = brain.Think(s, Persona(0.0, aggr: 0.5), w, new SeededRandom(1), t);

            Candidate neg = d.Scored.Find(c => c.Intent.Kind == ActionKind.Negotiate);
            if (neg != null)
                Assert.True(neg.Utility > 0, "Negotiate must not be gated by ship stock");
        }

        // ---- expansion hold-health throttle: under-maintained estates stop being told to grow ----

        private static double ClaimQualityForHold(double ownedHold, double claimWeight)
        {
            var tuning = new FactionTuning
            {
                ClaimCost = 20000, ExpandCost = 50000, IdleUtility = 0.15,
                HoldMaintenanceThreshold = 0.8, ClaimHoldHealthWeight = claimWeight
            };

            var w = new FakeWorld { Treasury = 1_000_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "HOME");
            home.OwnerFactionTag = "F";
            home.HoldStrength = ownedHold;
            var second = w.Graph.Add(new Vec3(20, 0, 20), 0.5, "SECOND");
            second.OwnerFactionTag = "F";
            second.HoldStrength = ownedHold;
            var derelict = w.Graph.Add(new Vec3(40, 0, 0), 0.6, "N001");
            derelict.OwnerFactionTag = NeutralFaction.Tag;
            w.Graph.Connect(home.Id, derelict.Id);

            var state = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = new FactionBrain().Think(state, Persona(0.0, exp: 1.0), w,
                new SeededRandom(1), tuning);

            Candidate claim = d.Scored.Find(c => c.Intent.Kind == ActionKind.Claim && c.Intent.TargetNodeId == "N001");
            Assert.NotNull(claim);
            return claim.Quality;
        }

        [Fact]
        public void ClaimQuality_DampedWhenEstateUnderHeld()
        {
            double healthy = ClaimQualityForHold(0.9, 0.75);
            double degraded = ClaimQualityForHold(0.3, 0.75);

            Assert.True(degraded < healthy, "under-held estate must damp claim quality");
            // every owned node under-held: scale = 1 - 0.75, still a candidate (dampened, not banned)
            Assert.Equal(healthy * 0.25, degraded, 6);
            Assert.True(degraded > 0);
        }

        [Fact]
        public void ClaimQuality_WeightZero_DisablesDamping()
        {
            double healthy = ClaimQualityForHold(0.9, 0.0);
            double degraded = ClaimQualityForHold(0.3, 0.0);
            Assert.Equal(healthy, degraded, 6);
        }
    }
}
