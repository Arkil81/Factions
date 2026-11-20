using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;

namespace FactionsUI.Assets;

/// <summary>
/// Owns the Assets tab's data: registers for the server's snapshots, holds the decoded register,
/// and subscribes only while the tab is actually being looked at, so a closed K menu costs the
/// server nothing — which matters here because the server pays an entity sweep per pass.
///
/// The tab itself is <see cref="AssetsPage"/>, which is rebuilt every time the terminal opens;
/// this outlives it, so a K menu reopened a moment later still has a register to draw.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class AssetsComponent : MySessionComponentBase
{
    internal static AssetsComponent Instance { get; private set; }

    internal readonly AssetsWorld World = new AssetsWorld();

    /// <summary>Set while a subscribe has been sent and no snapshot has come back yet, so the page
    /// can say "waiting" instead of showing a convincingly empty list.</summary>
    internal bool AwaitingFirstSnapshot { get; private set; }

    private bool _subscribed;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.AssetsSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        World.Clear();
        _subscribed = false;
        Instance = null;
    }

    /// <summary>True when the server has granted the assets feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Assets);
        }
    }

    /// <summary>
    /// Ask the server to start or stop sending. Idempotent and safe to call every frame: the page
    /// calls it repeatedly while it is on screen, so a tab entered before the uplink was granted
    /// starts streaming as soon as it is, rather than staying blank until reopened.
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
                FactionsWire.ChannelId, FactionsWire.WriteAssetsRequest(streaming), true);
            _subscribed = streaming;
            if (streaming) AwaitingFirstSnapshot = !World.HasSnapshot;
        }
        catch (Exception ex)
        {
            Plugin.Log("assets subscribe (" + streaming + ") failed: " + ex);
            // Leave _subscribed alone: the send did not happen, so the next call must retry.
        }
    }

    private void OnSnapshot(byte[] data)
    {
        string factionTag;
        string factionName;
        List<WireAssetNode> nodes;
        List<WireAssetFleet> fleets;
        List<WireAssetGrid> grids;
        if (!FactionsWire.TryReadAssetsSnapshot(
                data, out factionTag, out factionName, out nodes, out fleets, out grids))
        {
            Plugin.Log("ASSETS_SNAPSHOT unreadable");
            return;
        }
        World.Apply(factionTag, factionName, nodes, fleets, grids);
        AwaitingFirstSnapshot = false;
        if (Config.Current.DebugLogging)
        {
            Plugin.Log("ASSETS_SNAPSHOT faction=" + (string.IsNullOrEmpty(factionTag) ? "(none)" : factionTag)
                       + " nodes=" + nodes.Count + " fleets=" + fleets.Count + " grids=" + grids.Count);
        }
    }

    /// <summary>Where the player is, for the distance column. Mirrors the map's own rule: the
    /// controlled entity first, then the character, then the camera.</summary>
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
            Plugin.Log("assets local position lookup failed: " + ex);
        }
        return Vector3D.Zero;
    }
}
