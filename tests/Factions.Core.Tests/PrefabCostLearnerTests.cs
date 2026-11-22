using System;
using System.Collections.Generic;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    public class PrefabCostLearnerTests
    {
        private static double Prior(string id)
        {
            switch (id)
            {
                case "Alsvidr": return 0.66;
                case "Transport": return 1.95;
                default: return 0.5;
            }
        }

        private static PrefabCostLearner.Hull H(string id, bool awake = true, double logic = 0.5)
        {
            return new PrefabCostLearner.Hull { PrefabId = id, Awake = awake, LogicMs = logic };
        }

        /// <summary>A world whose base physics is 0.4 ms plus other grids the formula prices right,
        /// where Alsvidr really costs 2.2 ms and a transport really costs what the formula says.
        /// Presence varies across windows the way fleets come and go.</summary>
        private static PrefabCostLearner Run(int windows, double alsvidrMs = 2.2, int seed = 7)
        {
            var rng = new Random(seed);
            var l = new PrefabCostLearner();
            for (int w = 0; w < windows; w++)
            {
                int a = rng.Next(0, 3), t = rng.Next(0, 2);
                double other = 0.5 + rng.NextDouble();
                var hulls = new List<PrefabCostLearner.Hull>();
                for (int i = 0; i < a; i++) hulls.Add(H("Alsvidr"));
                for (int i = 0; i < t; i++) hulls.Add(H("Transport", true, 0.2));
                double physics = 0.4 + other + a * alsvidrMs + t * 1.95 + (rng.NextDouble() - 0.5) * 0.4;
                l.Observe(physics, other, hulls, Prior);
            }
            return l;
        }

        [Fact]
        public void APrefabTheFormulaUnderPricesIsLearnedAtWhatItCosts()
        {
            PrefabCostLearner.Estimate e = Run(400).Get("Alsvidr");
            Assert.InRange(e.PhysicsMs, 2.0, 2.4);
            Assert.Equal(0.66, e.PriorMs, 6);
        }

        [Fact]
        public void APrefabTheFormulaPricesRightStaysWhereItWas()
        {
            Assert.InRange(Run(400).Get("Transport").PhysicsMs, 1.75, 2.15);
        }

        [Fact]
        public void TwoWindowsLeaveItNearTheFormula()
        {
            var l = new PrefabCostLearner();
            l.Observe(0.9, 0.5, new List<PrefabCostLearner.Hull>(), Prior);
            l.Observe(3.1, 0.5, new List<PrefabCostLearner.Hull> { H("Alsvidr") }, Prior);
            PrefabCostLearner.Estimate e = l.Get("Alsvidr");
            Assert.True(e.PhysicsMs < 1.0, "one window of evidence against a prior of ten: " + e.PhysicsMs);
            Assert.True(e.PhysicsMs > 0.66, "but it moves toward the evidence: " + e.PhysicsMs);
            Assert.Equal(1.0, e.Windows, 6);
        }

        [Fact]
        public void LogicIsTheMeasuredMeanAndAnAsleepHullCountsForLogicOnly()
        {
            var l = new PrefabCostLearner();
            l.Observe(1.0, 0.5, new List<PrefabCostLearner.Hull> { H("Alsvidr", false, 0.4) }, Prior);
            l.Observe(1.0, 0.5, new List<PrefabCostLearner.Hull> { H("Alsvidr", false, 0.6) }, Prior);
            PrefabCostLearner.Estimate e = l.Get("Alsvidr");
            Assert.Equal(0.5, e.LogicMs, 3);
            Assert.Equal(0.66, e.PhysicsMs, 2);
        }

        [Fact]
        public void AnUnmeasuredWindowIsIgnored()
        {
            var l = new PrefabCostLearner();
            l.Observe(double.NaN, 0.5, new List<PrefabCostLearner.Hull> { H("Alsvidr") }, Prior);
            Assert.Null(l.Get("Alsvidr"));
            Assert.Equal(0, l.TotalWindows);
        }

        [Fact]
        public void ItSurvivesARestartThroughItsDocument()
        {
            PrefabCostLearner a = Run(300);
            string json = JsonConvert.SerializeObject(a.ToDoc());
            Assert.DoesNotContain("TotalMs", json);
            PrefabCostLearner b = PrefabCostLearner.FromDoc(JsonConvert.DeserializeObject<PrefabCostLearner.Doc>(json));
            Assert.Equal(a.Get("Alsvidr").PhysicsMs, b.Get("Alsvidr").PhysicsMs, 9);
            Assert.Equal(a.Get("Transport").LogicMs, b.Get("Transport").LogicMs, 9);
            Assert.Equal(a.TotalWindows, b.TotalWindows, 9);
        }

        [Fact]
        public void AMalformedDocumentRestoresNothing()
        {
            var d = Run(50).ToDoc();
            d.Aty.RemoveAt(0);
            Assert.Equal(0, PrefabCostLearner.FromDoc(d).PrefabCount);
        }

        [Fact]
        public void EvidenceDecaysAndAForgottenPrefabLeavesTheModel()
        {
            var l = new PrefabCostLearner(new PrefabCostLearner.Options { HalfLifeWindows = 2 });
            l.Observe(3.0, 0.5, new List<PrefabCostLearner.Hull> { H("Alsvidr") }, Prior);
            Assert.Equal(1, l.PrefabCount);
            for (int i = 0; i < 4; i++) l.Observe(0.9, 0.5, new List<PrefabCostLearner.Hull>(), Prior);
            Assert.Equal(0, l.PrefabCount);
        }

        [Fact]
        public void MergeWeightsEachServerByItsWindows()
        {
            var main = new[] { new PrefabCostLearner.Estimate { PrefabId = "Alsvidr", PhysicsMs = 2.0, LogicMs = 0.5, Windows = 30 } };
            var day = new[] { new PrefabCostLearner.Estimate { PrefabId = "alsvidr", PhysicsMs = 1.0, LogicMs = 0.1, Windows = 10 } };
            Dictionary<string, PrefabCostLearner.Estimate> m = PrefabCostLearner.Merge(new[] { main, day });
            Assert.Single(m);
            Assert.Equal(1.75, m["Alsvidr"].PhysicsMs, 9);
            Assert.Equal(0.4, m["Alsvidr"].LogicMs, 9);
            Assert.Equal(40, m["Alsvidr"].Windows, 9);
        }

        [Fact]
        public void FormatNamesLearnedAgainstFormulaCostliestFirst()
        {
            var l = Run(400);
            string s = PrefabCostLearner.Format(l.Estimates(), 20, 2.0, 10);
            string[] lines = s.Split('\n');
            Assert.StartsWith("serverperf prefabs: 2 learned", lines[0]);
            Assert.StartsWith("Alsvidr ", lines[1]);
            Assert.Contains("formula 0.66", lines[1]);
            Assert.Contains("; OVER)", lines[1]);
            Assert.Equal("serverperf prefabs: nothing learned yet (no materialised hull of a known prefab in a window with per-entity cost).",
                         PrefabCostLearner.Format(new List<PrefabCostLearner.Estimate>(), 20, 2.0, 10));
        }
    }
}
