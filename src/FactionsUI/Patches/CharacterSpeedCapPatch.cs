using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// The client half of the server's suit speed clamp fix (FAC-285).
///
/// <para><c>MyCharacterProxy.ApplyGravity</c> clamps the suit against a threshold cached when the
/// proxy is built, but clamps it <i>to</i> the live grid limit. This client's character is built
/// against its local environment definition — vanilla 100 m/s — and <see cref="GridSpeedComponent"/>
/// raises the limit afterwards, so past the old 110 m/s the suit is set to the new limit and held
/// there. The replacement reads the threshold live too. The server-side reasoning is in
/// <c>Factions/CharacterSpeedCapPatch.cs</c>.</para>
/// </summary>
[HarmonyPatch]
internal static class CharacterSpeedCapPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "character speed cap: suit clamp patch bound"
            : "character speed cap: MyCharacterProxy.ApplyGravity did not resolve -- after the server's "
              + "grid speed arrives, the jetpack will snap to the new limit once past the old one.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyCharacterProxy), "ApplyGravity", new[] { typeof(Vector3) });
    }

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static bool Prefix(MyCharacterProxy __instance, Vector3 gravity)
    {
        var body = __instance.CharacterRigidBody;
        if (body == null) return true;

        body.LinearVelocity += gravity * (1f / 60f);
        float max = __instance.CharacterFlyingMaxLinearVelocity();
        Vector3 velocity = body.LinearVelocity;
        if (velocity.LengthSquared() > max * max)
        {
            velocity.Normalize();
            body.LinearVelocity = velocity * max;
        }
        return false;
    }
}
