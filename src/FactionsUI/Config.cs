using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FactionsUI.Settings;
using FactionsUI.Settings.Elements;
using FactionsUI.Settings.Tools;
using VRage.Input;

namespace FactionsUI;

/// <summary>
/// Persisted client settings. Every property carrying a Settings element attribute becomes a
/// control in the in-game config dialog (Plugins list -> FactionsUI -> Config); the screen writes
/// the file back on close. Persisted as XML under the game's Storage folder by
/// <see cref="ConfigStorage"/>, so keep every option XML-serializable.
/// </summary>
public class Config : INotifyPropertyChanged
{
    #region Options

    private bool enabled = true;
    private bool debugLogging = false;
    private bool rockContactPhysics = false;
    private bool trafficVoice = true;
    private float voiceVolume = 1f;

    private Binding mapKey = new Binding(MyKeys.M);

    private Binding marketKey = new Binding();
    private Binding expandKey = new Binding();
    private Binding expandCommitKey = new Binding();
    private bool mapShowLabels = true;
    private bool mapShowRoutes = true;
    private bool mapShowEmptyNodes = false;
    private bool mapShowSeparation = false;
    private bool mapShowBodies = true;

    #endregion

    #region User interface

    public readonly string Title = "Factions UI";

    [Separator("Scanner")]

    [Checkbox(description: "Show Factions scanner contacts on the HUD")]
    public bool Enabled
    {
        get => enabled;
        set => SetField(ref enabled, value);
    }

    [Separator("Map")]

    [Keybind(label: "Open the map", description: "Vanilla binds M to Symmetry Switch, which only acts while building; the map does not open while the block placer is active, so the two can share the key.")]
    public Binding MapKey
    {
        get => mapKey;
        set => SetField(ref mapKey, value);
    }

    [Separator("Market")]

    [Keybind(label: "Open the market", description: "Prices at every station your faction knows, and the trades they imply. Unbound by default \u2014 pick a key that does not clash with your build binds.")]
    public Binding MarketKey
    {
        get => marketKey;
        set => SetField(ref marketKey, value);
    }

    [Checkbox(label: "Station labels", description: "Names beside stations on the map. The nearest few dozen are labelled; beyond that the text would overlap into noise.")]
    public bool MapShowLabels
    {
        get => mapShowLabels;
        set => SetField(ref mapShowLabels, value);
    }

    [Checkbox(label: "Fleet routes", description: "Draw each convoy's remaining itinerary.")]
    public bool MapShowRoutes
    {
        get => mapShowRoutes;
        set => SetField(ref mapShowRoutes, value);
    }

    [Checkbox(label: "Empty nodes", description: "Show graph nodes with nothing built on them. Off by default; they outnumber everything worth looking at.")]
    public bool MapShowEmptyNodes
    {
        get => mapShowEmptyNodes;
        set => SetField(ref mapShowEmptyNodes, value);
    }

    [Checkbox(label: "Planet markers", description: "Ring and name each planet and moon on the map. A body is the only thing on the map drawn by the game itself rather than by us, so without a marker it has no name, no selection and nothing to double-click at system scale, where the largest planet is a few pixels wide.")]
    public bool MapShowBodies
    {
        get => mapShowBodies;
        set => SetField(ref mapShowBodies, value);
    }

    [Checkbox(label: "Exclusion sphere", description: "Draw the volume that decides your next Expand: everything within the separation in force where you are standing. Nothing may be inside it, so any node caught within is exactly what would refuse you, and those are ringed. Green when the site is clear, red when it is taken. Centred on where the node would actually LAND \u2014 over a planet that is the ground below you, not the ship. Off by default.")]
    public bool MapShowSeparation
    {
        get => mapShowSeparation;
        set => SetField(ref mapShowSeparation, value);
    }

    [Separator("Expand")]

    [Keybind(label: "Placement readout", description: "Toggle the readout that says whether a node may be created where you are standing, what it costs, and where it would actually settle. Unbound by default \u2014 the server answers only while it is open.")]
    public Binding ExpandKey
    {
        get => expandKey;
        set => SetField(ref expandKey, value);
    }

    [Keybind(label: "Create node here", description: "Ask to create a node at your position. Only acts while the placement readout is open and says CLEAR, and always asks for confirmation first \u2014 it is the largest single purchase in the game and cannot be undone.")]
    public Binding ExpandCommitKey
    {
        get => expandCommitKey;
        set => SetField(ref expandCommitKey, value);
    }

    [Separator("Radio")]

    /// <summary>The Station ATC channel. Switched from the Radio window on the Comms page
    /// (<see cref="Radio.RadioScreen"/>), not from this settings screen; still saved here.</summary>
    public bool TrafficVoice
    {
        get => trafficVoice;
        set => SetField(ref trafficVoice, value);
    }

    [Slider(0f, 1f, 0.05f, SliderAttribute.SliderType.Float, label: "Voice volume", description: "On top of the game's Sound volume, which the radio also follows.")]
    public float VoiceVolume
    {
        get => voiceVolume;
        set => SetField(ref voiceVolume, value);
    }

    [Separator("Diagnostics")]

    [Checkbox(label: "Log diagnostics", description: "Writes scanner per-kind counts, map message sizes and landing-gear probes (LANDING_PROBE) to SpaceEngineers.log. Off by default; only useful when something is missing.")]
    public bool DebugLogging
    {
        get => debugLogging;
        set => SetField(ref debugLogging, value);
    }

    [Checkbox(label: "Rock collision on contact", description: "Asteroids this client builds get collision only near a moving ship, character or object, instead of everywhere in view. Saves ~640 KB of physics memory per asteroid.")]
    public bool RockContactPhysics
    {
        get => rockContactPhysics;
        set => SetField(ref rockContactPhysics, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
