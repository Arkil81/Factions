using System;
using System.Globalization;
using System.Text;
using Factions.Wire;
using FactionsUI.Fleet;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Store;

/// <summary>
/// The create dialog behind the Store window's "New line..." button: one hull, and how many of it to
/// keep on sale.
///
/// <para>Two fields where the doctrine's dialog has five. A shelf line names no role — the shop files
/// a hull under its own capability class — and no scope node, because the shelf is faction-wide by
/// construction. What is left is the hull and the count.</para>
///
/// <para><b>The hull list is the Fleet window's.</b> <see cref="FleetDoctrineComponent.Prefabs"/> is
/// filled once per doctrine subscribe and is the largest payload in the protocol; asking the server
/// for a second copy to show the same rows would double it. The consequence is stated rather than
/// hidden: with the list not yet delivered this dialog says so and offers to open the Fleet window,
/// which is what fetches it.</para>
///
/// <para>The stats line is the same shape the Build and doctrine dialogs use, so the three cannot
/// describe one prefab differently. Every number in it is the server's; the client has no prefab
/// catalogue.</para>
/// </summary>
internal sealed class NewShelfLineScreen : MyGuiScreenBase
{
    /// <summary>Deliberately the doctrine dialog's, which took it from <c>StationBuildScreen</c>:
    /// these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.62f, 0.52f);

    internal static NewShelfLineScreen Instance { get; private set; }

    private MyGuiControlCombobox _prefab;
    private MyGuiControlTextbox _target;
    private MyGuiControlLabel _stats;
    private MyGuiControlLabel _hint;
    private MyGuiControlLabel _status;

    private NewShelfLineScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The Store window behind this dialog keeps drawing, so the player can see what they already
        // sell — and so its own subscription keeps being re-asserted behind this one.
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
            MyGuiSandbox.AddScreen(new NewShelfLineScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("new shelf line dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.NewShelfLineScreen";
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
        AddCaption("Sell a hull");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float fieldLeft = left + 0.19f;
        var fieldSize = new Vector2(0.34f, 0.04f);
        float row = topLeft.Y + 0.10f;
        const float rowStep = 0.055f;

        AddLabel(new Vector2(left, row + 0.008f), "Hull", 0.75f);
        _prefab = new MyGuiControlCombobox
        {
            Position = new Vector2(fieldLeft, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = fieldSize,
            Name = "NewShelfPrefab",
        };
        _prefab.ItemSelected += RefreshStats;
        Controls.Add(_prefab);

        row += rowStep;
        _stats = AddLabel(new Vector2(left, row), "", 0.65f);

        row += rowStep;
        AddLabel(new Vector2(left, row + 0.008f), "Keep on sale", 0.75f);
        _target = new MyGuiControlTextbox(defaultText: "1")
        {
            Position = new Vector2(fieldLeft, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = fieldSize,
            Name = "NewShelfTarget",
        };
        Controls.Add(_target);

        row += rowStep;
        _hint = AddLabel(new Vector2(left, row), "", 0.65f);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = -topLeft.Y - 0.10f;
        Controls.Add(new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Add the line. The server validates both fields.",
            text: new StringBuilder("Add"),
            textScale: 0.7f,
            onButtonClick: OnCreateClicked));

        Controls.Add(new MyGuiControlButton(
            position: new Vector2(left + 0.18f, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: null,
            text: new StringBuilder("Cancel"),
            textScale: 0.7f,
            onButtonClick: _ => CloseScreen()));

        _status = AddLabel(new Vector2(left, -topLeft.Y - 0.05f), "", 0.7f);

        FillPrefabs();
        RefreshStats();
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    // ---- Picker -------------------------------------------------------------------------------

    /// <summary>Every hull the server offered the doctrine dialog, unfiltered by role: the shop files
    /// a hull under whatever class it belongs to, so a player is not choosing one.</summary>
    private void FillPrefabs()
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        _prefab.ClearItems();
        if (component == null || !component.HasOptions || component.Prefabs.Count == 0)
        {
            // Named rather than left as an empty combo: an empty picker and a picker whose list has
            // not arrived look identical, and only one of them is fixed by waiting.
            _status.Text = "The hull list has not arrived yet - open the Fleet window once, then "
                           + "come back.";
            return;
        }

        for (int i = 0; i < component.Prefabs.Count; i++)
        {
            WireBuildPrefab p = component.Prefabs[i];
            // The key is the index into the component's flat list, so the selection needs no second
            // lookup by id.
            _prefab.AddItem(i, string.IsNullOrEmpty(p.Label) ? p.PrefabId : p.Label, null, null, false);
        }
        if (_prefab.GetItemsCount() > 0) _prefab.SelectItemByIndex(0);
    }

    private void RefreshStats()
    {
        WireBuildPrefab prefab;
        if (!TrySelectedPrefab(out prefab))
        {
            _stats.Text = "";
            _hint.Text = "";
            return;
        }

        var sb = new StringBuilder();
        sb.Append("cargo ").Append(prefab.CargoLiters.ToString("N0")).Append(" L");
        sb.Append("  ·  ").Append(prefab.Dps.ToString("N0")).Append(" dps");
        sb.Append("  ·  ").Append((prefab.GridSize ?? "").ToLowerInvariant());
        sb.Append("  ·  ").Append(string.IsNullOrEmpty(prefab.Propulsion) ? "none" : prefab.Propulsion);
        if (prefab.DrillYieldScore > 0)
            sb.Append("  ·  drill ").Append(prefab.DrillYieldScore.ToString("N1"));
        sb.Append("  ·  ").Append(prefab.BomUnits.ToString("N0")).Append(" units");
        sb.Append("  ·  flies from a yard: ").Append(prefab.CanFlyFromHere ? "yes" : "no");
        _stats.Text = sb.ToString();

        _hint.Text = prefab.CanFlyFromHere
            ? "Copies on sale are reserved: your fleet can never dispatch one."
            : "No yard you own can fly this hull out. The line would never build anything.";
    }

    private bool TrySelectedPrefab(out WireBuildPrefab prefab)
    {
        prefab = new WireBuildPrefab();
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component == null || _prefab == null || _prefab.GetItemsCount() == 0) return false;
        int key = (int)_prefab.GetSelectedKey();
        if (key < 0 || key >= component.Prefabs.Count) return false;
        prefab = component.Prefabs[key];
        return true;
    }

    // ---- Create -------------------------------------------------------------------------------

    private void OnCreateClicked(MyGuiControlButton button)
    {
        ShelfComponent component = ShelfComponent.Instance;
        if (component == null) return;

        WireBuildPrefab prefab;
        if (!TrySelectedPrefab(out prefab))
        {
            _status.Text = "Pick a hull first.";
            return;
        }

        double target;
        if (!TryParseNumber(_target.Text, out target) || target <= 0)
        {
            _status.Text = "Keep on sale must be a positive whole number.";
            return;
        }

        component.SendCreate(prefab.PrefabId, (int)target);

        // The Store window behind this one shows the result, including a refusal, in its status line.
        CloseScreen();
    }

    /// <summary>Invariant-culture only: a comma decimal separator on a localised client would
    /// otherwise parse as something the player did not type.</summary>
    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
