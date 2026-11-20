using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Factions.Wire;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage;

namespace Factions
{
    /// <summary>
    /// Server-to-server transport over Nexus's mod-message bridge, and nothing else: it moves a
    /// <see cref="NexusRelayWire"/> frame to another cluster member and hands back the frames that
    /// arrive. What a frame means is <see cref="UplinkManager"/>'s business.
    ///
    /// <para>Nexus offers a Torch plugin no compile-time API. At its own session load it publishes a
    /// dispatcher to mods as a one-shot mod message on channel 20240902: a
    /// <c>MyTuple&lt;byte[], Func&lt;int, Func&lt;object, object&gt;&gt;&gt;</c> whose second item
    /// maps a method ordinal to a callable. This registers for that message at plugin init, before
    /// any session exists, so the one shot is never missed. Until it arrives every send reports
    /// failure and nothing is queued; the agent re-forwards on its own cadence.</para>
    ///
    /// <para>Incoming frames arrive as the serialized <c>ModAPIMsg</c> envelope rather than the bytes
    /// that were sent — Nexus forwards <c>Incoming.Data</c>, not <c>msg.msgData</c> — so a partial
    /// protobuf contract reads the four fields this needs and ignores the rest. Delivery thread is
    /// not documented, so frames are queued and drained from the game thread.</para>
    /// </summary>
    internal sealed class NexusRelay
    {
        private const long NexusApiChannel = 20240902L;

        // Ordinals of NGPlugin's mod-facing dispatcher, verified against v3.03.01 (docs/nexus.md).
        private const int OrdinalIsServerOnline       = 4;
        private const int OrdinalSendModMsgToServer   = 5;
        private const int OrdinalGetAllOnlinePlayers  = 8;

        /// <summary>The four fields of NGPlugin.API.ModAPIMsg this relay reads. Tags are Nexus's.</summary>
        [ProtoContract]
        private sealed class ModApiEnvelope
        {
            [ProtoMember(10)] public byte fromServerID { get; set; }
            [ProtoMember(20)] public byte toServerID { get; set; }
            [ProtoMember(25)] public long targetModMessageID { get; set; }
            [ProtoMember(30)] public byte[] msgData { get; set; }
        }

        public struct Inbound
        {
            public byte FromServerId;
            public byte[] Frame;
        }

        private readonly ConcurrentQueue<Inbound> _inbox = new ConcurrentQueue<Inbound>();
        private volatile Func<int, Func<object, object>> _dispatch;
        private Action<object> _apiHandler;
        private Action<object> _relayHandler;
        private bool _registered;
        private bool _apiLogged;

        /// <summary>Counters for the <c>factions nexus</c> verb. Read on the game thread only.</summary>
        public int FramesSent;
        public int FramesReceived;
        public int SendsWithoutApi;
        public int FramesUnreadable;

        /// <summary>True once Nexus has published its dispatcher into this process.</summary>
        public bool ApiAvailable { get { return _dispatch != null; } }

        /// <summary>Call at plugin init, before any session. Registers through
        /// <see cref="MyAPIUtilities.Static"/> rather than <c>MyAPIGateway.Utilities</c> for the
        /// reason <see cref="VpfAmmoBridge.Register"/> records: the latter is assigned later.</summary>
        public void Register()
        {
            if (_registered) return;
            try
            {
                _apiHandler = OnNexusApi;
                _relayHandler = OnRelayMessage;
                MyAPIUtilities.Static.RegisterMessageHandler(NexusApiChannel, _apiHandler);
                MyAPIUtilities.Static.RegisterMessageHandler(NexusRelayWire.ModChannelId, _relayHandler);
                _registered = true;
            }
            catch (Exception ex)
            {
                _apiHandler = null;
                _relayHandler = null;
                FactionsPlugin.Log.Warn("Nexus relay could not register its mod-message handlers: " + ex.Message);
            }
        }

        public void Unregister()
        {
            if (!_registered) return;
            try { MyAPIUtilities.Static.UnregisterMessageHandler(NexusApiChannel, _apiHandler); } catch { }
            try { MyAPIUtilities.Static.UnregisterMessageHandler(NexusRelayWire.ModChannelId, _relayHandler); } catch { }
            _registered = false;
            _dispatch = null;
        }

        private void OnNexusApi(object payload)
        {
            try
            {
                if (payload is MyTuple<byte[], Func<int, Func<object, object>>>)
                {
                    var tuple = (MyTuple<byte[], Func<int, Func<object, object>>>)payload;
                    _dispatch = tuple.Item2;
                    if (!_apiLogged)
                    {
                        _apiLogged = true;
                        FactionsPlugin.Log.Info("Nexus mod API received; server-to-server relay is available.");
                    }
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn("Nexus mod API handler threw: " + ex.Message);
            }
        }

        private void OnRelayMessage(object payload)
        {
            byte[] bytes = payload as byte[];
            if (bytes == null) return;
            try
            {
                ModApiEnvelope env;
                using (var ms = new MemoryStream(bytes))
                    env = Serializer.Deserialize<ModApiEnvelope>(ms);
                if (env == null || env.msgData == null) { FramesUnreadable++; return; }
                _inbox.Enqueue(new Inbound { FromServerId = env.fromServerID, Frame = env.msgData });
            }
            catch
            {
                FramesUnreadable++;
            }
        }

        /// <summary>Hands one frame to Nexus for <paramref name="targetServerId"/>. False when the
        /// dispatcher has not been published yet or Nexus refused (it refuses IDs of 1 and below).
        /// Fire-and-forget on the far side; a true here means handed over, not delivered.</summary>
        public bool TrySend(byte targetServerId, byte[] frame)
        {
            Func<int, Func<object, object>> dispatch = _dispatch;
            if (dispatch == null || frame == null) { SendsWithoutApi++; return false; }
            try
            {
                Func<object, object> send = dispatch(OrdinalSendModMsgToServer);
                if (send == null) { SendsWithoutApi++; return false; }
                object result = send(MyTuple.Create<byte[], long, byte>(frame, NexusRelayWire.ModChannelId, targetServerId));
                bool ok = result is bool && (bool)result;
                if (ok) FramesSent++;
                return ok;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn("Nexus relay send to server " + targetServerId + " threw: " + ex.Message);
                return false;
            }
        }

        /// <summary>Nexus's point-in-time liveness poll; null while the dispatcher is unavailable.</summary>
        public bool? IsServerOnline(byte serverId)
        {
            Func<int, Func<object, object>> dispatch = _dispatch;
            if (dispatch == null) return null;
            try
            {
                Func<object, object> f = dispatch(OrdinalIsServerOnline);
                if (f == null) return null;
                object r = f(serverId);
                return r is bool ? (bool)r : (bool?)null;
            }
            catch { return null; }
        }

        /// <summary>Cluster-wide online SteamIDs as Nexus knows them; null when unavailable.</summary>
        public List<ulong> OnlinePlayers()
        {
            Func<int, Func<object, object>> dispatch = _dispatch;
            if (dispatch == null) return null;
            try
            {
                Func<object, object> f = dispatch(OrdinalGetAllOnlinePlayers);
                return f == null ? null : f(null) as List<ulong>;
            }
            catch { return null; }
        }

        /// <summary>Delivers queued frames on the calling (game) thread. Bounded per call so a
        /// burst cannot own a frame.</summary>
        public int Drain(Action<byte, byte[]> handle, int max)
        {
            int n = 0;
            Inbound item;
            while (n < max && _inbox.TryDequeue(out item))
            {
                n++;
                FramesReceived++;
                try { handle(item.FromServerId, item.Frame); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Relay frame handling failed."); }
            }
            return n;
        }

        public string Status()
        {
            return "api=" + (ApiAvailable ? "available" : "not published")
                 + " sent=" + FramesSent + " received=" + FramesReceived
                 + " unreadable=" + FramesUnreadable + " sendsWithoutApi=" + SendsWithoutApi;
        }
    }
}
