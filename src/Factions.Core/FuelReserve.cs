using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>The demand side of docs/fuel-plan.md: what a founded node asks to hold so the hulls
    /// that leave it can be fuelled from stock rather than bought out of the treasury.
    ///
    /// <para><c>reserve = max(FuelBurnEma × FuelReserveHours, FuelReserveFloor) − stock</c>, posted
    /// as a <c>Need:{fuel}</c> pinned at the node with an <c>Item:{fuel}</c> beside it, the way
    /// every other operational need rides beside its item row. From there it is existing machinery:
    /// <c>MatchVectors</c> hauls it, <c>OperationalNeedFor</c> raises the node's reference stock so
    /// the oracle stops calling fuel a glut, and a demanded output is what admits the gas recipe and
    /// sites a FuelDepot.</para>
    ///
    /// <para><b>A departure buffer rides inside it</b> (<see cref="BufferTarget"/>): burn ×
    /// <see cref="FactionTuning.FuelBufferLeadHours"/>, the stock a node needs to bridge the refill
    /// lead. It is a floor under the reserve, and the one part of the shared <c>Item:</c> row the
    /// catalogue's gas top-up may not write away (<see cref="ProtectBuffer"/>).</para>
    ///
    /// <para><b>Every founded node, not only those holding hulls</b> — decision 6. A node with none
    /// is where a hull lands empty after a delivery; ship stock raises the row's urgency
    /// (<see cref="DemandUrgency.ParkedHullScale"/>) rather than deciding whether it exists.</para>
    ///
    /// <para>Nothing here is keyed on a gas name. The keys come from what the faction's own hulls
    /// burn — <see cref="ThrustBand.FuelId"/> and <see cref="FuelManifest.ReactorFuelKey"/> — so a
    /// modded propellant is a different key and not a new case.</para></summary>
    public static class FuelReserve
    {
        /// <summary>Distinct fuel keys the hulls parked across <paramref name="ownedNodes"/> burn,
        /// plus any key those nodes have already handed out. One list per faction per cycle: every
        /// founded node then asks for the same keys, which is what makes a hull-less node ask at
        /// all.</summary>
        public static List<string> FactionFuelKeys(IEnumerable<SpatialNode> ownedNodes, IPrefabCatalogue catalogue)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (ownedNodes == null) return keys;

            var fuelCatalogue = catalogue as IFuelPrefabCatalogue;
            var prefabsSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (SpatialNode node in ownedNodes)
            {
                if (node == null) continue;
                foreach (KeyValuePair<string, double> burned in node.FuelBurnEma)
                    if (seen.Add(burned.Key)) keys.Add(burned.Key);

                if (catalogue == null) continue;
                foreach (KeyValuePair<string, int> stock in node.ShipStock)
                {
                    if (stock.Value <= 0 || string.IsNullOrEmpty(stock.Key)) continue;
                    if (!prefabsSeen.Add(stock.Key)) continue;
                    AddHullKeys(keys, seen, catalogue, fuelCatalogue, stock.Key);
                }
            }
            return keys;
        }

        private static void AddHullKeys(List<string> keys, HashSet<string> seen, IPrefabCatalogue catalogue,
                                        IFuelPrefabCatalogue fuelCatalogue, string prefabId)
        {
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = catalogue.ThrustBands(prefabId);
            if (bands != null)
            {
                foreach (KeyValuePair<ThrustDirection, IReadOnlyList<ThrustBand>> direction in bands)
                {
                    IReadOnlyList<ThrustBand> list = direction.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        string fuelId = list[i].FuelId;
                        if (!string.IsNullOrEmpty(fuelId) && seen.Add(fuelId)) keys.Add(fuelId);
                    }
                }
            }

            if (fuelCatalogue == null) return;
            FuelManifest manifest = fuelCatalogue.FuelManifest(prefabId);
            if (manifest != null && !string.IsNullOrEmpty(manifest.ReactorFuelKey)
                && seen.Add(manifest.ReactorFuelKey)) keys.Add(manifest.ReactorFuelKey);
        }

        /// <summary>Units of <paramref name="key"/> this node should be standing ready with:
        /// its measured hourly burn over <see cref="FactionTuning.FuelReserveHours"/>, never below
        /// the floor for the key's kind — <see cref="FactionTuning.FuelReserveFloor"/> litres for a
        /// gas, <see cref="FactionTuning.FuelReserveFloorReactorKg"/> kilograms for reactor fuel.
        /// The gross target, before the node's own stock is netted off it.</summary>
        public static long TargetStock(SpatialNode node, string key, DateTime nowUtc, FactionTuning tuning)
        {
            if (node == null || tuning == null) return 0;
            double burn = FuelDraw.BurnRateAt(node, key, nowUtc, tuning);
            double fromBurn = burn * tuning.FuelReserveHours;
            long target = fromBurn > 0 ? (long)Math.Ceiling(fromBurn) : 0;
            // The departure buffer is part of the reserve, never a second row beside it. It binds
            // here only when the lead is set longer than the reserve horizon.
            long buffer = Buffer(burn, key, tuning);
            if (buffer > target) target = buffer;
            long floor = FloorFor(key, tuning);
            return target > floor ? target : floor;
        }

        /// <summary>Units of <paramref name="key"/> a node keeps back from its routable surplus for
        /// its own departures: its departure buffer or the key's reserve floor, whichever is larger
        /// (<see cref="KeepAt"/>), and 0 wherever the reserve pass posts nothing — below
        /// <see cref="FuelDrawModes.Draw"/>, an unfounded node, or a key outside the owner's reserve
        /// keys. A haul, a player haul contract and a trade sale all size a source by what is left, so
        /// fuel delivered into a reserve is not routed straight back out while the node's next
        /// departure flies short.</summary>
        public static long Keep(SpatialNode node, string key, DateTime nowUtc, WorldModel world, FactionTuning tuning)
        {
            if (node == null) return 0;
            return Keeps(world, node.OwnerFactionTag, key, tuning) ? KeepAt(node, key, nowUtc, tuning) : 0;
        }

        /// <summary>The per-key half of <see cref="Keep"/>: whether <paramref name="factionTag"/>'s
        /// nodes keep <paramref name="key"/> back at all. One lookup, so a caller walking every node
        /// for one key asks once and then calls <see cref="KeepAt"/> per node.</summary>
        public static bool Keeps(WorldModel world, string factionTag, string key, FactionTuning tuning)
        {
            if (world == null) return false;
            if (tuning == null || tuning.FuelDrawMode < FuelDrawModes.Draw) return false;
            return world.IsFuelReserveKey(factionTag, key);
        }

        /// <summary>The per-node half of <see cref="Keep"/>, for a key <see cref="Keeps"/> has
        /// admitted: <c>max(buffer, floor)</c>, 0 at a node the reserve pass skips. The buffer is
        /// what the node's next <see cref="FactionTuning.FuelBufferLeadHours"/> of departures burn,
        /// so a node never ships that away. It is deliberately not the whole reserve target
        /// (<see cref="TargetStock"/>, <see cref="FactionTuning.FuelReserveHours"/> of burn): a depot
        /// holds its own output, and keeping six hours of its burn there leaves the rest of the
        /// estate nothing to draw on.</summary>
        public static long KeepAt(SpatialNode node, string key, DateTime nowUtc, FactionTuning tuning)
        {
            if (!PostsReserve(node) || tuning == null) return 0;
            long buffer = Buffer(FuelDraw.BurnRateAt(node, key, nowUtc, tuning), key, tuning);
            long floor = FloorFor(key, tuning);
            return buffer > floor ? buffer : floor;
        }

        /// <summary>Whether the reserve pass posts at <paramref name="node"/>: a founded node only,
        /// since nothing departs from one that is not.</summary>
        private static bool PostsReserve(SpatialNode node)
        {
            return node != null && node.StableStationId != 0;
        }

        /// <summary>Units of <paramref name="key"/> a departure node must hold to bridge the refill
        /// lead: its measured burn per hour times <see cref="FactionTuning.FuelBufferLeadHours"/>,
        /// capped at <see cref="FactionTuning.FuelBufferMaxFloors"/> reserve floors. Gross, before
        /// the node's own stock.
        ///
        /// <para>The draw happens at departure and the refill arrives after it, so a node holding
        /// only what the last delivery brought hands it to the next departure and flies the one
        /// after short. Measured 2026-09-29: short nodes received 107–129% of their hourly
        /// shortfall and stayed short.</para>
        ///
        /// <para><b>0 at a node nothing departs from</b> — no burn, no buffer; the reserve floor
        /// (decision 6) is what such a node asks for. 0 below <see cref="FuelDrawModes.Draw"/>,
        /// where nothing is drawn.</para></summary>
        public static long BufferTarget(SpatialNode node, string key, DateTime nowUtc, FactionTuning tuning)
        {
            if (node == null || tuning == null || string.IsNullOrEmpty(key)) return 0;
            if (tuning.FuelDrawMode < FuelDrawModes.Draw) return 0;
            return Buffer(FuelDraw.BurnRateAt(node, key, nowUtc, tuning), key, tuning);
        }

        private static long Buffer(double burnPerHour, string key, FactionTuning tuning)
        {
            if (burnPerHour <= 0 || tuning.FuelBufferLeadHours <= 0) return 0;
            double buffer = burnPerHour * tuning.FuelBufferLeadHours;
            if (tuning.FuelBufferMaxFloors > 0)
            {
                double cap = tuning.FuelBufferMaxFloors * FloorFor(key, tuning);
                if (cap > 0 && buffer > cap) buffer = cap;
            }
            return buffer > 0 ? (long)Math.Ceiling(buffer) : 0;
        }

        private static long FloorFor(string key, FactionTuning tuning)
        {
            return InventoryKeyMapper.IsGasKey(key) ? tuning.FuelReserveFloor : tuning.FuelReserveFloorReactorKg;
        }

        /// <summary>Relative change in a node's buffer that earns a new <c>FUEL_BUFFER</c> line.</summary>
        public const double BufferTraceChangeFraction = 0.25;

        /// <summary>Whether a buffer moved far enough from the last one traced to be worth a line:
        /// it appeared, it went away, or it moved by <see cref="BufferTraceChangeFraction"/> of
        /// itself. Keeps the trace to state changes at ~450 departure nodes a cycle.</summary>
        public static bool BufferChangedMaterially(long priorTraced, long buffer)
        {
            if (priorTraced <= 0) return buffer > 0;
            if (buffer <= 0) return true;
            return Math.Abs(buffer - priorTraced) >= BufferTraceChangeFraction * priorTraced;
        }

        /// <summary>The <c>FUEL_BUFFER</c> line: a node's burn in units per hour (litres of gas,
        /// kilograms of reactor fuel), the lead it bridges in hours, the buffer that makes, and what
        /// the node holds.</summary>
        public static string FormatBufferLine(string factionTag, string nodeId, string key, double burnPerHour,
                                              double leadHours, long target, long have)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "FUEL_BUFFER " + (factionTag ?? "?")
                 + " node=" + (nodeId ?? "?")
                 + " key=" + (key ?? "?")
                 + " burn=" + burnPerHour.ToString("F0", inv)
                 + " lead=" + leadHours.ToString("0.##", inv)
                 + " target=" + target.ToString(inv)
                 + " have=" + have.ToString(inv);
        }

        /// <summary>The quantity a standing top-up sharing this node's <c>Item:{key}</c> row may
        /// write without shrinking the departure buffer out of it. <c>Item:</c> rows are keyed on
        /// (faction, key, node) and the last poster's quantity stands. The catalogue pass's gas
        /// top-up runs after the reserve and posts <c>2 × reference − stock</c>, a <b>net</b>
        /// quantity that <c>MatchVectors</c> nets a second time, so it can leave a departure node's
        /// row asking for less than its buffer. Below its buffer a departure node keeps at least the
        /// buffer, gross; above it, or with no buffer, the top-up's own quantity stands.</summary>
        public static long ProtectBuffer(SpatialNode node, string key, long stock, long topUpQuantity,
                                         DateTime nowUtc, FactionTuning tuning)
        {
            long buffer = BufferTarget(node, key, nowUtc, tuning);
            return buffer > stock && buffer > topUpQuantity ? buffer : topUpQuantity;
        }

        /// <summary>Posts one founded node's reserve rows and returns how many keys it asked for.
        /// A node already holding its target asks for nothing, so a well-supplied estate goes quiet
        /// instead of standing a row up forever.</summary>
        public static int PostForNode(string factionTag, SpatialNode node, IReadOnlyList<string> keys,
                                      IProductionExecutionContext ctx, FactionTuning tuning,
                                      DateTime nowUtc, DateTime expiresUtc)
        {
            if (node == null || keys == null || keys.Count == 0 || ctx == null || tuning == null) return 0;
            if (!PostsReserve(node)) return 0;   // not founded: nothing departs from it

            int posted = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.IsNullOrEmpty(key)) continue;

                long target = TargetStock(node, key, nowUtc, tuning);
                if (target <= 0) continue;
                long have = ctx.GetNodeInventory(node.Id, key);
                long shortfall = target - have;
                if (shortfall <= 0) continue;

                // The Need: row carries what is still missing, the Item: row beside it the gross
                // want -- MatchVectors nets the destination's stock itself, so a poster that netted
                // there too would halve every fuel haul.
                ctx.PostDemand(DemandEntry.ForNeed(factionTag, key, node.Id, expiresUtc, shortfall,
                                                   DemandClass.Fuel));
                ctx.PostDemand(new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind       = DemandRegister.ItemKind(key),
                    NodeId     = node.Id,
                    ExpiresUtc = expiresUtc,
                    Quantity   = target,
                    Class      = DemandClass.Fuel,
                });
                posted++;
            }
            return posted;
        }
    }
}
