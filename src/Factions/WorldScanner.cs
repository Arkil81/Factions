using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// SE adapter: scans the loaded world for static, NPC-faction-owned station grids and maps each to
    /// a Layer-1 <see cref="SourceDoc"/> via the SE-free <see cref="SourceMapping"/>. Read-only over the
    /// world. MUST run on the game thread — entity enumeration is not thread-safe (callers marshal via
    /// <c>MyAPIGateway.Utilities.InvokeOnGameThread</c>).
    /// </summary>
    public static class WorldScanner
    {
        /// <summary>Result of one world pass: qualifying station sources bucketed by registered tag,
        /// plus counters for the admin summary.</summary>
        public sealed class ScanData
        {
            public readonly Dictionary<string, List<SourceDoc>> ByFaction =
                new Dictionary<string, List<SourceDoc>>(StringComparer.OrdinalIgnoreCase);
            public int GridsExamined;       // cube grids seen
            public int QualifyingStations;  // static, non-projected, big-enough, NPC-owned
            public int UnregisteredGrids;   // qualifying stations whose NPC faction is not in the registry
            public int EconomyStationsSeen;       // SE economy stations on registered NPC factions
            public int EconomyStationsUnspawned;  // ...not yet a live grid -> added as sources here
        }

        /// <summary>Enumerate world grids and bucket qualifying NPC stations by registered faction tag.</summary>
        public static ScanData Scan(ICollection<string> registeredTags, Func<string, long> stationPrefabBookValueLookup = null,
            Func<string, List<WeaponBand>> stationWeaponBandsLookup = null)
        {
            var data = new ScanData();
            if (MySession.Static == null || MySession.Static.Factions == null) return data;

            var registered = new HashSet<string>(registeredTags ?? new string[0], StringComparer.OrdinalIgnoreCase);

            MyAPIGateway.Entities.GetEntities(null, e =>
            {
                var grid = e as MyCubeGrid;
                if (grid == null) return false;
                data.GridsExamined++;

                // Static stations only: skip ships, projected/preview grids, and debris-sized grids.
                if (!grid.IsStatic || grid.Projector != null) return false;
                if (grid.BlocksCount < SourceMapping.MinQualifyingBlocks) return false;

                MyFaction faction = GetOwningNpcFaction(grid);
                if (faction == null) return false;
                data.QualifyingStations++;

                if (!registered.Contains(faction.Tag)) { data.UnregisteredGrids++; return false; }

                Vector3D pos = grid.PositionComp.GetPosition();
                string prefabId = GetStationPrefabName(faction, grid.EntityId);
                long? prefabValue = GetBookValue(stationPrefabBookValueLookup, prefabId);
                List<WeaponBand> weapons = GetWeaponBands(stationWeaponBandsLookup, prefabId);

                // If this grid is a spawned SE economy station, use the stable E<id> identity
                // so the node survives despawn/respawn cycles without changing its key.
                long econId = GetEconomyStationId(faction, grid.EntityId);
                SourceDoc src = econId != 0L
                    ? SourceMapping.FromEconomyStation(econId, pos.X, pos.Y, pos.Z, prefabId, prefabValue, weapons)
                    : SourceMapping.FromGrid(grid.EntityId, grid.BlocksCount, pos.X, pos.Y, pos.Z, prefabId, prefabValue, weapons);
                src.DisplayName = grid.DisplayName;

                List<SourceDoc> list;
                if (!data.ByFaction.TryGetValue(faction.Tag, out list))
                {
                    list = new List<SourceDoc>();
                    data.ByFaction[faction.Tag] = list;
                }
                list.Add(src);
                return false; // we side-effect into ByFaction; don't collect into the (null) output set
            });

            // SE economy trade stations: their LOCATIONS live in the save (per faction) but the grid does
            // not spawn until a player gets near, so the grid pass above misses the undiscovered ones. Add
            // those straight from each faction's station list (Cornerstone II: the asset is real even when
            // unmaterialized). Spawned ones (StationEntityId != 0) are already covered by the grid pass.
            if (MySession.Static.Settings.EnableEconomy)
            {
                foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                {
                    MyFaction f = kv.Value;
                    if (f == null || !FactionsPlugin.IsEffectivelyNpc(f) || !registered.Contains(f.Tag)) continue;
                    foreach (var station in f.Stations)   // var: type inferred from f.Stations (avoids namespace lookup)
                    {
                        data.EconomyStationsSeen++;
                        if (station.StationEntityId != 0L) continue;   // spawned -> grid pass already added it as E<id>
                        Vector3D p = station.Position;
                        long? prefabValue = GetBookValue(stationPrefabBookValueLookup, station.PrefabName);
                        List<WeaponBand> weapons2 = GetWeaponBands(stationWeaponBandsLookup, station.PrefabName);
                        SourceDoc src2 = SourceMapping.FromEconomyStation(station.Id, p.X, p.Y, p.Z,
                            station.PrefabName, prefabValue, weapons2);
                        src2.DisplayName = station.Name;
                        List<SourceDoc> list2;
                        if (!data.ByFaction.TryGetValue(f.Tag, out list2))
                        {
                            list2 = new List<SourceDoc>();
                            data.ByFaction[f.Tag] = list2;
                        }
                        list2.Add(src2);
                        data.EconomyStationsUnspawned++;
                    }
                }
            }

            return data;
        }

        /// <summary>
        /// Resolve a grid's owning faction, but only when that faction is an NPC faction. Uses the
        /// BigOwners first-non-zero idiom shared by GroupPlugin's GridExtensions / CrunchEcon's FacUtils.
        /// </summary>
        private static MyFaction GetOwningNpcFaction(MyCubeGrid grid)
        {
            List<long> owners = grid.BigOwners;
            long ownerId = 0L;
            if (owners != null && owners.Count > 0 && owners[0] != 0L) ownerId = owners[0];
            else if (owners != null && owners.Count > 1) ownerId = owners[1];
            if (ownerId == 0L) return null;

            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            if (faction == null || !FactionsPlugin.IsEffectivelyNpc(faction)) return null;
            return faction;
        }

        /// <summary>Returns the SE economy station Id if <paramref name="entityId"/> is a spawned
        /// economy station belonging to <paramref name="faction"/>; otherwise 0.</summary>
        private static long GetEconomyStationId(MyFaction faction, long entityId)
        {
            if (faction == null) return 0L;
            foreach (var station in faction.Stations)
            {
                if (station != null && station.StationEntityId == entityId)
                    return station.Id;
            }
            return 0L;
        }

        private static string GetStationPrefabName(MyFaction faction, long stationEntityId)
        {
            if (faction == null) return null;
            foreach (var station in faction.Stations)
            {
                if (station == null || station.StationEntityId != stationEntityId) continue;
                return station.PrefabName;
            }
            return null;
        }

        private static long? GetBookValue(Func<string, long> lookup, string prefabId)
        {
            if (lookup == null || string.IsNullOrEmpty(prefabId)) return null;
            long value = lookup(prefabId);
            return value > 0 ? (long?)value : null;
        }

        private static List<WeaponBand> GetWeaponBands(Func<string, List<WeaponBand>> lookup, string prefabId)
        {
            if (lookup == null || string.IsNullOrEmpty(prefabId)) return null;
            List<WeaponBand> bands = lookup(prefabId);
            return bands != null && bands.Count > 0 ? bands : null;
        }
    }
}
