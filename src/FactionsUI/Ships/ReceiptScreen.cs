using System;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Ships;

/// <summary>
/// What a refuel or rearm delivered, item by item: how much went into the guns or tanks, how much
/// into cargo, at what price, and the total. A message box carried one sentence; a purchase of two
/// magazine types split between guns and hold needs a table.
///
/// <para>Every number is the server's — what actually landed, priced as it was charged. The notes
/// under the table are the server's own words, verbatim, for what the table cannot say: a line the
/// station ran out of, a refund for magazines that did not fit.</para>
/// </summary>
internal sealed class ReceiptScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.66f, 0.58f);
    private const int Columns = 5;

    private readonly string _caption;
    private readonly bool _gas;
    private readonly WireReceiptLine[] _lines;
    private readonly long _balanceAfter;
    private readonly string _notes;

    private ReceiptScreen(string caption, bool gas, WireReceiptLine[] lines, long balanceAfter, string notes)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _caption = caption;
        _gas = gas;
        _lines = lines ?? new WireReceiptLine[0];
        _balanceAfter = balanceAfter;
        _notes = notes ?? "";
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string caption, bool gas, WireReceiptLine[] lines, long balanceAfter, string notes)
    {
        try
        {
            MyGuiSandbox.AddScreen(new ReceiptScreen(caption, gas, lines, balanceAfter, notes));
        }
        catch (Exception ex)
        {
            Plugin.Log("receipt open failed: " + ex);
            TerminalShipAssetsComponent.ShowMessage(caption, notes);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.ReceiptScreen";
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption(_caption);

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float width = WindowSize.X - 0.06f;
        float y = topLeft.Y + 0.09f;

        var table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, y),
            Size = new Vector2(width, 0f),
            Name = "DockReceipt",
            ColumnsCount = Columns,
            VisibleRowsCount = Math.Max(3, Math.Min(6, _lines.Length)),
        };
        table.SetCustomColumnWidths(new float[Columns] { 0.34f, 0.15f, 0.12f, 0.17f, 0.22f });
        table.SetColumnName(0, new StringBuilder("Item"));
        table.SetColumnName(1, new StringBuilder(_gas ? "Tanks" : "Guns"));
        table.SetColumnName(2, new StringBuilder("Cargo"));
        table.SetColumnName(3, new StringBuilder(_gas ? "Per 1,000 L" : "Each"));
        table.SetColumnName(4, new StringBuilder("Credits"));

        long total = 0;
        foreach (WireReceiptLine line in _lines)
        {
            var row = new MyGuiControlTable.Row(line);
            row.AddCell(new MyGuiControlTable.Cell(string.IsNullOrEmpty(line.Name) ? line.Key : line.Name));
            row.AddCell(new MyGuiControlTable.Cell(_gas ? Num(line.Loaded) + " L" : Num(line.Loaded)));
            row.AddCell(new MyGuiControlTable.Cell(_gas ? "" : Num(line.Cargo)));
            row.AddCell(new MyGuiControlTable.Cell(Num(line.UnitPrice)));
            row.AddCell(new MyGuiControlTable.Cell(Num(line.Credits)));
            table.Add(row);
            total += line.Credits;
        }
        Controls.Add(table);
        y += table.Size.Y + 0.015f;

        AddLabel(new Vector2(left, y), "Total charged: " + Num(total) + " cr", 0.85f);
        y += 0.035f;
        AddLabel(new Vector2(left, y), "Balance now: " + Num(_balanceAfter) + " cr", 0.75f);
        y += 0.04f;

        if (_notes.Length > 0)
        {
            var notes = new MyGuiControlMultilineText(
                new Vector2(left, y), new Vector2(width, Math.Max(0.05f, -topLeft.Y - 0.10f - y)), null, "Blue", 0.75f,
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
            {
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            };
            notes.Text = new StringBuilder(_notes);
            Controls.Add(notes);
        }

        Controls.Add(new MyGuiControlButton(
            position: new Vector2(left, -topLeft.Y - 0.08f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            text: new StringBuilder("OK"),
            textScale: 0.7f,
            onButtonClick: b => CloseScreen()));
    }

    private void AddLabel(Vector2 position, string text, float scale)
    {
        Controls.Add(new MyGuiControlLabel(position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
    }

    private static string Num(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
