using System;
using System.IO;

namespace Factions.Core
{
    /// <summary>
    /// Resolves where Factions keeps its data, which is not necessarily where Torch keeps this
    /// instance's plugin storage.
    ///
    /// <para>On a Nexus cluster the instances share one data folder, so brains, indexes and
    /// catalogues belong there rather than three times over in three per-instance directories.
    /// <c>tuning.json</c> itself stays per-instance: it is the file that names the data directory,
    /// so it cannot live inside it.</para>
    /// </summary>
    public static class DataDirectory
    {
        /// <summary>
        /// The effective data directory. An unset <paramref name="configured"/> keeps
        /// <paramref name="instanceDir"/> — the pre-cluster behaviour. A rooted path is taken as
        /// given; a relative one resolves against <paramref name="instanceDir"/>.
        /// </summary>
        public static string Resolve(string configured, string instanceDir)
        {
            if (string.IsNullOrEmpty(configured) || configured.Trim().Length == 0)
                return instanceDir;

            string trimmed = configured.Trim();
            return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(instanceDir, trimmed);
        }

        /// <summary>
        /// Where to read a read-only <b>input</b> from: the data directory if it is there, otherwise
        /// the instance directory.
        ///
        /// <para>Redirecting the data directory moves everything downstream of
        /// <see cref="Resolve"/> with it, including operator- and deploy-authored inputs
        /// (<c>capability_recipes.json</c>, <c>fleet_prefabs.json</c>,
        /// <c>station_name_additions.json</c>, the world recipes) that every loader treats as
        /// optional. Those inputs belong in the data directory — every instance running one world
        /// must read the same copy — and the fallback exists so one still sitting in the instance
        /// directory from before a redirect is read rather than quietly missing.</para>
        ///
        /// <para><b>Inputs fall back; state never does.</b> Brains, the index and the caches must
        /// come from the data directory alone: falling back for those would resurrect a stale local
        /// world beside the shared one, which is the failure this whole arrangement exists to
        /// prevent. Use this for things that are read once at load and never written back.</para>
        /// </summary>
        /// <param name="exists">Existence test — <c>File.Exists</c> or <c>Directory.Exists</c>.</param>
        /// <param name="usedFallback">True when the instance copy was chosen, so the caller can say so.</param>
        /// <returns>The path to read. When neither exists, the data-directory path — the canonical
        /// place, so a "not found" message points at where the file should go.</returns>
        public static string ResolveInput(string fileName, string dataDir, string instanceDir,
            Func<string, bool> exists, out bool usedFallback)
        {
            usedFallback = false;
            string primary = Path.Combine(dataDir, fileName);
            if (string.IsNullOrEmpty(instanceDir) || string.Equals(dataDir, instanceDir,
                    StringComparison.OrdinalIgnoreCase))
                return primary;

            if (exists == null || exists(primary)) return primary;

            string fallback = Path.Combine(instanceDir, fileName);
            if (!exists(fallback)) return primary;

            usedFallback = true;
            return fallback;
        }

        /// <summary>
        /// One <c>INPUT_DIVERGED</c> line when an input exists in both the data directory and the
        /// instance directory with different content, naming the copy <see cref="ResolveInput"/>
        /// reads and the one it ignores; null when there is nothing to say.
        ///
        /// <para><b>Why this exists (FAC-241).</b> <see cref="ResolveInput"/> falls back only when the
        /// primary is <i>absent</i>, so a stale primary silently wins: a deploy refreshed
        /// <c>station_name_additions.json</c> in the instance directory while a twelve-day-old copy in
        /// the data directory went on being read, and nothing was logged. Precedence is deliberately
        /// left alone here — which directory owns which file is a separate decision — this only makes
        /// the disagreement loud.</para>
        ///
        /// <para>Content, not timestamps: two copies with different mtimes can be byte-identical
        /// (the live <c>fleet_prefabs.json</c> pair was), and that is not a divergence.</para>
        /// </summary>
        public static string DescribeDivergence(string fileName, string dataDir, string instanceDir)
        {
            return DescribeDivergence(fileName, dataDir, instanceDir,
                File.Exists, File.ReadAllBytes, File.GetLastWriteTimeUtc);
        }

        /// <summary>Testable core of <see cref="DescribeDivergence(string,string,string)"/>.</summary>
        public static string DescribeDivergence(string fileName, string dataDir, string instanceDir,
            Func<string, bool> exists, Func<string, byte[]> read, Func<string, DateTime> lastWriteUtc)
        {
            if (string.IsNullOrEmpty(dataDir) || string.IsNullOrEmpty(instanceDir)
                || string.Equals(dataDir, instanceDir, StringComparison.OrdinalIgnoreCase))
                return null;

            string primary = Path.Combine(dataDir, fileName);
            string fallback = Path.Combine(instanceDir, fileName);
            if (!exists(primary) || !exists(fallback)) return null;

            byte[] primaryBytes = read(primary);
            byte[] fallbackBytes = read(fallback);
            if (SameBytes(primaryBytes, fallbackBytes)) return null;

            bool usedFallback;
            string chosen = ResolveInput(fileName, dataDir, instanceDir, exists, out usedFallback);
            string ignored = usedFallback ? primary : fallback;
            byte[] chosenBytes = usedFallback ? fallbackBytes : primaryBytes;
            byte[] ignoredBytes = usedFallback ? primaryBytes : fallbackBytes;

            return "INPUT_DIVERGED file=" + fileName
                 + " read=" + chosen + " read_mtime=" + Stamp(lastWriteUtc(chosen))
                 + " read_bytes=" + chosenBytes.Length
                 + " ignored=" + ignored + " ignored_mtime=" + Stamp(lastWriteUtc(ignored))
                 + " ignored_bytes=" + ignoredBytes.Length;
        }

        /// <summary>
        /// One <c>INPUT_READ</c> line naming the copy of an input that was actually read, with its
        /// mtime and size, so a boot log answers "which file, and how old" without a trip to the box
        /// (FAC-241: a stale copy was once read for twelve days with nothing naming it). Null when
        /// <paramref name="path"/> does not exist; the loaders report absence themselves.
        /// </summary>
        public static string DescribeRead(string fileName, string path,
            Func<string, bool> exists, Func<string, DateTime> lastWriteUtc, Func<string, long> length)
        {
            if (string.IsNullOrEmpty(path) || !exists(path)) return null;
            return "INPUT_READ file=" + fileName + " read=" + path
                 + " mtime=" + Stamp(lastWriteUtc(path)) + " bytes=" + length(path);
        }

        /// <summary><see cref="DescribeRead(string,string,Func{string,bool},Func{string,DateTime},Func{string,long})"/>
        /// against the file system.</summary>
        public static string DescribeRead(string fileName, string path)
        {
            return DescribeRead(fileName, path, File.Exists, File.GetLastWriteTimeUtc,
                p => new FileInfo(p).Length);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// True when the resolved data directory holds an empty registry while this instance's own
        /// directory holds a populated one — the shape of a mis-pointed <c>DataDirectory</c>, and a
        /// disagreement that is never intentional.
        ///
        /// <para>Deliberately one-directional. The reverse (data directory populated, instance empty)
        /// is the normal steady state after a cutover and must stay silent. Both empty is a world
        /// before <c>factions init</c> and is equally normal — which is exactly why this cannot be
        /// the only guard; see <see cref="RedirectChanged"/> for the case it cannot see.</para>
        /// </summary>
        /// <param name="redirected">False when the data directory *is* the instance directory, in
        /// which case the two counts are the same file and comparing them is meaningless.</param>
        public static bool RegistrySwapLooksWrong(bool redirected, int resolvedCount, int instanceCount)
        {
            if (!redirected) return false;
            return resolvedCount == 0 && instanceCount > 0;
        }

        /// <summary>
        /// True when this boot resolved to a different data directory than the last one did.
        ///
        /// <para><b>Why this exists and the registry comparison is not enough.</b>
        /// <see cref="RegistrySwapLooksWrong"/> catches a flip *toward* an empty shared directory
        /// while local state exists. It cannot catch the reverse — a silent revert to the instance
        /// directory — because at the moment a revert happens the registries frequently agree
        /// (commonly both empty), and the reverted boot then looks exactly like a first boot.
        ///
        /// That reverse case is the one observed live: <c>DataDirectory</c> lives in
        /// <c>tuning.json</c>, which is per-instance and regenerable, and <c>tuning_defaults.json</c>
        /// carries no key for it — so a regenerated <c>tuning.json</c> comes back empty, meaning the
        /// instance directory. The setting fails <b>open</b>, toward local, with no error.</para>
        ///
        /// <para>This only ever raises an alarm; it never changes where anything resolves. State
        /// still never falls back.</para>
        /// </summary>
        /// <param name="remembered">Data directory recorded by the previous boot; null or empty on a
        /// first run, which is not a change.</param>
        public static bool RedirectChanged(string remembered, string resolved)
        {
            if (string.IsNullOrEmpty(remembered)) return false;
            if (string.IsNullOrEmpty(resolved)) return false;
            return !string.Equals(
                remembered.TrimEnd('\\', '/'), resolved.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="configured"/> asks for somewhere other than the
        /// instance directory — i.e. when a failure to use it is a configuration error rather than
        /// a missing optional setting.</summary>
        public static bool IsRedirected(string configured)
        {
            return !string.IsNullOrEmpty(configured) && configured.Trim().Length > 0;
        }
    }
}
