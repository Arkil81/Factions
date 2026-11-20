using System;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Who may act for a player faction. Leading it — the SE leader or founder flag — or being a server
    /// admin, who keeps the authority SE itself gives an admin over every faction. Read from SE on every
    /// call and never cached: promotion, demotion and the admin level all change while a player is
    /// online.
    ///
    /// <para>One answer for every surface that can spend a faction's treasury or components, or set its
    /// diplomacy,
    /// chat and the FactionsUI client alike, so the two cannot drift apart. Reading is not gated
    /// here: any member may see what their faction holds and has ordered.</para>
    /// </summary>
    internal static class PlayerAuthority
    {
        /// <summary>True when <paramref name="identityId"/> is leader or founder of the SE faction
        /// tagged <paramref name="factionTag"/>.</summary>
        internal static bool IsLeader(long identityId, string factionTag)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return false;
            MyFaction se = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (se == null || !string.Equals(se.Tag, factionTag, StringComparison.OrdinalIgnoreCase)) return false;
            return se.IsLeader(identityId) || se.IsFounder(identityId);
        }

        /// <summary>True when <paramref name="identityId"/> is a server admin or above.</summary>
        internal static bool IsAdmin(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Players == null) return false;
            ulong steamId = MySession.Static.Players.TryGetSteamId(identityId);
            return steamId != 0UL && MySession.Static.GetUserPromoteLevel(steamId) >= MyPromoteLevel.Admin;
        }

        /// <summary>True when <paramref name="identityId"/> may spend <paramref name="factionTag"/>'s
        /// treasury or components, change any setting that spends them, or act on its diplomacy.</summary>
        internal static bool MayCommand(long identityId, string factionTag)
        {
            return IsLeader(identityId, factionTag) || IsAdmin(identityId);
        }

        /// <summary>The sentence a refused member is shown, or null when they may act.</summary>
        internal static string SpendRefusal(long identityId, string factionTag)
        {
            if (MayCommand(identityId, factionTag)) return null;
            return "Only a leader of [" + factionTag + "] can spend its treasury or components, or change what it is set to spend.";
        }
    }
}
