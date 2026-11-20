using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The load governor's ledger (docs/load-governor.md, build step 3): what each party costs the
    /// frame, in milliseconds per frame. Read-only — nothing acts on it.
    ///
    /// <para><b>Two terms per grid.</b> <i>Logic</i> is measured: the entity-update time the
    /// orchestrator hook attributed to the grid. <i>Physics</i> is predicted, because Havok steps a
    /// whole world at once and has no per-grid figure to read:</para>
    /// <code>
    ///   weight(g) = blocks(g) ^ SizeExponent  x  (awake ? AwakeMultiplier : 1)      dynamic grids
    ///   weight(g) = 0                                                               static grids
    ///   physics(g) = measuredPhysics x weight(g) / sum(weight)
    /// </code>
    /// <para>Only the proportions of the prediction matter: normalising to the measured physics
    /// time makes the attribution add up to what the server actually paid, whatever the model's
    /// absolute error.</para>
    ///
    /// <para><b>Static grids weigh nothing.</b> A fixed body is never integrated, and at
    /// blocks^2.2 a 3,000-block station would otherwise outweigh a moving ship by an order of
    /// magnitude and absorb the physics a ship caused.</para>
    ///
    /// <para><b>Parallel logic is scaled to the frame.</b> Worker-thread time is CPU summed over
    /// threads; what the main thread waits for is the pass's wall time. Each grid's parallel share
    /// is multiplied by wall/CPU for the window so logic is in the same unit as physics.</para>
    ///
    /// <para><b>Not charged to any party:</b> logic on entities that are on no grid (characters,
    /// floating objects, voxels) goes to <see cref="World"/>, and logic on a grid the census has not
    /// seen goes to <see cref="Unknown"/>.</para>
    /// </summary>
    public sealed class LoadLedger
    {
        public const string World = "world";
        public const string Unknown = "unknown";

        public sealed class Coefficients
        {
            public double SizeExponent = 1.3;
            public double AwakeMultiplier = 15.0;
        }

        /// <summary>One simulated grid as the census saw it.</summary>
        public sealed class SimulatedGrid
        {
            public long GridId;
            public string Party;
            public string Name;
            public int Blocks;
            public bool Awake;
            public bool IsStatic;
        }

        /// <summary>Measured entity-update time of one grid for the window, ms per frame.</summary>
        public struct GridLogic
        {
            public double SerialMs;
            public double ParallelCpuMs;
        }

        public sealed class PartyCost
        {
            public double LogicMs;
            public double PhysicsMs;
            public int SimulatedGrids;
            public double TotalMs { get { return LogicMs + PhysicsMs; } }
        }

        public sealed class GridCost
        {
            public long GridId;
            public string Party;
            public string Name;
            public double LogicMs;
            public double PhysicsMs;
            public double Weight;
            public double TotalMs { get { return LogicMs + PhysicsMs; } }
        }

        public sealed class Result
        {
            public readonly Dictionary<string, PartyCost> Parties = new Dictionary<string, PartyCost>(StringComparer.Ordinal);
            public readonly List<GridCost> Grids = new List<GridCost>();
            /// <summary>Measured physics the model had no weight to put on (no dynamic simulated
            /// grid), so it is attributed to nobody.</summary>
            public double UnattributedPhysicsMs;
            public double MeasuredPhysicsMs;
            public double LogicMs;
            public double ParallelScale;
        }

        public static double PhysicsWeight(int blocks, bool awake, bool isStatic, Coefficients c)
        {
            if (isStatic || blocks <= 0) return 0.0;
            if (c == null) c = new Coefficients();
            return Math.Pow(blocks, c.SizeExponent) * (awake ? c.AwakeMultiplier : 1.0);
        }

        /// <param name="simulated">Every grid SE stepped at the last census.</param>
        /// <param name="logicByGrid">Measured logic per grid id, over every grid, simulated or not.
        /// Grid id 0 is logic on entities that are on no grid.</param>
        /// <param name="partyOf">Party for any grid the census saw; null for one it did not.</param>
        /// <param name="measuredPhysicsMs">Havok step plus world transforms, ms per frame. Negative
        /// or NaN when not measured this window; physics is then not attributed.</param>
        /// <param name="parallelWallMs">Wall time of the parallel passes, ms per frame.</param>
        /// <param name="parallelCpuMs">Worker CPU of the parallel passes, ms per frame.</param>
        public static Result Compute(IList<SimulatedGrid> simulated,
                                     IDictionary<long, GridLogic> logicByGrid,
                                     Func<long, string> partyOf,
                                     double measuredPhysicsMs,
                                     double parallelWallMs,
                                     double parallelCpuMs,
                                     Coefficients coefficients)
        {
            var r = new Result();
            r.ParallelScale = parallelCpuMs > 0 && parallelWallMs >= 0 ? Math.Min(1.0, parallelWallMs / parallelCpuMs) : 1.0;
            bool physicsKnown = !double.IsNaN(measuredPhysicsMs) && measuredPhysicsMs >= 0;
            r.MeasuredPhysicsMs = physicsKnown ? measuredPhysicsMs : 0.0;

            var gridRows = new Dictionary<long, GridCost>();
            double weightSum = 0;
            if (simulated != null)
            {
                foreach (SimulatedGrid g in simulated)
                {
                    if (g == null || gridRows.ContainsKey(g.GridId)) continue;
                    double w = PhysicsWeight(g.Blocks, g.Awake, g.IsStatic, coefficients);
                    weightSum += w;
                    string party = string.IsNullOrEmpty(g.Party) ? Unknown : g.Party;
                    gridRows[g.GridId] = new GridCost { GridId = g.GridId, Party = party, Name = g.Name, Weight = w };
                    Party(r, party).SimulatedGrids++;
                }
            }

            if (physicsKnown)
            {
                if (weightSum > 0)
                {
                    foreach (GridCost row in gridRows.Values)
                    {
                        row.PhysicsMs = measuredPhysicsMs * row.Weight / weightSum;
                        Party(r, row.Party).PhysicsMs += row.PhysicsMs;
                    }
                }
                else r.UnattributedPhysicsMs = measuredPhysicsMs;
            }

            if (logicByGrid != null)
            {
                foreach (KeyValuePair<long, GridLogic> kv in logicByGrid)
                {
                    double ms = kv.Value.SerialMs + kv.Value.ParallelCpuMs * r.ParallelScale;
                    if (ms <= 0) continue;
                    r.LogicMs += ms;
                    string party;
                    GridCost row;
                    if (kv.Key == 0) party = World;
                    else if (gridRows.TryGetValue(kv.Key, out row))
                    {
                        row.LogicMs += ms;
                        party = row.Party;
                    }
                    else
                    {
                        string p = partyOf != null ? partyOf(kv.Key) : null;
                        party = string.IsNullOrEmpty(p) ? Unknown : p;
                    }
                    Party(r, party).LogicMs += ms;
                }
            }

            r.Grids.AddRange(gridRows.Values);
            r.Grids.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            return r;
        }

        private static PartyCost Party(Result r, string party)
        {
            PartyCost p;
            if (!r.Parties.TryGetValue(party, out p))
            {
                p = new PartyCost();
                r.Parties[party] = p;
            }
            return p;
        }
    }
}
