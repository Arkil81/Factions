using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Data.Audio;
using VRage.Game;
using VRage.Game.Components;
using VRageMath;

namespace FactionsUI.Atc;

/// <summary>
/// The traffic-control voice (FAC-419, docs/atc-voice.md): plays the server's
/// <see cref="WireMessageKind.TrafficCallout"/>s (a station controller) and
/// <see cref="WireMessageKind.TrafficPilotCall"/>s (the pilots) on the radio.
///
/// <para><b>Layering.</b> Each station is its own channel (<see cref="AtcChannel"/>): within it the
/// pilot calls and the controller answers in turn. Pilot calls from different stations layer, at most
/// <see cref="MaxChannels"/> channels at once; only one controller speaks at a time, and a routine
/// controller line that comes due while another speaks is dropped. When more want the air, a hostile
/// warning goes first, then the player's own faction's stations, then the longest waiting.</para>
///
/// <para><b>Aiming by ear.</b> Every line is heard through the player's radio as it points while the
/// line plays (<see cref="AtcRadio"/>): the static, the squelch and a pilot's signal follow the
/// reflectors' gain toward the source each frame, and each antenna's own squelch (<see cref="AtcSquelch"/>)
/// decides whether it hears a call at all.</para>
///
/// <para><b>Playback</b> is SE's raw voice, the path voice chat uses (verified live on 2026-09-29, see
/// scripts/atc-audio/README.md): <see cref="IMyAudio.GetSound(IMy3DSoundEmitter, MySoundDimensions)"/>
/// gives a 24 kHz mono voice. A controller line goes in as one buffer with a static bed on a second
/// voice; a pilot call is streamed in chunks.</para>
///
/// <para><b>Volume</b> follows SE's own Sound volume slider (<see cref="IMyAudio.VolumeHud"/>, which
/// also goes to 0 on alt-tab when the game mutes out of focus) times the plugin's Voice volume, and is
/// silent while SE is muted. A raw voice has no output routing, so none of that applies on its own.</para>
///
/// <para>Voices are acquired per line and destroyed after it: SE rebuilds its audio engine on every
/// session load, which invalidates every voice.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class AtcComponent : MySessionComponentBase
{
    /// <summary>Stations heard at once. Each is up to two SE voices. A station left waiting past its
    /// staleness loses the call (<see cref="AtcChannel.PilotStaleSeconds"/>).</summary>
    public const int MaxChannels = 2;
    private const int ChannelCap = 64;

    private static readonly Dictionary<string, AtcChannel> Channels = new Dictionary<string, AtcChannel>(StringComparer.Ordinal);
    private static readonly Random Rng = new Random();
    private static DateTime _lastTick;
    private static bool _active;

    // Stations whose controller is on the air now, and whether the line is a hostile warning. The
    // scanner rings their HUD icon (docs/atc-voice.md, "Who is speaking").
    private static readonly Dictionary<string, bool> Speaking = new Dictionary<string, bool>(StringComparer.Ordinal);

    internal static void SetSpeaking(string nodeId, bool hostile)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        lock (Speaking) Speaking[nodeId] = hostile;
    }

    internal static void ClearSpeaking(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        lock (Speaking) Speaking.Remove(nodeId);
    }

    /// <summary>Whether this station's controller is speaking now.</summary>
    internal static bool TrySpeaking(string nodeId, out bool hostile)
    {
        hostile = false;
        if (string.IsNullOrEmpty(nodeId)) return false;
        lock (Speaking) return Speaking.TryGetValue(nodeId, out hostile);
    }

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.TrafficCallout, OnCallout);
        UplinkComponent.RegisterHandler(WireMessageKind.TrafficPilotCall, OnPilotCall);
        _preferenceSent = null;
        _active = true;
        if (!AtcLibrary.Ready) Plugin.Log("ATC voice library unavailable: " + AtcLibrary.Problem);
    }

    public override void BeforeStart()
    {
        AtcSquelch.Register();
    }

    protected override void UnloadData()
    {
        _active = false;
        AtcSquelch.Unregister();
        foreach (AtcChannel c in Channels.Values) c.Stop();
        Channels.Clear();
        lock (Speaking) Speaking.Clear();
        AtcRadio.Reset();
        _preferenceSent = null;
    }

    private static AtcChannel ChannelFor(string nodeId)
    {
        string key = nodeId ?? "";
        if (!Channels.TryGetValue(key, out AtcChannel ch))
        {
            if (Channels.Count >= ChannelCap) return null;
            Channels[key] = ch = new AtcChannel(key);
        }
        return ch;
    }

    private static void OnCallout(byte[] data)
    {
        if (!FactionsWire.TryReadTrafficCallout(data, out WireTrafficCallout c))
        {
            Plugin.Log("TRAFFIC_CALLOUT unreadable");
            return;
        }
        if (!Config.Current.TrafficVoice || !AtcLibrary.Ready) return;
        List<AtcStep> steps = AtcLines.Callout(c, Rng);
        if (steps == null) return;
        ChannelFor(c.NodeId)?.Add(new AtcControllerLine(c, steps, Rng), DateTime.UtcNow);
        if (Config.Current.DebugLogging)
            Plugin.Log("ATC queued " + c.Event + " " + c.Voice + " " + c.ActorTag + " @" + c.NodeId + " reach="
                       + c.Aim.OmniReachKm.ToString("0") + "km share=" + c.DistanceShare);
    }

    private static void OnPilotCall(byte[] data)
    {
        if (!FactionsWire.TryReadTrafficPilotCall(data, out WireTrafficPilotCall c))
        {
            Plugin.Log("TRAFFIC_PILOT_CALL unreadable");
            return;
        }
        if (!Config.Current.TrafficVoice || !AtcLibrary.Ready) return;
        ChannelFor(c.NodeId)?.Add(new AtcPilotLine(c, Rng.Next()), DateTime.UtcNow);
    }

    // ---- per frame, from Plugin.Update ------------------------------------------------------------

    internal static void Tick()
    {
        if (!_active) return;
        DateTime now = DateTime.UtcNow;
        float dt = _lastTick == default ? 0f : (float)Math.Min(0.25, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        IMyAudio audio = MyAudio.Static;
        float master = audio == null || audio.Mute ? 0f : audio.VolumeHud * Config.Current.VoiceVolume;
        SendPreference();
        AtcRadio.Refresh(now);
        AtcSquelch.SaveIfDirty();

        if (!Config.Current.TrafficVoice)
        {
            if (Channels.Count > 0) { foreach (AtcChannel c in Channels.Values) c.Stop(); Channels.Clear(); }
            return;
        }

        int playing = 0;
        bool controller = false;
        foreach (AtcChannel c in Channels.Values)
        {
            c.TickPlaying(now, master, dt);
            if (c.Playing) playing++;
            if (c.PlayingController) controller = true;
        }
        if (playing < MaxChannels && AtcLibrary.Ready && audio != null)
        {
            string mine = MyFactionTag();
            var waiting = new List<AtcChannel>();
            foreach (AtcChannel c in Channels.Values) if (!c.Playing && !c.Empty) waiting.Add(c);
            waiting.Sort((a, b) =>
            {
                int h = b.HasHostile.CompareTo(a.HasHostile);
                if (h != 0) return h;
                int m = (b.OwnerTag == mine).CompareTo(a.OwnerTag == mine);
                return m != 0 ? m : a.Oldest.CompareTo(b.Oldest);
            });
            foreach (AtcChannel c in waiting)
            {
                if (playing >= MaxChannels) break;
                if (!c.TryStart(now, master, Rng, controller)) continue;
                playing++;
                if (c.PlayingController) controller = true;
            }
        }

        if (Channels.Count > 0)
        {
            List<string> idle = null;
            foreach (KeyValuePair<string, AtcChannel> kv in Channels)
                if (kv.Value.Empty) (idle ??= new List<string>()).Add(kv.Key);
            if (idle != null) foreach (string k in idle) Channels.Remove(k);
        }
    }

    private static bool? _preferenceSent;

    /// <summary>Tells the server whether this player wants the voice, once per link and again when
    /// the setting changes: off, the server sends the fleet chat whispers again.</summary>
    private static void SendPreference()
    {
        UplinkComponent link = UplinkComponent.Instance;
        if (link == null || !link.HasFeature(WireFeatures.TrafficVoice)) { _preferenceSent = null; return; }
        bool want = Config.Current.TrafficVoice;
        if (_preferenceSent == want) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, FactionsWire.WriteTrafficVoicePreference(want), true);
            _preferenceSent = want;
            if (Config.Current.DebugLogging) Plugin.Log("ATC voice preference sent: " + (want ? "on" : "off"));
        }
        catch (Exception ex) { Plugin.Log("ATC voice preference not sent: " + ex.Message); }
    }

    private static string MyFactionTag()
    {
        try
        {
            long id = MyAPIGateway.Session?.Player?.IdentityId ?? 0;
            return id == 0 ? null : MyAPIGateway.Session.Factions.TryGetPlayerFaction(id)?.Tag;
        }
        catch { return null; }
    }

    /// <summary>The whole line as one PCM buffer: clips and silences back to back.</summary>
    internal static byte[] Assemble(List<AtcStep> steps)
    {
        var parts = new List<byte[]>(steps.Count);
        int total = 0;
        foreach (AtcStep s in steps)
        {
            byte[] b = s.Key != null ? AtcLibrary.Get(s.Key) : new byte[s.SilenceMs * AtcLibrary.Rate / 1000 * 2];
            if (b == null) continue;
            parts.Add(b);
            total += b.Length;
        }
        var pcm = new byte[total];
        int at = 0;
        foreach (byte[] b in parts) { Buffer.BlockCopy(b, 0, pcm, at, b.Length); at += b.Length; }
        return pcm;
    }
}
