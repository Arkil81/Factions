using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Factions.Commands
{
    /// <summary>
    /// <c>!factions kit</c> lists the kits the caller may claim, with each one's description and wait;
    /// <c>!factions kit &lt;name&gt;</c> claims one (<see cref="KitService"/>). Runs on the server the
    /// player is connected to, since the items go to their character there; on a Nexus agent the
    /// listing and the result arrive as a whisper once the authority has answered.
    /// </summary>
    public class PlayerKitCommands : CommandModule
    {
        [Command("factions kit", "List the kits you can claim, or claim one: !factions kit [name]")]
        [Permission(MyPromoteLevel.None)]
        public void Kit(string name = null)
        {
            KitService kits = FactionsPlugin.Instance != null ? FactionsPlugin.Instance.Kits : null;
            if (kits == null) { Context.Respond("Kits are not available on this server right now."); return; }
            CommandCaller caller = PlayerCommandHelpers.Caller(Context);
            if (caller == null) { Context.Respond("Run this in-game, not from the server console."); return; }
            string said = kits.Run(caller, name);
            if (said != null) Context.Respond(said);
        }
    }
}
