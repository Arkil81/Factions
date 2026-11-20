using System;
using System.Collections.Generic;
using Factions.Wire;
using VRage.Audio;
using VRage.Data.Audio;
using VRageMath;

namespace FactionsUI.Atc;

/// <summary>One line on the radio, played on its own SE voices and heard through the listener's
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

    /// <summary>A one-pole glide toward a target, so a turn is heard as a sweep rather than a click.</summary>
    protected static float Glide(float value, float target, float dt, float tau) =>
        value + (target - value) * Math.Min(1f, dt / tau);

    protected static void Destroy(IMySourceVoice v)
    {
        if (v == null) return;
        try { if (v.IsValid) { v.Stop(force: true); v.Destroy(); } }
        catch (Exception ex) { Plugin.Log("ATC voice teardown failed: " + ex.Message); }
    }
}

/// <summary>
/// A station controller's line. Two voices: the speech as one buffer, and a static bed under it
/// whose level follows the listener's radio every frame (<see cref="AtcRadio.Controller"/>). The
/// approved distance ladder, read live and per antenna: none under 25% of reach, then 25, 12 and 5 dB
/// under the voice, the last also fading; past the reach, or under the antenna's own squelch
/// (<see cref="AtcSquelch"/>), the line goes quiet until the listener turns back onto the station.
/// </summary>
internal sealed class AtcControllerLine : AtcLine
{
    private readonly WireTrafficCallout _c;
    private readonly List<AtcStep> _steps;
    private readonly Random _rng;
    private IMySourceVoice _voice, _static;
    private DateTime _startedAt, _endsAt;
    private float[] _fade;
    private float _staticGain = -1f, _open = -1f;

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

    /// <summary>How the listener's radio hears the station right now (<see cref="AtcRadio.Controller"/>).</summary>
    public AtcRadio.Reception Reception() => AtcRadio.Controller(_c.Aim, _c.DistanceShare);

    public override bool Audible(DateTime now) => Reception().Open;

    private bool Start(DateTime now, float master)
    {
        byte[] pcm = AtcComponent.Assemble(_steps);
        if (pcm == null || pcm.Length == 0) return false;
        IMySourceVoice v = MyAudio.Static?.GetSound(new AtcEmitter(), MySoundDimensions.D2);
        if (v == null) return false; // no audio device, or audio disabled
        byte[] st = AtcLibrary.Get("fx.static");
        IMySourceVoice s = st == null ? null : MyAudio.Static.GetSound(new AtcEmitter(), MySoundDimensions.D2);
        _voice = v;
        _startedAt = now;
        _endsAt = now.AddSeconds(pcm.Length / 2.0 / AtcLibrary.Rate);
        _fade = FadePoints();
        v.SetVolume(0f);
        v.SubmitBuffer(pcm);
        if (s != null)
        {
            s.SetVolume(0f);
            s.SubmitBuffer(Loop(st, pcm.Length));
            _static = s;
        }
        Mix(now, master, 1f);
        v.StartBuffered();
        _static?.StartBuffered();
        // The line does not say who is speaking; the station's HUD icon shows it while it plays.
        AtcComponent.SetSpeaking(_c.NodeId, Hostile);
        if (Config.Current.DebugLogging)
            Plugin.Log("ATC playing " + _c.Event + " @" + _c.NodeId + " " + (_endsAt - now).TotalSeconds.ToString("0.0")
                       + " s, share=" + Reception().Share.ToString("0.00") + " snr=" + Reception().SnrDb.ToString("0")
                       + "dB squelch=" + Reception().SquelchDb.ToString("0") + "dB gain=" + AtcRadio.GainDb(_c.Aim).ToString("0.0") + "dB");
        return true;
    }

    public override bool Tick(DateTime now, float master, float dt)
    {
        if (_voice == null) return Start(now, master);
        if (now >= _endsAt || !_voice.IsValid) return false;
        Mix(now, master, dt);
        return true;
    }

    private void Mix(DateTime now, float master, float dt)
    {
        AtcRadio.Reception r = Reception();
        // The static under the voice is the signal-to-noise on the approved ladder; the squelch of the
        // antenna that hears it best opens and closes the line. Both glide, so a turn is a sweep.
        float staticTarget = r.SnrDb >= AtcRadio.CleanDb ? 0f : Db(-r.SnrDb);
        float openTarget = r.Open ? 1f : 0f;
        _staticGain = _staticGain < 0 ? staticTarget : Glide(_staticGain, staticTarget, dt, 0.15f);
        _open = _open < 0 ? openTarget : Glide(_open, openTarget, dt, 0.05f);
        float fade = r.Share >= 0.8 ? FadeAt(now) : 1f;
        _voice.SetVolume(master * fade * _open);
        if (_static != null && _static.IsValid) _static.SetVolume(master * _staticGain * _open);
    }

    public override void Stop()
    {
        Destroy(_voice);
        Destroy(_static);
        if (_startedAt != default(DateTime)) AtcComponent.ClearSpeaking(_c.NodeId);
        _voice = _static = null;
    }

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

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

    private float FadeAt(DateTime now)
    {
        double span = (_endsAt - _startedAt).TotalSeconds;
        double x = span <= 0 ? 0 : Math.Max(0, Math.Min(1, (now - _startedAt).TotalSeconds / span)) * (_fade.Length - 1);
        int k = Math.Min((int)x, _fade.Length - 2);
        return MathHelper.Lerp(_fade[k], _fade[k + 1], (float)(x - k));
    }
}

/// <summary>
/// A pilot's call, streamed. The worker thread prepares everything that does not depend on the
/// listener's antennas (<see cref="AtcPilot.Prepare"/>); the game thread mixes it at the live
/// signal-to-noise in <see cref="ChunkMs"/> chunks, keeping <see cref="LeadMs"/> queued on the voice.
/// The SNR is the server's, with no reflector gain, plus the gain toward the pilot now of whichever
/// antenna hears it best above its own squelch, less the path loss since the call arrived: turn onto a
/// pilot and they come up out of the static, turn away and the squelch closes on them.
///
/// <para>SE's raw voice ends itself when its queue runs dry (<c>MySourceVoice.OnAllBuffersFinished</c>),
/// so the lead is what keeps a slow frame from cutting a call short.</para>
/// </summary>
internal sealed class AtcPilotLine : AtcLine
{
    private const int ChunkMs = 50;
    private const int LeadMs = 250;
    /// <summary>A call this far under the squelch is not worth starting: nothing of it would be heard.</summary>
    private const double StartMarginDb = 6;

    private readonly WireTrafficPilotCall _c;
    private readonly int _seed;
    private readonly double? _distanceAtArrival;
    private System.Threading.Tasks.Task<AtcPilot.Prepared> _processing;
    private AtcPilot.Prepared _p;
    private IMySourceVoice _voice;
    private DateTime _playFrom;
    private int _submitted;
    private float _open;

    public AtcPilotLine(WireTrafficPilotCall c, int seed)
    {
        _c = c;
        _seed = seed;
        _distanceAtArrival = AtcRadio.DistanceKm(c.Aim);
    }

    public WireTrafficPilotCall Call => _c;

    /// <summary>How the listener's radio hears this pilot right now: the server's signal-to-noise (no
    /// antenna gain) less the path loss since the call arrived, through whichever antenna clears its own
    /// squelch by the most (<see cref="AtcRadio.Pilot"/>).</summary>
    public AtcRadio.Reception Reception()
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
        if (_p == null)
        {
            if (_processing == null)
            {
                (byte[] clip, byte[] noise) = AtcPilot.PickClip(new Random(_seed));
                if (clip == null || noise == null) return false;
                float[] src = AtcPilot.ToFloat(clip), st = AtcPilot.ToFloat(noise);
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
            _voice = MyAudio.Static?.GetSound(new AtcEmitter(), MySoundDimensions.D2);
            if (_voice == null) return false;
            _voice.SetVolume(master);
            _playFrom = now;
            Feed(now);
            _voice.StartBuffered();
            if (Config.Current.DebugLogging)
                Plugin.Log("ATC pilot " + _c.Event + " @" + _c.NodeId + " snr=" + Reception().SnrDb.ToString("0.0") + "dB (sent "
                           + (_c.SnrDeciDb / 10.0).ToString("0.0") + ", gain " + AtcRadio.GainDb(_c.Aim).ToString("0.0")
                           + ") squelch=" + Reception().SquelchDb.ToString("0") + "dB doppler=" + _c.DopplerHz + "Hz flutter="
                           + _c.FlutterHz + "Hz cutoff=" + _c.CutoffHz + "Hz");
            return true;
        }
        int total = _p.Signal.Length;
        double played = (now - _playFrom).TotalSeconds * AtcPilot.Rate;
        if (played >= total || _voice == null || !_voice.IsValid) return false;
        if (_submitted < total && !_voice.IsPlaying)
        {
            if (Config.Current.DebugLogging) Plugin.Log("ATC pilot underrun at " + (played / AtcPilot.Rate).ToString("0.00") + " s");
            return false;
        }
        _voice.SetVolume(master);
        Feed(now);
        return true;
    }

    /// <summary>Mixes and queues chunks until <see cref="LeadMs"/> is ahead of what has played.</summary>
    private void Feed(DateTime now)
    {
        int total = _p.Signal.Length, chunk = AtcPilot.Rate * ChunkMs / 1000;
        double played = (now - _playFrom).TotalSeconds * AtcPilot.Rate;
        int want = (int)Math.Min(total, played + AtcPilot.Rate * LeadMs / 1000.0);
        AtcRadio.Reception r = Reception();
        while (_submitted < want)
        {
            int n = Math.Min(chunk, total - _submitted);
            _voice.SubmitBuffer(AtcPilot.Mix(_p, _submitted, n, r.SnrDb, r.SquelchDb, ref _open));
            _submitted += n;
        }
    }

    public override void Stop()
    {
        Destroy(_voice);
        _voice = null;
    }
}
