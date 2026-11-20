using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;            // MyVoxelBase
using Sandbox.Game.Replication;         // MyExternalReplicable<T>
using VRage.Network;                    // MyClientInfo

namespace Factions
{
    /// <summary>
    /// Stops pristine feature rock from being replicated to a client that is building it locally.
    /// This is where the whole of <see cref="PlayerRockManager"/>'s saving is actually taken: every
    /// other part of that feature is bookkeeping so that this refusal is safe.
    ///
    /// <para><b>A postfix, never a prefix.</b> SE's own <c>ShouldReplicate</c> answers a set of
    /// questions this has no business re-answering — a closed entity, a missing storage, a planet.
    /// Running after it and only ever turning a <c>true</c> into a <c>false</c> means this can
    /// withhold rock but can never cause something to replicate that SE had decided against.</para>
    ///
    /// <para><b>Three conditions, all required.</b> The voxel must be rock this session's
    /// materialiser spawned <i>and</i> still owns — ownership is released the instant anything edits
    /// it, which is exactly when the client's derived copy stops being identical and the edit has to
    /// stream down. The client must be enrolled in
    /// <see cref="PlayerRockManager.ClientDerivesRock"/>, which a vanilla client never is. Miss
    /// either and SE's answer stands untouched, which is why this degrades per client rather than
    /// globally — the failure mode the server half spent a live session on
    /// (<c>Save = false</c>: rock that collided and rendered for nobody) is not reachable from
    /// here.</para>
    ///
    /// <para><b>The handover on first edit needs no code.</b> <c>MyVoxelReplicable.OnLoad</c> does
    /// <c>TryGetEntityById</c> and swaps the incoming storage into the entity the client already
    /// has, rather than building a second one — so a client-derived rock is upgraded in place the
    /// moment someone drills it. That works only because both ends agree on the entity id, which is
    /// why the id is carried on the wire rather than re-derived
    /// (<see cref="Factions.Wire.WireRockObject.EntityId"/>).</para>
    /// </summary>
    [HarmonyPatch]
    internal static class FeatureRockReplicationGate
    {
        /// <summary>Set when the target could not be resolved, so the condition is reported once
        /// rather than presenting as rock that silently never stops replicating.</summary>
        public static bool TargetMissing { get; private set; }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            // MyVoxelReplicable is internal to Sandbox.Game, so it cannot be named in the attribute.
            Type replicable = AccessTools.TypeByName("Sandbox.Game.Replication.MyVoxelReplicable");
            if (replicable == null)
            {
                TargetMissing = true;
                yield break;
            }

            MethodBase shouldReplicate = AccessTools.Method(
                replicable, "ShouldReplicate", new[] { typeof(MyClientInfo) });
            if (shouldReplicate == null)
            {
                TargetMissing = true;
                yield break;
            }

            yield return shouldReplicate;
        }

        /// <summary>
        /// <paramref name="__instance"/> is taken as <see cref="object"/> and cast here rather than
        /// declared as the base type. The patched class is internal, so the declared type would have
        /// to be its public generic base — which Harmony would have to accept as assignable at patch
        /// time, and a patch that fails to apply is a silent loss of the whole feature. A castclass
        /// on a per-client path costs nothing and cannot fail to bind.
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(object __instance, MyClientInfo client, ref bool __result)
        {
            if (!__result) return;                       // SE already refuses; nothing to add

            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            if (rock == null) return;                    // no session, or no features authored

            var replicable = __instance as MyExternalReplicable<MyVoxelBase>;
            MyVoxelBase voxel = replicable == null ? null : replicable.Instance;
            if (voxel == null) return;

            if (!rock.OwnsPristine(voxel)) return;       // not ours, or a player has edited it
            if (!client.IsValid) return;

            if (!PlayerRockManager.ClientDerivesRock(client.EndpointId.Id.Value)) return;

            __result = false;
        }
    }
}
