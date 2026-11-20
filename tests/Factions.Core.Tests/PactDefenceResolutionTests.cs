using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The resolution half of pact defence (FAC-276): where an allied Guard's hull goes when its duty
    /// window closes, what an exercised pact is worth to the faction that was defended, and the
    /// one-defender-per-node rule now that the defender standing at a node can fly someone else's
    /// colours.
    ///
    /// <para>The cast is the live pair: SPRT raids an IMPR node, UVCO is IMPR's pact partner and the
    /// one that sends a Guard.</para>
    /// </summary>
    public class PactDefenceResolutionTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>Roll 0 — the decisive end of the duel jitter, so an overwhelming loadout wins
        /// outright rather than by a margin the seed could move.</summary>
        private static IRandom AlwaysWin() { return new FixedRandom(0.0); }

        private static List<WeaponBand> Bands(float dps, float rangeM = 2000)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            n.Radius = 40;
            return n;
        }

        private static FactionState MakeState(string tag)
        {
            return new FactionState { Tag = tag, RngSeed = 1 };
        }

        private static void Ally(FactionState a, FactionState b)
        {
            a.PactWith.Add(b.Tag);
            b.PactWith.Add(a.Tag);
        }

        /// <summary>A Guard whose duty window has already closed: resolved on arrival, and now past
        /// <c>EstimatedArrivalUtc + GuardDuration</c>, which is the expiry test the cleanup pass
        /// applies before it calls the hull home.</summary>
        private static PendingFleetAction ExpiredGuard(WorldModel w, string actor, string originId, string nodeId)
        {
            DateTime arrived = w.UtcNow - w.Tuning.GuardDuration - TimeSpan.FromMinutes(1);
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = actor,
                TargetNodeId = nodeId,
                OriginNodeId = originId,
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                DepartedUtc = arrived,
                EstimatedArrivalUtc = arrived,
                Resolved = true,
                WeaponBands = Bands(500),
            };
        }

        /// <summary>A Guard still inside its duty window, standing over <paramref name="nodeId"/>.</summary>
        private static PendingFleetAction StandingGuard(WorldModel w, string actor, string originId,
                                                        string nodeId, float dps)
        {
            DateTime arrived = w.UtcNow.AddMinutes(-5);
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = actor,
                TargetNodeId = nodeId,
                OriginNodeId = originId,
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                DepartedUtc = arrived,
                EstimatedArrivalUtc = arrived,
                Resolved = true,
                WeaponBands = Bands(dps),
            };
        }

        private static PendingFleetAction ArrivingRaid(WorldModel w, string actor, string targetTag,
                                                       string nodeId, float dps)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = actor,
                TargetTag = targetTag,
                TargetNodeId = nodeId,
                ConvoySize = 3,
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                WeaponBands = Bands(dps),
            };
        }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log)
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        private static int Stock(WorldModel w, string nodeId)
        {
            int n;
            w.Graph.Nodes[nodeId].ShipStock.TryGetValue("Gunship", out n);
            return n;
        }

        // ---- the hull comes home ----------------------------------------------------------------

        // Characteristic failure: the ally pays for the defence twice — once for the dispatch, and
        // again by handing the hull to the faction it defended, so its combat pool bleeds into its
        // partners' until it can no longer defend anyone.
        [Fact]
        public void AnAlliedGuardExpiring_CreditsItsHullAtItsOwnOrigin_NotTheDefendedNode()
        {
            WorldModel w = TestSupport.World();
            var owner = MakeState("IMPR");
            var ally  = MakeState("UVCO");
            Ally(owner, ally);
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "UVCO1", "UVCO", new Vec3(100, 0, 0));
            ally.PendingFleets.Add(ExpiredGuard(w, "UVCO", "UVCO1", "IMPR1"));

            PendingFleetAction guard = ally.PendingFleets[0];
            Assert.True(w.UtcNow >= guard.EstimatedArrivalUtc.Add(w.Tuning.GuardDuration),
                        "the fixture must outrun GuardDuration or no hull is returned at all");

            var states = new List<FactionState> { owner, ally };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Empty(ally.PendingFleets);
            Assert.Equal(1, Stock(w, "UVCO1"));
            Assert.Equal(0, Stock(w, "IMPR1"));
            Assert.Equal("SHIP_RETURNED UVCO prefab=Gunship node=UVCO1 count=1 stock=1 defended=IMPR1",
                         Line(log, "SHIP_RETURNED"));
        }

        // Characteristic failure: the override leaks into the ordinary case and a faction's own Guard
        // stops landing where it stood, or the line grows a token every existing reader has to parse.
        [Fact]
        public void AnOwnGuardExpiring_StillCreditsTheDefendedNode_AndTheLineIsUnchanged()
        {
            WorldModel w = TestSupport.World();
            var owner = MakeState("IMPR");
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "IMPR2", "IMPR", new Vec3(100, 0, 0));
            owner.PendingFleets.Add(ExpiredGuard(w, "IMPR", "IMPR2", "IMPR1"));

            var states = new List<FactionState> { owner };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(1, Stock(w, "IMPR1"));
            Assert.Equal(0, Stock(w, "IMPR2"));
            Assert.Equal("SHIP_RETURNED IMPR prefab=Gunship node=IMPR1 count=1 stock=1",
                         Line(log, "SHIP_RETURNED"));
        }

        // Characteristic failure: the hull is deleted. An origin that changed hands while the Guard
        // was away has no pool of the actor's to land in, and crediting it anyway would hand a ship
        // to whoever took the node.
        [Fact]
        public void AnAlliedGuardWhoseOriginWasLost_FallsBackToTheDefendedNode()
        {
            WorldModel w = TestSupport.World();
            var owner = MakeState("IMPR");
            var ally  = MakeState("UVCO");
            Ally(owner, ally);
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "UVCO1", "SPRT", new Vec3(100, 0, 0));   // taken while the Guard was standing
            ally.PendingFleets.Add(ExpiredGuard(w, "UVCO", "UVCO1", "IMPR1"));

            var states = new List<FactionState> { owner, ally };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(1, Stock(w, "IMPR1"));
            Assert.Equal(0, Stock(w, "UVCO1"));
            string line = Line(log, "SHIP_RETURNED");
            Assert.Equal("SHIP_RETURNED UVCO prefab=Gunship node=IMPR1 count=1 stock=1", line);
        }

        // ---- an exercised pact is worth something -----------------------------------------------

        // Characteristic failure: an ally spends a hull defending a partner and the partnership is no
        // warmer for it, so nothing in the model ever distinguishes a pact that is used from one that
        // is only signed.
        [Fact]
        public void AnAlliedGuardIntercepting_LiftsTheDefendedOwnersStandingTowardTheDefender()
        {
            WorldModel w = TestSupport.World();
            var owner  = MakeState("IMPR");
            var ally   = MakeState("UVCO");
            var raider = MakeState("SPRT");
            Ally(owner, ally);
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "UVCO1", "UVCO", new Vec3(100, 0, 0));
            ally.PendingFleets.Add(StandingGuard(w, "UVCO", "UVCO1", "IMPR1", 600));
            raider.PendingFleets.Add(ArrivingRaid(w, "SPRT", "IMPR", "IMPR1", 60));

            var states = new List<FactionState> { owner, ally, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.NotNull(Line(log, "GUARD_INTERCEPT"));
            Assert.True(w.Tuning.PactDefenceRelationBoost > 0,
                        "the lift is tuning-gated; a zero default makes this test vacuous");
            Assert.Equal(w.Tuning.PactDefenceRelationBoost, owner.RelationTo("UVCO"), 6);
            Assert.Equal("PACT_DEFENDED IMPR by UVCO @IMPR1 rel+0.050", Line(log, "PACT_DEFENDED"));
            // The defender is not paid for its own act; only the faction that was defended warms.
            Assert.Equal(0.0, ally.RelationTo("IMPR"), 6);
        }

        // Characteristic failure: the lift is paid to any non-owner Guard, so a passing third party's
        // patrol buys standing nobody agreed to.
        [Fact]
        public void AnUnalliedGuardIntercepting_LiftsNothing()
        {
            WorldModel w = TestSupport.World();
            var owner  = MakeState("IMPR");
            var ally   = MakeState("UVCO");
            var raider = MakeState("SPRT");
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "UVCO1", "UVCO", new Vec3(100, 0, 0));
            ally.PendingFleets.Add(StandingGuard(w, "UVCO", "UVCO1", "IMPR1", 600));
            raider.PendingFleets.Add(ArrivingRaid(w, "SPRT", "IMPR", "IMPR1", 60));

            var states = new List<FactionState> { owner, ally, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.NotNull(Line(log, "GUARD_INTERCEPT"));
            Assert.Equal(0.0, owner.RelationTo("UVCO"), 6);
            Assert.Null(Line(log, "PACT_DEFENDED"));
        }

        // Characteristic failure: the lift is paid for showing up rather than for winning, so an ally
        // that is destroyed defending a partner still banks the same standing as one that held.
        [Fact]
        public void AnAlliedGuardThatLoses_LiftsNothing()
        {
            WorldModel w = TestSupport.World();
            var owner  = MakeState("IMPR");
            var ally   = MakeState("UVCO");
            var raider = MakeState("SPRT");
            Ally(owner, ally);
            AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "UVCO1", "UVCO", new Vec3(100, 0, 0));
            ally.PendingFleets.Add(StandingGuard(w, "UVCO", "UVCO1", "IMPR1", 1));
            raider.PendingFleets.Add(ArrivingRaid(w, "SPRT", "IMPR", "IMPR1", 8000));

            var states = new List<FactionState> { owner, ally, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.NotNull(Line(log, "RAID_BREAKS_GUARD"));
            Assert.Null(Line(log, "GUARD_INTERCEPT"));
            Assert.Equal(0.0, owner.RelationTo("UVCO"), 6);
            Assert.Null(Line(log, "PACT_DEFENDED"));
        }

        // ---- one defender per node, whoever owns it ---------------------------------------------

        // Characteristic failure: the node's owner sees only its own fleets, so it stands a second
        // hull on a node an ally is already covering — a hull tied up for no added cover, and one the
        // dispatch path would then refuse to replace anywhere it is needed.
        [Fact]
        public void APatrolArrivingWhereAnAlliedGuardStands_GoesHomeInsteadOfStanding()
        {
            WorldModel w = TestSupport.World();
            var owner = MakeState("IMPR");
            var ally  = MakeState("UVCO");
            Ally(owner, ally);
            SpatialNode target = AddNode(w, "IMPR1", "IMPR", new Vec3(0, 0, 0));
            AddNode(w, "IMPR2", "IMPR", new Vec3(50, 0, 0));
            AddNode(w, "UVCO1", "UVCO", new Vec3(100, 0, 0));
            target.Threat = 0.8;
            ally.PendingFleets.Add(StandingGuard(w, "UVCO", "UVCO1", "IMPR1", 600));
            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Patrol,
                ActorTag = "IMPR",
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                OriginNodeId = "IMPR2",
                TargetNodeId = "IMPR1",
                OriginPosKm = new Vec3(50, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "IMPR2", Kind = FleetStopKind.Return }
                },
                WeaponBands = Bands(500),
                LastResolvedSegment = -1,
            });

            var states = new List<FactionState> { owner, ally };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.True(w.Graph.Nodes["IMPR1"].Threat > 0,
                        "the node must still be threatened, or the sweep goes home for the other reason");
            Assert.Null(Line(log, "PATROL_STANDS"));
            Assert.NotNull(Line(log, "PATROL_AT"));
            Assert.Equal(ActionKind.Patrol, owner.PendingFleets[0].Kind);
        }
    }
}
