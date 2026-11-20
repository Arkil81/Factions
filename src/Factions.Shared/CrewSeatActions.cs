using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;

namespace Factions
{
    /// <summary>
    /// The crew orders on every seat's toolbar list — <i>Crew: mount up</i>, <i>dismount</i>,
    /// <i>at ease</i>, <i>automatic</i> — registered the same way on the server and the client
    /// (docs/ground-operations.md). A cockpit, passenger seat or control seat carries them; a
    /// cryo chamber does not.
    ///
    /// <para>Built exactly as <see cref="WingAntennaActions"/>, for the same reasons: a toolbar slot
    /// saves an action id that each end resolves against its own registration, and the actions go
    /// in through <c>CustomActionGetter</c> so the seat's vanilla controls are still built.</para>
    ///
    /// <para>A press runs on the machine whose toolbar holds the slot: the presser's client for a
    /// seat they sit in, the server for a timer, button panel or event controller.</para>
    /// </summary>
    public static class CrewSeatActions
    {
        private static List<IMyTerminalAction> _actions;
        private static Action<IMyTerminalBlock, WireCrewOrder> _apply;
        private static IMyTerminalControls _controls;

        /// <summary>True once the actions are registered with the terminal.</summary>
        public static bool Registered { get; private set; }

        /// <summary>Why <see cref="Register"/> failed; null when it did not.</summary>
        public static string FailureReason { get; private set; }

        /// <summary>Creates the actions and hooks them into every seat's action list, with
        /// <paramref name="apply"/> as what a press does. Idempotent: a second call only replaces the
        /// handler. Must run on the game thread once the session's terminal controls exist.</summary>
        public static bool Register(Action<IMyTerminalBlock, WireCrewOrder> apply)
        {
            _apply = apply;
            if (Registered) return true;
            FailureReason = null;
            try
            {
                IMyTerminalControls controls = MyAPIGateway.TerminalControls;
                if (controls == null)
                {
                    FailureReason = "no terminal controls";
                    return false;
                }
                var actions = new List<IMyTerminalAction>();
                foreach (WireCrewOrder order in CrewActions.All)
                {
                    IMyTerminalAction a = controls.CreateAction<IMyCockpit>(CrewActions.Id(order));
                    if (a == null)
                    {
                        FailureReason = "CreateAction returned null for " + CrewActions.Id(order);
                        return false;
                    }
                    WireCrewOrder o = order;
                    a.Name = new StringBuilder(CrewActions.Name(order));
                    a.Icon = IconFor(order);
                    a.ValidForGroups = false;
                    a.Enabled = IsSeat;
                    a.Action = b => Press(b, o);
                    a.Writer = (b, sb) => { };
                    actions.Add(a);
                }
                _actions = actions;
                _controls = controls;
                controls.CustomActionGetter += OnCustomActionGetter;
                Registered = true;
                return true;
            }
            catch (Exception ex)
            {
                FailureReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Unhooks the actions. A slot that names one keeps its id, and finds it again on the
        /// next registration.</summary>
        public static void Unregister()
        {
            _apply = null;
            if (!Registered) return;
            try
            {
                if (_controls != null) _controls.CustomActionGetter -= OnCustomActionGetter;
            }
            catch { }
            _controls = null;
            _actions = null;
            Registered = false;
        }

        private static bool IsSeat(IMyTerminalBlock block)
        {
            return block is IMyCockpit && !(block is IMyCryoChamber);
        }

        private static void Press(IMyTerminalBlock block, WireCrewOrder order)
        {
            Action<IMyTerminalBlock, WireCrewOrder> apply = _apply;
            if (apply != null && block != null) apply(block, order);
        }

        private static void OnCustomActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            List<IMyTerminalAction> ours = _actions;
            if (ours == null || actions == null || !IsSeat(block)) return;
            actions.AddRange(ours);
        }

        private static string IconFor(WireCrewOrder order)
        {
            switch (order)
            {
                case WireCrewOrder.Mount:    return "Textures\\GUI\\Icons\\Actions\\CharacterSwitchOn.dds";
                case WireCrewOrder.Dismount: return "Textures\\GUI\\Icons\\Actions\\CharacterSwitchOff.dds";
                case WireCrewOrder.Ease:     return "Textures\\GUI\\Icons\\Actions\\NeutralSwitchOff.dds";
                default:                     return "Textures\\GUI\\Icons\\Actions\\Reset.dds";
            }
        }
    }
}
