using System;
using System.Collections.Generic;
using Factions.Wire;
using VRage.Audio;
using VRage.Data.Audio;

namespace FactionsUI.Atc;

/// <summary>One line on the radio, played on its own SE voice and heard through the listener's
/// radio as it points while the line plays (docs/atc-voice.md, "Aiming by ear").</summary>
internal abstract class AtcLine
{
    public DateTime QueuedAt;

    /// <summary>Whether starting it now would be heard at all: out of reach, it waits in its channel
    /// until it goes stale, so a listener who turns onto the station still catches it.</summary>
    public abstract bool Audible(DateTime now);

    /// <summary>Per frame once started; false when finished.</summary>
    public abstract bool Tick(DateTime now, float master, float dt);

    public abstract void Stop();

    protected static void Destroy(IMySourceVoice v)
    {
        if (v == null) return;
        try { if (v.IsValid) { v.Stop(force: true); v.Destroy(); } }
        catch (Exception ex) { Plugin.Log("ATC voice teardown failed: " + ex.Message); }
    }
}

/// <summary>
/// A line mixed on the game thread in <see cref="ChunkMs"/> chunks at the listener's reception now,
/// keeping <see cref="LeadMs"/> queued on one SE voice at a fixed volume of 1. Every level is applied
/// per sample in the chunks (<see cref="AtcMixer"/>), so nothing the listener moves steps the audio.
///
/// <para>SE's raw voice ends itself when its queue runs dry (<c>MySourceVoice.OnAllBuffersFinished</c>),
/// so the lead is what keeps a slow frame from cutting a line short.</para>
/// </summary>
internal abstract class AtcStreamedLine : AtcLine
{
    private const int ChunkMs = 50;
    private const int LeadMs = 250;

    private IMySourceVoice _voice;
    private DateTime _playFrom;
    private int _submitted, _total;

    protected bool Started => _voice != null;

    /// <summary>How the listener's radio hears the line right now.</summary>
    public abstract AtcRadio.Reception Reception();

    protected abstract string Kind { get; }

    /// <summary>Samples [from, from+count) as 16-bit PCM, at reception <paramref name="r"/>.</summary>
    protected abstract byte[] MixChunk(int from, int count, float master, AtcRadio.Reception r);

    protected bool Begin(DateTime now, int total, float master)
    {
        if (total <= 0) return false;
        IMySourceVoice v = MyAudio.Static?.GetSound(new AtcEmitter(), MySoundDimensions.D2);
        if (v == null) return false; // no audio device, or audio disabled
        _voice = v;
        _total = total;
        _playFrom = now;
        v.SetVolume(1f);
        Feed(now, master);
        v.StartBuffered();
        return true;
    }

    protected bool Continue(DateTime now, float master)
    {
        double played = (now - _playFrom).TotalSeconds * AtcLibrary.Rate;
        if (played >= _total || _voice == null || !_voice.IsValid) return false;
        if (_submitted < _total && !_voice.IsPlaying)
        {
            if (Config.Current.DebugLogging) Plugin.Log("ATC " + Kind + " underrun at " + (played / AtcLibrary.Rate).ToString("0.00") + " s");
            return false;
        }
        Feed(now, master);
        return true;
    }

    /// <summary>Mixes and queues chunks until <see cref="LeadMs"/> is ahead of what has played.</summary>
    private void Feed(DateTime now, float master)
    {
        int chunk = AtcLibrary.Rate * ChunkMs / 1000;
        double played = (now - _playFrom).TotalSeconds * AtcLibrary.Rate;
        int want = (int)Math.Min(_total, played + AtcLibrary.Rate * LeadMs / 1000.0);
        if (_submitted >= want) return;
        AtcRadio.Reception r = Reception();
        while (_submitted < want)
        {
            int n = Math.Min(chunk, _total - _submitted);
            _voice.SubmitBuffer(MixChunk(_submitted, n, master, r));
            _submitted += n;
        }
    }

    public override void Stop()
    {
        Destroy(_voice);
        _voice = null;
    }
}

/// <summary>
/// A station controller's line: the speech with a static bed under it whose level follows the
/// listener's radio (<see cref="AtcRadio.Controller"/>). The approved distance ladder, read live and
/// per antenna: none under 25% of reach, then 25, 12 and 5 dB under the voice, the last also fading;
/// past the reach, or under the antenna's own squelch (<see cref="AtcSquelch"/>), the line goes quiet
/// until the listener turns back onto the station.
/// </summary>
internal sealed class AtcControllerLine : AtcStreamedLine
{
    private const double StaticTauSeconds = 0.150;

    private readonly WireTrafficCallout _c;
    private readonly List<AtcStep> _steps;
    private readonly Random _rng;
    private float[] _speech, _noise, _fade;
    private AtcLineGains _gains;

    public AtcControllerLine(WireTrafficCallout c, List<AtcStep> steps, Random rng)
    {
        _c = c;
        _steps = steps;
        _rng = rng;
    }

    public WireTrafficCallout Callout => _c;
    /// <summary>A line that must be said: a hostile warning, or a trespass hail addressed to this
    /// player. It goes first in its channel, waits for another controller rather than being dropped,
    /// and is never collapsed into an advisory.</summary>
    public bool Hostile => _c.Event == WireTrafficEvent.HostileInbound || FactionsWire.IsHail(_c.Event);

    protected override string Kind => "controller";

    /// <summary>How the listener's radio hears the station right now (<see cref="AtcRadio.Controller"/>).</summary>
    public override AtcRadio.Reception Reception() => AtcRadio.Controller(_c.Aim, _c.DistanceShare);

    public override bool Audible(DateTime now) => Reception().Open;

    private bool Start(DateTime now, float master)
    {
        byte[] pcm = AtcComponent.Assemble(_steps);
        if (pcm == null || pcm.Length < 2) return false;
        byte[] st = AtcLibrary.Get("fx.static");
        _speech = AtcMixer.ToFloat(pcm);
        _noise = st == null || st.Length < 2 ? null : AtcMixer.ToFloat(Loop(st, pcm.Length));
        _fade = FadePoints();
        _gains = new AtcLineGains(AtcLibrary.Rate, StaticTauSeconds);
        if (!Begin(now, _speech.Length, master)) return false;
        // The line does not say who is speaking; the station's HUD icon shows it while it plays.
        AtcComponent.SetSpeaking(_c.NodeId, Hostile);
        if (Config.Current.DebugLogging)
            Plugin.Log("ATC playing " + _c.Event + " @" + _c.NodeId + " " + (_speech.Length / (double)AtcLibrary.Rate).ToString("0.0")
                       + " s, share=" + Reception().Share.ToString("0.00") + " snr=" + Reception().SnrDb.ToString("0")
                       + "dB squelch=" + Reception().SquelchDb.ToString("0") + "dB gain=" + AtcRadio.GainDb(_c.Aim).ToString("0.0") + "dB");
        return true;
    }

    public override bool Tick(DateTime now, float master, float dt) =>
        Started ? Continue(now, master) : Start(now, master);

    protected override byte[] MixChunk(int from, int count, float master, AtcRadio.Reception r)
    {
        // The static under the voice is the signal-to-noise on the approved ladder; the squelch of the
        // antenna that hears it best opens and closes the line.
        float noiseTarget = r.SnrDb >= AtcRadio.CleanDb ? 0f : (float)Math.Pow(10, -r.SnrDb / 20);
        return AtcMixer.Controller(_speech, _noise, _fade, from, count, noiseTarget, r.Share >= 0.8,
                                   r.SnrDb - r.SquelchDb, master, _gains);
    }

    public override void Stop()
    {
        if (Started) AtcComponent.ClearSpeaking(_c.NodeId);
        base.Stop();
    }

    private byte[] Loop(byte[] src, int length)
    {
        var r = new byte[length & ~1];
        int offset = _rng.Next(src.Length / 2) * 2;
        for (int i = 0; i < r.Length; i++) r[i] = src[(offset + i) % src.Length];
        return r;
    }

    private float[] FadePoints()
    {
        var p = new float[9];
        for (int i = 0; i < p.Length; i++) p[i] = 0.35f + (float)_rng.NextDouble() * 0.65f;
        return p;
    }
}

/// <summary>
/// A pilot's call, streamed. The worker thread prepares everything that does not depend on the
/// listener's antennas (<see cref="AtcPilot.Prepare"/>); the game thread mixes it at the live
/// signal-to-noise (<see cref="AtcMixer.Pilot"/>). The SNR is the server's, with no reflector gain,
/// plus the gain toward the pilot now of whichever antenna hears it best above its own squelch, less
/// the path loss since the call arrived: turn onto a pilot and they come up out of the static, turn
/// away and the squelch closes on them.
/// </summary>
internal sealed class AtcPilotLine : AtcStreamedLine
{
    /// <summary>A call this far under the squelch is not worth starting: nothing of it would be heard.</summary>
    private const double StartMarginDb = 6;
    private const double StaticTauSeconds = 0.050;

    private readonly WireTrafficPilotCall _c;
    private readonly int _seed;
    private readonly double? _distanceAtArrival;
    private System.Threading.Tasks.Task<AtcPilot.Prepared> _processing;
    private AtcPilot.Prepared _p;
    private AtcLineGains _gains;

    public AtcPilotLine(WireTrafficPilotCall c, int seed)
    {
        _c = c;
        _seed = seed;
        _distanceAtArrival = AtcRadio.DistanceKm(c.Aim);
    }

    public WireTrafficPilotCall Call => _c;

    protected override string Kind => "pilot";

    /// <summary>How the listener's radio hears this pilot right now: the server's signal-to-noise (no
    /// antenna gain) less the path loss since the call arrived, through whichever antenna clears its own
    /// squelch by the most (<see cref="AtcRadio.Pilot"/>).</summary>
    public override AtcRadio.Reception Reception()
    {
        double snr = _c.SnrDeciDb / 10.0;
        double? d = AtcRadio.DistanceKm(_c.Aim);
        if (d.HasValue && _distanceAtArrival.HasValue)
            snr -= 20 * Math.Log10(Math.Max(d.Value, 0.05) / Math.Max(_distanceAtArrival.Value, 0.05));
        return AtcRadio.Pilot(_c.Aim, snr, _c.SquelchDb);
    }

    public override bool Audible(DateTime now)
    {
        AtcRadio.Reception r = Reception();
        return r.SnrDb >= r.SquelchDb - StartMarginDb;
    }

    public override bool Tick(DateTime now, float master, float dt)
    {
        if (Started) return Continue(now, master);
        if (_processing == null)
        {
            (byte[] clip, byte[] noise) = AtcPilot.PickClip(new Random(_seed));
            if (clip == null || noise == null) return false;
            float[] src = AtcMixer.ToFloat(clip), st = AtcMixer.ToFloat(noise);
            _processing = System.Threading.Tasks.Task.Run(() => AtcPilot.Prepare(src, st, _c, _seed));
            return true;
        }
        if (!_processing.IsCompleted) return true;
        if (_processing.IsFaulted)
        {
            Plugin.Log("ATC pilot processing failed: " + _processing.Exception?.GetBaseException().Message);
            return false;
        }
        _p = _processing.Result;
        _processing = null;
        _gains = new AtcLineGains(AtcLibrary.Rate, StaticTauSeconds);
        if (!Begin(now, _p.Signal.Length, master)) return false;
        if (Config.Current.DebugLogging)
            Plugin.Log("ATC pilot " + _c.Event + " @" + _c.NodeId + " snr=" + Reception().SnrDb.ToString("0.0") + "dB (sent "
                       + (_c.SnrDeciDb / 10.0).ToString("0.0") + ", gain " + AtcRadio.GainDb(_c.Aim).ToString("0.0")
                       + ") squelch=" + Reception().SquelchDb.ToString("0") + "dB doppler=" + _c.DopplerHz + "Hz flutter="
                       + _c.FlutterHz + "Hz cutoff=" + _c.CutoffHz + "Hz");
        return true;
    }

    protected override byte[] MixChunk(int from, int count, float master, AtcRadio.Reception r) =>
        AtcMixer.Pilot(_p.Signal, _p.FadeDb, _p.Static, from, count, r.SnrDb, r.SquelchDb, master, _gains);
}
