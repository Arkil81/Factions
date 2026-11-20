using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class DataDirectoryTests
    {
        private static readonly string InstanceDir = Path.Combine(Path.GetTempPath(), "instance");

        [Fact]
        public void UnsetKeepsTheInstanceDirectory()
        {
            Assert.Equal(InstanceDir, DataDirectory.Resolve(null, InstanceDir));
            Assert.Equal(InstanceDir, DataDirectory.Resolve("", InstanceDir));
            Assert.Equal(InstanceDir, DataDirectory.Resolve("   ", InstanceDir));
        }

        [Fact]
        public void UnsetIsNotRedirected()
        {
            // Drives whether an unusable directory is a config error or just an absent setting.
            Assert.False(DataDirectory.IsRedirected(null));
            Assert.False(DataDirectory.IsRedirected(""));
            Assert.False(DataDirectory.IsRedirected("  "));
            Assert.True(DataDirectory.IsRedirected("shared"));
        }

        [Fact]
        public void RootedPathIsTakenAsGiven()
        {
            // Path.GetTempPath is rooted on both Linux and Windows, so this stays platform-neutral.
            string shared = Path.Combine(Path.GetTempPath(), "awl-shared");
            Assert.Equal(shared, DataDirectory.Resolve(shared, InstanceDir));
        }

        [Fact]
        public void RelativePathResolvesAgainstTheInstanceDirectory()
        {
            Assert.Equal(Path.Combine(InstanceDir, "shared"),
                         DataDirectory.Resolve("shared", InstanceDir));
        }

        [Fact]
        public void SurroundingWhitespaceIsIgnored()
        {
            // A hand-edited tuning.json is the expected source of these values.
            Assert.Equal(Path.Combine(InstanceDir, "shared"),
                         DataDirectory.Resolve("  shared  ", InstanceDir));
        }

        [Fact]
        public void DefaultTuningKeepsTheInstanceDirectory()
        {
            // The field must be additive: a tuning.json without the key changes nothing.
            var tuning = new FactionTuning();
            Assert.False(DataDirectory.IsRedirected(tuning.DataDirectory));
            Assert.Equal(InstanceDir, DataDirectory.Resolve(tuning.DataDirectory, InstanceDir));
        }

        // ---- ResolveInput: read-only inputs may fall back to the instance dir, state may not ----

        private static readonly string DataDir = Path.Combine(Path.GetTempPath(), "shared");

        private static Func<string, bool> Present(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        // ---- DescribeDivergence: two copies that disagree are named, anything else is silent ----

        private static readonly DateTime DataMtime = new DateTime(2026, 8, 22, 8, 46, 0, DateTimeKind.Utc);
        private static readonly DateTime InstanceMtime = new DateTime(2026, 9, 3, 10, 40, 0, DateTimeKind.Utc);

        private static string Divergence(string fileName, string dataDir, string instanceDir,
            byte[] dataBytes, byte[] instanceBytes)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string dataPath = dataDir == null ? null : Path.Combine(dataDir, fileName);
            if (dataBytes != null) files[dataPath] = dataBytes;
            if (instanceBytes != null) files[Path.Combine(instanceDir, fileName)] = instanceBytes;
            return DataDirectory.DescribeDivergence(fileName, dataDir, instanceDir,
                files.ContainsKey, p => files[p],
                p => string.Equals(p, dataPath, StringComparison.OrdinalIgnoreCase) ? DataMtime : InstanceMtime);
        }

        [Fact]
        public void DifferingCopiesNameTheOneReadAndTheOneIgnored()
        {
            // The live failure: a stale data-dir copy wins and the deploy's fresh instance copy is
            // never read. The line must say which is which, with what distinguishes them.
            string line = Divergence("station_name_additions.json", DataDir, InstanceDir,
                new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 4, 5 });

            Assert.NotNull(line);
            Assert.StartsWith("INPUT_DIVERGED file=station_name_additions.json ", line);
            Assert.Contains(" read=" + Path.Combine(DataDir, "station_name_additions.json")
                          + " read_mtime=2026-08-22T08:46:00Z read_bytes=3 ", line);
            Assert.EndsWith(" ignored=" + Path.Combine(InstanceDir, "station_name_additions.json")
                          + " ignored_mtime=2026-09-03T10:40:00Z ignored_bytes=5", line);
        }

        [Fact]
        public void SameLengthDifferentContentIsStillADivergence()
        {
            // A size check alone would pass a one-word edit; the comparison is byte by byte.
            Assert.NotNull(Divergence("capability_recipes.json", DataDir, InstanceDir,
                new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3 }));
        }

        [Fact]
        public void IdenticalCopiesAreSilentWhateverTheirTimestamps()
        {
            // The live fleet_prefabs.json pair: different mtimes, byte-identical. Not a divergence.
            Assert.Null(Divergence("fleet_prefabs.json", DataDir, InstanceDir,
                new byte[] { 7, 7, 7 }, new byte[] { 7, 7, 7 }));
        }

        [Fact]
        public void ASingleCopyIsSilent()
        {
            // One copy cannot disagree with anything; ResolveInput already picks the only one.
            Assert.Null(Divergence("world_features.json", DataDir, InstanceDir, new byte[] { 1 }, null));
            Assert.Null(Divergence("faction_recipe.json", DataDir, InstanceDir, null, new byte[] { 1 }));
            Assert.Null(Divergence("world_recipe.json", DataDir, InstanceDir, null, null));
        }

        [Fact]
        public void AnUnredirectedDataDirectoryIsSilent()
        {
            // Data dir == instance dir means one file seen twice, and must not compare it with itself.
            Assert.Null(Divergence("fleet_prefabs.json", InstanceDir, InstanceDir,
                new byte[] { 1 }, new byte[] { 2 }));
            Assert.Null(Divergence("fleet_prefabs.json", InstanceDir.ToUpperInvariant(), InstanceDir,
                new byte[] { 1 }, new byte[] { 2 }));
            Assert.Null(Divergence("fleet_prefabs.json", DataDir, null, new byte[] { 1 }, null));
            Assert.Null(Divergence("fleet_prefabs.json", null, InstanceDir, null, new byte[] { 1 }));
        }

        [Fact]
        public void TheCopyNamedAsReadIsTheOneResolveInputReturns()
        {
            // The line must follow precedence rather than restate it, or it names the wrong file
            // the day precedence changes.
            string fileName = "capability_recipes.json";
            string dataPath = Path.Combine(DataDir, fileName);
            string instancePath = Path.Combine(InstanceDir, fileName);
            bool fellBack;
            string resolved = DataDirectory.ResolveInput(fileName, DataDir, InstanceDir,
                Present(dataPath, instancePath), out fellBack);

            string line = Divergence(fileName, DataDir, InstanceDir, new byte[] { 1 }, new byte[] { 2 });
            Assert.Contains(" read=" + resolved + " ", line);
        }

        [Fact]
        public void InputPrefersTheDataDirectoryWhenItIsThere()
        {
            string want = Path.Combine(DataDir, "fleet_prefabs.json");
            bool fellBack;
            Assert.Equal(want, DataDirectory.ResolveInput(
                "fleet_prefabs.json", DataDir, InstanceDir,
                Present(want, Path.Combine(InstanceDir, "fleet_prefabs.json")), out fellBack));
            Assert.False(fellBack);
        }

        [Fact]
        public void InputFallsBackToTheInstanceCopy()
        {
            // The case that bites on a first redirect: a deploy wrote it to the instance dir and
            // the shared directory is still empty.
            string instanceCopy = Path.Combine(InstanceDir, "station_name_additions.json");
            bool fellBack;
            Assert.Equal(instanceCopy, DataDirectory.ResolveInput(
                "station_name_additions.json", DataDir, InstanceDir, Present(instanceCopy), out fellBack));
            Assert.True(fellBack);
        }

        [Fact]
        public void MissingEverywhereNamesTheDataDirectory()
        {
            // So a "not found" message points at where the file should go, not where it isn't.
            bool fellBack;
            Assert.Equal(Path.Combine(DataDir, "capability_recipes.json"), DataDirectory.ResolveInput(
                "capability_recipes.json", DataDir, InstanceDir, Present(), out fellBack));
            Assert.False(fellBack);
        }

        [Fact]
        public void NoRedirectMeansOneLocationAndNoFallback()
        {
            // dataDir == instanceDir is the standalone case; there is nowhere else to look.
            bool fellBack;
            string p = DataDirectory.ResolveInput("fleet_prefabs.json", InstanceDir, InstanceDir,
                Present(Path.Combine(InstanceDir, "fleet_prefabs.json")), out fellBack);
            Assert.Equal(Path.Combine(InstanceDir, "fleet_prefabs.json"), p);
            Assert.False(fellBack);
        }

        [Fact]
        public void StateIsNotResolvedThroughThisAtAll()
        {
            // Guards the rule rather than the code: brains and the index must come from the data
            // directory alone. A fallback there would resurrect a stale local world beside the
            // shared one. FactionStore composes its own paths from BaseDir and never calls this.
            string brainInShared = Path.Combine(DataDir, "brains", "IGAT.json");
            string brainInInstance = Path.Combine(InstanceDir, "brains", "IGAT.json");
            bool fellBack;
            string resolved = DataDirectory.ResolveInput(Path.Combine("brains", "IGAT.json"),
                DataDir, InstanceDir, Present(brainInInstance), out fellBack);
            // It *would* fall back if anyone wired it up -- which is exactly why they must not.
            Assert.Equal(brainInInstance, resolved);
            Assert.True(fellBack);
            Assert.NotEqual(brainInShared, resolved);
        }

        [Fact]
        public void AReadWriteStoreMustNotUseThisAtAll()
        {
            // custom_prefabs looked like an input and is not: CustomPrefabStore keeps the directory
            // it was loaded from and writes captures back into it. Resolving it here would send new
            // captures to the instance dir, and keep doing so, because the fallback is chosen
            // exactly when the shared folder is missing. The rule is "read once at load and never
            // written back" -- this asserts what the helper would wrongly do, so the reason the call
            // site avoids it stays visible.
            string localCaptures = Path.Combine(InstanceDir, "custom_prefabs");
            bool fellBack;
            string resolved = DataDirectory.ResolveInput("custom_prefabs", DataDir, InstanceDir,
                Present(localCaptures), out fellBack);
            Assert.Equal(localCaptures, resolved);
            Assert.True(fellBack);
            Assert.NotEqual(Path.Combine(DataDir, "custom_prefabs"), resolved);
        }

        [Fact]
        public void ANullExistsCheckTakesTheDataDirectory()
        {
            bool fellBack;
            Assert.Equal(Path.Combine(DataDir, "x.json"),
                DataDirectory.ResolveInput("x.json", DataDir, InstanceDir, null, out fellBack));
            Assert.False(fellBack);
        }

        // ---- FAC-086 guards: the two alarms, and what each cannot see ----------------------------

        [Fact]
        public void RegistrySwap_FiresOnlyWhenTheSharedRegistryIsEmptyAndLocalIsNot()
        {
            // The exact accident: pointed at a directory whose registry is empty while this
            // instance still holds a real one.
            Assert.True(DataDirectory.RegistrySwapLooksWrong(true, 0, 48));
        }

        [Fact]
        public void RegistrySwap_StaysSilentOnTheNormalSteadyState()
        {
            // After a correct cutover the data dir is populated and the instance copy is the ghost.
            // Warning here would fire on every healthy boot.
            Assert.False(DataDirectory.RegistrySwapLooksWrong(true, 48, 0));
            Assert.False(DataDirectory.RegistrySwapLooksWrong(true, 48, 48));
        }

        [Fact]
        public void RegistrySwap_CannotSeeTheCaseThatActuallyHappened()
        {
            // Both empty is a world before `factions init` -- and it is also what a silent revert
            // looks like at the moment it happens. This guard is blind to it by design, which is
            // why RedirectChanged exists. Asserting the blindness so nobody "fixes" it into a
            // false alarm on every fresh world.
            Assert.False(DataDirectory.RegistrySwapLooksWrong(true, 0, 0));
        }

        [Fact]
        public void RegistrySwap_IsMeaninglessWithoutARedirect()
        {
            // Not redirected means both counts came from the same file.
            Assert.False(DataDirectory.RegistrySwapLooksWrong(false, 0, 48));
        }

        [Fact]
        public void RedirectChanged_FiresOnASilentRevertToTheInstanceDirectory()
        {
            // The observed failure: tuning.json regenerated, DataDirectory came back empty, so the
            // boot resolved to the instance dir while the world's state sits in the shared one.
            Assert.True(DataDirectory.RedirectChanged(
                @"C:\Torch\awl-shared\Factions", @"C:\Torch\awl-standalone-test\Instance\Factions"));
        }

        [Fact]
        public void RedirectChanged_IsSilentWhenNothingMoved()
        {
            Assert.False(DataDirectory.RedirectChanged(
                @"C:\Torch\awl-shared\Factions", @"C:\Torch\awl-shared\Factions"));
        }

        [Fact]
        public void RedirectChanged_TreatsAFirstRunAsNoChange()
        {
            // No marker yet: a fresh instance must not greet its first boot with an alarm.
            Assert.False(DataDirectory.RedirectChanged(null, @"C:\Torch\awl-shared\Factions"));
            Assert.False(DataDirectory.RedirectChanged("", @"C:\Torch\awl-shared\Factions"));
        }

        [Fact]
        public void RedirectChanged_IgnoresTrailingSeparatorAndCase()
        {
            // A path that differs only in spelling is not a move, and warning on it would train
            // people to ignore the alarm.
            Assert.False(DataDirectory.RedirectChanged(
                @"C:\Torch\awl-shared\Factions\", @"c:\torch\awl-shared\factions"));
        }

        // ---- PeekDataDirectory: Init finds the shared folder without loading tuning ----

        private static string PeekIn(string defaultsJson, string tuningJson, out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "factions-test-peek-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            if (defaultsJson != null) File.WriteAllText(Path.Combine(dir, "tuning_defaults.json"), defaultsJson);
            if (tuningJson != null) File.WriteAllText(Path.Combine(dir, "tuning.json"), tuningJson);
            return new FactionStore(dir).PeekDataDirectory();
        }

        private static void Remove(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void PeekReadsTheDataDirectoryFromTuning()
        {
            string dir = null;
            try
            {
                Assert.Equal(@"C:\Torch\awl-shared\Factions", PeekIn("{ \"TuneVersion\": 60 }",
                    "{ \"DataDirectory\": \"C:\\\\Torch\\\\awl-shared\\\\Factions\" }", out dir));
            }
            finally { Remove(dir); }
        }

        [Fact]
        public void PeekAgreesWithLoadTuningOnTheSameFiles()
        {
            // The whole point: Init and session load must land on the same folder, or the ore map
            // comes from one place and every other input from another.
            string dir = null;
            try
            {
                string peeked = PeekIn("{ \"DataDirectory\": \"from-defaults\" }",
                    "{ \"DataDirectory\": \"from-tuning\" }", out dir);
                var loaded = new FactionTuning();
                new FactionStore(dir).LoadTuning(loaded);
                Assert.Equal("from-tuning", peeked);
                Assert.Equal(loaded.DataDirectory, peeked);
            }
            finally { Remove(dir); }
        }

        [Fact]
        public void PeekWithoutTheKeyIsTheInstanceDirectory()
        {
            // An unset key resolves to the instance directory -- the pre-cluster layout, unchanged.
            string dir = null;
            try
            {
                string peeked = PeekIn("{ \"TuneVersion\": 60 }", "{ \"TuneVersion\": 60 }", out dir);
                Assert.Equal("", peeked);
                Assert.Equal(InstanceDir, DataDirectory.Resolve(peeked, InstanceDir));
            }
            finally { Remove(dir); }
        }

        [Fact]
        public void PeekSurvivesMissingAndUnreadableFiles()
        {
            string dir = null;
            try
            {
                Assert.Equal("", PeekIn(null, null, out dir));
                Remove(dir);
                // A broken tuning.json keeps the defaults layer, as LoadTuning does.
                Assert.Equal("from-defaults", PeekIn("{ \"DataDirectory\": \"from-defaults\" }", "{ not json", out dir));
            }
            finally { Remove(dir); }
        }

        [Fact]
        public void PeekWritesNothing()
        {
            // LoadTuning writes tuning.json back; a peek at Init must not, or it would author a
            // file before the session's own load decides what goes in it.
            string dir = null;
            try
            {
                string tuning = "{ \"DataDirectory\": \"shared\" }";
                PeekIn(null, tuning, out dir);
                Assert.Equal(tuning, File.ReadAllText(Path.Combine(dir, "tuning.json")));
                Assert.False(File.Exists(Path.Combine(dir, "tuning_defaults.json")));
                Assert.Equal(1, Directory.GetFiles(dir).Length);
            }
            finally { Remove(dir); }
        }

        // ---- DescribeRead: the boot line naming which copy of an input was read ----

        [Fact]
        public void ReadLineNamesThePathItsAgeAndSize()
        {
            string path = Path.Combine(DataDir, "faction_recipe.json");
            string line = DataDirectory.DescribeRead("faction_recipe.json", path,
                Present(path), p => DataMtime, p => 24665L);
            Assert.Equal("INPUT_READ file=faction_recipe.json read=" + path
                       + " mtime=2026-08-22T08:46:00Z bytes=24665", line);
        }

        [Fact]
        public void ReadLineIsSilentForAnAbsentFile()
        {
            // The loaders already report absence, each in its own words; a second line would only
            // repeat it without the consequence.
            string path = Path.Combine(DataDir, "world_recipe.json");
            Assert.Null(DataDirectory.DescribeRead("world_recipe.json", path, Present(),
                p => DataMtime, p => 1L));
            Assert.Null(DataDirectory.DescribeRead("world_recipe.json", null, Present(),
                p => DataMtime, p => 1L));
        }
    }
}
