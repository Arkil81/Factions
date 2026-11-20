using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="SquadronAssembler.AssembleByCost"/> (FAC-423): a patrol flies the hulls that
    /// cost least fuel for its circuit while meeting the circuit's threat, and with no threat known
    /// the posture-sized count at the lowest cost per DPS. The hulls and their per-leg prices are
    /// FAC-109's live figures, rounded.
    /// </summary>
    public class PatrolHullCostTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dps = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                { "Naginata", 90600 }, { "Alsvidr", 48400 }, { "Wakizashi", 13400 }, { "IonPicket", 20000 },
                { "Mystery", 30000 }, { "Barge", 0 },
            };
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 1e6; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                double dps;
                return _dps.TryGetValue(prefabId, out dps) && dps > 0
                    ? new[] { new WeaponBand { Dps = (float)dps, RangeM = 2000 } }
                    : new WeaponBand[0];
            }
        }

        /// <summary>Fuel credits per hull for the circuit: FAC-109's Agni-hop litres, a free ion hull,
        /// and one the model cannot price.</summary>
        private static double Fuel(string prefabId)
        {
            switch (prefabId)
            {
                case "Naginata": return 3590;
                case "Alsvidr": return 875;
                case "Wakizashi": return 70;
                case "IonPicket": return 0;
                default: return -1;
            }
        }

        private static SpatialNode Node(params (string id, int count)[] stock)
        {
            var node = new SpatialNode { Id = "N1" };
            foreach (var s in stock) node.ShipStock[s.id] = s.count;
            return node;
        }

        private static List<string> Assemble(SpatialNode node, int minHulls, int maxHulls, double need)
        {
            var cat = new Catalogue();
            return SquadronAssembler.AssembleByCost(node, cat, id => true, null, minHulls, maxHulls, need, Fuel);
        }

        [Fact]
        public void QuietCircuit_FliesThePostureCountAtTheLowestCostPerDps()
        {
            SpatialNode node = Node(("Naginata", 2), ("Alsvidr", 2), ("Wakizashi", 3));
            List<string> squadron = Assemble(node, 2, 4, 0);
            Assert.Equal(new[] { "Wakizashi", "Wakizashi" }, squadron);
        }

        [Fact]
        public void AThreat_IsMetByTheCheapestSetThatReachesIt()
        {
            // 80k: three Wakizashis (40.2k) and an Alsvidr (48.4k) reach it for 1,085; one Naginata
            // alone reaches it for 3,590.
            SpatialNode node = Node(("Naginata", 2), ("Alsvidr", 2), ("Wakizashi", 3));
            List<string> squadron = Assemble(node, 1, 6, 80000);
            squadron.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "Alsvidr", "Wakizashi", "Wakizashi", "Wakizashi" }, squadron);
        }

        [Fact]
        public void AThreat_OneHullMeetsCheaperThanAMix_TakesTheOne()
        {
            // Two hulls allowed: no pair of the cheap hulls reaches 90k, so the single Naginata goes.
            SpatialNode node = Node(("Naginata", 1), ("Alsvidr", 1), ("Wakizashi", 3));
            Assert.Equal(new[] { "Naginata" }, Assemble(node, 1, 2, 90000));
        }

        [Fact]
        public void AThreatOutOfReach_SendsTheStrongest()
        {
            SpatialNode node = Node(("Naginata", 2), ("Alsvidr", 2), ("Wakizashi", 3));
            List<string> squadron = Assemble(node, 1, 3, 1e6);
            Assert.Equal(new[] { "Naginata", "Naginata", "Alsvidr" }, squadron);
        }

        [Fact]
        public void AHullOnFreePower_WinsWhereItCanFly()
        {
            SpatialNode node = Node(("Naginata", 2), ("Wakizashi", 3), ("IonPicket", 1));
            Assert.Equal("IonPicket", Assemble(node, 1, 4, 0)[0]);
        }

        [Fact]
        public void AnUnpricedHull_RanksLastButIsStillEligible()
        {
            SpatialNode node = Node(("Mystery", 1), ("Wakizashi", 1));
            Assert.Equal(new[] { "Wakizashi", "Mystery" }, Assemble(node, 2, 2, 0));
        }

        [Fact]
        public void TheChosenHullsLeaveTheNodesStock_AndAnUnarmedHullNeverGoes()
        {
            SpatialNode node = Node(("Wakizashi", 3), ("Barge", 5));
            List<string> squadron = Assemble(node, 2, 2, 0);
            Assert.Equal(2, squadron.Count);
            Assert.Equal(1, node.ShipStock["Wakizashi"]);
            Assert.Equal(5, node.ShipStock["Barge"]);
        }
    }
}
