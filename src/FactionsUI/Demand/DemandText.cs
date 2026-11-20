using System;
using Factions.Wire;

namespace FactionsUI.Demand;

/// <summary>
/// How a demand row reads, shared by the station window's Demand tab and the faction Demand window
/// so one demand is described the same way in both.
/// </summary>
internal static class DemandText
{
    /// <summary>The inventory key an <c>Item:</c> kind names, or null for a <c>Ship:</c> demand.</summary>
    internal static string ItemKey(string kind)
    {
        return kind != null && kind.StartsWith("Item:", StringComparison.Ordinal) ? kind.Substring(5) : null;
    }

    /// <summary>The item's name, or "<role> hull to <destination>" for a hull.</summary>
    internal static string Label(string kind, string destination)
    {
        string key = ItemKey(kind);
        if (key != null) return ItemNames.Describe(key);
        if (kind != null && kind.StartsWith("Ship:", StringComparison.Ordinal))
            return kind.Substring(5) + " hull" + (string.IsNullOrEmpty(destination) ? "" : " to " + destination);
        return kind ?? "";
    }

    internal static string Tooltip(string kind, bool destinedForSale)
    {
        return kind + (destinedForSale ? "\nWanted to stock a store." : "");
    }

    /// <summary>The smoothed use per hour, blank when there is none.</summary>
    internal static string Rate(WireNodeDemand d)
    {
        string key = ItemKey(d.Kind);
        if (key == null || d.UsedPerHour <= 0) return "";
        if (d.UsedPerHour < 1) return "<1";
        return Quantities.Item(key, (long)Math.Round(d.UsedPerHour));
    }

    internal static string ClassLabel(WireDemandClass why, string kind)
    {
        switch (why)
        {
            case WireDemandClass.Catalog: return "standing stock";
            case WireDemandClass.StoreStock: return "store stock";
            case WireDemandClass.RecipeInput: return "production";
            case WireDemandClass.Fuel: return "fuel and ammo";
            case WireDemandClass.Install: return "install";
            case WireDemandClass.ShipBuild: return "ship build";
            case WireDemandClass.Upkeep: return "workers' food";
            default: return kind != null && kind.StartsWith("Ship:", StringComparison.Ordinal) ? "fleet" : "";
        }
    }

    internal static string ClassTooltip(WireDemandClass why, string kind)
    {
        switch (why)
        {
            case WireDemandClass.Catalog: return "A standing top-up: stock the faction likes to keep, not something blocked without it.";
            case WireDemandClass.StoreStock: return "Stock for a store listing players can buy.";
            case WireDemandClass.RecipeInput: return "An input a refinery or assembler is short of.";
            case WireDemandClass.Fuel: return "Fuel or ammunition for the hulls that depart from the station.";
            case WireDemandClass.Install: return "Part of the bill for a capability being installed, or a station being founded.";
            case WireDemandClass.ShipBuild: return "Part of the bill for a hull on a shipyard queue.";
            case WireDemandClass.Upkeep: return "Food for the workers living in the station's Habitat, a few hours ahead.";
            default:
                return kind != null && kind.StartsWith("Ship:", StringComparison.Ordinal)
                    ? "A hull of this role is wanted, for a fleet that could not leave without one."
                    : null;
        }
    }

    internal static string EndedTooltip(bool consumed)
    {
        return consumed
            ? "Met: the shipyard delivered it, or hulls already standing at the station covered it."
            : "Lapsed: nothing asked for it again before it expired. Usually the shortfall was "
              + "covered and its poster stopped asking; it can also mean what wanted it stopped.";
    }
}
