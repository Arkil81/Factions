using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Grid contacts for the player scanner and map (FAC-533): every grid of
    /// <see cref="FactionTuning.SignatureGridMinBlocks"/> or more that is neither a node's station
    /// nor a sim hull, one per mechanical group, led by its largest grid. Player-owned, vanilla NPC
    /// and unowned alike.
    ///
    /// <para>The group set is rebuilt on the grid sampler's throttle, beside the player-owned list
    /// it already enumerates for; positions and samples are read on each scanner tick.</para>
    /// </summary>
    public partial class FactionsPlugin
    {
        private sealed class HullGroup
        {
            public MyCubeGrid Lead;
            public long[] Members;
            public int Blocks;
        }

        private readonly List<HullGroup> _hullGroups = new List<HullGroup>();
        private readonly List<MyCubeGrid> _hullScratch = new List<MyCubeGrid>();
        private readonly HashSet<long> _hullGrouped = new HashSet<long>();
        private readonly List<IMyCubeGrid> _groupScratch = new List<IMyCubeGrid>();
        /// <summary>Whether a grid is a node's station, per grid id. A yes is kept for the grid's
        /// life; a no is asked again every <see cref="StationVerdictRetrySeconds"/>, because a station
        /// grid exists before the materialiser records it, and a sample in that gap would otherwise
        /// list the station as a player grid for good. The answer walks every SE faction's stations.</summary>
        private Dictionary<long, bool> _stationVerdict = new Dictionary<long, bool>();
        private Dictionary<long, bool> _stationVerdictNext = new Dictionary<long, bool>();
        private const double StationVerdictRetrySeconds = 30.0;
        private DateTime _stationVerdictRetriedAt;
        private bool _stationVerdictRetry;
        private readonly List<ScannerGridCandidate> _scannerGrids = new List<ScannerGridCandidate>();
        private readonly Dictionary<long, string> _ownerTagScratch = new Dictionary<long, string>();
        private readonly Dictionary<long, string> _ownerNameScratch = new Dictionary<long, string>();
        private readonly Dictionary<long, bool> _ownerOfflineScratch = new Dictionary<long, bool>();

        /// <summary>Called from the owned-grid enumeration for every grid past the size floor. An
        /// agent keeps only player-owned grids: the authority's scanner takes those from its report.</summary>
        private void CollectHull(MyCubeGrid grid, bool playerOwned)
        {
            if (_runtime != null || (playerOwned && _nexusRole == NexusRole.Agent)) _hullScratch.Add(grid);
        }

        /// <summary>Authority: each agent's player-owned grid groups, for the scanner and the map.</summary>
        private readonly NexusScannerGridBook _agentScannerGrids = new NexusScannerGridBook();
        private readonly HashSet<long> _scannerGridIds = new HashSet<long>();

        /// <summary>Agent: set by each sample, cleared when a report carries it.</summary>
        private bool _agentScannerGridsSampled;

        /// <summary>Agent: the grid groups sampled here, for the next report; null when nothing was
        /// sampled since the last one.</summary>
        private List<NexusScannerGrid> ReadAgentScannerGrids()
        {
            if (!_agentScannerGridsSampled) return null;
            _agentScannerGridsSampled = false;
            var list = new List<NexusScannerGrid>(_scannerGrids.Count);
            for (int i = 0; i < _scannerGrids.Count; i++) list.Add(NexusScannerGrid.From(_scannerGrids[i]));
            return list;
        }

        /// <summary>Groups the grids the last enumeration collected. Runs on the sampler's throttle.</summary>
        private void RebuildHullGroups()
        {
            _hullGroups.Clear();
            _hullGrouped.Clear();
            _stationVerdictNext.Clear();
            DateTime now = DateTime.UtcNow;
            _stationVerdictRetry = (now - _stationVerdictRetriedAt).TotalSeconds >= StationVerdictRetrySeconds;
            if (_stationVerdictRetry) _stationVerdictRetriedAt = now;
            for (int i = 0; i < _hullScratch.Count; i++)
            {
                MyCubeGrid grid = _hullScratch[i];
                if (grid == null || grid.MarkedForClose || _hullGrouped.Contains(grid.EntityId)) continue;

                _groupScratch.Clear();
                try { ((IMyCubeGrid)grid).GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(_groupScratch); }
                catch { }
                if (_groupScratch.Count == 0) _groupScratch.Add(grid);

                MyCubeGrid lead = null;
                int blocks = 0;
                bool excluded = false;
                var members = new long[_groupScratch.Count];
                for (int m = 0; m < _groupScratch.Count; m++)
                {
                    var member = _groupScratch[m] as MyCubeGrid;
                    if (member == null) continue;
                    long id = member.EntityId;
                    members[m] = id;
                    _hullGrouped.Add(id);
                    blocks += member.BlocksCount;
                    if (lead == null || member.BlocksCount > lead.BlocksCount
                        || (member.BlocksCount == lead.BlocksCount && id < lead.EntityId)) lead = member;
                    if (!excluded && (SimGridMarks.IsMarked(id) || NpcGridNaming.IsSimBornName(member.Name, id)
                                      || IsStationVerdict(id)))
                        excluded = true;
                }
                if (excluded || lead == null) continue;
                _hullGroups.Add(new HullGroup { Lead = lead, Members = members, Blocks = blocks });
            }
            _hullScratch.Clear();
            Dictionary<long, bool> swap = _stationVerdict;
            _stationVerdict = _stationVerdictNext;
            _stationVerdictNext = swap;
        }

        /// <summary>Whether a grid is a node's station, which the scanner leaves to the node's own
        /// marker. An agent has no runtime: its stations are the ones its hands stand, so a station
        /// a player founded on an agent's shard is not reported to the authority as a player grid.</summary>
        private bool IsStationVerdict(long gridId)
        {
            bool station;
            if (!_stationVerdict.TryGetValue(gridId, out station) || (!station && _stationVerdictRetry))
                station = _runtime != null
                    ? _runtime.IsStationGrid(gridId)
                    : _agentHands != null && _agentHands.Stations != null
                      && _agentHands.Stations.NodeIdForStationGrid(gridId) != null;
            _stationVerdictNext[gridId] = station;
            return station;
        }

        /// <summary>This tick's grid candidates. Rebuilt once per tick before the scanner and the map
        /// read it.</summary>
        private List<ScannerGridCandidate> ScannerGridsNow()
        {
            return _scannerGrids;
        }

        private void BuildScannerGrids()
        {
            _scannerGrids.Clear();
            if (MySession.Static == null || (_runtime == null && _nexusRole != NexusRole.Agent)) return;
            _ownerTagScratch.Clear();
            _ownerNameScratch.Clear();
            _ownerOfflineScratch.Clear();
            for (int i = 0; i < _hullGroups.Count; i++)
            {
                HullGroup g = _hullGroups[i];
                MyCubeGrid lead = g.Lead;
                if (lead == null || lead.MarkedForClose || lead.Closed) continue;
                long id = lead.EntityId;
                Vector3D p = lead.PositionComp.GetPosition();
                Vector3 v = lead.Physics != null ? lead.Physics.LinearVelocity : Vector3.Zero;
                double sig, broadcast;
                if (!_gridSignature.TryGetValue(id, out sig)) sig = 0;
                if (!_gridBroadcastKm.TryGetValue(id, out broadcast)) broadcast = 0;
                double dps = 0;
                for (int m = 0; m < g.Members.Length; m++)
                {
                    WeaponSample w;
                    if (_gridWeapons.TryGetValue(g.Members[m], out w)) dps += w.Dps;
                }
                long owner = lead.BigOwners != null && lead.BigOwners.Count > 0 ? lead.BigOwners[0] : 0L;
                _scannerGrids.Add(new ScannerGridCandidate
                {
                    EntityId = id,
                    PositionKm = new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0),
                    VelocityKmPerMin = new Vec3(v.X * 0.06, v.Y * 0.06, v.Z * 0.06),
                    Signature = sig,
                    BroadcastRadiusKm = broadcast,
                    OwnerIdentityId = owner,
                    OwnerTag = owner != 0 ? OwnerTagOf(owner) : null,
                    OwnerName = owner != 0 ? OwnerNameOf(owner) : null,
                    OwnerOffline = owner != 0 && OwnerOfflineOf(owner),
                    Large = lead.GridSizeEnum == MyCubeSize.Large,
                    Blocks = g.Blocks,
                    Name = lead.DisplayName,
                    WeaponDps = dps,
                });
            }
            if (_runtime != null) AppendAgentScannerGrids();
        }

        /// <summary>Authority: the player-owned grids on every agent's shard, so a player is scanned
        /// against the whole cluster and not only against the grids standing on this server. Owner
        /// tag, name and offline are judged here where this server's tables know the owner; the
        /// agent's own tag and name stand in where they do not.</summary>
        private void AppendAgentScannerGrids()
        {
            _scannerGridIds.Clear();
            for (int i = 0; i < _scannerGrids.Count; i++) _scannerGridIds.Add(_scannerGrids[i].EntityId);
            int local = _scannerGrids.Count;
            _agentScannerGrids.AppendFresh(_scannerGrids, DateTime.UtcNow, AgentContactsMaxAge, _scannerGridIds);
            for (int i = local; i < _scannerGrids.Count; i++)
            {
                ScannerGridCandidate g = _scannerGrids[i];
                g.OwnerTag = OwnerTagOf(g.OwnerIdentityId) ?? g.OwnerTag;
                g.OwnerName = OwnerNameOf(g.OwnerIdentityId) ?? g.OwnerName;
                g.OwnerOffline = OwnerOfflineOf(g.OwnerIdentityId);
                _scannerGrids[i] = g;
            }
        }

        private string OwnerTagOf(long owner)
        {
            string tag;
            if (_ownerTagScratch.TryGetValue(owner, out tag)) return tag;
            MyFaction f = MySession.Static.Factions != null ? MySession.Static.Factions.GetPlayerFaction(owner) : null;
            tag = f != null ? f.Tag : null;
            _ownerTagScratch[owner] = tag;
            return tag;
        }

        /// <summary>A human owner's name; null for an NPC, whose identity name says nothing.</summary>
        private string OwnerNameOf(long owner)
        {
            string name;
            if (_ownerNameScratch.TryGetValue(owner, out name)) return name;
            name = null;
            if (IsHumanIdentity(owner))
            {
                MyIdentity identity = MySession.Static.Players.TryGetIdentity(owner);
                name = identity != null ? identity.DisplayName : null;
            }
            _ownerNameScratch[owner] = name;
            return name;
        }

        private bool OwnerOfflineOf(long owner)
        {
            bool offline;
            if (_ownerOfflineScratch.TryGetValue(owner, out offline)) return offline;
            offline = _offlineRaidMonitor != null && _offlineRaidMonitor.IsProtectedOwner(owner);
            _ownerOfflineScratch[owner] = offline;
            return offline;
        }

        /// <summary>The largest grid of <paramref name="grid"/>'s mechanical group: the key its
        /// group's contact is listed under.</summary>
        internal static long GroupLeadOf(MyCubeGrid grid)
        {
            if (grid == null) return 0;
            var scratch = new List<IMyCubeGrid>();
            try { ((IMyCubeGrid)grid).GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(scratch); }
            catch { }
            MyCubeGrid lead = grid;
            for (int i = 0; i < scratch.Count; i++)
            {
                var m = scratch[i] as MyCubeGrid;
                if (m == null) continue;
                if (m.BlocksCount > lead.BlocksCount || (m.BlocksCount == lead.BlocksCount && m.EntityId < lead.EntityId))
                    lead = m;
            }
            return lead.EntityId;
        }
    }
}
