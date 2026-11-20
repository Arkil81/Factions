using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;

namespace Factions
{
    /// <summary>One wing command as it reached the server: from a client's toolbar press, a block the
    /// server runs, or the bench. The host fills in what it read of the world before asking
    /// <see cref="FactionRuntime.IssueWing"/>.</summary>
    internal struct WingRequest
    {
        public WingVia Via;
        public WingCommand Command;
        public long AntennaEntityId;
        /// <summary>The presser (client, bench) or the antenna's owner (block).</summary>
        public long CommanderIdentityId;
        public long PresserLock;
        public long Crosshair;
        public long PilotLock;
        public long CombatBlockLock;
        public long CockpitRetained;
        /// <summary>A bench command reaches only this scenario's fleets.</summary>
        public string BenchLabel;
    }

    /// <summary>
    /// The runtime's side of the wing commander (docs/fleet-tactics.md, Package 2): which hulls a
    /// commander's order reaches, the per-hull link, and handing each obeying hull to the flight
    /// controller (<see cref="FleetFlightController.CommandWing"/>).
    ///
    /// <para><b>Who obeys.</b> The materialised hulls — leads and escorts — of the commander's own
    /// player faction, each inside the antenna's current broadcast radius and inside the observation
    /// bubble of some player, read per hull once a second. A hull that loses the link keeps its last
    /// order for <see cref="WingRules.LinkGrace"/>, then reverts to its fleet's own flight and tactics
    /// (<c>WING_LINK_LOST</c>, one line per order per read). The order ends when no hull obeys it,
    /// or on <i>return</i> / <i>release</i>.</para>
    ///
    /// <para><b>What a commanded hull skips.</b> Its fleet's own flight asks — launch, approach,
    /// holding fight, formation on its lead — and the stall clock. Capture, loss and every other
    /// check of the fleet still run, and a hull the order releases is handed back to the cruise
    /// autopilot on its fleet's waypoint, which the fleet's flight re-takes.</para>
    ///
    /// <para><b>Learning.</b> A fleet holding station with an obeying hull is marked
    /// <see cref="PendingFleetAction.Commanded"/> with the order's tactic, so its duel fights that
    /// tactic and records it as a demonstration.</para>
    ///
    /// <para>Nothing here reads a feature switch: every path starts from a player's (or the
    /// operator's) command, and a world with no order is untouched.</para>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private static readonly TimeSpan WingReadInterval = TimeSpan.FromSeconds(1);

        private readonly Dictionary<long, WingOrder> _wingOrders = new Dictionary<long, WingOrder>();
        private readonly Dictionary<long, WingOrder> _wingByHull = new Dictionary<long, WingOrder>();
        private readonly List<WingHull> _wingHullScratch = new List<WingHull>();
        private readonly List<Vec3> _wingObserverScratch = new List<Vec3>();
        private readonly HashSet<long> _wingSeenScratch = new HashSet<long>();
        private readonly List<long> _wingForgetScratch = new List<long>();
        private readonly List<WingOrder> _wingOrderScratch = new List<WingOrder>();
        private DateTime _wingNextReadUtc;
        private Func<Vec3, double, bool> _wingPlayerWithin;

        private struct WingHull
        {
            public long EntityId;
            public FactionState State;
            public int FleetIndex;
            public Vec3 PosM;
        }

        /// <summary>True while a hull obeys a commander's order: its fleet's own flight leaves it alone.</summary>
        internal bool IsCommanded(long entityId)
        {
            return _wingByHull.Count > 0 && _wingByHull.ContainsKey(entityId);
        }

        /// <summary>The per-frame gate for a materialised fleet's lead: when a commander's order flies
        /// it, only the fleet's escorts are flown here and the lead's own flight checks are skipped.</summary>
        private bool FlyCommanded(PendingFleetAction fleet)
        {
            long eid = fleet.MaterialisedEntityId;
            if (!IsCommanded(eid)) return false;
            _stalledSince.Remove(eid);
            _safezoneArrivedSince.Remove(eid);
            FlyEscorts(fleet);
            return true;
        }

        /// <summary>Takes one command: validates it, changes or ends the commander's order, and writes
        /// <c>WING_ORDER</c> or <c>WING_REFUSED</c>. Returns what to tell the presser.</summary>
        internal string IssueWing(WingRequest req, out bool ok)
        {
            ok = false;
            if (req.Command == WingCommand.ProtectMe)
                return CallStation(new StationCallRequest
                {
                    Via = req.Via,
                    AntennaEntityId = req.AntennaEntityId,
                    PresserIdentityId = req.Via == WingVia.Block ? 0 : req.CommanderIdentityId,
                    BenchLabel = req.BenchLabel,
                }, out ok);
            DateTime now = DateTime.UtcNow;
            string tag = WingWorld.FactionTagOf(req.CommanderIdentityId);
            FactionState own = tag != null ? FindState(tag) : null;
            if (own == null || !own.IsPlayerFaction)
                return RefuseWing(req, tag, WingRefusal.NoPlayerFaction);

            long gridId;
            WingAntennaReading antenna = WingWorld.ReadAntenna(req.AntennaEntityId, out gridId);
            WingRefusal refusal = WingRules.CheckAntenna(antenna, req.CommanderIdentityId, own.Tag);
            if (refusal != WingRefusal.None) return RefuseWing(req, own.Tag, refusal);

            WingOrder order;
            _wingOrders.TryGetValue(req.CommanderIdentityId, out order);

            if (WingRules.EndsOrder(req.Command))
            {
                if (order == null) return RefuseWing(req, own.Tag, WingRefusal.NoOrder);
                int obeyed = 0, recalled = 0;
                foreach (KeyValuePair<long, WingLink> kv in order.Links)
                {
                    if (kv.Value.Lapsed) continue;
                    obeyed++;
                    if (EndWingHull(kv.Key, req.Command == WingCommand.Return, now)) recalled++;
                }
                RemoveWingOrder(req.CommanderIdentityId);
                _log.Write("WING_ORDER tag=" + own.Tag + " cmdr=" + req.CommanderIdentityId
                           + " cmd=" + WingRules.Token(req.Command) + " via=" + ViaToken(req.Via)
                           + " antenna=" + req.AntennaEntityId + " wing=" + obeyed
                           + (req.Command == WingCommand.Return ? " recalled=" + recalled : "")
                           + BenchToken(req.BenchLabel));
                ok = true;
                return req.Command == WingCommand.Return
                    ? "Wing: " + obeyed + " hull(s) released, " + recalled + " fleet(s) heading home."
                    : "Wing: " + obeyed + " hull(s) released to their own tactics.";
            }

            CollectWingHulls(own.Tag, req.BenchLabel, false, _wingHullScratch);
            _wingObserverScratch.Clear();
            int inRange = 0;
            for (int i = 0; i < _wingHullScratch.Count; i++)
            {
                if (!WingRules.InRange(antenna, _wingHullScratch[i].PosM) || !InPlayerBubble(_wingHullScratch[i].PosM)) continue;
                inRange++;
                _wingObserverScratch.Add(_wingHullScratch[i].PosM);
            }
            if (inRange == 0) return RefuseWing(req, own.Tag, WingRefusal.NoHulls);

            long target = 0;
            WingTargetSource source = WingTargetSource.None;
            if (WingRules.NeedsTarget(req.Command))
            {
                target = WingRules.PickTarget(req.Via, req.PresserLock, req.Crosshair, req.PilotLock,
                                              req.CombatBlockLock, req.CockpitRetained, out source);
                WingTargetReading t = WingWorld.ReadTarget(target, req.CommanderIdentityId);
                Vec3 commanderPos = GridPosM(gridId) ?? antenna.PosM;
                refusal = WingRules.CheckTarget(t, commanderPos, _wingObserverScratch);
                if (refusal != WingRefusal.None) return RefuseWing(req, own.Tag, refusal);
            }

            if (order == null || order.AntennaEntityId != req.AntennaEntityId)
            {
                if (order != null) RemoveWingOrder(req.CommanderIdentityId, now);
                order = new WingOrder
                {
                    CommanderIdentityId = req.CommanderIdentityId,
                    FactionTag = own.Tag,
                    AntennaEntityId = req.AntennaEntityId,
                    BenchLabel = req.BenchLabel,
                };
                _wingOrders[req.CommanderIdentityId] = order;
            }
            order.Via = req.Via;
            order.BenchLabel = req.BenchLabel;
            order.Cut = false;
            order.Apply(req.Command, gridId, target, now);
            TakeHullsFromOtherOrders(order, antenna);
            ReadWingOrder(order, now);

            int obeying = 0;
            foreach (KeyValuePair<long, WingLink> kv in order.Links)
                if (!kv.Value.Lapsed) obeying++;
            _log.Write("WING_ORDER tag=" + own.Tag + " cmdr=" + req.CommanderIdentityId
                       + " cmd=" + WingRules.Token(req.Command) + " via=" + ViaToken(req.Via)
                       + " antenna=" + req.AntennaEntityId
                       + " radius=" + antenna.RadiusM.ToString("0", CultureInfo.InvariantCulture)
                       + " wing=" + obeying
                       + " anchor=" + order.AnchorEntityId
                       + " target=" + order.TargetEntityId
                       + (WingRules.NeedsTarget(req.Command) ? " source=" + WingRules.Token(source) : "")
                       + BenchToken(order.BenchLabel));
            ok = true;
            switch (req.Command)
            {
                case WingCommand.FormOnMe:    return "Wing: " + obeying + " hull(s) forming on you.";
                case WingCommand.FocusTarget: return "Wing: " + obeying + " hull(s) attacking your target.";
                case WingCommand.Hold:        return "Wing: " + obeying + " hull(s) holding.";
                default:                      return "Wing: " + obeying + " hull(s) breaking off.";
            }
        }

        /// <summary>A hull answers the latest order that reaches it: hulls inside this order's range
        /// leave any other order of the same faction.</summary>
        private void TakeHullsFromOtherOrders(WingOrder order, WingAntennaReading antenna)
        {
            foreach (WingOrder other in _wingOrders.Values)
            {
                if (other == order || !string.Equals(other.FactionTag, order.FactionTag, StringComparison.OrdinalIgnoreCase)) continue;
                for (int i = 0; i < _wingHullScratch.Count; i++)
                {
                    long eid = _wingHullScratch[i].EntityId;
                    if (!WingRules.InRange(antenna, _wingHullScratch[i].PosM)) continue;
                    other.Forget(eid);
                    WingOrder claimed;
                    if (_wingByHull.TryGetValue(eid, out claimed) && claimed == other) _wingByHull.Remove(eid);
                }
            }
        }

        private string RefuseWing(WingRequest req, string tag, WingRefusal refusal)
        {
            _log.Write("WING_REFUSED tag=" + (tag ?? "none") + " cmdr=" + req.CommanderIdentityId
                       + " cmd=" + WingRules.Token(req.Command) + " via=" + ViaToken(req.Via)
                       + " antenna=" + req.AntennaEntityId + " reason=" + WingRules.Token(refusal)
                       + BenchToken(req.BenchLabel));
            return "Wing refused: " + RefusalText(refusal);
        }

        private static string RefusalText(WingRefusal refusal)
        {
            switch (refusal)
            {
                case WingRefusal.NoAntenna:         return "that antenna is gone.";
                case WingRefusal.AntennaDamaged:    return "the antenna is damaged.";
                case WingRefusal.AntennaOff:        return "the antenna is switched off.";
                case WingRefusal.AntennaUnpowered:  return "the antenna has no power.";
                case WingRefusal.NotBroadcasting:   return "the antenna is not broadcasting.";
                case WingRefusal.NotYourAntenna:    return "the antenna is not yours or your faction's.";
                case WingRefusal.NoPlayerFaction:   return "you are not in a Factions player faction.";
                case WingRefusal.NoHulls:           return "none of your faction's hulls is inside the antenna's range.";
                case WingRefusal.NoTarget:          return "no target — lock one, or put it under your crosshair.";
                case WingRefusal.TargetGone:        return "the target is gone.";
                case WingRefusal.TargetNotHostile:  return "the target is not hostile.";
                case WingRefusal.TargetOutOfBubble: return "the target is too far away.";
                case WingRefusal.TargetNotVisible:  return "nobody in the wing can see the target.";
                case WingRefusal.NoOrder:           return "there is no wing order to end.";
                default:                            return WingRules.Token(refusal) + ".";
            }
        }

        private static string ViaToken(WingVia via)
        {
            switch (via)
            {
                case WingVia.Block: return "block";
                case WingVia.Bench: return "bench";
                case WingVia.Host:  return "host";
                default:            return "client";
            }
        }

        private static string BenchToken(string label)
        {
            return label != null ? " bench=" + label : "";
        }

        /// <summary>The once-a-second wing read, from the observation pass.</summary>
        private void TickWing(DateTime now, Func<Vec3, double, bool> isPlayerWithin)
        {
            _wingPlayerWithin = isPlayerWithin;
            TickStationCalls(now);
            if (_wingOrders.Count == 0)
            {
                if (_wingByHull.Count > 0) _wingByHull.Clear();
                return;
            }
            if (now < _wingNextReadUtc) return;
            _wingNextReadUtc = now + WingReadInterval;

            // Newest first, so a hull two orders reach answers the one issued last.
            _wingByHull.Clear();
            _wingOrderScratch.Clear();
            _wingOrderScratch.AddRange(_wingOrders.Values);
            _wingOrderScratch.Sort((a, b) => b.IssuedUtc.CompareTo(a.IssuedUtc));
            for (int i = 0; i < _wingOrderScratch.Count; i++)
            {
                WingOrder o = _wingOrderScratch[i];
                ReadWingOrder(o, now);
                // A station's call stands with nobody answering it yet; its own read ends it.
                if (o.StationGridId == 0 && !o.AnyObeys()) _wingOrders.Remove(o.Key);
            }
        }

        /// <summary>One read of one order: the antenna, the target, each hull's link, and the
        /// directive for every hull that obeys.</summary>
        private void ReadWingOrder(WingOrder order, DateTime now)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            long gridId;
            WingAntennaReading antenna = WingWorld.ReadAntenna(order.AntennaEntityId, out gridId);
            WingRefusal carried = order.Cut
                ? WingRefusal.Cut
                : WingRules.CheckAntenna(antenna, order.CommanderIdentityId, order.FactionTag);

            CollectWingHulls(order.FactionTag, order.BenchLabel, order.StationGridId != 0, _wingHullScratch);

            if (order.TargetEntityId != 0)
            {
                _wingObserverScratch.Clear();
                for (int i = 0; i < _wingHullScratch.Count; i++)
                    if (order.Obeys(_wingHullScratch[i].EntityId)) _wingObserverScratch.Add(_wingHullScratch[i].PosM);
                WingTargetReading t = WingWorld.ReadTarget(order.TargetEntityId, order.CommanderIdentityId);
                Vec3 commanderPos = GridPosM(gridId) ?? antenna.PosM;
                if (WingRules.CheckTarget(t, commanderPos, _wingObserverScratch) != WingRefusal.None)
                    order.TargetEntityId = 0;
            }

            // Formation slots in entity-id order among the hulls that obey, so a slot does not move
            // while the wing is unchanged.
            _wingHullScratch.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            _wingSeenScratch.Clear();
            int lapsed = 0, slot = 0;
            WingRefusal lapseReason = WingRefusal.None;
            for (int i = 0; i < _wingHullScratch.Count; i++)
            {
                WingHull h = _wingHullScratch[i];
                WingOrder claimed;
                if (_wingByHull.TryGetValue(h.EntityId, out claimed) && claimed != order) continue;
                _wingSeenScratch.Add(h.EntityId);
                WingRefusal link = carried;
                if (link == WingRefusal.None && !WingRules.InRange(antenna, h.PosM)) link = WingRefusal.OutOfRange;
                if (link == WingRefusal.None && !InPlayerBubble(h.PosM)) link = WingRefusal.OutOfBubble;

                WingLinkChange change = order.UpdateLink(h.EntityId, link == WingRefusal.None, link, now);
                if (change == WingLinkChange.Lapsed)
                {
                    lapsed++;
                    lapseReason = LostReasonOf(order, h.EntityId, link);
                    PendingFleetAction lapsedFleet = h.State.PendingFleets[h.FleetIndex];
                    if (vanilla != null) vanilla.EndWing(h.EntityId, lapsedFleet.TargetPosKm);
                    continue;
                }
                if (!order.Obeys(h.EntityId)) continue;

                PendingFleetAction fleet = h.State.PendingFleets[h.FleetIndex];
                _wingByHull[h.EntityId] = order;

                // What the order does not decide — the arm under a focus or a formation, the
                // break-off unless it is breaking off — is the fleet's own learned pick.
                TacticOrder? learned = LearnedOrder(h.State, h.FleetIndex, fleet, now);
                TacticArm learnedArm = learned.HasValue ? learned.Value.Arm : TacticResolver.DefaultArm;
                BreakOffLevel learnedBreakOff = learned.HasValue ? learned.Value.BreakOff : TacticResolver.DefaultBreakOff;
                TacticOrder tactic = order.ToTacticOrder(learnedArm, learnedBreakOff);
                CommandedTactic commanded = order.Tactic(learnedArm, learnedBreakOff);
                if (vanilla != null)
                    vanilla.CommandWing(h.EntityId, new WingDirective
                    {
                        AnchorId = order.AnchorEntityId,
                        TargetId = order.TargetEntityId,
                        Holding = order.Holding,
                        BreakingOff = order.BreakingOff,
                        Order = tactic,
                        Slot = slot++,
                        Label = fleet.LogId,
                        FleetBands = fleet.WeaponBands,
                    });
                // A station's call flies the fleet's own tactic, so its fight is learned as its own.
                if (order.StationGridId == 0 && fleet.HoldsStation(now, _tuning))
                {
                    fleet.Commanded = commanded;
                    h.State.PendingFleets[h.FleetIndex] = fleet;
                }
            }

            // Hulls that have left the world: their fleet's own checks settle them.
            _wingForgetScratch.Clear();
            foreach (KeyValuePair<long, WingLink> kv in order.Links)
                if (!_wingSeenScratch.Contains(kv.Key)) _wingForgetScratch.Add(kv.Key);
            for (int i = 0; i < _wingForgetScratch.Count; i++) order.Forget(_wingForgetScratch[i]);

            if (lapsed > 0)
                _log.Write("WING_LINK_LOST tag=" + order.FactionTag + " cmdr=" + order.CommanderIdentityId
                           + " antenna=" + order.AntennaEntityId + " reason=" + WingRules.Token(lapseReason)
                           + " hulls=" + lapsed + " grace=" + WingRules.LinkGrace.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                           + "s -> own tactics" + BenchToken(order.BenchLabel));
        }

        private static WingRefusal LostReasonOf(WingOrder order, long hullId, WingRefusal fallback)
        {
            foreach (KeyValuePair<long, WingLink> kv in order.Links)
                if (kv.Key == hullId && kv.Value.LostReason != WingRefusal.None) return kv.Value.LostReason;
            return fallback;
        }

        /// <summary>Ends the order on every hull that obeys it and forgets it.</summary>
        private void RemoveWingOrder(long commanderIdentityId, DateTime now)
        {
            WingOrder order;
            if (!_wingOrders.TryGetValue(commanderIdentityId, out order)) return;
            foreach (KeyValuePair<long, WingLink> kv in order.Links)
                if (!kv.Value.Lapsed) EndWingHull(kv.Key, false, now);
            RemoveWingOrder(commanderIdentityId);
        }

        private void RemoveWingOrder(long commanderIdentityId)
        {
            WingOrder order;
            if (!_wingOrders.TryGetValue(commanderIdentityId, out order)) return;
            _wingOrders.Remove(commanderIdentityId);
            var drop = new List<long>();
            foreach (KeyValuePair<long, WingOrder> kv in _wingByHull)
                if (kv.Value == order) drop.Add(kv.Key);
            for (int i = 0; i < drop.Count; i++) _wingByHull.Remove(drop[i]);
        }

        /// <summary>Releases one hull from the order back to its fleet. With
        /// <paramref name="recall"/>, its fleet also breaks off its dwell or duty and heads home
        /// (<see cref="PendingFleetResolver.BeginRetreat"/>); returns whether it did.</summary>
        private bool EndWingHull(long hullId, bool recall, DateTime now)
        {
            _wingByHull.Remove(hullId);
            FactionState st;
            int fi;
            if (!FindFleetOfHull(hullId, out st, out fi)) return false;
            PendingFleetAction fleet = st.PendingFleets[fi];
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla != null) vanilla.EndWing(hullId, fleet.TargetPosKm);
            if (!recall || !PendingFleetResolver.BeginRetreat(ref fleet, now, _tuning, "commanded")) return false;
            st.PendingFleets[fi] = fleet;
            _log.Write("FLEET_RETREATING " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                       + " eid=" + fleet.MaterialisedEntityId + " @" + (fleet.TargetNodeId ?? "?")
                       + " reason=commanded");
            return true;
        }

        private bool FindFleetOfHull(long hullId, out FactionState state, out int fleetIndex)
        {
            foreach (FactionState st in _states)
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.MaterialisedEntityId == hullId
                        || (f.EscortEntityIds != null && f.EscortEntityIds.Contains(hullId)))
                    {
                        state = st;
                        fleetIndex = fi;
                        return true;
                    }
                }
            state = null;
            fleetIndex = -1;
            return false;
        }

        /// <summary>Every materialised hull — lead and escorts — of <paramref name="factionTag"/>'s
        /// fleets, a player faction's only, with its world position. A bench label narrows it to that
        /// scenario's fleets. For a station's call (<paramref name="stationCall"/>) the faction may be
        /// a player's or an NPC's, and only armed fleets answer.</summary>
        private void CollectWingHulls(string factionTag, string benchLabel, bool stationCall, List<WingHull> into)
        {
            into.Clear();
            if (_spawner == null || string.IsNullOrEmpty(factionTag)) return;
            foreach (FactionState st in _states)
            {
                if (st == null || (!stationCall && !st.IsPlayerFaction)
                    || !string.Equals(st.Tag, factionTag, StringComparison.OrdinalIgnoreCase)) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Intercepted || f.MaterialisedEntityId <= 0) continue;
                    if (stationCall && (f.WeaponBands == null || f.WeaponBands.Count == 0)) continue;
                    if (benchLabel != null && !string.Equals(f.BenchLabel, benchLabel, StringComparison.OrdinalIgnoreCase)) continue;
                    AddWingHull(st, fi, f.MaterialisedEntityId, into);
                    if (f.EscortEntityIds != null)
                        for (int i = 0; i < f.EscortEntityIds.Count; i++)
                            if (f.EscortEntityIds[i] > 0) AddWingHull(st, fi, f.EscortEntityIds[i], into);
                }
            }
        }

        private void AddWingHull(FactionState st, int fi, long eid, List<WingHull> into)
        {
            Vec3? km = _spawner.GetGridPositionKm(eid);
            if (!km.HasValue) return;
            into.Add(new WingHull
            {
                EntityId = eid, State = st, FleetIndex = fi,
                PosM = new Vec3(km.Value.X * 1000.0, km.Value.Y * 1000.0, km.Value.Z * 1000.0),
            });
        }

        private Vec3? GridPosM(long gridId)
        {
            if (gridId == 0 || _spawner == null) return null;
            Vec3? km = _spawner.GetGridPositionKm(gridId);
            if (!km.HasValue) return null;
            return new Vec3(km.Value.X * 1000.0, km.Value.Y * 1000.0, km.Value.Z * 1000.0);
        }

        /// <summary>Whether a player is within the observation radius: a hull nobody is near is left
        /// to its fleet, which dematerialises it.</summary>
        private bool InPlayerBubble(Vec3 posM)
        {
            Func<Vec3, double, bool> within = _wingPlayerWithin;
            if (within == null || _tuning == null) return true;
            return within(new Vec3(posM.X / 1000.0, posM.Y / 1000.0, posM.Z / 1000.0), _tuning.ObservationRadiusKm);
        }

        // ---- bench -----------------------------------------------------------------------------

        /// <summary><c>bench wing &lt;label&gt; cut</c>: every order reaching that scenario reads its
        /// link as lost, as an antenna switched off would; null when none does.</summary>
        internal string BenchWingCut(string label)
        {
            int n = 0;
            foreach (WingOrder o in _wingOrders.Values)
                if (string.Equals(o.BenchLabel, label, StringComparison.OrdinalIgnoreCase))
                {
                    o.Cut = true;
                    n++;
                }
            return n > 0
                ? "link cut on " + n + " order(s); the wing keeps its order for "
                  + WingRules.LinkGrace.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                  + " s, then reverts (WING_LINK_LOST reason=cut)."
                : null;
        }

        /// <summary><c>bench wing &lt;label&gt; status</c>: each order reaching the scenario, and each
        /// hull's link.</summary>
        internal string BenchWingStatus(string label)
        {
            var sb = new StringBuilder();
            DateTime now = DateTime.UtcNow;
            foreach (WingOrder o in _wingOrders.Values)
            {
                if (!string.Equals(o.BenchLabel, label, StringComparison.OrdinalIgnoreCase)) continue;
                long gridId;
                WingAntennaReading a = WingWorld.ReadAntenna(o.AntennaEntityId, out gridId);
                WingRefusal carried = o.Cut ? WingRefusal.Cut : WingRules.CheckAntenna(a, o.CommanderIdentityId, o.FactionTag);
                sb.Append("\n  cmdr=").Append(o.CommanderIdentityId).Append(' ').Append(o.FactionTag)
                  .Append(" last=").Append(WingRules.Token(o.LastCommand))
                  .Append(" antenna=").Append(o.AntennaEntityId).Append(" link=").Append(WingRules.Token(carried))
                  .Append(" radius=").Append(a.RadiusM.ToString("0", CultureInfo.InvariantCulture))
                  .Append(" anchor=").Append(o.AnchorEntityId).Append(" target=").Append(o.TargetEntityId)
                  .Append(o.Holding ? " holding" : "").Append(o.BreakingOff ? " breaking_off" : "");
                foreach (KeyValuePair<long, WingLink> kv in o.Links)
                {
                    Vec3? pos = GridPosM(kv.Key);
                    sb.Append("\n    hull=").Append(kv.Key);
                    if (kv.Value.Lapsed) sb.Append(" lapsed");
                    else if (kv.Value.LostUtc.HasValue)
                        sb.Append(" grace_left=").Append(Math.Max(0, (WingRules.LinkGrace - (now - kv.Value.LostUtc.Value)).TotalSeconds)
                                                             .ToString("0", CultureInfo.InvariantCulture)).Append('s')
                          .Append(" lost=").Append(WingRules.Token(kv.Value.LostReason));
                    else sb.Append(" linked");
                    if (pos.HasValue && a.Exists)
                        sb.Append(" d=").Append(pos.Value.DistanceTo(a.PosM).ToString("0", CultureInfo.InvariantCulture)).Append('m');
                }
            }
            return sb.Length > 0 ? "wing " + label + ":" + sb : null;
        }
    }
}
