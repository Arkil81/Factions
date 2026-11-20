using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a station's traffic controller can report (docs/atc-voice.md, FAC-419).</summary>
    public enum TrafficEventKind : byte
    {
        /// <summary>A fleet reached a mission stop at a node.</summary>
        Arrive = 1,
        /// <summary>A fleet left a node on a leg that goes somewhere.</summary>
        Depart = 2,
        /// <summary>An attacker entered its target node's sensor envelope.</summary>
        HostileInbound = 3,
        /// <summary>A departure joined its station's traffic queue (docs/traffic-control.md, T8):
        /// "hold position, number n for departure".</summary>
        Queued = 4,
        /// <summary>An arrival joined its station's traffic queue: "hold at the outer marker".</summary>
        Held = 5,
        /// <summary>Queued five minutes and still waiting: "departure delayed" / "expect further delay".</summary>
        Delayed = 6,
        /// <summary>An arrival sent on to another station after waiting five minutes (T3).</summary>
        Diverted = 7,
        /// <summary>Queue head popped: cleared inbound, or cleared to depart.</summary>
        Cleared = 8,
    }

    /// <summary>One thing a station's controller would say. Queued on
    /// <see cref="WorldModel"/> for the host, which decides who is in radio range.</summary>
    public sealed class TrafficEvent
    {
        public TrafficEventKind Kind;
        public string NodeId;
        public Vec3 NodePosKm;
        public string ActorTag;
        public ActionKind FleetKind;
        public FleetStopKind StopKind;
        public string FleetKey;
        /// <summary>The fleet's id, spoken by the controller so a callout names one ship (T9).</summary>
        public string FleetId;
        /// <summary>Queue events: the fleet's place in its station's queue (1-based), whether it is a
        /// departure, and for a divert the station it was sent to.</summary>
        public int QueuePosition;
        public bool Departure;
        public string DivertNodeId;
        public string DivertNodeName;
        public DateTime Utc;
        /// <summary>Where the pilot is when they call, and how fast they are moving (m/s): the radio
        /// path to each listener starts here (FAC-419 slice 5, <see cref="RadioPath"/>). An arrival
        /// calls in on final approach, <see cref="FactionTuning.AtcPilotCallDistanceKm"/> out; a
        /// departure calls from the stop as it pulls away; a hostile calls where it was detected.
        /// The speed is capped at what a grid can fly (<see cref="TrafficWatch.RadioSpeedCapMs"/>):
        /// the sim's cruise is abstract, and a ship makes its call at normal speeds.</summary>
        public Vec3 PilotPosKm;
        public Vec3 PilotVelMs;
        /// <summary>How loud the pilot's ship is when it calls (<see cref="FleetSensing.SignatureOf"/>):
        /// a listener with antennas hears it as far as their scanner would see it
        /// (<see cref="RadioPath.PilotRangeKm"/>).</summary>
        public double PilotSignature;
        /// <summary>The fleet's materialised hull, 0 when it is abstract: the host reads the hull's own
        /// broadcast radius from it, which is what SE's antenna channel already shows.</summary>
        public long PilotEntityId;

        public override string ToString()
        {
            return Kind + " " + ActorTag + " " + FleetKind + "/" + StopKind + " @ " + NodeId + " " + FleetKey;
        }
    }

    /// <summary>
    /// Finds traffic events by comparing each fleet's current leg with what the previous pass saw,
    /// once per arrivals pass (called at the top of <see cref="PendingFleetResolver.ResolveArrivals"/>,
    /// before anything resolves, so an arrival is seen while its leg is still the current one).
    ///
    /// <para>A scan rather than a hook at every dispatch site: fleets are created in seven places,
    /// and a leg changes in several more. Comparing legs sees all of them the same way, including
    /// ones added later, and needs no change to any of those sites.</para>
    ///
    /// <para>The first scan after a boot only records what it sees. Otherwise every fleet already in
    /// flight would announce a departure the moment the server came up.</para>
    ///
    /// <para>What counts:
    /// <list type="bullet">
    /// <item><b>Depart</b> -- a new leg starting from a real node, unless it is a dwell at that same
    /// node (Mine, Engage) or an interception hold.</item>
    /// <item><b>Arrive</b> -- a leg that has arrived at a mission stop. Navigation and Intercept
    /// stops are transit, as <see cref="ShipActivity"/> already treats them, and a dwell stop
    /// resolves when the dwell ENDS, so it would be announced late.</item>
    /// <item><b>HostileInbound</b> -- a Raid, Conquer or Retaliate fleet the target node's own
    /// sensor detects (<see cref="Sensing.Detects"/>, fog of war), once per fleet and target.
    /// Never at dispatch: the target cannot see a raid leave another station.</item>
    /// </list></para>
    ///
    /// <para>Bench fleets are skipped: a staged fight is not traffic.</para>
    /// </summary>
    public static class TrafficWatch
    {
        /// <summary>How long after departing the pilot's velocity is averaged over, so the easing out of
        /// the stop does not read as standing still.</summary>
        public const double CallLeadSeconds = 20.0;

        /// <summary>The fastest a pilot is heard moving: the grid speed limit when the server sets one,
        /// otherwise SE's vanilla 100 m/s. The sim's cruise between nodes is abstract (a fleet covering
        /// 30 km in 20 s read 2.5 kHz of Doppler on 2026-09-29) and is not what a ship does on approach.</summary>
        public static double RadioSpeedCapMs(FactionTuning t)
        {
            double cap = 0;
            if (t != null && GridSpeedPolicy.IsOverride(t.GridMaxSpeedLarge)) cap = Math.Max(cap, t.GridMaxSpeedLarge);
            if (t != null && GridSpeedPolicy.IsOverride(t.GridMaxSpeedSmall)) cap = Math.Max(cap, t.GridMaxSpeedSmall);
            return cap > 0 ? cap : 100.0;
        }

        /// <summary>When on its leg the fleet is <paramref name="km"/> from its stop: a search over the
        /// leg, since the transit profile eases rather than moving at one speed. The departure time
        /// when the whole leg is shorter than that.</summary>
        public static DateTime WhenDistanceFromTarget(PendingFleetAction f, double km, FactionTuning tuning)
        {
            DateTime lo = f.DepartedUtc, hi = f.EstimatedArrivalUtc;
            if (hi <= lo || f.CurrentPosKm(lo, tuning).DistanceTo(f.TargetPosKm) <= km) return lo;
            for (int i = 0; i < 40; i++)
            {
                DateTime mid = lo.AddTicks((hi - lo).Ticks / 2);
                if (f.CurrentPosKm(mid, tuning).DistanceTo(f.TargetPosKm) > km) lo = mid; else hi = mid;
            }
            return hi;
        }

        public static void Scan(IList<FactionState> states, WorldModel world, FactionTuning tuning,
                                IItemVolumeCatalogue volumes = null)
        {
            if (states == null || world == null) return;
            TrafficWatchState st = world.TrafficWatch;
            DateTime now = world.UtcNow;
            bool announce = st.Seeded;
            var present = new HashSet<string>(StringComparer.Ordinal);

            for (int si = 0; si < states.Count; si++)
            {
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                if (fleets == null) continue;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.IsBench || f.Intercepted) continue;
                    string key = (f.ActorTag ?? "") + "|" + (f.FleetId ?? f.LogId);
                    present.Add(key);

                    string leg = f.DepartedUtc.Ticks + "|" + (f.TargetNodeId ?? "") + "|" + (int)f.CurrentStopKind;
                    TrafficWatchState.Seen seen;
                    if (!st.Fleets.TryGetValue(key, out seen) || seen.Leg != leg)
                    {
                        seen = new TrafficWatchState.Seen { Leg = leg };
                        st.Fleets[key] = seen;
                        if (announce && DepartsFromNode(f, world))
                            Emit(world, TrafficEventKind.Depart, f.OriginNodeId, f, key, now, tuning, volumes);
                    }

                    if (!seen.Arrived && !f.Resolved && f.HasArrived(now) && IsMissionStop(f))
                    {
                        seen.Arrived = true;
                        if (announce) Emit(world, TrafficEventKind.Arrive, f.TargetNodeId, f, key, now, tuning, volumes);
                    }

                    if (IsHostile(f) && f.TargetNodeId != null)
                    {
                        string pair = key + ">" + f.TargetNodeId;
                        if (!st.HostileAnnounced.Contains(pair) && TargetSees(f, world, tuning, volumes, now))
                        {
                            st.HostileAnnounced.Add(pair);
                            if (announce) Emit(world, TrafficEventKind.HostileInbound, f.TargetNodeId, f, key, now, tuning, volumes);
                        }
                    }
                }
            }

            if (st.Fleets.Count > present.Count)
            {
                var gone = new List<string>();
                foreach (string k in st.Fleets.Keys) if (!present.Contains(k)) gone.Add(k);
                for (int i = 0; i < gone.Count; i++) st.Fleets.Remove(gone[i]);
                st.HostileAnnounced.RemoveWhere(p => !present.Contains(p.Substring(0, p.IndexOf('>'))));
            }
            st.Seeded = true;
        }

        private static bool DepartsFromNode(PendingFleetAction f, WorldModel world)
        {
            return f.OriginNodeId != null
                   && !PendingFleetResolver.IsDwellStop(f.CurrentStopKind)
                   && f.CurrentStopKind != FleetStopKind.Intercept
                   && !string.Equals(f.OriginNodeId, f.TargetNodeId, StringComparison.Ordinal)
                   && !LeavesFromTheField(f, world);
        }

        /// <summary>A miner's delivery leg starts out on the ring it worked (<see cref="MineWander"/>),
        /// tens of km from the deposit it still names as its origin: it is not leaving the station.</summary>
        private static bool LeavesFromTheField(PendingFleetAction f, WorldModel world)
        {
            SpatialNode origin;
            return f.Kind == ActionKind.Mine && world != null
                   && world.Graph.Nodes.TryGetValue(f.OriginNodeId, out origin)
                   && f.OriginPosKm.DistanceTo(origin.Position) > MineWander.ClearanceKm / 2;
        }

        private static bool IsMissionStop(PendingFleetAction f)
        {
            return f.TargetNodeId != null
                   && f.CurrentStopKind != FleetStopKind.Navigation
                   && f.CurrentStopKind != FleetStopKind.Intercept
                   && !PendingFleetResolver.IsDwellStop(f.CurrentStopKind);
        }

        private static bool IsHostile(PendingFleetAction f)
        {
            return (f.Kind == ActionKind.Raid || f.Kind == ActionKind.Conquer || f.Kind == ActionKind.Retaliate)
                   && f.CurrentStopKind != FleetStopKind.Return;
        }

        private static bool TargetSees(PendingFleetAction f, WorldModel world, FactionTuning tuning,
                                       IItemVolumeCatalogue volumes, DateTime now)
        {
            SpatialNode node;
            if (world.Graph == null || !world.Graph.Nodes.TryGetValue(f.TargetNodeId, out node)) return false;
            if (node.OwnerFactionTag == null || string.Equals(node.OwnerFactionTag, f.ActorTag, StringComparison.Ordinal))
                return false;
            double strength = Sensing.NodeStrengthKm(node, tuning);
            if (strength <= 0) return false;
            double signature = FleetSensing.SignatureOf(f, world.PrefabCatalogue, volumes, tuning, now);
            return Sensing.Detects(node.Position, strength, f.CurrentPosKm(now, tuning), signature, 0, null);
        }

        private static void Emit(WorldModel world, TrafficEventKind kind, string nodeId, PendingFleetAction f,
                                 string key, DateTime now, FactionTuning tuning, IItemVolumeCatalogue volumes)
        {
            SpatialNode node;
            Vec3 pos = world.Graph != null && world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.Position : default(Vec3);
            // Where the call is placed and the span its velocity is read over: 5 km out on final approach
            // for an arrival, the pull-away for a departure, a short window around detection for a hostile.
            DateTime call = kind == TrafficEventKind.Arrive
                ? WhenDistanceFromTarget(f, tuning != null ? tuning.AtcPilotCallDistanceKm : 5.0, tuning) : now;
            DateTime from = kind == TrafficEventKind.Depart ? now : call.AddSeconds(-1);
            DateTime to = kind == TrafficEventKind.Depart ? now.AddSeconds(CallLeadSeconds) : call.AddSeconds(1);
            Vec3 a = f.CurrentPosKm(from, tuning), b = f.CurrentPosKm(to, tuning);
            double span = (to - from).TotalSeconds;
            double vx = (b.X - a.X) * 1000.0 / span, vy = (b.Y - a.Y) * 1000.0 / span, vz = (b.Z - a.Z) * 1000.0 / span;
            double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz), cap = RadioSpeedCapMs(tuning);
            if (speed > cap) { double k = cap / speed; vx *= k; vy *= k; vz *= k; }
            world.AddTrafficEvent(new TrafficEvent
            {
                Kind = kind, NodeId = nodeId, NodePosKm = pos, ActorTag = f.ActorTag, FleetKind = f.Kind,
                StopKind = f.CurrentStopKind, FleetKey = key, FleetId = f.FleetId, Utc = now,
                PilotPosKm = f.CurrentPosKm(call, tuning),
                PilotVelMs = new Vec3(vx, vy, vz),
                PilotSignature = FleetSensing.SignatureOf(f, world.PrefabCatalogue, volumes, tuning, call),
                PilotEntityId = f.MaterialisedEntityId,
            });
        }
    }

    /// <summary>Runtime only, never persisted: what <see cref="TrafficWatch"/> saw last pass.</summary>
    public sealed class TrafficWatchState
    {
        public sealed class Seen
        {
            public string Leg;
            public bool Arrived;
        }

        public bool Seeded;
        public readonly Dictionary<string, Seen> Fleets = new Dictionary<string, Seen>(StringComparer.Ordinal);
        public readonly HashSet<string> HostileAnnounced = new HashSet<string>(StringComparer.Ordinal);
    }
}
