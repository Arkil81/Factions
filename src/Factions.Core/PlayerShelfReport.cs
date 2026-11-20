using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One shelf line with the live counts <see cref="StoreStockManager"/> evaluates it
    /// against. Every number comes from the manager's own helper, never from a second count.</summary>
    public sealed class ShelfLineReport
    {
        public ShelfLine Line;
        /// <summary><see cref="StoreStockManager.CountListed"/>: copies reserved for sale across the
        /// estate. The shelf's "have", and deliberately not ship stock -- a hull the fleet owns is
        /// not on sale.</summary>
        public long OnSale;
        /// <summary><see cref="StoreStockManager.CountShelfBuildsInProgress"/>: shelf builds of this
        /// hull in flight. Fleet builds of the same hull are excluded, because their output never
        /// reaches the shelf.</summary>
        public int Building;
        /// <summary>The capability class this hull is filed under
        /// (<see cref="StoreShelfPlan.ClassOf"/>), or null when the catalogue can class it as
        /// nothing -- which is the one way a line can be permanently unbuildable.</summary>
        public string Role;
        /// <summary>Why the manager could not act on this line on its last pass, or null.
        ///
        /// <para><b>Manager-owned and read here rather than recomputed</b>, exactly as
        /// <see cref="DoctrineLineReport.RouteBlocked"/> is: it is
        /// <see cref="ShelfLine.LastBlockedLogSignature"/>, the state
        /// <c>StoreStockManager.ServiceShelf</c> sets on the pass that found the line unbuildable and
        /// clears on the pass that does not. So it is <b>null until the first store pass after a
        /// restart</b> (the field is not persisted), and it means "on the last pass", not "right
        /// now". Recomputing it would be a second answer to a question the evaluator already
        /// answers.</para></summary>
        public string BlockedReason;
    }

    /// <summary>One hull this faction holds reserved for sale, across every node it owns.</summary>
    public sealed class ShelfCensusRow
    {
        public string PrefabId;
        /// <summary>Copies reserved for sale (<see cref="SpatialNode.ListedForSale"/>).</summary>
        public long OnSale;
        /// <summary>True when no shelf line names this hull. **The reason this census exists.** A
        /// faction can hold hulls on its shelf that nothing in its list asked for -- everything
        /// stocked before the player wrote a line, and anything left over after one is removed -- and
        /// no window in the game showed them before this one. A doctrine census cannot: its rows are
        /// <c>UnlistedStockAt</c>, which subtracts exactly this stock out.</summary>
        public bool Unlined;
    }

    /// <summary>
    /// Everything the store window shows, assembled from the model: the shelf with its live counts,
    /// and a census of the hulls actually on sale including the ones no line named.
    ///
    /// <para>In Core rather than in the host so it is unit-testable and so the sim can print the same
    /// picture the client draws -- the same reasoning as <see cref="FleetDoctrineReport"/>, whose
    /// shape this mirrors. The host maps these plain objects onto wire structs and adds nothing.</para>
    ///
    /// <para><b>Nothing here is a second computation.</b> On-sale and building come from the
    /// <see cref="StoreStockManager"/> helpers the evaluator itself uses, and the role comes from
    /// <see cref="StoreShelfPlan.ClassOf"/>, which is what decides the role a shelf build is queued
    /// under. A window that counted for itself could tell a player a line was satisfied while the
    /// manager kept building.</para>
    /// </summary>
    public sealed class PlayerShelfReport
    {
        /// <summary>The shelf in list order, which is the order the evaluator walks it in.</summary>
        public readonly List<ShelfLineReport> Lines = new List<ShelfLineReport>();

        /// <summary>Every hull on sale, ordered by prefab id, with the unlined ones marked.</summary>
        public readonly List<ShelfCensusRow> Census = new List<ShelfCensusRow>();

        /// <summary>True when the player has switched restocking off entirely
        /// (<see cref="FactionState.StoreShelfOptOut"/>). Carried separately from the lines because
        /// it is the one state an empty list cannot express, and because a shelf that is full and
        /// switched off looks identical to one that is full and working.</summary>
        public bool StockingOff;
        /// <summary>Assembles the report for one faction. An empty shelf is a real answer, not a
        /// missing one: the census still describes what the shop is holding, which is exactly the
        /// state the window is first opened in.</summary>
        public static PlayerShelfReport Build(WorldModel world, FactionState st)
        {
            var report = new PlayerShelfReport();
            if (world == null || st == null) return report;

            report.StockingOff = st.StoreShelfOptOut;
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));

            for (int i = 0; i < st.Shelf.Count; i++)
            {
                ShelfLine line = st.Shelf[i];
                report.Lines.Add(new ShelfLineReport
                {
                    Line = line,
                    OnSale = StoreStockManager.CountListed(owned, line.PrefabId, world.PrefabCatalogue),
                    Building = StoreStockManager.CountShelfBuildsInProgress(owned, line.PrefabId, world.PrefabCatalogue),
                    Role = StoreShelfPlan.ClassOf(world.PrefabCatalogue, line.PrefabId),
                    BlockedReason = BlockedReasonOf(line),
                });
            }

            BuildCensus(st, owned, report);
            return report;
        }

        /// <summary>The bare reason token out of the manager's log signature -- the signature is
        /// <c>line=… prefab=… reason=X</c>, and only the X is a fact about the line. Parsed rather
        /// than recomputed for the reason the field's own doc gives.</summary>
        private static string BlockedReasonOf(ShelfLine line)
        {
            string sig = line.LastBlockedLogSignature;
            if (string.IsNullOrEmpty(sig)) return null;
            const string marker = "reason=";
            int at = sig.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            int start = at + marker.Length;
            int end = sig.IndexOf(' ', start);
            return end < 0 ? sig.Substring(start) : sig.Substring(start, end - start);
        }

        /// <summary>Every prefab with a reservation anywhere on the estate, summed and marked with
        /// whether the shelf still names it.</summary>
        private static void BuildCensus(FactionState st, List<SpatialNode> owned, PlayerShelfReport report)
        {
            var onSale = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (SpatialNode node in owned)
                foreach (KeyValuePair<string, int> kv in node.ListedForSale)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    long have;
                    onSale.TryGetValue(kv.Key, out have);
                    onSale[kv.Key] = have + kv.Value;
                }

            var ids = new List<string>(onSale.Keys);
            ids.Sort(StringComparer.Ordinal);   // two snapshots of an unchanged world read identically
            foreach (string id in ids)
                report.Census.Add(new ShelfCensusRow
                {
                    PrefabId = id,
                    OnSale = onSale[id],
                    Unlined = st.ShelfLineFor(id) == null,
                });
        }
    }
}
