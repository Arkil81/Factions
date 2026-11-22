using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// The last telemetry window's costliest parties, grids, entity types and scripts, kept so a
    /// bridge verb (<c>factions serverperf top</c>) can print them.
    ///
    /// <para><b>Why it exists.</b> The per-grid and per-type cost series (<c>ledger_party</c>,
    /// <c>ledger_grid</c>, <c>entity_cost</c>, <c>entity_cost_type</c>, <c>pb_cost_block</c>) were
    /// written only to InfluxDB. On a host whose InfluxDB is reachable only from the box itself
    /// (production), the measurement that names what is costing the frame had no other reader.
    /// This holds the rows the window already computed for those points; nothing new is measured.</para>
    ///
    /// <para>Every figure is ms per frame over the window, the unit the governor budgets in.</para>
    /// </summary>
    public sealed class ServerPerfTop
    {
        public sealed class Row
        {
            public string Name;
            /// <summary>Owning party, or null where the row has none (a type).</summary>
            public string Party;
            public double LogicMs;
            public double PhysicsMs;
            /// <summary>Grids in a party row; calls per window in an entity row; 0 elsewhere.</summary>
            public long Count;
            public double TotalMs { get { return LogicMs + PhysicsMs; } }
        }

        public DateTime AtUtc;
        public double WindowSeconds;
        public long Frames;
        /// <summary>Game-thread busy time per frame; NaN when the window had no busy samples.</summary>
        public double BusyMs = double.NaN;
        /// <summary>This plugin's own per-frame tick.</summary>
        public double OursMs;
        /// <summary>Havok step plus world-transform time; NaN when the step hook did not report.</summary>
        public double PhysicsMs = double.NaN;
        /// <summary>Entity update cost: serial (game thread), parallel CPU summed over workers, and the
        /// wall time the game thread waited for the parallel passes.</summary>
        public double SerialMs;
        public double ParallelCpuMs;
        public double ParallelWallMs;
        /// <summary>True when the ledger ran this window (needs per-entity cost and a census).</summary>
        public bool HasLedger;
        public double UnattributedPhysicsMs;

        public readonly List<Row> Parties = new List<Row>();
        /// <summary>Ledger grids: logic plus apportioned physics.</summary>
        public readonly List<Row> Grids = new List<Row>();
        /// <summary>Entity update cost by grid, logic only, including the "(not on a grid)" bucket
        /// the ledger has no row for.</summary>
        public readonly List<Row> EntityGrids = new List<Row>();
        public readonly List<Row> Types = new List<Row>();
        public readonly List<Row> Scripts = new List<Row>();

        /// <summary>One section per line; the bridge flattens newlines to " | ".</summary>
        public static string Format(ServerPerfTop t, int n, DateTime nowUtc)
        {
            if (t == null)
                return "serverperf top: no window has closed yet.";
            if (n <= 0) n = 10;

            var sb = new StringBuilder();
            double age = (nowUtc - t.AtUtc).TotalSeconds;
            sb.Append("serverperf top: window ").Append(F0(t.WindowSeconds)).Append(" s ending ")
              .Append(t.AtUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("Z (")
              .Append(F0(age < 0 ? 0 : age)).Append(" s ago), ").Append(t.Frames).Append(" frames; ms/frame: busy ")
              .Append(Ms(t.BusyMs)).Append(", physics ").Append(Ms(t.PhysicsMs))
              .Append(", entity logic serial ").Append(Ms(t.SerialMs))
              .Append(" + parallel wait ").Append(Ms(t.ParallelWallMs))
              .Append(" (parallel cpu ").Append(Ms(t.ParallelCpuMs)).Append(")")
              .Append(", Factions tick ").Append(Ms(t.OursMs));
            if (t.HasLedger)
                sb.Append(", physics not charged to a grid ").Append(Ms(t.UnattributedPhysicsMs));

            if (t.HasLedger)
            {
                Section(sb, "parties", t.Parties, n, r =>
                    r.Name + " " + Ms(r.TotalMs) + " (logic " + Ms(r.LogicMs) + " + physics " + Ms(r.PhysicsMs)
                    + ", " + r.Count + " simulated grids)");
                Section(sb, "grids", t.Grids, n, r =>
                    Label(r) + " " + Ms(r.TotalMs) + " (logic " + Ms(r.LogicMs) + " + physics " + Ms(r.PhysicsMs) + ")");
            }
            else
            {
                sb.Append('\n').Append("parties/grids: no ledger this window (needs per-entity cost ON and a grid census).");
            }
            Section(sb, "entity logic by grid", t.EntityGrids, n, r => Label(r) + " " + Ms(r.TotalMs) + " (" + r.Count + " calls)");
            Section(sb, "entity types", t.Types, n, r => r.Name + " " + Ms(r.TotalMs) + " (" + r.Count + " calls)");
            Section(sb, "scripts", t.Scripts, n, r => Label(r) + " " + Ms(r.TotalMs));
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, List<Row> rows, int n, Func<Row, string> line)
        {
            sb.Append('\n').Append(title).Append(": ");
            if (rows.Count == 0) { sb.Append("none"); return; }
            var sorted = new List<Row>(rows);
            sorted.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            int shown = Math.Min(n, sorted.Count);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(line(sorted[i]));
            }
            if (sorted.Count > shown) sb.Append("; +").Append(sorted.Count - shown).Append(" more");
        }

        private static string Label(Row r)
        {
            string name = string.IsNullOrEmpty(r.Name) ? "(unnamed)" : r.Name;
            return string.IsNullOrEmpty(r.Party) ? name : name + " [" + r.Party + "]";
        }

        private static string Ms(double v)
        {
            return double.IsNaN(v) ? "n/a" : v.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string F0(double v)
        {
            return v.ToString("F0", CultureInfo.InvariantCulture);
        }
    }
}
