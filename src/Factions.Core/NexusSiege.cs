using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Fire a player put into a station on a Nexus agent since its last flush. Additive, so
    /// it is applied exactly once: numbered per agent session, re-sent until acknowledged.</summary>
    public sealed class NexusSiegeEvent
    {
        public long Seq;
        public string Node;
        /// <summary>Identity of the player whose fire it was.</summary>
        public long Attacker;
        /// <summary>The attacker's faction tag as the agent read it; the authority prefers its own.</summary>
        public string AttackerTag;
        /// <summary>Damage the station's safe zone absorbed.</summary>
        public double Zone;
        /// <summary>Damage that reached the station's blocks (its zone already down).</summary>
        public double Block;
        /// <summary>Whether the station had no working power producer at the flush.</summary>
        public bool PoweredDown;
        /// <summary>The attacker's reply to an offer (<see cref="NexusNotice.Offer"/>); no damage and
        /// no node. Numbered with the fire so it is applied once and in order.</summary>
        public NexusAnswer Answer;
    }

    public enum NexusAnswer
    {
        None = 0,
        Yes = 1,
        No = 2,
    }

    /// <summary>A standing station's safe zone as the authority's siege state has it. Only nodes off
    /// the default (on, untouched) are sent; an agent treats every other standing station as default.</summary>
    public sealed class NexusZoneState
    {
        public string Node;
        public bool Off;
        public double Fraction;

        /// <summary>The state to send for <paramref name="n"/>, or null when it is the default.</summary>
        public static NexusZoneState For(SpatialNode n, FactionTuning tuning, DateTime nowUtc)
        {
            if (n == null) return null;
            bool on = PlayerSiege.ZoneShouldBeEnabled(n, nowUtc);
            double fraction = PlayerSiege.IntegrityFraction(n, tuning, nowUtc);
            if (on && fraction >= 1.0) return null;
            return new NexusZoneState { Node = n.Id, Off = !on, Fraction = fraction };
        }
    }

    /// <summary>A siege notice for a player on an agent: chat, a HUD line, or both.</summary>
    public sealed class NexusNotice
    {
        /// <summary>Clock-seeded on the authority, so an agent's mark stays valid across its restarts.</summary>
        public long Seq;
        public long Identity;
        public string Text;
        public string Font;
        /// <summary>HUD notification length; 0 for none.</summary>
        public int HudMs;
        public bool Chat;
        /// <summary>A question: the agent takes this player's next yes or no from chat and sends it
        /// back as an <see cref="NexusSiegeEvent.Answer"/>.</summary>
        public bool Offer;
        /// <summary>Who the chat line is from; null for "Factions".</summary>
        public string Author;
    }

    /// <summary>
    /// The authority's high-water mark of siege events per agent. A new agent session starts the
    /// count again; an event at or below the mark is a re-send and is dropped.
    /// </summary>
    public sealed class NexusSiegeInbox
    {
        private sealed class Mark
        {
            public string Session;
            public long Through;
        }

        private readonly Dictionary<byte, Mark> _byHome = new Dictionary<byte, Mark>();

        /// <summary>The events of <paramref name="events"/> not yet applied, in order; <paramref name="through"/>
        /// is what to acknowledge (0 when the agent sent nothing and nothing is held for it).</summary>
        public List<NexusSiegeEvent> Accept(byte home, string session, IList<NexusSiegeEvent> events, out long through)
        {
            var fresh = new List<NexusSiegeEvent>();
            Mark m;
            if (!_byHome.TryGetValue(home, out m) || m.Session != session)
            {
                m = new Mark { Session = session };
                _byHome[home] = m;
            }
            if (events != null)
                for (int i = 0; i < events.Count; i++)
                {
                    NexusSiegeEvent e = events[i];
                    if (e == null || e.Seq <= m.Through) continue;
                    fresh.Add(e);
                    m.Through = e.Seq;
                }
            through = events != null && events.Count > 0 ? m.Through : 0;
            return fresh;
        }
    }

    /// <summary>
    /// Siege notices the authority owes players on agents, held per agent until its report says they
    /// were shown. A notice older than the hold limit is dropped unsent: a siege line read a minute
    /// late describes a state that has already moved. A new agent session drops what was held for the
    /// old one, whose players have reconnected since.
    /// </summary>
    public sealed class NexusNoticeOutbox
    {
        private sealed class Held
        {
            public NexusNotice Notice;
            public DateTime AddedUtc;
        }

        private sealed class Queue
        {
            public string Session;
            public readonly List<Held> Items = new List<Held>();
        }

        private readonly Dictionary<byte, Queue> _byHome = new Dictionary<byte, Queue>();
        private long _last;

        public void Add(byte home, NexusNotice notice, DateTime nowUtc)
        {
            if (notice == null) return;
            Queue q;
            if (!_byHome.TryGetValue(home, out q)) { q = new Queue(); _byHome[home] = q; }
            _last = Math.Max(_last + 1, nowUtc.Ticks);
            notice.Seq = _last;
            q.Items.Add(new Held { Notice = notice, AddedUtc = nowUtc });
        }

        /// <summary>What to send <paramref name="home"/> now, oldest first, after dropping what it
        /// acknowledged and what is older than <paramref name="maxAge"/>. Null when nothing is held.</summary>
        public List<NexusNotice> Pending(byte home, string session, long shownThrough, DateTime nowUtc, TimeSpan maxAge)
        {
            Queue q;
            if (!_byHome.TryGetValue(home, out q)) return null;
            if (q.Session != session)
            {
                if (q.Session != null) q.Items.Clear();
                q.Session = session;
            }
            q.Items.RemoveAll(h => h.Notice.Seq <= shownThrough || nowUtc - h.AddedUtc > maxAge);
            if (q.Items.Count == 0) return null;
            var list = new List<NexusNotice>(q.Items.Count);
            for (int i = 0; i < q.Items.Count; i++) list.Add(q.Items[i].Notice);
            return list;
        }
    }
}
