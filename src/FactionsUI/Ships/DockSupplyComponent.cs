using System;
using System.Globalization;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Ships;

/// <summary>
/// Client side of Refuel and Rearm at a Services Terminal: sends the requests the three injected
/// buttons and the rearm dialog make, and turns each reply into a dialog.
///
/// <para><b>Refuel is quote, confirm, commit.</b> The first request moves nothing and answers what
/// a fill would deliver and cost; the player confirms that sentence, and the commit carries the
/// quoted total as a ceiling the server will not charge past. So the number the player agrees to is
/// the most they can pay, even if the station's price moves in between.</para>
///
/// <para><b>Nothing is decided here.</b> Which terminal, which ship, which station and at what
/// price are all resolved again by the server on every request; the terminal entity id is borrowed
/// from <see cref="TerminalShipAssetsComponent"/>, which captures it for every Services Terminal
/// screen whatever the server grants.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class DockSupplyComponent : MySessionComponentBase
{
    internal static DockSupplyComponent Instance { get; private set; }

    /// <summary>The ship a pending refuel quote is about, so the confirmed commit names the same
    /// one even if the selection moved while the dialog was up.</summary>
    private long _refuelGrid;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.DockRefuelResult, OnRefuelResult);
        UplinkComponent.RegisterHandler(WireMessageKind.RearmSurvey, OnRearmSurvey);
        UplinkComponent.RegisterHandler(WireMessageKind.RearmResult, OnRearmResult);
    }

    protected override void UnloadData()
    {
        Instance = null;
    }

    /// <summary>True when the server granted refuel and rearm to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.DockSupply);
        }
    }

    private static long TerminalEntityId
    {
        get
        {
            TerminalShipAssetsComponent terminal = TerminalShipAssetsComponent.Instance;
            return terminal == null ? 0 : terminal.TerminalEntityId;
        }
    }

    // ---- Requests -----------------------------------------------------------------------------

    internal void RequestRefuelQuote(long gridEntityId, WireDockGas gas)
    {
        long terminal = TerminalEntityId;
        if (!Available || terminal == 0 || gridEntityId == 0) return;
        _refuelGrid = gridEntityId;
        Send(FactionsWire.WriteDockRefuelRequest(terminal, gridEntityId, gas, false, 0), "refuel quote request");
    }

    private void RequestRefuelCommit(long gridEntityId, WireDockGas gas, long maxCredits)
    {
        long terminal = TerminalEntityId;
        if (!Available || terminal == 0 || gridEntityId == 0) return;
        Send(FactionsWire.WriteDockRefuelRequest(terminal, gridEntityId, gas, true, maxCredits), "refuel request");
    }

    internal void RequestRearmSurvey(long gridEntityId)
    {
        long terminal = TerminalEntityId;
        if (!Available || terminal == 0 || gridEntityId == 0) return;
        Send(FactionsWire.WriteRearmSurveyRequest(terminal, gridEntityId), "rearm survey request");
    }

    internal void RequestRearm(long gridEntityId, long maxCredits, WireRearmPick[] picks, WireRearmReserve[] reserves)
    {
        long terminal = TerminalEntityId;
        if (!Available || terminal == 0 || gridEntityId == 0) return;
        Send(FactionsWire.WriteRearmRequest(terminal, gridEntityId, maxCredits, picks, reserves), "rearm request");
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

    // ---- Replies ------------------------------------------------------------------------------

    private void OnRefuelResult(byte[] data)
    {
        WireDockGas gas;
        bool ok, quote;
        long litres, credits, balanceAfter;
        string message;
        WireReceiptLine[] receipt;
        if (!FactionsWire.TryReadDockRefuelResult(data, out gas, out ok, out quote, out litres, out credits,
                out message, out balanceAfter, out receipt))
        {
            Plugin.Log("DOCK_REFUEL_RESULT unreadable");
            return;
        }

        string caption = gas == WireDockGas.Oxygen ? "REFUEL O2" : "REFUEL H2";
        if (!ok)
        {
            TerminalShipAssetsComponent.ShowMessage(caption, message);
            return;
        }
        if (!quote)
        {
            if (receipt != null && receipt.Length > 0) ReceiptScreen.Open(caption, true, receipt, balanceAfter, message);
            else TerminalShipAssetsComponent.ShowMessage(caption, message);
            return;
        }

        // A quote: the player confirms the server's own sentence, and the commit carries its total.
        long grid = _refuelGrid;
        TerminalShipAssetsComponent.ConfirmYesNo(caption, message,
            () => RequestRefuelCommit(grid, gas, credits),
            "Buy " + credits.ToString("N0", CultureInfo.InvariantCulture) + " cr");
    }

    private void OnRearmSurvey(byte[] data)
    {
        long terminal, grid, balance;
        bool ok;
        string message;
        double cargoFree;
        WireRearmMagazine[] magazines;
        WireRearmGroup[] groups;
        if (!FactionsWire.TryReadRearmSurvey(data, out terminal, out grid, out ok, out message, out balance,
                out cargoFree, out magazines, out groups))
        {
            Plugin.Log("REARM_SURVEY unreadable");
            return;
        }
        // An answer for a terminal the player has walked away from is dropped, not drawn.
        if (terminal != TerminalEntityId) return;
        if (!ok)
        {
            TerminalShipAssetsComponent.ShowMessage("REARM", message);
            return;
        }
        RearmScreen.Open(grid, balance, cargoFree, magazines, groups);
    }

    private void OnRearmResult(byte[] data)
    {
        bool ok;
        string message;
        long balanceAfter;
        WireReceiptLine[] receipt;
        if (!FactionsWire.TryReadRearmResult(data, out ok, out message, out balanceAfter, out receipt))
        {
            Plugin.Log("REARM_RESULT unreadable");
            return;
        }
        if (ok && receipt != null && receipt.Length > 0)
            ReceiptScreen.Open("REARM", false, receipt, balanceAfter, message);
        else
            TerminalShipAssetsComponent.ShowMessage(ok ? "REARM" : "REARM REFUSED", message);
    }
}
