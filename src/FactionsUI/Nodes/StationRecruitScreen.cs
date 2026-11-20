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
/// Recruiting workers into this station's Habitat out of its body's civilian pool: how many live
/// here, the beds they have, what each recruit costs, and how long the node's food and air keep them.
///
/// <para><b>Why a window and not a bare button.</b> A recruit spends the faction treasury and
/// commits it to feeding one more mouth, so the station window offers it and this one confirms it,
/// the shape <see cref="StationFoundScreen"/> uses for the other purchase. It stays open after a
/// click, unlike that one: recruiting is repeatable, and the reply under the buttons says how many
/// the server granted.</para>
///
/// <para>The server trims a request to the beds left and what the treasury buys, so the buttons ask
/// for a number and the reply is the answer. FAC-493.</para>
/// </summary>
internal sealed class StationRecruitScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.55f, 0.42f);

    internal static StationRecruitScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _one;
    private MyGuiControlButton _five;
    private MyGuiControlButton _fill;

    private int _room;
    private int _drawnRevision = -1;

    private StationRecruitScreen(string nodeId)
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
            StationRecruitScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationRecruitScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("recruit window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationRecruitScreen";
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
        AddCaption("Recruit workers");

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
            Name = "StationRecruitDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.155f, 0.04f);
        float buttonTop = top + 0.04f + _detail.Size.Y + 0.02f;
        _one = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Recruit 1",
            "Recruit one worker and pay for them now.", b => Send(1));
        _five = AddActionButton(new Vector2(left + 0.17f, buttonTop), buttonSize, "Recruit 5",
            "Recruit five workers, or as many as fit, and pay for them now.", b => Send(5));
        _fill = AddActionButton(new Vector2(left + 0.34f, buttonTop), buttonSize, "Fill the beds",
            "Recruit a worker for every empty bed, or as many as the treasury buys.", b => Send(_room));

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
            else RefreshStatus(component);
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
        if (!detail.Exists || !detail.Owned)
        {
            WriteUnavailable("Only your own faction's stations can recruit.");
            return;
        }

        _heading.Text = (string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName)
                        + "  -  " + detail.Id;
        _room = Math.Max(0, detail.WorkerBeds - detail.Workers);
        RefreshDetail(detail);
        bool open = detail.RecruitPrice > 0 && _room > 0;
        if (_one != null) _one.Enabled = open;
        if (_five != null) _five.Enabled = open && _room > 1;
        if (_fill != null)
        {
            _fill.Enabled = open;
            _fill.Text = _room > 0 ? "Fill the beds (" + _room + ")" : "Fill the beds";
        }
        RefreshStatus(component);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        if (_one != null) _one.Enabled = false;
        if (_five != null) _five.Enabled = false;
        if (_fill != null) _fill.Enabled = false;
        if (_status != null) _status.Text = message;
    }

    private void RefreshDetail(WireNodeDetail detail)
    {
        _detail.Clear();
        AppendLine("Workers:  " + detail.Workers + " of " + detail.WorkerBeds + " beds");
        if (detail.RecruitPrice > 0)
            AppendLine("Price:    " + detail.RecruitPrice.ToString("N0", CultureInfo.InvariantCulture)
                       + " credits a worker, from the faction treasury");
        AppendLine("Supplies: " + NodeDetailScreen.DescribeWorkerSupply(detail));
        AppendLine("");

        if (detail.WorkerBeds <= 0)
            AppendLine("This station has no Habitat. Develop one first; each level adds beds.");
        else if (detail.RecruitPrice <= 0)
            AppendLine("There is nobody to recruit here. Workers are recruited on Tellus and live "
                       + "where there are beds for them.");
        else if (_room <= 0)
            AppendLine("Every bed is taken. Another Habitat level adds more.");
        else
            AppendLine("Every worker eats and breathes from this station's stock: keep food and oxygen "
                       + "here. The ones it cannot feed leave.");
    }

    private void AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text)) _detail.AppendText(text);
        _detail.AppendLine();
    }

    private void RefreshStatus(NodeDetailComponent component)
    {
        if (_status == null) return;
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

    private void Send(int count)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null || count <= 0) return;
        component.SendCommand(WireNodeCommand.Recruit, "", count);
    }
}
