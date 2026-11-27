using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Factions.Commands
{
    /// <summary>
    /// <c>!factions adminintel [on|off]</c>: whether the Factions page shows an admin every faction's
    /// true figures and treasury, or the same estimates any player gets. Off until the admin turns it
    /// on, and remembered across restarts (<see cref="Factions.Core.AdminIntelView"/>). Admin-only by
    /// its own check, not <c>[Permission]</c>, so it also works when forwarded from a Nexus agent.
    /// </summary>
    public class AdminIntelCommands : CommandModule
    {
        [Command("factions adminintel", "Admins: see every faction's true figures on the Factions page, or play with estimates: !factions adminintel [on|off]")]
        [Permission(MyPromoteLevel.None)]
        public void AdminIntel(string state = null)
        {
            CommandCaller caller = PlayerCommandHelpers.Caller(Context);
            if (caller == null) { Context.Respond("Run this in-game, not from the server console."); return; }
            if (!PlayerCommandHelpers.IsAdmin(Context)) { Context.Respond("Only an admin has an admin view."); return; }
            PlayerFactionIntelManager intel = FactionsPlugin.Instance != null ? FactionsPlugin.Instance.FactionIntel : null;
            if (intel == null) { Context.Respond("Faction intel is not available on this server right now."); return; }

            if (string.IsNullOrEmpty(state))
            {
                Context.Respond(intel.AdminViewOn(caller.SteamId)
                    ? "Admin view is on: the Factions page shows every faction's true figures and treasury. Turn it off with !factions adminintel off."
                    : "Admin view is off: the Factions page shows you the same estimates as any player. Turn it on with !factions adminintel on.");
                return;
            }
            bool on;
            if (string.Equals(state, "on", System.StringComparison.OrdinalIgnoreCase)) on = true;
            else if (string.Equals(state, "off", System.StringComparison.OrdinalIgnoreCase)) on = false;
            else { Context.Respond("Use !factions adminintel on, or !factions adminintel off."); return; }

            bool saved = intel.SetAdminView(caller.SteamId, on);
            Context.Respond((on
                    ? "Admin view on: the Factions page shows every faction's true figures and treasury."
                    : "Admin view off: the Factions page shows you the same estimates as any player.")
                + " Reselect a faction to refresh it."
                + (saved ? "" : " It could not be saved, so it lasts until the next restart."));
        }
    }
}
