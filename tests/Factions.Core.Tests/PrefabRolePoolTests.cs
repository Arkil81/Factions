using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-237. The rule that decides what a faction may choose from for a role.</summary>
    public class PrefabRolePoolTests
    {
        [Fact]
        public void AFactionWithItsOwnHullsCanStillReachTheUniversalPool()
        {
            // THE DEFECT. This was `if (specific.Count > 0) return specific;` -- so IMPR (Miner
            // hint: 5 Trade hulls, none of them plain) could never see any of the 75 plain hulls in
            // the universal pool, and built a drill hull for every cargo job. Measured live
            // 2026-09-03: IMPR 0 plain of 10 builds, SPRT 0 of 8, against IGSL's 44 of 49 -- and
            // IGSL differs only in having plain hulls inside its own hint.
            IReadOnlyList<string> pool = PrefabRolePool.Combine(
                new[] { "Miner_Drill_A", "Miner_Drill_B" },
                new[] { "Generic_Plain_Hauler" });

            Assert.Contains("Generic_Plain_Hauler", pool);
            Assert.Equal(3, pool.Count);
        }

        [Fact]
        public void SpecificHullsComeFirst_BecauseOrderIsThePreference()
        {
            // Load-bearing, not cosmetic: SelectPrefabBom breaks a cost tie on pool position, so
            // this ordering is the whole mechanism by which a faction keeps flying its own hulls.
            // If Combine ever sorted, that preference would silently disappear with no test failing
            // anywhere near it -- hence asserting the boundary and not just the membership.
            IReadOnlyList<string> pool = PrefabRolePool.Combine(
                new[] { "Own_B", "Own_A" },
                new[] { "Generic_A", "Generic_B" });

            Assert.Equal(new[] { "Own_B", "Own_A", "Generic_A", "Generic_B" }, pool.ToArray());
        }

        [Fact]
        public void AFactionWithNoHullsOfItsOwnGetsTheUniversalPoolUnchanged()
        {
            // The pre-existing behaviour for an unhinted faction, asserted so the union cannot be
            // read as having changed it.
            IReadOnlyList<string> pool = PrefabRolePool.Combine(
                new string[0], new[] { "Generic_A", "Generic_B" });

            Assert.Equal(new[] { "Generic_A", "Generic_B" }, pool.ToArray());
        }

        [Fact]
        public void ADuplicateAcrossBothGroupsAppearsOnce_AtItsSpecificPosition()
        {
            IReadOnlyList<string> pool = PrefabRolePool.Combine(
                new[] { "Shared", "Own" }, new[] { "Shared", "Generic" });

            Assert.Equal(new[] { "Shared", "Own", "Generic" }, pool.ToArray());
        }

        // ---- the ranking rule: the only place the specific-first preference is observable -------

        [Fact]
        public void AtEqualCost_TheEarlierPoolPositionWins_WhichIsTheFactionsOwnHull()
        {
            // THE PREFERENCE. The pool is faction-specific-first, so "earlier position" means
            // "the faction's own hull". This assertion is the load-bearing one: an integration
            // test that drives List<T>.Sort CANNOT catch the removal of the position term --
            // mutation-checked, the suite stayed green across three runs at both 2 and 20 tied
            // candidates, because introsort happens to leave element 0 in place regardless.
            Assert.True(PrefabRolePool.CompareByCostThenPoolPosition(40, 0, 40, 1) < 0);
            Assert.True(PrefabRolePool.CompareByCostThenPoolPosition(40, 5, 40, 2) > 0);
        }

        [Fact]
        public void ACheaperCandidateWinsFromAnyPosition_OrTheUnionWouldChangeNothing()
        {
            // The paired direction. Without it the test above is satisfied by a rule that always
            // prefers the faction's own hull -- which is the override FAC-237 removed, sneaking
            // back in as a tie-break. Cost must still dominate, or the union is decorative.
            Assert.True(PrefabRolePool.CompareByCostThenPoolPosition(39, 19, 40, 0) < 0);
            Assert.True(PrefabRolePool.CompareByCostThenPoolPosition(41, 0, 40, 19) > 0);
        }

        [Fact]
        public void TheComparisonIsATotalOrder_SoAnEqualPairIsReportedEqual()
        {
            // Same cost AND same position is the only equal case; anything else must order
            // strictly, or Sort's behaviour on the pool is undefined.
            Assert.Equal(0, PrefabRolePool.CompareByCostThenPoolPosition(40, 3, 40, 3));
        }

        [Fact]
        public void NullsAndEmptiesAreTolerated_AndTheResultIsNeverNull()
        {
            Assert.Empty(PrefabRolePool.Combine(null, null));
            Assert.Equal(new[] { "A" }, PrefabRolePool.Combine(null, new[] { "A" }).ToArray());
            Assert.Equal(new[] { "A" }, PrefabRolePool.Combine(new[] { "A" }, null).ToArray());
            Assert.Equal(new[] { "A" }, PrefabRolePool.Combine(new[] { "A", null, "" }, null).ToArray());
        }
    }
}
