using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// Lets a player sell to a station from a block that has more than one inventory.
    ///
    /// <para><c>MyStoreBlock.SellToStation</c> resolves the seller's inventory with
    /// <c>entity.TryGetInventory(out MyInventory)</c>, which is <c>GetInventory(index: 0)</c>. On a
    /// <c>MyProductionBlock</c> index 0 is the <b>input</b> inventory, so an assembler's finished
    /// components — which sit in the output inventory — count as absent and the sale is refused with
    /// <c>NotEnoughAmount</c>. A refinery and an O2/H2 generator are built the same way.</para>
    ///
    /// <para>A transpiler rather than a prefix because the goods cannot be moved into index 0 first:
    /// <c>MyAssembler.Init</c> sets <c>InputInventory.Constraint</c> from the block definition, so
    /// that inventory rejects anything the assembler cannot consume. Redirecting the two reads moves
    /// nothing, so no constraint is ever tested.</para>
    ///
    /// <para>A deliberate divergence from stock SE behaviour rather than a repair — see FAC-177.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoreBlock), "SellToStation")]
    static class SellFromAllInventoriesPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>The <c>PhysicalItem</c> branch touches the seller's inventory exactly twice, once
        /// to count and once to draw, and both are redirected to the owner-wide equivalents below.
        ///
        /// <para>The call site is against <b><c>MyInventoryBase</c></b>, not <c>MyInventory</c> — read
        /// from the emitted IL, not inferred from decompiled C#:</para>
        /// <code>
        /// callvirt instance MyFixedPoint MyInventoryBase::GetItemAmount(MyDefinitionId, MyItemFlags, bool)
        /// callvirt instance MyFixedPoint MyInventoryBase::RemoveItemsOfType(MyFixedPoint, MyDefinitionId, MyItemFlags, bool)
        /// </code>
        ///
        /// <para>A miss logs and returns the original instructions untouched. It must never throw:
        /// a throw fails its whole patch group (<c>HarmonyPatcher</c>).</para></summary>
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = new List<CodeInstruction>(instructions);

            MethodInfo getAmount = AccessTools.Method(typeof(MyInventoryBase), "GetItemAmount",
                new[] { typeof(MyDefinitionId), typeof(MyItemFlags), typeof(bool) });
            MethodInfo removeType = AccessTools.Method(typeof(MyInventoryBase), "RemoveItemsOfType",
                new[] { typeof(MyFixedPoint), typeof(MyDefinitionId), typeof(MyItemFlags), typeof(bool) });
            if (getAmount == null || removeType == null)
            {
                Log.Error("SellToStation: MyInventoryBase.GetItemAmount/RemoveItemsOfType did not resolve. "
                        + "A sale reads only the block's first inventory; nothing else is affected.");
                return original;
            }

            MethodInfo ourAmount = AccessTools.Method(typeof(SellFromAllInventoriesPatch), nameof(GetItemAmountAcrossOwner));
            MethodInfo ourRemove = AccessTools.Method(typeof(SellFromAllInventoriesPatch), nameof(RemoveItemsOfTypeAcrossOwner));

            var patched = new List<CodeInstruction>(original.Count);
            int amountCalls = 0, removeCalls = 0;

            foreach (CodeInstruction ins in original)
            {
                var called = ins.operand as MethodInfo;
                bool isCall = ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call;

                if (isCall && getAmount.Equals(called))
                {
                    // The instance is already on the stack ahead of the arguments, so a static whose
                    // first parameter is that instance consumes an identical stack.
                    patched.Add(new CodeInstruction(OpCodes.Call, ourAmount) { labels = ins.labels, blocks = ins.blocks });
                    amountCalls++;
                    continue;
                }
                if (isCall && removeType.Equals(called))
                {
                    patched.Add(new CodeInstruction(OpCodes.Call, ourRemove) { labels = ins.labels, blocks = ins.blocks });
                    removeCalls++;
                    continue;
                }
                patched.Add(ins);
            }

            if (amountCalls != 1 || removeCalls != 1)
            {
                Log.Error("SellToStation: expected one GetItemAmount and one RemoveItemsOfType call, found "
                        + amountCalls + " and " + removeCalls + ". A sale reads only the block's first "
                        + "inventory; nothing else is affected.");
                return original;
            }

            Log.Info("SellToStation: a sale reads and draws across all of a block's inventories.");
            return patched;
        }

        /// <summary>What the seller holds across every inventory of the block, not just index 0.</summary>
        public static MyFixedPoint GetItemAmountAcrossOwner(
            MyInventoryBase inventory, MyDefinitionId contentId, MyItemFlags flags, bool substitute)
        {
            if (inventory == null) return 0;

            MyEntity owner = OwnerOf(inventory);
            if (owner == null || owner.InventoryCount <= 1)
                return inventory.GetItemAmount(contentId, flags, substitute);

            MyFixedPoint total = 0;
            for (int i = 0; i < owner.InventoryCount; i++)
            {
                MyInventory inv = owner.GetInventory(i);
                if (inv != null) total += inv.GetItemAmount(contentId, flags, substitute);
            }

            if (StationStoreManager.DebugEnabled)
                Log.Info("SELL_SPAN " + contentId.SubtypeName + " owner=" + owner.GetType().Name
                    + " inventories=" + owner.InventoryCount
                    + " index0=" + ((double)inventory.GetItemAmount(contentId, flags, substitute)).ToString("0.##")
                    + " total=" + ((double)total).ToString("0.##"));
            return total;
        }

        /// <summary>Draws <paramref name="amount"/> from the block's inventories, index 0 first so an
        /// ordinary single-inventory sale behaves exactly as before.</summary>
        /// <returns>How much was actually removed — the original's own return means the same.</returns>
        public static MyFixedPoint RemoveItemsOfTypeAcrossOwner(
            MyInventoryBase inventory, MyFixedPoint amount, MyDefinitionId contentId, MyItemFlags flags, bool spawn)
        {
            if (inventory == null || amount <= 0) return 0;

            MyFixedPoint removed = inventory.RemoveItemsOfType(amount, contentId, flags, spawn);
            MyFixedPoint remaining = amount - removed;
            if (remaining <= 0) return removed;

            MyEntity owner = OwnerOf(inventory);
            if (owner == null) return removed;

            for (int i = 0; i < owner.InventoryCount && remaining > 0; i++)
            {
                MyInventory inv = owner.GetInventory(i);
                if (inv == null || ReferenceEquals(inv, inventory)) continue;

                MyFixedPoint took = inv.RemoveItemsOfType(remaining, contentId, flags, spawn);
                if (took <= 0) continue;
                removed += took;
                remaining -= took;
            }

            if (StationStoreManager.DebugEnabled)
                Log.Info("SELL_DRAW " + contentId.SubtypeName + " asked=" + ((double)amount).ToString("0.##")
                    + " removed=" + ((double)removed).ToString("0.##"));
            return removed;
        }

        /// <summary>The entity whose inventories to span. Only <c>MyInventory</c> exposes
        /// <c>Owner</c>; a base-typed inventory that is not one is left to behave as before.</summary>
        private static MyEntity OwnerOf(MyInventoryBase inventory)
        {
            var concrete = inventory as MyInventory;
            return concrete != null ? concrete.Owner : null;
        }
    }
}
