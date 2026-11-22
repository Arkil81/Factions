using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PrefabCeilingLearnedTests
    {
        private static PrefabCeiling Ceiling(params PrefabCostLearner.Estimate[] learned)
        {
            var map = new Dictionary<string, PrefabCostLearner.Estimate>();
            foreach (PrefabCostLearner.Estimate e in learned) map[e.PrefabId] = e;
            return new PrefabCeiling
            {
                CeilingMs = 2.0,
                LearnedMinWindows = 20,
                Learned = id => { PrefabCostLearner.Estimate e; return map.TryGetValue(id, out e) ? e : null; },
            };
        }

        [Fact]
        public void AnEstablishedLearnedCostPutsAFormulaPassOverTheCeiling()
        {
            PrefabCeiling c = Ceiling(new PrefabCostLearner.Estimate { PrefabId = "Alsvidr", PhysicsMs = 1.9, LogicMs = 0.5, Windows = 63 });
            Assert.False(c.IsOver(1477), "the formula alone passes it");
            Assert.True(c.IsOver("Alsvidr", 1477));
            Assert.Equal(2.4, c.PredictedAwakeMs("Alsvidr", 1477), 9);
        }

        [Fact]
        public void AnEstablishedLearnedCostCanAlsoBringAHullBackUnder()
        {
            PrefabCeiling c = Ceiling(new PrefabCostLearner.Estimate { PrefabId = "Angurvadal", PhysicsMs = 1.6, LogicMs = 0.2, Windows = 40 });
            Assert.True(c.IsOver(4566));
            Assert.False(c.IsOver("Angurvadal", 4566));
        }

        [Fact]
        public void TooFewWindowsAndUnknownPrefabsFallBackToTheFormula()
        {
            PrefabCeiling c = Ceiling(new PrefabCostLearner.Estimate { PrefabId = "Alsvidr", PhysicsMs = 3.0, Windows = 19.9 });
            Assert.Null(c.LearnedFor("Alsvidr"));
            Assert.Equal(c.PredictedAwakeMs(1477), c.PredictedAwakeMs("Alsvidr", 1477), 12);
            Assert.Equal(c.PredictedAwakeMs(900), c.PredictedAwakeMs("Unseen", 900), 12);
        }

        [Fact]
        public void WithNoLearnedTableItIsTheFormula()
        {
            var c = new PrefabCeiling { CeilingMs = 2.0 };
            Assert.Equal(c.IsOver(4566), c.IsOver("Angurvadal", 4566));
            Assert.Null(c.LearnedFor("Angurvadal"));
        }
    }
}
