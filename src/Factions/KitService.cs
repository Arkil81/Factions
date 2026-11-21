using System;
using System.Collections.Generic;
using System.IO;
using Factions.Commands;
using Factions.Core;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>!factions kit</c>: named item bundles a player claims at most once per cooldown. The kits
    /// are <c>kits.json</c> (<see cref="KitCatalogue"/>); the cooldowns are one ledger for the whole
    /// cluster, keyed by Steam id and held where the simulation runs.
    ///
    /// <para>A standalone server or the authority checks and stamps its own ledger. An agent cannot:
    /// it asks the authority (<see cref="Factions.Wire.RelayKind.KitAsk"/>) and hands the items out
    /// itself when the claim is stamped, because the player's character is on the agent. An agent
    /// that then cannot hand them out asks the authority to undo the stamp.</para>
    /// </summary>
    internal sealed class KitService
    {
        private static readonly Logger Log = LogManager.GetLogger("Factions");

        /// <summary>How long an agent waits for the authority before telling the player it did not
        /// answer. The same as a forwarded chat command's.</summary>
        internal static readonly TimeSpan AnswerTimeout = NexusChatCommands.AnswerTimeout;

        /// <summary>How long an answer that came too late is still recognised, so a claim stamped
        /// after the player was told it failed is undone rather than left standing.</summary>
        private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(5);

        private readonly bool _holdsLedger;
        private readonly Func<string> _dataDir;
        private readonly string _instanceDir;
        private readonly Func<NexusKitAsk, bool> _ask;
        private readonly Action<ulong, string> _whisper;
        private readonly Action<string> _record;

        private KitCatalogue _catalogue;
        private string _cataloguePath;
        private DateTime _catalogueStamp;
        private KitCooldowns _ledger;

        private sealed class Pending
        {
            public ulong Steam;
            public KitOp Op;
            public string Kit;
            public int Rank;
            public DateTime SentUtc;
        }

        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        /// <summary>Asks the player was told had failed, by id, until <see cref="LateWindow"/> after.</summary>
        private readonly Dictionary<long, Pending> _late = new Dictionary<long, Pending>();
        private long _nextId;

        /// <param name="holdsLedger">True on a standalone server and the authority.</param>
        /// <param name="ask">Agent: hands an ask to the authority; false when it could not.</param>
        /// <param name="whisper">Agent: tells a player an answer that arrived after their command returned.</param>
        /// <param name="record">The DecisionLog: one line per claim handed out or undone.</param>
        internal KitService(bool holdsLedger, Func<string> dataDir, string instanceDir,
                            Func<NexusKitAsk, bool> ask, Action<ulong, string> whisper, Action<string> record)
        {
            _record = record;
            _holdsLedger = holdsLedger;
            _dataDir = dataDir;
            _instanceDir = instanceDir;
            _ask = ask;
            _whisper = whisper;
        }

        // ---------------------------------------------------------------- catalogue

        /// <summary>The kits as <c>kits.json</c> reads now. Re-read when the file's write time moves, so
        /// an edit applies to the next command without a reload. Absent, the defaults apply, and the
        /// ledger's holder writes them out once as a file to edit.</summary>
        internal KitCatalogue Catalogue()
        {
            string dataDir = _dataDir();
            bool usedFallback;
            string path = DataDirectory.ResolveInput(KitCatalogue.FileName, dataDir, _instanceDir, File.Exists, out usedFallback);
            try
            {
                if (!File.Exists(path))
                {
                    if (_catalogue == null || _cataloguePath != null)
                    {
                        _catalogue = KitCatalogue.Defaults();
                        _cataloguePath = null;
                        if (_holdsLedger && WriteDefaults(path))
                            Log.Info("Kits: wrote the default " + KitCatalogue.FileName + " to " + path + ".");
                    }
                    return _catalogue;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (_catalogue != null && path == _cataloguePath && stamp == _catalogueStamp) return _catalogue;

                var problems = new List<string>();
                KitCatalogue read = KitCatalogue.Parse(File.ReadAllText(path), problems);
                foreach (string p in problems) Log.Warn("Kits: " + path + ": skipped " + p + ".");
                _catalogue = read;
                _cataloguePath = path;
                _catalogueStamp = stamp;
                Log.Info("Kits: " + read.Kits.Count + " kit(s) from " + path
                         + (usedFallback ? " (instance copy)" : "") + ".");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Kits: " + path + " could not be read; "
                             + (_catalogue != null ? "keeping the kits last read." : "using the defaults."));
                if (_catalogue == null) _catalogue = KitCatalogue.Defaults();
                _cataloguePath = path;
                _catalogueStamp = SafeStamp(path);
            }
            return _catalogue;
        }

        private static DateTime SafeStamp(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
        }

        private static bool WriteDefaults(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, KitCatalogue.Defaults().ToJson());
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Kits: could not write the default " + KitCatalogue.FileName + " to " + path + ".");
                return false;
            }
        }

        // ---------------------------------------------------------------- ledger (authority, standalone)

        private string LedgerPath { get { return Path.Combine(_dataDir(), KitCooldowns.FileName); } }

        private KitCooldowns Ledger()
        {
            if (_ledger != null) return _ledger;
            string path = LedgerPath;
            try
            {
                _ledger = File.Exists(path) ? KitCooldowns.FromJson(File.ReadAllText(path)) : new KitCooldowns();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Kits: " + path + " could not be read; every kit is ready for everyone.");
                _ledger = new KitCooldowns();
            }
            return _ledger;
        }

        private void SaveLedger(DateTime now)
        {
            string path = LedgerPath;
            try
            {
                _ledger.Prune(Catalogue(), now);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, _ledger.ToJson());
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Kits: could not save " + path + "; waits since the last save are lost at restart.");
            }
        }

        /// <summary>Authority and standalone: one ask, from a local command or an agent's frame.</summary>
        internal NexusKitAnswer Answer(NexusKitAsk ask, DateTime now)
        {
            bool changed;
            NexusKitAnswer answer = KitDesk.Answer(ask, Catalogue(), Ledger(), now, out changed);
            if (changed) SaveLedger(now);
            return answer;
        }

        // ---------------------------------------------------------------- the command

        /// <summary>Runs <c>!factions kit [name]</c> for <paramref name="caller"/>. Answers what to say
        /// now; on an agent the result follows as a whisper when the authority answers.</summary>
        internal string Run(CommandCaller caller, string name)
        {
            KitCatalogue catalogue = Catalogue();
            int rank = (int)caller.Promote;
            DateTime now = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (_holdsLedger)
                {
                    KitCooldowns ledger = Ledger();
                    return KitDesk.Listing(catalogue, rank, k => ledger.Remaining(caller.SteamId, k, now));
                }
                return Send(new NexusKitAsk { Op = KitOp.List, Steam = caller.SteamId, Rank = rank }, now);
            }

            KitDefinition kit = catalogue.Find(name);
            string refusal = KitDesk.Refusal(name, kit, rank);
            if (refusal != null) return refusal;
            if (CharacterOf(caller.SteamId) == null) return "You need to be alive and in your suit to claim a kit.";

            var claim = new NexusKitAsk { Op = KitOp.Claim, Steam = caller.SteamId, Kit = kit.Name, Rank = rank };
            if (!_holdsLedger) return Send(claim, now);

            NexusKitAnswer answer = Answer(claim, now);
            return answer.Ok ? Deliver(caller.SteamId, kit, answer, now) : answer.Message;
        }

        /// <summary>Hands a stamped claim's items out, or undoes the stamp when there is nobody to
        /// hand them to.</summary>
        private string Deliver(ulong steam, KitDefinition kit, NexusKitAnswer stamped, DateTime now)
        {
            int dropped;
            List<string> unknown;
            bool given = Grant(steam, kit, out dropped, out unknown);
            if (!given)
            {
                Undo(steam, kit.Name, stamped.StampedUtc, now);
                Record("KIT_UNDONE steam=" + steam + " kit=" + kit.Name + " reason=no-character");
                return "You need to be alive and in your suit to claim a kit.";
            }
            Record("KIT_CLAIMED steam=" + steam + " kit=" + kit.Name + " dropped=" + dropped
                     + (unknown.Count > 0 ? " unknown=" + string.Join(",", unknown) : ""));
            if (unknown.Count > 0)
                Log.Warn("Kits: kit '" + kit.Name + "' names item(s) this server does not define: " + string.Join(", ", unknown) + ".");
            string said = "Kit " + kit.Name + " claimed.";
            if (dropped > 0) said += " Your inventory was full, so " + (dropped == 1 ? "one stack is" : dropped + " stacks are") + " floating beside you.";
            if (kit.Cooldown > TimeSpan.Zero) said += " Next claim in " + KitDesk.FormatWait(kit.Cooldown) + ".";
            return said;
        }

        private void Undo(ulong steam, string kit, DateTime stampedUtc, DateTime now)
        {
            var release = new NexusKitAsk { Op = KitOp.Release, Steam = steam, Kit = kit, StampedUtc = stampedUtc };
            if (_holdsLedger) Answer(release, now);
            else
            {
                release.Id = ++_nextId;
                if (_ask == null || !_ask(release))
                    Log.Warn("KIT_RELEASE_UNSENT steam=" + steam + " kit=" + kit + "; the wait stands.");
            }
        }

        // ---------------------------------------------------------------- agent

        private string Send(NexusKitAsk ask, DateTime now)
        {
            ask.Id = ++_nextId;
            if (_ask == null || !_ask(ask))
                return "The cluster's simulation server cannot be reached from here; try again shortly.";
            _pending[ask.Id] = new Pending { Steam = ask.Steam, Op = ask.Op, Kit = ask.Kit, Rank = ask.Rank, SentUtc = now };
            return null;
        }

        /// <summary>Agent: the authority's answer to one of this server's asks.</summary>
        internal void OnAnswer(NexusKitAnswer answer)
        {
            DateTime now = DateTime.UtcNow;
            Pending p;
            if (!_pending.TryGetValue(answer.Id, out p))
            {
                // The player was told it failed: a claim stamped since must not stand.
                Pending late;
                if (_late.TryGetValue(answer.Id, out late))
                {
                    _late.Remove(answer.Id);
                    if (answer.Op == KitOp.Claim && answer.Ok) Undo(late.Steam, answer.Kit, answer.StampedUtc, now);
                }
                return;
            }
            _pending.Remove(answer.Id);

            switch (answer.Op)
            {
                case KitOp.List:
                    Dictionary<string, long> waits = answer.WaitSeconds;
                    Tell(p.Steam, KitDesk.Listing(Catalogue(), p.Rank, k =>
                    {
                        long s;
                        return waits != null && waits.TryGetValue(k.Name, out s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;
                    }));
                    return;

                case KitOp.Claim:
                    if (!answer.Ok) { Tell(p.Steam, answer.Message); return; }
                    KitDefinition kit = Catalogue().Find(answer.Kit);
                    if (kit == null)
                    {
                        Undo(p.Steam, answer.Kit, answer.StampedUtc, now);
                        Tell(p.Steam, KitDesk.Refusal(answer.Kit, null, p.Rank));
                        return;
                    }
                    Tell(p.Steam, Deliver(p.Steam, kit, answer, now));
                    return;
            }
        }

        /// <summary>Agent: tells the players whose asks went unanswered, and forgets late ids.</summary>
        internal void Tick(DateTime now)
        {
            if (_pending.Count > 0)
            {
                List<long> due = null;
                foreach (KeyValuePair<long, Pending> e in _pending)
                    if (now - e.Value.SentUtc > AnswerTimeout) (due ?? (due = new List<long>())).Add(e.Key);
                if (due != null)
                    foreach (long id in due)
                    {
                        Pending p = _pending[id];
                        _pending.Remove(id);
                        p.SentUtc = now;
                        _late[id] = p;
                        Tell(p.Steam, "The cluster's simulation server did not answer; no kit was handed out.");
                    }
            }
            if (_late.Count > 0)
            {
                List<long> gone = null;
                foreach (KeyValuePair<long, Pending> e in _late)
                    if (now - e.Value.SentUtc > LateWindow) (gone ?? (gone = new List<long>())).Add(e.Key);
                if (gone != null)
                    foreach (long id in gone) _late.Remove(id);
            }
        }

        private void Record(string line)
        {
            if (_record != null) _record(line);
        }

        private void Tell(ulong steam, string line)
        {
            if (string.IsNullOrEmpty(line) || _whisper == null) return;
            try { _whisper(steam, line); }
            catch (Exception ex) { Log.Warn(ex, "Kits: could not tell " + steam + "."); }
        }

        // ---------------------------------------------------------------- the items

        private static MyCharacter CharacterOf(ulong steam)
        {
            MyPlayer player;
            if (MySession.Static == null || MySession.Static.Players == null
                || !MySession.Static.Players.TryGetPlayerBySteamId(steam, out player) || player == null) return null;
            MyCharacter character = player.Character;
            return character != null && !character.IsDead && !character.MarkedForClose ? character : null;
        }

        /// <summary>Puts the kit in the player's inventory; what does not fit floats beside them, as
        /// anything SE hands a full inventory does. False when they have no living character, with
        /// nothing handed out.</summary>
        private static bool Grant(ulong steam, KitDefinition kit, out int dropped, out List<string> unknown)
        {
            dropped = 0;
            unknown = new List<string>();
            MyCharacter character = CharacterOf(steam);
            MyInventory inventory = character != null ? character.GetInventory() : null;
            if (inventory == null) return false;

            MatrixD at = character.WorldMatrix;
            foreach (KitItem item in kit.Items)
            {
                MyDefinitionId id;
                MyPhysicalItemDefinition definition;
                if (!TryParseId(item, out id) || !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out definition))
                {
                    unknown.Add(item.TypeId + "/" + item.SubtypeId);
                    continue;
                }
                var builder = MyObjectBuilderSerializer.CreateNewObject(id) as MyObjectBuilder_PhysicalObject;
                if (builder == null) { unknown.Add(item.TypeId + "/" + item.SubtypeId); continue; }

                MyFixedPoint want = (MyFixedPoint)(double)item.Amount;
                MyFixedPoint fits = inventory.CheckConstraint(id) ? inventory.ComputeAmountThatFits(id) : MyFixedPoint.Zero;
                fits = MyFixedPoint.Min(fits, want);
                if (definition.HasIntegralAmounts) fits = MyFixedPoint.Floor(fits);
                if (fits > MyFixedPoint.Zero && inventory.AddItems(fits, builder)) want -= fits;
                if (want <= MyFixedPoint.Zero) continue;

                var rest = MyObjectBuilderSerializer.CreateNewObject(id) as MyObjectBuilder_PhysicalObject;
                MyFloatingObjects.Spawn(new MyPhysicalInventoryItem(want, rest),
                    at.Translation + at.Forward * 1.5 + at.Up, at.Forward, at.Up, character.Physics);
                dropped++;
            }
            return true;
        }

        private static bool TryParseId(KitItem item, out MyDefinitionId id)
        {
            string type = item.TypeId.Trim();
            if (!type.StartsWith(MyObjectBuilderType.LEGACY_TYPE_PREFIX, StringComparison.Ordinal))
                type = MyObjectBuilderType.LEGACY_TYPE_PREFIX + type;
            return MyDefinitionId.TryParse(type, item.SubtypeId.Trim(), out id);
        }
    }
}
