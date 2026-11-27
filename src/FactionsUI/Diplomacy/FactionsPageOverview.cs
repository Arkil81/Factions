using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Diplomacy;

/// <summary>
/// The Overview and Members tabs on SE's Factions page (docs/fog-of-war.md → The Factions page:
/// Overview and Members). Overview, the default, shows the viewer's word-band estimates of the selected
/// faction; Members is the vanilla member table and its buttons, untouched, shown or hidden as a set.
///
/// <para>Measured off the page vanilla has just built, like <see cref="FactionsPageReadout"/>: the tab
/// buttons sit in the member table's header panel, and the rows inside the table's own rectangle.</para>
///
/// <para><b>Fails closed.</b> Every vanilla control the tabs hide must resolve, or nothing is added; an
/// exception while building removes what was added and shows the member controls again. The page is how
/// a player joins or leaves a faction, and the member table is how a leader runs one.</para>
/// </summary>
internal static class FactionsPageOverview
{
    private const string TabOverviewName = "FactionsUI.TabOverview";
    private const string TabMembersName = "FactionsUI.TabMembers";
    private const string HeaderName = "FactionsUI.OverviewHeader";
    private const string RowPrefix = "FactionsUI.OverviewRow";
    private const int RowCount = 4;

    /// <summary>The vanilla controls that are the Members tab.</summary>
    private static readonly string[] MemberControlNames =
    {
        "labelFactionMembers", "tableMembers", "buttonPromote", "buttonKick", "buttonAcceptJoin",
        "buttonDemote", "buttonShareProgress", "buttonAddNpc",
    };

    private static readonly Vector2 TabSize = new Vector2(0.075f, 0.03f);

    private static readonly List<MyGuiControlBase> Members = new List<MyGuiControlBase>();
    private static MyGuiControlButton _tabOverview;
    private static MyGuiControlButton _tabMembers;
    private static MyGuiControlLabel _header;
    private static readonly MyGuiControlLabel[] Rows = new MyGuiControlLabel[RowCount];

    private static bool _overview = true;
    private static string _tag;
    private static int _drawnRevision = -1;

    internal static bool Attached => _header != null;

    /// <summary>Adds the tabs to a freshly built Factions page. Throws when a control it measures
    /// against or hides is missing; the caller removes whatever was added.</summary>
    internal static void Build(MyGuiControlTabPage page)
    {
        Detach();
        MyGuiControlBase panel = page.Controls.GetControlByName("panelFactionMembersNamePanel");
        if (panel == null) throw new InvalidOperationException("the Factions page has no panelFactionMembersNamePanel control");
        var members = new List<MyGuiControlBase>(MemberControlNames.Length);
        foreach (string name in MemberControlNames)
        {
            MyGuiControlBase c = page.Controls.GetControlByName(name);
            if (c == null) throw new InvalidOperationException("the Factions page has no " + name + " control");
            members.Add(c);
        }
        MyGuiControlBase label = members[0];
        MyGuiControlBase table = members[1];

        float right = panel.PositionX + panel.Size.X - 0.004f;
        float midY = panel.PositionY + panel.Size.Y * 0.5f;
        var tabMembers = Tab(TabMembersName, "Members", new Vector2(right, midY),
            "The faction's members, and a leader's member actions.", OnMembersClicked);
        var tabOverview = Tab(TabOverviewName, "Overview", new Vector2(right - TabSize.X - 0.004f, midY),
            "What you know of this faction, from your own scans.", OnOverviewClicked);
        var header = new MyGuiControlLabel(
            position: label.Position, size: label.Size, text: "Overview", textScale: 0.8f, font: "Blue",
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Name = HeaderName,
        };

        float rowWidth = table.Size.X - 0.02f;
        var rows = new MyGuiControlLabel[RowCount];
        for (int i = 0; i < RowCount; i++)
        {
            rows[i] = new MyGuiControlLabel(
                position: new Vector2(table.PositionX + 0.01f, table.PositionY + 0.012f + i * 0.03f),
                size: new Vector2(rowWidth, 0.025f), text: "", textScale: 0.7f, font: "Blue",
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                isAutoEllipsisEnabled: true, maxWidth: rowWidth)
            {
                Name = RowPrefix + i,
            };
        }

        page.Controls.Add(tabOverview);
        page.Controls.Add(tabMembers);
        page.Controls.Add(header);
        foreach (MyGuiControlLabel r in rows) page.Controls.Add(r);

        Members.AddRange(members);
        _tabOverview = tabOverview;
        _tabMembers = tabMembers;
        _header = header;
        for (int i = 0; i < RowCount; i++) Rows[i] = rows[i];
        _overview = true;
        _tag = null;
        _drawnRevision = -1;
        ApplyMode();
    }

    private static MyGuiControlButton Tab(string name, string text, Vector2 position, string tip,
                                          Action<MyGuiControlButton> onClick)
    {
        return new MyGuiControlButton(
            position: position, visualStyle: MyGuiControlButtonStyleEnum.Rectangular, size: TabSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER, toolTip: tip,
            text: new StringBuilder(text), textScale: 0.6f, onButtonClick: onClick)
        {
            Name = name,
        };
    }

    /// <summary>Removes the tabs after a failed build and shows the member controls again.</summary>
    internal static void Remove(MyGuiControlTabPage page)
    {
        ShowMembers();
        if (page != null)
        {
            var names = new List<string> { TabOverviewName, TabMembersName, HeaderName };
            for (int i = 0; i < RowCount; i++) names.Add(RowPrefix + i);
            foreach (string name in names)
            {
                MyGuiControlBase c = page.Controls.GetControlByName(name);
                if (c != null) page.Controls.Remove(c);
            }
        }
        Detach();
    }

    internal static void Detach()
    {
        Members.Clear();
        _tabOverview = null;
        _tabMembers = null;
        _header = null;
        for (int i = 0; i < RowCount; i++) Rows[i] = null;
        _tag = null;
        _drawnRevision = -1;
    }

    private static void ShowMembers()
    {
        foreach (MyGuiControlBase c in Members)
        {
            try { c.Visible = true; }
            catch { }
        }
    }

    /// <summary>The controller's selection, reported after each vanilla refresh.</summary>
    internal static void OnSelection(IMyFaction selected)
    {
        if (!Attached) return;
        _tag = selected != null ? selected.Tag : null;
        if (_overview && _tag != null) FactionIntelComponent.Instance?.Want(_tag);
        Redraw();
    }

    /// <summary>Every frame, from <c>Plugin.Update</c>: drops the controls once the terminal has
    /// closed, asks again on the component's slow beat, and redraws when an answer arrived.</summary>
    internal static void Tick(bool terminalOpen)
    {
        if (!terminalOpen && Attached) Detach();
        if (!Attached) return;
        FactionIntelComponent component = FactionIntelComponent.Instance;
        if (component == null) return;
        if (_overview && _tag != null) component.Want(_tag);
        if (component.Revision != _drawnRevision) Redraw();
    }

    private static void OnOverviewClicked(MyGuiControlButton sender)
    {
        _overview = true;
        ApplyMode();
        if (_tag != null) FactionIntelComponent.Instance?.Want(_tag);
    }

    private static void OnMembersClicked(MyGuiControlButton sender)
    {
        _overview = false;
        ApplyMode();
    }

    private static void ApplyMode()
    {
        if (!Attached) return;
        foreach (MyGuiControlBase c in Members) c.Visible = !_overview;
        _header.Visible = _overview;
        for (int i = 0; i < RowCount; i++) Rows[i].Visible = _overview;
        // The open tab's button is the one that cannot be pressed, which is how it reads as open.
        _tabOverview.Enabled = !_overview;
        _tabMembers.Enabled = _overview;
        Redraw();
    }

    private static void Redraw()
    {
        if (!Attached) return;
        FactionIntelComponent component = FactionIntelComponent.Instance;
        _drawnRevision = component != null ? component.Revision : -1;
        string[] text = new string[RowCount];
        string[] tips = new string[RowCount];
        WireFactionIntel intel;
        if (_tag == null)
        {
            text[0] = "Select a faction.";
        }
        else if (component == null || !component.TryGet(_tag, out intel))
        {
            text[0] = "Known nodes: ...";
        }
        else
        {
            Describe(intel, text, tips);
        }
        for (int i = 0; i < RowCount; i++)
        {
            Rows[i].Text = text[i] ?? "";
            Rows[i].SetToolTip(tips[i]);
        }
    }

    /// <summary>The four rows for one answer. Word bands for a foreign faction; the viewer's own, or
    /// any for an admin, are exact.</summary>
    internal static void Describe(WireFactionIntel f, string[] text, string[] tips)
    {
        if (f.Exact)
        {
            text[0] = "Nodes held: " + f.KnownNodes;
            text[1] = "Wealth: " + WealthWord(f.Wealth) + ", ledger " + Credits(f.Ledger) + " SC";
            text[2] = "Fleet strength: " + StrengthWord(f.Strength) + ", " + Quantities.Count(f.StrengthDps) + " DPS";
            tips[2] = "Every station's defence, parked hulls included, plus the strongest armed fleet in flight.";
            if (f.HasTreasury) text[3] = "Treasury: " + Credits(f.Treasury) + " SC";
            return;
        }
        text[0] = "Known nodes: " + (f.KnownNodes > 0 ? f.KnownNodes + " known" : "none");
        tips[0] = "Stations and claims you have identified as this faction's.";
        text[1] = "Estimated wealth: " + (f.Wealth == (byte)WireWealthBand.Unknown
            ? "Unknown"
            : WealthWord(f.Wealth) + ", from " + f.KnownNodes + (f.KnownNodes == 1 ? " node" : " nodes"));
        tips[1] = "From how developed the places you know are. It counts only what you have identified.";
        text[2] = "Estimated fleet strength: " + (f.Strength == (byte)WireStrengthBand.Unknown
            ? "Unknown"
            : StrengthWord(f.Strength) + (f.StrengthAgeSeconds > 0 ? ", " + NodeLabels.Age(f.StrengthAgeSeconds) : ""));
        tips[2] = "Defences you have scanned up close, plus the strongest of its fleets you have seen up close "
                  + "lately. It counts only what you have seen.";
    }

    private static string WealthWord(byte band)
    {
        return band == (byte)WireWealthBand.Unknown ? "Unknown" : ((WireWealthBand)band).ToString();
    }

    private static string StrengthWord(byte band)
    {
        return band == (byte)WireStrengthBand.Unknown ? "Unknown" : ((WireStrengthBand)band).ToString();
    }

    private static string Credits(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
