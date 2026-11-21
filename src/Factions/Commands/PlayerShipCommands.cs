using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// Player-permission command for capturing a ship into the Factions build pool from Steam
    /// Workshop / player-built hulls the vanilla prefab scan never sees — see
    /// <see cref="FactionRuntime.CaptureShipForPlayer"/> for the full capture pipeline.
    /// </summary>
    public class PlayerShipCommands : CommandModule
    {
        [Command("factions ship capture",
            "Capture the ship you're looking at: !factions ship capture <own|all> <name>")]
        [Permission(MyPromoteLevel.None)]
        public void Capture(string scope = null, string name = null)
        {
            AgentCaptures relayed = FactionsPlugin.Instance != null ? FactionsPlugin.Instance.AgentCaptures : null;
            if (relayed != null)
            {
                if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(name)) { Context.Respond("Usage: !factions ship capture <own|all> <name>"); return; }
                Context.Respond(relayed.Chat(NexusCaptureKind.Ship, Context.Player, scope, name, null));
                return;
            }
            FactionState owner;
            string error;
            if (!PlayerCommandHelpers.TryResolveCallerFaction(Context, out owner, out error)) { Context.Respond(error); return; }

            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(name))
            {
                Context.Respond("Usage: !factions ship capture <own|all> <name>");
                return;
            }

            Context.Respond(FactionsPlugin.Instance.Runtime.CaptureShipForPlayer(owner, Context.Player, scope, name));
        }
    }
}
