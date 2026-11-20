using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="RestorationDiff"/>: a live grid against its design. The characteristic
    /// failures are a player's addition read as a missing block, a multi-cell block's far cells
    /// ignored, and a transform off by a rotation — which matches almost nothing.
    /// </summary>
    public class RestorationDiffTests
    {
        private static readonly Dictionary<string, int> ArmorBom = new Dictionary<string, int> { { "SteelPlate", 25 } };
        private static readonly Dictionary<string, int> CockpitBom = new Dictionary<string, int>
        {
            { "InteriorPlate", 20 }, { "Construction", 20 }, { "Computer", 100 }, { "Display", 10 },
        };
        private static readonly Dictionary<string, int> TankBom = new Dictionary<string, int>
        {
            { "SteelPlate", 80 }, { "LargeTube", 40 }, { "SmallTube", 60 }, { "Computer", 8 }, { "Construction", 40 },
        };

        private static RestorationBlock B(string def, int x, int y, int z, GridDir fwd = GridDir.Forward, GridDir up = GridDir.Up,
                                          IReadOnlyDictionary<string, int> bom = null, bool anchor = false, GridCell? size = null)
        {
            return new RestorationBlock
            {
                DefinitionId = def,
                Min = new GridCell(x, y, z),
                Forward = fwd,
                Up = up,
                Size = size ?? new GridCell(1, 1, 1),
                Components = bom ?? ArmorBom,
                IsAnchor = anchor,
                Integrity = 100f,
                MaxIntegrity = 100f,
            };
        }

        /// <summary>The live copy of a design block under <paramref name="t"/>, whole.</summary>
        private static RestorationBlock Moved(RestorationBlock b, GridTransform t)
        {
            RestorationPlacement p = RestorationDiff.Place(b, t);
            return new RestorationBlock
            {
                DefinitionId = b.DefinitionId,
                Min = p.Min,
                Forward = p.Forward,
                Up = p.Up,
                Size = b.Size,
                IsAnchor = b.IsAnchor,
                Integrity = b.MaxIntegrity,
                MaxIntegrity = b.MaxIntegrity,
            };
        }

        /// <summary>A small asymmetric hull: a cockpit, a 1x1x3 tank lying along X, and armour of
        /// three different definitions so no rotation of it maps onto itself.</summary>
        private static List<RestorationBlock> Design()
        {
            return new List<RestorationBlock>
            {
                B("MyObjectBuilder_Cockpit/LargeBlockCockpit", 0, 0, 0, GridDir.Forward, GridDir.Up, CockpitBom, anchor: true),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, 0, 1),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, 0, 2),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorSlope", 1, 0, 1, GridDir.Right, GridDir.Up),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorCorner", 0, 1, 2, GridDir.Backward, GridDir.Left),
                B("MyObjectBuilder_OxygenTank/LargeHydrogenTank", -3, 0, 2, GridDir.Left, GridDir.Up, TankBom, size: new GridCell(1, 1, 3)),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, -1, 0),
            };
        }

        private static List<RestorationBlock> LiveCopy(List<RestorationBlock> design, GridTransform t)
        {
            return design.Select(b => Moved(b, t)).ToList();
        }

        [Fact]
        public void An_untouched_grid_is_all_intact_and_costs_nothing()
        {
            List<RestorationBlock> design = Design();
            RestorationPlan plan = RestorationDiff.Diff(design, LiveCopy(design, GridTransform.Identity), GridTransform.Identity);

            Assert.Equal(design.Count, plan.Intact);
            Assert.Empty(plan.Missing);
            Assert.Empty(plan.Damaged);
            Assert.Empty(plan.Blocked);
            Assert.Empty(plan.Extra);
            Assert.Empty(plan.Bill);
            Assert.Equal(1.0, plan.MatchedShareOfCurrent);
        }

        [Fact]
        public void A_ground_off_block_is_missing_and_billed_its_full_bom()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live.RemoveAt(5); // the tank

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            RestorationEntry missing = Assert.Single(plan.Missing);
            Assert.Equal(5, missing.ReferenceIndex);
            Assert.Equal(80L, plan.Bill["SteelPlate"]);
            Assert.Equal(40L, plan.Bill["LargeTube"]);
            Assert.Equal(design.Count - 1, plan.Intact);
        }

        [Fact]
        public void A_damaged_block_is_billed_what_its_stack_lacks_not_its_bom()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live[0].Integrity = 40f;
            live[0].MissingComponents = new Dictionary<string, int> { { "Computer", 30 }, { "Display", 10 } };

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            RestorationEntry damaged = Assert.Single(plan.Damaged);
            Assert.Equal(0, damaged.CurrentIndex);
            Assert.Equal(2, plan.Bill.Count);
            Assert.Equal(30L, plan.Bill["Computer"]);
            Assert.Equal(10L, plan.Bill["Display"]);
            Assert.Empty(plan.Missing);
        }

        [Fact]
        public void A_deformed_block_needs_a_weld_and_no_components()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live[1].Deformed = true;

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            Assert.Single(plan.Damaged);
            Assert.Equal(1, plan.DeformedOnly);
            Assert.Empty(plan.Bill);
            Assert.Equal(0, RestorationDiff.CountBelowFullIntegrity(live));
        }

        [Fact]
        public void A_players_addition_is_extra_and_never_missing()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live.Add(B("MyObjectBuilder_Beacon/LargeBlockBeacon", 5, 5, 5));

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            Assert.Equal(new[] { live.Count - 1 }, plan.Extra);
            Assert.Empty(plan.Missing);
            Assert.Empty(plan.Blocked);
            Assert.Equal(design.Count, plan.Intact);
        }

        [Fact]
        public void A_block_in_a_design_blocks_cell_blocks_it_and_is_extra()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live.RemoveAt(1); // armour at (0,0,1)
            live.Add(B("MyObjectBuilder_InteriorLight/SmallLight", 0, 0, 1));

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            RestorationEntry blocked = Assert.Single(plan.Blocked);
            Assert.Equal(1, blocked.ReferenceIndex);
            Assert.Equal(live.Count - 1, blocked.CurrentIndex);
            Assert.Empty(plan.Missing);
            Assert.Equal(new[] { live.Count - 1 }, plan.Extra);
            Assert.Empty(plan.Bill);
        }

        [Fact]
        public void A_turned_block_at_the_same_cell_is_not_the_design_block()
        {
            List<RestorationBlock> design = Design();
            List<RestorationBlock> live = LiveCopy(design, GridTransform.Identity);
            live[3].Forward = GridDir.Left;   // the slope, turned where it stands

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            Assert.Equal(3, Assert.Single(plan.Blocked).ReferenceIndex);
            Assert.Equal(new[] { 3 }, plan.Extra);
        }

        [Fact]
        public void A_multi_cell_block_occupies_its_rotated_footprint()
        {
            // Size (2,1,3) facing Left: +X goes to Forward (-Z) and +Z to Right (+X), so the far
            // corner is Min + (2,0,1) — SE's ComputeMax.
            var b = B("MyObjectBuilder_Thrust/LargeBlockLargeThrust", 10, 0, 0, GridDir.Left, GridDir.Up, size: new GridCell(2, 1, 3));
            Assert.Equal(new GridCell(12, 0, 1), b.Max);

            var tank = B("MyObjectBuilder_OxygenTank/LargeHydrogenTank", 0, 0, 0, GridDir.Forward, GridDir.Up, TankBom, size: new GridCell(1, 1, 3));
            Assert.Equal(new GridCell(0, 0, 2), tank.Max);

            var design = new List<RestorationBlock> { tank };
            // Nothing at the tank's Min, but a player block in its far cell.
            var live = new List<RestorationBlock> { B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, 0, 2) };

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            Assert.Empty(plan.Missing);
            Assert.Equal(0, Assert.Single(plan.Blocked).CurrentIndex);
        }

        [Fact]
        public void A_rotated_multi_cell_block_keeps_its_lowest_corner_as_min()
        {
            // A tank along +Z from the origin, turned so +Z points to -X: its cells run from
            // (-2,0,0) to (0,0,0), and its Min is the lowest of them, not the image of the old Min.
            GridRotation turn;
            Assert.True(GridRotation.TryFromOrientation(GridDir.Right, GridDir.Up, out turn));
            Assert.Equal(GridDir.Left, turn.ZTo);
            var t = new GridTransform(turn, GridCell.Zero);

            var tank = B("MyObjectBuilder_OxygenTank/LargeHydrogenTank", 0, 0, 0, GridDir.Forward, GridDir.Up, TankBom, size: new GridCell(1, 1, 3));
            RestorationPlacement p = RestorationDiff.Place(tank, t);
            Assert.Equal(new GridCell(-2, 0, 0), p.Min);
            Assert.Equal(new GridCell(0, 0, 0), p.Max);
            Assert.Equal(GridDir.Right, p.Forward);

            // The live tank, written out by hand, is the design's tank under that transform.
            var live = new List<RestorationBlock>
            {
                B("MyObjectBuilder_OxygenTank/LargeHydrogenTank", -2, 0, 0, GridDir.Right, GridDir.Up, TankBom, size: new GridCell(1, 1, 3)),
            };
            Assert.Equal(1, RestorationDiff.Diff(new List<RestorationBlock> { tank }, live, t).Intact);
        }

        [Fact]
        public void A_multi_cell_live_block_blocks_a_design_block_in_any_of_its_cells()
        {
            var design = new List<RestorationBlock> { B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, 0, 2) };
            var live = new List<RestorationBlock>
            {
                B("MyObjectBuilder_OxygenTank/LargeHydrogenTank", 0, 0, 0, bom: TankBom, size: new GridCell(1, 1, 3)),
            };

            RestorationPlan plan = RestorationDiff.Diff(design, live, GridTransform.Identity);

            Assert.Single(plan.Blocked);
            Assert.Empty(plan.Missing);
        }

        [Fact]
        public void There_are_24_rotations_each_with_an_inverse()
        {
            Assert.Equal(24, GridRotation.All.Length);
            Assert.Equal(24, new HashSet<GridRotation>(GridRotation.All).Count);
            Assert.Equal(GridRotation.Identity, GridRotation.All[0]);
            foreach (GridRotation r in GridRotation.All)
            {
                Assert.Equal(GridRotation.Identity, r.Inverse().After(r));
                Assert.Equal(GridRotation.Identity, r.After(r.Inverse()));
            }
            GridRotation none;
            Assert.False(GridRotation.TryFromOrientation(GridDir.Up, GridDir.Down, out none));
        }

        [Fact]
        public void A_block_orientation_rotation_matches_ses_matrix()
        {
            // SE: MatrixI(forward, up) sends +X to Right = the left of (up, forward) flipped, +Y to
            // up, +Z to Backward. Forward=Down, Up=Forward: Right = Down x Forward = (0,-1,0)x(0,0,-1) = (1,0,0).
            GridRotation r;
            Assert.True(GridRotation.TryFromOrientation(GridDir.Down, GridDir.Forward, out r));
            Assert.Equal(GridDir.Right, r.XTo);
            Assert.Equal(GridDir.Forward, r.YTo);
            Assert.Equal(GridDir.Up, r.ZTo);
            Assert.Equal(GridDir.Down, r.Apply(GridDir.Forward));
        }

        public static IEnumerable<object[]> AllRotations()
        {
            for (int i = 0; i < 24; i++) yield return new object[] { i };
        }

        [Theory]
        [MemberData(nameof(AllRotations))]
        public void The_transform_is_recovered_in_every_orientation(int rotationIndex)
        {
            List<RestorationBlock> design = Design();
            var t = new GridTransform(GridRotation.All[rotationIndex], new GridCell(7, -4, 11));
            List<RestorationBlock> live = LiveCopy(design, t);
            live.RemoveAt(2);                                             // an armour block ground off
            live.Add(B("MyObjectBuilder_Beacon/LargeBlockBeacon", 40, 40, 40)); // the owner's beacon

            RestorationFit fit = RestorationDiff.FindTransform(design, live);
            RestorationPlan plan = RestorationDiff.Diff(design, live, fit.Transform);

            Assert.Equal(t, fit.Transform);
            Assert.Equal(design.Count - 1, fit.Matched);
            Assert.Equal(2, Assert.Single(plan.Missing).ReferenceIndex);
            Assert.Equal(new[] { live.Count - 1 }, plan.Extra);
            Assert.Empty(plan.Blocked);
            Assert.Equal(design.Count - 1, plan.Intact);
            // The missing block goes back where the transform puts it.
            RestorationPlacement expect = RestorationDiff.Place(design[2], t);
            Assert.Equal(expect.Min, plan.Missing[0].Placement.Min);
        }

        [Fact]
        public void Without_a_surviving_anchor_a_shared_definition_aligns_it()
        {
            List<RestorationBlock> design = Design();
            var t = new GridTransform(GridRotation.All[13], new GridCell(-2, 3, 5));
            List<RestorationBlock> live = LiveCopy(design, t);
            live.RemoveAt(0); // the cockpit is gone

            RestorationFit fit = RestorationDiff.FindTransform(design, live);

            Assert.Equal("shared", fit.Via);
            Assert.Equal(t, fit.Transform);
            Assert.Equal(0, Assert.Single(RestorationDiff.Diff(design, live, fit.Transform).Missing).ReferenceIndex);
        }

        [Fact]
        public void A_transform_off_by_a_rotation_matches_almost_nothing()
        {
            List<RestorationBlock> design = Design();
            var t = new GridTransform(GridRotation.All[5], new GridCell(1, 2, 3));
            List<RestorationBlock> live = LiveCopy(design, t);

            var wrong = new GridTransform(GridRotation.All[6], t.Offset);
            RestorationPlan plan = RestorationDiff.Diff(design, live, wrong);

            Assert.True(plan.MatchedShareOfCurrent < 0.3, "matched " + plan.MatchedShareOfCurrent);
            Assert.Equal(1.0, RestorationDiff.Diff(design, live, t).MatchedShareOfCurrent);
        }

        [Fact]
        public void An_identical_grid_keeps_identity_over_an_equal_scoring_candidate()
        {
            // Two identical armour blocks and nothing else: shifting by one also scores 1 of 2, but
            // identity scores 2 of 2 and is tried first.
            var design = new List<RestorationBlock>
            {
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                B("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
            };
            RestorationFit fit = RestorationDiff.FindTransform(design, LiveCopy(design, GridTransform.Identity));
            Assert.Equal(GridTransform.Identity, fit.Transform);
            Assert.Equal("identity", fit.Via);
            Assert.Equal(2, fit.Matched);
        }

        [Fact]
        public void The_fingerprint_ignores_order_and_position_but_not_shape()
        {
            List<RestorationBlock> design = Design();
            string fp = RestorationDiff.Fingerprint(design);

            var reversed = new List<RestorationBlock>(design);
            reversed.Reverse();
            Assert.Equal(fp, RestorationDiff.Fingerprint(reversed));

            var shift = new GridTransform(GridRotation.Identity, new GridCell(5, -9, 2));
            Assert.Equal(fp, RestorationDiff.Fingerprint(LiveCopy(design, shift)));

            List<RestorationBlock> turned = LiveCopy(design, GridTransform.Identity);
            turned[3].Forward = GridDir.Left;
            Assert.NotEqual(fp, RestorationDiff.Fingerprint(turned));

            List<RestorationBlock> damaged = LiveCopy(design, GridTransform.Identity);
            damaged[0].Integrity = 1f;
            Assert.Equal(fp, RestorationDiff.Fingerprint(damaged));
            Assert.Equal(16, fp.Length);
        }

        [Fact]
        public void Registration_counts_blocks_below_full_integrity()
        {
            List<RestorationBlock> live = LiveCopy(Design(), GridTransform.Identity);
            Assert.Equal(0, RestorationDiff.CountBelowFullIntegrity(live));
            live[2].Integrity = 99.9f;
            live[4].MissingComponents = new Dictionary<string, int> { { "SteelPlate", 1 } };
            Assert.Equal(2, RestorationDiff.CountBelowFullIntegrity(live));
        }
    }
}
