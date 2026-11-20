using System;
using System.Collections.Generic;
using Factions.Wire;

namespace FactionsUI.Atc;

/// <summary>
/// The pilots' side of the traffic voice (FAC-419 slice 5, docs/atc-voice.md): each
/// <see cref="WireMessageKind.TrafficPilotCall"/> is a garbled pilot call, carried over the radio
/// physics the server computed for this listener. The processing is the radio physics bench's,
/// in C#:
/// <list type="bullet">
/// <item><b>Doppler</b>: a sideband link shifts every voice frequency by the same Hz, done with a
/// Hilbert-transform frequency shifter.</item>
/// <item><b>Multipath flutter</b>: Rician fading, reflections setting its depth, the flutter rate its
/// speed.</item>
/// <item><b>Planet shadow</b>: the diffracted path as a low-pass.</item>
/// <item><b>Path loss</b>: the receiver's automatic gain control holds the voice at LevelDb, so a
/// weaker signal is heard as more static under it (static at LevelDb - SNR), not as a quieter
/// voice. The squelch cuts out wherever the faded signal drops below it.</item>
/// </list>
/// What does not depend on where the listener's antennas point is prepared on a worker thread
/// (<see cref="Prepare"/>). The rest -- the signal-to-noise, which moves with the reflectors' gain
/// toward the pilot and with distance, and the squelch it opens -- is mixed on the game thread in
/// short chunks as the call plays (<see cref="AtcPilotLine"/>), so a listener aims by ear.
/// </summary>
internal static class AtcPilot
{
    internal const int Rate = AtcLibrary.Rate;
    private const double ShuffleShare = 0.6; // the operator's approved G1/G2 mix

    /// <summary>A pilot call with everything but the signal-to-noise applied.</summary>
    internal sealed class Prepared
    {
        /// <summary>The voice at its AGC level, faded, shifted and filtered.</summary>
        public float[] Signal;
        /// <summary>The instantaneous fade, dB, which the squelch reads against the live SNR.</summary>
        public float[] FadeDb;
        /// <summary>Static at the voice's level: scaled by 10^(-SNR/20) as it plays.</summary>
        public float[] Static;
    }

    /// <summary>Picks a garbled clip for a call, or null when the library lacks one.</summary>
    internal static (byte[] Clip, byte[] Noise) PickClip(Random rng)
    {
        string slot = rng.NextDouble() < ShuffleShare ? "pilot.shuffle" : "pilot.smear";
        string[] keys = AtcLibrary.Slot(slot);
        if (keys == null || keys.Length == 0) return (null, null);
        return (AtcLibrary.Get(keys[rng.Next(keys.Length)]), AtcLibrary.Get("fx.static"));
    }

    internal static float[] ToFloat(byte[] pcm)
    {
        var f = new float[pcm.Length / 2];
        for (int i = 0; i < f.Length; i++) f[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
        return f;
    }

    // ---- the physics, off the game thread ---------------------------------------------------------

    /// <summary>Reference level of a controller line (the phrase clips' median RMS, -19.4 dBFS).</summary>
    private const double ControllerRms = 0.107;
    private const double MaxNoiseOverVoiceDb = 6.0;

    internal static Prepared Prepare(float[] src, float[] staticLoop, WireTrafficPilotCall c, int seed)
    {
        var rng = new Random(seed);
        int n = src.Length;
        float[] x = c.DopplerHz != 0 ? FrequencyShift(src, c.DopplerHz) : (float[])src.Clone();
        if (c.CutoffHz < 3350) { x = LowPass(x, c.CutoffHz); x = LowPass(x, c.CutoffHz); }
        float[] env = Fading(n, c.FlutterHz, c.Reflections / 255.0, rng);

        // AGC: the voice stays at LevelDb whatever the path; the static rises as the SNR falls (Mix).
        // Modelling the voice itself falling 6 dB per doubling, as the tuning bench did, put a 2 km call
        // 16 dB under the controller: inaudible in game (2026-09-29).
        double sigRms = ControllerRms * Math.Pow(10, c.LevelDb / 20.0);
        double srcRms = Rms(x), staticRms = Rms(staticLoop);
        double gs = srcRms > 0 ? sigRms / srcRms : 0, gn = staticRms > 0 ? sigRms / staticRms : 0;
        int off = rng.Next(staticLoop.Length);
        var p = new Prepared { Signal = new float[n], FadeDb = new float[n], Static = new float[n] };
        for (int i = 0; i < n; i++)
        {
            p.Signal[i] = (float)(x[i] * gs * env[i]);
            p.FadeDb[i] = (float)(20 * Math.Log10(Math.Max(env[i], 1e-4f)));
            p.Static[i] = (float)(staticLoop[(off + i) % staticLoop.Length] * gn);
        }
        return p;
    }

    /// <summary>Mixes samples [from, from+count) at this signal-to-noise into 16-bit PCM. The static is
    /// never allowed above the voice by more than <see cref="MaxNoiseOverVoiceDb"/> (past that the squelch
    /// has closed anyway). <paramref name="open"/> carries the squelch gate across chunks.</summary>
    internal static byte[] Mix(Prepared p, int from, int count, double snrDb, double squelchDb, ref float open)
    {
        double gn = Math.Pow(10, -Math.Max(snrDb, -MaxNoiseOverVoiceDb) / 20.0);
        int ramp = Rate * 5 / 1000;
        var pcm = new byte[count * 2];
        for (int k = 0; k < count; k++)
        {
            int i = from + k;
            float target = snrDb + p.FadeDb[i] >= squelchDb ? 1f : 0f;
            open += (target - open) / ramp;
            float s = Math.Max(-1f, Math.Min(1f, open * (float)(p.Signal[i] + p.Static[i] * gn)));
            short q = (short)(s * 32767);
            pcm[2 * k] = (byte)q;
            pcm[2 * k + 1] = (byte)(q >> 8);
        }
        return pcm;
    }

    private const int HilbertM = 63;
    private static readonly float[] Hilbert = BuildHilbert();

    private static float[] BuildHilbert()
    {
        var h = new float[2 * HilbertM + 1];
        for (int k = -HilbertM; k <= HilbertM; k++)
        {
            if ((k & 1) == 0) continue;
            double w = 0.54 + 0.46 * Math.Cos(Math.PI * k / HilbertM);
            h[k + HilbertM] = (float)(2.0 / (Math.PI * k) * w);
        }
        return h;
    }

    /// <summary>Single-sideband shift: every frequency moves by <paramref name="hz"/>. The analytic
    /// signal is x delayed by HilbertM plus j times its Hilbert transform; its rotation by the offset,
    /// real part, is the shifted signal.</summary>
    internal static float[] FrequencyShift(float[] x, double hz)
    {
        int n = x.Length, L = Hilbert.Length;
        var y = new float[n];
        double dphi = 2 * Math.PI * hz / Rate, c = 1, s = 0, cd = Math.Cos(dphi), sd = Math.Sin(dphi);
        for (int i = 0; i < n; i++)
        {
            double acc = 0;
            for (int k = 0; k < L; k += 2) { int j = i - k; if (j >= 0) acc += Hilbert[k] * x[j]; }
            double xd = i >= HilbertM ? x[i - HilbertM] : 0;
            y[i] = (float)(xd * c - acc * s);
            double nc = c * cd - s * sd; s = s * cd + c * sd; c = nc;
            if ((i & 1023) == 0) { double m = Math.Sqrt(c * c + s * s); c /= m; s /= m; }
        }
        return y;
    }

    /// <summary>Rician fading: a steady line-of-sight part plus 16 scattered paths whose phases turn at
    /// up to the flutter rate. Reflections 0 is steady; 1 is full Rayleigh fading.</summary>
    internal static float[] Fading(int n, double flutterHz, double reflections, Random rng)
    {
        var env = new float[n];
        if (reflections <= 0.001) { for (int i = 0; i < n; i++) env[i] = 1f; return env; }
        double k = (1 - reflections) / Math.Max(reflections, 1e-3);
        double los = Math.Sqrt(k / (k + 1)), sc = Math.Sqrt(1 / (k + 1)) / Math.Sqrt(16);
        var re = new double[16]; var im = new double[16]; var cr = new double[16]; var ci = new double[16];
        for (int p = 0; p < 16; p++)
        {
            double ph = 2 * Math.PI * rng.NextDouble(), f = flutterHz * Math.Cos(2 * Math.PI * rng.NextDouble());
            re[p] = Math.Cos(ph); im[p] = Math.Sin(ph);
            cr[p] = Math.Cos(2 * Math.PI * f / Rate); ci[p] = Math.Sin(2 * Math.PI * f / Rate);
        }
        for (int i = 0; i < n; i++)
        {
            double sr = 0, si = 0;
            for (int p = 0; p < 16; p++)
            {
                sr += re[p]; si += im[p];
                double nr = re[p] * cr[p] - im[p] * ci[p]; im[p] = re[p] * ci[p] + im[p] * cr[p]; re[p] = nr;
            }
            double gr = los + sc * sr, gi = sc * si;
            env[i] = (float)Math.Sqrt(gr * gr + gi * gi);
        }
        return env;
    }

    internal static float[] LowPass(float[] x, double f0)
    {
        double w = 2 * Math.PI * Math.Min(f0, Rate * 0.45) / Rate, cw = Math.Cos(w), al = Math.Sin(w) / (2 * 0.7071);
        double a0 = 1 + al, a1 = -2 * cw, a2 = 1 - al, b0 = (1 - cw) / 2, b1 = 1 - cw, b2 = (1 - cw) / 2;
        var y = new float[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = (float)v;
        }
        return y;
    }

    private static double Rms(float[] x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * (double)x[i];
        return x.Length > 0 ? Math.Sqrt(s / x.Length) : 0;
    }
}
