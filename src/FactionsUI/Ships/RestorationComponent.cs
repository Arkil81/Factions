using System;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;

namespace FactionsUI.Ships;

/// <summary>
/// Client side of Restore: the requests the Repair tab's panel (<see cref="RepairPanel"/>) makes and
/// the answers it shows. When a Services Terminal opens, a probe asks whether this station's Repair
/// tab is Restore's; only a yes activates the panel, so a station Factions does not run keeps
/// vanilla's tab.
///
/// <para>A running restore's news (paused, resumed, done) arrives as a notice and is shown on the
/// HUD — the player is usually on the ship by then. While the panel shows a running restore, it is
/// refreshed every <see cref="RefreshFrames"/> frames.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class RestorationComponent : MySessionComponentBase
{
    private const int NoticeMs = 8000;

    /// <summary>Two seconds at 60 frames: the server's own restore interval.</summary>
    private const int RefreshFrames = 120;

    internal static RestorationComponent Instance { get; private set; }

    /// <summary>A managed station's panel still not up after five seconds is logged.</summary>
    private const int ActivateLogAfterFrames = 300;
    private const int ActivateRetryEveryFrames = 10;

    private long _terminal;
    private int _frames;
    private int _activateFrames;

    /// <summary>The server said the open terminal's Repair tab is Restore's.</summary>
    internal bool Managed { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.RestoreQuote, OnQuote);
        UplinkComponent.RegisterHandler(WireMessageKind.RestoreResult, OnResult);
    }

    protected override void UnloadData()
    {
        Instance = null;
    }

    /// <summary>True when the server granted restoration to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Restoration);
        }
    }

    internal void OnTerminalOpened(long terminalEntityId)
    {
        _terminal = terminalEntityId;
        Managed = false;
        if (!Available || terminalEntityId == 0) return;
        Send(FactionsWire.WriteRestoreQuoteRequest(terminalEntityId, 0), "restore probe");
    }

    internal void OnTerminalClosed()
    {
        _terminal = 0;
        Managed = false;
    }

    public override void UpdateAfterSimulation()
    {
        RetryActivation();
        RepairPanel panel = RepairPanel.Current;
        if (panel == null || !RepairPanel.Active || panel.Offer != WireRestoreOffer.Running) { _frames = 0; return; }
        if (++_frames < RefreshFrames) return;
        _frames = 0;
        panel.Refresh();
    }

    /// <summary>A managed station whose panel is not up yet: vanilla's controls were not ready when
    /// the answer came. Tries every <see cref="ActivateRetryEveryFrames"/> frames for as long as the
    /// terminal is open — the Repair tab's content may not be ready until it is shown — and logs once
    /// when <see cref="ActivateLogAfterFrames"/> have passed without it.</summary>
    private void RetryActivation()
    {
        RepairPanel panel = RepairPanel.Current;
        if (!Managed || panel == null || RepairPanel.Active) { _activateFrames = 0; return; }
        if (_activateFrames < 0) return;
        _activateFrames++;
        if (_activateFrames % ActivateRetryEveryFrames != 0) return;
        try
        {
            if (panel.TryActivate(_activateFrames == ActivateLogAfterFrames) && Config.Current.DebugLogging)
                Plugin.Log("repair panel up after " + _activateFrames + " frames");
        }
        catch (Exception ex)
        {
            _activateFrames = -1;
            Plugin.Log("repair panel activation failed: " + ex);
        }
    }

    internal void RequestQuote(long gridEntityId)
    {
        if (!Available || _terminal == 0 || gridEntityId == 0) return;
        Send(FactionsWire.WriteRestoreQuoteRequest(_terminal, gridEntityId), "restore quote request");
    }

    internal void Request(long gridEntityId, WireRestoreAction action, long maxCredits)
    {
        if (!Available || _terminal == 0 || gridEntityId == 0) return;
        Send(FactionsWire.WriteRestoreRequest(_terminal, gridEntityId, action, maxCredits), "restore request");
    }

    private static void Send(byte[] payload, string what)
    {
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, payload, true);
        }
        catch (Exception ex)
        {
            Plugin.Log(what + " failed: " + ex);
        }
    }

    private void OnQuote(byte[] data)
    {
        if (!FactionsWire.TryReadRestoreQuote(data, out WireRestoreQuote q))
        {
            Plugin.Log("RESTORE_QUOTE unreadable");
            return;
        }
        // An answer for a terminal the player has walked away from is dropped.
        if (q.TerminalEntityId != _terminal || _terminal == 0) return;
        if (Config.Current.DebugLogging)
            Plugin.Log("RESTORE_QUOTE grid=" + q.GridEntityId + " managed=" + q.Managed + " offer=" + q.Offer
                       + " lines=" + (q.Lines == null ? 0 : q.Lines.Length));

        Managed = q.Managed;
        RepairPanel panel = RepairPanel.Current;
        if (panel == null) return;
        if (q.Managed && !RepairPanel.Active)
        {
            try { panel.TryActivate(false); }
            catch (Exception ex) { Plugin.Log("repair panel activation failed: " + ex); }
        }
        if (q.GridEntityId != 0) panel.OnQuote(q);
    }

    private void OnResult(byte[] data)
    {
        if (!FactionsWire.TryReadRestoreResult(data, out long grid, out bool ok, out bool notice, out string message,
                out long balanceAfter))
        {
            Plugin.Log("RESTORE_RESULT unreadable");
            return;
        }
        if (!notice)
        {
            RepairPanel.Current?.OnResult(grid, ok, message);
            return;
        }
        try
        {
            MyAPIGateway.Utilities.ShowNotification(message, NoticeMs, ok ? MyFontEnum.White : MyFontEnum.Red);
        }
        catch (Exception ex)
        {
            Plugin.Log("restore notice failed: " + ex);
        }
        RepairPanel.Current?.Refresh();
    }
}
