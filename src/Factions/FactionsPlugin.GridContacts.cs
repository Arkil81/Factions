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
        /// <summary>Whether a grid is a node's station, per grid id. Asked once per grid: the answer
        /// walks every SE faction's stations.</summary>
        private Dictionary<long, bool> _stationVerdict = new Dictionary<long, bool>();
        private Dictionary<long, bool> _stationVerdictNext = new Dictionary<long, bool>();
        private readonly List<ScannerGridCandidate> _scannerGrids = new List<ScannerGridCandidate>();
        private readonly Dictionary<long, string> _ownerTagScratch = new Dictionary<long, string>();
        private readonly Dictionary<long, string> _ownerNameScratch = new Dictionary<long, string>();
        private readonly Dictionary<long, bool> _ownerOfflineScratch = new Dictionary<long, bool>();

        /// <summary>Called from the owned-grid enumeration for every grid past the size floor.</summary>
        private void CollectHull(MyCubeGrid grid)
        {
            if (_runtime != null) _hullScratch.Add(grid);
        }

        /// <summary>Groups the grids the last enumeration collected. Runs on the sampler's throttle.</summary>
        private void RebuildHullGroups()
        {
            _hullGroups.Clear();
            _hullGrouped.Clear();
            _stationVerdictNext.Clear();
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

        private bool IsStationVerdict(long gridId)
        {
            bool station;
            if (!_stationVerdict.TryGetValue(gridId, out station))
                station = _runtime != null && _runtime.IsStationGrid(gridId);
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
            if (_runtime == null || MySession.Static == null) return;
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
