using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FactionsUI.Atc;

/// <summary>
/// The traffic-control voice library (FAC-419, docs/atc-voice.md): the clips rendered by
/// scripts/atc-audio/render_assets.py into src/FactionsUI/Content/Atc, which Pulsar hands the plugin
/// as its asset folder (<see cref="Plugin.LoadAssets"/>).
///
/// <para>manifest.txt is the index: <c>clip &lt;key&gt; &lt;path&gt;</c>, <c>slot &lt;name&gt; &lt;key&gt;...</c>,
/// <c>chatter &lt;key&gt;...</c>, <c>spacing &lt;name&gt; &lt;ms&gt;</c>. A clip is read on first use,
/// expanded from 8-bit mu-law to the 16-bit PCM SE's raw voice takes (24 kHz mono), faded in and out
/// over 4 ms so a clip next to a silent gap does not tick, and kept.</para>
/// </summary>
internal static class AtcLibrary
{
    public const int Rate = 24000;
    private const int EdgeFadeSamples = Rate * 4 / 1000;

    private static readonly Dictionary<string, string> Clips = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> Slots = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> SpacingMs = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte[]> Pcm = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static string[] _chatter = new string[0];
    private static string _dir;

    /// <summary>Why the library is unusable, or null when it loaded.</summary>
    public static string Problem { get; private set; } = "no asset folder";

    public static bool Ready => Problem == null;

    /// <summary>Called once from <see cref="Plugin.LoadAssets"/>, before Init.</summary>
    public static void Load(string assetFolder)
    {
        Clips.Clear(); Slots.Clear(); SpacingMs.Clear(); Pcm.Clear();
        _dir = assetFolder == null ? null : Path.Combine(assetFolder, "Atc");
        string manifest = _dir == null ? null : Path.Combine(_dir, "manifest.txt");
        if (manifest == null || !File.Exists(manifest))
        {
            Problem = "manifest.txt not found under " + (assetFolder ?? "(no asset folder)");
            return;
        }
        try
        {
            foreach (string line in File.ReadAllLines(manifest))
            {
                string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2) continue;
                switch (t[0])
                {
                    case "clip": if (t.Length == 3) Clips[t[1]] = t[2]; break;
                    case "slot": Slots[t[1]] = Sub(t, 2); break;
                    case "chatter": _chatter = Sub(t, 1); break;
                    case "spacing": if (t.Length == 3 && int.TryParse(t[2], out int ms)) SpacingMs[t[1]] = ms; break;
                }
            }
            Problem = Clips.Count == 0 ? "manifest.txt lists no clips" : null;
        }
        catch (Exception ex)
        {
            Problem = "manifest.txt unreadable: " + ex.Message;
        }
    }

    private static string[] Sub(string[] t, int from)
    {
        var r = new string[t.Length - from];
        Array.Copy(t, from, r, 0, r.Length);
        return r;
    }

    public static bool Has(string key) => Clips.ContainsKey(key);

    public static string[] Slot(string name) => Slots.TryGetValue(name, out string[] keys) ? keys : null;

    public static string[] Chatter => _chatter;

    public static int Spacing(string name, int fallbackMs) => SpacingMs.TryGetValue(name, out int ms) ? ms : fallbackMs;

    public static int ClipCount => Clips.Count;

    /// <summary>The clip as 16-bit little-endian PCM, or null when it is missing or unreadable.</summary>
    public static byte[] Get(string key)
    {
        if (Pcm.TryGetValue(key, out byte[] pcm)) return pcm;
        if (!Clips.TryGetValue(key, out string rel)) return null;
        try
        {
            pcm = Decode(File.ReadAllBytes(Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (Exception ex)
        {
            Plugin.Log("ATC clip " + key + " unreadable: " + ex.Message);
            pcm = null;
        }
        Pcm[key] = pcm;
        return pcm;
    }

    /// <summary>A WAV's data chunk as 16-bit PCM with the edge fade. Accepts mu-law (format 7, what
    /// the renderer writes) and 16-bit PCM (format 1). Anything but 24 kHz mono is refused: SE's raw
    /// voice would play it at the wrong speed.</summary>
    internal static byte[] Decode(byte[] f)
    {
        if (f.Length < 12 || Encoding.ASCII.GetString(f, 0, 4) != "RIFF" || Encoding.ASCII.GetString(f, 8, 4) != "WAVE")
            throw new InvalidDataException("not a RIFF/WAVE file");
        int format = 0, channels = 0, rate = 0, bits = 0;
        for (int p = 12; p + 8 <= f.Length;)
        {
            string id = Encoding.ASCII.GetString(f, p, 4);
            int size = BitConverter.ToInt32(f, p + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(f, p + 8);
                channels = BitConverter.ToInt16(f, p + 10);
                rate = BitConverter.ToInt32(f, p + 12);
                bits = BitConverter.ToInt16(f, p + 22);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != Rate) throw new InvalidDataException(rate + " Hz " + channels + " ch, need 24000 mono");
                int n = Math.Min(size, f.Length - p - 8);
                short[] s;
                if (format == 7 && bits == 8)
                {
                    s = new short[n];
                    for (int i = 0; i < n; i++) s[i] = MuLaw[f[p + 8 + i]];
                }
                else if (format == 1 && bits == 16)
                {
                    s = new short[n / 2];
                    Buffer.BlockCopy(f, p + 8, s, 0, s.Length * 2);
                }
                else throw new InvalidDataException("format " + format + "/" + bits + " bit, need mu-law or 16-bit PCM");
                Fade(s);
                var pcm = new byte[s.Length * 2];
                Buffer.BlockCopy(s, 0, pcm, 0, pcm.Length);
                return pcm;
            }
            p += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    private static void Fade(short[] s)
    {
        int n = Math.Min(EdgeFadeSamples, s.Length / 2);
        for (int i = 0; i < n; i++)
        {
            float g = i / (float)n;
            s[i] = (short)(s[i] * g);
            s[s.Length - 1 - i] = (short)(s[s.Length - 1 - i] * g);
        }
    }

    /// <summary>G.711 mu-law expansion, the 256-entry table.</summary>
    private static readonly short[] MuLaw = BuildMuLaw();

    private static short[] BuildMuLaw()
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;
            int sign = u & 0x80, exponent = (u >> 4) & 0x07, mantissa = u & 0x0F;
            int magnitude = ((mantissa << 3) + 0x84) << exponent;
            magnitude -= 0x84;
            t[i] = (short)(sign != 0 ? -magnitude : magnitude);
        }
        return t;
    }
}
