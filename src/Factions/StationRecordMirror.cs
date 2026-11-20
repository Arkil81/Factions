using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Keeps a Nexus agent's SE faction station records as the authority holds them (FAC-506 step 2b).
    ///
    /// <para>Nexus copies station records between servers only when it syncs a save, and skips player
    /// factions' outright (NGPlugin <c>SaveSync</c>: <c>Stations = val.Stations</c> unless the faction is
    /// player-made); its live faction sync carries membership and relations, not stations. So a station
    /// the authority founds, refits or hands to a new owner would stand on an agent as it was at the
    /// agent's last restart, or not at all. The authority puts its records in the node snapshot
    /// (<see cref="Fill"/>) and the agent makes its own match (<see cref="Reconcile"/>).</para>
    /// </summary>
    internal static class StationRecordMirror
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Authority: copies each founded node's SE record into its snapshot row.</summary>
        public static int Fill(NexusNodeSnapshot snapshot)
        {
            if (snapshot == null || MySession.Static == null || MySession.Static.Factions == null) return 0;
            Dictionary<long, KeyValuePair<MyFaction, MyFactionStation>> byId = Index();
            int filled = 0;
            foreach (NexusSnapshotNode row in snapshot.Nodes)
            {
                KeyValuePair<MyFaction, MyFactionStation> held;
                if (row == null || row.Station == 0 || !byId.TryGetValue(row.Station, out held)) continue;
                MyFactionStation st = held.Value;
                Vector3D p = st.Position, up = st.Up, fwd = st.Forward;
                row.Record = new NexusStationRecord
                {
                    Se = st.PrefabName, Type = (int)st.Type, Name = st.Name,
                    X = p.X, Y = p.Y, Z = p.Z,
                    Ux = up.X, Uy = up.Y, Uz = up.Z,
                    Fx = fwd.X, Fy = fwd.Y, Fz = fwd.Z,
                };
                filled++;
            }
            return filled;
        }

        /// <summary>What one reconcile changed.</summary>
        public struct Tally
        {
            public int Created, Moved, Renamed, Replaced, NoFaction;
            public bool Any { get { return Created + Moved + Renamed + Replaced > 0; } }
        }

        /// <summary>Agent: makes every snapshot record exist under its node's owner, with the
        /// authority's name, prefab and type. A record whose prefab or type changed is rebuilt, and a
        /// station standing for it here is closed so it stands again as the new design.</summary>
        public static Tally Reconcile(NexusNodeSnapshot snapshot, StationMaterializer materializer)
        {
            var t = new Tally();
            if (snapshot == null || MySession.Static == null || MySession.Static.Factions == null
                || StationFounder.StationsDictField == null) return t;
            Dictionary<long, KeyValuePair<MyFaction, MyFactionStation>> byId = Index();
            var factionByTag = new Dictionary<string, MyFaction>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                if (kv.Value != null && kv.Value.Tag != null && !factionByTag.ContainsKey(kv.Value.Tag))
                    factionByTag[kv.Value.Tag] = kv.Value;

            foreach (NexusSnapshotNode row in snapshot.Nodes)
            {
                if (row == null || row.Record == null || row.Station == 0 || row.Owner == null) continue;
                MyFaction owner;
                if (!factionByTag.TryGetValue(row.Owner, out owner)) { t.NoFaction++; continue; }
                try { ReconcileOne(row, owner, byId, materializer, ref t); }
                catch (Exception ex) { Log.Warn(ex, "StationRecordMirror: record for " + row.Id + " threw."); }
            }
            if (t.Created > 0 || t.Moved > 0) StationMaterializer.MarkClaimsDirty();
            return t;
        }

        private static void ReconcileOne(NexusSnapshotNode row, MyFaction owner,
            Dictionary<long, KeyValuePair<MyFaction, MyFactionStation>> byId, StationMaterializer materializer, ref Tally t)
        {
            NexusStationRecord r = row.Record;
            KeyValuePair<MyFaction, MyFactionStation> held;
            MyFactionStation station = null;
            if (byId.TryGetValue(row.Station, out held))
            {
                station = held.Value;
                if (!ReferenceEquals(held.Key, owner))
                {
                    StationFounder.MigrateStationRegistration(row.Station, station, held.Key, owner.Tag);
                    t.Moved++;
                }
            }

            var type = (MyStationTypeEnum)r.Type;
            if (station != null && string.Equals(station.PrefabName, r.Se, StringComparison.Ordinal) && station.Type == type)
            {
                if (r.Name != null && !string.Equals(station.Name, r.Name, StringComparison.Ordinal)
                    && StationFounder.StationNameSetter != null)
                {
                    StationFounder.StationNameSetter.Invoke(station, new object[] { r.Name });
                    MyCubeGrid grid;
                    if (station.StationEntityId != 0 && MyEntities.TryGetEntityById(station.StationEntityId, out grid, false))
                    {
                        grid.DisplayName = r.Name;
                        StationFounder.RenameBeacons(grid, r.Name);
                    }
                    t.Renamed++;
                }
                return;
            }

            bool standing = false;
            if (station != null)
            {
                standing = station.StationEntityId != 0 || station.SafeZoneEntityId != 0;
                StationFounder.CloseStanding(station, owner.Tag, row.Id);
            }
            var replacement = new MyFactionStation(
                row.Station, r.Name ?? (station != null ? station.Name : row.Id), new Vector3D(r.X, r.Y, r.Z), type, owner, r.Se,
                generatedItemsContainerTypeId: StationFounder.ResolveContainerTypeId(type),
                up: new Vector3((float)r.Ux, (float)r.Uy, (float)r.Uz),
                forward: new Vector3((float)r.Fx, (float)r.Fy, (float)r.Fz),
                isDeep: false);
            var dict = StationFounder.StationsDictField.GetValue(owner) as Dictionary<long, IMyFactionStation>;
            if (dict == null) return;
            dict[row.Station] = replacement;
            if (station == null) t.Created++;
            else
            {
                t.Replaced++;
                if (standing && materializer != null) materializer.ForgetMaterialization(row.Id);
            }
        }

        /// <summary>Every faction station record on this server, by station id.</summary>
        private static Dictionary<long, KeyValuePair<MyFaction, MyFactionStation>> Index()
        {
            var byId = new Dictionary<long, KeyValuePair<MyFaction, MyFactionStation>>();
            if (StationFounder.StationsDictField == null) return byId;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction f = kv.Value;
                if (f == null) continue;
                var dict = StationFounder.StationsDictField.GetValue(f) as Dictionary<long, IMyFactionStation>;
                if (dict == null) continue;
                foreach (KeyValuePair<long, IMyFactionStation> s in dict)
                {
                    var concrete = s.Value as MyFactionStation;
                    if (concrete != null && !byId.ContainsKey(s.Key))
                        byId[s.Key] = new KeyValuePair<MyFaction, MyFactionStation>(f, concrete);
                }
            }
            return byId;
        }
    }
}
