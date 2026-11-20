using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Factions.Wire;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Localization;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage;
using VRage.Game.ModAPI;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// Aims SE's ingame-help "economy station" objective at the station the server already chose for
/// this player (FAC-063).
///
/// <para>Vanilla <c>MyIngameHelpEconomyStation.FindStationAround</c> has two faults on this server.
/// Its nearest-station test compares against a distance it never updates, so it marks whichever
/// station it enumerated last. And it only accepts a station whose safe zone has already
/// materialised within 150 km, which most stations here have not when a player arrives — so the
/// objective usually never fires, and the Datapad objective, which lists it as a prerequisite, never
/// appears either.</para>
///
/// <para>The replacement prefers the server's welcome GPS (<see cref="WelcomeGps"/>): the station
/// <c>RespawnPlacement</c> picked by zone tier and hostility, which the objective marker then agrees
/// with. With no welcome GPS it falls back to vanilla's search with the nearest test working. The
/// condition is polled, so a welcome GPS that arrives after the first poll is still picked up.</para>
/// </summary>
[HarmonyPatch]
internal static class IngameHelpStationPatch
{
    private const string TargetTypeName = "Sandbox.Game.SessionComponents.IngameHelp.MyIngameHelpEconomyStation";
    private const double VanillaSearchRadiusM = 150000.0;

    private static FieldInfo _stationGps;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        MethodBase target = Target();
        Type type = AccessTools.TypeByName(TargetTypeName);
        _stationGps = type != null ? AccessTools.Field(type, "m_stationGPS") : null;
        bool resolved = target != null && _stationGps != null && _stationGps.FieldType == typeof(MyGps);
        Plugin.Log(resolved
            ? "ingame help: station objective patch bound"
            : "ingame help: MyIngameHelpEconomyStation.FindStationAround or m_stationGPS did not resolve -- "
              + "the economy-station objective keeps vanilla's search.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        Type type = AccessTools.TypeByName(TargetTypeName);
        return type != null ? AccessTools.Method(type, "FindStationAround") : null;
    }

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static bool Prefix(object __instance, ref bool __result)
    {
        __result = false;
        MyPlayer player = MySession.Static?.LocalHumanPlayer;
        if (player?.Character == null || player.Identity == null) return false;

        Vector3D position = player.Character.PositionComp.GetPosition();
        Vector3D station;
        string source;
        if (TryWelcomeStation(player.Identity.IdentityId, position, out station))
            source = "welcome";
        else if (TryNearestMaterialisedStation(position, out station))
            source = "nearest";
        else
            return false;

        var gps = new MyGps
        {
            Name = MyTexts.GetString(MySpaceTexts.IngameHelp_Economy_GPSName),
            Description = MyTexts.GetString(MySpaceTexts.IngameHelp_Economy_GPSDesc),
            Coords = station,
            ShowOnHud = true,
            DiscardAt = null,
            IsLocal = true,
            IsObjective = true,
        };
        gps.UpdateHash();
        _stationGps.SetValue(__instance, gps);
        MySession.Static.Gpss.SendAddGpsRequest(MySession.Static.LocalPlayerId, ref gps, 0L);

        Plugin.Log("ingame help: station objective -> " + source + " "
                   + ((station - position).Length() / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " km");
        __result = true;
        return false;
    }

    /// <summary>The nearest of this player's welcome GPSes. The server keeps one per player, but a
    /// list saved before it did can hold several until the next respawn retires them.</summary>
    private static bool TryWelcomeStation(long identityId, Vector3D position, out Vector3D station)
    {
        station = default;
        var gpses = new List<IMyGps>();
        MySession.Static.Gpss.GetGpsList(identityId, gpses);

        double best = double.PositiveInfinity;
        foreach (IMyGps gps in gpses)
        {
            if (gps == null || !WelcomeGps.IsWelcome(gps.Name, gps.Description)) continue;
            double d = (gps.Coords - position).LengthSquared();
            if (d < best)
            {
                best = d;
                station = gps.Coords;
            }
        }
        return best < double.PositiveInfinity;
    }

    /// <summary>Vanilla's search, with the running minimum it forgot to keep.</summary>
    private static bool TryNearestMaterialisedStation(Vector3D position, out Vector3D station)
    {
        station = default;
        if (MyProceduralWorldGenerator.Static == null) return false;

        var area = new BoundingBoxD(position - VanillaSearchRadiusM, position + VanillaSearchRadiusM);
        var seeds = new List<MyObjectSeed>();
        MyProceduralWorldGenerator.Static.GetOverlapAllBoundingBox<MyStationCellGenerator>(area, seeds);

        double best = double.PositiveInfinity;
        foreach (MyObjectSeed seed in seeds)
        {
            if (!(seed.UserData is MyFactionStation factionStation)) continue;
            if (!MyEntities.TryGetEntityById(factionStation.SafeZoneEntityId, out var entity) || !(entity is MySafeZone)) continue;
            Vector3D at = entity.PositionComp.GetPosition();
            double d = (at - position).LengthSquared();
            if (d < best)
            {
                best = d;
                station = at;
            }
        }
        return best < double.PositiveInfinity;
    }
}
