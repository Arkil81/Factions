using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// The Build dialog: queue one hull at one shipyard, once.
///
/// <para>Outside the fleet doctrine on purpose. The doctrine says what the fleet is made of and
/// keeps it there; this queues a single hull and then it is done. A hand-built hull that matches a
/// doctrine line counts toward that line's "have" — in-progress is any queue entry for the same
/// prefab, whoever queued it — and one that matches no line is simply extra stock.</para>
///
/// <para>The hull table (<see cref="HullPicker"/>) is filled from the server's own pick-list
/// (<see cref="StationBuildComponent"/>), resolved server-side against the graph, the prefab
/// catalogue and the recipe registry. The client has none of those, so it must not be left to decide
/// what is eligible. The list is a convenience, not an authorisation: the request is re-validated
/// through the same <c>FactionRuntime</c> entry point the chat verb reaches, and its answer is what
/// the status line shows.</para>
/// </summary>
internal sealed class StationBuildScreen : MyGuiScreenBase
{
    /// <summary>Wide enough for the hull table's nine columns; <c>NewDoctrineLineScreen</c> uses the
    /// same width for the same table.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.78f);

    internal static StationBuildScreen Instance { get; private set; }

    private readonly string _nodeId;

    private HullPicker _hulls;
    private MyGuiControlLabel _hint;
    private MyGuiControlLabel _status;
    private MyGuiControlButton _queue;

    private int _drawnRevision = -1;

    private StationBuildScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The window this opened from keeps drawing, so the player can see the queue they are
        // adding to — and so its own watch keeps being re-asserted behind this one.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>Opens the dialog on <paramref name="nodeId"/>, or retargets the one already open.
    /// Both openers — the station window and the queue window — call this and neither has to know
    /// whether the other left one up.</summary>
    internal static void Open(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            StationBuildScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                // A dialog on a different yard is closed rather than reused: what can fly out of one
                // yard is not what can fly out of the other.
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationBuildScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("build dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationBuildScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        StationBuildComponent component = StationBuildComponent.Instance;
        if (component != null) component.Query(_nodeId, force: true);
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current dialog for the reason NodeDetailScreen records: SE
        // defers both the removal and the add. Nothing to release — this screen holds no watch.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Build a hull");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;

        _hulls = new HullPicker(Controls, new Vector2(left, topLeft.Y + 0.09f), WindowSize.X - 0.06f, 8,
                                "StationBuild");
        _hulls.SelectionChanged += OnSelectionChanged;

        _hint = AddLabel(new Vector2(left, _hulls.Bottom), "", 0.65f);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = -topLeft.Y - 0.10f;
        _queue = new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Queue this hull at this shipyard. The server validates every field.",
            text: new StringBuilder("Queue"),
            textScale: 0.7f,
            onButtonClick: OnQueueClicked)
        {
            Enabled = false,
        };
        Controls.Add(_queue);

        var cancel = new MyGuiControlButton(
            position: new Vector2(left + 0.18f, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: null,
            text: new StringBuilder("Close"),
            textScale: 0.7f,
            onButtonClick: _ => CloseScreen());
        Controls.Add(cancel);

        _status = AddLabel(new Vector2(left, -topLeft.Y - 0.05f), "Waiting for the server...", 0.7f);

        _drawnRevision = -1;
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        StationBuildComponent component = StationBuildComponent.Instance;
        if (component != null)
        {
            // Re-asked every frame rather than only on open, matching the node window's watch: a
            // dialog opened before the uplink was granted would otherwise stay blank until it was
            // reopened. The component drops a repeat within its own interval.
            component.Query(_nodeId, force: false);
            if (_drawnRevision != component.Revision) Rebuild(component);
        }
        return base.Draw();
    }

    private void Rebuild(StationBuildComponent component)
    {
        _drawnRevision = component.Revision;

        if (!StationBuildComponent.Available)
        {
            WriteUnavailable("This server does not offer the Build button.");
            return;
        }
        if (!component.HasOptions || !string.Equals(component.NodeId, _nodeId, StringComparison.Ordinal))
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        if (component.Prefabs.Count == 0)
        {
            WriteUnavailable("There is nothing here you may queue. This station needs a Shipyard, "
                             + "and the catalogue needs a hull for one of its roles.");
            return;
        }

        _hulls.SetHulls(component.Prefabs, component.Roles);
        RefreshHint();
        RefreshStatus(component);
    }

    /// <summary>Every state where there is nothing to pick. One path, so no half-filled table can
    /// survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_hulls != null) _hulls.Clear();
        if (_hint != null) _hint.Text = "";
        if (_queue != null) _queue.Enabled = false;
        if (_status != null) _status.Text = message;
    }

    private void OnSelectionChanged()
    {
        RefreshHint();
        if (_queue != null) _queue.Enabled = _hulls.HasSelection;
    }

    /// <summary>The one number in the table a player cannot act on after the fact. A hull that
    /// cannot leave the yard it was built at is a stranded grid and a wasted BOM, and the server will
    /// still build it if asked — this is the only warning there is.</summary>
    private void RefreshHint()
    {
        WireBuildPrefab prefab;
        if (!_hulls.TryGetSelected(out prefab))
        {
            _hint.Text = _hulls.HullCount == 0 ? "" : "Pick a hull. Click a column header to sort.";
            return;
        }
        _hint.Text = prefab.CanFlyFromHere
            ? "Built once. The fleet doctrine decides what is built on its own."
            : "This hull cannot fly out of this yard. It would be built and then stranded here.";
    }

    private void RefreshStatus(StationBuildComponent component)
    {
        if (!string.IsNullOrEmpty(component.LastResultMessage)
            && (DateTime.UtcNow - component.LastResultUtc).TotalSeconds
               < StationBuildComponent.ResultMessageSeconds)
        {
            _status.Text = OneLine(component.LastResultMessage);
            return;
        }
        _status.Text = component.Prefabs.Count + " hull(s) this shipyard can build.";
    }

    // ---- Queue --------------------------------------------------------------------------------

    private void OnQueueClicked(MyGuiControlButton button)
    {
        StationBuildComponent component = StationBuildComponent.Instance;
        if (component == null) return;

        WireBuildPrefab prefab;
        if (!_hulls.TryGetSelected(out prefab))
        {
            _status.Text = "Pick a hull first.";
            return;
        }

        component.SendBuild(_nodeId, prefab.PrefabId);
        _status.Text = "Asked the server to queue " + prefab.PrefabId + "...";
        // The dialog stays open: the server's answer is the only place a refusal is ever shown, and
        // it is shown here.
        component.Query(_nodeId, force: true);
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line
    /// is a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
