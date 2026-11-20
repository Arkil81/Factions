using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A fleet's id names one fleet: minted unique within its actor's roster at dispatch,
    /// re-minted on load where a brain repeats one, and a dismissed copy goes home with its hull and
    /// cargo instead of being written off.</summary>
    public class FleetIdMintTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 20, 22, 39, DateTimeKind.Utc);

        private static PendingFleetAction Haul(string id, string target = "N217")
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP", OriginNodeId = "Home", TargetNodeId = target,
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddMinutes(5), FleetId = id,
                ShipPrefabId = "Hauler", ConvoySize = 1, LastResolvedSegment = -1,
            };
        }

        [Fact]
        public void MintId_AgainstAnEmptyRoster_IsTheInputHash()
        {
            string plain = PendingFleetAction.MintId((IList<PendingFleetAction>)null, "EXP", T0, "N217", "N600:Gas:Hydrogen");
            string minted = PendingFleetAction.MintId(new List<PendingFleetAction>(), "EXP", T0, "N217", "N600:Gas:Hydrogen");
            Assert.Equal(plain, minted);
            Assert.Equal(6, minted.Length);
        }

        [Fact]
        public void MintId_SameInputsTwice_AgainstTheRoster_AreDistinct_AndDeterministic()
        {
            var roster = new List<PendingFleetAction>();
            string first = PendingFleetAction.MintId(roster, "EXP", T0, "N217", "N600:Gas:Hydrogen");
            roster.Add(Haul(first));
            string second = PendingFleetAction.MintId(roster, "EXP", T0, "N217", "N600:Gas:Hydrogen");
            Assert.NotEqual(first, second);
            Assert.Equal(second, PendingFleetAction.MintId(roster, "EXP", T0, "N217", "N600:Gas:Hydrogen"));

            roster.Add(Haul(second));
            string third = PendingFleetAction.MintId(roster, "EXP", T0, "N217", "N600:Gas:Hydrogen");
            Assert.DoesNotContain(third, new[] { first, second });
        }

        [Fact]
        public void StagedCycle_MintsDistinctIds_BeforeAnyFleetIsCommitted()
        {
            // A staged cycle's fleets are not in the roster until commit, so the cycle itself must
            // remember what it minted -- and must not reuse an id the roster already holds.
            var world = TestSupport.World();
            var st = new FactionState { Tag = "EXP" };
            string held = PendingFleetAction.MintId((IList<PendingFleetAction>)null, "EXP", T0, "N217", "x");
            st.PendingFleets.Add(Haul(held));

            OperationsStagedWorldEffects effects = OperationsStagedWorldEffects.Capture(st, world);
            string a = effects.MintFleetId("EXP", T0, "N217", "x");
            string b = effects.MintFleetId("EXP", T0, "N217", "x");
            Assert.NotEqual(held, a);
            Assert.NotEqual(held, b);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void RemintDuplicateIds_FirstHolderKeepsItsId_LaterOnesGetTheirOwn()
        {
            var roster = new List<PendingFleetAction>
            {
                Haul("40A672"), Haul("40A672", "N265"), Haul("D82EC5"), Haul("40A672", "N011"), Haul(null),
            };

            Assert.Equal(2, PendingFleetAction.RemintDuplicateIds(roster));

            Assert.Equal("40A672", roster[0].FleetId);
            Assert.Equal("D82EC5", roster[2].FleetId);
            Assert.Null(roster[4].FleetId);
            var ids = roster.Take(4).Select(f => f.FleetId).ToList();
            Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(0, PendingFleetAction.RemintDuplicateIds(roster));
        }

        [Fact]
        public void BrainRoundTrip_KeepsUniqueIds_AndSplitsARepeatedOne()
        {
            var graph = new SpatialGraph();
            graph.Add(new Vec3(0, 0, 0), 0.5, "Home").OwnerFactionTag = "EXP";
            var st = new FactionState { Tag = "EXP" };
            PendingFleetAction clipper = Haul("40A672");
            clipper.ShipPrefabId = "Clipper";
            clipper.Cargo = new Dictionary<string, long> { { "Gas:Hydrogen", 4394654 } };
            PendingFleetAction laev = Haul("40A672");
            laev.ShipPrefabId = "Laevateinn";
            laev.Cargo = new Dictionary<string, long> { { "Gas:Hydrogen", 4188750 } };
            st.PendingFleets.Add(clipper);
            st.PendingFleets.Add(laev);
            st.PendingFleets.Add(Haul("D82EC5", "N600"));

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap));
                FactionState loaded = FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), new SpatialGraph());

                Assert.Equal(3, loaded.PendingFleets.Count);
                PendingFleetAction c = loaded.PendingFleets.Single(f => f.ShipPrefabId == "Clipper");
                PendingFleetAction l = loaded.PendingFleets.Single(f => f.ShipPrefabId == "Laevateinn");
                Assert.Equal("40A672", c.FleetId);
                Assert.NotEqual("40A672", l.FleetId);
                Assert.Equal("D82EC5", loaded.PendingFleets.Single(f => f.TargetNodeId == "N600").FleetId);
                Assert.False(l.IsDuplicateOf(c));
                Assert.Equal(4188750, l.Cargo["Gas:Hydrogen"]);
                Assert.Equal(4394654, c.Cargo["Gas:Hydrogen"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void IsDuplicateOf_MatchesOnlyACopy()
        {
            PendingFleetAction a = Haul("40A672");
            Assert.True(a.IsDuplicateOf(a));
            Assert.False(Haul("40A673").IsDuplicateOf(a));
            Assert.False(Haul("40A672", "N265").IsDuplicateOf(a));
            PendingFleetAction later = a;
            later.DepartedUtc = T0.AddSeconds(1);
            Assert.False(later.IsDuplicateOf(a));
        }

        [Fact]
        public void DismissedDuplicate_ReturnsItsHullAndCargoToOrigin_InsteadOfBeingWrittenOff()
        {
            var world = TestSupport.World();
            world.UtcNow = T0.AddMinutes(1);
            SpatialNode home = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Home");
            home.OwnerFactionTag = "EXP";
            home.HoldStrength = 1.0;
            SpatialNode pickup = world.Graph.Add(new Vec3(50, 0, 0), 1.0, "N217");
            pickup.OwnerFactionTag = "EXP";
            pickup.HoldStrength = 1.0;

            // What DismissSiblings writes on a copy of a fleet whose grid was lost.
            PendingFleetAction dup = Haul("40A672");
            dup.Cargo = new Dictionary<string, long> { { "Ingot:Iron", 40 } };
            dup.MaterialisedEntityId = -1;
            dup.ForcedRatio = 0.0;
            dup.ForcedRatioCause = PendingFleetResolver.SiblingDismissCause;
            dup.EstimatedArrivalUtc = world.UtcNow;
            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(dup);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, world.Tuning,
                                                 new SeededRandom(1), log);

            Assert.DoesNotContain(st.PendingFleets, f => !f.Resolved);
            int stock;
            home.ShipStock.TryGetValue("Hauler", out stock);
            Assert.Equal(1, stock);
            Assert.Equal(40, world.GetNodeInventory("Home", "Ingot:Iron"));
            Assert.Equal(0, world.GetNodeInventory("N217", "Ingot:Iron"));
            Assert.Contains(log, l => l.StartsWith("FLEET_RECALLED [40A672] EXP kind=Haul node=Home hulls=1")
                                     && l.Contains("cargo=40") && l.Contains("why=sibling_dismiss"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_LOST"));
        }
    }
}
