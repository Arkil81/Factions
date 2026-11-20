using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Use-it-or-lose-it decay (<see cref="WorldModel.DecayInfluence"/>) for engine-invented and
    /// scan-backed nodes. Engine nodes (N###) are deleted at hold ≤ 0. Scan-backed nodes (E###/G###) are
    /// floored at hold 0.1 — they always yield something and survive between scans.</summary>
    public class WorldModelTests
    {
        private sealed class SurfacePlanetCatalogue : IPlanetCatalogue
        {
            // A real backing PlanetRecord -- WorldModel.LocalSurfaceG needs Planets to enumerate at
            // least one record (it samples a synthetic point at Center+SurfaceRadius, not through
            // GravityAt/NearestSurfacePoint directly), even though this fake's own GravityAt/
            // NearestSurfacePoint are constant overrides that don't otherwise consult it.
            private readonly PlanetRecord _planet = new PlanetRecord { Id = "P", Center = new Vec3(100, 0, 0), SurfaceRadius = 10, GravityRadius = 1000 };
            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }
            public double GravityAt(Vec3 posKm) { return 1.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return new Vec3(100, 0, 0); }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static SpatialNode Owned(WorldModel w, string id, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = hold;
            return n;
        }

        private static FactionTuning SeparationTuning()
        {
            return new FactionTuning
            {
                NodeFrontierMaxOwnedNeighbors = 3,
                NodeExpansionReachPct = 0.25,
                NodeExpansionSampleCount = 4,
                SurfaceGravityThreshold = 0.5,
                OrbitGravityMin = 0.01,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
            };
        }

        [Fact]
        public void EngineSource_DecaysAndIsAbandonedToUnkn_WhenNeglected()
        {
            var w = new WorldModel();
            Owned(w, "N001", 0.05);                                  // a faint engine-invented source (never reinforced → no grace period)

            w.DecayInfluence(0.03);                                  // still hanging on
            Assert.True(w.Graph.Nodes.ContainsKey("N001"));
            Assert.True(w.Graph.Nodes["N001"].HoldStrength < 0.05);  // it decayed

            w.DecayInfluence(0.03);                                  // fades past zero -> handed to UNKN (territory recedes, node persists)
            Assert.True(w.Graph.Nodes.ContainsKey("N001"));
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(w.UtcNow, w.Graph.Nodes["N001"].AbandonedUtc); // stamped at the moment of transfer
        }

        [Fact]
        public void GetExpiredUnknNodeIds_ReturnsOnlyPastGraceUnclaimedUnknNodes()
        {
            var w = new WorldModel();
            w.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            w.Tuning.UnknGraceDuration = TimeSpan.FromHours(8);

            SpatialNode expired = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "OLD");
            expired.OwnerFactionTag = NeutralFaction.Tag;
            expired.AbandonedUtc = w.UtcNow - TimeSpan.FromHours(9); // past the 8h grace window

            SpatialNode fresh = w.Graph.Add(new Vec3(1, 0, 0), 0.5, "FRESH");
            fresh.OwnerFactionTag = NeutralFaction.Tag;
            fresh.AbandonedUtc = w.UtcNow - TimeSpan.FromHours(1); // still within grace

            Assert.Equal(new[] { "OLD" }, w.GetExpiredUnknNodeIds());
        }

        [Fact]
        public void GetExpiredUnknNodeIds_ExcludesStillClaimedOrNonUnknNodes()
        {
            var w = new WorldModel();
            w.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            w.Tuning.UnknGraceDuration = TimeSpan.FromHours(8);

            SpatialNode claimed = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "CLAIMED");
            claimed.OwnerFactionTag = "F"; // real faction -- not UNKN, must never expire
            claimed.AbandonedUtc = w.UtcNow - TimeSpan.FromHours(100);

            SpatialNode reclaimed = w.Graph.Add(new Vec3(1, 0, 0), 0.5, "RECLAIMED");
            reclaimed.OwnerFactionTag = NeutralFaction.Tag;
            // AbandonedUtc left at its DateTime.MinValue default -- e.g. an UNKN node whose Claim just
            // ran but hasn't stamped a new abandon timestamp (there is none to stamp -- it's owned now
            // in the very next tick), or a node recovered via `factions reconcileterritory apply`.

            Assert.Empty(w.GetExpiredUnknNodeIds());
        }

        [Fact]
        public void RemoveNode_DeletesNodeAndStripsDanglingNeighborRefs()
        {
            var w = new WorldModel();
            w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            w.Graph.Add(new Vec3(1, 0, 0), 0.5, "B");
            w.Graph.Connect("A", "B");

            w.RemoveNode("B");

            Assert.False(w.Graph.Nodes.ContainsKey("B"));
            Assert.DoesNotContain("B", w.Graph.Nodes["A"].Neighbors);
        }

        [Fact]
        public void ScanBackedNodes_FloorsAt01_AndNeverVanish()
        {
            var w = new WorldModel();
            string grid = SourceMapping.GridSourceId(123);           // a real world grid
            string econ = SourceMapping.EconomyStationSourceId(7);   // a real economy station
            Owned(w, grid, 0.05);
            Owned(w, econ, 0.05);

            for (int i = 0; i < 10; i++) w.DecayInfluence(0.03);     // many ticks with no reinforcement

            Assert.True(w.Graph.Nodes.ContainsKey(grid), "a real grid asset must survive use-it-or-lose-it decay");
            Assert.True(w.Graph.Nodes.ContainsKey(econ), "a real economy station must survive use-it-or-lose-it decay");
            Assert.True(w.Graph.Nodes[grid].HoldStrength >= 0.1, "scan-backed node is floored at 0.1, not deleted");
            Assert.True(w.Graph.Nodes[econ].HoldStrength >= 0.1, "scan-backed node is floored at 0.1, not deleted");
        }

        [Fact]
        public void DecayInfluence_UsesPassedRate_RespectsGracePeriod_AndKeepsExistingDecayContract()
        {
            var w = new WorldModel();
            w.Tuning = new FactionTuning { ReinforceGracePeriod = TimeSpan.FromMinutes(30) };
            w.UtcNow = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

            SpatialNode engine = Owned(w, "N100", 0.5);
            string scanBackedId = SourceMapping.GridSourceId(456);
            SpatialNode scanBacked = Owned(w, scanBackedId, 0.11);
            SpatialNode graceSuppressed = Owned(w, "N200", 0.5);
            graceSuppressed.LastReinforcedUtc = w.UtcNow - TimeSpan.FromMinutes(5);

            w.DecayInfluence(0.03);

            Assert.Equal(0.47, engine.HoldStrength, 6);
            Assert.Equal(0.08, scanBacked.HoldStrength, 6);
            Assert.Equal(0.5, graceSuppressed.HoldStrength, 6);

            w.UtcNow = w.UtcNow.AddHours(1);
            w.DecayInfluence(0.6);

            Assert.True(w.Graph.Nodes.ContainsKey("N100"));
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N100"].OwnerFactionTag); // handed to UNKN, not removed
            Assert.True(w.Graph.Nodes.ContainsKey(scanBackedId));
            Assert.Equal(0.1, w.Graph.Nodes[scanBackedId].HoldStrength, 6);
            Assert.True(w.Graph.Nodes.ContainsKey("N200"));
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N200"].OwnerFactionTag);
        }

        [Fact]
        public void HoldDecayPerHour_ConvertsThirtySecondPassToHourlyInvariantRate()
        {
            var tuning = new FactionTuning { HoldDecayPerHour = 0.24 };

            double perPass = tuning.HoldDecayForIntervalSeconds(30.0);

            Assert.Equal(0.002, perPass, 9);
            Assert.Equal(0.24, perPass * 120.0, 9);
        }

        [Fact]
        public void HoldDecayPerHour_IsIndependentOfGlobalPassInterval()
        {
            var tuning = new FactionTuning { HoldDecayPerHour = 0.24 };

            Assert.Equal(0.24, tuning.HoldDecayForIntervalSeconds(30.0) * 120.0, 9);
            Assert.Equal(0.24, tuning.HoldDecayForIntervalSeconds(60.0) * 60.0, 9);
        }

        [Fact]
        public void PlayerHoldDecayPerHour_IsIndependentOfGlobalPassInterval()
        {
            var tuning = new FactionTuning { PlayerHoldDecayPerHour = 0.12 };

            Assert.Equal(0.12, tuning.PlayerHoldDecayForIntervalSeconds(30.0) * 120.0, 9);
            Assert.Equal(0.12, tuning.PlayerHoldDecayForIntervalSeconds(60.0) * 60.0, 9);
        }

        [Fact]
        public void DecayInfluence_AppliesPlayerRateOnlyToPlayerFactionOwnedNodes()
        {
            var w = new WorldModel();
            w.Tuning = new FactionTuning();

            SpatialNode npcNode = Owned(w, "N001", 0.5);
            npcNode.OwnerFactionTag = "NPC";
            SpatialNode playerNode = w.Graph.Add(new Vec3(1, 0, 0), 0.5, "N002");
            playerNode.OwnerFactionTag = "PLYR";
            playerNode.HoldStrength = 0.5;

            var states = new List<FactionState>
            {
                new FactionState { Tag = "NPC", IsPlayerFaction = false },
                new FactionState { Tag = "PLYR", IsPlayerFaction = true },
            };

            w.DecayInfluence(0.03, 0.10, states);

            Assert.Equal(0.47, npcNode.HoldStrength, 6);
            Assert.Equal(0.40, playerNode.HoldStrength, 6);
        }

        [Fact]
        public void DecayInfluence_NullStates_AppliesBaseRateToEveryone()
        {
            var w = new WorldModel();
            w.Tuning = new FactionTuning();
            SpatialNode n = Owned(w, "N001", 0.5);

            w.DecayInfluence(0.03, 0.10, null);

            Assert.Equal(0.47, n.HoldStrength, 6); // playerRate never consulted without a states list
        }

        // Expansion into empty deep space yields an empty node with no resource value -- and now does
        // so because the world was asked and had nothing to report, not because a die said Empty.
        // The parent is deliberately a rich OreField: under the retired expand-bias table that made an
        // ore-bearing child the likeliest outcome, so a parent that still influenced the child would
        // fail this. See FAC-102.
        [Fact]
        public void ExpandFrom_NothingUnderIt_IsEmptyWithNoResourceValue()
        {
            var w = new WorldModel();
            w.Tuning = new FactionTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "P001");
            parent.OwnerFactionTag = "F";

            for (int i = 0; i < 20; i++)
            {
                SpatialNode child = w.ExpandFrom("F", "P001");
                Assert.Equal("", NodeDisplay.DominantOre(child));
                Assert.Equal(0.0, child.ResourceValue);
            }
        }


        [Fact]
        public void ExpandFrom_SurfaceDensePlacement_RefusesRatherThanCommittingInsideTheFloor()
        {
            // Every Surface candidate snaps to the one surface point this catalogue offers, which is
            // where the blocker already stands. Nothing clears the 15 km floor, so nothing is created:
            // no node, and no violation record either, since a refusal has nothing to report.
            WorldModel w = new WorldModel();
            w.Tuning = SeparationTuning();
            w.Planets = new SurfacePlanetCatalogue();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";
            w.Graph.Add(new Vec3(100, 0, 0), 0.5, "NBR1");
            int before = w.Graph.Nodes.Count;

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.Null(child);
            Assert.Equal(before, w.Graph.Nodes.Count);
            Assert.Empty(w.SeparationViolations);
        }

        [Fact]
        public void ExpandFrom_WhenNothingIsLegal_RefusesAndCreatesNothing()
        {
            // Every candidate lands 625 km out (reach step off the 500 km deep-space floor) and every
            // one of them is inside something else's floor. Before this, the roomiest of the rejected
            // candidates was committed anyway, 400 km from LOOSE, and its violation recorded.
            WorldModel w = new WorldModel(new SequenceRandom(0.0, 0.5, 0.5));   // one -X sample, then +X
            w.Tuning = SeparationTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";
            w.Graph.Add(new Vec3(620, 0, 0), 0.5, "TIGHT");     // 5 km from the +X candidate
            w.Graph.Add(new Vec3(-225, 0, 0), 0.5, "LOOSE");    // 400 km from the -X candidate
            int before = w.Graph.Nodes.Count;

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.Null(child);
            Assert.Equal(before, w.Graph.Nodes.Count);
            Assert.Empty(w.SeparationViolations);
            Assert.Empty(w.Graph.OwnedBy("F").Where(n => n.Id != "P001"));
        }

        [Fact]
        public void ExpandFrom_WhenOneCandidateIsLegal_StillCommitsIt()
        {
            // Positive control for the refusal above: the same draw with LOOSE moved out of the -X
            // candidate's floor. The one legal sample is committed, at its sampled position, clean.
            WorldModel w = new WorldModel(new SequenceRandom(0.0, 0.5, 0.5));
            w.Tuning = SeparationTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";
            w.Graph.Add(new Vec3(620, 0, 0), 0.5, "TIGHT");
            w.Graph.Add(new Vec3(-1200, 0, 0), 0.5, "FAR");     // 575 km from the -X candidate

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.NotNull(child);
            Assert.Equal(-625.0, child.Position.X, 3);
            Assert.Equal("F", child.OwnerFactionTag);
            Assert.Empty(w.SeparationViolations);
        }

        [Fact]
        public void ExpandFrom_DeepSpaceDensePlacement_UsesReachStepWhenSpaceIsAvailable()
        {
            WorldModel w = new WorldModel(new SequenceRandom(0.90, 0.50, 0.50, 0.10, 0.10));
            w.Tuning = SeparationTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.NotNull(child);
            Assert.Equal(NodeZone.DeepSpace, child.Zone);
            Assert.True(child.Position.X > 0.0);
            Assert.Empty(w.SeparationViolations);
        }

        [Fact]
        public void GetNodeSeparationViolations_ReportsExistingPairsWithoutMutatingState()
        {
            WorldModel w = new WorldModel();
            w.Tuning = SeparationTuning();

            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N001");
            a.Zone = NodeZone.DeepSpace;
            SpatialNode b = w.Graph.Add(new Vec3(100, 0, 0), 0.5, "N002");
            b.Zone = NodeZone.DeepSpace;

            int nodeCountBefore = w.Graph.Nodes.Count;
            IList<NodeSeparationViolation> violations = w.GetNodeSeparationViolations();

            Assert.Equal(nodeCountBefore, w.Graph.Nodes.Count);
            Assert.Empty(w.SeparationViolations);
            Assert.Equal(2, violations.Count);

            Assert.Equal("N001", violations[0].NewNodeId);
            Assert.Equal("N002", violations[0].OffendingNeighborId);
            Assert.Equal(100.0, violations[0].ActualDistanceKm);
            Assert.Equal(500.0, violations[0].RequiredMinSepKm);

            Assert.Equal("N002", violations[1].NewNodeId);
            Assert.Equal("N001", violations[1].OffendingNeighborId);
            Assert.Equal(100.0, violations[1].ActualDistanceKm);
            Assert.Equal(500.0, violations[1].RequiredMinSepKm);
        }

        private sealed class SequenceRandom : IRandom
        {
            private readonly Queue<double> _values;

            public SequenceRandom(params double[] values)
            {
                _values = new Queue<double>(values);
            }

            public double NextDouble()
            {
                if (_values.Count == 0) return 0.5;
                return _values.Dequeue();
            }
        }

        [Fact]
        public void ExpandDecision_PrefersFrontierOrigin()
        {
            FactionTuning tuning = SeparationTuning();
            tuning.ExpandCost = 1;
            tuning.ClaimCost = 10;
            tuning.InsolvencyFloor = -10;
            tuning.ComfortLedger = 1000;
            tuning.TopN = 10;

            WorldModel w = new WorldModel(new SeededRandom(1));
            w.Tuning = tuning;
            w.SetTreasury("F", 100);

            SpatialNode interior = w.Graph.Add(new Vec3(0, 0, 0), 0.3, "HOME");
            interior.OwnerFactionTag = "F";
            interior.HoldStrength = 0.8;
            SpatialNode edge = w.Graph.Add(new Vec3(200, 0, 0), 0.9, "EDGE");
            edge.OwnerFactionTag = "F";
            edge.HoldStrength = 0.8;
            SpatialNode a2 = w.Graph.Add(new Vec3(0, 50, 0), 0.3, "A"); a2.OwnerFactionTag = "F";
            SpatialNode b2 = w.Graph.Add(new Vec3(0, -50, 0), 0.3, "B"); b2.OwnerFactionTag = "F";
            SpatialNode c2 = w.Graph.Add(new Vec3(50, 0, 0), 0.3, "C"); c2.OwnerFactionTag = "F";
            SpatialNode d2 = w.Graph.Add(new Vec3(-50, 0, 0), 0.3, "D"); d2.OwnerFactionTag = "F";
            w.Graph.Connect("HOME", "A");
            w.Graph.Connect("HOME", "B");
            w.Graph.Connect("HOME", "C");
            w.Graph.Connect("HOME", "D");
            w.Graph.Connect("HOME", "EDGE");

            FactionState s = new FactionState { Tag = "F", RngSeed = 1 };
            s.Drives[Drive.Growth] = 1.0;
            Personality p = new Personality { Temperature = 0.0, Traits = new Traits() };
            p.Weights[Drive.Growth] = 5;
            p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Security] = 1;
            p.Weights[Drive.Rivalry] = 1;
            p.Weights[Drive.Caution] = 1;

            Decision d0 = new FactionBrain().Think(s, p, w, new SeededRandom(1), tuning);
            Candidate expand = d0.Scored.First(c => c.Intent.Kind == ActionKind.Expand);

            Assert.Equal("EDGE", expand.Intent.TargetNodeId);
        }

        [Fact]
        public void ExpandFrom_PrefersRoomierCandidateWithinReachCap()
        {
            WorldModel w = new WorldModel(new SequenceRandom(
                0.90, 0.50, 0.50,
                0.10, 0.50, 0.50,
                0.10, 0.10));
            w.Tuning = SeparationTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";

            w.Graph.Add(new Vec3(600, 0, 0), 0.5, "BLOCK_POSITIVE");

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.True(child.Position.X < 0.0);
            Assert.True(child.Position.DistanceTo(parent.Position) >= 625.0);
            Assert.Empty(w.SeparationViolations);
        }

        [Fact]
        public void ExpandFrom_ConnectsToNearbyExistingNodes_NotJustOrigin()
        {
            // A single (1,0,0)-direction sample deterministically places the child at exactly
            // parent + 625km along +X (the DeepSpace reach cap, SeparationTuning's dominant term
            // over the small default ExpandStep=35km) -- see ExpandFrom_PrefersRoomierCandidateWithinReachCap
            // for the same reach-cap math. NodeExpansionSampleCount=1 removes multi-candidate scoring
            // so no other node in the graph can perturb which point gets chosen.
            FactionTuning tuning = SeparationTuning();
            tuning.NodeExpansionSampleCount = 1;
            WorldModel w = new WorldModel(new SequenceRandom(1.0, 0.5, 0.5));
            w.Tuning = tuning;

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";
            // 550 km from the child: outside the 500 km floor, so the placement is legal, and inside
            // the 625 km reach cap, so the densification pass links it.
            SpatialNode nearby = w.Graph.Add(new Vec3(625, 550, 0), 0.5, "NEARBY");

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.Contains("P001", child.Neighbors);      // the pre-existing single-edge-per-Expand link
            Assert.Contains("NEARBY", child.Neighbors);    // the new densification link
            Assert.Contains(child.Id, nearby.Neighbors);   // undirected
        }

        [Fact]
        public void ExpandFrom_DoesNotConnectToNodesOutsideReachCap()
        {
            FactionTuning tuning = SeparationTuning();
            tuning.NodeExpansionSampleCount = 1;
            WorldModel w = new WorldModel(new SequenceRandom(1.0, 0.5, 0.5));
            w.Tuning = tuning;

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";
            SpatialNode far = w.Graph.Add(new Vec3(1300, 0, 0), 0.5, "FAR"); // ~675km from the child, outside the 625km reach cap

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.DoesNotContain("FAR", child.Neighbors);
            Assert.DoesNotContain(child.Id, far.Neighbors);
        }

        [Fact]
        public void DensifyTopology_DryRun_ReportsWithoutMutating()
        {
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            SpatialNode b = w.Graph.Add(new Vec3(100, 0, 0), 0.5, "B"); // well within the 625km DeepSpace reach cap

            TopologyDensifyResult result = w.DensifyTopology(applyFix: false);

            Assert.Equal(1, result.LinksAdded);
            Assert.Empty(a.Neighbors);
            Assert.Empty(b.Neighbors);
        }

        [Fact]
        public void DensifyTopology_Apply_ConnectsPreviouslyUnlinkedNearbyNodes()
        {
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            SpatialNode b = w.Graph.Add(new Vec3(100, 0, 0), 0.5, "B");

            TopologyDensifyResult result = w.DensifyTopology(applyFix: true);

            Assert.Equal(1, result.LinksAdded);
            Assert.Contains("B", a.Neighbors);
            Assert.Contains("A", b.Neighbors);
        }

        [Fact]
        public void DensifyTopology_Apply_IsIdempotent()
        {
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            w.Graph.Add(new Vec3(100, 0, 0), 0.5, "B");

            w.DensifyTopology(applyFix: true);
            TopologyDensifyResult second = w.DensifyTopology(applyFix: true);

            Assert.Equal(0, second.LinksAdded);
            Assert.Equal(1, second.PairsWithinReach); // still found the pair, just already linked
        }

        [Fact]
        public void DensifyTopology_SkipsPairsOutsideReachCap()
        {
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            SpatialNode b = w.Graph.Add(new Vec3(700, 0, 0), 0.5, "B"); // outside the 625km reach cap

            TopologyDensifyResult result = w.DensifyTopology(applyFix: true);

            Assert.Equal(0, result.LinksAdded);
            Assert.Empty(a.Neighbors);
            Assert.Empty(b.Neighbors);
        }

        [Fact]
        public void DensifyTopology_RequiresBothSidesOwnZoneReach_DeepSpaceReachAloneIsNotEnough()
        {
            // A DeepSpace node's own reach (625km) alone would sweep in B, but B is a Surface node
            // whose own reach (15 * 1.25 = 18.75km) does not reach A at 100km -- must NOT connect,
            // since DensifyTopology requires both sides' own reach to agree (Min, not Max).
            //
            // Both nodes' zones come from their positions, not from a hand-set field: separation is
            // resolved per position now (WorldModel.SeparationAtKm), which is what lets an authored
            // feature or body reach this pass at all. Planet at the origin, surface 10 km, gravity
            // limit 30 km -- so B on the surface reads Surface and A at 110 km reads DeepSpace, and
            // they sit exactly 100 km apart as before.
            WorldModel w = new WorldModel(new SeededRandom(1))
            {
                Tuning = SeparationTuning(),
                Planets = new LowGravityPlanetCatalogue(peakG: 1.0, surfaceRadius: 10, gravityRadius: 30),
            };
            SpatialNode a = w.Graph.Add(new Vec3(110, 0, 0), 0.5, "A");
            SpatialNode b = w.Graph.Add(new Vec3(10, 0, 0), 0.5, "B");
            w.ReclassifyNodes(true);
            Assert.Equal(NodeZone.DeepSpace, a.Zone);
            Assert.Equal(NodeZone.Surface, b.Zone);
            Assert.Equal(100.0, a.Position.DistanceTo(b.Position), 6);

            TopologyDensifyResult result = w.DensifyTopology(applyFix: true);

            Assert.Equal(0, result.LinksAdded);
            Assert.Empty(a.Neighbors);
            Assert.Empty(b.Neighbors);
        }

        [Fact]
        public void DensifyTopology_DoesNotTouchOwnershipOrHoldStrength()
        {
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            a.OwnerFactionTag = "F";
            a.HoldStrength = 0.42;
            SpatialNode b = w.Graph.Add(new Vec3(100, 0, 0), 0.5, "B");
            b.OwnerFactionTag = NeutralFaction.Tag;
            b.HoldStrength = 0.0;

            w.DensifyTopology(applyFix: true);

            Assert.Equal("F", a.OwnerFactionTag);
            Assert.Equal(0.42, a.HoldStrength);
            Assert.Equal(NeutralFaction.Tag, b.OwnerFactionTag);
            Assert.Equal(0.0, b.HoldStrength);
        }

        [Fact]
        public void DensifyTopology_LargeGraph_CompletesWithinBudget()
        {
            // Automated version of the live benchmark gate: this codebase independently found a
            // *different* whole-graph pass (InfluenceField.BuildSnapshot) costing ~3.8s at this exact
            // world's ~9,008-node scale despite looking algorithmically cheap on paper -- this test
            // catches a similar regression here before it ever reaches the live server. The threshold
            // is deliberately generous (not the ~300ms live comfort figure) to avoid CI flakiness on
            // slower hardware; it exists to catch an accidental reintroduction of O(n^2) behavior, not
            // to certify live performance -- read the real elapsedMs live before running `apply`.
            WorldModel w = new WorldModel(new SeededRandom(1)) { Tuning = SeparationTuning() };
            var rng = new Random(1);
            for (int i = 0; i < 12000; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 200000.0;
                double y = (rng.NextDouble() - 0.5) * 200000.0;
                double z = (rng.NextDouble() - 0.5) * 200000.0;
                w.Graph.Add(new Vec3(x, y, z), 0.5);
            }

            TopologyDensifyResult result = w.DensifyTopology(applyFix: false);

            Assert.Equal(12000, result.NodesScanned);
            Assert.True(result.ElapsedMs < 5000.0, "DensifyTopology took " + result.ElapsedMs + "ms for 12000 nodes -- investigate before it reaches the live server.");
        }

        [Fact]
        public void ExpandFrom_RivalNodeStillBlocksPlacement()
        {
            WorldModel w = new WorldModel(new SequenceRandom(
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.90, 0.50, 0.50,
                0.10, 0.10));
            w.Tuning = SeparationTuning();

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";

            // Every sample lands on +X at 625 km -- exactly where the rival stands -- so the rival's
            // floor is the only thing deciding, and it decides the same way an own node would: a refusal.
            SpatialNode rival = w.Graph.Add(new Vec3(625, 0, 0), 0.5, "R1");
            rival.OwnerFactionTag = "R";
            int before = w.Graph.Nodes.Count;

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.Null(child);
            Assert.Equal(before, w.Graph.Nodes.Count);
            Assert.Empty(w.SeparationViolations);
        }

        [Fact]
        public void ExpandFrom_NullOriginSeedStillPlacesAwayFromWorldOrigin()
        {
            WorldModel w = new WorldModel(new SequenceRandom(0.90, 0.50, 0.50));
            w.Tuning = SeparationTuning();

            SpatialNode child = w.ExpandFrom("F", null);

            Assert.NotNull(child);
            Assert.True(child.Position.X > 30.0);
            Assert.Equal(NodeZone.DeepSpace, child.Zone);
            Assert.Empty(w.SeparationViolations);
        }

        private sealed class FakePriceCatalogue : IItemPriceCatalogue
        {
            private readonly System.Collections.Generic.Dictionary<string, long> _prices
                = new System.Collections.Generic.Dictionary<string, long>();

            public FakePriceCatalogue Set(string subTypeId, long price) { _prices[subTypeId] = price; return this; }

            public long BasePrice(string subTypeId)
            {
                long value;
                return !string.IsNullOrEmpty(subTypeId) && _prices.TryGetValue(subTypeId, out value) ? value : 0;
            }
        }

        [Fact]
        public void GetItemBookValue_BareKeyItem_UsesRealPriceNotFallback()
        {
            WorldModel w = TestSupport.World();
            w.Catalogue = new TuningItemCatalogue(w.Tuning);
            w.PriceCatalogue = new FakePriceCatalogue().Set("SemiAutoPistolItem", 218);

            long price = w.GetItemBookValue("SemiAutoPistolItem", ItemKind.ProcessedGood);

            Assert.Equal(218, price);
        }

        [Fact]
        public void GetItemBookValue_PrefixedKeyItem_StillResolvesRealPrice()
        {
            WorldModel w = TestSupport.World();
            w.Catalogue = new TuningItemCatalogue(w.Tuning);
            w.PriceCatalogue = new FakePriceCatalogue().Set("Comp:SteelPlate", 3003);

            long price = w.GetItemBookValue("Comp:SteelPlate", ItemKind.ProcessedGood);

            Assert.Equal(3003, price);
        }

        [Fact]
        public void GetItemBookValue_UnknownItem_FallsBackToCatalogueCreditValue()
        {
            WorldModel w = TestSupport.World();
            w.Catalogue = new TuningItemCatalogue(w.Tuning);
            w.PriceCatalogue = new FakePriceCatalogue();

            long price = w.GetItemBookValue("SomeUnpricedItem", ItemKind.ProcessedGood);

            Assert.Equal(w.Tuning.FallbackFor(ItemKind.ProcessedGood), price);
        }

        [Fact]
        public void GetItemBookValue_NullPriceCatalogue_FallsBackToCatalogueCreditValue()
        {
            WorldModel w = TestSupport.World();
            w.Catalogue = new TuningItemCatalogue(w.Tuning);
            w.PriceCatalogue = null;

            long price = w.GetItemBookValue("SemiAutoPistolItem", ItemKind.ProcessedGood);

            Assert.Equal(w.Tuning.FallbackFor(ItemKind.ProcessedGood), price);
        }

        // ---- Spend/Earn: atomic debit/credit-and-track (Accountant refactor) ------------------

        [Fact]
        public void Spend_MatchesAddTreasuryPlusAddPendingExpense()
        {
            WorldModel viaSpend = TestSupport.World();
            WorldModel viaTwoCalls = TestSupport.World();
            viaSpend.SetTreasury("F", 50_000);
            viaTwoCalls.SetTreasury("F", 50_000);

            viaSpend.Spend("F", 1234);
            viaTwoCalls.AddTreasury("F", -1234);
            viaTwoCalls.AddPendingExpense("F", 1234);

            Assert.Equal(viaTwoCalls.GetTreasury("F"), viaSpend.GetTreasury("F"));

            var node = viaSpend.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F"; node.HoldStrength = 1.0; node.Radius = 24;
            var node2 = viaTwoCalls.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node2.OwnerFactionTag = "F"; node2.HoldStrength = 1.0; node2.Radius = 24;

            viaSpend.AccrueIncome();
            viaTwoCalls.AccrueIncome();

            Assert.Equal(viaTwoCalls.GetNetFlow("F"), viaSpend.GetNetFlow("F"));
        }

        [Fact]
        public void Earn_MatchesAddTreasuryPlusAddPendingIncome()
        {
            WorldModel viaEarn = TestSupport.World();
            WorldModel viaTwoCalls = TestSupport.World();
            viaEarn.SetTreasury("F", 50_000);
            viaTwoCalls.SetTreasury("F", 50_000);

            viaEarn.Earn("F", 4321);
            viaTwoCalls.AddTreasury("F", 4321);
            viaTwoCalls.AddPendingIncome("F", 4321);

            Assert.Equal(viaTwoCalls.GetTreasury("F"), viaEarn.GetTreasury("F"));

            var stateEarn = new FactionState { Tag = "F" };
            var stateTwoCalls = new FactionState { Tag = "F" };

            viaEarn.AccrueIncome(new[] { stateEarn });
            viaTwoCalls.AccrueIncome(new[] { stateTwoCalls });

            Assert.Equal(stateTwoCalls.CentralBankRecentIncome, stateEarn.CentralBankRecentIncome);
        }

        // ---- HasShipStockFor route-awareness -------------------------------------------------

        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            private readonly PlanetRecord[] _planets;

            // ThrustPhysics.InAtmosphere derives atmosphere membership from a real PlanetRecord's
            // Center/AtmosphereRadius/HasAtmosphere (not from any flag on this fake directly) — so
            // "in atmosphere everywhere" needs one huge-radius planet at the origin, and "never in
            // atmosphere" needs zero planets, matching how the real IPlanetCatalogue contract works.
            public ConstantGravityCatalogue(double g, bool inAtmosphereEverywhere)
            {
                _g = g;
                _planets = inAtmosphereEverywhere
                    ? new[] { new PlanetRecord { Center = new Vec3(0, 0, 0), SurfaceRadius = 1,
                        GravityRadius = 1_000_000, AtmosphereRadius = 1_000_000, HasAtmosphere = true } }
                    : new PlanetRecord[0];
            }
            public IEnumerable<PlanetRecord> Planets { get { return _planets; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private sealed class ThrustFakeCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public void SetAtmoOnlyDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                        EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = true,
                    }},
                };
            }

            /// <summary>Full-effectiveness-everywhere thrust (mirrors the vanilla Hydrogen curve) —
            /// unlike <see cref="SetAtmoOnlyDown"/>, feasible both in deep space and on a surface, so
            /// tests using this can isolate the mass-trim behaviour from the dry-TWR route gate.</summary>
            public void SetHydrogenDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            public long CargoLiters(string prefabId) { return 5000; } // always cargo-suitable
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands;
                return ThrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }

            public double DrillYieldScore(string prefabId) { return 0; }
        }

        [Fact]
        public void HasShipStockFor_NoTargetNode_RoleOnlyCheck_Unchanged()
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new ThrustFakeCatalogue();
            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["AtmoHauler"] = 1;

            Assert.True(w.HasShipStockFor("F", ActionKind.Trade)); // no target -> role-only, cargo-suitable stub always true
        }

        [Fact]
        public void HasShipStockFor_TargetDeepSpace_AtmoOnlyStock_ReadsAsNoStock()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetAtmoOnlyDown("AtmoHauler", dryMassKg: 1000, forceN: 20000);

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            SpatialNode origin = Owned(w, "N1", 1.0);
            origin.ShipStock["AtmoHauler"] = 1;
            SpatialNode target = Owned(w, "N2", 1.0);
            target.OwnerFactionTag = "Other"; // a real rival node to target

            Assert.False(w.HasShipStockFor("F", ActionKind.Trade, "N2"));
        }

        [Fact]
        public void HasShipStockFor_TargetSurface_AtmoOnlyStock_ReadsAsHasStock()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetAtmoOnlyDown("AtmoHauler", dryMassKg: 1000, forceN: 20000); // TWR ~2.04 at g=1, in atmosphere

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            SpatialNode origin = Owned(w, "N1", 1.0);
            origin.ShipStock["AtmoHauler"] = 1;
            SpatialNode target = Owned(w, "N2", 1.0);
            target.OwnerFactionTag = "Other";

            Assert.True(w.HasShipStockFor("F", ActionKind.Trade, "N2"));
        }

        // ---- TakeRaidLootNodeInventory mass trim (Slice E) -----------------------------------

        private sealed class FakeMassCatalogue : IItemMassCatalogue
        {
            private readonly Dictionary<string, double> _massByKey = new Dictionary<string, double>();
            public FakeMassCatalogue Set(string invKey, double kgPerUnit) { _massByKey[invKey] = kgPerUnit; return this; }
            public double MassKgPerUnit(string invKey)
            {
                double v;
                return _massByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        /// <summary>Builds a world + target node whose inventory holds far more mass than the given
        /// hull can lift at g=1 (real dry TWR ~1.53 at that gravity, comfortably clearing the real
        /// default MinDryTwr=1.05, so the scenario is "route-feasible ship, overloaded manifest" —
        /// the case Slice E's trim exists for). Gold is high-value/heavy; Scrap is low-value/light.</summary>
        private static WorldModel MakeOverloadWorld(out FakeMassCatalogue masses)
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000); // dryTwr ~1.53 at g=1

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);
            w.PriceCatalogue = new FakePriceCatalogue().Set("Gold", 100).Set("Scrap", 1);

            SpatialNode target = Owned(w, "Target", 1.0);
            target.OwnerFactionTag = "Other";
            w.AddNodeInventory("Target", "Gold", 1000);
            w.AddNodeInventory("Target", "Scrap", 100);

            masses = new FakeMassCatalogue().Set("Gold", 10).Set("Scrap", 1);
            return w;
        }

        [Fact]
        public void TakeRaidLootNodeInventory_FitsVolumeButOverloadsTwr_TrimsLowestValueItemsFirst()
        {
            FakeMassCatalogue masses;
            WorldModel w = MakeOverloadWorld(out masses);

            bool massTrimmed;
            Dictionary<string, long> cargo = w.TakeRaidLootNodeInventory("Target", ratio: 1.0,
                cargoCapacityLiters: 100000, prices: w.PriceCatalogue, itemVolumes: null,
                itemMasses: masses, shipPrefabId: "Hauler", massTrimmed: out massTrimmed);

            Assert.True(massTrimmed);
            Assert.NotNull(cargo);
            // Scrap (lowest value) is dropped entirely first; Gold (highest value) is trimmed down
            // to what's left of the ~456 kg cargo ceiling once Scrap's 100 kg wasn't enough alone.
            Assert.False(cargo.ContainsKey("Scrap"));
            Assert.Equal(45, cargo["Gold"]);
        }

        [Fact]
        public void TakeRaidLootNodeInventory_ItemMassesNull_FallsBackToNoMassConstraint()
        {
            FakeMassCatalogue masses;
            WorldModel w = MakeOverloadWorld(out masses);

            Dictionary<string, long> cargo = w.TakeRaidLootNodeInventory("Target", ratio: 1.0,
                cargoCapacityLiters: 100000, prices: w.PriceCatalogue, itemVolumes: null,
                itemMasses: null, shipPrefabId: "Hauler");

            // No mass catalogue wired — behaves exactly like the pre-Slice-E volume-only gate.
            Assert.Equal(1000, cargo["Gold"]);
            Assert.Equal(100, cargo["Scrap"]);
        }

        [Fact]
        public void TakeRaidLootNodeInventory_FitsBothConstraints_Unaffected()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000);

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);
            w.PriceCatalogue = new FakePriceCatalogue().Set("Gold", 100);

            SpatialNode target = Owned(w, "Target", 1.0);
            target.OwnerFactionTag = "Other";
            w.AddNodeInventory("Target", "Gold", 10); // 10 * 10kg = 100kg, well under the ~456kg ceiling

            var masses = new FakeMassCatalogue().Set("Gold", 10);

            bool massTrimmed;
            Dictionary<string, long> cargo = w.TakeRaidLootNodeInventory("Target", ratio: 1.0,
                cargoCapacityLiters: 100000, prices: w.PriceCatalogue, itemVolumes: null,
                itemMasses: masses, shipPrefabId: "Hauler", massTrimmed: out massTrimmed);

            Assert.False(massTrimmed);
            Assert.Equal(10, cargo["Gold"]);
        }

        // ---- AuditStrandedShipStock diagnostic -----------------------------------------------

        [Fact]
        public void AuditStrandedShipStock_AtmoOnlyHullInDeepSpace_CountedAsStranded()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetAtmoOnlyDown("AtmoHauler", dryMassKg: 1000, forceN: 20000); // 0 force outside atmosphere -> originTwr=0

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["AtmoHauler"] = 3;

            StrandedShipStockResult r = w.AuditStrandedShipStock();

            Assert.Equal(3, r.TotalAvailableUnits);
            Assert.Equal(3, r.StrandedUnits);
            Assert.Equal(1, r.NodesWithStrandedStock);
            Assert.Equal(1, r.DistinctStrandedPrefabs);
            Assert.Contains(r.Details, l => l.Contains("AtmoHauler") && l.Contains("count=3"));
        }

        [Fact]
        public void AuditStrandedShipStock_HydrogenHullAnywhere_NeverStranded()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("HydroHauler", dryMassKg: 1000, forceN: 15000); // full effectiveness everywhere

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false); // deep space

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["HydroHauler"] = 5;

            StrandedShipStockResult r = w.AuditStrandedShipStock();

            Assert.Equal(5, r.TotalAvailableUnits);
            Assert.Equal(0, r.StrandedUnits);
            Assert.Equal(0, r.NodesWithStrandedStock);
        }

        [Fact]
        public void AuditStrandedShipStock_ListedForSaleUnits_ExcludedFromCount()
        {
            var cat = new ThrustFakeCatalogue();
            cat.SetAtmoOnlyDown("AtmoHauler", dryMassKg: 1000, forceN: 20000);

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["AtmoHauler"] = 3;
            n.ListedForSale["AtmoHauler"] = 3; // all 3 reserved for player purchase -- still buyable, not "stranded"

            StrandedShipStockResult r = w.AuditStrandedShipStock();

            Assert.Equal(0, r.TotalAvailableUnits);
            Assert.Equal(0, r.StrandedUnits);
        }

        [Fact]
        public void AuditStrandedShipStock_UnscannedPrefab_NotCounted()
        {
            var cat = new ThrustFakeCatalogue(); // no DryMassKg entry at all -> unscanned

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["MysteryPrefab"] = 4;

            StrandedShipStockResult r = w.AuditStrandedShipStock();

            Assert.Equal(4, r.TotalAvailableUnits); // still counted as "available" stock overall
            Assert.Equal(0, r.StrandedUnits);        // but not flagged stranded -- unknown, don't gate
        }

        // ---- NearestPlanetId / PlanetId caching (planet-aware Shipyard investment) -----------

        private sealed class TwoPlanetCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord _terra = new PlanetRecord
            {
                Id = "Terra", Center = new Vec3(0, 0, 0), SurfaceRadius = 1,
                GravityRadius = 1000, AtmosphereRadius = 100, HasAtmosphere = true,
            };
            private readonly PlanetRecord _mars = new PlanetRecord
            {
                Id = "Mars", Center = new Vec3(10000, 0, 0), SurfaceRadius = 1,
                GravityRadius = 1000, AtmosphereRadius = 80, HasAtmosphere = true,
            };
            public IEnumerable<PlanetRecord> Planets { get { yield return _terra; yield return _mars; } }
            public double GravityAt(Vec3 posKm) { return 1.0; } // always Surface-classified for these tests
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void NearestPlanetId_ReturnsNearestByDistance()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new TwoPlanetCatalogue();

            Assert.Equal("Terra", w.NearestPlanetId(new Vec3(1, 0, 0)));
            Assert.Equal("Mars", w.NearestPlanetId(new Vec3(9999, 0, 0)));
        }

        [Fact]
        public void NearestPlanetId_NoPlanetsInCatalogue_ReturnsNull()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            Assert.Null(w.NearestPlanetId(new Vec3(0, 0, 0)));
        }

        [Fact]
        public void ReclassifyNodes_RepairsPlanetIdAlongsideZone()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new TwoPlanetCatalogue();

            SpatialNode nearTerra = w.Graph.Add(new Vec3(1, 0, 0), 0.5, "N1");
            nearTerra.Zone = NodeZone.Surface;
            nearTerra.PlanetId = "StalePlanetId"; // simulates data from before this feature existed

            SpatialNode nearMars = w.Graph.Add(new Vec3(9999, 0, 0), 0.5, "N2");
            nearMars.Zone = NodeZone.Surface;
            nearMars.PlanetId = null;

            NodeReclassifyResult dryRun = w.ReclassifyNodes(applyFix: false);
            Assert.Equal(2, dryRun.PlanetIdChanged);
            Assert.Equal("StalePlanetId", nearTerra.PlanetId); // dry run -- unchanged
            Assert.Null(nearMars.PlanetId);

            NodeReclassifyResult applied = w.ReclassifyNodes(applyFix: true);
            Assert.Equal(2, applied.PlanetIdChanged);
            Assert.Equal("Terra", nearTerra.PlanetId);
            Assert.Equal("Mars", nearMars.PlanetId);

            // A third pass with nothing left to fix reports zero changes -- confirms the repair is
            // idempotent, not just a one-shot count.
            NodeReclassifyResult stable = w.ReclassifyNodes(applyFix: true);
            Assert.Equal(0, stable.PlanetIdChanged);
        }

        // ---- Relative-gravity zone classification + Orbit snap band -------------------------

        /// <summary>Single planet whose true peak surface gravity is configurable (NOT hardcoded to
        /// 1.0 like every other fake catalogue in this file) and whose gravity genuinely falls off with
        /// distance from Center — exercises the relative-gravity math in
        /// <see cref="WorldModel.ClassifyZone"/>/<see cref="WorldModel.LocalSurfaceG"/>, which every
        /// other fake here leaves as a no-op (they all use a position-independent constant, so
        /// relativeG always saturates to 1.0 for any nonzero g). <see cref="NearestSurfacePoint"/> is a
        /// real radial projection (not identity) since it's still exercised directly by the genuine
        /// Surface snap-to-surface behavior (<see cref="WorldModel.ExpandFrom"/>/
        /// <see cref="WorldModel.ReclassifyNodes"/>'s own Surface branches) — <see
        /// cref="WorldModel.LocalSurfaceG"/> itself no longer calls it (it samples a fixed synthetic
        /// point at exactly SurfaceRadius from Center instead, to stay position-independent even when
        /// the live host's NearestSurfacePoint is terrain-following).</summary>
        private sealed class LowGravityPlanetCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord _planet;
            private readonly double _peakG;

            public LowGravityPlanetCatalogue(double peakG, double surfaceRadius = 10, double gravityRadius = 100)
            {
                _peakG = peakG;
                _planet = new PlanetRecord
                {
                    Id = "LowG", Center = new Vec3(0, 0, 0),
                    SurfaceRadius = surfaceRadius, GravityRadius = gravityRadius,
                    AtmosphereRadius = surfaceRadius, HasAtmosphere = true,
                };
            }

            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_planet.Center);
                if (d <= _planet.SurfaceRadius) return _peakG;
                if (d >= _planet.GravityRadius) return 0;
                return _peakG * (_planet.GravityRadius - d) / (_planet.GravityRadius - _planet.SurfaceRadius);
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_planet.Center);
                if (d < 1e-6) return new Vec3(_planet.Center.X + _planet.SurfaceRadius, _planet.Center.Y, _planet.Center.Z);
                double scale = _planet.SurfaceRadius / d;
                return new Vec3(_planet.Center.X + (posKm.X - _planet.Center.X) * scale,
                                 _planet.Center.Y + (posKm.Y - _planet.Center.Y) * scale,
                                 _planet.Center.Z + (posKm.Z - _planet.Center.Z) * scale);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>A parent planet with a moon orbiting inside its gravity well — the configuration
        /// where "nearest planet by center distance" and "planet whose gravity acts here" diverge.
        /// Gravity falls off linearly from each body's own surface to its own gravity radius, and the
        /// strongest contribution wins, so a point can read the parent's gravity while sitting closer
        /// to the moon's center.</summary>
        private sealed class MoonInsideParentWellCatalogue : IPlanetCatalogue
        {
            internal static readonly Vec3 ParentCenter = new Vec3(0, 0, 0);
            internal static readonly Vec3 MoonCenter = new Vec3(150, 0, 0);
            internal const double ParentSurfaceRadius = 60;
            internal const double ParentGravityRadius = 105;
            internal const double MoonSurfaceRadius = 9.5;
            internal const double MoonGravityRadius = 12.3;

            private readonly PlanetRecord _parent = new PlanetRecord
            {
                Id = "Parent", Center = ParentCenter,
                SurfaceRadius = ParentSurfaceRadius, GravityRadius = ParentGravityRadius,
                AtmosphereRadius = ParentSurfaceRadius, HasAtmosphere = true,
            };
            private readonly PlanetRecord _moon = new PlanetRecord
            {
                Id = "Moon", Center = MoonCenter,
                SurfaceRadius = MoonSurfaceRadius, GravityRadius = MoonGravityRadius,
                AtmosphereRadius = MoonSurfaceRadius, HasAtmosphere = false,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return _parent; yield return _moon; } }

            public double GravityAt(Vec3 posKm)
            {
                double best = 0;
                foreach (PlanetRecord p in Planets)
                {
                    double d = posKm.DistanceTo(p.Center);
                    double g;
                    if (d <= p.SurfaceRadius) g = 1.0;
                    else if (d >= p.GravityRadius) g = 0;
                    else g = (p.GravityRadius - d) / (p.GravityRadius - p.SurfaceRadius);
                    if (g > best) best = g;
                }
                return best;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        // 100 km from the parent's center (inside its 105 km well) but 99 km from the moon's center
        // (outside the moon's 12.3 km well) -- the moon is nearer, the parent supplies the gravity.
        private static readonly Vec3 MismatchZonePos = new Vec3(75.663, 65.385, 0);

        [Fact]
        public void GravityReference_UsesTheWellThePointIsIn_NotTheNearerCenter()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new MoonInsideParentWellCatalogue();

            double toParent = MismatchZonePos.DistanceTo(MoonInsideParentWellCatalogue.ParentCenter);
            double toMoon = MismatchZonePos.DistanceTo(MoonInsideParentWellCatalogue.MoonCenter);
            Assert.True(toMoon < toParent, "fixture must place the moon nearer by center distance");
            Assert.True(toParent < MoonInsideParentWellCatalogue.ParentGravityRadius, "must sit inside the parent's well");
            Assert.True(toMoon > MoonInsideParentWellCatalogue.MoonGravityRadius, "must sit outside the moon's well");

            Assert.Equal(NodeZone.Orbit, w.ClassifyZone(MismatchZonePos));
        }

        /// <summary>An Orbit node in the mismatch zone must be snapped into the band of the planet
        /// actually holding it, a short radial correction — not flung across to the nearer-by-center
        /// moon's band, which sits ~87 km away.</summary>
        [Fact]
        public void ReclassifyNodes_OrbitSnap_StaysInTheWellThatSuppliesTheGravity()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new MoonInsideParentWellCatalogue();

            SpatialNode n = w.Graph.Add(MismatchZonePos, 0.5, "N1");
            n.Zone = NodeZone.Orbit;

            NodeReclassifyResult applied = w.ReclassifyNodes(applyFix: true);
            Assert.Equal(1, applied.OrbitRepositioned);

            double toParent = n.Position.DistanceTo(MoonInsideParentWellCatalogue.ParentCenter);
            double toMoon = n.Position.DistanceTo(MoonInsideParentWellCatalogue.MoonCenter);

            // Landed in the parent's [surface, gravity] band...
            Assert.InRange(toParent,
                MoonInsideParentWellCatalogue.ParentSurfaceRadius,
                MoonInsideParentWellCatalogue.ParentGravityRadius);
            // ...at the radius where the parent's relative gravity equals the band ceiling.
            Assert.Equal(102.75, toParent, 1);
            // ...and nowhere near the moon's own band.
            Assert.True(toMoon > MoonInsideParentWellCatalogue.MoonGravityRadius,
                "node must not be snapped into the moon's band");

            // The correction is a few km, not the ~87 km jump the nearer-center lookup produced.
            Assert.True(n.Position.DistanceTo(MismatchZonePos) < 10.0);
        }

        /// <summary>Interior falloff matching the live host (measured with `factions gravityprobe`):
        /// gravity decreases linearly to zero going INWARD from the surface, so deep inside the body
        /// it reads far below the surface baseline. Every other fake here holds gravity constant
        /// inside the body, which cannot exercise the buried-node case at all.</summary>
        private sealed class SolidBodyGravityCatalogue : IPlanetCatalogue
        {
            internal const double SurfaceR = 60;
            internal const double GravityR = 101.55;
            private const double PeakG = 0.9;
            private readonly PlanetRecord _p = new PlanetRecord
            {
                Id = "Solid", Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                AtmosphereRadius = SurfaceR, HasAtmosphere = false,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return _p; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d >= GravityR) return 0;
                if (d <= SurfaceR) return PeakG * (d / SurfaceR);          // linear to zero at the core
                return PeakG * (GravityR - d) / (GravityR - SurfaceR);     // falls off outside
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d < 1e-6) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>A node buried below roughly half the surface radius reads under
        /// SurfaceGravityThreshold, but it is inside the body and must classify Surface -- otherwise
        /// the orbit snap flings it out of the planet instead of the Surface snap lifting it up.</summary>
        [Theory]
        [InlineData(0.0)]     // dead centre, gravity ~0
        [InlineData(0.393)]   // the live IGSL N628 case
        [InlineData(0.49)]    // just under the threshold crossing
        [InlineData(0.90)]    // already above it before the fix
        public void ClassifyZone_InsideTheBody_IsSurfaceWhateverTheRelativeReading(double fractionOfRadius)
        {
            WorldModel w = TestSupport.World();
            w.Planets = new SolidBodyGravityCatalogue();

            var pos = new Vec3(SolidBodyGravityCatalogue.SurfaceR * fractionOfRadius, 0, 0);
            Assert.Equal(NodeZone.Surface, w.ClassifyZone(pos));
        }

        [Fact]
        public void ClassifyZone_OutsideTheBody_StillUsesTheRelativeReading()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new SolidBodyGravityCatalogue();

            // Well out in the band: below the surface threshold, and genuinely not inside the body.
            Assert.Equal(NodeZone.Orbit, w.ClassifyZone(new Vec3(95, 0, 0)));
            Assert.Equal(NodeZone.DeepSpace, w.ClassifyZone(new Vec3(SolidBodyGravityCatalogue.GravityR + 5, 0, 0)));
        }

        /// <summary>The repair a buried node needs: lifted to the surface sphere, not flung to the
        /// orbit shell. Guards the whole point of the ClassifyZone fix end-to-end.</summary>
        [Fact]
        public void ReclassifyNodes_BuriedNode_IsLiftedToTheSurface_NotFlungToOrbit()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new SolidBodyGravityCatalogue();

            SpatialNode n = w.Graph.Add(new Vec3(SolidBodyGravityCatalogue.SurfaceR * 0.393, 0, 0), 0.5, "N628");
            n.Zone = NodeZone.Orbit;

            NodeReclassifyResult applied = w.ReclassifyNodes(applyFix: true);

            Assert.Equal(NodeZone.Surface, n.Zone);
            Assert.Equal(1, applied.Repositioned);       // Surface snap
            Assert.Equal(0, applied.OrbitRepositioned);  // NOT the orbit snap
            Assert.Equal(SolidBodyGravityCatalogue.SurfaceR, n.Position.DistanceTo(new Vec3(0, 0, 0)), 3);
        }

        /// <summary>A Surface snap can start anywhere inside the body, so the fence must allow a move
        /// of up to the surface radius even when that exceeds the narrower orbit band.</summary>
        [Fact]
        public void MaxPlausibleSnapDistance_AllowsAFullSurfaceRadiusMove()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new SolidBodyGravityCatalogue();

            double band = SolidBodyGravityCatalogue.GravityR - SolidBodyGravityCatalogue.SurfaceR; // 41.55
            Assert.True(SolidBodyGravityCatalogue.SurfaceR > band, "fixture must have surface radius exceed the band");
            Assert.Equal(SolidBodyGravityCatalogue.SurfaceR, w.MaxPlausibleSnapDistanceKm(new Vec3(10, 0, 0)), 6);
        }

        /// <summary>The drift sweep's safety fence: a legitimate reposition cannot move a node
        /// further than its gravity planet's band width, so anything beyond that is not a safe
        /// automatic re-anchor.</summary>
        [Fact]
        public void MaxPlausibleSnapDistance_ResolvesAgainstTheGravityPlanet_NotTheNearerCenter()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new MoonInsideParentWellCatalogue();

            // In the mismatch zone the parent supplies the gravity, so the PARENT bounds the move --
            // not the nearer-by-center moon, whose own reach is far smaller.
            Assert.Equal(MoonInsideParentWellCatalogue.ParentSurfaceRadius,
                w.MaxPlausibleSnapDistanceKm(MismatchZonePos), 6);

            // Right next to the moon, inside its own well, the moon's reach applies.
            Vec3 byMoon = new Vec3(MoonInsideParentWellCatalogue.MoonCenter.X + 11, 0, 0);
            Assert.Equal(MoonInsideParentWellCatalogue.MoonSurfaceRadius,
                w.MaxPlausibleSnapDistanceKm(byMoon), 6);
            Assert.True(w.MaxPlausibleSnapDistanceKm(byMoon)
                      < w.MaxPlausibleSnapDistanceKm(MismatchZonePos));
        }

        [Fact]
        public void MaxPlausibleSnapDistance_NoPlanets_IsZero()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            Assert.Equal(0.0, w.MaxPlausibleSnapDistanceKm(new Vec3(0, 0, 0)), 6);
        }

        [Fact]
        public void ReclassifyNodes_OrbitSnap_InMismatchZone_IsIdempotent()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new MoonInsideParentWellCatalogue();

            SpatialNode n = w.Graph.Add(MismatchZonePos, 0.5, "N1");
            n.Zone = NodeZone.Orbit;

            w.ReclassifyNodes(applyFix: true);
            Vec3 afterFirst = n.Position;
            NodeReclassifyResult second = w.ReclassifyNodes(applyFix: true);

            Assert.Equal(0, second.OrbitRepositioned);
            Assert.Equal(0.0, n.Position.DistanceTo(afterFirst), 6);
        }

        [Fact]
        public void ClassifyZone_LowGravityPlanetSurface_ClassifiesSurfaceRelativeNotAbsolute()
        {
            // Raw g at the surface is only 0.3 -- well below the OLD absolute SurfaceGravityThreshold
            // (0.5), which would have misclassified this as Orbit/DeepSpace. Relative math correctly
            // reads it as 100% of this planet's own true surface gravity -> Surface. This is the actual
            // regression case (the operator's "the Moon" concern).
            WorldModel w = TestSupport.World();
            w.Planets = new LowGravityPlanetCatalogue(peakG: 0.3, surfaceRadius: 10, gravityRadius: 100);

            NodeZone zone = w.ClassifyZone(new Vec3(10, 0, 0)); // exactly at SurfaceRadius
            Assert.Equal(NodeZone.Surface, zone);
        }

        [Fact]
        public void ClassifyZone_LowGravityPlanetMidBand_ClassifiesOrbit()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new LowGravityPlanetCatalogue(peakG: 0.3, surfaceRadius: 10, gravityRadius: 100);

            // d=82 -> relativeG = (100-82)/90 = 0.2 -- between the Orbit snap ceiling and the Surface
            // threshold on any planet, regardless of its absolute peak gravity.
            NodeZone zone = w.ClassifyZone(new Vec3(82, 0, 0));
            Assert.Equal(NodeZone.Orbit, zone);
        }

        [Fact]
        public void ClassifyZone_ZeroRawGravity_ClassifiesDeepSpace()
        {
            WorldModel w = TestSupport.World();
            w.Planets = new LowGravityPlanetCatalogue(peakG: 0.3, surfaceRadius: 10, gravityRadius: 100);

            Assert.Equal(NodeZone.DeepSpace, w.ClassifyZone(new Vec3(200, 0, 0))); // outside GravityRadius

            WorldModel noPlanets = TestSupport.World(); // default NullPlanetCatalogue -- g=0 everywhere
            Assert.Equal(NodeZone.DeepSpace, noPlanets.ClassifyZone(new Vec3(0, 0, 0)));
        }

        [Fact]
        public void ClassifyZone_ConstantGravityCatalogue_MatchesLegacyAbsoluteBehavior_RegressionGuard()
        {
            // ConstantGravityCatalogue can only usefully represent the two binary cases below --
            // its position-independent GravityAt plus identity NearestSurfacePoint make relativeG
            // saturate to exactly 1.0 for ANY nonzero g (see LowGravityPlanetCatalogue's own doc
            // comment), so it cannot represent a meaningful mid-band Orbit case; that's covered above.
            WorldModel w = TestSupport.World();

            w.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);
            Assert.Equal(NodeZone.Surface, w.ClassifyZone(new Vec3(0, 0, 0)));

            w.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);
            Assert.Equal(NodeZone.DeepSpace, w.ClassifyZone(new Vec3(0, 0, 0)));
        }

        [Fact]
        public void ReclassifyNodes_OrbitNodeOutsideBand_RepositionsOnlyWhenApplyFixTrue()
        {
            WorldModel w = TestSupport.World();
            var planets = new LowGravityPlanetCatalogue(peakG: 0.3, surfaceRadius: 10, gravityRadius: 100);
            w.Planets = planets;

            // d=70 -> relativeG = (100-70)/90 = 0.333 -- inside the wide legacy Orbit band and well
            // outside the new tight OrbitGravityMin ceiling (default 0.05), so this must reposition.
            SpatialNode n = w.Graph.Add(new Vec3(70, 0, 0), 0.5, "N1");

            NodeReclassifyResult dryRun = w.ReclassifyNodes(applyFix: false);
            Assert.Equal(1, dryRun.OrbitRepositioned);
            Assert.Equal(70.0, n.Position.X); // dry run -- unchanged

            NodeReclassifyResult applied = w.ReclassifyNodes(applyFix: true);
            Assert.Equal(1, applied.OrbitRepositioned);
            Assert.NotEqual(70.0, n.Position.X); // moved

            double g = planets.GravityAt(n.Position);
            double localSurfaceG = planets.GravityAt(planets.NearestSurfacePoint(n.Position));
            double relativeG = g / localSurfaceG;
            Assert.True(relativeG > 0 && relativeG <= w.Tuning.OrbitGravityMin + 1e-6,
                "relativeG=" + relativeG + " should be within the snap band");

            // A third pass with nothing left to fix reports zero -- idempotent, not a one-shot count.
            NodeReclassifyResult stable = w.ReclassifyNodes(applyFix: true);
            Assert.Equal(0, stable.OrbitRepositioned);
        }

        [Fact]
        public void ExpandFrom_OrbitCandidate_SnapsIntoTightenedRelativeBand()
        {
            var planets = new LowGravityPlanetCatalogue(peakG: 0.3, surfaceRadius: 10, gravityRadius: 100);

            // SequenceRandom(0.75, 0.5, 0.5) forces RandomPointOnSphere's rejection sample to (x,y,z)
            // = (0.5, 0, 0), i.e. direction (1,0,0) -- the candidate lands at exactly (ExpandStep, 0, 0)
            // from the parent (placed at the planet's Center) on the first attempt. Uniform separation
            // floors of 1.0km (below) cap every attempt's score at exactly 1.0 via Math.Min(nearestDistance,
            // reachCap); ties keep the FIRST candidate reaching that cap (strict '>' comparison), so
            // later attempts -- which drain the sequence and fall back to a degenerate/guarded direction
            // once exhausted -- can never displace this deliberately engineered attempt-0 candidate.
            WorldModel w = new WorldModel(new SequenceRandom(0.75, 0.5, 0.5))
            {
                Planets = planets,
                ExpandStep = 60, // relativeG here = (100-60)/90 = 0.444 -- comfortably below the 0.5 Surface threshold
                Tuning = new FactionTuning
                {
                    NodeExpansionSampleCount = 1,
                    NodeExpansionReachPct = 0,
                    NodeSeparationSurfaceKm = 1.0,
                    NodeSeparationOrbitKm = 1.0,
                    NodeSeparationDeepSpaceKm = 1.0,
                    SurfaceGravityThreshold = 0.5,
                    OrbitGravityMin = 0.05,
                }
            };

            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "P001");
            parent.OwnerFactionTag = "F";

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.NotNull(child);
            Assert.Equal(NodeZone.Orbit, child.Zone);

            double g = planets.GravityAt(child.Position);
            double localSurfaceG = planets.GravityAt(planets.NearestSurfacePoint(child.Position));
            double relativeG = g / localSurfaceG;
            Assert.True(relativeG > 0 && relativeG <= w.Tuning.OrbitGravityMin + 1e-6,
                "relativeG=" + relativeG + " should be within the snap band");
        }

        // ---- ClearOrbitStranding: cancel doomed builds + dismantle stranded stock -------------

        private sealed class SimpleRecipeRegistry : IRecipeRegistry
        {
            public readonly Dictionary<string, Dictionary<string, long>> BomByPrefab = new Dictionary<string, Dictionary<string, long>>();

            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                Dictionary<string, long> bom;
                if (outputTypeId != "Prefab" || !BomByPrefab.TryGetValue(outputSubTypeId, out bom))
                    yield break;
                var inputs = new List<RecipeItem>();
                foreach (var kv in bom)
                    inputs.Add(new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value });
                yield return new ConversionRecipe { BlueprintId = outputSubTypeId, Inputs = inputs.ToArray(), Outputs = new RecipeItem[0] };
            }

            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return BomByPrefab.Count; } }
        }

        private static WorldModel ClearStrandingWorld(out ThrustFakeCatalogue cat, out SimpleRecipeRegistry recipes)
        {
            WorldModel w = TestSupport.World();
            cat = new ThrustFakeCatalogue();
            // Weak Atmospheric thruster -- fails MinDryTwr (1.05) at g=1.0 in atmosphere by a wide margin.
            cat.SetAtmoOnlyDown("StrandedHull", dryMassKg: 5000, forceN: 100);
            // Hydrogen thruster -- full effectiveness everywhere, always feasible.
            cat.SetHydrogenDown("SafeHull", dryMassKg: 1000, forceN: 50000);
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            recipes = new SimpleRecipeRegistry();
            recipes.BomByPrefab["StrandedHull"] = new Dictionary<string, long> { ["Steel"] = 50 };
            w.Recipes = recipes;
            return w;
        }

        [Fact]
        public void ClearOrbitStranding_DryRun_ReportsCountsWithoutMutating()
        {
            ThrustFakeCatalogue cat; SimpleRecipeRegistry recipes;
            WorldModel w = ClearStrandingWorld(out cat, out recipes);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.BuildQueue.Add(new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "StrandedHull" });
            n.BuildQueue[0].Collected["Comp:Steel"] = 20;
            n.ShipStock["StrandedHull"] = 3;

            OrbitStrandingClearResult result = w.ClearOrbitStranding(applyFix: false);

            Assert.Equal(1, result.BuildsCanceled);
            Assert.Equal(20, result.BuildItemsRefunded);
            Assert.Equal(3, result.ShipsDismantled);
            Assert.Equal(150, result.BomUnitsCredited); // 50 Comp:Steel per unit x 3 units

            // Nothing actually mutated.
            Assert.Single(n.BuildQueue);
            Assert.Equal(20, n.BuildQueue[0].Collected["Comp:Steel"]);
            Assert.Equal(3, n.ShipStock["StrandedHull"]);
            Assert.False(n.Inventory.ContainsKey("Comp:Steel"));
        }

        [Fact]
        public void ClearOrbitStranding_ApplyMode_CancelsBuild_RefundsCollected_LogsTag()
        {
            ThrustFakeCatalogue cat; SimpleRecipeRegistry recipes;
            WorldModel w = ClearStrandingWorld(out cat, out recipes);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.BuildQueue.Add(new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "StrandedHull" });
            n.BuildQueue[0].Collected["Comp:Steel"] = 20;

            var log = new List<string>();
            OrbitStrandingClearResult result = w.ClearOrbitStranding(applyFix: true, log: log);

            Assert.Equal(1, result.BuildsCanceled);
            Assert.Empty(n.BuildQueue);
            Assert.Equal(20, n.Inventory["Comp:Steel"]);
            Assert.Contains(log, l => l.Contains("BUILD_CANCELED_ZONE") && l.Contains("node=N1") && l.Contains("prefab=StrandedHull") && l.Contains("refunded=20"));
        }

        [Fact]
        public void ClearOrbitStranding_SkipsFreeBuild_NullPrefabId()
        {
            ThrustFakeCatalogue cat; SimpleRecipeRegistry recipes;
            WorldModel w = ClearStrandingWorld(out cat, out recipes);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.BuildQueue.Add(new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = null });
            n.BuildQueue[0].Collected["Comp:Steel"] = 5;

            OrbitStrandingClearResult result = w.ClearOrbitStranding(applyFix: true);

            Assert.Equal(0, result.BuildsCanceled);
            Assert.Single(n.BuildQueue);
        }

        [Fact]
        public void ClearOrbitStranding_DismantlesInfeasibleShipStock_CreditsFullBom_ExcludesListedForSale()
        {
            ThrustFakeCatalogue cat; SimpleRecipeRegistry recipes;
            WorldModel w = ClearStrandingWorld(out cat, out recipes);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["StrandedHull"] = 5;
            n.ListedForSale["StrandedHull"] = 2; // player-purchasable -- must not be dismantled

            var log = new List<string>();
            OrbitStrandingClearResult result = w.ClearOrbitStranding(applyFix: true, log: log);

            Assert.Equal(3, result.ShipsDismantled); // Available = 5 - 2
            Assert.Equal(150, result.BomUnitsCredited); // 50 x 3
            Assert.Equal(2, n.ShipStock["StrandedHull"]); // only the listed-for-sale units remain
            Assert.Equal(2, n.ListedForSale["StrandedHull"]); // untouched
            Assert.Equal(150, n.Inventory["Comp:Steel"]);
            Assert.Contains(log, l => l.Contains("SHIP_DISMANTLED_STRANDED") && l.Contains("count=3") && l.Contains("bomCredited=150"));
        }

        [Fact]
        public void ClearOrbitStranding_FeasibleStock_NotDismantled_RegressionGuard()
        {
            ThrustFakeCatalogue cat; SimpleRecipeRegistry recipes;
            WorldModel w = ClearStrandingWorld(out cat, out recipes);

            SpatialNode n = Owned(w, "N1", 1.0);
            n.ShipStock["SafeHull"] = 4;

            OrbitStrandingClearResult result = w.ClearOrbitStranding(applyFix: true);

            Assert.Equal(0, result.ShipsDismantled);
            Assert.Equal(4, n.ShipStock["SafeHull"]);
        }

        [Fact]
        public void PendingDemandHistorySnapshot_IsNonDestructive()
        {
            WorldModel world = TestSupport.World();
            world.AddPendingDemandHistoryCredit("N1", "Ore", 10);
            var method = typeof(WorldModel).GetMethod("SnapshotPendingDemandHistory");
            Assert.NotNull(method);

            var snapshot = Assert.IsType<Dictionary<string, double>>(method.Invoke(world, new object[] { "N1" }));
            Assert.Equal(10, snapshot["Ore"]);
            Dictionary<string, double> drained = world.DrainPendingDemandHistory("N1");
            Assert.Equal(10, drained["Ore"]);
        }

        [Fact]
        public void PendingDemandHistoryConditionalConsume_RejectsChangedCredit_ThenConsumesExactSnapshot()
        {
            WorldModel world = TestSupport.World();
            world.AddPendingDemandHistoryCredit("N1", "Ore", 10);
            var snapshotMethod = typeof(WorldModel).GetMethod("SnapshotPendingDemandHistory");
            var consumeMethod = typeof(WorldModel).GetMethod("TryConsumePendingDemandHistory");
            Assert.NotNull(snapshotMethod);
            Assert.NotNull(consumeMethod);

            var stale = Assert.IsType<Dictionary<string, double>>(snapshotMethod.Invoke(world, new object[] { "N1" }));
            world.AddPendingDemandHistoryCredit("N1", "Ore", 5);
            Assert.False((bool)consumeMethod.Invoke(world, new object[] { "N1", stale }));

            var current = Assert.IsType<Dictionary<string, double>>(snapshotMethod.Invoke(world, new object[] { "N1" }));
            Assert.Equal(15, current["Ore"]);
            Assert.True((bool)consumeMethod.Invoke(world, new object[] { "N1", current }));
            Assert.Null(world.DrainPendingDemandHistory("N1"));
        }

        [Fact]
        public void ClearOrbitStranding_ComposesReclassify_EvaluatesFeasibilityAtNewPosition()
        {
            // Weak Ion-style thruster: strong at low gravity (mult=1.0 at g=0), weak at high gravity
            // (mult=0.2 at g=1.0) -- infeasible at the node's stale wide-Orbit-band position, feasible
            // once ReclassifyNodes moves it into the tight low-gravity snap band.
            var cat = new ThrustFakeCatalogue();
            cat.DryMassKgByPrefab["IonHull"] = 1000;
            cat.ThrustBandsByPrefab["IonHull"] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                {
                    ThrusterType = "Ion", ForceMagnitudeN = 3000,
                    MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                    EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
                    NeedsAtmosphereForInfluence = false,
                }},
            };

            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = cat;
            var planets = new LowGravityPlanetCatalogue(peakG: 1.0, surfaceRadius: 10, gravityRadius: 100);
            w.Planets = planets;

            // d=56 -> relativeG = (100-56)/90 = 0.489 -- Orbit (just under the 0.5 Surface threshold),
            // deep enough in the well that IonHull's weak thrust fails MinDryTwr (1.05) here.
            SpatialNode n = w.Graph.Add(new Vec3(56, 0, 0), 0.5, "N1");
            n.OwnerFactionTag = "F";
            n.ShipStock["IonHull"] = 2;

            OrbitStrandingClearResult dryRun = w.ClearOrbitStranding(applyFix: false);
            Assert.Equal(2, dryRun.ShipsDismantled); // dry run doesn't move the node -- still infeasible there

            OrbitStrandingClearResult applied = w.ClearOrbitStranding(applyFix: true);
            Assert.Equal(0, applied.ShipsDismantled); // apply mode repositions FIRST -- now feasible
            Assert.Equal(2, n.ShipStock["IonHull"]); // untouched
        }
    }
}
