using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Campaigns;

/// <summary>
/// The campaigns window: every offensive objective the player's faction holds — the directives
/// they placed on the map and, for a faction with a brain, the ones it nominated — with Cancel for
/// the player's own. Placing one happens on the map, where the target is; this window is for
/// seeing what stands and calling one off.
///
/// Same chrome contract as <see cref="Fleet.FleetDoctrineScreen"/>: opened from the Assets tab,
/// draws over its opener, data from <see cref="CampaignsComponent"/>'s own subscription.
/// </summary>
internal sealed class CampaignsScreen : MyGuiScreenBase
{
    private const int Columns = 5;
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static CampaignsScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _table;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _cancel;
    private int _drawnRevision = -1;
    private string _selectedFocusId;

    private CampaignsScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
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
            MyGuiSandbox.AddScreen(new CampaignsScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("campaigns window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.CampaignsScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        CampaignsComponent component = CampaignsComponent.Instance;
        if (component != null) component.SetWant(CampaignsComponent.WantWindow, true);
    }

    public override void OnRemoved()
    {
        if (Instance != this) { base.OnRemoved(); return; }
        CampaignsComponent component = CampaignsComponent.Instance;
        if (component != null) component.SetWant(CampaignsComponent.WantWindow, false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Campaigns");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount decides the real height (see FleetDoctrineScreen); everything below the
        // table is placed from its measured size.
        _table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.52f, 0f),
            Name = "Campaigns",
            ColumnsCount = Columns,
            VisibleRowsCount = 11,
        };
        _table.SetCustomColumnWidths(new float[Columns] { 0.14f, 0.32f, 0.16f, 0.24f, 0.14f });
        _table.SetColumnName(0, new StringBuilder("Kind"));
        _table.SetColumnName(1, new StringBuilder("Target"));
        // Two clocks, not one: the objective stamps an attempt whether or not anything flew, so
        // "attempted" alone reads a refusal and a landed strike identically.
        _table.SetColumnName(2, new StringBuilder("Attempted"));
        _table.SetColumnName(3, new StringBuilder("Outcome"));
        _table.SetColumnName(4, new StringBuilder("Expires"));
        _table.ItemSelected += OnSelected;
        Controls.Add(_table);

        float tableHeight = _table.Size.Y;
        float detailLeft = left + _table.Size.X + 0.02f;

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(detailLeft, top + 0.04f),
            size: new Vector2(0.31f, tableHeight),
            backgroundColor: null,
            font: "Blue",
            textScale: 0.75f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: null,
            drawScrollbarV: true,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            textPadding: new MyGuiBorderThickness(0.006f, 0.004f))
        {
            VisualStyle = MyGuiControlMultilineStyleEnum.BackgroundBordered,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Name = "CampaignDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = top + 0.04f + tableHeight + 0.015f;
        _cancel = new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Call off this campaign. Only one you placed can be cancelled here.",
            text: new StringBuilder("Cancel campaign"),
            textScale: 0.7f,
            onButtonClick: OnCancelClicked)
        {
            Enabled = false,
        };
        Controls.Add(_cancel);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        CampaignsComponent component = CampaignsComponent.Instance;
        if (component != null)
        {
            component.SetWant(CampaignsComponent.WantWindow, true);
            if (_drawnRevision != component.Revision) Rebuild(component);
            else RefreshDetail(component);
        }
        return base.Draw();
    }

    private void Rebuild(CampaignsComponent component)
    {
        _drawnRevision = component.Revision;
        if (!CampaignsComponent.Available)
        {
            WriteUnavailable("This server does not offer campaigns to the client.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        if (string.IsNullOrEmpty(component.FactionTag))
        {
            WriteUnavailable("You are not in a faction Factions has registered.");
            return;
        }

        int mine = 0;
        for (int i = 0; i < component.Campaigns.Count; i++)
            if (component.Campaigns[i].PlayerIssued) mine++;
        _heading.Text = "[" + component.FactionTag + "]  " + mine + "/" + component.Cap
                        + " directives, " + component.Campaigns.Count + " campaigns";
        RebuildRows(component);
        RefreshButtons();
        RefreshDetail(component);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Campaigns";
        if (_table != null) _table.Clear();
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        _selectedFocusId = null;
        if (_cancel != null) _cancel.Enabled = false;
        if (_status != null) _status.Text = message;
    }

    private void RebuildRows(CampaignsComponent component)
    {
        _table.Clear();
        for (int i = 0; i < component.Campaigns.Count; i++)
        {
            WireCampaign c = component.Campaigns[i];
            var row = new MyGuiControlTable.Row(c);
            row.AddCell(new MyGuiControlTable.Cell(KindColumn(c)));
            row.AddCell(new MyGuiControlTable.Cell(TargetColumn(c), null, TargetColumn(c)));
            row.AddCell(new MyGuiControlTable.Cell(LastStrikeColumn(c)));
            row.AddCell(new MyGuiControlTable.Cell(OutcomeColumn(c), null, OutcomeDetail(c)));
            row.AddCell(new MyGuiControlTable.Cell(ExpiresColumn(c)));
            _table.Add(row);
        }
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedFocusId)) return;
        for (int i = 0; i < _table.RowsCount; i++)
        {
            if (!(_table.GetRow(i).UserData is WireCampaign)) continue;
            var c = (WireCampaign)_table.GetRow(i).UserData;
            if (!string.Equals(c.FocusId, _selectedFocusId, StringComparison.Ordinal)) continue;
            _table.SelectedRowIndex = i;
            return;
        }
        _selectedFocusId = null;
    }

    internal static string KindColumn(WireCampaign c)
    {
        string kind = c.Kind == 1 ? "Conquest" : c.Kind == 2 ? "Defend" : "Raid";
        return c.PlayerIssued ? kind + " *" : kind;
    }

    private static string TargetColumn(WireCampaign c)
    {
        if (!string.IsNullOrEmpty(c.TargetNodeId))
            return string.IsNullOrEmpty(c.TargetNodeName) ? c.TargetNodeId : c.TargetNodeName + "  (" + c.TargetNodeId + ")";
        return string.IsNullOrEmpty(c.RegionKey) ? "-" : c.RegionKey;
    }

    private static string LastStrikeColumn(WireCampaign c)
    {
        if (c.LastDispatchUtcTicks <= 0) return c.CandidateCount == 0 ? "no target" : "none yet";
        return Ago(c.LastDispatchUtcTicks);
    }

    private static string Ago(long utcTicks)
    {
        double seconds = (DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc)).TotalSeconds;
        if (seconds < 0) seconds = 0;
        return seconds < 120 ? ((int)seconds) + "s ago" : ((int)(seconds / 60)) + "m ago";
    }

    /// <summary>The verb for <see cref="WireCampaign.LastOutcome"/>. A defensive focus never
    /// attacks and so never carries one.</summary>
    private static string OutcomeVerb(byte outcome)
    {
        switch (outcome)
        {
            case 1: return "refused";
            case 2: return "intercepted";
            case 3: return "struck";
            default: return null;
        }
    }

    private static string OutcomeColumn(WireCampaign c)
    {
        string verb = OutcomeVerb(c.LastOutcome);
        if (verb == null) return "-";
        return c.LastOutcomeUtcTicks > 0 ? verb + " " + Ago(c.LastOutcomeUtcTicks) : verb;
    }

    /// <summary>The tooltip behind the outcome cell: the arithmetic that decided it, in the terms
    /// a player can act on. The range is the point, not the totals -- a squadron out-ranged at the
    /// opening bracket loses before anything it carries can fire, and building more of the same
    /// hull changes nothing.</summary>
    private static string OutcomeDetail(WireCampaign c)
    {
        string verb = OutcomeVerb(c.LastOutcome);
        if (verb == null) return "Nothing has come back under this campaign yet.";
        var sb = new StringBuilder();
        sb.Append(verb == "struck" ? "The strike landed." : "Outgunned.");
        if (c.LastOutcomeOpposedDps > 0)
            sb.Append("\nAt ").Append(c.LastOutcomeRangeM).Append(" m: ")
              .Append(c.LastOutcomeOwnDps).Append(" against ").Append(c.LastOutcomeOpposedDps).Append('.');
        if (c.LastOutcomeHullsTotal > 0)
            sb.Append("\nCommitted ").Append(c.LastOutcomeHullsTotal)
              .Append(c.LastOutcomeHullsTotal == 1 ? " hull, lost " : " hulls, lost ")
              .Append(c.LastOutcomeHullsLost).Append('.');
        return sb.ToString();
    }

    private static string ExpiresColumn(WireCampaign c)
    {
        if (c.ExpiresUtcTicks <= 0) return "-";
        double minutes = (new DateTime(c.ExpiresUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes;
        if (minutes <= 0) return "due";
        return minutes < 90 ? "in " + ((int)minutes) + "m" : "in " + ((int)(minutes / 60)) + "h";
    }

    private WireCampaign? Selected()
    {
        MyGuiControlTable.Row row = _table != null ? _table.SelectedRow : null;
        if (row == null || !(row.UserData is WireCampaign)) return null;
        return (WireCampaign)row.UserData;
    }

    private void OnSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedFocusId = row != null && row.UserData is WireCampaign
            ? ((WireCampaign)row.UserData).FocusId
            : null;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        WireCampaign? selected = Selected();
        if (_cancel != null) _cancel.Enabled = selected.HasValue && selected.Value.PlayerIssued;
    }

    private void OnCancelClicked(MyGuiControlButton button)
    {
        WireCampaign? selected = Selected();
        CampaignsComponent component = CampaignsComponent.Instance;
        if (component == null || !selected.HasValue || !selected.Value.PlayerIssued) return;
        component.SendCancel(selected.Value.FocusId);
    }

    private void RefreshDetail(CampaignsComponent component)
    {
        if (_detail == null || _status == null) return;
        WireCampaign? selected = Selected();
        _detail.Clear();
        if (!selected.HasValue)
        {
            _detail.AppendText(component.Campaigns.Count == 0
                ? "No campaigns.\n\nOpen the map, select a station your faction does not own, and "
                  + "choose Raid or Conquer. Your faction's combat hulls will be sent against it "
                  + "whenever it can afford a strike."
                : "Select a campaign. Rows marked * are yours to cancel.");
        }
        else
        {
            WriteDetail(selected.Value, component);
        }
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds < CampaignsComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }
        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = "Raid " + component.RaidCost + " / Conquer " + component.ConquerCost
                       + " per strike   -   updated " + age + "s ago";
    }

    private void WriteDetail(WireCampaign c, CampaignsComponent component)
    {
        AppendLine(KindColumn(c) + "  " + c.FocusId);
        AppendLine("");
        AppendLine("Target: " + TargetColumn(c));
        AppendLine("Staging from: " + c.StagingNodeId);
        AppendLine("Cause: " + CauseName(c.Cause));
        AppendLine((c.Kind == 2 ? "Stations on the circuit: " : "Targets ranked: ") + c.CandidateCount);
        AppendLine((c.Kind == 2 ? "Last patrol: " : "Last attempt: ") + LastStrikeColumn(c));
        if (OutcomeVerb(c.LastOutcome) != null)
        {
            AppendLine("Outcome: " + OutcomeColumn(c));
            if (c.LastOutcomeOpposedDps > 0)
                AppendLine("  Decided at " + c.LastOutcomeRangeM + " m: " + c.LastOutcomeOwnDps
                           + " against " + c.LastOutcomeOpposedDps
                           + (c.LastOutcome == 1
                                  ? " -- no fleet was sent."
                                  : " -- the reach that beat it."));
            if (c.LastOutcomeHullsTotal > 0)
                AppendLine("  Committed " + c.LastOutcomeHullsTotal + " hulls, lost " + c.LastOutcomeHullsLost + ".");
        }
        AppendLine("Expires: " + ExpiresColumn(c));
        AppendLine("");
        if (c.Kind == 2)
            AppendLine("A defensive focus keeps a patrol circuit over the station at its centre and "
                       + "your stations near it. A patrol that finds a station under threat stays to "
                       + "defend it. Each circuit costs the faction's patrol cost.");
        else if (c.Kind == 1)
            AppendLine("A conquest raids the target while it is still firmly held and sends a Conquer "
                       + "fleet once its hold is low enough. It closes when the ground is taken. Each "
                       + "strike costs " + component.ConquerCost + " (conquer) or " + component.RaidCost + " (raid).");
        else
            AppendLine("A raid campaign keeps striking the target for loot and to wear its hold down. "
                       + "Each strike costs " + component.RaidCost + ".");
        if (!c.PlayerIssued)
            AppendLine("\nNominated by the faction's own brain; not yours to cancel.");
    }

    private static string CauseName(byte cause)
    {
        switch (cause)
        {
            case 0: return "predation";
            case 1: return "grudge";
            case 2: return "retaliation";
            case 3: return "terror";
            case 4: return "your order";
            default: return "unknown";
        }
    }

    private void AppendLine(string text)
    {
        _detail.AppendText(text);
        _detail.AppendLine();
    }

    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
