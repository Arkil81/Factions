using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;

namespace FactionsUI.Map;

/// <summary>
/// Owns the map's data: registers for the server's map messages, holds the decoded world, and
/// subscribes only while the window is actually open so a closed map costs the server nothing.
///
/// The window itself is <see cref="MapScreen"/>; this outlives it, so a map reopened a moment later
/// still has a world to draw while the first refresh is in flight.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class MapComponent : MySessionComponentBase
{
    internal static MapComponent Instance { get; private set; }

    internal readonly MapWorld World = new MapWorld();

    /// <summary>Set while a subscribe has been sent and no world message has come back yet, so the
    /// screen can say "waiting" instead of showing a convincingly empty galaxy.</summary>
    internal bool AwaitingFirstWorld { get; private set; }

    private bool _subscribed;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.MapWorld, OnWorld);
        UplinkComponent.RegisterHandler(WireMessageKind.MapFleets, OnFleets);
    }

    protected override void UnloadData()
    {
        World.Clear();
        _subscribed = false;
        Instance = null;
    }

    /// <summary>True when the server has granted the map feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Map);
        }
    }

    /// <summary>
    /// Ask the server to start or stop sending. Idempotent and safe to call every frame: the screen
    /// calls it repeatedly while open, so a map opened before the uplink was granted starts
    /// streaming as soon as it is, rather than staying blank until reopened.
    /// </summary>
    internal void SetStreaming(bool streaming)
    {
        if (_subscribed == streaming) return;

        if (!Available)
        {
            // Nothing to unsubscribe from if the link is gone; drop the local flag so a later
            // subscribe is not swallowed as a no-op.
            if (!streaming) _subscribed = false;
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteMapRequest(streaming), true);
            _subscribed = streaming;
            if (streaming) AwaitingFirstWorld = !World.HasWorld;
        }
        catch (Exception ex)
        {
            Plugin.Log("map subscribe (" + streaming + ") failed: " + ex);
            // Leave _subscribed alone: the send did not happen, so the next call must retry.
        }
    }

    private void OnWorld(byte[] data)
    {
        List<WireMapNode> nodes;
        List<WireMapFaction> factions;
        if (!FactionsWire.TryReadMapWorld(data, out nodes, out factions))
        {
            Plugin.Log("MAP_WORLD unreadable");
            return;
        }
        World.ApplyWorld(nodes, factions);
        AwaitingFirstWorld = false;
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("MAP_WORLD nodes=" + nodes.Count + " factions=" + factions.Count);
        }
    }

    private void OnFleets(byte[] data)
    {
        List<WireMapFleet> fleets;
        List<WireMapAlarm> alarms;
        if (!FactionsWire.TryReadMapFleets(data, out fleets, out alarms))
        {
            Plugin.Log("MAP_FLEETS unreadable");
            return;
        }
        World.ApplyFleets(fleets, alarms);
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("MAP_FLEETS fleets=" + fleets.Count + " alarms=" + alarms.Count);
        }
    }

    /// <summary>Where to put the camera when the map opens: the player, so the first thing they see
    /// is where they are.</summary>
    internal static Vector3D LocalPlayerPosition()
    {
        try
        {
            var controlled = MyAPIGateway.Session?.ControlledObject?.Entity;
            if (controlled != null && !controlled.MarkedForClose) return controlled.GetPosition();

            var character = MyAPIGateway.Session?.Player?.Character;
            if (character != null && !character.MarkedForClose) return character.GetPosition();

            var camera = MyAPIGateway.Session?.Camera;
            if (camera != null) return camera.Position;
        }
        catch (Exception ex)
        {
            Plugin.Log("map local position lookup failed: " + ex);
        }
        return Vector3D.Zero;
    }
}
