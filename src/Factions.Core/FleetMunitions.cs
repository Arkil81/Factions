using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>What one group of a hull's weapons fires: the magazine it loads, how much of it the
    /// weapons' own inventories hold, and how fast sustained fire empties it. One slot per
    /// (magazine, range) pair on the hull — weapons sharing both are summed.
    ///
    /// <para><see cref="Dps"/> is the same per-block figure the prefab's
    /// <see cref="WeaponBand"/>s are built from, so a hull's slots sum to its bands' total whenever
    /// every scored weapon resolved a magazine.</para></summary>
    public struct MunitionSlot
    {
        /// <summary>Inventory key of the magazine item — a bare subtype, e.g. <c>NATO_25x184mm</c>.</summary>
        public string MagazineKey;
        public float RangeM;
        public float Dps;
        /// <summary>Magazines emptied per second of sustained fire, reload duty cycle included.</summary>
        public float MagazinesPerSecond;
        /// <summary>Magazines the weapons' own inventories hold when full — the floor of a sortie
        /// load, not the load itself (see <see cref="FleetMunitions.SortieLoad"/>).</summary>
        public int FullLoad;

        /// <summary>The weapon makes this magazine for itself from power or fuel (a weapon
        /// framework's ammo generation), so it is never drawn, hauled or reserved, and never runs
        /// out in a duel. Its rate already caps <see cref="Dps"/> (FAC-478). Absent from the JSON
        /// when false, which is nearly every slot.</summary>
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public bool SelfSupplied;
    }

    /// <summary>Values of <see cref="FactionTuning.MunitionDrawMode"/>. Plain integers, like
    /// <see cref="FuelDrawModes"/>, so the field round-trips through <c>tuning.json</c> as the
    /// number the defaults file carries.</summary>
    public static class MunitionDrawModes
    {
        /// <summary>Nothing is drawn and every fleet fights on whatever its hull is modelled to
        /// carry, without limit.</summary>
        public const int Off = 0;
        /// <summary>Every armed fleet draws a full load from its departure node, spends it in the
        /// duel, and returns what is left where its hulls land.</summary>
        public const int Draw = 1;
    }

    /// <summary>
    /// Fleet ammunition: a full load drawn from the departure node at dispatch, spent in the
    /// abstract duel, and returned to node stock wherever the fleet's hulls are credited back.
    ///
    /// <para><b>Only what was drawn counts.</b> A prefab spawns with magazines already aboard;
    /// none of that is credited to the fleet or ever returned to stock. A fleet whose node was
    /// short departs short and fights with what it drew.</para>
    ///
    /// <para><b>Combat kinds have priority.</b> A non-combat fleet draws only the stock above what
    /// the node's parked combat hulls would need for a full load, so an armed convoy dispatched
    /// first cannot empty the node a Guard is about to leave from.</para>
    ///
    /// <para>A fleet with <see cref="PendingFleetAction.MunitionsArmed"/> false — mode off, an
    /// unarmed hull, or one dispatched before this existed — fights on its weapon bands without
    /// limit, exactly as before.</para>
    ///
    /// <para>Provenance: FAC-386.</para>
    /// </summary>
    public static class FleetMunitions
    {
        /// <summary>The hulls the fleet's munitions are counted over: a squadron's members still
        /// standing, otherwise the prefab once per convoy ship. The same reading the duel's
        /// <c>EffectiveBands</c> takes.</summary>
        public static List<string> Hulls(PendingFleetAction fleet)
        {
            var hulls = new List<string>();
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                IReadOnlyList<string> standing = fleet.HullsToReturn;
                for (int i = 0; i < standing.Count; i++)
                    if (!string.IsNullOrEmpty(standing[i])) hulls.Add(standing[i]);
                return hulls;
            }
            if (string.IsNullOrEmpty(fleet.ShipPrefabId)) return hulls;
            int count = Math.Max(1, fleet.ConvoySize);
            for (int i = 0; i < count; i++) hulls.Add(fleet.ShipPrefabId);
            return hulls;
        }

        /// <summary>Every slot of every hull in <paramref name="hulls"/>, one entry per hull slot.
        /// Empty when the catalogue carries no munition data.</summary>
        public static List<MunitionSlot> SlotsOf(IPrefabCatalogue catalogue, IList<string> hulls)
        {
            var slots = new List<MunitionSlot>();
            var munitionCatalogue = catalogue as IMunitionPrefabCatalogue;
            if (munitionCatalogue == null || hulls == null) return slots;
            for (int i = 0; i < hulls.Count; i++)
            {
                IReadOnlyList<MunitionSlot> own = munitionCatalogue.MunitionSlots(hulls[i]);
                if (own == null) continue;
                for (int s = 0; s < own.Count; s++)
                    if (!string.IsNullOrEmpty(own[s].MagazineKey) && !own[s].SelfSupplied) slots.Add(own[s]);
            }
            return slots;
        }

        /// <summary>The fire of the hulls' weapons that make their own magazines, as bands that never
        /// run dry. <see cref="SlotsOf"/> leaves these out, so nothing draws or reserves for them;
        /// the duel fights them from here instead, or a hull armed with nothing else would be
        /// scored unarmed.</summary>
        public static List<WeaponBand> SelfSuppliedBands(IPrefabCatalogue catalogue, IList<string> hulls, double scale)
        {
            var bands = new List<WeaponBand>();
            var munitionCatalogue = catalogue as IMunitionPrefabCatalogue;
            if (munitionCatalogue == null || hulls == null) return bands;
            for (int i = 0; i < hulls.Count; i++)
            {
                IReadOnlyList<MunitionSlot> own = munitionCatalogue.MunitionSlots(hulls[i]);
                if (own == null) continue;
                for (int s = 0; s < own.Count; s++)
                    if (own[s].SelfSupplied && own[s].Dps > 0f)
                        bands.Add(new WeaponBand { Dps = (float)(own[s].Dps * scale), RangeM = own[s].RangeM });
            }
            return bands;
        }

        /// <summary>Magazines one slot takes on a sortie: what it is estimated to fire in
        /// <see cref="FactionTuning.MunitionSortieDuels"/> full duels, never less than its weapons'
        /// own inventories hold.
        ///
        /// <para>The estimate is the duel's own arithmetic run forward: magazines per second over
        /// <see cref="FactionTuning.CombatTurnFireSeconds"/> for every turn whose range bracket the
        /// slot reaches. A heavy gun that fires its turret inventory dry in one turn carries what a
        /// fight needs, the way conveyor-fed stock keeps a real one firing. A quick-firing gun whose
        /// inventory already outlasts a duel keeps that.</para></summary>
        public static long SortieLoad(MunitionSlot slot, FactionTuning tuning)
        {
            return SortieLoad(slot, tuning, 1);
        }

        /// <summary>The same, for a sortie that plans <paramref name="fights"/> duels — see
        /// <see cref="FightsPlanned"/>.</summary>
        public static long SortieLoad(MunitionSlot slot, FactionTuning tuning, int fights)
        {
            long floor = slot.FullLoad > 0 ? slot.FullLoad : 0;
            if (tuning == null || slot.MagazinesPerSecond <= 0 || tuning.MunitionSortieDuels <= 0) return floor;
            if (fights < 1) fights = 1;

            int turns = tuning.CombatTurns > 0 ? tuning.CombatTurns : 1;
            double[] brackets = tuning.CombatRangeBracketsM != null && tuning.CombatRangeBracketsM.Length == turns
                ? tuning.CombatRangeBracketsM : null;
            int reached = 0;
            for (int t = 0; t < turns; t++)
            {
                double bracket = brackets != null ? brackets[t] : 0;
                if (bracket <= 0 || slot.RangeM >= bracket) reached++;
            }
            double estimate = slot.MagazinesPerSecond * tuning.CombatTurnFireSeconds * reached
                              * tuning.MunitionSortieDuels * fights;
            long wanted = estimate > 0 ? (long)Math.Ceiling(estimate - 1e-6) : 0;
            return wanted > floor ? wanted : floor;
        }

        /// <summary>Duels a fleet's own itinerary plans: one per <see cref="FleetStopKind.Engage"/>
        /// dwell it is on or has queued, never fewer than one. A raid besieges its target for
        /// <see cref="FactionTuning.RaidSiegeCycles"/> dwells and fights at each, so it carries that
        /// many duels of fire. A Guard or Patrol plans none and carries one. Read off the stops
        /// rather than off the kind, so an immediate dispatch or a dwell tuned off (neither queues
        /// the dwells) is not loaded for fights it will not have.</summary>
        public static int FightsPlanned(PendingFleetAction fleet)
        {
            int fights = fleet.CurrentStopKind == FleetStopKind.Engage ? 1 : 0;
            if (fleet.RemainingStops != null)
                for (int i = 0; i < fleet.RemainingStops.Count; i++)
                    if (fleet.RemainingStops[i].Kind == FleetStopKind.Engage) fights++;
            return fights > 1 ? fights : 1;
        }

        /// <summary>Magazines a sortie load of <paramref name="hulls"/> takes, per magazine key —
        /// <see cref="SortieLoad"/> summed over every slot, for <paramref name="fights"/> duels.</summary>
        public static Dictionary<string, long> FullLoad(IPrefabCatalogue catalogue, IList<string> hulls, FactionTuning tuning,
                                                        int fights = 1)
        {
            var load = new Dictionary<string, long>(StringComparer.Ordinal);
            List<MunitionSlot> slots = SlotsOf(catalogue, hulls);
            for (int i = 0; i < slots.Count; i++)
            {
                long slotLoad = SortieLoad(slots[i], tuning, fights);
                if (slotLoad <= 0) continue;
                long held;
                load.TryGetValue(slots[i].MagazineKey, out held);
                load[slots[i].MagazineKey] = held + slotLoad;
            }
            return load;
        }

        /// <summary>What the combat hulls parked at <paramref name="node"/> would draw between them
        /// for one duel each, plus one load for the node's own station
        /// (<see cref="StationMunitions.FullLoad"/>) — the stock a non-combat departure leaves alone,
        /// and what the standing reserve asks the node to hold. A raid's further siege duels are not
        /// held in reserve; its departure asks for them through <see cref="PostShortfall"/>.
        /// <paramref name="perHullCache"/>, when given, memoises each prefab's load across calls.</summary>
        public static Dictionary<string, long> CombatReserve(SpatialNode node, IPrefabCatalogue catalogue,
                                                             FactionTuning tuning,
                                                             Dictionary<string, Dictionary<string, long>> perHullCache = null)
        {
            var reserve = new Dictionary<string, long>(StringComparer.Ordinal);
            if (node == null || catalogue == null) return reserve;
            foreach (KeyValuePair<string, long> need in StationMunitions.FullLoad(node, catalogue, tuning, perHullCache))
                reserve[need.Key] = need.Value;
            if (node.ShipStock.Count == 0) return reserve;
            foreach (KeyValuePair<string, int> parked in node.ShipStock)
            {
                if (parked.Value <= 0 || !ShipPool.IsCombatSuitable(catalogue, parked.Key)) continue;
                Dictionary<string, long> perHull;
                if (perHullCache == null || !perHullCache.TryGetValue(parked.Key, out perHull))
                {
                    perHull = FullLoad(catalogue, new[] { parked.Key }, tuning);
                    if (perHullCache != null) perHullCache[parked.Key] = perHull;
                }
                foreach (KeyValuePair<string, long> need in perHull)
                {
                    long held;
                    reserve.TryGetValue(need.Key, out held);
                    reserve[need.Key] = held + need.Value * parked.Value;
                }
            }
            return reserve;
        }

        /// <summary>Arms a fleet about to depart: draws a full load for its hulls out of its origin
        /// node's stock, sets <see cref="PendingFleetAction.Munitions"/> to what it got, and writes
        /// <c>MUNITIONS_SHORT</c> when the node could not supply it all. Changes nothing for mode
        /// off or a fleet with no armed hull. A fleet already armed is left alone, so a site that
        /// passes the same fleet twice cannot draw twice.
        ///
        /// <para>Only a node the fleet's own faction owns is drawn from. Anywhere else the fleet
        /// departs armed with nothing, which is what an ally's or a lost node can offer it.</para></summary>
        public static void Arm(ref PendingFleetAction fleet, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            Arm(ref fleet, world, tuning, log != null ? (Action<string>)log.Add : null);
        }

        /// <summary>The same, for a site that writes its log through a callback.</summary>
        public static void Arm(ref PendingFleetAction fleet, WorldModel world, FactionTuning tuning, Action<string> log)
        {
            if (world == null || tuning == null || tuning.MunitionDrawMode < MunitionDrawModes.Draw) return;
            if (fleet.MunitionsArmed) return;

            Dictionary<string, long> need = FullLoad(world.PrefabCatalogue, Hulls(fleet), tuning, FightsPlanned(fleet));
            if (need.Count == 0) return;

            SpatialNode node = null;
            if (!string.IsNullOrEmpty(fleet.OriginNodeId)) world.Graph.Nodes.TryGetValue(fleet.OriginNodeId, out node);
            bool owned = node != null && string.Equals(node.OwnerFactionTag, fleet.ActorTag, StringComparison.Ordinal);
            bool combat = FactionsLoadActuator.IsCombat(fleet.Kind);
            Dictionary<string, long> reserve = owned && !combat
                ? CombatReserve(node, world.PrefabCatalogue, tuning)
                : null;

            var loaded = new Dictionary<string, long>(StringComparer.Ordinal);
            long needTotal = 0, drawnTotal = 0;
            StringBuilder shortKeys = null;
            foreach (KeyValuePair<string, long> want in need)
            {
                long drawn = 0;
                if (owned)
                {
                    long have;
                    node.Inventory.TryGetValue(want.Key, out have);
                    long held = 0;
                    if (reserve != null) reserve.TryGetValue(want.Key, out held);
                    long available = have - held;
                    drawn = available <= 0 ? 0 : Math.Min(available, want.Value);
                    if (drawn > 0) world.AddNodeInventory(node.Id, want.Key, -drawn);
                }
                loaded[want.Key] = drawn;
                needTotal += want.Value;
                drawnTotal += drawn;
                if (drawn < want.Value)
                {
                    if (shortKeys == null) shortKeys = new StringBuilder();
                    else shortKeys.Append(',');
                    shortKeys.Append(want.Key).Append('=')
                             .Append(drawn.ToString(CultureInfo.InvariantCulture)).Append('/')
                             .Append(want.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            fleet.Munitions = loaded;
            fleet.MunitionsArmed = true;

            int demanded = 0;
            if (shortKeys != null && combat && owned)
                demanded = PostShortfall(world, tuning, fleet.ActorTag, node.Id, need, loaded);

            if (log == null) return;
            // A combat fleet flying short is the event an operator needs to see; a convoy short of
            // rounds is routine whenever combat holds the stock, and stays behind the debug toggle.
            if (shortKeys != null && (combat || DebugTraces.Munitions))
                log("MUNITIONS_SHORT " + (fleet.LogId ?? "?") + " " + (fleet.ActorTag ?? "?")
                        + " kind=" + fleet.Kind
                        + " node=" + (fleet.OriginNodeId ?? "?")
                        + (owned ? "" : " node_not_owned")
                        + " loaded=" + drawnTotal.ToString(CultureInfo.InvariantCulture)
                        + "/" + needTotal.ToString(CultureInfo.InvariantCulture)
                        + " short=" + shortKeys
                        + (demanded > 0 ? " demanded=" + demanded.ToString(CultureInfo.InvariantCulture) : ""));
            else if (DebugTraces.Munitions)
                log("MUNITIONS_DRAWN " + (fleet.LogId ?? "?") + " " + (fleet.ActorTag ?? "?")
                        + " kind=" + fleet.Kind + " node=" + (fleet.OriginNodeId ?? "?")
                        + " loaded=" + drawnTotal.ToString(CultureInfo.InvariantCulture)
                        + "/" + needTotal.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Asks for the magazines a combat departure from <paramref name="nodeId"/> went
        /// without, so the haul path moves them to where fleets actually leave from. Returns how
        /// many keys it asked for. Consumption alone cannot raise this: it is booked for rounds
        /// fired, and a node whose fleets leave empty fires nothing. Rows as
        /// <see cref="ShortfallDemand.Post"/>, with the full load as the gross quantity.</summary>
        public static int PostShortfall(WorldModel world, FactionTuning tuning, string factionTag, string nodeId,
                                        IDictionary<string, long> fullLoad, IDictionary<string, long> loaded)
        {
            if (world == null || world.Demand == null || tuning == null || string.IsNullOrEmpty(nodeId)) return 0;
            if (tuning.MunitionShortfallDemandMinutes <= 0) return 0;
            DateTime expires = world.UtcNow.AddMinutes(tuning.MunitionShortfallDemandMinutes);

            int posted = 0;
            foreach (KeyValuePair<string, long> want in fullLoad)
            {
                long got;
                loaded.TryGetValue(want.Key, out got);
                if (ShortfallDemand.Post(world.Demand, factionTag, nodeId, want.Key, want.Value, want.Value - got, expires))
                    posted++;
            }
            return posted;
        }

        /// <summary>Posts one founded node's standing ammunition reserve and returns how many keys it
        /// asked for: <see cref="FactionTuning.MunitionReserveLoads"/> full loads for the combat
        /// hulls parked there and the node's own station, less what the node holds. The departure shortfall
        /// (<see cref="PostShortfall"/>) only reacts after a fleet has already flown short; this
        /// keeps stock at the node before the next one leaves.
        ///
        /// <para>Rows as <see cref="PostShortfall"/>'s: a <c>Need:</c> for the gap and a gross
        /// <c>Item:</c> for the target, class <see cref="DemandClass.Fuel"/>, the same shape
        /// <see cref="FuelReserve.PostForNode"/> posts. An <c>Item:</c> row already asking for the
        /// target is left as its poster wrote it. A node holding its target asks for nothing.</para></summary>
        public static int PostReserveForNode(string factionTag, SpatialNode node, IPrefabCatalogue catalogue,
                                             IProductionExecutionContext ctx, FactionTuning tuning, DateTime expiresUtc,
                                             IDictionary<string, DemandEntry> itemRowsByNodeKind,
                                             Dictionary<string, Dictionary<string, long>> perHullCache)
        {
            if (node == null || ctx == null || tuning == null || catalogue == null) return 0;
            if (tuning.MunitionDrawMode < MunitionDrawModes.Draw || tuning.MunitionReserveLoads <= 0) return 0;
            if (node.StableStationId == 0) return 0;   // not founded: nothing departs from it

            Dictionary<string, long> perLoad = CombatReserve(node, catalogue, tuning, perHullCache);
            int posted = 0;
            foreach (KeyValuePair<string, long> load in perLoad)
            {
                long target = (long)Math.Ceiling(load.Value * tuning.MunitionReserveLoads);
                if (target <= 0) continue;
                long have = ctx.GetNodeInventory(node.Id, load.Key);
                long gap = target - have;
                if (gap <= 0) continue;

                ctx.PostDemand(DemandEntry.ForNeed(factionTag, load.Key, node.Id, expiresUtc, gap, DemandClass.Fuel));
                string itemKind = DemandRegister.ItemKind(load.Key);
                DemandEntry prior;
                bool covered = itemRowsByNodeKind != null
                               && itemRowsByNodeKind.TryGetValue(node.Id + "|" + itemKind, out prior)
                               && prior.Quantity >= target;
                if (!covered)
                    ctx.PostDemand(new DemandEntry
                    {
                        FactionTag = factionTag,
                        Kind       = itemKind,
                        NodeId     = node.Id,
                        ExpiresUtc = expiresUtc,
                        Quantity   = target,
                        Class      = DemandClass.Fuel,
                    });
                posted++;
            }
            return posted;
        }

        /// <summary>Credits the munitions aboard <paramref name="hullsCredited"/> of the fleet's
        /// <see cref="PendingFleetAction.HullCount"/> hulls to <paramref name="node"/> — the share
        /// the landed hulls carry; the rest was lost with the hulls that did not come back. Empties
        /// the fleet's record so a second credit path reaching the same fleet returns nothing.
        /// Returns the magazines credited.</summary>
        public static long Credit(ref PendingFleetAction fleet, SpatialNode node, int hullsCredited,
                                  WorldModel world, IList<string> log)
        {
            if (!fleet.MunitionsArmed || fleet.Munitions == null || fleet.Munitions.Count == 0) return 0;
            int total = fleet.HullCount;
            long credited = 0, aboard = 0;
            if (node != null && world != null && hullsCredited > 0 && total > 0)
            {
                double share = hullsCredited >= total ? 1.0 : (double)hullsCredited / total;
                foreach (KeyValuePair<string, long> held in fleet.Munitions)
                {
                    if (held.Value <= 0) continue;
                    aboard += held.Value;
                    long back = share >= 1.0 ? held.Value : (long)Math.Floor(held.Value * share);
                    if (back <= 0) continue;
                    world.AddNodeInventory(node.Id, held.Key, back);
                    credited += back;
                }
            }
            else
            {
                foreach (KeyValuePair<string, long> held in fleet.Munitions)
                    if (held.Value > 0) aboard += held.Value;
            }

            if (DebugTraces.Munitions && log != null && aboard > 0)
                log.Add("MUNITIONS_RETURNED " + (fleet.LogId ?? "?") + " " + (fleet.ActorTag ?? "?")
                        + " node=" + (node != null ? node.Id : "none")
                        + " hulls=" + hullsCredited.ToString(CultureInfo.InvariantCulture)
                        + "/" + total.ToString(CultureInfo.InvariantCulture)
                        + " returned=" + credited.ToString(CultureInfo.InvariantCulture)
                        + "/" + aboard.ToString(CultureInfo.InvariantCulture));

            fleet.Munitions.Clear();
            return credited;
        }

        /// <summary>True for a fleet armed from node stock that holds no magazine at all — it fires
        /// nothing. False for a fleet that was never armed, which fires its bands without limit.</summary>
        public static bool IsEmpty(PendingFleetAction fleet)
        {
            if (!fleet.MunitionsArmed || fleet.Munitions == null) return false;
            foreach (KeyValuePair<string, long> held in fleet.Munitions)
                if (held.Value > 0) return false;
            return true;
        }

        /// <summary>Sets a materialised fleet's munitions to what its grids were read to hold,
        /// never above what it had — a live hull cannot hand back rounds it was not given.
        /// A key the read did not find is spent. Returns the magazines the read accounted as
        /// spent.</summary>
        public static long ApplyObserved(ref PendingFleetAction fleet, IDictionary<string, long> observed,
                                         WorldModel world = null)
        {
            if (!fleet.MunitionsArmed || fleet.Munitions == null || fleet.Munitions.Count == 0) return 0;
            long spent = 0;
            var keys = new List<string>(fleet.Munitions.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                long had = fleet.Munitions[keys[i]];
                long seen = 0;
                if (observed != null) observed.TryGetValue(keys[i], out seen);
                long now = seen < 0 ? 0 : Math.Min(had, seen);
                spent += had - now;
                fleet.Munitions[keys[i]] = now;
                CreditSpent(world, fleet, keys[i], had - now);
            }
            return spent;
        }

        /// <summary>Books magazines a fleet fired as consumption at its home node — the node that
        /// supplied them and will be asked to again. Volume-weighted like every other consumption
        /// credit.</summary>
        internal static void CreditSpent(WorldModel world, PendingFleetAction fleet, string key, long spent)
        {
            if (world == null || spent <= 0 || string.IsNullOrEmpty(key)) return;
            string homeId = fleet.ReturnNodeId ?? fleet.OriginNodeId;
            if (string.IsNullOrEmpty(homeId)) return;
            double litres = world.ItemVolumes != null ? world.ItemVolumes.VolumeLitersPerUnit(key) : 0;
            world.AddPendingDemandHistoryCredit(homeId, key, litres > 0 ? spent * litres : spent);
        }
    }

    /// <summary>One side of a duel whose fleets fire only what they carry.
    ///
    /// <para>Each turn is <see cref="FactionTuning.CombatTurnFireSeconds"/> of sustained fire. A
    /// slot in range asks for its magazines for that span; when its fleet holds less of that
    /// magazine than every in-range slot sharing it asks for, each gets the same fraction and fires
    /// at that fraction of its Dps. What a side fires beyond its fleets — a station's own weapons,
    /// a fleet that was never armed — fires without limit.</para>
    ///
    /// <para>Nothing is written to a fleet until <see cref="Commit"/>, so the duel can run to its
    /// end on local counts.</para></summary>
    public sealed class DuelMunitions
    {
        private sealed class Member
        {
            public int Si;
            public int Fi;
            public double Scale;
            public List<MunitionSlot> Slots;
            public Dictionary<string, double> Remaining;
            public Dictionary<string, long> Start;
        }

        private readonly List<Member> _members = new List<Member>();
        private readonly List<WeaponBand> _unlimited = new List<WeaponBand>();
        private readonly double _fireSeconds;
        private readonly Dictionary<string, double> _needScratch = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _fractionScratch = new Dictionary<string, double>(StringComparer.Ordinal);

        private DuelMunitions(double fireSeconds)
        {
            _fireSeconds = fireSeconds;
        }

        /// <summary>The side made of <paramref name="fleets"/>, each fleet's never-armed bands
        /// taken from <paramref name="bandsOf"/>, plus <paramref name="unlimitedBands"/>.
        /// <paramref name="scale"/> multiplies every armed slot's Dps and consumption alike — a
        /// weakened fleet has fewer guns, not emptier ones.
        ///
        /// <para>Null when no fleet on the side is armed, or the mode is off: the caller then fights
        /// on its bands exactly as it did before munitions existed.</para></summary>
        public static DuelMunitions ForSide(IList<FactionState> states, IList<(int si, int fi)> fleets,
                                            Func<PendingFleetAction, List<WeaponBand>> bandsOf,
                                            List<WeaponBand> unlimitedBands, double scale,
                                            WorldModel world, FactionTuning tuning)
        {
            if (states == null || fleets == null || world == null || tuning == null) return null;
            if (tuning.MunitionDrawMode < MunitionDrawModes.Draw) return null;

            var side = new DuelMunitions(tuning.CombatTurnFireSeconds > 0 ? tuning.CombatTurnFireSeconds : 0);
            for (int i = 0; i < fleets.Count; i++)
            {
                PendingFleetAction fleet = states[fleets[i].si].PendingFleets[fleets[i].fi];
                if (!fleet.MunitionsArmed || fleet.Munitions == null)
                {
                    List<WeaponBand> own = bandsOf != null ? bandsOf(fleet) : fleet.WeaponBands;
                    if (own != null) side._unlimited.AddRange(own);
                    continue;
                }
                var member = new Member
                {
                    Si = fleets[i].si,
                    Fi = fleets[i].fi,
                    Scale = scale,
                    Slots = FleetMunitions.SlotsOf(world.PrefabCatalogue, FleetMunitions.Hulls(fleet)),
                    Remaining = new Dictionary<string, double>(StringComparer.Ordinal),
                    Start = new Dictionary<string, long>(StringComparer.Ordinal),
                };
                foreach (KeyValuePair<string, long> held in fleet.Munitions)
                {
                    member.Start[held.Key] = held.Value;
                    member.Remaining[held.Key] = held.Value;
                }
                side._members.Add(member);
                side._unlimited.AddRange(FleetMunitions.SelfSuppliedBands(world.PrefabCatalogue, FleetMunitions.Hulls(fleet), scale));
            }
            if (side._members.Count == 0) return null;
            if (unlimitedBands != null) side._unlimited.AddRange(unlimitedBands);
            return side;
        }

        /// <summary>The bands this side can fire from what its magazines hold now: every unlimited
        /// band, and each armed slot at the share of a turn's fire its magazine type still covers
        /// (every slot of that type firing, as at point blank). A slot whose magazines are spent is
        /// gone. Nothing is spent.</summary>
        public List<WeaponBand> LoadedBands()
        {
            var bands = new List<WeaponBand>(_unlimited);
            for (int m = 0; m < _members.Count; m++)
            {
                Member member = _members[m];
                _needScratch.Clear();
                for (int s = 0; s < member.Slots.Count; s++)
                {
                    MunitionSlot slot = member.Slots[s];
                    double need;
                    _needScratch.TryGetValue(slot.MagazineKey, out need);
                    _needScratch[slot.MagazineKey] = need + slot.MagazinesPerSecond * _fireSeconds * member.Scale;
                }
                for (int s = 0; s < member.Slots.Count; s++)
                {
                    MunitionSlot slot = member.Slots[s];
                    double have, need;
                    member.Remaining.TryGetValue(slot.MagazineKey, out have);
                    _needScratch.TryGetValue(slot.MagazineKey, out need);
                    double fraction = have <= 0 ? 0 : need <= 0 ? 1 : Math.Min(1.0, have / need);
                    if (fraction <= 0) continue;
                    bands.Add(new WeaponBand { Dps = (float)(slot.Dps * member.Scale * fraction), RangeM = (float)slot.RangeM });
                }
            }
            return bands;
        }

        /// <summary>The Dps this side lands in one turn fought at <paramref name="bracketM"/>, and
        /// the magazines that turn spends.</summary>
        public double Fire(double bracketM)
        {
            double dps = CombatStrength.InRangeDps(_unlimited, bracketM);
            for (int m = 0; m < _members.Count; m++)
            {
                Member member = _members[m];
                _needScratch.Clear();
                _fractionScratch.Clear();
                for (int s = 0; s < member.Slots.Count; s++)
                {
                    MunitionSlot slot = member.Slots[s];
                    if (!InRange(slot, bracketM)) continue;
                    double need;
                    _needScratch.TryGetValue(slot.MagazineKey, out need);
                    _needScratch[slot.MagazineKey] = need + slot.MagazinesPerSecond * _fireSeconds * member.Scale;
                }
                foreach (KeyValuePair<string, double> need in _needScratch)
                {
                    double have;
                    member.Remaining.TryGetValue(need.Key, out have);
                    double fraction;
                    if (have <= 0) fraction = 0;
                    else if (need.Value <= 0) fraction = 1;
                    else fraction = Math.Min(1.0, have / need.Value);
                    _fractionScratch[need.Key] = fraction;
                    if (need.Value > 0) member.Remaining[need.Key] = Math.Max(0, have - need.Value * fraction);
                }
                for (int s = 0; s < member.Slots.Count; s++)
                {
                    MunitionSlot slot = member.Slots[s];
                    if (!InRange(slot, bracketM)) continue;
                    double fraction;
                    _fractionScratch.TryGetValue(slot.MagazineKey, out fraction);
                    dps += slot.Dps * member.Scale * fraction;
                }
            }
            return dps;
        }

        /// <summary>Writes each armed fleet's remaining magazines back — a magazine opened in the
        /// fight is spent — and credits what was spent to the fleet's home node's consumption
        /// history. Returns the magazines spent across the side.</summary>
        public long Commit(IList<FactionState> states, WorldModel world, IList<string> log)
        {
            long spentTotal = 0;
            for (int m = 0; m < _members.Count; m++)
            {
                Member member = _members[m];
                PendingFleetAction fleet = states[member.Si].PendingFleets[member.Fi];
                if (fleet.Munitions == null) continue;

                long spentFleet = 0;
                foreach (KeyValuePair<string, long> start in member.Start)
                {
                    double remaining;
                    member.Remaining.TryGetValue(start.Key, out remaining);
                    long left = (long)Math.Floor(remaining + 1e-6);
                    if (left > start.Value) left = start.Value;
                    long spent = start.Value - left;
                    fleet.Munitions[start.Key] = left;
                    if (spent <= 0) continue;
                    spentFleet += spent;
                    FleetMunitions.CreditSpent(world, fleet, start.Key, spent);
                }
                spentTotal += spentFleet;
                if (DebugTraces.Munitions && log != null)
                    log.Add("MUNITIONS_SPENT " + (fleet.LogId ?? "?") + " " + (fleet.ActorTag ?? "?")
                            + " spent=" + spentFleet.ToString(CultureInfo.InvariantCulture)
                            + " left=" + Sum(fleet.Munitions).ToString(CultureInfo.InvariantCulture));
            }
            return spentTotal;
        }

        private static bool InRange(MunitionSlot slot, double bracketM)
        {
            return bracketM <= 0 || slot.RangeM >= bracketM;
        }

        private static long Sum(Dictionary<string, long> munitions)
        {
            long total = 0;
            foreach (KeyValuePair<string, long> held in munitions) if (held.Value > 0) total += held.Value;
            return total;
        }
    }
}
