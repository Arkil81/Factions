using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Factions.Core;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// Replaces vanilla's input-tray handling for every assembler in assemble mode, survival kits
    /// included, with <see cref="AssemblerInputPlanner"/>'s rules. Vanilla could leave the tray full of
    /// partial sets for good (FAC-504).
    ///
    /// <para><b>The seam.</b> <c>MyAssembler.GetComponentsFromConveyor</c> (<c>MyAssembler.cs:652</c>)
    /// is vanilla's only input pull in assemble mode. <c>UpdateAssembleMode</c> calls it from
    /// <c>DoUpdateTimerTick</c> on the server, every 1–4 s by presence tier, and only while the block
    /// is working, enabled and on the conveyor system. A block with conveyor use off is therefore
    /// never touched, and neither is disassembly (<c>UpdateDisassembleMode</c>).</para>
    ///
    /// <para><b>Where items go.</b> They are pushed only into conveyor-reachable cargo containers.
    /// Containers already holding a stack of the item come first, so stacks combine.
    /// <c>MyInventory.Transfer</c> merges into an existing stack. Reachability for each item uses
    /// vanilla's own <c>CanTransfer</c>, which is size-aware: a large item never routes through a
    /// small conveyor. It caches its answer asynchronously, so a first push can wait a tick. Pulls go
    /// through vanilla's <c>PullItem</c>, after drawing on the block's own output tray first. A tier-2
    /// tool's tier-1 input usually sits there.</para>
    ///
    /// <para><b>Failure.</b> An exception falls back to vanilla for that tick, warned once per block.
    /// Cooperative mode keeps vanilla's behaviour: an idle slave still asks a master for work.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyAssembler), "GetComponentsFromConveyor")]
    internal static class AssemblerInputPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static readonly MethodInfo CanTransferM = AccessTools.Method(typeof(MyGridConveyorSystem), "CanTransfer",
            new[] { typeof(IMyConveyorEndpointBlock), typeof(IMyConveyorEndpointBlock), typeof(MyDefinitionId), typeof(bool), typeof(bool) });
        private static readonly MethodInfo FromOtherAssemblersM = AccessTools.Method(typeof(MyAssembler), "GetItemFromOtherAssemblers");

        /// <summary>How long a block's reachable-inventory list is trusted before it is walked again.
        /// Vanilla's own list refreshes only when a terminal opens.</summary>
        private const long ReachRefreshMs = 10000;
        private const double Eps = 1e-6;

        /// <summary>Per-block state. Weakly keyed, so a closed block takes it with it.</summary>
        internal sealed class State
        {
            public long ReachStamp = long.MinValue;
            public readonly List<MyCubeBlock> Reach = new List<MyCubeBlock>();
            public string LastKey;
            public AssemblerInputMode LastMode;
            public string LastRecipe;
            public int LastUnits;
            public double Pushed;
            public double Pulled;
            public int Plans;
            public bool Warned;
        }

        private static readonly ConditionalWeakTable<MyAssembler, State> States = new ConditionalWeakTable<MyAssembler, State>();

        /// <summary>The planner's last read of <paramref name="asm"/>, for the test bench. Null before
        /// its first tick.</summary>
        internal static State Peek(MyAssembler asm)
        {
            State s;
            return asm != null && States.TryGetValue(asm, out s) ? s : null;
        }

        [HarmonyPrefix]
        private static bool Prefix(MyAssembler __instance)
        {
            if (__instance == null || __instance.InputInventory == null || __instance.CubeGrid == null) return true;
            State st = States.GetOrCreateValue(__instance);
            try
            {
                Manage(__instance, st);
                return false;
            }
            catch (Exception ex)
            {
                if (!st.Warned)
                {
                    st.Warned = true;
                    Log.Warn("Assembler input: " + __instance.EntityId + " fell back to vanilla (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
                return true;
            }
        }

        private static void Manage(MyAssembler asm, State st)
        {
            MyInventory input = asm.InputInventory;
            MyInventory output = asm.OutputInventory;
            var ids = new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal);

            var queue = new List<AssemblerQueueEntry>();
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (MyProductionBlock.QueueItem qi in asm.Queue)
            {
                MyBlueprintDefinitionBase bp = qi.Blueprint;
                if (bp == null) continue;
                double mult = 1.0 / asm.GetEfficiencyMultiplierForBlueprint(bp);
                var e = new AssemblerQueueEntry { Recipe = bp.Id.SubtypeName, Amount = (double)qi.Amount };
                foreach (MyBlueprintDefinitionBase.Item pre in bp.Prerequisites)
                {
                    string k = Key(pre.Id, ids);
                    needed.Add(k);
                    e.PerUnit.Add(new KeyValuePair<string, double>(k, (double)pre.Amount * mult));
                }
                queue.Add(e);
            }

            Dictionary<string, double> tray = Contents(input, ids, null);
            if (queue.Count == 0 && tray.Count == 0)
            {
                Note(asm, st, AssemblerInputMode.Idle, null, 0, 0, 0);
                AskMaster(asm);
                return;
            }

            RefreshReach(asm, st);
            var reach = new Dictionary<string, double>(StringComparer.Ordinal);
            if (needed.Count > 0)
            {
                if (output != null) AddContents(reach, output, ids, needed);
                foreach (MyCubeBlock b in st.Reach)
                {
                    if (b == null || b.Closed) continue;
                    for (int i = 0; i < b.InventoryCount; i++)
                    {
                        MyInventory inv = b.GetInventory(i);
                        if (inv == null || inv == input || (inv.GetFlags() & MyInventoryFlags.CanSend) == 0) continue;
                        AddContents(reach, inv, ids, needed);
                    }
                }
            }

            AssemblerInputPlan plan = AssemblerInputPlanner.Plan(queue, tray, reach,
                k => UnitVolume(ids, k), (double)input.MaxVolume);
            st.Plans++;

            double pushed = 0, pulled = 0;
            var sb = DebugTraces.AssemblerInput ? new StringBuilder() : null;
            foreach (KeyValuePair<string, double> p in plan.PushOut)
            {
                double moved = Push(asm, st, input, ids[p.Key], p.Value);
                pushed += moved;
                if (sb != null && moved > Eps) sb.Append(" out:").Append(Short(p.Key)).Append('=').Append(moved.ToString("0.###"));
            }
            foreach (KeyValuePair<string, double> p in plan.PullIn)
            {
                double moved = Pull(asm, input, output, ids[p.Key], p.Value);
                pulled += moved;
                if (sb != null && moved > Eps) sb.Append(" in:").Append(Short(p.Key)).Append('=').Append(moved.ToString("0.###"));
            }
            st.Pushed += pushed;
            st.Pulled += pulled;

            string recipe = plan.QueueIndex >= 0 ? queue[plan.QueueIndex].Recipe : null;
            Note(asm, st, plan.Mode, recipe, plan.Units, pushed, pulled, sb);
            if (plan.Mode == AssemblerInputMode.Idle) AskMaster(asm);
        }

        /// <summary>Records the plan and writes ASSEMBLER_INPUT when its mode or item changed.</summary>
        private static void Note(MyAssembler asm, State st, AssemblerInputMode mode, string recipe, int units,
            double pushed, double pulled, StringBuilder moves = null)
        {
            string key = mode + "|" + recipe;
            bool changed = key != st.LastKey;
            st.LastKey = key;
            st.LastMode = mode;
            st.LastRecipe = recipe;
            st.LastUnits = units;
            if (!changed || !DebugTraces.AssemblerInput) return;
            Log.Info("ASSEMBLER_INPUT asm=" + asm.EntityId + " grid=" + asm.CubeGrid.DisplayName + " mode=" + mode
                     + " item=" + (recipe ?? "-") + " units=" + units
                     + " pushed=" + pushed.ToString("0.###") + " pulled=" + pulled.ToString("0.###")
                     + (moves != null && moves.Length > 0 ? moves.ToString() : ""));
        }

        /// <summary>Vanilla's tail of <c>GetComponentsFromConveyor</c>: an idle cooperative slave
        /// asks a master for part of its queue.</summary>
        private static void AskMaster(MyAssembler asm)
        {
            if (!asm.IsSlave || asm.RepeatEnabled || FromOtherAssemblersM == null) return;
            FromOtherAssemblersM.Invoke(asm, new object[] { 5f });
        }

        private static void RefreshReach(MyAssembler asm, State st)
        {
            long now = Environment.TickCount;
            if (st.ReachStamp != long.MinValue && now - st.ReachStamp < ReachRefreshMs && now >= st.ReachStamp) return;
            st.ReachStamp = now;
            st.Reach.Clear();
            var found = new List<IMyConveyorEndpoint>();
            MyGridConveyorSystem.FindReachable(asm.ConveyorEndpoint, found,
                v => v.CubeBlock != null && v.CubeBlock != asm && v.CubeBlock.HasInventory && asm.FriendlyWithBlock(v.CubeBlock));
            var seen = new HashSet<long>();
            foreach (IMyConveyorEndpoint v in found)
                if (seen.Add(v.CubeBlock.EntityId)) st.Reach.Add(v.CubeBlock);
        }

        /// <summary>Moves up to <paramref name="amount"/> of <paramref name="id"/> from the tray into
        /// reachable cargo containers, combining with existing stacks first. Returns what moved.</summary>
        private static double Push(MyAssembler asm, State st, MyInventory src, MyDefinitionId id, double amount)
        {
            var withStack = new List<MyInventory>();
            var rest = new List<MyInventory>();
            var owner = new Dictionary<MyInventory, MyCubeBlock>();
            foreach (MyCubeBlock b in st.Reach)
            {
                if (!(b is MyCargoContainer) || b.Closed) continue;
                for (int i = 0; i < b.InventoryCount; i++)
                {
                    MyInventory inv = b.GetInventory(i);
                    if (inv == null || (inv.GetFlags() & MyInventoryFlags.CanReceive) == 0 || !inv.CheckConstraint(id)) continue;
                    owner[inv] = b;
                    if (inv.GetItemAmount(id) > 0) withStack.Add(inv); else rest.Add(inv);
                }
            }
            withStack.AddRange(rest);

            double left = amount;
            foreach (MyInventory dst in withStack)
            {
                if (left <= Eps) break;
                double fits = (double)dst.ComputeAmountThatFits(id);
                if (fits <= Eps) continue;
                if (!CanTransfer(asm, owner[dst] as IMyConveyorEndpointBlock, id, true)) continue;
                double want = Math.Min(left, fits);
                double moved = (double)MyInventory.Transfer(src, dst, id, MyItemFlags.None, (MyFixedPoint)want);
                left -= moved;
            }
            return amount - left;
        }

        /// <summary>Brings up to <paramref name="amount"/> of <paramref name="id"/> into the tray, from
        /// the block's own output tray first, then over the conveyors. Returns what arrived.</summary>
        private static double Pull(MyAssembler asm, MyInventory input, MyInventory output, MyDefinitionId id, double amount)
        {
            double left = amount;
            if (output != null)
            {
                double own = Math.Min(left, (double)output.GetItemAmount(id));
                if (own > Eps) left -= (double)MyInventory.Transfer(output, input, id, MyItemFlags.None, (MyFixedPoint)own);
            }
            if (left > Eps)
                left -= (double)asm.CubeGrid.GridSystems.ConveyorSystem.PullItem(id, (MyFixedPoint)left, asm, input, false, false);
            return amount - left;
        }

        private static bool CanTransfer(MyAssembler asm, IMyConveyorEndpointBlock dst, MyDefinitionId id, bool push)
        {
            if (dst == null) return false;
            if (CanTransferM == null) return true;
            return (bool)CanTransferM.Invoke(null, new object[] { asm, dst, id, push, false });
        }

        private static Dictionary<string, double> Contents(MyInventory inv, Dictionary<string, MyDefinitionId> ids, HashSet<string> only)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            AddContents(d, inv, ids, only);
            return d;
        }

        private static void AddContents(Dictionary<string, double> d, MyInventory inv, Dictionary<string, MyDefinitionId> ids, HashSet<string> only)
        {
            foreach (MyPhysicalInventoryItem item in inv.GetItems())
            {
                if (item.Content == null) continue;
                string k = Key(item.Content.GetObjectId(), ids);
                if (only != null && !only.Contains(k)) continue;
                double v;
                d.TryGetValue(k, out v);
                d[k] = v + (double)item.Amount;
            }
        }

        private static string Key(MyDefinitionId id, Dictionary<string, MyDefinitionId> ids)
        {
            string k = id.ToString();
            if (!ids.ContainsKey(k)) ids[k] = id;
            return k;
        }

        private static double UnitVolume(Dictionary<string, MyDefinitionId> ids, string key)
        {
            MyDefinitionId id;
            MyPhysicalItemDefinition def;
            if (ids.TryGetValue(key, out id) && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out def))
                return def.Volume;
            return 0;
        }

        private static string Short(string key)
        {
            return key.StartsWith("MyObjectBuilder_", StringComparison.Ordinal) ? key.Substring(16) : key;
        }
    }
}
