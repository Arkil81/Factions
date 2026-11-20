using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// One telemetry window of programmable-block script cost, per block and summarised: the
    /// measurement the load governor's own programmable-block limiter is to be sized from
    /// (docs/load-governor.md, toolbox step 3, second line).
    ///
    /// <para><b>Per frame, like every other cost figure.</b> A block's window total divided by the
    /// frames in the window, so a script on <c>Update100</c> and one on <c>Update1</c> are compared
    /// by what they take from the frame, not by what one run costs.</para>
    ///
    /// <para><b>Scheduled against external.</b> A run the script scheduled itself
    /// (<c>Runtime.UpdateFrequency</c>) can be slowed by skipping ticks; a run something else started
    /// — the terminal, a timer or sensor action, another script, a mod — carries a command and
    /// cannot be dropped. The split says how much of the cost a slowdown can reach.</para>
    /// </summary>
    public static class PbCostSummary
    {
        /// <summary>Party of a block whose grid the last census did not see: spawned since, or
        /// closed.</summary>
        public const string UnknownParty = "unknown";

        /// <summary>One block's window totals, as the host collected them.</summary>
        public sealed class Block
        {
            public long BlockId;
            public long GridId;
            public double ScheduledMs;
            public double ExternalMs;
            public long ScheduledRuns;
            public long ExternalRuns;
            /// <summary>The longest single run in the window. A slowdown bounds a script's share
            /// of the frame, never one run, so this is the figure it cannot fix.</summary>
            public double MaxRunMs;

            public double TotalMs { get { return ScheduledMs + ExternalMs; } }
        }

        public sealed class PartyRow
        {
            public string Party;
            public int Blocks;
            public int Over;
            public double MsPerFrame;
        }

        public sealed class BlockRow
        {
            public Block Block;
            public string Party;
            public double MsPerFrame;
            public double ScheduledMsPerFrame;
            public double ExternalMsPerFrame;
        }

        public sealed class Result
        {
            public int Blocks;
            public long Runs;
            public long ExternalRuns;
            public double MsPerFrame;
            public double ScheduledMsPerFrame;
            public double ExternalMsPerFrame;
            /// <summary>Per-block ms/frame, nearest rank.</summary>
            public double P50MsPerFrame;
            public double P95MsPerFrame;
            public double MaxMsPerFrame;
            public double MaxRunMs;
            /// <summary>Blocks whose ms/frame exceeded the threshold.</summary>
            public int Over;
            public double OverThresholdMsPerFrame;
            public List<PartyRow> Parties = new List<PartyRow>();
            public List<BlockRow> Top = new List<BlockRow>();
        }

        /// <param name="partyOf">The party a grid is charged to, or null when unknown.</param>
        public static Result Summarise(IEnumerable<Block> blocks, long frames, double overMsPerFrame,
                                       Func<long, string> partyOf, int topN)
        {
            var r = new Result { OverThresholdMsPerFrame = overMsPerFrame };
            if (blocks == null || frames <= 0) return r;

            var rows = new List<BlockRow>();
            var parties = new Dictionary<string, PartyRow>(StringComparer.Ordinal);
            foreach (Block b in blocks)
            {
                if (b == null || b.ScheduledRuns + b.ExternalRuns == 0) continue;
                string party = partyOf != null ? partyOf(b.GridId) : null;
                if (string.IsNullOrEmpty(party)) party = UnknownParty;
                var row = new BlockRow
                {
                    Block = b,
                    Party = party,
                    MsPerFrame = b.TotalMs / frames,
                    ScheduledMsPerFrame = b.ScheduledMs / frames,
                    ExternalMsPerFrame = b.ExternalMs / frames,
                };
                rows.Add(row);

                r.Runs += b.ScheduledRuns + b.ExternalRuns;
                r.ExternalRuns += b.ExternalRuns;
                r.MsPerFrame += row.MsPerFrame;
                r.ScheduledMsPerFrame += row.ScheduledMsPerFrame;
                r.ExternalMsPerFrame += row.ExternalMsPerFrame;
                if (b.MaxRunMs > r.MaxRunMs) r.MaxRunMs = b.MaxRunMs;
                bool over = row.MsPerFrame > overMsPerFrame;
                if (over) r.Over++;

                PartyRow p;
                if (!parties.TryGetValue(party, out p))
                {
                    p = new PartyRow { Party = party };
                    parties[party] = p;
                }
                p.Blocks++;
                p.MsPerFrame += row.MsPerFrame;
                if (over) p.Over++;
            }

            r.Blocks = rows.Count;
            if (rows.Count == 0) return r;

            rows.Sort((a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));
            r.MaxMsPerFrame = rows[0].MsPerFrame;
            r.P50MsPerFrame = NearestRankFromTop(rows, 0.50);
            r.P95MsPerFrame = NearestRankFromTop(rows, 0.95);

            int n = Math.Min(Math.Max(topN, 0), rows.Count);
            for (int i = 0; i < n; i++) r.Top.Add(rows[i]);

            r.Parties.AddRange(parties.Values);
            r.Parties.Sort((a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));
            return r;
        }

        /// <summary>Nearest-rank percentile of a list sorted descending.</summary>
        private static double NearestRankFromTop(List<BlockRow> descending, double p)
        {
            int count = descending.Count;
            int rank = (int)Math.Ceiling(p * count);   // 1-based rank in ascending order
            if (rank < 1) rank = 1;
            if (rank > count) rank = count;
            return descending[count - rank].MsPerFrame;
        }
    }
}
