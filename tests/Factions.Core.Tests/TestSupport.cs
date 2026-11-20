using System;
using System.IO;
using Factions.Core;

namespace Factions.Core.Tests
{
    internal static class TestSupport
    {
        public static FactionTuning Tuning()
        {
            return TuningWithOverride(null);
        }

        /// <summary>The shipped defaults, read once. <see cref="FactionStore.LoadTuning"/> parses this
        /// text per call either way; re-reading the file per call only adds I/O.</summary>
        private static string _defaultsJson;
        private static readonly object _defaultsGate = new object();

        private static string DefaultsJson()
        {
            lock (_defaultsGate)
            {
                if (_defaultsJson == null)
                    _defaultsJson = File.ReadAllText(Path.Combine(FindRepoRoot(), "instance", "tuning_defaults.json"));
                return _defaultsJson;
            }
        }

        /// <summary>Loads tuning the way a real host does — <c>tuning_defaults.json</c> merged with an
        /// optional <c>tuning.json</c> override — from a directory of its own.
        ///
        /// <para>The directory is per call and cannot be shared: <c>LoadTuning</c> ends by calling
        /// <c>SaveTuning</c>, so it writes a <c>tuning.json</c> into whatever directory it is given, and
        /// a second call against the same directory would read that file back as an override. It is
        /// also deleted per call. Left behind, these accumulate — one per call, at 988 call sites — and
        /// every later <c>CreateDirectory</c> in <c>%TEMP%</c> pays an NTFS index insert against the
        /// whole pile. Cleared 2026-09-14 on the Windows box: <b>1.26 million</b> of them, and a suite
        /// run 31 minutes in had spent <b>1,831 s of kernel time against 16 s of user time</b> — the
        /// tests were not computing, they were waiting on the filesystem. Wall clock had drifted from
        /// ~10 minutes to over 40 as the pile grew, which is the characteristic signature: a suite that
        /// gets slower every time anyone runs it, so nothing ever fails and nobody looks.</para></summary>
        public static FactionTuning TuningWithOverride(string tuningJson)
        {
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-tuning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "tuning_defaults.json"), DefaultsJson());
                if (tuningJson != null)
                    File.WriteAllText(Path.Combine(dir, "tuning.json"), tuningJson);
                var store = new FactionStore(dir);
                var tuning = new FactionTuning();
                store.LoadTuning(tuning);
                return tuning;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Gives <paramref name="n"/> a deposit holding the named ores in equal share, and
        /// returns it. Since FAC-102 a node mints ore only if there is ore under it, so a fixture that
        /// wants a productive node has to say what is in the ground rather than name a kind.
        ///
        /// <para>Sets <see cref="SpatialNode.Richness"/> from that deposit the same way
        /// <c>WorldModel.RefreshNodeContent</c> does, because since FAC-219 the yield and drill-rate
        /// paths read it rather than ResourceValue — a fixture that set only the ground would build a
        /// node with ore under it that mints nothing.</para></summary>
        public static SpatialNode WithOre(SpatialNode n, params string[] ores)
        {
            if (ores == null || ores.Length == 0) ores = new[] { "Iron" };
            n.Deposit = new NodeDeposit();
            foreach (string ore in ores)
                n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            n.Richness = NodeContent.RichnessFor(n, Tuning());
            return n;
        }

        public static WorldModel World(int seed = 1)
        {
            FactionTuning tuning = Tuning();
            // Model an unupgraded machine unless a test says otherwise: the shipped default models a
            // four-speed-module fit, which would silently scale every production expectation. Set
            // Tuning.ProductionProductivityBonus in the test to exercise the upgraded model.
            tuning.ProductionProductivityBonus = 0.0;
            return new WorldModel(new SeededRandom(seed))
            {
                Tuning = tuning,
                UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
        }

        /// <summary>Absolute path to a file in the repo, resolved from the test output directory.
        /// Uses the same anchor the tuning fixture already relies on.</summary>
        public static string RepoFile(string relativePath)
        {
            return Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string _repoRoot;

        private static string FindRepoRoot()
        {
            if (_repoRoot != null) return _repoRoot;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "instance", "tuning_defaults.json")))
                    return (_repoRoot = dir);
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("Could not locate instance/tuning_defaults.json from test output directory.");
        }
    }
}
