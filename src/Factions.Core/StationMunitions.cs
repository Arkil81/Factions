using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// A materialised station's ammunition: drawn from its node's stock when the grid stands, topped
    /// up from that stock while it stands, and credited back when it despawns.
    ///
    /// <para><b>Only what was drawn counts</b>, as for fleets (<see cref="FleetMunitions"/>). The
    /// prefab's or scan's own magazines are stripped at spawn and earn nothing, and a credit never
    /// exceeds <see cref="SpatialNode.StationMunitions"/>, the record of what was drawn and is still
    /// aboard. The host reads the grids; this class owns every change to stock and the record.</para>
    ///
    /// <para>A station grid is never saved, so whatever a record holds at boot was aboard a grid
    /// that no longer exists: <see cref="CreditAfterRestart"/> hands it back. The record is lowered
    /// by every read (<see cref="ApplyObserved"/>), so it is at most one top-up interval stale.</para>
    ///
    /// <para>Provenance: FAC-441.</para>
    /// </summary>
    public static class StationMunitions
    {
        public static bool Enabled(FactionTuning tuning)
        {
            return tuning != null && tuning.MunitionDrawMode >= MunitionDrawModes.Draw;
        }

        /// <summary>What <paramref name="node"/>'s station is loaded with: one sortie load of its
        /// design's weapons (<see cref="FleetMunitions.FullLoad"/>), the same sizing a fleet hull
        /// takes. Empty for a node with no founded station, or a design the catalogue carries no
        /// munition data for.</summary>
        public static Dictionary<string, long> FullLoad(SpatialNode node, IPrefabCatalogue catalogue, FactionTuning tuning,
                                                        Dictionary<string, Dictionary<string, long>> perPrefabCache = null)
        {
            if (node == null || node.StableStationId == 0 || string.IsNullOrEmpty(node.StationPrefabId) || catalogue == null)
                return new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> load;
            if (perPrefabCache != null && perPrefabCache.TryGetValue(node.StationPrefabId, out load)) return load;
            load = FleetMunitions.FullLoad(catalogue, new[] { node.StationPrefabId }, tuning);
            if (perPrefabCache != null) perPrefabCache[node.StationPrefabId] = load;
            return load;
        }

        /// <summary>Draws, per key, what <paramref name="target"/> asks for beyond what is
        /// <paramref name="aboard"/>, as far as the node's stock goes, and adds it to the record.
        /// Returns what was drawn, for the host to place; whatever it cannot place goes back through
        /// <see cref="ReturnUnplaced"/>.</summary>
        public static Dictionary<string, long> Draw(WorldModel world, SpatialNode node,
                                                    IDictionary<string, long> target, IDictionary<string, long> aboard)
        {
            var drawn = new Dictionary<string, long>(StringComparer.Ordinal);
            if (world == null || node == null || target == null) return drawn;
            foreach (KeyValuePair<string, long> want in target)
            {
                long have = 0;
                if (aboard != null) aboard.TryGetValue(want.Key, out have);
                long gap = want.Value - Math.Max(0, have);
                if (gap <= 0) continue;
                long stock;
                node.Inventory.TryGetValue(want.Key, out stock);
                long take = Math.Min(gap, stock);
                if (take <= 0) continue;
                world.AddNodeInventory(node.Id, want.Key, -take);
                AddToRecord(node, want.Key, take);
                drawn[want.Key] = take;
            }
            return drawn;
        }

        /// <summary>Returns to stock what a <see cref="Draw"/> took and the grids had no room for,
        /// never more than the record holds. Returns the magazines returned.</summary>
        public static long ReturnUnplaced(WorldModel world, SpatialNode node, IDictionary<string, long> unplaced)
        {
            if (world == null || node == null || unplaced == null || node.StationMunitions == null) return 0;
            long returned = 0;
            foreach (KeyValuePair<string, long> left in unplaced)
            {
                long held;
                if (left.Value <= 0 || !node.StationMunitions.TryGetValue(left.Key, out held)) continue;
                long back = Math.Min(left.Value, held);
                if (back <= 0) continue;
                world.AddNodeInventory(node.Id, left.Key, back);
                AddToRecord(node, left.Key, -back);
                returned += back;
            }
            TrimRecord(node);
            return returned;
        }

        /// <summary>Lowers the record to what the grids were read to hold, never raising it — a
        /// grid cannot hand back rounds it was not given — and books what went as consumption at the
        /// node, the way a fleet's fired magazines are. A key the read did not find is spent.
        /// Returns the magazines spent.</summary>
        public static long ApplyObserved(WorldModel world, SpatialNode node, IDictionary<string, long> observed)
        {
            if (node == null || node.StationMunitions == null || node.StationMunitions.Count == 0) return 0;
            long spent = 0;
            var keys = new List<string>(node.StationMunitions.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                long had = node.StationMunitions[keys[i]];
                long seen = 0;
                if (observed != null) observed.TryGetValue(keys[i], out seen);
                long now = seen < 0 ? 0 : Math.Min(had, seen);
                if (now == had) continue;
                node.StationMunitions[keys[i]] = now;
                spent += had - now;
                CreditSpent(world, node, keys[i], had - now);
            }
            TrimRecord(node);
            return spent;
        }

        /// <summary>A station despawning: applies the final read, credits what is left of the record
        /// to stock, and clears it. Returns the magazines credited; <paramref name="spent"/> is what
        /// the final read found gone.</summary>
        public static long Credit(WorldModel world, SpatialNode node, IDictionary<string, long> observed, out long spent)
        {
            spent = ApplyObserved(world, node, observed);
            return CreditRecord(world, node);
        }

        /// <summary>Hands back every record in the world in full: after a restart no station grid
        /// stands, so each record is magazines aboard a grid that is gone. Returns the magazines
        /// credited; <paramref name="nodes"/> is how many nodes held any.</summary>
        public static long CreditAfterRestart(WorldModel world, out int nodes)
        {
            nodes = 0;
            if (world == null) return 0;
            long credited = 0;
            foreach (SpatialNode node in world.Graph.Nodes.Values)
            {
                if (node.StationMunitions == null) continue;
                long back = CreditRecord(world, node);
                if (back > 0) nodes++;
                credited += back;
            }
            return credited;
        }

        /// <summary>Hands one node's record back in full: the grid that held it is gone without a
        /// final read (the server holding it restarted). Returns the magazines credited.</summary>
        public static long CreditStranded(WorldModel world, SpatialNode node)
        {
            return world == null || node == null ? 0 : CreditRecord(world, node);
        }

        /// <summary>Magazines on the record, summed over keys.</summary>
        public static long Aboard(SpatialNode node)
        {
            long total = 0;
            if (node != null && node.StationMunitions != null)
                foreach (KeyValuePair<string, long> held in node.StationMunitions)
                    if (held.Value > 0) total += held.Value;
            return total;
        }

        private static long CreditRecord(WorldModel world, SpatialNode node)
        {
            if (node.StationMunitions == null) return 0;
            long credited = 0;
            foreach (KeyValuePair<string, long> held in node.StationMunitions)
            {
                if (held.Value <= 0) continue;
                world.AddNodeInventory(node.Id, held.Key, held.Value);
                credited += held.Value;
            }
            node.StationMunitions = null;
            return credited;
        }

        private static void CreditSpent(WorldModel world, SpatialNode node, string key, long spent)
        {
            if (world == null || spent <= 0) return;
            double litres = world.ItemVolumes != null ? world.ItemVolumes.VolumeLitersPerUnit(key) : 0;
            world.AddPendingDemandHistoryCredit(node.Id, key, litres > 0 ? spent * litres : spent);
        }

        private static void AddToRecord(SpatialNode node, string key, long delta)
        {
            if (node.StationMunitions == null)
                node.StationMunitions = new Dictionary<string, long>(StringComparer.Ordinal);
            long held;
            node.StationMunitions.TryGetValue(key, out held);
            long next = held + delta;
            node.StationMunitions[key] = next < 0 ? 0 : next;
        }

        private static void TrimRecord(SpatialNode node)
        {
            if (node.StationMunitions == null) return;
            foreach (KeyValuePair<string, long> held in node.StationMunitions)
                if (held.Value > 0) return;
            node.StationMunitions = null;
        }
    }
}
