using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-060: with no prefab tagged as merchandise, the shelf is chosen by SE faction
    /// type (which capability class) shaded by personality (which of them). These tests pin the two
    /// properties that make the rule worth having — a type leads, and a trait can shade but never
    /// invert it — plus the fill order, which is what a 5-slot shelf actually shows.</summary>
    public class StoreShelfPlanTests
    {
        private static Personality Trait(double merc = 0, double aggr = 0, double xeno = 0, double exp = 0)
        {
            return new Personality
            {
                Id = "test",
                Traits = new Traits { Mercantilism = merc, Aggression = aggr, Xenophobia = xeno, Expansionism = exp }
            };
        }

        [Fact]
        public void EachTypeLeadsWithItsOwnClass()
        {
            Assert.Equal(StoreShelfPlan.Armed, Leader("Military"));
            Assert.Equal(StoreShelfPlan.Armed, Leader("Pirate"));
            Assert.Equal(StoreShelfPlan.Drill, Leader("Miner"));
            Assert.Equal(StoreShelfPlan.Cargo, Leader("Trader"));
            Assert.Equal(StoreShelfPlan.Cargo, Leader("Builder"));
        }

        [Fact]
        public void AnUnknownOrUnresolvedTypeStillGetsAShelf()
        {
            // Mirrors FactionStorePolicy: an unresolved tag keeps its store, so the plan must
            // return a usable mix rather than nothing.
            Dictionary<string, double> shares = StoreShelfPlan.Shares(null, null);
            Assert.Equal(1.0, Sum(shares), 6);
            foreach (KeyValuePair<string, double> kv in shares) Assert.True(kv.Value > 0);
        }

        [Fact]
        public void AMaximalTraitShadesTheMixButNeverInvertsTheType()
        {
            // A Military faction with maximum Mercantilism: cargo must rise against a neutral
            // personality, and armed must STILL lead. A trait that could flip the leader would make
            // the type decorative.
            Dictionary<string, double> neutral = StoreShelfPlan.Shares("Military", Trait());
            Dictionary<string, double> merchant = StoreShelfPlan.Shares("Military", Trait(merc: 1.0));

            Assert.True(merchant[StoreShelfPlan.Cargo] > neutral[StoreShelfPlan.Cargo]);
            Assert.True(merchant[StoreShelfPlan.Armed] > merchant[StoreShelfPlan.Cargo]);
        }

        [Fact]
        public void AMaximalTraitOnTheTypesOwnClassStillCannotFlipAnotherType()
        {
            // The symmetric case, and the one that would fail if TraitInfluence were raised too far:
            // a Trader with maximum Aggression shelves more warships, but still mostly freighters.
            Dictionary<string, double> shares = StoreShelfPlan.Shares("Trader", Trait(aggr: 1.0, xeno: 1.0));
            Assert.True(shares[StoreShelfPlan.Cargo] > shares[StoreShelfPlan.Armed]);
        }

        [Fact]
        public void TheShelfFillsTowardTheTargetMix_NotAllOfOneClass()
        {
            // A 5-slot Miner shelf, filled one hull at a time by largest shortfall. The property
            // that matters is that it is a MIX: a plan that returned its leading class every time
            // would produce five miners and read as working.
            var listed = new Dictionary<string, int>(StringComparer.Ordinal);
            var picked = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                string cls = StoreShelfPlan.NextClass("Miner", Trait(), null, listed, 5);
                picked.Add(cls);
                int have; listed.TryGetValue(cls, out have);
                listed[cls] = have + 1;
            }

            Assert.Equal(5, picked.Count);
            Assert.True(listed[StoreShelfPlan.Drill] >= 2, "a Miner shelf should lead with drills");
            Assert.True(listed.Count >= 2, "the shelf must not be one class repeated");
        }

        [Fact]
        public void FillOrderIsDeterministic()
        {
            var a = new Dictionary<string, int>(StringComparer.Ordinal);
            var b = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < 7; i++)
            {
                string ca = StoreShelfPlan.NextClass("Military", Trait(aggr: 0.9), null, a, 7);
                string cb = StoreShelfPlan.NextClass("Military", Trait(aggr: 0.9), null, b, 7);
                Assert.Equal(ca, cb);
                Bump(a, ca); Bump(b, cb);
            }
        }

        [Fact]
        public void ClassOfCountsAMultiRoleHullUnderItsNarrowestCapability()
        {
            // A drill-and-cargo hull is a miner, not a freighter: counting it as cargo would let one
            // hull satisfy the class a Miner faction is most short of.
            var cat = new StubCatalogue();
            cat.Set("Miner", cargo: 5000, dps: 0, drill: 4.0, grid: "Small");
            cat.Set("Gunship", cargo: 0, dps: 900, drill: 0, grid: "Small");
            cat.Set("Freighter", cargo: 90000, dps: 0, drill: 0, grid: "Large");
            cat.Set("ArmedMiner", cargo: 5000, dps: 900, drill: 4.0, grid: "Large");

            Assert.Equal(StoreShelfPlan.Drill, StoreShelfPlan.ClassOf(cat, "Miner"));
            Assert.Equal(StoreShelfPlan.Armed, StoreShelfPlan.ClassOf(cat, "Gunship"));
            Assert.Equal(StoreShelfPlan.Cargo, StoreShelfPlan.ClassOf(cat, "Freighter"));
            Assert.Equal(StoreShelfPlan.Drill, StoreShelfPlan.ClassOf(cat, "ArmedMiner"));
        }

        [Fact]
        public void TheSameHullIsShelvableAtOneStationAndNotAnother()
        {
            // The property per-station gating exists for. One faction, one shared pool, two stations
            // in different regimes: the ion hull is offered in deep space and withheld in the well,
            // and the hydrogen hull is offered at both. A gate applied at the yard instead of here
            // could not produce this -- it would give both stations the same shelf.
            var cat = new ThrustCatalogue();
            cat.SetIon("Ion_Freighter", dryMassKg: 1000, forceN: 5000);        // ~0.10 TWR at 1 g
            cat.SetHydrogen("H2_Freighter", dryMassKg: 1000, forceN: 15000);   // clears everywhere

            var well  = new ConstantGravity(1.0);
            var deep  = new ConstantGravity(0.0);
            var pos   = new Vec3(0, 0, 0);

            Assert.False(StoreShelfPlan.IsShelfableAt(cat, "Ion_Freighter", well, pos, 1.05));
            Assert.True(StoreShelfPlan.IsShelfableAt(cat, "Ion_Freighter", deep, pos, 1.05));
            Assert.True(StoreShelfPlan.IsShelfableAt(cat, "H2_Freighter", well, pos, 1.05));
            Assert.True(StoreShelfPlan.IsShelfableAt(cat, "H2_Freighter", deep, pos, 1.05));
        }

        [Fact]
        public void AnUnscannedHullIsNeverWithheld_SoMissingDataCannotEmptyAShelf()
        {
            // Mirrors ShipPool.IsRouteFeasible's rollout-safety rule. The failure this prevents is a
            // shelf that silently goes empty because the catalogue lacks thrust data, which reads as
            // "nothing to sell" rather than as a scan problem.
            var cat = new ThrustCatalogue();   // no thrust or mass recorded for this id
            Assert.True(StoreShelfPlan.IsShelfableAt(cat, "Unscanned", new ConstantGravity(1.0),
                new Vec3(0, 0, 0), 1.05));
        }

        [Fact]
        public void WithNoPlanetDataTheGateWithholdsNothing()
        {
            var cat = new ThrustCatalogue();
            cat.SetIon("Ion_Freighter", dryMassKg: 1000, forceN: 5000);
            Assert.True(StoreShelfPlan.IsShelfableAt(cat, "Ion_Freighter", null, new Vec3(0, 0, 0), 1.05));
        }

        private sealed class ConstantGravity : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravity(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private sealed class ThrustCatalogue : IPrefabCatalogue
        {
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

            public void SetHydrogen(string id, double dryMassKg, float forceN)
            {
                _mass[id] = dryMassKg;
                _bands[id] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
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

            public bool HasPrefab(string p) => true;
            public long CargoLiters(string p) => 0;
            public long Cost(string p) => 0;
            public double Dps(string p) => 0;
            public double DryMassKg(string p) { double v; return _mass.TryGetValue(p ?? "", out v) ? v : 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p)
            {
                IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> v;
                return _bands.TryGetValue(p ?? "", out v) ? v : null;
            }
            public string GridSize(string p) => "Large";
            public string FactionHint(string p) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string p) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string r, string h) => new string[0];
            public IEnumerable<string> AllPrefabIds => _mass.Keys;
            public double DrillYieldScore(string p) => 0;
            public IReadOnlyList<string> RolesFor(string p) => new string[0];
            public IReadOnlyDictionary<string, long> GasTankLiters(string p)
            { return new Dictionary<string, long>(); }
        }

        private static string Leader(string type)
        {
            Dictionary<string, double> s = StoreShelfPlan.Shares(type, null);
            string best = null; double top = -1;
            foreach (KeyValuePair<string, double> kv in s)
                if (kv.Value > top) { top = kv.Value; best = kv.Key; }
            return best;
        }

        private static double Sum(Dictionary<string, double> d)
        {
            double t = 0; foreach (KeyValuePair<string, double> kv in d) t += kv.Value; return t;
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            int have; d.TryGetValue(k, out have); d[k] = have + 1;
        }

        private sealed class StubCatalogue : IPrefabCatalogue
        {
            private sealed class E { public long Cargo; public double Dps; public double Drill; public string Grid; }
            private readonly Dictionary<string, E> _e = new Dictionary<string, E>(StringComparer.OrdinalIgnoreCase);

            public void Set(string id, long cargo, double dps, double drill, string grid)
            {
                _e[id] = new E { Cargo = cargo, Dps = dps, Drill = drill, Grid = grid };
            }

            private E Get(string id) { E e; return id != null && _e.TryGetValue(id, out e) ? e : null; }

            public bool HasPrefab(string p) => Get(p) != null;
            public long CargoLiters(string p) { E e = Get(p); return e == null ? 0 : e.Cargo; }
            public long Cost(string p) => 0;
            public double Dps(string p) { E e = Get(p); return e == null ? 0 : e.Dps; }
            public double DryMassKg(string p) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p) => null;
            public string GridSize(string p) { E e = Get(p); return e == null ? null : e.Grid; }
            public string FactionHint(string p) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string p)
            {
                E e = Get(p);
                return e == null || e.Dps <= 0
                    ? (IReadOnlyList<WeaponBand>)new WeaponBand[0]
                    : new[] { new WeaponBand { Dps = (float)e.Dps, RangeM = 800f } };
            }
            public IReadOnlyList<string> GetNamesForRole(string r, string h) => new string[0];
            public IEnumerable<string> AllPrefabIds => _e.Keys;
            public double DrillYieldScore(string p) { E e = Get(p); return e == null ? 0 : e.Drill; }
            public IReadOnlyList<string> RolesFor(string p) => new string[0];
            public IReadOnlyDictionary<string, long> GasTankLiters(string p)
            { return new Dictionary<string, long>(); }
        }
    }
}
