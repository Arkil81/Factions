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
    }
}
