using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the timed restoration: the step order (welds first, then placements outward from what
    /// survives), the bill's split ship → node → bank at a premium, per-block charging (a refused
    /// block costs nothing), pause and resume, and the stops that name a component.
    /// </summary>
    public class RestorationWorkTests
    {
        private static readonly Dictionary<string, int> Plate = new Dictionary<string, int> { { "SteelPlate", 10 } };
        private static readonly DateTime T0 = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        private static RestorationBlock B(int x, int y, int z, IReadOnlyDictionary<string, int> bom = null, string def = "Armor/Block")
        {
            return new RestorationBlock
            {
                DefinitionId = def,
                Min = new GridCell(x, y, z),
                Components = bom ?? Plate,
                Integrity = 100f,
                MaxIntegrity = 100f,
            };
        }

        private static RestorationBlock Damaged(RestorationBlock b, int missingPlates)
        {
            return new RestorationBlock
            {
                DefinitionId = b.DefinitionId,
                Min = b.Min,
                Components = b.Components,
                Integrity = 40f,
                MaxIntegrity = 100f,
                MissingComponents = new Dictionary<string, int> { { "SteelPlate", missingPlates } },
            };
        }

        /// <summary>A line of seven blocks along X, 0..6.</summary>
        private static List<RestorationBlock> Line(int n)
        {
            var list = new List<RestorationBlock>();
            for (int i = 0; i < n; i++) list.Add(B(i, 0, 0));
            return list;
        }

        // ---- order ----------------------------------------------------------------------------

        [Fact]
        public void Welds_come_first_then_placements_walk_outward_from_the_survivors()
        {
            // Design: x=0..6. Survivors: x=0 (damaged) and x=6. Missing 1..5 in design order would
            // place 5 before anything touches it; the walk must reach it from 6.
            List<RestorationBlock> design = Line(7);
            var current = new List<RestorationBlock> { Damaged(design[0], 4), B(6, 0, 0) };
            RestorationPlan plan = RestorationDiff.Diff(design, current, GridTransform.Identity);

            RestorationSequence seq = RestorationOrder.Build(plan, current);

            Assert.Equal(1, seq.Welds);
            Assert.Equal(5, seq.Places);
            Assert.Equal(0, seq.Unreachable);
            Assert.Equal(RestorationStepKind.Weld, seq.Steps[0].Kind);
            // First ring in design order: x=1 (touches 0) then x=5 (touches 6); then 2, 4; then 3.
            int[] xs = seq.Steps.Skip(1).Select(s => s.Placement.Min.X).ToArray();
            Assert.Equal(new[] { 1, 5, 2, 4, 3 }, xs);
        }

        [Fact]
        public void Every_placement_touches_a_survivor_or_an_earlier_placement()
        {
            List<RestorationBlock> design = new List<RestorationBlock>();
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 4; y++)
                    design.Add(B(x, y, 0));
            var current = new List<RestorationBlock> { B(3, 3, 0) };
            RestorationPlan plan = RestorationDiff.Diff(design, current, GridTransform.Identity);
            RestorationSequence seq = RestorationOrder.Build(plan, current);

            var occupied = new HashSet<GridCell> { new GridCell(3, 3, 0) };
            foreach (RestorationStep s in seq.Steps)
            {
                GridCell c = s.Placement.Min;
                bool touches = occupied.Contains(c + new GridCell(1, 0, 0)) || occupied.Contains(c + new GridCell(-1, 0, 0))
                            || occupied.Contains(c + new GridCell(0, 1, 0)) || occupied.Contains(c + new GridCell(0, -1, 0));
                Assert.True(touches, "placed " + c + " with no mounted neighbour");
                occupied.Add(c);
            }
            Assert.Equal(15, seq.Places);
        }

        [Fact]
        public void A_missing_block_nothing_reaches_is_unreachable_and_a_blocked_one_is_not_placed()
        {
            // Design: x=0,1 and an island at x=5. Survivor x=0. The island is never adjacent.
            var design = new List<RestorationBlock> { B(0, 0, 0), B(1, 0, 0), B(5, 0, 0), B(0, 1, 0) };
            // The owner put a different block in (0,1,0): blocked.
            var current = new List<RestorationBlock> { B(0, 0, 0), B(0, 1, 0, def: "Other/Block") };
            RestorationPlan plan = RestorationDiff.Diff(design, current, GridTransform.Identity);
            RestorationSequence seq = RestorationOrder.Build(plan, current);

            Assert.Equal(1, seq.Places);
            Assert.Equal(1, seq.Unreachable);
            Assert.Equal(1, seq.Blocked);
            Assert.Equal(new GridCell(1, 0, 0), seq.Steps.Single().Placement.Min);
        }

        // ---- bill -----------------------------------------------------------------------------

        private static RestorationSupply Supply(long ship, long node, long nodePrice, long bankPrice)
        {
            var s = new RestorationSupply();
            if (ship > 0) s.Ship["SteelPlate"] = ship;
            if (node > 0) s.Node["SteelPlate"] = new RestorationOffer { Available = node, UnitPrice = nodePrice };
            if (bankPrice > 0) s.BankPrice["SteelPlate"] = bankPrice;
            return s;
        }

        [Fact]
        public void The_bill_draws_ship_cargo_then_the_node_then_the_bank_at_its_premium()
        {
            RestorationSupply supply = Supply(ship: 7, node: 5, nodePrice: 100, bankPrice: RestorationBilling.BankUnitPrice(100));
            var need = new Dictionary<string, long> { { "SteelPlate", 20 } };

            RestorationBill bill = RestorationBilling.Price(need, supply);
            RestorationBillLine l = bill.Lines.Single();

            Assert.Equal(7, l.FromShip);
            Assert.Equal(5, l.FromNode);
            Assert.Equal(500, l.NodeCredits);
            Assert.Equal(8, l.FromBank);
            Assert.Equal(150, RestorationBilling.BankUnitPrice(100));
            Assert.Equal(8 * 150, l.BankCredits);
            Assert.Equal(500 + 1200, bill.Credits);
            Assert.Equal(0, l.Short);
            // Pricing does not draw anything down.
            Assert.Equal(7, supply.Ship["SteelPlate"]);
            Assert.Equal(5, supply.Node["SteelPlate"].Available);
        }

        [Fact]
        public void The_bank_premium_makes_a_short_node_dearer_never_free()
        {
            Assert.True(RestorationBilling.BankUnitPrice(100) > 100);
            Assert.Equal(2, RestorationBilling.BankUnitPrice(0));
            Assert.Equal(2, RestorationBilling.BankUnitPrice(1));
        }

        [Fact]
        public void A_component_no_source_has_is_short()
        {
            RestorationSupply supply = Supply(ship: 3, node: 0, nodePrice: 0, bankPrice: 0);
            RestorationBill bill = RestorationBilling.Price(new Dictionary<string, long> { { "SteelPlate", 10 } }, supply);
            Assert.Equal("SteelPlate", bill.FirstShort);
            Assert.Equal(7, bill.Lines.Single().Short);
        }

        // ---- the job --------------------------------------------------------------------------

        private sealed class FakeApplier : IRestorationApplier
        {
            public long Wallet;
            public readonly HashSet<GridCell> Refuse = new HashSet<GridCell>();
            public readonly List<GridCell> Applied = new List<GridCell>();
            public readonly List<long> Charges = new List<long>();
            public long Balance { get { return Wallet; } }

            /// <summary>A cell refused until the other cell is built: a slope that mounts only on a
            /// neighbour placed later in the same pass.</summary>
            public readonly Dictionary<GridCell, GridCell> MountsOn = new Dictionary<GridCell, GridCell>();
            public int Attempts;

            public bool Apply(RestorationStep step)
            {
                Attempts++;
                if (Refuse.Contains(step.Placement.Min)) return false;
                GridCell mount;
                if (MountsOn.TryGetValue(step.Placement.Min, out mount) && !Applied.Contains(mount)) return false;
                Applied.Add(step.Placement.Min);
                return true;
            }

            public bool Settle(RestorationStep step, RestorationBill bill)
            {
                if (bill.Credits > Wallet) return false;
                Wallet -= bill.Credits;
                Charges.Add(bill.Credits);
                return true;
            }
        }

        private static List<RestorationStep> Places(int n)
        {
            var steps = new List<RestorationStep>();
            for (int i = 0; i < n; i++)
                steps.Add(new RestorationStep
                {
                    Kind = RestorationStepKind.Place,
                    ReferenceIndex = i,
                    Placement = new RestorationPlacement { Min = new GridCell(i, 0, 0), Max = new GridCell(i, 0, 0) },
                    Bill = new Dictionary<string, int>(Plate),
                });
            return steps;
        }

        [Fact]
        public void Each_block_is_charged_as_it_is_placed_and_cargo_goes_first()
        {
            // 3 blocks x 10 plates: 15 in cargo, 10 at the node for 100, the bank at 150.
            RestorationSupply supply = Supply(ship: 15, node: 10, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(3), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };

            job.Pump(T0, true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(3, job.Placed);
            // Block 1: 10 from cargo, free. Block 2: 5 cargo + 5 node. Block 3: 5 node + 5 bank.
            Assert.Equal(new long[] { 0, 500, 500 + 750 }, app.Charges.ToArray());
            Assert.Equal(15, job.FromShipUnits);
            Assert.Equal(10, job.FromNodeUnits);
            Assert.Equal(5, job.FromBankUnits);
            Assert.Equal(1000, job.PaidNode);
            Assert.Equal(750, job.PaidBank);
            Assert.Equal(1750, job.Paid);
            Assert.Equal(0, supply.Ship["SteelPlate"]);
        }

        [Fact]
        public void The_sum_of_per_block_charges_equals_the_quoted_total()
        {
            RestorationSupply quoteSupply = Supply(ship: 15, node: 10, nodePrice: 100, bankPrice: 150);
            List<RestorationStep> steps = Places(3);
            var seq = new RestorationSequence();
            seq.Steps.AddRange(steps);
            RestorationBill quote = RestorationBilling.Price(seq.Need(), quoteSupply);

            RestorationSupply runSupply = Supply(ship: 15, node: 10, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(steps, quote.Credits, T0);
            job.Pump(T0, true, DateTime.MinValue, runSupply, new FakeApplier { Wallet = 1000000 });

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(quote.Credits, job.Paid);
        }

        [Fact]
        public void A_block_the_game_refuses_costs_nothing()
        {
            RestorationSupply supply = Supply(ship: 0, node: 100, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(3), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            app.Refuse.Add(new GridCell(1, 0, 0));

            for (int i = 0; i < 10 && !job.Finished; i++)
                job.Pump(T0.AddSeconds(i * RestorationJob.IntervalSeconds), true, DateTime.MinValue, supply, app);

            Assert.Equal(2, job.Placed);
            Assert.Equal(1, job.Skipped);
            Assert.Equal(2, app.Charges.Count);
            Assert.Equal(2000, job.Paid);
            Assert.Equal(80, supply.Node["SteelPlate"].Available);
        }

        [Fact]
        public void A_block_refused_for_a_neighbour_placed_later_is_placed_on_the_next_pass_and_charged_once()
        {
            RestorationSupply supply = Supply(ship: 0, node: 100, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(4), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            // x=0 mounts only on x=1, x=1 only on x=3: pass 1 places 2 and 3, pass 2 places 1, pass 3 places 0.
            app.MountsOn[new GridCell(0, 0, 0)] = new GridCell(1, 0, 0);
            app.MountsOn[new GridCell(1, 0, 0)] = new GridCell(3, 0, 0);

            for (int i = 0; i < 10 && !job.Finished; i++)
                job.Pump(T0.AddSeconds(i * RestorationJob.IntervalSeconds), true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(4, job.Placed);
            Assert.Equal(0, job.Skipped);
            Assert.Equal(3, job.Passes);
            Assert.Equal(2, job.Retried);
            Assert.Equal(new[] { new GridCell(2, 0, 0), new GridCell(3, 0, 0), new GridCell(1, 0, 0), new GridCell(0, 0, 0) },
                         app.Applied.ToArray());
            // Four blocks, four charges: a refusal costs nothing, however often it is retried.
            Assert.Equal(4, app.Charges.Count);
            Assert.Equal(4000, job.Paid);
            Assert.Equal(60, supply.Node["SteelPlate"].Available);
        }

        [Fact]
        public void Passes_stop_when_one_places_nothing_and_what_is_left_is_skipped()
        {
            RestorationSupply supply = Supply(ship: 0, node: 100, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(3), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            app.Refuse.Add(new GridCell(1, 0, 0));

            for (int i = 0; i < 10 && !job.Finished; i++)
                job.Pump(T0.AddSeconds(i * RestorationJob.IntervalSeconds), true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(2, job.Placed);
            Assert.Equal(1, job.Skipped);
            // Pass 1 places two and refuses one; pass 2 retries it, places nothing, and ends it.
            Assert.Equal(2, job.Passes);
            Assert.Equal(4, app.Attempts);
            Assert.Equal(0, job.Remaining);
            Assert.Equal(2000, job.Paid);
        }

        [Fact]
        public void A_pass_that_refuses_everything_ends_the_job_without_a_retry()
        {
            var job = new RestorationJob(Places(2), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            app.Refuse.Add(new GridCell(0, 0, 0));
            app.Refuse.Add(new GridCell(1, 0, 0));

            job.Pump(T0, true, DateTime.MinValue, Supply(ship: 100, node: 0, nodePrice: 0, bankPrice: 0), app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(1, job.Passes);
            Assert.Equal(2, job.Skipped);
            Assert.Empty(app.Charges);
        }

        [Fact]
        public void The_next_pass_starts_at_an_interval_boundary_so_its_steps_are_priced_with_their_own_supply()
        {
            var job = new RestorationJob(Places(RestorationJob.BlocksPerInterval), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            app.MountsOn[new GridCell(0, 0, 0)] = new GridCell(1, 0, 0);

            job.Pump(T0, true, DateTime.MinValue, Supply(ship: 100, node: 0, nodePrice: 0, bankPrice: 0), app);

            Assert.Equal(RestorationJobStatus.Running, job.Status);
            Assert.Equal(2, job.Passes);
            Assert.Equal(1, job.Remaining);
            Assert.Equal(new GridCell(0, 0, 0), job.Upcoming(0).Placement.Min);
            Assert.Null(job.Upcoming(1));
            job.Pump(T0.AddSeconds(RestorationJob.IntervalSeconds), true, DateTime.MinValue,
                     Supply(ship: 100, node: 0, nodePrice: 0, bankPrice: 0), app);
            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(RestorationJob.BlocksPerInterval, job.Placed);
        }

        [Fact]
        public void A_refused_weld_is_skipped_at_once_and_not_retried()
        {
            var steps = new List<RestorationStep>
            {
                new RestorationStep { Kind = RestorationStepKind.Weld, Placement = new RestorationPlacement { Min = new GridCell(9, 9, 9) } },
            };
            steps.AddRange(Places(1));
            var job = new RestorationJob(steps, long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };
            app.Refuse.Add(new GridCell(9, 9, 9));

            job.Pump(T0, true, DateTime.MinValue, Supply(ship: 100, node: 0, nodePrice: 0, bankPrice: 0), app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(1, job.Skipped);
            Assert.Equal(1, job.Passes);
            Assert.Equal(2, app.Attempts);
        }

        [Fact]
        public void An_attached_top_counts_apart_from_placements()
        {
            var steps = new List<RestorationStep>
            {
                new RestorationStep
                {
                    Kind = RestorationStepKind.AttachTop, Grid = 1,
                    Placement = new RestorationPlacement { Min = new GridCell(5, 5, 5) },
                    Bill = new Dictionary<string, int>(Plate),
                },
            };
            var job = new RestorationJob(steps, long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };

            job.Pump(T0, true, DateTime.MinValue, Supply(ship: 100, node: 0, nodePrice: 0, bankPrice: 0), app);

            Assert.Equal(1, job.Attached);
            Assert.Equal(0, job.Placed);
            Assert.Equal(10, job.FromShipUnits);
        }

        [Fact]
        public void An_unaffordable_block_stops_the_job_naming_the_component_and_charges_nothing_for_it()
        {
            RestorationSupply supply = Supply(ship: 0, node: 100, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(3), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1500 };

            job.Pump(T0, true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Stopped, job.Status);
            Assert.Equal(1, job.Placed);
            Assert.Equal(1000, job.Paid);
            Assert.Equal(500, app.Wallet);
            Assert.Contains("SteelPlate", job.StopReason);
            Assert.Contains("cannot afford", job.StopReason);
            Assert.Single(app.Applied);
        }

        [Fact]
        public void A_component_no_source_supplies_stops_the_job_naming_it()
        {
            RestorationSupply supply = Supply(ship: 10, node: 0, nodePrice: 0, bankPrice: 0);
            var job = new RestorationJob(Places(2), long.MaxValue, T0);
            var app = new FakeApplier { Wallet = 1000000 };

            job.Pump(T0, true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Stopped, job.Status);
            Assert.Equal(1, job.Placed);
            Assert.StartsWith("SteelPlate", job.StopReason);
        }

        [Fact]
        public void The_confirmed_budget_is_never_exceeded()
        {
            RestorationSupply supply = Supply(ship: 0, node: 100, nodePrice: 100, bankPrice: 150);
            var job = new RestorationJob(Places(3), 2500, T0);
            var app = new FakeApplier { Wallet = 1000000 };

            job.Pump(T0, true, DateTime.MinValue, supply, app);

            Assert.Equal(RestorationJobStatus.Stopped, job.Status);
            Assert.Equal(2, job.Placed);
            Assert.Equal(2000, job.Paid);
            Assert.Contains("2,500", job.StopReason);
        }

        [Fact]
        public void Steps_run_a_few_per_interval()
        {
            RestorationSupply supply = Supply(ship: 1000, node: 0, nodePrice: 0, bankPrice: 0);
            var job = new RestorationJob(Places(RestorationJob.BlocksPerInterval * 2 + 1), long.MaxValue, T0);
            var app = new FakeApplier();

            Assert.Equal(RestorationJob.BlocksPerInterval, job.Pump(T0, true, DateTime.MinValue, supply, app));
            Assert.Equal(0, job.Pump(T0.AddSeconds(RestorationJob.IntervalSeconds / 2), true, DateTime.MinValue, supply, app));
            Assert.Equal(RestorationJob.BlocksPerInterval, job.Pump(T0.AddSeconds(RestorationJob.IntervalSeconds), true, DateTime.MinValue, supply, app));
            Assert.Equal(1, job.Pump(T0.AddSeconds(RestorationJob.IntervalSeconds * 2), true, DateTime.MinValue, supply, app));
            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(RestorationJob.EstimatedSeconds(RestorationJob.BlocksPerInterval * 2 + 1),
                         (int)Math.Ceiling(RestorationJob.IntervalSeconds * 3));
        }

        [Fact]
        public void Leaving_range_pauses_and_returning_resumes()
        {
            RestorationSupply supply = Supply(ship: 1000, node: 0, nodePrice: 0, bankPrice: 0);
            var job = new RestorationJob(Places(12), long.MaxValue, T0);
            var app = new FakeApplier();

            job.Pump(T0, true, DateTime.MinValue, supply, app);
            int before = app.Applied.Count;

            Assert.Equal(0, job.Pump(T0.AddSeconds(10), false, DateTime.MinValue, supply, app));
            Assert.Equal(RestorationJobStatus.Paused, job.Status);
            Assert.Equal("range", job.PauseReason);
            Assert.Equal(0, job.Pump(T0.AddSeconds(60), false, DateTime.MinValue, supply, app));
            Assert.Equal(before, app.Applied.Count);

            Assert.Equal(RestorationJob.BlocksPerInterval, job.Pump(T0.AddSeconds(61), true, DateTime.MinValue, supply, app));
            Assert.Equal(RestorationJobStatus.Running, job.Status);
            Assert.Null(job.PauseReason);
        }

        [Fact]
        public void A_hit_pauses_until_the_ship_has_been_quiet()
        {
            RestorationSupply supply = Supply(ship: 1000, node: 0, nodePrice: 0, bankPrice: 0);
            var job = new RestorationJob(Places(12), long.MaxValue, T0);
            var app = new FakeApplier();
            DateTime hit = T0.AddSeconds(5);

            Assert.Equal(0, job.Pump(T0.AddSeconds(6), true, hit, supply, app));
            Assert.Equal("damage", job.PauseReason);
            Assert.Equal(0, job.Pump(hit.AddSeconds(RestorationJob.DamageQuietSeconds - 1), true, hit, supply, app));
            Assert.Empty(app.Applied);

            Assert.True(job.Pump(hit.AddSeconds(RestorationJob.DamageQuietSeconds), true, hit, supply, app) > 0);
            Assert.Equal(RestorationJobStatus.Running, job.Status);
        }

        [Fact]
        public void A_weld_with_no_components_is_free()
        {
            var steps = new List<RestorationStep>
            {
                new RestorationStep { Kind = RestorationStepKind.Weld, Placement = new RestorationPlacement() },
            };
            var job = new RestorationJob(steps, 0, T0);
            var app = new FakeApplier();

            job.Pump(T0, true, DateTime.MinValue, new RestorationSupply(), app);

            Assert.Equal(RestorationJobStatus.Done, job.Status);
            Assert.Equal(1, job.Welded);
            Assert.Equal(0, job.Paid);
        }
    }
}
