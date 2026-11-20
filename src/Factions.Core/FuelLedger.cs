using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One priced leg, as the meter hands it to a ledger. A value type so a staged
    /// dispatch can buffer it without allocating per leg beyond the <see cref="LegBurn"/> the
    /// pricing already made.</summary>
    public struct FuelLegRecord
    {
        public string FactionTag;
        public ActionKind Kind;
        /// <summary>The dispatching fleet's <see cref="PendingFleetAction.LogId"/>, so a line
        /// written when the record is published — the staged path writes its draw at commit — names
        /// the same fleet the pricing line did.</summary>
        public string LogId;
        /// <summary>The convoy's whole burn: every hull's, summed.</summary>
        public LegBurn Burn;
        /// <summary>Kilometres of the polyline this burn was priced over.</summary>
        public double LegKm;
        /// <summary>Hulls the burn was summed over.</summary>
        public int Hulls;
        /// <summary>Of <see cref="LegBurn.ElectricMwh"/> and its resolved battery/reactor shares,
        /// the part the FSD stretches asked for. The remainder is electric thruster bands.</summary>
        public double FsdMwh;
        public bool ProfileCacheHit;
        /// <summary>No cargo mass was available at the site, so the hulls were priced dry. An
        /// undercount, counted separately rather than folded in.</summary>
        public bool DryMassOnly;

        /// <summary>The node this leg departs from, when the site could name one. Carried on the
        /// record rather than passed beside it so a staged dispatch cycle, which buffers the record
        /// and publishes it at commit, can run the draw against the node the leg actually left —
        /// without the buffer and the draw being two lists that have to stay aligned.
        /// <para>Null at a site with no resolved origin (a clearance waypoint); nothing is drawn
        /// there.</para></summary>
        public string OriginNodeId;

        /// <summary>What <see cref="FuelDraw.TryFuel"/> moved for this leg, or null in every mode
        /// below <see cref="FuelDrawModes.Draw"/>.</summary>
        public FuelDrawResult Draw;
    }

    /// <summary>Where a priced leg is filed. The direct implementation is
    /// <see cref="FuelLedger"/>; <see cref="OperationsStagedWorldEffects"/> implements it as a
    /// buffer so a staged dispatch cycle that is discarded files nothing, and one that commits
    /// files exactly once.</summary>
    public interface IFuelLegSink
    {
        void RecordFuelLeg(FuelLegRecord record);
        /// <summary>The leg's FUEL_DRAWN line, already formatted and already gated. Routed with
        /// the record so a discarded cycle drops both together.</summary>
        void AddFuelLog(string line);
    }

    /// <summary>Running totals for one (faction, kind) bucket, and the shape every rollup uses.
    /// Gas is per fuel key because a modded propellant is a different key, not a new case.</summary>
    public sealed class FuelTotals
    {
        public long Legs;
        /// <summary>Legs whose burn came back with <see cref="LegBurn.HasBurnData"/> false: the
        /// hull's bands carried no consumption data, so the leg burned nothing rather than burning
        /// a guess. An unknown, not a zero.</summary>
        public long LegsWithoutBurnData;
        /// <summary>Legs priced on dry mass alone — see <see cref="FuelLegRecord.DryMassOnly"/>.</summary>
        public long LegsDryMassOnly;
        public long Hulls;
        public double Km;
        public readonly Dictionary<string, double> GasLitres = new Dictionary<string, double>(StringComparer.Ordinal);
        public double HoverGasLitres;
        public double DeltaVGasLitres;
        public double BatteryMwh;
        public double ReactorFuelKg;
        /// <summary>Electricity neither a battery nor a reactor covered. Never a gate in measure
        /// mode; it is the part of the model a hull has no store for.</summary>
        public double UncoveredMwh;
        public double FsdMwh;

        // ---- what the draw actually moved (FuelDrawMode 2 only) ---------------------------------
        /// <summary>Gas units taken out of departing nodes' stock.</summary>
        public long DrawnGasLitres;
        /// <summary>Gas units the credit fallback bought because the stock was short. Its share of
        /// <c>drawn + imported</c> is docs/fuel-plan.md decision 1's acceptance metric: it falls as
        /// the supply chain closes, and a share that stays flat means the fallback must stay on.</summary>
        public long ImportedGasLitres;
        /// <summary>Gas units neither stock nor treasury answered.</summary>
        public long ShortGasLitres;
        public long DrawnReactorKg;
        public long ImportedReactorKg;
        public long ShortReactorKg;
        /// <summary>Credits the fallback spent.</summary>
        public long ImportCredits;

        /// <summary>Imported share of the fuel that actually reached a hull, 0..1.</summary>
        public double ImportedShare
        {
            get
            {
                double reached = DrawnGasLitres + ImportedGasLitres + DrawnReactorKg + ImportedReactorKg;
                return reached > 0 ? (double)(ImportedGasLitres + ImportedReactorKg) / reached : 0;
            }
        }

        /// <summary>Share of the gas departing legs needed that neither stock nor the fallback
        /// supplied, 0..1 — the ramp-up number with the fallback off: it falls as the supply side
        /// catches up with the burn. Litres only; reactor fuel is <see cref="ReactorShortShare"/>,
        /// because a litre and a kilogram do not add and the gas volume would hide it.</summary>
        public double ShortShare
        {
            get
            {
                double needed = DrawnGasLitres + ImportedGasLitres + ShortGasLitres;
                return needed > 0 ? ShortGasLitres / needed : 0;
            }
        }

        /// <summary><see cref="ShortShare"/> for reactor fuel, in kilograms.</summary>
        public double ReactorShortShare
        {
            get
            {
                double needed = DrawnReactorKg + ImportedReactorKg + ShortReactorKg;
                return needed > 0 ? ShortReactorKg / needed : 0;
            }
        }

        /// <summary>True once any leg in the bucket was drawn for, met or not.</summary>
        public bool HasDraw
        {
            get
            {
                return DrawnGasLitres + ImportedGasLitres + ShortGasLitres
                     + DrawnReactorKg + ImportedReactorKg + ShortReactorKg > 0;
            }
        }

        public double TotalGasLitres
        {
            get { double total = 0; foreach (double litres in GasLitres.Values) total += litres; return total; }
        }

        /// <summary>Hover's share of the gas total, 0 when nothing was burned.</summary>
        public double HoverShare
        {
            get
            {
                double gas = HoverGasLitres + DeltaVGasLitres;
                return gas > 0 ? HoverGasLitres / gas : 0;
            }
        }

        public void Add(FuelLegRecord record)
        {
            Legs++;
            Hulls += record.Hulls;
            Km += record.LegKm;
            if (record.DryMassOnly) LegsDryMassOnly++;
            if (record.Draw != null)
            {
                DrawnGasLitres += record.Draw.DrawnGasLitres;
                ImportedGasLitres += record.Draw.BoughtGasLitres;
                ShortGasLitres += record.Draw.ShortGasLitres;
                DrawnReactorKg += record.Draw.DrawnReactorKg;
                ImportedReactorKg += record.Draw.BoughtReactorKg;
                ShortReactorKg += record.Draw.ShortReactorKg;
                ImportCredits += record.Draw.Credits;
            }
            LegBurn burn = record.Burn;
            if (burn == null || !burn.HasBurnData) { LegsWithoutBurnData++; if (burn == null) return; }
            foreach (KeyValuePair<string, double> gas in burn.GasLitres)
            {
                double existing;
                GasLitres.TryGetValue(gas.Key, out existing);
                GasLitres[gas.Key] = existing + gas.Value;
            }
            HoverGasLitres += burn.HoverGasLitres;
            DeltaVGasLitres += burn.DeltaVGasLitres;
            BatteryMwh += burn.BatteryMwh;
            ReactorFuelKg += burn.ReactorFuelKg;
            UncoveredMwh += burn.ElectricMwh;
            FsdMwh += record.FsdMwh;
        }

        /// <summary>Folds another bucket in, for the world and per-faction rollups a snapshot
        /// builds from the per-(faction, kind) buckets.</summary>
        public void Add(FuelTotals other)
        {
            if (other == null) return;
            Legs += other.Legs;
            LegsWithoutBurnData += other.LegsWithoutBurnData;
            LegsDryMassOnly += other.LegsDryMassOnly;
            Hulls += other.Hulls;
            Km += other.Km;
            foreach (KeyValuePair<string, double> gas in other.GasLitres)
            {
                double existing;
                GasLitres.TryGetValue(gas.Key, out existing);
                GasLitres[gas.Key] = existing + gas.Value;
            }
            HoverGasLitres += other.HoverGasLitres;
            DeltaVGasLitres += other.DeltaVGasLitres;
            BatteryMwh += other.BatteryMwh;
            ReactorFuelKg += other.ReactorFuelKg;
            UncoveredMwh += other.UncoveredMwh;
            FsdMwh += other.FsdMwh;
            DrawnGasLitres += other.DrawnGasLitres;
            ImportedGasLitres += other.ImportedGasLitres;
            ShortGasLitres += other.ShortGasLitres;
            DrawnReactorKg += other.DrawnReactorKg;
            ImportedReactorKg += other.ImportedReactorKg;
            ShortReactorKg += other.ShortReactorKg;
            ImportCredits += other.ImportCredits;
        }
    }

    /// <summary>An immutable read of the ledger, for a command response or a report. Built by
    /// <see cref="FuelLedger.Snapshot"/>; nothing in it aliases the live totals.</summary>
    public sealed class FuelLedgerSnapshot
    {
        public DateTime SinceUtc;
        public int Mode;
        public int CacheHits;
        public int CacheMisses;
        public int CacheEntries;
        public double PricingMs;
        public FuelTotals World = new FuelTotals();
        public readonly Dictionary<string, FuelTotals> ByFaction = new Dictionary<string, FuelTotals>(StringComparer.Ordinal);
        public readonly Dictionary<ActionKind, FuelTotals> ByKind = new Dictionary<ActionKind, FuelTotals>();
    }

    /// <summary>What every leg the world flies would burn, totalled per faction and per action
    /// kind. In-memory and never persisted: it is a measuring instrument, restarted with the
    /// process, and <c>factions fuel</c> reads it.
    ///
    /// <para><b>It draws nothing.</b> docs/fuel-plan.md's slice 1 is entered in
    /// <see cref="FuelDrawModes.Measure"/>, where the model is priced against live traffic before
    /// any inventory is touched — the Sim's traffic mix is not the server's, so the balance
    /// question can only be answered from the real dispatch rate.</para>
    ///
    /// <para>Bounded by construction: factions × action kinds × fuel keys, with one bucket each.
    /// Recorded on the thread that owns the world — directly at resolution, and through
    /// <see cref="OperationsStagedWorldEffects"/>'s buffer for the staged dispatch cycles, so a
    /// cycle that is discarded and re-run is counted once.</para></summary>
    public sealed class FuelLedger : IFuelLegSink
    {
        private readonly Dictionary<string, FuelTotals> _buckets = new Dictionary<string, FuelTotals>(StringComparer.Ordinal);
        private readonly List<string> _bucketTags = new List<string>();
        private readonly List<ActionKind> _bucketKinds = new List<ActionKind>();
        private long _pricingTicks;

        public FuelLedger() : this(DateTime.UtcNow) { }

        public FuelLedger(DateTime sinceUtc)
        {
            SinceUtc = sinceUtc;
            Profiles = new LegProfileCache();
        }

        /// <summary>When the current totals started accumulating — process start, or the last
        /// <see cref="Reset"/>.</summary>
        public DateTime SinceUtc { get; private set; }

        /// <summary>The hull-independent leg profiles the meter reuses. Its hit and miss counters
        /// are the acceptance read for the pricing cost, so it lives beside the totals they
        /// explain.</summary>
        public LegProfileCache Profiles { get; private set; }

        /// <summary>Stopwatch ticks spent pricing legs, so a live read can state the cost of the
        /// meter itself rather than inferring it.</summary>
        public void AddPricingTicks(long ticks) { if (ticks > 0) _pricingTicks += ticks; }

        public void RecordFuelLeg(FuelLegRecord record) { Record(record); }

        /// <summary>A <see cref="FuelLedger"/> has no log of its own; the meter writes the line
        /// where it was called from. Present so the ledger can stand in as a sink.</summary>
        public void AddFuelLog(string line) { }

        public void Record(FuelLegRecord record)
        {
            string tag = record.FactionTag ?? "";
            string key = tag + "" + ((int)record.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
            FuelTotals bucket;
            if (!_buckets.TryGetValue(key, out bucket))
            {
                bucket = new FuelTotals();
                _buckets[key] = bucket;
                _bucketTags.Add(tag);
                _bucketKinds.Add(record.Kind);
            }
            bucket.Add(record);
        }

        /// <summary>The convenience form docs/fuel-plan.md names: everything the ledger needs
        /// about one hull-summed leg.</summary>
        public void Record(string factionTag, ActionKind kind, LegBurn burn, double legKm, bool profileCacheHit)
        {
            Record(new FuelLegRecord
            {
                FactionTag = factionTag,
                Kind = kind,
                Burn = burn,
                LegKm = legKm,
                Hulls = 1,
                ProfileCacheHit = profileCacheHit,
            });
        }

        /// <summary>An immutable copy for a reader. Rollups are built here rather than kept live
        /// so the accumulate path stays one dictionary lookup.</summary>
        public FuelLedgerSnapshot Snapshot(int mode)
        {
            var snapshot = new FuelLedgerSnapshot
            {
                SinceUtc = SinceUtc,
                Mode = mode,
                CacheHits = Profiles.Hits,
                CacheMisses = Profiles.Misses,
                CacheEntries = Profiles.Count,
                PricingMs = _pricingTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            };
            int i = 0;
            foreach (KeyValuePair<string, FuelTotals> entry in _buckets)
            {
                string tag = _bucketTags[i];
                ActionKind kind = _bucketKinds[i];
                i++;
                snapshot.World.Add(entry.Value);
                Accumulate(snapshot.ByFaction, tag, entry.Value);
                FuelTotals byKind;
                if (!snapshot.ByKind.TryGetValue(kind, out byKind)) snapshot.ByKind[kind] = byKind = new FuelTotals();
                byKind.Add(entry.Value);
            }
            return snapshot;
        }

        private static void Accumulate(Dictionary<string, FuelTotals> into, string key, FuelTotals value)
        {
            FuelTotals bucket;
            if (!into.TryGetValue(key, out bucket)) into[key] = bucket = new FuelTotals();
            bucket.Add(value);
        }

        /// <summary>Clears the totals, the cache counters and the pricing clock, and restamps
        /// <see cref="SinceUtc"/>. The cached profiles themselves are kept: they describe geometry,
        /// which a reset of the measurement window does not change.</summary>
        public void Reset(DateTime utcNow)
        {
            _buckets.Clear();
            _bucketTags.Clear();
            _bucketKinds.Clear();
            _pricingTicks = 0;
            Profiles.ResetCounters();
            SinceUtc = utcNow;
        }
    }
}
