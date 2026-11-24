using System;
using FactionsUI.Atc;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The client radio mix: a level the listener moves while a line plays -- the
    /// signal-to-noise as an antenna turns, the squelch slider, the volume, the distance fade -- must
    /// glide sample by sample, never step at a chunk boundary. Measured on a DC input, where any gain
    /// step shows up as the same step in the output.</summary>
    public class AtcMixerTests
    {
        private const int Rate = 24000;
        private const int Chunk = Rate / 20;   // the 50 ms the playback streams in
        /// <summary>The largest sample-to-sample change a gliding gain may make on a 0.5 DC input: a
        /// stepped one makes ten to a hundred times this.</summary>
        private const float MaxStep = 0.005f;

        private static float[] Dc(int n, float v) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        private static float[] Samples(byte[] pcm)
        {
            var f = new float[pcm.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32767f;
            return f;
        }

        private static float MaxJump(float[] s, int from)
        {
            float m = 0;
            for (int i = Math.Max(1, from); i < s.Length; i++) m = Math.Max(m, Math.Abs(s[i] - s[i - 1]));
            return m;
        }

        /// <summary>A pilot call of pure DC static (no voice, no fade), mixed chunk by chunk at the SNR,
        /// squelch and volume each chunk names.</summary>
        private static float[] Pilot(params (double Snr, double Squelch, float Master)[] chunks)
        {
            int n = chunks.Length * Chunk;
            float[] signal = new float[n], fade = new float[n], noise = Dc(n, 0.5f);
            var g = new AtcLineGains(Rate, 0.050);
            var outp = new float[n];
            for (int c = 0; c < chunks.Length; c++)
            {
                float[] s = Samples(AtcMixer.Pilot(signal, fade, noise, c * Chunk, Chunk, chunks[c].Snr, chunks[c].Squelch, chunks[c].Master, g));
                Array.Copy(s, 0, outp, c * Chunk, Chunk);
            }
            return outp;
        }

        [Fact]
        public void A_turning_antenna_sweeps_the_static_instead_of_stepping_it()
        {
            // 0 dB SNR for 200 ms, then 30 dB for 400 ms: the static falls by 30 dB at a chunk boundary.
            float[] s = Pilot((0, -20, 1f), (0, -20, 1f), (0, -20, 1f), (0, -20, 1f),
                              (30, -20, 1f), (30, -20, 1f), (30, -20, 1f), (30, -20, 1f), (30, -20, 1f), (30, -20, 1f), (30, -20, 1f), (30, -20, 1f));
            Assert.True(MaxJump(s, Chunk) < MaxStep, "largest step " + MaxJump(s, Chunk));
            Assert.InRange(s[4 * Chunk - 1], 0.49f, 0.51f);              // before: the static at full
            Assert.InRange(s[s.Length - 1], 0.0f, 0.5f * 0.0316f * 1.1f); // after: down 30 dB
        }

        [Fact]
        public void Moving_the_squelch_over_the_signal_fades_the_line_out_and_back()
        {
            // Open at squelch -20; closed by squelch 40; open again at -20.
            float[] s = Pilot((0, -20, 1f), (0, -20, 1f), (0, -20, 1f), (0, 40, 1f), (0, 40, 1f), (0, 40, 1f), (0, 40, 1f), (0, 40, 1f),
                              (0, -20, 1f), (0, -20, 1f), (0, -20, 1f));
            Assert.True(MaxJump(s, Chunk) < MaxStep, "largest step " + MaxJump(s, Chunk));
            Assert.True(s[8 * Chunk - 1] < 0.005f, "closed: " + s[8 * Chunk - 1]);
            Assert.True(s[s.Length - 1] > 0.49f, "reopened: " + s[s.Length - 1]);
        }

        [Fact]
        public void A_volume_change_glides()
        {
            float[] s = Pilot((0, -20, 1f), (0, -20, 1f), (0, -20, 1f), (0, -20, 0.1f), (0, -20, 0.1f), (0, -20, 0.1f), (0, -20, 0.1f),
                              (0, -20, 0.1f), (0, -20, 0.1f), (0, -20, 0.1f), (0, -20, 0.1f));
            Assert.True(MaxJump(s, Chunk) < MaxStep, "largest step " + MaxJump(s, Chunk));
            Assert.InRange(s[s.Length - 1], 0.045f, 0.055f);
        }

        [Fact]
        public void A_line_keys_up_rather_than_clicking_on()
        {
            float[] s = Pilot((0, -20, 1f), (0, -20, 1f));
            Assert.True(MaxJump(s, 0) < MaxStep, "largest step " + MaxJump(s, 0));
            Assert.True(s[0] < MaxStep, "first sample " + s[0]);
        }

        [Fact]
        public void Squelch_holds_open_through_its_hysteresis_and_closes_past_it()
        {
            var gate = new AtcGate(Rate);
            for (int i = 0; i < Rate / 10; i++) gate.Next(0);
            Assert.True(gate.Open);
            for (int i = 0; i < Rate / 10; i++) gate.Next(-AtcGate.HysteresisDb + 0.1);
            Assert.True(gate.Open);
            Assert.True(gate.Level > 0.99f);
            gate.Next(-AtcGate.HysteresisDb - 0.1);
            Assert.False(gate.Open);
            // Below the threshold but inside the hysteresis does not reopen it.
            gate.Next(-0.5);
            Assert.False(gate.Open);
            gate.Next(0);
            Assert.True(gate.Open);
        }

        [Fact]
        public void Squelch_opens_in_about_ten_ms_and_closes_in_about_forty()
        {
            var gate = new AtcGate(Rate);
            int opened = 0;
            while (gate.Next(10) < 1 - 1 / Math.E) opened++;
            Assert.InRange(opened, Rate * 9 / 1000, Rate * 11 / 1000);
            for (int i = 0; i < Rate / 2; i++) gate.Next(10);
            int closed = 0;
            while (gate.Next(-10) > 1 / Math.E) closed++;
            Assert.InRange(closed, Rate * 38 / 1000, Rate * 42 / 1000);
        }

        [Fact]
        public void A_controller_crossing_into_the_fading_band_sweeps_down()
        {
            int n = 12 * Chunk;
            float[] speech = Dc(n, 0.5f), fade = { 0.35f, 0.35f };
            var g = new AtcLineGains(Rate, 0.150);
            var outp = new float[n];
            for (int c = 0; c < 12; c++)
            {
                float[] s = Samples(AtcMixer.Controller(speech, null, fade, c * Chunk, Chunk, 0f, c >= 4, 30, 1f, g));
                Array.Copy(s, 0, outp, c * Chunk, Chunk);
            }
            Assert.True(MaxJump(outp, 0) < MaxStep, "largest step " + MaxJump(outp, 0));
            Assert.InRange(outp[4 * Chunk - 1], 0.49f, 0.51f);
            Assert.InRange(outp[n - 1], 0.17f, 0.18f);
        }

        [Fact]
        public void The_fade_runs_through_its_points_across_the_line()
        {
            float[] p = { 0.4f, 1f, 0.6f };
            Assert.Equal(0.4f, AtcMixer.FadeAt(p, 0, 101), 4);
            Assert.Equal(1f, AtcMixer.FadeAt(p, 50, 101), 4);
            Assert.Equal(0.6f, AtcMixer.FadeAt(p, 100, 101), 4);
            Assert.Equal(0.7f, AtcMixer.FadeAt(p, 25, 101), 4);
        }
    }
}
