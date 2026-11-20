using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How a faction greets a ship it detects in its space. Set by the relation alone, so the
    /// same faction welcomes a friend and warns off an enemy in the same minute.</summary>
    public enum HailTone
    {
        /// <summary>No hail: the pilot belongs to the faction whose space it is.</summary>
        None = 0,
        Welcome = 1,
        Notice = 2,
        Warning = 3
    }

    /// <summary>
    /// The rules for hailing a ship that enters a faction's space: whose space a point is, what tone
    /// the hail takes, and what it says. SE-free; the host supplies the relation band and delivers
    /// the line.
    ///
    /// <para>Provenance: FAC-483 slice 1. Design: docs/fleet-events.md, "Trespass: a hostile ship in
    /// our space".</para>
    /// </summary>
    public static class TrespassPolicy
    {
        /// <summary>The faction whose space <paramref name="posKm"/> lies in: the one with the most
        /// influence there, provided that influence reaches <paramref name="minInfluence"/>. Null over
        /// open space and over neutral ground — <see cref="InfluenceField.DominantAt"/> never names
        /// <see cref="NeutralFaction.Tag"/>.</summary>
        public static string SpaceOwnerAt(InfluenceField field, Vec3 posKm, double minInfluence)
        {
            if (field == null) return null;
            string top;
            double topVal, secondVal;
            field.DominantAt(posKm, out top, out topVal, out secondVal);
            if (top == null || topVal <= 0 || topVal < minInfluence) return null;
            return top;
        }

        /// <summary>The tone for a pilot whose standing with the faction reads
        /// <paramref name="relationBand"/> — SE's own band name, as <see cref="ReputationScale.BandOf"/>
        /// and <c>MyRelationsBetweenFactions</c> spell it: <c>Enemies</c>, <c>Neutral</c>,
        /// <c>Friends</c>. Anything else reads as neutral: an unknown standing is told whose space it
        /// is, neither welcomed nor threatened.</summary>
        public static HailTone ToneFor(string relationBand, bool pilotIsMember)
        {
            if (pilotIsMember) return HailTone.None;
            if (string.Equals(relationBand, "Enemies", StringComparison.OrdinalIgnoreCase)) return HailTone.Warning;
            if (string.Equals(relationBand, "Friends", StringComparison.OrdinalIgnoreCase)) return HailTone.Welcome;
            return HailTone.Notice;
        }

        /// <summary>What the hail says. Null for <see cref="HailTone.None"/>.</summary>
        public static string HailText(HailTone tone, string factionName)
        {
            string name = string.IsNullOrEmpty(factionName) ? "this faction's" : factionName;
            switch (tone)
            {
                case HailTone.Welcome:
                    return "Welcome to " + name + " space. You are among friends here.";
                case HailTone.Notice:
                    return "You have entered " + name + " space. Mind your conduct and you will have no trouble from us.";
                case HailTone.Warning:
                    return "You have entered " + name + " space. You are not welcome here. Turn back.";
                default:
                    return null;
            }
        }

        /// <summary>The faction's node closest to <paramref name="posKm"/>, named or not, or null. The
        /// node whose space the ship is in, and the one the hail is logged against.</summary>
        public static SpatialNode NearestOwnedNode(SpatialGraph graph, string tag, Vec3 posKm)
        {
            if (graph == null || tag == null) return null;
            SpatialNode best = null;
            double bestD2 = double.MaxValue;
            foreach (SpatialNode n in graph.Nodes.Values)
            {
                if (n == null || n.OwnerFactionTag != tag) continue;
                double d2 = Sensing.DistanceSquared(n.Position, posKm);
                if (d2 < bestD2) { bestD2 = d2; best = n; }
            }
            return best;
        }

        /// <summary>Who signs the hail: the nearest node's station when it has a name, otherwise the
        /// faction itself. Never a named station further off, which can be a whole sector away when
        /// the space belongs to an unnamed outpost.</summary>
        public static string HailAuthor(SpatialNode nearest, string factionName)
        {
            if (nearest != null && !string.IsNullOrEmpty(nearest.DisplayName)) return nearest.DisplayName + " control";
            return string.IsNullOrEmpty(factionName) ? "Traffic control" : factionName;
        }

        /// <summary>Whether a ship with this standing is an intruder the faction defends against, not
        /// merely a visitor it hails: SE's own Enemies band, the same reading the tone takes.</summary>
        public static bool IsIntruder(HailTone tone)
        {
            return tone == HailTone.Warning;
        }

        /// <summary>How long an intruder mark outlives the last sighting. The host re-marks on every
        /// 5 s pass while the ship is seen, so this only bridges a missed pass or two.</summary>
        public static readonly TimeSpan IntruderMemory = TimeSpan.FromSeconds(60);

        /// <summary>The Engage dwell an intruder's stand-in threat carries: a reactive Guard is in time
        /// when it can reach the node inside this. The same window a station call gives.</summary>
        public static TimeSpan IntruderAnswerWindow { get { return StationCallRules.AnswerWindow; } }

        /// <summary>Threat added to the node when a mark is fresh, at most once per
        /// <see cref="IntruderThreatInterval"/>. Enough for patrol ordering to lead with the node;
        /// below <see cref="FactionTuning.PatrolStandThreatFloor"/>, so presence alone never makes
        /// a patrol stand.</summary>
        public const double IntruderThreat = 0.15;

        /// <summary>The shortest gap between two threat charges at one node. At the default
        /// <see cref="FactionTuning.ThreatDecayPerHour"/> of 0.7, one charge of
        /// <see cref="IntruderThreat"/> has decayed away in under 13 minutes, so a pilot slipping in
        /// and out of a border cannot stack trespass threat above one charge.</summary>
        public static readonly TimeSpan IntruderThreatInterval = TimeSpan.FromMinutes(15);

        /// <summary>The log token for a tone.</summary>
        public static string ToneName(HailTone tone)
        {
            switch (tone)
            {
                case HailTone.Welcome: return "welcome";
                case HailTone.Notice: return "notice";
                case HailTone.Warning: return "warning";
                default: return "none";
            }
        }
    }

    /// <summary>
    /// Which space each pilot is in and when each faction last hailed them, so a ship is hailed once
    /// per entry and not again for re-entering inside the repetition guard.
    ///
    /// <para><b>"In" means in the space and seen.</b> The host reports a faction only when the ship
    /// is in that faction's space <i>and</i> the faction detects it, so a ship that slips in
    /// unseen is hailed at the moment it is first detected, which is the entry as far as the faction
    /// knows.</para>
    ///
    /// <para><b>Keyed by pilot, not hull.</b> The hail addresses a person, and changing ships inside
    /// one faction's space is not entering it again.</para>
    /// </summary>
    public sealed class TrespassHailBook
    {
        private readonly Dictionary<long, string> _inside = new Dictionary<long, string>();

        /// <summary>Per (pilot, faction): the later of the last hail and the last time the pilot was
        /// seen leaving. Measuring the guard from the exit, not only from the hail, is what keeps a
        /// ship that idles on a border, slipping in and out every few minutes, from being hailed
        /// once per guard window for as long as it stays there.</summary>
        private readonly Dictionary<string, DateTime> _lastHail =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Records that <paramref name="identityId"/> is now in <paramref name="spaceTag"/>'s
        /// space and seen by it (null: in nobody's, or unseen). True when this is an entry that
        /// should be hailed: a change of space, into a faction this pilot has neither been hailed by
        /// nor left within <paramref name="guardSeconds"/>. Records the hail when it returns
        /// true.</summary>
        public bool Observe(long identityId, string spaceTag, DateTime nowUtc, double guardSeconds)
        {
            string was;
            _inside.TryGetValue(identityId, out was);
            if (string.Equals(was, spaceTag, StringComparison.Ordinal)) return false;

            if (was != null) _lastHail[Key(identityId, was)] = nowUtc;
            if (spaceTag == null) { _inside.Remove(identityId); return false; }
            _inside[identityId] = spaceTag;

            string key = Key(identityId, spaceTag);
            DateTime last;
            if (_lastHail.TryGetValue(key, out last) && (nowUtc - last).TotalSeconds < guardSeconds)
                return false;
            _lastHail[key] = nowUtc;
            return true;
        }

        /// <summary>The space this pilot was last recorded in, or null.</summary>
        public string InsideOf(long identityId)
        {
            string tag;
            return _inside.TryGetValue(identityId, out tag) ? tag : null;
        }

        /// <summary>Treats an offline pilot as having left, so logging back in is an entry (hailed once
        /// the guard has run from the logout), and drops stamps older than the guard, which can no
        /// longer suppress anything.</summary>
        public void Prune(ICollection<long> onlineIdentities, DateTime nowUtc, double guardSeconds)
        {
            if (_inside.Count > 0)
            {
                var gone = new List<long>();
                foreach (long id in _inside.Keys)
                    if (onlineIdentities == null || !onlineIdentities.Contains(id)) gone.Add(id);
                for (int i = 0; i < gone.Count; i++)
                {
                    _lastHail[Key(gone[i], _inside[gone[i]])] = nowUtc;
                    _inside.Remove(gone[i]);
                }
            }
            if (_lastHail.Count > 0)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, DateTime> kv in _lastHail)
                    if ((nowUtc - kv.Value).TotalSeconds >= guardSeconds) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) _lastHail.Remove(stale[i]);
            }
        }

        public int StampCount { get { return _lastHail.Count; } }

        private static string Key(long identityId, string tag)
        {
            return identityId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + tag;
        }
    }
}
