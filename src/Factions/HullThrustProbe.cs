using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// One line saying why a hull can or cannot push: its thrusters by fuel and powered state, the
    /// tanks and power that feed the hydrogen ones, how many conveyor groups SE's thrust component
    /// keeps, and whether an H2 thruster can reach a tank at all.
    ///
    /// <para>A thruster SE reports unpowered contributes nothing to any direction, so a hull whose
    /// every thruster is hydrogen reads zero lift when its gas does not arrive. The four ways that
    /// happens — no gas, no electricity for the tanks, no conveyor path, a stale thrust group — read
    /// differently here, which is the point of reading them together.</para>
    ///
    /// <para>Game thread only.</para>
    /// </summary>
    internal static class HullThrustProbe
    {
        private static readonly FieldInfo ConnectedGroupsF = typeof(MyEntityThrustComponent).GetField(
            "m_connectedGroups", BindingFlags.Instance | BindingFlags.NonPublic);

        public static string Describe(MyCubeGrid grid)
        {
            if (grid == null || grid.Closed) return "grid=gone";

            int h2 = 0, h2Powered = 0, h2Enabled = 0, other = 0, otherPowered = 0;
            int tanks = 0, tanksWorking = 0, stockpiling = 0, batteries = 0, batteriesWorking = 0;
            int engines = 0, enginesWorking = 0;
            double fill = 0, stored = 0;
            MyThrust firstH2 = null;
            var tankEndpoints = new System.Collections.Generic.List<IMyConveyorEndpoint>();

            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                var thrust = block as MyThrust;
                if (thrust != null)
                {
                    bool hydrogen = thrust.FuelDefinition != null
                        && thrust.FuelDefinition.Id == MyResourceDistributorComponent.HydrogenId;
                    if (hydrogen)
                    {
                        h2++;
                        if (thrust.Enabled) h2Enabled++;
                        if (thrust.IsPowered) h2Powered++;
                        if (firstH2 == null) firstH2 = thrust;
                    }
                    else
                    {
                        other++;
                        if (thrust.IsPowered) otherPowered++;
                    }
                    continue;
                }

                var tank = block as MyGasTank;
                if (tank != null)
                {
                    tanks++;
                    var api = (Sandbox.ModAPI.Ingame.IMyGasTank)tank;
                    fill += api.FilledRatio;
                    if (tank.IsWorking) tanksWorking++;
                    if (api.Stockpile) stockpiling++;
                    if (tank.ConveyorEndpoint != null) tankEndpoints.Add(tank.ConveyorEndpoint);
                    continue;
                }

                var battery = block as MyBatteryBlock;
                if (battery != null)
                {
                    batteries++;
                    stored += battery.CurrentStoredPower;
                    if (battery.IsWorking) batteriesWorking++;
                    continue;
                }

                if (block.BlockDefinition != null
                    && block.BlockDefinition.Id.TypeId.ToString().EndsWith("HydrogenEngine", StringComparison.Ordinal))
                {
                    engines++;
                    if (block.IsWorking) enginesWorking++;
                }
            }

            int reachable = 0;
            if (firstH2 != null && firstH2.ConveyorEndpoint != null)
                for (int i = 0; i < tankEndpoints.Count; i++)
                    if (MyGridConveyorSystem.Reachable(firstH2.ConveyorEndpoint, tankEndpoints[i])) reachable++;

            var sb = new StringBuilder();
            sb.Append("h2=").Append(h2).Append(" h2On=").Append(h2Enabled).Append(" h2Powered=").Append(h2Powered)
              .Append(" otherThr=").Append(other).Append(" otherPowered=").Append(otherPowered)
              .Append(" tanks=").Append(tanks).Append(" tankFill=")
              .Append((tanks > 0 ? fill / tanks : 0).ToString("F2", CultureInfo.InvariantCulture))
              .Append(" tankWorking=").Append(tanksWorking).Append(" stockpile=").Append(stockpiling)
              .Append(" tankReach=").Append(reachable).Append('/').Append(tankEndpoints.Count)
              .Append(" bat=").Append(batteries).Append(" batWorking=").Append(batteriesWorking)
              .Append(" stored=").Append(stored.ToString("F3", CultureInfo.InvariantCulture))
              .Append(" h2eng=").Append(engines).Append(" h2engWorking=").Append(enginesWorking)
              .Append(" thrustGroups=").Append(ThrustGroups(grid))
              .Append(" static=").Append(grid.IsStatic);
            return sb.ToString();
        }

        /// <summary><see cref="GridAssembler.RegroupThrust"/> on demand. Returns what it touched, for
        /// the reply.</summary>
        public static string Refresh(MyCubeGrid grid)
        {
            if (grid == null || grid.Closed) return "grid=gone";
            return GridAssembler.RegroupThrust(grid)
                ? "conveyors flagged, thrust groups and tables marked"
                : "conveyors flagged; no thrust component";
        }

        private static string ThrustGroups(MyCubeGrid grid)
        {
            var thrust = grid.Components.Get<MyEntityThrustComponent>();
            if (thrust == null) return "none";
            if (ConnectedGroupsF == null) return "?";
            var groups = ConnectedGroupsF.GetValue(thrust) as ICollection;
            return groups == null ? "?" : groups.Count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
