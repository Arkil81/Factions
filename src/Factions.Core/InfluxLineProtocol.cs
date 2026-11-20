using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// Builds InfluxDB line-protocol records.
    ///
    /// <para><b>Why this is in Core.</b> The escaping rules are the whole of the correctness here and
    /// they are pure text: a measurement escapes commas and spaces, a tag or field key also escapes
    /// <c>=</c>, a string field value is quoted and escapes <c>"</c> and <c>\</c> instead, and an
    /// empty tag value is not a tag at all — Influx rejects <c>tag=</c> and drops the whole batch
    /// with it. None of that needs a server, so none of it should only be testable against one.</para>
    ///
    /// <para><b>A batch is text, not a request.</b> This type never touches the network. The host
    /// owns transport, retry and the background thread; this owns what goes on the wire.</para>
    /// </summary>
    public sealed class InfluxLineProtocol
    {
        // Line protocol carries integers with a trailing 'i'. A double must not, and must never
        // arrive in exponent form or with a locale's decimal comma -- both parse as a syntax error
        // on the server, which rejects the entire batch rather than the line.
        private const string FloatFormat = "0.######";

        private readonly StringBuilder _sb = new StringBuilder(4096);
        private readonly List<KeyValuePair<string, string>> _tags =
            new List<KeyValuePair<string, string>>(8);

        private int _lineCount;

        /// <summary>Tags applied to every point built afterwards, cleared only by
        /// <see cref="Reset"/>. These identify the emitter — which box, which Torch root — and
        /// repeating them at each call site is how one of them ends up spelled differently.</summary>
        public InfluxLineProtocol CommonTag(string key, string value)
        {
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                _tags.Add(new KeyValuePair<string, string>(key, value));
            return this;
        }

        public int LineCount { get { return _lineCount; } }

        public bool IsEmpty { get { return _lineCount == 0; } }

        /// <summary>Starts a point. Call <see cref="Tag"/> zero or more times, then
        /// <see cref="Field"/> at least once, then <see cref="End"/>.
        /// <para>A point with no fields is a syntax error on the server, so <see cref="End"/>
        /// discards one rather than emitting it.</para></summary>
        public PointBuilder Point(string measurement, long timestampUnixNanos)
        {
            return new PointBuilder(this, measurement, timestampUnixNanos);
        }

        /// <summary>The batch as one newline-separated payload, or null when nothing was built.
        /// Null rather than empty on purpose: an empty POST body is a 400 from Influx, and a caller
        /// that tests for null cannot send one by accident.</summary>
        public string Build()
        {
            return _lineCount == 0 ? null : _sb.ToString();
        }

        /// <summary>Drops every built line. Common tags are dropped too — this returns the builder
        /// to the state it was constructed in, so a reused instance cannot silently accumulate a
        /// second copy of them.</summary>
        public void Reset()
        {
            _sb.Length = 0;
            _tags.Clear();
            _lineCount = 0;
        }

        /// <summary>Unix nanoseconds, which is line protocol's default precision.</summary>
        public static long ToUnixNanos(DateTime utc)
        {
            // DateTime.Ticks are 100 ns since year 1; the Unix epoch is 621355968000000000 of them.
            long ticks = utc.ToUniversalTime().Ticks - 621355968000000000L;
            return ticks * 100L;
        }

        /// <summary>One point under construction. A struct so that building a batch of a few hundred
        /// points on the game thread's flush allocates nothing per point.</summary>
        public struct PointBuilder
        {
            private readonly InfluxLineProtocol _owner;
            private readonly long _timestamp;
            private readonly int _startLength;
            private bool _anyField;

            internal PointBuilder(InfluxLineProtocol owner, string measurement, long timestamp)
            {
                _owner = owner;
                _timestamp = timestamp;
                _anyField = false;
                _startLength = owner._sb.Length;

                AppendEscaped(owner._sb, measurement, false);
                for (int i = 0; i < owner._tags.Count; i++)
                {
                    owner._sb.Append(',');
                    AppendEscaped(owner._sb, owner._tags[i].Key, true);
                    owner._sb.Append('=');
                    AppendEscaped(owner._sb, owner._tags[i].Value, true);
                }
            }

            /// <summary>Adds a tag. A null or empty value is skipped rather than written: Influx
            /// rejects an empty tag value and fails the whole batch, and "this dimension does not
            /// apply to this point" is the ordinary reason a caller has one.
            /// <para>Must precede every <see cref="Field"/> on the same point — line protocol puts
            /// all tags before the first field, and nothing here reorders them.</para></summary>
            public PointBuilder Tag(string key, string value)
            {
                if (_anyField) throw new InvalidOperationException("Tags must precede fields.");
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return this;
                StringBuilder sb = _owner._sb;
                sb.Append(',');
                AppendEscaped(sb, key, true);
                sb.Append('=');
                AppendEscaped(sb, value, true);
                return this;
            }

            /// <summary>A floating-point field. NaN and infinity are skipped — line protocol has no
            /// representation for either, and a single one rejects the batch it is in.</summary>
            public PointBuilder Field(string key, double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return this;
                BeginField(key);
                _owner._sb.Append(value.ToString(FloatFormat, CultureInfo.InvariantCulture));
                return this;
            }

            /// <summary>An integer field, written with line protocol's <c>i</c> suffix.
            /// <para>A field that is an integer in one point and a float in another is a type
            /// conflict on the server, which rejects the later write. Pick one per field name and
            /// keep it.</para></summary>
            public PointBuilder Field(string key, long value)
            {
                BeginField(key);
                _owner._sb.Append(value.ToString(CultureInfo.InvariantCulture)).Append('i');
                return this;
            }

            public PointBuilder Field(string key, bool value)
            {
                BeginField(key);
                _owner._sb.Append(value ? 'T' : 'F');
                return this;
            }

            /// <summary>A string field. Quoted rather than escaped like a tag, so a value with
            /// spaces or commas in it needs no caller-side treatment.</summary>
            public PointBuilder Field(string key, string value)
            {
                if (value == null) return this;
                BeginField(key);
                StringBuilder sb = _owner._sb;
                sb.Append('"');
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == '"' || c == '\\') sb.Append('\\');
                    // A newline inside a quoted field still terminates the line for the parser.
                    sb.Append(c == '\n' || c == '\r' ? ' ' : c);
                }
                sb.Append('"');
                return this;
            }

            /// <summary>Closes the point. A point that received no field is removed entirely rather
            /// than written: line protocol requires at least one, and a fieldless line rejects the
            /// batch around it.</summary>
            public void End()
            {
                StringBuilder sb = _owner._sb;
                if (!_anyField)
                {
                    sb.Length = _startLength;
                    return;
                }
                sb.Append(' ').Append(_timestamp.ToString(CultureInfo.InvariantCulture)).Append('\n');
                _owner._lineCount++;
            }

            private void BeginField(string key)
            {
                StringBuilder sb = _owner._sb;
                sb.Append(_anyField ? ',' : ' ');
                AppendEscaped(sb, key, true);
                sb.Append('=');
                _anyField = true;
            }
        }

        /// <summary>Escapes a measurement name, tag key, tag value or field key.
        /// <para><paramref name="escapeEquals"/> separates the two rules: a measurement escapes
        /// comma and space only, while everything else in the key position also escapes
        /// <c>=</c>.</para></summary>
        private static void AppendEscaped(StringBuilder sb, string value, bool escapeEquals)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ',' || c == ' ' || (escapeEquals && c == '=')) sb.Append('\\');
                // A literal newline would split one point into two unparseable halves.
                sb.Append(c == '\n' || c == '\r' ? '_' : c);
            }
        }
    }
}
