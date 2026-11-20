using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// The host half of the two-level reputation model
    /// (docs/player-progression.md → Reputation and standing).
    ///
    /// <para>Two jobs, in opposite directions. <b>Reading:</b> it is the live
    /// <see cref="IMemberStandingSource"/> — it turns SE's own player↔faction reputations for a
    /// player faction's roster into the one aggregate <see cref="Diplomacy.Drift"/> senses.
    /// <b>Writing:</b> it accrues player trade into <see cref="TradeReputationLedger"/> and grants a
    /// step of SE reputation each time the threshold is crossed.</para>
    ///
    /// <para><b>Aggression is deliberately not written here.</b> SE already degrades player
    /// reputation on damage by itself, from six call sites — <c>MyShipGrinder</c> and
    /// <c>MyAngleGrinder</c> (grinding), <c>MySlimBlock.DoDamage</c> and <c>MyExplosion</c>
    /// (damage), <c>MyCharacter</c> (killing) — resolving the victim faction from the grid's
    /// <c>BigOwners[0]</c> and already passing <c>propagate: false</c>. A hostility writer of ours
    /// would double-count against SE's own <c>DamageInflicted</c> accumulator. That is the whole
    /// finding of FAC-182, and it is why this class only ever writes the trade side.</para>
    ///
    /// <para><b>Member reputation display (docs/diplomacy-plan.md rule 4).</b> While a player is a
    /// member of a registered player faction, SE's personal-reputation field shows each managed NPC's
    /// standing toward that faction, and the member's own record lives in
    /// <see cref="MemberReputationBook"/> (<c>member_reputation.json</c>). The rules are Core's
    /// <see cref="MemberReputationSync"/>; this class only reads and writes SE for it, captures SE's
    /// conduct deltas into it (<see cref="PersonalReputationConductPatch"/> and
    /// <c>FactionReputationChanged</c>), and runs its pass on the economy quantum.</para>
    ///
    /// <para>Game thread only: every SE faction and reputation call below requires it.</para>
    /// </summary>
    internal sealed class PlayerReputationManager : IMemberStandingSource
    {
        /// <summary>The live instance, for the Harmony patch that feeds it conduct. Null outside a
        /// session.</summary>
        internal static PlayerReputationManager Current;

        /// <summary>Set by <c>factions debug repsync</c>: REPSYNC_SEED / _PROJECT / _CAPTURE /
        /// _WRITEBACK / _PASS lines, one per change. Off by default.</summary>
        internal static bool DebugRepSync;

        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Set by <c>factions debug reputation</c>. Off by default like every other trace —
        /// the Torch log is a diagnostic channel, and a line per store transaction is not free.</summary>
        internal static bool DebugEnabled;

        private readonly FactionRuntime _runtime;
        private readonly FactionTuning _tuning;
        private readonly TradeReputationLedger _ledger;
        private readonly Func<long, DateTime, DateTime?> _lastSeen;
        private readonly SePersonalReputationField _field = new SePersonalReputationField();
        private readonly MemberReputationSync _sync;
        // True when member_reputation.json exists and could not be read. Projection is refused rather
        // than reseeded: the fields it would seed from show faction standings, not personal records.
        private readonly bool _bookUnreadable;
        private readonly object _gate = new object();
        private bool _attached;
        private MemberReputationPassResult _lastPass;
        private DateTime _lastPassUtc;

        // Last computed aggregate per (npc, player) pair. Two purposes: it spares Drift an
        // O(members) sweep of SE lookups on every economy quantum, and it is what "zero activity
        // holds its last value" actually holds -- a pair whose roster has gone empty keeps answering
        // with what it last knew instead of falling back to neutral.
        private readonly Dictionary<string, double> _aggregates =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _computedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private static readonly TimeSpan AggregateTtl = TimeSpan.FromSeconds(60);

        public PlayerReputationManager(FactionRuntime runtime, FactionTuning tuning, string storageDir,
                                       Func<long, DateTime, DateTime?> lastSeen)
        {
            _runtime = runtime;
            _tuning = tuning;
            _ledger = new TradeReputationLedger(storageDir);
            _lastSeen = lastSeen;

            MemberReputationBook book;
            try { book = new MemberReputationBook(storageDir); }
            catch (Exception ex)
            {
                _bookUnreadable = true;
                book = new MemberReputationBook(null);
                Log.Error(ex, "Member reputation: " + MemberReputationBook.FileName + " could not be read. "
                        + "Player-faction members' SE reputation is NOT being projected or written back "
                        + "until the file is repaired or removed; reseeding from SE's field would replace "
                        + "each member's own record with their faction's standing.");
            }
            _sync = new MemberReputationSync(book, _field);
        }

        // ---- lifecycle --------------------------------------------------------------------------

        /// <summary>Subscribes to SE's reputation and membership events. Game thread, session loaded.</summary>
        public void Attach()
        {
            if (_attached || MySession.Static == null || MySession.Static.Factions == null) return;
            MyFactionCollection factions = MySession.Static.Factions;
            factions.FactionReputationChanged += OnSeReputationChanged;
            factions.OnPlayerJoined += OnPlayerJoined;
            factions.OnPlayerLeft += OnPlayerLeft;
            factions.FactionStateChanged += OnFactionStateChanged;
            _attached = true;
            Current = this;
        }

        /// <summary>Unsubscribes and persists. The records outlive the session: SE's save holds the
        /// projected fields, and the next session resumes from this file.</summary>
        public void Detach()
        {
            if (Current == this) Current = null;
            if (_attached && MySession.Static != null && MySession.Static.Factions != null)
            {
                MyFactionCollection factions = MySession.Static.Factions;
                factions.FactionReputationChanged -= OnSeReputationChanged;
                factions.OnPlayerJoined -= OnPlayerJoined;
                factions.OnPlayerLeft -= OnPlayerLeft;
                factions.FactionStateChanged -= OnFactionStateChanged;
            }
            _attached = false;
            SaveBook();
        }

        // ---- reading: IMemberStandingSource -----------------------------------------------------

        /// <summary>The aggregate <paramref name="npcFactionTag"/> holds toward
        /// <paramref name="playerFactionTag"/>. False unless the target really is a registered player
        /// faction and something is known about its roster.</summary>
        public bool TryGetAggregate(string npcFactionTag, string playerFactionTag, out double aggregate)
        {
            aggregate = 0.0;
            if (string.IsNullOrEmpty(npcFactionTag) || string.IsNullOrEmpty(playerFactionTag)) return false;
            if (_runtime == null) return false;

            FactionState target = _runtime.FindFaction(playerFactionTag);
            if (target == null || !target.IsPlayerFaction) return false;

            string key = npcFactionTag + "|" + playerFactionTag;
            DateTime now = DateTime.UtcNow;

            DateTime computed;
            if (_computedAt.TryGetValue(key, out computed) && now - computed < AggregateTtl)
                return _aggregates.TryGetValue(key, out aggregate);

            double fresh;
            if (TryComputeAggregate(npcFactionTag, playerFactionTag, now, out fresh))
            {
                _aggregates[key] = fresh;
                _computedAt[key] = now;
                aggregate = fresh;
                return true;
            }

            // Nothing to recompute from -- an empty roster, or a scale the world does not carry.
            // Hold the last value rather than answering neutral: resetting on absence would be a
            // laundering path. Not restamped, so a roster that comes back is picked up at once.
            return _aggregates.TryGetValue(key, out aggregate);
        }

        private bool TryComputeAggregate(string npcFactionTag, string playerFactionTag,
                                         DateTime nowUtc, out double aggregate)
        {
            aggregate = 0.0;
            if (MySession.Static == null || MySession.Static.Factions == null) return false;

            ReputationScale scale = _runtime.CurrentReputationScale();
            if (scale == null) return false;

            MyFaction npc = MySession.Static.Factions.TryGetFactionByTag(npcFactionTag);
            MyFaction player = MySession.Static.Factions.TryGetFactionByTag(playerFactionTag);
            if (npc == null || player == null) return false;

            var ids = new List<long>();
            foreach (var kv in player.Members)
            {
                long identityId = kv.Key;
                if (identityId == 0) continue;
                // Station crew are faction members SE never marks NPC (FAC-179). They have no
                // conduct and no activity, and counting them would dilute the roster with neutrals.
                if (MySession.Static.Players.IdentityIsNpc(identityId)) continue;
                ids.Add(identityId);
            }

            // Personal records, not SE's field: while someone is a member that field shows this very
            // standing, and reading it back would close the loop standing -> field -> aggregate.
            List<MemberReputation> roster;
            lock (_gate)
                roster = MemberStandingRoster.Build(ids, npcFactionTag, _sync.Book, _field, scale,
                    id => _lastSeen != null ? _lastSeen(id, nowUtc) : null);

            return MemberStandingAggregate.TryCompute(
                roster, nowUtc, _tuning.MemberStandingAlpha, _tuning.MemberStandingHalfLifeDays,
                out aggregate);
        }

        // ---- writing: the trade accumulator -----------------------------------------------------

        /// <summary>
        /// One completed player store transaction at a Factions-managed station.
        /// </summary>
        /// <param name="identityId">The buyer. 0 (unattributed) records nothing — the goods and the
        /// credits still moved, only the attribution is absent.</param>
        /// <param name="factionTag">The station's owning faction.</param>
        /// <param name="creditsDelta">The faction's real SE wallet change. Signed; only its
        /// magnitude is used, because direction comes from <paramref name="isBuy"/>.</param>
        /// <param name="isBuy">True when the player bought from the station (drawing it down), false
        /// when they sold into it (supplying it). Taken from the direction the patch observed rather
        /// than from the sign of the delta, which is 0 for a free item.</param>
        public void RecordTrade(long identityId, string factionTag, long creditsDelta, bool isBuy)
        {
            if (identityId == 0 || string.IsNullOrEmpty(factionTag)) return;
            if (_tuning == null || _tuning.TradeReputationThreshold <= 0.0) return;
            // A player faction holds no SE reputation (GenerateChanges returns nothing for a
            // PlayerMade target), so trade at a player's station -- their own included -- earns
            // nothing and accrues nothing.
            if (IsPlayerFactionTag(factionTag)) return;

            double magnitude = Math.Abs((double)creditsDelta);
            if (magnitude <= 0.0) return;

            // Provision outweighs consumption: bringing a faction what it lacks is worth more
            // standing than buying what it has spare.
            double weight = isBuy ? _tuning.TradeConsumptionWeight : _tuning.TradeProvisionWeight;
            if (weight <= 0.0) return;

            DateTime now = DateTime.UtcNow;
            int steps = _ledger.Accrue(identityId, factionTag, magnitude * weight,
                                       _tuning.TradeReputationThreshold, now);
            if (steps <= 0)
            {
                if (DebugEnabled)
                    Log.Info("REP_TRADE identity=" + identityId + " faction=" + factionTag
                           + " credited=" + (long)(magnitude * weight)
                           + " remainder=" + (long)_ledger.RemainderFor(identityId, factionTag)
                           + " steps=0");
                return;
            }

            int delta = steps * _tuning.TradeReputationStep;
            int granted;
            string to;
            if (TryCreditMemberRecord(identityId, factionTag, delta, out granted)) to = "record";
            else { granted = GrantReputation(identityId, factionTag, delta); to = "se"; }
            if (DebugEnabled || granted != 0)
                Log.Info("REP_TRADE identity=" + identityId + " faction=" + factionTag
                       + " credited=" + (long)(magnitude * weight)
                       + " steps=" + steps + " delta=" + granted + " to=" + to
                       + " remainder=" + (long)_ledger.RemainderFor(identityId, factionTag));
        }

        /// <summary>A member whose SE field shows their faction's standing earns into their stored
        /// record instead, by the amount SE would have applied (its owner multiplier included), so
        /// trade stays conduct that counts toward the aggregate and survives leaving.</summary>
        /// <returns>False when the pair is not recorded — the field is then still the player's own
        /// record and the grant goes to SE as before.</returns>
        private bool TryCreditMemberRecord(long identityId, string factionTag, int delta, out int applied)
        {
            applied = 0;
            lock (_gate)
            {
                int before;
                if (!_sync.Book.TryGet(identityId, factionTag, out before)) return false;
                ArmTrace();
                _sync.CaptureAdd(identityId, factionTag,
                    MemberReputationSync.ScaleDelta(_field.OwnerMultiplier(), delta), ReputationChangeKind.None);
                int after;
                _sync.Book.TryGet(identityId, factionTag, out after);
                applied = after - before;
            }
            return true;
        }

        private bool IsPlayerFactionTag(string tag)
        {
            FactionState st = _runtime != null ? _runtime.FindFaction(tag) : null;
            if (st != null && st.IsPlayerFaction) return true;
            if (MySession.Static == null || MySession.Static.Factions == null) return false;
            MyFaction f = MySession.Static.Factions.TryGetFactionByTag(tag);
            return f != null && f.FactionType == MyFactionTypes.PlayerMade;
        }

        /// <summary>Moves one player's SE reputation with one faction.
        ///
        /// <para><c>propagate: false</c> always. With propagation on, one positive delta toward a
        /// single faction also moves this player's standing with every other non-player faction,
        /// scaled by that faction's relation to the target — on this world, up to fourteen changes
        /// from one trade, smeared across a roster whose relations Factions is itself simulating.</para>
        ///
        /// <para><c>ReputationChangeReason.None</c>, not <c>Decay</c>: this IS the player's own doing
        /// and they should see the notification. <c>Decay</c> is reserved for simulation churn the
        /// player was not part of. Not <c>Contract</c> either — that value is exempt from SE's own
        /// pirate-gain rate limit, which trade has no business bypassing.</para></summary>
        /// <returns>The delta actually applied; 0 when SE refused it (a PlayerMade or
        /// StaticReputation target, or no session).</returns>
        private int GrantReputation(long identityId, string factionTag, int delta)
        {
            if (delta == 0) return 0;
            if (MySession.Static == null || MySession.Static.Factions == null) return 0;

            MyFaction faction = MySession.Static.Factions.TryGetFactionByTag(factionTag);
            if (faction == null) return 0;

            bool applied = MySession.Static.Factions.AddFactionPlayerReputation(
                identityId, faction.FactionId, delta, ReputationChangeReason.None, propagate: false);
            return applied ? delta : 0;
        }

        // ---- member reputation display (diplomacy-plan.md rule 4) --------------------------------

        /// <summary>The economy-quantum pass, from <see cref="FactionRuntime.AfterDiplomacyPass"/>:
        /// release former members, seed new ones, and project each managed NPC's standing onto every
        /// member's SE field. Bounded by <c>MemberReputationSyncMaxWritesPerPass</c>; one read per
        /// (member, NPC) otherwise, which is dictionary lookups.</summary>
        public void OnEconomyQuantum()
        {
            if (_bookUnreadable || _runtime == null || _tuning == null) return;
            if (MySession.Static == null || MySession.Static.Factions == null) return;

            lock (_gate)
            {
                ArmTrace();
                MemberReputationPassResult result;
                if (!_tuning.MemberReputationSyncEnabled)
                {
                    if (_sync.Book.IdentityCount == 0) return;
                    result = new MemberReputationPassResult { Released = _sync.ReleaseAll() };
                }
                else
                {
                    ReputationScale scale = _runtime.CurrentReputationScale();
                    if (scale == null) return;

                    var byTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
                    foreach (FactionState st in _runtime.States)
                        if (st != null && !string.IsNullOrEmpty(st.Tag)) byTag[st.Tag] = st;

                    result = _sync.RunPass(CollectMembers(), ManagedNpcTags(), (npc, player) =>
                        {
                            FactionState st;
                            return byTag.TryGetValue(npc, out st) ? st.RelationTo(player) : 0.0;
                        },
                        scale, _tuning.HostileRelationThreshold, _tuning.AllyRelationThreshold,
                        _tuning.MemberReputationSyncMinDelta, _tuning.MemberReputationSyncMaxWritesPerPass);
                }
                _lastPass = result;
                _lastPassUtc = DateTime.UtcNow;

                if (DebugRepSync && (result.Seeded > 0 || result.Written > 0 || result.Resent > 0
                                     || result.Deferred > 0 || result.Released > 0))
                    Log.Info("REPSYNC_PASS members=" + result.Members + " seeded=" + result.Seeded
                           + " written=" + result.Written + " resent=" + result.Resent
                           + " deferred=" + result.Deferred + " released=" + result.Released);
            }
            // Before any SE save can capture a projected field: a record seeded this pass must be on
            // disk no later than the value that overwrote the field it came from.
            SaveBook();
        }

        /// <summary>
        /// From <see cref="VanillaDiplomacyBroadcast.Raise"/>, after it raised a state change between
        /// two SE factions. When the act runs SE's member loop on every client
        /// (<see cref="VanillaDiplomacy.RunsMemberLoop"/>) and the pair is a registered player
        /// faction and a managed NPC, the faction's members are marked for a re-send toward that NPC
        /// on the pass that follows — in the same diplomacy phase when the broadcast came from the
        /// relation projection.
        ///
        /// <para>Only the player members' side can be repaired. The same loop also overwrites the
        /// NPC's members' reputation toward the player faction on each client, and SE refuses any
        /// reputation toward a PlayerMade faction, so there is no replicating write for it; nothing
        /// reads those values.</para>
        /// </summary>
        internal void MarkMemberResend(VanillaFactionAct act, long fromFactionId, long toFactionId)
        {
            if (!VanillaDiplomacy.RunsMemberLoop(act) || _runtime == null) return;
            if (MySession.Static == null || MySession.Static.Factions == null) return;
            MyFaction from = MySession.Static.Factions.TryGetFactionById(fromFactionId) as MyFaction;
            MyFaction to = MySession.Static.Factions.TryGetFactionById(toFactionId) as MyFaction;
            if (from == null || to == null) return;

            MyFaction player = IsRegisteredPlayerFaction(from) ? from : (IsRegisteredPlayerFaction(to) ? to : null);
            MyFaction npc = player == from ? to : from;
            if (player == null || IsRegisteredPlayerFaction(npc)) return;

            lock (_gate)
            {
                _sync.MarkResend(player.Tag, npc.Tag);
                if (DebugRepSync)
                    Log.Info("REPSYNC_RESEND_MARKED faction=" + player.Tag + " npc=" + npc.Tag + " act=" + act);
            }
        }

        private bool IsRegisteredPlayerFaction(MyFaction f)
        {
            FactionState st = f != null ? _runtime.FindFaction(f.Tag) : null;
            return st != null && st.IsPlayerFaction;
        }

        /// <summary>Every human member of every registered player faction.</summary>
        private List<PlayerFactionMember> CollectMembers()
        {
            var members = new List<PlayerFactionMember>();
            MyFactionCollection factions = MySession.Static.Factions;
            foreach (FactionState st in _runtime.States)
            {
                if (st == null || !st.IsPlayerFaction || string.IsNullOrEmpty(st.Tag)) continue;
                MyFaction f = factions.TryGetFactionByTag(st.Tag);
                if (f == null) continue;
                foreach (var kv in f.Members)
                {
                    long id = kv.Key;
                    if (id == 0 || MySession.Static.Players.IdentityIsNpc(id)) continue;
                    members.Add(new PlayerFactionMember(id, f.Tag));
                }
            }
            return members;
        }

        /// <summary>The NPC factions whose standing is projected: registered, not a player faction,
        /// present in SE, and able to hold personal reputation at all (not PlayerMade, not None, not
        /// StaticReputation — SE refuses every write to a static faction).</summary>
        private List<string> ManagedNpcTags()
        {
            var tags = new List<string>();
            MyFactionCollection factions = MySession.Static.Factions;
            foreach (FactionState st in _runtime.States)
            {
                if (st == null || st.IsPlayerFaction || string.IsNullOrEmpty(st.Tag)) continue;
                MyFaction f = factions.TryGetFactionByTag(st.Tag);
                if (f == null || f.StaticReputation) continue;
                if (f.FactionType == MyFactionTypes.PlayerMade || f.FactionType == MyFactionTypes.None) continue;
                tags.Add(f.Tag);
            }
            return tags;
        }

        /// <summary>The registered player faction this identity belongs to, or null.</summary>
        private string RegisteredPlayerFactionOf(long identityId)
        {
            MyFaction f = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (f == null) return null;
            FactionState st = _runtime.FindFaction(f.Tag);
            return st != null && st.IsPlayerFaction ? f.Tag : null;
        }

        /// <summary>Brings one identity's tracking in line with its membership right now: a member
        /// is seeded, a former member is written back. Called from SE's join/leave events so both
        /// land in the same frame as the change rather than a quantum later; the pass catches any
        /// event that was missed.</summary>
        private void ReconcileIdentity(long identityId)
        {
            if (identityId == 0 || _bookUnreadable || _runtime == null || !_tuning.MemberReputationSyncEnabled) return;
            if (MySession.Static == null || MySession.Static.Factions == null) return;
            if (MySession.Static.Players.IdentityIsNpc(identityId)) return;

            lock (_gate)
            {
                ArmTrace();
                if (RegisteredPlayerFactionOf(identityId) != null) _sync.Seed(identityId, ManagedNpcTags());
                else if (_sync.Book.IsTracked(identityId)) _sync.WriteBack(identityId);
            }
            SaveBook();
        }

        private void OnPlayerJoined(MyFaction faction, long identityId)
        {
            if (SimulationGate.Deferred) return;
            try { ReconcileIdentity(identityId); }
            catch (Exception ex) { Log.Warn(ex, "Member reputation: seeding a joining member failed."); }
        }

        private void OnPlayerLeft(MyFaction faction, long identityId)
        {
            if (SimulationGate.Deferred) return;
            try { ReconcileIdentity(identityId); }
            catch (Exception ex) { Log.Warn(ex, "Member reputation: writing back a leaving member failed."); }
        }

        /// <summary>A disbanded faction kicks nobody (SE removes the faction and leaves its members'
        /// rows behind), so OnPlayerLeft never fires for them. Reconcile every tracked identity.</summary>
        private void OnFactionStateChanged(MyFactionStateChange action, long fromFactionId, long toFactionId,
                                           long playerId, long senderId)
        {
            if (action != MyFactionStateChange.RemoveFaction || SimulationGate.Deferred) return;
            try
            {
                List<long> tracked;
                lock (_gate) tracked = _sync.Book.TrackedIdentities();
                foreach (long id in tracked) ReconcileIdentity(id);
            }
            catch (Exception ex) { Log.Warn(ex, "Member reputation: writing back a disbanded faction's members failed."); }
        }

        /// <summary>SE's per-pair change event. Fires inside <c>ChangeReputationWithPlayer</c> before
        /// the value is stored, so the field still reads the old value here.
        ///
        /// <para>With the entry-point patch bound, a change inside <c>AddFactionPlayerReputation</c>
        /// is left to its postfix, which sees the raw delta; this handler then only takes absolute
        /// sets (a mod or an admin). Without the patch, it takes everything, as observed deltas.</para></summary>
        private void OnSeReputationChanged(long identityId, long factionId, int newValue, ReputationChangeReason reason)
        {
            if (SimulationGate.Deferred) return;
            if (PersonalReputationConductPatch.Bound && PersonalReputationConductPatch.InAddCall) return;
            try
            {
                lock (_gate)
                {
                    if (_sync.IsWriting || !_sync.Book.IsTracked(identityId)) return;
                    MyFaction f = MySession.Static.Factions.TryGetFactionById(factionId) as MyFaction;
                    if (f == null) return;
                    ArmTrace();
                    ReputationChangeKind kind = KindOf(reason);
                    if (PersonalReputationConductPatch.Bound)
                        _sync.CaptureSet(identityId, f.Tag, newValue, kind);
                    else
                    {
                        int old;
                        if (_field.TryRead(identityId, f.Tag, out old))
                            _sync.CaptureObserved(identityId, f.Tag, old, newValue, kind);
                    }
                }
            }
            catch (Exception ex) { Log.Warn(ex, "Member reputation: capturing a reputation change failed."); }
        }

        /// <summary>From <see cref="PersonalReputationConductPatch"/>'s postfix, for a call SE
        /// accepted. Re-derives what SE applied from the raw request, exactly as
        /// <c>GenerateChanges</c> does, so a delta the projected field's bound swallowed still reaches
        /// the record: the target faction by the owner multiplier, and — for a positive, propagating
        /// change — every other recorded NPC by the multiplier for its relation to the target.</summary>
        internal void OnConductAdded(long identityId, long factionId, int delta, ReputationChangeReason reason, bool propagate)
        {
            if (SimulationGate.Deferred || delta == 0) return;
            lock (_gate)
            {
                if (_sync.IsWriting || !_sync.Book.IsTracked(identityId)) return;
                MyFactionCollection factions = MySession.Static != null ? MySession.Static.Factions : null;
                if (factions == null) return;
                MyFaction target = factions.TryGetFactionById(factionId) as MyFaction;
                if (target == null) return;

                ArmTrace();
                ReputationChangeKind kind = KindOf(reason);
                _sync.CaptureAdd(identityId, target.Tag,
                    MemberReputationSync.ScaleDelta(_field.OwnerMultiplier(), delta), kind);

                if (!propagate || delta <= 0) return;
                foreach (KeyValuePair<string, int> r in _sync.Book.RecordsOf(identityId))
                {
                    if (string.Equals(r.Key, target.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                    MyFaction other = factions.TryGetFactionByTag(r.Key);
                    if (other == null || other.FactionType == MyFactionTypes.None
                        || other.FactionType == MyFactionTypes.PlayerMade) continue;
                    float multiplier;
                    if (!_field.TryPropagationMultiplier(target.FactionId, other.FactionId, out multiplier)) continue;
                    _sync.CaptureAdd(identityId, other.Tag, MemberReputationSync.ScaleDelta(multiplier, delta), kind);
                }
            }
        }

        private static ReputationChangeKind KindOf(ReputationChangeReason reason)
        {
            switch (reason)
            {
                case ReputationChangeReason.Hostility: return ReputationChangeKind.Hostility;
                case ReputationChangeReason.Contract:  return ReputationChangeKind.Contract;
                case ReputationChangeReason.Admin:     return ReputationChangeKind.Admin;
                case ReputationChangeReason.Mod:       return ReputationChangeKind.Mod;
                case ReputationChangeReason.Decay:     return ReputationChangeKind.Decay;
                default:                               return ReputationChangeKind.None;
            }
        }

        /// <summary>Points the sync's trace at the Torch log while <c>factions debug repsync</c> is
        /// on, and at nothing otherwise — the sync checks for null before it builds a line.</summary>
        private void ArmTrace()
        {
            _sync.Trace = DebugRepSync ? TraceSink : null;
        }

        private static readonly Action<string> TraceSink = line => Log.Info(line);

        // ---- housekeeping -----------------------------------------------------------------------

        /// <summary>Persist and bound the ledger. Called on a slow tick.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (_tuning != null && _tuning.TradeReputationRetentionDays > 0.0)
                _ledger.Compact(nowUtc, TimeSpan.FromDays(_tuning.TradeReputationRetentionDays));
            _ledger.SaveIfDirty();
            SaveBook();
        }

        private void SaveBook()
        {
            if (_bookUnreadable) return;   // never overwrite the unreadable file with an empty book
            lock (_gate)
            {
                if (!_sync.Book.IsDirty) return;
                try { _sync.Book.SaveIfDirty(); }
                catch (Exception ex) { Log.Warn(ex, "Member reputation: saving " + MemberReputationBook.FileName + " failed."); }
            }
        }

        // ---- diagnosis --------------------------------------------------------------------------

        /// <summary>
        /// What SE actually holds, for the operator: the world's reputation marks, every faction's
        /// <c>MyFactionTypes</c>, and each known player's standing with each registered faction.
        ///
        /// <para>Exists because there was no way to read a player↔faction reputation on this server
        /// at all, which is what made FAC-182 look like it needed a live flight test. It stays
        /// because the faction-type column is the live confirmation that a Magnate faction really is
        /// <c>PlayerMade</c> — the split between the two levels rests on it.</para>
        /// </summary>
        public string Report(string filterTag)
        {
            var sb = new StringBuilder();
            if (MySession.Static == null || MySession.Static.Factions == null)
                return "reputation: no session loaded.";

            ReputationScale scale = _runtime != null ? _runtime.CurrentReputationScale() : null;
            sb.Append("Reputation scale: ");
            if (scale == null) sb.AppendLine("NONE (this world carries no economy session component).");
            else sb.AppendLine(scale.HostileMin + " / " + scale.HostileMid + " / " + scale.NeutralMin
                             + " / " + scale.NeutralMid + " / " + scale.FriendlyMin + " / "
                             + scale.FriendlyMid + " / " + scale.FriendlyMax);

            sb.Append("Trade: threshold=").Append((long)_tuning.TradeReputationThreshold)
              .Append(" step=").Append(_tuning.TradeReputationStep)
              .Append(" provisionWeight=").Append(_tuning.TradeProvisionWeight)
              .Append(" consumptionWeight=").Append(_tuning.TradeConsumptionWeight)
              .Append(" ledgerPairs=").Append(_ledger.EntryCount).AppendLine();
            sb.Append("Aggregate: alpha=").Append(_tuning.MemberStandingAlpha)
              .Append(" halfLifeDays=").Append(_tuning.MemberStandingHalfLifeDays)
              .Append(" driftWeight=").Append(_tuning.MemberStandingDriftWeight).AppendLine();
            lock (_gate)
            {
                sb.Append("Member display: enabled=").Append(_tuning.MemberReputationSyncEnabled)
                  .Append(_bookUnreadable ? " BOOK UNREADABLE (not projecting)" : "")
                  .Append(" capture=").Append(PersonalReputationConductPatch.Bound ? "entry-point" : "event-only")
                  .Append(" members=").Append(_sync.Book.IdentityCount)
                  .Append(" records=").Append(_sync.Book.RecordCount);
                if (_lastPass != null)
                    sb.Append(" lastPass=").Append(_lastPassUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                      .Append(" (members=").Append(_lastPass.Members).Append(" seeded=").Append(_lastPass.Seeded)
                      .Append(" written=").Append(_lastPass.Written).Append(" resent=").Append(_lastPass.Resent)
                      .Append(" deferred=").Append(_lastPass.Deferred)
                      .Append(" released=").Append(_lastPass.Released).Append(")");
                else sb.Append(" lastPass=none");
                sb.Append(" resendPending=").Append(_sync.PendingResends);
                sb.AppendLine();
            }

            var players = new List<MyPlayer>();
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                if (!p.IsBot && p.Identity != null) players.Add(p);

            sb.AppendLine("Factions (tag, SE type, members):");
            foreach (MyFaction f in MySession.Static.Factions.GetAllFactions())
            {
                if (f == null) continue;
                if (!string.IsNullOrEmpty(filterTag)
                    && !string.Equals(f.Tag, filterTag, StringComparison.OrdinalIgnoreCase)) continue;

                FactionState st = _runtime != null ? _runtime.FindFaction(f.Tag) : null;
                sb.Append("  ").Append(f.Tag)
                  .Append("  type=").Append(f.FactionType)
                  .Append(" staticRep=").Append(f.StaticReputation)
                  .Append(" members=").Append(f.Members.Count)
                  .Append(" registered=").Append(st != null ? (st.IsPlayerFaction ? "player" : "npc") : "no")
                  .AppendLine();

                foreach (MyPlayer p in players)
                {
                    long id = p.Identity.IdentityId;
                    Tuple<MyRelationsBetweenFactions, int> rel =
                        MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(id, f.FactionId);
                    if (rel == null) continue;
                    // active = SE's field (what stores, contracts and the vanilla screen read);
                    // personal = the player's own record, which is the field itself unless they are a
                    // member whose field shows their faction's standing.
                    int personal;
                    bool stored;
                    lock (_gate) stored = _sync.Book.TryGet(id, f.Tag, out personal);
                    if (!stored) personal = rel.Item2;
                    sb.Append("      ").Append(p.Identity.DisplayName)
                      .Append(" (").Append(id).Append(")  active=").Append(rel.Item2)
                      .Append(" [").Append(rel.Item1).Append("]")
                      .Append(" personal=").Append(stored ? personal.ToString(CultureInfo.InvariantCulture) : "=active");
                    if (scale != null) sb.Append(" norm=").Append(scale.Normalise(personal).ToString("0.000", CultureInfo.InvariantCulture));
                    sb.Append("  tradeRemainder=").Append((long)_ledger.RemainderFor(id, f.Tag))
                      .Append(" tradeLifetime=").Append((long)_ledger.LifetimeFor(id, f.Tag))
                      .AppendLine();
                }
            }

            if (players.Count == 0)
                sb.AppendLine("  (no players online — per-player standing rows need a connected player.)");

            lock (_gate)
            {
                List<long> tracked = _sync.Book.TrackedIdentities();
                if (tracked.Count > 0)
                {
                    tracked.Sort();
                    sb.AppendLine("Stored personal records (every member, online or not): identity: TAG=personal/active");
                    foreach (long id in tracked)
                    {
                        sb.Append("  ").Append(id).Append(":");
                        foreach (KeyValuePair<string, int> r in _sync.Book.RecordsOf(id))
                        {
                            if (!string.IsNullOrEmpty(filterTag)
                                && !string.Equals(r.Key, filterTag, StringComparison.OrdinalIgnoreCase)) continue;
                            int active;
                            sb.Append(" ").Append(r.Key).Append("=").Append(r.Value).Append("/")
                              .Append(_field.TryRead(id, r.Key, out active) ? active.ToString(CultureInfo.InvariantCulture) : "?");
                        }
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }
    }
}
