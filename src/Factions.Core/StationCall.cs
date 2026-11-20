using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Why a station's call ended.</summary>
    public enum StationCallEnd
    {
        None = 0,
        /// <summary>The antenna stayed lost (destroyed, off, unpowered, not broadcasting) through
        /// <see cref="WingRules.LinkGrace"/>.</summary>
        AntennaLost,
        /// <summary>Nothing has threatened the station for <see cref="StationCallRules.ThreatClearAfter"/>.</summary>
        ThreatCleared,
        /// <summary>The Defend focus the call placed or refreshed has run out.</summary>
        FocusExpired,
        /// <summary>The station grid is no longer in the world.</summary>
        StationGone,
    }

    /// <summary>Where a station call's target came from, in the order
    /// <see cref="StationCallRules.PickTarget"/> tries them.</summary>
    public enum StationCallTargetSource
    {
        None = 0,
        /// <summary>The grid whose weapon last damaged the station.</summary>
        Damage,
        /// <summary>A working combat AI block's lock on the station grid.</summary>
        CombatBlockLock,
        /// <summary>The nearest hostile grid within <see cref="StationCallRules.HostileNearM"/>.</summary>
        HostileNear,
    }

    /// <summary>What a call did to the station faction's Defend focus.</summary>
    public enum StationCallFocus
    {
        None = 0,
        /// <summary>A player-issued Defend focus was placed on the node.</summary>
        Placed,
        /// <summary>The standing Defend focus on the node was given a fresh expiry.</summary>
        Refreshed,
        /// <summary>A Defend focus centred elsewhere already covers the node; nothing changed.</summary>
        Covered,
        /// <summary><see cref="CampaignFocusLifecycle.TryStartPlayer"/> refused for another reason
        /// (the cap, a node not the faction's own).</summary>
        Refused,
        /// <summary>Not asked: the station's faction is an NPC faction, whose foci its brain sets.</summary>
        NotPlayer,
    }

    /// <summary>
    /// The station call's rules, SE-free (docs/fleet-tactics.md, "A station calls for protection").
    /// A station's antenna says <i>protect me</i>; the faction's armed hulls inside its broadcast
    /// radius take it as a wing order anchored on the station (<see cref="WingOrder"/>), the node is
    /// marked as calling so <c>FleetManager</c>'s reactive Guard dispatch answers it, and a player
    /// faction's Defend focus is placed on the node or refreshed. The host reads the world and asks
    /// here.
    /// </summary>
    public static class StationCallRules
    {
        /// <summary>A call with no threat this long ends.</summary>
        public static readonly TimeSpan ThreatClearAfter = TimeSpan.FromSeconds(30);

        /// <summary>A hit this recent still names its attacker as the call's target.</summary>
        public static readonly TimeSpan DamageMemory = TimeSpan.FromSeconds(30);

        /// <summary>How long a Guard may take to reach a calling node: the dwell the call's stand-in
        /// threat carries, so any defender inside the reactive dispatch's own transit bound is in time.</summary>
        public static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(5);

        /// <summary>A repeat call inside this of the last written line is counted, not written.</summary>
        public static readonly TimeSpan RepeatLogInterval = TimeSpan.FromSeconds(30);

        /// <summary>A hostile grid within this of the station is a threat, and the target when
        /// nothing has damaged the station and no combat block holds a lock.</summary>
        public const double HostileNearM = 3000.0;

        /// <summary>A static grid that is not a materialised station answers for the nearest node
        /// its faction owns within this.</summary>
        public const double NodeReachM = 5000.0;

        /// <summary>Whether the antenna carries the station's call: functional, on, powered,
        /// broadcasting at a radius, and owned by the station's faction.</summary>
        public static WingRefusal CheckAntenna(WingAntennaReading a, string stationFactionTag)
        {
            return WingRules.CheckAntenna(a, 0, stationFactionTag);
        }

        /// <summary>The call's target, first non-zero of: whoever damaged the station, a combat AI
        /// block's lock, the nearest hostile near it.</summary>
        public static long PickTarget(long damager, long combatBlockLock, long hostileNear,
                                      out StationCallTargetSource source)
        {
            if (damager != 0) { source = StationCallTargetSource.Damage; return damager; }
            if (combatBlockLock != 0) { source = StationCallTargetSource.CombatBlockLock; return combatBlockLock; }
            if (hostileNear != 0) { source = StationCallTargetSource.HostileNear; return hostileNear; }
            source = StationCallTargetSource.None;
            return 0;
        }

        /// <summary>Whether a station cannot call and its node gets no reactive Guard: a materialised
        /// NPC station with no working antenna. An
        /// abstract station has no grid and its call is assumed; a player's station calls only when
        /// its owner's blocks say so, and is never silenced here.</summary>
        public static bool Silenced(bool materialised, bool playerFaction, bool antennaWorks)
        {
            return materialised && !playerFaction && !antennaWorks;
        }

        /// <summary>Whether the host fires a station's call itself when it is attacked: a
        /// materialised NPC station.</summary>
        public static bool HostCalls(bool materialised, bool playerFaction)
        {
            return materialised && !playerFaction;
        }

        /// <summary>Places the player faction's Defend focus on <paramref name="nodeId"/> through
        /// <see cref="CampaignFocusLifecycle.TryStartPlayer"/>, or, when a standing one is centred
        /// there, refreshes its expiry. A focus centred elsewhere that already covers the node is
        /// left as it is: the coverage dedup refuses a second circuit over the same nodes.</summary>
        public static StationCallFocus PlaceOrRefreshFocus(FactionState s, FactionTuning t, DateTime nowUtc,
            IWorldView world, string nodeId, ISet<string> regionMembers, out CampaignFocus focus,
            out OffensiveCampaigns.PlayerResult result)
        {
            focus = null;
            result = OffensiveCampaigns.PlayerResult.NotReady;
            if (s == null || !s.IsPlayerFaction) return StationCallFocus.NotPlayer;
            result = CampaignFocusLifecycle.TryStartPlayer(s, t, nowUtc, world, nodeId, regionMembers, out focus);
            if (result == OffensiveCampaigns.PlayerResult.Ok) return StationCallFocus.Placed;
            if (result != OffensiveCampaigns.PlayerResult.Duplicate) return StationCallFocus.Refused;
            focus = CampaignFocusLifecycle.RefreshPlayer(s, t, nowUtc, nodeId);
            return focus != null ? StationCallFocus.Refreshed : StationCallFocus.Covered;
        }

        /// <summary>Whether a player-issued Defend focus centred on <paramref name="nodeId"/> stands now.</summary>
        public static bool FocusLive(FactionState s, string nodeId, DateTime nowUtc)
        {
            if (s == null || nodeId == null) return false;
            foreach (CampaignFocus f in s.CampaignFoci)
                if (f != null && f.PlayerIssued && f.CenterNodeId == nodeId
                    && CampaignFocusLifecycle.HasDefendRegionObjective(f)
                    && (f.ExpiresAtUtc == DateTime.MinValue || f.ExpiresAtUtc > nowUtc))
                    return true;
            return false;
        }

        public static string Token(StationCallEnd end)
        {
            switch (end)
            {
                case StationCallEnd.AntennaLost:   return "antenna_lost";
                case StationCallEnd.ThreatCleared: return "threat_cleared";
                case StationCallEnd.FocusExpired:  return "focus_expired";
                case StationCallEnd.StationGone:   return "station_gone";
                default:                           return "none";
            }
        }

        public static string Token(StationCallTargetSource source)
        {
            switch (source)
            {
                case StationCallTargetSource.Damage:          return "damaged_by";
                case StationCallTargetSource.CombatBlockLock: return "combat_block_lock";
                case StationCallTargetSource.HostileNear:     return "hostile_near";
                default:                                      return "none";
            }
        }

        public static string Token(StationCallFocus focus)
        {
            switch (focus)
            {
                case StationCallFocus.Placed:    return "placed";
                case StationCallFocus.Refreshed: return "refreshed";
                case StationCallFocus.Covered:   return "covered";
                case StationCallFocus.Refused:   return "refused";
                case StationCallFocus.NotPlayer: return "npc";
                default:                         return "none";
            }
        }
    }

    /// <summary>
    /// One station's standing call for protection. Never persisted: a restart ends every call, and
    /// the next attack or press raises it again.
    ///
    /// <para><b>It ends</b> (<see cref="Read"/>) when the antenna stays lost through
    /// <see cref="WingRules.LinkGrace"/> — the same grace a wing hull gets — when nothing has
    /// threatened the station for <see cref="StationCallRules.ThreatClearAfter"/>, or when the Defend
    /// focus it placed or refreshed has run out. A repeat call (<see cref="Repeat"/>) counts as
    /// threat, so a timer or event controller that keeps firing keeps it up.</para>
    /// </summary>
    public sealed class StationCall
    {
        public string NodeId;
        public long StationGridId;
        public long AntennaEntityId;
        public string FactionTag;
        /// <summary>The antenna's owner, whose relation to a grid decides whether it is hostile.</summary>
        public long OwnerIdentityId;
        public bool PlayerFaction;
        public WingVia Via;
        public string BenchLabel;
        public DateTime StartedUtc;
        public DateTime LastThreatUtc;
        public DateTime? AntennaLostUtc;
        public WingRefusal AntennaLostReason;
        public long TargetEntityId;
        public StationCallTargetSource TargetSource;
        /// <summary>Whether a Defend focus was placed or refreshed for this call: its expiry then
        /// ends the call.</summary>
        public bool TracksFocus;
        /// <summary>Repeat calls since the last line was written, and when that was.</summary>
        public int RepeatsUnlogged;
        public DateTime LastLoggedUtc;

        public StationCall(DateTime nowUtc)
        {
            StartedUtc = nowUtc;
            LastThreatUtc = nowUtc;
            LastLoggedUtc = nowUtc;
        }

        /// <summary>A repeat call from the same station: threat is present now. Returns whether the
        /// line for it is due (<see cref="StationCallRules.RepeatLogInterval"/> since the last one);
        /// otherwise the repeat is counted.</summary>
        public bool Repeat(DateTime nowUtc)
        {
            LastThreatUtc = nowUtc;
            RepeatsUnlogged++;
            if (nowUtc - LastLoggedUtc < StationCallRules.RepeatLogInterval) return false;
            return true;
        }

        /// <summary>Marks the repeat line written.</summary>
        public int TakeRepeats(DateTime nowUtc)
        {
            int n = RepeatsUnlogged;
            RepeatsUnlogged = 0;
            LastLoggedUtc = nowUtc;
            return n;
        }

        /// <summary>One read of the call: <paramref name="antenna"/> is the antenna's standing
        /// (<see cref="StationCallRules.CheckAntenna"/>), <paramref name="threat"/> whether anything
        /// threatens the station now, <paramref name="focusLive"/> whether its Defend focus stands.
        /// Returns why the call ends, or <see cref="StationCallEnd.None"/>.</summary>
        public StationCallEnd Read(DateTime nowUtc, bool stationExists, WingRefusal antenna, bool threat, bool focusLive)
        {
            if (!stationExists) return StationCallEnd.StationGone;
            if (antenna != WingRefusal.None)
            {
                if (!AntennaLostUtc.HasValue)
                {
                    AntennaLostUtc = nowUtc;
                    AntennaLostReason = antenna;
                }
                else if (nowUtc - AntennaLostUtc.Value >= WingRules.LinkGrace)
                    return StationCallEnd.AntennaLost;
            }
            else
            {
                AntennaLostUtc = null;
                AntennaLostReason = WingRefusal.None;
            }
            if (TracksFocus && !focusLive) return StationCallEnd.FocusExpired;
            if (threat) LastThreatUtc = nowUtc;
            else if (nowUtc - LastThreatUtc >= StationCallRules.ThreatClearAfter) return StationCallEnd.ThreatCleared;
            return StationCallEnd.None;
        }
    }
}
