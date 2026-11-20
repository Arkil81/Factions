using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One withdrawable holding: a prefab a node has spare hulls of, and how many.</summary>
    public struct ShipAssetOffer
    {
        public string PrefabId;
        public int Count;
    }

    /// <summary>
    /// The two rules behind the Services Terminal's ship-asset surface: which of a node's hulls a
    /// player may take out, and which of a hull's cargo keys the node will take in.
    ///
    /// <para>Both are here rather than in the host for the reason <see cref="ServiceSettlement"/>
    /// gives: the host side is SE entity work with no offline harness, so the arithmetic is the part
    /// that can be pinned, and it is the part that does not live in the manager.</para>
    ///
    /// <para>Provenance: FAC-283.</para>
    /// </summary>
    public static class ShipAssetRules
    {
        /// <summary>Inventory-key prefixes a hull may bring into a node's stock. Deliberately the
        /// ones the node's own economy consumes — ore feeds refining, ingots feed assembly,
        /// components feed builds, seeds let a farm grow a crop its body does not. Anything else
        /// (ammunition, tools, bottles, a bare-subtype key) would land as a channel nothing draws
        /// down, so it refuses the send instead of quietly becoming stock that never moves.</summary>
        public static readonly string[] AcceptedCargoPrefixes = { "Ore:", "Ingot:", "Comp:", InventoryKeyMapper.SeedPrefix };

        /// <summary>Hulls of one prefab a node may hand over: stock less the copies a store listing
        /// has already claimed, floored at zero. Never negative even though the invariant on
        /// <see cref="SpatialNode.ListedForSale"/> says it cannot exceed stock — a floor here is one
        /// line, and a negative row on the wire would read as a debt the player could withdraw.</summary>
        public static int WithdrawableCount(int stock, int listed)
        {
            int spare = stock - (listed > 0 ? listed : 0);
            return spare > 0 ? spare : 0;
        }

        /// <summary>Every prefab at one node with at least one hull spare, in the stock's own
        /// enumeration order.
        ///
        /// <para>The empty prefab id is skipped rather than reported as an anonymous row: it is the
        /// free-build marker, there is no prefab behind it, and nothing could be spawned from a
        /// withdrawal of one. <see cref="Factions.Core"/> has no other use for that key either — the
        /// node window names it "(free build)" for the same reason.</para></summary>
        public static List<ShipAssetOffer> WithdrawableAt(
            IDictionary<string, int> shipStock, IDictionary<string, int> listedForSale)
        {
            var offers = new List<ShipAssetOffer>();
            if (shipStock == null) return offers;

            foreach (KeyValuePair<string, int> pair in shipStock)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                int listed = 0;
                if (listedForSale != null) listedForSale.TryGetValue(pair.Key, out listed);
                int count = WithdrawableCount(pair.Value, listed);
                if (count <= 0) continue;
                offers.Add(new ShipAssetOffer { PrefabId = pair.Key, Count = count });
            }
            return offers;
        }

        /// <summary>Whether a cargo key may be credited to a node.</summary>
        public static bool IsAcceptedCargoKey(string invKey)
        {
            if (string.IsNullOrEmpty(invKey)) return false;
            for (int i = 0; i < AcceptedCargoPrefixes.Length; i++)
                if (invKey.StartsWith(AcceptedCargoPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Sorts a hull's cargo census into what the node will take and what refuses the send.
        ///
        /// <para>Gas is skipped silently rather than refused: a gas key names a tank's contents, not
        /// an item, and no player can unload one to get past a refusal. Everything else that is not
        /// ore, an ingot or a component is named back to the player, who can empty it and retry —
        /// which is why the refusal carries keys rather than a count.</para>
        /// </summary>
        /// <param name="census">Inventory key -> units, unsummed and unordered.</param>
        /// <param name="accepted">Filled with the summed acceptable entries. Untouched entries are
        /// left as they were, so a caller may reuse a dictionary.</param>
        /// <param name="refusedKeys">Filled with the offending keys, ordinal-sorted and
        /// deduplicated, so a refusal message reads the same way twice.</param>
        /// <returns>True when nothing was refused and the send may proceed.</returns>
        public static bool FilterCargo(
            IEnumerable<KeyValuePair<string, long>> census,
            IDictionary<string, long> accepted,
            IList<string> refusedKeys)
        {
            if (census == null) return true;

            var refused = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> entry in census)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value <= 0) continue;
                if (InventoryKeyMapper.IsGasKey(entry.Key)) continue;

                if (!IsAcceptedCargoKey(entry.Key))
                {
                    if (seen.Add(entry.Key)) refused.Add(entry.Key);
                    continue;
                }
                if (accepted == null) continue;
                long cur;
                accepted.TryGetValue(entry.Key, out cur);
                accepted[entry.Key] = cur + entry.Value;
            }

            if (refused.Count == 0) return true;

            refused.Sort(StringComparer.Ordinal);
            if (refusedKeys != null)
                for (int i = 0; i < refused.Count; i++) refusedKeys.Add(refused[i]);
            return false;
        }
    }
}
