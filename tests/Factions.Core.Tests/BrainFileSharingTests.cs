using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-371: a brain write must survive another process reading the same file, and a
    /// reader must not deny the plugin's write. File sharing is only enforced on Windows, so the
    /// tests that hold a conflicting handle return early elsewhere; the Windows run is the one that
    /// proves them.</summary>
    public class BrainFileSharingTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static bool SharingEnforced
        {
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        private string BrainFile(string tag)
        {
            return Path.Combine(_dir, "brains", tag + ".json");
        }

        [Fact]
        public void SaveBrain_ReplacesAnExistingFile_AndLeavesNoTempBehind()
        {
            var store = new FactionStore(_dir);
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 1 });
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 2 });

            Assert.Equal(2, store.LoadBrain("ALFA").Treasury);
            Assert.Equal(new[] { BrainFile("ALFA") }, Directory.GetFiles(Path.Combine(_dir, "brains")));
        }

        [Fact]
        public void LoadBrain_ReadsWhileAWriterHoldsTheFile()
        {
            if (!SharingEnforced) return;
            var store = new FactionStore(_dir);
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 7 });

            // The handle File.WriteAllText takes. File.ReadAllText's Read-only share refuses it.
            using (new FileStream(BrainFile("ALFA"), FileMode.Open, FileAccess.Write, FileShare.Read))
                Assert.Equal(7, store.LoadBrain("ALFA").Treasury);
        }

        [Fact]
        public void SaveBrain_IsNotBlockedByASharedReader()
        {
            if (!SharingEnforced) return;
            var store = new FactionStore(_dir);
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 1 });

            // The handle ReadShared takes, held across the write.
            using (new FileStream(BrainFile("ALFA"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 2 }, 0);

            Assert.Equal(2, store.LoadBrain("ALFA").Treasury);
        }

        [Fact]
        public void SaveBrain_WithoutRetries_ThrowsWhileAnUnsharingReaderHoldsTheFile()
        {
            if (!SharingEnforced) return;
            var store = new FactionStore(_dir);
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 1 });

            // File.ReadAllText's handle: the reader that lost the 2026-09-21 session load.
            using (new FileStream(BrainFile("ALFA"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception ex = Record.Exception(() => store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 2 }, 0));
                Assert.NotNull(ex);
                Assert.True(FactionStore.IsRefusedWrite(ex), ex.GetType().Name + ": " + ex.Message);
            }
            Assert.Equal(1, store.LoadBrain("ALFA").Treasury);
        }

        [Fact]
        public void SaveBrain_WithRetries_WritesOnceAnUnsharingReaderLetsGo()
        {
            if (!SharingEnforced) return;
            var store = new FactionStore(_dir);
            store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 1 });

            // A dedicated thread, not the pool: under the parallel suite a pool task can start after
            // a short retry budget has run out. The budget is generous for the same reason; what is
            // under test is that the write outlasts the reader, not the production count of three.
            var held = new FileStream(BrainFile("ALFA"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var writing = new ManualResetEventSlim(false);
            var release = new Thread(() =>
            {
                writing.Wait();
                Thread.Sleep(FactionStore.BrainWriteRetryDelayMs + 20);
                held.Dispose();
            });
            release.Start();
            try
            {
                writing.Set();
                store.SaveBrain(new BrainDoc { Tag = "ALFA", Treasury = 2 }, 40);
            }
            finally
            {
                release.Join();
                held.Dispose();
            }
            Assert.Equal(2, store.LoadBrain("ALFA").Treasury);
        }

        [Fact]
        public void IsRefusedWrite_ExcludesFailuresARetryCannotFix()
        {
            Assert.True(FactionStore.IsRefusedWrite(new IOException("sharing violation")));
            Assert.True(FactionStore.IsRefusedWrite(new UnauthorizedAccessException()));
            Assert.False(FactionStore.IsRefusedWrite(new FileNotFoundException()));
            Assert.False(FactionStore.IsRefusedWrite(new DirectoryNotFoundException()));
            Assert.False(FactionStore.IsRefusedWrite(new InvalidOperationException()));
        }
    }
}
