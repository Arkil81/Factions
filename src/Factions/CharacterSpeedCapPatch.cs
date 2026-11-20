using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Makes the suit's speed clamp compare against the same limit it clamps to.
    ///
    /// <para><c>MyCharacterProxy.ApplyGravity</c>, run every frame while the jetpack is on, clamps the
    /// suit when its speed exceeds <c>m_maxCharacterSpeedSq</c> — cached once, when the proxy is
    /// built — but sets the clamped speed from <c>MyGridPhysics.ShipMaxLinearVelocity()</c> read live.
    /// When the grid limit is raised after a character exists, the two disagree: past the old limit
    /// the suit is <i>set</i> to the new one, and every later frame puts it back there, so dampeners
    /// cannot bring it down. <c>factions gridspeed</c> raises the limit at runtime, so every character
    /// already in the world hits this; on a client it is every character, because the uplink applies
    /// the server's limit after the local character is built.</para>
    ///
    /// <para>The replacement body is vanilla's with the threshold read from
    /// <c>CharacterFlyingMaxLinearVelocity()</c>, which is the same live sum vanilla clamps to. When
    /// the limit has not moved since the proxy was built, the two are equal and nothing
    /// changes.</para>
    ///
    /// <para><c>FactionsUI</c> carries the same patch. FAC-285.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class CharacterSpeedCapPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>Whether the patch bound, for <c>factions gridspeed</c>'s report.</summary>
        internal static bool Bound { get { return HarmonyPatcher.IsLive(typeof(CharacterSpeedCapPatch)); } }

        private static bool _outcomeLogged;

        /// <summary>A miss disables this class only and must never throw: a throw fails its whole
        /// patch group (<c>HarmonyPatcher</c>).</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Character speed cap: suit clamp patch bound.");
                else
                    Log.Error("Character speed cap: MyCharacterProxy.ApplyGravity(Vector3) did not resolve, "
                            + "so a character built before a grid speed change keeps the old suit threshold "
                            + "and snaps to the new limit once past it.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyCharacterProxy), "ApplyGravity", new[] { typeof(Vector3) });
        }

        [HarmonyPrefix]
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
}
