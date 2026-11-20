using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The two strings a simulation-born grid carries, doing two different jobs.
    ///
    /// <para><b>The display name</b> (<see cref="Tag"/>, <see cref="Apply"/>) is the human-facing
    /// signal. A player faction's convoy is owned by the player's own identity (the hull spawns as
    /// the faction founder's), so in-world nothing but the tag separates it from the player's ships;
    /// and SE names a split fragment <c>Small Grid 1234</c>, which is also what a player's
    /// forgotten-to-rename hull is called. It is never a predicate: a display name is rewritten
    /// under a live entity by several things and a player can type anything into one.</para>
    ///
    /// <para><b>The entity name</b> (<see cref="NamePrefix"/>, <see cref="NameFor"/>,
    /// <see cref="IsSimBornName"/>) is a predicate, and the only one that survives a restart intact.
    /// It is not shown to players, is not editable from the terminal, is written to
    /// <c>MyObjectBuilder_EntityBase.Name</c> in the sector file, and — unlike the NPC-spawned bit,
    /// which <c>MyNPCGridClaimSessionComponent</c> clears on any grid a non-NPC identity owns once
    /// the claim timer runs out — SE never touches it.</para>
    /// </summary>
    public static class NpcGridNaming
    {
        public const string Tag = "[NPC]";

        /// <summary>What a piece shot off a tracked hull, or ordnance it released, is renamed to.</summary>
        public const string DebrisName = "[NPC] Debris";

        /// <summary>The tagged form of <paramref name="name"/>: <c>[NPC] name</c>, or the bare tag for
        /// a grid that has no name. Idempotent — an already-tagged name comes back unchanged, so a
        /// grid that is marked twice (a subgrid reached through two hulls' groups) is not tagged twice.</summary>
        public static string Apply(string name)
        {
            string trimmed = name == null ? "" : name.Trim();
            if (trimmed.Length == 0) return Tag;
            if (IsTagged(trimmed)) return trimmed;
            return Tag + " " + trimmed;
        }

        public static bool IsTagged(string name)
        {
            return name != null && name.TrimStart().StartsWith(Tag, StringComparison.Ordinal);
        }

        /// <summary>What a spawned fleet hull is called (docs/traffic-control.md, T10):
        /// <c>[NPC] EXP Haul 646CEA</c> — the owner's tag, the fleet's kind and its id, the id being the
        /// one the log, the dashboard and the traffic-control radio use, so a ship in view can be matched
        /// to a callout. Null when there is no id.</summary>
        public static string FleetDisplayName(string factionTag, string kind, string fleetId)
        {
            if (string.IsNullOrEmpty(fleetId)) return null;
            string tag = string.IsNullOrEmpty(factionTag) ? "" : factionTag + " ";
            string k = string.IsNullOrEmpty(kind) ? "" : kind + " ";
            return Tag + " " + tag + k + fleetId;
        }

        /// <summary>What a fleet hull's antennas and beacons broadcast: <c>[NPC] Haul 646CEA</c>. SE puts
        /// the owner's tag in front of any HUD text itself (<c>EXP.[NPC] Haul 646CEA</c>), so the tag is not
        /// repeated here; the rest is the hull's own name. Null when there is no id.</summary>
        public static string FleetBroadcastText(string kind, string fleetId)
        {
            if (string.IsNullOrEmpty(fleetId)) return null;
            return Tag + " " + (string.IsNullOrEmpty(kind) ? "" : kind + " ") + fleetId;
        }

        /// <summary>The fleet id at the end of a spawn key (<c>TAG_ticks_Kind_target_ID</c>, an escort's
        /// with <c>#n</c> after it); null when the last segment is not one.</summary>
        public static string FleetIdOfKey(string spawnKey)
        {
            if (string.IsNullOrEmpty(spawnKey)) return null;
            string key = spawnKey;
            int hash = key.LastIndexOf('#');
            if (hash > 0) key = key.Substring(0, hash);
            int us = key.LastIndexOf('_');
            if (us < 0 || us == key.Length - 1) return null;
            string id = key.Substring(us + 1);
            if (id.Length > 12) return null;
            foreach (char c in id) if (!char.IsLetterOrDigit(c)) return null;
            return id;
        }

        /// <summary>The prefix of the entity name every simulation-born grid carries.</summary>
        public const string NamePrefix = "factions:npc:";

        /// <summary>The entity name for the grid <paramref name="entityId"/>, carrying
        /// <paramref name="fleetKey"/> when there is one: <c>factions:npc:&lt;eid&gt;</c> or
        /// <c>factions:npc:&lt;eid&gt;:&lt;fleetKey&gt;</c>.
        ///
        /// <para>The entity id is the whole of the uniqueness: <c>MyEntities</c> keys entities by
        /// name and drops a second entity claiming a name already taken, so two grids must never be
        /// handed the same one. The fleet key follows it unparsed, colons and all, because the
        /// parse takes everything past the third colon.</para></summary>
        public static string NameFor(long entityId, string fleetKey)
        {
            string key = fleetKey == null ? "" : fleetKey;
            return key.Length == 0
                ? NamePrefix + entityId.ToString(CultureInfo.InvariantCulture)
                : NamePrefix + entityId.ToString(CultureInfo.InvariantCulture) + ":" + key;
        }

        /// <summary>Whether <paramref name="name"/> is one this plugin wrote. Ordinal and exact: SE
        /// gives every unnamed entity its own id as a name, a player can set one through the mod
        /// API, and neither shape may read as ours.</summary>
        public static bool IsSimBornName(string name)
        {
            long eid;
            string fleetKey;
            return TryParseName(name, out eid, out fleetKey);
        }

        /// <summary>Whether <paramref name="name"/> is one this plugin wrote <b>for
        /// <paramref name="entityId"/> itself</b>. The id has to match, and this is the form
        /// everything that acts on a grid uses: a blueprint keeps the entity name of the grid it was
        /// saved from, so a player pasting a copy of an NPC hull gets the name with a new entity id,
        /// and only the id tells the copy from the original.</summary>
        public static bool IsSimBornName(string name, long entityId)
        {
            long eid;
            string fleetKey;
            return TryParseName(name, out eid, out fleetKey) && eid == entityId;
        }

        /// <summary>Why a grid is, or is not, a candidate for having the durable entity name
        /// written onto it after the fact. See <see cref="ShouldBackfillName"/>.</summary>
        public enum BackfillVerdict
        {
            /// <summary>Write the name. The grid carries a weaker NPC signal and no durable one.</summary>
            Write = 0,
            /// <summary>Already carries a correct durable name — readoption's job, not backfill's.</summary>
            AlreadyNamed = 1,
            /// <summary>Carries a name of ours, but minted for a different entity id: a player pasted
            /// a blueprint taken from one of our hulls. Emphatically not ours.</summary>
            ForeignName = 2,
            /// <summary>Nothing marks it as simulation-born.</summary>
            NotOurs = 3,
        }

        /// <summary>Whether the durable entity name should be written onto a grid that predates the
        /// marking, judged from the three things a caller can read off a live grid.
        ///
        /// <para><b>Why this is a Core function over three booleans.</b> It is the whole of the
        /// correctness — the host half is a loop and one assignment — and it is the part that decides
        /// whether a player's ship gets claimed by the simulation. Here it can be tested; inside the
        /// entity walk it could only be reasoned about.</para>
        ///
        /// <para><b>The display tag is admitted, and only here.</b> <see cref="Tag"/> is not a
        /// predicate — a player can type it into any grid's name — so nothing reaps on it. Promoting
        /// it to the durable name under an operator's dry run is a different act from deleting on it:
        /// the operator sees the list first, and a grid wrongly promoted is then subject to the
        /// reaper's own refusals rather than to deletion.</para></summary>
        /// <param name="nameIsOursForThisGrid"><see cref="IsSimBornName(string,long)"/> against the
        /// grid's own entity id.</param>
        /// <param name="nameIsOursForAnother"><see cref="IsSimBornName(string)"/> true while
        /// <paramref name="nameIsOursForThisGrid"/> is false — our prefix, someone else's id.</param>
        /// <param name="npcSpawnedBit">SE's <c>IsNpcSpawnedGrid</c>, which its claim timer clears.</param>
        /// <param name="displayTagged"><see cref="IsTagged"/> on the display name.</param>
        public static BackfillVerdict ShouldBackfillName(bool nameIsOursForThisGrid,
                                                         bool nameIsOursForAnother,
                                                         bool npcSpawnedBit,
                                                         bool displayTagged)
        {
            if (nameIsOursForThisGrid) return BackfillVerdict.AlreadyNamed;
            // Checked before the weak signals on purpose: a pasted blueprint of an NPC hull carries
            // both our name prefix AND our display tag, and is the one case where the tag is
            // actively misleading.
            if (nameIsOursForAnother) return BackfillVerdict.ForeignName;
            if (npcSpawnedBit || displayTagged) return BackfillVerdict.Write;
            return BackfillVerdict.NotOurs;
        }

        /// <summary>The entity id and fleet key <paramref name="name"/> carries. False when the name
        /// is not one of ours or carries no readable id, in which case nothing is set.</summary>
        public static bool TryParseName(string name, out long entityId, out string fleetKey)
        {
            entityId = 0L;
            fleetKey = "";
            if (name == null || !name.StartsWith(NamePrefix, StringComparison.Ordinal)) return false;

            string rest = name.Substring(NamePrefix.Length);
            if (rest.Length == 0) return false;

            int colon = rest.IndexOf(':');
            string idPart = colon < 0 ? rest : rest.Substring(0, colon);

            long parsed;
            if (!long.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
                return false;

            entityId = parsed;
            if (colon >= 0) fleetKey = rest.Substring(colon + 1);
            return true;
        }
    }

    /// <summary>Whose a simulation-born grid is and how it came to be. <c>ParentEid</c> is the tracked
    /// grid it split from or was adopted through, <c>0</c> for a grid the spawner produced itself.</summary>
    public sealed class SimGridRecord
    {
        public string FleetKey;
        public string FactionTag;
        public string Kind;
        public long ParentEid;
        public DateTime MarkedUtc;

        /// <summary>The record was rebuilt from the grid's entity name after a restart rather than
        /// written when the grid was marked. What the name carries is all it holds, so
        /// <see cref="MarkedUtc"/> is this process's boot and dates nothing about the grid.</summary>
        public bool Restored;
    }

    /// <summary>
    /// Every grid this process has marked as simulation-born, by entity id. Per-process and not
    /// persisted: SE saves the NPC-spawned bit, the display name and the entity name on the grid
    /// itself, and a mod-storage record is dropped from the sector file unless a mod definition
    /// claims its GUID (<c>MyModStorageComponent.Serialize</c>), which a Torch plugin has no clean
    /// way to do. After a restart the entity name says simulation-born and carries the fleet key,
    /// so the record is rebuilt from it on the first reaper pass (<c>Restored</c>) rather than
    /// persisted.
    /// </summary>
    public sealed class SimGridRegistry
    {
        private readonly Dictionary<long, SimGridRecord> _byEid = new Dictionary<long, SimGridRecord>();

        public int Count { get { return _byEid.Count; } }

        public bool IsMarked(long entityId) { return entityId > 0 && _byEid.ContainsKey(entityId); }

        public bool TryGet(long entityId, out SimGridRecord record)
        {
            if (entityId > 0) return _byEid.TryGetValue(entityId, out record);
            record = null;
            return false;
        }

        /// <summary>Records <paramref name="entityId"/> once. False when it is already marked or is not
        /// a real id, so a caller can count what it newly marked rather than what it touched.</summary>
        public bool Mark(long entityId, string fleetKey, string factionTag, string kind, long parentEid, DateTime nowUtc)
        {
            return Mark(entityId, fleetKey, factionTag, kind, parentEid, nowUtc, false);
        }

        /// <summary>As above, with <paramref name="restored"/> set for a record rebuilt from the
        /// grid's entity name after a restart: it carries a fleet key and nothing else, so nothing
        /// may read an age off it.</summary>
        public bool Mark(long entityId, string fleetKey, string factionTag, string kind, long parentEid, DateTime nowUtc, bool restored)
        {
            if (entityId <= 0 || _byEid.ContainsKey(entityId)) return false;
            _byEid[entityId] = new SimGridRecord
            {
                FleetKey   = fleetKey ?? "",
                FactionTag = factionTag ?? "",
                Kind       = kind ?? "",
                ParentEid  = parentEid,
                MarkedUtc  = nowUtc,
                Restored   = restored,
            };
            return true;
        }

        public bool Forget(long entityId) { return entityId > 0 && _byEid.Remove(entityId); }

        /// <summary>Drops <paramref name="entityId"/>'s record when it is filed under a fleet key
        /// other than <paramref name="fleetKey"/>, so the spawn that built the grid can mark it as
        /// its own. True when a record was dropped.
        ///
        /// <para>A spawn's own callback is the authority on whose a grid is. Adoption is a guess
        /// made from ownership and distance, and a hull assembled over several frames is in the
        /// world before its callback runs, so a busy station's neighbour can adopt it first. Left
        /// there, the neighbour's despawn closes it.</para></summary>
        public bool ReleaseForeign(long entityId, string fleetKey)
        {
            SimGridRecord rec;
            if (entityId <= 0 || !_byEid.TryGetValue(entityId, out rec)) return false;
            if (string.Equals(rec.FleetKey, fleetKey ?? "", StringComparison.Ordinal)) return false;
            return _byEid.Remove(entityId);
        }

        public void Clear() { _byEid.Clear(); }

        /// <summary>Copies every marked id into <paramref name="into"/> (cleared first), so a caller can
        /// walk the set while marking or forgetting.</summary>
        public void CopyEids(List<long> into)
        {
            into.Clear();
            foreach (KeyValuePair<long, SimGridRecord> kv in _byEid) into.Add(kv.Key);
        }

        /// <summary>Copies every id marked under <paramref name="fleetKey"/> into
        /// <paramref name="into"/> (cleared first). A despawn closes these alongside the lead's
        /// mechanical group: a split fragment and released ordnance are attached to nothing, so the
        /// fleet key is the only thing that still says they belong to the hull being closed.</summary>
        public void CopyEidsForFleet(string fleetKey, List<long> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(fleetKey)) return;
            foreach (KeyValuePair<long, SimGridRecord> kv in _byEid)
                if (string.Equals(kv.Value.FleetKey, fleetKey, StringComparison.Ordinal)) into.Add(kv.Key);
        }

        public int CountForFleet(string fleetKey)
        {
            if (string.IsNullOrEmpty(fleetKey)) return 0;
            int n = 0;
            foreach (KeyValuePair<long, SimGridRecord> kv in _byEid)
                if (string.Equals(kv.Value.FleetKey, fleetKey, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
