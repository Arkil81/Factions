using System;
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
/// The Tactics window: what this faction's hulls have learned about fighting each opponent — the
/// tactic it leans to, how sure it is, the situations that evidence came from, and the last few
/// fights it learned from (docs/fleet-tactics.md, "Legibility").
///
/// <para>Read-only. A player override comes later and will write the player-override rung of the
/// server's tactic resolver; this window is where its controls go.</para>
///
/// <para>Opened from the Fleet window's <b>Tactics...</b> button and drawn over it
/// (<see cref="MyGuiScreenBase.CanHideOthers"/> false). Data comes from
/// <see cref="TacticsComponent"/> and its own subscription.</para>
/// </summary>
internal sealed class TacticsScreen : MyGuiScreenBase
{
    private const int OpponentColumns = 5;
    private const int ContextColumns = 4;
    private const int ReviewColumns = 7;

    /// <summary>The Fleet window's: interior offsets confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static TacticsScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _contextHeading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _opponents;
    private MyGuiControlTable _contexts;
    private MyGuiControlTable _reviews;

    private int _drawnRevision = -1;

    /// <summary>The selected opponent's tag, kept across snapshots so the cadence does not move it.</summary>
    private string _selectedTag;

    private TacticsScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The Fleet window behind this one keeps drawing and keeps its own subscription.
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
            MyGuiSandbox.AddScreen(new TacticsScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("tactics window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.TacticsScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        TacticsComponent component = TacticsComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        if (Instance != this) { base.OnRemoved(); return; }
        TacticsComponent component = TacticsComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Tactics");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // Placed from each table's REAL size: MyGuiControlTable overwrites Size.Y from its row count.
        float oppTop = top + 0.04f;
        _opponents = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, oppTop),
            Size = new Vector2(0.86f, 0f),
            Name = "TacticsOpponents",
            ColumnsCount = OpponentColumns,
            VisibleRowsCount = 5,
        };
        _opponents.SetCustomColumnWidths(new float[OpponentColumns] { 0.14f, 0.30f, 0.16f, 0.16f, 0.24f });
        _opponents.SetColumnName(0, new StringBuilder("Opponent"));
        _opponents.SetColumnName(1, new StringBuilder("Leans to"));
        _opponents.SetColumnName(2, new StringBuilder("Confidence"));
        _opponents.SetColumnName(3, new StringBuilder("Fights"));
        _opponents.SetColumnName(4, new StringBuilder("Last fought (UTC)"));
        _opponents.ItemSelected += OnOpponentSelected;
        Controls.Add(_opponents);

        const float sideWidth = 0.42f;
        float rightLeft = left + sideWidth + 0.02f;
        float sideTop = oppTop + _opponents.Size.Y + 0.02f;

        _contextHeading = new MyGuiControlLabel(
            new Vector2(left, sideTop), null, "Situations", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_contextHeading);
        Controls.Add(new MyGuiControlLabel(
            new Vector2(rightLeft, sideTop), null, "Recent fights", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        float sideTableTop = sideTop + 0.028f;
        _contexts = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, sideTableTop),
            Size = new Vector2(sideWidth, 0f),
            Name = "TacticsContexts",
            ColumnsCount = ContextColumns,
            VisibleRowsCount = 7,
        };
        _contexts.SetCustomColumnWidths(new float[ContextColumns] { 0.34f, 0.36f, 0.15f, 0.15f });
        _contexts.SetColumnName(0, new StringBuilder("Range Pace Guns"));
        _contexts.SetColumnName(1, new StringBuilder("Tactic"));
        _contexts.SetColumnName(2, new StringBuilder("Mean"));
        _contexts.SetColumnName(3, new StringBuilder("n"));
        Controls.Add(_contexts);

        _reviews = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(rightLeft, sideTableTop),
            Size = new Vector2(sideWidth, 0f),
            Name = "TacticsReviews",
            ColumnsCount = ReviewColumns,
            VisibleRowsCount = 7,
        };
        _reviews.SetCustomColumnWidths(new float[ReviewColumns] { 0.13f, 0.11f, 0.10f, 0.26f, 0.13f, 0.13f, 0.14f });
        _reviews.SetColumnName(0, new StringBuilder("When"));
        _reviews.SetColumnName(1, new StringBuilder("vs"));
        _reviews.SetColumnName(2, new StringBuilder("Sit."));
        _reviews.SetColumnName(3, new StringBuilder("Tactic"));
        _reviews.SetColumnName(4, new StringBuilder("Pred."));
        _reviews.SetColumnName(5, new StringBuilder("Got"));
        _reviews.SetColumnName(6, new StringBuilder("Reward"));
        Controls.Add(_reviews);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        TacticsComponent component = TacticsComponent.Instance;
        if (component != null)
        {
            component.SetStreaming(true);
            if (_drawnRevision != component.Revision) Rebuild(component);
            else RefreshStatus(component);
        }
        return base.Draw();
    }

    private void Rebuild(TacticsComponent component)
    {
        _drawnRevision = component.Revision;

        if (!TacticsComponent.Available)
        {
            WriteUnavailable("This server does not offer the Tactics window to the client.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        WireTacticsHeader h = component.Header;
        if (string.IsNullOrEmpty(h.FactionTag))
        {
            WriteUnavailable("You are not in a faction Factions has registered.");
            return;
        }

        _heading.Text = "[" + h.FactionTag + "]  " + component.Opponents.Count + " opponent(s)  -  starts from "
                        + Tactic(h.PriorArm, h.PriorBreakOff) + "  -  " + h.Updates + " fight(s) learned from";
        RebuildOpponents(component);
        RebuildContexts(component);
        RebuildReviews(component);
        RefreshStatus(component);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Tactics";
        if (_opponents != null) _opponents.Clear();
        if (_contexts != null) _contexts.Clear();
        if (_reviews != null) _reviews.Clear();
        if (_status != null) _status.Text = message;
    }

    private void RebuildOpponents(TacticsComponent component)
    {
        _opponents.Clear();
        int selectedIndex = -1;
        for (int i = 0; i < component.Opponents.Count; i++)
        {
            WireTacticOpponent o = component.Opponents[i];
            var row = new MyGuiControlTable.Row(o);
            row.AddCell(new MyGuiControlTable.Cell(o.Tag ?? ""));
            string leans = Tactic(o.Preferred.Arm, o.Preferred.BreakOff)
                           + (o.Preferred.Evidence < 0.05f ? "  (default)" : "");
            row.AddCell(new MyGuiControlTable.Cell(leans, null,
                "Best posterior mean against " + o.Tag + " over every situation. \"(default)\": nothing it has "
                + "learned beats its starting tactic yet."));
            row.AddCell(new MyGuiControlTable.Cell(Pct(o.Preferred.Mean), null,
                "Expected share of the damage traded, n=" + Num(o.Preferred.Evidence)));
            row.AddCell(new MyGuiControlTable.Cell(Num(o.Evidence)));
            row.AddCell(new MyGuiControlTable.Cell(When(o.LastUtcTicks)));
            _opponents.Add(row);
            if (string.Equals(o.Tag, _selectedTag, StringComparison.Ordinal)) selectedIndex = i;
        }
        if (selectedIndex < 0 && component.Opponents.Count > 0)
        {
            selectedIndex = 0;
            _selectedTag = component.Opponents[0].Tag;
        }
        if (selectedIndex >= 0) _opponents.SelectedRowIndex = selectedIndex;
        else _selectedTag = null;
    }

    private void RebuildContexts(TacticsComponent component)
    {
        _contexts.Clear();
        WireTacticOpponent? selected = Selected(component);
        _contextHeading.Text = selected.HasValue ? "Situations vs " + selected.Value.Tag : "Situations";
        if (!selected.HasValue || selected.Value.Contexts == null) return;

        foreach (WireTacticContext ctx in selected.Value.Contexts)
        {
            if (ctx.Cells == null) continue;
            for (int k = 0; k < ctx.Cells.Length; k++)
            {
                WireTacticCell c = ctx.Cells[k];
                var row = new MyGuiControlTable.Row(ctx);
                row.AddCell(k == 0
                    ? new MyGuiControlTable.Cell(ContextShort(ctx.Context), null, ContextLong(ctx.Context))
                    : new MyGuiControlTable.Cell(""));
                row.AddCell(new MyGuiControlTable.Cell(Tactic(c.Arm, c.BreakOff)));
                row.AddCell(new MyGuiControlTable.Cell(Pct(c.Mean)));
                row.AddCell(new MyGuiControlTable.Cell(Num(c.Evidence)));
                _contexts.Add(row);
            }
        }
    }

    private void RebuildReviews(TacticsComponent component)
    {
        _reviews.Clear();
        for (int i = 0; i < component.Reviews.Count; i++)
        {
            WireTacticReview r = component.Reviews[i];
            var row = new MyGuiControlTable.Row(r);
            string detail = "vs " + r.Opponent + ", " + ContextLong(r.Context) + ", " + Tactic(r.Arm, r.BreakOff)
                            + ": predicted " + (float.IsNaN(r.Predicted) ? "none" : Pct(r.Predicted))
                            + ", got " + Pct(r.Actual) + " of the damage, " + r.HullsLost + "/" + r.HullsBrought
                            + " hulls lost" + (r.Mistake ? " - a mistake, learned from twice over" : "");
            row.AddCell(new MyGuiControlTable.Cell(When(r.UtcTicks), null, detail));
            row.AddCell(new MyGuiControlTable.Cell(r.Opponent ?? ""));
            row.AddCell(new MyGuiControlTable.Cell(ContextShort(r.Context)));
            row.AddCell(new MyGuiControlTable.Cell(Tactic(r.Arm, r.BreakOff)));
            row.AddCell(new MyGuiControlTable.Cell(float.IsNaN(r.Predicted) ? "-" : Pct(r.Predicted)));
            row.AddCell(new MyGuiControlTable.Cell(Pct(r.Actual) + (r.Mistake ? " !" : "")));
            row.AddCell(new MyGuiControlTable.Cell(Pct(r.Reward)));
            _reviews.Add(row);
        }
    }

    private void RefreshStatus(TacticsComponent component)
    {
        if (_status == null || !component.HasSnapshot || string.IsNullOrEmpty(component.Header.FactionTag)) return;
        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        string learning = component.Header.Learning
            ? "Your hulls learn from every fight they are in"
            : "Learning is off on this server: only staged test fights teach your hulls";
        _status.Text = learning + ". Read-only for now   -   updated " + age + "s ago";
    }

    // ---- Selection ----------------------------------------------------------------------------

    private WireTacticOpponent? Selected(TacticsComponent component)
    {
        if (string.IsNullOrEmpty(_selectedTag)) return null;
        for (int i = 0; i < component.Opponents.Count; i++)
            if (string.Equals(component.Opponents[i].Tag, _selectedTag, StringComparison.Ordinal))
                return component.Opponents[i];
        return null;
    }

    private void OnOpponentSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedTag = row != null && row.UserData is WireTacticOpponent
            ? ((WireTacticOpponent)row.UserData).Tag
            : null;
        TacticsComponent component = TacticsComponent.Instance;
        if (component != null) RebuildContexts(component);
    }

    // ---- Formatting ---------------------------------------------------------------------------

    private static string Tactic(byte arm, byte breakOff)
    {
        return WireTactics.Arm(arm) + ", leave " + WireTactics.BreakOff(breakOff).ToLowerInvariant();
    }

    private static string Pct(float share)
    {
        return (share * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Num(float n)
    {
        return n.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string When(long utcTicks)
    {
        if (utcTicks <= 0) return "-";
        return new DateTime(utcTicks, DateTimeKind.Utc).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The server's context key — range, pace, guns, each <c>+ = -</c> from this faction's
    /// side — spaced out under the column heading.</summary>
    private static string ContextShort(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 3) return key ?? "";
        return key[0] + "     " + key[1] + "     " + key[2];
    }

    private static string ContextLong(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 3) return key ?? "";
        return Axis(key[0], "outranging", "even range", "outranged") + ", "
               + Axis(key[1], "faster", "even pace", "slower") + ", "
               + Axis(key[2], "outgunning", "even guns", "outgunned");
    }

    private static string Axis(char c, string ahead, string even, string behind)
    {
        return c == '+' ? ahead : c == '-' ? behind : even;
    }
}
