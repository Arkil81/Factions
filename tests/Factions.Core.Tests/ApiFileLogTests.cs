using System;
using System.IO;
using Factions.Api.Services;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The dashboard service's process log: day rolling, retention by file-name date, and
    /// that a write which cannot land is dropped rather than thrown.</summary>
    public class ApiFileLogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "apifilelog-" + Guid.NewGuid().ToString("N"));
        private DateTime _now = new DateTime(2026, 10, 8, 23, 57, 55, DateTimeKind.Local);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private ApiFileLog Log() => new ApiFileLog(_dir, () => _now);

        [Fact]
        public void WriteCreatesTheDirectoryAndAppendsOneLinePerCall()
        {
            ApiFileLog log = Log();
            log.Info("API_STOPPING wsClients=1");
            log.Error("API_UNHANDLED terminating=True boom");

            string[] lines = File.ReadAllLines(Path.Combine(_dir, "Factions.Api-2026-10-08.log"));
            Assert.Equal(2, lines.Length);
            Assert.Equal("23:57:55.0000 [INFO] API_STOPPING wsClients=1", lines[0]);
            Assert.Equal("23:57:55.0000 [ERROR] API_UNHANDLED terminating=True boom", lines[1]);
        }

        [Fact]
        public void LinesRollToTheFileOfTheirLocalDay()
        {
            ApiFileLog log = Log();
            log.Info("before midnight");
            _now = _now.AddMinutes(5);
            log.Info("after midnight");

            Assert.Single(File.ReadAllLines(log.PathFor(new DateTime(2026, 10, 8))));
            Assert.Single(File.ReadAllLines(log.PathFor(new DateTime(2026, 10, 9))));
        }

        [Fact]
        public void PruneDeletesOnlyDayFilesOlderThanTheWindow()
        {
            Directory.CreateDirectory(_dir);
            ApiFileLog log = Log();
            string old = log.PathFor(new DateTime(2026, 9, 23));     // 15 days back
            string edge = log.PathFor(new DateTime(2026, 9, 24));    // exactly 14 days back
            string today = log.PathFor(new DateTime(2026, 10, 8));
            string foreign = Path.Combine(_dir, "Factions.Api-notadate.log");
            string other = Path.Combine(_dir, "something-else.log");
            foreach (string p in new[] { old, edge, today, foreign, other }) File.WriteAllText(p, "x");

            int deleted = log.Prune(keepDays: 14);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(edge));
            Assert.True(File.Exists(today));
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(other));
        }

        [Fact]
        public void PruneOfAMissingDirectoryDeletesNothing()
        {
            Assert.Equal(0, Log().Prune(keepDays: 14));
        }

        [Fact]
        public void AWriteThatCannotLandIsDroppedNotThrown()
        {
            // The log directory's path is taken by a file, so neither CreateDirectory nor the open
            // can succeed.
            File.WriteAllText(_dir, "a file where the directory should be");
            try
            {
                ApiFileLog log = Log();
                log.Error("API_UNHANDLED terminating=True boom");
                Assert.Equal(0, log.Prune(keepDays: 14));
                Assert.True(File.Exists(_dir));
            }
            finally
            {
                File.Delete(_dir);
            }
        }
    }
}
