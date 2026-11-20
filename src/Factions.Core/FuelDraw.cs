using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What one fuel key's draw moved. Quantities are inventory units: litres for a gas
    /// (1 L per unit, docs/fuel-plan.md decision 5) and kilograms for reactor fuel.</summary>
    public struct FuelKeyDraw
    {
        public string Key;
        /// <summary>Whole units the leg drew for: its burn plus the node's carried fraction, rounded
        /// down (<see cref="SpatialNode.FuelDrawCarry"/>).</summary>
        public long Needed;
        /// <summary>Units taken from the departing node's stock.</summary>
        public long Drawn;
        /// <summary>Units the credit fallback bought because the stock was short.</summary>
        public long Bought;
        /// <summary>Units neither answered: the fallback is off, or the faction could not pay for
        /// them either. The hull flies without them.</summary>
        public long Short;
        /// <summary>Credits the purchase cost, at <c>price × FuelImportPremium</c>.</summary>
        public long Credits;
        /// <summary>True for a propellant key, false for reactor fuel — the two are counted apart
        /// because a litre and a kilogram do not add.</summary>
        public bool IsGas;
    }

    /// <summary>Everything one leg's draw moved, per key and in total.</summary>
    public sealed class FuelDrawResult
    {
        public readonly List<FuelKeyDraw> Keys = new List<FuelKeyDraw>(2);

        public long DrawnGasLitres;
        public long BoughtGasLitres;
        public long ShortGasLitres;
        public long DrawnReactorKg;
        public long BoughtReactorKg;
        public long ShortReactorKg;
        public long Credits;
        /// <summary>Keys whose shortfall was posted as demand at the departing node
        /// (<see cref="FactionTuning.FuelShortfallDemandMinutes"/>).</summary>
        public int DemandedKeys;

        /// <summary>True when anything at all moved — stock, credits, or a recorded shortfall. The
        /// gate on writing a line or a burn estimate, so a leg that needed nothing is silent.</summary>
        public bool Any
        {
            get { return Keys.Count > 0; }
        }

        /// <summary>The imported share of what actually reached the hull, 0..1 — docs/fuel-plan.md's
        /// decision 1 acceptance metric. It falls as the supply chain closes, because the fallback
        /// only ever covers the shortfall.</summary>
        public double ImportedShare
        {
            get
            {
                double reached = DrawnGasLitres + BoughtGasLitres + DrawnReactorKg + BoughtReactorKg;
                return reached > 0 ? (BoughtGasLitres + BoughtReactorKg) / reached : 0;
            }
        }
    }

    /// <summary>Takes a leg's burn out of the departing node, and buys what the node could not
    /// supply. docs/fuel-plan.md's "Refuel at departure, from node stock": a hull leaves full, and
    /// what the trip needs is drawn from the node it leaves at every <em>owned</em> stop.
    ///
    /// <para><b>Nothing here runs below <see cref="FuelDrawModes.Draw"/>.</b> Measure mode prices
    /// the leg and stops; this is the half that touches the world, and it is behind the mode an
    /// operator selects because at the measured burn — ~394 GL of hydrogen a day network-wide — an
    /// armed draw is a balance decision, not a default.</para>
    ///
    /// <para>SE-free, and it writes no log: the caller formats the line where it has the fleet in
    /// hand, so a staged dispatch cycle that is discarded drops the line with the draw.</para></summary>
    public static class FuelDraw
    {
        /// <summary>Below this the burn estimate is dropped rather than kept as a near-zero double,
        /// so an idle node's map empties and leaves the brain file. Matches the reasoning behind
        /// <c>OperationsManager</c>'s DemandHistory prune floor.</summary>
        public const double BurnEmaPruneBelow = 0.5;

        /// <summary>Takes what <paramref name="burn"/> costs out of <paramref name="node"/>, buys
        /// the shortfall through <paramref name="st"/>'s treasury when the fallback is on, posts
        /// what is still missing as demand at the node, and credits the leg's burn to the node's
        /// burn estimate and what was drawn to its consumption history.
        ///
        /// <para>Electricity is already resolved by <see cref="FuelBurn.ForLeg"/>: the battery's
        /// share is free at an owned node because station power is not modelled, and only the
        /// reactor's share reaches stock. An uncovered remainder is never a gate — the abstract
        /// fleet is modelled as carrying the drive whatever the prefab holds.</para>
        ///
        /// <para>Returns an empty result, having changed nothing, at a node this faction does not
        /// own: buying at a foreign node's store is slice 3, and a battery is only free at home.</para></summary>
        public static FuelDrawResult TryFuel(SpatialNode node, LegBurn burn, FactionState st,
                                             WorldModel world, FactionTuning tuning)
        {
            return TryFuel(node, burn, st != null ? st.Tag : null, world, tuning);
        }

        /// <summary>The same, for a site that has the departing faction's tag but not its state —
        /// every dispatch site, which knows only <see cref="PendingFleetAction.ActorTag"/>.</summary>
        public static FuelDrawResult TryFuel(SpatialNode node, LegBurn burn, string tag,
                                             WorldModel world, FactionTuning tuning)
        {
            if (node == null || burn == null || world == null || tuning == null) return Empty;
            if (tuning.FuelDrawMode < FuelDrawModes.Draw) return Empty;
            if (string.IsNullOrEmpty(tag)) return Empty;
            if (!string.Equals(node.OwnerFactionTag, tag, StringComparison.Ordinal)) return Empty;

            var result = new FuelDrawResult();
            foreach (KeyValuePair<string, double> gas in burn.GasLitres)
                Take(result, node, world, tuning, tag, gas.Key, gas.Value, true);
            if (burn.ReactorFuelKg > 0 && !string.IsNullOrEmpty(burn.ReactorFuelKey))
                Take(result, node, world, tuning, tag, burn.ReactorFuelKey, burn.ReactorFuelKg, false);

            return result;
        }

        /// <summary>The shared do-nothing result. Mode 1 reaches this on every leg, so it must not
        /// allocate.</summary>
        public static readonly FuelDrawResult Empty = new FuelDrawResult();

        private static void Take(FuelDrawResult result, SpatialNode node, WorldModel world,
                                 FactionTuning tuning, string tag, string key, double quantity, bool isGas)
        {
            if (string.IsNullOrEmpty(key) || quantity <= 0) return;

            // The burn estimate is what the leg needed, met or not. A hull flies short rather than
            // waiting, so the need is the node's real departure rate; the reserve and the depot
            // sizing both read it, and a dry node that counted only what it handed out would size
            // both toward zero exactly where fuel is missing.
            CreditBurn(node, world.UtcNow, tuning, key, quantity);

            // Whole units are drawn and the fraction is carried to the node's next departure, so
            // over many legs the draw equals the burn. Rounding each leg up would draw a whole
            // kilogram for a leg that burned 0.02 kg.
            double carry;
            node.FuelDrawCarry.TryGetValue(key, out carry);
            double owed = quantity + carry;
            long needed = (long)Math.Floor(owed);
            node.FuelDrawCarry[key] = owed - needed;
            if (needed <= 0) return;

            long have;
            node.Inventory.TryGetValue(key, out have);
            long drawn = have <= 0 ? 0 : (have < needed ? have : needed);
            if (drawn > 0)
            {
                world.AddNodeInventory(node.Id, key, -drawn);
                // A draw is a consumption event, fed the way a recipe input is -- consumption-fed,
                // never delivery-fed (docs/material-vectors.md's DemandHistory addendum). Gas is one
                // litre per unit, so the unit count is already the volume weighting.
                world.AddPendingDemandHistoryCredit(node.Id, key, drawn);
            }

            long shortfall = needed - drawn;
            long bought = 0, credits = 0;
            if (shortfall > 0 && tuning.FuelImportPremium > 0)
            {
                long unitPrice = ImportUnitPrice(world, tuning, key);
                long treasury = world.GetTreasury(tag);
                if (unitPrice > 0 && treasury > 0)
                {
                    // A wallet cannot go negative, so a faction that cannot pay for the whole
                    // shortfall buys what it can afford and flies short of the rest. A failed debit
                    // must never read as free fuel.
                    long affordable = ItemCredits.Affordable(key, treasury, unitPrice);
                    bought = affordable < shortfall ? affordable : shortfall;
                    if (bought > 0)
                    {
                        credits = ItemCredits.For(key, bought, unitPrice);
                        world.Spend(tag, credits);
                    }
                }
            }

            bool demanded = false;
            long missing = shortfall - bought;
            if (missing > 0 && tuning.FuelShortfallDemandMinutes > 0)
                demanded = ShortfallDemand.Post(world.Demand, tag, node.Id, key, needed, missing,
                                                world.UtcNow.AddMinutes(tuning.FuelShortfallDemandMinutes));
            if (demanded) result.DemandedKeys++;

            result.Keys.Add(new FuelKeyDraw
            {
                Key = key,
                Needed = needed,
                Drawn = drawn,
                Bought = bought,
                Short = shortfall - bought,
                Credits = credits,
                IsGas = isGas,
            });
            result.Credits += credits;
            if (isGas)
            {
                result.DrawnGasLitres += drawn;
                result.BoughtGasLitres += bought;
                result.ShortGasLitres += shortfall - bought;
            }
            else
            {
                result.DrawnReactorKg += drawn;
                result.BoughtReactorKg += bought;
                result.ShortReactorKg += shortfall - bought;
            }
        }

        /// <summary>What one <b>priced</b> unit of <paramref name="key"/> costs the treasury when it
        /// is imported: the item's own price times <see cref="FactionTuning.FuelImportPremium"/>,
        /// never below 1. The premium is what keeps importing dearer than hauling, so the imported
        /// share falls as the chain closes rather than settling wherever it started.
        ///
        /// <para>A priced unit of gas is a kilolitre while a burn is in litres, so the two meet
        /// through <see cref="ItemCredits"/> and never by a bare multiplication.</para></summary>
        public static long ImportUnitPrice(WorldModel world, FactionTuning tuning, string key)
        {
            if (world == null || tuning == null || string.IsNullOrEmpty(key)) return 0;
            if (tuning.FuelImportPremium <= 0) return 0;
            long basePrice = world.ItemUnitValue(ItemPriceResolver.StripInventoryPrefix(key));
            if (basePrice <= 0) basePrice = 1;
            long price = (long)Math.Ceiling(basePrice * tuning.FuelImportPremium);
            return price > 0 ? price : 1;
        }

        // ---- the burn estimate that sizes a node's reserve -------------------------------------

        /// <summary>Folds <paramref name="units"/> of <paramref name="key"/> into
        /// <paramref name="node"/>'s <see cref="SpatialNode.FuelBurnEma"/> as a rate: the stored
        /// value decays by <c>e</c> over <see cref="FactionTuning.FuelBurnEmaHours"/> and each draw
        /// adds <c>units ÷ that span</c>, so a node drawing steadily settles at its real units per
        /// hour and one that stops falls away instead of holding its lifetime total.</summary>
        public static void CreditBurn(SpatialNode node, DateTime nowUtc, FactionTuning tuning,
                                      string key, double units)
        {
            if (node == null || tuning == null || string.IsNullOrEmpty(key) || units <= 0) return;
            double tauHours = tuning.FuelBurnEmaHours;
            if (tauHours <= 0) return;

            Decay(node, nowUtc, tauHours);
            double prior;
            node.FuelBurnEma.TryGetValue(key, out prior);
            node.FuelBurnEma[key] = prior + units / tauHours;
            node.FuelBurnEmaAtUtc = nowUtc;
        }

        /// <summary>Folds a leg's modelled burn into <paramref name="node"/>'s rate estimate without
        /// taking anything from it — what measure mode does in place of a draw.
        ///
        /// <para>The rate has to exist before the draw is armed. Depot capacity is sized from it
        /// (<c>ProductionManager</c>'s gas stage), and a faction cannot be asked to build the supply
        /// side for a burn it has no measurement of. Refuses at <see cref="FuelDrawModes.Draw"/> and
        /// above, where <see cref="TryFuel"/> credits the units it actually moved.</para></summary>
        public static void MeterBurn(SpatialNode node, LegBurn burn, DateTime nowUtc, FactionTuning tuning)
        {
            if (node == null || burn == null || tuning == null) return;
            if (tuning.FuelDrawMode >= FuelDrawModes.Draw) return;

            foreach (KeyValuePair<string, double> gas in burn.GasLitres)
                CreditBurn(node, nowUtc, tuning, gas.Key, gas.Value);
            if (!string.IsNullOrEmpty(burn.ReactorFuelKey) && burn.ReactorFuelKg > 0)
                CreditBurn(node, nowUtc, tuning, burn.ReactorFuelKey, burn.ReactorFuelKg);
        }

        /// <summary>The burn estimate for one key as of <paramref name="nowUtc"/>, in units per
        /// hour. Derived on read, the way <see cref="SpatialNode.ZoneIntegrity"/> and ship activity
        /// are, so nothing has to tick it.</summary>
        public static double BurnRateAt(SpatialNode node, string key, DateTime nowUtc, FactionTuning tuning)
        {
            if (node == null || tuning == null || string.IsNullOrEmpty(key)) return 0;
            double stored;
            if (!node.FuelBurnEma.TryGetValue(key, out stored) || stored <= 0) return 0;
            double decayed = stored * DecayFactor(node.FuelBurnEmaAtUtc, nowUtc, tuning.FuelBurnEmaHours);
            return decayed > 0 ? decayed : 0;
        }

        private static void Decay(SpatialNode node, DateTime nowUtc, double tauHours)
        {
            if (node.FuelBurnEma.Count == 0) return;
            double factor = DecayFactor(node.FuelBurnEmaAtUtc, nowUtc, tauHours);
            if (factor >= 1.0) return;

            var keys = new List<string>(node.FuelBurnEma.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                double next = node.FuelBurnEma[keys[i]] * factor;
                if (next < BurnEmaPruneBelow) node.FuelBurnEma.Remove(keys[i]);
                else node.FuelBurnEma[keys[i]] = next;
            }
        }

        private static double DecayFactor(DateTime sinceUtc, DateTime nowUtc, double tauHours)
        {
            if (tauHours <= 0 || sinceUtc == default(DateTime)) return 1.0;
            double hours = (nowUtc - sinceUtc).TotalHours;
            if (hours <= 0) return 1.0;           // also covers a clock that went backwards on reload
            return Math.Exp(-hours / tauHours);
        }

        /// <summary>Hulls parked at <paramref name="node"/>. Read by <see cref="DemandUrgency"/> to
        /// rank a dry shipyard above a dry outpost, and by the reserve poster.</summary>
        public static int ParkedHulls(SpatialNode node)
        {
            if (node == null || node.ShipStock.Count == 0) return 0;
            int total = 0;
            foreach (KeyValuePair<string, int> stock in node.ShipStock)
                if (stock.Value > 0) total += stock.Value;
            return total;
        }
    }
}
