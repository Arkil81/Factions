using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Engine.Physics;          // MyGridPhysicalHierarchy
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;    // MyMechanicalConnectionBlockBase
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;   // MyTargetLockingBlockComponent
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;
using IMyRadioAntenna = Sandbox.ModAPI.IMyRadioAntenna;

namespace Factions
{
    /// <summary>
    /// The wing commander's reads of the SE world: an antenna's state, a player's or a grid's target
    /// lock, a target's standing. The rules they feed are Core's (<see cref="WingRules"/>); this
    /// only turns entities into <see cref="WingAntennaReading"/> and <see cref="WingTargetReading"/>.
    ///
    /// <para><b>Locks</b> (FAC-403's spike, decompiled SE): a player's lock lives on the character
    /// (<c>MyCharacter.TargetLockingComp</c>, server-authoritative), a combat AI block's in its
    /// <c>MyTargetLockingBlockComponent</c>, and a ship controller keeps its last pilot's in the
    /// unsynced <c>MyShipController.TargetData</c>, lost on restart. A lock still being acquired
    /// counts: its target id is already the grid the player pointed at, and the server checks it
    /// either way.</para>
    /// </summary>
    internal static class WingWorld
    {
        /// <summary>Nexus authority: what agents read of the antennas and targets of orders given
        /// there, served for an entity this server does not have. Null elsewhere.</summary>
        internal static NexusWingReadings Relayed;

        /// <summary>The antenna, and the grid it stands on (zero when there is none).</summary>
        public static WingAntennaReading ReadAntenna(long antennaEntityId, out long gridEntityId)
        {
            gridEntityId = 0;
            var r = new WingAntennaReading();
            IMyRadioAntenna antenna = antennaEntityId != 0 ? MyEntities.GetEntityById(antennaEntityId) as IMyRadioAntenna : null;
            if (antenna == null)
            {
                NexusWingReadings relayed = Relayed;
                WingAntennaReading read;
                if (relayed != null && relayed.TryAntenna(antennaEntityId, DateTime.UtcNow, out read, out gridEntityId)) return read;
                return r;
            }
            if (antenna.Closed || antenna.MarkedForClose) return r;
            r.Exists = true;
            r.Functional = antenna.IsFunctional;
            r.Enabled = antenna.Enabled;
            r.Powered = antenna.IsWorking;
            r.Broadcasting = antenna.EnableBroadcasting;
            r.RadiusM = antenna.Radius;
            r.OwnerIdentityId = antenna.OwnerId;
            r.OwnerFactionTag = FactionTagOf(antenna.OwnerId);
            Vector3D p = antenna.GetPosition();
            r.PosM = new Vec3(p.X, p.Y, p.Z);
            gridEntityId = antenna.CubeGrid != null ? antenna.CubeGrid.EntityId : 0;
            return r;
        }

        /// <summary>The SE faction tag of an identity, or null when it is in none.</summary>
        public static string FactionTagOf(long identityId)
        {
            if (identityId == 0 || MySession.Static == null || MySession.Static.Factions == null) return null;
            IMyFaction f = MySession.Static.Factions.TryGetPlayerFaction(identityId);
            return f != null ? f.Tag : null;
        }

        /// <summary>The grid an entity id names — the entity itself, or the grid a block or subpart
        /// sits on — or zero.</summary>
        public static long GridOf(long entityId)
        {
            if (entityId == 0) return 0;
            MyEntity e = MyEntities.GetEntityById(entityId);
            if (e == null) return 0;
            MyEntity top = e.GetTopMostParent() as MyEntity;
            MyCubeGrid grid = top as MyCubeGrid;
            return grid != null && !grid.Closed ? grid.EntityId : 0;
        }

        /// <summary>A target as the commander sees it: whether it is there and whether the commander
        /// is hostile to its owner, the way SE's own relation rules have it.</summary>
        public static WingTargetReading ReadTarget(long gridEntityId, long commanderIdentityId)
        {
            var t = new WingTargetReading { EntityId = gridEntityId };
            MyCubeGrid grid;
            if (gridEntityId != 0 && !MyEntities.TryGetEntityById(gridEntityId, out grid, false))
            {
                NexusWingReadings relayed = Relayed;
                WingTargetReading read;
                if (relayed != null && relayed.TryTarget(gridEntityId, commanderIdentityId, DateTime.UtcNow, out read)) return read;
                return t;
            }
            if (gridEntityId == 0 || !MyEntities.TryGetEntityById(gridEntityId, out grid, false)
                || grid.Closed || grid.MarkedForClose)
                return t;
            t.Exists = true;
            Vector3D c = grid.PositionComp.WorldVolume.Center;
            t.PosM = new Vec3(c.X, c.Y, c.Z);
            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            t.Hostile = owner != 0 && owner != commanderIdentityId
                && MyIDModule.GetRelationPlayerPlayer(commanderIdentityId, owner) == MyRelationsBetweenPlayers.Enemies;
            return t;
        }

        /// <summary>The online player behind an identity, or null.</summary>
        public static MyPlayer PlayerOf(long identityId)
        {
            if (identityId == 0 || MySession.Static == null || MySession.Static.Players == null) return null;
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                if (p != null && !p.IsBot && p.Identity != null && p.Identity.IdentityId == identityId) return p;
            return null;
        }

        public static MyPlayer PlayerOfSteam(ulong steamId)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return null;
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                if (p != null && !p.IsBot && p.Identity != null && p.Id.SteamId == steamId) return p;
            return null;
        }

        /// <summary>The grid an identity's own lock is on — locked or locking — or zero.</summary>
        public static long PresserLock(long identityId)
        {
            MyPlayer p = PlayerOf(identityId);
            MyCharacter ch = p != null ? p.Character : null;
            return ch != null ? CharacterLock(ch) : 0;
        }

        private static long CharacterLock(MyCharacter ch)
        {
            MyTargetLockingComponent comp = ch.TargetLockingComp;
            return comp != null ? GridOf(comp.TargetData.TargetId) : 0;
        }

        /// <summary>The locks a block trigger may read on the antenna's grid and its subgrids: a
        /// seated pilot's, a working combat AI block's, and a ship controller's retained copy of its
        /// last pilot's.</summary>
        public static void GridLocks(long gridEntityId, out long pilotLock, out long combatBlockLock, out long cockpitRetained)
        {
            pilotLock = combatBlockLock = cockpitRetained = 0;
            MyCubeGrid grid;
            if (gridEntityId != 0 && !MyEntities.TryGetEntityById(gridEntityId, out grid, false))
            {
                NexusWingReadings.StationGrid relayed;
                if (TryRelayedStation(gridEntityId, out relayed))
                {
                    pilotLock = relayed.Pilot;
                    combatBlockLock = relayed.Combat;
                    cockpitRetained = relayed.Retained;
                }
                return;
            }
            if (gridEntityId == 0 || !MyEntities.TryGetEntityById(gridEntityId, out grid, false) || grid.Closed) return;
            var grids = new List<MyCubeGrid>();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Logical != null)
                MyCubeGridGroups.Static.Logical.GetGroupNodes(grid, grids);
            if (grids.Count == 0) grids.Add(grid);
            foreach (MyCubeGrid g in grids)
            {
                if (g == null || g.Closed) continue;
                foreach (MyCubeBlock block in g.GetFatBlocks())
                {
                    var sc = block as MyShipController;
                    if (sc != null)
                    {
                        if (pilotLock == 0 && sc.Pilot != null) pilotLock = CharacterLock(sc.Pilot);
                        if (cockpitRetained == 0) cockpitRetained = GridOf(sc.TargetData.TargetId);
                        continue;
                    }
                    if (combatBlockLock != 0 || !block.IsWorking) continue;
                    MyTargetLockingBlockComponent locking = block.Components.Get<MyTargetLockingBlockComponent>();
                    if (locking != null) combatBlockLock = GridOf(locking.TargetData.TargetId);
                }
            }
        }

        // ---- station calls ---------------------------------------------------------------------

        private static readonly List<MyEntity> NearScratch = new List<MyEntity>();

        /// <summary>A station grid on a Nexus agent whose call is being read there and relayed; false
        /// when this server is not the authority or the readings are stale.</summary>
        private static bool TryRelayedStation(long gridEntityId, out NexusWingReadings.StationGrid station)
        {
            NexusWingReadings relayed = Relayed;
            if (relayed != null && relayed.TryStation(gridEntityId, DateTime.UtcNow, out station)) return true;
            station = default(NexusWingReadings.StationGrid);
            return false;
        }

        /// <summary>Whether the grid is in the world: here, or on the agent relaying its station's call.</summary>
        public static bool GridExists(long gridEntityId)
        {
            MyCubeGrid grid;
            NexusWingReadings.StationGrid relayed;
            if (gridEntityId != 0 && !MyEntities.TryGetEntityById(gridEntityId, out grid, false))
                return TryRelayedStation(gridEntityId, out relayed);
            return gridEntityId != 0 && MyEntities.TryGetEntityById(gridEntityId, out grid, false)
                && !grid.Closed && !grid.MarkedForClose;
        }

        /// <summary>The grid's majority owner, or zero.</summary>
        public static long GridOwner(long gridEntityId)
        {
            MyCubeGrid grid;
            if (gridEntityId == 0 || !MyEntities.TryGetEntityById(gridEntityId, out grid, false) || grid.Closed) return 0;
            return grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
        }

        /// <summary>Whether the grid is a static grid — a station, as SE has it.</summary>
        public static bool IsStaticGrid(long gridEntityId)
        {
            MyCubeGrid grid;
            NexusWingReadings.StationGrid relayed;
            if (gridEntityId != 0 && !MyEntities.TryGetEntityById(gridEntityId, out grid, false))
                return TryRelayedStation(gridEntityId, out relayed) && relayed.Static;
            return gridEntityId != 0 && MyEntities.TryGetEntityById(gridEntityId, out grid, false)
                && !grid.Closed && grid.IsStatic;
        }

        /// <summary>The antenna blocks on a grid, into <paramref name="into"/> (cleared first).</summary>
        public static void Antennas(long gridEntityId, List<long> into)
        {
            into.Clear();
            MyCubeGrid grid;
            NexusWingReadings.StationGrid relayed;
            if (gridEntityId != 0 && !MyEntities.TryGetEntityById(gridEntityId, out grid, false))
            {
                if (TryRelayedStation(gridEntityId, out relayed) && relayed.Antennas != null) into.AddRange(relayed.Antennas);
                return;
            }
            if (gridEntityId == 0 || !MyEntities.TryGetEntityById(gridEntityId, out grid, false) || grid.Closed) return;
            foreach (MyCubeBlock block in grid.GetFatBlocks())
                if (block is IMyRadioAntenna) into.Add(block.EntityId);
        }

        /// <summary>Switches every antenna on a grid on or off; returns how many it touched.</summary>
        public static int SetAntennasEnabled(long gridEntityId, bool enabled)
        {
            MyCubeGrid grid;
            if (gridEntityId == 0 || !MyEntities.TryGetEntityById(gridEntityId, out grid, false) || grid.Closed) return 0;
            int n = 0;
            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                var a = block as IMyRadioAntenna;
                if (a == null) continue;
                a.Enabled = enabled;
                n++;
            }
            return n;
        }

        /// <summary>The hull whose weapon dealt a hit (<see cref="HullOf"/>). <c>AttackerId</c> is the
        /// launching block's entity id for a gun's round and a missile's impact, and the firing
        /// player's character for a missile's explosion; a block is walked to its grid, a character to
        /// the grid of the seat it sits in, and a grid is itself. Zero for a character on foot or
        /// anything else that is not on a grid.</summary>
        public static long AttackerGridOf(long attackerEntityId)
        {
            long mountedOn;
            return AttackerGridOf(attackerEntityId, out mountedOn);
        }

        /// <summary><see cref="AttackerGridOf(long)"/>, and the grid the weapon itself sits on in
        /// <paramref name="mountedOnGridId"/> — a turret subgrid where the hull is another.</summary>
        public static long AttackerGridOf(long attackerEntityId, out long mountedOnGridId)
        {
            mountedOnGridId = 0;
            if (attackerEntityId == 0) return 0;
            MyEntity attacker;
            if (!MyEntities.TryGetEntityById(attackerEntityId, out attacker) || attacker == null) return 0;
            MyCubeGrid grid = null;
            var block = attacker as MyCubeBlock;
            if (block != null) grid = block.CubeGrid;
            else if (attacker is MyCubeGrid) grid = (MyCubeGrid)attacker;
            else if (attacker is MyCharacter)
            {
                var seat = attacker.Parent as MyCubeBlock;
                if (seat != null) grid = seat.CubeGrid;
            }
            if (grid == null) return 0;
            mountedOnGridId = grid.EntityId;
            MyCubeGrid hull = HullOf(grid);
            return hull != null ? hull.EntityId : grid.EntityId;
        }

        /// <summary>The hull a grid is mounted on: walked up SE's physical hierarchy for as long as the
        /// link to the parent is a rotor, hinge or piston, so a turret on a rotor subgrid answers the
        /// ship it sits on. Docking (connectors, landing gear) is not followed: a docked ship stays its
        /// own hull. The grid itself when it has no mechanical parent. Allocates nothing.</summary>
        public static MyCubeGrid HullOf(MyCubeGrid grid)
        {
            MyGridPhysicalHierarchy h = MyGridPhysicalHierarchy.Static;
            if (grid == null || h == null) return grid;
            for (int depth = 0; depth < MaxMountDepth; depth++)
            {
                if (!(h.GetEntityConnectingToParent(grid) is MyMechanicalConnectionBlockBase)) return grid;
                MyCubeGrid parent = h.GetParent(grid);
                if (parent == null || parent == grid) return grid;
                grid = parent;
            }
            return grid;
        }

        /// <summary>How many mechanical links <see cref="HullOf"/> walks at most.</summary>
        private const int MaxMountDepth = 8;

        /// <summary>The nearest grid within <paramref name="radiusM"/> of the station grid that
        /// <paramref name="ownerIdentityId"/> is hostile to, or zero.</summary>
        public static long NearestHostileGrid(long stationGridId, long ownerIdentityId, double radiusM)
        {
            MyCubeGrid station;
            NexusWingReadings.StationGrid relayed;
            if (stationGridId != 0 && !MyEntities.TryGetEntityById(stationGridId, out station, false))
                return TryRelayedStation(stationGridId, out relayed) ? relayed.HostileNear : 0;
            if (stationGridId == 0 || !MyEntities.TryGetEntityById(stationGridId, out station, false) || station.Closed) return 0;
            Vector3D c = station.PositionComp.WorldVolume.Center;
            var sphere = new BoundingSphereD(c, radiusM);
            NearScratch.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, NearScratch);
            long best = 0;
            double bestD = double.MaxValue;
            try
            {
                foreach (MyEntity e in NearScratch)
                {
                    var g = e as MyCubeGrid;
                    if (g == null || g.Closed || g.EntityId == stationGridId) continue;
                    WingTargetReading t = ReadTarget(g.EntityId, ownerIdentityId);
                    if (!t.Exists || !t.Hostile) continue;
                    double d = Vector3D.DistanceSquared(c, g.PositionComp.WorldVolume.Center);
                    if (d < bestD) { bestD = d; best = g.EntityId; }
                }
            }
            finally { NearScratch.Clear(); }
            return best;
        }

        /// <summary>For <c>factions bench wing</c>: the player's identity and an antenna on the grid
        /// they are seated in — the first working, broadcasting one, else the first — or why not.</summary>
        public static string SeatedAntenna(ulong steamId, out long identityId, out long antennaEntityId)
        {
            identityId = 0;
            antennaEntityId = 0;
            MyPlayer player = null;
            if (steamId != 0) player = PlayerOfSteam(steamId);
            else if (MySession.Static != null && MySession.Static.Players != null)
                foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                    if (p != null && !p.IsBot && p.Identity != null) { player = p; break; }
            if (player == null) return steamId != 0 ? "player " + steamId + " is not online." : "no player online.";
            identityId = player.Identity.IdentityId;

            var sc = player.Controller != null ? player.Controller.ControlledEntity as MyShipController : null;
            if (sc == null || sc.CubeGrid == null) return "the player is not seated in a ship.";
            var grids = new List<MyCubeGrid>();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Logical != null)
                MyCubeGridGroups.Static.Logical.GetGroupNodes(sc.CubeGrid, grids);
            if (grids.Count == 0) grids.Add(sc.CubeGrid);
            long fallback = 0;
            foreach (MyCubeGrid g in grids)
                foreach (MyCubeBlock block in g.GetFatBlocks())
                {
                    var a = block as IMyRadioAntenna;
                    if (a == null) continue;
                    if (a.IsWorking && a.EnableBroadcasting)
                    {
                        antennaEntityId = block.EntityId;
                        return null;
                    }
                    if (fallback == 0) fallback = block.EntityId;
                }
            if (fallback == 0) return "the player's ship has no antenna.";
            antennaEntityId = fallback;
            return null;
        }
    }
}
