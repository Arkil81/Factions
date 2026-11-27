using System;

namespace Factions.Core
{
    /// <summary>
    /// The text of the GPS waypoint a delivery contract gives the player who accepts it. Vanilla
    /// gives every delivery the same localised name, so two accepted contracts produce identical
    /// markers and the contract screen never says where either one delivers; these names carry the
    /// destination and the item.
    /// </summary>
    public static class ContractWaypoint
    {
        /// <summary>SE's own cap on a GPS name (<c>MyGps.NAME_MAX_LENGTH</c>).</summary>
        public const int NameMaxLength = 32;

        /// <summary><c>place: item</c>, cut to <see cref="NameMaxLength"/>. The place leads, so a cut
        /// shortens the item first. Null when there is no place to name, which leaves the vanilla
        /// name in force.</summary>
        public static string Name(string place, string item)
        {
            if (string.IsNullOrWhiteSpace(place)) return null;
            string p = place.Trim();
            string full = string.IsNullOrWhiteSpace(item) ? p : p + ": " + item.Trim();
            return full.Length <= NameMaxLength ? full : full.Substring(0, NameMaxLength).TrimEnd();
        }

        /// <summary>The marker's description: what to deliver, where, and for whom. A null or empty
        /// <paramref name="faction"/> is left out.</summary>
        public static string Description(string place, string faction, int amount, string item)
        {
            string what = (amount > 0 ? amount + " x " : "") + (string.IsNullOrWhiteSpace(item) ? "items" : item.Trim());
            string to = string.IsNullOrWhiteSpace(place) ? "" : " to " + place.Trim();
            string who = string.IsNullOrWhiteSpace(faction) ? "" : " (" + faction.Trim() + ")";
            return "Deliver " + what + to + who + ".";
        }

        /// <summary>The name to show for a station: the node's own name when it has one, else the
        /// station grid's name, else the owning faction's tag followed by "station".</summary>
        public static string Place(string nodeName, string gridName, string factionTag)
        {
            if (!string.IsNullOrWhiteSpace(nodeName)) return nodeName.Trim();
            if (!string.IsNullOrWhiteSpace(gridName)) return gridName.Trim();
            if (!string.IsNullOrWhiteSpace(factionTag)) return factionTag.Trim() + " station";
            return null;
        }
    }
}
