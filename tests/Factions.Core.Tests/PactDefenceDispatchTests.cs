using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FleetManager's Guard dispatch across a pact: a faction defends a partner's threatened node
    /// with a hull of its own when nothing in its own estate needs one, and exactly one defender is
    /// paid for per node no matter which of the two allies gets there first.
    ///
    /// <para>Provenance: FAC-276.</para>
    /// </summary>
    public class PactDefenceDispatchTests
    {
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

        private static FactionTuning GuardTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GuardCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
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

        private static Cat StockCombatShip(WorldModel w, SpatialNode node, string prefabId = "Gunship")
        {
            node.ShipStock[prefabId] = 1;
            Cat cat = w.PrefabCatalogue as Cat;
            if (cat == null) w.PrefabCatalogue = cat = new Cat();
            cat.Armed.Add(prefabId);
            return cat;
        }

        private static FactionState Raider(WorldModel w, string tag, string targetNodeId, double etaMinutes)
        {
            var st = new FactionState { Tag = tag, RngSeed = 1 };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = tag,
                TargetNodeId = targetNodeId,
                OriginPosKm = new Vec3(500, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(etaMinutes),
                FleetId = "RAID" + targetNodeId,
                LastResolvedSegment = -1,
            });
            return st;
        }

        private static List<string> GuardLines(List<string> log)
        {
            return log.Where(m => m.IndexOf("GUARD_DISPATCH", StringComparison.Ordinal) >= 0).ToList();
        }

        private static string GuardLine(List<string> log)
        {
            return GuardLines(log).FirstOrDefault();
        }

        /// <summary>Two estates side by side: ALY holds the only combat hull in the world, PTR holds
        /// the node a raider is coming for. Nothing but the pact can put ALY's hull on PTR's node.</summary>
        private static WorldModel MakePactWorld()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("ALY", 500000);
            w.SetTreasury("PTR", 500000);
            SpatialNode allyHome = AddNode(w, "ALY_HOME", "ALY", new Vec3(0, 0, 0));
            AddNode(w, "ALY_EDGE", "ALY", new Vec3(10, 0, 0));
            AddNode(w, "PTR_HOME", "PTR", new Vec3(20, 0, 0));
            AddNode(w, "PTR_EDGE", "PTR", new Vec3(30, 0, 0));
            w.Graph.Connect("ALY_HOME", "ALY_EDGE");
            w.Graph.Connect("ALY_EDGE", "PTR_HOME");
            w.Graph.Connect("PTR_HOME", "PTR_EDGE");
            StockCombatShip(w, allyHome);
            return w;
        }

        private static void Pact(FactionState a, FactionState b)
        {
            a.PactWith.Add(b.Tag);
            b.PactWith.Add(a.Tag);
        }

        // The headline. ALY's own estate is quiet, PTR's edge has a raid inbound, and the two hold a
        // pact -- so the one combat hull in the world flies to a node ALY does not own.
        // Characteristic failure: no GUARD_DISPATCH at all, because the candidate scan never leaves
        // world.Graph.OwnedBy(st.Tag).
        [Fact]
        public void PartnerNodeThreatened_OwnEstateQuiet_DispatchesGuardToThePartner()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            Pact(ally, partner);
            FactionState raider = Raider(w, "RVN", "PTR_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> PTR_EDGE", line, StringComparison.Ordinal);
            Assert.Contains("ally=PTR", line, StringComparison.Ordinal);
            Assert.Contains("vs=RVN", line, StringComparison.Ordinal);

            PendingFleetAction guard = Assert.Single(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
            Assert.Equal("PTR_EDGE", guard.TargetNodeId);
            Assert.Equal("PTR", guard.TargetTag);
        }

        // The discriminator for the test above: the same world with no pact must produce nothing.
        // Without this, a manager that defended every threatened node in the galaxy would pass.
        // Characteristic failure: a Guard flies to a stranger's node.
        [Fact]
        public void PartnerNodeThreatened_WithoutAPact_DispatchesNothing()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "PTR_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            Assert.Null(GuardLine(log));
            Assert.Empty(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // Own estate strictly first, and the urgency ranking must not be able to overturn that: the
        // partner's raid lands in 5 minutes and ALY's own in 30, and ALY still defends its own.
        // Characteristic failure: one merged ranking, so the sooner raid wins and ALY leaves its own
        // node open to cover a partner.
        [Fact]
        public void OwnNodeAndPartnerNodeBothThreatened_OwnNodeWinsEvenWhenLessUrgent()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            Pact(ally, partner);
            FactionState fast = Raider(w, "RVN", "PTR_EDGE", 5);
            FactionState slow = Raider(w, "ORK", "ALY_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, fast, slow }, w, t, log);

            string line = Assert.Single(GuardLines(log));
            Assert.Contains("-> ALY_EDGE", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ally=", line, StringComparison.Ordinal);
            Assert.Equal("ALY_EDGE", Assert.Single(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard)).TargetNodeId);
        }

        // One defender per node, counted across factions: the resolver accepts any non-raider Guard
        // as the defender, so PTR's own hull already fills the slot and ALY's would buy nothing.
        // Characteristic failure: the ally pays for a second Guard because the has-a-defender test
        // only ever looked at the asking faction's own PendingFleets.
        [Fact]
        public void PartnerAlreadyGuardsTheNode_AllyDispatchesNothing()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            Pact(ally, partner);
            partner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = "PTR",
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                OriginNodeId = "PTR_HOME",
                TargetNodeId = "PTR_EDGE",
                TargetTag = "PTR",
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(1),
                FleetId = "PTRGUARD",
                LastResolvedSegment = -1,
            });
            FactionState raider = Raider(w, "RVN", "PTR_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            Assert.Null(GuardLine(log));
            Assert.Empty(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // The states are walked in order, so the owner of a node can be reached after the ally that
        // just covered it. Both have money and a hull; exactly one defender may be bought.
        // Characteristic failure: the per-node defender index is a snapshot taken before the loop, so
        // PTR does not see the Guard ALY dispatched this same tick and sends a second one.
        [Fact]
        public void AllyDispatchesFirst_TheOwnerLaterInTheSameTickDoesNotSendASecond()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            StockCombatShip(w, w.Graph.Nodes["PTR_HOME"]);      // the owner can afford its own defender
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            Pact(ally, partner);
            FactionState raider = Raider(w, "RVN", "PTR_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            string line = Assert.Single(GuardLines(log));
            Assert.StartsWith("ALY GUARD_DISPATCH", line, StringComparison.Ordinal);
            Assert.Contains("ally=PTR", line, StringComparison.Ordinal);
            Assert.Single(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
            Assert.Empty(partner.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // A pact is not a licence to shoot another pact partner. When both sides of a quarrel are
        // allies of the would-be defender, it stays home.
        // Characteristic failure: the ally picks a side and fires on a faction it is allied with.
        [Fact]
        public void RaiderIsAlsoAPactPartner_NoDispatchToTheOtherPartnersNode()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "PTR_EDGE", 30);
            Pact(ally, partner);
            Pact(ally, raider);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            Assert.Null(GuardLine(log));
            Assert.Empty(ally.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // The ordinary case has to stay byte-identical: an own-node defence names no ally, so a
        // grep for pact defences cannot be polluted by the ninety-nine percent of dispatches that
        // are not one.
        // Characteristic failure: ally= appended unconditionally, naming the faction itself.
        [Fact]
        public void OwnNodeDefence_LineCarriesNoAllyField()
        {
            WorldModel w = MakePactWorld();
            FactionTuning t = GuardTuning();
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            var partner = new FactionState { Tag = "PTR", RngSeed = 1 };
            Pact(ally, partner);
            FactionState raider = Raider(w, "RVN", "ALY_EDGE", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { ally, partner, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> ALY_EDGE", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ally=", line, StringComparison.Ordinal);
            // The last field is still contact_margin_s: nothing was appended after it.
            Assert.StartsWith("contact_margin_s=", line.Substring(line.LastIndexOf(' ') + 1),
                              StringComparison.Ordinal);
        }
    }
}
