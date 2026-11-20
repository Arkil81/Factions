using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-060: ListedForSale is a hard floor under ShipPool.Available, so a reservation
    /// whose reason has lapsed strands the hull — out of dispatch, and off every shelf too if no
    /// station can sell it. These tests pin the release rule and, more importantly, the property the
    /// operator asked for by name: it must not churn.</summary>
    public class StoreShelfReleaseTests
    {
        private static WorldModel MakeWorld(IPrefabCatalogue cat, IPlanetCatalogue planets = null)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            w.PrefabCatalogue = cat;
            if (planets != null) w.Planets = planets;
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, bool station = true, string tag = "F",
                                       Vec3 pos = default(Vec3))
        {
            SpatialNode n = w.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            if (station) n.StableStationId = Math.Abs(id.GetHashCode()) + 1;
            return n;
        }

        private static FactionTuning Tuning(int small = 5, int large = 2, int margin = 1, int perPass = 1)
        {
            return new FactionTuning
            {
                StoreShipSlotsSmallGrid = small,
                StoreShipSlotsLargeGrid = large,
                StoreShelfReleaseMarginSlots = margin,
                StoreShelfReleasesPerPass = perPass,
                // Without this the gate is vacuous: MinDryTwr has no initialiser, so a fresh
                // FactionTuning admits every hull at any gravity and the unsellable tests would
                // pass for the wrong reason. 1.05 is the live value.
                MinDryTwr = 1.05,
            };
        }

        private static void Reserve(SpatialNode n, string prefab, int count)
        {
            int s; n.ShipStock.TryGetValue(prefab, out s); n.ShipStock[prefab] = s + count;
            int r; n.ListedForSale.TryGetValue(prefab, out r); n.ListedForSale[prefab] = r + count;
        }

        private static int Listed(SpatialNode n, string prefab)
        {
            int r; return n.ListedForSale.TryGetValue(prefab, out r) ? r : 0;
        }

        // ---- the anti-churn property ------------------------------------------------------

        [Fact]
        public void InsideTheDeadBand_NothingIsReleased_SoStockingAndDrainingCannotAlternate()
        {
            // Target 5, margin 1. At 5 the stocker is satisfied; at 6 the releaser is still silent.
            // If the two shared one threshold, 6 would release to 5, the stocker would rebuild to 6,
            // and the shelf would oscillate every cadence forever — the failure this exists to stop.
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            var log = new List<string>();

            Reserve(n, "Hull", 5);
            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, log));
            Assert.Equal(5, Listed(n, "Hull"));

            Reserve(n, "Hull", 1); // now 6 == target + margin, the top of the band
            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, log));
            Assert.Equal(6, Listed(n, "Hull"));
            Assert.Empty(log);
        }

        [Fact]
        public void AboveTheDeadBand_ItDrains_ButOnlyToTheTopOfTheBand()
        {
            // 8 against target 5 + margin 1: it must stop at 6, not at 5. Draining to the target
            // would land exactly where the stocker starts refilling.
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hull", 8);
            var log = new List<string>();

            FactionTuning t = Tuning(perPass: 10);
            StoreShelfRelease.Run(State(), w, t, new List<SpatialNode> { n }, log);

            Assert.Equal(6, Listed(n, "Hull"));
            Assert.Contains(log, l => l.Contains("reason=" + StoreShelfRelease.ReasonOverTarget));
        }

        [Fact]
        public void TheDeadBandIsPerSite_SoTwoFullShelvesAreNotOneOversizedOne()
        {
            // Target 5, margin 1, two yards each holding exactly 6. The estate holds 12, but neither
            // shelf is over its own band. Counted across the network this drains both, one hull per
            // pass, against a stocker that refills them per site -- the oscillation the band exists
            // to stop, moved up one scope.
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode a = Node(w, "N1");
            SpatialNode b = Node(w, "N2");
            Reserve(a, "Hull", 6);
            Reserve(b, "Hull", 6);
            var log = new List<string>();

            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(perPass: 10),
                                                  new List<SpatialNode> { a, b }, log));
            Assert.Equal(6, Listed(a, "Hull"));
            Assert.Equal(6, Listed(b, "Hull"));
            Assert.Empty(log);
        }

        [Fact]
        public void ARepeatedPassIsAFixedPoint_WhichIsWhatNotChurningMeans()
        {
            // The check that a flat shelf count cannot make: run the pass three times and assert
            // nothing moves after the first. A count read once looks identical whether the shelf is
            // stable or being handed back and rebuilt.
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hull", 8);
            FactionTuning t = Tuning(perPass: 10);

            var first = new List<string>();
            StoreShelfRelease.Run(State(), w, t, new List<SpatialNode> { n }, first);
            int settled = Listed(n, "Hull");

            for (int i = 0; i < 3; i++)
            {
                var later = new List<string>();
                int moved = StoreShelfRelease.Run(State(), w, t, new List<SpatialNode> { n }, later);
                Assert.Equal(0, moved);
                Assert.Empty(later);
                Assert.Equal(settled, Listed(n, "Hull"));
            }
        }

        [Fact]
        public void ThePerPassBudgetBoundsTheRate_SoAMisSetTargetCannotEmptyAShopAtOnce()
        {
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hull", 20);

            StoreShelfRelease.Run(State(), w, Tuning(perPass: 1), new List<SpatialNode> { n }, new List<string>());
            Assert.Equal(19, Listed(n, "Hull"));
        }

        // ---- the reasons ------------------------------------------------------------------

        [Fact]
        public void AnOverTargetDrain_SkipsAHullTheFleetCannotDispatchFromThatNode()
        {
            // The residue FAC-330's build-time fix cannot reach: a hull reserved before that shipped,
            // sitting in a well. Handing it back does not rescue it -- it drops the reservation and
            // puts the hull in a pool that can never send it anywhere, at a node it can never leave.
            // The shelf keeps it (a buyer takes delivery where they stand) and drains a hull that
            // flies instead.
            //
            // The counts are the discriminator: Ion is the MOST stocked, so an ungated pass takes it
            // first and the assertion below would read 3, not 5.
            var cat = new Cat();
            cat.Grid["Ion"] = "Small"; cat.SetIon("Ion", dryMassKg: 1000, forceN: 5000);        // 0.10 TWR at 1 g
            cat.Grid["Lifter"] = "Small"; cat.SetIon("Lifter", dryMassKg: 1000, forceN: 60000); // 1.22 TWR at 1 g
            WorldModel w = MakeWorld(cat, new PerNodeGravity(1.0, 0.0));
            SpatialNode well = Node(w, "Well", pos: new Vec3(0, 0, 0));
            SpatialNode deep = Node(w, "Deep", pos: new Vec3(9000, 0, 0));  // keeps Ion sellable, so phase 1 stays out
            Reserve(well, "Ion", 5);
            Reserve(well, "Lifter", 3);                                     // 8 listed against target 5 + margin 1
            var log = new List<string>();

            int released = StoreShelfRelease.Run(State(), w, Tuning(perPass: 10),
                                                 new List<SpatialNode> { well, deep }, log);

            Assert.Equal(2, released);
            Assert.Equal(5, Listed(well, "Ion"));      // untouched
            Assert.Equal(1, Listed(well, "Lifter"));   // drained to the top of the band instead
            Assert.DoesNotContain(log, l => l.Contains("prefab=Ion"));
        }

        [Fact]
        public void AnOverTargetDrain_StillStopsWhenNothingAtTheNodeCanFly()
        {
            // Nothing eligible is not a reason to hand back the ineligible one. The shelf stays over
            // target, which costs a slot and creates nothing; a sale is what drains it.
            var cat = new Cat();
            cat.Grid["Ion"] = "Small"; cat.SetIon("Ion", dryMassKg: 1000, forceN: 5000);
            WorldModel w = MakeWorld(cat, new PerNodeGravity(1.0, 0.0));
            SpatialNode well = Node(w, "Well", pos: new Vec3(0, 0, 0));
            SpatialNode deep = Node(w, "Deep", pos: new Vec3(9000, 0, 0));
            Reserve(well, "Ion", 9);
            var log = new List<string>();

            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(perPass: 10),
                                                  new List<SpatialNode> { well, deep }, log));
            Assert.Equal(9, Listed(well, "Ion"));
            Assert.Empty(log);
        }

        [Fact]
        public void AHullNoStationOfOursCanOffer_IsHandedBack_EvenWithTheShelfBelowTarget()
        {
            // The strand the per-station shelf gate makes possible: reserved, invisible on every
            // shelf, and excluded from dispatch. The dead band deliberately does not protect it —
            // it is not filling a slot, it is occupying one.
            var cat = new Cat(); cat.Grid["Ion"] = "Small";
            cat.SetIon("Ion", dryMassKg: 1000, forceN: 5000);   // ~0.10 TWR at 1 g
            WorldModel w = MakeWorld(cat, new ConstantGravity(1.0));
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Ion", 1);                                // one hull, target is 5
            var log = new List<string>();

            int released = StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, log);

            Assert.Equal(1, released);
            Assert.Equal(0, Listed(n, "Ion"));
            Assert.Contains(log, l => l.Contains(StoreShelfRelease.ReasonUnsellable));
            // The hull itself is untouched — only a sale removes it.
            Assert.Equal(1, n.ShipStock["Ion"]);
        }

        [Fact]
        public void AHullOneStationCanOffer_IsKept_EvenIfAnotherStationCannot()
        {
            // The pool is shared: sellable ANYWHERE we sell is enough. A per-station AND would
            // strand every hull the moment a faction founded a station in a second regime.
            var cat = new Cat(); cat.Grid["Ion"] = "Small";
            cat.SetIon("Ion", dryMassKg: 1000, forceN: 5000);
            WorldModel w = MakeWorld(cat, new PerNodeGravity(1.0, 0.0));
            SpatialNode well = Node(w, "Well", pos: new Vec3(0, 0, 0));
            SpatialNode deep = Node(w, "Deep", pos: new Vec3(9000, 0, 0));
            Reserve(well, "Ion", 1);

            int released = StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { well, deep },
                                                 new List<string>());

            Assert.Equal(0, released);
            Assert.Equal(1, Listed(well, "Ion"));
        }

        [Fact]
        public void AFactionWithNoFoundedStation_KeepsItsReservations()
        {
            // Nowhere to sell is not the same as unsellable: there is no station whose regime could
            // rule the hull out, so releasing here would drain a shelf being built up in advance.
            var cat = new Cat(); cat.Grid["Ion"] = "Small";
            cat.SetIon("Ion", dryMassKg: 1000, forceN: 5000);
            WorldModel w = MakeWorld(cat, new ConstantGravity(1.0));
            SpatialNode n = Node(w, "N1", station: false);
            Reserve(n, "Ion", 1);

            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode>(), new List<string>()));
            Assert.Equal(1, Listed(n, "Ion"));
        }

        [Fact]
        public void APersistentFleetDemandDoesNotDrainTheShelf()
        {
            // The live defect, as a regression. On v0.145.0 "a node needs this hull" was its own
            // release reason, outside the dead band, on the reasoning that releasing satisfies the
            // demand because the hull gets dispatched. It does not — release only makes the hull
            // available, and the proactive poster re-posts the demand every cadence while the zone
            // still has no route-feasible stock. Three factions' shelves drained one hull per pass
            // over 45 minutes; IGSL's emptied.
            //
            // So: a demand that never clears, a shelf inside the dead band, and ten passes.
            var cat = new Cat(); cat.Grid["Hauler"] = "Small"; cat.Cargo["Hauler"] = 5000;
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hauler", 5);                       // exactly the target
            w.Demand.Post(DemandEntry.ForShip("F", "Trade", "N1", w.UtcNow.AddHours(9)));

            for (int pass = 0; pass < 10; pass++)
            {
                var log = new List<string>();
                Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, log));
                Assert.Empty(log);
            }
            Assert.Equal(5, Listed(n, "Hauler"));
        }

        [Fact]
        public void OverTarget_ANodeNeedDecidesWhichHullGoesBack_NotWhetherOneDoes()
        {
            // The node need's whole remaining job. Two prefabs on an over-target shelf: the
            // most-stocked one would go on count alone, but a node is trying to build the other, so
            // that one goes instead. Both halves are asserted — a release happens (the shelf CAN
            // spare one) and it is the wanted hull (the need steered the pick).
            // The two prefabs must differ in a way the DEMAND can see. Making both Trade-suitable
            // gives the node need nothing to discriminate on, and the first key in dictionary order
            // wins regardless of the need -- the test then passes or fails on iteration order
            // instead of on the rule.
            var cat = new Cat();
            cat.Grid["Wanted"]  = "Small"; cat.Cargo["Wanted"]  = 5000; cat.Drill["Wanted"] = 4.0;
            cat.Grid["Surplus"] = "Small"; cat.Cargo["Surplus"] = 5000;   // no drill
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Surplus", 6);                      // most-stocked
            Reserve(n, "Wanted", 2);                       // 8 total, target 5 + margin 1
            // Ship:Mine needs drill AND cargo, so only "Wanted" satisfies it.
            w.Demand.Post(DemandEntry.ForShip("F", "Mine", "N1", w.UtcNow.AddMinutes(5)));
            var log = new List<string>();

            int released = StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, log);

            Assert.Equal(1, released);
            Assert.Contains(log, l => l.Contains("reason=" + StoreShelfRelease.ReasonOverTarget)
                                      && l.Contains("pick=" + StoreShelfRelease.PickNodeNeeds));
            Assert.Equal(1, Listed(n, "Wanted"));
            Assert.Equal(6, Listed(n, "Surplus"));
        }

        [Fact]
        public void TheRateKnobAtZeroIsOff_NotOne()
        {
            // The second defect the live window found: the knob was read as "> 0 ? value : 1", so 0
            // meant 1 and the operator had no live lever at all when this pass misbehaved.
            var cat = new Cat(); cat.Grid["Hull"] = "Small";
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hull", 20);                        // far over target

            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(perPass: 0),
                new List<SpatialNode> { n }, new List<string>()));
            Assert.Equal(20, Listed(n, "Hull"));
        }

        [Fact]
        public void AShelfDemandIsNotANodeNeed_OrTheShelfWouldReleaseWhatItJustReserved()
        {
            // The store's own restock demand must never count as the node needing the hull: that is
            // the reservation working, and reading it as a need is a one-line route to the exact
            // oscillation the dead band exists to prevent.
            var cat = new Cat(); cat.Grid["Hauler"] = "Small"; cat.Cargo["Hauler"] = 5000;
            WorldModel w = MakeWorld(cat);
            SpatialNode n = Node(w, "N1");
            Reserve(n, "Hauler", 1);
            w.Demand.Post(DemandEntry.ForShip("F", "Trade", "N1", w.UtcNow.AddMinutes(5),
                destinedForSale: true, requiredGridSize: "Small"));

            Assert.Equal(0, StoreShelfRelease.Run(State(), w, Tuning(), new List<SpatialNode> { n }, new List<string>()));
            Assert.Equal(1, Listed(n, "Hauler"));
        }

        private static FactionState State() { return new FactionState { Tag = "F" }; }

        // ---- fixtures ---------------------------------------------------------------------

        private sealed class ConstantGravity : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravity(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>Gravity by position: the well sits at the origin, everything else is vacuum.
        /// The two stations must therefore be at DIFFERENT positions — with both at the origin this
        /// fixture returns one gravity for both and the test has no discriminator at all, which is
        /// how the first version of it failed.</summary>
        private sealed class PerNodeGravity : IPlanetCatalogue
        {
            private readonly double _atOrigin, _elsewhere;
            public PerNodeGravity(double atOrigin, double elsewhere)
            { _atOrigin = atOrigin; _elsewhere = elsewhere; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm)
            { return posKm.X == 0 && posKm.Y == 0 && posKm.Z == 0 ? _atOrigin : _elsewhere; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private sealed class Cat : IPrefabCatalogue
        {
            public readonly Dictionary<string, string> Grid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> Drill = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, double> _mass = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> _bands
                = new Dictionary<string, IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>(StringComparer.OrdinalIgnoreCase);

            public void SetIon(string id, double dryMassKg, float forceN)
            {
                _mass[id] = dryMassKg;
                _bands[id] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Ion", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            public bool HasPrefab(string p) => true;
            public long CargoLiters(string p) { long v; return Cargo.TryGetValue(p ?? "", out v) ? v : 0; }
            public long Cost(string p) => 0;
            public double Dps(string p) => 0;
            public double DryMassKg(string p) { double v; return _mass.TryGetValue(p ?? "", out v) ? v : 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p)
            {
                IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> v;
                return _bands.TryGetValue(p ?? "", out v) ? v : null;
            }
            public string GridSize(string p) { string v; return Grid.TryGetValue(p ?? "", out v) ? v : "Small"; }
            public string FactionHint(string p) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string p) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string r, string h) => new string[0];
            public IEnumerable<string> AllPrefabIds => Grid.Keys;
            public double DrillYieldScore(string p) { double v; return Drill.TryGetValue(p ?? "", out v) ? v : 0; }
            public IReadOnlyList<string> RolesFor(string p) => new string[0];
            public IReadOnlyDictionary<string, long> GasTankLiters(string p)
            { return new Dictionary<string, long>(); }
        }
    }
}
