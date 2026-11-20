using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Factions.Core;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>factions asmtest</c>: a headless rig for the assembler input-tray rules
    /// (<see cref="AssemblerInputPatch"/>, FAC-504). It provides a character under its own identity,
    /// a static grid that identity owns (one LargeAssembler conveyor-joined to a large cargo container,
    /// and a charged battery), items put straight into any of the three inventories, a queue, and a
    /// status read of all of it.
    ///
    /// <para><b>Layout.</b> The two blocks are joined by reading their conveyor ports, not by a
    /// hand-placed offset that could be wrong for some block model. A probe grid holding both blocks
    /// apart is spawned 1 km away and its ports read (grid coordinates). The container is then shifted
    /// so one of its ports meets an assembler port, the probe is closed, and the real grid is
    /// spawned.</para>
    ///
    /// <para>Nothing is saved. The identity is <c>phantom-asmtest</c>, so
    /// <see cref="BenchPhantoms.SweepStaleIdentities"/> removes one a save kept anyway at the next
    /// boot.</para>
    /// </summary>
    internal static class AssemblerTestBench
    {
        private const string Name = PhantomPlan.IdentityPrefix + "asmtest";
        private const string AssemblerSubtype = "LargeAssembler";
        private const string ContainerSubtype = "LargeBlockLargeContainer";
        private const string BatterySubtype = "LargeBlockBatteryBlock";

        private static long _identityId;
        private static Vector3D _anchor;
        private static MyCharacter _character;
        private static MyCubeGrid _grid;

        internal static string Handle(string[] t)
        {
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "status";
            try
            {
                switch (sub)
                {
                    case "character": return Character(t);
                    case "grid": return Grid();
                    case "give": return Give(t);
                    case "queue": return Queue(t);
                    case "status": return Status();
                    case "clear": return Clear();
                    default: return Usage();
                }
            }
            catch (Exception ex)
            {
                return "asmtest " + sub + ": failed (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }

        private static string Usage()
        {
            return "Usage: factions asmtest character [x y z] | grid | give <in|out|cargo> <Type/Subtype> <amount|fill>"
                   + " | queue <blueprint> <amount> | queue clear | status | clear";
        }

        // ---- character -------------------------------------------------------------------------------

        private static string Character(string[] t)
        {
            if (_character != null && !_character.Closed)
                return "asmtest character: already out at " + Fmt(_character.PositionComp.GetPosition()) + ".";
            Vector3D pos;
            if (t.Length >= 6)
                pos = new Vector3D(D(t[3]), D(t[4]), D(t[5]));
            else
                pos = DefaultPosition();
            EnsureIdentity();
            _character = MyCharacter.CreateCharacter(MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up), Vector3.Zero,
                Name, null, null, null, findNearPos: true, AIMode: false, cockpit: null, useInventory: true,
                identityId: _identityId, addDefaultItems: false);
            if (_character == null) return "asmtest character: SE returned no character.";
            _character.Save = false;
            _anchor = _character.PositionComp.GetPosition();
            return "asmtest character: spawned identity=" + _identityId + " at " + Fmt(_character.PositionComp.GetPosition()) + ".";
        }

        /// <summary>Beside the first online player, so the rig can be watched; else the first point
        /// along a diagonal with no natural gravity. A character in a gravity well falls out from beside
        /// the rig: 20 km from the test world's origin it fell 14 km in 40 s.</summary>
        private static Vector3D DefaultPosition()
        {
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                if (p != null && p.Character != null && !p.Character.Closed)
                    return p.Character.PositionComp.GetPosition() + new Vector3D(0, 30, 0);
            Vector3D pos = new Vector3D(20000, 20000, 20000);
            for (int i = 0; i < 12 && MyGravityProviderSystem.CalculateNaturalGravityInPoint(pos).LengthSquared() > 1e-4f; i++)
                pos *= 2;
            return pos;
        }

        private static void EnsureIdentity()
        {
            if (_identityId != 0 && MySession.Static.Players.TryGetIdentity(_identityId) != null) return;
            _identityId = MySession.Static.Players.CreateNewIdentity(Name).IdentityId;
        }

        // ---- grid ------------------------------------------------------------------------------------

        private static string Grid()
        {
            if (_character == null || _character.Closed) return "asmtest grid: spawn the character first (factions asmtest character).";
            if (_grid != null && !_grid.Closed) return "asmtest grid: already out (" + _grid.EntityId + ").";

            Vector3D near = _anchor + new Vector3D(0, 0, -25);
            Vector3D probeAt = near + new Vector3D(0, 1000, 0);

            // 1. Probe: both blocks far apart, so their ports can be read in grid coordinates.
            var probeA = Block<MyObjectBuilder_Assembler>(AssemblerSubtype, Vector3I.Zero);
            var probeC = Block<MyObjectBuilder_CargoContainer>(ContainerSubtype, new Vector3I(20, 0, 0));
            MyCubeGrid probe = Spawn(new List<MyObjectBuilder_CubeBlock> { probeA, probeC }, probeAt, "asmtest-probe");
            if (probe == null) return "asmtest grid: the probe grid did not spawn.";
            Vector3I delta;
            BoundingBoxI aBox, cBox;
            string why;
            bool solved = Solve(probe, out delta, out aBox, out cBox, out why);
            probe.Close();
            if (!solved) return "asmtest grid: no port pairing found (" + why + ").";

            // 2. The rig: the container shifted onto an assembler port, the battery beside the container.
            Vector3I cMin = new Vector3I(20, 0, 0) + delta;
            BoundingBoxI cShifted = new BoundingBoxI(cBox.Min + delta, cBox.Max + delta);
            Vector3I bMin;
            if (!PlaceBattery(aBox, cShifted, out bMin)) return "asmtest grid: no free cell for the battery.";

            var blocks = new List<MyObjectBuilder_CubeBlock>
            {
                Block<MyObjectBuilder_Assembler>(AssemblerSubtype, Vector3I.Zero),
                Block<MyObjectBuilder_CargoContainer>(ContainerSubtype, cMin),
                Battery(bMin),
            };
            _grid = Spawn(blocks, MyEntities.FindFreePlaceCustom(near, 20f) ?? near, "asmtest");
            if (_grid == null) return "asmtest grid: the rig did not spawn.";

            // The conveyor graph is built after this frame, so whether the ports joined is read by
            // 'status', not here.
            return "asmtest grid: spawned " + _grid.EntityId + " at " + Fmt(_grid.PositionComp.GetPosition())
                   + " container@" + cMin + " battery@" + bMin + ".";
        }

        /// <summary>The shift that puts one of the container's ports onto the connecting position of
        /// one of the assembler's, with the two bodies apart.</summary>
        private static bool Solve(MyCubeGrid probe, out Vector3I delta, out BoundingBoxI aBox, out BoundingBoxI cBox, out string why)
        {
            delta = Vector3I.Zero; aBox = default(BoundingBoxI); cBox = default(BoundingBoxI); why = null;
            MySlimBlock a = null, c = null;
            foreach (MySlimBlock s in probe.GetBlocks())
            {
                if (s.FatBlock is MyAssembler) a = s;
                else if (s.FatBlock is MyCargoContainer) c = s;
            }
            var ae = a != null ? a.FatBlock as IMyConveyorEndpointBlock : null;
            var ce = c != null ? c.FatBlock as IMyConveyorEndpointBlock : null;
            if (ae == null || ce == null || ae.ConveyorEndpoint == null || ce.ConveyorEndpoint == null)
            {
                why = "a block or its conveyor endpoint is missing";
                return false;
            }
            aBox = new BoundingBoxI(a.Min, a.Max);
            cBox = new BoundingBoxI(c.Min, c.Max);
            int aPorts = ae.ConveyorEndpoint.GetLineCount(), cPorts = ce.ConveyorEndpoint.GetLineCount();
            for (int i = 0; i < aPorts; i++)
            {
                ConveyorLinePosition want = ae.ConveyorEndpoint.GetPosition(i).GetConnectingPosition();
                for (int j = 0; j < cPorts; j++)
                {
                    ConveyorLinePosition have = ce.ConveyorEndpoint.GetPosition(j);
                    if (have.Direction != want.Direction) continue;
                    Vector3I d = want.LocalGridPosition - have.LocalGridPosition;
                    var moved = new BoundingBoxI(cBox.Min + d, cBox.Max + d);
                    if (Overlaps(aBox, moved)) continue;
                    delta = d;
                    return true;
                }
            }
            why = "assembler ports=" + aPorts + " container ports=" + cPorts;
            return false;
        }

        private static bool PlaceBattery(BoundingBoxI a, BoundingBoxI c, out Vector3I min)
        {
            var candidates = new[]
            {
                new Vector3I(c.Max.X + 1, c.Min.Y, c.Min.Z), new Vector3I(c.Min.X - 1, c.Min.Y, c.Min.Z),
                new Vector3I(c.Min.X, c.Max.Y + 1, c.Min.Z), new Vector3I(c.Min.X, c.Min.Y - 1, c.Min.Z),
                new Vector3I(c.Min.X, c.Min.Y, c.Max.Z + 1), new Vector3I(c.Min.X, c.Min.Y, c.Min.Z - 1),
            };
            foreach (Vector3I m in candidates)
            {
                var box = new BoundingBoxI(m, m);
                if (!Overlaps(a, box) && !Overlaps(c, box)) { min = m; return true; }
            }
            min = Vector3I.Zero;
            return false;
        }

        private static bool Overlaps(BoundingBoxI x, BoundingBoxI y)
        {
            return x.Min.X <= y.Max.X && y.Min.X <= x.Max.X
                && x.Min.Y <= y.Max.Y && y.Min.Y <= x.Max.Y
                && x.Min.Z <= y.Max.Z && y.Min.Z <= x.Max.Z;
        }

        private static T Block<T>(string subtype, Vector3I min) where T : MyObjectBuilder_CubeBlock
        {
            var b = (T)MyObjectBuilderSerializer.CreateNewObject(typeof(T), subtype);
            b.SubtypeName = subtype;
            b.EntityId = MyEntityIdentifier.AllocateId();
            b.Min = min;
            b.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
            b.Owner = _identityId;
            b.BuiltBy = _identityId;
            b.ShareMode = MyOwnershipShareModeEnum.None;
            return b;
        }

        private static MyObjectBuilder_BatteryBlock Battery(Vector3I min)
        {
            MyObjectBuilder_BatteryBlock b = Block<MyObjectBuilder_BatteryBlock>(BatterySubtype, min);
            var def = MyDefinitionManager.Static.GetCubeBlockDefinition(new MyDefinitionId(typeof(MyObjectBuilder_BatteryBlock), BatterySubtype))
                as MyBatteryBlockDefinition;
            b.MaxStoredPower = def != null ? def.MaxStoredPower : 3f;
            b.CurrentStoredPower = b.MaxStoredPower;
            b.ProducerEnabled = true;
            return b;
        }

        private static MyCubeGrid Spawn(List<MyObjectBuilder_CubeBlock> blocks, Vector3D at, string name)
        {
            var g = new MyObjectBuilder_CubeGrid
            {
                EntityId = MyEntityIdentifier.AllocateId(),
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(at, Vector3.Forward, Vector3.Up),
                DisplayName = name,
                CubeBlocks = blocks,
                // SE's trash cleaner skips an indestructible grid (MySessionComponentTrash.GetTrashState,
                // first test). Without it a 3-block grid with no player near is gone within 30 s on a
                // world with BlockCountThreshold 20.
                GridGeneralDamageModifier = 0f,
            };
            var grid = MyEntities.CreateFromObjectBuilderAndAdd(g, true) as MyCubeGrid;
            if (grid != null) grid.Save = false;
            return grid;
        }

        // ---- items and queue -------------------------------------------------------------------------

        private static string Give(string[] t)
        {
            if (t.Length < 6) return Usage();
            MyInventory inv = Inventory(t[3]);
            if (inv == null) return "asmtest give: no '" + t[3] + "' inventory (spawn the grid; in, out or cargo).";
            MyDefinitionId id;
            if (!MyDefinitionId.TryParse("MyObjectBuilder_" + t[4], out id)) return "asmtest give: cannot parse '" + t[4] + "'.";
            var ob = MyObjectBuilderSerializer.CreateNewObject(id) as MyObjectBuilder_PhysicalObject;
            if (ob == null) return "asmtest give: '" + t[4] + "' is not a physical item.";
            MyFixedPoint fits = inv.ComputeAmountThatFits(id);
            MyFixedPoint amount = t[5].Equals("fill", StringComparison.OrdinalIgnoreCase) ? fits : (MyFixedPoint)D(t[5]);
            amount = MyFixedPoint.Min(amount, fits);
            if (amount <= 0) return "asmtest give: nothing fits.";
            inv.AddItems(amount, ob);
            return "asmtest give: " + amount + " " + t[4] + " into " + t[3] + " (" + Fill(inv) + ").";
        }

        private static string Queue(string[] t)
        {
            MyAssembler asm = Assembler();
            if (asm == null) return "asmtest queue: spawn the grid first.";
            if (t.Length >= 4 && t[3].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                asm.ClearQueue();
                return "asmtest queue: cleared.";
            }
            if (t.Length < 5) return Usage();
            var bp = MyDefinitionManager.Static.TryGetBlueprintDefinitionByResultId(new MyDefinitionId(typeof(MyObjectBuilder_Component), t[3]));
            MyDefinitionId bpId = new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), t[3]);
            MyBlueprintDefinitionBase named = MyDefinitionManager.Static.GetBlueprintDefinition(bpId);
            if (named != null) bp = named;
            if (bp == null) return "asmtest queue: no blueprint '" + t[3] + "'.";
            if (!asm.CanUseBlueprint(bp)) return "asmtest queue: the assembler cannot use '" + bp.Id.SubtypeName + "'.";
            asm.AddQueueItemRequest(bp, (MyFixedPoint)D(t[4]));
            return "asmtest queue: +" + t[4] + " " + bp.Id.SubtypeName + ".";
        }

        // ---- status / clear --------------------------------------------------------------------------

        private static string Status()
        {
            var sb = new StringBuilder("asmtest:");
            sb.Append(" character=").Append(_character != null && !_character.Closed ? Fmt(_character.PositionComp.GetPosition()) : "none");
            MyAssembler asm = Assembler();
            if (asm == null) return sb.Append(" grid=none").ToString();
            sb.Append(" grid=").Append(_grid.EntityId)
              .Append("\n assembler: working=").Append(asm.IsWorking).Append(" enabled=").Append(asm.Enabled)
              .Append(" conveyor=").Append(asm.UseConveyorSystem).Append(" state=").Append(asm.CurrentState)
              .Append(" producing=").Append(asm.IsProducing)
              .Append(" power=").Append(asm.ResourceSink.CurrentInputByType(MyResourceDistributorComponent.ElectricityId).ToString("0.###"))
              .Append('/').Append(asm.ResourceSink.RequiredInputByType(MyResourceDistributorComponent.ElectricityId).ToString("0.###")).Append(" MW");
            sb.Append("\n queue:");
            int i = 0;
            foreach (MyProductionBlock.QueueItem q in asm.Queue)
                sb.Append(" [").Append(i++).Append("] ").Append(q.Blueprint != null ? q.Blueprint.Id.SubtypeName : "?").Append(" x").Append(q.Amount);
            if (i == 0) sb.Append(" empty");
            sb.Append("\n in (").Append(Fill(asm.InputInventory)).Append("):").Append(Items(asm.InputInventory));
            sb.Append("\n out (").Append(Fill(asm.OutputInventory)).Append("):").Append(Items(asm.OutputInventory));
            MyCargoContainer box = Container();
            if (box != null) sb.Append("\n conveyor=").Append(MyGridConveyorSystem.ComputeCanTransfer(asm, box, null) ? "joined" : "NOT joined");
            if (box != null) sb.Append("\n cargo (").Append(Fill(box.GetInventory(0))).Append("):").Append(Items(box.GetInventory(0)));
            AssemblerInputPatch.State st = AssemblerInputPatch.Peek(asm);
            sb.Append("\n planner: ");
            if (st == null) sb.Append("not run yet");
            else sb.Append("mode=").Append(st.LastMode).Append(" item=").Append(st.LastRecipe ?? "-").Append(" units=").Append(st.LastUnits)
                   .Append(" plans=").Append(st.Plans).Append(" pushed=").Append(st.Pushed.ToString("0.###"))
                   .Append(" pulled=").Append(st.Pulled.ToString("0.###")).Append(" reach=").Append(st.Reach.Count)
                   .Append(st.Warned ? " FELL-BACK" : "");
            return sb.ToString();
        }

        private static string Clear()
        {
            int closed = 0;
            if (_grid != null && !_grid.Closed) { _grid.Close(); closed++; }
            if (_character != null && !_character.Closed) { _character.Close(); closed++; }
            _grid = null; _character = null;
            if (_identityId != 0)
            {
                MySession.Static.Players.RemoveIdentity(_identityId);
                _identityId = 0;
            }
            return "asmtest clear: closed " + closed + " entit" + (closed == 1 ? "y" : "ies") + " and removed the identity.";
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private static MyAssembler Assembler()
        {
            if (_grid == null || _grid.Closed) return null;
            return _grid.GetFatBlocks().OfType<MyAssembler>().FirstOrDefault();
        }

        private static MyCargoContainer Container()
        {
            if (_grid == null || _grid.Closed) return null;
            return _grid.GetFatBlocks().OfType<MyCargoContainer>().FirstOrDefault();
        }

        private static MyInventory Inventory(string which)
        {
            MyAssembler asm = Assembler();
            switch (which.ToLowerInvariant())
            {
                case "in": return asm != null ? asm.InputInventory : null;
                case "out": return asm != null ? asm.OutputInventory : null;
                case "cargo": MyCargoContainer c = Container(); return c != null ? c.GetInventory(0) : null;
                default: return null;
            }
        }

        private static string Items(MyInventory inv)
        {
            if (inv == null) return " -";
            var sb = new StringBuilder();
            foreach (MyPhysicalInventoryItem it in inv.GetItems())
            {
                MyDefinitionId id = it.Content.GetObjectId();
                sb.Append(' ').Append(id.TypeId.ToString().Replace("MyObjectBuilder_", "")).Append('/').Append(id.SubtypeName)
                  .Append('=').Append(((double)it.Amount).ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? " empty" : sb.ToString();
        }

        private static string Fill(MyInventory inv)
        {
            if (inv == null) return "-";
            return ((double)inv.CurrentVolume).ToString("0.0000", CultureInfo.InvariantCulture) + "/"
                   + ((double)inv.MaxVolume).ToString("0.####", CultureInfo.InvariantCulture) + " m3";
        }

        private static double D(string s)
        {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Fmt(Vector3D v)
        {
            return v.X.ToString("0", CultureInfo.InvariantCulture) + "," + v.Y.ToString("0", CultureInfo.InvariantCulture)
                   + "," + v.Z.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
