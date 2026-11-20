using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox.Game.Multiplayer;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Respawn;

/// <summary>
/// The client end of respawning at a founded node (docs/new-player-experience.md → Respawning at
/// your own nodes): asks for the faction's founded nodes while the respawn screen is open, holds the
/// last answer, and turns a refused node spawn into a message the player can read.
///
/// <para>The rows themselves are drawn by <see cref="Patches.RespawnScreenNodeRowsPatch"/>; the
/// spawn rides SE's own respawn request, so nothing here sends it.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class NodeSpawnComponent : MySessionComponentBase
{
    /// <summary>The screen asks on open and on every vanilla refresh; this bounds a burst of both.</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long after a node spawn is sent a respawn failure is still read as its refusal.</summary>
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(30);

    internal static NodeSpawnComponent Instance { get; private set; }

    /// <summary>The last list, each row stamped with when its cooldown ends. Empty until the server
    /// has answered, and whenever the player has no faction.</summary>
    internal readonly List<NodeSpawnRowData> Rows = new List<NodeSpawnRowData>();

    /// <summary>Raised on the game thread when a new list has replaced <see cref="Rows"/>.</summary>
    internal event Action RowsChanged;

    private DateTime _lastRequestUtc = DateTime.MinValue;
    private NodeSpawnRowData _pending;
    private DateTime _pendingSentUtc;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.NodeSpawnList, OnList);
        MyPlayerCollection.OnRespawnRequestFailureEvent += OnRespawnFailed;
    }

    protected override void UnloadData()
    {
        MyPlayerCollection.OnRespawnRequestFailureEvent -= OnRespawnFailed;
        Rows.Clear();
        RowsChanged = null;
        Instance = null;
    }

    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.NodeSpawn);
        }
    }

    /// <summary>Ask for a fresh list. Safe to call as often as the screen refreshes.</summary>
    internal void RequestList()
    {
        if (!Available) return;
        DateTime now = DateTime.UtcNow;
        if (now - _lastRequestUtc < MinRequestInterval) return;
        _lastRequestUtc = now;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteNodeSpawnListRequest(), true);
        }
        catch (Exception ex)
        {
            Plugin.Log("node spawn list request failed: " + ex);
        }
    }

    /// <summary>Remembers the node a respawn request was just sent for, so a refusal can name it.</summary>
    internal void MarkSent(NodeSpawnRowData row)
    {
        _pending = row;
        _pendingSentUtc = DateTime.UtcNow;
        if (Config.Current.DebugLogging)
            Plugin.Log("NODE_SPAWN sent node=" + row.Wire.NodeId + " station=" + row.Wire.StationId);
    }

    private void OnList(byte[] data)
    {
        List<WireNodeSpawnRow> rows;
        if (!FactionsWire.TryReadNodeSpawnList(data, out rows))
        {
            Plugin.Log("NODE_SPAWN_LIST unreadable");
            return;
        }
        DateTime now = DateTime.UtcNow;
        Rows.Clear();
        foreach (WireNodeSpawnRow row in rows)
            Rows.Add(new NodeSpawnRowData(row, now.AddSeconds(row.CooldownSeconds)));
        if (Config.Current.DebugLogging) Plugin.Log("NODE_SPAWN_LIST rows=" + rows.Count);
        try { RowsChanged?.Invoke(); }
        catch (Exception ex) { Plugin.Log("node spawn rows refresh failed: " + ex); }
    }

    private void OnRespawnFailed(ulong steamId)
    {
        NodeSpawnRowData pending = _pending;
        _pending = null;
        if (pending == null || DateTime.UtcNow - _pendingSentUtc > PendingWindow) return;
        try
        {
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Error,
                buttonType: MyMessageBoxButtonsType.OK,
                messageText: new StringBuilder("The respawn at " + pending.Wire.DisplayName
                    + " was refused. Its row in the list shows why."),
                messageCaption: new StringBuilder("RESPAWN REFUSED")));
        }
        catch (Exception ex)
        {
            Plugin.Log("node spawn refusal notice failed: " + ex);
        }
    }
}

/// <summary>A row's user data on the respawn screen. Its own type so every vanilla branch on the
/// row's data — preview, detail panel, cooldown labels — passes it by.</summary>
internal sealed class NodeSpawnRowData
{
    internal readonly WireNodeSpawnRow Wire;
    internal readonly DateTime CooldownEndsUtc;

    internal NodeSpawnRowData(WireNodeSpawnRow wire, DateTime cooldownEndsUtc)
    {
        Wire = wire;
        CooldownEndsUtc = cooldownEndsUtc;
    }

    private const byte BlockCooldown = 4;

    /// <summary>True while the row cannot be picked. A cooldown lifts on the client's own clock, so
    /// the row opens without waiting for the next list.</summary>
    internal bool Blocked(DateTime nowUtc)
    {
        if (Wire.Block == 0) return false;
        return Wire.Block != BlockCooldown || nowUtc < CooldownEndsUtc;
    }

    internal string Status(DateTime nowUtc)
    {
        if (!Blocked(nowUtc)) return FactionsWire.NodeSpawnBlockText(0);
        if (Wire.Block != BlockCooldown) return FactionsWire.NodeSpawnBlockText(Wire.Block);
        int left = (int)Math.Ceiling((CooldownEndsUtc - nowUtc).TotalSeconds);
        return FactionsWire.NodeSpawnBlockText(Wire.Block) + " " + (left / 60) + ":" + (left % 60).ToString("00");
    }
}
