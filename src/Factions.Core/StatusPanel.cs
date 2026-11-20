using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How a status panel item reads at a glance. The client maps each to a colour; the
    /// ordinals travel on the wire as a byte and must not be renumbered.</summary>
    public enum StatusPanelTone : byte
    {
        /// <summary>Nothing to act on yet.</summary>
        Info = 0,
        /// <summary>Under way: the player is doing the right thing and has to keep doing it.</summary>
        Pending = 1,
        /// <summary>The player can act now.</summary>
        Ready = 2,
        /// <summary>About to be lost.</summary>
        Danger = 3,
    }

    /// <summary>One line of a status panel item. <see cref="StatusPanelItem.ClockToken"/> in the text
    /// is replaced by the time left until <see cref="CountdownTo"/>, which the client counts down on
    /// its own: the server sends the line again only when its text or its deadline moves.</summary>
    public sealed class StatusPanelLine
    {
        public string Text;
        /// <summary>default(DateTime) when the line has no countdown.</summary>
        public DateTime CountdownTo;

        public StatusPanelLine(string text, DateTime countdownTo = default(DateTime))
        {
            Text = text;
            CountdownTo = countdownTo;
        }
    }

    /// <summary>
    /// One situation shown in the client's HUD status panel: a heading and a few short lines. Built in
    /// Core beside the clocks it reads (<see cref="PlayerSiegeNotice"/>), so the panel and the chat
    /// notices word the same state the same way.
    ///
    /// <para><b>Why deadlines and not seconds.</b> A countdown written into the text changes every
    /// second, so "send on change" would send every second. An absolute deadline changes only when the
    /// state does; the host converts it to seconds-left at send time, which keeps the client's clock
    /// out of it.</para>
    /// </summary>
    public sealed class StatusPanelItem
    {
        /// <summary>Replaced on the client by m:ss until the line's deadline.</summary>
        public const string ClockToken = "{clock}";

        /// <summary>Stable per situation, e.g. "siege:N098". Two items with one key are one item.</summary>
        public string Key;
        public StatusPanelTone Tone;
        public readonly List<StatusPanelLine> Lines = new List<StatusPanelLine>();

        public StatusPanelItem(string key, StatusPanelTone tone)
        {
            Key = key;
            Tone = tone;
        }

        public StatusPanelItem Add(string text, DateTime countdownTo = default(DateTime))
        {
            Lines.Add(new StatusPanelLine(text, countdownTo));
            return this;
        }

        /// <summary>A digest of what the player would see, deadlines rounded to the second. Equal
        /// digests mean there is nothing new to send.</summary>
        public long Digest()
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = Mix(h, Key);
                h = (h ^ (byte)Tone) * 1099511628211L;
                for (int i = 0; i < Lines.Count; i++)
                {
                    h = Mix(h, Lines[i].Text);
                    long seconds = Lines[i].CountdownTo == default(DateTime)
                        ? 0L
                        : Lines[i].CountdownTo.Ticks / TimeSpan.TicksPerSecond;
                    h = (h ^ seconds) * 1099511628211L;
                }
                return h;
            }
        }

        private static long Mix(long h, string s)
        {
            unchecked
            {
                if (s == null) return (h ^ 0x5bd1e995L) * 1099511628211L;
                for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 1099511628211L;
                return (h ^ 0xffL) * 1099511628211L;
            }
        }
    }
}
