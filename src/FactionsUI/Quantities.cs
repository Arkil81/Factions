using System.Globalization;

namespace FactionsUI;

/// <summary>
/// Large numbers written to fit a table column: in full while short, then stepped by 1000 with a
/// suffix. Shared by the station window's stock columns and the hull table, so one quantity reads
/// the same everywhere. The exact figure belongs in the cell's tooltip.
/// </summary>
internal static class Quantities
{
    internal static readonly string[] CountUnits = { "", "k", "M", "G" };

    /// <summary><paramref name="value"/> in full below <paramref name="firstStep"/>, otherwise divided
    /// by 1000 per suffix, with one decimal under 100: 12,345 -> "12.3k", 1,500 kL -> "1.5 ML".</summary>
    internal static string Scaled(long value, string[] suffixes, long firstStep)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        if (value < firstStep) return value.ToString("N0", invariant) + suffixes[0];
        double v = value;
        int step = 0;
        // 999.5 rather than 1000, so a value that would round to "1000k" is written "1M".
        while (v >= 999.5 && step < suffixes.Length - 1) { v /= 1000.0; step++; }
        return (v < 99.95 ? v.ToString("0.#", invariant) : v.ToString("0", invariant)) + suffixes[step];
    }

    /// <summary>A count with k/M/G past four digits: 4,800 stays, 42,000 -> "42k".</summary>
    internal static string Count(long value)
    {
        return Scaled(value, CountUnits, 10000);
    }

    internal static string Exact(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static readonly string[] GasUnits = { " kL", " ML", " GL" };

    /// <summary>An amount of one inventory key. Gas is held in litres and sold in kilolitres; this
    /// speaks the store's unit and steps up to ML and GL, because a depot's shelf runs to millions of
    /// kilolitres and the columns hold about six characters. The exact figure goes in the tooltip.</summary>
    internal static string Item(string key, long units)
    {
        if (!ItemNames.IsGas(key)) return Count(units);
        if (units > 0 && units < 1000) return "<1 kL";
        return Scaled(units / 1000L, GasUnits, 1000);
    }

    internal static string ItemExact(string key, long units)
    {
        return Exact(units) + (ItemNames.IsGas(key) ? " L" : "");
    }
}
