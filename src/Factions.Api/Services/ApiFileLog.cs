// Explicit, because this file is also source-included by Factions.Core.Tests, which
// disables nullable reference types project-wide.
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Factions.Api.Services
{
    /// <summary>Day-rolling text log for the dashboard service's own lifecycle: start, the host's
    /// stop phases, process exit, unhandled exceptions, and the framework's warnings and errors.
    ///
    /// <para>The service otherwise writes only to the console, which a Windows service does not
    /// have, and to the Application event log, which the deploying account cannot read. A crash
    /// therefore left a WER report and nothing anyone could open. This file sits next to the
    /// binaries (<c>logs\Factions.Api-YYYY-MM-DD.log</c>), which a publish copies over but never
    /// deletes.</para>
    ///
    /// <para>Every write opens, appends and closes, so a line is on disk before the next statement
    /// runs: the process may be torn down immediately after an unhandled-exception line. A failed
    /// write is dropped — logging must never be the thing that crashes the service.</para></summary>
    public sealed class ApiFileLog
    {
        public const string FilePrefix = "Factions.Api-";
        public const string FileSuffix = ".log";

        private readonly string _dir;
        private readonly Func<DateTime> _localNow;
        private readonly object _lock = new();

        /// <param name="dir">Directory the day files are written to; created on first write.</param>
        /// <param name="localNow">Local-time clock. Local, to line up with Torch's own logs on the
        /// same box.</param>
        public ApiFileLog(string dir, Func<DateTime>? localNow = null)
        {
            _dir = dir;
            _localNow = localNow ?? (() => DateTime.Now);
        }

        public string Directory => _dir;

        public string PathFor(DateTime localDay)
            => Path.Combine(_dir, FilePrefix + localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileSuffix);

        public void Info(string line) => Write("INFO", line);
        public void Warn(string line) => Write("WARN", line);
        public void Error(string line) => Write("ERROR", line);

        public void Write(string level, string line)
        {
            try
            {
                DateTime now = _localNow();
                string text = now.ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture)
                              + " [" + level + "] " + line + Environment.NewLine;
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                lock (_lock)
                {
                    System.IO.Directory.CreateDirectory(_dir);
                    // ReadWrite|Delete sharing: an outgoing and an incoming process may overlap by
                    // a moment during a deploy, and a reader tailing the file must not block either.
                    using var fs = new FileStream(PathFor(now), FileMode.Append, FileAccess.Write,
                                                  FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // Dropped: see the class summary.
            }
        }

        /// <summary>Deletes this log's day files older than <paramref name="keepDays"/> days, by the
        /// date in the file name. Files that do not match the name pattern are left alone.
        /// Returns how many were deleted.</summary>
        public int Prune(int keepDays)
        {
            int deleted = 0;
            try
            {
                if (!System.IO.Directory.Exists(_dir)) return 0;
                DateTime cutoff = _localNow().Date.AddDays(-keepDays);
                foreach (string path in System.IO.Directory.GetFiles(_dir, FilePrefix + "*" + FileSuffix))
                {
                    string name = Path.GetFileName(path);
                    string stamp = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length);
                    if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out DateTime day))
                        continue;
                    if (day >= cutoff) continue;
                    try { File.Delete(path); deleted++; }
                    catch { /* in use or not ours to delete; the next start tries again */ }
                }
            }
            catch
            {
                // Dropped: see the class summary.
            }
            return deleted;
        }
    }
}
