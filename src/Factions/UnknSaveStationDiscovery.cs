using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Read-only recovery mapper for the explicit UNKN territory reconciliation. Raw checkpoint
    /// station records do not preserve enough provenance to distinguish vanilla stations from
    /// Factions-founded lazy stations, so this maps only the stable E### IDs explicitly present in
    /// the pre-apply UNKN brain. It never enumerates MyFaction.Stations.
    /// </summary>
    internal static class UnknSaveStationDiscovery
    {
        internal const int MaxCandidates = 64;

        internal sealed class Result
        {
            internal readonly List<SourceDoc> Candidates = new List<SourceDoc>();
            internal readonly List<string> MissingEligibleIds = new List<string>();
            internal string Error;
            internal int RawUnknStationRecords;
            internal int IgnoredRawStationRecords;

            internal bool IsSafe
            {
                get { return string.IsNullOrEmpty(Error) && Candidates.Count <= MaxCandidates; }
            }
        }

        internal static Result Discover(string checkpointPath, IEnumerable<string> eligibleSourceIds,
            Func<string, long> stationPrefabBookValueLookup,
            Func<string, List<WeaponBand>> stationWeaponBandsLookup)
        {
            var result = new Result();
            var eligible = new HashSet<string>(StringComparer.Ordinal);
            if (eligibleSourceIds != null)
                foreach (string id in eligibleSourceIds)
                    if (SourceMapping.IsEconomyStationSourceId(id)) eligible.Add(id);
            if (eligible.Count > MaxCandidates)
            {
                result.Error = "eligible candidate count " + eligible.Count + " exceeds hard limit " + MaxCandidates;
                return result;
            }
            if (string.IsNullOrEmpty(checkpointPath) || !File.Exists(checkpointPath))
            {
                result.Error = "checkpoint missing";
                return result;
            }

            var found = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using (TextReader checkpoint = File.OpenText(checkpointPath))
                foreach (XmlNode station in SandboxStationReader.ReadFactionStations(checkpoint, NeutralFaction.Tag))
                {
                    result.RawUnknStationRecords++;
                    SourceDoc source;
                    if (!TryMapEligibleCandidate(station, eligible, stationPrefabBookValueLookup,
                        stationWeaponBandsLookup, out source))
                    {
                        result.IgnoredRawStationRecords++;
                        continue;
                    }
                    if (found.Add(source.Id)) result.Candidates.Add(source);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }

            foreach (string id in eligible)
                if (!found.Contains(id)) result.MissingEligibleIds.Add(id);
            result.Candidates.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            result.MissingEligibleIds.Sort(StringComparer.Ordinal);
            if (result.Candidates.Count > MaxCandidates)
                result.Error = "candidate count " + result.Candidates.Count + " exceeds hard limit " + MaxCandidates;
            return result;
        }

        private static bool TryMapEligibleCandidate(XmlNode station, ISet<string> eligible,
            Func<string, long> stationPrefabBookValueLookup,
            Func<string, List<WeaponBand>> stationWeaponBandsLookup, out SourceDoc source)
        {
            source = null;
            if (station == null) return false;
            long id;
            if (!long.TryParse(Text(station, "Id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0)
                return false;
            string sourceId = SourceMapping.EconomyStationSourceId(id);
            if (!eligible.Contains(sourceId)) return false;

            double x, y, z;
            if (!TryCoordinate(station, "X", out x) || !TryCoordinate(station, "Y", out y) || !TryCoordinate(station, "Z", out z))
                return false;
            string prefabId = Text(station, "PrefabName");
            long? prefabValue = null;
            if (stationPrefabBookValueLookup != null && !string.IsNullOrEmpty(prefabId))
                prefabValue = stationPrefabBookValueLookup(prefabId);
            List<WeaponBand> weapons = stationWeaponBandsLookup == null || string.IsNullOrEmpty(prefabId)
                ? null : stationWeaponBandsLookup(prefabId);
            source = SourceMapping.FromEconomyStation(id, x, y, z, prefabId, prefabValue, weapons);
            source.DisplayName = Text(station, "Name");
            return true;
        }

        private static string Text(XmlNode node, string name)
        {
            XmlNode child = node.SelectSingleNode("./" + name);
            return child == null ? null : child.InnerText;
        }

        private static bool TryCoordinate(XmlNode station, string axis, out double value)
        {
            value = 0;
            XmlNode position = station.SelectSingleNode("./Position");
            if (position == null || position.Attributes == null) return false;
            XmlAttribute attribute = position.Attributes[axis.ToLowerInvariant()];
            return attribute != null && double.TryParse(attribute.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }
    }
}
