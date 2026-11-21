using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Factions.Wire;
using VRage.FileSystem;
using VRage.Utils;

namespace FactionsUI.Atc;

/// <summary>
/// Each antenna's radio squelch, in dB (docs/atc-voice.md, "Squelch per antenna"): a traffic call
/// picked up through an antenna is heard only if its signal-to-noise there clears the antenna's
/// threshold. The operator, 2026-09-29: "another slider to select the dB threshold we listen to, per
/// antenna". Turn the omnidirectional antennas up to hear only strong, near traffic, and leave a dish
/// low to pull faint calls in where it points.
///
/// <para><b>The listener's, not the ship's.</b> Stored on this client, keyed by the antenna's entity
/// id, in <c>Storage\FactionsUI.squelch</c> beside the plugin's settings: another player flying the
/// same ship keeps their own. No server message, so moving the slider is heard at once.</para>
///
/// <para>The slider sits in every antenna's terminal, from 0 to 40 dB. An antenna never set reads
/// its kind's default (<see cref="DefaultFor"/>): an omnidirectional antenna
/// <see cref="OmniDefaultDb"/>, so only near, strong traffic comes through, and a reflector
/// <see cref="ReflectorDefaultDb"/>, so a dish pulls faint calls in where it is pointed.</para>
/// </summary>
internal static class AtcSquelch
{
    /// <summary>The server's <c>AtcSquelchDb</c>: what a suit or a seat with no antenna hears at.</summary>
    public const float NoAntennaDb = 3f;

    /// <summary>An omnidirectional antenna never set: a pilot is heard within about 5.6 km, a
    /// controller within half its reach.</summary>
    public const float OmniDefaultDb = 20f;

    /// <summary>A reflector never set: the server's squelch, so a dish hears as far as it points.</summary>
    public const float ReflectorDefaultDb = NoAntennaDb;
    public const float MaxDb = 40f;

    private static readonly Dictionary<long, float> Thresholds = new();
    private static bool _loaded, _dirty;
    private static DateTime _dirtySince;
    private static IMyTerminalControlSlider _slider;
    private static IMyTerminalControls _controls;

    private static string FilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name + ".squelch");

    public static float DefaultFor(bool reflector) => reflector ? ReflectorDefaultDb : OmniDefaultDb;

    public static float Get(long antennaId, bool reflector)
    {
        Load();
        return Thresholds.TryGetValue(antennaId, out float db) ? db : DefaultFor(reflector);
    }

    public static void Set(long antennaId, bool reflector, float db)
    {
        Load();
        db = (float)Math.Round(Math.Max(0f, Math.Min(MaxDb, db)));
        if (Math.Abs(db - DefaultFor(reflector)) < 0.01f) Thresholds.Remove(antennaId); else Thresholds[antennaId] = db;
        if (!_dirty) _dirtySince = DateTime.UtcNow;
        _dirty = true;
    }

    /// <summary>Adds the slider to every antenna's terminal. Once the session's controls exist.</summary>
    public static void Register()
    {
        if (_slider != null) return;
        try
        {
            IMyTerminalControls controls = MyAPIGateway.TerminalControls;
            if (controls == null) return;
            IMyTerminalControlSlider s = controls.CreateControl<IMyTerminalControlSlider, IMyRadioAntenna>("FactionsRadioSquelch");
            if (s == null) return;
            s.Title = MyStringId.GetOrCompute("Radio squelch");
            s.Tooltip = MyStringId.GetOrCompute("Traffic calls this antenna picks up are heard only above this signal-to-noise. "
                                                + "Higher hears only strong, near calls. Yours only: other players keep their own.");
            s.SetLimits(0f, MaxDb);
            s.Getter = b => Get(b.EntityId, IsReflector(b));
            s.Setter = (b, v) => Set(b.EntityId, IsReflector(b), v);
            s.Writer = (b, sb) => sb.Append(Get(b.EntityId, IsReflector(b)).ToString("0", CultureInfo.InvariantCulture)).Append(" dB");
            s.Visible = b => b is IMyRadioAntenna;
            s.SupportsMultipleBlocks = true;
            _slider = s;
            _controls = controls;
            controls.CustomControlGetter += OnCustomControlGetter;
        }
        catch (Exception ex) { Plugin.Log("radio squelch slider not registered: " + ex.Message); }
    }

    public static void Unregister()
    {
        try { if (_controls != null) _controls.CustomControlGetter -= OnCustomControlGetter; }
        catch { }
        _controls = null;
        _slider = null;
        SaveIfDirty(force: true);
    }

    private static bool IsReflector(IMyTerminalBlock b) => RadioAim.TryKindOf(b?.BlockDefinition.SubtypeName, out _);

    private static void OnCustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
    {
        if (_slider != null && controls != null && block is IMyRadioAntenna) controls.Add(_slider);
    }

    /// <summary>Writes the file a couple of seconds after the last change: a slider drag is many sets.</summary>
    public static void SaveIfDirty(bool force = false)
    {
        if (!_dirty || (!force && (DateTime.UtcNow - _dirtySince).TotalSeconds < 2)) return;
        _dirty = false;
        try
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<long, float> kv in Thresholds)
                sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('=')
                  .Append(kv.Value.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex) { Plugin.Log("radio squelch not saved: " + ex.Message); }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (long.TryParse(line.Substring(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                    && float.TryParse(line.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float db))
                    Thresholds[id] = db;
            }
        }
        catch (Exception ex) { Plugin.Log("radio squelch not loaded: " + ex.Message); }
    }
}
