using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>The `factions fuel` response: what the model says the world has burned since the
    /// ledger's window opened. A command response, so it may name its own units and say what mode
    /// it is in; it is not a log line.
    ///
    /// <para>Formatted in Core so the host and the Sim print one thing and it can be asserted on
    /// without SE.</para></summary>
    public static class FuelLedgerReport
    {
        /// <summary>Faction and kind rows printed, so one busy world cannot produce a response
        /// nobody can read in a chat window.</summary>
        public const int MaxRows = 12;

        public static string Format(FuelLedgerSnapshot snapshot, DateTime utcNow)
        {
            if (snapshot == null) return "fuel: no ledger.";
            var sb = new StringBuilder(1024);

            TimeSpan window = utcNow - snapshot.SinceUtc;
            if (window < TimeSpan.Zero) window = TimeSpan.Zero;
            int lookups = snapshot.CacheHits + snapshot.CacheMisses;

            sb.Append("fuel: mode=").Append(FuelDrawModes.Name(snapshot.Mode))
              .Append(snapshot.Mode >= FuelDrawModes.Draw
                      ? " (legs draw from the departing node; a shortfall is demanded at the node, and bought only while the import fallback is on)"
                      : " (measure mode draws nothing; the model is priced against live traffic)")
              .Append('\n');
            sb.Append("since ").Append(snapshot.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("Z, window ").Append(Duration(window))
              .Append(" | profile cache hits=").Append(Count(snapshot.CacheHits))
              .Append(" misses=").Append(Count(snapshot.CacheMisses))
              .Append(" entries=").Append(Count(snapshot.CacheEntries))
              .Append(" hit=").Append(lookups > 0 ? Percent((double)snapshot.CacheHits / lookups) : "n/a")
              .Append('\n');
            sb.Append("pricing ").Append(snapshot.PricingMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms total");
            if (snapshot.World.Legs > 0)
                sb.Append(", ").Append((snapshot.PricingMs * 1000.0 / snapshot.World.Legs).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" us/leg");
            sb.Append('\n');

            sb.Append("world  ").Append(TotalsLine(snapshot.World)).Append('\n');
            if (snapshot.World.LegsWithoutBurnData > 0 || snapshot.World.LegsDryMassOnly > 0)
                sb.Append("       unpriced_bands=").Append(Count(snapshot.World.LegsWithoutBurnData))
                  .Append(" legs  dry_mass_only=").Append(Count(snapshot.World.LegsDryMassOnly))
                  .Append(" legs\n");
            AppendDrawLine(sb, snapshot.World);

            AppendRows(sb, "by faction", Rank(snapshot.ByFaction));
            var kinds = new List<KeyValuePair<string, FuelTotals>>();
            foreach (KeyValuePair<ActionKind, FuelTotals> entry in snapshot.ByKind)
                kinds.Add(new KeyValuePair<string, FuelTotals>(entry.Key.ToString(), entry.Value));
            AppendRows(sb, "by kind", Rank(kinds));

            return sb.ToString();
        }

        /// <summary>What the draw actually moved over the window: stock against imports, and the
        /// imported share of the two. docs/fuel-plan.md decision 1 turns the fallback off on that
        /// share, so it is stated rather than left to be derived from two other numbers. Absent
        /// entirely when nothing has been drawn, which is every mode below
        /// <see cref="FuelDrawModes.Draw"/>.</summary>
        private static void AppendDrawLine(StringBuilder sb, FuelTotals world)
        {
            long moved = world.DrawnGasLitres + world.ImportedGasLitres
                       + world.DrawnReactorKg + world.ImportedReactorKg;
            if (moved <= 0 && world.ShortGasLitres <= 0 && world.ShortReactorKg <= 0) return;

            sb.Append("draw   drawn=").Append(Metric(world.DrawnGasLitres)).Append('L')
              .Append(" imported=").Append(Metric(world.ImportedGasLitres)).Append('L')
              .Append(" short=").Append(Metric(world.ShortGasLitres)).Append('L')
              .Append(" rkg=").Append(Metric(world.DrawnReactorKg))
              .Append('/').Append(Metric(world.ImportedReactorKg))
              .Append(" credits=").Append(Metric(world.ImportCredits))
              .Append(" imported_share=").Append(Percent(world.ImportedShare))
              .Append(" short_share=").Append(Percent(world.ShortShare))
              .Append(" rshort_share=").Append(Percent(world.ReactorShortShare))
              .Append('\n');
        }

        private static void AppendRows(StringBuilder sb, string heading, List<KeyValuePair<string, FuelTotals>> rows)
        {
            sb.Append(heading).Append(" (gas litres, descending):");
            if (rows.Count == 0) { sb.Append(" none\n"); return; }
            sb.Append('\n');
            int shown = rows.Count < MaxRows ? rows.Count : MaxRows;
            for (int i = 0; i < shown; i++)
                sb.Append("  ").Append(rows[i].Key.PadRight(12)).Append(TotalsLine(rows[i].Value)).Append('\n');
            if (rows.Count > shown)
                sb.Append("  ... ").Append((rows.Count - shown).ToString(CultureInfo.InvariantCulture))
                  .Append(" more\n");
        }

        private static string TotalsLine(FuelTotals totals)
        {
            var sb = new StringBuilder(128);
            sb.Append("legs=").Append(Count(totals.Legs))
              .Append(" km=").Append(Metric(totals.Km))
              .Append(" gas=").Append(Metric(totals.TotalGasLitres)).Append('L')
              .Append(" hover=").Append(Percent(totals.HoverShare))
              .Append(" rkg=").Append(Metric(totals.ReactorFuelKg))
              .Append(" batt=").Append(Metric(totals.BatteryMwh)).Append("MWh")
              .Append(" unc=").Append(Metric(totals.UncoveredMwh)).Append("MWh");
            if (totals.HasDraw)
                sb.Append(" short=").Append(Percent(totals.ShortShare))
                  .Append(" rshort=").Append(Percent(totals.ReactorShortShare));
            return sb.ToString();
        }

        private static List<KeyValuePair<string, FuelTotals>> Rank(IEnumerable<KeyValuePair<string, FuelTotals>> rows)
        {
            var ranked = new List<KeyValuePair<string, FuelTotals>>(rows);
            ranked.Sort(delegate (KeyValuePair<string, FuelTotals> a, KeyValuePair<string, FuelTotals> b)
            {
                int byGas = b.Value.TotalGasLitres.CompareTo(a.Value.TotalGasLitres);
                if (byGas != 0) return byGas;
                return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return ranked;
        }

        private static string Duration(TimeSpan span)
        {
            if (span.TotalHours >= 1)
                return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h "
                     + span.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (span.TotalMinutes >= 1)
                return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m "
                     + span.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static string Count(long value) { return value.ToString("#,0", CultureInfo.InvariantCulture); }

        private static string Percent(double fraction)
        {
            return (fraction * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>Engineering-prefixed magnitude. The numbers here run from single litres to
        /// 10^11, so a fixed format is unreadable at one end or the other.</summary>
        public static string Metric(double value)
        {
            double magnitude = Math.Abs(value);
            if (magnitude >= 1e12) return (value / 1e12).ToString("0.00", CultureInfo.InvariantCulture) + "T";
            if (magnitude >= 1e9) return (value / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "G";
            if (magnitude >= 1e6) return (value / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (magnitude >= 1e3) return (value / 1e3).ToString("0.00", CultureInfo.InvariantCulture) + "k";
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
