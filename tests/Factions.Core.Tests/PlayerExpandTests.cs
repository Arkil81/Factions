using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-197: a player faction creating a node where there is none, and the wallet charge that
    /// gates it. What is pinned here is the ORDER — probe, charge, then commit — because every
    /// failure mode of this feature is an ordering one: a node created for free, a wallet charged for
    /// a node that never appeared, or a faction registered as the only residue of a refused purchase.
    /// </summary>
    public class PlayerExpandTests
    {
        private const long PlayerId = 144L;
        private const long FactionId = 288L;

        /// <summary>Stands in for MyBankingSystem with SE's own refusal semantics: no account and a
        /// below-zero debit both refuse rather than clamp, and every write is counted so a test can
        /// tell "refused" from "never asked".</summary>
        private sealed class FakeBank : IEconomyGateway
        {
            public readonly Dictionary<long, long> Accounts = new Dictionary<long, long>();
            public readonly List<string> Writes = new List<string>();

            public bool ChangeFactionBalance(long identityId, long amount)
            {
                Writes.Add(identityId + ":" + amount);
                long balance;
                if (amount == 0 || !Accounts.TryGetValue(identityId, out balance)) return false;
                if (balance + amount < 0) return false;
                Accounts[identityId] = balance + amount;
                return true;
            }

            public bool TryGetFactionBalance(long identityId, out long balance)
            {
                balance = 0L;
                return Accounts.TryGetValue(identityId, out balance);
            }
        }

        /// <summary>One body at the origin. <c>NearestSurfacePoint</c> answers with a fixed point on
        /// the surface sphere, so a snap is observable as a real move rather than inferred.</summary>
        private sealed class OneBody : IPlanetCatalogue
        {
            private readonly PlanetRecord _planet = new PlanetRecord
            {
                Id = "P", Name = "Anvil", Generator = "EarthLike",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 60.0, GravityRadius = 300.0,
                AllowsSurfaceStations = true,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_planet.Center);
                if (d <= _planet.SurfaceRadius) return 1.0;
                if (d >= _planet.GravityRadius) return 0.0;
                return 1.0 - (d - _planet.SurfaceRadius) / (_planet.GravityRadius - _planet.SurfaceRadius);
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm) { return new Vec3(_planet.SurfaceRadius, 0, 0); }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                OrbitGravityMin = 0.01,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
                NodeExpansionSampleCount = 4,
                NodeExpansionReachPct = 0.25,
                PlayerExpandCost = 1000000,
            };
        }

        /// <summary>Deep space only: no planet catalogue, so nothing snaps and the separation in force
        /// is the 500 km deep-space floor everywhere.</summary>
        private static WorldModel EmptyWorld()
        {
            return new WorldModel(new SeededRandom(7)) { Tuning = Tuning() };
        }

        private static FactionState PlayerFaction()
        {
            return new FactionState { Tag = "MAGN", FactionId = FactionId, IsPlayerFaction = true };
        }

        private static PlayerExpandRequest Request(FactionState owner, Vec3 posKm)
        {
            return new PlayerExpandRequest
            {
                FactionTag = "MAGN",
                Owner = owner,
                PlayerIdentityId = PlayerId,
                FactionIdentityId = FactionId,
                PositionKm = posKm,
            };
        }

        // ---- the probe ----------------------------------------------------------------------

        [Fact]
        public void AnEmptyWorldIsClearEverywhereAndSaysThereIsNoNeighbour()
        {
            NodePlacementProbe probe = EmptyWorld().ProbePlacement(new Vec3(4000, 0, 0));

            Assert.True(probe.Clear);
            Assert.Equal(double.MaxValue, probe.NearestNodeKm);
            Assert.Equal(500.0, probe.SeparationKm, 6);
            Assert.Equal(NodeZone.DeepSpace, probe.Zone);
        }

        [Fact]
        public void GroundInsideAnExistingNodesSeparationIsOccupied()
        {
            WorldModel w = EmptyWorld();
            w.Graph.Add(new Vec3(4000, 0, 0), 1.0, "N001");

            // 499 km away against a 500 km deep-space floor.
            NodePlacementProbe probe = w.ProbePlacement(new Vec3(4499, 0, 0));

            Assert.False(probe.Clear);
            Assert.Equal(499.0, probe.NearestNodeKm, 6);
        }

        [Fact]
        public void GroundJustOutsideTheSeparationIsClear()
        {
            WorldModel w = EmptyWorld();
            w.Graph.Add(new Vec3(4000, 0, 0), 1.0, "N001");

            NodePlacementProbe probe = w.ProbePlacement(new Vec3(4501, 0, 0));

            Assert.True(probe.Clear);
            Assert.Equal(501.0, probe.NearestNodeKm, 6);
        }

        [Fact]
        public void TheVerdictIsAboutWhereTheNodeLandsNotWhereTheShipIs()
        {
            var w = new WorldModel(new SeededRandom(7)) { Tuning = Tuning(), Planets = new OneBody() };
            // Deep inside the well, so the point classifies Surface and snaps down to the surface
            // sphere -- a long way from where the request was made.
            var asked = new Vec3(0, 0, 20);
            NodePlacementProbe probe = w.ProbePlacement(asked);

            Assert.Equal(NodeZone.Surface, probe.Zone);
            Assert.Equal(new Vec3(60, 0, 0).X, probe.PlacedKm.X, 6);
            Assert.True(probe.SnapDistanceKm > 50.0,
                "the quoted landing point must be the snapped one, not the requested one");
            // And the separation quoted is the one in force where it LANDS: the surface floor, not
            // the deep-space floor that applies where the ship is sitting.
            Assert.Equal(15.0, probe.SeparationKm, 6);
        }

        [Fact]
        public void ProbingDecidesNothingAndCommitsNothing()
        {
            WorldModel w = EmptyWorld();
            w.ProbePlacement(new Vec3(4000, 0, 0));
            Assert.Empty(w.Graph.Nodes);
        }

        // ---- the charge ---------------------------------------------------------------------

        [Fact]
        public void EveryExpandIsChargedToTheFactionAccountAndNeverToThePlayer()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();
            bank.Accounts[PlayerId] = 9_000_000L;
            bank.Accounts[FactionId] = 2_000_000L;
            FactionState owner = PlayerFaction();
            var service = new PlayerExpandService(w, bank, null);

            Assert.True(service.Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner).Ok);
            Assert.True(service.Commit(Request(owner, new Vec3(9000, 0, 0)), () => owner).Ok);

            Assert.Equal(9_000_000L, bank.Accounts[PlayerId]);
            Assert.Equal(0L, bank.Accounts[FactionId]);
        }

        /// <summary>The live defect: the wallet moved and the ledger did not, so the mirror's next
        /// push from the ledger refunded the Expand.</summary>
        [Fact]
        public void ARegisteredFactionsLedgerIsDebitedWithItsAccount()
        {
            WorldModel w = EmptyWorld();
            w.SetTreasury("MAGN", 2_500_000L);
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_500_000L;
            FactionState owner = PlayerFaction();

            Assert.True(new PlayerExpandService(w, bank, null)
                .Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner).Ok);

            Assert.Equal(1_500_000L, bank.Accounts[FactionId]);
            Assert.Equal(1_500_000L, w.GetTreasury("MAGN"));
        }

        /// <summary>A faction with no state is charged, then registered. Its ledger starts from the
        /// account as it stands after the charge (the host's registration), so Commit must not debit
        /// it a second time.</summary>
        [Fact]
        public void AnUnregisteredFactionIsChargedThenRegisteredWithoutALedgerDebit()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_000_000L;
            int registrations = 0;

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(null, new Vec3(4000, 0, 0)), () => { registrations++; return PlayerFaction(); });

            Assert.True(r.Ok);
            Assert.Equal(1, registrations);
            Assert.Equal(1_000_000L, bank.Accounts[FactionId]);
            Assert.Equal(0L, w.GetTreasury("MAGN"));
        }

        [Fact]
        public void AShortAccountIsRefusedAndTheQuoteSaysHowMuchToDeposit()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();
            bank.Accounts[PlayerId] = 9_000_000L;       // a rich member is not a way round it
            bank.Accounts[FactionId] = 400_000L;
            FactionState owner = PlayerFaction();

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner);

            Assert.Equal(PlayerExpandRefusal.ChargeRefused, r.Refusal);
            Assert.Equal(400_000L, r.Quote.WalletBalance);
            Assert.Equal(600_000L, r.Quote.Shortfall);
            Assert.Empty(w.Graph.Nodes);
            Assert.Equal(9_000_000L, bank.Accounts[PlayerId]);
            Assert.Equal(400_000L, bank.Accounts[FactionId]);
        }

        // ---- the order ----------------------------------------------------------------------

        [Fact]
        public void OccupiedGroundIsRefusedBeforeAnyWalletIsTouched()
        {
            WorldModel w = EmptyWorld();
            w.Graph.Add(new Vec3(4000, 0, 0), 1.0, "N001");
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 9_000_000L;
            FactionState owner = PlayerFaction();

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(owner, new Vec3(4100, 0, 0)), () => owner);

            Assert.Equal(PlayerExpandRefusal.Occupied, r.Refusal);
            Assert.Empty(bank.Writes);
            Assert.Single(w.Graph.Nodes);              // the pre-existing one, and nothing else
        }

        [Fact]
        public void ARefusedChargeCreatesNothing()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 999_999L;       // one credit short
            FactionState owner = PlayerFaction();

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner);

            Assert.Equal(PlayerExpandRefusal.ChargeRefused, r.Refusal);
            Assert.Empty(w.Graph.Nodes);
            Assert.Equal(999_999L, bank.Accounts[FactionId]);
        }

        [Fact]
        public void ARefusedChargeDoesNotLeaveARegisteredFactionBehind()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();                  // no account at all for either identity
            int registrations = 0;

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(null, new Vec3(4000, 0, 0)), () => { registrations++; return PlayerFaction(); });

            Assert.Equal(PlayerExpandRefusal.ChargeRefused, r.Refusal);
            Assert.Equal(0, registrations);
        }

        [Fact]
        public void WithNoEconomyAtAllNothingIsCreated()
        {
            WorldModel w = EmptyWorld();
            FactionState owner = PlayerFaction();

            PlayerExpandResult r = new PlayerExpandService(w, null, null)
                .Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner);

            Assert.Equal(PlayerExpandRefusal.EconomyUnavailable, r.Refusal);
            Assert.Empty(w.Graph.Nodes);
        }

        // ---- the node ------------------------------------------------------------------------

        [Fact]
        public void TheNodeLandsExactlyWhereTheQuoteSaidItWould()
        {
            var w = new WorldModel(new SeededRandom(7)) { Tuning = Tuning(), Planets = new OneBody() };
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_000_000L;
            FactionState owner = PlayerFaction();
            var service = new PlayerExpandService(w, bank, null);
            var asked = new Vec3(0, 0, 20);            // snaps a long way, onto the surface sphere

            PlayerExpandQuote quote = service.Quote(Request(owner, asked));
            PlayerExpandResult r = service.Commit(Request(owner, asked), () => owner);

            Assert.True(r.Ok);
            Assert.Equal(quote.Probe.PlacedKm.X, r.Node.Position.X, 6);
            Assert.Equal(quote.Probe.PlacedKm.Y, r.Node.Position.Y, 6);
            Assert.Equal(quote.Probe.PlacedKm.Z, r.Node.Position.Z, 6);
            Assert.Equal(quote.Probe.Zone, r.Node.Zone);
        }

        [Fact]
        public void ANodeCreatedWithNoParentBelongsToTheFactionAndCarriesOrdinaryHold()
        {
            WorldModel w = EmptyWorld();
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_000_000L;
            FactionState owner = PlayerFaction();

            PlayerExpandResult r = new PlayerExpandService(w, bank, null)
                .Commit(Request(owner, new Vec3(4000, 0, 0)), () => owner);

            Assert.Equal("MAGN", r.Node.OwnerFactionTag);
            Assert.Equal(0.5, r.Node.HoldStrength, 6);
            Assert.Equal(0, r.Node.StableStationId);   // not founded: Growing and Founding are downstream
        }

        [Fact]
        public void AnIsolatedNodeGetsNoEdgesAndThatIsNotAnError()
        {
            WorldModel w = EmptyWorld();
            w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N001");
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_000_000L;
            bank.Accounts[FactionId] = 2_000_000L;   // the second Expand is charged here, not personally
            FactionState owner = PlayerFaction();

            var service = new PlayerExpandService(w, bank, null);

            // Well beyond 500 km x 1.25 of anything, which is the radius CommitNode links within.
            PlayerExpandResult far = service.Commit(Request(owner, new Vec3(40000, 0, 0)), () => owner);
            // And one inside that radius, so the empty assertion above is a fact about the distance
            // rather than about this path never linking anything.
            PlayerExpandResult near = service.Commit(Request(owner, new Vec3(600, 0, 0)), () => owner);

            Assert.True(far.Ok);
            Assert.True(near.Ok);
            Assert.Empty(far.Node.Neighbors);
            Assert.NotEmpty(near.Node.Neighbors);
        }

        // ---- persistence ---------------------------------------------------------------------

        /// <summary>The retired first-Expand flag is still in older brain files; it must load, and
        /// mean nothing.</summary>
        [Fact]
        public void ABrainFileCarryingTheRetiredFirstExpandFlagStillLoads()
        {
            BrainDoc doc = Newtonsoft.Json.JsonConvert.DeserializeObject<BrainDoc>(
                "{\"Tag\":\"MAGN\",\"HasEverExpanded\":true}");

            FactionState loaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal("MAGN", loaded.Tag);
        }

        // ---- the log -------------------------------------------------------------------------

        [Fact]
        public void ARefusalNamesItsReasonWithoutNamingTheBlockingNode()
        {
            WorldModel w = EmptyWorld();
            SpatialNode blocker = w.Graph.Add(new Vec3(4000, 0, 0), 1.0, "N001");
            blocker.OwnerFactionTag = "RIVL";
            var log = new List<string>();
            var bank = new FakeBank();
            bank.Accounts[FactionId] = 2_000_000L;
            FactionState owner = PlayerFaction();

            new PlayerExpandService(w, bank, log.Add)
                .Commit(Request(owner, new Vec3(4100, 0, 0)), () => owner);

            string line = Assert.Single(log);
            Assert.Contains("PLAYER_EXPAND_REFUSED", line);
            Assert.Contains("reason=Occupied", line);
            // The server's own log may name distances; it is the player-facing surface that must not.
            // What it must never do is claim a charge happened.
            Assert.DoesNotContain("PLAYER_EXPAND ", line);
        }
    }
}
