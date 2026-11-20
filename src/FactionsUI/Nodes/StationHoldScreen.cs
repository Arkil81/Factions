using System;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// Hold upkeep for one node, and the faction-wide budget that pays for it.
///
/// <para><b>Why this exists.</b> An NPC faction's brain keeps its own territory topped up; a player
/// faction's brain is switched off by design, so before this there was no way to spend money holding
/// ground. A node taken by force decayed back to its previous owner within hours — measured live on
/// 2026-09-04, where a conquered station was counter-raided all evening and re-claimed overnight
/// while its new owner sat on 1.1M credits with nothing to spend them on.</para>
///
/// <para>A window of its own for the same reason <see cref="StationDevelopScreen"/> is one: the
/// station window is already two full columns, and these controls do not fit under either. It reads
/// <see cref="NodeDetailComponent"/>'s snapshot rather than taking a watch of its own — the station
/// window behind it owns that lifecycle.</para>
///
/// <para><b>The budget is faction-wide despite arriving on a node.</b> Same shape as the develop
/// menu's reserve and priority rows. The two fractions travel as whole percent because the command
/// request carries an int and a share is a percentage everywhere a player meets one; the cost is
/// that the shipped treasury default (0.1%) cannot be typed at that resolution, so "Use world
/// default" clears the override rather than a 0 that would mean a real zero.</para>
/// </summary>
internal sealed class StationHoldScreen : MyGuiScreenBase
{
    /// <summary>Deliberately <see cref="StationDevelopScreen"/>'s, for the reason that screen
    /// records: these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationHoldScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _toggle;
    private MyGuiControlButton _rankUp;
    private MyGuiControlButton _rankDown;
    private MyGuiControlButton _incomeUp;
    private MyGuiControlButton _incomeDown;
    private MyGuiControlButton _treasuryUp;
    private MyGuiControlButton _treasuryDown;
    private MyGuiControlButton _budgetDefault;

    private int _drawnRevision = -1;

    private StationHoldScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            StationHoldScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationHoldScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("hold window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationHoldScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window, for the reason NodeDetailScreen records.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Hold this node");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;
        Vector2 buttonSize = new Vector2(0.16f, 0.04f);
        float gap = 0.17f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        _detail = new MyGuiControlMultilineText(
            new Vector2(left, top + 0.05f), new Vector2(0.84f, 0.34f), null, "Blue", 0.8f,
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
        };
        Controls.Add(_detail);

        float row = top + 0.42f;
        _toggle = AddActionButton(new Vector2(left, row), buttonSize, "Hold",
            "Keep this node's hold topped up while you are away. Costs credits from the faction "
            + "treasury each time it is topped up.", OnToggleClicked);
        _rankUp = AddActionButton(new Vector2(left + gap, row), buttonSize, "Rank +1",
            "Serve this node before your other held nodes when the budget is tight.", OnRankUpClicked);
        _rankDown = AddActionButton(new Vector2(left + gap * 2, row), buttonSize, "Rank -1",
            "Serve this node after your other held nodes.", OnRankDownClicked);

        row += 0.06f;
        _incomeUp = AddActionButton(new Vector2(left, row), buttonSize, "Income +5%",
            "Raise the share of recent income upkeep may spend per window.", OnIncomeUpClicked);
        _incomeDown = AddActionButton(new Vector2(left + gap, row), buttonSize, "Income -5%",
            "Lower it.", OnIncomeDownClicked);
        _treasuryUp = AddActionButton(new Vector2(left + gap * 2, row), buttonSize, "Treasury +1%",
            "Raise the share of spendable treasury surplus upkeep may draw on per window.",
            OnTreasuryUpClicked);
        _treasuryDown = AddActionButton(new Vector2(left + gap * 3, row), buttonSize, "Treasury -1%",
            "Lower it.", OnTreasuryDownClicked);

        row += 0.06f;
        _budgetDefault = AddActionButton(new Vector2(left, row), buttonSize, "Use world default",
            "Clear your own budget settings and follow the world's. This is also the only way back "
            + "to a default finer than one percent.", OnBudgetDefaultClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    /// <summary>Named arguments, copied from <see cref="StationDevelopScreen"/>'s: this
    /// constructor's positional order is not the one it reads like, and getting it wrong compiles
    /// as four unrelated conversion errors rather than anything that names the real mistake.</summary>
    private MyGuiControlButton AddActionButton(
        Vector2 position, Vector2 size, string text, string tooltip, Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: position,
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: size,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: onClick)
        {
            Enabled = false,
        };
        Controls.Add(button);
        return button;
    }

    // ---- Drawing ------------------------------------------------------------------------------

    public override bool Draw()
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null)
        {
            // Re-asserted every frame for the reason the develop screen records: the window behind
            // may be closed first, and this screen never took a watch of its own.
            component.Watch(_nodeId);
            if (component.Revision != _drawnRevision) Refresh(component);
        }
        return base.Draw();
    }

    private void Refresh(NodeDetailComponent component)
    {
        _drawnRevision = component.Revision;
        WireNodeDetail detail = component.Detail;

        if (!detail.Exists || !string.Equals(detail.Id, _nodeId, StringComparison.Ordinal))
        {
            _heading.Text = _nodeId;
            _detail.Text = new StringBuilder("Waiting for this node...");
            SetEnabled(false, false);
            return;
        }

        _heading.Text = (string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName)
                        + "   hold " + detail.HoldStrength.ToString("0.00", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        if (!detail.Owned)
        {
            sb.AppendLine("This node is not yours, so there is no upkeep to set.");
            _detail.Text = sb;
            SetEnabled(false, false);
            return;
        }

        sb.AppendLine(detail.HoldOptIn
            ? "HELD. Factions tops this node up while you are away, rank " + detail.HoldRank + "."
            : "Not held. Its hold decays and the node is eventually lost to whoever claims it.");
        sb.AppendLine("Each top-up costs " + detail.HoldCost + " credits from the faction treasury.");
        sb.AppendLine();
        sb.AppendLine("Faction budget per window — what upkeep may spend:");
        sb.AppendLine("  income   " + Percent(detail.HoldBudgetIncomeFraction)
                      + Marker(detail.HoldBudgetOverrideMask, 1));
        sb.AppendLine("  treasury " + Percent(detail.HoldBudgetTreasuryFraction)
                      + Marker(detail.HoldBudgetOverrideMask, 2));
        sb.AppendLine("  floor    " + detail.HoldBudgetFloor + " credits"
                      + Marker(detail.HoldBudgetOverrideMask, 4));
        sb.AppendLine();
        sb.AppendLine("Your treasury is the hard limit whatever the budget says. The floor is set "
                      + "with !factions station budget floor <credits>.");
        _detail.Text = sb;

        _toggle.Text = detail.HoldOptIn ? "Stop holding" : "Hold";
        SetEnabled(true, detail.HoldOptIn);
    }

    private void SetEnabled(bool owned, bool held)
    {
        if (_toggle != null) _toggle.Enabled = owned;
        if (_rankUp != null) _rankUp.Enabled = owned && held;
        if (_rankDown != null) _rankDown.Enabled = owned && held;
        if (_incomeUp != null) _incomeUp.Enabled = owned;
        if (_incomeDown != null) _incomeDown.Enabled = owned;
        if (_treasuryUp != null) _treasuryUp.Enabled = owned;
        if (_treasuryDown != null) _treasuryDown.Enabled = owned;
        if (_budgetDefault != null) _budgetDefault.Enabled = owned;
    }

    private static string Percent(double fraction)
    {
        return (fraction * 100.0).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static string Marker(byte mask, int bit)
    {
        return (mask & bit) != 0 ? "   (yours)" : "   (world default)";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnToggleClicked(MyGuiControlButton button)
    {
        // No value: the server flips from what it holds, so a stale snapshot cannot invert it.
        Send(WireNodeCommand.HoldToggle, "", 0);
    }

    private void OnRankUpClicked(MyGuiControlButton button) { Nudge(1); }
    private void OnRankDownClicked(MyGuiControlButton button) { Nudge(-1); }

    private void Nudge(int delta)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null) return;
        int rank = component.Detail.HoldRank + delta;
        Send(WireNodeCommand.HoldRank, "", rank < 0 ? 0 : rank);
    }

    private void OnIncomeUpClicked(MyGuiControlButton button) { NudgeBudget("income", 5); }
    private void OnIncomeDownClicked(MyGuiControlButton button) { NudgeBudget("income", -5); }
    private void OnTreasuryUpClicked(MyGuiControlButton button) { NudgeBudget("treasury", 1); }
    private void OnTreasuryDownClicked(MyGuiControlButton button) { NudgeBudget("treasury", -1); }

    /// <summary>Nudges one budget knob by whole percent, off the value the snapshot is showing.
    /// Clamped at zero here as well as on the server: a negative share is not a thing, and -1 is the
    /// wire's own "clear this knob" sentinel, so it must never be reached by arithmetic.</summary>
    private void NudgeBudget(string knob, int deltaPercent)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null) return;
        double current = string.Equals(knob, "income", StringComparison.Ordinal)
            ? component.Detail.HoldBudgetIncomeFraction
            : component.Detail.HoldBudgetTreasuryFraction;
        int percent = (int)Math.Round(current * 100.0) + deltaPercent;
        if (percent < 0) percent = 0;
        Send(WireNodeCommand.HoldBudget, knob, percent);
    }

    private void OnBudgetDefaultClicked(MyGuiControlButton button)
    {
        // -1 is the wire's clear sentinel; three sends because the knobs are independent.
        Send(WireNodeCommand.HoldBudget, "income", -1);
        Send(WireNodeCommand.HoldBudget, "treasury", -1);
        Send(WireNodeCommand.HoldBudget, "floor", -1);
    }

    private void Send(WireNodeCommand command, string target, int value)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null) return;
        component.SendCommand(command, target, value);
    }
}
