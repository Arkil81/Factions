using System;
using System.Text;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Radio;

/// <summary>
/// The <b>Radio...</b> button on SE's own Comms page (K menu), which opens <see cref="RadioScreen"/>.
/// Added by <see cref="Patches.CommsPageRadioPatch"/> after vanilla builds the page.
///
/// <para><b>Room is made, not found.</b> Vanilla fills the page's left column to the bottom with the
/// Players and Factions lists, at absolute positions. The Factions list gives up just enough rows for
/// the button, and the button takes the space they leave; the list scrolls, so nothing in it becomes
/// unreachable. Everything is placed from the list's own position and size, so nothing here depends
/// on vanilla's constants.</para>
///
/// <para>Shown whether or not the server offers a channel: the switches are this client's settings,
/// and setting one before linking is harmless.</para>
/// </summary>
internal static class CommsPageRadio
{
    private const string FactionListName = "FactionListbox";
    private const string ButtonName = "FactionsUI.RadioButton";
    private const float ButtonHeight = 0.034f;
    private const float Gap = 0.006f;

    /// <summary>Adds the button. Throws on anything unexpected; the caller restores the page.</summary>
    internal static void Build(MyGuiControlTabPage page)
    {
        if (!(page.Controls.GetControlByName(FactionListName) is MyGuiControlListbox list))
            throw new InvalidOperationException("no " + FactionListName + " on the Comms page");

        float rowHeight = list.ItemSize.Y;
        int give = rowHeight > 0f ? (int)Math.Ceiling((ButtonHeight + Gap) / rowHeight) : 0;
        if (give <= 0 || list.VisibleRowsCount - give < 2)
            throw new InvalidOperationException("Factions list too short to give up " + give + " rows");

        float bottom = list.PositionY + list.Size.Y;
        list.VisibleRowsCount -= give;
        float top = list.PositionY + list.Size.Y + Gap;
        if (top + ButtonHeight > bottom + 0.001f)
            throw new InvalidOperationException("Factions list did not shrink: " + bottom + " -> " + top);

        var button = new MyGuiControlButton(
            position: new Vector2(list.PositionX, top),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(list.Size.X, ButtonHeight),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "The radio channels you listen to.",
            text: new StringBuilder("Radio..."),
            textScale: 0.7f,
            onButtonClick: OnRadioClicked)
        {
            Name = ButtonName,
        };
        page.Controls.Add(button);
    }

    /// <summary>Undoes <see cref="Build"/> as far as it got, leaving the page as vanilla built it.</summary>
    internal static void Remove(MyGuiControlTabPage page, int vanillaRows)
    {
        MyGuiControlBase button = page.Controls.GetControlByName(ButtonName);
        if (button != null) page.Controls.Remove(button);
        if (page.Controls.GetControlByName(FactionListName) is MyGuiControlListbox list && vanillaRows > 0)
            list.VisibleRowsCount = vanillaRows;
    }

    internal static int Rows(MyGuiControlTabPage page)
    {
        return page.Controls.GetControlByName(FactionListName) is MyGuiControlListbox list ? list.VisibleRowsCount : 0;
    }

    private static void OnRadioClicked(MyGuiControlButton sender)
    {
        RadioScreen.Open();
    }
}
