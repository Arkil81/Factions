using Factions.Wire;

namespace FactionsUI;

/// <summary>
/// What a convoy is called, what it is drawn as, and how many hulls that marker stands for.
///
/// <para><b>One place, for the same reason <see cref="NodeLabels"/> is.</b> The map marker, the
/// Assets table and the Assets detail pane all answer "what kind of fleet is this", and two of them
/// used to hold a private copy of the same ordinal switch. A map that calls a convoy "Raid" while
/// the table calls it "Fleet" is the kind of disagreement nobody files and everybody distrusts.</para>
///
/// <para><b>Colour is not in here, deliberately.</b> <see cref="Palette"/> owns that axis and it
/// carries ownership only — own, neutral, enemy. The glyph carries the mission. Keeping the two
/// independent is what lets one glance answer "whose is it" and "what is it doing" separately, and
/// it is why the web dashboard's kind-coloured markers are not mirrored here: on an operator's
/// screen an enemy trade convoy may safely draw green, and in the cockpit it may not.</para>
/// </summary>
internal static class FleetIcons
{
    // ---- Kind ordinals --------------------------------------------------------------------
    // Core ActionKind, as the wire carries it. The four combat kinds are already named in
    // Factions.Wire.FleetEngagement and are used from there rather than redeclared, so a client
    // reading `Kind == 8` still says what 8 is and there is only ever one answer.

    private const byte GuardKind  = 4;
    private const byte TradeKind  = 10;
    private const byte GiftKind   = 12;
    private const byte PatrolKind = 13;
    private const byte FerryKind  = 14;
    private const byte MusterKind = 20;
    private const byte HaulKind   = 21;
    private const byte MineKind   = 22;

    // ---- Mission families -----------------------------------------------------------------

    /// <summary>
    /// The silhouette a fleet marker draws. Twelve mission kinds collapse to six shapes plus a
    /// fallback, because a marker is eighteen pixels across and a player can learn six silhouettes
    /// at that size — not twelve. Kinds are grouped by what a player would *do* about them, which
    /// is the only distinction a tactical map owes them: a Retaliate and a Conquer are both a strike
    /// force inbound, and nothing the player can do differs between the two.
    /// </summary>
    internal enum Glyph
    {
        /// <summary>A convoy whose kind this client has never heard of. Still drawn — a contact the
        /// server knows about and the client cannot name is exactly the one worth seeing.</summary>
        Generic = 0,
        Raid,
        Guard,
        Patrol,
        Trade,
        Haul,
        Mine,
    }

    /// <summary>The glyph for a wire kind ordinal.</summary>
    public static Glyph GlyphFor(byte kind)
    {
        switch (kind)
        {
            case FleetEngagement.RaidKind:
            case FleetEngagement.RetaliateKind:
            case FleetEngagement.ConquerKind:
                return Glyph.Raid;

            case GuardKind:
            case FleetEngagement.ReinforceKind:
                return Glyph.Guard;

            case PatrolKind:
            case MusterKind:
                return Glyph.Patrol;

            case TradeKind:
            case GiftKind:
                return Glyph.Trade;

            case HaulKind:
            case FerryKind:
                return Glyph.Haul;

            case MineKind:
                return Glyph.Mine;

            default:
                return Glyph.Generic;
        }
    }

    /// <summary>The icon name <see cref="IconStore"/> resolves to a file on disk.</summary>
    public static string TextureName(Glyph glyph)
    {
        switch (glyph)
        {
            case Glyph.Raid:   return "fleet_raid";
            case Glyph.Guard:  return "fleet_guard";
            case Glyph.Patrol: return "fleet_patrol";
            case Glyph.Trade:  return "fleet_trade";
            case Glyph.Haul:   return "fleet_haul";
            case Glyph.Mine:   return "fleet_mine";
            default:           return "fleet_generic";
        }
    }

    /// <summary>
    /// What to call a convoy in one word. Each kind keeps its own name even where two share a
    /// glyph: a marker has eighteen pixels and has to generalise, a table cell does not.
    /// </summary>
    public static string Label(byte kind)
    {
        switch (kind)
        {
            case FleetEngagement.ReinforceKind: return "Reinforce";
            case GuardKind:                     return "Guard";
            case FleetEngagement.RetaliateKind: return "Retaliate";
            case FleetEngagement.RaidKind:      return "Raid";
            case TradeKind:                     return "Trade";
            case GiftKind:                      return "Gift";
            case PatrolKind:                    return "Patrol";
            case FerryKind:                     return "Ferry";
            case FleetEngagement.ConquerKind:   return "Conquer";
            case MusterKind:                    return "Muster";
            case HaulKind:                      return "Haul";
            case MineKind:                      return "Mine";
            default:                            return "Fleet";
        }
    }

    // ---- Echelon --------------------------------------------------------------------------

    /// <summary>
    /// One echelon mark, as a fraction of the marker it sits over. Small enough to read as an
    /// annotation on that marker rather than as a second contact beside it, large enough that
    /// three of them in a row are still three at a glance.
    ///
    /// <para><b>These three live here, not in either renderer, because both draw them.</b> The map
    /// and the HUD scanner show the same convoys to the same person seconds apart; marks that were
    /// a different size or spacing on the two would read as two different annotations. They were
    /// private to <c>MapRenderer</c> until the HUD became the second consumer.</para>
    /// </summary>
    public const float EchelonMarkFraction = 0.38f;

    /// <summary>Centre-to-centre spacing of repeated marks, as a fraction of the marker. Wider
    /// than <see cref="EchelonMarkFraction"/>, so two bars never touch and read as one thick
    /// one.</summary>
    public const float EchelonSpacingFraction = 0.46f;

    /// <summary>Clearance between the top of the marker and the bottom of the mark, as a fraction
    /// of the marker.</summary>
    public const float EchelonGapFraction = 0.20f;

    /// <summary>How tall the whole mark band is above the marker's centre, as a fraction of the
    /// marker: the gap, plus the mark itself. What a caller needs to know to put something else
    /// above the marks -- the HUD's contact label, which would otherwise draw through them.</summary>
    public static float EchelonBandFraction(int count)
    {
        return count > 0 ? 0.5f + EchelonGapFraction + EchelonMarkFraction : 0.5f;
    }

    /// <summary>
    /// The icon name for one echelon mark, or null for <see cref="EchelonGlyph.None"/>.
    ///
    /// <para>The ladder itself is <see cref="FleetEchelon"/>'s, in the shared wire source, because
    /// it has to stay band for band with the web dashboard's copy and only there is it under
    /// test. This maps its answer onto the files this client draws.</para>
    /// </summary>
    public static string TextureName(EchelonGlyph glyph)
    {
        switch (glyph)
        {
            case EchelonGlyph.Dot:   return "echelon_dot";
            case EchelonGlyph.Bar:   return "echelon_bar";
            case EchelonGlyph.Cross: return "echelon_cross";
            default:                 return null;
        }
    }
}
