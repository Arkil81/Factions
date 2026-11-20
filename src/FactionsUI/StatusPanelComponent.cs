using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Wire;
using Sandbox.Graphics;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// The HUD status panel: one fixed place near the right edge where Factions shows what the player is
/// in the middle of — a siege, an occupation — as a heading and a few short lines.
///
/// <para><b>Why it exists.</b> These used to be vanilla notifications re-sent every five seconds.
/// Vanilla draws them centre-screen at 1.2x text scale and the re-send outlived each one, so the line
/// never left the screen. Here the server sends the panel only when what it shows changes; the
/// countdowns arrive as seconds left at send time and tick here, every frame, with no traffic.</para>
///
/// <para><b>It empties itself.</b> The server re-sends a non-empty panel every few seconds; a panel
/// older than <see cref="StaleSeconds"/> is a server that stopped refreshing it — a lost link, a
/// restart — and is dropped rather than left showing a countdown to nothing.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class StatusPanelComponent : MySessionComponentBase
{
    public static StatusPanelComponent Instance { get; private set; }

    private const float HeadingScale = 0.7f;
    private const float LineScale = 0.6f;
    private const float LineGap = 0.002f;
    private const float ItemGap = 0.014f;
    private const double StaleSeconds = 20.0;

    /// <summary>Right edge, a third of the way down: the top centre belongs to the site readout and
    /// the bottom-left to the signature readout and vanilla's own furniture. Operator's choice,
    /// 2026-10-15.
    ///
    /// <para><b>Left-aligned at a fixed edge, not right-aligned.</b> v0.543.0 right-aligned each line
    /// at 0.985 with <c>useFullClientArea</c>, and SE placed every line's right edge differently —
    /// shorter lines further right — so a line moved sideways whenever its countdown's digits changed
    /// width (operator screenshot, 2026-10-15). A left edge does not depend on SE measuring the text.
    /// 0.80 leaves a fifth of the screen, room for the longest line at 0.6 scale.</para></summary>
    private static readonly Vector2 Anchor = new Vector2(0.80f, 0.32f);

    private static readonly Color InfoColour = new Color(210, 210, 210);
    private static readonly Color PendingColour = new Color(235, 190, 90);
    private static readonly Color ReadyColour = new Color(110, 255, 140);
    private static readonly Color DangerColour = new Color(255, 90, 80);
    private static readonly Color DetailColour = new Color(195, 195, 195);

    private List<WireStatusPanelItem> _items = new List<WireStatusPanelItem>();
    private DateTime _receivedUtc;

    public override void LoadData()
    {
        Instance = this;
        _items = new List<WireStatusPanelItem>();
        UplinkComponent.RegisterHandler(WireMessageKind.StatusPanel, OnPanel);
    }

    protected override void UnloadData()
    {
        Instance = null;
        _items = new List<WireStatusPanelItem>();
    }

    private void OnPanel(byte[] data)
    {
        List<WireStatusPanelItem> items;
        if (!FactionsWire.TryReadStatusPanel(data, out items)) return;
        _receivedUtc = DateTime.UtcNow;
        _items = items;
    }

    /// <summary>Called from the HUD's own draw pass — see <see cref="Patches.HudMarkerPatch"/>.</summary>
    public void Draw()
    {
        List<WireStatusPanelItem> items = _items;
        if (items.Count == 0) return;
        UplinkComponent uplink = UplinkComponent.Instance;
        if (uplink == null || !uplink.HasFeature(WireFeatures.StatusPanel)) return;

        double age = (DateTime.UtcNow - _receivedUtc).TotalSeconds;
        if (age > StaleSeconds)
        {
            _items = new List<WireStatusPanelItem>();
            return;
        }

        float y = Anchor.Y;
        for (int i = 0; i < items.Count; i++)
        {
            WireStatusPanelLine[] lines = items[i].Lines;
            if (lines == null) continue;
            Color tone = ToneColour(items[i].Tone);
            for (int j = 0; j < lines.Length; j++)
            {
                bool heading = j == 0;
                float scale = heading ? HeadingScale : LineScale;
                string text = Render(lines[j], age);
                MyGuiManager.DrawString("Blue", text, new Vector2(Anchor.X, y), scale,
                    heading ? tone : DetailColour,
                    MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                    useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
                y += MyGuiManager.MeasureString("Blue", text, scale).Y + LineGap;
            }
            y += ItemGap;
        }
    }

    /// <summary>The line's text with its countdown, counted from when the panel arrived.</summary>
    internal static string Render(WireStatusPanelLine line, double secondsSinceReceipt)
    {
        string text = line.Text ?? "";
        if (line.CountdownSeconds < 0 || text.IndexOf(WireStatusPanelLine.ClockToken, StringComparison.Ordinal) < 0)
            return text;
        int left = (int)Math.Ceiling(Math.Max(0.0, line.CountdownSeconds - secondsSinceReceipt));
        string clock = (left / 60).ToString(CultureInfo.InvariantCulture) + ":"
                     + (left % 60).ToString("00", CultureInfo.InvariantCulture);
        return text.Replace(WireStatusPanelLine.ClockToken, clock);
    }

    private static Color ToneColour(byte tone)
    {
        switch (tone)
        {
            case 1: return PendingColour;
            case 2: return ReadyColour;
            case 3: return DangerColour;
            default: return InfoColour;
        }
    }
}
