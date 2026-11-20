using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Density, per-band materials, and per-material size ranges — the authoring surface the
    /// asteroid spawner reads. See docs/ore-geography.md.</summary>
    public class WorldFeatureDensityTests
    {
        private const double MaxDensity = 8.0; // FactionTuning.FeatureMaxDensityPer1000Km3 default

        private static WorldFeature Ring(double innerKm, double outerKm, double halfThicknessKm)
        {
            return new WorldFeature
            {
                Name = "Ring",
                Shape = WorldFeatureShape.Ring,
                Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = innerKm,
                OuterRadiusKm = outerKm,
                HalfThicknessKm = halfThicknessKm,
            };
        }

        // ---- Density is a 0..1 factor against the tuning ceiling ------------------------------

        [Fact]
        public void Density_zero_is_the_default_and_asks_for_nothing()
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600 };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron" });

            Assert.Equal(0.0, f.Density);
            Assert.Equal(0L, f.ObjectCount(MaxDensity));
        }

        /// <summary>The point of the 0..1 shape: it is a fraction of the ceiling, so the ceiling is
        /// the only place an absolute number lives.</summary>
        [Fact]
        public void Density_scales_linearly_against_the_tuning_ceiling()
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 100 };

            f.Density = 1.0;
            long full = f.ObjectCount(MaxDensity);

            f.Density = 0.5;
            Assert.InRange(f.ObjectCount(MaxDensity), full / 2 - 1, full / 2 + 1);

            // Halving the ceiling and halving the density are the same statement.
            f.Density = 1.0;
            Assert.InRange(f.ObjectCount(MaxDensity / 2.0), full / 2 - 1, full / 2 + 1);
        }

        [Fact]
        public void Sphere_count_is_density_times_volume_over_the_ceiling()
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 100, Density = 1.0 };

            double volume = (4.0 / 3.0) * System.Math.PI * 1e6;             // 100 km ball
            Assert.Equal(volume, f.VolumeKm3(), 3);
            Assert.Equal((long)System.Math.Round(volume * MaxDensity / 1000.0), f.ObjectCount(MaxDensity));
        }

        [Fact]
        public void Shell_volume_excludes_its_hollow_centre()
        {
            var shell = new WorldFeature
            {
                Name = "Pocket", Shape = WorldFeatureShape.Shell,
                InnerRadiusKm = 200, OuterRadiusKm = 900,
            };
            double expected = (4.0 / 3.0) * System.Math.PI * (900.0 * 900 * 900 - 200.0 * 200 * 200);
            Assert.Equal(expected, shell.VolumeKm3(), 3);
        }

        [Fact]
        public void A_ceiling_of_zero_disables_every_feature()
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600, Density = 1.0 };
            Assert.Equal(0L, f.ObjectCount(0.0));
        }

        // ---- Banded rings are summed band by band ---------------------------------------------

        /// <summary>The gaps between a ring's bands are genuinely empty. Charging them volume would
        /// either put rock in them or thin the bands out to pay for them.</summary>
        [Fact]
        public void Banded_ring_charges_only_its_bands_not_the_gaps()
        {
            WorldFeature solid = Ring(180, 450, 3.6);
            solid.Density = 0.5;
            long solidCount = solid.ObjectCount(MaxDensity);

            WorldFeature banded = Ring(180, 450, 3.6);
            banded.Density = 0.5;
            banded.Bands.Add(new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200 });
            banded.Bands.Add(new FeatureBand { InnerRadiusKm = 400, OuterRadiusKm = 450 });

            Assert.True(banded.ObjectCount(MaxDensity) < solidCount);
            Assert.Equal(
                banded.BandVolumeKm3(banded.Bands[0]) + banded.BandVolumeKm3(banded.Bands[1]),
                banded.Bands.Sum(b => banded.BandVolumeKm3(b)), 3);
        }

        [Fact]
        public void A_band_density_overrides_the_features_and_absence_inherits_it()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.Density = 0.25;
            var dense = new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200, Density = 1.0 };
            var inherits = new FeatureBand { InnerRadiusKm = 400, OuterRadiusKm = 450 };
            ring.Bands.Add(dense);
            ring.Bands.Add(inherits);

            Assert.Equal(1.0, ring.DensityAt(dense));
            Assert.Equal(0.25, ring.DensityAt(inherits));
            Assert.Equal(0.25, ring.DensityAt(null));
        }

        /// <summary>Zero is a real authored value, distinct from "not authored" — which is exactly
        /// what a nullable buys and a plain double would lose.</summary>
        [Fact]
        public void A_band_density_of_zero_is_authored_emptiness_not_inheritance()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.Density = 1.0;
            var empty = new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200, Density = 0.0 };
            ring.Bands.Add(empty);

            Assert.Equal(0.0, ring.DensityAt(empty));
            Assert.Equal(0L, ring.ObjectCount(MaxDensity));
        }

        // ---- Per-band materials ----------------------------------------------------------------

        [Fact]
        public void A_band_without_materials_inherits_the_features()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            var band = new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200 };
            ring.Bands.Add(band);

            Assert.Same(ring.Materials, ring.MaterialsAt(band));
        }

        [Fact]
        public void A_band_with_materials_replaces_the_features_rather_than_adding_to_them()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            var band = new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200 };
            band.Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Platinum" } };
            ring.Bands.Add(band);

            List<FeatureMaterial> resolved = ring.MaterialsAt(band);
            Assert.Single(resolved);
            Assert.Equal("Platinum", resolved[0].SubTypeId);
        }

        /// <summary>An empty list means barren on a band for the same reason it does on a feature.
        /// Omitting the key and writing [] have to stay different things.</summary>
        [Fact]
        public void An_explicitly_empty_band_material_list_is_barren_not_inherited()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            var barren = new FeatureBand
            {
                InnerRadiusKm = 180, OuterRadiusKm = 200,
                Materials = new List<FeatureMaterial>(),
            };
            ring.Bands.Add(barren);

            Assert.Empty(ring.MaterialsAt(barren));
        }

        [Fact]
        public void TryResolve_reports_the_band_the_point_fell_in()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            var inner = new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200 };
            var outer = new FeatureBand { InnerRadiusKm = 400, OuterRadiusKm = 450 };
            ring.Bands.Add(inner);
            ring.Bands.Add(outer);

            FeatureBand hit;
            Assert.True(ring.TryResolve(new Vec3(190, 0, 0), new Vec3(0, 0, 0), out hit));
            Assert.Same(inner, hit);

            Assert.True(ring.TryResolve(new Vec3(420, 0, 0), new Vec3(0, 0, 0), out hit));
            Assert.Same(outer, hit);

            Assert.False(ring.TryResolve(new Vec3(300, 0, 0), new Vec3(0, 0, 0), out hit)); // in a gap
            Assert.Null(hit);
        }

        /// <summary>A solid ring, a sphere and a shell have no bands, so a hit reports a null band —
        /// which must read as "use the feature's own" rather than as a miss.</summary>
        [Fact]
        public void A_hit_on_an_unbanded_feature_reports_a_null_band()
        {
            var sphere = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600 };
            sphere.AddMaterial(new FeatureMaterial { SubTypeId = "Iron" });

            FeatureBand band;
            Assert.True(sphere.TryResolve(new Vec3(10, 0, 0), new Vec3(0, 0, 0), out band));
            Assert.Null(band);
            Assert.Same(sphere.Materials, sphere.MaterialsAt(band));
        }

        // ---- Band materials reach the ore geography -------------------------------------------

        [Fact]
        public void Sampling_inside_a_band_reads_that_bands_composition()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice", Share = 1.0 });
            ring.Bands.Add(new FeatureBand
            {
                InnerRadiusKm = 180, OuterRadiusKm = 200,
                Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Platinum", Share = 1.0 } },
            });
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 400, OuterRadiusKm = 450 });

            var geo = new WorldFeatureGeography(new[] { ring }, null);

            OreSample inner;
            Assert.True(geo.TrySample(new Vec3(190, 0, 0), 1.0, out inner));
            Assert.Equal("Platinum", Assert.Single(inner.Ores).SubTypeId);

            OreSample outer;
            Assert.True(geo.TrySample(new Vec3(420, 0, 0), 1.0, out outer));
            Assert.Equal("Ice", Assert.Single(outer.Ores).SubTypeId);
        }

        [Fact]
        public void MintableOres_includes_ores_that_exist_only_in_one_band()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Ice" });
            ring.Bands.Add(new FeatureBand
            {
                InnerRadiusKm = 180, OuterRadiusKm = 200,
                Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Platinum" } },
            });

            var geo = new WorldFeatureGeography(new[] { ring }, null);
            List<string> ores = geo.MintableOres.ToList();

            Assert.Contains("Ice", ores);
            Assert.Contains("Platinum", ores);
        }

        // ---- SizeRange -------------------------------------------------------------------------

        [Fact]
        public void An_unauthored_size_range_resolves_to_the_default()
        {
            var m = new FeatureMaterial { SubTypeId = "Iron" };

            Assert.Null(m.SizeRange);
            SizeRange resolved = m.ResolvedSizeRange();
            Assert.Equal(64.0, resolved.MinM);
            Assert.Equal(256.0, resolved.MaxM);
        }

        [Fact]
        public void An_authored_size_range_is_returned_as_written()
        {
            var m = new FeatureMaterial { SubTypeId = "Silver", SizeRange = new SizeRange(48.0, 110.0) };

            Assert.Equal(48.0, m.ResolvedSizeRange().MinM);
            Assert.Equal(110.0, m.ResolvedSizeRange().MaxM);
        }

        /// <summary>Two materials in one band must be able to disagree about size — that is the whole
        /// reason SizeRange sits on the material rather than on the band.</summary>
        [Fact]
        public void Materials_in_one_band_carry_independent_size_ranges()
        {
            var band = new FeatureBand
            {
                InnerRadiusKm = 180, OuterRadiusKm = 200,
                Materials = new List<FeatureMaterial>
                {
                    new FeatureMaterial { SubTypeId = "Ice",  SizeRange = new SizeRange(128.0, 420.0) },
                    new FeatureMaterial { SubTypeId = "Iron", SizeRange = new SizeRange(96.0, 260.0) },
                },
            };

            Assert.Equal(420.0, band.Materials[0].ResolvedSizeRange().MaxM);
            Assert.Equal(260.0, band.Materials[1].ResolvedSizeRange().MaxM);
        }

        // ---- Deposit volume ---------------------------------------------------------------------

        /// <summary>The authored answer to "how much ore", as opposed to "which ore". Null inherits
        /// the world's setting, which is what every feature authored before these existed does.</summary>
        [Fact]
        public void Deposit_controls_default_to_inheriting_the_world()
        {
            var m = new FeatureMaterial { SubTypeId = "Iron" };

            Assert.Null(m.DepositCountCoefficient);
            Assert.Null(m.DepositSizeDenominator);
        }

        [Fact]
        public void Deposit_controls_round_trip_through_the_file()
        {
            const string json = @"{
              ""Features"": [{
                ""Name"": ""Belt"", ""Shape"": ""Sphere"", ""OuterRadiusKm"": 600.0, ""Density"": 0.5,
                ""Materials"": [
                  { ""SubTypeId"": ""Iron"", ""DepositCountCoefficient"": 4.0, ""DepositSizeDenominator"": 30.0 },
                  { ""SubTypeId"": ""Silver"" }
                ]
              }]
            }";

            string status;
            WorldFeature f = Assert.Single(WorldFeatureFile.Parse(json, out status));

            Assert.Equal(4.0, f.Materials[0].DepositCountCoefficient);
            Assert.Equal(30.0, f.Materials[0].DepositSizeDenominator);
            Assert.Null(f.Materials[1].DepositCountCoefficient);   // omitted inherits the world
            Assert.Null(f.Materials[1].DepositSizeDenominator);
        }

        // ---- Validation ------------------------------------------------------------------------

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.5)]
        public void A_density_outside_zero_to_one_is_rejected_by_name(double density)
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600, Density = density };

            string why;
            Assert.False(WorldFeatureFile.Validate(f, out why));
            Assert.Contains("Density", why);
        }

        [Fact]
        public void A_band_density_outside_zero_to_one_is_rejected_by_name()
        {
            WorldFeature ring = Ring(180, 450, 3.6);
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 180, OuterRadiusKm = 200, Density = 2.0 });

            string why;
            Assert.False(WorldFeatureFile.Validate(ring, out why));
            Assert.Contains("Density", why);
        }

        [Theory]
        [InlineData(0.0, 100.0)]
        [InlineData(-5.0, 100.0)]
        [InlineData(200.0, 100.0)]
        public void An_impossible_size_range_is_rejected_by_name(double minM, double maxM)
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600 };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron", SizeRange = new SizeRange(minM, maxM) });

            string why;
            Assert.False(WorldFeatureFile.Validate(f, out why));
            Assert.Contains("SizeRange", why);
            Assert.Contains("Iron", why);
        }

        [Fact]
        public void A_null_size_range_validates()
        {
            var f = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Sphere, OuterRadiusKm = 600 };
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron" });

            string why;
            Assert.True(WorldFeatureFile.Validate(f, out why));
            Assert.Null(why);
        }

        // ---- Round-trip through the authored file ----------------------------------------------

        /// <summary>The fields have to survive the JSON, not just exist in C#. Omitted keys must land
        /// on the inheriting values rather than on zero.</summary>
        [Fact]
        public void Density_materials_and_size_ranges_round_trip_through_the_file()
        {
            const string json = @"{
              ""Features"": [{
                ""Name"": ""Ring"",
                ""Shape"": ""Ring"",
                ""InnerRadiusKm"": 180.0,
                ""OuterRadiusKm"": 450.0,
                ""HalfThicknessKm"": 3.6,
                ""Density"": 0.35,
                ""Bands"": [
                  { ""InnerRadiusKm"": 180.0, ""OuterRadiusKm"": 200.0, ""Density"": 0.8,
                    ""Materials"": [ { ""SubTypeId"": ""Iron"", ""Share"": 1.0,
                                       ""SizeRange"": { ""MinM"": 96.0, ""MaxM"": 260.0 } } ] },
                  { ""InnerRadiusKm"": 400.0, ""OuterRadiusKm"": 450.0 }
                ],
                ""Materials"": [ { ""SubTypeId"": ""Ice"", ""Share"": 1.0 } ]
              }]
            }";

            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(json, out status);

            WorldFeature f = Assert.Single(features);
            Assert.Equal(0.35, f.Density);

            FeatureBand authored = f.Bands[0];
            Assert.Equal(0.8, ring_density(f, authored));
            Assert.Equal("Iron", f.MaterialsAt(authored)[0].SubTypeId);
            Assert.Equal(96.0, f.MaterialsAt(authored)[0].ResolvedSizeRange().MinM);

            FeatureBand inheriting = f.Bands[1];
            Assert.Equal(0.35, ring_density(f, inheriting));                     // omitted Density inherits
            Assert.Equal("Ice", f.MaterialsAt(inheriting)[0].SubTypeId);         // omitted Materials inherit
            Assert.Equal(256.0, f.MaterialsAt(inheriting)[0].ResolvedSizeRange().MaxM); // omitted SizeRange defaults
        }

        private static double ring_density(WorldFeature f, FeatureBand b) { return f.DensityAt(b); }
    }
}
