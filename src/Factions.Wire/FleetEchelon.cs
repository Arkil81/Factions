namespace Factions.Wire
{
    /// <summary>
    /// The mark drawn above a fleet marker. <see cref="None"/> draws nothing.
    /// </summary>
    public enum EchelonGlyph : byte { None = 0, Dot = 1, Bar = 2, Cross = 3 }

    /// <summary>
    /// How many hulls a convoy is, drawn the way a tactical map draws unit strength: as a mark
    /// above the marker, never as a bigger marker. APP-6 puts the echelon field above the frame
    /// for exactly that reason -- the symbol has to stay the size it is, or a dense board stops
    /// being readable.
    ///
    /// <para><b>Why this is here and not in the client.</b> Two surfaces draw it -- the in-game map
    /// and the web dashboard's <c>fleetEchelon.ts</c> -- and they show the same world to the same
    /// people. An operator who has learned that three bars means a dozen hulls on one screen must
    /// not find it means something else on the other. Only one of the two can be C#, so this half
    /// lives beside <see cref="FleetEngagement"/> for the reason that one gives: it is then
    /// exercised by the wire tests instead of only by a live client, and
    /// <c>FleetEchelonTests</c> pins it band for band against the TypeScript.</para>
    ///
    /// <para><b>The size axis was tried first and abandoned.</b> The dashboard scaled the marker
    /// itself, at <c>6 + convoySize * 1.5</c>, which drew a 24-hull squadron at six times the area
    /// of a lone trader, buried the nodes underneath it, and grew again every time the squadron
    /// ceiling moved. The marker is now a constant size on both surfaces and this carries strength
    /// instead.</para>
    /// </summary>
    public static class FleetEchelon
    {
        /// <summary>A mark and how many of it to draw.</summary>
        public struct Mark
        {
            public EchelonGlyph Glyph;
            public int Count;
        }

        /// <summary>
        /// The ladder. It counts hulls exactly while counting is still legible, then bands.
        ///
        /// <list type="table">
        /// <item><term>1</term><description>nothing -- most fleets are one hull, and a mark on
        /// every marker carries no signal</description></item>
        /// <item><term>2, 3</term><description>that many dots; the dot count IS the hull
        /// count</description></item>
        /// <item><term>4-6, 7-11, 12-17</term><description>one, two, three bars</description></item>
        /// <item><term>18+</term><description>a cross. The top band is reachable:
        /// <c>RaidSquadronMaxHulls</c> is 24.</description></item>
        /// </list>
        /// </summary>
        /// <param name="convoySize">Hulls. Zero is a legacy fleet whose size was never written,
        /// and reads as one.</param>
        public static Mark For(int convoySize)
        {
            if (convoySize >= 18) return Of(EchelonGlyph.Cross, 1);
            if (convoySize >= 12) return Of(EchelonGlyph.Bar, 3);
            if (convoySize >= 7) return Of(EchelonGlyph.Bar, 2);
            if (convoySize >= 4) return Of(EchelonGlyph.Bar, 1);
            if (convoySize == 3) return Of(EchelonGlyph.Dot, 3);
            if (convoySize == 2) return Of(EchelonGlyph.Dot, 2);
            return Of(EchelonGlyph.None, 0);
        }

        /// <summary>
        /// The same mark as text, for the surfaces that list a convoy rather than draw it. A
        /// player decodes the map from a detail pane, so the pane shows the mark itself rather
        /// than a name for it -- which is what the dashboard's fleet panel does with this ladder.
        /// </summary>
        public static string Text(int convoySize)
        {
            Mark mark = For(convoySize);
            string glyph;
            switch (mark.Glyph)
            {
                case EchelonGlyph.Dot: glyph = "●"; break;     // BLACK CIRCLE
                case EchelonGlyph.Bar: glyph = "▮"; break;     // BLACK VERTICAL RECTANGLE
                case EchelonGlyph.Cross: glyph = "✕"; break;   // MULTIPLICATION X
                default: return "";
            }

            var sb = new System.Text.StringBuilder(mark.Count);
            for (int i = 0; i < mark.Count; i++) sb.Append(glyph);
            return sb.ToString();
        }

        private static Mark Of(EchelonGlyph glyph, int count)
        {
            var mark = new Mark();
            mark.Glyph = glyph;
            mark.Count = count;
            return mark;
        }
    }
}
