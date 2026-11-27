using System;

namespace FactionsUI.Atc;

/// <summary>A one-pole glide toward a target, stepped once per sample, so a level that moves is heard
/// as a sweep rather than a step. The first step lands on the target.</summary>
internal sealed class AtcGlide
{
    private readonly float _k;
    private bool _primed;

    public AtcGlide(double tauSeconds, int rate) { _k = AtcGate.Coefficient(tauSeconds, rate); }

    public float Value { get; private set; }

    public float Next(float target)
    {
        if (!_primed) { _primed = true; Value = target; }
        else Value += (target - Value) * _k;
        return Value;
    }
}

/// <summary>The squelch on one line, per sample: it opens when the signal clears the threshold and
/// closes only once it falls <see cref="HysteresisDb"/> under it, and the audio ramps in over
/// <see cref="OpenTauSeconds"/> and out over <see cref="CloseTauSeconds"/>. It starts closed, so a
/// line keys up rather than clicks on.</summary>
internal sealed class AtcGate
{
    public const double HysteresisDb = 2;
    public const double OpenTauSeconds = 0.010;
    public const double CloseTauSeconds = 0.040;

    private readonly float _kOpen, _kClose;

    public AtcGate(int rate)
    {
        _kOpen = Coefficient(OpenTauSeconds, rate);
        _kClose = Coefficient(CloseTauSeconds, rate);
    }

    public bool Open { get; private set; }
    public float Level { get; private set; }

    /// <summary>One sample at <paramref name="marginDb"/> = signal-to-noise less the squelch.</summary>
    public float Next(double marginDb)
    {
        if (Open) { if (marginDb < -HysteresisDb) Open = false; }
        else if (marginDb >= 0) Open = true;
        Level += ((Open ? 1f : 0f) - Level) * (Open ? _kOpen : _kClose);
        return Level;
    }

    internal static float Coefficient(double tauSeconds, int rate) => (float)(1 - Math.Exp(-1.0 / (tauSeconds * rate)));
}

/// <summary>The levels one line carries from chunk to chunk: its squelch, the static under it, the
/// listener's volume, and a controller's distance fade.</summary>
internal sealed class AtcLineGains
{
    public const double MasterTauSeconds = 0.050;
    public const double FadeTauSeconds = 0.050;

    public readonly AtcGate Gate;
    public readonly AtcGlide Static, Master, Fade;

    public AtcLineGains(int rate, double staticTauSeconds)
    {
        Gate = new AtcGate(rate);
        Static = new AtcGlide(staticTauSeconds, rate);
        Master = new AtcGlide(MasterTauSeconds, rate);
        Fade = new AtcGlide(FadeTauSeconds, rate);
    }
}

/// <summary>
/// The radio mix, sample by sample (docs/atc-voice.md, "Aiming by ear"). Every level that moves while a
/// line plays -- the static the signal-to-noise sets, the squelch, the volume, the distance fade --
/// glides per sample from where the last chunk left it, so a turning antenna or a slider drag is a
/// sweep, never a step. SE's voice gets these chunks at a fixed volume of 1.
/// </summary>
internal static class AtcMixer
{
    /// <summary>The static is never allowed above the voice by more than this (past it the squelch has
    /// closed anyway).</summary>
    public const double MaxNoiseOverVoiceDb = 6.0;

    private static readonly float MaxNoiseGain = (float)Math.Pow(10, MaxNoiseOverVoiceDb / 20.0);

    /// <summary>The static under a controller line on the distance ladder: none at
    /// <paramref name="cleanDb"/> and above, and never louder than the last rung inside reach,
    /// <paramref name="edgeDb"/>. Past the reach the ladder reads -100 dB, which closes the squelch;
    /// taken as a level it is a gain of 10^5, heard as a burst of full-scale static as the squelch
    /// closes and again for over a second after it reopens.</summary>
    public static float ControllerNoise(double snrDb, double cleanDb, double edgeDb) =>
        snrDb >= cleanDb ? 0f : (float)Math.Pow(10, -Math.Max(snrDb, edgeDb) / 20.0);

    /// <summary>A pilot call, samples [from, from+count): the voice and its static at this
    /// signal-to-noise, the squelch read against the instantaneous fade.</summary>
    public static byte[] Pilot(float[] signal, float[] fadeDb, float[] noise, int from, int count,
                               double snrDb, double squelchDb, float master, AtcLineGains g)
    {
        float noiseTarget = (float)Math.Pow(10, -Math.Max(snrDb, -MaxNoiseOverVoiceDb) / 20.0);
        var pcm = new byte[count * 2];
        for (int k = 0; k < count; k++)
        {
            int i = from + k;
            float open = g.Gate.Next(snrDb + fadeDb[i] - squelchDb);
            float gn = g.Static.Next(noiseTarget);
            float m = g.Master.Next(master);
            Write(pcm, k, m * open * (signal[i] + noise[i] * gn));
        }
        return pcm;
    }

    /// <summary>A controller line, samples [from, from+count) of <paramref name="speech"/>. The static
    /// bed at <paramref name="noiseTarget"/>; at the edge of reach (<paramref name="fading"/>) the voice
    /// also wanders between <paramref name="fadePoints"/> across the line.</summary>
    public static byte[] Controller(float[] speech, float[] noise, float[] fadePoints, int from, int count,
                                    float noiseTarget, bool fading, double marginDb, float master, AtcLineGains g)
    {
        noiseTarget = Math.Min(noiseTarget, MaxNoiseGain);
        var pcm = new byte[count * 2];
        for (int k = 0; k < count; k++)
        {
            int i = from + k;
            float open = g.Gate.Next(marginDb);
            float gn = g.Static.Next(noiseTarget);
            float fade = g.Fade.Next(fading ? FadeAt(fadePoints, i, speech.Length) : 1f);
            float m = g.Master.Next(master);
            float n = noise == null ? 0f : noise[i] * gn;
            Write(pcm, k, m * open * (speech[i] * fade + n));
        }
        return pcm;
    }

    /// <summary>The fade at sample <paramref name="i"/> of <paramref name="total"/>: a line through the
    /// points, spread evenly over the line.</summary>
    public static float FadeAt(float[] points, int i, int total)
    {
        double x = total <= 1 ? 0 : Math.Max(0, Math.Min(1, i / (double)(total - 1))) * (points.Length - 1);
        int k = Math.Min((int)x, points.Length - 2);
        float t = (float)(x - k);
        return points[k] + (points[k + 1] - points[k]) * t;
    }

    public static float[] ToFloat(byte[] pcm)
    {
        var f = new float[pcm.Length / 2];
        for (int i = 0; i < f.Length; i++) f[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
        return f;
    }

    private static void Write(byte[] pcm, int k, float s)
    {
        short q = (short)(Math.Max(-1f, Math.Min(1f, s)) * 32767);
        pcm[2 * k] = (byte)q;
        pcm[2 * k + 1] = (byte)(q >> 8);
    }
}
