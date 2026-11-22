using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>One stack a kit hands out: an SE physical item by its object-builder type and
    /// subtype, e.g. <c>ConsumableItem</c>/<c>Medkit</c>.</summary>
    public sealed class KitItem
    {
        public string TypeId { get; set; }
        public string SubtypeId { get; set; }
        public long Amount { get; set; }
    }

    /// <summary>A named bundle of items a player claims by chat command, at most once per
    /// <see cref="CooldownMinutes"/>, and only at or above <see cref="RequiredRank"/>.</summary>
    public sealed class KitDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Minutes between two claims by one player, cluster-wide. Zero: no cooldown.</summary>
        public double CooldownMinutes { get; set; }
        /// <summary>A promote level name (<see cref="KitRank.Names"/>); empty is <c>None</c>.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string RequiredRank { get; set; }
        public List<KitItem> Items { get; set; }

        [JsonIgnore]
        public TimeSpan Cooldown
        {
            get { return CooldownMinutes > 0 ? TimeSpan.FromMinutes(CooldownMinutes) : TimeSpan.Zero; }
        }

        /// <summary>The rank's ordinal; an unreadable name never parses, because the catalogue drops
        /// such a kit at load.</summary>
        [JsonIgnore]
        public int RankOrdinal
        {
            get { int r; return KitRank.TryParse(RequiredRank, out r) ? r : KitRank.Names.Length; }
        }
    }

    /// <summary>SE's promote levels, by name, in <c>MyPromoteLevel</c>'s ordinal order. Core holds no
    /// SE reference, so the host passes the caller's level as its ordinal.</summary>
    public static class KitRank
    {
        public static readonly string[] Names = { "None", "Scripter", "Moderator", "SpaceMaster", "Admin", "Owner" };

        /// <summary>Null or empty is <c>None</c>. Case-insensitive.</summary>
        public static bool TryParse(string name, out int rank)
        {
            rank = 0;
            if (string.IsNullOrWhiteSpace(name)) return true;
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(Names[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) { rank = i; return true; }
            return false;
        }

        public static string Name(int rank)
        {
            return rank >= 0 && rank < Names.Length ? Names[rank] : rank.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The server's kits, from <c>kits.json</c> — an operator-authored input, read from the shared data
    /// directory with the instance copy as fallback, and re-read whenever the file changes. Absent, the
    /// built-in <see cref="Defaults"/> apply: the two kits production ran under the old Kits plugin.
    /// </summary>
    public sealed class KitCatalogue
    {
        public const string FileName = "kits.json";

        public List<KitDefinition> Kits { get; set; }

        public KitCatalogue() { Kits = new List<KitDefinition>(); }

        public KitDefinition Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim();
            for (int i = 0; i < Kits.Count; i++)
                if (string.Equals(Kits[i].Name, n, StringComparison.OrdinalIgnoreCase)) return Kits[i];
            return null;
        }

        /// <summary>Production's kits as the old Kits plugin carried them (<c>Kits.xml</c> on Day and
        /// Dawn): <c>starter</c> every two hours for everyone, <c>sc</c> every four for Scripter and up. The starter
        /// kit has carried 1000 cobalt and 2000 each of iron, nickel and silicon ingots as well since
        /// 2026-11-21 (operator).</summary>
        public static KitCatalogue Defaults()
        {
            var c = new KitCatalogue();
            c.Kits.Add(new KitDefinition
            {
                Name = "starter",
                Description = "A pistol and ten magazines, two medkits, a powerkit, 1000 ice, 1000 cobalt ingots, 2000 each of iron, nickel and silicon ingots, and tier-2 grinder, welder and drill.",
                CooldownMinutes = 120,
                Items = new List<KitItem>
                {
                    Item("PhysicalGunObject", "FullAutoPistolItem", 1),
                    Item("AmmoMagazine", "FullAutoPistolMagazine", 10),
                    Item("ConsumableItem", "Medkit", 2),
                    Item("ConsumableItem", "Powerkit", 1),
                    Item("Ore", "Ice", 1000),
                    Item("Ingot", "Cobalt", 1000),
                    Item("Ingot", "Iron", 2000),
                    Item("Ingot", "Nickel", 2000),
                    Item("Ingot", "Silicon", 2000),
                    Item("PhysicalGunObject", "AngleGrinder2Item", 1),
                    Item("PhysicalGunObject", "Welder2Item", 1),
                    Item("PhysicalGunObject", "HandDrill2Item", 1),
                },
            });
            c.Kits.Add(new KitDefinition
            {
                Name = "sc",
                Description = "100,000 space credits.",
                CooldownMinutes = 240,
                RequiredRank = "Scripter",
                Items = new List<KitItem> { Item("PhysicalObject", "SpaceCredit", 100000) },
            });
            return c;
        }

        private static KitItem Item(string type, string subtype, long amount)
        {
            return new KitItem { TypeId = type, SubtypeId = subtype, Amount = amount };
        }

        /// <summary>Reads a catalogue, dropping each kit that cannot be handed out and saying why in
        /// <paramref name="problems"/>: no name, a name already taken, an unknown rank, a negative
        /// cooldown, no items, or an item without a type, subtype or positive amount.</summary>
        public static KitCatalogue Parse(string json, List<string> problems)
        {
            var read = JsonConvert.DeserializeObject<KitCatalogue>(json) ?? new KitCatalogue();
            var kept = new KitCatalogue();
            if (read.Kits == null) return kept;
            foreach (KitDefinition k in read.Kits)
            {
                string why = Invalid(k, kept);
                if (why != null) { if (problems != null) problems.Add(why); continue; }
                k.Name = k.Name.Trim();
                kept.Kits.Add(k);
            }
            return kept;
        }

        private static string Invalid(KitDefinition k, KitCatalogue kept)
        {
            if (k == null) return "an empty kit entry";
            if (string.IsNullOrWhiteSpace(k.Name)) return "a kit with no name";
            if (k.Name.Trim().IndexOf(' ') >= 0) return "kit '" + k.Name + "': a name cannot contain a space";
            if (kept.Find(k.Name) != null) return "kit '" + k.Name + "': the name is already taken";
            int rank;
            if (!KitRank.TryParse(k.RequiredRank, out rank))
                return "kit '" + k.Name + "': unknown rank '" + k.RequiredRank + "' (one of " + string.Join(", ", KitRank.Names) + ")";
            if (k.CooldownMinutes < 0 || double.IsNaN(k.CooldownMinutes)) return "kit '" + k.Name + "': negative cooldown";
            if (k.Items == null || k.Items.Count == 0) return "kit '" + k.Name + "': no items";
            foreach (KitItem i in k.Items)
                if (i == null || string.IsNullOrWhiteSpace(i.TypeId) || string.IsNullOrWhiteSpace(i.SubtypeId) || i.Amount <= 0)
                    return "kit '" + k.Name + "': an item needs a TypeId, a SubtypeId and a positive Amount";
            return null;
        }

        public string ToJson() { return JsonConvert.SerializeObject(this, Formatting.Indented); }
    }

    /// <summary>
    /// When each player last claimed each kit, keyed by Steam id so one ledger serves every server of
    /// a cluster. Held by the simulation authority alone (or a standalone server) and saved to
    /// <c>kit_cooldowns.json</c> in the data directory; an agent asks the authority instead.
    ///
    /// <para>Only the claim time is stored. The wait is worked out against the kit's cooldown as the
    /// catalogue reads now, so an operator who shortens a cooldown shortens every wait already running.</para>
    /// </summary>
    public sealed class KitCooldowns
    {
        public const string FileName = "kit_cooldowns.json";

        private readonly Dictionary<ulong, Dictionary<string, DateTime>> _claimed =
            new Dictionary<ulong, Dictionary<string, DateTime>>();

        public int PlayerCount { get { return _claimed.Count; } }

        /// <summary>Time until <paramref name="steam"/> may claim <paramref name="kit"/> again; zero
        /// when they may now.</summary>
        public TimeSpan Remaining(ulong steam, KitDefinition kit, DateTime now)
        {
            Dictionary<string, DateTime> byKit;
            DateTime at;
            if (kit == null || kit.Cooldown <= TimeSpan.Zero) return TimeSpan.Zero;
            if (!_claimed.TryGetValue(steam, out byKit) || !byKit.TryGetValue(Key(kit.Name), out at)) return TimeSpan.Zero;
            TimeSpan left = at + kit.Cooldown - now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>Records a claim at <paramref name="now"/>, or answers false with the wait still
        /// to run. A kit without a cooldown is never recorded.</summary>
        public bool TryStamp(ulong steam, KitDefinition kit, DateTime now, out TimeSpan remaining)
        {
            remaining = Remaining(steam, kit, now);
            if (remaining > TimeSpan.Zero) return false;
            if (kit.Cooldown <= TimeSpan.Zero) return true;
            Dictionary<string, DateTime> byKit;
            if (!_claimed.TryGetValue(steam, out byKit))
            {
                byKit = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                _claimed[steam] = byKit;
            }
            byKit[Key(kit.Name)] = now;
            return true;
        }

        /// <summary>Undoes the claim stamped at <paramref name="stampedAt"/>, for a kit that could not
        /// be handed out after all. A later claim is left alone.</summary>
        public bool Release(ulong steam, string kit, DateTime stampedAt)
        {
            Dictionary<string, DateTime> byKit;
            DateTime at;
            string key = Key(kit);
            if (!_claimed.TryGetValue(steam, out byKit) || !byKit.TryGetValue(key, out at) || at != stampedAt) return false;
            byKit.Remove(key);
            if (byKit.Count == 0) _claimed.Remove(steam);
            return true;
        }

        /// <summary>Drops every stamp whose wait has run out, or whose kit is gone from the catalogue,
        /// so the file holds only the waits still running.</summary>
        public int Prune(KitCatalogue catalogue, DateTime now)
        {
            int dropped = 0;
            var players = new List<ulong>(_claimed.Keys);
            foreach (ulong steam in players)
            {
                Dictionary<string, DateTime> byKit = _claimed[steam];
                var kits = new List<string>(byKit.Keys);
                foreach (string key in kits)
                {
                    KitDefinition kit = catalogue != null ? catalogue.Find(key) : null;
                    if (kit != null && byKit[key] + kit.Cooldown > now) continue;
                    byKit.Remove(key);
                    dropped++;
                }
                if (byKit.Count == 0) _claimed.Remove(steam);
            }
            return dropped;
        }

        private static string Key(string kit) { return (kit ?? "").Trim().ToLowerInvariant(); }

        private sealed class Doc
        {
            public Dictionary<string, Dictionary<string, DateTime>> Claimed { get; set; }
        }

        public string ToJson()
        {
            var doc = new Doc { Claimed = new Dictionary<string, Dictionary<string, DateTime>>() };
            foreach (KeyValuePair<ulong, Dictionary<string, DateTime>> p in _claimed)
                doc.Claimed[p.Key.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, DateTime>(p.Value);
            return JsonConvert.SerializeObject(doc, Formatting.Indented,
                new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc });
        }

        public static KitCooldowns FromJson(string json)
        {
            var c = new KitCooldowns();
            Doc doc = JsonConvert.DeserializeObject<Doc>(json,
                new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc });
            if (doc == null || doc.Claimed == null) return c;
            foreach (KeyValuePair<string, Dictionary<string, DateTime>> p in doc.Claimed)
            {
                ulong steam;
                if (p.Value == null || !ulong.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out steam)) continue;
                var byKit = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, DateTime> k in p.Value) byKit[Key(k.Key)] = k.Value;
                if (byKit.Count > 0) c._claimed[steam] = byKit;
            }
            return c;
        }
    }

    public enum KitOp
    {
        /// <summary>The caller's waits, for the listing.</summary>
        List = 0,
        /// <summary>Stamp a claim, or refuse it.</summary>
        Claim = 1,
        /// <summary>Undo a stamped claim the agent could not hand out.</summary>
        Release = 2,
    }

    /// <summary>An agent's kit request to the authority, which holds the cluster's cooldowns.</summary>
    public sealed class NexusKitAsk
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        /// <summary>The agent's number for the ask, echoed in the answer.</summary>
        public long Id;
        public KitOp Op;
        public ulong Steam;
        public string Kit;
        /// <summary>The caller's promote level on the agent, as its ordinal.</summary>
        public int Rank;
        /// <summary>Release only: the stamp being undone.</summary>
        public DateTime StampedUtc;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusKitAsk ask)
        {
            ask = NexusHandsCodec.Decode<NexusKitAsk>(payload, NexusHandsCodec.Lean);
            if (ask == null || ask.Version != CurrentVersion) { ask = null; return false; }
            return true;
        }
    }

    /// <summary>The authority's answer to a <see cref="NexusKitAsk"/>.</summary>
    public sealed class NexusKitAnswer
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public long Id;
        public KitOp Op;
        public bool Ok;
        public string Kit;
        /// <summary>Claim: what to tell the player when refused.</summary>
        public string Message;
        /// <summary>Claim: the stamp taken, which a release must name.</summary>
        public DateTime StampedUtc;
        /// <summary>List: whole seconds still to wait, per kit name; a kit that is ready is absent.</summary>
        public Dictionary<string, long> WaitSeconds;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusKitAnswer answer)
        {
            answer = NexusHandsCodec.Decode<NexusKitAnswer>(payload, NexusHandsCodec.Lean);
            if (answer == null || answer.Version != CurrentVersion) { answer = null; return false; }
            return true;
        }
    }

    /// <summary>
    /// The kit rules, in one place for the standalone server, the authority and the agents: who may
    /// claim what, when, and what the player is told.
    /// </summary>
    public static class KitDesk
    {
        /// <summary>Why <paramref name="kit"/> cannot be claimed at <paramref name="rank"/>, or null.
        /// An unknown kit and one above the caller's rank read the same, so a player cannot find the
        /// staff kits by guessing names.</summary>
        public static string Refusal(string name, KitDefinition kit, int rank)
        {
            if (kit == null || rank < kit.RankOrdinal)
                return "There is no kit called '" + (name ?? "") + "'. !factions kit lists the ones you can claim.";
            return null;
        }

        /// <summary>The authority's side of an ask: stamps or refuses a claim, undoes one, or reports the
        /// caller's waits. <paramref name="changed"/> says whether the ledger needs saving.</summary>
        public static NexusKitAnswer Answer(NexusKitAsk ask, KitCatalogue catalogue, KitCooldowns cooldowns,
                                            DateTime now, out bool changed)
        {
            changed = false;
            var answer = new NexusKitAnswer { Id = ask.Id, Op = ask.Op, Kit = ask.Kit };
            switch (ask.Op)
            {
                case KitOp.List:
                    answer.Ok = true;
                    foreach (KitDefinition k in catalogue.Kits)
                    {
                        TimeSpan left = cooldowns.Remaining(ask.Steam, k, now);
                        if (left <= TimeSpan.Zero) continue;
                        if (answer.WaitSeconds == null) answer.WaitSeconds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        answer.WaitSeconds[k.Name] = (long)Math.Ceiling(left.TotalSeconds);
                    }
                    return answer;

                case KitOp.Release:
                    changed = cooldowns.Release(ask.Steam, ask.Kit, ask.StampedUtc);
                    answer.Ok = changed;
                    return answer;

                case KitOp.Claim:
                    KitDefinition kit = catalogue.Find(ask.Kit);
                    string refusal = Refusal(ask.Kit, kit, ask.Rank);
                    if (refusal != null) { answer.Message = refusal; return answer; }
                    TimeSpan wait;
                    if (!cooldowns.TryStamp(ask.Steam, kit, now, out wait))
                    {
                        answer.Message = WaitMessage(kit, wait);
                        return answer;
                    }
                    answer.Ok = true;
                    answer.Kit = kit.Name;
                    answer.StampedUtc = now;
                    changed = kit.Cooldown > TimeSpan.Zero;
                    return answer;

                default:
                    answer.Message = "Unknown kit request.";
                    return answer;
            }
        }

        public static string WaitMessage(KitDefinition kit, TimeSpan wait)
        {
            return "You can claim the " + kit.Name + " kit again in " + FormatWait(wait) + ".";
        }

        /// <summary>The kits <paramref name="rank"/> may claim, each with its description and its wait.</summary>
        public static string Listing(KitCatalogue catalogue, int rank, Func<KitDefinition, TimeSpan> wait)
        {
            var visible = new List<KitDefinition>();
            foreach (KitDefinition k in catalogue.Kits)
                if (rank >= k.RankOrdinal) visible.Add(k);
            if (visible.Count == 0) return "There are no kits on this server.";

            int width = 0;
            foreach (KitDefinition k in visible) width = Math.Max(width, k.Name.Length);
            var sb = new StringBuilder("Kits (claim one with !factions kit <name>):");
            foreach (KitDefinition k in visible)
            {
                TimeSpan left = wait(k);
                sb.AppendLine();
                sb.Append("  ").Append(k.Name.PadRight(width)).Append("  ")
                  .Append(left > TimeSpan.Zero ? "in " + FormatWait(left) : "ready");
                if (k.Cooldown > TimeSpan.Zero) sb.Append(", every ").Append(FormatWait(k.Cooldown));
                sb.Append(" - ").Append(string.IsNullOrWhiteSpace(k.Description) ? Contents(k) : k.Description.Trim());
            }
            return sb.ToString();
        }

        /// <summary>The kit's items, for one with no description.</summary>
        public static string Contents(KitDefinition kit)
        {
            var parts = new List<string>();
            foreach (KitItem i in kit.Items) parts.Add(i.Amount.ToString(CultureInfo.InvariantCulture) + " " + i.SubtypeId);
            return string.Join(", ", parts);
        }

        /// <summary>A wait in hours and minutes, rounded up to the minute: "1 h 05 min", "12 min".</summary>
        public static string FormatWait(TimeSpan wait)
        {
            long minutes = (long)Math.Ceiling(wait.TotalMinutes);
            if (minutes < 1) minutes = 1;
            long h = minutes / 60, m = minutes % 60;
            if (h == 0) return m.ToString(CultureInfo.InvariantCulture) + " min";
            if (m == 0) return h.ToString(CultureInfo.InvariantCulture) + " h";
            return h.ToString(CultureInfo.InvariantCulture) + " h " + m.ToString("00", CultureInfo.InvariantCulture) + " min";
        }
    }
}
