using System;
using System.Reflection;
using FactionsUI.Assets;
using FactionsUI.Events;
using FactionsUI.Map;
using FactionsUI.Settings;
using FactionsUI.Settings.Layouts;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Input;
using VRage.Utils;
using VRage.Plugins;

// Pulsar's Roslyn compile does not generate an AssemblyInfo, so the version is declared here.
// IDE/CLI builds define DEV_BUILD and let the SDK generate it instead — declaring both is a
// duplicate-attribute error.
#if !DEV_BUILD
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
#endif

namespace FactionsUI;

/// <summary>
/// Client-side companion to the Factions Torch plugin. Pulsar instantiates the first type in
/// this assembly implementing <see cref="IPlugin"/> and injects the static members below by
/// reflection (see Pulsar's PluginInstance.DependencyInject).
/// </summary>
public class Plugin : IPlugin, IHandleInputPlugin
{
    public const string Name = "FactionsUI";

    public static Plugin Instance { get; private set; }

    private SettingsGenerator _settings;

    public void Init(object gameInstance)
    {
        Instance = this;
        _settings = new SettingsGenerator();

        // Before anything draws. The map asks IconStore for a path every frame and falls back to
        // stock art when it has none, so a late extraction costs the session its icons silently
        // rather than failing where someone would see it.
        IconStore.Extract();

        // Session components carrying [MySessionComponentDescriptor] are registered by Pulsar
        // from this assembly, so the scanner client attaches there rather than from Init().
        var harmony = new Harmony(Name);
        // Not Harmony.PatchAll: a class that fails costs its own patch group. Pulsar would catch the
        // throw, but only after Harmony had kept every class patched before it and skipped the rest
        // of this method.
        var patchReport = Factions.HarmonyPatcher.PatchAll(harmony, Assembly.GetExecutingAssembly());
        foreach (var failure in patchReport.Failures)
            Log(failure.Describe());
        Log(patchReport.Summary());

        // Applied explicitly rather than by attribute: the target is resolved by name at runtime,
        // and this is the one patch whose success or failure has to be REPORTED -- the server is
        // told whether it took, and refuses to let this client derive its own rock if the two ends
        // disagree about it. See Factions.Shared/README.md and RockComponent.
        Factions.FeatureRockMaterialControl.TryApply(harmony);
        Rock.RockContactPhysics.TryPatch(harmony);
        NetSyncTrace.TryPatch(harmony);
        // LANDING_PROBE under "Log diagnostics": the client half of the server's 'factions debug landing'.
        Factions.LandingProbe.TryPatch(harmony, "client", () => Config.Current.DebugLogging, Log,
                                       new[] { 0.25, 0.5, 1.0, 2.0, 3.0 });
        if (!Factions.LandingProbe.AllBound)
            Log("landing probe NOT fully bound: " + Factions.LandingProbe.BindReport);
        Factions.IntruderAlarmEvent.TryApply(harmony);
        if (!Factions.IntruderAlarmEvent.Active)
            Log("Intruder Alarm event INACTIVE (" + Factions.IntruderAlarmEvent.InactiveReason + ")");
        Log(Factions.FeatureRockMaterialControl.Active
            ? "voxel material control ACTIVE"
            : "voxel material control INACTIVE (" + (Factions.FeatureRockMaterialControl.InactiveReason ?? "unknown")
              + ") -- this client will not derive its own rock; the server keeps sending it.");

        Log("initialised");
    }

    /// <summary>
    /// Pulsar calls this every frame. The Assets tab drives its own server subscription from here
    /// rather than from a patch on the terminal's close, so the only vanilla method this plugin
    /// patches for the K menu is the one that builds the tabs.
    /// </summary>
    public void Update()
    {
        Factions.LandingProbe.Tick();
        bool terminalOpen = MyGuiScreenTerminal.IsOpen;

        if (!_assetsUpdateFailed)
        {
            try
            {
                AssetsPage.Tick(terminalOpen);
            }
            catch (Exception ex)
            {
                // Once, then never again: this runs every frame, and a persistent fault would other-
                // wise fill the log faster than the player could read it.
                _assetsUpdateFailed = true;
                Log("assets tab update failed, disabled for this session: " + ex);
            }
        }

        // Disabled independently of the Assets tab: one tab faulting is no reason to stop driving
        // the other, and each owns its own server subscription.
        if (!_eventsUpdateFailed)
        {
            try
            {
                EventsPage.Tick(terminalOpen);
            }
            catch (Exception ex)
            {
                _eventsUpdateFailed = true;
                Log("events tab update failed, disabled for this session: " + ex);
            }
        }

        // The Factions page's diplomacy readout and the Diplomacy window share one subscription,
        // driven from here for the reason the Assets tab's is: no patch on the terminal's close.
        if (!_diplomacyUpdateFailed)
        {
            try
            {
                Diplomacy.FactionsPageReadout.Tick(terminalOpen);
                Diplomacy.FactionsPageOverview.Tick(terminalOpen);
            }
            catch (Exception ex)
            {
                _diplomacyUpdateFailed = true;
                Log("diplomacy readout update failed, disabled for this session: " + ex);
            }
        }

        // Every frame, paused or not: a line in progress has its volume and fading set here.
        if (!_atcFailed)
        {
            try
            {
                Atc.AtcComponent.Tick();
            }
            catch (Exception ex)
            {
                _atcFailed = true;
                Log("traffic voice update failed, disabled for this session: " + ex);
            }
        }
    }

    private bool _atcFailed;

    /// <summary>Pulsar calls this before Init with the folder FactionsUI.xml declares as its
    /// AssetFolder (Pulsar Legacy/Loader/PluginInstance.LoadAssets). It holds the traffic-control
    /// voice library.</summary>
    public void LoadAssets(string folder)
    {
        Atc.AtcLibrary.Load(folder);
        Log(Atc.AtcLibrary.Ready
            ? "ATC voice library: " + Atc.AtcLibrary.ClipCount + " clips in " + folder
            : "ATC voice library unavailable: " + Atc.AtcLibrary.Problem);
    }

    private bool _assetsUpdateFailed;
    private bool _eventsUpdateFailed;
    private bool _diplomacyUpdateFailed;

    /// <summary>
    /// Pulsar calls this every frame from MySandboxGame, BEFORE the GUI gets input and regardless of
    /// which screen has focus. It therefore cannot consume a keypress -- whatever is focused sees the
    /// same key on the same frame -- so every reason not to act has to be checked here.
    /// </summary>
    public void HandleInput()
    {
        try
        {
            // The market first: it is the narrower gate, and both keys are checked every frame
            // because this handler cannot consume a press.
            if (Config.Current.MarketKey.HasPressed(MyInput.Static)
                && Market.MarketComponent.Available
                && Market.MarketScreen.Instance == null
                && FromGameplay())
            {
                Market.MarketScreen.Open();
                return;
            }

            // The placement readout and its commit, before the map: both are unbound by default,
            // and neither opens a screen the map key would then have to fight for focus with.
            ExpandComponent expand = ExpandComponent.Instance;
            if (expand != null && ExpandComponent.Available && FromGameplay())
            {
                if (Config.Current.ExpandKey.HasPressed(MyInput.Static))
                {
                    expand.SetWant(ExpandComponent.WantReadout, !expand.IsOpen);
                    return;
                }
                if (Config.Current.ExpandCommitKey.HasPressed(MyInput.Static))
                {
                    expand.RequestCommit();
                    return;
                }
            }

            if (!Config.Current.MapKey.HasPressed(MyInput.Static)) return;
            if (!CanOpenMap()) return;
            MyGuiSandbox.AddScreen(new MapScreen());
        }
        catch (Exception ex)
        {
            Log("map keybind handling failed: " + ex);
        }
    }

    /// <summary>The half of <see cref="CanOpenMap"/> that is about the player rather than the map:
    /// in gameplay, not in a menu, and not typing. Shared so a second window cannot drift from it.</summary>
    private static bool FromGameplay()
    {
        if (MySession.Static == null || !MySession.Static.Ready) return false;
        MyGuiScreenBase focused = MyScreenManager.GetScreenWithFocus();
        if (!(focused is MyGuiScreenGamePlay)) return false;
        if (focused.FocusedControl is MyGuiControlTextbox) return false;
        return true;
    }

    private static bool CanOpenMap()
    {
        if (MySession.Static == null || !MySession.Static.Ready) return false;

        // The map closes itself on its own key; it must not also be reopened by the same press.
        if (MapScreen.Instance != null) return false;

        // Only from gameplay. Any other screen up means the player is in a menu, and a focused
        // text field means they are typing an M, not asking for a map.
        MyGuiScreenBase focused = MyScreenManager.GetScreenWithFocus();
        if (!(focused is MyGuiScreenGamePlay)) return false;
        if (focused.FocusedControl is MyGuiControlTextbox) return false;

        // Vanilla binds M to SYMMETRY_SWITCH, which only acts while the block placer is up. Since
        // this handler cannot consume the key, standing aside there is what lets both keep working.
        if (MyCubeBuilder.Static != null && MyCubeBuilder.Static.IsActivated) return false;

        return true;
    }

    public void Dispose()
    {
        // Never UnpatchAll() here — it would tear down other plugins' patches too.
        Instance = null;
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        _settings.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(_settings.Dialog);
    }

    /// <summary>Writes to the SE client log. Deliberately not Pulsar's injected `PulsarLog`
    /// delegate: that is typed against NLog, which would drag an NLog reference into this project
    /// purely so the reflection-based field injection can type-match.</summary>
    public static void Log(string message)
    {
        MyLog.Default.WriteLineAndConsole(Name + ": " + message);
    }
}
