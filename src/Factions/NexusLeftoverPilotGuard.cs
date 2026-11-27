using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Stops Nexus from sending a pilot a second time after it sent them inside a ship (FAC-530). The
    /// mechanism is on <see cref="TransportedPilotLedger"/>. This class binds the two Nexus methods by name
    /// (NGPlugin has no compile-time API) and does nothing when NGPlugin is not loaded.
    ///
    /// <list type="bullet">
    /// <item><c>NGPlugin.BoundarySystem.GridTransportMessage.StartTransfer(List&lt;MyCubeGrid&gt;, MyCubeGrid)</c>:
    /// the pilots seated in the grids are read before the call and recorded once it returns true, which
    /// means the ship was published to another server.</item>
    /// <item><c>NGPlugin.BoundarySystem.TransportSync.RequestCharacterTransfer(MyCharacter, ServerAPI, Vector3D?)</c>:
    /// the boundary scanner's send of a loose character. For a recorded pilot whose player has left this
    /// server, the character is closed and the send skipped.</item>
    /// </list>
    ///
    /// <para>Both were read in the NGPlugin v3.03.07 and v3.03.25 decompiles, 2026-11-26. Arguments are
    /// taken by position, not by name, because the assembly is obfuscated. It runs on every Nexus server
    /// whether or not that server simulates: the server that sends the ship is the one that has to act.</para>
    ///
    /// <para>Switched as <see cref="FeatureSwitch.NexusLeftoverPilot"/>, because it closes a player's
    /// character. With the switch off it only logs <c>NEXUS_LEFTOVER_PILOT action=sent</c> for each
    /// leftover it would have closed. That line, together with the <c>action=closed</c> one, is the only
    /// record of a decision taken on a player's character. It fires at most once per pilot per crossing,
    /// so it is not gated behind a debug flag.</para>
    /// </summary>
    internal static class NexusLeftoverPilotGuard
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly TransportedPilotLedger Ledger = new TransportedPilotLedger();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private const string GridTransportTypeName = "NGPlugin.BoundarySystem.GridTransportMessage";
        private const string TransportSyncTypeName = "NGPlugin.BoundarySystem.TransportSync";

        internal static bool Bound { get; private set; }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                Type gridTransport = AccessTools.TypeByName(GridTransportTypeName);
                Type transportSync = AccessTools.TypeByName(TransportSyncTypeName);
                if (gridTransport == null && transportSync == null) return; // no Nexus: nothing to guard
                if (gridTransport == null || transportSync == null)
                {
                    Log.Error("Nexus leftover-pilot guard: " + (gridTransport == null ? GridTransportTypeName : TransportSyncTypeName)
                              + " did not resolve; a pilot sent with a ship can still be sent again as a loose character.");
                    return;
                }

                MethodInfo startTransfer = AccessTools.Method(gridTransport, "StartTransfer",
                    new[] { typeof(List<MyCubeGrid>), typeof(MyCubeGrid) });
                MethodInfo requestCharacter = null;
                foreach (MethodInfo m in transportSync.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "RequestCharacterTransfer" && p.Length >= 1 && p[0].ParameterType == typeof(MyCharacter))
                    {
                        requestCharacter = m;
                        break;
                    }
                }
                if (startTransfer == null || requestCharacter == null)
                {
                    Log.Error("Nexus leftover-pilot guard: "
                              + (startTransfer == null ? "GridTransportMessage.StartTransfer(List<MyCubeGrid>, MyCubeGrid)"
                                                       : "TransportSync.RequestCharacterTransfer(MyCharacter, ...)")
                              + " did not resolve; a pilot sent with a ship can still be sent again as a loose character.");
                    return;
                }

                harmony.Patch(startTransfer,
                    prefix: new HarmonyMethod(Method("StartTransferPrefix")),
                    postfix: new HarmonyMethod(Method("StartTransferPostfix")));
                harmony.Patch(requestCharacter, prefix: new HarmonyMethod(Method("RequestCharacterPrefix")));
                Bound = true;
                Log.Info("Nexus leftover-pilot guard: bound, switch " + FeatureSwitch.NexusLeftoverPilot + " "
                         + (FeatureSwitch.IsOn(FeatureSwitch.NexusLeftoverPilot) ? "on" : "off") + ".");
            }
            catch (Exception ex)
            {
                Log.Error("Nexus leftover-pilot guard: could not patch Nexus: " + ex.Message);
            }
        }

        private static MethodInfo Method(string name)
        {
            return typeof(NexusLeftoverPilotGuard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>The seated pilots, read while they are still seated. The same blocks Nexus reads
        /// (<c>GetFatBlocks&lt;MyCockpit&gt;()</c>, cryo chambers included).</summary>
        private static void StartTransferPrefix(List<MyCubeGrid> __0, out List<KeyValuePair<long, long>> __state)
        {
            __state = null;
            try
            {
                if (__0 == null) return;
                foreach (MyCubeGrid grid in __0)
                {
                    if (grid == null) continue;
                    foreach (MyCockpit cockpit in grid.GetFatBlocks<MyCockpit>())
                    {
                        MyCharacter pilot = cockpit == null ? null : cockpit.Pilot;
                        if (pilot == null) continue;
                        if (__state == null) __state = new List<KeyValuePair<long, long>>();
                        __state.Add(new KeyValuePair<long, long>(pilot.EntityId, pilot.GetPlayerIdentityId()));
                    }
                }
            }
            catch (Exception ex)
            {
                __state = null;
                Log.Warn("Nexus leftover-pilot guard: reading the pilots of a ship being sent failed: " + ex.Message);
            }
        }

        /// <summary>False means Nexus teleported the ship within this server, or the call threw: nothing
        /// was sent, so nobody is left behind.</summary>
        private static void StartTransferPostfix(bool __result, List<KeyValuePair<long, long>> __state)
        {
            if (!__result || __state == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            foreach (KeyValuePair<long, long> pilot in __state)
                Ledger.Record(pilot.Key, pilot.Value, now);
        }

        private static bool RequestCharacterPrefix(MyCharacter __0)
        {
            try
            {
                MyCharacter character = __0;
                if (character == null || character.MarkedForClose) return true;

                long identityId = character.GetPlayerIdentityId();
                double age;
                bool byEntity;
                bool matched = Ledger.TryMatch(character.EntityId, identityId, Clock.Elapsed.TotalSeconds, out age, out byEntity);
                if (!matched) return true;

                bool online = identityId != 0 && MySession.Static != null && MySession.Static.Players.IsPlayerOnline(identityId);
                LeftoverPilotAction action = TransportedPilotLedger.Decide(
                    FeatureSwitch.IsOn(FeatureSwitch.NexusLeftoverPilot), true, online);
                if (action == LeftoverPilotAction.Send) return true;

                Log.Info("NEXUS_LEFTOVER_PILOT action=" + (action == LeftoverPilotAction.Close ? "closed" : "sent")
                         + " steam=" + character.ControlSteamId + " identity=" + identityId + " eid=" + character.EntityId
                         + " match=" + (byEntity ? "entity" : "identity")
                         + " shipSentAgoS=" + age.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                if (action != LeftoverPilotAction.Close) return true;

                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    if (!character.MarkedForClose) character.Close();
                });
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("Nexus leftover-pilot guard: judging a loose character failed, Nexus sends it: " + ex.Message);
                return true;
            }
        }
    }
}
