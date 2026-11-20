using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace FactionsUI;

/// <summary>
/// Client end of the uplink: the connection to a Factions server, and nothing feature-specific.
/// Announces this plugin, records which features the server granted, and hands every other message
/// to whichever feature owns that kind. Pulsar registers types carrying
/// <see cref="MySessionComponentDescriptor"/> from the plugin assembly as real session components,
/// so this gets a normal session lifecycle with full ModAPI access and no Harmony patching.
///
/// Silent by design against a server without the Factions plugin: the request is sent, nothing
/// answers, and after <see cref="MaxAttempts"/> tries the component gives up and stays idle.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class UplinkComponent : MySessionComponentBase
{
    private const int RetryIntervalTicks = 300;   // ~5 s at 60 ticks/s
    private const int MaxAttempts = 5;

    public static UplinkComponent Instance { get; private set; }

    /// <summary>Server-granted state. Nothing is granted until <see cref="Linked"/> is true.</summary>
    public bool Linked { get; private set; }
    public string ServerVersion { get; private set; }
    public WireFeatures Features { get; private set; }

    public bool HasFeature(WireFeatures feature)
    {
        return Linked && (Features & feature) != 0;
    }

    /// <summary>Feature message sinks, keyed by the kind they own. A feature registers here rather
    /// than this component knowing anything about it.</summary>
    private static readonly Dictionary<WireMessageKind, Action<byte[]>> Handlers =
        new Dictionary<WireMessageKind, Action<byte[]>>();

    public static void RegisterHandler(WireMessageKind kind, Action<byte[]> handler)
    {
        Handlers[kind] = handler;
    }

    private bool _registered;
    private int _tick;
    private int _attempts;
    /// <summary>The server this component last saw. On a Nexus cluster a sector crossing is a
    /// seamless switch: the session survives, LoadData does not run again, and the new server has
    /// never heard from this client. A changed server identity is the re-hello trigger.</summary>
    private ulong _observedServerId;

    public override void LoadData()
    {
        Instance = this;
        // LoadData runs on the update thread, which SE requires for handler registration.
        try
        {
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(FactionsWire.ChannelId, OnMessage);
            _registered = true;
        }
        catch (Exception ex)
        {
            Plugin.Log("uplink handler registration failed: " + ex);
        }
    }

    protected override void UnloadData()
    {
        if (_registered)
        {
            try { MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(FactionsWire.ChannelId, OnMessage); }
            catch (Exception ex) { Plugin.Log("uplink handler unregistration failed: " + ex); }
            _registered = false;
        }
        Linked = false;
        Instance = null;
    }

    public override void UpdateAfterSimulation()
    {
        WatchServerIdentity();
        if (Linked || !_registered || _attempts >= MaxAttempts) return;
        // Retry rather than send once: the first ticks of a session can precede a usable
        // multiplayer/player state, and a lost uplink would otherwise never be retried.
        if (_tick++ % RetryIntervalTicks != 0) return;

        if (MyAPIGateway.Session == null || MyAPIGateway.Multiplayer == null) return;
        if (!MyAPIGateway.Multiplayer.MultiplayerActive) return;   // no server to uplink to
        if (MyAPIGateway.Session.Player == null) return;

        _attempts++;
        try
        {
            byte[] request = FactionsWire.WriteUplinkRequest(PluginVersion());
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, request, true);
            Plugin.Log("UPLINK_REQUEST sent (attempt " + _attempts + "/" + MaxAttempts + ")");
        }
        catch (Exception ex)
        {
            Plugin.Log("uplink send failed: " + ex);
        }
    }

    /// <summary>Re-arms the hello when the server behind the session changes. Covers the seamless
    /// switch, and also a server without Factions handing the player to one with it after the
    /// five attempts were spent.</summary>
    private void WatchServerIdentity()
    {
        ulong serverId;
        try
        {
            if (MyAPIGateway.Multiplayer == null || !MyAPIGateway.Multiplayer.MultiplayerActive) return;
            serverId = MyAPIGateway.Multiplayer.ServerId;
        }
        catch { return; }
        if (serverId == 0 || serverId == _observedServerId) return;
        bool first = _observedServerId == 0;
        _observedServerId = serverId;
        if (!first) ResetLink("server changed to " + serverId);
    }

    private void ResetLink(string reason)
    {
        bool wasLinked = Linked;
        Linked = false;
        Features = WireFeatures.None;
        ServerVersion = null;
        _attempts = 0;
        _tick = 0;
        Plugin.Log("UPLINK_RESET reason=" + reason + (wasLinked ? " (was linked)" : ""));
    }

    /// <summary>Called by SeamlessClient, by name, on session components it registered as mod
    /// assemblies, when a server switch completes. Whether Pulsar's registration qualifies is not
    /// known; <see cref="WatchServerIdentity"/> does not depend on it.</summary>
    public void SeamlessServerLoaded()
    {
        ResetLink("seamless switch");
    }

    public void SeamlessServerUnloaded()
    {
        Linked = false;
    }

    private void OnMessage(ushort channelId, byte[] data, ulong senderSteamId, bool fromServer)
    {
        // Only the server grants or refuses; ignore anything a peer could put on this channel.
        if (!fromServer) return;
        try
        {
            WireMessageKind kind;
            int protocolVersion;
            if (!FactionsWire.TryReadHeader(data, out kind, out protocolVersion)) return;

            if (kind == WireMessageKind.UplinkGranted)
            {
                string serverVersion;
                WireFeatures features;
                if (!FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features))
                {
                    Plugin.Log("UPLINK_GRANTED unreadable (server protocol " + protocolVersion + ")");
                    return;
                }
                Linked = true;
                ServerVersion = serverVersion;
                Features = features;
                Plugin.Log("UPLINK_GRANTED server=" + serverVersion + " features=" + features);
                return;
            }
            if (kind == WireMessageKind.UplinkRefused)
            {
                Linked = false;
                _attempts = MaxAttempts;   // a refusal is final; do not hammer the server

                // A refusal across a protocol mismatch cannot be read past its header, so its reason
                // text never reaches here; the header still names the server's protocol. No notice
                // from this side: the server shows the player one, which also reaches clients too
                // old to have this code, and a second would only stack on top of it.
                int serverProtocol;
                if (FactionsWire.TryReadRefusalProtocol(data, out serverProtocol))
                {
                    Plugin.Log("UPLINK_REFUSED protocol client=" + FactionsWire.ProtocolVersion + " server=" + serverProtocol);
                    return;
                }

                string reason;
                FactionsWire.TryReadUplinkRefused(data, out reason);
                Plugin.Log("UPLINK_REFUSED reason=" + (reason ?? "(unknown)"));
                return;
            }

            // Everything else belongs to a feature. Nothing is accepted before the link is granted.
            Action<byte[]> handler;
            if (Linked && Handlers.TryGetValue(kind, out handler)) handler(data);
        }
        catch (Exception ex)
        {
            Plugin.Log("uplink message handling failed: " + ex);
        }
    }

    private static string PluginVersion()
    {
        try { return typeof(Plugin).Assembly.GetName().Version.ToString(); }
        catch { return "unknown"; }
    }
}
