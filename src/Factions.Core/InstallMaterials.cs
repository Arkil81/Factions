using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One line of a capability install's material gate: what the install needs of a key,
    /// and what the faction holds toward it, split into the two pools the gate actually sums.
    ///
    /// <para><see cref="Reserved"/> is stock already staged out of routable inventory by
    /// <c>ProductionManager.ReserveInstallStock</c>/<c>ReserveInstallBomStock</c>;
    /// <see cref="Routable"/> is ordinary inventory still exposed to every other consumer. Both
    /// count toward the install, so a row is only genuinely blocking when their sum falls short —
    /// reserved-alone would report "short" for a key the faction has plenty of and simply has not
    /// claimed yet this tick.</para></summary>
    public struct InstallMaterialRow
    {
        /// <summary>Inventory key ("Comp:SteelPlate") for a BOM-backed capability, or an
        /// <see cref="ItemKind"/> name ("Ingot") for one charged through the flat install costs.
        /// <see cref="ByKind"/> says which.</summary>
        public string Key;
        /// <summary>True when <see cref="Key"/> names an <see cref="ItemKind"/> bucket rather than a
        /// specific inventory key — a flat-cost capability claims "any key of this kind" and cannot
        /// be attributed to one component at all.</summary>
        public bool ByKind;
        public long Need;
        public long Reserved;
        public long Routable;

        public long Have { get { return Reserved + Routable; } }
        /// <summary>Shortfall against <see cref="Need"/>, floored at 0 — the amount that has to
        /// appear before this row stops blocking the install.</summary>
        public long Shortfall { get { long s = Need - Have; return s > 0 ? s : 0; } }
    }

    /// <summary>The material half of a capability install's gate, in the same shape a Shipyard
    /// <see cref="BuildQueueEntry"/> reports its BOM: what is needed, what is held, what is short.
    ///
    /// <para>Mirrors <see cref="DevelopStationValidator"/>'s own item-stock branch exactly — BOM-backed
    /// capabilities check specific component keys, everything else checks the flat
    /// <see cref="ItemKind"/> costs — so an observer can never report an install as fundable that the
    /// gate would reject, or vice versa.</para></summary>
    public static class InstallMaterials
    {
        /// <summary>The flat install-cost kinds, in the order they are reported. Same set
        /// <c>ProductionManager</c> reserves against and <see cref="DevelopStationValidator"/>
        /// gates on.</summary>
        public static readonly ItemKind[] FlatCostKinds =
            new ItemKind[] { ItemKind.Ore, ItemKind.Ingot, ItemKind.ProcessedGood, ItemKind.Hydrogen };

        /// <summary>Per-key material state for installing <paramref name="cap"/> at
        /// <paramref name="factionTag"/>. Faction-wide on both sides: the reservation target is the
        /// cumulative BOM across the faction's owned nodes, so a per-node answer would be a slice of
        /// a claim that is not per-node in the first place. Empty when the capability costs no items.</summary>
        public static List<InstallMaterialRow> For(IWorldView world, string factionTag,
            NodeCapabilityKind cap, FactionTuning tuning)
        {
            var rows = new List<InstallMaterialRow>();
            if (world == null || tuning == null || string.IsNullOrEmpty(factionTag)) return rows;

            Dictionary<string, long> bom;
            if (tuning.TryGetInstallBom(cap, out bom))
            {
                foreach (KeyValuePair<string, long> kv in bom)
                {
                    if (kv.Value <= 0) continue;
                    var row = new InstallMaterialRow();
                    row.Key      = kv.Key;
                    row.Need     = kv.Value;
                    row.Reserved = world.GetFactionReservedInventoryByKey(factionTag, kv.Key);
                    row.Routable = world.GetFactionInventoryByKey(factionTag, kv.Key);
                    rows.Add(row);
                }
                rows.Sort(ByShortfallThenKey);
                return rows;
            }

            foreach (ItemKind kind in FlatCostKinds)
            {
                long need = tuning.InstallCostFor(cap, kind);
                if (need <= 0) continue;
                var row = new InstallMaterialRow();
                row.Key      = kind.ToString();
                row.ByKind   = true;
                row.Need     = need;
                row.Reserved = world.GetReservedInventory(factionTag, kind);
                row.Routable = world.GetInventory(factionTag, kind);
                rows.Add(row);
            }
            rows.Sort(ByShortfallThenKey);
            return rows;
        }

        /// <summary>Fraction of the whole install's material need that is covered, 0..1. Summed over
        /// need rather than averaged over rows, so a 500-unit key short by half outweighs a 4-unit
        /// key that is already met. 1 when nothing is needed.</summary>
        public static double Progress(List<InstallMaterialRow> rows)
        {
            if (rows == null || rows.Count == 0) return 1.0;
            long need = 0, covered = 0;
            foreach (InstallMaterialRow r in rows)
            {
                if (r.Need <= 0) continue;
                need += r.Need;
                covered += r.Have < r.Need ? r.Have : r.Need;
            }
            if (need <= 0) return 1.0;
            return (double)covered / need;
        }

        /// <summary>Blocking rows first, largest shortfall at the top — the reading order for
        /// "what, if anything, is holding this install up".</summary>
        private static int ByShortfallThenKey(InstallMaterialRow a, InstallMaterialRow b)
        {
            if (a.Shortfall != b.Shortfall) return a.Shortfall > b.Shortfall ? -1 : 1;
            return string.CompareOrdinal(a.Key, b.Key);
        }
    }
}
