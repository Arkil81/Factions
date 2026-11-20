using System;
using System.Text;
using Factions.Wire;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Diplomacy;

/// <summary>
/// The stance readout and the <b>Diplomacy...</b> button on SE's own Factions page (K menu), in the
/// band under the reputation bar that the hidden private-info box leaves empty whenever a foreign
/// faction is selected (docs/client-plugin.md, Part 17).
///
/// <para>Vanilla lays that page out with absolute positions, so nothing here is placed by a
/// constant: the band is measured off the reputation bar the page has just built. What is drawn is
/// two lines — both stances, then embargo and truce — and the button right-aligned beside them.</para>
///
/// <para>Shown only when the selected faction is one the server sent a row for: a managed NPC, while
/// the viewer is in a registered player faction. Everything else — the viewer's own faction, a
/// player faction, SE's default factions, no selection — leaves the page exactly as vanilla drew it.</para>
///
/// <para>Two hooks feed it (<see cref="Patches.FactionsPageDiplomacyPatch"/>): the page build adds
/// the controls, and the controller's <c>RefreshFactionProperties</c> — which every selection and
/// every vanilla refresh funnels through — reports the selection. A snapshot arriving between the two
/// is picked up by <see cref="Tick"/> on the next frame.</para>
/// </summary>
internal static class FactionsPageReadout
{
    private const string HeadName = "FactionsUI.DiplomacyHead";
    private const string TailName = "FactionsUI.DiplomacyTail";
    private const string ButtonName = "FactionsUI.DiplomacyButton";

    private static readonly Vector2 ButtonSize = new Vector2(0.12f, 0.034f);

    private static MyGuiControlLabel _head;
    private static MyGuiControlLabel _tail;
    private static MyGuiControlButton _button;

    private static string _selectedTag;
    private static bool _selectedForeign;
    private static int _drawnRevision = -1;
    private static DateTime _drawnUtc;

    internal static bool Attached => _head != null;

    /// <summary>The NPC the page has selected, when the readout is showing one.</summary>
    internal static string ShownTag => _head != null && _head.Visible ? _selectedTag : null;

    /// <summary>Adds the controls to a freshly built Factions page. Throws when the page does not
    /// have the reputation bar it is measured against; the caller removes whatever was added.</summary>
    internal static void Build(MyGuiControlTabPage page)
    {
        Detach();
        MyGuiControlBase bar = page.Controls.GetControlByName("progressReputation");
        if (bar == null) throw new InvalidOperationException("the Factions page has no progressReputation control");

        float left = bar.PositionX;
        float right = bar.PositionX + bar.Size.X;
        float top = bar.PositionY + bar.Size.Y + 0.003f;
        float textWidth = bar.Size.X - ButtonSize.X - 0.012f;

        var head = new MyGuiControlLabel(
            position: new Vector2(left, top), size: new Vector2(textWidth, 0.018f), text: "",
            textScale: 0.62f, font: "Blue", originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            isAutoEllipsisEnabled: true, maxWidth: textWidth)
        {
            Name = HeadName,
            Visible = false,
        };
        var tail = new MyGuiControlLabel(
            position: new Vector2(left, top + 0.019f), size: new Vector2(textWidth, 0.018f), text: "",
            textScale: 0.62f, font: "Blue", originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            isAutoEllipsisEnabled: true, maxWidth: textWidth)
        {
            Name = TailName,
            Visible = false,
        };
        var button = new MyGuiControlButton(
            position: new Vector2(right, top),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: ButtonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP,
            toolTip: "Embargo, gift, peace, non-aggression and alliance toward this faction. "
                     + "Any member can read it; only a leader can act.",
            text: new StringBuilder("Diplomacy..."),
            textScale: 0.7f,
            onButtonClick: OnDiplomacyClicked)
        {
            Name = ButtonName,
            Visible = false,
            ShowTooltipWhenDisabled = true,
        };

        page.Controls.Add(head);
        page.Controls.Add(tail);
        page.Controls.Add(button);
        _head = head;
        _tail = tail;
        _button = button;
        _selectedTag = null;
        _selectedForeign = false;
        _drawnRevision = -1;
    }

    /// <summary>Removes the controls from <paramref name="page"/> after a failed build, so a half-
    /// built readout is never left on the page.</summary>
    internal static void Remove(MyGuiControlTabPage page)
    {
        if (page != null)
        {
            foreach (string name in new[] { HeadName, TailName, ButtonName })
            {
                MyGuiControlBase c = page.Controls.GetControlByName(name);
                if (c != null) page.Controls.Remove(c);
            }
        }
        Detach();
    }

    internal static void Detach()
    {
        _head = null;
        _tail = null;
        _button = null;
        _selectedTag = null;
        _selectedForeign = false;
        _drawnRevision = -1;
    }

    /// <summary>The controller's selection, reported after each vanilla refresh.</summary>
    internal static void OnSelection(IMyFaction selected, IMyFaction user)
    {
        if (!Attached) return;
        _selectedTag = selected != null ? selected.Tag : null;
        _selectedForeign = selected != null && (user == null || user.FactionId != selected.FactionId);
        Redraw();
    }

    /// <summary>Every frame, from <c>Plugin.Update</c>. Holds the subscription while the page or the
    /// Diplomacy window is up, drops the page's controls once the terminal has closed, and redraws
    /// when a snapshot arrived.</summary>
    internal static void Tick(bool terminalOpen)
    {
        if (!terminalOpen && Attached) Detach();

        DiplomacyComponent component = DiplomacyComponent.Instance;
        if (component == null) return;
        component.SetStreaming((terminalOpen && Attached) || DiplomacyScreen.Instance != null);

        if (!Attached) return;
        // The truce countdown moves without a snapshot, so the text is refreshed on a slow beat too.
        if (component.Revision != _drawnRevision || (DateTime.UtcNow - _drawnUtc).TotalSeconds >= 20)
            Redraw();
    }

    private static void Redraw()
    {
        if (!Attached) return;
        DiplomacyComponent component = DiplomacyComponent.Instance;
        _drawnRevision = component != null ? component.Revision : -1;
        _drawnUtc = DateTime.UtcNow;

        WireDiplomacyRow row;
        bool show = component != null && _selectedForeign && component.HasSnapshot
                    && component.TryGetRow(_selectedTag, out row);
        if (!show)
        {
            _head.Visible = false;
            _tail.Visible = false;
            _button.Visible = false;
            return;
        }

        component.TryGetRow(_selectedTag, out row);
        long now = component.ServerNowTicks;
        _head.Text = DiplomacyText.StanceHead(row);
        _tail.Text = DiplomacyText.StanceTail(row, now);
        string tip = DiplomacyText.Tooltip(row, now);
        _head.SetToolTip(tip);
        _tail.SetToolTip(tip);
        _head.Visible = true;
        _tail.Visible = true;
        _button.Visible = true;
    }

    private static void OnDiplomacyClicked(MyGuiControlButton sender)
    {
        string tag = ShownTag;
        if (!string.IsNullOrEmpty(tag)) DiplomacyScreen.Open(tag);
    }
}
