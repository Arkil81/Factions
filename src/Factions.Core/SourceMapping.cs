using System;

namespace Factions.Core
{
    /// <summary>
    /// Pure (SE-free) policy for turning a detected world grid into a persisted Layer-1 influence
    /// <see cref="SourceDoc"/>. It lives in Core — alongside the sim and tests — so the balance knobs
    /// can be tuned offline, exactly the way the rest of the engine is. The SE adapter
    /// (<c>Factions.WorldScanner</c>) only reads raw grid numbers and feeds them in here.
    /// </summary>
    public static class SourceMapping
    {
        public const int    MinQualifyingBlocks  = 50;     // skip debris / respawn pods / tiny junk
        public const int    FullStrengthBlocks   = 1500;   // block count at/above which strength = 1.0
        public const double DefaultResourceValue = 0.5;    // until a production/cargo signal exists
        public const double MetersPerKm          = 1000.0; // SE world coords are metres; sources are km
        public const double EconomyStationStrength      = 1.0; // an SE trade station is fully operational on discovery
        public const double EconomyStationResourceValue = 0.7; // trade hubs are economically valuable

        /// <summary>Stable, collision-free source id for a grid (distinct from engine "N###" ids).</summary>
        public static string GridSourceId(long entityId) { return "G" + entityId.ToString(); }

        /// <summary>True if an id was produced by <see cref="GridSourceId"/> (a grid-backed source).</summary>
        public static bool IsGridSourceId(string id) { return id != null && id.Length > 1 && id[0] == 'G'; }

        /// <summary>Recover the SE EntityId from a grid source id; false if it is not a grid id.</summary>
        public static bool TryGetEntityId(string id, out long entityId)
        {
            entityId = 0;
            return IsGridSourceId(id) && long.TryParse(id.Substring(1), out entityId);
        }

        /// <summary>
        /// Map a detected grid (raw SE inputs: metre coordinates + block count) to a persisted source.
        /// Position is converted metres→km so distances and <see cref="SourceDoc.Radius"/> share units
        /// for the heatmap's e^(−d/radius); strength scales with size and clamps to 0..1.
        /// </summary>
        public static SourceDoc FromGrid(long entityId, int blockCount, double xMeters, double yMeters, double zMeters,
            string stationPrefabId = null, long? stationBookValue = null,
            System.Collections.Generic.List<WeaponBand> stationWeapons = null)
        {
            return new SourceDoc
            {
                Id = GridSourceId(entityId),
                X = xMeters / MetersPerKm,
                Y = yMeters / MetersPerKm,
                Z = zMeters / MetersPerKm,
                Strength = 1.0,             // real station: fully established on discovery
                ResourceValue = DefaultResourceValue,
                StableStationId = entityId,  // the grid itself is the station; enables Develop
                StationPrefabId = stationPrefabId,
                StationBookValue = stationBookValue,
                StationWeapons = stationWeapons,
                LastReinforcedUtc = System.DateTime.UtcNow,
            };
        }

        /// <summary>Stable source id for an SE economy trade station — distinct from grid "G###" and engine
        /// "N###" ids. These locations exist in the save even before the grid spawns on player discovery.</summary>
        public static string EconomyStationSourceId(long stationId) { return "E" + stationId.ToString(); }

        public static bool IsEconomyStationSourceId(string id) { return id != null && id.Length > 1 && id[0] == 'E'; }

        /// <summary>True if a source id denotes a REAL, scan-discovered asset — a world grid (<c>G###</c>) or an
        /// SE economy station (<c>E###</c>) — as opposed to an engine-invented <c>N###</c> node. A real asset
        /// exists in the save until a re-scan reconciles it, so it is EXEMPT from use-it-or-lose-it hold decay:
        /// it must not fade out of the model between scans just because the AI never reinforced it.</summary>
        public static bool IsScanBacked(string id) { return IsGridSourceId(id) || IsEconomyStationSourceId(id); }

        /// <summary>Map an SE economy station (location from the save; grid not necessarily spawned) to a
        /// source. Per Cornerstone II the asset is real whether or not its grid is materialized, so it gets
        /// full economic weight (type-agnostic defaults for now; tunable offline).</summary>
        public static SourceDoc FromEconomyStation(long stationId, double xMeters, double yMeters, double zMeters,
            string stationPrefabId = null, long? stationBookValue = null,
            System.Collections.Generic.List<WeaponBand> stationWeapons = null)
        {
            return new SourceDoc
            {
                Id = EconomyStationSourceId(stationId),
                X = xMeters / MetersPerKm,
                Y = yMeters / MetersPerKm,
                Z = zMeters / MetersPerKm,
                Strength = EconomyStationStrength,
                ResourceValue = EconomyStationResourceValue,
                StableStationId = stationId,   // embedded so StationMaterializer can find the MyStation
                StationPrefabId = stationPrefabId,
                StationBookValue = stationBookValue,
                StationWeapons = stationWeapons,
                LastReinforcedUtc = System.DateTime.UtcNow,
            };
        }

        /// <summary>Recover the SE <c>MyStation.Id</c> from an economy station source id.
        /// Mirrors the <c>E###</c> encoding used by <see cref="EconomyStationSourceId"/>.</summary>
        public static bool TryGetStationId(string id, out long stationId)
        {
            stationId = 0;
            return IsEconomyStationSourceId(id) && long.TryParse(id.Substring(1), out stationId);
        }
    }
}
