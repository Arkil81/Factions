using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Hull integrity: what an abstract fight takes out of the hulls that survive it, how
    /// a damaged fleet's firepower is read, and what a damaged hull costs to repair. The host writes
    /// the materialised half (grid block integrity at despawn); everything here is the part the sim
    /// and the host share. See docs/fleet-events.md, "Hull integrity".
    ///
    /// <para>The parameters are code constants rather than tuning keys: they are the slice's first
    /// numbers and have not yet been measured against a live fight.</para></summary>
    public static class HullDamage
    {
        /// <summary>A reading at or above this counts as whole. Block integrity summed over a grid
        /// moves by fractions of a percent for scratches that nothing should pay a repair bill for.</summary>
        public const double WholeAt = 0.99;

        /// <summary>The lowest integrity an abstract fight leaves a hull that survives it. A side
        /// the duel reduced to nothing still brings home the hulls its loss rolls spared, and a hull
        /// that flew home is not a wreck.</summary>
        public const double MinSurvivingIntegrity = 0.25;

        /// <summary>Fraction of the prefab's BOM a hull at integrity 0 costs to repair. One: missing
        /// integrity is missing components, so a hull at 0.6 owes 40% of its bill.</summary>
        public const double RepairBomShare = 1.0;

        /// <summary>Yard hours to repair a hull from 0 to whole once its parts are in; a hull at 0.6
        /// takes 40% of it.</summary>
        public const double RepairHoursPerHull = 4.0;

        /// <summary>The components <paramref name="bom"/> costs to bring a hull at
        /// <paramref name="integrity"/> back to whole: each line times the missing fraction times
        /// <see cref="RepairBomShare"/>, rounded up so a damaged hull never repairs for free. Empty
        /// for a whole hull or an empty BOM.</summary>
        public static Dictionary<string, long> RepairBill(IReadOnlyDictionary<string, long> bom, double integrity)
        {
            var bill = new Dictionary<string, long>();
            double missing = 1.0 - Clamp01(integrity);
            if (bom == null || missing <= 1.0 - WholeAt) return bill;
            foreach (KeyValuePair<string, long> kv in bom)
            {
                if (kv.Value <= 0) continue;
                long units = (long)Math.Ceiling(kv.Value * missing * RepairBomShare - 1e-9);
                if (units > 0) bill[kv.Key] = units;
            }
            return bill;
        }

        /// <summary>How long a yard works a hull at <paramref name="integrity"/> once its parts are in.</summary>
        public static TimeSpan RepairTime(double integrity)
        {
            double missing = 1.0 - Clamp01(integrity);
            return TimeSpan.FromHours(RepairHoursPerHull * missing);
        }

        /// <summary>How long a yard works <paramref name="hull"/> once its parts are in: a repair by
        /// its missing integrity, a refit by the share of its target design that changes — a refit
        /// replacing a third of a design takes as long as repairing a hull a third destroyed.</summary>
        public static TimeSpan WorkTime(DamagedHull hull)
        {
            if (hull == null) return TimeSpan.Zero;
            if (hull.IsRefit) return TimeSpan.FromHours(RepairHoursPerHull * Clamp01(hull.RefitShare));
            return RepairTime(hull.Integrity);
        }

        /// <summary>Applies an abstract fight to the hulls that come out of it. The duel tracks one
        /// capacity pool per side, and <paramref name="survivingFraction"/> is what is left of it, so
        /// the rule is uniform: every hull slot keeps that fraction of its integrity, floored at
        /// <see cref="MinSurvivingIntegrity"/> (never raising a hull already below the floor).
        /// Uniform is the only split the duel supports: it does not say which hull took which hits,
        /// and scaling each hull by the same fraction scales the fleet's firepower by exactly the
        /// fraction the duel reported. Deterministic: no roll, and the order of slots is irrelevant.
        /// Answers whether anything changed; an armed fleet's bands are rebuilt at the new integrity
        /// when it did (<see cref="RescaleBands"/>).</summary>
        public static bool ApplyAttrition(ref PendingFleetAction fleet, double survivingFraction, IPrefabCatalogue cat)
        {
            double r = Clamp01(survivingFraction);
            bool changed = false;
            int slots = fleet.HullSlotCount;
            for (int i = 0; i < slots; i++)
            {
                double old = fleet.IntegrityOf(i);
                double next = Math.Min(old, Math.Max(MinSurvivingIntegrity, old * r));
                if (next >= old) continue;
                fleet.SetIntegrity(i, next);
                if (fleet.IntegrityOf(i) < old) changed = true;
            }
            if (changed) RescaleBands(ref fleet, cat);
            return changed;
        }

        /// <summary>Rebuilds an armed fleet's <see cref="PendingFleetAction.WeaponBands"/> at its
        /// recorded integrity. A fleet with no bands is left without: convoys are not given bands at
        /// dispatch, and every reader that derives them reads the integrity itself.</summary>
        public static void RescaleBands(ref PendingFleetAction fleet, IPrefabCatalogue cat)
        {
            if (fleet.WeaponBands == null || fleet.WeaponBands.Count == 0) return;
            List<WeaponBand> bands = ScaledBands(fleet, cat);
            if (bands != null && bands.Count > 0) fleet.WeaponBands = bands;
        }

        /// <summary>The fleet's bands built hull by hull from the catalogue, each hull's bands scaled
        /// by its integrity: the surviving squadron members one by one, any other fleet as its prefab
        /// times <see cref="PendingFleetAction.ConvoySize"/> at its single reading. Null when there
        /// is no catalogue; empty when it knows none of the hulls.</summary>
        public static List<WeaponBand> ScaledBands(PendingFleetAction fleet, IPrefabCatalogue cat)
        {
            if (cat == null) return null;
            var bands = new List<WeaponBand>();
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                IReadOnlyList<string> hulls = fleet.HullsToReturn;
                for (int i = 0; i < hulls.Count; i++)
                    AddScaled(bands, cat.WeaponBands(hulls[i]), 1, fleet.IntegrityOf(i));
                return bands;
            }
            AddScaled(bands, string.IsNullOrEmpty(fleet.ShipPrefabId) ? null : cat.WeaponBands(fleet.ShipPrefabId),
                      Math.Max(1, fleet.ConvoySize), fleet.IntegrityOf(0));
            return bands;
        }

        private static void AddScaled(List<WeaponBand> into, IReadOnlyList<WeaponBand> perShip, int count, double integrity)
        {
            if (perShip == null) return;
            for (int b = 0; b < perShip.Count; b++)
                into.Add(new WeaponBand { Dps = (float)(perShip[b].Dps * count * integrity), RangeM = perShip[b].RangeM });
        }

        /// <summary>Whether <paramref name="after"/>'s hulls differ from a reading taken earlier
        /// (<paramref name="before"/>, the <see cref="PendingFleetAction.HullIntegrity"/> it had, null
        /// for whole; <paramref name="slotsBefore"/>, its <see cref="PendingFleetAction.HullSlotCount"/>):
        /// a slot lost or an integrity changed.</summary>
        public static bool Differs(IReadOnlyList<double> before, int slotsBefore, PendingFleetAction after)
        {
            if (after.HullSlotCount != slotsBefore) return true;
            for (int i = 0; i < slotsBefore; i++)
            {
                double was = before == null || i >= before.Count ? 1.0 : before[i];
                if (Math.Abs(after.IntegrityOf(i) - was) > 0.0005) return true;
            }
            return false;
        }

        /// <summary>Each hull slot as <c>prefab:integrity</c>, comma-separated, for a log line.</summary>
        public static string Describe(PendingFleetAction fleet)
        {
            var sb = new StringBuilder();
            int slots = fleet.HullSlotCount;
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            for (int i = 0; i < slots; i++)
            {
                if (i > 0) sb.Append(',');
                string prefab = hulls.Count > i ? hulls[i] : fleet.ShipPrefabId;
                sb.Append(string.IsNullOrEmpty(prefab) ? "?" : prefab).Append(':')
                  .Append(fleet.IntegrityOf(i).ToString("0.00", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Lands <paramref name="count"/> hulls of <paramref name="prefabId"/> at
        /// <paramref name="node"/>: whole ones into <see cref="SpatialNode.ShipStock"/> exactly as
        /// <see cref="ShipPool.Return(SpatialNode, string, int)"/> always has, damaged ones into
        /// <see cref="SpatialNode.DamagedHulls"/> with their repair bill, one <c>REPAIR_QUEUED</c>
        /// line each. A hull with no prefab or no recipe has no bill to pay and lands whole. Answers
        /// how many went to repair.</summary>
        public static int Land(WorldModel world, SpatialNode node, string nodeId, string prefabId, int count,
                               double integrity, string fleetLogId, string actorTag, IList<string> log)
        {
            if (node == null || count <= 0) return 0;
            if (Clamp01(integrity) >= WholeAt || string.IsNullOrEmpty(prefabId))
            {
                ShipPool.Return(node, prefabId, count);
                return 0;
            }
            bool hasRecipe;
            Dictionary<string, long> bom = ProductionManager.GetPrefabBom(world != null ? world.Recipes : null, prefabId, out hasRecipe);
            if (!hasRecipe)
            {
                ShipPool.Return(node, prefabId, count);
                if (log != null)
                    log.Add("REPAIR_SKIPPED " + actorTag + " fleet=" + (fleetLogId ?? "?") + " prefab=" + prefabId
                            + " node=" + (nodeId ?? node.Id) + " integrity=" + F2(integrity) + " why=no_recipe count=" + count);
                return 0;
            }
            Dictionary<string, long> bill = RepairBill(bom, integrity);
            long units = 0;
            foreach (long u in bill.Values) units += u;
            int yard;
            bool hasYard = node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out yard) && yard >= 1;
            for (int i = 0; i < count; i++)
            {
                var hull = new DamagedHull
                {
                    PrefabId  = prefabId,
                    Integrity = Clamp01(integrity),
                    QueuedUtc = world != null ? world.UtcNow : DateTime.UtcNow,
                };
                foreach (KeyValuePair<string, long> kv in bill) hull.Bill[kv.Key] = kv.Value;
                node.DamagedHulls.Add(hull);
                if (log != null)
                    log.Add("REPAIR_QUEUED " + actorTag + " fleet=" + (fleetLogId ?? "?") + " prefab=" + prefabId
                            + " node=" + (nodeId ?? node.Id) + " integrity=" + F2(integrity)
                            + " bill_units=" + units + " hours=" + RepairTime(integrity).TotalHours.ToString("0.0", CultureInfo.InvariantCulture)
                            + (hasYard ? "" : " yard=none"));
            }
            return count;
        }

        /// <summary>The order a node's yard serves its damaged hulls in: the smallest outstanding bill
        /// first, so a scraped hull is back in stock before a wreck's parts are found; ties go to the
        /// higher integrity, then the earlier <see cref="DamagedHull.QueuedUtc"/>, then the order the
        /// hulls landed in. A parts-complete hull owes nothing and so leads. Deterministic: no roll,
        /// and the outstanding bill is read once, before anything is absorbed.</summary>
        public static List<DamagedHull> ServedOrder(IList<DamagedHull> hulls)
        {
            var order = new List<DamagedHull>();
            if (hulls == null || hulls.Count == 0) return order;
            int n = hulls.Count;
            var outstanding = new long[n];
            var index = new int[n];
            for (int i = 0; i < n; i++)
            {
                index[i] = i;
                outstanding[i] = hulls[i] == null ? long.MaxValue : hulls[i].OutstandingUnits();
            }
            Array.Sort(index, delegate (int a, int b)
            {
                // Damage is mended before a sound hull is upgraded: a refit never holds a part a
                // repair is waiting on.
                bool refitA = hulls[a] != null && hulls[a].IsRefit, refitB = hulls[b] != null && hulls[b].IsRefit;
                if (refitA != refitB) return refitA ? 1 : -1;
                if (outstanding[a] != outstanding[b]) return outstanding[a].CompareTo(outstanding[b]);
                DamagedHull ha = hulls[a], hb = hulls[b];
                if (ha != null && hb != null)
                {
                    if (ha.Integrity != hb.Integrity) return hb.Integrity.CompareTo(ha.Integrity);
                    if (ha.QueuedUtc != hb.QueuedUtc) return ha.QueuedUtc.CompareTo(hb.QueuedUtc);
                }
                return a.CompareTo(b);
            });
            for (int i = 0; i < n; i++) if (hulls[index[i]] != null) order.Add(hulls[index[i]]);
            return order;
        }

        /// <summary>Services every damaged hull at <paramref name="node"/> in
        /// <see cref="ServedOrder"/>: each absorbs what its bill still lacks from node inventory,
        /// leaving alone whatever the node's working builds are still owed, and then from the parts
        /// already absorbed by hulls further down the order that are still waiting -- so the order
        /// is binding, and a wreck that collected first cannot hold a part a scraped hull needs to
        /// finish. A hull whose parts are in is never drawn on. Once its parts are in, a hull is
        /// credited to <see cref="SpatialNode.ShipStock"/> after <see cref="RepairTime"/>; every
        /// parts-complete hull is worked at once, with no slot limit. A node without a Shipyard
        /// repairs nothing, and neither does a paused yard.
        ///
        /// <para>Logs <c>REPAIR_PARTS_COMPLETE</c> with the hull's served position when its parts
        /// are in, <c>REPAIR_DONE</c> per hull returned to stock, and one <c>REPAIR_REASSIGNED</c>
        /// per pass that moved parts between hulls. Answers how many finished.</para></summary>
        public static int ServiceRepairs(WorldModel world, SpatialNode node, string factionTag, IList<string> log)
        {
            if (node == null || node.DamagedHulls.Count == 0 || node.ShipyardPaused) return 0;
            int yard;
            if (!node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out yard) || yard < 1) return 0;

            DateTime now = world.UtcNow;
            List<DamagedHull> order = ServedOrder(node.DamagedHulls);

            // Parts held by waiting hulls not yet visited: a key nobody further down holds is never
            // searched for, which keeps a settled queue's pass linear in its size.
            var heldBehind = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].PartsComplete) continue;
                foreach (KeyValuePair<string, long> kv in order[i].Collected)
                {
                    long running;
                    heldBehind.TryGetValue(kv.Key, out running);
                    heldBehind[kv.Key] = running + kv.Value;
                }
            }

            long reassigned = 0;
            int receivers = 0;
            var donors = new HashSet<DamagedHull>();
            var finished = new List<DamagedHull>();
            var keys = new List<string>();
            for (int pos = 0; pos < order.Count; pos++)
            {
                DamagedHull hull = order[pos];
                if (!hull.PartsComplete)
                {
                    foreach (KeyValuePair<string, long> kv in hull.Collected)
                        heldBehind[kv.Key] = heldBehind[kv.Key] - kv.Value;

                    keys.Clear();
                    keys.AddRange(hull.Bill.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    bool complete = true;
                    long received = 0;
                    for (int k = 0; k < keys.Count; k++)
                    {
                        string key = keys[k];
                        long bill = hull.Bill[key];
                        long have;
                        hull.Collected.TryGetValue(key, out have);
                        if (have >= bill) continue;

                        long available;
                        node.Inventory.TryGetValue(key, out available);
                        available -= node.BuildQueueReserved(key, now);
                        long take = available <= 0 ? 0 : Math.Min(available, bill - have);
                        if (take > 0)
                        {
                            world.AddNodeInventory(node.Id, key, -take);
                            have += take;
                        }

                        long behind;
                        heldBehind.TryGetValue(key, out behind);
                        for (int d = order.Count - 1; d > pos && have < bill && behind > 0; d--)
                        {
                            DamagedHull donor = order[d];
                            if (donor.PartsComplete) continue;
                            long held;
                            if (!donor.Collected.TryGetValue(key, out held) || held <= 0) continue;
                            long move = Math.Min(held, bill - have);
                            if (held - move > 0) donor.Collected[key] = held - move;
                            else donor.Collected.Remove(key);
                            have += move;
                            behind -= move;
                            received += move;
                            donors.Add(donor);
                        }
                        heldBehind[key] = behind;

                        if (have > 0) hull.Collected[key] = have;
                        if (have < bill) complete = false;
                    }
                    if (received > 0) { reassigned += received; receivers++; }
                    if (!complete) continue;

                    hull.PartsCompleteUtc = now;
                    if (log != null)
                        log.Add(factionTag + (hull.IsRefit ? " REFIT_PARTS_COMPLETE prefab=" : " REPAIR_PARTS_COMPLETE prefab=")
                                + hull.PrefabId + (hull.IsRefit ? " to=" + hull.RefitTo : "") + " node=" + node.Id
                                + " integrity=" + F2(hull.Integrity) + " pos=" + (pos + 1) + "/" + order.Count
                                + " bill_units=" + hull.BillUnits()
                                + " hours=" + WorkTime(hull).TotalHours.ToString("0.0", CultureInfo.InvariantCulture));
                }
                if (now - hull.PartsCompleteUtc < WorkTime(hull)) continue;
                finished.Add(hull);
            }

            if (reassigned > 0 && log != null)
                log.Add(factionTag + " REPAIR_REASSIGNED node=" + node.Id + " units=" + reassigned
                        + " to=" + receivers + " from=" + donors.Count);

            for (int i = 0; i < finished.Count; i++)
            {
                DamagedHull hull = finished[i];
                node.DamagedHulls.Remove(hull);
                if (hull.IsRefit)
                {
                    FinishRefit(world, node, hull, factionTag, now, log);
                    continue;
                }
                ShipPool.Return(node, hull.PrefabId, 1);
                int stock;
                node.ShipStock.TryGetValue(hull.PrefabId ?? "", out stock);
                if (log != null)
                    log.Add(factionTag + " REPAIR_DONE prefab=" + hull.PrefabId + " node=" + node.Id
                            + " integrity=" + F2(hull.Integrity) + " consumed=" + DescribeBill(hull.Collected)
                            + " hours=" + (now - hull.QueuedUtc).TotalHours.ToString("0.0", CultureInfo.InvariantCulture)
                            + " stock=" + stock);
            }
            return finished.Count;
        }

        /// <summary>A finished refit rejoins stock as its target, and what the old design held
        /// beyond the new one goes into the yard's inventory (<see cref="HullRefit.Surplus"/>).</summary>
        private static void FinishRefit(WorldModel world, SpatialNode node, DamagedHull hull, string factionTag,
                                        DateTime now, IList<string> log)
        {
            ShipPool.Return(node, hull.RefitTo, 1);
            Dictionary<string, long> surplus = HullRefit.Surplus(world.Recipes, hull.PrefabId, hull.RefitTo);
            foreach (KeyValuePair<string, long> kv in surplus)
            {
                if (kv.Value <= 0) continue;
                long have;
                node.Inventory.TryGetValue(kv.Key, out have);
                node.Inventory[kv.Key] = have + kv.Value;
            }
            int stock;
            node.ShipStock.TryGetValue(hull.RefitTo, out stock);
            if (log != null)
                log.Add(factionTag + " REFIT_DONE prefab=" + hull.PrefabId + " to=" + hull.RefitTo + " node=" + node.Id
                        + " consumed=" + DescribeBill(hull.Collected)
                        + " credited=" + DescribeBill(surplus)
                        + " hours=" + (now - hull.QueuedUtc).TotalHours.ToString("0.0", CultureInfo.InvariantCulture)
                        + " stock=" + stock);
        }

        /// <summary>Where one damaged hull stands, as a reader shows it.</summary>
        public static RepairState StateOf(SpatialNode node, DamagedHull hull)
        {
            int yard;
            if (node == null || !node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out yard) || yard < 1)
                return RepairState.NoShipyard;
            if (node.ShipyardPaused) return RepairState.YardPaused;
            return hull != null && hull.PartsComplete ? RepairState.Repairing : RepairState.WaitingForParts;
        }

        /// <summary>When a parts-complete hull rejoins stock; default while it still waits for parts.</summary>
        public static DateTime DoneUtc(DamagedHull hull)
        {
            if (hull == null || !hull.PartsComplete) return default(DateTime);
            return hull.PartsCompleteUtc + WorkTime(hull);
        }

        /// <summary><paramref name="node"/>'s repair queue as a reader shows it: in
        /// <see cref="ServedOrder"/>, with consecutive hulls that are the same in every respect a
        /// row shows -- prefab, integrity (and so bill), what they have absorbed and when their
        /// parts came in -- folded into one row with a count. A fleet that came home together lands
        /// as one row rather than a screenful of copies. Empty for a node with nothing queued.</summary>
        public static List<RepairQueueRow> QueueView(SpatialNode node)
        {
            var rows = new List<RepairQueueRow>();
            if (node == null || node.DamagedHulls.Count == 0) return rows;
            List<DamagedHull> order = ServedOrder(node.DamagedHulls);
            RepairQueueRow current = null;
            for (int i = 0; i < order.Count; i++)
            {
                DamagedHull hull = order[i];
                if (current != null && SameRow(current.Hull, hull))
                {
                    current.Count++;
                    continue;
                }
                current = new RepairQueueRow
                {
                    Hull = hull,
                    PrefabId = hull.PrefabId,
                    RefitTo = hull.RefitTo,
                    Count = 1,
                    Position = i + 1,
                    Integrity = hull.Integrity,
                    State = StateOf(node, hull),
                    BillUnits = hull.BillUnits(),
                    CollectedUnits = hull.CollectedUnits(),
                    QueuedUtc = hull.QueuedUtc,
                    PartsCompleteUtc = hull.PartsCompleteUtc,
                    DoneUtc = DoneUtc(hull),
                };
                rows.Add(current);
            }
            return rows;
        }

        private static bool SameRow(DamagedHull a, DamagedHull b)
        {
            if (!string.Equals(a.PrefabId, b.PrefabId, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.RefitTo, b.RefitTo, StringComparison.Ordinal)) return false;
            if (a.Integrity != b.Integrity || a.PartsCompleteUtc != b.PartsCompleteUtc) return false;
            if (a.Collected.Count != b.Collected.Count) return false;
            foreach (KeyValuePair<string, long> kv in a.Collected)
            {
                long other;
                if (!b.Collected.TryGetValue(kv.Key, out other) || other != kv.Value) return false;
            }
            return true;
        }

        /// <summary>Counts the hulls in repair at <paramref name="node"/>, of
        /// <paramref name="prefabId"/> only unless it is null: <paramref name="waiting"/> still short
        /// of parts, <paramref name="repairing"/> with their parts in. Adds to both. With a
        /// <paramref name="catalogue"/>, every version of <paramref name="prefabId"/>'s design counts
        /// (<see cref="PrefabVersions.SameFamily"/>).</summary>
        public static void CountRepairs(SpatialNode node, string prefabId, ref int waiting, ref int repairing,
                                        IPrefabCatalogue catalogue = null)
        {
            if (node == null) return;
            for (int i = 0; i < node.DamagedHulls.Count; i++)
            {
                DamagedHull hull = node.DamagedHulls[i];
                if (hull == null) continue;
                if (prefabId != null && !PrefabVersions.SameFamily(catalogue, hull.PrefabId, prefabId)) continue;
                if (hull.PartsComplete) repairing++;
                else waiting++;
            }
        }

        private static string DescribeBill(Dictionary<string, long> items)
        {
            if (items.Count == 0) return "none";
            var keys = new List<string>(items.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(keys[i]).Append(':').Append(items[keys[i]].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private static double Clamp01(double v)
        {
            return double.IsNaN(v) ? 1.0 : (v < 0 ? 0 : (v > 1 ? 1 : v));
        }
    }

    /// <summary>Where a damaged hull stands in its node's repair queue.</summary>
    public enum RepairState : byte
    {
        /// <summary>The yard is still collecting its bill.</summary>
        WaitingForParts = 0,
        /// <summary>The parts are in; it rejoins stock at <see cref="HullDamage.DoneUtc"/>.</summary>
        Repairing = 1,
        /// <summary>The node has no Shipyard. The hull waits here only while the faction has no
        /// yard it can reach; otherwise <see cref="RepairTransfer"/> flies it to one.</summary>
        NoShipyard = 2,
        /// <summary>The node's yard is paused.</summary>
        YardPaused = 3,
    }

    /// <summary>One row of a node's repair queue: one hull, or several identical ones
    /// (<see cref="HullDamage.QueueView"/>).</summary>
    public sealed class RepairQueueRow
    {
        /// <summary>The row's first hull; its <see cref="DamagedHull.Bill"/> and
        /// <see cref="DamagedHull.Collected"/> are every hull's in the row.</summary>
        public DamagedHull Hull;
        public string PrefabId;
        /// <summary>The version a refit row rejoins stock as; null for a repair.</summary>
        public string RefitTo;
        public int Count;
        /// <summary>1-based served position of the row's first hull.</summary>
        public int Position;
        public double Integrity;
        public RepairState State;
        /// <summary>Per hull.</summary>
        public long BillUnits;
        public long CollectedUnits;
        public DateTime QueuedUtc;
        /// <summary>Default while the row waits for parts.</summary>
        public DateTime PartsCompleteUtc;
        public DateTime DoneUtc;
    }
}
