using System;

namespace Factions.Core
{
    /// <summary>
    /// The performance ceiling on the prefab pool (FAC-385): a hull predicted to cost more than
    /// <see cref="CeilingMs"/> of the frame when awake is never chosen by role.
    ///
    /// <para><b>The prediction</b> is the load ledger's physics law, calibrated absolutely on the
    /// 2026-09-23 benches: five hulls flown one at a time, awake and alone, measured
    /// <c>CostPerBlockExpMs x blocks^Exponent</c> ms/frame with a median coefficient of 5.0e-5 at
    /// exponent 1.3 (docs/load-governor.md, step 3). <c>blocks</c> is the prefab's total over every
    /// grid it spawns, which is what a spawned hull puts in the world.</para>
    ///
    /// <para><b>Learned costs</b> (<see cref="Learned"/>) replace the formula for a prefab once it
    /// has been flown long enough to be measured: the formula is a bench law for a hull drifting in
    /// zero gravity and carries no logic term, and it priced an <c>Alsvidr</c> flying in 0.86 g at
    /// 0.66 ms against a measured 1.3-2.9 (production Main, 2026-11-21).</para>
    ///
    /// <para>A ceiling of zero or less disables it.</para>
    /// </summary>
    public sealed class PrefabCeiling
    {
        public double CeilingMs = 2.0;
        public double CostPerBlockExpMs = 5.0e-5;
        public double Exponent = 1.3;

        public bool Enabled { get { return CeilingMs > 0; } }

        public double PredictedAwakeMs(int blocks)
        {
            return blocks <= 0 ? 0.0 : CostPerBlockExpMs * Math.Pow(blocks, Exponent);
        }

        public bool IsOver(int blocks)
        {
            return Enabled && PredictedAwakeMs(blocks) > CeilingMs;
        }

        /// <summary>What each prefab was measured to cost on the servers that flew it
        /// (<see cref="PrefabCostLearner"/>); null judges by the formula alone. A learned cost is used
        /// once it rests on <see cref="LearnedMinWindows"/> windows of presence.</summary>
        public Func<string, PrefabCostLearner.Estimate> Learned;

        public double LearnedMinWindows = 20;

        /// <summary>The learned cost of <paramref name="prefabId"/> when there is enough of it, else
        /// null.</summary>
        public PrefabCostLearner.Estimate LearnedFor(string prefabId)
        {
            Func<string, PrefabCostLearner.Estimate> learned = Learned;
            if (learned == null || string.IsNullOrEmpty(prefabId)) return null;
            PrefabCostLearner.Estimate e = learned(prefabId);
            return e != null && e.Windows >= LearnedMinWindows ? e : null;
        }

        /// <summary>Awake cost of a prefab: measured where it has been learned, the formula otherwise.</summary>
        public double PredictedAwakeMs(string prefabId, int blocks)
        {
            PrefabCostLearner.Estimate e = LearnedFor(prefabId);
            return e != null ? e.TotalMs : PredictedAwakeMs(blocks);
        }

        public bool IsOver(string prefabId, int blocks)
        {
            return Enabled && PredictedAwakeMs(prefabId, blocks) > CeilingMs;
        }
    }
}
