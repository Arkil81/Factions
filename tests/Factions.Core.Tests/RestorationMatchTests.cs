using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="RestorationMatch"/>: a damaged grid with nothing marking its design, found
    /// in a catalogue. The characteristic failure is a confident wrong answer — a player's own build
    /// matched to whichever catalogue hull it most resembles.
    /// </summary>
    public class RestorationMatchTests
    {
        private const string Cockpit = "MyObjectBuilder_Cockpit/LargeBlockCockpit";

        private static readonly string[] Palette =
        {
            "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock",
            "MyObjectBuilder_CubeBlock/LargeBlockArmorSlope",
            "MyObjectBuilder_CubeBlock/LargeBlockArmorCorner",
            "MyObjectBuilder_CubeBlock/LargeHeavyBlockArmorBlock",
            "MyObjectBuilder_Thrust/LargeBlockSmallThrust",
            "MyObjectBuilder_Gyro/LargeBlockGyro",
            "MyObjectBuilder_InteriorLight/SmallLight",
            "MyObjectBuilder_Reactor/LargeBlockSmallGenerator",
            "MyObjectBuilder_CargoContainer/LargeBlockSmallContainer",
            "MyObjectBuilder_LargeGatlingTurret/",
        };

        private static readonly GridDir[] Dirs =
            { GridDir.Forward, GridDir.Backward, GridDir.Left, GridDir.Right, GridDir.Up, GridDir.Down };

        private static RestorationBlock Block(string def, GridCell at, GridDir fwd, GridDir up, bool anchor = false)
        {
            return new RestorationBlock
            {
                DefinitionId = def, Min = at, Forward = fwd, Up = up, IsAnchor = anchor,
                Integrity = 100f, MaxIntegrity = 100f,
            };
        }

        private static RestorationBlock Copy(RestorationBlock b, GridCell at)
        {
            return Block(b.DefinitionId, at, b.Forward, b.Up, b.IsAnchor);
        }

        /// <summary>A seeded random hull: a cockpit at the origin and <paramref name="n"/> single-cell
        /// blocks of the palette in distinct cells of a 9x5x13 box, each turned at random.</summary>
        private static List<RestorationBlock> Hull(int seed, int n)
        {
            var rng = new Random(seed);
            var used = new HashSet<GridCell> { GridCell.Zero };
            var list = new List<RestorationBlock> { Block(Cockpit, GridCell.Zero, GridDir.Forward, GridDir.Up, anchor: true) };
            while (list.Count < n + 1)
            {
                var c = new GridCell(rng.Next(-4, 5), rng.Next(-2, 3), rng.Next(-6, 7));
                if (!used.Add(c)) continue;
                GridDir f = Dirs[rng.Next(6)], u = Dirs[rng.Next(6)];
                if ((int)f / 2 == (int)u / 2) { used.Remove(c); continue; }
                list.Add(Block(Palette[rng.Next(Palette.Length)], c, f, u));
            }
            return list;
        }

        private static List<RestorationBlock> Moved(List<RestorationBlock> blocks, GridTransform t)
        {
            return blocks.Select(b =>
            {
                RestorationPlacement p = RestorationDiff.Place(b, t);
                return Block(b.DefinitionId, p.Min, p.Forward, p.Up, b.IsAnchor);
            }).ToList();
        }

        /// <summary>Removes <paramref name="share"/> of the non-anchor blocks, chosen by a seeded shuffle.</summary>
        private static List<RestorationBlock> Damaged(List<RestorationBlock> blocks, double share, int seed)
        {
            var rng = new Random(seed);
            List<int> order = Enumerable.Range(0, blocks.Count).Where(i => !blocks[i].IsAnchor).OrderBy(i => rng.Next()).ToList();
            var gone = new HashSet<int>(order.Take((int)Math.Round(order.Count * share)));
            return blocks.Where((b, i) => !gone.Contains(i)).ToList();
        }

        private sealed class Catalogue
        {
            public readonly List<RestorationCatalogueEntry> Entries = new List<RestorationCatalogueEntry>();
            public readonly Dictionary<RestorationCatalogueEntry, List<RestorationBlock>> Blocks =
                new Dictionary<RestorationCatalogueEntry, List<RestorationBlock>>();

            public void Add(string id, List<RestorationBlock> blocks)
            {
                RestorationCatalogueEntry e = RestorationCatalogueEntry.FromBlocks(id, 0, blocks);
                Entries.Add(e);
                Blocks[e] = blocks;
            }

            public RestorationMatchResult Match(List<RestorationBlock> live, out RestorationCandidateSelection sel,
                                                int maxCandidates = RestorationMatch.MaxCandidates)
            {
                sel = RestorationMatch.Select(live, Entries, maxCandidates);
                var cands = sel.Selected.Select(e => new RestorationMatchCandidate { Entry = e, Blocks = Blocks[e] }).ToList();
                return RestorationMatch.Match(live, cands);
            }
        }

        /// <summary>The target, a dozen unrelated hulls, and a near twin of the target with a fifth of
        /// its blocks moved — every one of them holding the target's cockpit definition.</summary>
        private static Catalogue ShipCatalogue(List<RestorationBlock> target)
        {
            var cat = new Catalogue();
            for (int i = 0; i < 12; i++) cat.Add("Decoy" + i.ToString("00"), Hull(100 + i, 60));
            cat.Add("Laevateinn", target);
            cat.Add("Laevateinn_Refit", Relocated(target, 0.2, 9));
            return cat;
        }

        /// <summary>The same blocks, <paramref name="share"/> of the non-anchor ones moved to cells the
        /// hull leaves empty — the same definition counts, a different shape.</summary>
        private static List<RestorationBlock> Relocated(List<RestorationBlock> blocks, double share, int seed)
        {
            var rng = new Random(seed);
            var used = new HashSet<GridCell>(blocks.Select(b => b.Min));
            List<int> order = Enumerable.Range(0, blocks.Count).Where(i => !blocks[i].IsAnchor).OrderBy(i => rng.Next()).ToList();
            var move = new HashSet<int>(order.Take((int)Math.Round(order.Count * share)));
            var result = new List<RestorationBlock>();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!move.Contains(i)) { result.Add(Copy(blocks[i], blocks[i].Min)); continue; }
                GridCell c;
                do c = new GridCell(rng.Next(8, 14), rng.Next(-2, 3), rng.Next(-6, 7)); while (!used.Add(c));
                result.Add(Copy(blocks[i], c));
            }
            return result;
        }

        [Fact]
        public void A_prefab_with_30_percent_removed_and_turned_is_matched_with_its_transform()
        {
            List<RestorationBlock> target = Hull(1, 80);
            Catalogue cat = ShipCatalogue(target);
            var t = new GridTransform(GridRotation.All[7], new GridCell(5, -3, 11));
            List<RestorationBlock> live = Moved(Damaged(target, 0.3, 2), t);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(live, out sel);

            Assert.Equal("anchor", sel.Via);
            Assert.Equal(Cockpit, sel.KeyDefinition);
            Assert.True(r.Accepted);
            Assert.Equal("Laevateinn", r.Best.Entry.Id);
            Assert.Equal(t, r.Best.Fit.Transform);
            Assert.Equal(1.0, r.Best.Score);
            Assert.Equal(live.Count, r.Best.Fit.Matched);
            Assert.InRange(r.Best.Coverage, 0.69, 0.71);
            Assert.Equal("Laevateinn_Refit", r.RunnerUp.Entry.Id);
            Assert.InRange(r.Margin, 0.1, 0.3);

            RestorationPlan plan = RestorationDiff.Diff(target, live, r.Best.Fit.Transform);
            Assert.Equal(target.Count - live.Count, plan.Missing.Count);
            Assert.Empty(plan.Extra);
        }

        [Fact]
        public void A_player_build_near_a_catalogue_hull_is_unmatched_not_matched_to_it()
        {
            // The player started from a catalogue hull and rebuilt 40 % of it: every block of the
            // hull's kind is still there, so its definition counts cannot tell them apart — only the
            // threshold can.
            List<RestorationBlock> hull = Hull(1, 80);
            var cat = new Catalogue();
            for (int i = 0; i < 12; i++) cat.Add("Decoy" + i.ToString("00"), Hull(100 + i, 60));
            cat.Add("Laevateinn", hull);
            List<RestorationBlock> build = Moved(Relocated(hull, 0.4, 5), new GridTransform(GridRotation.All[3], new GridCell(2, 2, 2)));

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(build, out sel);

            Assert.False(r.Accepted);
            Assert.Equal("Laevateinn", r.Best.Entry.Id);
            Assert.InRange(r.Best.Score, 0.5, RestorationMatch.AcceptScore - 0.01);
            Assert.Contains(sel.Selected, e => e.Id == "Laevateinn");
        }

        [Fact]
        public void A_player_build_unlike_anything_in_the_catalogue_is_unmatched()
        {
            List<RestorationBlock> target = Hull(1, 80);
            Catalogue cat = ShipCatalogue(target);
            List<RestorationBlock> build = Hull(999, 70);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(build, out sel);

            Assert.False(r.Accepted);
            Assert.True(r.Best == null || r.Best.Score < 0.3);
        }

        [Fact]
        public void The_match_is_the_same_whatever_order_the_catalogue_is_in()
        {
            List<RestorationBlock> target = Hull(1, 80);
            Catalogue cat = ShipCatalogue(target);
            var t = new GridTransform(GridRotation.All[13], new GridCell(-4, 0, 3));
            List<RestorationBlock> live = Moved(Damaged(target, 0.3, 4), t);

            RestorationCandidateSelection sel;
            RestorationMatchResult first = cat.Match(live, out sel);
            List<string> firstOrder = sel.Selected.Select(e => e.Id).ToList();

            var rng = new Random(77);
            for (int round = 0; round < 3; round++)
            {
                var shuffled = new Catalogue();
                foreach (RestorationCatalogueEntry e in cat.Entries.OrderBy(_ => rng.Next()))
                {
                    shuffled.Entries.Add(e);
                    shuffled.Blocks[e] = cat.Blocks[e];
                }
                RestorationCandidateSelection s2;
                RestorationMatchResult again = shuffled.Match(live, out s2);
                Assert.Equal(firstOrder, s2.Selected.Select(e => e.Id).ToList());
                Assert.Equal(first.Best.Entry.Id, again.Best.Entry.Id);
                Assert.Equal(first.Best.Fit.Transform, again.Best.Fit.Transform);
                Assert.Equal(first.Best.Score, again.Best.Score);
                Assert.Equal(first.RunnerUp.Entry.Id, again.RunnerUp.Entry.Id);
            }
            Assert.Equal("Laevateinn", first.Best.Entry.Id);
            Assert.Equal(t, first.Best.Fit.Transform);
        }

        [Fact]
        public void Candidates_past_the_cap_are_dropped_lowest_bound_first_and_counted()
        {
            List<RestorationBlock> target = Hull(1, 80);
            var cat = new Catalogue();
            // Forty near twins, each short of more blocks than the last, so their bounds descend while
            // their ids ascend — an ordering by id alone would pick the wrong five.
            for (int i = 0; i < 40; i++)
                cat.Add("Twin" + (39 - i).ToString("00"), target.Take(target.Count - i / 4).ToList());
            cat.Add("Laevateinn", target);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(Damaged(target, 0.0, 1), out sel, maxCandidates: 5);

            Assert.Equal(41, sel.Eligible);
            Assert.Equal(5, sel.Selected.Count);
            Assert.Equal(36, sel.Dropped);
            Assert.True(sel.CapHit);
            Assert.Equal(new[] { "Laevateinn", "Twin36", "Twin37", "Twin38", "Twin39" }, sel.Selected.Select(e => e.Id).ToArray());
            Assert.Equal(5, r.Scored);
            Assert.Equal("Laevateinn", r.Best.Entry.Id);
        }

        [Fact]
        public void A_reference_whose_counts_cannot_reach_the_threshold_is_pruned_unscored()
        {
            List<RestorationBlock> target = Hull(1, 80);
            var cat = new Catalogue();
            cat.Add("Laevateinn", target);
            cat.Add("Shuttle", Hull(50, 12));

            RestorationCandidateSelection sel;
            cat.Match(target, out sel);

            Assert.Equal(2, sel.Eligible);
            Assert.Equal(1, sel.Pruned);
            Assert.Equal("Laevateinn", Assert.Single(sel.Selected).Id);
        }

        [Fact]
        public void Equal_scores_go_to_the_reference_the_grid_covers_more_of()
        {
            List<RestorationBlock> hull = Hull(1, 80);
            var extended = hull.Select(b => Copy(b, b.Min)).ToList();
            for (int i = 0; i < 20; i++)
                extended.Add(Block(Palette[0], new GridCell(20 + i, 0, 0), GridDir.Forward, GridDir.Up));
            var cat = new Catalogue();
            cat.Add("Alpha_Extended", extended);   // ordinal-first, so only coverage can put it second
            cat.Add("Zeta", hull);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(hull, out sel);

            Assert.True(r.Accepted);
            Assert.Equal(1.0, r.Best.Score);
            Assert.Equal(1.0, r.RunnerUp.Score);
            Assert.Equal("Zeta", r.Best.Entry.Id);
            Assert.Equal(0.0, r.Margin);
        }

        [Fact]
        public void With_no_controller_left_the_rarest_shared_definition_keys_the_candidates()
        {
            List<RestorationBlock> target = Hull(1, 80);
            Catalogue cat = ShipCatalogue(target);
            var t = new GridTransform(GridRotation.All[5], new GridCell(1, 1, 1));
            List<RestorationBlock> live = Moved(Damaged(target, 0.3, 2).Where(b => !b.IsAnchor).ToList(), t);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(live, out sel);

            Assert.Equal("shared", sel.Via);
            Assert.True(r.Accepted);
            Assert.Equal("Laevateinn", r.Best.Entry.Id);
            Assert.Equal(t, r.Best.Fit.Transform);
        }

        [Fact]
        public void Too_few_surviving_blocks_refuse_a_match()
        {
            List<RestorationBlock> target = Hull(1, 80);
            Catalogue cat = ShipCatalogue(target);

            RestorationCandidateSelection sel;
            RestorationMatchResult r = cat.Match(target.Take(RestorationMatch.MinMatchedBlocks - 1).ToList(), out sel);

            Assert.NotNull(sel.Refused);
            Assert.Empty(sel.Selected);
            Assert.False(r.Accepted);
            Assert.Null(r.Best);
        }

        [Fact]
        public void A_transform_search_with_more_anchor_pairs_than_the_cap_says_so()
        {
            var reference = new List<RestorationBlock>();
            for (int i = 0; i < RestorationDiff.MaxAnchorPairs + 6; i++)
                reference.Add(Block(Cockpit, new GridCell(i, 0, 0), GridDir.Forward, GridDir.Up, anchor: true));
            var live = new List<RestorationBlock> { Block(Cockpit, new GridCell(0, 5, 0), GridDir.Left, GridDir.Up, anchor: true) };

            Assert.True(RestorationDiff.FindTransform(reference, live).PairCapHit);
            Assert.False(RestorationDiff.FindTransform(reference.Take(3).ToList(), live).PairCapHit);
        }
    }
}
