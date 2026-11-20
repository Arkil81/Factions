using System;
using Sandbox.Graphics.GUI;

namespace FactionsUI;

/// <summary>
/// Rebuilding a <see cref="MyGuiControlTable"/> without losing the player's place in it.
///
/// <para><see cref="MyGuiControlTable.Clear"/> drops the selection and every <c>Add</c> re-runs the
/// table's scrollbar layout, which leaves it at the top. A screen that rebuilds its rows on every
/// server snapshot therefore throws the player back to row 0 each time one lands, and a row below
/// the fold can never be kept in view long enough to act on. Two rules avoid that: rebuild only when
/// the content actually changed (<see cref="Changed"/>), and when it did, put the scroll position
/// back (<see cref="PreservingScroll"/>).</para>
/// </summary>
internal static class TableRows
{
    /// <summary>True when <paramref name="signature"/> differs from <paramref name="drawn"/>, which is
    /// then updated to it. The signature is whatever text identifies the rows as drawn: keys, the
    /// numbers shown, their order.</summary>
    internal static bool Changed(ref string drawn, string signature)
    {
        if (string.Equals(drawn, signature, StringComparison.Ordinal)) return false;
        drawn = signature;
        return true;
    }

    /// <summary>Runs <paramref name="rebuild"/> and restores the scroll offset it had before. The
    /// scrollbar clamps the value to the new row count, so a shorter list lands on its last page
    /// rather than past the end.</summary>
    internal static void PreservingScroll(MyGuiControlTable table, Action rebuild)
    {
        float offset = table != null && table.ScrollBar != null ? table.ScrollBar.Value : 0f;
        rebuild();
        if (table != null && table.ScrollBar != null && offset > 0f)
            table.ScrollBar.Value = offset;
    }
}
