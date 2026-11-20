using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Traffic control's presence rule (docs/traffic-control.md, slice 2): which civilian fleets near
    /// players may materialise, and which materialised ones are shed, so Factions' predicted cost near
    /// each player stays inside that player's share of the budget.
    ///
    /// <para><b>Neighbourhoods.</b> Players within <see cref="Settings.CeilingRadiusKm"/> of each other
    /// form one; its weight is the sum of theirs. A player's weight is
    /// <see cref="Settings.OverShareWeight"/> to the power of their load-governor step (T12), so a
    /// player over their own share is left less traffic.</para>
    ///
    /// <para><b>Shares</b> are max-min fair over the Factions budget: a neighbourhood that would use
    /// less than its weighted share takes only what it would use, and the rest is divided among the
    /// others (T11). One player alone gets all of it.</para>
    ///
    /// <para><b>Attribution.</b> Every fleet within the ceiling radius of a player counts against the
    /// nearest player's neighbourhood. Combat fleets are not gated here but their cost is counted, so
    /// civilian traffic fills only what they leave.</para>
    ///
    /// <para><b>Servers.</b> On a Nexus cluster the authority plans for every shard's players, but each
    /// shard's frame pays for the grids flown there. A player carries the <see cref="Player.Server"/>
    /// whose frame they are in; players on different servers never share a neighbourhood, and each
    /// server's neighbourhoods divide that server's budget: the planning server's own, or the one its
    /// agent reported (<see cref="ServerLoad"/>), with fleets near its players priced at its
    /// multipliers. A server that reported none is not limited.</para>
    ///
    /// <para><b>Admission</b> takes civilian candidates nearest first and stops at the first that
    /// does not fit, so what is visible is a clean radius around the player. <b>Shedding</b> starts
    /// only when use exceeds the share by <see cref="Settings.ShedHysteresis"/>, takes the farthest
    /// sheddable fleet beyond <see cref="Settings.ShedMinDistanceKm"/>, and at most
    /// <see cref="Settings.MaxShedPerPlan"/> per neighbourhood per plan, so a fleet at the edge does
    /// not flicker and a burst of despawns does not replace a burst of spawns.</para>
    /// </summary>
    public static class TrafficPresence
    {
        public sealed class Settings
        {
            public double CeilingRadiusKm = 15.0;
            /// <summary>Stations a live fight has closed (FAC-384): their queues hold — no arrival or
            /// departure is cleared — without blocking the neighbourhood's other stations, and an
            /// arrival queued at one is due to divert at once.</summary>
            public ICollection<string> Closed;
            public double OverShareWeight = 0.5;
            public double ShedHysteresis = 0.10;
            public double ShedMinDistanceKm = 5.0;
            public int MaxShedPerPlan = 1;
            /// <summary>An arrival queued this long diverts (T3).</summary>
            public double DivertSeconds = 300.0;
            /// <summary>A departure, or an arrival that cannot divert, queued this long has its delay
            /// called once; it keeps waiting (T8).</summary>
            public double DelaySeconds = 300.0;
        }

        public struct Player
        {
            public string Party;
            public Vec3 PosKm;
            public int GovernorStep;
            /// <summary>The server whose frame this player is in; 0 is the planning server.</summary>
            public byte Server;
        }

        /// <summary>Another server's budget and multipliers, as it reported them.</summary>
        public struct ServerLoad
        {
            public double BudgetMs;
            public double KCivilian;
            public double KCombat;
        }

        public struct Fleet
        {
            public string Key;
            public Vec3 PosKm;
            /// <summary>Predicted ms per frame, already scaled by the planning server's multiplier.</summary>
            public double CostMs;
            /// <summary>The same prediction unscaled, priced at another server's multiplier when the
            /// fleet is nearest one of that server's players. Zero leaves <see cref="CostMs"/>.</summary>
            public double BaseMs;
            /// <summary>Priced at the combat multiplier rather than the civilian one.</summary>
            public bool CombatClass;
            /// <summary>Civilian traffic is gated and may be shed; combat is only counted.</summary>
            public bool Civilian;
            /// <summary>Has a live grid or a spawn in flight.</summary>
            public bool Materialised;
            /// <summary>May be shed now: materialised civilian, past its settle grace.</summary>
            public bool Sheddable;
            /// <summary>The queued station this unmaterialised civilian fleet is arriving at or leaving;
            /// null when it is not station traffic.</summary>
            public string QueueStation;
            public bool QueueDeparture;
            /// <summary>An arrival whose stop another station of the same owner can take.</summary>
            public bool Divertable;
            /// <summary>Heading for a queued station: a ship coming in to dock, which a queue's pressure
            /// does not shed (it is what the queue is for).</summary>
            public bool Inbound;
        }

        public sealed class Neighbourhood
        {
            public readonly List<string> Parties = new List<string>();
            /// <summary>The server whose budget this neighbourhood shares.</summary>
            public byte Server;
            public double Weight;
            public double ShareMs;
            public double UsedMs;
            public double DemandMs;
            /// <summary>Distance of the first civilian candidate refused; the ceiling when none was.</summary>
            public double EffectiveRadiusKm;
            public int Admitted;
            public int Refused;
            public int Shed;
            public int QueuedArrivals;
            public int QueuedDepartures;
            public int Cleared;
            /// <summary>Queued arrivals admitted to materialise at their holding slots this plan.</summary>
            public int HoldingVisible;
            public string Label { get { return Parties.Count > 0 ? Parties[0] : ""; } }
        }

        public sealed class Plan
        {
            public readonly HashSet<string> Admitted = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Refused = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Shed = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<Neighbourhood> Neighbourhoods = new List<Neighbourhood>();
            public double BudgetMs;
            /// <summary>Held in a station queue: the runtime freezes these where they stand.</summary>
            public readonly HashSet<string> Queued = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<StationQueues.Entry> Joined = new List<StationQueues.Entry>();
            public readonly List<StationQueues.Entry> Cleared = new List<StationQueues.Entry>();
            public readonly List<StationQueues.Entry> DivertDue = new List<StationQueues.Entry>();
            public readonly List<StationQueues.Entry> DelayDue = new List<StationQueues.Entry>();
            /// <summary>The share of the neighbourhood each queued station belongs to, for spacing its
            /// holding slots (<see cref="HoldingSlots.SpacingKm"/>).</summary>
            public readonly Dictionary<string, double> StationShareMs = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        private struct Attributed
        {
            public int Index;
            public double DistanceKm;
        }

        public static Plan Compute(IList<Player> players, IList<Fleet> fleets, double budgetMs, Settings s)
        {
            return Compute(players, fleets, budgetMs, s, null, null, DateTime.MinValue);
        }

        /// <summary>
        /// The plan with station queues. A fleet flagged with <see cref="Fleet.QueueStation"/> and not
        /// yet cleared for it joins that station's queue instead of competing for admission. In each
        /// neighbourhood the order is: fleets cleared earlier, then queue heads oldest first while they
        /// fit (strict FIFO: a head that does not fit blocks the rest), then other traffic nearest first.
        /// </summary>
        /// <param name="stations">Position of every queued station, i.e. every station a player is near.</param>
        public static Plan Compute(IList<Player> players, IList<Fleet> fleets, double budgetMs, Settings s,
                                   IDictionary<string, Vec3> stations, StationQueues queues, DateTime now)
        {
            return Compute(players, fleets, budgetMs, s, stations, queues, now, null);
        }

        /// <summary>The plan across servers: <paramref name="budgetMs"/> is the planning server's,
        /// <paramref name="servers"/> every other server's that reported one.</summary>
        public static Plan Compute(IList<Player> players, IList<Fleet> fleets, double budgetMs, Settings s,
                                   IDictionary<string, Vec3> stations, StationQueues queues, DateTime now,
                                   IDictionary<byte, ServerLoad> servers)
        {
            if (s == null) s = new Settings();
            var plan = new Plan { BudgetMs = Math.Max(0.0, budgetMs) };
            if (players == null || players.Count == 0) return plan;

            int[] hood = GroupPlayers(players, s.CeilingRadiusKm);
            int hoods = 0;
            for (int i = 0; i < hood.Length; i++) hoods = Math.Max(hoods, hood[i] + 1);
            for (int n = 0; n < hoods; n++) plan.Neighbourhoods.Add(new Neighbourhood { EffectiveRadiusKm = s.CeilingRadiusKm });
            for (int i = 0; i < players.Count; i++)
            {
                Neighbourhood nb = plan.Neighbourhoods[hood[i]];
                nb.Server = players[i].Server;
                nb.Parties.Add(players[i].Party ?? "");
                nb.Weight += Math.Pow(s.OverShareWeight, Math.Max(0, players[i].GovernorStep));
            }

            var candidates = new List<Attributed>[hoods];
            var priority = new List<Attributed>[hoods];
            var holding = new List<Attributed>[hoods];
            var shedable = new List<Attributed>[hoods];
            for (int n = 0; n < hoods; n++)
            {
                candidates[n] = new List<Attributed>(); priority[n] = new List<Attributed>();
                holding[n] = new List<Attributed>(); shedable[n] = new List<Attributed>();
            }
            bool queueing = queues != null && stations != null;
            var seenQueued = new HashSet<string>(StringComparer.Ordinal);
            var seenCleared = new HashSet<string>(StringComparer.Ordinal);

            // Each queued station belongs to the neighbourhood of the player nearest it.
            var stationHood = new Dictionary<string, int>(StringComparer.Ordinal);
            if (queueing)
                foreach (KeyValuePair<string, Vec3> station in stations)
                {
                    int nearestToStation = 0;
                    double bestToStation = double.MaxValue;
                    for (int p = 0; p < players.Count; p++)
                    {
                        double d = players[p].PosKm.DistanceTo(station.Value);
                        if (d < bestToStation) { bestToStation = d; nearestToStation = p; }
                    }
                    stationHood[station.Key] = hood[nearestToStation];
                }

            if (fleets != null)
                for (int f = 0; f < fleets.Count; f++)
                {
                    Fleet fl = fleets[f];
                    int nearest = -1;
                    double best = double.MaxValue;
                    for (int p = 0; p < players.Count; p++)
                    {
                        double d = players[p].PosKm.DistanceTo(fl.PosKm);
                        if (d < best) { best = d; nearest = p; }
                    }
                    if (nearest < 0) continue;
                    bool stationTraffic = queueing && fl.Civilian && fl.Key != null && fl.QueueStation != null
                                          && stations.ContainsKey(fl.QueueStation);
                    // Out of every player's reach only queued station traffic counts: a ship holding
                    // at a slot beyond the visible radius keeps its place in the queue.
                    if (best > s.CeilingRadiusKm && !stationTraffic) continue;
                    bool beyond = best > s.CeilingRadiusKm;
                    int hoodIndex = beyond ? stationHood[fl.QueueStation] : hood[nearest];
                    Neighbourhood nb = plan.Neighbourhoods[hoodIndex];
                    double cost = Math.Max(0.0, CostOn(fl, nb.Server, servers));
                    nb.DemandMs += cost;
                    bool cleared = stationTraffic && queues.IsCleared(fl.Key, fl.QueueStation, fl.QueueDeparture);
                    if (beyond && cleared)
                    {
                        // Cleared and on its way in from beyond the radius: nothing to admit yet.
                        seenCleared.Add(fl.Key);
                        nb.DemandMs -= cost;
                        continue;
                    }
                    if (stationTraffic && !cleared)
                    {
                        // Queued: waits for its turn, whether abstract or holding visibly at its slot.
                        bool joined;
                        StationQueues.Entry e = queues.Join(fl.Key, fl.QueueStation, fl.QueueDeparture,
                                                            fl.Divertable, cost, now, out joined, fl.Materialised);
                        seenQueued.Add(fl.Key);
                        plan.Queued.Add(fl.Key);
                        if (joined) plan.Joined.Add(e);
                        nb.DemandMs -= cost;   // counted at its station's neighbourhood below
                        if (fl.Materialised)
                        {
                            nb.UsedMs += cost;
                            if (fl.Sheddable && best >= s.ShedMinDistanceKm)
                                shedable[hoodIndex].Add(new Attributed { Index = f, DistanceKm = best });
                        }
                        else if (!fl.QueueDeparture && !beyond)
                            holding[hoodIndex].Add(new Attributed { Index = f, DistanceKm = best });
                        continue;
                    }
                    if (cleared) seenCleared.Add(fl.Key);
                    if (fl.Materialised || !fl.Civilian)
                    {
                        // Counted whether or not it is gated: combat spawns regardless, and what is
                        // already in the world is already costing.
                        nb.UsedMs += cost;
                        if (fl.Materialised && fl.Sheddable && fl.Civilian && best >= s.ShedMinDistanceKm)
                            shedable[hood[nearest]].Add(new Attributed { Index = f, DistanceKm = best });
                    }
                    else if (cleared)
                        priority[hood[nearest]].Add(new Attributed { Index = f, DistanceKm = best });
                    else
                        candidates[hood[nearest]].Add(new Attributed { Index = f, DistanceKm = best });
                }

            if (queueing) queues.Retain(seenQueued, seenCleared);

            // A queue's demand belongs to the neighbourhood nearest its station.
            if (queueing)
                foreach (string station in queues.Stations)
                {
                    int sh;
                    if (!stationHood.TryGetValue(station, out sh)) continue;
                    Neighbourhood nb = plan.Neighbourhoods[sh];
                    foreach (StationQueues.Entry e in queues.Queue(station))
                    {
                        nb.DemandMs += Math.Max(0.0, e.CostMs);
                        if (e.Departure) nb.QueuedDepartures++; else nb.QueuedArrivals++;
                    }
                }

            ShareByServer(plan.Neighbourhoods, plan.BudgetMs, servers);
            foreach (KeyValuePair<string, int> sh in stationHood)
                plan.StationShareMs[sh.Key] = plan.Neighbourhoods[sh.Value].ShareMs;

            for (int n = 0; n < hoods; n++)
            {
                Neighbourhood nb = plan.Neighbourhoods[n];
                double used = nb.UsedMs;

                // Cleared earlier: admitted first, each on its own merits.
                List<Attributed> pr = priority[n];
                pr.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
                foreach (Attributed a in pr)
                {
                    string key = fleets[a.Index].Key;
                    double cost = Math.Max(0.0, CostOn(fleets[a.Index], nb.Server, servers));
                    if (used + cost <= nb.ShareMs) { used += cost; nb.Admitted++; plan.Admitted.Add(key); }
                    else { nb.Refused++; plan.Refused.Add(key); }
                }

                // Queue heads, oldest first across this neighbourhood's stations, until one does not fit.
                double blockedHeadMs = 0.0;
                if (queueing) used = PopQueues(plan, nb, n, stationHood, queues, used, out blockedHeadMs, s.Closed);

                // Other traffic and ships holding at their slots, nearest first: a holding ship is part
                // of what the player sees, and is admitted to materialise at its slot like any other,
                // but stays queued; one not admitted simply waits unseen.
                List<Attributed> c = candidates[n];
                foreach (Attributed a in holding[n])
                    if (plan.Queued.Contains(fleets[a.Index].Key)) c.Add(a);
                c.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
                bool full = false;
                foreach (Attributed a in c)
                {
                    string key = fleets[a.Index].Key;
                    double cost = Math.Max(0.0, CostOn(fleets[a.Index], nb.Server, servers));
                    bool held = key != null && plan.Queued.Contains(key);
                    // The room a blocked queue head is waiting for is not handed to passing traffic, or
                    // a station's queue never moves while the share is full of other ships. Holders are
                    // the exception: their slots lie past the radius a player at the station sees, so one
                    // is only a candidate when a player has flown out to the holding field to look.
                    if (!full && used + cost + (held ? 0.0 : blockedHeadMs) <= nb.ShareMs)
                    {
                        used += cost;
                        if (held) nb.HoldingVisible++; else nb.Admitted++;
                        if (key != null) plan.Admitted.Add(key);
                        continue;
                    }
                    if (!full) nb.EffectiveRadiusKm = a.DistanceKm;
                    full = true;
                    if (held) continue;
                    nb.Refused++;
                    if (key != null) plan.Refused.Add(key);
                }

                // Over the share, the farthest ships go. A station queue whose head does not fit
                // shrinks the radius too: ships flying away go once they are past the shed distance,
                // so the room they would hold all the way out to the ceiling goes to the queue.
                bool overShare = nb.UsedMs > nb.ShareMs * (1.0 + s.ShedHysteresis);
                bool pressed = blockedHeadMs > 0.0 && nb.UsedMs + blockedHeadMs > nb.ShareMs;
                if (pressed) nb.EffectiveRadiusKm = Math.Min(nb.EffectiveRadiusKm, s.ShedMinDistanceKm);
                if (overShare || pressed)
                {
                    // Ships flying away go before ships coming in to dock, farthest first within each:
                    // a docking ship has usually waited its turn in the queue, and shedding it on the
                    // approach throws that away. Queue pressure alone never sheds one.
                    List<Attributed> sh = shedable[n];
                    sh.Sort((a, b) =>
                    {
                        int byDirection = fleets[a.Index].Inbound.CompareTo(fleets[b.Index].Inbound);
                        return byDirection != 0 ? byDirection : b.DistanceKm.CompareTo(a.DistanceKm);
                    });
                    double left = nb.UsedMs;
                    double target = pressed ? nb.ShareMs - blockedHeadMs : nb.ShareMs;
                    foreach (Attributed a in sh)
                    {
                        if (nb.Shed >= s.MaxShedPerPlan || left <= target) break;
                        string key = fleets[a.Index].Key;
                        if (key == null) continue;
                        if (!overShare && fleets[a.Index].Inbound) continue;
                        plan.Shed.Add(key);
                        left -= Math.Max(0.0, CostOn(fleets[a.Index], nb.Server, servers));
                        nb.Shed++;
                    }
                }
            }
            if (queueing) CollectDue(plan, queues, s, now);
            return plan;
        }

        /// <param name="blockedHeadMs">The cost of the head that did not fit, 0 when every queue
        /// emptied: room the rest of the plan must leave free.</param>
        private static double PopQueues(Plan plan, Neighbourhood nb, int n, Dictionary<string, int> stationHood,
                                        StationQueues queues, double used, out double blockedHeadMs,
                                        ICollection<string> closed = null)
        {
            blockedHeadMs = 0.0;
            while (true)
            {
                StationQueues.Entry head = null;
                foreach (KeyValuePair<string, int> sh in stationHood)
                {
                    if (sh.Value != n) continue;
                    if (closed != null && closed.Contains(sh.Key)) continue;
                    IReadOnlyList<StationQueues.Entry> q = queues.Queue(sh.Key);
                    if (q.Count == 0) continue;
                    if (head == null || q[0].JoinedUtc < head.JoinedUtc) head = q[0];
                }
                if (head == null) return used;
                double cost = head.Materialised ? 0.0 : Math.Max(0.0, head.CostMs);
                if (used + cost > nb.ShareMs) { blockedHeadMs = cost; return used; }
                used += cost;
                queues.Clear(head);
                plan.Queued.Remove(head.Key);
                plan.Cleared.Add(head);
                // Cleared is admitted in the same plan: the room was counted for it just now, and a
                // clearance that has to win admission again a second later can lose it and leave
                // unseen, which is what the queue exists to stop.
                plan.Admitted.Add(head.Key);
                nb.Cleared++;
            }
        }

        private static void CollectDue(Plan plan, StationQueues queues, Settings s, DateTime now)
        {
            foreach (string station in new List<string>(queues.Stations))
                foreach (StationQueues.Entry e in queues.Queue(station))
                {
                    double waited = (now - e.JoinedUtc).TotalSeconds;
                    bool closed = s.Closed != null && s.Closed.Contains(station);
                    if (!e.Departure && e.Divertable)
                    {
                        if (closed || waited >= s.DivertSeconds) plan.DivertDue.Add(e);
                    }
                    else if (!e.DelayReported && waited >= s.DelaySeconds)
                    {
                        e.DelayReported = true;
                        plan.DelayDue.Add(e);
                    }
                }
        }

        /// <summary>Max-min fair by weight: a neighbourhood asking for less than its weighted part of
        /// what is left gets exactly what it asks for, and the others divide the remainder.</summary>
        /// <summary>A fleet's predicted cost in the frame of <paramref name="server"/>.</summary>
        private static double CostOn(Fleet fl, byte server, IDictionary<byte, ServerLoad> servers)
        {
            ServerLoad load;
            if (server == 0 || fl.BaseMs <= 0 || servers == null || !servers.TryGetValue(server, out load))
                return fl.CostMs;
            return fl.BaseMs * (fl.CombatClass ? load.KCombat : load.KCivilian);
        }

        /// <summary>Each server's neighbourhoods divide that server's budget; one with none reported is
        /// unlimited.</summary>
        private static void ShareByServer(List<Neighbourhood> hoods, double localBudgetMs, IDictionary<byte, ServerLoad> servers)
        {
            var byServer = new Dictionary<byte, List<Neighbourhood>>();
            foreach (Neighbourhood nb in hoods)
            {
                List<Neighbourhood> list;
                if (!byServer.TryGetValue(nb.Server, out list)) { list = new List<Neighbourhood>(); byServer[nb.Server] = list; }
                list.Add(nb);
            }
            foreach (KeyValuePair<byte, List<Neighbourhood>> kv in byServer)
            {
                ServerLoad load;
                double budget = kv.Key == 0 ? localBudgetMs
                              : servers != null && servers.TryGetValue(kv.Key, out load) ? Math.Max(0.0, load.BudgetMs)
                              : double.PositiveInfinity;
                Share(kv.Value, budget);
            }
        }

        private static void Share(List<Neighbourhood> hoods, double budgetMs)
        {
            var open = new List<Neighbourhood>(hoods);
            double remaining = budgetMs;
            while (open.Count > 0)
            {
                double weight = 0.0;
                foreach (Neighbourhood nb in open) weight += nb.Weight;
                double pass = remaining;
                bool settled = false;
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    Neighbourhood nb = open[i];
                    double fair = weight > 0 ? pass * nb.Weight / weight : 0.0;
                    if (nb.DemandMs <= fair)
                    {
                        nb.ShareMs = nb.DemandMs;
                        remaining -= nb.DemandMs;
                        open.RemoveAt(i);
                        settled = true;
                    }
                }
                if (settled) continue;
                foreach (Neighbourhood nb in open)
                    nb.ShareMs = weight > 0 ? remaining * nb.Weight / weight : 0.0;
                break;
            }
        }

        /// <summary>Neighbourhood index per player: players within <paramref name="radiusKm"/> of each
        /// other, transitively, share one.</summary>
        private static int[] GroupPlayers(IList<Player> players, double radiusKm)
        {
            int count = players.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    if (players[i].Server == players[j].Server && players[i].PosKm.DistanceTo(players[j].PosKm) <= radiusKm)
                        parent[Find(parent, i)] = Find(parent, j);

            var index = new Dictionary<int, int>();
            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i);
                int n;
                if (!index.TryGetValue(root, out n)) { n = index.Count; index[root] = n; }
                result[i] = n;
            }
            return result;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
    }
}
