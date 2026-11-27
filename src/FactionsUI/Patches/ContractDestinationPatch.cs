using System;
using HarmonyLib;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.Screens.Models;
using Sandbox.Game.World;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// Adds the destination to an item-delivery contract's description in the Economy Overview's
/// Active Contracts tab, which otherwise names only the issuing faction. The destination is read
/// from the GPS marker the server gave the player for that contract (matched by contract id), so it
/// carries the server's station name, and the distance is measured from the local player.
///
/// <para>Available contracts on a contract block have no marker yet and are left as SE wrote them.
/// Fails open: anything thrown leaves vanilla's text.</para>
/// </summary>
[HarmonyPatch(typeof(MyContractModelObtainAndDeliver), nameof(MyContractModelObtainAndDeliver.Description), MethodType.Getter)]
internal static class ContractDestinationPatch
{
    private static void Postfix(MyContractModelObtainAndDeliver __instance, ref string __result)
    {
        try
        {
            string destination = Destination(__instance.Id);
            if (destination != null) __result = (__result ?? "") + "\n\n" + destination;
        }
        catch (Exception ex)
        {
            Plugin.Log("contract destination not added: " + ex.Message);
        }
    }

    private static string Destination(long contractId)
    {
        MySession session = MySession.Static;
        if (session == null || session.Gpss == null || contractId == 0) return null;
        MyGps gps = session.Gpss.GetGpsByContractId(session.LocalPlayerId, contractId);
        if (gps == null) return null;

        string text = string.IsNullOrWhiteSpace(gps.Description) ? gps.Name : gps.Description;
        MyPlayer player = session.LocalHumanPlayer;
        if (player == null) return text;
        double km = Vector3D.Distance(player.GetPosition(), gps.Coords) / 1000.0;
        return text + "\nDistance: " + km.ToString(km < 10 ? "0.0" : "0") + " km";
    }
}
