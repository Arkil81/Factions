using System;

namespace Factions.Wire
{
    /// <summary>How the station GPS the server hands an arriving player is recognised, one table for
    /// both ends. The server writes it (<c>RespawnPlacement</c>) and retires the previous one on the
    /// next respawn; the client finds it again to aim SE's ingame-help station objective at the same
    /// station (<c>IngameHelpStationPatch</c>). A rule only one end knew would be a marker the client
    /// never finds, and the objective would fall back to SE's own search without anything saying so.
    ///
    /// <para>The GPS is named after the station, because the name is what SE's GPS list shows, so the
    /// marker is recognised by its description. Markers sent before that carry
    /// <see cref="LegacyNamePrefix"/> plus a node id as their name and are still recognised, so the
    /// next respawn retires them too.</para></summary>
    public static class WelcomeGps
    {
        public const string DescriptionPrefix = "Nearest trading station: ";

        public const string LegacyNamePrefix = "Factions-Welcome-";

        public static string Description(string stationName, string ownerTag)
        {
            return DescriptionPrefix + stationName + " [" + ownerTag + "]";
        }

        public static bool IsWelcome(string gpsName, string gpsDescription)
        {
            return (gpsDescription != null && gpsDescription.StartsWith(DescriptionPrefix, StringComparison.Ordinal))
                || (gpsName != null && gpsName.StartsWith(LegacyNamePrefix, StringComparison.Ordinal));
        }
    }
}
