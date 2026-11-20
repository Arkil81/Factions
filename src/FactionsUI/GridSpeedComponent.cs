using Factions.Wire;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.Game.Components;

namespace FactionsUI;

/// <summary>
/// Applies the server's grid speed limits to this client.
///
/// <para><b>Why the client needs telling at all.</b> SE keeps the limits on
/// <c>MySector.EnvironmentDefinition</c>, and the sector save carries only that definition's Id —
/// each machine resolves it against its own local content and mods. So a server that changes
/// <c>LargeShipMaxSpeed</c> changes nothing on any client, and the client's own prediction goes on
/// clamping at whatever its local definition says. Above the server's limit that shows up as
/// rubber-banding; below it, as a ship that will not reach the speed the server would allow.</para>
///
/// <para>The usual fix is a speed mod, so that both ends load the same definition. This does the same
/// job over the uplink instead, which means the operator can change it live with
/// <c>!factions gridspeed</c> and no one has to resubscribe to anything.</para>
///
/// <para><b>Applied to the definition, not to any grid.</b> Everything that asks SE for the limit —
/// <c>MyGridPhysics.GetShipMaxLinearVelocity</c> and the ship-controller clamp among them — re-reads
/// the definition, so the new value is in force on the next query. A grid whose Havok body already
/// carries the old ceiling keeps it until it reloads or its hierarchy changes; that matches the
/// server's behaviour deliberately, so the two ends do not disagree about which grids have moved to
/// the new limit.</para>
///
/// <para>One reader caches it: <c>MyCharacterProxy</c> stores the suit's speed threshold when the
/// character is built, which on a joining client is before this runs.
/// <see cref="Patches.CharacterSpeedCapPatch"/> makes that clamp read the limit live.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class GridSpeedComponent : MySessionComponentBase
{
    /// <summary>What the server last told us, for the diagnostic line. Zero before the first
    /// message, which is also what an unlinked or ungranted client keeps.</summary>
    public static double LargeMps { get; private set; }
    public static double SmallMps { get; private set; }

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.GridSpeedConfig, OnConfig);
    }

    private static void OnConfig(byte[] data)
    {
        double large, small;
        if (!FactionsWire.TryReadGridSpeedConfig(data, out large, out small)) return;

        MyEnvironmentDefinition env = MySector.EnvironmentDefinition;
        if (env == null)
        {
            Plugin.Log("GRID_SPEED_CONFIG ignored -- no environment definition yet.");
            return;
        }

        double wasLarge = env.LargeShipMaxSpeed;
        double wasSmall = env.SmallShipMaxSpeed;

        // A zero would stop every ship dead, and the server only ever sends a clamped positive
        // value -- so treat it as a malformed message rather than an instruction.
        if (large > 0) env.LargeShipMaxSpeed = (float)large;
        if (small > 0) env.SmallShipMaxSpeed = (float)small;

        LargeMps = env.LargeShipMaxSpeed;
        SmallMps = env.SmallShipMaxSpeed;

        Plugin.Log("GRID_SPEED_CONFIG large=" + wasLarge + " -> " + env.LargeShipMaxSpeed
                   + " small=" + wasSmall + " -> " + env.SmallShipMaxSpeed + " (m/s)");
    }
}
