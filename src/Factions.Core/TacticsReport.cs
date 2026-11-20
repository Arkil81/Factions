using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One arm × break-off cell as the Tactics window shows it: the posterior mean with the
    /// personality prior, and the evidence behind it, both decayed to the report's clock.</summary>
    public sealed class TacticsCellReport
    {
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        public double Mean;
        /// <summary>Evidence in the cell, prior excluded — zero when the mean is the prior alone.</summary>
        public double Evidence;
    }

    /// <summary>One context bucket against one opponent: the cells with evidence, best mean first.</summary>
    public sealed class TacticsContextReport
    {
        /// <summary><see cref="TacticContext.Key"/>.</summary>
        public string Context;
        public double Evidence;
        public readonly List<TacticsCellReport> Cells = new List<TacticsCellReport>();
    }

    /// <summary>What a faction has learned against one opponent.</summary>
    public sealed class TacticsOpponentReport
    {
        public string OpponentTag;
        public DateTime LastUtc;
        /// <summary>The cell with the best posterior mean over every context pooled, prior included —
        /// the personality default when nothing learned beats it (then its evidence may be zero).</summary>
        public TacticsCellReport Preferred;
        /// <summary>Evidence across every cell and context.</summary>
        public double Evidence;
        /// <summary>Most evidence first, at most <see cref="TacticsReport.MaxContextsPerOpponent"/>.</summary>
        public readonly List<TacticsContextReport> Contexts = new List<TacticsContextReport>();
    }

    /// <summary>
    /// The Tactics window's payload: one faction's <see cref="TacticMemory"/> as a player reads it —
    /// per opponent, most recently fought first, the tactic the faction leans to and how sure it is,
    /// the context buckets behind that, and the last few reviews.
    ///
    /// <para>Read-only, and built from the memory alone, so it is the same numbers
    /// <c>factions bench tactics</c> prints (<see cref="TacticMemory.Describe"/>): a cell's mean is
    /// its own posterior, not the sibling-pooled draw a pick samples from.</para>
    ///
    /// <para>In Core so it is unit-testable; the host maps it onto wire structs and adds nothing.</para>
    /// </summary>
    public sealed class TacticsReport
    {
        public const int MaxContextsPerOpponent = 9;
        public const int MaxCellsPerContext = 4;
        /// <summary>A cell with less decayed evidence than this is not listed.</summary>
        public const double MinEvidence = 0.05;

        public string FactionTag;
        public string PersonalityId;
        public TacticArm PriorArm;
        public BreakOffLevel PriorBreakOff;
        /// <summary>Outcomes recorded, ever.</summary>
        public long Updates;
        /// <summary>Whether real duels pick from and write to the memory
        /// (<see cref="WorldModel.TacticLearning"/>); bench scenarios always do.</summary>
        public bool Learning;

        public readonly List<TacticsOpponentReport> Opponents = new List<TacticsOpponentReport>();
        /// <summary>Newest first.</summary>
        public readonly List<TacticReview> Reviews = new List<TacticReview>();

        /// <summary>Assembles <paramref name="st"/>'s own report as of <paramref name="nowUtc"/>. Only
        /// <paramref name="st"/>'s memory is read; a null state gives an empty report.</summary>
        public static TacticsReport Build(FactionState st, bool learning, DateTime nowUtc)
        {
            var report = new TacticsReport { Learning = learning };
            if (st == null) return report;

            TacticMemory m = st.TacticMemory;
            DuelStance def = TacticChoice.DefaultFor(st.PersonalityId);
            report.FactionTag = st.Tag;
            report.PersonalityId = st.PersonalityId;
            report.PriorArm = def.Arm;
            report.PriorBreakOff = def.BreakOff;
            report.Updates = m.Updates;

            foreach (KeyValuePair<string, TacticMemory.Opponent> opp in m.Opponents())
                report.Opponents.Add(OpponentOf(opp.Key, opp.Value, def, nowUtc));
            report.Opponents.Sort((x, y) => y.LastUtc != x.LastUtc
                ? y.LastUtc.CompareTo(x.LastUtc)
                : string.CompareOrdinal(x.OpponentTag, y.OpponentTag));

            for (int i = m.Reviews.Count - 1; i >= 0; i--) report.Reviews.Add(m.Reviews[i]);
            return report;
        }

        private static TacticsOpponentReport OpponentOf(string tag, TacticMemory.Opponent opp, DuelStance def, DateTime nowUtc)
        {
            var row = new TacticsOpponentReport { OpponentTag = tag, LastUtc = opp.LastUtc };
            var pooledA = new double[TacticMemory.Actions];
            var pooledB = new double[TacticMemory.Actions];

            foreach (KeyValuePair<string, TacticMemory.Bucket> kv in opp.Buckets)
            {
                TacticMemory.Bucket b = kv.Value;
                double f = TacticMemory.DecayFactor(b.Utc, nowUtc);
                var ctx = new TacticsContextReport { Context = kv.Key };
                for (int i = 0; i < TacticMemory.Actions; i++)
                {
                    double a = b.A[i] * f, bb = b.B[i] * f;
                    pooledA[i] += a;
                    pooledB[i] += bb;
                    ctx.Evidence += a + bb;
                    if (a + bb < MinEvidence) continue;
                    ctx.Cells.Add(Cell(i, a, bb, def));
                }
                if (ctx.Cells.Count == 0) continue;
                ctx.Cells.Sort(ByMean);
                if (ctx.Cells.Count > MaxCellsPerContext) ctx.Cells.RemoveRange(MaxCellsPerContext, ctx.Cells.Count - MaxCellsPerContext);
                row.Contexts.Add(ctx);
            }
            row.Contexts.Sort((x, y) => y.Evidence != x.Evidence
                ? y.Evidence.CompareTo(x.Evidence)
                : string.CompareOrdinal(x.Context, y.Context));
            if (row.Contexts.Count > MaxContextsPerOpponent)
                row.Contexts.RemoveRange(MaxContextsPerOpponent, row.Contexts.Count - MaxContextsPerOpponent);

            for (int i = 0; i < TacticMemory.Actions; i++)
            {
                row.Evidence += pooledA[i] + pooledB[i];
                TacticsCellReport c = Cell(i, pooledA[i], pooledB[i], def);
                if (row.Preferred == null || ByMean(c, row.Preferred) < 0) row.Preferred = c;
            }
            return row;
        }

        private static TacticsCellReport Cell(int index, double a, double b, DuelStance def)
        {
            double a0, b0;
            TacticMemory.Prior(index, def, out a0, out b0);
            return new TacticsCellReport
            {
                Arm = TacticMemory.ArmOf(index),
                BreakOff = TacticMemory.BreakOffOf(index),
                Mean = (a0 + a) / (a0 + b0 + a + b),
                Evidence = a + b,
            };
        }

        /// <summary>Best mean first; a tie goes to the lower cell index, so two reports of one
        /// memory read identically.</summary>
        private static int ByMean(TacticsCellReport x, TacticsCellReport y)
        {
            if (x.Mean != y.Mean) return y.Mean.CompareTo(x.Mean);
            return TacticMemory.Index(x.Arm, x.BreakOff).CompareTo(TacticMemory.Index(y.Arm, y.BreakOff));
        }
    }
}
