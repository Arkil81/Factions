using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="RestorationGroup"/>: a ship's mechanical group against its design. The
    /// characteristic failures are a subgrid's blocks diffed in the main grid's frame (every one reads
    /// missing, and placements land beside the rotor), a missing rotor head that leaves its whole turret
    /// unbuildable, and a live top of another type paired anyway — which places the design's turret on
    /// the wrong head.
    /// </summary>
    public class RestorationGroupTests
    {
        private const string Cockpit = "MyObjectBuilder_Cockpit/LargeBlockCockpit";
        private const string Armor = "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock";
        private const string Stator = "MyObjectBuilder_MotorAdvancedStator/LargeAdvancedStator";
        private const string Rotor = "MyObjectBuilder_MotorAdvancedRotor/LargeAdvancedRotor";
        private const string SmallRotor = "MyObjectBuilder_MotorAdvancedRotor/SmallAdvancedRotor";
        private const string Gatling = "MyObjectBuilder_LargeGatlingTurret/LargeGatlingTurret";
        private const string Hinge = "MyObjectBuilder_MotorAdvancedStator/LargeHinge";
        private const string HingeHead = "MyObjectBuilder_MotorAdvancedRotor/LargeHingeHead";

        private static readonly Dictionary<string, int> Plate = new Dictionary<string, int> { { "SteelPlate", 10 } };
        private static readonly Dictionary<string, int> RotorBom = new Dictionary<string, int> { { "SteelPlate", 30 }, { "LargeTube", 6 } };

        private static readonly GridTransform T0 = new GridTransform(GridRotation.All[5], new GridCell(10, -3, 7));
        private static readonly GridTransform T1 = new GridTransform(GridRotation.All[13], new GridCell(-2, 4, 1));
        private static readonly GridTransform T2 = new GridTransform(GridRotation.All[20], new GridCell(3, 3, 3));

        private static RestorationBlock B(string def, int x, int y, int z, GridDir fwd = GridDir.Forward, GridDir up = GridDir.Up,
                                          IReadOnlyDictionary<string, int> bom = null)
        {
            return new RestorationBlock
            {
                DefinitionId = def,
                Min = new GridCell(x, y, z),
                Forward = fwd,
                Up = up,
                Components = bom ?? Plate,
                IsAnchor = def == Cockpit,
                Integrity = 100f,
                MaxIntegrity = 100f,
            };
        }

        private static RestorationBlock Moved(RestorationBlock b, GridTransform t, string def = null)
        {
            RestorationPlacement p = RestorationDiff.Place(b, t);
            return new RestorationBlock
            {
                DefinitionId = def ?? b.DefinitionId,
                Min = p.Min,
                Forward = p.Forward,
                Up = p.Up,
                Components = b.Components,
                IsAnchor = b.IsAnchor,
                Integrity = 100f,
                MaxIntegrity = 100f,
            };
        }

        private static RestorationBlock Damage(RestorationBlock b)
        {
            b.Integrity = 30f;
            b.MissingComponents = new Dictionary<string, int> { { "SteelPlate", 4 } };
            return b;
        }

        // Design grid 0, the hull: cockpit, armour, and a stator at (0,1,0) (index 3).
        private static List<RestorationBlock> Hull()
        {
            return new List<RestorationBlock>
            {
                B(Cockpit, 0, 0, 0), B(Armor, 1, 0, 0), B(Armor, 2, 0, 0), B(Stator, 0, 1, 0, GridDir.Right, GridDir.Up),
                B(Armor, 1, 1, 0),
            };
        }

        // Design grid 1, the turret on the rotor: the head at (5,5,5) (index 0), a gatling, armour,
        // and a hinge at (6,6,5) (index 4).
        private static List<RestorationBlock> Turret()
        {
            return new List<RestorationBlock>
            {
                B(Rotor, 5, 5, 5, bom: RotorBom), B(Gatling, 5, 6, 5), B(Armor, 6, 5, 5), B(Armor, 6, 6, 4),
                B(Hinge, 6, 6, 5, GridDir.Up, GridDir.Backward),
            };
        }

        // Design grid 2, the hinge's top: the head (index 0) and one armour block.
        private static List<RestorationBlock> HingeTop()
        {
            return new List<RestorationBlock> { B(HingeHead, 0, 0, 0), B(Armor, 0, 1, 0) };
        }

        private static RestorationAssembly Design(bool withHinge = true)
        {
            var a = new RestorationAssembly();
            a.Grids.Add(Hull());
            a.Grids.Add(Turret());
            a.Joints.Add(new RestorationJoint { BaseGrid = 0, BaseBlock = 3, TopGrid = 1, TopBlock = 0 });
            if (withHinge)
            {
                a.Grids.Add(HingeTop());
                a.Joints.Add(new RestorationJoint { BaseGrid = 1, BaseBlock = 4, TopGrid = 2, TopBlock = 0 });
            }
            return a;
        }

        private static List<RestorationBlock> Copy(IReadOnlyList<RestorationBlock> design, GridTransform t)
        {
            return design.Select(b => Moved(b, t)).ToList();
        }

        /// <summary>The whole ship, live: each grid under its own transform, joined as designed.</summary>
        private static RestorationAssembly Live(RestorationAssembly design)
        {
            var a = new RestorationAssembly();
            a.Grids.Add(Copy(design.Grids[0], T0));
            a.Grids.Add(Copy(design.Grids[1], T1));
            if (design.Grids.Count > 2) a.Grids.Add(Copy(design.Grids[2], T2));
            foreach (RestorationJoint j in design.Joints)
                a.Joints.Add(new RestorationJoint { BaseGrid = j.BaseGrid, BaseBlock = j.BaseBlock, TopGrid = j.TopGrid, TopBlock = j.TopBlock });
            return a;
        }

        private static List<RestorationBlock> Grid(RestorationAssembly a, int g)
        {
            return (List<RestorationBlock>)a.Grids[g];
        }

        [Fact]
        public void A_whole_group_needs_nothing_and_every_grid_is_paired()
        {
            RestorationAssembly design = Design();
            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, Live(design), 0, T0);

            Assert.Equal(3, plan.Grids.Count);
            Assert.Empty(plan.Sequence.Steps);
            Assert.Empty(plan.Unpaired);
            Assert.Equal(0, plan.ExtraLiveGrids);
            Assert.Equal(T1, plan.Grids[1].Transform);
            Assert.Equal(T2, plan.Grids[2].Transform);
            Assert.All(plan.Grids, g => Assert.Equal(g.Plan.ReferenceBlocks, g.Plan.Intact));
        }

        [Fact]
        public void A_subgrid_is_diffed_in_its_own_frame_found_through_its_joint()
        {
            RestorationAssembly design = Design(withHinge: false);
            RestorationAssembly live = Live(design);
            Damage(Grid(live, 1)[1]);                   // the gatling
            Grid(live, 1).RemoveAt(3);                  // an armour block ground off

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Equal(2, plan.Grids.Count);
            Assert.Equal(1, plan.Grids[1].LiveGrid);
            Assert.Equal(T1, plan.Grids[1].Transform);
            Assert.Equal(1, plan.Sequence.Welds);
            Assert.Equal(1, plan.Sequence.Places);
            RestorationStep weld = plan.Sequence.Steps[0];
            RestorationStep place = plan.Sequence.Steps[1];
            Assert.Equal(RestorationStepKind.Weld, weld.Kind);
            Assert.Equal(1, weld.Grid);
            Assert.Equal(T1.Apply(new GridCell(5, 6, 5)), weld.Placement.Min);
            Assert.Equal(RestorationStepKind.Place, place.Kind);
            Assert.Equal(1, place.Grid);
            Assert.Equal(RestorationDiff.Place(design.Grids[1][3], T1).Min, place.Placement.Min);
            // The hull is whole: nothing of the subgrid reads missing against it.
            Assert.Equal(design.Grids[0].Count, plan.Grids[0].Plan.Intact);
            Assert.Equal(0, plan.Extra);
            Assert.Equal("1:1/1", plan.GridsField());
        }

        [Fact]
        public void A_missing_top_is_attached_then_its_grid_built_outward_from_it()
        {
            RestorationAssembly design = Design(withHinge: false);
            RestorationAssembly live = Live(design);
            live.Grids.RemoveAt(1);
            live.Joints.Clear();

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Equal(2, plan.Grids.Count);
            Assert.True(plan.Grids[1].Attach);
            Assert.Equal(1, plan.Sequence.Attaches);
            List<RestorationStep> steps = plan.Sequence.Steps;
            Assert.Equal(RestorationStepKind.AttachTop, steps[0].Kind);
            Assert.Equal(1, steps[0].Grid);
            Assert.Equal(0, steps[0].ReferenceIndex);
            Assert.Equal(30, steps[0].Bill["SteelPlate"]);
            Assert.Equal(6, steps[0].Bill["LargeTube"]);
            // Placements are in design coordinates until the top exists, walking out from the head.
            Assert.Equal(new[] { new GridCell(5, 6, 5), new GridCell(6, 5, 5), new GridCell(6, 6, 5), new GridCell(6, 6, 4) },
                         steps.Skip(1).Select(s => s.Placement.Min).ToArray());
            Assert.All(steps.Skip(1), s => Assert.Equal(1, s.Grid));
            Assert.Equal(0, plan.Sequence.Unreachable);
            Assert.Equal("1:0/4+attach", plan.GridsField());
        }

        [Fact]
        public void A_missing_base_is_placed_before_its_top_is_attached_and_the_chain_follows()
        {
            RestorationAssembly design = Design();
            RestorationAssembly live = new RestorationAssembly();
            List<RestorationBlock> hull = Copy(design.Grids[0], T0);
            hull.RemoveAt(3);                           // the stator, and with it everything above
            live.Grids.Add(hull);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Equal(3, plan.Grids.Count);
            Assert.True(plan.Grids[1].Attach);
            Assert.True(plan.Grids[2].Attach);
            List<RestorationStep> steps = plan.Sequence.Steps;
            int stator = steps.FindIndex(s => s.Grid == 0 && s.Kind == RestorationStepKind.Place && s.ReferenceIndex == 3);
            int rotor = steps.FindIndex(s => s.Grid == 1 && s.Kind == RestorationStepKind.AttachTop);
            int hinge = steps.FindIndex(s => s.Grid == 1 && s.Kind == RestorationStepKind.Place && s.ReferenceIndex == 4);
            int head = steps.FindIndex(s => s.Grid == 2 && s.Kind == RestorationStepKind.AttachTop);
            int last = steps.FindIndex(s => s.Grid == 2 && s.Kind == RestorationStepKind.Place);
            Assert.True(stator >= 0 && stator < rotor && rotor < hinge && hinge < head && head < last,
                        string.Join(" ", steps.Select(s => s.Grid + ":" + s.Kind + ":" + s.ReferenceIndex)));
            Assert.Equal(2, plan.Sequence.Attaches);
            Assert.Empty(plan.Unpaired);
        }

        [Fact]
        public void A_live_top_of_another_type_leaves_its_subgrid_and_everything_beyond_unpaired()
        {
            RestorationAssembly design = Design();
            RestorationAssembly live = Live(design);
            Grid(live, 1)[0] = Moved(design.Grids[1][0], T1, SmallRotor);
            Damage(Grid(live, 2)[1]);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Single(plan.Grids);
            Assert.Equal(2, plan.Unpaired.Count);
            Assert.Equal(1, plan.Unpaired[0].DesignGrid);
            Assert.Equal(RestorationGroup.TopDiffers, plan.Unpaired[0].Reason);
            Assert.Equal(2, plan.Unpaired[1].DesignGrid);
            Assert.Equal(RestorationGroup.ParentUnpaired, plan.Unpaired[1].Reason);
            // Nothing is welded on a grid the restore could not pair.
            Assert.Empty(plan.Sequence.Steps);
            Assert.Equal(2, plan.ExtraLiveGrids);
            Assert.Equal("1:top-differs,2:parent-unpaired", plan.GridsField());
        }

        [Fact]
        public void A_nested_subgrid_is_paired_through_the_chain_of_joints()
        {
            RestorationAssembly design = Design();
            RestorationAssembly live = Live(design);
            Grid(live, 2).RemoveAt(1);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Equal(3, plan.Grids.Count);
            Assert.Equal(2, plan.Grids[2].LiveGrid);
            Assert.Equal(1, plan.Grids[2].Parent);
            RestorationStep place = plan.Sequence.Steps.Single();
            Assert.Equal(2, place.Grid);
            Assert.Equal(T2.Apply(new GridCell(0, 1, 0)), place.Placement.Min);
        }

        [Fact]
        public void Pairing_walks_up_from_a_top_when_the_selected_grid_is_the_subgrid()
        {
            RestorationAssembly design = Design(withHinge: false);
            RestorationAssembly live = Live(design);
            Grid(live, 0).RemoveAt(4);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 1, live, 1, T1);

            Assert.Equal(2, plan.Grids.Count);
            Assert.Equal(0, plan.Grids[1].DesignGrid);
            Assert.Equal(0, plan.Grids[1].LiveGrid);
            Assert.Equal(T0, plan.Grids[1].Transform);
            RestorationStep place = plan.Sequence.Steps.Single();
            Assert.Equal(T0.Apply(new GridCell(1, 1, 0)), place.Placement.Min);
        }

        [Fact]
        public void The_owners_own_subgrid_is_left_alone()
        {
            RestorationAssembly design = Design(withHinge: false);
            RestorationAssembly live = Live(design);
            // An extra stator on the hull holding an extra grid the design never had.
            Grid(live, 0).Add(Moved(B(Stator, 2, 1, 0), T0));
            live.Grids.Add(new List<RestorationBlock> { B(Rotor, 0, 0, 0), B(Armor, 0, 1, 0) });
            live.Joints.Add(new RestorationJoint { BaseGrid = 0, BaseBlock = Grid(live, 0).Count - 1, TopGrid = 2, TopBlock = 0 });

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Equal(1, plan.ExtraLiveGrids);
            Assert.Equal(1, plan.Extra);
            Assert.Empty(plan.Sequence.Steps);
        }

        [Fact]
        public void A_missing_base_the_restore_cannot_place_leaves_its_subgrid_unpaired()
        {
            RestorationAssembly design = Design(withHinge: false);
            var live = new RestorationAssembly();
            List<RestorationBlock> hull = Copy(design.Grids[0], T0);
            hull[3] = Moved(B(Armor, 0, 1, 0), T0);     // the owner's armour where the stator was
            live.Grids.Add(hull);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);

            Assert.Single(plan.Grids);
            Assert.Equal(RestorationGroup.BaseNotRestorable, plan.Unpaired.Single().Reason);
            Assert.Equal(1, plan.Sequence.Blocked);
            Assert.Empty(plan.Sequence.Steps);
        }

        [Fact]
        public void The_group_bill_covers_every_grid_and_the_attached_top()
        {
            RestorationAssembly design = Design(withHinge: false);
            RestorationAssembly live = Live(design);
            live.Grids.RemoveAt(1);
            live.Joints.Clear();
            Grid(live, 0).RemoveAt(4);

            RestorationGroupPlan plan = RestorationGroup.Plan(design, 0, live, 0, T0);
            Dictionary<string, long> need = plan.Sequence.Need();

            // Hull armour 10 + four turret blocks 40 + the rotor head 30.
            Assert.Equal(80, need["SteelPlate"]);
            Assert.Equal(6, need["LargeTube"]);
            Assert.Equal(86, plan.BillTotal);
        }

        [Fact]
        public void A_group_fingerprint_keeps_a_single_grid_name_and_sees_a_subgrid_change()
        {
            RestorationAssembly design = Design();
            Assert.Equal(RestorationDiff.Fingerprint(design.Grids[0]), RestorationDiff.Fingerprint(new[] { design.Grids[0] }));

            string whole = RestorationDiff.Fingerprint(design.Grids);
            Assert.NotEqual(RestorationDiff.Fingerprint(design.Grids[0]), whole);

            // The group's grids in another order, and a subgrid moved on its lattice, are the same design.
            var reordered = new List<IReadOnlyList<RestorationBlock>>
            {
                design.Grids[0], design.Grids[2], Copy(design.Grids[1], new GridTransform(GridRotation.Identity, new GridCell(4, -9, 2))),
            };
            Assert.Equal(whole, RestorationDiff.Fingerprint(reordered));

            List<RestorationBlock> cut = Turret();
            cut.RemoveAt(2);
            Assert.NotEqual(whole, RestorationDiff.Fingerprint(new List<IReadOnlyList<RestorationBlock>> { design.Grids[0], cut, design.Grids[2] }));
        }
    }
}
