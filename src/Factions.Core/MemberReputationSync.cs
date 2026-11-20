using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>SE's <c>ReputationChangeReason</c>, mirrored so Core can reason about it without an
    /// engine reference. The host converts by name, never by ordinal.</summary>
    public enum ReputationChangeKind
    {
        None,
        Hostility,
        Contract,
        Admin,
        Mod,
        Decay,
    }

    /// <summary>
    /// SE's personal-reputation field for one (player, NPC faction) pair, as the host exposes it.
    /// Implemented over <c>MyFactionCollection</c> by the host and by a dictionary in the tests.
    /// </summary>
    public interface IPersonalReputationField
    {
        /// <summary>The value SE currently holds. False when the pair cannot be read at all (no
        /// session, no such faction) — never "the value is zero".</summary>
        bool TryRead(long identityId, string npcTag, out int reputation);

        /// <summary>Sets the field, silently and without propagation. True when SE holds
        /// <paramref name="reputation"/> afterwards.</summary>
        bool Write(long identityId, string npcTag, int reputation);

        /// <summary>SE's own <c>ClampReputation</c>: the running world's bounds, not vanilla's.</summary>
        int Clamp(int reputation);

        /// <summary>Sends the field's current value to every client again without changing it. True
        /// when the send was raised.</summary>
        bool Resend(long identityId, string npcTag);
    }

    /// <summary>One member of a registered player faction, as the projection pass sees them.</summary>
    public struct PlayerFactionMember
    {
        public long IdentityId;
        public string FactionTag;

        public PlayerFactionMember(long identityId, string factionTag)
        {
            IdentityId = identityId;
            FactionTag = factionTag;
        }
    }

    /// <summary>What one projection pass did, for the operator's readback.</summary>
    public sealed class MemberReputationPassResult
    {
        public int Members;
        public int Seeded;
        public int Written;
        /// <summary>Writes the per-pass budget pushed to the next pass.</summary>
        public int Deferred;
        /// <summary>Identities no longer in a registered player faction, written back and dropped.</summary>
        public int Released;
        /// <summary>Fields already at the standing, sent to clients again because a vanilla
        /// relation broadcast overwrote the clients' copy (see
        /// <see cref="MemberReputationSync.MarkResend"/>). Counted against the same write budget.</summary>
        public int Resent;
    }

    /// <summary>
    /// The personal reputation records of player-faction members, kept by Factions while SE's field
    /// shows the faction standing (docs/player-progression.md → Reputation and standing;
    /// docs/diplomacy-plan.md rule 4).
    ///
    /// <para>Keyed by SE identity id, then NPC faction tag. Values are in SE's own units, so a record
    /// seeded from SE and written back to it round-trips exactly. An identity is present here exactly
    /// while it is a member of a registered player faction; leaving drops it after the write-back, so
    /// a later rejoin seeds afresh from the field that is by then theirs again.</para>
    ///
    /// <para>Self-persisting beside <c>players.json</c> as <c>member_reputation.json</c>:
    /// <c>{"SchemaVersion":1,"Members":{"&lt;identityId&gt;":{"&lt;TAG&gt;":&lt;rep&gt;}}}</c>. A null
    /// storage directory keeps it in memory only.</para>
    /// </summary>
    public sealed class MemberReputationBook
    {
        public const string FileName = "member_reputation.json";
        private const int SchemaVersion = 1;

        private readonly string _path;
        private readonly Dictionary<long, Dictionary<string, int>> _records =
            new Dictionary<long, Dictionary<string, int>>();
        private bool _dirty;

        public MemberReputationBook(string storageDir)
        {
            _path = string.IsNullOrEmpty(storageDir) ? null : Path.Combine(storageDir, FileName);
            Load();
        }

        public int IdentityCount { get { return _records.Count; } }

        public int RecordCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<long, Dictionary<string, int>> kv in _records) n += kv.Value.Count;
                return n;
            }
        }

        public bool IsDirty { get { return _dirty; } }

        public bool IsTracked(long identityId) { return _records.ContainsKey(identityId); }

        /// <summary>A snapshot, so the caller may release identities while walking it.</summary>
        public List<long> TrackedIdentities() { return new List<long>(_records.Keys); }

        public bool TryGet(long identityId, string npcTag, out int reputation)
        {
            reputation = 0;
            Dictionary<string, int> byTag;
            return !string.IsNullOrEmpty(npcTag)
                && _records.TryGetValue(identityId, out byTag)
                && byTag.TryGetValue(npcTag, out reputation);
        }

        /// <summary>This identity's records, tag → value. Empty when untracked.</summary>
        public IList<KeyValuePair<string, int>> RecordsOf(long identityId)
        {
            var list = new List<KeyValuePair<string, int>>();
            Dictionary<string, int> byTag;
            if (_records.TryGetValue(identityId, out byTag)) list.AddRange(byTag);
            list.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Marks an identity as a member with nothing recorded yet. Seeding then adds one
        /// record per NPC; tracking without a record keeps the membership fact across a restart.</summary>
        public void Track(long identityId)
        {
            if (identityId == 0 || _records.ContainsKey(identityId)) return;
            _records[identityId] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _dirty = true;
        }

        /// <summary>Records <paramref name="reputation"/> only when nothing is recorded for the pair.
        /// False when a record already exists — a seed never overwrites the live record.</summary>
        public bool Seed(long identityId, string npcTag, int reputation)
        {
            if (identityId == 0 || string.IsNullOrEmpty(npcTag)) return false;
            Track(identityId);
            Dictionary<string, int> byTag = _records[identityId];
            if (byTag.ContainsKey(npcTag)) return false;
            byTag[npcTag] = reputation;
            _dirty = true;
            return true;
        }

        /// <summary>Overwrites an existing record. False (and nothing written) when there is none.</summary>
        public bool Set(long identityId, string npcTag, int reputation)
        {
            Dictionary<string, int> byTag;
            if (string.IsNullOrEmpty(npcTag) || !_records.TryGetValue(identityId, out byTag)
                || !byTag.ContainsKey(npcTag)) return false;
            if (byTag[npcTag] == reputation) return true;
            byTag[npcTag] = reputation;
            _dirty = true;
            return true;
        }

        /// <summary>Drops the identity and returns what it held, for the write-back.</summary>
        public IList<KeyValuePair<string, int>> Release(long identityId)
        {
            IList<KeyValuePair<string, int>> held = RecordsOf(identityId);
            if (_records.Remove(identityId)) _dirty = true;
            return held;
        }

        public void SaveIfDirty()
        {
            if (!_dirty || _path == null) return;
            var doc = new BookFile
            {
                SchemaVersion = SchemaVersion,
                Members = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal),
            };
            foreach (KeyValuePair<long, Dictionary<string, int>> kv in _records)
            {
                var byTag = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> r in kv.Value) byTag[r.Key] = r.Value;
                doc.Members[kv.Key.ToString(CultureInfo.InvariantCulture)] = byTag;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(doc, Formatting.Indented));
            if (File.Exists(_path))
            {
                string backup = _path + ".bak";
                File.Replace(temp, _path, backup);
                try { File.Delete(backup); } catch { }
            }
            else File.Move(temp, _path);
            _dirty = false;
        }

        private void Load()
        {
            if (_path == null || !File.Exists(_path)) return;
            // Unlike the trade ledger, a corrupt file is not cheap: these records are the only copy of
            // a member's own conduct while SE's field shows their faction's standing. Reseeding from
            // that field would launder every member to the standing. So a parse failure throws, and
            // the host decides — it refuses to project rather than reseed.
            BookFile doc = JsonConvert.DeserializeObject<BookFile>(File.ReadAllText(_path));
            if (doc == null || doc.Members == null) return;
            foreach (KeyValuePair<string, SortedDictionary<string, int>> kv in doc.Members)
            {
                long id;
                if (!long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id == 0)
                    continue;
                var byTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (kv.Value != null)
                    foreach (KeyValuePair<string, int> r in kv.Value)
                        if (!string.IsNullOrEmpty(r.Key)) byTag[r.Key] = r.Value;
                _records[id] = byTag;
            }
        }

        private sealed class BookFile
        {
            public int SchemaVersion { get; set; }
            public SortedDictionary<string, SortedDictionary<string, int>> Members { get; set; }
        }
    }

    /// <summary>
    /// Rule 4 of docs/diplomacy-plan.md, SE-free: while a player is a member of a registered player
    /// faction, SE's personal-reputation field shows each managed NPC's standing toward that faction,
    /// and the member's own record lives in <see cref="MemberReputationBook"/>.
    ///
    /// <list type="bullet">
    /// <item><b>Seed</b> — once per membership, from the field, before the first projection write
    /// touches it. Until then the field IS the personal record.</item>
    /// <item><b>Capture</b> — every conduct delta SE applies to a tracked member is applied to the
    /// stored record instead, by the same amount SE would have applied to it. Decay and our own
    /// writes are not conduct.</item>
    /// <item><b>Project</b> — the field is set to the standing, mapped through
    /// <see cref="ReputationScale.ReputationFor"/> and the world's clamp, when it is at least the
    /// minimum delta away.</item>
    /// <item><b>Write back</b> — on leaving, the stored record goes back into the field and the
    /// identity is dropped.</item>
    /// </list>
    ///
    /// <para>Game-thread only, like every SE call the host makes on its behalf.</para>
    /// </summary>
    public sealed class MemberReputationSync
    {
        private readonly MemberReputationBook _book;
        private readonly IPersonalReputationField _field;
        private bool _writing;
        // "<player faction tag>|<npc tag>": pairs whose members' client copies a vanilla relation
        // broadcast overwrote. The next pass expands each into its current members.
        private readonly HashSet<string> _resend = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // "<identity>|<npc tag>": members still owed a re-send because the budget deferred them.
        private HashSet<string> _resendMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MemberReputationSync(MemberReputationBook book, IPersonalReputationField field)
        {
            if (book == null) throw new ArgumentNullException("book");
            if (field == null) throw new ArgumentNullException("field");
            _book = book;
            _field = field;
        }

        public MemberReputationBook Book { get { return _book; } }

        /// <summary>True while this class is writing the field itself. A change SE reports in that
        /// window is our own and must never be captured as conduct.</summary>
        public bool IsWriting { get { return _writing; } }

        /// <summary>Line sink for the <c>repsync</c> debug trace. Null when the trace is off; every
        /// line is built only after this is checked.</summary>
        public Action<string> Trace { get; set; }

        // ---- the rules, as pure functions -------------------------------------------------------

        /// <summary>A change arriving through SE's additive path (<c>AddFactionPlayerReputation</c>)
        /// is conduct unless it is decay. Vanilla passes Hostility for damage, grinding and killing,
        /// Contract for contracts, Admin for the admin screen; nothing in vanilla adds with None, and
        /// our own writes are excluded by <see cref="IsWriting"/> rather than by reason.
        /// <para>Decay is excluded because the world runs with decay off on purpose (FAC-202), and
        /// a decay step computed against the projected field has neither the size nor the direction
        /// it would have against the personal record.</para></summary>
        public static bool IsConductOnAddPath(ReputationChangeKind kind)
        {
            return kind != ReputationChangeKind.Decay;
        }

        /// <summary>A change arriving as an absolute set (<c>SetReputationBetweenPlayerAndFaction</c>)
        /// is conduct only from a mod or an admin. None on that path is the engine resetting a pair —
        /// <c>ChangeFactionRelation</c>'s member loop, <c>ForceRelationToPlayers</c> — which is
        /// politics or bookkeeping, not anything the player did.</summary>
        public static bool IsConductOnSetPath(ReputationChangeKind kind)
        {
            return kind == ReputationChangeKind.Mod || kind == ReputationChangeKind.Admin;
        }

        /// <summary>The field is written only when it is at least <paramref name="minDelta"/> (and
        /// never less than one point) away from the target.</summary>
        public static bool ShouldWrite(int current, int target, int minDelta)
        {
            int threshold = minDelta < 1 ? 1 : minDelta;
            long gap = (long)target - current;
            return Math.Abs(gap) >= threshold;
        }

        /// <summary>SE's own arithmetic for a scaled delta: <c>(int)(multiplier * (float)delta)</c>,
        /// truncating toward zero exactly as <c>GenerateChanges</c> does.</summary>
        public static int ScaleDelta(float multiplier, int delta)
        {
            return (int)(multiplier * (float)delta);
        }

        /// <summary>The field value a standing projects to: <see cref="ReputationScale.ReputationFor"/>
        /// (the same knots the faction-table projection uses), then the world's clamp.</summary>
        public int ProjectedReputation(double standing, ReputationScale scale,
                                       double hostileRelationThreshold, double allyRelationThreshold)
        {
            if (scale == null) scale = ReputationScale.Vanilla;
            return _field.Clamp(scale.ReputationFor(standing, hostileRelationThreshold, allyRelationThreshold));
        }

        // ---- forced re-send ---------------------------------------------------------------------

        /// <summary>
        /// Marks every member of <paramref name="playerFactionTag"/> for a re-send of their field
        /// toward <paramref name="npcTag"/> on the next pass, whether or not the server value moved.
        ///
        /// <para>Why: a vanilla relation broadcast (DeclareWar, AcceptPeace, AcceptFriendRequest —
        /// <see cref="VanillaDiplomacy.RunsMemberLoop"/>) makes each client run
        /// <c>ChangeFactionRelation</c>, whose member loop overwrites the client's copy of every
        /// member's personal reputation toward the NPC with the band's midpoint. The server copy is
        /// untouched, so the ordinary minimum-delta rule would leave the client wrong until the
        /// standing next moved.</para>
        /// </summary>
        public void MarkResend(string playerFactionTag, string npcTag)
        {
            if (string.IsNullOrEmpty(playerFactionTag) || string.IsNullOrEmpty(npcTag)) return;
            _resend.Add(PairKey(playerFactionTag, npcTag));
        }

        /// <summary>Marked pairs not yet expanded, plus members a pass has deferred.</summary>
        public int PendingResends { get { return _resend.Count + _resendMembers.Count; } }

        /// <summary>Whether this member is still owed a re-send toward this NPC.</summary>
        public bool IsOwedResend(long identityId, string npcTag)
        {
            return _resendMembers.Contains(MemberKey(identityId, npcTag));
        }

        private static string PairKey(string playerFactionTag, string npcTag)
        {
            return playerFactionTag + "|" + npcTag;
        }

        private static string MemberKey(long identityId, string npcTag)
        {
            return identityId.ToString(CultureInfo.InvariantCulture) + "|" + npcTag;
        }

        // ---- membership -------------------------------------------------------------------------

        /// <summary>Seeds every NPC tag this identity has no record for, from the field. Tracks the
        /// identity even when nothing could be read, so it is known to be a member.</summary>
        /// <returns>Records seeded by this call — 0 on every call after the first for a given pair.</returns>
        public int Seed(long identityId, IList<string> npcTags)
        {
            if (identityId == 0 || npcTags == null) return 0;
            _book.Track(identityId);
            int seeded = 0;
            for (int i = 0; i < npcTags.Count; i++)
            {
                string tag = npcTags[i];
                int existing;
                if (string.IsNullOrEmpty(tag) || _book.TryGet(identityId, tag, out existing)) continue;
                int value;
                if (!_field.TryRead(identityId, tag, out value)) continue;
                if (!_book.Seed(identityId, tag, value)) continue;
                seeded++;
                Action<string> trace = Trace;
                if (trace != null)
                    trace("REPSYNC_SEED identity=" + identityId + " npc=" + tag + " record=" + value);
            }
            return seeded;
        }

        /// <summary>Writes every stored record back into the field and drops the identity.</summary>
        /// <returns>Records written back.</returns>
        public int WriteBack(long identityId)
        {
            if (!_book.IsTracked(identityId)) return 0;
            IList<KeyValuePair<string, int>> held = _book.Release(identityId);
            int written = 0;
            foreach (KeyValuePair<string, int> r in held)
            {
                int before;
                bool read = _field.TryRead(identityId, r.Key, out before);
                if (read && before == r.Value) { written++; continue; }
                if (!WriteGuarded(identityId, r.Key, r.Value)) continue;
                written++;
                Action<string> trace = Trace;
                if (trace != null)
                    trace("REPSYNC_WRITEBACK identity=" + identityId + " npc=" + r.Key
                        + " field=" + (read ? before.ToString(CultureInfo.InvariantCulture) : "?")
                        + " -> " + r.Value);
            }
            return written;
        }

        // ---- conduct capture --------------------------------------------------------------------

        /// <summary>A delta SE applied (or, at a bound of the projected field, would have applied) to
        /// a tracked member through the additive path. <paramref name="seDelta"/> is already scaled
        /// by SE's own multiplier for this faction; the record moves by it, clamped to the world's
        /// bounds — exactly what SE would have done had the field still held the record.</summary>
        /// <returns>True when a stored record moved.</returns>
        public bool CaptureAdd(long identityId, string npcTag, int seDelta, ReputationChangeKind kind)
        {
            if (_writing || !IsConductOnAddPath(kind) || seDelta == 0) return false;
            int before;
            if (!_book.TryGet(identityId, npcTag, out before)) return false;
            int after = _field.Clamp((int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)before + seDelta)));
            if (after == before) return false;
            _book.Set(identityId, npcTag, after);
            Action<string> trace = Trace;
            if (trace != null)
                trace("REPSYNC_CAPTURE identity=" + identityId + " npc=" + npcTag + " reason=" + kind
                    + " delta=" + seDelta + " record=" + before + " -> " + after);
            return true;
        }

        /// <summary>An absolute set a mod or an admin made on a tracked member. The record takes the
        /// value, as the field would have.</summary>
        public bool CaptureSet(long identityId, string npcTag, int newValue, ReputationChangeKind kind)
        {
            if (_writing || !IsConductOnSetPath(kind)) return false;
            int before;
            if (!_book.TryGet(identityId, npcTag, out before)) return false;
            int after = _field.Clamp(newValue);
            if (after == before) return false;
            _book.Set(identityId, npcTag, after);
            Action<string> trace = Trace;
            if (trace != null)
                trace("REPSYNC_CAPTURE identity=" + identityId + " npc=" + npcTag + " reason=" + kind
                    + " set=" + newValue + " record=" + before + " -> " + after);
            return true;
        }

        /// <summary>The fallback when the host cannot see SE's additive entry point: the change as
        /// SE reports it, old field value to new. Loses the part of a delta the projected field's
        /// bound swallowed, and treats a mod's absolute set as a delta; the host logs that it is
        /// running degraded.</summary>
        public bool CaptureObserved(long identityId, string npcTag, int oldValue, int newValue,
                                    ReputationChangeKind kind)
        {
            if (kind == ReputationChangeKind.None) return false;   // on this path None is an engine reset
            return CaptureAdd(identityId, npcTag, newValue - oldValue, kind);
        }

        // ---- the projection pass ----------------------------------------------------------------

        /// <summary>
        /// One economy quantum's worth: release identities that are no longer members, seed the
        /// ones that are, and project each managed NPC's standing onto their field.
        /// </summary>
        /// <param name="members">Every current member of a registered player faction.</param>
        /// <param name="npcTags">The managed NPC factions whose standing is projected.</param>
        /// <param name="standing">(npc tag, player faction tag) → the NPC's standing, −1..1.</param>
        /// <param name="minDelta">Smallest move worth a write; below 1 reads as 1.</param>
        /// <param name="maxWrites">Field writes allowed this pass; the rest wait for the next one.
        /// Non-positive means unbounded.</param>
        public MemberReputationPassResult RunPass(IList<PlayerFactionMember> members, IList<string> npcTags,
                                                  Func<string, string, double> standing, ReputationScale scale,
                                                  double hostileRelationThreshold, double allyRelationThreshold,
                                                  int minDelta, int maxWrites)
        {
            var result = new MemberReputationPassResult();
            var current = new HashSet<long>();
            if (members != null)
                foreach (PlayerFactionMember m in members)
                    if (m.IdentityId != 0) current.Add(m.IdentityId);

            foreach (long id in _book.TrackedIdentities())
            {
                if (current.Contains(id)) continue;
                WriteBack(id);
                result.Released++;
            }

            if (members == null || npcTags == null || standing == null) return result;

            // Expand marked pairs into their current members, so a budget smaller than a roster
            // resumes where it stopped instead of restarting from the first member every pass.
            if (_resend.Count > 0)
            {
                foreach (PlayerFactionMember m in members)
                {
                    if (m.IdentityId == 0 || string.IsNullOrEmpty(m.FactionTag)) continue;
                    for (int j = 0; j < npcTags.Count; j++)
                        if (_resend.Contains(PairKey(m.FactionTag, npcTags[j])))
                            _resendMembers.Add(MemberKey(m.IdentityId, npcTags[j]));
                }
                _resend.Clear();
            }
            // Only the members the budget defers stay owed; anyone handled, gone, or unseeded is not.
            var stillOwed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < members.Count; i++)
            {
                PlayerFactionMember m = members[i];
                if (m.IdentityId == 0 || string.IsNullOrEmpty(m.FactionTag)) continue;
                result.Members++;
                result.Seeded += Seed(m.IdentityId, npcTags);

                for (int j = 0; j < npcTags.Count; j++)
                {
                    string npc = npcTags[j];
                    int record;
                    // Never write a field whose value is not safely recorded first: an unseeded pair
                    // still holds the player's own record, and projecting over it would destroy it.
                    if (!_book.TryGet(m.IdentityId, npc, out record)) continue;

                    double s = standing(npc, m.FactionTag);
                    int target = ProjectedReputation(s, scale, hostileRelationThreshold, allyRelationThreshold);
                    int field;
                    if (!_field.TryRead(m.IdentityId, npc, out field)) continue;
                    bool forced = _resendMembers.Count > 0 && _resendMembers.Contains(MemberKey(m.IdentityId, npc));
                    bool due = ShouldWrite(field, target, minDelta);
                    if (!due && !forced) continue;
                    if (maxWrites > 0 && result.Written + result.Resent >= maxWrites)
                    {
                        result.Deferred++;
                        if (forced) stillOwed.Add(MemberKey(m.IdentityId, npc));
                        continue;
                    }
                    if (!due && field == target)
                    {
                        // The server already holds the standing; only the clients' copy is wrong.
                        if (!ResendGuarded(m.IdentityId, npc)) continue;
                        result.Resent++;
                        Action<string> resendTrace = Trace;
                        if (resendTrace != null)
                            resendTrace("REPSYNC_RESEND identity=" + m.IdentityId + " npc=" + npc
                                      + " faction=" + m.FactionTag + " field=" + field);
                        continue;
                    }
                    // Due, or forced and within the minimum delta of the target: an ordinary write
                    // replicates, and lands the field exactly on the standing.
                    if (!WriteGuarded(m.IdentityId, npc, target)) continue;
                    result.Written++;
                    Action<string> trace = Trace;
                    if (trace != null)
                        trace("REPSYNC_PROJECT identity=" + m.IdentityId + " npc=" + npc
                            + " faction=" + m.FactionTag
                            + " standing=" + s.ToString("0.000", CultureInfo.InvariantCulture)
                            + " field=" + field + " -> " + target + " record=" + record);
                }
            }

            _resendMembers = stillOwed;
            return result;
        }

        /// <summary>Releases every tracked identity — the kill switch's path, and the one a disabled
        /// feature takes so no member is left showing a standing nobody maintains.</summary>
        public int ReleaseAll()
        {
            int released = 0;
            foreach (long id in _book.TrackedIdentities()) { WriteBack(id); released++; }
            return released;
        }

        private bool ResendGuarded(long identityId, string npcTag)
        {
            _writing = true;
            try { return _field.Resend(identityId, npcTag); }
            finally { _writing = false; }
        }

        private bool WriteGuarded(long identityId, string npcTag, int value)
        {
            _writing = true;
            try { return _field.Write(identityId, npcTag, value); }
            finally { _writing = false; }
        }
    }

    /// <summary>
    /// The member-standing roster read from personal records rather than from SE's field. This is
    /// what cuts the loop rule 4 would otherwise close (standing → field → aggregate → standing):
    /// a tracked member contributes their stored record; an untracked one contributes the field,
    /// which for them still IS the record.
    /// </summary>
    public static class MemberStandingRoster
    {
        /// <summary>A member's personal record toward <paramref name="npcTag"/>, in SE units.</summary>
        public static bool TryPersonalRecord(long identityId, string npcTag, MemberReputationBook book,
                                             IPersonalReputationField field, out int reputation)
        {
            if (book != null && book.TryGet(identityId, npcTag, out reputation)) return true;
            reputation = 0;
            return field != null && field.TryRead(identityId, npcTag, out reputation);
        }

        /// <summary>The roster <see cref="MemberStandingAggregate.TryCompute"/> reads.</summary>
        public static List<MemberReputation> Build(IEnumerable<long> memberIds, string npcTag,
                                                   MemberReputationBook book, IPersonalReputationField field,
                                                   ReputationScale scale, Func<long, DateTime?> lastSeen)
        {
            var roster = new List<MemberReputation>();
            if (memberIds == null) return roster;
            if (scale == null) scale = ReputationScale.Vanilla;
            foreach (long id in memberIds)
            {
                if (id == 0) continue;
                int rep;
                if (!TryPersonalRecord(id, npcTag, book, field, out rep)) continue;
                DateTime? seen = lastSeen != null ? lastSeen(id) : null;
                roster.Add(new MemberReputation
                {
                    IdentityId = id,
                    Standing = scale.Normalise(rep),
                    // Never seen online is not the same as seen long ago, but for weighting purposes
                    // it is the weakest claim on the roster's attention, so it takes the oldest stamp.
                    LastSeenUtc = seen ?? DateTime.MinValue,
                });
            }
            return roster;
        }
    }
}
