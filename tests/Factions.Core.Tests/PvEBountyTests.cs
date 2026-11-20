using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the Core-side surface Guard-via-PvEBounty actually needs: the pure, side-effect-free
    /// station lookup used at contract-generation time (<see
    /// cref="PendingFleetResolver.TryFindIncomingThreat"/>), and the new <c>onFleetRemoved</c>
    /// callback on <see cref="PendingFleetResolver.ResolveArrivals"/> -- the single convergence point
    /// for "this Raid/Retaliate/Conquer fleet is done, win or lose" that a torch-host bounty tracker
    /// keyed by FleetId can react to. Per docs/contracts.md, generating or merely holding an accepted
    /// contract must have zero effect on the raid itself, so there is deliberately no Core-side pause,
    /// link, or timeout mechanism left to test here -- that's the point. The SE-side contract
    /// generation/acceptance/completion hooks themselves are Torch-host code and out of scope for
    /// these Core tests.
    /// </summary>
    public class PvEBountyTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }
        private static IRandom NoJitter() => new FixedRandom(0.5);

        private static PendingFleetAction MakeRaid(WorldModel w, string actor, string target, string targetNode, string fleetId)
        {
            DateTime now = w.UtcNow;
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = actor, TargetTag = target, TargetNodeId = targetNode,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, FleetId = fleetId,
            };
        }

        [Fact]
        public void TryFindIncomingThreat_FindsRaidTargetingNode()
        {
            WorldModel w = MakeWorld();
            var stRaider = new FactionState { Tag = "RVN" };
            stRaider.PendingFleets.Add(MakeRaid(w, "RVN", "MERC", "MERC1", "aaaaaa"));
            var states = new List<FactionState> { stRaider };

            PendingFleetAction found;
            bool ok = PendingFleetResolver.TryFindIncomingThreat(states, "MERC1", out found);

            Assert.True(ok);
            Assert.Equal("aaaaaa", found.FleetId);
        }

        [Fact]
        public void TryFindIncomingThreat_IgnoresWrongNode_Resolved_Intercepted_AndMissingFleetId()
        {
            WorldModel w = MakeWorld();
            var st = new FactionState { Tag = "RVN" };
            st.PendingFleets.Add(MakeRaid(w, "RVN", "MERC", "OTHER", "wrongnode"));

            var resolved = MakeRaid(w, "RVN", "MERC", "MERC1", "resolved1");
            resolved.Resolved = true;
            st.PendingFleets.Add(resolved);

            var intercepted = MakeRaid(w, "RVN", "MERC", "MERC1", "intercepted1");
            intercepted.Intercepted = true;
            st.PendingFleets.Add(intercepted);

            var noFleetId = MakeRaid(w, "RVN", "MERC", "MERC1", null);
            st.PendingFleets.Add(noFleetId);

            var states = new List<FactionState> { st };
            PendingFleetAction found;
            bool ok = PendingFleetResolver.TryFindIncomingThreat(states, "MERC1", out found);

            Assert.False(ok);
        }

        [Fact]
        public void TryFindIncomingThreat_DoesNotCareAboutMaterialisation()
        {
            // Generation-time eligibility must not depend on whether the fleet has ever been
            // materialised -- the whole point of the corrected design is that the contract is
            // offerable purely because the raid is real and in transit.
            WorldModel w = MakeWorld();
            var st = new FactionState { Tag = "RVN" };
            var raid = MakeRaid(w, "RVN", "MERC", "MERC1", "aaaaaa");
            raid.MaterialisedEntityId = 0;
            st.PendingFleets.Add(raid);
            var states = new List<FactionState> { st };

            PendingFleetAction found;
            Assert.True(PendingFleetResolver.TryFindIncomingThreat(states, "MERC1", out found));
        }

        [Fact]
        public void ResolveArrivals_InvokesOnFleetRemoved_WhenRaidResolvesNormally()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var stRaider = new FactionState { Tag = "RVN" };
            stRaider.PendingFleets.Add(MakeRaid(w, "RVN", "MERC", "MERC1", "aaaaaa"));
            var states = new List<FactionState> { stRaider };

            var removedFleetIds = new List<string>();
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null,
                onFleetRemoved: id => removedFleetIds.Add(id));

            // No defender present -> resolves via the bare station-defense fallback either way
            // (destroyed or breaks through); either outcome removes it from PendingFleets this tick.
            Assert.Empty(stRaider.PendingFleets);
            Assert.Equal(new List<string> { "aaaaaa" }, removedFleetIds);
        }

        [Fact]
        public void ResolveArrivals_InvokesOnFleetRemoved_ForInterceptedFleet()
        {
            WorldModel w = MakeWorld();
            var st = new FactionState { Tag = "RVN" };
            var intercepted = MakeRaid(w, "RVN", "MERC", "MERC1", "bbbbbb");
            intercepted.Intercepted = true;
            st.PendingFleets.Add(intercepted);
            var states = new List<FactionState> { st };

            var removedFleetIds = new List<string>();
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null,
                onFleetRemoved: id => removedFleetIds.Add(id));

            Assert.Empty(st.PendingFleets);
            Assert.Equal(new List<string> { "bbbbbb" }, removedFleetIds);
        }

        [Fact]
        public void ResolveArrivals_DoesNotInvokeOnFleetRemoved_ForInFlightFleet()
        {
            WorldModel w = MakeWorld();
            var st = new FactionState { Tag = "RVN" };
            PendingFleetAction inFlight = MakeRaid(w, "RVN", "MERC", "MERC1", "cccccc");
            inFlight.EstimatedArrivalUtc = w.UtcNow.AddMinutes(10); // not arrived yet
            st.PendingFleets.Add(inFlight);
            var states = new List<FactionState> { st };

            var removedFleetIds = new List<string>();
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null,
                onFleetRemoved: id => removedFleetIds.Add(id));

            Assert.Single(st.PendingFleets); // still in flight, untouched
            Assert.Empty(removedFleetIds);
        }

        [Fact]
        public void ReconcileAndResolveOnLoad_ForwardsOnFleetRemoved()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var stRaider = new FactionState { Tag = "RVN" };
            var overdue = MakeRaid(w, "RVN", "MERC", "MERC1", "dddddd");
            overdue.DepartedUtc = w.UtcNow.AddMinutes(-50);
            overdue.EstimatedArrivalUtc = w.UtcNow.AddMinutes(-10); // overdue -> force-resolved at full ratio
            stRaider.PendingFleets.Add(overdue);
            var states = new List<FactionState> { stRaider };

            var removedFleetIds = new List<string>();
            var log = new List<string>();
            PendingFleetResolver.ReconcileAndResolveOnLoad(states, w, w.Tuning, NoJitter(), log, null,
                onFleetRemoved: id => removedFleetIds.Add(id));

            Assert.Empty(stRaider.PendingFleets);
            Assert.Equal(new List<string> { "dddddd" }, removedFleetIds);
        }
    }
}
