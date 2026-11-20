using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;

namespace Factions
{
    /// <summary>
    /// The <i>Intruder Alarm</i> event on every Event Controller: its first toolbar column runs when
    /// an intruder is detected aboard the controller's ship, its second when the ship's alarm stands
    /// down (FAC-440).
    ///
    /// <para><b>Why both ends.</b> SE builds the event list on each client from the
    /// <see cref="IMyEventControllerEntityComponent"/> components on the block, and resolves the saved
    /// selection on the server the same way. The server needs the component to keep a selection made by
    /// a client; the client needs it to offer the event at all. A player without FactionsUI does not see
    /// the event, but a block one has configured still fires on the server for everyone.</para>
    ///
    /// <para><b>Never serialized</b> (<c>IsSerialized</c> is false): a vanilla client receives the
    /// block's object builder and has no type to read it with. The selection itself is the block's own
    /// <c>SelectedEvent</c> id, which vanilla saves. It is attached before the block's <c>Init</c>,
    /// because <c>Init</c> resolves that saved id against the components present at that moment.</para>
    ///
    /// <para>It has no block list, threshold or condition: the host decides when an intruder is aboard
    /// and calls <c>TriggerAction</c> itself.</para>
    /// </summary>
    public sealed class IntruderAlarmEvent : MyEntityComponentBase, IMyEventControllerEntityComponent
    {
        /// <summary>The saved selection id. Vanilla events use small definition ids.</summary>
        public const long SelectionId = 440000001L;

        private static readonly MyStringId DisplayName = MyStringId.GetOrCompute("Intruder Alarm");

        public override string ComponentTypeDebugString { get { return "FactionsIntruderAlarm"; } }
        public long UniqueSelectionId { get { return SelectionId; } }
        public MyStringId EventDisplayName { get { return DisplayName; } }
        public bool IsSelected { get; set; }
        public string YesNoToolbarYesDescription { get { return "Intruder aboard"; } }
        public string YesNoToolbarNoDescription { get { return "All clear"; } }
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }

        /// <summary>True once the attach patch is in place.</summary>
        public static bool Active { get; private set; }

        /// <summary>Why <see cref="TryApply"/> did not bind; null when it did.</summary>
        public static string InactiveReason { get; private set; }

        /// <summary>Patches the Event Controller's <c>Init</c> to attach the event. Never throws.</summary>
        public static void TryApply(Harmony harmony)
        {
            Active = false;
            InactiveReason = null;
            try
            {
                if (harmony == null) { InactiveReason = "no Harmony instance"; return; }
                Type block = AccessTools.TypeByName("SpaceEngineers.Game.Entities.Blocks.MyEventControllerBlock");
                MethodInfo init = block != null
                    ? AccessTools.Method(block, "Init", new[] { typeof(MyObjectBuilder_CubeBlock), typeof(MyCubeGrid) })
                    : null;
                if (init == null) { InactiveReason = "MyEventControllerBlock.Init not found"; return; }
                MethodInfo attach = typeof(IntruderAlarmEvent).GetMethod("Attach", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(init, prefix: new HarmonyMethod(attach));
                Active = true;
            }
            catch (Exception ex)
            {
                InactiveReason = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void Attach(MyEntity __instance)
        {
            if (__instance != null && !__instance.Components.Has<IntruderAlarmEvent>())
                __instance.Components.Add(new IntruderAlarmEvent());
        }
    }
}
