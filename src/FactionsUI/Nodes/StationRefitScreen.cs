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
/// The refit panel for one founded station: every design it could be rebuilt as, what each costs
/// from where it stands, and the three actions that rebuild it.
///
/// <para>A window of its own, opened from the node window's button column, for the reason
/// <see cref="StationDevelopScreen"/> records: the station window is already two full columns, and a
/// candidate table with a per-component breakdown beside it does not fit under either.</para>
///
/// <para>Unlike the develop menu this does not borrow the node window's subscription — there is none
/// to borrow. <see cref="StationRefitComponent"/> is request/response, so this asks when it opens,
/// again on a throttle while it is up, and again after every command.</para>
/// </summary>
internal sealed class StationRefitScreen : MyGuiScreenBase
{
    private const int DesignColumns = 7;

    /// <summary>Deliberately <see cref="NodeDetailScreen"/>'s, for the reason the develop menu
    /// records: these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationRefitScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _designs;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _refit;
    private MyGuiControlButton _force;
    private MyGuiControlButton _apply;

    private int _drawnRevision = -1;

    /// <summary>Prefab id of the selected row, so a snapshot arriving while the player reads one
    /// does not move the selection out from under the action buttons.</summary>
    private string _selectedPrefabId;

    private StationRefitScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // Left false on purpose: the station window that opened this keeps drawing behind it.
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
            StationRefitScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationRefitScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("refit window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationRefitScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        // Ask once on open rather than waiting out the poll throttle: the window is empty until the
        // first snapshot lands.
        StationRefitComponent component = StationRefitComponent.Instance;
        if (component != null) component.Query(_nodeId, force: true);
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window for the reason NodeDetailScreen records: SE
        // defers both the removal and the add, so a replaced window can be removed after its
        // successor has already loaded.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Refit station");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall this ends up — MyGuiControlTable's
        // RefreshInternals overwrites Size.Y — so everything below is placed off its real size
        // afterwards. Twelve rows covers the vanilla station pool for one site with room for a
        // faction's own captures; past that the table scrolls.
        _designs = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.56f, 0f),
            Name = "StationRefitDesigns",
            ColumnsCount = DesignColumns,
            VisibleRowsCount = 12,
        };
        _designs.SetCustomColumnWidths(
            new float[DesignColumns] { 0.28f, 0.10f, 0.10f, 0.10f, 0.09f, 0.13f, 0.20f });
        _designs.SetColumnName(0, new StringBuilder("Design"));
        _designs.SetColumnName(1, new StringBuilder("Kind"));
        _designs.SetColumnName(2, new StringBuilder("Site"));
        _designs.SetColumnName(3, new StringBuilder("Blocks"));
        _designs.SetColumnName(4, new StringBuilder("DPS"));
        _designs.SetColumnName(5, new StringBuilder("Refit BOM"));
        _designs.SetColumnName(6, new StringBuilder("Status"));
        _designs.ItemSelected += OnDesignSelected;
        Controls.Add(_designs);

        // Measured off the table's post-construction size rather than a matching constant, so the
        // two columns cannot drift apart when VisibleRowsCount changes.
        float tableHeight = _designs.Size.Y;
        float detailLeft = left + _designs.Size.X + 0.02f;

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(detailLeft, top + 0.04f),
            size: new Vector2(0.26f, tableHeight),
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
            Name = "StationRefitDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.19f, 0.04f);
        const float buttonGap = 0.21f;
        float buttonTop = top + 0.04f + tableHeight + 0.015f;
        _refit = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Refit",
            "Queue the rebuild of this station as the selected design. Its components become a bill "
            + "this station has to collect, exactly like a ship build; nothing is spent from the "
            + "treasury.",
            OnRefitClicked);
        _force = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Force",
            "Queue it anyway when something already holds this station's one working slot. A ship "
            + "build keeps everything it has collected; a rebuild already queued here is refunded "
            + "and replaced.",
            OnForceClicked);
        _apply = AddActionButton(new Vector2(left, buttonTop + 0.05f), buttonSize, "Apply now",
            "Stand a finished rebuild up immediately instead of waiting for the station to empty. "
            + "Anything inside the old station ends up outside it.",
            OnApplyClicked);

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
        // The station window behind this one can be retargeted onto another node, and this panel
        // acts on the node it was opened for — closing is the only correct answer, exactly as the
        // builds and develop menus do. A null watch is the other case (the window behind was closed,
        // not moved), and this panel holds its own request/response state, so it stays live.
        NodeDetailComponent detail = NodeDetailComponent.Instance;
        if (detail != null
            && !string.IsNullOrEmpty(detail.WatchedNodeId)
            && !string.Equals(detail.WatchedNodeId, _nodeId, StringComparison.Ordinal))
        {
            CloseScreen();
            return base.Draw();
        }

        StationRefitComponent component = StationRefitComponent.Instance;
        if (component != null)
        {
            // Throttled inside the component, so this is a poll rather than sixty requests a second.
            // Re-asked every frame for the reason the node window re-asserts its watch: a window
            // opened before the uplink was granted starts filling in as soon as it is.
            component.Query(_nodeId, force: false);
            if (_drawnRevision != component.Revision) Rebuild(component);
        }
        return base.Draw();
    }

    private void Rebuild(StationRefitComponent component)
    {
        _drawnRevision = component.Revision;

        if (!StationRefitComponent.Available)
        {
            WriteUnavailable("This server does not offer station refit.");
            return;
        }
        if (!component.HasSnapshot
            || !string.Equals(component.Refit.NodeId, _nodeId, StringComparison.Ordinal))
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }

        WireStationRefit refit = component.Refit;
        if (!refit.MayRefit)
        {
            WriteUnavailable(string.IsNullOrEmpty(refit.Message)
                ? "This station cannot be refitted."
                : refit.Message);
            return;
        }

        _heading.Text = _nodeId + "  -  standing as '"
                        + (string.IsNullOrEmpty(refit.CurrentPrefabId) ? "(unknown)" : refit.CurrentPrefabId)
                        + "'  (" + refit.Site + ")";
        RebuildRows(refit);
        RefreshDetail(refit);
        RefreshButtons(refit);
        RefreshStatus(component, refit);
    }

    /// <summary>Every state where there is nothing to act on. One path, so no half-populated table
    /// can survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_designs != null) _designs.Clear();
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        _selectedPrefabId = null;
        SetButtonsEnabled(false, false);
        if (_status != null) _status.Text = message;
    }

    private void RebuildRows(WireStationRefit refit)
    {
        _designs.Clear();
        WireStationRefitCandidate[] candidates = refit.Candidates ?? new WireStationRefitCandidate[0];
        for (int i = 0; i < candidates.Length; i++)
        {
            WireStationRefitCandidate c = candidates[i];
            var row = new MyGuiControlTable.Row(c);
            row.AddCell(new MyGuiControlTable.Cell(c.Id));
            row.AddCell(new MyGuiControlTable.Cell(c.Kind));
            row.AddCell(new MyGuiControlTable.Cell(c.Site));
            row.AddCell(new MyGuiControlTable.Cell(c.BlockCount.ToString(CultureInfo.InvariantCulture)));
            row.AddCell(new MyGuiControlTable.Cell(c.Dps.ToString("0", CultureInfo.InvariantCulture)));
            row.AddCell(new MyGuiControlTable.Cell(BomText(c)));
            row.AddCell(new MyGuiControlTable.Cell(ShortStatus(refit, c), null, LongStatus(refit, c)));
            _designs.Add(row);
        }
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedPrefabId)) return;
        for (int i = 0; i < _designs.RowsCount; i++)
        {
            if (!(_designs.GetRow(i).UserData is WireStationRefitCandidate)) continue;
            var candidate = (WireStationRefitCandidate)_designs.GetRow(i).UserData;
            if (!string.Equals(candidate.Id, _selectedPrefabId, StringComparison.Ordinal)) continue;
            _designs.SelectedRowIndex = i;
            return;
        }
        _selectedPrefabId = null;
    }

    private void RefreshDetail(WireStationRefit refit)
    {
        _detail.Clear();
        AppendState(refit);

        WireStationRefitCandidate selected;
        if (!TryGetSelected(out selected))
        {
            AppendLine("Pick a design to see what rebuilding this station as it would cost from "
                       + "where it stands.");
            AppendLine("");
            AppendLine("The bill is the difference, not the whole design: a component the station "
                       + "already stands as enough of is not charged again, and surplus in the old "
                       + "design is not refunded.");
            return;
        }

        AppendLine(selected.Id + "  (" + selected.Kind + ", " + selected.Site + ")");
        AppendLine("Blocks " + selected.BlockCount.ToString(CultureInfo.InvariantCulture)
                   + "   DPS " + selected.Dps.ToString("0", CultureInfo.InvariantCulture)
                   + "   value " + selected.Cost.ToString(CultureInfo.InvariantCulture));
        AppendLine("");
        AppendLine(LongStatus(refit, selected));
        AppendLine("");

        if (selected.NetBomTotal < 0)
        {
            AppendLine("No build recipe, so there is no bill to charge for the rebuild.");
            return;
        }
        if (selected.NetBom == null || selected.NetBom.Length == 0)
        {
            AppendLine("Costs nothing: this station already stands as everything this design asks "
                       + "for.");
            return;
        }

        AppendLine("Refit bill (" + selected.NetBomTotal.ToString(CultureInfo.InvariantCulture)
                   + " units):");
        for (int i = 0; i < selected.NetBom.Length; i++)
        {
            WireBuildComponent component = selected.NetBom[i];
            // Have is only ever non-zero on the design this station is already collecting toward,
            // so a plain "need" line is the normal case and the pair is the exception.
            AppendLine("  " + component.Key + "  "
                       + (component.Have > 0
                           ? component.Have.ToString(CultureInfo.InvariantCulture) + " / "
                             + component.Need.ToString(CultureInfo.InvariantCulture)
                           : "x" + component.Need.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The three states the station itself can be in, above whatever design is selected: a
    /// rebuild collecting, one finished and waiting, and a siege that refuses every refit.</summary>
    private void AppendState(WireStationRefit refit)
    {
        bool said = false;
        if (!string.IsNullOrEmpty(refit.CollectingPrefabId))
        {
            AppendLine("Collecting toward '" + refit.CollectingPrefabId + "': "
                       + refit.CollectedUnits.ToString(CultureInfo.InvariantCulture) + " of "
                       + refit.CollectingBomUnits.ToString(CultureInfo.InvariantCulture) + " units.");
            said = true;
        }
        if (!string.IsNullOrEmpty(refit.PendingPrefabId))
        {
            AppendLine("'" + refit.PendingPrefabId + "' is paid for and waiting to be stood up"
                       + (refit.Observed ? " — somebody is at the station, so it is being held."
                                         : "."));
            said = true;
        }
        if (refit.SiegeBlocked)
        {
            AppendLine("Under siege: you do not rebuild the walls while they are being shot at.");
            said = true;
        }
        if (said) AppendLine("");
    }

    private void AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text)) _detail.AppendText(text);
        _detail.AppendLine();
    }

    /// <summary>Buttons turn on with a selection and nothing else, except Apply — which acts on the
    /// station rather than the selected row, so it follows the pending state. A blocked refit stays
    /// clickable on purpose, the rule the develop menu records: the client holds no copy of the gate
    /// to disable it against, and the server's refusal is a sentence where a dead button is
    /// silence.</summary>
    private void RefreshButtons(WireStationRefit refit)
    {
        WireStationRefitCandidate selected;
        bool haveSelection = TryGetSelected(out selected);
        SetButtonsEnabled(haveSelection, !string.IsNullOrEmpty(refit.PendingPrefabId));
    }

    private void SetButtonsEnabled(bool designActions, bool applyAction)
    {
        if (_refit != null) _refit.Enabled = designActions;
        if (_force != null) _force.Enabled = designActions;
        if (_apply != null) _apply.Enabled = applyAction;
    }

    private void RefreshStatus(StationRefitComponent component, WireStationRefit refit)
    {
        // A command's reply outranks the age line for as long as it is fresh: it is the only place a
        // refusal ("'Bastion' is a surface design; N412 is a space node.") is ever shown in full.
        // Only this node's, though — a refusal names the node it was about, and the message outlives
        // the panel it was raised in.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && string.Equals(component.LastCommandNodeId, _nodeId, StringComparison.Ordinal)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < StationRefitComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }
        if (!string.IsNullOrEmpty(refit.Message))
        {
            _status.Text = OneLine(refit.Message);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        int count = refit.Candidates == null ? 0 : refit.Candidates.Length;
        _status.Text = count + " design(s) for this site   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnDesignSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedPrefabId = row != null && row.UserData is WireStationRefitCandidate
            ? ((WireStationRefitCandidate)row.UserData).Id
            : null;
        StationRefitComponent component = StationRefitComponent.Instance;
        if (component != null && component.HasSnapshot)
        {
            RefreshDetail(component.Refit);
            RefreshButtons(component.Refit);
        }
    }

    private void OnRefitClicked(MyGuiControlButton button)
    {
        Send(WireStationRefitCommand.Queue);
    }

    private void OnForceClicked(MyGuiControlButton button)
    {
        Send(WireStationRefitCommand.Force);
    }

    private void OnApplyClicked(MyGuiControlButton button)
    {
        StationRefitComponent component = StationRefitComponent.Instance;
        // Apply names no design: it stands up whatever the server says is pending, which is the one
        // action here that a stale snapshot must not be able to misdirect.
        if (component != null) component.SendCommand(WireStationRefitCommand.Apply, null);
    }

    private void Send(WireStationRefitCommand command)
    {
        StationRefitComponent component = StationRefitComponent.Instance;
        if (component == null || string.IsNullOrEmpty(_selectedPrefabId)) return;
        component.SendCommand(command, _selectedPrefabId);
    }

    private bool TryGetSelected(out WireStationRefitCandidate candidate)
    {
        candidate = default(WireStationRefitCandidate);
        MyGuiControlTable.Row row = _designs != null ? _designs.SelectedRow : null;
        if (row == null || !(row.UserData is WireStationRefitCandidate)) return false;
        candidate = (WireStationRefitCandidate)row.UserData;
        return true;
    }

    // ---- Formatting ---------------------------------------------------------------------------

    /// <summary>The net bill as one number. -1 is not zero: the first is a design nothing can price,
    /// the second one this station already stands as everything of.</summary>
    private static string BomText(WireStationRefitCandidate candidate)
    {
        if (candidate.NetBomTotal < 0) return "n/a";
        return candidate.NetBomTotal.ToString(CultureInfo.InvariantCulture);
    }

    private static string ShortStatus(WireStationRefit refit, WireStationRefitCandidate candidate)
    {
        if (candidate.Current) return "Current";
        if (Matches(refit.PendingPrefabId, candidate.Id)) return "Paid for";
        if (Matches(refit.CollectingPrefabId, candidate.Id))
        {
            return "Building " + Percent(refit.CollectedUnits, refit.CollectingBomUnits) + "%";
        }
        if (candidate.NetBomTotal < 0) return "No recipe";
        if (refit.SiegeBlocked) return "Under siege";
        return "Available";
    }

    private static string LongStatus(WireStationRefit refit, WireStationRefitCandidate candidate)
    {
        if (candidate.Current)
            return "This station already stands as this design.";
        if (Matches(refit.PendingPrefabId, candidate.Id))
            return "Already paid for. It will be standing the next time you arrive, or use Apply now "
                   + "to have it swapped immediately.";
        if (Matches(refit.CollectingPrefabId, candidate.Id))
            return "This station is collecting toward this design: "
                   + refit.CollectedUnits.ToString(CultureInfo.InvariantCulture) + " of "
                   + refit.CollectingBomUnits.ToString(CultureInfo.InvariantCulture) + " units.";
        if (candidate.NetBomTotal < 0)
            return "This design has no build recipe, so there is no bill to charge for the rebuild.";
        if (refit.SiegeBlocked)
            return "A siege is running here; every refit is refused until it ends.";
        if (!string.IsNullOrEmpty(refit.CollectingPrefabId))
            return "Another rebuild is collecting at this station. Force replaces it, refunding what "
                   + "it has collected.";
        return "Ready to queue. The bill is the difference from the design standing here now.";
    }

    private static bool Matches(string a, string b)
    {
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int Percent(long have, long need)
    {
        if (need <= 0) return 100;
        long pct = have * 100L / need;
        return pct < 0 ? 0 : (pct > 100 ? 100 : (int)pct);
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line is
    /// a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
