using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;

namespace FactionsUI;

/// <summary>
/// What to call an inventory key on screen.
///
/// <para>A key is a type prefix and a subtype — <c>Ore:Magnesium</c>, <c>Ingot:Magnesium</c>,
/// <c>Comp:SteelPlate</c>. Showing the subtype alone makes the ore and the ingot the same word, and
/// showing the raw key makes every row read like a database field. SE already names both, and names
/// them apart: "Magnesium Ore" against "Magnesium Powder", "Silicon Ore" against "Silicon Wafer".
/// This resolves the key to that name.</para>
///
/// <para><b>One place, deliberately</b>, and the same reasoning as <see cref="NodeLabels"/>: the map
/// of key to definition is also the map any screen needs in reverse to say which key a held item
/// enters node stock under, and two copies of that rule drift into two answers about which key names
/// which subtype.</para>
/// </summary>
internal static class ItemNames
{
    private const string OrePrefix   = "Ore:";
    private const string IngotPrefix = "Ingot:";
    private const string CompPrefix  = "Comp:";
    private const string GasPrefix   = "Gas:";
    private const string SeedPrefix  = "Seed:";

    /// <summary>Built on first use and kept. Null until the definitions are loaded, so an early
    /// caller falls back rather than caching an empty map for the session.</summary>
    private static Dictionary<string, string> _names;

    /// <summary>
    /// The inventory key an SE item is stocked under, or null for anything node stock has no channel
    /// for. The server derives the same key from the same definition and refuses one it does not
    /// recognise, so this decides what a screen OFFERS and never what the server accepts.
    ///
    /// <para>Gas never comes out of here: it has no physical item to find in an inventory in the
    /// first place. <see cref="Describe"/> still names a gas key, because a market row can carry
    /// one.</para>
    /// </summary>
    public static string Key(MyDefinitionId id)
    {
        string typeId = id.TypeId.ToString();
        if (string.IsNullOrEmpty(typeId) || string.IsNullOrEmpty(id.SubtypeName)) return null;
        if (typeId.EndsWith("_Ore", StringComparison.OrdinalIgnoreCase)) return OrePrefix + id.SubtypeName;
        if (typeId.EndsWith("_Ingot", StringComparison.OrdinalIgnoreCase)) return IngotPrefix + id.SubtypeName;
        if (typeId.EndsWith("_Component", StringComparison.OrdinalIgnoreCase)) return CompPrefix + id.SubtypeName;
        if (typeId.EndsWith("_SeedItem", StringComparison.OrdinalIgnoreCase)) return SeedPrefix + id.SubtypeName;
        return null;
    }

    /// <summary>Whether the key is a gas, whose stock is counted in litres.</summary>
    public static bool IsGas(string invKey)
    {
        return invKey != null && invKey.StartsWith(GasPrefix, StringComparison.Ordinal);
    }

    /// <summary>The item's own name, or a qualified subtype for a key SE has no item for.</summary>
    public static string Describe(string invKey)
    {
        if (string.IsNullOrEmpty(invKey)) return "";

        Dictionary<string, string> names = Names();
        string name;
        if (names != null && names.TryGetValue(invKey, out name)) return name;
        return Qualified(invKey);
    }

    /// <summary>
    /// The subtype with its channel spelled out: "Magnesium (ore)".
    ///
    /// <para>Gas always lands here — it has no <c>MyPhysicalItemDefinition</c> to carry a name — and
    /// so does every key until the definitions are loaded. Keeping the channel in the text is what
    /// makes that case a plainer name rather than an ambiguous one.</para>
    /// </summary>
    private static string Qualified(string invKey)
    {
        if (invKey.StartsWith(OrePrefix, StringComparison.OrdinalIgnoreCase))
            return invKey.Substring(OrePrefix.Length) + " (ore)";
        if (invKey.StartsWith(IngotPrefix, StringComparison.OrdinalIgnoreCase))
            return invKey.Substring(IngotPrefix.Length) + " (ingot)";
        if (invKey.StartsWith(CompPrefix, StringComparison.OrdinalIgnoreCase))
            return invKey.Substring(CompPrefix.Length) + " (component)";
        if (invKey.StartsWith(GasPrefix, StringComparison.OrdinalIgnoreCase))
            // The unit is worth spelling out: a market row for gas is quantity and price per
            // kilolitre, which is what SE's store settles, while every other row is per unit.
            return invKey.Substring(GasPrefix.Length) + " (gas, kL)";
        if (invKey.StartsWith(SeedPrefix, StringComparison.OrdinalIgnoreCase))
            return invKey.Substring(SeedPrefix.Length) + " (seed)";
        return invKey;
    }

    private static Dictionary<string, string> Names()
    {
        if (_names != null) return _names;
        if (MyDefinitionManager.Static == null) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MyPhysicalItemDefinition def in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
        {
            if (def == null) continue;
            string key = Key(def.Id);
            if (key == null) continue;
            string name = def.DisplayNameText;
            if (string.IsNullOrEmpty(name)) continue;
            if (!map.ContainsKey(key)) map[key] = name;
        }

        if (map.Count == 0) return null;
        _names = map;
        return _names;
    }
}
