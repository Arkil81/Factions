using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Factions.Core;

namespace Factions.Sim
{
    /// <summary>Renders a Y=0 (orbital-plane) slice of the influence heatmap — colour for live
    /// watching, ascii for logs/fallback.</summary>
    public static class HeatmapRenderer
    {
        private static readonly string ESC = ((char)27).ToString(); // robust ANSI escape

        public static string Home() { return ESC + "[H"; }
        public static string Reset() { return ESC + "[0m"; }

        // Predefined ramps for the three canonical sim factions (preserves demo appearance).
        // All other tags are assigned a ramp from the pool below via a tag-name hash.
        private static readonly Dictionary<string, int[]> Ramp = new Dictionary<string, int[]>
        {
            { "MERC", new[] { 17, 19, 21, 27, 39 } },     // blue
            { "RVN",  new[] { 52, 88, 124, 160, 196 } },   // red
            { "STL",  new[] { 22, 28, 34, 40, 46 } },      // green
        };

        // 256-colour ramp pool: 5-step dark→bright, one per hue family.
        // Used for live-mode server factions whose tags aren't in the dict above.
        private static readonly int[][] RampPool = new int[][]
        {
            new[] { 17,  19,  21,  27,  39  },  // blue
            new[] { 52,  88,  124, 160, 196 },  // red
            new[] { 22,  28,  34,  40,  46  },  // green
            new[] { 23,  30,  37,  44,  51  },  // cyan
            new[] { 53,  89,  125, 161, 197 },  // magenta
            new[] { 94,  130, 166, 202, 208 },  // orange  (stops at 208; 214/220 are too yellow)
            new[] { 55,  56,  92,  93,  129 },  // violet  (was purple 198 ≈ magenta 197; 129=RGB(175,0,255))
            new[] { 58,  64,  70,  76,  82  },  // lime
            new[] { 94,  130, 136, 137, 180 },  // tan/brown (replaces indigo which was ≈ blue; 180=wheat)
            new[] { 95,  131, 168, 205, 213 },  // pink/rose
            new[] { 234, 238, 242, 246, 250 },  // grey     (replaces gold/amber 220=yellow)
            new[] { 29,  35,  41,  47,  86  },  // teal-green
        };

        // Populated once per Frame call; guarantees each tag gets a unique pool slot.
        private static readonly Dictionary<string, int[]> _tagRamp = new Dictionary<string, int[]>();

        private static void BuildTagRamps(IList<(FactionState st, Personality p)> fs)
        {
            _tagRamp.Clear();
            // Assign hardcoded ramps first.
            foreach (var f in fs)
            {
                int[] r;
                if (Ramp.TryGetValue(f.st.Tag, out r)) _tagRamp[f.st.Tag] = r;
            }
            // Collect unassigned tags, sort for stability, then assign pool slots sequentially.
            var remaining = new List<string>();
            foreach (var f in fs)
                if (!_tagRamp.ContainsKey(f.st.Tag)) remaining.Add(f.st.Tag);
            remaining.Sort();
            int slot = 0;
            foreach (string tag in remaining)
            {
                _tagRamp[tag] = RampPool[slot % RampPool.Length];
                slot++;
            }
        }

        private static int[] GetRamp(string tag)
        {
            if (tag == null) return RampPool[0];
            int[] r;
            return _tagRamp.TryGetValue(tag, out r) ? r : RampPool[0];
        }

        private const int Contested = 226; // yellow
        private const int Empty = 234;     // near-black

        public static void EnableAnsi()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
                IntPtr h = GetStdHandle(-11);
                uint mode;
                if (GetConsoleMode(h, out mode)) SetConsoleMode(h, mode | 0x0004);
            }
            catch { }
        }

        public static string Frame(WorldModel world, IList<(FactionState st, Personality p)> fs, int tick, int cols, int rows, bool color,
            IDictionary<string, string> lastAction = null,
            IList<PendingFleetAction> convoys = null,
            IList<string> eventLog = null)
        {
            BuildTagRamps(fs);
            var field = (InfluenceField)world.Influence;

            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag == null) continue;
                double r = n.Radius > 0 ? n.Radius : 40;
                if (n.Position.X - r < minX) minX = n.Position.X - r;
                if (n.Position.X + r > maxX) maxX = n.Position.X + r;
                if (n.Position.Z - r < minZ) minZ = n.Position.Z - r;
                if (n.Position.Z + r > maxZ) maxZ = n.Position.Z + r;
            }
            if (minX > maxX) { minX = -100; maxX = 100; minZ = -100; maxZ = 100; }
            double spanX = Math.Max(1, maxX - minX), spanZ = Math.Max(1, maxZ - minZ);

            // stamp actual owned-source positions so you can SEE each source (even a besieged one a rival
            // dominates) — markers pop in on Expand/Claim and vanish on Withdraw.
            var srcCell = new Dictionary<int, string>();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag == null) continue;
                int sc = (int)((n.Position.X - minX) / spanX * cols);
                int sr = (int)((n.Position.Z - minZ) / spanZ * rows);
                if (sc < 0 || sc >= cols || sr < 0 || sr >= rows) continue;
                srcCell[sr * cols + sc] = n.OwnerFactionTag;
            }

            // in-flight convoy markers: cell index -> fleet (first convoy per cell wins)
            var convoyCell = new Dictionary<int, PendingFleetAction>();
            if (convoys != null)
            {
                foreach (PendingFleetAction fleet in convoys)
                {
                    Vec3 pos = fleet.CurrentPosKm(world.UtcNow);
                    int cc = (int)((pos.X - minX) / spanX * cols);
                    int cr = (int)((pos.Z - minZ) / spanZ * rows);
                    if (cc < 0 || cc >= cols || cr < 0 || cr >= rows) continue;
                    int key = cr * cols + cc;
                    if (!convoyCell.ContainsKey(key)) convoyCell[key] = fleet;
                }
            }

            // Pass 1: sample every cell to find the peak influence value for normalisation.
            int cells = rows * cols;
            var cellTag    = new string[cells];
            var cellTop    = new double[cells];
            var cellSecond = new double[cells];
            double maxV = 1e-12;
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    double x = minX + (col + 0.5) / cols * spanX;
                    double z = minZ + (row + 0.5) / rows * spanZ;
                    string tag; double top, second;
                    field.DominantAt(new Vec3(x, 0, z), out tag, out top, out second);
                    int i = row * cols + col;
                    cellTag[i] = tag; cellTop[i] = top; cellSecond[i] = second;
                    if (top > maxV) maxV = top;
                }
            }

            // Pass 2: render using v/maxV so the brightest cell always hits ramp[4].
            var map = new StringBuilder();
            int contestedCells = 0;
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int i = row * cols + col;
                    string tag = cellTag[i];
                    double vNorm = cellTop[i] / maxV;
                    bool contested = tag != null && vNorm >= 0.05 && (cellSecond[i] / (cellTop[i] + 1e-9)) > 0.6;
                    if (contested) contestedCells++;
                    string srcTag;
                    PendingFleetAction convoy;
                    if (srcCell.TryGetValue(i, out srcTag))
                        map.Append(color ? SourceCellColor(srcTag) : SourceCellAscii(srcTag));
                    else if (convoyCell.TryGetValue(i, out convoy))
                        map.Append(color ? ConvoyCellColor(convoy) : ConvoyCellAscii(convoy));
                    else
                        map.Append(color ? ColorCell(tag, vNorm, contested) : AsciiCell(tag, vNorm, contested));
                }
                if (color) map.Append(ESC + "[0m");
                map.Append('\n');
            }

            double half = Math.Max(InfluenceOctree.CellSizeKm, Math.Max(spanX, spanZ) * 0.6);
            int octreeNodes = InfluenceOctree.CountNodes(field, world.Graph, new Vec3((minX + maxX) / 2, 0, (minZ + maxZ) / 2), half);

            var head = new StringBuilder();
            head.Append("Factions  influence + economy   tick ").Append(tick)
                .Append("   octree-nodes ").Append(octreeNodes)
                .Append("   contested-cells ").Append(contestedCells);
            if (convoys != null) head.Append("   convoys ").Append(convoys.Count);
            head.Append('\n');
            foreach (var f in fs)
            {
                int srcs = 0, dom = 0; double mass = 0, ctlSum = 0;
                foreach (SpatialNode n in world.Graph.Nodes.Values)
                {
                    if (n.OwnerFactionTag == f.st.Tag) { srcs++; mass += n.HoldStrength; ctlSum += field.Control(n.Position, f.st.Tag); }
                    string topTag; double topVal, secondVal;
                    field.DominantAt(n.Position, out topTag, out topVal, out secondVal);
                    if (topTag == f.st.Tag) dom++;
                }
                long flow = world.GetNetFlow(f.st.Tag);
                string act = (lastAction != null && lastAction.ContainsKey(f.st.Tag)) ? lastAction[f.st.Tag] : "";
                int fleetCount = 0;
                if (convoys != null) foreach (PendingFleetAction fl in convoys) if (fl.ActorTag == f.st.Tag) fleetCount++;
                head.Append("  ").Append(Swatch(f.st.Tag, color)).Append(' ').Append(f.st.Tag.PadRight(4))
                    .Append(" src ").Append(srcs.ToString().PadLeft(2))
                    .Append(" dom ").Append(dom.ToString().PadLeft(2))
                    .Append(" mass ").Append(mass.ToString("0.0").PadLeft(4))
                    .Append(" ctl ").Append((srcs > 0 ? ctlSum / srcs : 0).ToString("0.00"))
                    .Append(" cash ").Append(world.GetTreasury(f.st.Tag).ToString().PadLeft(7))
                    .Append(" flow ").Append(flow >= 0 ? "+" : "-").Append(Math.Abs(flow).ToString().PadLeft(5))
                    .Append(" fleets ").Append(fleetCount)
                    .Append("  ").Append(act).Append('\n');
            }

            var footer = new StringBuilder();
            if (eventLog != null && eventLog.Count > 0)
            {
                footer.Append("events:\n");
                int start = Math.Max(0, eventLog.Count - 8);
                for (int i = start; i < eventLog.Count; i++)
                    footer.Append("  ").Append(eventLog[i]).Append('\n');
            }

            return head.ToString() + map.ToString() + footer.ToString();
        }

        private static string Swatch(string tag, bool color)
        {
            if (!color) return "[" + (tag != null && tag.Length > 0 ? tag[0].ToString() : "?") + "]";
            int code = tag != null ? GetRamp(tag)[4] : Empty;
            return ESC + "[48;5;" + code + "m  " + ESC + "[0m";
        }

        private static string ColorCell(string tag, double v, bool contested)
        {
            int code = Empty;
            if (tag != null && v >= 0.03)
            {
                int[] ramp = GetRamp(tag);
                int idx = v >= 0.6 ? 4 : v >= 0.3 ? 3 : v >= 0.15 ? 2 : v >= 0.07 ? 1 : 0;
                code = ramp[idx];
            }
            // contested: keep faction bg so ownership is still visible; overlay !! in bright yellow
            if (contested) return ESC + "[48;5;" + code + ";38;5;226m!!";
            return ESC + "[48;5;" + code + "m  ";
        }

        private static string AsciiCell(string tag, double v, bool contested)
        {
            if (tag == null || v < 0.03) return " .";
            char c = tag[0];
            if (contested) return "!" + char.ToUpper(c);
            return v >= 0.3 ? (" " + char.ToUpper(c)) : (" " + char.ToLower(c));
        }

        // a source marker (the faction letter, '*'-flagged) — stamped on top of the influence wash so the
        // actual asset is visible even where a rival dominates the cell.
        private static string SourceCellAscii(string tag)
        {
            return "*" + char.ToUpper(tag.Length > 0 ? tag[0] : '?');
        }

        private static string SourceCellColor(string tag)
        {
            int code = tag != null ? GetRamp(tag)[4] : Empty;
            return ESC + "[48;5;" + code + ";38;5;231m " + char.ToUpper(tag != null && tag.Length > 0 ? tag[0] : '?');
        }

        // in-flight convoy marker: bold bright text on a mid-intensity faction colour so it pops
        // against the influence wash but doesn't drown out source markers.
        private static string ConvoyCellAscii(PendingFleetAction f)
        {
            char m = f.Kind == ActionKind.Raid ? 'R' : f.Kind == ActionKind.Trade ? '~' : 'o';
            char first = f.ActorTag != null && f.ActorTag.Length > 0 ? char.ToLower(f.ActorTag[0]) : '?';
            return "" + first + m;
        }

        private static string ConvoyCellColor(PendingFleetAction f)
        {
            int bgCode = GetRamp(f.ActorTag)[2];
            char m = f.Kind == ActionKind.Raid ? 'R' : f.Kind == ActionKind.Trade ? '~' : 'o';
            return ESC + "[48;5;" + bgCode + ";38;5;231;1m" + m + m;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint m);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint m);
    }
}
