using VRageMath;

namespace FactionsUI;

/// <summary>Colours shared by every FactionsUI surface, so the scanner HUD and the map answer
/// "whose is it" the same way.</summary>
internal static class Palette
{
    /// <summary>Colour carries ownership, never kind — the icon carries kind. Keeping the two
    /// axes independent is what lets a glance answer "whose is it" and "what is it" separately.</summary>
    public static Color RelationColour(byte relation)
    {
        switch (relation)
        {
            case 1: return new Color(90, 160, 255);    // Own
            case 2: return Color.White;                // Neutral
            case 3: return new Color(230, 60, 60);     // Enemy
            default: return Color.Gray;                // Unknown / claimable
        }
    }

    /// <summary>Relation colour, dimmed when the node is remembered rather than currently detected.
    ///
    /// <para>Dimming rather than recolouring is deliberate: a remembered node's *identity* is still
    /// trustworthy -- position and kind cannot go stale for something that does not move -- so it
    /// should read as the same node seen less clearly, not as a different category. What may be
    /// stale is who holds it, which is exactly what a washed-out version of the relation colour
    /// communicates.</para></summary>
    public static Color KnowledgeColour(byte relation, bool detected)
    {
        Color live = RelationColour(relation);
        if (detected) return live;
        // Toward grey rather than toward black: a darkened colour reads as a different faction on a
        // black starfield, while a desaturated one reads as the same one, unclear.
        const float Keep = 0.45f;
        byte grey = 70;
        return new Color(
            (byte)(live.R * Keep + grey * (1f - Keep)),
            (byte)(live.G * Keep + grey * (1f - Keep)),
            (byte)(live.B * Keep + grey * (1f - Keep)),
            (byte)190);
    }
}
