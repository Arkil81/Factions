using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Factions.Commands
{
    /// <summary>Crew orders by chat (docs/ground-operations.md). The hull is the crewed one the
    /// caller sits or stands in, else the nearest crewed hull within vanilla's bot range; the caller
    /// must own it or share a faction with its owner.</summary>
    public class PlayerCrewCommands : CommandModule
    {
        [Command("factions crew mount", "Your ship's crew boards and stays seated: !factions crew mount")]
        [Permission(MyPromoteLevel.None)]
        public void Mount() { Order(CrewOrder.Mount); }

        [Command("factions crew dismount", "Your ship's crew gets out and forms up behind it: !factions crew dismount")]
        [Permission(MyPromoteLevel.None)]
        public void Dismount() { Order(CrewOrder.Dismount); }

        [Command("factions crew ease", "Your ship's crew walks about freely: !factions crew ease")]
        [Permission(MyPromoteLevel.None)]
        public void Ease() { Order(CrewOrder.Ease); }

        [Command("factions crew auto", "Your ship's crew sits on the ground and walks in space: !factions crew auto")]
        [Permission(MyPromoteLevel.None)]
        public void Auto() { Order(CrewOrder.Auto); }

        private void Order(CrewOrder order)
        {
            IMyPlayer player = Context.Player;
            if (player == null) { Context.Respond("Run this in-game; the bridge form is factions crew order <grid> <order>."); return; }
            MyCubeGrid hull = CrewPresence.OrderHull(player.Character as MyCharacter, null);
            if (hull == null) { Context.Respond("No crewed ship within " + CrewOrderControl.BotRangeM.ToString("0") + " m. Board one first."); return; }
            bool ok;
            Context.Respond(CrewOrderControl.Issue(hull, player.IdentityId, false, order, CrewPresence.CrewCount(hull.EntityId), out ok));
        }
    }
}
