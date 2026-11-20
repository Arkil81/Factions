using System;
using System.Collections.Generic;
using System.IO;
using VRage.FileSystem;

namespace FactionsUI;

/// <summary>
/// The plugin's own icon art, put somewhere the renderer can name.
///
/// <para><b>Why the art has to be written to disk at all.</b> Pulsar compiles this plugin from
/// <c>.cs</c> source with Roslyn, so there is no assembly for a binary resource to ride in and no
/// plugin folder guaranteed to exist at runtime. The bytes therefore travel as source — base64 in
/// <see cref="FleetIconArt"/>, emitted here — and land beside the config file, under the one
/// directory the plugin already knows is writable (<see cref="Settings.ConfigStorage"/> uses the
/// same root).</para>
///
/// <para><b>Why an absolute path works.</b> <c>MyResourceUtils.GetTextureFullPath</c> returns a
/// rooted texture name verbatim instead of resolving it under the game's Content folder, so any
/// file on disk is drawable by <c>MyGuiManager.DrawSpriteBatch</c>. PNG is loaded as readily as
/// DDS: SE registers an ImageSharp loader alongside its DDS one.</para>
///
/// <para><b>The art must be PREMULTIPLIED, and this is the trap.</b> The sprite pass draws with
/// <c>premultipliedAlpha: true</c>, which selects the pixel shader compiled WITHOUT
/// <c>PREMULTIPLY_ALPHA</c> — that macro exists for the other case and means "premultiply this for
/// me". A straight-alpha texture therefore has its transparent surround composited at full
/// strength and the whole quad draws as a flat rectangle in the surround's colour. That is what
/// FAC-369 measured on <c>GravityHudGlobe.dds</c>, whose transparent texels carry (178,201,214);
/// the rule recorded there as "BC7/DX10 with black transparent texels" is that same fact seen from
/// the file side, since black transparent texels are what premultiplication produces. Art
/// generated for this store premultiplies, and its generator asserts it.</para>
///
/// <para><b>The content hash is in the filename, not a version field.</b> SE's texture manager
/// caches by path, so overwriting a file in place is not guaranteed to be picked up. A changed
/// icon gets a new name instead, which cannot collide with anything already resident.</para>
/// </summary>
internal static class IconStore
{
    /// <summary>Resolved absolute paths by icon name. Empty until <see cref="Extract"/> runs, and
    /// missing an entry for any icon that failed to write — callers fall back rather than hand a
    /// bad path to the renderer, which would draw nothing at all, silently.</summary>
    private static readonly Dictionary<string, string> Paths =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool _extracted;

    /// <summary>Whether any icon at all is available. False means every draw falls back to stock
    /// SE art; the map still works.</summary>
    public static bool Available { get { return Paths.Count > 0; } }

    private static string IconDirectory
    {
        get { return Path.Combine(MyFileSystem.UserDataPath, "Storage", "Factions", "icons"); }
    }

    /// <summary>
    /// Writes any icon not already on disk and records where each one landed. Safe to call more
    /// than once; only the first call does work.
    ///
    /// <para>A failure here is not fatal and is not allowed to be: the icons are presentation, and
    /// a client that cannot write them should still draw a map. Each icon is written independently
    /// so one bad file does not cost the rest.</para>
    /// </summary>
    public static void Extract()
    {
        if (_extracted) return;
        _extracted = true;

        string directory = IconDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Plugin.Log("icon store unavailable, falling back to stock markers: " + ex.Message);
            return;
        }

        int written = 0;
        foreach (KeyValuePair<string, string> art in FleetIconArt.Base64)
        {
            byte[] png;
            try
            {
                png = Convert.FromBase64String(art.Value);
            }
            catch (Exception ex)
            {
                Plugin.Log("icon " + art.Key + " is not decodable: " + ex.Message);
                continue;
            }

            string path = Path.Combine(directory, art.Key + "-" + Hash8(png) + ".png");
            try
            {
                // Length rather than a re-read: the name already carries the content hash, so a
                // file of the right name and size is the right file.
                var existing = new FileInfo(path);
                if (!existing.Exists || existing.Length != png.Length)
                {
                    File.WriteAllBytes(path, png);
                    written++;
                }
                Paths[art.Key] = path;
            }
            catch (Exception ex)
            {
                Plugin.Log("icon " + art.Key + " could not be written: " + ex.Message);
            }
        }

        Prune(directory);
        Plugin.Log("icons ready: " + Paths.Count + "/" + FleetIconArt.Base64.Count
                   + " (" + written + " written)");
    }

    /// <summary>The absolute path for an icon, or null when it is not on disk. Null is a caller's
    /// cue to draw stock art, never a path to hand the renderer.</summary>
    public static string Texture(string name)
    {
        string path;
        return name != null && Paths.TryGetValue(name, out path) ? path : null;
    }

    /// <summary>
    /// Deletes icon files this build did not write. Without it every art revision leaves its
    /// predecessor behind for good, since the hash in the name means a new version never
    /// overwrites an old one.
    /// </summary>
    private static void Prune(string directory)
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in Paths) keep.Add(entry.Value);

            string[] files = Directory.GetFiles(directory, "*.png");
            for (int i = 0; i < files.Length; i++)
            {
                if (keep.Contains(files[i])) continue;
                try { File.Delete(files[i]); }
                catch (Exception) { /* a locked leftover costs disk, not correctness */ }
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping. Failing it must not cost the icons that did write.
        }
    }

    /// <summary>FNV-1a over the file bytes, as eight hex digits. A name, not a checksum: it only
    /// has to differ when the art does.</summary>
    private static string Hash8(byte[] bytes)
    {
        unchecked
        {
            const uint Offset = 2166136261;
            const uint Prime = 16777619;
            uint hash = Offset;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= Prime;
            }
            return hash.ToString("x8");
        }
    }
}
