using System;
using System.Globalization;
using System.Text;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// The create dialog behind the station Mine window's "New order..." button.
///
/// <para>There is no hub picker: the hub is the station this was opened from. That is the whole
/// difference from the retired faction-wide dialog, and it is the point of moving the surface —
/// a mine order works the deposits within reach of one station, so the station the player is
/// looking at is the answer rather than a question.</para>
///
/// <para>Everything is re-validated server-side through the same <c>FactionRuntime</c> entry point
/// the chat verb reaches: the hub's ownership, that something mineable is within reach of it, the
/// ore names, and the list cap.</para>
/// </summary>
internal sealed class NewMineOrderScreen : MyGuiScreenBase
{
    /// <summary>Deliberately <c>StationBuildScreen</c>'s: these interior offsets are the ones
    /// confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.62f, 0.62f);

    internal static NewMineOrderScreen Instance { get; private set; }

    private readonly string _hubNodeId;
    private readonly string _hubLabel;

    private MyGuiControlTextbox _period;
    private MyGuiControlTextbox _ores;
    private MyGuiControlLabel _status;

    private NewMineOrderScreen(string hubNodeId, string hubLabel)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _hubNodeId = hubNodeId;
        _hubLabel = hubLabel;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The list behind this dialog keeps drawing, so the player can see what they already have.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string hubNodeId, string hubLabel)
    {
        if (string.IsNullOrEmpty(hubNodeId)) return;
        try
        {
            NewMineOrderScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._hubNodeId, hubNodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new NewMineOrderScreen(hubNodeId, hubLabel));
        }
        catch (Exception ex)
        {
            Plugin.Log("new mine order dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.NewMineOrderScreen";
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
        AddCaption("New mine order");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float fieldLeft = left + 0.19f;
        var fieldSize = new Vector2(0.34f, 0.04f);
        float row = topLeft.Y + 0.10f;
        const float rowStep = 0.055f;

        AddLabel(new Vector2(left, row + 0.008f), "Hub", 0.75f);
        // A label, not a picker: this dialog is opened ON the hub.
        AddLabel(new Vector2(fieldLeft, row + 0.008f), _hubLabel ?? _hubNodeId, 0.75f);

        row += rowStep;
        AddLabel(new Vector2(left, row + 0.008f), "Period (s)", 0.75f);
        _period = AddTextbox(new Vector2(fieldLeft, row), fieldSize, "NewMinePeriod", "120");

        row += rowStep;
        AddLabel(new Vector2(left, row + 0.008f), "Ores", 0.75f);
        _ores = AddTextbox(new Vector2(fieldLeft, row), fieldSize, "NewMineOres", "");

        row += rowStep;
        AddLabel(new Vector2(left, row),
            "Ores blank = each deposit's own natural mix. Otherwise a weighted list, e.g. Iron:50, Nickel:50.",
            0.65f);

        row += rowStep;
        AddLabel(new Vector2(left, row),
            "The period is a CEILING on the fallback poll rate, not a floor: a deposit coming off "
            + "cooldown is scanned sooner.", 0.65f);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = -topLeft.Y - 0.10f;
        var create = new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Create the order. The server validates every field.",
            text: new StringBuilder("Create"),
            textScale: 0.7f,
            onButtonClick: OnCreateClicked);
        Controls.Add(create);

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
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    private MyGuiControlTextbox AddTextbox(Vector2 position, Vector2 size, string name, string defaultText)
    {
        var box = new MyGuiControlTextbox(defaultText: defaultText)
        {
            Position = position,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = size,
            Name = name,
        };
        Controls.Add(box);
        return box;
    }

    // ---- Create -------------------------------------------------------------------------------

    private void OnCreateClicked(MyGuiControlButton button)
    {
        StationMineComponent component = StationMineComponent.Instance;
        if (component == null) return;

        double period;
        if (!TryParseNumber(_period.Text, out period) || period <= 0)
        {
            _status.Text = "Period must be a positive number of seconds.";
            return;
        }

        component.SendCreate(_hubNodeId, period, (_ores.Text ?? "").Trim());

        // The list window behind this one shows the result, including a refusal, in its status line.
        CloseScreen();
    }

    /// <summary>Invariant-culture only: a comma decimal separator on a localised client would
    /// otherwise parse as something the player did not type.</summary>
    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
