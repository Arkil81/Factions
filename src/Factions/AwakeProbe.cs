using System;
using System.Collections.Generic;
using System.Text;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>factions awake [all]</c>: why Havok keeps a grid awake. One line per simulated dynamic
    /// grid, awake and asleep alike, beside every cause SE's source shows can wake a body or keep it
    /// awake. Read-only, on demand, nothing retained.
    ///
    /// <para><b>Why a table and not a verdict.</b> The candidate causes are many (an overridden
    /// gyro disables deactivation; a working rotor with a non-zero target, a moving piston, a
    /// suspension wheel, a connector change and a mass change call <c>Activate</c>; a force added
    /// without <c>activeOnly</c> wakes the body; anything awake in the same simulation island keeps
    /// the island awake). Read side by side across awake and asleep grids, the cause separates;
    /// asserted from one grid, it does not.</para>
    ///
    /// <para>Havok exposes no per-body island id, so SE's physical group — grids joined by
    /// rotors, pistons, connectors or landing gear — stands in for the island.</para>
    /// </summary>
    internal static class AwakeProbe
    {
        private sealed class Row
        {
            public string Name;
            public int Blocks;
            public bool Awake;
            public bool DeactivationEnabled = true;
            public int Counter0;
            public int Counter1;
            public double Speed;
            public double AngularSpeed;
            public double GravityG;
            public int PhysicalGroup;
            public int AwakeInGroup;
            public int RotorsTurning;
            public int PistonsMoving;
            public int Wheels;
            public int ConnectorsLocked;
            public int GearsLocked;
            public int GyrosOverridden;
            public int ThrustersOn;
            public bool Dampeners;
            public bool Piloted;
            public double NearestCharacterM = -1;
        }

        internal static string Report(bool includeAsleep)
        {
            var rows = new List<Row>();
            var characters = new List<Vector3D>();
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var ch = e as Sandbox.Game.Entities.Character.MyCharacter;
                if (ch != null && !ch.IsDead) characters.Add(ch.PositionComp.GetPosition());
            }

            var group = new List<MyCubeGrid>(8);
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var grid = e as MyCubeGrid;
                if (grid == null || grid.Closed || grid.MarkedForClose || grid.IsStatic) continue;
                var body = grid.Physics as MyPhysicsBody;
                HkRigidBody rb = body != null ? body.RigidBody : null;
                if (rb == null || !body.Enabled) continue;
                Row r;
                try { r = Read(grid, rb, characters, group); }
                catch (Exception ex) { r = new Row { Name = grid.DisplayName + " (read failed: " + ex.GetType().Name + ")" }; }
                if (r.Awake || includeAsleep) rows.Add(r);
            }

            rows.Sort((a, b) => a.Awake != b.Awake ? (a.Awake ? -1 : 1) : b.Blocks.CompareTo(a.Blocks));
            int awake = 0;
            foreach (Row r in rows) if (r.Awake) awake++;

            var sb = new StringBuilder();
            sb.Append("awake probe: ").Append(awake).Append(" awake");
            if (includeAsleep) sb.Append(", ").Append(rows.Count - awake).Append(" asleep");
            sb.Append(" dynamic grid(s) in world bodies. Columns: blocks, speed m/s, spin rad/s, g, ")
              .Append("physical-group size (awake in group), deactivation on/off and counters, then the causes present.\n");
            foreach (Row r in rows)
            {
                sb.Append(r.Awake ? "AWAKE  " : "asleep ")
                  .Append(r.Blocks).Append("b ")
                  .Append(r.Speed.ToString("0.00")).Append("m/s ")
                  .Append(r.AngularSpeed.ToString("0.000")).Append("rad/s ")
                  .Append(r.GravityG.ToString("0.00")).Append("g ")
                  .Append("grp=").Append(r.PhysicalGroup).Append("(").Append(r.AwakeInGroup).Append(") ")
                  .Append("deact=").Append(r.DeactivationEnabled ? "on" : "OFF")
                  .Append(" c=").Append(r.Counter0).Append("/").Append(r.Counter1);
                if (r.RotorsTurning > 0) sb.Append(" rotorsTurning=").Append(r.RotorsTurning);
                if (r.PistonsMoving > 0) sb.Append(" pistonsMoving=").Append(r.PistonsMoving);
                if (r.Wheels > 0) sb.Append(" wheels=").Append(r.Wheels);
                if (r.ConnectorsLocked > 0) sb.Append(" connectorsLocked=").Append(r.ConnectorsLocked);
                if (r.GearsLocked > 0) sb.Append(" gearsLocked=").Append(r.GearsLocked);
                if (r.GyrosOverridden > 0) sb.Append(" gyrosOverridden=").Append(r.GyrosOverridden);
                if (r.ThrustersOn > 0) sb.Append(" thrustersOn=").Append(r.ThrustersOn);
                if (r.Dampeners) sb.Append(" dampeners");
                if (r.Piloted) sb.Append(" PILOTED");
                if (r.NearestCharacterM >= 0) sb.Append(" char=").Append(r.NearestCharacterM.ToString("0")).Append("m");
                sb.Append("  ").Append(r.Name).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static Row Read(MyCubeGrid grid, HkRigidBody rb, List<Vector3D> characters, List<MyCubeGrid> group)
        {
            var r = new Row
            {
                Name = grid.DisplayName ?? "(unnamed)",
                Blocks = grid.BlocksCount,
                Awake = rb.IsActive,
                Counter0 = rb.DeactivationCounter0,
                Counter1 = rb.DeactivationCounter1,
                Speed = grid.Physics.LinearVelocity.Length(),
                AngularSpeed = grid.Physics.AngularVelocity.Length(),
                Dampeners = grid.DampenersEnabled,
            };
            try { r.DeactivationEnabled = rb.EnableDeactivation; } catch { }

            Vector3D pos = grid.PositionComp.GetPosition();
            float mult;
            r.GravityG = MyGravityProviderSystem.CalculateNaturalGravityInPoint(pos, out mult).Length() / 9.81;

            // Within the grid's own radius plus 10 m: close enough to be standing on it or inside
            // it, which puts the character's proxy in the grid's simulation island.
            double reach = grid.PositionComp.WorldVolume.Radius + 10.0;
            foreach (Vector3D c in characters)
            {
                double d = Vector3D.Distance(pos, c);
                if (d <= reach && (r.NearestCharacterM < 0 || d < r.NearestCharacterM)) r.NearestCharacterM = d;
            }

            group.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Physical != null)
                MyCubeGridGroups.Static.Physical.GetGroupNodes(grid, group);
            r.PhysicalGroup = Math.Max(1, group.Count);
            foreach (MyCubeGrid g in group)
                if (g != null && g.Physics != null && g.Physics.IsActive) r.AwakeInGroup++;

            foreach (MyCubeBlock b in grid.GetFatBlocks())
            {
                var rotor = b as IMyMotorStator;
                if (rotor != null && !(b is IMyMotorSuspension))
                {
                    if (rotor.IsWorking && Math.Abs(rotor.TargetVelocityRPM) > 1e-4f && !rotor.RotorLock) r.RotorsTurning++;
                    continue;
                }
                if (b is IMyMotorSuspension) { r.Wheels++; continue; }
                var piston = b as IMyPistonBase;
                if (piston != null)
                {
                    if (piston.IsWorking && Math.Abs(piston.Velocity) > 1e-4f
                        && piston.Status != Sandbox.ModAPI.Ingame.PistonStatus.Extended
                        && piston.Status != Sandbox.ModAPI.Ingame.PistonStatus.Retracted) r.PistonsMoving++;
                    continue;
                }
                var connector = b as IMyShipConnector;
                if (connector != null)
                {
                    if (connector.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected) r.ConnectorsLocked++;
                    continue;
                }
                var gear = b as SpaceEngineers.Game.ModAPI.IMyLandingGear;
                if (gear != null)
                {
                    if (gear.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Locked) r.GearsLocked++;
                    continue;
                }
                var gyro = b as IMyGyro;
                if (gyro != null)
                {
                    if (gyro.IsWorking && gyro.GyroOverride) r.GyrosOverridden++;
                    continue;
                }
                var thrust = b as IMyThrust;
                if (thrust != null)
                {
                    if (thrust.IsWorking && thrust.CurrentThrust > 0f) r.ThrustersOn++;
                    continue;
                }
                var controller = b as IMyShipController;
                if (controller != null && controller.IsUnderControl) r.Piloted = true;
            }
            return r;
        }
    }
}
