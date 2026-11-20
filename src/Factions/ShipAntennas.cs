using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Reads the antennas a player's ship carries into the plain numbers <see cref="ShipSensor"/>
    /// scores. The host counts and measures; Core decides what that is worth.
    ///
    /// <para><b>The whole mechanical group counts</b>, so an antenna on a rotor or piston subgrid
    /// is part of the ship. A connector-docked grid is not: that is another ship.</para>
    ///
    /// <para><b>Only antennas the pilot may use count</b> — owned by them, shared with their
    /// faction, or unowned. Sitting in someone else's cockpit does not hand over their sensors.</para>
    /// </summary>
    internal static class ShipAntennas
    {
        [ThreadStatic] private static List<MyCubeGrid> _group;

        /// <param name="dishes">When given, also collects the counted antennas that are dishes
        /// (<see cref="TryDishKind"/>), for the radio's directional gain (FAC-419).</param>
        public static void Read(MyCubeGrid grid, long pilotIdentityId, List<AntennaReading> into,
                                List<MyRadioAntenna> dishes = null)
        {
            into.Clear();
            if (dishes != null) dishes.Clear();
            if (grid == null) return;

            if (_group == null) _group = new List<MyCubeGrid>();
            _group.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, _group); }
                catch (Exception) { _group.Clear(); }
            }
            if (!_group.Contains(grid)) _group.Add(grid);

            for (int g = 0; g < _group.Count; g++)
            {
                MyCubeGrid member = _group[g];
                if (member == null || member.Closed) continue;
                foreach (MyRadioAntenna antenna in member.GetFatBlocks<MyRadioAntenna>())
                {
                    if (antenna == null || !antenna.IsWorking) continue;
                    MyRelationsBetweenPlayerAndBlock relation = antenna.GetUserRelationToOwner(pilotIdentityId);
                    if (relation != MyRelationsBetweenPlayerAndBlock.Owner
                        && relation != MyRelationsBetweenPlayerAndBlock.FactionShare
                        && relation != MyRelationsBetweenPlayerAndBlock.NoOwnership) continue;

                    into.Add(new AntennaReading
                    {
                        VolumeM3 = VolumeM3(antenna),
                        RadiusM = antenna.GetRadius(),
                        Broadcasting = antenna.EnableBroadcasting.Value
                    });
                    RadioDishKind kind;
                    if (dishes != null && TryDishKind(antenna, out kind)) dishes.Add(antenna);
                }
            }
            _group.Clear();
        }

        /// <summary>The widest radius this grid is announcing itself over, km, or 0 when nothing on
        /// it is broadcasting. SE draws a broadcasting grid on every receiver inside this radius, so
        /// it is the floor our own detection must not fall below (FAC-140).
        ///
        /// <para>Widest rather than summed, and the whole mechanical group, matching
        /// <see cref="Read"/>: what announces you is the loudest antenna you are running.
        /// Ownership is deliberately NOT filtered here — an antenna belonging to anyone at all still
        /// broadcasts the grid it is bolted to.</para></summary>
        public static double BroadcastKm(MyCubeGrid grid)
        {
            if (grid == null) return 0;

            if (_group == null) _group = new List<MyCubeGrid>();
            _group.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, _group); }
                catch (Exception) { _group.Clear(); }
            }
            if (!_group.Contains(grid)) _group.Add(grid);

            double widestM = 0;
            for (int g = 0; g < _group.Count; g++)
            {
                MyCubeGrid member = _group[g];
                if (member == null || member.Closed) continue;
                foreach (MyRadioAntenna antenna in member.GetFatBlocks<MyRadioAntenna>())
                {
                    if (antenna == null || !antenna.IsWorking || !antenna.EnableBroadcasting.Value) continue;
                    double radiusM = antenna.GetRadius();
                    if (radiusM > widestM) widestM = radiusM;
                }
            }
            _group.Clear();
            return widestM / 1000.0;
        }

        /// <summary>An antenna with a reflector, and which kind: vanilla's <c>LargeBlockRadioAntennaDish</c>
        /// (the Antenna Dish) and any modded antenna whose subtype names a dish; the Contact Pack's
        /// Parabolic Antenna (<c>*CompactRadioAntennaReskin</c>, large and small).</summary>
        public static bool TryDishKind(MyRadioAntenna antenna, out RadioDishKind kind)
        {
            kind = RadioDishKind.Dish;
            string subtype = antenna != null && antenna.BlockDefinition != null ? antenna.BlockDefinition.Id.SubtypeName : null;
            Factions.Wire.WireReflector wire;
            if (!Factions.Wire.RadioAim.TryKindOf(subtype, out wire)) return false;
            kind = (RadioDishKind)(byte)wire;
            return true;
        }

        /// <summary>Which way a reflector points, in world space, right now. The dish mounts on its
        /// bottom face only and looks along the block's Up; the Parabolic Antenna's reflector faces
        /// the block's local +Z, which is SE's Backward (operator, 2026-09-29).</summary>
        public static RadioDish DishOf(MyRadioAntenna antenna, RadioDishKind kind)
        {
            VRageMath.MatrixD m = antenna.WorldMatrix;
            VRageMath.Vector3D axis = kind == RadioDishKind.Parabolic ? m.Backward : m.Up;
            return new RadioDish { Kind = kind, Axis = new Vec3(axis.X, axis.Y, axis.Z) };
        }

        /// <summary>Block volume from the definition's <c>Size</c>. The only field that tells antenna
        /// variants apart: none declares its own <c>MaxBroadcastRadius</c> or power input.</summary>
        public static double VolumeM3(MyCubeBlock block)
        {
            MyCubeBlockDefinition def = block != null ? block.BlockDefinition : null;
            if (def == null || MyDefinitionManager.Static == null) return 0;
            double edge = MyDefinitionManager.Static.GetCubeSize(def.CubeSize);
            return (double)def.Size.X * def.Size.Y * def.Size.Z * edge * edge * edge;
        }
    }
}
