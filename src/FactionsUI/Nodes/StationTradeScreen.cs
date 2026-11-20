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
/// Whether Factions trades on this faction's behalf, and the faction-wide budget that pays for it.
///
/// <para><b>Why this exists.</b> An NPC faction's brain ranks trade partners every cycle and its
/// manager flies convoys off that ranking; a player faction's brain is switched off by design, so
/// before this a player faction could neither fly out to buy what it was short of nor sell what it
/// had spare. Worse, it was invisible: the same gate that stopped its own convoys also stopped it
/// publishing what it lacked, so no neighbour could offer either. Measured live on 2026-09-09, where
/// a player faction carried ninety unmet demands across twenty-seven items while every NPC faction
/// around it traded normally.</para>
///
/// <para><b>Two switches, not one.</b> Selling moves goods the faction already has; buying spends
/// its treasury and tells every neighbour what it is short of. A player willing to do the first is
/// not thereby willing to do the second.</para>
///
/// <para>A window of its own for the reason <see cref="StationHoldScreen"/> is one, and it reads
/// <see cref="NodeDetailComponent"/>'s snapshot rather than taking a watch of its own — the station
/// window behind it owns that lifecycle.</para>
///
/// <para><b>The partner count and the hull row are the point of this window, not decoration.</b>
/// Both failures they report are otherwise silent: a faction that ranks no partner dispatches
/// nothing and logs nothing, and a buy pass with no cargo hull returns without a counter. A window
/// showing only the two switches would say "on" over a feature doing nothing at all.</para>
/// </summary>
internal sealed class StationTradeScreen : MyGuiScreenBase
{
    /// <summary>Deliberately <see cref="StationHoldScreen"/>'s, for the reason that screen
    /// records: these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationTradeScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlCheckbox _buyBox;
    private MyGuiControlCheckbox _sellBox;
    private MyGuiControlLabel _buyLabel;
    private MyGuiControlLabel _sellLabel;
    private MyGuiControlButton _incomeUp;
    private MyGuiControlButton _incomeDown;
    private MyGuiControlButton _treasuryUp;
    private MyGuiControlButton _treasuryDown;
    private MyGuiControlButton _budgetDefault;

    private int _drawnRevision = -1;

    /// <summary>True while <see cref="Refresh"/> is writing the boxes from the snapshot.
    ///
    /// <para>Assigning <c>IsChecked</c> may raise <c>IsCheckedChanged</c>, and this screen writes
    /// both boxes on every revision — so without this guard a refresh would send the command that
    /// caused it, every time the snapshot moved. Written defensively rather than because the setter
    /// was confirmed to fire: it is correct either way, and the failure it prevents is a per-frame
    /// command loop against the server.</para></summary>
    private bool _syncing;

    private StationTradeScreen(string nodeId)
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
            StationTradeScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationTradeScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("trade window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationTradeScreen";
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
        AddCaption("Trade for this faction");

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

        // Tickboxes rather than flipping buttons: these are two independent on/off states, and a
        // button reading "Stop buying" states the current setting only by implication -- with two
        // side by side that is easy to misread.
        float row = top + 0.42f;
        _buyLabel = AddLabel(new Vector2(left, row + 0.004f), "Buy what we are short of");
        _buyBox = AddCheckbox(new Vector2(left + 0.30f, row),
            "Send your own convoys out to acquire what your stations are short of, and let "
            + "neighbouring factions see that shortage so they can supply it. Spends the faction "
            + "treasury.", OnBuyChanged);

        row += 0.045f;
        _sellLabel = AddLabel(new Vector2(left, row + 0.004f), "Sell what we have spare");
        _sellBox = AddCheckbox(new Vector2(left + 0.30f, row),
            "Send surplus out to a faction that has asked for it. Earns credits and costs you the "
            + "goods.", OnSellChanged);

        row += 0.055f;
        _incomeUp = AddActionButton(new Vector2(left, row), buttonSize, "Income +5%",
            "Raise the share of recent income trade may spend per window.", OnIncomeUpClicked);
        _incomeDown = AddActionButton(new Vector2(left + gap, row), buttonSize, "Income -5%",
            "Lower it.", OnIncomeDownClicked);
        _treasuryUp = AddActionButton(new Vector2(left + gap * 2, row), buttonSize, "Treasury +1%",
            "Raise the share of spendable treasury surplus trade may draw on per window.",
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

    private MyGuiControlLabel AddLabel(Vector2 position, string text)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, 0.8f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    /// <summary>A tickbox. The handler is attached AFTER
    /// construction and every later write goes through <see cref="_syncing"/>, so neither the
    /// initial value nor a refresh can be mistaken for a click.</summary>
    private MyGuiControlCheckbox AddCheckbox(Vector2 position, string tooltip, Action<MyGuiControlCheckbox> onChanged)
    {
        var box = new MyGuiControlCheckbox(
            position: position,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip)
        {
            Enabled = false,
        };
        box.IsCheckedChanged = onChanged;
        Controls.Add(box);
        return box;
    }

    /// <summary>Named arguments, copied from <see cref="StationHoldScreen"/>'s: this constructor's
    /// positional order is not the one it reads like, and getting it wrong compiles as four
    /// unrelated conversion errors rather than anything that names the real mistake.</summary>
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
            SetChecked(false, false);
            SetEnabled(false);
            return;
        }

        _heading.Text = string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName;

        var sb = new StringBuilder();
        if (!detail.Owned)
        {
            sb.AppendLine("This node is not yours, so there is no trade policy to set here.");
            _detail.Text = sb;
            SetChecked(false, false);
            SetEnabled(false);
            return;
        }

        bool anyOn = detail.TradeBuyOptIn || detail.TradeSellOptIn;

        // The boxes below carry the on/off state now, so this says what each one MEANS rather than
        // repeating whether it is set.
        sb.AppendLine(detail.TradeBuyOptIn
            ? "Buying is on: your convoys go out for what you are short of, and neighbours can see"
              + " the shortage and offer to supply it."
            : "Buying is off: nothing is fetched, and no other faction can tell what you lack.");
        sb.AppendLine(detail.TradeSellOptIn
            ? "Selling is on: surplus goes out to factions that have asked for it."
            : "Selling is off: your surplus stays where it is.");
        sb.AppendLine();

        if (anyOn)
        {
            // The two silent failures. Reported before the budget, because neither is fixed by
            // money and both make everything below irrelevant.
            if (detail.TradePartners == 0)
            {
                sb.AppendLine("NO PARTNERS RANKED — nothing will be dispatched either way.");
                sb.AppendLine("  A faction qualifies when you are not hostile to it, have not");
                sb.AppendLine("  embargoed it, and your influence reaches one of its nodes.");
            }
            else
            {
                sb.AppendLine(detail.TradePartners + " partner faction(s) ranked.");
            }

            if (!detail.TradeHasHull)
                sb.AppendLine("NO TRADE-CAPABLE HULL — the buying direction cannot dispatch without one.");

            sb.AppendLine("Convoys out: " + detail.TradeInFlight + " of " + detail.TradeMaxFleets + " allowed.");
            sb.AppendLine("Rows published for neighbours to serve: " + detail.TradeUnserved
                          + (detail.TradeBuyOptIn ? "" : "  (buying is off)"));
            sb.AppendLine();
        }

        sb.AppendLine("Faction budget per window — what dispatching may spend:");
        sb.AppendLine("  income   " + Percent(detail.TradeBudgetIncomeFraction)
                      + Marker(detail.TradeBudgetOverrideMask, 1));
        sb.AppendLine("  treasury " + Percent(detail.TradeBudgetTreasuryFraction)
                      + Marker(detail.TradeBudgetOverrideMask, 2));
        sb.AppendLine("  floor    " + detail.TradeBudgetFloor + " credits"
                      + Marker(detail.TradeBudgetOverrideMask, 4));
        sb.AppendLine();
        sb.AppendLine("This bounds what you spend SENDING convoys. The goods themselves are paid "
                      + "for at the stop. Your treasury is the hard limit whatever the budget says. "
                      + "The floor is set with !factions station tradebudget floor <credits>.");
        _detail.Text = sb;

        SetChecked(detail.TradeBuyOptIn, detail.TradeSellOptIn);
        SetEnabled(true);
    }

    /// <summary>Writes both boxes from the snapshot without either write reading as a click.</summary>
    private void SetChecked(bool buy, bool sell)
    {
        _syncing = true;
        try
        {
            if (_buyBox != null) _buyBox.IsChecked = buy;
            if (_sellBox != null) _sellBox.IsChecked = sell;
        }
        finally { _syncing = false; }
    }

    private void SetEnabled(bool owned)
    {
        if (_buyBox != null) _buyBox.Enabled = owned;
        if (_sellBox != null) _sellBox.Enabled = owned;
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

    /// <summary>Sends the state the BOX now shows, not a flip of what the snapshot said. The wire
    /// verb carries the direction and the wanted value, so a stale snapshot re-asserts what the
    /// player ticked instead of inverting the other direction.
    ///
    /// <para>Ignored while <see cref="_syncing"/>: that write came from the server, not the
    /// player.</para></summary>
    private void OnBuyChanged(MyGuiControlCheckbox box)
    {
        if (_syncing || box == null) return;
        Send(WireNodeCommand.TradeToggle, "buy", box.IsChecked ? 1 : 0);
    }

    private void OnSellChanged(MyGuiControlCheckbox box)
    {
        if (_syncing || box == null) return;
        Send(WireNodeCommand.TradeToggle, "sell", box.IsChecked ? 1 : 0);
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
            ? component.Detail.TradeBudgetIncomeFraction
            : component.Detail.TradeBudgetTreasuryFraction;
        int percent = (int)Math.Round(current * 100.0) + deltaPercent;
        if (percent < 0) percent = 0;
        Send(WireNodeCommand.TradeBudget, knob, percent);
    }

    private void OnBudgetDefaultClicked(MyGuiControlButton button)
    {
        // -1 is the wire's clear sentinel; three sends because the knobs are independent.
        Send(WireNodeCommand.TradeBudget, "income", -1);
        Send(WireNodeCommand.TradeBudget, "treasury", -1);
        Send(WireNodeCommand.TradeBudget, "floor", -1);
    }

    private void Send(WireNodeCommand command, string target, int value)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null) return;
        component.SendCommand(command, target, value);
    }
}
