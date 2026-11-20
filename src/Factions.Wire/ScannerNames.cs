using System;

namespace Factions.Wire
{
    /// <summary>
    /// Whether the scanner HUD may draw a node's name. Asked by the client only while Alt is held,
    /// SE's own gesture for more detail; an icon does not carry text on its own. FAC-143.
    ///
    /// <para><b>A GPS already on the HUD wins.</b> A player who has a marker for a station sees
    /// its name drawn by SE at the same spot, and the scanner's copy would overprint it. Matched on
    /// the text SE draws (<see cref="HudText"/>), not the GPS's internal name: the welcome marker
    /// is named <c>Factions-Welcome-&lt;node&gt;</c> and displays the station's name.</para>
    /// </summary>
    public static class ScannerNames
    {
        /// <summary>What SE's HUD writes beside a GPS: its display name, or its name when that is
        /// empty (<c>MyHudMarkerRender</c>).</summary>
        public static string HudText(string gpsName, string gpsDisplayName)
        {
            return string.IsNullOrEmpty(gpsDisplayName) ? gpsName : gpsDisplayName;
        }

        /// <summary>True when <paramref name="hudText"/> already says <paramref name="nodeName"/>:
        /// the bare name, or the map's disambiguated <c>"&lt;name&gt; (&lt;TAG&gt;)"</c>, which is
        /// what the map's GPS button names a marker for one of the shared station names.</summary>
        public static bool GpsShowsName(string hudText, string nodeName)
        {
            if (string.IsNullOrEmpty(hudText) || string.IsNullOrEmpty(nodeName)) return false;
            if (string.Equals(hudText, nodeName, StringComparison.Ordinal)) return true;
            return hudText.Length > nodeName.Length + 3
                && hudText.StartsWith(nodeName + " (", StringComparison.Ordinal)
                && hudText[hudText.Length - 1] == ')';
        }
    }
}
