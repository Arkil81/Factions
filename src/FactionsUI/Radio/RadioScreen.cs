using System;
using System.Text;
using FactionsUI.Settings;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Radio;

/// <summary>
/// The Radio window, off the <b>Radio...</b> button on SE's Comms page (<see cref="CommsPageRadio"/>):
/// one switch per channel the player may listen to. The switches are the player's, not a ship's or an
/// antenna's — they hold wherever the player is, unlike the per-antenna squelch.
///
/// <para><b>Station ATC</b> is <see cref="Config.TrafficVoice"/>, the setting that used to sit in the
/// plugin's settings screen: on, the station controllers and pilots are heard
/// (<see cref="Atc.AtcComponent"/>) and the server stops the fleet chat whispers; off, the whispers
/// come back. The server is told by <c>AtcComponent</c>'s own preference message, not from here.</para>
/// </summary>
internal sealed class RadioScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.42f, 0.28f);

    /// <summary>One row per channel. A new channel is a new row here and a setting in <see cref="Config"/>.</summary>
    private static readonly Channel[] Channels =
    {
        new Channel(
            "Station ATC",
            "Station controllers call the traffic within radio range: arrivals, departures and hostile "
            + "fleets, with the pilots calling in. Replaces the fleet chat messages; off, they come back.",
            () => Config.Current.TrafficVoice,
            on => Config.Current.TrafficVoice = on),
    };

    internal static RadioScreen Instance { get; private set; }

    private RadioScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The terminal behind keeps drawing.
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
            MyGuiSandbox.AddScreen(new RadioScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("radio window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.RadioScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        if (Instance == this) Instance = null;
        try { ConfigStorage.Save(Config.Current); }
        catch (Exception ex) { Plugin.Log("radio settings not saved: " + ex.Message); }
        base.OnRemoved();
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Radio");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float right = -topLeft.X - 0.03f;
        float row = topLeft.Y + 0.09f;

        Controls.Add(new MyGuiControlMultilineText(
            new Vector2(left, row), new Vector2(right - left, 0.05f), null, "Blue", 0.7f,
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Text = new StringBuilder("The channels you listen to, wherever you are."),
        });
        row += 0.06f;

        foreach (Channel channel in Channels)
        {
            Controls.Add(new MyGuiControlLabel(
                new Vector2(left, row + 0.004f), null, channel.Label, null, 0.8f, "Blue",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
            var box = new MyGuiControlCheckbox(
                position: new Vector2(right, row),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP,
                toolTip: channel.Tooltip,
                isChecked: channel.Get());
            Channel bound = channel;
            box.IsCheckedChanged = b => { if (b != null) bound.Set(b.IsChecked); };
            Controls.Add(box);
            row += 0.045f;
        }
    }

    private sealed class Channel
    {
        public readonly string Label;
        public readonly string Tooltip;
        public readonly Func<bool> Get;
        public readonly Action<bool> Set;

        public Channel(string label, string tooltip, Func<bool> get, Action<bool> set)
        {
            Label = label;
            Tooltip = tooltip;
            Get = get;
            Set = set;
        }
    }
}
