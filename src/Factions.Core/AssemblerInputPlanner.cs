using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One queued blueprint, as the input planner sees it.</summary>
    public sealed class AssemblerQueueEntry
    {
        /// <summary>The blueprint's id, carried through for reporting only.</summary>
        public string Recipe;
        /// <summary>Units still queued.</summary>
        public double Amount;
        /// <summary>Prerequisite item key → amount for one unit, with the assembler's efficiency
        /// multiplier already applied.</summary>
        public IList<KeyValuePair<string, double>> PerUnit = new List<KeyValuePair<string, double>>();
    }

    public enum AssemblerInputMode
    {
        /// <summary>Nothing queued: the tray is emptied.</summary>
        Idle,
        /// <summary>Something queued, but no queued item has a whole unit's materials in reach: the
        /// tray is emptied.</summary>
        Blocked,
        /// <summary>A queued item can be built: the tray holds whole units of it and nothing else.</summary>
        Stocking,
    }

    /// <summary>What an assembler's input tray should do this tick. Amounts are item units, keyed as the
    /// caller keyed its inputs.</summary>
    public sealed class AssemblerInputPlan
    {
        public AssemblerInputMode Mode;
        /// <summary>The queue index being stocked; -1 unless <see cref="Mode"/> is Stocking.</summary>
        public int QueueIndex = -1;
        /// <summary>Whole units of the stocked item the tray is filled for.</summary>
        public int Units;
        /// <summary>Items to move out of the tray to storage.</summary>
        public readonly Dictionary<string, double> PushOut = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>Items to bring into the tray.</summary>
        public readonly Dictionary<string, double> PullIn = new Dictionary<string, double>(StringComparer.Ordinal);

        public bool IsNoOp { get { return PushOut.Count == 0 && PullIn.Count == 0; } }
    }

    /// <summary>
    /// Replaces vanilla's input-tray behaviour for an assembler in assemble mode.
    ///
    /// <para><b>Why.</b> Vanilla's <c>MyAssembler.GetComponentsFromConveyor</c> pulls each queued
    /// item's prerequisites separately, so for an item it can never finish it still pulls the
    /// prerequisites that are available. It stops pulling at 99 % fill and never pushes input back in
    /// assemble mode. A bulk queue therefore jams the block for good with a tray of partial sets. Seen
    /// on Main 2026-10-28: the tray held 4.0000 of 4 m³ of cobalt, gravel and silver, under a queue of
    /// tool upgrades (FAC-504).</para>
    ///
    /// <para><b>Rules</b> (operator, 2026-10-28):</para>
    /// <list type="bullet">
    /// <item><b>No queue:</b> empty the tray.</item>
    /// <item><b>A queue that can't be built:</b> empty the tray.</item>
    /// <item><b>A buildable queue:</b> fill the tray with whole units of the item being built, and
    /// clear away anything that doesn't belong to it.</item>
    /// </list>
    ///
    /// <para>"The item being built" is the first queued item with at least one whole unit's materials
    /// in reach, not necessarily queue index 0. Vanilla's <c>UpdateProduction</c> already walks past
    /// items it lacks materials for, so stocking only index 0 would idle the block behind an item that
    /// waits on a later one. That is the tool-tier case: tier 2 needs the tier-1 tool, queued
    /// last.</para>
    ///
    /// <para>"In reach" is the tray plus everything the caller counts as pullable (conveyor-reachable
    /// storage and the block's own output tray). Over-stock of an item that belongs is left alone
    /// unless the missing materials need its space.</para>
    /// </summary>
    public static class AssemblerInputPlanner
    {
        /// <summary>The share of the tray volume the planner fills. Below vanilla's 0.99 pull cutoff,
        /// so a block handed back to vanilla is never left at it.</summary>
        public const double FillFraction = 0.95;

        private const double Eps = 1e-6;

        /// <param name="queue">The production queue, in order.</param>
        /// <param name="input">What the tray holds now.</param>
        /// <param name="reachable">What can be pulled in from outside the tray.</param>
        /// <param name="unitVolume">Volume of one unit of an item key, m³.</param>
        /// <param name="maxVolume">The tray's capacity, m³.</param>
        public static AssemblerInputPlan Plan(IList<AssemblerQueueEntry> queue,
            IDictionary<string, double> input, IDictionary<string, double> reachable,
            Func<string, double> unitVolume, double maxVolume)
        {
            var plan = new AssemblerInputPlan();
            if (queue == null || queue.Count == 0)
            {
                plan.Mode = AssemblerInputMode.Idle;
                PushAll(plan, input);
                return plan;
            }

            for (int i = 0; i < queue.Count; i++)
            {
                int units = BuildableUnits(queue[i], input, reachable, unitVolume, maxVolume);
                if (units < 1) continue;
                plan.Mode = AssemblerInputMode.Stocking;
                plan.QueueIndex = i;
                plan.Units = units;
                Stock(plan, queue[i], units, input, unitVolume, maxVolume);
                return plan;
            }

            plan.Mode = AssemblerInputMode.Blocked;
            PushAll(plan, input);
            return plan;
        }

        /// <summary>Whole units of <paramref name="e"/> worth putting in the tray: bounded by the
        /// materials in reach, the units still queued and what fits in
        /// <see cref="FillFraction"/> of the tray. 0 when not one unit can be had, or when one unit
        /// does not fit in the tray at all.</summary>
        public static int BuildableUnits(AssemblerQueueEntry e, IDictionary<string, double> input,
            IDictionary<string, double> reachable, Func<string, double> unitVolume, double maxVolume)
        {
            if (e == null || e.Amount <= Eps) return 0;
            double queued = Math.Ceiling(e.Amount - Eps);
            double avail = queued;
            double unitVol = 0;
            foreach (KeyValuePair<string, double> p in e.PerUnit)
            {
                if (p.Value <= Eps) continue;
                double have = Get(input, p.Key) + Get(reachable, p.Key);
                avail = Math.Min(avail, Math.Floor((have + Eps) / p.Value));
                unitVol += p.Value * Math.Max(0, unitVolume(p.Key));
            }
            if (avail < 1) return 0;
            if (unitVol > maxVolume + Eps) return 0;
            double cap = unitVol <= Eps ? queued : Math.Max(1, Math.Floor(maxVolume * FillFraction / unitVol + Eps));
            double units = Math.Min(avail, Math.Min(queued, cap));
            return units >= int.MaxValue ? int.MaxValue : (int)units;
        }

        private static void Stock(AssemblerInputPlan plan, AssemblerQueueEntry e, int units,
            IDictionary<string, double> input, Func<string, double> unitVolume, double maxVolume)
        {
            var target = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> p in e.PerUnit)
                if (p.Value > Eps) target[p.Key] = Get(target, p.Key) + p.Value * units;

            double keptVol = 0;
            if (input != null)
                foreach (KeyValuePair<string, double> held in input)
                {
                    if (held.Value <= Eps) continue;
                    if (!target.ContainsKey(held.Key)) plan.PushOut[held.Key] = held.Value;
                    else keptVol += held.Value * Math.Max(0, unitVolume(held.Key));
                }

            double needVol = 0;
            foreach (KeyValuePair<string, double> t in target)
            {
                double deficit = t.Value - Get(input, t.Key);
                if (deficit <= Eps) continue;
                plan.PullIn[t.Key] = deficit;
                needVol += deficit * Math.Max(0, unitVolume(t.Key));
            }

            // Over-stock of a belonging item is harmless until the deficits need its room; then only
            // the excess above the target goes.
            if (needVol > maxVolume - keptVol + Eps)
                foreach (KeyValuePair<string, double> t in target)
                {
                    double excess = Get(input, t.Key) - t.Value;
                    if (excess > Eps) plan.PushOut[t.Key] = excess;
                }
        }

        private static void PushAll(AssemblerInputPlan plan, IDictionary<string, double> input)
        {
            if (input == null) return;
            foreach (KeyValuePair<string, double> held in input)
                if (held.Value > Eps) plan.PushOut[held.Key] = held.Value;
        }

        private static double Get(IDictionary<string, double> d, string key)
        {
            double v;
            return d != null && d.TryGetValue(key, out v) ? v : 0;
        }
    }
}
