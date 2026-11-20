using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Diplomacy;

/// <summary>
/// The Diplomacy window: everything the vanilla Factions page has no button for, toward one NPC
/// faction — embargo and lift, a gift at its stated cost, a peace offer, a non-aggression or alliance
/// proposal, and withdrawing whichever offer is out — plus the pending offers both ways across every
/// NPC (docs/diplomacy-plan.md, rule 5; docs/client-plugin.md, Part 17).
///
/// <para>Opened from the Diplomacy... button on the Factions page, over the terminal, in the
/// Develop.../Build... pattern. It reads <see cref="DiplomacyComponent"/>'s snapshot and holds no
/// subscription of its own: <see cref="FactionsPageReadout.Tick"/> keeps the one subscription alive
/// while this window is up.</para>
///
/// <para><b>Buttons are disabled only for a non-leader</b>, with a tooltip saying why, and for a
/// withdraw with nothing out. Every other refusal (no war to end, a gift on cooldown, a short
/// treasury) stays clickable on purpose, for the reason <c>StationDevelopScreen</c> records: the
/// client holds no copy of the rule to disable against, and the server's refusal is a sentence where
/// a dead button is silence. The detail block already says what is in the way.</para>
/// </summary>
internal sealed class DiplomacyScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.62f, 0.84f);

    internal static DiplomacyScreen Instance { get; private set; }

    private string _npcTag;

    private MyGuiControlLabel _heading;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _embargo;
    private MyGuiControlButton _gift;
    private MyGuiControlButton _peace;
    private MyGuiControlButton _withdraw;
    private MyGuiControlButton _nonAggression;
    private MyGuiControlButton _alliance;
    private MyGuiControlTable _offers;
    private MyGuiControlLabel _status;

    private int _drawnRevision = -1;
    private DateTime _drawnUtc;

    private DiplomacyScreen(string npcTag)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _npcTag = npcTag;
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string npcTag)
    {
        if (string.IsNullOrEmpty(npcTag)) return;
        try
        {
            DiplomacyScreen existing = Instance;
            if (existing != null)
            {
                existing.Retarget(npcTag);
                return;
            }
            MyGuiSandbox.AddScreen(new DiplomacyScreen(npcTag));
        }
        catch (Exception ex)
        {
            Plugin.Log("diplomacy window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.DiplomacyScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    private void Retarget(string npcTag)
    {
        _npcTag = npcTag;
        _drawnRevision = -1;
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Diplomacy");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float width = WindowSize.X - 0.04f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(left, top + 0.04f),
            size: new Vector2(width, 0.22f),
            backgroundColor: null,
            font: "Blue",
            textScale: 0.72f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: null,
            drawScrollbarV: true,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            textPadding: new MyGuiBorderThickness(0.006f, 0.004f))
        {
            VisualStyle = MyGuiControlMultilineStyleEnum.BackgroundBordered,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Name = "DiplomacyDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2((width - 0.02f) * 0.5f, 0.04f);
        float col2 = left + buttonSize.X + 0.02f;
        float row1 = top + 0.04f + 0.22f + 0.015f;
        _embargo = AddButton(new Vector2(left, row1), buttonSize, "Embargo", OnEmbargoClicked);
        _gift = AddButton(new Vector2(col2, row1), buttonSize, "Send gift", OnGiftClicked);
        _peace = AddButton(new Vector2(left, row1 + 0.05f), buttonSize, "Offer peace", OnPeaceClicked);
        _withdraw = AddButton(new Vector2(col2, row1 + 0.05f), buttonSize, "Withdraw offer", OnWithdrawClicked);
        _nonAggression = AddButton(new Vector2(left, row1 + 0.10f), buttonSize, "Propose non-aggression", OnNonAggressionClicked);
        _alliance = AddButton(new Vector2(col2, row1 + 0.10f), buttonSize, "Propose alliance", OnAllianceClicked);

        float offersTop = row1 + 0.16f;
        Controls.Add(new MyGuiControlLabel(
            new Vector2(left, offersTop), null, "Pending offers, every faction (select one to switch to it)", null, 0.72f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        // VisibleRowsCount, not Size.Y, decides the table's height; see StationDevelopScreen.
        _offers = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, offersTop + 0.03f),
            Size = new Vector2(width, 0f),
            Name = "DiplomacyOffers",
            ColumnsCount = 3,
            VisibleRowsCount = 4,
        };
        _offers.SetCustomColumnWidths(new float[3] { 0.2f, 0.5f, 0.3f });
        _offers.SetColumnName(0, new StringBuilder("Faction"));
        _offers.SetColumnName(1, new StringBuilder("Offer"));
        _offers.SetColumnName(2, new StringBuilder("Expires in"));
        _offers.ItemSelected += OnOfferSelected;
        Controls.Add(_offers);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            isAutoEllipsisEnabled: true, maxWidth: width);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    private MyGuiControlButton AddButton(Vector2 position, Vector2 size, string text, Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: position,
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: size,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: onClick)
        {
            Enabled = false,
            ShowTooltipWhenDisabled = true,
        };
        Controls.Add(button);
        return button;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        DiplomacyComponent component = DiplomacyComponent.Instance;
        if (component != null
            && (_drawnRevision != component.Revision || (DateTime.UtcNow - _drawnUtc).TotalSeconds >= 20))
            Rebuild(component);
        return base.Draw();
    }

    private void Rebuild(DiplomacyComponent component)
    {
        _drawnRevision = component.Revision;
        _drawnUtc = DateTime.UtcNow;

        if (!DiplomacyComponent.Available)
        {
            WriteUnavailable("Not linked to a Factions server.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        if (string.IsNullOrEmpty(component.FactionTag))
        {
            WriteUnavailable("Your faction is not registered with Factions yet.");
            return;
        }

        WireDiplomacyRow row;
        if (!component.TryGetRow(_npcTag, out row))
        {
            WriteUnavailable(_npcTag + " is not an NPC faction your faction can deal with.");
            RebuildOffers(component);
            return;
        }

        long now = component.ServerNowTicks;
        _heading.Text = "[" + component.FactionTag + "] and " + row.NpcTag;
        _detail.Clear();
        _detail.AppendText(DiplomacyText.Readout(row, component.FactionTag, component.GiftCost, component.Treasury, now));
        RefreshButtons(component, row);
        RebuildOffers(component);
        RefreshStatus(component);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _npcTag ?? "";
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        foreach (MyGuiControlButton b in Buttons()) if (b != null) b.Enabled = false;
        if (_offers != null) _offers.Clear();
        if (_status != null) _status.Text = message;
    }

    private MyGuiControlButton[] Buttons()
    {
        return new[] { _embargo, _gift, _peace, _withdraw, _nonAggression, _alliance };
    }

    private void RefreshButtons(DiplomacyComponent component, WireDiplomacyRow row)
    {
        bool leader = component.IsLeader;
        string notLeader = "Only a leader of [" + component.FactionTag + "] can change its diplomacy.";

        _embargo.Text = row.EmbargoByYou ? "Lift embargo" : "Embargo";
        Set(_embargo, leader, notLeader, row.EmbargoByYou
            ? "Lift your embargo on " + row.NpcTag + "."
              + (row.EmbargoByThem ? " " + row.NpcTag + " still embargoes you, so trade stays barred." : "")
            : "Stop convoy trade with " + row.NpcTag + " both ways. Your stance does not change; an embargo is not an attack.");

        _gift.Text = "Send gift (" + component.GiftCost.ToString("N0") + ")";
        Set(_gift, leader, notLeader,
            "Pay " + component.GiftCost.ToString("N0") + " from the faction treasury to improve " + row.NpcTag
            + "'s view of you. Your own stance does not change.");

        Set(_peace, leader, notLeader,
            "Offer " + row.NpcTag + " a truce. It holds when they are war-weary or offer peace too. "
            + "Refused when neither side is hostile.");

        bool offerOut = !string.IsNullOrEmpty(row.YouOffered);
        Set(_withdraw, leader && offerOut, leader ? "You have no offer out to " + row.NpcTag + "." : notLeader,
            "Withdraw your " + DiplomacyText.OfferName(row.YouOffered) + " offer to " + row.NpcTag + ".");

        Set(_nonAggression, leader, notLeader,
            "Propose a non-aggression window. It concludes if " + row.NpcTag + " offers peace too or is war-weary.");
        Set(_alliance, leader, notLeader,
            "Propose an alliance. It concludes only if " + row.NpcTag + " proposes one to you too.");
    }

    private static void Set(MyGuiControlButton button, bool enabled, string disabledWhy, string tooltip)
    {
        button.Enabled = enabled;
        button.SetToolTip(enabled ? tooltip : disabledWhy);
    }

    private void RebuildOffers(DiplomacyComponent component)
    {
        _offers.Clear();
        long now = component.ServerNowTicks;
        foreach (WireDiplomacyRow r in component.Ordered)
        {
            if (!string.IsNullOrEmpty(r.YouOffered))
                AddOffer(r.NpcTag, "you offered " + DiplomacyText.OfferName(r.YouOffered),
                         DiplomacyText.Remaining(r.YouOfferedExpiresTicks, now));
            if (!string.IsNullOrEmpty(r.TheyOffered))
                AddOffer(r.NpcTag, "they offered " + DiplomacyText.OfferName(r.TheyOffered),
                         DiplomacyText.Remaining(r.TheyOfferedExpiresTicks, now));
        }
    }

    private void AddOffer(string tag, string what, string expires)
    {
        var row = new MyGuiControlTable.Row(tag);
        row.AddCell(new MyGuiControlTable.Cell(tag));
        row.AddCell(new MyGuiControlTable.Cell(what));
        row.AddCell(new MyGuiControlTable.Cell(string.IsNullOrEmpty(expires) ? "-" : expires));
        _offers.Add(row);
    }

    private void RefreshStatus(DiplomacyComponent component)
    {
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds < DiplomacyComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            _status.SetToolTip(component.LastCommandMessage);
            return;
        }
        _status.Text = component.IsLeader
            ? "You lead [" + component.FactionTag + "]: these act for the whole faction."
            : "Read-only: only a leader of [" + component.FactionTag + "] can act. Chat: !factions diplomacy status";
        _status.SetToolTip((MyToolTips)null);
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnEmbargoClicked(MyGuiControlButton b)
    {
        DiplomacyComponent component = DiplomacyComponent.Instance;
        WireDiplomacyRow row;
        if (component == null || !component.TryGetRow(_npcTag, out row)) return;
        Send(row.EmbargoByYou ? WireDiplomacyAct.Lift : WireDiplomacyAct.Embargo);
    }

    private void OnGiftClicked(MyGuiControlButton b) { Send(WireDiplomacyAct.Gift); }
    private void OnPeaceClicked(MyGuiControlButton b) { Send(WireDiplomacyAct.Peace); }
    private void OnWithdrawClicked(MyGuiControlButton b) { Send(WireDiplomacyAct.Withdraw); }
    private void OnNonAggressionClicked(MyGuiControlButton b) { Send(WireDiplomacyAct.NonAggression); }
    private void OnAllianceClicked(MyGuiControlButton b) { Send(WireDiplomacyAct.Alliance); }

    private void Send(WireDiplomacyAct act)
    {
        DiplomacyComponent component = DiplomacyComponent.Instance;
        if (component == null || string.IsNullOrEmpty(_npcTag)) return;
        component.SendCommand(act, _npcTag);
    }

    private void OnOfferSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        string tag = row != null ? row.UserData as string : null;
        if (string.IsNullOrEmpty(tag) || string.Equals(tag, _npcTag, StringComparison.OrdinalIgnoreCase)) return;
        Retarget(tag);
    }

    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
