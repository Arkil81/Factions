using System;
using System.Text;

namespace FactionsUI;

/// <summary>
/// How a node is named and iconed now that <c>NodeKind</c> is gone (FAC-102).
///
/// <para>A node used to be one of eight labels a die picked at creation. It is now what is actually
/// under it and what stands on it, which the wire carries as three fields: the dominant ore, whether
/// the ground has been sampled at all, and a capability bitmask. Every screen has to turn those back
/// into one short human phrase.</para>
///
/// <para><b>One place, deliberately.</b> The map's marker label, the Assets table, the Assets detail
/// pane and the node window all say this, and three of them used to hold their own copy of the same
/// switch. Copies of a label drift, and a map that calls a node "Ore field" while the node window
/// calls it "Iron" is the kind of disagreement nobody files a ticket about and everybody distrusts.</para>
/// </summary>
internal static class NodeLabels
{
    /// <summary>A scanned defence band as the panel and the node window write it: the word and the
    /// pip count the map draws, with the reading's age when it has one. Null when not read.</summary>
    public static string Defence(byte band, int ageSeconds)
    {
        string text;
        switch ((Factions.Wire.WireDefenceBand)band)
        {
            case Factions.Wire.WireDefenceBand.Undefended: text = "undefended (0/3)"; break;
            case Factions.Wire.WireDefenceBand.Light: text = "light (1/3)"; break;
            case Factions.Wire.WireDefenceBand.Contested: text = "contested (2/3)"; break;
            case Factions.Wire.WireDefenceBand.Fortress: text = "fortress (3/3)"; break;
            default: return null;
        }
        return ageSeconds > 0 ? text + ", " + Age(ageSeconds) : text;
    }

    /// <summary>"just now", "12 min ago", "5 h ago", "3 d ago".</summary>
    public static string Age(int seconds)
    {
        if (seconds < 60) return "just now";
        if (seconds < 3600) return (seconds / 60) + " min ago";
        if (seconds < 86400) return (seconds / 3600) + " h ago";
        return (seconds / 86400) + " d ago";
    }

    /// <summary>The map label for a station the viewer has not identified: how developed it looks,
    /// which is all a sensor below Identity has learned. Null for a stage that names nothing.</summary>
    public static string StageWord(byte stage)
    {
        switch ((Factions.Wire.WireNodeStage)stage)
        {
            case Factions.Wire.WireNodeStage.Outpost: return "Outpost";
            case Factions.Wire.WireNodeStage.Settlement: return "Settlement";
            case Factions.Wire.WireNodeStage.Hub: return "Hub";
            case Factions.Wire.WireNodeStage.Capital: return "Capital";
            default: return null;
        }
    }

    /// <summary>Bits in the map's capability mask, matching <c>NodeCapabilityKind</c>'s ordinals.
    /// Assets and node detail carry the named list instead and never need these.</summary>
    [Flags]
    internal enum Capability : ushort
    {
        None         = 0,
        Refinery     = 1 << 0,
        Manufactory  = 1 << 1,
        Shipyard     = 1 << 2,
        FuelDepot    = 1 << 3,
        ManufactoryII= 1 << 4,
        RefineryII   = 1 << 5,
        SensorArray  = 1 << 6,
        PrototechForge = 1 << 7,
        // Sixteen bits since protocol 50.
        PrototechRefinery = 1 << 8,
        Farm          = 1 << 9,
        FoodProcessor = 1 << 10,
        Habitat       = 1 << 11,
    }

    /// <summary>Capabilities that refine or assemble -- the generic industry marker. The food chain
    /// and the two that have their own marker are left out.</summary>
    private const Capability Industry = Capability.Refinery | Capability.Manufactory
        | Capability.ManufactoryII | Capability.RefineryII | Capability.PrototechForge
        | Capability.PrototechRefinery | Capability.SensorArray;

    /// <summary>
    /// What to call a node in one phrase: its ore if it has any, else what stands on it, else what
    /// is known about the ground.
    ///
    /// <para>The unsurveyed case is the reason <c>Surveyed</c> exists on the wire. "Nothing here" and
    /// "nobody has looked" are opposite instructions to a player — do not bother, versus go and
    /// look — and an empty ore string alone cannot tell them apart.</para>
    /// </summary>
    public static string Describe(string dominantOre, bool surveyed, ushort capabilityMask, bool hasStation)
    {
        if (!string.IsNullOrEmpty(dominantOre)) return dominantOre;

        string caps = DescribeCapabilities(capabilityMask);
        if (caps != null) return caps;
        if (hasStation) return "Station";
        return surveyed ? "Barren" : "Unsurveyed";
    }

    /// <summary>The named-capability form, for the two screens that carry the list rather than the
    /// mask. Same wording as the mask form so the two cannot disagree.</summary>
    public static string Describe(string dominantOre, bool surveyed, string[] capabilityNames, bool hasStation)
    {
        if (!string.IsNullOrEmpty(dominantOre)) return dominantOre;

        string caps = MostSignificant(capabilityNames);
        if (caps != null) return caps;
        if (hasStation) return "Station";
        return surveyed ? "Barren" : "Unsurveyed";
    }

    /// <summary>
    /// Capabilities in the order a player cares about finding one, most significant first.
    ///
    /// <para><b>Not ordinal order, and both paths must use this one.</b> The map is told a bitmask
    /// and the two other screens are told a name list, and the name list arrives in whatever order
    /// the server serialised it. Reading its first element would let the same node read "Sensor
    /// array" in the Assets table and "Shipyard" on the map — exactly the disagreement this whole
    /// class exists to prevent, and the first version of it did that.</para>
    /// </summary>
    private static readonly Capability[] BySignificance =
    {
        Capability.Shipyard, Capability.PrototechForge, Capability.PrototechRefinery, Capability.RefineryII,
        Capability.ManufactoryII, Capability.Refinery, Capability.Manufactory, Capability.FuelDepot,
        Capability.FoodProcessor, Capability.Farm, Capability.Habitat, Capability.SensorArray,
    };

    private static readonly string[] BySignificanceLabel =
    {
        "Shipyard", "Prototech forge", "Prototech refinery", "Refinery II",
        "Manufactory II", "Refinery", "Manufactory", "Fuel depot",
        "Food processor", "Farm", "Habitat", "Sensor array",
    };

    /// <summary>The most significant capability in a bitmask, or null for none.</summary>
    private static string DescribeCapabilities(ushort mask)
    {
        var m = (Capability)mask;
        for (int i = 0; i < BySignificance.Length; i++)
            if ((m & BySignificance[i]) != 0) return BySignificanceLabel[i];
        return null;
    }

    /// <summary>The most significant capability in a NAME list, ranked identically to the mask.
    /// An unrecognised name still gets shown rather than dropped -- a capability added server-side
    /// that this client has never heard of should read as itself, not as "Barren".</summary>
    private static string MostSignificant(string[] names)
    {
        if (names == null || names.Length == 0) return null;

        for (int i = 0; i < BySignificance.Length; i++)
        {
            string wanted = BySignificance[i].ToString();
            for (int j = 0; j < names.Length; j++)
                if (string.Equals(names[j], wanted, StringComparison.OrdinalIgnoreCase))
                    return BySignificanceLabel[i];
        }
        return Spaced(names[0]);   // unknown to this client, but real -- show it
    }

    /// <summary>"ManufactoryII" -> "Manufactory II". The server sends enum names; a player reads
    /// prose.</summary>
    private static string Spaced(string enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return enumName;
        var sb = new StringBuilder(enumName.Length + 2);
        for (int i = 0; i < enumName.Length; i++)
        {
            char c = enumName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(enumName[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The marker texture for a node. Ore first, because the ore is why a player goes there.
    ///
    /// <para>Only textures SE already ships are used — a missing texture draws nothing at all, and a
    /// map of invisible markers is worse than a map of approximate ones.</para>
    ///
    /// <para><b>Every one of them is filled art, never a contour.</b> The map draws over the real
    /// world, so a marker has to hold against a lit ring band as well as against black space, and
    /// the inventory-filter glyphs this table used to reach for (<c>filter_ore</c>,
    /// <c>filter_ingot</c>, <c>filter_hydrogen</c>) are outlines with a transparent interior — 7 to
    /// 8 percent of their texels survive an alpha cut, against 34 to 52 for the ore icons beside
    /// them. At an 18 px marker that is a sub-pixel contour, and the relation tint makes it worse
    /// rather than better: Neutral is white, so a neutral node drew as a white outline on
    /// Cauldron's near-white rings (FAC-366).</para>
    /// </summary>
    public static string Texture(string dominantOre, bool surveyed, ushort capabilityMask, bool hasStation)
    {
        if (!string.IsNullOrEmpty(dominantOre))
        {
            if (string.Equals(dominantOre, "Ice", StringComparison.OrdinalIgnoreCase))
                return "Textures\\GUI\\Icons\\ore_H2O_ice.dds";
            if (IsRare(dominantOre))
                return "Textures\\GUI\\Icons\\ore_Pt_platinum.dds";
            // The generic rock rather than a per-ore icon: the marker is tinted by relation, so the
            // silhouette is all that distinguishes one ore from another and at this size it does
            // not. Which ore it is, the label says.
            return "Textures\\GUI\\Icons\\ore_rock.dds";
        }

        var m = (Capability)capabilityMask;
        // A Shipyard and a Refinery were the same dot under the old kind icon. They are not the same
        // thing to a player deciding where to fly, so the mask picks between them.
        if ((m & Capability.Shipyard) != 0) return "Textures\\HUD\\marker_scenario.dds";
        if ((m & (Capability.FuelDepot)) != 0) return "Textures\\GUI\\Icons\\HydrogenIcon.dds";
        if ((m & Industry) != 0) return "Textures\\GUI\\Icons\\ingot\\ingot_class.dds";
        // The blocks' own cube icons, which are rendered art rather than outlines.
        if ((m & Capability.FoodProcessor) != 0) return "Textures\\GUI\\Icons\\Cubes\\food_processor.dds";
        if ((m & Capability.Farm) != 0) return "Textures\\GUI\\Icons\\Cubes\\FarmPlot.dds";
        if (m != Capability.None) return "Textures\\GUI\\Icons\\ingot\\ingot_class.dds";

        if (hasStation) return "Textures\\HUD\\marker_scenario.dds";
        return "Textures\\HUD\\marker_neutral.dds";
    }

    /// <summary>The four ores the rare icon is for. A list rather than a lookup because the client
    /// has no registry and this is the whole set.</summary>
    private static bool IsRare(string ore)
    {
        return string.Equals(ore, "Platinum", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ore, "Gold", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ore, "Silver", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ore, "Uranium", StringComparison.OrdinalIgnoreCase);
    }
}
