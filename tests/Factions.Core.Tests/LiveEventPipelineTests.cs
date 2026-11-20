using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Factions;
using Factions.Api.Services;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The two ends of the live-event pipeline: the row caps that bound one demands
    /// snapshot, and the tail reader that follows the file the writer rotates under it.</summary>
    public class DemandsSnapshotPayloadTests
    {
        private static readonly DateTime At = new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);

        private static List<DemandEntry> Demands(string kindPrefix, int count)
        {
            var list = new List<DemandEntry>(count);
            for (int i = 0; i < count; i++)
                list.Add(new DemandEntry
                {
                    FactionTag = "EXP",
                    Kind = kindPrefix + i,
                    NodeId = "N" + i,
                    ExpiresUtc = At.AddMinutes(5),
                    Quantity = i,
                });
            return list;
        }

        private static List<object> Rows(Dictionary<string, object> payload)
            => (List<object>)payload["demands"];

        /// <summary>Demands as an unranked snapshot carries them — no world was consulted, so no
        /// row claims a measurement.</summary>
        private static List<DemandSnapshotRow> Unscored(List<DemandEntry> demands)
        {
            var rows = new List<DemandSnapshotRow>(demands.Count);
            foreach (DemandEntry d in demands) rows.Add(new DemandSnapshotRow { Demand = d });
            return rows;
        }

        [Fact]
        public void UncappedSnapshotCarriesEveryRowAndNoTotals()
        {
            var demands = Demands("Item:", 10);
            demands.AddRange(Demands("Ship:", 3));

            Dictionary<string, object> payload =
                DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>());

            Assert.Equal(13, Rows(payload).Count);
            // Omitted entirely when nothing was capped -- the array is then the whole truth.
            Assert.False(payload.ContainsKey("demandTotals"));
        }

        [Fact]
        public void OverflowingKindIsCappedAndReportsItsRealTotal()
        {
            int over = DemandsSnapshotPayload.MaxRowsPerKind * 3;
            Dictionary<string, object> payload =
                DemandsSnapshotPayload.Build("EXP", At, Unscored(Demands("Item:", over)), new List<InvestmentSignal>());

            Assert.Equal(DemandsSnapshotPayload.MaxRowsPerKind, Rows(payload).Count);

            var totals = Assert.IsType<Dictionary<string, object>>(payload["demandTotals"]);
            Assert.Equal(over, totals["item"]);
            Assert.Equal(0, totals["ship"]);
            Assert.Equal(0, totals["position"]);
        }

        [Fact]
        public void EachKindIsCappedSeparatelySoAFloodCannotCrowdOutTheOthers()
        {
            var demands = Demands("Item:", DemandsSnapshotPayload.MaxRowsPerKind * 5);
            demands.AddRange(Demands("Ship:", 3));
            demands.AddRange(Demands("Position:", 2));

            List<object> rows = Rows(DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>()));

            int ship = 0, position = 0, item = 0;
            foreach (object row in rows)
            {
                var kind = (string)((Dictionary<string, object>)row)["kind"];
                if (kind.StartsWith("Ship:", StringComparison.Ordinal)) ship++;
                else if (kind.StartsWith("Position:", StringComparison.Ordinal)) position++;
                else if (kind.StartsWith("Item:", StringComparison.Ordinal)) item++;
            }

            Assert.Equal(3, ship);
            Assert.Equal(2, position);
            Assert.Equal(DemandsSnapshotPayload.MaxRowsPerKind, item);
        }

        [Fact]
        public void KeptRowsAreThePrefixOfTheirKindInFlushOrder()
        {
            var demands = Demands("Item:", DemandsSnapshotPayload.MaxRowsPerKind + 5);
            List<object> rows = Rows(DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>()));

            Assert.Equal("Item:0", ((Dictionary<string, object>)rows[0])["kind"]);
            Assert.Equal("Item:" + (DemandsSnapshotPayload.MaxRowsPerKind - 1),
                         ((Dictionary<string, object>)rows[rows.Count - 1])["kind"]);
        }

        [Fact]
        public void OptionalShipOnlyKeysStayOffItemRows()
        {
            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, Unscored(Demands("Item:", 1)), new List<InvestmentSignal>()))[0];

            Assert.False(row.ContainsKey("destNodeId"));
            Assert.False(row.ContainsKey("viaNodeId"));
            Assert.False(row.ContainsKey("firstPostedUtc"));
        }

        [Fact]
        public void ShipRowsCarryTheirRoutingKeys()
        {
            var demands = new List<DemandEntry>
            {
                new DemandEntry
                {
                    FactionTag = "EXP", Kind = "Ship:Haul", NodeId = "A",
                    DestNodeId = "B", ViaNodeId = "C",
                    ExpiresUtc = At.AddMinutes(5), FirstPostedUtc = At.AddHours(-3),
                },
            };

            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>()))[0];

            Assert.Equal("B", row["destNodeId"]);
            Assert.Equal("C", row["viaNodeId"]);
            Assert.True(row.ContainsKey("firstPostedUtc"));
        }

        [Fact]
        public void EveryRowNamesWhatRaisedIt()
        {
            var demands = new List<DemandEntry>
            {
                new DemandEntry { FactionTag = "EXP", Kind = "Item:Ore:Iron",   NodeId = "N007", Quantity = 464_074,
                                  ExpiresUtc = At.AddMinutes(5), Class = DemandClass.RecipeInput },
                new DemandEntry { FactionTag = "EXP", Kind = "Item:Comp:Thrust", NodeId = "N007", Quantity = 31,
                                  ExpiresUtc = At.AddMinutes(5), Class = DemandClass.ShipBuild },
            };

            List<object> rows = Rows(DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>()));

            // The whole point: the ore the chain is rooted in and the component it feeds are the
            // same shortfall at different weights, and a reader cannot see that from the quantity.
            Assert.Equal("RecipeInput", ((Dictionary<string, object>)rows[0])["cls"]);
            Assert.Equal("ShipBuild",   ((Dictionary<string, object>)rows[1])["cls"]);
        }

        [Fact]
        public void AScoredRowCarriesItsUrgencyAndTheStockBehindIt()
        {
            var demands = new List<DemandSnapshotRow>
            {
                new DemandSnapshotRow
                {
                    Demand = new DemandEntry { FactionTag = "EXP", Kind = "Item:Ore:Iron", NodeId = "N007",
                                               Quantity = 464_074, ExpiresUtc = At.AddMinutes(5),
                                               Class = DemandClass.RecipeInput },
                    Scored = true, Urgency = 5.9996, HaveAtNode = 3_400,
                },
            };

            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, demands, new List<InvestmentSignal>()))[0];

            Assert.Equal(6.0, row["urgency"]);
            Assert.Equal(3_400L, row["have"]);
        }

        [Fact]
        public void AnUnscoredRowOmitsBothRatherThanReportingZeroStock()
        {
            // have=0 is a real reading — the node holds none of it. A row that was never measured
            // must not be indistinguishable from one that was and came back empty.
            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, Unscored(Demands("Ship:", 1)), new List<InvestmentSignal>()))[0];

            Assert.False(row.ContainsKey("urgency"));
            Assert.False(row.ContainsKey("have"));
        }

        [Fact]
        public void AStarvedRowStillReportsHaveZero()
        {
            var demands = new List<DemandSnapshotRow>
            {
                new DemandSnapshotRow
                {
                    Demand = new DemandEntry { FactionTag = "EXP", Kind = "Item:Comp:Thrust", NodeId = "N007",
                                               Quantity = 31, ExpiresUtc = At.AddMinutes(5),
                                               Class = DemandClass.ShipBuild },
                    Scored = true, Urgency = 4.0, HaveAtNode = 0,
                },
            };

            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, demands, new List<InvestmentSignal>()))[0];

            Assert.Equal(0L, row["have"]);
        }

        [Fact]
        public void RouteConstraintsStayOffTheRowsThatDoNotHaveThem()
        {
            // Live-measured 2026-08-31: set on 427, 32 and 37 of 10,812 rows respectively. Writing
            // all three unconditionally spent ~29% of the demands payload on defaults.
            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, Unscored(Demands("Item:", 1)), new List<InvestmentSignal>()))[0];

            Assert.False(row.ContainsKey("minCargoLiters"));
            Assert.False(row.ContainsKey("requiredPlanetId"));
            Assert.False(row.ContainsKey("requiredZone"));
        }

        [Fact]
        public void RouteConstraintsAreCarriedWhenTheyConstrainSomething()
        {
            var demands = new List<DemandEntry>
            {
                new DemandEntry
                {
                    FactionTag = "EXP", Kind = "Ship:Haul", NodeId = "A", ExpiresUtc = At.AddMinutes(5),
                    MinCargoLiters = 12_500.0, RequiredPlanetId = "P42", RequiredZone = NodeZone.Surface,
                },
            };

            var row = (Dictionary<string, object>)Rows(
                DemandsSnapshotPayload.Build("EXP", At, Unscored(demands), new List<InvestmentSignal>()))[0];

            Assert.Equal(12_500.0, row["minCargoLiters"]);
            Assert.Equal("P42", row["requiredPlanetId"]);
            Assert.Equal("Surface", row["requiredZone"]);
        }

        private static List<object> Investments(Dictionary<string, object> payload)
            => (List<object>)payload["investments"];

        private static InvestmentSignal Signal(NodeCapabilityKind cap, List<InstallMaterialRow> materials)
        {
            return new InvestmentSignal
            {
                Request    = new InvestmentRequest { FactionTag = "EXP", Cap = cap, Urgency = 0.8, ExpiresUtc = At.AddMinutes(5) },
                Target     = new InvestmentTarget { NodeId = "N1" },
                Affordable = true,
                Materials  = materials,
            };
        }

        [Fact]
        public void InvestmentRowCarriesItsInstallBomWithBothStockPools()
        {
            var materials = new List<InstallMaterialRow>
            {
                new InstallMaterialRow { Key = "Comp:MetalGrid", Need = 140, Reserved = 12, Routable = 0 },
                new InstallMaterialRow { Key = "Comp:SteelPlate", Need = 500, Reserved = 340, Routable = 80 },
            };

            var inv = (Dictionary<string, object>)Investments(
                DemandsSnapshotPayload.Build("EXP", At, new List<DemandSnapshotRow>(),
                    new List<InvestmentSignal> { Signal(NodeCapabilityKind.Shipyard, materials) }))[0];

            var bom = (List<object>)inv["bom"];
            Assert.Equal(2, bom.Count);
            var plate = (Dictionary<string, object>)bom[1];
            Assert.Equal("Comp:SteelPlate", plate["key"]);
            Assert.Equal(500L, plate["need"]);
            Assert.Equal(340L, plate["reserved"]);
            Assert.Equal(80L, plate["stock"]);
            // A component row carries no bucket marker -- only the flat-cost path does.
            Assert.False(plate.ContainsKey("byKind"));
        }

        [Fact]
        public void FlatCostRowIsMarkedAsAnItemKindBucket()
        {
            var materials = new List<InstallMaterialRow>
            {
                new InstallMaterialRow { Key = "Ingot", ByKind = true, Need = 60, Reserved = 3, Routable = 7 },
            };

            var inv = (Dictionary<string, object>)Investments(
                DemandsSnapshotPayload.Build("EXP", At, new List<DemandSnapshotRow>(),
                    new List<InvestmentSignal> { Signal(NodeCapabilityKind.Refinery, materials) }))[0];

            var row = (Dictionary<string, object>)((List<object>)inv["bom"])[0];
            Assert.Equal("Ingot", row["key"]);
            Assert.Equal(true, row["byKind"]);
        }

        [Fact]
        public void CapabilityWithNoItemCostOmitsTheBomKeyEntirely()
        {
            var inv = (Dictionary<string, object>)Investments(
                DemandsSnapshotPayload.Build("EXP", At, new List<DemandSnapshotRow>(),
                    new List<InvestmentSignal> { Signal(NodeCapabilityKind.Refinery, new List<InstallMaterialRow>()) }))[0];

            Assert.False(inv.ContainsKey("bom"));
        }
    }

    public class JsonlTailReaderTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public JsonlTailReaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "factions-tail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "events.jsonl");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private void Append(string text) => File.AppendAllText(_path, text, Utf8NoBom);

        private void AppendLines(params string[] lines)
        {
            foreach (string line in lines) Append(line + "\n");
        }

        private static string Line(string tag, int n)
            => "{\"type\":\"demands\",\"at\":\"2026-08-22T10:0" + n + ":00.000Z\",\"factionTag\":\"" + tag + "\"}";

        [Fact]
        public void StartingAtEndSkipsHistoryAndReturnsOnlyWhatFollows()
        {
            AppendLines(Line("AAA", 1), Line("BBB", 2));
            var reader = new JsonlTailReader(_path, startAtEnd: true);

            Assert.Empty(reader.ReadNewLines());

            AppendLines(Line("CCC", 3));
            Assert.Equal(new[] { Line("CCC", 3) }, reader.ReadNewLines());
        }

        [Fact]
        public void MissingFileReadsFromTheStartOnceItAppears()
        {
            var reader = new JsonlTailReader(_path, startAtEnd: true);
            Assert.Empty(reader.ReadNewLines());

            AppendLines(Line("AAA", 1));
            Assert.Equal(new[] { Line("AAA", 1) }, reader.ReadNewLines());
        }

        [Fact]
        public void APartialLineIsHeldBackUntilItsNewlineArrives()
        {
            var reader = new JsonlTailReader(_path, startAtEnd: true);
            AppendLines(Line("AAA", 1));
            Append(Line("BBB", 2).Substring(0, 20)); // writer caught mid-line

            // The complete line only -- a truncated one would reach the dashboard as unparseable
            // JSON, and its remainder would arrive as a second unparseable fragment.
            Assert.Equal(new[] { Line("AAA", 1) }, reader.ReadNewLines());

            Append(Line("BBB", 2).Substring(20) + "\n");
            Assert.Equal(new[] { Line("BBB", 2) }, reader.ReadNewLines());
        }

        [Fact]
        public void ASoleIncompleteLineYieldsNothingRatherThanAFragment()
        {
            var reader = new JsonlTailReader(_path, startAtEnd: true);
            Append("{\"type\":\"demands\",\"factionTag\":\"AA");

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(0, reader.Position);
        }

        [Fact]
        public void RotationToAShorterFileReplaysTheReplacementFromItsStart()
        {
            AppendLines(Line("AAA", 1), Line("BBB", 2));
            var reader = new JsonlTailReader(_path, startAtEnd: true);

            File.Move(_path, Path.Combine(_dir, "events.prev.jsonl"));
            AppendLines(Line("CCC", 3));

            Assert.Equal(new[] { Line("CCC", 3) }, reader.ReadNewLines());
        }

        [Fact]
        public void RotationIsDetectedEvenWhenTheReplacementIsAlreadyLongerThanTheOldOffset()
        {
            // Length alone cannot see this: the replacement has passed the offset the reader stopped
            // at, so seeking there would land mid-line in an unrelated file and skip everything
            // before it.
            for (int i = 0; i < 40; i++) AppendLines(Line("OLD", 1));
            var reader = new JsonlTailReader(_path, startAtEnd: true);
            long offset = reader.Position;

            File.Move(_path, Path.Combine(_dir, "events.prev.jsonl"));
            var replacement = new List<string>();
            for (int i = 0; i < 80; i++) replacement.Add(Line("NEW", 2));
            AppendLines(replacement.ToArray());
            Assert.True(new FileInfo(_path).Length > offset);

            List<string> read = reader.ReadNewLines();
            Assert.Equal(80, read.Count);
            Assert.All(read, line => Assert.Contains("NEW", line));
        }

        [Fact]
        public void APreambleLeftByAnOlderWriterIsNotHandedToTheDashboard()
        {
            File.WriteAllText(_path, Line("AAA", 1) + "\n", new UTF8Encoding(true));
            var reader = new JsonlTailReader(_path, startAtEnd: false);

            List<string> read = reader.ReadNewLines();
            Assert.Equal(new[] { Line("AAA", 1) }, read);
            // Ordinal, deliberately: U+FEFF is collation-ignorable, so the culture-sensitive
            // Assert.DoesNotContain(string, string) reports it "found" at position 0 of any string
            // at all -- green under DOTNET_SYSTEM_GLOBALIZATION_INVARIANT, red on Windows, and
            // meaningless either way.
            Assert.False(read[0].StartsWith("\uFEFF", StringComparison.Ordinal));
            Assert.Equal('{', read[0][0]);
        }

        [Fact]
        public void LinesWrittenJustBeforeTheRenameSurviveTheRotation()
        {
            // Seen live 2026-08-22: LiveEventWriter rotates from inside the append that pushes the
            // file past its trigger, so the triggering line exists for about a millisecond before
            // the rename. DVSR's snapshot was written at 12:52:23.914 and the file renamed at
            // .915 -- the reader jumped straight to the replacement and that snapshot never
            // reached the dashboard. Switching files must drain what the old one still held.
            AppendLines(Line("AAA", 1));
            var reader = new JsonlTailReader(_path, startAtEnd: true);

            AppendLines(Line("BBB", 2), Line("CCC", 3)); // written, not yet polled
            File.Move(_path, Path.Combine(_dir, "events.prev.jsonl"));
            AppendLines(Line("DDD", 4));

            Assert.Equal(new[] { Line("BBB", 2), Line("CCC", 3), Line("DDD", 4) }, reader.ReadNewLines());
        }

        [Fact]
        public void TheDrainedPredecessorIsNotReplayedOnTheNextRead()
        {
            AppendLines(Line("AAA", 1));
            var reader = new JsonlTailReader(_path, startAtEnd: true);
            AppendLines(Line("BBB", 2));
            File.Move(_path, Path.Combine(_dir, "events.prev.jsonl"));
            AppendLines(Line("CCC", 3));
            reader.ReadNewLines();

            Assert.Empty(reader.ReadNewLines());

            AppendLines(Line("DDD", 4));
            Assert.Equal(new[] { Line("DDD", 4) }, reader.ReadNewLines());
        }

        [Fact]
        public void APredecessorFromAnEarlierRotationIsNotMistakenForOurs()
        {
            // A .prev left by some earlier rotation is not the file this reader was following, and
            // replaying it would deliver stale events as if they were new.
            File.WriteAllText(Path.Combine(_dir, "events.prev.jsonl"),
                              Line("OLD", 9) + "\n" + Line("OLD", 9) + "\n", Utf8NoBom);
            AppendLines(Line("AAA", 1));
            var reader = new JsonlTailReader(_path, startAtEnd: true);

            File.Delete(_path);
            AppendLines(Line("BBB", 2));

            Assert.Equal(new[] { Line("BBB", 2) }, reader.ReadNewLines());
        }

        [Fact]
        public void RotationStillWorksWithNoPredecessorOnDisk()
        {
            AppendLines(Line("AAA", 1));
            var reader = new JsonlTailReader(_path, startAtEnd: true);

            File.Delete(_path);
            AppendLines(Line("BBB", 2));

            Assert.Equal(new[] { Line("BBB", 2) }, reader.ReadNewLines());
        }

        [Fact]
        public void PositionTracksLineBoundariesAcrossReads()
        {
            var reader = new JsonlTailReader(_path, startAtEnd: false);
            AppendLines(Line("AAA", 1), Line("BBB", 2));
            reader.ReadNewLines();

            Assert.Equal(new FileInfo(_path).Length, reader.Position);
            Assert.Empty(reader.ReadNewLines());
        }
    }
}
