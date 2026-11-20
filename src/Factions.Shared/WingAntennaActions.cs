using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;

namespace Factions
{
    /// <summary>
    /// The wing actions on every antenna's toolbar list — <i>Wing: form on me</i>, <i>focus my
    /// target</i>, <i>hold</i>, <i>break off</i>, <i>return</i>, <i>release</i>, and on a station
    /// <i>protect me</i> — registered the same
    /// way on the server and the client (docs/fleet-tactics.md, "The interface").
    ///
    /// <para><b>Why both ends.</b> A toolbar slot is saved as the block's entity id and the action id.
    /// The server resolves that id against its own action list whenever a block it runs (button
    /// panel, timer, event controller) activates the slot, and the client does when its player
    /// presses one; an id registered on only one end is a slot that silently does nothing on the
    /// other. So the ids come from one table (<see cref="WingActions"/>) and the registration is this
    /// one file, compiled into both.</para>
    ///
    /// <para><b>Through <c>CustomActionGetter</c>, not the per-type action list.</b>
    /// <c>MyToolbarItemTerminalBlock</c> lists a block's actions through
    /// <c>MyTerminalControls.GetActions(block)</c>, which runs the getter, so the actions appear in
    /// a slot's list and are found again by id. Adding to the type's list instead
    /// (<c>AddAction&lt;IMyRadioAntenna&gt;</c>) creates that list if the antenna's vanilla controls
    /// have not been built yet, and vanilla then skips building them. Group slots
    /// (<c>MyToolbarItemTerminalGroup</c>) read the type's list and do not show these actions.</para>
    ///
    /// <para><b>What each end does with a press</b> is its own: <see cref="Register"/> takes the
    /// handler. The client sends the command over the wire; the server takes it as a block's trigger,
    /// with no presser. A press runs on the machine whose toolbar holds the slot — the presser's
    /// client for a cockpit, the server for a block — and nowhere else.</para>
    /// </summary>
    public static class WingAntennaActions
    {
        private static List<IMyTerminalAction> _actions;
        private static Action<IMyTerminalBlock, WireWingCommand> _apply;
        private static IMyTerminalControls _controls;

        /// <summary>True once the actions are registered with the terminal.</summary>
        public static bool Registered { get; private set; }

        /// <summary>Why <see cref="Register"/> failed; null when it did not.</summary>
        public static string FailureReason { get; private set; }

        /// <summary>Creates the actions and hooks them into every antenna's action list, with
        /// <paramref name="apply"/> as what a press does. Idempotent: a second call only replaces the
        /// handler. Must run on the game thread once the session's terminal controls exist.</summary>
        public static bool Register(Action<IMyTerminalBlock, WireWingCommand> apply)
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
                foreach (WireWingCommand command in WingActions.All)
                {
                    IMyTerminalAction a = controls.CreateAction<IMyRadioAntenna>(WingActions.Id(command));
                    if (a == null)
                    {
                        FailureReason = "CreateAction returned null for " + WingActions.Id(command);
                        return false;
                    }
                    WireWingCommand c = command;
                    a.Name = new StringBuilder(WingActions.Name(command));
                    a.Icon = IconFor(command);
                    a.ValidForGroups = false;
                    a.Enabled = b => b is IMyRadioAntenna;
                    a.Action = b => Press(b, c);
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

        private static void Press(IMyTerminalBlock block, WireWingCommand command)
        {
            Action<IMyTerminalBlock, WireWingCommand> apply = _apply;
            if (apply != null && block != null) apply(block, command);
        }

        private static void OnCustomActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            List<IMyTerminalAction> ours = _actions;
            if (ours == null || actions == null || !(block is IMyRadioAntenna)) return;
            actions.AddRange(ours);
        }

        private static string IconFor(WireWingCommand command)
        {
            switch (command)
            {
                case WireWingCommand.FormOnMe:    return "Textures\\GUI\\Icons\\Actions\\SmallShipSwitchOn.dds";
                case WireWingCommand.FocusTarget: return "Textures\\GUI\\Icons\\Actions\\SubsystemTargeting_Weapons.dds";
                case WireWingCommand.Hold:        return "Textures\\GUI\\Icons\\Actions\\StationSwitchOn.dds";
                case WireWingCommand.BreakOff:    return "Textures\\GUI\\Icons\\Actions\\Reverse.dds";
                case WireWingCommand.Return:      return "Textures\\GUI\\Icons\\Actions\\SendSignal.dds";
                case WireWingCommand.ProtectMe:   return "Textures\\GUI\\Icons\\Actions\\SendSignal.dds";
                default:                          return "Textures\\GUI\\Icons\\Actions\\SmallShipSwitchOff.dds";
            }
        }
    }
}
