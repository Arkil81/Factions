using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Factions.Api.Services
{
    /// <summary>Follows an append-only JSON-lines file by byte offset, across the writer replacing
    /// it under the same name.
    /// <para>Two things the naive "seek to the offset I stopped at" loop gets wrong, both of which
    /// silently drop whole events:</para>
    /// <list type="bullet">
    ///   <item>A file swapped for a new one (<c>LiveEventWriter</c> rotates by rename once
    ///   <c>events.jsonl</c> passes its size trigger) is only recognised by a shrinking length while
    ///   the replacement is still smaller than the offset. If it has already grown past that offset,
    ///   seeking there lands in the middle of an unrelated line. The first bytes of the file are
    ///   therefore fingerprinted as well: they never change while a file is only appended to, and
    ///   always change when the file is replaced.</item>
    ///   <item>Reading to EOF while a line is still being written yields a truncated line, and the
    ///   remainder then arrives as a headless fragment — two unparseable messages instead of one
    ///   event. Only whole lines are returned; a trailing partial line is left for the next read.</item>
    ///   <item>Switching straight to the replacement abandons whatever the old file gained since the
    ///   last poll. That is not a rare race: the writer rotates from inside the very append that
    ///   pushes the file past its trigger, so the triggering line exists for under a millisecond
    ///   before the rename and is lost every single time (seen live 2026-08-22 — DVSR's snapshot,
    ///   written 1 ms before the rotation, never reached the dashboard). The renamed file is
    ///   therefore drained from the old offset first, and only then does the reader move to the
    ///   replacement.</item>
    /// </list></summary>
    public sealed class JsonlTailReader
    {
        private const int HeadFingerprintBytes = 64;
        private const int MaxBytesPerRead = 4 * 1024 * 1024;

        private readonly string _path;
        private readonly string _rotatedPath;
        private long _position;
        private byte[] _head = Array.Empty<byte>();

        /// <param name="startAtEnd">True to begin at the current end of an existing file, so a
        /// restart does not replay history.</param>
        /// <param name="rotatedPath">Where the writer renames the file when it rotates; defaults to
        /// <c>&lt;name&gt;.prev&lt;ext&gt;</c>, matching <c>LiveEventWriter</c>.</param>
        public JsonlTailReader(string path, bool startAtEnd, string rotatedPath = null)
        {
            _path = path;
            _rotatedPath = rotatedPath ?? DefaultRotatedPath(path);
            if (!startAtEnd || !File.Exists(path)) return;
            try
            {
                using var fs = Open(path);
                _position = fs.Length;
                _head = ReadHead(fs);
            }
            catch { /* fall back to reading from the start on the first poll */ }
        }

        /// <summary>Byte offset the next read resumes from — always on a line boundary.</summary>
        public long Position { get { return _position; } }

        /// <summary>Every complete line appended since the last call, in file order. Empty when
        /// there is nothing new, the file is gone, or only a partial line has landed so far.</summary>
        public List<string> ReadNewLines()
        {
            var lines = new List<string>();
            if (!File.Exists(_path))
            {
                _position = 0;
                _head = Array.Empty<byte>();
                return lines;
            }

            using (var fs = Open(_path))
            {
                long length = fs.Length;
                byte[] head = ReadHead(fs);
                bool rotated = length < _position || !ContinuesSameFile(_head, head);
                if (rotated)
                {
                    DrainRotatedFile(lines);
                    _position = 0;
                }
                _head = head;
                if (length > _position)
                {
                    long consumed;
                    AppendCompleteLines(fs, _position, lines, out consumed);
                    _position += consumed;
                }
            }
            return lines;
        }

        /// <summary>Reads whatever the file we were following gained between our last read and the
        /// writer renaming it. Skipped unless the renamed file really is that file — the same head
        /// fingerprint the rotation check uses — so an unrelated or already-drained predecessor is
        /// never replayed.</summary>
        private void DrainRotatedFile(List<string> lines)
        {
            if (_head.Length == 0) return;
            if (string.IsNullOrEmpty(_rotatedPath) || !File.Exists(_rotatedPath)) return;
            try
            {
                using var fs = Open(_rotatedPath);
                if (fs.Length <= _position) return;
                if (!ContinuesSameFile(_head, ReadHead(fs))) return;
                long consumed;
                AppendCompleteLines(fs, _position, lines, out consumed);
            }
            catch { /* best-effort: the replacement's own lines still get through */ }
        }

        /// <summary>Appends every whole line between <paramref name="from"/> and the end of
        /// <paramref name="fs"/>, reporting how many bytes that consumed. A trailing partial line
        /// consumes nothing, so the next read picks it up once its newline lands.</summary>
        private static void AppendCompleteLines(FileStream fs, long from, List<string> lines, out long consumed)
        {
            consumed = 0;
            int toRead = (int)Math.Min(fs.Length - from, MaxBytesPerRead);
            if (toRead <= 0) return;

            var buffer = new byte[toRead];
            fs.Seek(from, SeekOrigin.Begin);
            int got = 0;
            while (got < toRead)
            {
                int n = fs.Read(buffer, got, toRead - got);
                if (n <= 0) break;
                got += n;
            }
            if (got <= 0) return;

            int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', got - 1);
            if (lastNewline < 0) return; // a single line still mid-write; wait for its newline

            // Files written before LiveEventWriter dropped the UTF-8 preamble still carry one, and a
            // BOM in front of the first line is not parseable JSON on the dashboard side.
            int start = 0;
            if (from == 0 && got >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                start = 3;

            string text = Encoding.UTF8.GetString(buffer, start, lastNewline + 1 - start);
            foreach (string line in text.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);

            consumed = lastNewline + 1;
        }

        private static string DefaultRotatedPath(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path);
            return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
        }

        // FileShare.Delete matters on Windows: without it, holding this handle open makes the
        // writer's rotating rename fail with a sharing violation.
        private static FileStream Open(string path)
            => new FileStream(path, FileMode.Open, FileAccess.Read,
                              FileShare.ReadWrite | FileShare.Delete);

        private static byte[] ReadHead(FileStream fs)
        {
            int want = (int)Math.Min(fs.Length, HeadFingerprintBytes);
            if (want <= 0) return Array.Empty<byte>();
            var head = new byte[want];
            fs.Seek(0, SeekOrigin.Begin);
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(head, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == want) return head;
            var shorter = new byte[got];
            Array.Copy(head, shorter, got);
            return shorter;
        }

        /// <summary>True when <paramref name="current"/> can still be the file <paramref name="previous"/>
        /// was taken from — i.e. the earlier fingerprint is a prefix of the current one, which is all
        /// appending can do to it.</summary>
        private static bool ContinuesSameFile(byte[] previous, byte[] current)
        {
            if (previous.Length == 0) return true;
            if (current.Length < previous.Length) return false;
            for (int i = 0; i < previous.Length; i++)
                if (previous[i] != current[i]) return false;
            return true;
        }
    }
}
