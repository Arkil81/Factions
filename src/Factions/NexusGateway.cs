using System;
using System.Linq;
using System.Reflection;
using Factions.Core;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Reads this process's Nexus identity without referencing the Nexus assembly.
    ///
    /// Nexus ships no compile-time API for a Torch plugin: its consumer surface is pushed to *mods*
    /// as a one-shot mod message at Nexus's own session load, which is too late for a decision made
    /// while our session is loading. <c>NGPlugin.SyncBase.ServerID</c> is a public static set during
    /// Nexus's plugin init, so it is readable by reflection and readable early.
    ///
    /// NGPlugin.dll is obfuscated (SmartAssembly), so this shape is verified rather than assumed:
    /// against Nexus Global v3.03.01 both the type and the property survive obfuscation under those
    /// names. A future Nexus build may rename them, which is why an unreadable ID is reported as
    /// <see cref="NexusPresence.Unresolvable"/> and never silently treated as "no cluster".
    /// </summary>
    public static class NexusGateway
    {
        private const string NexusAssemblyName = "NGPlugin";
        private const string SyncBaseTypeName = "NGPlugin.SyncBase";
        private const string ServerIdPropertyName = "ServerID";

        /// <summary>
        /// Probes the current AppDomain for Nexus.
        /// </summary>
        /// <param name="serverId">This server's Nexus ID; 0 unless the result is Resolved.</param>
        /// <param name="detail">Human-readable detail for the log, whatever the outcome.</param>
        public static NexusPresence Probe(out int serverId, out string detail)
        {
            serverId = 0;
            try
            {
                Assembly nexus = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(
                        a.GetName().Name, NexusAssemblyName, StringComparison.OrdinalIgnoreCase));
                if (nexus == null)
                {
                    detail = "NGPlugin assembly not loaded.";
                    return NexusPresence.Absent;
                }

                Type syncBase = nexus.GetType(SyncBaseTypeName, throwOnError: false);
                if (syncBase == null)
                {
                    detail = "NGPlugin is loaded but " + SyncBaseTypeName + " was not found; "
                           + "Nexus may have changed shape.";
                    return NexusPresence.Unresolvable;
                }

                PropertyInfo prop = syncBase.GetProperty(
                    ServerIdPropertyName, BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                {
                    detail = SyncBaseTypeName + " has no public static " + ServerIdPropertyName
                           + "; Nexus may have changed shape.";
                    return NexusPresence.Unresolvable;
                }

                object raw = prop.GetValue(null);
                if (raw == null)
                {
                    detail = ServerIdPropertyName + " read as null.";
                    return NexusPresence.Unresolvable;
                }

                serverId = Convert.ToInt32(raw);
                detail = "Nexus server ID " + serverId + " read from " + SyncBaseTypeName + ".";
                return NexusPresence.Resolved;
            }
            catch (Exception ex)
            {
                // Reaching Nexus must never take the plugin down; an unreadable ID fails closed.
                detail = "Nexus probe threw (" + ex.GetType().Name + ": " + ex.Message + ").";
                return NexusPresence.Unresolvable;
            }
        }

        private const string RegionHandlerTypeName = "NGPlugin.BoundarySystem.RegionHandler";
        private static bool _sectorBound;
        private static MethodInfo _smallestSector;
        private static PropertyInfo _nodeData;
        private static PropertyInfo _onServerId;

        /// <summary>The Nexus server whose sector holds <paramref name="positionM"/>, through
        /// <c>RegionHandler.TryGetSmallestSector(Vector3D, out TreeNode&lt;SectorAPI&gt;, …)</c> and the
        /// node's <c>Data.OnServerID</c> (read in the Nexus v3 decompile, 2026-11-20). False when Nexus
        /// is absent, has changed shape, or no sector holds the point.</summary>
        public static bool TryServerAt(Vector3D positionM, out byte serverId)
        {
            serverId = 0;
            try
            {
                if (!_sectorBound) BindSectorLookup();
                if (_smallestSector == null) return false;
                var args = new object[] { positionM, null, null };
                if (!(bool)_smallestSector.Invoke(null, args) || args[1] == null) return false;
                if (_nodeData == null) _nodeData = args[1].GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                object sector = _nodeData == null ? null : _nodeData.GetValue(args[1]);
                if (sector == null) return false;
                if (_onServerId == null) _onServerId = sector.GetType().GetProperty("OnServerID", BindingFlags.Public | BindingFlags.Instance);
                if (_onServerId == null) return false;
                serverId = Convert.ToByte(_onServerId.GetValue(sector));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void BindSectorLookup()
        {
            _sectorBound = true;
            Assembly nexus = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, NexusAssemblyName, StringComparison.OrdinalIgnoreCase));
            Type handler = nexus == null ? null : nexus.GetType(RegionHandlerTypeName, throwOnError: false);
            if (handler == null) return;
            foreach (MethodInfo m in handler.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "TryGetSmallestSector") continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 3 && p[0].ParameterType == typeof(Vector3D)) { _smallestSector = m; return; }
            }
        }

        /// <summary>For the boot line: whether the sector lookup bound.</summary>
        public static bool SectorLookupBound
        {
            get { if (!_sectorBound) BindSectorLookup(); return _smallestSector != null; }
        }
    }
}
