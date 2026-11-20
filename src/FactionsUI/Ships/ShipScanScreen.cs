using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Ships;

/// <summary>
/// The Scan dialog behind the two terminal scan buttons: a name and a scope, and nothing else.
/// The ship variant records the hull selected on the Active Grids tab; the station variant records
/// the station the terminal itself stands on. One dialog, because they collect exactly the same two
/// fields and the server applies the same sanitising to both.
///
/// <para>A <see cref="MyGuiScreenBase"/> window rather than controls injected into the terminal, for
/// the reason FAC-282 records about SE's EmptyKeys screens — a text box and a picker would each need
/// a generated binding path that does not exist, while this framework is the one the rest of this
/// plugin's windows already use. It opens over the terminal (<see cref="MyGuiScreenBase.CanBeHidden"/>),
/// which keeps drawing behind it.</para>
///
/// <para>Neither field is authorisation. The server re-resolves the grid from its entity id, checks
/// the caller's distance and access, sanitises the name and applies the scope itself; this dialog
/// only collects what the chat verb would have taken as arguments.</para>
///
/// <para><b>Publish as.</b> When the server grants <see cref="WireFeatures.DesignVersions"/>, the
/// dialog asks which design the grid is and offers to publish the scan as that design's next
/// version — the name locked to the design's, the scope to the one the version inherits. A scan
/// under an existing design's name is what publishes a version server-side, so this choice only
/// fills the two fields correctly.</para>
/// </summary>
internal sealed class ShipScanScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.62f, 0.52f);

    private const int ScopeFaction = 0;
    private const int ScopeAll = 1;

    private const int ModeNew = 0;
    private const int ModeVersion = 1;

    internal static ShipScanScreen Instance { get; private set; }

    private readonly string _caption;
    private readonly string _hint;
    private readonly string _buttonTooltip;
    private readonly Action<string, string> _submit;
    private readonly string _defaultName;
    private readonly long _terminalEntityId;
    private readonly long _gridEntityId;
    private readonly bool _station;

    private MyGuiControlCombobox _mode;
    private MyGuiControlLabel _modeNote;
    private MyGuiControlTextbox _name;
    private MyGuiControlCombobox _scope;
    private MyGuiControlButton _scan;

    private int _drawnInfoRevision = -1;
    private WireDesignVersionInfo _offer;
    private bool _hasOffer;

    private ShipScanScreen(string caption, string hint, string buttonTooltip, string defaultName,
                           Action<string, string> submit, long terminalEntityId, long gridEntityId, bool station)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _caption = caption;
        _hint = hint;
        _buttonTooltip = buttonTooltip;
        _submit = submit;
        _defaultName = defaultName ?? "";
        _terminalEntityId = terminalEntityId;
        _gridEntityId = gridEntityId;
        _station = station;
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>The ship variant: records the hull selected on the Active Grids tab as a fleet
    /// prefab.</summary>
    internal static void Open(long gridEntityId, string defaultName)
    {
        if (gridEntityId == 0) return;
        TerminalShipAssetsComponent terminal = TerminalShipAssetsComponent.Instance;
        long terminalEntityId = terminal != null ? terminal.TerminalEntityId : 0L;
        Open(terminalEntityId, gridEntityId, false, "Scan this ship",
            "Records this hull as a prefab your stations can build and store. A ship only sends to "
            + "a node once its blocks match a prefab exactly, so scan it first if Send to Node says "
            + "there is no match.",
            "Send this ship's blueprint to the server as a new prefab.",
            defaultName,
            (scope, name) =>
            {
                TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
                if (component != null) component.RequestScan(gridEntityId, scope, name);
            });
    }

    /// <summary>The station variant: records the station this terminal stands on as a refit target.
    /// It names no grid — the server resolves the terminal's own, which is the only station the
    /// caller can be standing in.</summary>
    internal static void OpenForStation(long terminalEntityId, string defaultName)
    {
        if (terminalEntityId == 0) return;
        Open(terminalEntityId, 0L, true, "Scan this station",
            "Records this station as a design your founded nodes can be rebuilt as. It needs a "
            + "Store block, a Contract block and a Services Terminal, every grid static, and it can "
            + "only be used at nodes on the same side of the ground/space split it was scanned on.",
            "Send this station's blueprint to the server as a new design.",
            defaultName,
            (scope, name) =>
            {
                Nodes.StationRefitComponent component = Nodes.StationRefitComponent.Instance;
                if (component != null) component.RequestStationScan(terminalEntityId, scope, name);
            });
    }

    private static void Open(long terminalEntityId, long gridEntityId, bool station, string caption, string hint,
                             string buttonTooltip, string defaultName, Action<string, string> submit)
    {
        try
        {
            if (Instance != null) return;
            DesignVersionComponent versions = DesignVersionComponent.Instance;
            if (versions != null) versions.Query(terminalEntityId, gridEntityId, station);
            MyGuiSandbox.AddScreen(new ShipScanScreen(caption, hint, buttonTooltip, defaultName, submit,
                terminalEntityId, gridEntityId, station));
        }
        catch (Exception ex)
        {
            Plugin.Log("scan dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.ShipScanScreen";
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
        AddCaption(_caption);

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float fieldLeft = left + 0.16f;
        var fieldSize = new Vector2(0.36f, 0.04f);
        float row = topLeft.Y + 0.10f;

        AddLabel(new Vector2(left, row + 0.008f), "Publish as");
        _mode = new MyGuiControlCombobox
        {
            Position = new Vector2(fieldLeft, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = fieldSize,
            Name = "ScanMode",
        };
        _mode.AddItem(ModeNew, "New design", null, null, false);
        _mode.SelectItemByKey(ModeNew, false);
        _mode.ItemSelected += OnModeSelected;
        Controls.Add(_mode);
        _modeNote = new MyGuiControlLabel(
            new Vector2(fieldLeft, row + 0.045f), null,
            DesignVersionComponent.Available ? "Looking up this design..." : "", null, 0.65f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_modeNote);

        row += 0.08f;
        AddLabel(new Vector2(left, row + 0.008f), "Name");
        _name = new MyGuiControlTextbox(defaultText: _defaultName)
        {
            Position = new Vector2(fieldLeft, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = fieldSize,
            Name = "ScanName",
        };
        Controls.Add(_name);

        row += 0.06f;
        AddLabel(new Vector2(left, row + 0.008f), "Usable by");
        _scope = new MyGuiControlCombobox
        {
            Position = new Vector2(fieldLeft, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = fieldSize,
            Name = "ScanScope",
        };
        _scope.AddItem(ScopeFaction, "This faction only", null, null, false);
        _scope.AddItem(ScopeAll, "Every faction", null, null, false);
        _scope.SelectItemByKey(ScopeFaction, false);
        Controls.Add(_scope);

        row += 0.08f;
        var hint = new MyGuiControlMultilineText(
            new Vector2(left, row), new Vector2(WindowSize.X - 0.06f, 0.12f), null, "Blue", 0.75f,
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
        };
        hint.Text = new StringBuilder(_hint);
        hint.Size = new Vector2(WindowSize.X - 0.06f, 0.10f);
        Controls.Add(hint);

        _scan = new MyGuiControlButton(
            position: new Vector2(left, -topLeft.Y - 0.09f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.16f, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: _buttonTooltip,
            text: new StringBuilder("Scan"),
            textScale: 0.7f,
            onButtonClick: OnScanClicked);
        Controls.Add(_scan);
    }

    private void AddLabel(Vector2 position, string text)
    {
        Controls.Add(new MyGuiControlLabel(
            position, null, text, null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
    }

    // ---- Publish as ---------------------------------------------------------------------------

    public override bool Draw()
    {
        DesignVersionComponent versions = DesignVersionComponent.Instance;
        if (versions != null && versions.Revision != _drawnInfoRevision)
        {
            _drawnInfoRevision = versions.Revision;
            if (versions.HasInfo) ApplyInfo(versions.Info);
        }
        return base.Draw();
    }

    /// <summary>Offers the version when the server does, and says why not when it does not. An
    /// answer about a different grid — a late one for a dialog since reopened — is ignored.</summary>
    private void ApplyInfo(WireDesignVersionInfo info)
    {
        long expected = _station ? _terminalEntityId : _gridEntityId;
        if (info.EntityId != expected || _mode == null) return;

        if (info.Offered && !string.IsNullOrEmpty(info.Family))
        {
            _offer = info;
            _hasOffer = true;
            _mode.ClearItems();
            _mode.AddItem(ModeVersion, "New version of " + info.Family + " (-> " + info.NextId + ")", null, null, false);
            _mode.AddItem(ModeNew, "New design", null, null, false);
            _mode.SelectItemByKey(ModeVersion, true);
            _modeNote.Text = "Stands as " + info.CurrentId + ". Usable by: " + info.ScopeLabel + ".";
            return;
        }

        _hasOffer = false;
        _modeNote.Text = !string.IsNullOrEmpty(info.Reason)
            ? info.Reason
            : "Not a published design - scanning makes a new one.";
    }

    private void OnModeSelected()
    {
        bool version = _hasOffer && _mode != null && _mode.GetSelectedKey() == ModeVersion;
        if (_name != null)
        {
            _name.SetText(new StringBuilder(version ? _offer.Family : _defaultName));
            _name.Enabled = !version;
        }
        if (_scope != null)
        {
            if (version) _scope.SelectItemByKey(_offer.ScopeAll ? ScopeAll : ScopeFaction, false);
            _scope.Enabled = !version;
        }
    }

    // ---- Action -------------------------------------------------------------------------------

    private void OnScanClicked(MyGuiControlButton button)
    {
        try
        {
            // The terminal's own usability, which both variants share: the station scan is taken at
            // the same block, and the server refuses either one on its own account anyway.
            TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
            if (component == null || !component.MayUse)
            {
                CloseScreen();
                return;
            }

            string name = _name == null ? "" : _name.Text;
            string scope = _scope != null && _scope.GetSelectedKey() == ScopeAll ? "all" : "own";
            _submit(scope, name);
            CloseScreen();
        }
        catch (Exception ex)
        {
            Plugin.Log("scan request failed: " + ex);
            CloseScreen();
        }
    }
}
