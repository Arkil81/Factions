using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>A hub whose extraction order is deposit-bound: every catchment deposit of the ore is
    /// worked or cooling down (or there is none), and the order still falls short. The reason and the
    /// ore for new ground; where that ground is, and whether to go, is the expansion path's
    /// (docs/mining-plan.md, "Capacity growth"; FAC-106).
    ///
    /// <para>Derived every Operations pass from that pass's plan and published on
    /// <see cref="FactionState.ExtractionGroundSignals"/>. Never persisted: a restart re-derives it
    /// on the first pass, with a fresh <see cref="SinceUtc"/>.</para></summary>
    public sealed class ExtractionGroundSignal
    {
        public string HubNodeId;
        /// <summary>Inventory key of the ore (<c>Ore:Ice</c>).</summary>
        public string OreKey;
        /// <summary>The extraction rate the hub still lacks, litres per hour
        /// (<see cref="ExtractionCapacity.MissingPerHour"/> × litres per unit).</summary>
        public double MissingLitersPerHour;
        /// <summary>When the hub first read deposit-bound for this ore in an unbroken run of passes.
        /// How long a shortfall has stood is what separates a geography problem from a slow pass.</summary>
        public DateTime SinceUtc;

        public string Key { get { return ExtractionPlanner.OrderKey(HubNodeId, OreKey); } }
    }

    /// <summary>What <see cref="ExtractionCapacity.DeriveGroundSignals"/> remembers of one (hub, ore)
    /// between passes: the <c>since</c> it carries and when it was last signalled. In memory only.</summary>
    public sealed class ExtractionGroundMemo
    {
        public DateTime SinceUtc;
        public DateTime LastSignalledUtc;
    }

    /// <summary>
    /// Capacity growth for the NPC extraction planner (docs/mining-plan.md, "### 3. Capacity growth").
    /// Pure: turns one pass's <see cref="StandingExtractionOrder"/>s into the two answers the plan
    /// asks for — how many drill hulls a hull-bound order lacks, and which hubs need new ground.
    ///
    /// <para><b>The missing rate</b> is <c>target − deliverable − max(mined, in-flight)</c>, never
    /// below zero. <c>deliverable</c> is what the idle drills on free deposits add this pass;
    /// the trips already out count through the larger of the two readings of them — delivered ore
    /// (<see cref="StandingExtractionOrder.MinedPerHour"/>, which also sees the hulls flying home)
    /// and the sustained rate of the dwells still to finish
    /// (<see cref="StandingExtractionOrder.InFlightPerHour"/>, which sees a trip before it delivers).
    /// Taking the larger never counts one trip twice and errs toward building less.</para>
    /// </summary>
    public static class ExtractionCapacity
    {
        /// <summary>Units per hour the order lacks after every drill it has: see the class summary.
        /// Zero for a paused order.</summary>
        public static double MissingPerHour(StandingExtractionOrder o)
        {
            if (o == null || o.Paused) return 0;
            double missing = o.TargetPerHour - o.DeliverablePerHour - Math.Max(o.MinedPerHour, o.InFlightPerHour);
            return missing > 0 ? missing : 0;
        }

        /// <summary>How many hulls drilling at <paramref name="drillLitersPerSecond"/> (the build
        /// pick's rate, before richness) close a <see cref="ExtractionBound.HullBound"/> order's
        /// missing rate. Each new hull works the next unpaired free deposit, richest first, and adds
        /// the planner's per-pair rate: <c>drill × richness ÷ ((1 + overburden)(1 + cooldown)) × 3600</c>.
        /// Rounded up by construction — the hull that crosses the missing rate counts — and never more
        /// than there are unpaired deposits: one trip per deposit, so a hull beyond that has no seam.
        ///
        /// <para>0 when nothing is missing or no deposit is unpaired. 1 when the rate is unknown
        /// (no pick): one hull is what the dispatch fallback would have asked for.</para></summary>
        public static int HullsToCover(StandingExtractionOrder o, double drillLitersPerSecond, FactionTuning tuning)
        {
            if (o == null || tuning == null) return 0;
            double missingLiters = MissingPerHour(o) * o.LitersPerUnit;
            if (missingLiters <= 0) return 0;
            List<double> seams = o.UnpairedDepositFactors;
            if (seams == null || seams.Count == 0) return 0;
            if (drillLitersPerSecond <= 0) return 1;

            double rest = (1.0 + Math.Max(0, tuning.MineOverburdenFraction))
                        * (1.0 + Math.Max(0, tuning.MineCooldownMultiplier));
            double covered = 0;
            int hulls = 0;
            while (hulls < seams.Count && covered < missingLiters)
            {
                covered += drillLitersPerSecond * seams[hulls] / rest * 3600.0;
                hulls++;
            }
            return hulls;
        }

        /// <summary>One signal per open <see cref="ExtractionBound.DepositBound"/> order with a missing
        /// rate, hub then ore. <see cref="ExtractionGroundSignal.SinceUtc"/> carries over from
        /// <paramref name="previous"/> for a (hub, ore) that was already signalled, and is
        /// <paramref name="now"/> otherwise, so one pass off the bound resets it. The grace-window
        /// overload below is what the Operations pass calls.</summary>
        public static List<ExtractionGroundSignal> DeriveGroundSignals(IList<StandingExtractionOrder> orders,
            IList<ExtractionGroundSignal> previous, DateTime now)
        {
            return DeriveGroundSignals(orders, previous, null, now, 0);
        }

        /// <summary>As the overload above, with a grace window: a (hub, ore) that was signalled at most
        /// <paramref name="graceSeconds"/> ago keeps the <c>since</c> it had then, however many passes
        /// in between it was not signalled. A signal drops for a pass whenever an in-flight trip covers
        /// the target, the bound reads none, the order is absent or paused; none of those is the
        /// shortfall ending, so none of them may restart its clock.
        ///
        /// <para><paramref name="memory"/> is the caller's, keyed <c>OrderKey(hub, ore)</c>: updated
        /// for every signal derived here, and an entry not signalled for longer than the window is
        /// removed. Null, or a window of 0 or less, is the overload above.</para></summary>
        public static List<ExtractionGroundSignal> DeriveGroundSignals(IList<StandingExtractionOrder> orders,
            IList<ExtractionGroundSignal> previous, IDictionary<string, ExtractionGroundMemo> memory,
            DateTime now, double graceSeconds)
        {
            List<ExtractionGroundSignal> signals = DeriveGroundSignalsCore(orders, previous, memory, now, graceSeconds);
            if (memory == null) return signals;

            foreach (ExtractionGroundSignal s in signals)
            {
                ExtractionGroundMemo m;
                if (!memory.TryGetValue(s.Key, out m)) memory[s.Key] = m = new ExtractionGroundMemo();
                m.SinceUtc = s.SinceUtc;
                m.LastSignalledUtc = now;
            }
            if (memory.Count > signals.Count)
            {
                List<string> stale = null;
                foreach (KeyValuePair<string, ExtractionGroundMemo> kv in memory)
                    if (!WithinGrace(kv.Value, now, graceSeconds))
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                if (stale != null)
                    foreach (string key in stale) memory.Remove(key);
            }
            return signals;
        }

        /// <summary>The remembered <c>since</c> of <paramref name="key"/> when it was signalled within
        /// the window; <paramref name="now"/> otherwise.</summary>
        private static DateTime RememberedSince(IDictionary<string, ExtractionGroundMemo> memory, string key,
            DateTime now, double graceSeconds)
        {
            ExtractionGroundMemo m;
            return memory != null && memory.TryGetValue(key, out m) && WithinGrace(m, now, graceSeconds)
                ? m.SinceUtc
                : now;
        }

        private static bool WithinGrace(ExtractionGroundMemo m, DateTime now, double graceSeconds)
        {
            return graceSeconds > 0 && (now - m.LastSignalledUtc).TotalSeconds <= graceSeconds;
        }

        private static List<ExtractionGroundSignal> DeriveGroundSignalsCore(IList<StandingExtractionOrder> orders,
            IList<ExtractionGroundSignal> previous, IDictionary<string, ExtractionGroundMemo> memory,
            DateTime now, double graceSeconds)
        {
            var signals = new List<ExtractionGroundSignal>();
            if (orders == null) return signals;

            Dictionary<string, DateTime> since = null;
            if (previous != null && previous.Count > 0)
            {
                since = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                foreach (ExtractionGroundSignal p in previous)
                    if (p != null) since[p.Key] = p.SinceUtc;
            }

            foreach (StandingExtractionOrder o in orders)
            {
                if (o == null || o.Paused || o.Bound != ExtractionBound.DepositBound) continue;
                double missing = MissingPerHour(o);
                if (missing <= 0) continue;
                DateTime first;
                if (since == null || !since.TryGetValue(o.Key, out first))
                    first = RememberedSince(memory, o.Key, now, graceSeconds);
                signals.Add(new ExtractionGroundSignal
                {
                    HubNodeId = o.HubNodeId,
                    OreKey = o.OreKey,
                    MissingLitersPerHour = missing * o.LitersPerUnit,
                    SinceUtc = first,
                });
            }
            signals.Sort(CompareByHubThenOre);
            return signals;
        }

        /// <summary>The hull a drill-quality upgrade is measured against: the <b>upper median</b> of
        /// <paramref name="hulls"/> by drill rate, each hull counted once (<see cref="ExtractionHull.Count"/>).
        /// Null when there is none.
        ///
        /// <para>Not the slowest: nothing retires a drill hull, so a slow one replaced at dispatch stays
        /// parked in the catchment, and a rule keyed on the slowest would order upgrades for as long as
        /// it exists. The upper median stops once the fast hulls are at least as many as the slow ones —
        /// which, the hulls being flown fastest first, is every slow hull replaced one at a time.</para></summary>
        public static ExtractionHull UpgradeBasis(IList<ExtractionHull> hulls)
        {
            if (hulls == null || hulls.Count == 0) return null;
            var sorted = new List<ExtractionHull>(hulls.Count);
            int total = 0;
            foreach (ExtractionHull h in hulls)
                if (h != null && h.Count > 0 && h.LitersPerSecond > 0) { sorted.Add(h); total += h.Count; }
            if (total == 0) return null;
            sorted.Sort((a, b) =>
            {
                int cmp = a.LitersPerSecond.CompareTo(b.LitersPerSecond);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.PrefabId, b.PrefabId);
            });
            int index = total / 2;   // upper median: the middle one, or the faster of the two middles
            foreach (ExtractionHull h in sorted)
            {
                if (index < h.Count) return h;
                index -= h.Count;
            }
            return sorted[sorted.Count - 1];
        }

        /// <summary>Whether a deposit-bound order wants a drill-quality upgrade to a hull drilling at
        /// <paramref name="pickLitersPerSecond"/>: it is open, still short
        /// (<see cref="MissingPerHour"/> above zero), has a working hull to compare against, and the
        /// pick drills at least <see cref="FactionTuning.ExtractionUpgradeRateRatio"/> times as fast as
        /// the <see cref="UpgradeBasis"/>. A ratio at or below 1 turns it off.</summary>
        public static bool WantsUpgrade(StandingExtractionOrder o, double pickLitersPerSecond, FactionTuning tuning)
        {
            return o != null && WantsHubUpgrade(new[] { o }, pickLitersPerSecond, tuning);
        }

        /// <summary>The hub-level form of <see cref="WantsUpgrade"/>, and the one the Shipyard pass
        /// asks: one decision per hub, whatever ores it orders. <paramref name="hubOrders"/> are one
        /// hub's orders; only its open <see cref="ExtractionBound.DepositBound"/> ones count. The need is
        /// the largest of their missing rates (litres per hour), and the bar is measured against the
        /// upper median of their pooled working hulls (<see cref="PooledWorkingHulls"/>).</summary>
        public static bool WantsHubUpgrade(IList<StandingExtractionOrder> hubOrders, double pickLitersPerSecond,
            FactionTuning tuning)
        {
            if (hubOrders == null || tuning == null) return false;
            double ratio = tuning.ExtractionUpgradeRateRatio;
            if (ratio <= 1.0 || pickLitersPerSecond <= 0 || HubMissingLitersPerHour(hubOrders) <= 0) return false;
            ExtractionHull basis = UpgradeBasis(PooledWorkingHulls(hubOrders));
            return basis != null && pickLitersPerSecond >= ratio * basis.LitersPerSecond;
        }

        /// <summary>The largest missing rate, in litres per hour, among the open deposit-bound orders
        /// in <paramref name="hubOrders"/>; 0 when none is short.</summary>
        public static double HubMissingLitersPerHour(IList<StandingExtractionOrder> hubOrders)
        {
            double largest = 0;
            if (hubOrders == null) return largest;
            foreach (StandingExtractionOrder o in hubOrders)
            {
                if (!IsUpgradeOrder(o)) continue;
                double missing = MissingPerHour(o) * o.LitersPerUnit;
                if (missing > largest) largest = missing;
            }
            return largest;
        }

        /// <summary>The working hulls of the open deposit-bound orders in <paramref name="hubOrders"/>,
        /// each hull once. The planner hands every order at a hub the same index entries for what is
        /// parked at the hub and on a deposit that holds more than one of its ores, so the union is
        /// taken by reference: pooled, a hull is not counted once per ore.</summary>
        public static List<ExtractionHull> PooledWorkingHulls(IList<StandingExtractionOrder> hubOrders)
        {
            var pooled = new List<ExtractionHull>();
            if (hubOrders == null) return pooled;
            var seen = new HashSet<ExtractionHull>();
            foreach (StandingExtractionOrder o in hubOrders)
            {
                if (!IsUpgradeOrder(o) || o.WorkingHulls == null) continue;
                foreach (ExtractionHull h in o.WorkingHulls)
                    if (h != null && seen.Add(h)) pooled.Add(h);
            }
            return pooled;
        }

        private static bool IsUpgradeOrder(StandingExtractionOrder o)
        {
            return o != null && !o.Paused && o.Bound == ExtractionBound.DepositBound;
        }

        /// <summary>The <c>EXTRACTION_GROUND</c> trace line for one signal, behind
        /// <c>factions debug extraction</c>:
        /// <c>EXTRACTION_GROUND &lt;tag&gt; hub=&lt;node&gt; ore=&lt;key&gt; missing=&lt;L/h&gt; since=&lt;UTC&gt;</c>.</summary>
        public static string FormatGroundTrace(string tag, ExtractionGroundSignal s)
        {
            var sb = new StringBuilder(96);
            sb.Append("EXTRACTION_GROUND ").Append(tag ?? "?");
            if (s == null) return sb.ToString();
            sb.Append(" hub=").Append(s.HubNodeId ?? "?")
              .Append(" ore=").Append(s.OreKey ?? "?")
              .Append(" missing=").Append(ExtractionPlanner.Compact(s.MissingLitersPerHour))
              .Append(" since=").Append(s.SinceUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static int CompareByHubThenOre(ExtractionGroundSignal a, ExtractionGroundSignal b)
        {
            int cmp = string.CompareOrdinal(a.HubNodeId, b.HubNodeId);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OreKey, b.OreKey);
        }
    }
}
