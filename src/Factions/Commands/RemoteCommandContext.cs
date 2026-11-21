using System.Collections.Generic;
using Torch.API;
using Torch.API.Plugins;
using Torch.Commands;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions.Commands
{
    /// <summary>The player a command runs for, wherever they are connected.</summary>
    internal sealed class CommandCaller
    {
        public ulong SteamId;
        /// <summary>This server's identity for them.</summary>
        public long IdentityId;
        public MyPromoteLevel Promote;
        public bool HasPosition;
        /// <summary>Their character's world position, in metres.</summary>
        public Vector3D PositionM;
    }

    /// <summary>
    /// A Factions command typed by a player connected to a Nexus agent, run on the authority. Torch's
    /// own context finds its player among this server's connected players, which a relayed caller is
    /// not among, so the caller travels with the context instead, and what the command answers is
    /// collected to go back to the agent rather than sent as chat from here.
    /// </summary>
    internal sealed class RemoteCommandContext : CommandContext
    {
        public CommandCaller Caller { get; private set; }
        public readonly List<string> Responses = new List<string>();

        public RemoteCommandContext(ITorchBase torch, ITorchPlugin plugin, CommandCaller caller,
                                    string rawArgs, List<string> args)
            : base(torch, plugin, caller != null ? caller.SteamId : 0UL, rawArgs, args)
        {
            Caller = caller;
        }

        public override void Respond(string message, string sender = null, string font = null)
        {
            if (message != null) Responses.Add(message);
        }
    }
}
