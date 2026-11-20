using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The deterministic cell planner behind feature rock. Everything here rests on one
    /// property — a cell's contents are a pure function of (worldSeed, feature, cellCoord) — because
    /// that is what lets rock be despawned and come back identical. See docs/ore-geography.md.</summary>
    public class FeatureRockPlannerTests
    {
        private const int WorldSeed = 20260825;
        private const double MaxDensity = 8.0;   // FeatureMaxDensityPer1000Km3 default
        private const double CellKm = 5.0;       // FeatureRockCellSizeKm default
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        private static WorldFeature Belt(double radiusKm, double density)
        {
            var f = new WorldFeature
            {
                Name = "Belt",
                Shape = WorldFeatureShape.Sphere,
                OuterRadiusKm = radiusKm,
                Density = density,
            };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron", Share = 1.0 });
            return f;
        }

        private static List<FeatureRockObject> Cell(WorldFeature f, CellCoord c, int seed = WorldSeed)
        {
            return FeatureRockPlanner.ObjectsInCell(f, Origin, c, seed, MaxDensity, CellKm).ToList();
        }

        private static List<FeatureRockObject> AllIn(WorldFeature f, double radiusKm, int seed = WorldSeed)
        {
            var all = new List<FeatureRockObject>();
            foreach (CellCoord c in FeatureRockPlanner.CellsWithin(Origin, radiusKm, CellKm))
                all.AddRange(Cell(f, c, seed));
            return all;
        }

        // ---- Determinism: the property everything else rests on -------------------------------

        [Fact]
        public void The_same_cell_generates_identically_every_time()
        {
            WorldFeature f = Belt(60, 1.0);
            var c = new CellCoord(3, 0, -2);

            List<FeatureRockObject> a = Cell(f, c);
            List<FeatureRockObject> b = Cell(f, c);

            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].StorageName, b[i].StorageName);
                Assert.Equal(a[i].PositionKm.X, b[i].PositionKm.X, 12);
                Assert.Equal(a[i].PositionKm.Y, b[i].PositionKm.Y, 12);
                Assert.Equal(a[i].PositionKm.Z, b[i].PositionKm.Z, 12);
                Assert.Equal(a[i].SizeM, b[i].SizeM, 12);
                Assert.Equal(a[i].MaterialSubTypeId, b[i].MaterialSubTypeId);
                Assert.Equal(a[i].ShapeSeed, b[i].ShapeSeed);
            }
        }

        /// <summary>The despawn/respawn contract: a cell regenerates without its neighbours ever
        /// having been visited. A shared RNG stream would fail this the moment players arrived from
        /// a different direction.</summary>
        [Fact]
        public void A_cell_does_not_depend_on_any_other_cell_having_been_generated()
        {
            WorldFeature f = Belt(60, 1.0);
            var target = new CellCoord(2, 1, 0);

            List<FeatureRockObject> alone = Cell(f, target);

            var warmed = new List<FeatureRockObject>();
            foreach (CellCoord c in FeatureRockPlanner.CellsWithin(Origin, 40, CellKm))
                warmed.AddRange(Cell(f, c));
            List<FeatureRockObject> afterWarm = Cell(f, target);

            Assert.Equal(alone.Count, afterWarm.Count);
            for (int i = 0; i < alone.Count; i++)
                Assert.Equal(alone[i].StorageName, afterWarm[i].StorageName);
        }

        [Fact]
        public void A_different_world_seed_produces_a_different_field()
        {
            WorldFeature f = Belt(60, 1.0);

            List<FeatureRockObject> a = AllIn(f, 60, WorldSeed);
            List<FeatureRockObject> b = AllIn(f, 60, WorldSeed + 1);

            Assert.NotEmpty(a);
            Assert.NotEmpty(b);
            Assert.NotEqual(
                a.Select(o => o.PositionKm.X).ToList(),
                b.Select(o => o.PositionKm.X).ToList());
        }

        /// <summary>Two features cannot collide in the same cell just because they share a
        /// coordinate — identity includes the feature.</summary>
        [Fact]
        public void Two_features_in_one_cell_generate_independently()
        {
            WorldFeature one = Belt(60, 1.0);
            WorldFeature two = Belt(60, 1.0);
            two.Name = "Other Belt";
            var c = new CellCoord(1, 0, 0);

            List<string> a = Cell(one, c).Select(o => o.StorageName).ToList();
            List<string> b = Cell(two, c).Select(o => o.StorageName).ToList();

            Assert.NotEmpty(a);
            Assert.Empty(a.Intersect(b));
        }

        [Fact]
        public void Storage_names_are_unique_across_a_whole_field()
        {
            WorldFeature f = Belt(60, 1.0);
            List<FeatureRockObject> all = AllIn(f, 60);

            Assert.NotEmpty(all);
            Assert.Equal(all.Count, all.Select(o => o.StorageName).Distinct().Count());
        }

        // ---- Density -------------------------------------------------------------------------

        [Fact]
        public void Density_zero_generates_nothing()
        {
            Assert.Empty(AllIn(Belt(60, 0.0), 60));
        }

        /// <summary>The cell sampler and the authoring-facing ObjectCount are two views of one
        /// number, and they have to agree or the operator is tuning against a lie.</summary>
        [Fact]
        public void Cell_sampling_reproduces_the_authored_object_count()
        {
            WorldFeature f = Belt(60, 0.5);

            long authored = f.ObjectCount(MaxDensity);
            int sampled = AllIn(f, 60 + CellKm).Count;

            Assert.InRange(sampled, authored * 0.85, authored * 1.15);
        }

        [Fact]
        public void Halving_density_roughly_halves_what_is_generated()
        {
            int dense = AllIn(Belt(60, 1.0), 60 + CellKm).Count;
            int sparse = AllIn(Belt(60, 0.5), 60 + CellKm).Count;

            Assert.InRange(sparse, dense * 0.4, dense * 0.6);
        }

        /// <summary>Most cells hold less than one object at realistic densities, so rounding a
        /// per-cell count would empty the entire feature.</summary>
        [Fact]
        public void A_density_below_one_object_per_cell_still_produces_rock()
        {
            var f = new WorldFeature
            {
                Name = "Thin", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 200, Density = 0.02,
            };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron" });

            // 0.02 x 8/1000 x 125 km3 = 0.02 expected per cell.
            Assert.NotEmpty(AllIn(f, 200));
        }

        // ---- Shape emerges by rejection --------------------------------------------------------

        [Fact]
        public void Nothing_is_generated_outside_the_feature_volume()
        {
            WorldFeature f = Belt(40, 1.0);

            foreach (FeatureRockObject o in AllIn(f, 60))
            {
                double d = System.Math.Sqrt(
                    o.PositionKm.X * o.PositionKm.X +
                    o.PositionKm.Y * o.PositionKm.Y +
                    o.PositionKm.Z * o.PositionKm.Z);
                Assert.True(d <= 40.0 + 1e-9, "object at " + d + " km is outside the 40 km belt");
            }
        }

        [Fact]
        public void A_ring_gap_between_bands_stays_empty()
        {
            var ring = new WorldFeature
            {
                Name = "Ring", Shape = WorldFeatureShape.Ring, Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 40, OuterRadiusKm = 120, HalfThicknessKm = 4, Density = 1.0,
            };
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 40, OuterRadiusKm = 60 });
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 100, OuterRadiusKm = 120 });

            foreach (FeatureRockObject o in AllIn(ring, 130))
            {
                double radial = System.Math.Sqrt(o.PositionKm.X * o.PositionKm.X + o.PositionKm.Z * o.PositionKm.Z);
                bool inABand = (radial >= 40 && radial <= 60) || (radial >= 100 && radial <= 120);
                Assert.True(inABand, "object at radial " + radial + " km fell in a gap between bands");
            }
        }

        /// <summary>A cell straddling several bands must honour each band's own density. Drawing at
        /// the peak and rejecting per band is what makes that work; a per-cell count could not.</summary>
        [Fact]
        public void A_denser_band_really_holds_more_than_a_sparse_one()
        {
            var ring = new WorldFeature
            {
                Name = "Ring", Shape = WorldFeatureShape.Ring, Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 40, OuterRadiusKm = 120, HalfThicknessKm = 4, Density = 0.1,
            };
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            var dense = new FeatureBand { InnerRadiusKm = 40, OuterRadiusKm = 60, Density = 1.0 };
            var sparse = new FeatureBand { InnerRadiusKm = 100, OuterRadiusKm = 120, Density = 0.1 };
            ring.Bands.Add(dense);
            ring.Bands.Add(sparse);

            int inDense = 0, inSparse = 0;
            foreach (FeatureRockObject o in AllIn(ring, 130))
            {
                double radial = System.Math.Sqrt(o.PositionKm.X * o.PositionKm.X + o.PositionKm.Z * o.PositionKm.Z);
                if (radial <= 60) inDense++; else inSparse++;
            }

            // The sparse band has ~2.7x the area but a tenth the density.
            Assert.True(inDense > inSparse * 2,
                "dense band held " + inDense + " against the sparse band's " + inSparse);
        }

        // ---- Materials and sizes ---------------------------------------------------------------

        [Fact]
        public void A_bands_own_materials_are_what_its_objects_are_made_of()
        {
            var ring = new WorldFeature
            {
                Name = "Ring", Shape = WorldFeatureShape.Ring, Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 40, OuterRadiusKm = 120, HalfThicknessKm = 4, Density = 1.0,
            };
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            ring.Bands.Add(new FeatureBand
            {
                InnerRadiusKm = 40, OuterRadiusKm = 60,
                Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Platinum" } },
            });
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 100, OuterRadiusKm = 120 });

            foreach (FeatureRockObject o in AllIn(ring, 130))
            {
                double radial = System.Math.Sqrt(o.PositionKm.X * o.PositionKm.X + o.PositionKm.Z * o.PositionKm.Z);
                Assert.Equal(radial <= 60 ? "Platinum" : "Ice", o.MaterialSubTypeId);
            }
        }

        [Fact]
        public void A_barren_band_generates_nothing_even_at_full_density()
        {
            var ring = new WorldFeature
            {
                Name = "Ring", Shape = WorldFeatureShape.Ring, Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 40, OuterRadiusKm = 120, HalfThicknessKm = 4, Density = 1.0,
            };
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            ring.Bands.Add(new FeatureBand
            {
                InnerRadiusKm = 40, OuterRadiusKm = 60,
                Materials = new List<FeatureMaterial>(),   // authored barren
            });

            Assert.Empty(AllIn(ring, 130));
        }

        [Fact]
        public void Object_size_stays_inside_its_own_materials_range()
        {
            var f = new WorldFeature
            {
                Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 60, Density = 1.0,
            };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Ice",  Share = 1, SizeRange = new SizeRange(300, 400) });
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron", Share = 1, SizeRange = new SizeRange(50, 80) });

            List<FeatureRockObject> all = AllIn(f, 60);
            Assert.NotEmpty(all);
            foreach (FeatureRockObject o in all)
            {
                if (o.MaterialSubTypeId == "Ice") Assert.InRange(o.SizeM, 300, 400);
                else Assert.InRange(o.SizeM, 50, 80);
            }
        }

        [Fact]
        public void Shares_are_respected_across_a_field()
        {
            var f = new WorldFeature
            {
                Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 80, Density = 1.0,
            };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron",   Share = 0.9 });
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Silver", Share = 0.1 });

            List<FeatureRockObject> all = AllIn(f, 80);
            int iron = all.Count(o => o.MaterialSubTypeId == "Iron");

            Assert.InRange(iron / (double)all.Count, 0.8, 0.97);
        }

        // ---- Deposit volume ---------------------------------------------------------------------

        [Fact]
        public void Deposit_controls_reach_the_planned_object()
        {
            var f = new WorldFeature
            {
                Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 60, Density = 1.0,
            };
            f.AddMaterial(new FeatureMaterial
            {
                SubTypeId = "Iron",
                DepositCountCoefficient = 4.0,
                DepositSizeDenominator = 30.0,
            });

            List<FeatureRockObject> all = AllIn(f, 60);
            Assert.NotEmpty(all);
            foreach (FeatureRockObject o in all)
            {
                Assert.Equal(4.0, o.DepositCountCoefficient);
                Assert.Equal(30.0, o.DepositSizeDenominator);
            }
        }

        /// <summary>Two materials in one feature must be able to disagree about ore volume, for the
        /// same reason they can disagree about size: it is a property of the deposit, not the
        /// region.</summary>
        [Fact]
        public void Two_materials_carry_independent_deposit_volume()
        {
            var f = new WorldFeature
            {
                Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 80, Density = 1.0,
            };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron",   Share = 1, DepositCountCoefficient = 5.0 });
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Silver", Share = 1, DepositCountCoefficient = 0.5 });

            List<FeatureRockObject> all = AllIn(f, 80);
            Assert.Contains(all, o => o.MaterialSubTypeId == "Iron" && o.DepositCountCoefficient == 5.0);
            Assert.Contains(all, o => o.MaterialSubTypeId == "Silver" && o.DepositCountCoefficient == 0.5);
        }

        // ---- Cell arithmetic -------------------------------------------------------------------

        [Fact]
        public void CellOf_and_CellsWithin_agree_about_negative_coordinates()
        {
            var pos = new Vec3(-12.5, 0.1, -0.1);
            CellCoord c = FeatureRockPlanner.CellOf(pos, CellKm);

            Assert.Equal(-3, c.X);   // -12.5 / 5 = -2.5 -> floor -3
            Assert.Equal(0, c.Y);
            Assert.Equal(-1, c.Z);
            Assert.Contains(c, FeatureRockPlanner.CellsWithin(pos, 0.0, CellKm).ToList());
        }

        [Fact]
        public void CellCouldTouch_rejects_cells_the_feature_cannot_reach()
        {
            WorldFeature f = Belt(40, 1.0);

            Assert.True(FeatureRockPlanner.CellCouldTouch(f, Origin, new CellCoord(0, 0, 0), CellKm));
            Assert.False(FeatureRockPlanner.CellCouldTouch(f, Origin, new CellCoord(100, 0, 0), CellKm));
        }

        /// <summary>The AABB pre-filter must never reject a cell that would have produced
        /// something — it is an optimisation, and a false negative silently loses rock.</summary>
        [Fact]
        public void CellCouldTouch_never_rejects_a_cell_that_holds_objects()
        {
            WorldFeature f = Belt(40, 1.0);

            foreach (CellCoord c in FeatureRockPlanner.CellsWithin(Origin, 60, CellKm))
            {
                if (Cell(f, c).Count == 0) continue;
                Assert.True(FeatureRockPlanner.CellCouldTouch(f, Origin, c, CellKm),
                    "cell " + c + " holds objects but the AABB filter rejected it");
            }
        }

        [Fact]
        public void An_anchored_feature_generates_around_its_resolved_centre()
        {
            WorldFeature f = Belt(30, 1.0);
            var anchor = new Vec3(1000, 0, 0);

            var all = new List<FeatureRockObject>();
            foreach (CellCoord c in FeatureRockPlanner.CellsWithin(anchor, 30, CellKm))
                all.AddRange(FeatureRockPlanner.ObjectsInCell(f, anchor, c, WorldSeed, MaxDensity, CellKm));

            Assert.NotEmpty(all);
            foreach (FeatureRockObject o in all)
                Assert.InRange(o.PositionKm.X, 1000 - 30, 1000 + 30);
        }
    }
}
