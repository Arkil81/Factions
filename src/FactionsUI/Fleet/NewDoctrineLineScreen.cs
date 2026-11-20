using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Fleet;

/// <summary>
/// The create dialog behind the Fleet window's "New line..." button: one hull, how many the faction
/// should own, how many may build at once, and where. A line names a hull and no role; the server
/// reads what it answers off the hull.
///
/// <para>The hull table is the whole reason this dialog is not a chat verb. The complaints the
/// doctrine was written for — a stream of tiny drones, an ion hull that cannot leave a planet — are
/// both answered by comparing cargo, Dps, propulsion and BOM cost across hulls before choosing one
/// (<see cref="HullPicker"/>). Every one of those numbers is the server's; the client has no prefab
/// catalogue and could not derive a single field here.</para>
///
/// <para>The pick-lists are a convenience, not an authorisation: the create request is re-validated
/// through the same <c>FactionRuntime</c> entry point the chat verb reaches.</para>
/// </summary>
internal sealed class NewDoctrineLineScreen : MyGuiScreenBase
{
    /// <summary>Wide enough for the hull table's nine columns; <c>StationBuildScreen</c> uses the
    /// same width for the same table.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.9f);

    /// <summary>Combo key for "any owned shipyard". Negative so it cannot collide with an index into
    /// the component's scope-node list.</summary>
    private const int AnyYardKey = -1;

    internal static NewDoctrineLineScreen Instance { get; private set; }

    private HullPicker _hulls;
    private MyGuiControlCombobox _scope;
    private MyGuiControlTextbox _target;
    private MyGuiControlTextbox _concurrent;
    private MyGuiControlLabel _hint;
    private MyGuiControlLabel _status;
    private MyGuiControlButton _create;

    private NewDoctrineLineScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The doctrine behind this dialog keeps drawing, so the player can see what they already
        // have — and so its own subscription keeps being re-asserted behind this one.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open()
    {
        try
        {
            if (Instance != null) return;
            FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
            if (component == null || !component.HasOptions)
            {
                Plugin.Log("new doctrine line dialog refused: no pick-lists yet");
                return;
            }
            MyGuiSandbox.AddScreen(new NewDoctrineLineScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("new doctrine line dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.NewDoctrineLineScreen";
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
        AddCaption("New doctrine line");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        var fieldSize = new Vector2(0.10f, 0.04f);
        const float rowStep = 0.055f;

        _hulls = new HullPicker(Controls, new Vector2(left, topLeft.Y + 0.09f), WindowSize.X - 0.06f, 8,
                                "NewDoctrine");
        _hulls.SelectionChanged += RefreshHint;

        float row = _hulls.Bottom;
        AddLabel(new Vector2(left, row + 0.008f), "Fleet target", 0.75f);
        _target = AddTextbox(new Vector2(left + 0.15f, row), fieldSize, "NewDoctrineTarget", "1",
            "How many of this hull the faction keeps. A ceiling as well as a floor: losses are "
            + "replaced up to it, and nothing builds past it.");
        AddLabel(new Vector2(left + 0.30f, row + 0.008f), "Build at once", 0.75f);
        _concurrent = AddTextbox(new Vector2(left + 0.46f, row), fieldSize, "NewDoctrineConcurrent", "1",
            "How many of this line may be under construction at the same time, across all yards.");

        row += rowStep;
        AddLabel(new Vector2(left, row + 0.008f), "Where", 0.75f);
        _scope = AddCombo(new Vector2(left + 0.15f, row), new Vector2(0.34f, 0.04f), "NewDoctrineScope");

        row += rowStep;
        _hint = AddLabel(new Vector2(left, row), "", 0.65f);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = -topLeft.Y - 0.10f;
        _create = new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Create the line. The server validates every field.",
            text: new StringBuilder("Create"),
            textScale: 0.7f,
            onButtonClick: OnCreateClicked);
        Controls.Add(_create);

        var cancel = new MyGuiControlButton(
            position: new Vector2(left + 0.18f, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: null,
            text: new StringBuilder("Cancel"),
            textScale: 0.7f,
            onButtonClick: _ => CloseScreen());
        Controls.Add(cancel);

        _status = AddLabel(new Vector2(left, -topLeft.Y - 0.05f), "", 0.7f);

        FillScopes();
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component != null) _hulls.SetHulls(component.Prefabs, component.Roles);
        RefreshHint();
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    private MyGuiControlCombobox AddCombo(Vector2 position, Vector2 size, string name)
    {
        var combo = new MyGuiControlCombobox
        {
            Position = position,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = size,
            Name = name,
        };
        Controls.Add(combo);
        return combo;
    }

    private MyGuiControlTextbox AddTextbox(Vector2 position, Vector2 size, string name, string defaultText,
                                           string tooltip)
    {
        var box = new MyGuiControlTextbox(defaultText: defaultText)
        {
            Position = position,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = size,
            Name = name,
        };
        box.SetToolTip(tooltip);
        Controls.Add(box);
        return box;
    }

    // ---- Pickers ------------------------------------------------------------------------------

    /// <summary>"Any yard" first and selected by default: an unscoped line is the ordinary case, and
    /// scoping one is the exception a player takes deliberately.</summary>
    private void FillScopes()
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        _scope.ClearItems();
        if (component == null) return;

        _scope.AddItem(AnyYardKey, "Any yard", null, null, false);
        for (int i = 0; i < component.ScopeNodes.Count; i++)
        {
            WireOrderOption option = component.ScopeNodes[i];
            _scope.AddItem(i, string.IsNullOrEmpty(option.Label)
                ? option.Id
                : option.Label + "  (" + option.Id + ")", null, null, false);
        }
        _scope.SelectItemByIndex(0);
    }

    /// <summary>The one thing the table cannot say on its own row: what a "no" in Flies means for
    /// a doctrine line.</summary>
    private void RefreshHint()
    {
        WireBuildPrefab prefab;
        if (!_hulls.TryGetSelected(out prefab))
        {
            _hint.Text = _hulls.HullCount == 0
                ? "No hull the faction can build fits a doctrine role."
                : "Pick a hull. Click a column header to sort.";
            return;
        }

        // "From a yard", not "from here": this dialog is not opened on a yard, and the server
        // answered for every yard the faction owns. A line whose hull can leave none of them would
        // place nothing anywhere and say so as "blocked" in the list behind this dialog.
        _hint.Text = prefab.CanFlyFromHere
            ? "Fleet target is a ceiling as well as a floor: nothing builds past it, however much work waits."
            : "No yard you own can fly this hull out. The line would never place a build.";
    }

    /// <summary>Null for "any owned shipyard" — the server reads an empty scope as unscoped.</summary>
    private string SelectedScopeNodeId()
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component == null || _scope == null || _scope.GetItemsCount() == 0) return null;
        int key = (int)_scope.GetSelectedKey();
        if (key < 0 || key >= component.ScopeNodes.Count) return null;
        return component.ScopeNodes[key].Id;
    }

    // ---- Create -------------------------------------------------------------------------------

    private void OnCreateClicked(MyGuiControlButton button)
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component == null) return;

        WireBuildPrefab prefab;
        if (!_hulls.TryGetSelected(out prefab))
        {
            _status.Text = "Pick a hull first.";
            return;
        }

        double target, concurrent;
        if (!TryParseNumber(_target.Text, out target) || target <= 0)
        {
            _status.Text = "Fleet target must be a positive whole number.";
            return;
        }
        if (!TryParseNumber(_concurrent.Text, out concurrent) || concurrent <= 0)
        {
            _status.Text = "Build at once must be a positive whole number.";
            return;
        }

        component.SendCreate(prefab.PrefabId, (int)target, (int)concurrent, SelectedScopeNodeId());

        // The Fleet window behind this one shows the result, including a refusal, in its status line.
        CloseScreen();
    }

    /// <summary>Invariant-culture only: a comma decimal separator on a localised client would
    /// otherwise parse as something the player did not type.</summary>
    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
