using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// Founding one claim: what a station there costs, what the faction has, and the two styles it can
/// be built in.
///
/// <para><b>Why a window and not a bare button.</b> Founding spends the faction's money in one
/// irreversible step and picks a look that cannot be changed afterwards, so the claim's own window
/// offers it and this one confirms it — the same shape <see cref="StationDevelopScreen"/> uses for
/// the other purchase a station window can make. The two style buttons are the confirmation: there
/// is no separate "yes", because choosing a style already is one.</para>
///
/// <para>It reads <see cref="NodeDetailComponent"/>'s snapshot rather than holding a subscription of
/// its own, and closes itself if the window behind is retargeted onto another node — both for the
/// reasons <see cref="StationDevelopScreen"/> records.</para>
/// </summary>
internal sealed class StationFoundScreen : MyGuiScreenBase
{
    /// <summary>Deliberately narrower than the develop menu's: there is no table here, only a
    /// paragraph and two buttons.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.55f, 0.42f);

    internal static StationFoundScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _standard;
    private MyGuiControlButton _fortified;

    private int _drawnRevision = -1;

    private StationFoundScreen(string nodeId)
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
            StationFoundScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationFoundScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("found window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationFoundScreen";
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

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Found station");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(left, top + 0.04f),
            size: new Vector2(WindowSize.X - 0.04f, 0.19f),
            backgroundColor: null,
            font: "Blue",
            textScale: 0.8f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: null,
            drawScrollbarV: true,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            textPadding: new MyGuiBorderThickness(0.006f, 0.004f))
        {
            VisualStyle = MyGuiControlMultilineStyleEnum.BackgroundBordered,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Name = "StationFoundDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.23f, 0.04f);
        float buttonTop = top + 0.04f + _detail.Size.Y + 0.02f;
        _standard = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Found: standard",
            "Build the station in your faction's own style, and pay for it now.",
            OnStandardClicked);
        _fortified = AddActionButton(new Vector2(left + 0.25f, buttonTop), buttonSize, "Found: fortified",
            "Build the station in the heavier, pirate-cut style. Same price; only the look differs.",
            OnFortifiedClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

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

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null)
        {
            if (!string.IsNullOrEmpty(component.WatchedNodeId)
                && !string.Equals(component.WatchedNodeId, _nodeId, StringComparison.Ordinal))
            {
                CloseScreen();
                return base.Draw();
            }
            component.Watch(_nodeId);
            if (_drawnRevision != component.Revision) Rebuild(component);
        }
        return base.Draw();
    }

    private void Rebuild(NodeDetailComponent component)
    {
        _drawnRevision = component.Revision;

        if (!NodeDetailComponent.Available)
        {
            WriteUnavailable("Not linked to a Factions server.");
            return;
        }
        if (!component.HasSnapshot
            || !string.Equals(component.Detail.Id, _nodeId, StringComparison.Ordinal))
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }

        WireNodeDetail detail = component.Detail;
        if (!detail.Exists)
        {
            WriteUnavailable("This claim is no longer on the server's map.");
            return;
        }
        if (!detail.Owned)
        {
            WriteUnavailable("Only your own faction's claims can be founded on.");
            return;
        }

        _heading.Text = (string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName)
                        + "  -  " + detail.Id;
        RefreshDetail(detail);
        // The block travels from the same validator the command runs, so a button offered here is
        // one the server will honour.
        bool ready = (WireFoundBlock)detail.FoundBlock == WireFoundBlock.None;
        if (_standard != null) _standard.Enabled = ready;
        if (_fortified != null) _fortified.Enabled = ready;
        RefreshStatus(component, detail);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        if (_standard != null) _standard.Enabled = false;
        if (_fortified != null) _fortified.Enabled = false;
        if (_status != null) _status.Text = message;
    }

    private void RefreshDetail(WireNodeDetail detail)
    {
        _detail.Clear();
        AppendLine("This claim has no station on it. Founding one plants a real station on the node, "
                   + "which is what makes it eligible for capabilities, a store and contracts.");
        AppendLine("");
        AppendLine("Cost:     " + detail.FoundCost + " credits");
        AppendLine("Treasury: " + detail.FoundHave + " credits");
        AppendLine("");

        switch ((WireFoundBlock)detail.FoundBlock)
        {
            case WireFoundBlock.None:
                AppendLine("Pick a style below. The style is the station's look and cannot be changed "
                           + "afterwards; it does not change what the station can do or what it costs.");
                break;
            case WireFoundBlock.AlreadyFounded:
                AppendLine("This node already has a station.");
                break;
            case WireFoundBlock.InsufficientTreasury:
                AppendLine("Your faction is " + (detail.FoundCost - detail.FoundHave)
                           + " credits short. The faction treasury pays for this, not your own wallet "
                           + "— trade at your stations, or deposit to the faction account on the "
                           + "faction terminal.");
                break;
        }
    }

    private void AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text)) _detail.AppendText(text);
        _detail.AppendLine();
    }

    private void RefreshStatus(NodeDetailComponent component, WireNodeDetail detail)
    {
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < NodeDetailComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = "updated " + age + "s ago";
    }

    private static string OneLine(string text)
    {
        return string.IsNullOrEmpty(text) ? "" : text.Replace('\n', ' ').Replace('\r', ' ');
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnStandardClicked(MyGuiControlButton button)
    {
        Send("standard");
    }

    private void OnFortifiedClicked(MyGuiControlButton button)
    {
        Send("fortified");
    }

    /// <summary>Closes on the way out rather than waiting for the reply. The purchase is one-shot
    /// and the claim's own window is behind this one showing the result, so leaving a live pair of
    /// Found buttons over a node that now has a station invites a second click at a refusal.</summary>
    private void Send(string style)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null) return;
        if (_standard != null) _standard.Enabled = false;
        if (_fortified != null) _fortified.Enabled = false;
        component.SendCommand(WireNodeCommand.Found, style, 0);
        CloseScreen();
    }
}
