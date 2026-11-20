using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Factions.Core;

namespace Factions.Api
{
    /// <summary>The one thing the dashboard's rebuilt world needs from a prefab catalogue: each
    /// prefab's <see cref="IPrefabCatalogue.Cost"/>, which prices a hull as central-bank collateral.
    /// Read from the <c>prefab_catalogue.json</c> the host writes at session load. Every other member
    /// answers "unknown prefab", which nothing on the snapshot path asks. Without this the dashboard
    /// would price every hull at the flat fallback and report a collateral and target the host never
    /// used.</summary>
    internal sealed class PrefabCostCatalogue : IPrefabCatalogue
    {
        private static readonly IReadOnlyDictionary<string, long> NoTanks = new Dictionary<string, long>();
        private static readonly IReadOnlyList<WeaponBand> NoBands = new List<WeaponBand>();
        private static readonly IReadOnlyList<string> NoNames = new List<string>();
        private static readonly IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> NoThrust =
            new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();

        private readonly Dictionary<string, long> _costs;

        private PrefabCostCatalogue(Dictionary<string, long> costs) { _costs = costs; }

        /// <summary>Null when the file is missing or unreadable, so the caller leaves the world
        /// without a catalogue rather than with an empty one.</summary>
        public static PrefabCostCatalogue? Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var prefabs = JsonSerializer.Deserialize<Dictionary<string, PrefabDto>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (prefabs == null) return null;
                var costs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in prefabs)
                    if (kv.Value != null && kv.Value.Cost > 0) costs[kv.Key] = kv.Value.Cost;
                return new PrefabCostCatalogue(costs);
            }
            catch
            {
                return null;
            }
        }

        public long Cost(string prefabId)
        {
            return prefabId != null && _costs.TryGetValue(prefabId, out long cost) ? cost : 0;
        }

        public long CargoLiters(string prefabId) => 0;
        public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) => NoTanks;
        public string FactionHint(string prefabId) => "All";
        public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => NoBands;
        public string GridSize(string prefabId) => "Unknown";
        public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => NoNames;
        public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => NoThrust;
        public double DryMassKg(string prefabId) => 0;
        public double DrillYieldScore(string prefabId) => 0;
    }
}
