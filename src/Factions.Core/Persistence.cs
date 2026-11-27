using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Factions.Core
{
    // ---- persistence DTOs (plain, JSON-friendly) -----------------------------------------------

    /// <summary>
    /// One persisted discovered-set entry: when a node was last detected, and what was true about it
    /// then. Property names are single letters because there is one of these per (faction, node) --
    /// at live scale that is thousands of entries per brain file, and the lean-JSON rule bites here
    /// before it bites anywhere else.
    ///
    /// <para><b>Two shapes are read and both are written.</b> An entry with no detail serialises as a
    /// bare timestamp -- which is exactly what every brain written before this existed already
    /// contains, so old files load unchanged and a file that gains no detail gains no bytes. An entry
    /// with detail serialises as an object. The converter below is what makes the pair legal in one
    /// dictionary.</para>
    /// </summary>
    [JsonConverter(typeof(KnowledgeEntryDocConverter))]
    public sealed class KnowledgeEntryDoc
    {
        public DateTime T;      // last detected, UTC
        public string O;        // owner tag at last detection
        public double H;        // hold strength at last detection
        public double V;        // resource value at last detection
        public bool HasDetail;  // not serialised: it is the shape, not a field
        public byte G;          // remembered intel tier (IntelTier); omitted while 0
        public byte S;          // stage at that reading (NodeStage); omitted while 0
        public DateTime GT;     // when G was read; omitted with G
        public byte D;          // defence band (DefenceBand) at the last Armament reading; omitted while 0
        public DateTime DT;     // when D was read; omitted with D
        public float DV;        // the scanned defence DPS behind D, server-side only; omitted with D

        public static KnowledgeEntryDoc From(KnownNode n)
        {
            return new KnowledgeEntryDoc
            {
                T = n.LastSeenUtc, HasDetail = n.HasDetail, O = n.OwnerTag, H = n.HoldStrength,
                V = n.ResourceValue, G = (byte)n.Tier, S = (byte)n.Stage, GT = n.GradedUtc,
                D = (byte)n.Defence, DT = n.DefenceUtc, DV = n.DefenceDps,
            };
        }

        public KnownNode ToKnownNode()
        {
            return new KnownNode
            {
                LastSeenUtc = T, HasDetail = HasDetail, OwnerTag = O, HoldStrength = H, ResourceValue = V,
                Tier = HasDetail ? (IntelTier)G : IntelTier.None,
                Stage = HasDetail && G != 0 ? (NodeStage)S : NodeStage.None,
                GradedUtc = HasDetail && G != 0 ? GT : default(DateTime),
                Defence = HasDetail ? (DefenceBand)D : DefenceBand.Unknown,
                DefenceUtc = HasDetail && D != 0 ? DT : default(DateTime),
                DefenceDps = HasDetail && D != 0 ? DV : 0f,
            };
        }
    }

    /// <summary>Reads a discovered-set entry written either as a bare timestamp (pre-snapshot, and
    /// still the shape for a detection that carried no detail) or as an object. Writes back whichever
    /// shape the entry actually is.</summary>
    public sealed class KnowledgeEntryDocConverter : JsonConverter
    {
        public override bool CanConvert(Type t) { return t == typeof(KnowledgeEntryDoc); }

        public override void WriteJson(JsonWriter w, object value, JsonSerializer serializer)
        {
            var e = (KnowledgeEntryDoc)value;
            if (e == null) { w.WriteNull(); return; }
            if (!e.HasDetail) { w.WriteValue(e.T); return; }
            w.WriteStartObject();
            w.WritePropertyName("T"); w.WriteValue(e.T);
            if (!string.IsNullOrEmpty(e.O)) { w.WritePropertyName("O"); w.WriteValue(e.O); }
            w.WritePropertyName("H"); w.WriteValue(e.H);
            w.WritePropertyName("V"); w.WriteValue(e.V);
            if (e.G != 0)
            {
                w.WritePropertyName("G"); w.WriteValue(e.G);
                if (e.S != 0) { w.WritePropertyName("S"); w.WriteValue(e.S); }
                w.WritePropertyName("GT"); w.WriteValue(e.GT);
            }
            if (e.D != 0)
            {
                w.WritePropertyName("D"); w.WriteValue(e.D);
                w.WritePropertyName("DT"); w.WriteValue(e.DT);
                if (e.DV > 0) { w.WritePropertyName("DV"); w.WriteValue(e.DV); }
            }
            w.WriteEndObject();
        }

        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer serializer)
        {
            if (r.TokenType == JsonToken.Null) return null;
            if (r.TokenType == JsonToken.Date)
                return new KnowledgeEntryDoc { T = ToUtc(r.Value), HasDetail = false };
            if (r.TokenType == JsonToken.String)
            {
                DateTime parsed;
                if (!DateTime.TryParse((string)r.Value, CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out parsed))
                    return null;
                return new KnowledgeEntryDoc { T = parsed, HasDetail = false };
            }
            if (r.TokenType != JsonToken.StartObject) return null;

            JObject o = JObject.Load(r);
            JToken stamp = o["T"];
            if (stamp == null) return null;
            var entry = new KnowledgeEntryDoc { T = ToUtc(stamp.ToObject<DateTime>()) };
            JToken owner = o["O"], hold = o["H"], value = o["V"];
            // An object shape means a real detection wrote it, even if the node was unowned at the
            // time -- so detail is the shape, not the presence of an owner tag. Reading it off "O"
            // alone would silently downgrade every entry for a derelict node to no-detail.
            entry.HasDetail = true;
            if (owner != null) entry.O = owner.Type == JTokenType.Null ? null : owner.ToObject<string>();
            if (hold != null) entry.H = hold.ToObject<double>();
            if (value != null) entry.V = value.ToObject<double>();
            JToken grade = o["G"], stage = o["S"], graded = o["GT"];
            if (grade != null && graded != null)
            {
                entry.G = grade.ToObject<byte>();
                if (stage != null) entry.S = stage.ToObject<byte>();
                entry.GT = ToUtc(graded.ToObject<DateTime>());
            }
            JToken defence = o["D"], defenceAt = o["DT"];
            if (defence != null && defenceAt != null)
            {
                entry.D = defence.ToObject<byte>();
                entry.DT = ToUtc(defenceAt.ToObject<DateTime>());
                JToken dps = o["DV"];
                if (dps != null) entry.DV = dps.ToObject<float>();
            }
            return entry;
        }

        private static DateTime ToUtc(object raw)
        {
            var dt = (DateTime)raw;
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }

        private static DateTime ToUtc(DateTime dt)
        {
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }
    }

    public sealed class IndexEntry
    {
        public string Tag { get; set; }
        public string PersonalityId { get; set; }
        public int RngSeed { get; set; }
        public DateTime NextThinkUtc { get; set; }
    }

    /// <summary>World-level central-bank state: the money-supply anchor and when it was last
    /// committed. See <see cref="FactionStore.SaveCentralBankState"/>.</summary>
    public sealed class CentralBankStateDoc
    {
        public long CollateralAnchor { get; set; }
        public DateTime CollateralAnchorUtc { get; set; }
    }

    /// <summary>Persistence DTO for a <see cref="BuildQueueEntry"/>.</summary>
    public sealed class BuildQueueEntryDoc
    {
        public string Id { get; set; }
        /// <summary>Absent for a build named by hull (a player's or a doctrine line's), which carries
        /// no role.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Role { get; set; }
        public string PrefabId { get; set; }
        /// <summary>Claim rank against other builds and capability installs; 0 (the default, and
        /// what an older doc without the field loads as) is the original behaviour.</summary>
        public int Priority { get; set; }
        public Dictionary<string, long> Bom { get; set; }
        public Dictionary<string, long> Collected { get; set; }
        public Dictionary<string, long> ReservedInputs { get; set; }
        public List<BuildRecipeProgressDoc> RecipeProgress { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        /// <summary>Serviced seconds without progress. Absent in a doc written before stall-based
        /// abandonment shipped, which loads as 0 — a restart therefore hands every existing build a
        /// full fresh stall budget. Deliberately forgiving: the alternative would abandon builds on
        /// the first boot after the upgrade.</summary>
        public double StalledSeconds { get; set; }
        /// <summary>Serviced seconds since the entry was created, the denominator of its inflow
        /// rate. Absent in a doc written before the rate shipped, which loads as 0 — the entry then
        /// re-earns its measurement rather than inheriting a rate it never had.</summary>
        public double ServicedSeconds { get; set; }

        /// <summary>Whether this build stocks the player store. Written only when true — a fleet
        /// build is the overwhelming majority and carries no key. Absent loads as false, which is
        /// also the right reading of a doc written before the store roles were retired: such a doc
        /// records the purpose in <see cref="Role"/> instead, and the loader recovers it from there.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool DestinedForSale { get; set; }

        /// <summary>Whether a human set <see cref="Priority"/> by hand. Written only when true — the
        /// overwhelming majority are role-derived and carry no key. Absent loads as false, which is
        /// the correct reading of every doc written before the flag existed: those entries took
        /// whatever rank the table gave them, and a rank that no longer matches that table is a
        /// table change rather than an instruction.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool PriorityByPlayer { get; set; }
    }

    /// <summary>Persistence DTO for one recipe's entry in <see cref="BuildQueueEntry.RecipeProgress"/>.</summary>
    public sealed class BuildRecipeProgressDoc
    {
        public string BlueprintId { get; set; }
        public double Accum { get; set; }
        public DateTime LastTickUtc { get; set; }
    }

    /// <summary>LEGACY persistence DTO for the pre-<see cref="StandingOrder"/> one-order-per-hub
    /// auto-mine dictionary. Still READ (<see cref="FactionStore.ApplyBrainDoc"/> migrates each entry
    /// into <see cref="FactionState.StandingOrders"/> as a Mine order) and never written again, so an
    /// existing brain file keeps its standing orders across the upgrade. Delete once no live brain
    /// file carries the key.</summary>
    public sealed class MineAutoOrderDoc
    {
        public double PeriodSeconds { get; set; }
        public DateTime NextScanUtc { get; set; }
        public Dictionary<string, double> OreWeights { get; set; }
    }

    /// <summary>Persistence DTO for a <see cref="StandingOrder"/>. Kind-specific fields are nullable
    /// or default-valued so the half that does not apply to <see cref="Kind"/> is omitted from JSON
    /// entirely -- the repo's "keep JSON lean" rule, and the reason one DTO can carry every kind
    /// without a Mine order paying for the Stock fields or the reverse.</summary>
    public sealed class StandingOrderDoc
    {
        public string Id { get; set; }
        /// <summary>StandingOrderKind NAME, never its ordinal -- a reordered enum must not silently
        /// reinterpret an existing file, and an unrecognised kind is dropped on load rather than
        /// loaded as whatever happens to be 0. Same rule <see cref="FactionEventDoc.Kind"/> follows.</summary>
        public string Kind { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Priority { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Paused { get; set; }
        public DateTime CreatedUtc { get; set; }

        // Mine
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string HubNodeId { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double PeriodSeconds { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> OreWeights { get; set; }
        /// <summary>Manager-owned cursor. Persisted so a restart does not re-scan every standing
        /// order in the same tick the world loads.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime NextScanUtc { get; set; }

        // Stock
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string NodeId { get; set; }
        /// <summary>A build without this field loads the order with an empty NodeId, which
        /// <see cref="StockOrders.Targets"/> skips: a rollback asks for nothing.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool AllStations { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ItemKey { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long TargetQuantity { get; set; }

        // Build -- READ ONLY, and only to migrate. StandingOrderKind.Build is retired: a doc
        // carrying Kind "Build" becomes a DoctrineLineDoc's worth of state on
        // FactionState.FleetDoctrine (FactionStore.ApplyBrainDoc) and is never written back, so
        // these six fields only ever appear in a file written before the doctrine existed.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string PrefabId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Role { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TargetCount { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int MaxConcurrent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ScopeNodeId { get; set; }
        /// <summary>Dropped by the migration rather than carried: a hull on the shelf is never
        /// "ours" for doctrine purposes, so the listed slice is always subtracted now.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ExcludeListedForSale { get; set; }
    }

    /// <summary>Persistence DTO for a <see cref="DoctrineLine"/> -- one line of what a faction's
    /// fleet is made of. Every field is player-set; the manager-owned log signatures are deliberately
    /// absent, being state about logging rather than about the fleet. Defaults are omitted from JSON
    /// (the repo's "keep JSON lean" rule), and an absent <c>BrainDoc.FleetDoctrine</c> loads as an
    /// empty doctrine, which is exactly "behave as before".</summary>
    public sealed class DoctrineLineDoc
    {
        // A line carries no role: what it answers is read off its hull. A file written while lines
        // had one still carries "Role"; the deserializer drops it as an unknown member.
        public string Id { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string PrefabId { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TargetCount { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int MaxConcurrent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ScopeNodeId { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Priority { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Paused { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Persistence DTO for a <see cref="ShelfLine"/> -- one row of what a faction keeps on
    /// sale. Every field is player-set; the manager-owned log signatures are deliberately absent,
    /// being state about logging rather than about the shelf. Defaults are omitted from JSON, and an
    /// absent <c>BrainDoc.Shelf</c> loads as an empty shelf, which is exactly "behave as
    /// before".</summary>
    public sealed class ShelfLineDoc
    {
        public string Id { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string PrefabId { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TargetCount { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Paused { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>One ore in a persisted <see cref="SourceDoc.Deposit"/>. Short property names: this
    /// repeats per ore per node across every brain file.</summary>
    public sealed class DepositEntryDoc
    {
        /// <summary>Bare SE ore subtype, unprefixed.</summary>
        public string O { get; set; }
        /// <summary>Accumulated observation weight, not a normalised share — shares are derived on
        /// read so a survey can keep adding to this. See FAC-101.</summary>
        public double S { get; set; }
        /// <summary>Relative richness.</summary>
        public double R { get; set; }
        /// <summary>Rock volume, plain band-metres (<see cref="DepositEntry.Volume"/>). Omitted when
        /// unmeasured, which is every entry written before it existed.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? V { get; set; }
    }

    public sealed class SourceDoc
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Strength { get; set; }
        /// <summary>Legacy: the influence radius a node was saved with, before FAC-250 made it
        /// derived. Still <b>read</b> (an old save loads and its value is dropped, like <c>Kind</c>)
        /// and never written any more — hence <c>DefaultValueHandling.Ignore</c>, which is what keeps
        /// a dead <c>"Radius": 0.0</c> off every one of the several hundred nodes in every brain file.
        /// The lean-JSON rule in CLAUDE.md is about exactly this: the files are loaded one at a time
        /// and bloat compounds.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Radius { get; set; }
        public double ResourceValue { get; set; }
        /// <summary>Measured ore density, 0..1 — <see cref="SpatialNode.Richness"/>. Absent in JSON
        /// written before FAC-219; such a node loads at 0 and is re-derived by the boot audit pass
        /// (<see cref="WorldModel.ReclassifyNodes"/>), which is the only thing that reaches a node
        /// the ore seam declined for.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Richness { get; set; }
        /// <summary>Hostile-activity level, 0..1 — <see cref="SpatialNode.Threat"/>. Omitted when
        /// zero, which is nearly every node nearly always, so the lean-JSON rule costs this field
        /// nothing on a quiet world.
        /// <para>Persisted because every defensive mechanism reads it as a level and a restart that
        /// silently zeroed it made the signal a function of uptime instead of danger: measured live
        /// over 2026-09-11/12, the day with ONE restart and 1,603 raid lines dispatched 24 patrols
        /// and the day with SIXTEEN restarts and 2,412 raid lines dispatched none. It does not
        /// accrue decay across downtime, matching <see cref="SpatialNode.HoldStrength"/>, which is
        /// likewise only aged by a pass that does not run while the server is down.</para></summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Threat { get; set; }
        /// <summary>SE <c>MyStation.Id</c> for E### economy station nodes. 0 otherwise.</summary>
        public long StableStationId { get; set; }
        /// <summary>Nonzero once a physical asteroid field has been materialized near this node.
        /// 0 = not yet. See <see cref="SpatialNode.AsteroidFieldSeed"/>.</summary>
        public int AsteroidFieldSeed { get; set; }
        /// <summary>Prefab SubtypeId for station-backed nodes when known. Null otherwise.</summary>
        public string StationPrefabId { get; set; }
        /// <summary>The design a completed refit has paid for and the host has not yet rebuilt the
        /// node as — see <see cref="SpatialNode.PendingStationPrefabId"/>. Omitted from the JSON
        /// whenever nothing is owed, which is every node almost all of the time; an absent value
        /// loads as null, which reads as "no rebuild pending".</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string PendingStationPrefabId { get; set; }
        /// <summary>Cached station prefab book value in credits when known. Null otherwise.</summary>
        public long? StationBookValue { get; set; }
        /// <summary>Cached station prefab weapon loadout. Null/empty when unarmed or not yet scanned.</summary>
        public List<WeaponBand> StationWeapons { get; set; }
        /// <summary>Human-readable grid DisplayName for E### nodes. Null when not yet resolved.</summary>
        public string DisplayName { get; set; }
        /// <summary><b>Retired.</b> The old node-kind ordinal, written by every brain file up to
        /// FAC-102 and read as authority by none since: a node's kind is derived from its deposit and
        /// its site on every boot, exactly as <see cref="SpatialNode.Zone"/> is. Kept as a field so an
        /// existing brain still deserializes, and never written back — a file rewritten after FAC-102
        /// carries <see cref="KindLabel"/> instead.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? Kind { get; set; }

        /// <summary>The bare subtype of the ore dominating this node's deposit, or empty when there
        /// is none — written for readers that only ever see the file, since the dashboard API reads
        /// brain files directly rather than the live model.
        /// <para><b>Derived, not durable.</b> Written on every flush from the node's own deposit and
        /// never read back: <c>ApplyBrainDoc</c> ignores it. It is a rendering of state, not state,
        /// and the deposit it is derived from is persisted alongside it.</para></summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string DominantOre { get; set; }

        /// <summary><b>Retired.</b> The derived kind name written between FAC-102 stages 1 and 3.
        /// Read so an existing brain deserializes; never written.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string KindLabel { get; set; }
        /// <summary>The node's sampled ore deposit. Omitted entirely when never sampled or barren —
        /// most nodes are not deposits, and this file is loaded one faction at a time. An absent value
        /// loads as null, which the seeding pass reads as "not sampled yet".
        /// See <see cref="SpatialNode.Deposit"/>.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<DepositEntryDoc> Deposit { get; set; }

        /// <summary>Survey points examined for this node's deposit. The whole resumable state of the
        /// survey — the next point to probe is derived from it, so a restart continues rather than
        /// starting over and re-probing ground already covered. Omitted when zero.
        /// <para><b>With no <see cref="Deposit"/> beside it, this is the record that the ground was
        /// sampled and held nothing.</b> A barren deposit has no entries to write, so the count is
        /// the only thing separating it from ground nobody has looked at.</para></summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public long? DepositPoints { get; set; }

        /// <summary><see cref="NodeDeposit.Units"/> the deposit beside it was measured in. Absent on
        /// everything written before units were recorded, which loads as 0 and is re-surveyed.
        /// Omitted when there is no deposit.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? DepositUnits { get; set; }
        /// <summary>Undirected spatial topology: IDs of directly adjacent sources. Null for an old
        /// save with no topology or an isolated node; a current save writes a sorted, de-duplicated
        /// list. Links may cross brain files, so hydration reconnects whatever endpoint is resident
        /// and safely ignores a stale/missing endpoint.</summary>
        public List<string> Neighbors { get; set; }
        /// <summary>UTC timestamp of the last reinforce action (or discovery for scan-backed nodes).
        /// DateTime.MinValue = never reinforced; old JSON leaves this field absent (same effect).</summary>
        public DateTime LastReinforcedUtc { get; set; }
        /// <summary>Station safe zone down until (a raid got through), and when a player last did
        /// damage here. Both omitted when default. The first has to survive a restart: the physical
        /// safe zone is re-created from scratch on every materialisation, so without this a reboot
        /// or a despawn/respawn cycle hands a just-raided station its protection back for free.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime SafeZoneDownUntilUtc { get; set; }
        /// <summary>Whose raid opened that window. Null for most nodes and omitted; JSON written
        /// before this shipped loads null, which lets nobody's fire count as phase 2 through an
        /// opened window until the next raid lands.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string SafeZoneDownByTag { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime LastPlayerAggressionUtc { get; set; }
        /// <summary>Contested until — a landed attack withholding this node's traffic-driven hold
        /// gain. Omitted when default, which is almost every node. Has to survive a restart for the
        /// same reason the safe-zone window does: a reboot inside the window would hand a besieged
        /// node its regeneration back.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime ContestedUntilUtc { get; set; }
        /// <summary>Who that landed attack belonged to — the faction the derelict is reserved for
        /// while the window runs. Null for a player's siege and for every node not in one, so it is
        /// omitted from almost every node; JSON written before this shipped loads null, which reads
        /// as a player siege and holds the node for the remainder of a window that is at most
        /// minutes old at that point.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ContestedByTag { get; set; }
        /// <summary>A player siege in progress, written only while one exists — null for the
        /// overwhelming majority of nodes, which have never been besieged. Seven fields on every
        /// SourceDoc would be seven fields of nothing per node per flush; see CLAUDE.md's lean-JSON
        /// rule.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public SiegeDoc Siege { get; set; }
        /// <summary>Ship-traffic credit and the stamp it was written at — see
        /// <see cref="SpatialNode.ShipActivityCredits"/>. Durable: an accumulator, rebuildable from
        /// nothing else. Both omitted at their defaults, which is every node that has never had an
        /// owning-faction fleet resolve a stop on it, and every node in JSON written before this
        /// shipped — such a node loads at zero credit and simply starts accruing.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double ShipActivityCredits { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime ShipActivityAtUtc { get; set; }
        /// <summary>UTC timestamp this node was transferred to <see cref="NeutralFaction.Tag"/>
        /// (Withdraw or decay expiry). DateTime.MinValue = never abandoned -- the correct default for
        /// every node, old or new. See <see cref="WorldModel.GetExpiredUnknNodeIds"/>.</summary>
        public DateTime AbandonedUtc { get; set; }
        /// <summary>UTC timestamp of the last Haul convoy dispatched FROM this node.
        /// DateTime.MinValue = never -- the correct default for every node, old or new (no scan-backed
        /// fallback the way LastReinforcedUtc has). docs/material-vectors.md's DemandHistory addendum.</summary>
        public DateTime LastHaulDepartedUtc { get; set; }
        /// <summary>UTC timestamp of the last Haul convoy resolved TO this node. DateTime.MinValue = never.</summary>
        public DateTime LastHaulArrivedUtc { get; set; }
        /// <summary>UTC timestamp of the last Mine dwell that resolved at this node.
        /// DateTime.MinValue = never -- same default-safe convention as LastHaulDepartedUtc.</summary>
        public DateTime LastMinedUtc { get; set; }
        /// <summary>Absolute deadline before which this node cannot be mined again.
        /// DateTime.MinValue = never mined, already eligible.</summary>
        public DateTime MineCooldownUntilUtc { get; set; }
        /// <summary>Decaying, item-volume-weighted delivery-history record keyed by SubTypeId. Null
        /// when empty. docs/material-vectors.md's DemandHistory addendum.</summary>
        public Dictionary<string, double> DemandHistory { get; set; }
        /// <summary>Decaying record of units this node has sold to a paying counterparty, keyed by
        /// inventory key. Null when empty. See <see cref="SpatialNode.SalesHistory"/>.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> SalesHistory { get; set; }
        /// <summary>Units per hour of each fuel key this node hands to departing hulls, and when the
        /// estimate was last written — the decay is derived from that on read. Both null/default
        /// until a draw runs at this node, which is every node in every mode below
        /// <see cref="FuelDrawModes.Draw"/>. docs/fuel-plan.md.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> FuelBurnEma { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime FuelBurnEmaAtUtc { get; set; }
        /// <summary>Units per hour of each ore key Mine fleets deliver to this node, and when the
        /// estimate was last written. Both null/default at every node no Mine fleet delivers to.
        /// See <see cref="SpatialNode.MinedEma"/>; docs/mining-plan.md.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> MinedEma { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime MinedEmaAtUtc { get; set; }
        /// <summary>Per-node item stock keyed by <see cref="ItemKind"/> name. Null when empty.
        /// Replaces the faction-level <c>BrainDoc.Inventory</c> field (kept for migration reads).</summary>
        public Dictionary<string, long> Inventory { get; set; }
        /// <summary>Per-node stock physically reserved for capability installation. Null when empty.</summary>
        public Dictionary<string, long> ReservedInventory { get; set; }
        /// <summary>People living at this node, the clock their upkeep runs on, and the nutrition
        /// eaten ahead of what they owe. All three absent at every node without workers.
        /// See <see cref="SpatialNode.Workers"/>; FAC-493.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Workers { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime WorkersAtUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double WorkerFoodBank { get; set; }
        /// <summary>Magazines drawn into this node's materialised station and not yet credited back —
        /// <see cref="SpatialNode.StationMunitions"/>. Null when none. A station grid never survives a
        /// restart, so this is only ever read to hand the magazines back at boot.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, long> StationMunitions { get; set; }
        /// <summary>Loot drawn into this node's materialised station and not yet credited back —
        /// <see cref="SpatialNode.StationLoot"/>. Null when none; only ever read to hand it back at boot.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, long> StationLoot { get; set; }
        /// <summary><see cref="SpatialNode.LootRestockUtc"/>. Omitted when unset; a restart does not
        /// reopen a looted station's stores.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime LootRestockUtc { get; set; }
        /// <summary>Installed capabilities keyed by <see cref="NodeCapabilityKind"/> name → level.
        /// Null when empty (no capabilities installed). New in Slice 2.</summary>
        public Dictionary<string, int> Capabilities { get; set; }
        /// <summary>Ships built and ready to dispatch, keyed by prefab SubtypeId ("" = free-built).
        /// Not role-partitioned — see <see cref="ShipPool"/>. Null when empty.</summary>
        public Dictionary<string, int> ShipStock { get; set; }
        /// <summary>Legacy pre-unification field: role-keyed prefab-ID lists. Never written by a
        /// current save; read only so <c>ApplyBrainDoc</c> can migrate an old doc's exact prefab
        /// identity into the new prefab-keyed <see cref="ShipStock"/>. Safe to remove once no
        /// pre-2026-08-01 brain files remain in production.</summary>
        public Dictionary<string, List<string>> ShipPrefabQueue { get; set; }
        /// <summary>Of each prefab's <see cref="ShipStock"/> count, how many are reserved for store
        /// sale — see <see cref="SpatialNode.ListedForSale"/>. Null when empty.</summary>
        public Dictionary<string, int> ListedForSale { get; set; }
        /// <summary>Active build queue entries. Null when empty. Persisted so multi-day builds survive reboots.</summary>
        public List<BuildQueueEntryDoc> BuildQueue { get; set; }
        /// <summary>Hulls waiting here for repair -- see <see cref="SpatialNode.DamagedHulls"/>. Null
        /// when empty.</summary>
        public List<DamagedHullDoc> DamagedHulls { get; set; }
        /// <summary>Whether this station's shipyard is paused — see <see cref="SpatialNode.ShipyardPaused"/>.
        /// Absent in an older doc, which loads as false (not paused).</summary>
        public bool ShipyardPaused { get; set; }
        /// <summary>High-water mark behind <see cref="SpatialNode.MintBuildEntryId"/>. Absent in an
        /// older doc, which loads as 0 and is then raised past every id the node already carries —
        /// see the load path, where that recovery is what keeps a pre-existing queue's ids unique.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int BuildSeq { get; set; }
    }

    /// <summary>Persistence DTO for a future itinerary leg. Missing current-stop and remaining-stop
    /// fields identify a legacy single-leg fleet, which remains deliberately unmigrated on load.</summary>
    public sealed class FleetStopDoc
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string NodeId { get; set; }
        public string Kind { get; set; } // FleetStopKind name
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool HasPosition { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        // A clearance stop carries none of the Pickup/Mine payload, and a routed detour emits many of
        // them — omitting the defaults keeps the per-faction brain file from carrying dead weight.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string PickupItemKey { get; set; } // Pickup-kind stops only; null otherwise
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long PickupQuantity { get; set; }   // Pickup-kind stops only; 0 otherwise
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double DurationSeconds { get; set; }         // Mine-kind stops only; 0 otherwise
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, double> MineWeights { get; set; } // Mine-kind stops only; null otherwise
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double MineTargetLiters { get; set; }         // Mine-kind stops only; 0 otherwise
    }

    /// <summary>Persistence DTO for one in-flight fleet event.
    /// Transient fields not stored: <c>Intercepted</c> (cleaned up before flush).
    /// <c>MaterialisedEntityId</c>: only 0 and &gt;0 are stored; SpawningId (−2) and −1 are
    /// sanitised to 0 on write so a reboot never re-enters those transient states.
    /// <c>Resolved</c> IS stored so a patrol doesn't re-apply its arrival effect.
    /// <c>ForcedRatio</c> IS stored so a force-failed grid's ratio=0 survives a reboot, together
    /// with <c>ForcedRatioCause</c> naming the writer that set it.</summary>
    /// <summary>A player siege against one node's station safe zone. Persisted so a restart cannot
    /// hand a besieged or breached station its protection back for free, and so the conquer clock —
    /// ten minutes, long enough to span a restart — survives one.</summary>
    public sealed class SiegeDoc
    {
        public double ZoneIntegrity { get; set; }
        public DateTime ZoneIntegrityAtUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime SiegeStartedUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime SiegeLapseSinceUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime ZoneBreachedUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double PlayerRaidBlockDamage { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime PlayerRaidSucceededUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime PlayerPresenceStripUtc { get; set; }

        /// <summary>This node's siege state, or null when it has never been besieged — which is
        /// almost every node, and why the doc is nested and omitted rather than written flat.
        ///
        /// <para>The null test is here rather than at the call site because a reader that has to
        /// rebuild a node from the doc needs to agree with the writer about what "no siege" is; the
        /// sentinel is a negative <see cref="SpatialNode.ZoneIntegrity"/> AND no start stamp, since
        /// a siege that has just begun still carries the full pool.</para></summary>
        public static SiegeDoc From(SpatialNode n)
        {
            if (n == null) return null;
            if (n.ZoneIntegrity < 0 && n.SiegeStartedUtc == default(DateTime)) return null;
            return new SiegeDoc
            {
                ZoneIntegrity = n.ZoneIntegrity,
                ZoneIntegrityAtUtc = n.ZoneIntegrityAtUtc,
                SiegeStartedUtc = n.SiegeStartedUtc,
                SiegeLapseSinceUtc = n.SiegeLapseSinceUtc,
                ZoneBreachedUtc = n.ZoneBreachedUtc,
                PlayerRaidBlockDamage = n.PlayerRaidBlockDamage,
                PlayerRaidSucceededUtc = n.PlayerRaidSucceededUtc,
                PlayerPresenceStripUtc = n.PlayerPresenceStripUtc,
            };
        }

        /// <summary>Write this doc back onto a node. Used by the brain loader and by the dashboard's
        /// snapshot builder, which rebuilds a scratch node purely so it can ask
        /// <see cref="PlayerSiege"/> the same questions the host asks — the map and the in-game
        /// safe-zone colour then read one derivation rather than two.</summary>
        public void ApplyTo(SpatialNode n)
        {
            if (n == null) return;
            n.ZoneIntegrity = ZoneIntegrity;
            n.ZoneIntegrityAtUtc = ZoneIntegrityAtUtc;
            n.SiegeStartedUtc = SiegeStartedUtc;
            n.SiegeLapseSinceUtc = SiegeLapseSinceUtc;
            n.ZoneBreachedUtc = ZoneBreachedUtc;
            n.PlayerRaidBlockDamage = PlayerRaidBlockDamage;
            n.PlayerRaidSucceededUtc = PlayerRaidSucceededUtc;
            n.PlayerPresenceStripUtc = PlayerPresenceStripUtc;
        }
    }

    public sealed class PendingFleetDoc
    {
        public string Kind { get; set; }            // ActionKind name: Trade, Raid, or Patrol
        public string ActorTag { get; set; }
        public string TargetTag { get; set; }
        public string OriginNodeId { get; set; }
        /// <summary>Where a surviving hull is credited at the terminal leg (see
        /// <see cref="PendingFleetAction.ReturnNodeId"/>). Omitted when null, which is also what
        /// every fleet persisted before FAC-277 reads as -- the terminal falls back to
        /// <see cref="OriginNodeId"/> for those.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ReturnNodeId { get; set; }
        /// <summary>The raid holding this convoy on an Intercept leg (see
        /// <see cref="PendingFleetAction.InterceptedByFleetId"/>). Omitted when null.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string InterceptedByFleetId { get; set; }
        public string TargetNodeId { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double TargetZ { get; set; }
        public DateTime DepartedUtc { get; set; }
        public DateTime EstimatedArrivalUtc { get; set; }
        public long CargoValue { get; set; }
        public Dictionary<string, long> Cargo { get; set; }  // ItemKind name → quantity; null for non-Trade or empty
        /// <summary>Magazines aboard (see <see cref="PendingFleetAction.Munitions"/>). Zero entries
        /// are dropped: a key the fleet fires and does not hold reads back as absent, which the duel
        /// treats as empty. Omitted when nothing is aboard.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, long> Munitions { get; set; }
        /// <summary>See <see cref="PendingFleetAction.MunitionsArmed"/>. Omitted when false, which is
        /// also what every fleet persisted before munitions existed reads as.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool MunitionsArmed { get; set; }
        /// <summary>See <see cref="PendingFleetAction.RetreatReason"/>. Omitted when null.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string RetreatReason { get; set; }
        public double TradeMagnitude { get; set; }  // lane weight; seeds convoy-class selection
        public int ConvoySize { get; set; }          // ships to materialise per event (1 = solo, >1 = burst)
        public bool Resolved { get; set; }          // true if effects have been applied already
        /// <summary>See <see cref="PendingFleetAction.AgainstIntruder"/>. Omitted when false.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool AgainstIntruder { get; set; }
        public string CurrentStopKind { get; set; } // FleetStopKind name; missing = legacy Service
        public List<FleetStopDoc> RemainingStops { get; set; } // null for a one-way or legacy fleet
        /// <summary>See <see cref="PendingFleetAction.SuspendedStops"/>: the circuit of a patrol standing
        /// beside another defender. Omitted when null, which is every other fleet.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<FleetStopDoc> SuspendedStops { get; set; }
        public string PickupItemKey { get; set; } // current leg only, when CurrentStopKind == Pickup
        public long PickupQuantity { get; set; }
        public Dictionary<string, double> MineWeights { get; set; } // current leg only, when CurrentStopKind == Mine
        public double MineTargetLiters { get; set; }
        /// <summary>[x,y,z] of <see cref="PendingFleetAction.MineWanderUp"/>: a Mine dwell in
        /// gravity only, absent otherwise.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double[] MineWanderUp { get; set; }
        /// <summary>Active clearance polyline: total length, how much of it precedes the current leg,
        /// and the clock the whole polyline's single acceleration profile is anchored to. All zero
        /// for a fleet not flying a routed detour. Persisted so a reboot mid-arc does not restart the
        /// profile at every remaining waypoint.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double RouteDistanceKm { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double RouteCoveredKm { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime RouteDepartedUtc { get; set; }
        /// <summary>The leg's speed-tier run table, as parallel arrays in path order: kilometres and
        /// the <see cref="TransitTier"/> each run is flown in. Both omitted for a leg that is
        /// entirely open space, which is most of them. See <c>PendingFleetAction.LegZones</c>.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double[] ZoneRunKm { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int[] ZoneRunTiers { get; set; }
        /// <summary>Mid-leg retarget bookkeeping (see <c>PendingFleetResolver.Retarget</c>): how many
        /// times this fleet has been redirected and when it last was. Both are churn control that
        /// has to survive a reboot -- a fleet that comes back with a fresh budget and no cooldown is
        /// free to start oscillating again. Omitted when never retargeted.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int RetargetCount { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime LastRetargetUtc { get; set; }
        public string[] PathNodeIds { get; set; }   // null when no graph path (disconnected / immediate)
        public long MaterialisedEntityId { get; set; } // 0 = not spawned; >0 = live grid entity ID (SpawningId/-1 sanitised to 0)
        public string FleetId { get; set; }          // 6-hex log correlation ID; null for old brain docs
        public double? ForcedRatio { get; set; }     // overrides CompletionRatio at resolve time (null = use normal ratio)
        /// <summary>Writer tag for <c>ForcedRatio</c> ("grid_destroyed", "grid_stalled", ...), so a
        /// persisted ratio=0 says which mechanism produced it. Omitted when null.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ForcedRatioCause { get; set; }
        public string FerryRole { get; set; }        // Ferry only: ship role being repositioned ("Trade", "Raid", etc.)
        public string RelocationRole { get; set; }   // 0-cargo repositioning Haul only: role being relocated ("Trade"/"Mine"); Ferry stays reserved for ship-carries-ship
        /// <summary>Trade only: this convoy flies the acquisition direction, so its Pickup leg buys
        /// from a partner's node rather than loading from an owned one. Omitted when false (the
        /// ordinary sell direction), keeping existing brain files byte-identical.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsProcurement { get; set; }
        public string ShipPrefabId { get; set; }     // prefab SubtypeId of convoy ships; "" = free-built
        public long CargoCapacityLiters { get; set; } // total cargo capacity of the convoy (0 = unknown)
        /// <summary>Prefab IDs of a mixed Raid squadron's ships; null for non-squadron fleets. Old
        /// pre-unification brain files persisted this as a list of <c>{Role,PrefabId}</c> objects
        /// (the retired <c>SquadronMember</c> struct) — <see cref="SquadronConverter"/> migrates
        /// either shape into a flat prefab-ID list on load so an old save doesn't hard-crash
        /// deserialization (as it did live on 2026-08-01: JsonReaderException on this exact field).</summary>
        [JsonConverter(typeof(SquadronConverter))]
        public List<string> Squadron { get; set; }
        /// <summary>Live grid entity IDs of the squadron members other than the lead
        /// (<see cref="MaterialisedEntityId"/>). Only IDs &gt; 0 are written. Omitted when the fleet
        /// has no escorts, which is every non-squadron fleet — without it a restart mid-observation
        /// keeps no reference to the escort grids and orphans them in the world.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<long> EscortEntityIds { get; set; }
        /// <summary>Prefab IDs of the <see cref="Squadron"/> members whose grid has not been lost.
        /// Omitted when null, which is every fleet that has not lost a hull and means "all of
        /// <see cref="Squadron"/>".</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<string> SurvivingHulls { get; set; }
        /// <summary>Per-hull integrity, parallel to the surviving hulls (one entry for a fleet with
        /// no squadron) -- see <see cref="PendingFleetAction.HullIntegrity"/>. Omitted when every
        /// hull is whole; absent in an older doc, which loads as whole.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<double> HullIntegrity { get; set; }
        /// <summary>How the current dwell's fight has gone so far when the grids took part in it (see
        /// <see cref="PendingFleetAction.Fight"/>). Omitted when null, which is every fleet whose
        /// fight has only ever been abstract.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public FightProgressDoc Fight { get; set; }
        /// <summary><see cref="PendingFleetAction.HullsMeasured"/>. Omitted when false.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool HullsMeasured { get; set; }
        /// <summary><see cref="PendingFleetAction.HullsSettled"/>. Omitted when false.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool HullsSettled { get; set; }
    }

    /// <summary>Persistence DTO for a <see cref="FightProgress"/>. The decided segment it may hold
    /// is not written: a restart between that segment and the dwell's end fights the turns left.</summary>
    public sealed class FightProgressDoc
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TurnsFought { get; set; }
        public double AttackerBase { get; set; }
        public double DefenderBase { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int AttackerHulls { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int DefenderHulls { get; set; }
        /// <summary>Written only below 1.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? StationShare { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Physical { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool FoughtPhysically { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool FoughtAbstractly { get; set; }

        public static FightProgressDoc From(FightProgress p)
        {
            if (p == null) return null;
            return new FightProgressDoc
            {
                TurnsFought = p.TurnsFought,
                AttackerBase = p.AttackerBase,
                DefenderBase = p.DefenderBase,
                AttackerHulls = p.AttackerHulls,
                DefenderHulls = p.DefenderHulls,
                StationShare = p.StationShare < 1.0 ? (double?)p.StationShare : null,
                Physical = p.Physical,
                FoughtPhysically = p.FoughtPhysically,
                FoughtAbstractly = p.FoughtAbstractly,
            };
        }

        public static FightProgress To(FightProgressDoc d)
        {
            if (d == null) return null;
            return new FightProgress
            {
                TurnsFought = Math.Max(0, d.TurnsFought),
                AttackerBase = Math.Max(0, d.AttackerBase),
                DefenderBase = Math.Max(0, d.DefenderBase),
                AttackerHulls = Math.Max(0, d.AttackerHulls),
                DefenderHulls = Math.Max(0, d.DefenderHulls),
                StationShare = d.StationShare.HasValue ? Math.Max(0, Math.Min(1, d.StationShare.Value)) : 1.0,
                Physical = d.Physical,
                FoughtPhysically = d.FoughtPhysically,
                FoughtAbstractly = d.FoughtAbstractly,
            };
        }
    }

    /// <summary>Persistence DTO for a <see cref="DamagedHull"/>.</summary>
    public sealed class DamagedHullDoc
    {
        public string PrefabId { get; set; }
        public double Integrity { get; set; }
        public DateTime QueuedUtc { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public DateTime PartsCompleteUtc { get; set; }
        public Dictionary<string, long> Bill { get; set; }
        /// <summary>Null until the yard has absorbed something toward <see cref="Bill"/>.</summary>
        public Dictionary<string, long> Collected { get; set; }
        /// <summary>The version a refit hull rejoins stock as; absent for a repair.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string RefitTo { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double RefitShare { get; set; }
    }

    /// <summary>Reads <see cref="PendingFleetDoc.Squadron"/> as either the current flat
    /// <c>["prefabId", ...]</c> shape or the legacy <c>[{"Role":"...","PrefabId":"..."}, ...]</c>
    /// shape, normalizing both to a flat prefab-ID list. Writes are always the current shape —
    /// <c>SaveBrain</c> never produces the legacy object form.</summary>
    internal sealed class SquadronConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) { return objectType == typeof(List<string>); }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            JArray arr = JArray.Load(reader);
            var result = new List<string>();
            foreach (JToken tok in arr)
            {
                if (tok.Type == JTokenType.String)
                {
                    result.Add(tok.Value<string>());
                }
                else if (tok.Type == JTokenType.Object)
                {
                    JToken prefabId = tok["PrefabId"];
                    result.Add(prefabId != null ? (prefabId.Value<string>() ?? "") : "");
                }
            }
            return result.Count > 0 ? result : null;
        }
    }

    /// <summary>Persistence DTO for a single <see cref="GrudgeRecord"/>.</summary>
    /// <summary>Persistence DTO for one <see cref="FactionEvent"/> in a faction's history ring.
    /// The kind travels as its name so a reordered enum cannot silently reinterpret old files, and
    /// the two numeric fields are omitted whenever the kind carries no quantity — most kinds do
    /// not, and the ring is written for every faction on every flush.</summary>
    public sealed class FactionEventDoc
    {
        public DateTime Utc { get; set; }
        public string Kind { get; set; }            // FactionEventKind name
        public string ActorTag { get; set; }
        public string OtherTag { get; set; }        // omitted for a one-sided event
        public string NodeId { get; set; }          // omitted when the event has no place
        public long? Amount { get; set; }           // omitted when zero
        public double? Ratio { get; set; }          // omitted when zero
    }

    public sealed class GrudgeDoc
    {
        public string Kind { get; set; }       // GrudgeKind name
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>Persistence DTO for one outstanding <see cref="DiplomacyMessage"/>. Persisted so an
    /// offer involving a player faction survives a restart inside
    /// <see cref="FactionTuning.PlayerOfferTtl"/>; an NPC-to-NPC offer is pruned by its own short
    /// TTL on the first pass after load. Verbs travel as names.</summary>
    public sealed class ProposalDoc
    {
        public List<string> Verbs { get; set; }     // DiplomacyVerb names, in term order
        public List<string> Params { get; set; }    // per-term Param; omitted when every term has none
        public DateTime IssuedUtc { get; set; }
    }

    /// <summary>Persistence DTO for a non-operative campaign objective.</summary>
    public sealed class CampaignObjectiveDoc
    {
        public string Kind { get; set; }
        public string TargetNodeId { get; set; }
        // Region-targeted kinds (SeizeRegion, Terrorise) name a region rather than a node; omitted
        // for the defensive kind. Without these a region objective did not survive a restart at all.
        public string TargetRegionKind { get; set; }
        public string TargetRegionKey { get; set; }
        public string TargetPlanetId { get; set; }
        public int? WantedCombatHulls { get; set; }
        public long? WantedCombatDps { get; set; }   // Terrorise: the DPS budget (omitted when zero)
        // Offensive kinds only: why the campaign exists, and when it last dispatched. The cause is
        // stored by name for the same reason Kind is. The stamp has to survive a restart or the
        // tempo clock resets on every boot and a standing objective fires a fresh wave at load.
        // Candidates are deliberately absent: the brain rebuilds them on its next think.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Cause { get; set; }
        public DateTime? LastDispatchUtc { get; set; }
        // What came back from that dispatch, and the fleet it is waiting on. Every field is omitted
        // until there is an outcome to report, which is most objectives most of the time. Persisted
        // for the same reason the stamp above is: a fleet outlives a restart, and an objective that
        // forgot which one it sent would take the loss as never having happened.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string LastOutcome { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? LastOutcomeUtc { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? LastOutcomeRangeM { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? LastOutcomeOwnDps { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? LastOutcomeOpposedDps { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? LastOutcomeHullsLost { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? LastOutcomeHullsTotal { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string LastDispatchFleetId { get; set; }
    }

    /// <summary>Persistence DTO for one <see cref="FightRecord"/>.</summary>
    public sealed class FightDoc
    {
        public DateTime Utc { get; set; }
        public string Kind { get; set; }            // ActionKind name
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Attacking { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Opponent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string NodeId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Region { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long AttackerDps { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long DefenderDps { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double Ratio { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int HullsLost { get; set; }
        /// <summary>"AttackerArm/BreakOff:DefenderArm/BreakOff", e.g. "Kite/High:Standoff/Mid";
        /// omitted when range was not contested.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Arms { get; set; }
        /// <summary>The bracket of every turn fought, metres, space-separated; omitted with
        /// <see cref="Arms"/>.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Brackets { get; set; }
    }

    /// <summary>Persistence DTO for <see cref="TacticMemory"/>. Omitted from the brain when empty.</summary>
    public sealed class TacticMemoryDoc
    {
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long Updates { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<TacticOpponentDoc> Opponents { get; set; }
        /// <summary><see cref="TacticMemory.Reviews"/>, oldest first; omitted when there are none.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<TacticReviewDoc> Reviews { get; set; }
    }

    /// <summary>One <see cref="TacticReview"/> as the Tactics window reads it.</summary>
    public sealed class TacticReviewDoc
    {
        public DateTime Utc { get; set; }
        public string Vs { get; set; }
        public string Ctx { get; set; }
        /// <summary><see cref="TacticMemory.Index"/> of the arm × break-off fought.</summary>
        public int Cell { get; set; }
        /// <summary>Predicted damage share; omitted when there was no prediction.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public double? Pred { get; set; }
        public double Act { get; set; }
        public double Rew { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Mistake { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Commanded { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Lost { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Brought { get; set; }
    }

    public sealed class TacticOpponentDoc
    {
        public string Tag { get; set; }
        public DateTime Utc { get; set; }
        public List<TacticBucketDoc> Buckets { get; set; }
    }

    public sealed class TacticBucketDoc
    {
        /// <summary><see cref="TacticContext.Key"/>.</summary>
        public string Ctx { get; set; }
        public DateTime Utc { get; set; }
        /// <summary>Non-empty cells as "index:A:B", space-separated — one string so an indented
        /// brain file spends one line per key, not one per number.</summary>
        public string Cells { get; set; }
    }

    /// <summary>Persistence DTO for a single defensive campaign focus.</summary>
    public sealed class CampaignFocusDoc
    {
        public string Id { get; set; }
        public string CenterNodeId { get; set; }
        public double Priority { get; set; }
        public DateTime SetAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public List<string> RegionMembers { get; set; }
        public List<CampaignObjectiveDoc> Objectives { get; set; }
        // A hand-placed player directive rather than a brain nomination. Omitted when false, which
        // is every focus written before it existed and every one a brain nominates.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool PlayerIssued { get; set; }
    }

    /// <summary>Pure selection result for the explicit legacy territory reconciliation. It distinguishes
    /// unrecoverable engine derelicts from real economy stations, whose ownership must be rediscovered
    /// by the host scanner rather than trusted from an old UNKN brain.</summary>
    public sealed class TerritoryReconciliationPlan
    {
        public readonly HashSet<string> SerializedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> LegacyUnknEngineNodeIds = new List<string>();
        public readonly List<string> LegacyUnknEconomyStationIds = new List<string>();

        public static TerritoryReconciliationPlan Create(IEnumerable<BrainDoc> docs)
        {
            var plan = new TerritoryReconciliationPlan();
            if (docs == null) return plan;
            foreach (BrainDoc doc in docs)
            {
                if (doc == null || doc.Sources == null) continue;
                foreach (SourceDoc source in doc.Sources)
                {
                    if (source == null || string.IsNullOrEmpty(source.Id)) continue;
                    plan.SerializedSourceIds.Add(source.Id);
                    if (doc.Tag != NeutralFaction.Tag || doc.TopologyVersion >= 1) continue;
                    if (SourceMapping.IsEconomyStationSourceId(source.Id))
                        plan.LegacyUnknEconomyStationIds.Add(source.Id);
                    else if (!SourceMapping.IsScanBacked(source.Id))
                        plan.LegacyUnknEngineNodeIds.Add(source.Id);
                }
            }
            plan.LegacyUnknEngineNodeIds.Sort(StringComparer.Ordinal);
            plan.LegacyUnknEconomyStationIds.Sort(StringComparer.Ordinal);
            return plan;
        }

        /// <summary>Returns the explicit recovery allow-list: economy-station IDs already persisted
        /// in UNKN. A raw checkpoint cannot otherwise distinguish vanilla station records from
        /// Factions-founded lazy records, so the host must rehydrate only these stable IDs.</summary>
        public static List<string> UnknEconomyRecoveryIds(BrainDoc unkn)
        {
            var ids = new List<string>();
            if (unkn == null || !string.Equals(unkn.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase)
                || unkn.Sources == null) return ids;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SourceDoc source in unkn.Sources)
                if (source != null && SourceMapping.IsEconomyStationSourceId(source.Id) && seen.Add(source.Id))
                    ids.Add(source.Id);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        /// <summary>Returns candidate source IDs already owned by a non-UNKN persisted brain.
        /// Apply must reject these before writing UNKN because graph hydration keys by source ID.</summary>
        public static List<string> NonUnknSourceCollisions(IEnumerable<BrainDoc> docs, IEnumerable<SourceDoc> candidates)
        {
            var candidateIds = new HashSet<string>(StringComparer.Ordinal);
            if (candidates != null)
                foreach (SourceDoc candidate in candidates)
                    if (candidate != null && !string.IsNullOrEmpty(candidate.Id)) candidateIds.Add(candidate.Id);
            var collisions = new List<string>();
            if (candidateIds.Count == 0 || docs == null) return collisions;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BrainDoc doc in docs)
            {
                if (doc == null || string.Equals(doc.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase)
                    || doc.Sources == null) continue;
                foreach (SourceDoc source in doc.Sources)
                    if (source != null && candidateIds.Contains(source.Id)
                        && seen.Add(source.Id + "\u001f" + doc.Tag))
                        collisions.Add(source.Id + ":" + doc.Tag);
            }
            collisions.Sort(StringComparer.Ordinal);
            return collisions;
        }

        /// <summary>Replaces every persisted UNKN source with a validated, bounded economy-station
        /// candidate set. This is deliberately scoped to the supplied UNKN document: callers cannot
        /// use it to alter any other faction's ownership or persisted topology.</summary>
        public int ReplaceAllUnknSources(BrainDoc unkn, IEnumerable<SourceDoc> candidates)
        {
            if (unkn == null || !string.Equals(unkn.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                return 0;

            int removed = unkn.Sources == null ? 0 : unkn.Sources.Count;
            var replacement = new List<SourceDoc>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (candidates != null)
            {
                foreach (SourceDoc source in candidates)
                {
                    if (source == null || !SourceMapping.IsEconomyStationSourceId(source.Id)
                        || source.StableStationId <= 0 || !seen.Add(source.Id))
                        continue;
                    replacement.Add(source);
                }
            }

            unkn.Sources = replacement;
            unkn.TopologyVersion = 1;
            return removed;
        }
    }

    /// <summary>One role's persisted requirement. Bucket counts are written as a plain list — twelve
    /// numbers — rather than a keyed object, because the bucket index IS the key and naming it would
    /// triple the bytes for no reader's benefit (CLAUDE.md rule 4).</summary>
    public sealed class RoleRequirementDoc
    {
        public double[] Buckets { get; set; }
        public double UnmetEma { get; set; }
        public long Samples { get; set; }
        /// <summary>What the buckets measure (<see cref="RoleRequirement.CurrentBasis"/>). A record
        /// under any other basis is discarded on load, not read. Absent in files written before it
        /// existed, which is basis 0.</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Basis { get; set; }
    }

    public sealed class BrainDoc
    {
        /// <summary>Schema version for persisted spatial topology. 0 is a legacy document whose
        /// absent neighbor data must never be reconstructed geometrically.</summary>
        public int TopologyVersion { get; set; }
        public string Tag { get; set; }
        public long FactionId { get; set; }
        public string PersonalityId { get; set; }
        public int RngSeed { get; set; }
        public long Treasury { get; set; }                         // the private ledger (NOT SE credits)
        public long? Debt { get; set; }                            // outstanding borrowed credits (omitted when zero)
        public long? CentralBankSupportDebt { get; set; }          // rescue-loan principal (omitted when zero)
        public double? CentralBankRecentIncome { get; set; }       // EMA of real income for the debt-service cap (omitted when zero)
        /// <summary>Item inventories keyed by <see cref="ItemKind"/> name (e.g. "Ore"). Set externally
        /// after ToBrainDoc (like Treasury) so no WorldModel ref is needed in the mapper. Omitted when all zero.</summary>
        public Dictionary<string, long> Inventory { get; set; }
        public DateTime? DormantUntilUtc { get; set; }             // set only while collapsed-and-waiting (omitted otherwise)
        public bool? IsPlayerFaction { get; set; }                 // true only for a Magnate-claimed faction (omitted for NPCs)
        public DateTime? CentralBankSupportCooldownUntilUtc { get; set; } // set only while rescue lending is cooling down
        public DateTime? CentralBankGrantCooldownUntilUtc { get; set; }   // set only while grant policy is cooling down
        public double[] Drives { get; set; }                       // length 5, index = (int)Drive
        public Dictionary<string, DateTime> Cooldowns { get; set; }              // ActionKind name -> next-allowed
        public Dictionary<string, double> ActionRepetition { get; set; }         // ActionKind name -> repetition pressure
        public Dictionary<string, DateTime> TradeDestCooldowns { get; set; }   // partnerTag -> next-allowed Trade dispatch
        public Dictionary<string, DateTime> PatrolNodeCooldowns { get; set; } // nodeId -> next-allowed Patrol dispatch (omitted when empty)
        public Dictionary<string, DateTime> ReinforceNodeCooldowns { get; set; } // nodeId -> next-allowed Reinforce dispatch (omitted when empty)
        public Dictionary<string, double> ReinforcePresenceSeconds { get; set; } // nodeId -> accrued presence-as-Reinforce seconds (omitted when empty)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, KnowledgeEntryDoc> Knowledge { get; set; } // nodeId -> last detected + what was seen (omitted when empty); the owned-and-adjacent floor is a read-time rule and is deliberately NOT stored here
        public List<string> ReservationOptIn { get; set; } // NodeCapabilityKind names a player faction opted into ProductionManager investment/reservation for (omitted when empty)
        public Dictionary<string, int> InstallPriority { get; set; } // NodeCapabilityKind name -> capability-install claim rank vs ship builds (omitted when empty)
        public Dictionary<string, int> ReinforceOptIn { get; set; } // node id -> hold-maintenance rank, player-set (omitted when empty)
        public double? ReinforceIncomeFractionOverride { get; set; } // null = use world tuning
        public long? ReinforceBudgetFloorOverride { get; set; }      // null = use world tuning
        public double? ReinforceWealthFractionOverride { get; set; } // null = use world tuning
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? TradeBuyOptIn { get; set; }  // true only for a player faction that opted in to acquisition (omitted otherwise)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? TradeSellOptIn { get; set; } // true only for a player faction that opted in to selling (omitted otherwise)
        public double? TradeIncomeFractionOverride { get; set; } // null = use world tuning
        public long? TradeBudgetFloorOverride { get; set; }      // null = use world tuning
        public double? TradeWealthFractionOverride { get; set; } // null = use world tuning
        public Dictionary<string, MineAutoOrderDoc> MineAutoOrders { get; set; } // LEGACY read-only: hubNodeId -> standing auto-mine order, migrated into StandingOrders on load and never written again
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<StandingOrderDoc> StandingOrders { get; set; } // Magnate-tier standing orders, ranked (omitted when empty)
        public Dictionary<string, RoleRequirementDoc> FleetRequirements { get; set; } // role -> what that role's jobs have required (omitted when empty)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int StandingOrderSeq { get; set; }                  // never-reused id counter behind StandingOrder.Id (omitted when zero)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<DoctrineLineDoc> FleetDoctrine { get; set; }   // what this faction's fleet is made of, ranked (omitted when empty)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int DoctrineSeq { get; set; }                       // never-reused id counter behind DoctrineLine.Id (omitted when zero)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<ShelfLineDoc> Shelf { get; set; }              // what this faction keeps on sale (omitted when empty)
        public Dictionary<string, int> HullRefitCaps { get; set; } // design family -> hulls in refit at once, player-set (omitted when empty)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, int> DecommissionOnReturn { get; set; } // design family -> flying hulls still to dismantle on return (omitted when empty)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int ShelfSeq { get; set; }                          // never-reused id counter behind ShelfLine.Id (omitted when zero)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool StoreShelfOptOut { get; set; }                 // the player switched shelf restocking off (omitted when false, the default)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool CentralBankSellOptOut { get; set; }            // legacy faction-wide bank switch; written back only while not yet expanded into CentralBankDontSell
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<string> CentralBankDontSell { get; set; }      // inventory keys the central bank may not buy from this faction (omitted when empty)
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, long> CentralBankTargets { get; set; } // inventory key -> faction-wide stock the bank may not buy below (omitted when empty)
        public Dictionary<string, double> Relations { get; set; }
        public Dictionary<string, double> EarnedRelations { get; set; } // event-driven standing ledger (omitted when none)
        public Dictionary<string, double> Fatigue { get; set; }     // per-enemy war fatigue (omitted when none)
        public Dictionary<string, DateTime> TruceUntil { get; set; }// per-enemy non-aggression expiry (omitted when none)
        public Dictionary<string, DateTime> LastHostileActUtc { get; set; } // per-enemy last blow exchanged (omitted when none)
        public List<string> PactWith { get; set; }                 // tags of formally allied factions (omitted when none)
        public Dictionary<string, GrudgeDoc> Grudges { get; set; } // per-enemy grudge records (omitted when empty)
        public Dictionary<string, ProposalDoc> Proposals { get; set; } // outstanding diplomatic offers by recipient (omitted when none)
        public List<string> Embargoes { get; set; }                 // embargoed faction tags (omitted when empty)
        public List<string> Log { get; set; }
        public List<FactionEventDoc> Events { get; set; }          // recent per-faction history ring (omitted when empty)
        public List<SourceDoc> Sources { get; set; }
        public List<PendingFleetDoc> PendingFleets { get; set; }    // in-flight fleet events (replaces FleetEntityIds)
        public Dictionary<string, double> DemandPressure { get; set; } // ItemKind name → dynamic surplus threshold
        // Strategic focus state (ActionKind.Campaign / ActionKind.Muster) — brain-writes/manager-reads
        // signals that previously reset to default on every restart since they weren't mapped here.
        public string CampaignFocusRegion { get; set; }             // node ID being concentrated on (omitted when unset)
        public double? CampaignFocusPriority { get; set; }          // set only alongside CampaignFocusRegion
        public DateTime? CampaignSetAtUtc { get; set; }              // set only alongside CampaignFocusRegion
        public List<string> CampaignFocusRegionMembers { get; set; } // region node IDs incl. center (omitted when empty)
        public List<CampaignFocusDoc> CampaignFoci { get; set; }    // multi-focus replacement (omitted when empty)
        public double? MilitaryPosture { get; set; }                 // 0..1 deterrence build-up floor (omitted when 0)
        public string MusterFocusTag { get; set; }                   // rival being out-built (omitted when unset)
        public double? MusterFocusPriority { get; set; }             // set only alongside MusterFocusTag
        public DateTime? MusterSetAtUtc { get; set; }                 // set only alongside MusterFocusTag
        public DateTime? LastTerrorCampaignUtc { get; set; }          // terror campaign cadence clock (omitted when never)
        public string LastTerrorRegionKey { get; set; }               // the region the last terror campaign targeted (omitted when none)
        public int? TerrorEscalation { get; set; }                    // escalation steps, 0..cap (omitted when 0)
        public int? ControllerSpeaker { get; set; }                   // recorded voice for its stations (ControllerVoice); omitted when unassigned
        public DateTime? TerrorEscalationUtc { get; set; }            // when escalation last changed (omitted when never)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool TerrorRevenge { get; set; }                       // the next campaign returns to LastTerrorRegionKey (omitted when false)
        public long? TerrorMetDps { get; set; }                       // strongest opposition the last campaign met (omitted when 0)
        public long? TerrorStandingDps { get; set; }                  // combat DPS kept stocked between campaigns (omitted when 0)
        public List<FightDoc> Fights { get; set; }                    // recent fights, both sides (omitted when empty)
        public TacticMemoryDoc Tactics { get; set; }                  // learned tactics per opponent (omitted when empty)
    }

    /// <summary>Host policy config: which NPC factions discovery/sim should IGNORE (e.g. animal factions
    /// like spiders' SPID). Persisted as config.json — editable by hand or via 'factions ignore'.</summary>
    public sealed class FactionsConfig
    {
        public List<string> IgnoredTags { get; set; }
        public static FactionsConfig Default() { return new FactionsConfig { IgnoredTags = new List<string> { "SPID" } }; }
    }

    /// <summary>
    /// Resident-on-demand persistence: a tiny, always-loaded index + one heavy brain file per faction,
    /// loaded one at a time. The influence heatmap is NOT persisted (it's derived from the sources).
    /// Anti-bloat: the decision log is capped and only owned sources are stored, so a brain's size
    /// tracks live territory — not playtime.
    /// </summary>
    public sealed class FactionStore
    {
        public const int DefaultLogCap = 10;

        private static readonly JsonSerializerSettings S = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _baseDir;
        private readonly string _brainsDir;
        public FactionStore(string baseDir) { _baseDir = baseDir; _brainsDir = Path.Combine(baseDir, "brains"); }

        public string BaseDir    { get { return _baseDir; } }
        public string IndexPath  { get { return Path.Combine(_baseDir, "index.json"); } }
        public string BrainPath(string tag) { return Path.Combine(_brainsDir, tag + ".json"); }
        public string ConfigPath         { get { return Path.Combine(_baseDir, "config.json"); } }
        public string TuningPath         { get { return Path.Combine(_baseDir, "tuning.json"); } }
        public string TuningDefaultsPath { get { return Path.Combine(_baseDir, "tuning_defaults.json"); } }

        // TimeSpan values are serialised as total seconds so tuning.json is human-readable.
        private sealed class TimeSpanSecondsConverter : JsonConverter
        {
            public override bool CanConvert(System.Type t) { return t == typeof(TimeSpan); }
            public override void WriteJson(JsonWriter w, object v, JsonSerializer s)
                { w.WriteValue(((TimeSpan)v).TotalSeconds); }
            public override object ReadJson(JsonReader r, System.Type t, object existing, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Float || r.TokenType == JsonToken.Integer)
                    return TimeSpan.FromSeconds(System.Convert.ToDouble(r.Value));
                return existing ?? TimeSpan.Zero;
            }
        }

        private static readonly JsonSerializerSettings TuningSettings = new JsonSerializerSettings
        {
            Formatting           = Formatting.Indented,
            NullValueHandling    = NullValueHandling.Ignore,
            Converters           = new System.Collections.Generic.List<JsonConverter> { new TimeSpanSecondsConverter() },
        };

        /// <summary>Tuning versions at which the fleet-transit schedule contract changed such that
        /// still-pending fleets carry an arrival time the current contract would not have given them.
        /// A load that crosses any of these reschedules them. Append, never rewrite: an older server
        /// may still be crossing an earlier one.
        /// <para>15 = trapezoidal acceleration profile. 32 = legs billed for the line actually flown
        /// rather than the node-graph polyline. 51 = speed read from the ground a leg crosses rather
        /// than one global cruise, and a constant-acceleration ramp.</para></summary>
        private static readonly int[] FleetTransitContractTuneVersions = { 15, 32, 51 };

        /// <summary>Populate <paramref name="target"/> from tuning_defaults.json then tuning.json.
        /// tuning_defaults.json sets all baseline values; tuning.json overrides specific ones.
        /// Fields absent from both files remain at their C# zero-value. Returns whether the load crossed
        /// a <see cref="FleetTransitContractTuneVersions"/> watermark and pending fleets therefore need
        /// rescheduling.</summary>
        public bool LoadTuning(FactionTuning target)
        {
            Directory.CreateDirectory(_baseDir);
            FactionTuning defaults = null;
            if (File.Exists(TuningDefaultsPath))
            {
                try
                {
                    string defaultsJson = File.ReadAllText(TuningDefaultsPath);
                    defaults = JsonConvert.DeserializeObject<FactionTuning>(defaultsJson, TuningSettings) ?? new FactionTuning();
                    JsonConvert.PopulateObject(defaultsJson, target, TuningSettings);
                }
                catch { /* keep zeros on parse error */ }
            }
            int liveTuneVersion = target.TuneVersion;
            if (File.Exists(TuningPath))
            {
                try
                {
                    string tuningJson = File.ReadAllText(TuningPath);
                    FactionTuning existing = JsonConvert.DeserializeObject<FactionTuning>(tuningJson, TuningSettings);
                    if (existing != null)
                        liveTuneVersion = existing.TuneVersion;
                    JsonConvert.PopulateObject(tuningJson, target, TuningSettings);
                }
                catch { /* keep defaults on parse error */ }
            }
            bool transitContractMigrated = defaults != null
                && CrossesFleetTransitContract(liveTuneVersion, defaults.TuneVersion);
            ApplyVersionedContractDefaults(defaults, target, liveTuneVersion);
            RepairLoadedTuning(target);
            SaveTuning(target); // write merged result so tuning.json always reflects current defaults + overrides
            return transitContractMigrated;
        }

        /// <summary>
        /// The <see cref="FactionTuning.DataDirectory"/> a <see cref="LoadTuning"/> would produce,
        /// read without loading anything else: no migration, no repair, and nothing written back.
        ///
        /// <para><b>Why (FAC-241).</b> The data directory holds every input the instances sharing a
        /// world share, <c>world_recipe.json</c> included, but that file is read at plugin
        /// <c>Init</c> — SE bakes a saved planet's ore table while the world loads — and
        /// <see cref="LoadTuning"/> does not run until the session has loaded. This lets
        /// <c>Init</c> find the shared folder from the same two files, merged the same way, without
        /// running a load whose side effects belong to session start.</para>
        /// </summary>
        public string PeekDataDirectory()
        {
            var scratch = new FactionTuning();
            foreach (string path in new[] { TuningDefaultsPath, TuningPath })
            {
                if (!File.Exists(path)) continue;
                try { JsonConvert.PopulateObject(File.ReadAllText(path), scratch, TuningSettings); }
                catch { /* LoadTuning keeps the earlier layer on a parse error; so does this */ }
            }
            return scratch.DataDirectory ?? "";
        }

        private static bool CrossesFleetTransitContract(int liveTuneVersion, int defaultsTuneVersion)
        {
            foreach (int contractVersion in FleetTransitContractTuneVersions)
                if (liveTuneVersion < contractVersion && defaultsTuneVersion >= contractVersion)
                    return true;
            return false;
        }

        private static void ApplyVersionedContractDefaults(FactionTuning defaults, FactionTuning target, int liveTuneVersion)
        {
            if (defaults == null) return;
            if (defaults.TuneVersion <= liveTuneVersion) return;

            target.TuneVersion = defaults.TuneVersion;
            // Scheduler knobs must come through the version gate, not the field initialiser: LoadTuning
            // merges defaults first and then lets tuning.json override, and the running host rewrites
            // tuning.json every load -- so a value persisted by an earlier build wins over any new C#
            // default forever. Anything that has to change on an existing server belongs here.
            // The survey target, for exactly the reason above: 0.05 was persisted by every live
            // server, and it is coarser than a planet node's first sample, so the paced survey would
            // never run there however the C# default reads.
            target.SurveyTargetShare = defaults.SurveyTargetShare;
            // The pacing knobs for the same reason, and because SurveyProbesPerTick -- the rate
            // these replace -- is persisted by every live server and would otherwise keep
            // describing a duration the estate long ago outgrew.
            target.SurveyDurationHours = defaults.SurveyDurationHours;
            target.SurveyMaxProbesPerTick = defaults.SurveyMaxProbesPerTick;
            target.SurveyDeclinesBeforeParking = defaults.SurveyDeclinesBeforeParking;
            target.FrameSchedulerEnabled = defaults.FrameSchedulerEnabled;
            target.FrameSharedBudgetEnabled = defaults.FrameSharedBudgetEnabled;
            target.FrameMaxUnitsPerFrame = defaults.FrameMaxUnitsPerFrame;
            // Actor cadences, for the same reason: the frame admits ~one unit, so the intervals have
            // to describe work that actually fits. The previous values asked for ~4.9x the available
            // admissions, which meant no actor was keeping its declared cadence anyway.
            target.ProductionIntervalSeconds = defaults.ProductionIntervalSeconds;
            target.OpsIntervalSeconds = defaults.OpsIntervalSeconds;
            target.ShipyardIntervalSeconds = defaults.ShipyardIntervalSeconds;
            target.FleetIntervalSeconds = defaults.FleetIntervalSeconds;
            target.TradeIntervalSeconds = defaults.TradeIntervalSeconds;
            target.StoreStockIntervalSeconds = defaults.StoreStockIntervalSeconds;
            target.GlobalPassIntervalSeconds = defaults.GlobalPassIntervalSeconds;
            target.ArrivalsIntervalSeconds = defaults.ArrivalsIntervalSeconds;
            target.FactionTickIntervalSeconds = defaults.FactionTickIntervalSeconds;
            target.FlushEveryRealSeconds = defaults.FlushEveryRealSeconds;
            target.HoldMaintenanceThreshold = defaults.HoldMaintenanceThreshold;
            // Brain-action cooldowns, through the gate for the same reason as the cadences above: the
            // running host rewrites tuning.json every load, so a cooldown persisted by an earlier build
            // outlives any new shipped default. These pace how often a faction may take a structural
            // action; the action's own gates -- treasury, item stock, ResourceValue, hold -- decide
            // whether it can at all, and pacing the brain on top of those a second time only made the
            // world sit still.
            target.ExpandCooldown = defaults.ExpandCooldown;
            target.ClaimCooldown = defaults.ClaimCooldown;
            target.DevelopCooldown = defaults.DevelopCooldown;
            target.WithdrawCooldown = defaults.WithdrawCooldown;
            target.NegotiateCooldown = defaults.NegotiateCooldown;
            target.FoundCooldown = defaults.FoundCooldown;
            target.ActionCooldown = defaults.ActionCooldown;
            target.HoldDecayPerHour = defaults.HoldDecayPerHour;
            target.ReinforceCost = defaults.ReinforceCost;
            target.GuardCost = defaults.GuardCost;
            // The Guard strength gate's margin (TuneVersion 46): 1.2 was persisted by the first build that
            // carried the key and refused most fights; the duel model puts the break-even near 0.75.
            target.GuardCommitRatio = defaults.GuardCommitRatio;
            // Guard's treasury term (TuneVersion 47): every host persisted 0, an income-only budget.
            // A defence is now marshalled in several convoys, so a faction whose income is small
            // beside its treasury could field the hulls and not afford to send them.
            target.GuardWealthFractionPerWindow = defaults.GuardWealthFractionPerWindow;
            // The ship-activity cap (TuneVersion 48): 3.0 was persisted by the build that introduced
            // the key, and at one credit per fleet stop that is three stops -- which a world running
            // ~1,500 ships clears almost everywhere. Measured on the test server, 565 of 869 nodes
            // reached it within eight minutes of boot, so the traffic channel carried two thirds of
            // the estate instead of the trafficked tail it is for. The cap is the selectivity knob:
            // below it a node gains only its saturated share.
            target.ShipActivityCreditCap = defaults.ShipActivityCreditCap;
            // The ship-activity half-life (TuneVersion 49): 3600 was persisted by the build that
            // introduced the key, and at that span a node's credit settles at ~1.44x its stops per
            // hour -- measured on the test server, ~7 stops/hour for the mean node, so the settled
            // level sat AT a cap of 10 and 55% of the estate saturated. Raising the cap only delays
            // that; the half-life is what turns the measure from cumulative visits into a rate.
            target.ShipActivityHalfLifeSeconds = defaults.ShipActivityHalfLifeSeconds;
            // The ship-activity gain (TuneVersion 50): 0.30 was persisted by the builds before the
            // half-life moved. Break-even is HoldDecayPerHour / this as a share of the cap, so 0.30
            // put it at 0.8 -- a node had to sit at 8 of 10 to stand still, which once credit became
            // rate-limited left the mean node at -0.16 hold/hour and the estate shrinking. 1.20 puts
            // break-even at 0.2, i.e. ~5.6 stops/hour against a measured mean of ~7.
            target.ShipActivityHoldPerHour = defaults.ShipActivityHoldPerHour;
            target.ReferenceStockOre = defaults.ReferenceStockOre;
            target.ReferenceStockProcessedGood = defaults.ReferenceStockProcessedGood;
            target.ReferenceStockRareOre = defaults.ReferenceStockRareOre;
            target.ReferenceStockIngot = defaults.ReferenceStockIngot;
            target.ReferenceStockHydrogen = defaults.ReferenceStockHydrogen;
            target.ReferenceStockOxygen = defaults.ReferenceStockOxygen;
            // The kind-level answer for gas, which has no representative physical item to read a
            // price from -- see SeItemCatalogue. Per KILOLITRE, the unit SE quotes
            // MinimumHydrogenPrice in and the unit a store row sells.
            target.FallbackHydrogenValue = defaults.FallbackHydrogenValue;
            target.FallbackOxygenValue = defaults.FallbackOxygenValue;
            target.FleetAccelKmPerMin2 = defaults.FleetAccelKmPerMin2;
            target.FlushEveryRealSeconds = defaults.FlushEveryRealSeconds;
            target.CampaignFocusBaseCapacity = defaults.CampaignFocusBaseCapacity;
            target.CampaignFocusCombatPowerPerAdditional = defaults.CampaignFocusCombatPowerPerAdditional;
            target.CampaignFocusMaxConcurrent = defaults.CampaignFocusMaxConcurrent;
            target.MaxRefineryUpgrades = defaults.MaxRefineryUpgrades;
            target.OrbitGravityMin = defaults.OrbitGravityMin;
            target.ProductionProductivityBonus = defaults.ProductionProductivityBonus;
            target.ProductionSlotInputHorizonSeconds = defaults.ProductionSlotInputHorizonSeconds;
            target.ProductionCreditCapIntervals = defaults.ProductionCreditCapIntervals;
            // MineDrillFallbackLitersPerScorePoint was gated here while it was the drill-rate
            // coefficient. FAC-228 retired it: the rate now comes from MineDrillLitersPerScorePoint
            // and MineDutyCycle, both new keys no build has ever persisted, so they reach an existing
            // server from the shipped defaults without a gate.
            // Node rest time, through the gate because every live host persisted 10.0: a dwell earned
            // ten times its own length in lockout, which is what kept deposits idle between trips.
            // MineRateMultiplier, the other half of this balance change, is a new key no build has
            // ever written, so its inline initialiser reaches an existing server unaided.
            target.MineCooldownMultiplier = defaults.MineCooldownMultiplier;
            // The world density ceiling every feature's 0..1 Density scales against. It has to
            // come through the gate for the same reason as the scheduler knobs above: the host
            // rewrites tuning.json every load, so a value persisted by an earlier build outlives
            // any new default forever, and an authored world would silently keep the old density.
            target.FeatureMaxDensityPer1000Km3 = defaults.FeatureMaxDensityPer1000Km3;
            // The curve a deposit's worth is read on: a change to a value every live server has
            // already persisted, so it cannot arrive on its C# initialiser. See FAC-139.
            target.ResourceValueSaturationPerProbe = defaults.ResourceValueSaturationPerProbe;
            // The rarity bar moved with the price scale it is read on (FAC-191): every live server
            // persisted 4.0, which no ore-priced deposit can reach.
            target.RareDepositValueRatio = defaults.RareDepositValueRatio;
            // The trade-reputation rate. The eight player-reputation keys shipped as NEW keys and so
            // arrived on their initialisers, but they are persisted now -- every live tuning.json
            // carries 50000, which would outlive any new default forever.
            target.TradeReputationThreshold = defaults.TradeReputationThreshold;
            // The central-bank grant, for exactly the reason above and with a twist worth naming: the
            // C# initialiser has read 1,000,000 for some time while tuning_defaults.json shipped
            // 250,000, and the live tuning.json carried the 250,000 -- so the field that looks like
            // the default in source was never the number any server ran. Only this line changes that.
            target.CentralBankGrantAmount = defaults.CentralBankGrantAmount;
            // The free-mint table and its scales are a single global row each since FAC-102, so the
            // per-kind merge and the mineable-ore-scale adoption that used to reconcile eight rows
            // apiece are gone. A host carrying an older tuning.json has neither key; both fall to the
            // shipped defaults, which is the correct migration for a table that no longer has the
            // shape the persisted one was written in.
            if (target.NodeYieldTable == null) target.NodeYieldTable = defaults.NodeYieldTable;
            if (target.FreeMintScale == null) target.FreeMintScale = defaults.FreeMintScale;
            // ...and the Ingot channel is REMOVED from whatever the host already persisted, which the
            // two null checks above cannot do: they only fill an absent key, and every live
            // tuning.json carries both. A defaults edit alone would leave the row and the scale in
            // place on every running server. Surgical rather than `target.X = defaults.X`, so an
            // operator's own ore weights survive a migration that is only about one channel.
            RemoveIngotFreeMintChannel(target);
            // CapabilityAffinity is ADOPTED, not merely null-checked. The table changed shape when
            // node kinds were retired (f2122db): it used to be keyed by NodeKind with capability
            // sub-keys, and is now keyed by capability with condition sub-keys the live node is
            // tested against (Mineable, IceDeposit, HasIndustry, OnBody, Default). A null check
            // cannot migrate that -- an existing tuning.json HAS a table, so it kept the old one.
            //
            // Measured on awl-standalone-test 2026-09-01: the live file still carried six NodeKind
            // rows (Generic, OreField, IceField, Core, RareOreField, Empty) that no lookup can ever
            // reach, and its Refinery and FuelDepot rows had no readable sub-key at all -- so
            // NodeContent.CapabilityAffinityFor fell through to DefaultAffinity 0.5 for every node,
            // and "a fuel depot belongs on ice" was inert. That is InvestmentTargeting's ranking as
            // much as the estate seeder's.
            target.CapabilityAffinity = defaults.CapabilityAffinity;
            // An explicit `"BuildRolePriority": null` would otherwise leave every new build at rank 0.
            // A tuning.json that simply lacks the key keeps its C# initialiser and never reaches here.
            if (target.BuildRolePriority == null) target.BuildRolePriority = defaults.BuildRolePriority;
            // The urgency class weights are ADOPTED, not null-checked: the array is indexed by
            // DemandClass's own value and DemandClass.Fuel was inserted at 4, so every entry from
            // there up moved. A persisted six-entry array would give the fuel row Install's weight,
            // Install ShipBuild's, and ShipBuild nothing at all -- an existing server's ranking
            // silently re-mapped by a shape change it cannot see. An operator's own weights are
            // overwritten once, which is the only migration that can be right for a re-indexed table.
            target.UrgencyClassWeights = defaults.UrgencyClassWeights;
            // The fuel draw is armed with the import fallback off. Every live server persisted
            // measure mode and a 1.5 premium, so neither reaches an existing tuning.json otherwise.
            target.FuelDrawMode = defaults.FuelDrawMode;
            target.FuelImportPremium = defaults.FuelImportPremium;
            // Every live server persisted 4 when the key arrived.
            target.HaulSmallHullSearchNodes = defaults.HaulSmallHullSearchNodes;
        }

        /// <summary>Strips the Ingot free-mint channel from a persisted tuning: the yield-table rows
        /// on that channel and the <c>FreeMintScale</c> key that scales it. Gravel now comes from
        /// mining <c>Ore:Stone</c> and refining it, so nothing mints an ingot for free.
        ///
        /// <para><b>The scale has to go with the row, not merely be left dead.</b>
        /// <see cref="Economy.NodeItemIncome"/> walks <see cref="FactionTuning.AllItems"/> and asks
        /// <see cref="Economy.SourceItemYield"/> per kind, which consults only
        /// <see cref="NodeContent.FreeMintScaleFor"/> — it never looks at the yield table. A scale
        /// left behind would keep valuing every node for gravel income it no longer receives, in the
        /// number that feeds brain scoring and the insolvency guard. See FAC-233.</para></summary>
        private static void RemoveIngotFreeMintChannel(FactionTuning target)
        {
            if (target.FreeMintScale != null) target.FreeMintScale.Remove(ItemKind.Ingot.ToString());

            NodeYieldEntry[] table = target.NodeYieldTable;
            if (table == null) return;
            var kept = new List<NodeYieldEntry>(table.Length);
            foreach (NodeYieldEntry e in table)
            {
                if (e == null) continue;
                ItemKind kind;
                if (Enum.TryParse(e.ItemKind, out kind) && kind == ItemKind.Ingot) continue;
                kept.Add(e);
            }
            if (kept.Count != table.Length) target.NodeYieldTable = kept.ToArray();
        }

        /// <summary>Null for a null or barren deposit, so the key is omitted from the JSON entirely —
        /// most nodes are not deposits and the brain files are loaded one at a time.</summary>
        private static List<DepositEntryDoc> ToDepositDoc(NodeDeposit deposit)
        {
            if (deposit == null || deposit.IsBarren) return null;
            var docs = new List<DepositEntryDoc>(deposit.Entries.Count);
            for (int i = 0; i < deposit.Entries.Count; i++)
            {
                DepositEntry e = deposit.Entries[i];
                docs.Add(new DepositEntryDoc
                {
                    O = e.SubTypeId, S = e.Weight, R = e.Richness,
                    V = e.Volume > 0 ? (double?)e.Volume : null,
                });
            }
            return docs;
        }

        /// <summary>Null in, null out; a doc that reconstructs barren is null out too, since a barren
        /// deposit has no entries worth writing. What separates "sampled and barren" from "never
        /// sampled" is <see cref="SourceDoc.DepositPoints"/> beside it, which the caller applies.</summary>
        private static NodeDeposit FromDepositDoc(List<DepositEntryDoc> docs)
        {
            if (docs == null || docs.Count == 0) return null;
            var deposit = new NodeDeposit();
            for (int i = 0; i < docs.Count; i++)
            {
                DepositEntryDoc d = docs[i];
                if (d == null || string.IsNullOrEmpty(d.O)) continue;
                // S is weight, not share
                deposit.Entries.Add(new DepositEntry(d.O, d.S, d.R, d.V.HasValue ? d.V.Value : 0.0));
            }
            return deposit.IsBarren ? null : deposit;
        }

        private static void RepairLoadedTuning(FactionTuning target)
        {
            if (target.CampaignCooldown <= TimeSpan.Zero)
                target.CampaignCooldown = TimeSpan.FromHours(1);
        }

        /// <summary>Write current tuning values to tuning.json (creates or overwrites).</summary>
        public void SaveTuning(FactionTuning tuning)
        {
            Directory.CreateDirectory(_baseDir);
            File.WriteAllText(TuningPath, JsonConvert.SerializeObject(tuning, TuningSettings));
        }

        public void SaveIndex(IList<IndexEntry> entries)
        {
            Directory.CreateDirectory(_baseDir);
            File.WriteAllText(IndexPath, JsonConvert.SerializeObject(entries, S));
        }

        public List<IndexEntry> LoadIndex()
        {
            if (!File.Exists(IndexPath)) return new List<IndexEntry>();
            return JsonConvert.DeserializeObject<List<IndexEntry>>(ReadShared(IndexPath)) ?? new List<IndexEntry>();
        }

        /// <summary>Reads a state file without denying a concurrent writer. <c>File.ReadAllText</c>
        /// shares Read only, so while it holds the handle the plugin's next write of the same file
        /// throws. <c>Factions.Api</c> reads brains and the index through this store from another
        /// process on a 10 s poll, and its read lost the session load once (FAC-371). Delete is
        /// shared too, so <see cref="SaveBrain(BrainDoc, int)"/>'s <c>File.Replace</c> can swap the
        /// file under an open reader.</summary>
        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                return reader.ReadToEnd();
        }

        public string CentralBankStatePath { get { return Path.Combine(_baseDir, "central_bank_state.json"); } }

        /// <summary>Writes the world's money-supply anchor. World-level state that belongs to no
        /// faction, so it has no brain to live in; <c>central_bank.json</c> is a report the dashboard
        /// reads and is rewritten wholesale, so it is not the place for state either.</summary>
        public void SaveCentralBankState(WorldModel world)
        {
            if (world == null || world.CollateralAnchorUtc == DateTime.MinValue) return;
            Directory.CreateDirectory(_baseDir);
            var doc = new CentralBankStateDoc
            {
                CollateralAnchor = world.CollateralAnchor,
                CollateralAnchorUtc = world.CollateralAnchorUtc,
            };
            File.WriteAllText(CentralBankStatePath, JsonConvert.SerializeObject(doc, S));
        }

        /// <summary>Restores the anchor <see cref="SaveCentralBankState"/> wrote. A missing or
        /// unreadable file leaves the world unanchored, which the first income pass resolves by
        /// anchoring to the world as found.</summary>
        public void LoadCentralBankState(WorldModel world)
        {
            if (world == null || !File.Exists(CentralBankStatePath)) return;
            try
            {
                CentralBankStateDoc doc = JsonConvert.DeserializeObject<CentralBankStateDoc>(File.ReadAllText(CentralBankStatePath));
                if (doc != null) world.RestoreCollateralAnchor(doc.CollateralAnchor, doc.CollateralAnchorUtc);
            }
            catch { /* unanchored is a valid start */ }
        }

        public FactionsConfig LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return FactionsConfig.Default();
            FactionsConfig cfg = JsonConvert.DeserializeObject<FactionsConfig>(File.ReadAllText(ConfigPath)) ?? FactionsConfig.Default();
            if (cfg.IgnoredTags == null) cfg.IgnoredTags = new List<string>();
            return cfg;
        }

        public void SaveConfig(FactionsConfig cfg)
        {
            Directory.CreateDirectory(_baseDir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg ?? FactionsConfig.Default(), S));
        }

        public void SaveBrain(BrainDoc doc)
        {
            SaveBrain(doc, 0);
        }

        /// <summary>Delay between attempts of <see cref="SaveBrain(BrainDoc, int)"/>. Another
        /// process reading a brain holds it for milliseconds.</summary>
        public const int BrainWriteRetryDelayMs = 50;

        /// <summary>Writes a brain through a temp file and <c>File.Replace</c>, so a reader never
        /// sees a half-written file. A write the file system refuses (another process holding the
        /// file, FAC-371) is retried up to <paramref name="lockedRetries"/> more times,
        /// <see cref="BrainWriteRetryDelayMs"/> apart, then rethrown. The retry sleeps the calling
        /// thread: only a synchronous flush passes one, never the per-frame sweep.</summary>
        public void SaveBrain(BrainDoc doc, int lockedRetries)
        {
            Directory.CreateDirectory(_brainsDir);
            string path = BrainPath(doc.Tag);
            string json = JsonConvert.SerializeObject(doc, S);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    WriteReplacing(path, json);
                    return;
                }
                catch (Exception ex) when (attempt < lockedRetries && IsRefusedWrite(ex))
                {
                    System.Threading.Thread.Sleep(BrainWriteRetryDelayMs);
                }
            }
        }

        /// <summary>True for a write the file system refused rather than one that cannot succeed: a
        /// sharing violation, or <c>File.Replace</c> denied the replaced file, which Windows reports
        /// as an access error.</summary>
        public static bool IsRefusedWrite(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException)
                return false;
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        private static void WriteReplacing(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        public BrainDoc LoadBrain(string tag)
        {
            string p = BrainPath(tag);
            return File.Exists(p) ? JsonConvert.DeserializeObject<BrainDoc>(ReadShared(p)) : null;
        }

        public List<string> ListBrainTags()
        {
            var r = new List<string>();
            if (Directory.Exists(_brainsDir))
                foreach (string f in Directory.GetFiles(_brainsDir, "*.json"))
                    r.Add(Path.GetFileNameWithoutExtension(f));
            return r;
        }

        public void DeleteBrain(string tag)
        {
            string p = BrainPath(tag);
            if (File.Exists(p)) File.Delete(p);
        }

        // ---- transit tier run table serialization ------------------------------------------------

        /// <summary>Run lengths for the doc, or null when the leg is entirely open space — the tier
        /// table then says nothing the distance does not, and a null keeps it out of the JSON.</summary>
        public static double[] ZoneRunKmOf(LegZoneProfile zones)
        {
            if (!HasTiersWorthPersisting(zones)) return null;
            return zones.RunKm.ToArray();
        }

        /// <summary>Run tiers for the doc, parallel to <see cref="ZoneRunKmOf"/>.</summary>
        public static int[] ZoneRunTiersOf(LegZoneProfile zones)
        {
            if (!HasTiersWorthPersisting(zones)) return null;
            var tiers = new int[zones.RunTiers.Count];
            for (int i = 0; i < tiers.Length; i++) tiers[i] = (int)zones.RunTiers[i];
            return tiers;
        }

        private static bool HasTiersWorthPersisting(LegZoneProfile zones)
        {
            return zones != null && zones.HasGeometry && zones.RunKm.Count > 0 && !zones.IsPlainDeepSpace;
        }

        /// <summary>The run table back from the doc, or null when it is absent or malformed. A fleet
        /// that loads without one is scheduled on its distance, exactly as one saved before the
        /// tiers existed.</summary>
        public static LegZoneProfile ZonesFromDoc(double[] runKm, int[] runTiers)
        {
            if (runKm == null || runTiers == null || runKm.Length == 0 || runKm.Length != runTiers.Length)
                return null;

            var zones = new LegZoneProfile();
            zones.HasGeometry = true;
            for (int i = 0; i < runKm.Length; i++)
            {
                int tier = runTiers[i];
                if (tier < (int)TransitTier.Gravity || tier > (int)TransitTier.DeepSpace) return null;
                zones.Add((TransitTier)tier, runKm[i]);
            }
            return zones.TotalKm > 0 ? zones : null;
        }

        // ---- cargo / inventory serialization helpers -------------------------------------------

        /// <summary>Filter zero-value entries for JSON. Both fleet cargo and node inventory are now
        /// <c>Dictionary&lt;string, long&gt;</c> (SubTypeId-keyed), so no conversion is needed.</summary>
        public static Dictionary<string, long> SerializeCargo(Dictionary<string, long> cargo)
        {
            if (cargo == null || cargo.Count == 0) return null;
            var d = new Dictionary<string, long>();
            foreach (var kv in cargo)
                if (kv.Value > 0) d[kv.Key] = kv.Value;
            return d.Count > 0 ? d : null;
        }

        /// <summary>Squadron's DTO and domain shapes are both a plain prefab-ID list now (no role
        /// to carry once stock isn't role-bucketed) — this just normalizes empty to null so the
        /// "keep JSON lean" convention holds.</summary>
        public static List<string> NormalizeSquadron(List<string> squadron)
        {
            return squadron != null && squadron.Count > 0 ? squadron : null;
        }

        /// <summary>A loaded <see cref="PendingFleetAction.HullIntegrity"/>, or null when it is whole
        /// or does not have one entry per hull slot (<paramref name="slots"/>). Values clamp to 0..1;
        /// a non-number reads as whole.</summary>
        public static List<double> NormalizeHullIntegrity(List<double> integrity, int slots)
        {
            if (integrity == null || integrity.Count == 0 || integrity.Count != slots) return null;
            var clean = new List<double>(integrity.Count);
            bool damaged = false;
            for (int i = 0; i < integrity.Count; i++)
            {
                double v = integrity[i];
                v = double.IsNaN(v) ? 1.0 : (v < 0 ? 0 : (v >= HullDamage.WholeAt ? 1.0 : v));
                if (v < 1.0) damaged = true;
                clean.Add(v);
            }
            return damaged ? clean : null;
        }

        /// <summary>The DTOs for a node's <see cref="SpatialNode.DamagedHulls"/>, or null when it has none.</summary>
        public static List<DamagedHullDoc> DamagedHullDocs(List<DamagedHull> hulls)
        {
            if (hulls == null || hulls.Count == 0) return null;
            var docs = new List<DamagedHullDoc>(hulls.Count);
            foreach (DamagedHull h in hulls)
                docs.Add(new DamagedHullDoc
                {
                    PrefabId         = h.PrefabId,
                    Integrity        = h.Integrity,
                    QueuedUtc        = h.QueuedUtc,
                    PartsCompleteUtc = h.PartsCompleteUtc,
                    Bill             = new Dictionary<string, long>(h.Bill),
                    Collected        = h.Collected.Count > 0 ? new Dictionary<string, long>(h.Collected) : null,
                    RefitTo          = h.IsRefit ? h.RefitTo : null,
                    RefitShare       = h.IsRefit ? h.RefitShare : 0,
                });
            return docs;
        }

        /// <summary>Restores a node's damaged hulls. An entry with no prefab is dropped: it names
        /// nothing a repair could credit.</summary>
        public static void LoadDamagedHulls(SpatialNode node, List<DamagedHullDoc> docs)
        {
            if (docs == null) return;
            foreach (DamagedHullDoc d in docs)
            {
                if (d == null || string.IsNullOrEmpty(d.PrefabId)) continue;
                var h = new DamagedHull
                {
                    PrefabId         = d.PrefabId,
                    Integrity        = double.IsNaN(d.Integrity) ? 0 : Math.Max(0, Math.Min(1, d.Integrity)),
                    QueuedUtc        = d.QueuedUtc,
                    PartsCompleteUtc = d.PartsCompleteUtc,
                    RefitTo          = string.IsNullOrEmpty(d.RefitTo) ? null : d.RefitTo,
                    RefitShare       = double.IsNaN(d.RefitShare) ? 0 : Math.Max(0, Math.Min(1, d.RefitShare)),
                };
                if (d.Bill != null) foreach (var kv in d.Bill) if (kv.Value > 0) h.Bill[kv.Key] = kv.Value;
                if (d.Collected != null) foreach (var kv in d.Collected) if (kv.Value > 0) h.Collected[kv.Key] = kv.Value;
                node.DamagedHulls.Add(h);
            }
        }

        /// <summary>The escort entity IDs worth writing: the live ones. The lead's sentinel states
        /// have no escort analogue -- an escort that is gone is removed from the list rather than
        /// marked -- so anything not positive is a stale entry, and a list left with none of them
        /// becomes null so a fleet without escorts writes no field at all.</summary>
        public static List<long> NormalizeEscortIds(List<long> escortEntityIds)
        {
            if (escortEntityIds == null || escortEntityIds.Count == 0) return null;
            List<long> live = null;
            for (int i = 0; i < escortEntityIds.Count; i++)
            {
                if (escortEntityIds[i] <= 0) continue;
                if (live == null) live = new List<long>(escortEntityIds.Count);
                live.Add(escortEntityIds[i]);
            }
            return live;
        }

        /// <summary>The two retired store role strings. A brain written before FAC-060 slice 2
        /// records a shelf build's purpose in <c>BuildQueueEntry.Role</c> rather than in a flag, so
        /// the loader reads it back from there once. Kept as its own predicate rather than folded
        /// into <see cref="LegacyShipRoleKeys"/>, which answers a different question (does this doc
        /// predate the 2026-08-01 ship-pool unification) and must keep its own membership.</summary>
        internal static bool IsLegacyStoreRole(string role)
        {
            return string.Equals(role, "StoreSmall", StringComparison.Ordinal)
                || string.Equals(role, "StoreLarge", StringComparison.Ordinal);
        }

        /// <summary>Lowers every <see cref="BuildQueueEntry.Collected"/> key to at most what the entry's
        /// <see cref="BuildQueueEntry.Bom"/> asks for, removing keys the BOM does not name, and
        /// returns the units removed and how many keys lost any.
        ///
        /// <para>Absorption and ship-build production both clamp to the BOM, so the only way an entry
        /// holds more is a doc written while two entries at one node shared an id: the staged
        /// production commit resolves its per-entry overlay by id and writes one twin's
        /// <c>Collected</c> into both. The excess was duplicated rather than moved, so it is discarded
        /// rather than refunded. An entry left over its BOM can neither complete (its short keys stay
        /// short) nor stall out (a trickle of absorption resets the clock), so it holds a slot for
        /// good.</para>
        ///
        /// <para>Only <c>Collected</c> is repairable this way. The same commit shared the twins'
        /// <see cref="BuildQueueEntry.ReservedInputs"/>, which has no per-entry bound to clamp
        /// against.</para></summary>
        public static long ClampCollectedToBom(BuildQueueEntry entry, out int keys)
        {
            keys = 0;
            long discarded = 0;
            if (entry == null || entry.Collected.Count == 0) return 0;
            List<string> over = null;
            foreach (KeyValuePair<string, long> kv in entry.Collected)
            {
                long need;
                entry.Bom.TryGetValue(kv.Key, out need);
                if (kv.Value <= need) continue;
                if (over == null) over = new List<string>();
                over.Add(kv.Key);
            }
            if (over == null) return 0;
            foreach (string key in over)
            {
                long need;
                entry.Bom.TryGetValue(key, out need);
                discarded += entry.Collected[key] - need;
                keys++;
                if (need > 0) entry.Collected[key] = need;
                else entry.Collected.Remove(key);
            }
            return discarded;
        }

        private static readonly HashSet<string> LegacyShipRoleKeys =
            new HashSet<string>(StringComparer.Ordinal) { "Trade", "Raid", "Patrol", "Guard", "StoreSmall", "StoreLarge" };

        /// <summary>A doc predates the ship-pool unification (2026-08-01) if any node's persisted
        /// <see cref="SourceDoc.ShipStock"/> uses a role string as a key rather than a prefab
        /// SubtypeId — real prefab SubtypeIds never collide with the small, fixed legacy role
        /// vocabulary. Computed once per <see cref="BrainDoc"/> and used to gate every migration
        /// that only makes sense for a doc old enough to need it — see <c>ApplyBrainDoc</c>'s
        /// `Kind`/`Cooldowns` "Patrol"→"Guard" migrations, which corrupted real, current
        /// <see cref="ActionKind.Patrol"/> entries before this doc-age check existed.</summary>
        private static bool IsOldShapeDoc(BrainDoc d)
        {
            if (d.Sources == null) return false;
            foreach (SourceDoc sd in d.Sources)
            {
                if (sd.ShipStock == null) continue;
                foreach (string key in sd.ShipStock.Keys)
                    if (LegacyShipRoleKeys.Contains(key)) return true;
            }
            return false;
        }

        /// <summary>Credits one migrated ship of <paramref name="prefabId"/> into <paramref
        /// name="n"/>'s unified pool, reserving it via <see cref="SpatialNode.ListedForSale"/> when
        /// it came from a Store role bucket.</summary>
        private static void MigrateOneShip(SpatialNode n, string prefabId, bool isStoreRole)
        {
            int stock;
            n.ShipStock.TryGetValue(prefabId, out stock);
            n.ShipStock[prefabId] = stock + 1;
            if (isStoreRole)
            {
                int reserved;
                n.ListedForSale.TryGetValue(prefabId, out reserved);
                n.ListedForSale[prefabId] = reserved + 1;
            }
        }

        /// <summary>A persisted munitions map as the fleet carries it: never null for an armed fleet,
        /// because <see cref="FleetMunitions"/> writes into it.</summary>
        public static Dictionary<string, long> DeserializeMunitions(Dictionary<string, long> munitions)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            if (munitions == null) return d;
            foreach (var kv in munitions)
                if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key)) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>
        /// Deserialize SubTypeId-keyed cargo/inventory from JSON. Handles migration from old saves
        /// where keys were ItemKind names ("Ore", "ProcessedGood", "Hydrogen", "Oxygen", "Fuel").
        /// Old ItemKind keys are converted to a representative SubTypeId so old brain files load
        /// correctly without a manual migration step.
        /// </summary>
        public static Dictionary<string, long> DeserializeCargo(Dictionary<string, long> cargo)
        {
            if (cargo == null || cargo.Count == 0) return null;
            var d = new Dictionary<string, long>();
            foreach (var kv in cargo)
            {
                if (kv.Value <= 0) continue;
                string key = MigrateItemKindKey(kv.Key);
                long cur;
                d.TryGetValue(key, out cur);
                d[key] = cur + kv.Value;
            }
            return d.Count > 0 ? d : null;
        }

        // Map old inventory keys to their current canonical form (migration only).
        private static string MigrateItemKindKey(string key)
        {
            switch (key)
            {
                // Old ItemKind-bucket keys (pre-SubTypeId era).
                case "Ore":           return "Ore:Iron";  // was bare "Iron" before 0.39.4.0
                case "ProcessedGood": return "Comp:SteelPlate";
                case "Hydrogen":
                case "Fuel":          return "HydrogenBottle";
                case "Oxygen":        return "OxygenBottle";
                // Bare SE component names written before the Comp: prefix convention (pre-0.39.3.0).
                case "SteelPlate":    return "Comp:SteelPlate";
                case "Construction":  return "Comp:Construction";
                case "Computer":      return "Comp:Computer";
                // Bare SE ore names written before the Ore: prefix convention (pre-0.39.4.0).
                case "Iron":          return "Ore:Iron";
                case "Nickel":        return "Ore:Nickel";
                case "Cobalt":        return "Ore:Cobalt";
                case "Silicon":       return "Ore:Silicon";
                case "Magnesium":     return "Ore:Magnesium";
                case "Ice":           return "Ore:Ice";
                case "Silver":        return "Ore:Silver";
                case "Gold":          return "Ore:Gold";
                case "Platinum":      return "Ore:Platinum";
                case "Stone":         return "Ore:Stone";
                case "Uranium":       return "Ore:Uranium";
                default:              return key; // already correctly keyed
            }
        }

        // ---- mapping: runtime state <-> docs ----
        public static IndexEntry ToIndexEntry(FactionState s, DateTime nextThinkUtc)
        {
            return new IndexEntry { Tag = s.Tag, PersonalityId = s.PersonalityId, RngSeed = s.RngSeed, NextThinkUtc = nextThinkUtc };
        }

        /// <summary>Ordinal order, so an unchanged set writes an unchanged file.</summary>
        private static List<string> SortedKeys(HashSet<string> keys)
        {
            var list = new List<string>(keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>A fleet's queued stops as written to the brain file; null for none, so an
        /// itinerary-less fleet writes no key.</summary>
        private static List<FleetStopDoc> ToStopDocs(List<FleetStop> stops)
        {
            if (stops == null || stops.Count == 0) return null;
            var docs = new List<FleetStopDoc>(stops.Count);
            foreach (FleetStop stop in stops)
                docs.Add(new FleetStopDoc
                {
                    NodeId = stop.NodeId,
                    Kind = stop.Kind.ToString(),
                    HasPosition = stop.HasPosition,
                    X = stop.PositionKm.X, Y = stop.PositionKm.Y, Z = stop.PositionKm.Z,
                    PickupItemKey = stop.PickupItemKey,
                    PickupQuantity = stop.PickupQuantity,
                    DurationSeconds = stop.DurationSeconds,
                    MineWeights = stop.MineWeights,
                    MineTargetLiters = stop.MineTargetLiters
                });
            return docs;
        }

        /// <summary>The inverse of <see cref="ToStopDocs"/>. A stop that names nowhere to go is
        /// dropped, and a list left empty reads back as null.</summary>
        private static List<FleetStop> FromStopDocs(List<FleetStopDoc> docs)
        {
            if (docs == null || docs.Count == 0) return null;
            var stops = new List<FleetStop>(docs.Count);
            foreach (FleetStopDoc stopDoc in docs)
            {
                if (stopDoc == null) continue;
                FleetStopKind stopKind = FleetStopKind.Service;
                if (!string.IsNullOrEmpty(stopDoc.Kind))
                    Enum.TryParse<FleetStopKind>(stopDoc.Kind, out stopKind);
                if (stopKind != FleetStopKind.Navigation && string.IsNullOrEmpty(stopDoc.NodeId)) continue;
                if (stopKind == FleetStopKind.Navigation && !stopDoc.HasPosition
                    && string.IsNullOrEmpty(stopDoc.NodeId)) continue;
                stops.Add(new FleetStop
                {
                    NodeId = stopDoc.NodeId,
                    Kind = stopKind,
                    HasPosition = stopDoc.HasPosition,
                    PositionKm = new Vec3(stopDoc.X, stopDoc.Y, stopDoc.Z),
                    PickupItemKey = stopDoc.PickupItemKey,
                    PickupQuantity = stopDoc.PickupQuantity,
                    DurationSeconds = stopDoc.DurationSeconds,
                    MineWeights = stopDoc.MineWeights,
                    MineTargetLiters = stopDoc.MineTargetLiters
                });
            }
            return stops.Count == 0 ? null : stops;
        }

        public static BrainDoc ToBrainDoc(FactionState s, IEnumerable<SpatialNode> ownedSources, int logCap)
        {
            var drives = new double[DriveVector.Count];
            for (int i = 0; i < DriveVector.Count; i++) drives[i] = s.Drives[(Drive)i];

            var cooldowns = new Dictionary<string, DateTime>();
            foreach (var kv in s.Cooldowns) cooldowns[kv.Key.ToString()] = kv.Value;

            Dictionary<string, double> actionRepetition = null;
            if (s.ActionRepetition.Count > 0)
            {
                actionRepetition = new Dictionary<string, double>();
                foreach (var kv in s.ActionRepetition)
                    if (kv.Value > 0.001) actionRepetition[kv.Key.ToString()] = kv.Value;
                if (actionRepetition.Count == 0) actionRepetition = null;
            }

            Dictionary<string, DateTime> tradeDestCooldowns = s.TradeDestCooldowns.Count > 0
                ? new Dictionary<string, DateTime>(s.TradeDestCooldowns) : null;

            Dictionary<string, DateTime> patrolNodeCooldowns = s.PatrolNodeCooldowns.Count > 0
                ? new Dictionary<string, DateTime>(s.PatrolNodeCooldowns) : null;

            Dictionary<string, DateTime> reinforceNodeCooldowns = s.ReinforceNodeCooldowns.Count > 0
                ? new Dictionary<string, DateTime>(s.ReinforceNodeCooldowns) : null;

            Dictionary<string, double> reinforcePresenceSeconds = s.ReinforcePresenceSeconds.Count > 0
                ? new Dictionary<string, double>(s.ReinforcePresenceSeconds) : null;


            // Written straight out. Compaction of lapsed entries is the caller's job (see
            // FactionStore.Flush) so that serialising a brain never mutates the state it serialises.
            Dictionary<string, KnowledgeEntryDoc> knowledge = null;
            if (s.Knowledge.Count > 0)
            {
                knowledge = new Dictionary<string, KnowledgeEntryDoc>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, KnownNode> kv in s.Knowledge.Snapshots)
                    knowledge[kv.Key] = KnowledgeEntryDoc.From(kv.Value);
            }

            List<string> reservationOptIn = null;
            if (s.ReservationOptIn.Count > 0)
            {
                reservationOptIn = new List<string>();
                foreach (NodeCapabilityKind cap in s.ReservationOptIn) reservationOptIn.Add(cap.ToString());
            }

            Dictionary<string, int> installPriority = null;
            if (s.InstallPriority.Count > 0)
            {
                installPriority = new Dictionary<string, int>();
                foreach (var kv in s.InstallPriority) if (kv.Value != 0) installPriority[kv.Key.ToString()] = kv.Value;
                if (installPriority.Count == 0) installPriority = null;
            }

            // Only a player faction ever fills this; an NPC's ranking is the brain's and is derived.
            Dictionary<string, int> reinforceOptIn = null;
            if (s.ReinforceOptIn.Count > 0)
            {
                reinforceOptIn = new Dictionary<string, int>(s.ReinforceOptIn, StringComparer.OrdinalIgnoreCase);
                if (reinforceOptIn.Count == 0) reinforceOptIn = null;
            }

            // Written in rank order, not list order, so a brain file read by a human shows the same
            // sequence the managers evaluate. LastScanLogSignature is deliberately absent: it is a
            // log-dedup cursor, and persisting it would silence the first scan after every restart.
            // Omitted entirely when the faction has never observed a job, so a brain file does not
            // grow by a dozen zeroes per role for a faction that has done nothing.
            Dictionary<string, RoleRequirementDoc> fleetRequirements = null;
            foreach (var fr in s.FleetRequirements)
            {
                if (fr.Value == null || fr.Value.Samples <= 0) continue;
                if (fleetRequirements == null) fleetRequirements = new Dictionary<string, RoleRequirementDoc>(StringComparer.Ordinal);
                fleetRequirements[fr.Key] = new RoleRequirementDoc
                {
                    Buckets  = (double[])fr.Value.Buckets.Clone(),
                    UnmetEma = fr.Value.UnmetEma,
                    Samples  = fr.Value.Samples,
                    Basis    = RoleRequirement.CurrentBasis,
                };
            }

            List<StandingOrderDoc> standingOrders = null;
            if (s.StandingOrders.Count > 0)
            {
                standingOrders = new List<StandingOrderDoc>(s.StandingOrders.Count);
                foreach (StandingOrder o in s.StandingOrdersByRank())
                    standingOrders.Add(new StandingOrderDoc
                    {
                        Id = o.Id,
                        Kind = o.Kind.ToString(),
                        Priority = o.Priority,
                        Paused = o.Paused,
                        CreatedUtc = o.CreatedUtc,
                        HubNodeId = o.HubNodeId,
                        PeriodSeconds = o.PeriodSeconds,
                        OreWeights = o.OreWeights != null && o.OreWeights.Count > 0
                            ? new Dictionary<string, double>(o.OreWeights) : null,
                        NextScanUtc = o.NextScanUtc,
                        NodeId = o.NodeId,
                        AllStations = o.AllStations,
                        ItemKey = o.ItemKey,
                        TargetQuantity = o.TargetQuantity,
                    });
            }

            // Omitted entirely when the faction has no doctrine, which is the common case for every
            // NPC and for a player faction that has not written one. The per-faction brain files are
            // loaded one at a time and bloat compounds.
            List<DoctrineLineDoc> doctrine = null;
            if (s.FleetDoctrine.Count > 0)
            {
                doctrine = new List<DoctrineLineDoc>(s.FleetDoctrine.Count);
                foreach (DoctrineLine line in s.DoctrineByRank())
                    doctrine.Add(new DoctrineLineDoc
                    {
                        Id = line.Id,
                        PrefabId = line.PrefabId,
                        TargetCount = line.TargetCount,
                        MaxConcurrent = line.MaxConcurrent,
                        ScopeNodeId = line.ScopeNodeId,
                        Priority = line.Priority,
                        Paused = line.Paused,
                        CreatedUtc = line.CreatedUtc,
                    });
            }

            // Same omission rule as the doctrine above, and the common case is the same: every NPC
            // and every player faction that has not written one.
            List<ShelfLineDoc> shelf = null;
            if (s.Shelf.Count > 0)
            {
                shelf = new List<ShelfLineDoc>(s.Shelf.Count);
                foreach (ShelfLine line in s.Shelf)
                    shelf.Add(new ShelfLineDoc
                    {
                        Id = line.Id,
                        PrefabId = line.PrefabId,
                        TargetCount = line.TargetCount,
                        Paused = line.Paused,
                        CreatedUtc = line.CreatedUtc,
                    });
            }

            List<FactionEventDoc> events = null;
            if (s.RecentEvents.Count > 0)
            {
                events = new List<FactionEventDoc>(s.RecentEvents.Count);
                foreach (FactionEvent e in s.RecentEvents)
                    events.Add(new FactionEventDoc
                    {
                        Utc = e.Utc,
                        Kind = e.Kind.ToString(),
                        ActorTag = e.ActorTag,
                        OtherTag = e.OtherTag,
                        NodeId = e.NodeId,
                        Amount = e.Amount != 0 ? (long?)e.Amount : null,
                        Ratio = e.Ratio != 0 ? (double?)Math.Round(e.Ratio, 3) : null,
                    });
            }

            var log = new List<string>(s.Log);
            if (logCap > 0 && log.Count > logCap) log.RemoveRange(0, log.Count - logCap);

            var sources = new List<SourceDoc>();
            foreach (SpatialNode n in ownedSources)
            {
                Dictionary<string, int> caps = null;
                if (n.Capabilities.Count > 0)
                {
                    caps = new Dictionary<string, int>();
                    foreach (var kv in n.Capabilities)
                        if (kv.Value > 0) caps[kv.Key.ToString()] = kv.Value;
                    if (caps.Count == 0) caps = null;
                }
                Dictionary<string, int> shipStock = null;
                if (n.ShipStock.Count > 0)
                {
                    var ss = new Dictionary<string, int>();
                    foreach (var kv in n.ShipStock) if (kv.Value > 0) ss[kv.Key] = kv.Value;
                    if (ss.Count > 0) shipStock = ss;
                }
                Dictionary<string, int> listedForSale = null;
                if (n.ListedForSale.Count > 0)
                {
                    var lfs = new Dictionary<string, int>();
                    foreach (var kv in n.ListedForSale) if (kv.Value > 0) lfs[kv.Key] = kv.Value;
                    if (lfs.Count > 0) listedForSale = lfs;
                }

                Dictionary<string, double> demandHistory = null;
                if (n.DemandHistory.Count > 0)
                {
                    var dh = new Dictionary<string, double>();
                    foreach (var kv in n.DemandHistory) if (kv.Value > 0) dh[kv.Key] = kv.Value;
                    if (dh.Count > 0) demandHistory = dh;
                }

                Dictionary<string, double> salesHistory = null;
                if (n.SalesHistory.Count > 0)
                {
                    var sh = new Dictionary<string, double>();
                    foreach (var kv in n.SalesHistory) if (kv.Value > 0) sh[kv.Key] = kv.Value;
                    if (sh.Count > 0) salesHistory = sh;
                }

                Dictionary<string, double> fuelBurnEma = null;
                if (n.FuelBurnEma.Count > 0)
                {
                    var fb = new Dictionary<string, double>();
                    foreach (var kv in n.FuelBurnEma) if (kv.Value > 0) fb[kv.Key] = kv.Value;
                    if (fb.Count > 0) fuelBurnEma = fb;
                }

                Dictionary<string, double> minedEma = null;
                if (n.MinedEma.Count > 0)
                {
                    var me = new Dictionary<string, double>();
                    foreach (var kv in n.MinedEma) if (kv.Value > 0) me[kv.Key] = kv.Value;
                    if (me.Count > 0) minedEma = me;
                }

                List<BuildQueueEntryDoc> buildQueue = null;
                if (n.BuildQueue.Count > 0)
                {
                    buildQueue = new List<BuildQueueEntryDoc>(n.BuildQueue.Count);
                    foreach (BuildQueueEntry bqe in n.BuildQueue)
                    {
                        List<BuildRecipeProgressDoc> recipeProgress = null;
                        if (bqe.RecipeProgress.Count > 0)
                        {
                            recipeProgress = new List<BuildRecipeProgressDoc>(bqe.RecipeProgress.Count);
                            foreach (var kv in bqe.RecipeProgress)
                                if (kv.Value.Accum > 0)
                                    recipeProgress.Add(new BuildRecipeProgressDoc
                                    {
                                        BlueprintId = kv.Key,
                                        Accum       = kv.Value.Accum,
                                        LastTickUtc = kv.Value.LastTick,
                                    });
                            if (recipeProgress.Count == 0) recipeProgress = null;
                        }
                        buildQueue.Add(new BuildQueueEntryDoc
                        {
                            Id             = bqe.Id,
                            Role           = bqe.Role,
                            PrefabId       = bqe.PrefabId,
                            Priority       = bqe.Priority,
                            Bom            = SerializeCargo(bqe.Bom),
                            Collected      = SerializeCargo(bqe.Collected),
                            ReservedInputs = SerializeCargo(bqe.ReservedInputs),
                            RecipeProgress = recipeProgress,
                            CreatedUtc     = bqe.CreatedUtc,
                            ExpiresUtc     = bqe.ExpiresUtc,
                            StalledSeconds = bqe.StalledSeconds,
                            ServicedSeconds = bqe.ServicedSeconds,
                            DestinedForSale = bqe.DestinedForSale,
                            PriorityByPlayer = bqe.PriorityByPlayer,
                        });
                    }
                }

                sources.Add(new SourceDoc
                {
                    Id = n.Id, X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z,
                    Strength = n.HoldStrength, ResourceValue = n.ResourceValue,
                    Richness = n.Richness,
                    Threat = n.Threat,
                    StableStationId = n.StableStationId,
                    AsteroidFieldSeed = n.AsteroidFieldSeed,
                    StationPrefabId = n.StationPrefabId,
                    PendingStationPrefabId = n.PendingStationPrefabId,
                    StationBookValue = n.StationBookValue,
                    StationWeapons = n.StationWeapons,
                    DisplayName = n.DisplayName,
                    DominantOre = NodeDisplay.DominantOre(n),
                    Deposit = ToDepositDoc(n.Deposit),
                    DepositPoints = n.Deposit != null && n.Deposit.PointsExamined > 0
                        ? (long?)n.Deposit.PointsExamined : null,
                    // Only beside something it describes: a deposit that writes neither entries nor
                    // points is not reconstructed on load, so its units would be noise in the file.
                    DepositUnits = n.Deposit != null && (!n.Deposit.IsBarren || n.Deposit.PointsExamined > 0)
                        ? (int?)n.Deposit.Units : null,
                    Neighbors = n.Neighbors.Count == 0
                        ? null
                        : n.Neighbors.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList(),
                    LastReinforcedUtc = n.LastReinforcedUtc,
                    SafeZoneDownUntilUtc = n.SafeZoneDownUntilUtc,
                    SafeZoneDownByTag = n.SafeZoneDownByTag,
                    ContestedUntilUtc = n.ContestedUntilUtc,
                    ContestedByTag = n.ContestedByTag,
                    LastPlayerAggressionUtc = n.LastPlayerAggressionUtc,
                    // -1 is "never besieged", which is almost every node; writing nothing for those
                    // is the whole point of the nested doc, and SiegeDoc.From owns that test.
                    Siege = SiegeDoc.From(n),
                    ShipActivityCredits = n.ShipActivityCredits,
                    ShipActivityAtUtc = n.ShipActivityAtUtc,
                    AbandonedUtc = n.AbandonedUtc,
                    LastHaulDepartedUtc = n.LastHaulDepartedUtc,
                    LastHaulArrivedUtc = n.LastHaulArrivedUtc,
                    LastMinedUtc = n.LastMinedUtc,
                    MineCooldownUntilUtc = n.MineCooldownUntilUtc,
                    DemandHistory = demandHistory,
                    SalesHistory = salesHistory,
                    FuelBurnEma = fuelBurnEma,
                    // The timestamp is only meaningful beside a map, so it is written only with one:
                    // a lone stamp on every node would be bloat on a field nothing could read.
                    FuelBurnEmaAtUtc = fuelBurnEma != null ? n.FuelBurnEmaAtUtc : default(DateTime),
                    MinedEma = minedEma,
                    MinedEmaAtUtc = minedEma != null ? n.MinedEmaAtUtc : default(DateTime),
                    Inventory = SerializeCargo(n.Inventory),  // n.Inventory is now Dictionary<string,long>
                    ReservedInventory = SerializeCargo(n.ReservedInventory),
                    Workers = n.Workers > 0 ? n.Workers : 0,
                    WorkersAtUtc = n.Workers > 0 ? n.WorkersAtUtc : default(DateTime),
                    WorkerFoodBank = n.Workers > 0 ? n.WorkerFoodBank : 0,
                    StationMunitions = SerializeCargo(n.StationMunitions),
                    StationLoot = SerializeCargo(n.StationLoot),
                    LootRestockUtc = n.LootRestockUtc,
                    Capabilities = caps,
                    ShipStock = shipStock,
                    ListedForSale = listedForSale,
                    BuildQueue = buildQueue,
                    DamagedHulls = DamagedHullDocs(n.DamagedHulls),
                    ShipyardPaused = n.ShipyardPaused,
                    BuildSeq = n.BuildSeq,
                });
            }

            // Pending fleets: omit intercepted fleets (cleaned up before flush, but be defensive).
            List<PendingFleetDoc> pendingFleets = null;
            foreach (PendingFleetAction pf in s.PendingFleets)
            {
                if (pf.Intercepted) continue;  // shouldn't happen after cleanup, but be safe
                // A bench scenario is test tooling for the running session only: never written,
                // so a restart ends it and nothing of it reaches the brain file.
                if (pf.IsBench) continue;
                if (pendingFleets == null) pendingFleets = new List<PendingFleetDoc>();
                List<FleetStopDoc> remainingStops = ToStopDocs(pf.RemainingStops);
                pendingFleets.Add(new PendingFleetDoc
                {
                    Kind = pf.Kind.ToString(),
                    ActorTag = pf.ActorTag,
                    TargetTag = pf.TargetTag,
                    OriginNodeId = pf.OriginNodeId,
                    ReturnNodeId = pf.ReturnNodeId,
                    InterceptedByFleetId = pf.InterceptedByFleetId,
                    TargetNodeId = pf.TargetNodeId,
                    OriginX = pf.OriginPosKm.X, OriginY = pf.OriginPosKm.Y, OriginZ = pf.OriginPosKm.Z,
                    TargetX = pf.TargetPosKm.X, TargetY = pf.TargetPosKm.Y, TargetZ = pf.TargetPosKm.Z,
                    DepartedUtc = pf.DepartedUtc,
                    EstimatedArrivalUtc = pf.EstimatedArrivalUtc,
                    CargoValue = pf.CargoValue,
                    Cargo = SerializeCargo(pf.Cargo),  // pf.Cargo is now Dictionary<string,long>
                    Munitions = pf.MunitionsArmed ? SerializeCargo(pf.Munitions) : null,
                    MunitionsArmed = pf.MunitionsArmed,
                    RetreatReason = pf.RetreatReason,

                    TradeMagnitude = pf.TradeMagnitude,
                    ConvoySize = pf.ConvoySize,
                    Resolved = pf.Resolved,
                    AgainstIntruder = pf.AgainstIntruder,
                    CurrentStopKind = pf.CurrentStopKind.ToString(),
                    RemainingStops = remainingStops,
                    SuspendedStops = ToStopDocs(pf.SuspendedStops),
                    PickupItemKey = pf.PickupItemKey,
                    PickupQuantity = pf.PickupQuantity,
                    MineWeights = pf.MineWeights,
                    MineTargetLiters = pf.MineTargetLiters,
                    MineWanderUp = pf.MineWanderUp.HasValue
                        ? new[] { pf.MineWanderUp.Value.X, pf.MineWanderUp.Value.Y, pf.MineWanderUp.Value.Z } : null,
                    RouteDistanceKm = pf.RouteDistanceKm,
                    RouteCoveredKm = pf.RouteCoveredKm,
                    RouteDepartedUtc = pf.RouteDepartedUtc,
                    ZoneRunKm = ZoneRunKmOf(pf.LegZones),
                    ZoneRunTiers = ZoneRunTiersOf(pf.LegZones),
                    RetargetCount = pf.RetargetCount,
                    LastRetargetUtc = pf.LastRetargetUtc,
                    PathNodeIds = pf.PathNodeIds,
                    // Sanitise transient M states: SpawningId (−2) and −1 become 0 so a reboot
                    // never re-enters those states and triggers unguarded SpawnPrefab calls.
                    MaterialisedEntityId = (pf.MaterialisedEntityId <= 0 && pf.MaterialisedEntityId != 0) ? 0 : pf.MaterialisedEntityId,
                    FleetId    = pf.FleetId,
                    ForcedRatio         = pf.ForcedRatio.HasValue ? (double?)pf.ForcedRatio.Value : null,
                    ForcedRatioCause    = pf.ForcedRatioCause,
                    FerryRole           = pf.FerryRole,
                    RelocationRole      = pf.RelocationRole,
                    IsProcurement       = pf.IsProcurement,
                    ShipPrefabId        = pf.ShipPrefabId,
                    CargoCapacityLiters = pf.CargoCapacityLiters,
                    Squadron            = NormalizeSquadron(pf.Squadron),
                    EscortEntityIds     = NormalizeEscortIds(pf.EscortEntityIds),
                    SurvivingHulls      = pf.SurvivingHulls,
                    HullIntegrity       = pf.HullIntegrity != null && pf.HullIntegrity.Count > 0
                                          ? new List<double>(pf.HullIntegrity) : null,
                    Fight               = FightProgressDoc.From(pf.Fight),
                    HullsMeasured       = pf.HullsMeasured,
                    HullsSettled        = pf.HullsSettled
                });
            }

            Dictionary<string, double> demandPressure = null;
            if (s.DemandPressure.Count > 0)
            {
                demandPressure = new Dictionary<string, double>();
                foreach (var kv in s.DemandPressure)
                    if (kv.Value > 0) demandPressure[kv.Key.ToString()] = kv.Value;
                if (demandPressure.Count == 0) demandPressure = null;
            }

            Dictionary<string, GrudgeDoc> grudges = null;
            if (s.Grudges.Count > 0)
            {
                grudges = new Dictionary<string, GrudgeDoc>();
                foreach (var kv in s.Grudges)
                    grudges[kv.Key] = new GrudgeDoc { Kind = kv.Value.Kind.ToString(), ExpiresUtc = kv.Value.ExpiresUtc };
            }

            Dictionary<string, ProposalDoc> proposals = null;
            foreach (var kv in s.Proposals)
            {
                if (kv.Value == null || kv.Value.Terms == null || kv.Value.Terms.Length == 0) continue;
                var verbs = new List<string>();
                var pars = new List<string>();
                bool anyParam = false;
                foreach (DiplomacyTerm term in kv.Value.Terms)
                {
                    verbs.Add(term.Verb.ToString());
                    pars.Add(term.Param);
                    if (!string.IsNullOrEmpty(term.Param)) anyParam = true;
                }
                if (proposals == null) proposals = new Dictionary<string, ProposalDoc>();
                proposals[kv.Key] = new ProposalDoc { Verbs = verbs, Params = anyParam ? pars : null, IssuedUtc = kv.Value.IssuedUtc };
            }

            List<CampaignFocusDoc> campaignFoci = null;
            if (s.CampaignFoci.Count > 0)
            {
                campaignFoci = new List<CampaignFocusDoc>();
                foreach (CampaignFocus focus in s.CampaignFoci)
                {
                    if (focus == null || string.IsNullOrEmpty(focus.CenterNodeId)) continue;
                    List<CampaignObjectiveDoc> objectives = null;
                    if (focus.Objectives.Count > 0)
                    {
                        objectives = new List<CampaignObjectiveDoc>();
                        foreach (CampaignObjective objective in focus.Objectives)
                        {
                            if (objective == null) continue;
                            bool namesRegion = objective.TargetRegionKind.HasValue;
                            if (string.IsNullOrEmpty(objective.TargetNodeId) && !namesRegion) continue;
                            objectives.Add(new CampaignObjectiveDoc
                            {
                                Kind = objective.Kind.ToString(),
                                TargetNodeId = objective.TargetNodeId,
                                TargetRegionKind = namesRegion ? objective.TargetRegionKind.Value.ToString() : null,
                                TargetRegionKey = namesRegion ? objective.TargetRegionKey : null,
                                TargetPlanetId = namesRegion ? objective.TargetPlanetId : null,
                                WantedCombatHulls = objective.WantedCombatHulls > 0 ? (int?)objective.WantedCombatHulls : null,
                                WantedCombatDps = objective.WantedCombatDps > 0 ? (long?)objective.WantedCombatDps : null,
                                Cause = objective.Cause != CampaignCause.Predation ? objective.Cause.ToString() : null,
                                LastDispatchUtc = objective.LastDispatchUtc != DateTime.MinValue
                                    ? (DateTime?)objective.LastDispatchUtc : null,
                                LastOutcome = objective.LastOutcome != CampaignOutcome.None
                                    ? objective.LastOutcome.ToString() : null,
                                LastOutcomeUtc = objective.LastOutcomeUtc != DateTime.MinValue
                                    ? (DateTime?)objective.LastOutcomeUtc : null,
                                LastOutcomeRangeM = objective.LastOutcomeRangeM > 0
                                    ? (double?)objective.LastOutcomeRangeM : null,
                                LastOutcomeOwnDps = objective.LastOutcomeOwnDps > 0
                                    ? (double?)objective.LastOutcomeOwnDps : null,
                                LastOutcomeOpposedDps = objective.LastOutcomeOpposedDps > 0
                                    ? (double?)objective.LastOutcomeOpposedDps : null,
                                LastOutcomeHullsLost = objective.LastOutcomeHullsLost > 0
                                    ? (int?)objective.LastOutcomeHullsLost : null,
                                LastOutcomeHullsTotal = objective.LastOutcomeHullsTotal > 0
                                    ? (int?)objective.LastOutcomeHullsTotal : null,
                                LastDispatchFleetId = objective.LastDispatchFleetId,
                            });
                        }
                        if (objectives.Count == 0) objectives = null;
                    }
                    campaignFoci.Add(new CampaignFocusDoc
                    {
                        Id = focus.Id,
                        CenterNodeId = focus.CenterNodeId,
                        Priority = focus.Priority,
                        SetAtUtc = focus.SetAtUtc,
                        ExpiresAtUtc = focus.ExpiresAtUtc,
                        RegionMembers = focus.RegionMembers.Count > 0 ? new List<string>(focus.RegionMembers) : null,
                        Objectives = objectives,
                        PlayerIssued = focus.PlayerIssued
                    });
                }
                if (campaignFoci.Count == 0) campaignFoci = null;
            }

            return new BrainDoc
            {
                TopologyVersion = s.TopologyVersion,
                Tag = s.Tag,
                FactionId = s.FactionId,
                PersonalityId = s.PersonalityId,
                RngSeed = s.RngSeed,
                DormantUntilUtc = s.DormantUntilUtc == DateTime.MinValue ? (DateTime?)null : s.DormantUntilUtc,
                IsPlayerFaction = s.IsPlayerFaction ? (bool?)true : null,
                CentralBankSupportCooldownUntilUtc = s.CentralBankSupportCooldownUntilUtc == DateTime.MinValue
                    ? (DateTime?)null
                    : s.CentralBankSupportCooldownUntilUtc,
                CentralBankGrantCooldownUntilUtc = s.CentralBankGrantCooldownUntilUtc == DateTime.MinValue
                    ? (DateTime?)null
                    : s.CentralBankGrantCooldownUntilUtc,
                CentralBankSupportDebt = s.CentralBankSupportDebt > 0 ? (long?)s.CentralBankSupportDebt : null,
                CentralBankRecentIncome = s.CentralBankRecentIncome != 0 ? (double?)s.CentralBankRecentIncome : null,
                Drives = drives,
                Cooldowns = cooldowns,
                ActionRepetition = actionRepetition,
                TradeDestCooldowns = tradeDestCooldowns,
                PatrolNodeCooldowns = patrolNodeCooldowns,
                ReinforceNodeCooldowns = reinforceNodeCooldowns,
                ReinforcePresenceSeconds = reinforcePresenceSeconds,
                Knowledge = knowledge,
                ReservationOptIn = reservationOptIn,
                InstallPriority = installPriority,
                ReinforceOptIn = reinforceOptIn,
                ReinforceIncomeFractionOverride = s.ReinforceIncomeFractionOverride,
                ReinforceBudgetFloorOverride = s.ReinforceBudgetFloorOverride,
                ReinforceWealthFractionOverride = s.ReinforceWealthFractionOverride,
                TradeBuyOptIn = s.TradeBuyOptIn ? (bool?)true : null,
                TradeSellOptIn = s.TradeSellOptIn ? (bool?)true : null,
                TradeIncomeFractionOverride = s.TradeIncomeFractionOverride,
                TradeBudgetFloorOverride = s.TradeBudgetFloorOverride,
                TradeWealthFractionOverride = s.TradeWealthFractionOverride,
                StandingOrders = standingOrders,
                FleetDoctrine = doctrine,
                DoctrineSeq = s.DoctrineSeq,
                Shelf = shelf,
                ShelfSeq = s.ShelfSeq,
                HullRefitCaps = s.HullRefitCaps.Count > 0 ? new Dictionary<string, int>(s.HullRefitCaps) : null,
                DecommissionOnReturn = s.DecommissionOnReturn.Count > 0 ? new Dictionary<string, int>(s.DecommissionOnReturn) : null,
                StoreShelfOptOut = s.StoreShelfOptOut,
                CentralBankDontSell = s.CentralBankDontSell.Count > 0 ? SortedKeys(s.CentralBankDontSell) : null,
                CentralBankTargets = s.CentralBankTargets.Count > 0
                    ? new Dictionary<string, long>(s.CentralBankTargets, StringComparer.Ordinal) : null,
                CentralBankSellOptOut = s.LegacyCentralBankSellOptOut,
                FleetRequirements = fleetRequirements,
                StandingOrderSeq = s.StandingOrderSeq,
                Relations = new Dictionary<string, double>(s.Relations),
                EarnedRelations = s.EarnedRelations.Count > 0 ? new Dictionary<string, double>(s.EarnedRelations) : null,
                Fatigue = s.Fatigue.Count > 0 ? new Dictionary<string, double>(s.Fatigue) : null,
                TruceUntil = s.TruceUntil.Count > 0 ? new Dictionary<string, DateTime>(s.TruceUntil) : null,
                LastHostileActUtc = s.LastHostileActUtc.Count > 0 ? new Dictionary<string, DateTime>(s.LastHostileActUtc) : null,
                PactWith = s.PactWith.Count > 0 ? new List<string>(s.PactWith) : null,
                Grudges = grudges,
                Proposals = proposals,
                Embargoes = s.Embargoes.Count > 0 ? new List<string>(s.Embargoes) : null,
                Log = log,
                Events = events,
                Sources = sources,
                PendingFleets = pendingFleets,
                DemandPressure = demandPressure,
                CampaignFocusRegion = s.CampaignFoci.Count == 0 ? s.CampaignFocusRegion : null,
                CampaignFocusPriority = s.CampaignFoci.Count == 0 && s.CampaignFocusRegion != null ? (double?)s.CampaignFocusPriority : null,
                CampaignSetAtUtc = s.CampaignFoci.Count == 0 && s.CampaignFocusRegion != null ? (DateTime?)s.CampaignSetAtUtc : null,
                CampaignFocusRegionMembers = s.CampaignFoci.Count == 0 && s.CampaignFocusRegionMembers.Count > 0 ? new List<string>(s.CampaignFocusRegionMembers) : null,
                CampaignFoci = campaignFoci,
                MilitaryPosture = s.MilitaryPosture > 0 ? (double?)s.MilitaryPosture : null,
                MusterFocusTag = s.MusterFocusTag,
                MusterFocusPriority = s.MusterFocusTag != null ? (double?)s.MusterFocusPriority : null,
                MusterSetAtUtc = s.MusterFocusTag != null ? (DateTime?)s.MusterSetAtUtc : null,
                LastTerrorCampaignUtc = s.LastTerrorCampaignUtc != DateTime.MinValue ? (DateTime?)s.LastTerrorCampaignUtc : null,
                LastTerrorRegionKey = s.LastTerrorRegionKey,
                TerrorEscalation = s.TerrorEscalation > 0 ? (int?)s.TerrorEscalation : null,
                ControllerSpeaker = s.ControllerSpeaker >= 0 ? (int?)s.ControllerSpeaker : null,
                TerrorEscalationUtc = s.TerrorEscalationUtc != DateTime.MinValue ? (DateTime?)s.TerrorEscalationUtc : null,
                TerrorRevenge = s.TerrorRevenge,
                TerrorMetDps = s.TerrorMetDps > 0 ? (long?)s.TerrorMetDps : null,
                TerrorStandingDps = s.TerrorStandingDps > 0 ? (long?)s.TerrorStandingDps : null,
                Fights = ToFightDocs(s.Fights),
                Tactics = ToTacticDoc(s.TacticMemory),
            };
        }

        private static TacticMemoryDoc ToTacticDoc(TacticMemory m)
        {
            if (m == null || m.IsEmpty) return null;
            var doc = new TacticMemoryDoc { Updates = m.Updates };
            foreach (KeyValuePair<string, TacticMemory.Opponent> opp in m.Opponents())
            {
                var buckets = new List<TacticBucketDoc>();
                var keys = new List<string>(opp.Value.Buckets.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    TacticMemory.Bucket b = opp.Value.Buckets[key];
                    var cells = new StringBuilder();
                    for (int i = 0; i < TacticMemory.Actions; i++)
                    {
                        double a = Math.Round(b.A[i], 3), bb = Math.Round(b.B[i], 3);
                        if (a <= 0 && bb <= 0) continue;
                        if (cells.Length > 0) cells.Append(' ');
                        cells.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':')
                             .Append(a.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                             .Append(bb.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    if (cells.Length == 0) continue;
                    buckets.Add(new TacticBucketDoc { Ctx = key, Utc = b.Utc, Cells = cells.ToString() });
                }
                if (buckets.Count == 0) continue;
                if (doc.Opponents == null) doc.Opponents = new List<TacticOpponentDoc>();
                doc.Opponents.Add(new TacticOpponentDoc { Tag = opp.Key, Utc = opp.Value.LastUtc, Buckets = buckets });
            }
            foreach (TacticReview r in m.Reviews)
            {
                if (doc.Reviews == null) doc.Reviews = new List<TacticReviewDoc>();
                doc.Reviews.Add(new TacticReviewDoc
                {
                    Utc = r.Utc,
                    Vs = r.OpponentTag,
                    Ctx = r.Context,
                    Cell = TacticMemory.Index(r.Arm, r.BreakOff),
                    Pred = double.IsNaN(r.Predicted) ? (double?)null : Math.Round(r.Predicted, 3),
                    Act = Math.Round(r.Actual, 3),
                    Rew = Math.Round(r.Reward, 3),
                    Mistake = r.Mistake,
                    Commanded = r.Commanded,
                    Lost = r.HullsLost,
                    Brought = r.HullsBrought,
                });
            }
            return doc;
        }

        /// <summary>Tolerant: a malformed cell, context or opponent is skipped, never fatal.</summary>
        private static void FromTacticDoc(TacticMemoryDoc doc, TacticMemory m)
        {
            if (doc == null || m == null) return;
            m.Updates = Math.Max(0, doc.Updates);
            if (doc.Reviews != null)
                foreach (TacticReviewDoc r in doc.Reviews)
                {
                    if (r == null || string.IsNullOrEmpty(r.Vs) || r.Cell < 0 || r.Cell >= TacticMemory.Actions) continue;
                    m.RestoreReview(new TacticReview
                    {
                        Utc = r.Utc,
                        OpponentTag = r.Vs,
                        Context = r.Ctx,
                        Arm = TacticMemory.ArmOf(r.Cell),
                        BreakOff = TacticMemory.BreakOffOf(r.Cell),
                        Predicted = r.Pred.HasValue ? r.Pred.Value : double.NaN,
                        Actual = r.Act,
                        Reward = r.Rew,
                        Mistake = r.Mistake,
                        Commanded = r.Commanded,
                        HullsLost = r.Lost,
                        HullsBrought = r.Brought,
                    });
                }
            if (doc.Opponents == null) return;
            foreach (TacticOpponentDoc o in doc.Opponents)
            {
                if (o == null || string.IsNullOrEmpty(o.Tag) || o.Buckets == null) continue;
                foreach (TacticBucketDoc b in o.Buckets)
                {
                    if (b == null || b.Cells == null) continue;
                    var cells = new List<KeyValuePair<int, double[]>>();
                    foreach (string cell in b.Cells.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = cell.Split(':');
                        int idx; double a, bb;
                        if (parts.Length != 3
                            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)
                            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out bb))
                            continue;
                        cells.Add(new KeyValuePair<int, double[]>(idx, new[] { a, bb }));
                    }
                    m.Restore(o.Tag, o.Utc, b.Ctx, b.Utc, cells);
                }
            }
        }

        private static string ArmsText(DuelTactic t)
        {
            return t.AttackerArm + "/" + t.AttackerBreakOff + ":" + t.DefenderArm + "/" + t.DefenderBreakOff;
        }

        private static string BracketsText(DuelTactic t)
        {
            if (t.BracketsM == null || t.BracketsM.Length == 0) return null;
            var parts = new string[t.BracketsM.Length];
            for (int i = 0; i < parts.Length; i++) parts[i] = t.BracketsM[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(" ", parts);
        }

        /// <summary>The tactic a <see cref="FightDoc"/> carries, or null when absent or unreadable.</summary>
        private static DuelTactic ParseTactic(string arms, string brackets)
        {
            if (string.IsNullOrEmpty(arms)) return null;
            string[] sides = arms.Split(':');
            if (sides.Length != 2) return null;
            TacticArm aArm, dArm; BreakOffLevel aOff, dOff;
            if (!ParseStance(sides[0], out aArm, out aOff) || !ParseStance(sides[1], out dArm, out dOff)) return null;
            var t = new DuelTactic { AttackerArm = aArm, AttackerBreakOff = aOff, DefenderArm = dArm, DefenderBreakOff = dOff };
            if (!string.IsNullOrEmpty(brackets))
            {
                var list = new List<int>();
                foreach (string p in brackets.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int m;
                    if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) list.Add(m);
                }
                if (list.Count > 0) t.BracketsM = list.ToArray();
            }
            return t;
        }

        private static bool ParseStance(string text, out TacticArm arm, out BreakOffLevel breakOff)
        {
            arm = TacticArm.Standoff; breakOff = BreakOffLevel.Mid;
            string[] p = text.Split('/');
            return p.Length == 2
                && Enum.TryParse(p[0], out arm) && Enum.IsDefined(typeof(TacticArm), arm)
                && Enum.TryParse(p[1], out breakOff) && Enum.IsDefined(typeof(BreakOffLevel), breakOff);
        }

        private static List<FightDoc> ToFightDocs(List<FightRecord> fights)
        {
            if (fights == null || fights.Count == 0) return null;
            var docs = new List<FightDoc>(fights.Count);
            foreach (FightRecord f in fights)
                docs.Add(new FightDoc
                {
                    Utc = f.Utc,
                    Kind = f.Kind.ToString(),
                    Attacking = f.Attacking,
                    Opponent = f.OpponentTag,
                    NodeId = f.NodeId,
                    Region = f.RegionKey,
                    AttackerDps = f.AttackerDps,
                    DefenderDps = f.DefenderDps,
                    Ratio = f.AttackerRatio,
                    HullsLost = f.AttackerHullsLost,
                    Arms = f.Tactic != null ? ArmsText(f.Tactic) : null,
                    Brackets = f.Tactic != null ? BracketsText(f.Tactic) : null,
                });
            return docs;
        }

        /// <summary>Restore the topology recorded by the supplied complete brain set after every
        /// source has first been hydrated into <paramref name="graph"/>. Every doc's persisted
        /// <see cref="SourceDoc.Neighbors"/> is replayed regardless of <see cref="BrainDoc.TopologyVersion"/>
        /// — that field is retained solely for <see cref="TerritoryReconciliationPlan.Create"/>'s
        /// UNKN-only legacy-engine-node detection, not as a topology-trust gate here (a prior version
        /// of this method skipped every doc below version 1, which meant no faction's edges ever
        /// survived a restart except UNKN's, since only the one-shot `reconcileterritory apply`
        /// transaction ever bumped that field). Safe to always trust: stale/missing endpoints are
        /// ignored below, so replaying more docs can only add valid edges, never resurrect a broken
        /// one. Returns the count of newly created undirected links.</summary>
        public static int RestoreTopology(IEnumerable<BrainDoc> docs, SpatialGraph graph)
        {
            if (docs == null || graph == null) return 0;
            int restored = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BrainDoc doc in docs)
            {
                if (doc == null || doc.Sources == null) continue;
                foreach (SourceDoc source in doc.Sources)
                {
                    if (source == null || string.IsNullOrEmpty(source.Id) || source.Neighbors == null) continue;
                    foreach (string neighborId in source.Neighbors)
                    {
                        if (string.IsNullOrEmpty(neighborId) || neighborId == source.Id) continue;
                        SpatialNode sourceNode, neighborNode;
                        if (!graph.Nodes.TryGetValue(source.Id, out sourceNode)
                            || !graph.Nodes.TryGetValue(neighborId, out neighborNode)) continue;
                        string first = string.CompareOrdinal(source.Id, neighborId) < 0 ? source.Id : neighborId;
                        string second = first == source.Id ? neighborId : source.Id;
                        if (!seen.Add(first + "\u001f" + second)) continue;
                        bool alreadyLinked = sourceNode.Neighbors.Contains(neighborId);
                        graph.Connect(source.Id, neighborId);
                        if (!alreadyLinked) restored++;
                    }
                }
            }
            return restored;
        }

        /// <summary>Rebuild a FactionState from a brain doc and re-add its sources to the graph.
        /// <paramref name="log"/> collects one line per state-changing MIGRATION this load performed
        /// (today: a retired <c>Kind: "Build"</c> standing order becoming a doctrine line). Optional
        /// because most callers -- the API snapshot service, a test round-trip -- are reading, not
        /// booting a server; the host passes a list and writes what comes back to the decision log,
        /// so an upgrade that rewrites a player's fleet goal says so once.</summary>
        public static FactionState ApplyBrainDoc(BrainDoc d, SpatialGraph graph,
            TimeSpan? legacyCampaignLifetime = null, List<string> log = null)
        {
            bool isOldShapeDoc = IsOldShapeDoc(d);
            // UNKN's naming theme is enforced here, not just at registration (FactionsPlugin.
            // InitializeFromNpcFactions), so a world where UNKN was already registered before
            // NeutralFaction existed self-heals to "derelict" on the very next load instead of
            // staying stuck on whatever PersonalityFromFaction originally guessed.
            string personalityId = d.Tag == NeutralFaction.Tag ? "derelict" : d.PersonalityId;
            // Ratchet every non-UNKN faction's TopologyVersion forward on load: RestoreTopology no
            // longer gates on this field, but a stale "0" would still misreport a brain as legacy in
            // any future diagnostic. UNKN is excluded so factions reconcileterritory apply's own
            // one-shot version bump (Persistence.cs, ReplaceAllUnknSources) stays the sole source of
            // truth for whether that specific one-shot transaction has run.
            int topologyVersion = string.Equals(d.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase)
                ? d.TopologyVersion
                : Math.Max(d.TopologyVersion, 1);
            var s = new FactionState { Tag = d.Tag, FactionId = d.FactionId, PersonalityId = personalityId, RngSeed = d.RngSeed,
                TopologyVersion = topologyVersion };
            s.DormantUntilUtc = d.DormantUntilUtc ?? DateTime.MinValue;   // resume a reboot mid-dormancy
            s.IsPlayerFaction = d.IsPlayerFaction ?? false;
            s.CentralBankSupportCooldownUntilUtc = d.CentralBankSupportCooldownUntilUtc ?? DateTime.MinValue;
            s.CentralBankGrantCooldownUntilUtc = d.CentralBankGrantCooldownUntilUtc ?? DateTime.MinValue;
            s.CentralBankSupportDebt = d.CentralBankSupportDebt ?? 0;
            s.CentralBankRecentIncome = d.CentralBankRecentIncome ?? 0;
            s.CampaignFocusRegion = d.CampaignFocusRegion;
            s.CampaignFocusPriority = d.CampaignFocusPriority ?? 0;
            s.CampaignSetAtUtc = d.CampaignSetAtUtc ?? DateTime.MinValue;
            if (d.CampaignFocusRegionMembers != null)
                foreach (string nodeId in d.CampaignFocusRegionMembers) s.CampaignFocusRegionMembers.Add(nodeId);
            if (d.CampaignFoci != null && d.CampaignFoci.Count > 0)
                foreach (CampaignFocusDoc focusDoc in d.CampaignFoci)
                {
                    if (focusDoc == null || string.IsNullOrEmpty(focusDoc.CenterNodeId)) continue;
                    var focus = new CampaignFocus
                    {
                        Id = string.IsNullOrEmpty(focusDoc.Id) ? focusDoc.CenterNodeId : focusDoc.Id,
                        CenterNodeId = focusDoc.CenterNodeId,
                        Priority = focusDoc.Priority,
                        SetAtUtc = focusDoc.SetAtUtc,
                        ExpiresAtUtc = focusDoc.ExpiresAtUtc,
                        PlayerIssued = focusDoc.PlayerIssued
                    };
                    if (focusDoc.RegionMembers != null)
                        foreach (string nodeId in focusDoc.RegionMembers)
                            if (!string.IsNullOrEmpty(nodeId)) focus.RegionMembers.Add(nodeId);
                    if (focus.RegionMembers.Count == 0) focus.RegionMembers.Add(focus.CenterNodeId);
                    if (focusDoc.Objectives != null)
                        foreach (CampaignObjectiveDoc objectiveDoc in focusDoc.Objectives)
                        {
                            CampaignObjectiveKind kind;
                            if (objectiveDoc == null || !Enum.TryParse<CampaignObjectiveKind>(objectiveDoc.Kind, out kind)) continue;
                            PlacementRegionKind regionKind;
                            bool namesRegion = !string.IsNullOrEmpty(objectiveDoc.TargetRegionKind)
                                && Enum.TryParse<PlacementRegionKind>(objectiveDoc.TargetRegionKind, out regionKind);
                            if (string.IsNullOrEmpty(objectiveDoc.TargetNodeId) && !namesRegion) continue;
                            var objective = new CampaignObjective
                            {
                                Kind = kind,
                                TargetNodeId = objectiveDoc.TargetNodeId,
                                TargetRegionKey = objectiveDoc.TargetRegionKey,
                                TargetPlanetId = objectiveDoc.TargetPlanetId,
                                WantedCombatHulls = objectiveDoc.WantedCombatHulls ?? 0,
                                WantedCombatDps = objectiveDoc.WantedCombatDps ?? 0,
                                LastDispatchUtc = objectiveDoc.LastDispatchUtc ?? DateTime.MinValue,
                                LastOutcomeUtc = objectiveDoc.LastOutcomeUtc ?? DateTime.MinValue,
                                LastOutcomeRangeM = objectiveDoc.LastOutcomeRangeM ?? 0,
                                LastOutcomeOwnDps = objectiveDoc.LastOutcomeOwnDps ?? 0,
                                LastOutcomeOpposedDps = objectiveDoc.LastOutcomeOpposedDps ?? 0,
                                LastOutcomeHullsLost = objectiveDoc.LastOutcomeHullsLost ?? 0,
                                LastOutcomeHullsTotal = objectiveDoc.LastOutcomeHullsTotal ?? 0,
                                LastDispatchFleetId = objectiveDoc.LastDispatchFleetId,
                            };
                            CampaignCause cause;
                            if (!string.IsNullOrEmpty(objectiveDoc.Cause)
                                && Enum.TryParse<CampaignCause>(objectiveDoc.Cause, out cause))
                                objective.Cause = cause;
                            CampaignOutcome outcome;
                            if (!string.IsNullOrEmpty(objectiveDoc.LastOutcome)
                                && Enum.TryParse<CampaignOutcome>(objectiveDoc.LastOutcome, out outcome))
                                objective.LastOutcome = outcome;
                            if (namesRegion && Enum.TryParse<PlacementRegionKind>(objectiveDoc.TargetRegionKind, out regionKind))
                                objective.TargetRegionKind = regionKind;
                            focus.Objectives.Add(objective);
                        }
                    if (focus.Objectives.Count == 0)
                        focus.Objectives.Add(new CampaignObjective
                        {
                            Kind = CampaignObjectiveKind.DefendRegion,
                            TargetNodeId = focus.CenterNodeId
                        });
                    s.CampaignFoci.Add(focus);
                }
            else if (!string.IsNullOrEmpty(d.CampaignFocusRegion))
            {
                DateTime setAtUtc = d.CampaignSetAtUtc ?? DateTime.MinValue;
                TimeSpan lifetime = legacyCampaignLifetime ?? TimeSpan.FromHours(1);
                var legacyFocus = new CampaignFocus
                {
                    Id = d.CampaignFocusRegion,
                    CenterNodeId = d.CampaignFocusRegion,
                    Priority = d.CampaignFocusPriority ?? 0,
                    SetAtUtc = setAtUtc,
                    ExpiresAtUtc = setAtUtc == DateTime.MinValue ? DateTime.MinValue : setAtUtc.Add(lifetime)
                };
                if (d.CampaignFocusRegionMembers != null)
                    foreach (string nodeId in d.CampaignFocusRegionMembers)
                        if (!string.IsNullOrEmpty(nodeId)) legacyFocus.RegionMembers.Add(nodeId);
                if (legacyFocus.RegionMembers.Count == 0) legacyFocus.RegionMembers.Add(legacyFocus.CenterNodeId);
                legacyFocus.Objectives.Add(new CampaignObjective
                {
                    Kind = CampaignObjectiveKind.DefendRegion,
                    TargetNodeId = legacyFocus.CenterNodeId
                });
                s.CampaignFoci.Add(legacyFocus);
            }

            // Keep transitional singleton readers on the union of every persisted focus.
            CampaignFocusLifecycle.RefreshCompatibilityView(s);
            s.MilitaryPosture = d.MilitaryPosture ?? 0;
            s.MusterFocusTag = d.MusterFocusTag;
            s.MusterFocusPriority = d.MusterFocusPriority ?? 0;
            s.MusterSetAtUtc = d.MusterSetAtUtc ?? DateTime.MinValue;
            s.LastTerrorCampaignUtc = d.LastTerrorCampaignUtc ?? DateTime.MinValue;
            s.LastTerrorRegionKey = d.LastTerrorRegionKey;
            s.TerrorEscalation = Math.Max(0, d.TerrorEscalation ?? 0);
            s.ControllerSpeaker = d.ControllerSpeaker.HasValue && d.ControllerSpeaker.Value >= 0
                                  && d.ControllerSpeaker.Value < ControllerVoice.Speakers ? d.ControllerSpeaker.Value : -1;
            s.TerrorEscalationUtc = d.TerrorEscalationUtc ?? DateTime.MinValue;
            s.TerrorRevenge = d.TerrorRevenge;
            s.TerrorMetDps = Math.Max(0, d.TerrorMetDps ?? 0);
            s.TerrorStandingDps = Math.Max(0, d.TerrorStandingDps ?? 0);
            if (d.Fights != null)
                foreach (FightDoc f in d.Fights)
                {
                    if (f == null) continue;
                    ActionKind kind;
                    if (!Enum.TryParse<ActionKind>(f.Kind, out kind)) continue;
                    s.Fights.Add(new FightRecord
                    {
                        Utc = f.Utc,
                        Kind = kind,
                        Attacking = f.Attacking,
                        OpponentTag = f.Opponent,
                        NodeId = f.NodeId,
                        RegionKey = f.Region,
                        AttackerDps = f.AttackerDps,
                        DefenderDps = f.DefenderDps,
                        AttackerRatio = f.Ratio,
                        AttackerHullsLost = f.HullsLost,
                        Tactic = ParseTactic(f.Arms, f.Brackets),
                    });
                }
            FromTacticDoc(d.Tactics, s.TacticMemory);

            if (d.Drives != null)
                for (int i = 0; i < Math.Min(d.Drives.Length, DriveVector.Count); i++) s.Drives[(Drive)i] = d.Drives[i];

            if (d.Cooldowns != null)
                foreach (var kv in d.Cooldowns)
                {
                    string kindStr = (isOldShapeDoc && kv.Key == "Patrol") ? "Guard" : kv.Key; // migrate legacy "Patrol" (= old Guard) → "Guard" — only for docs old enough to need it, else a real current Patrol cooldown gets corrupted
                    ActionKind k;
                    if (Enum.TryParse<ActionKind>(kindStr, out k)) s.Cooldowns[k] = kv.Value;
                }

            if (d.ActionRepetition != null)
                foreach (var kv in d.ActionRepetition)
                {
                    ActionKind k;
                    if (Enum.TryParse<ActionKind>(kv.Key, out k) && kv.Value > 0) s.ActionRepetition[k] = kv.Value;
                }

            if (d.TradeDestCooldowns != null)
                foreach (var kv in d.TradeDestCooldowns) s.TradeDestCooldowns[kv.Key] = kv.Value;

            if (d.PatrolNodeCooldowns != null)
                foreach (var kv in d.PatrolNodeCooldowns) s.PatrolNodeCooldowns[kv.Key] = kv.Value;

            if (d.ReinforceNodeCooldowns != null)
                foreach (var kv in d.ReinforceNodeCooldowns) s.ReinforceNodeCooldowns[kv.Key] = kv.Value;

            if (d.ReinforcePresenceSeconds != null)
                foreach (var kv in d.ReinforcePresenceSeconds) s.ReinforcePresenceSeconds[kv.Key] = kv.Value;
            if (d.Knowledge != null && d.Knowledge.Count > 0)
            {
                var loaded = new Dictionary<string, KnownNode>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, KnowledgeEntryDoc> kv in d.Knowledge)
                {
                    if (kv.Value == null) continue;
                    loaded[kv.Key] = kv.Value.ToKnownNode();
                }
                s.Knowledge.LoadFrom(loaded);
            }


            s.StandingOrderSeq = d.StandingOrderSeq;
            // A missing key, a short list or a long one all load: the bucket count is an internal
            // detail that may change, and a brain file written by another build must not throw.
            if (d.FleetRequirements != null)
                foreach (var fr in d.FleetRequirements)
                {
                    if (fr.Value == null || string.IsNullOrEmpty(fr.Key)) continue;
                    // A record measured on another basis is not evidence about this one. The
                    // faction starts cold and keeps the live floor until it is warm again.
                    if (fr.Value.Basis != RoleRequirement.CurrentBasis) continue;
                    RoleRequirement r = s.RequirementFor(fr.Key);
                    if (fr.Value.Buckets != null)
                    {
                        int n = Math.Min(fr.Value.Buckets.Length, RoleRequirement.BucketCount);
                        for (int i = 0; i < n; i++) r.Buckets[i] = fr.Value.Buckets[i];
                    }
                    r.UnmetEma = fr.Value.UnmetEma;
                    r.Samples  = fr.Value.Samples;
                }

            s.ShelfSeq = d.ShelfSeq;
            s.StoreShelfOptOut = d.StoreShelfOptOut;
            s.LegacyCentralBankSellOptOut = d.CentralBankSellOptOut;
            if (d.CentralBankDontSell != null)
                foreach (string key in d.CentralBankDontSell) s.SetCentralBankRule(key, BankSaleMode.DontSell, 0);
            if (d.CentralBankTargets != null)
                foreach (KeyValuePair<string, long> kv in d.CentralBankTargets)
                    s.SetCentralBankRule(kv.Key, BankSaleMode.AboveTarget, kv.Value);
            if (d.HullRefitCaps != null)
                foreach (KeyValuePair<string, int> kv in d.HullRefitCaps)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) s.HullRefitCaps[kv.Key] = kv.Value;
            if (d.DecommissionOnReturn != null)
                foreach (KeyValuePair<string, int> kv in d.DecommissionOnReturn)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) s.DecommissionOnReturn[kv.Key] = kv.Value;
            if (d.Shelf != null)
                foreach (ShelfLineDoc l in d.Shelf)
                {
                    if (l == null) continue;
                    s.Shelf.Add(new ShelfLine
                    {
                        Id = l.Id, PrefabId = l.PrefabId,
                        TargetCount = l.TargetCount, Paused = l.Paused,
                        CreatedUtc = l.CreatedUtc,
                    });
                }

            s.DoctrineSeq = d.DoctrineSeq;
            if (d.FleetDoctrine != null)
                foreach (DoctrineLineDoc l in d.FleetDoctrine)
                {
                    if (l == null) continue;
                    s.FleetDoctrine.Add(new DoctrineLine
                    {
                        Id = l.Id, PrefabId = l.PrefabId,
                        TargetCount = l.TargetCount, MaxConcurrent = l.MaxConcurrent,
                        ScopeNodeId = l.ScopeNodeId, Priority = l.Priority, Paused = l.Paused,
                        CreatedUtc = l.CreatedUtc,
                    });
                }

            if (d.StandingOrders != null)
                foreach (StandingOrderDoc o in d.StandingOrders)
                {
                    // MIGRATION: the retired StandingOrderKind.Build. Handled BEFORE the enum parse,
                    // which no longer knows the name and would otherwise drop the order silently --
                    // the player's fleet goal would vanish on the upgrade with nothing in the log.
                    // The id is re-minted from the doctrine's own counter (SO-003 -> FD-001), and
                    // ExcludeListedForSale is dropped rather than carried: a listed hull is never
                    // "ours" for doctrine purposes, so the exclusion is unconditional now.
                    if (string.Equals(o.Kind, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        var migrated = new DoctrineLine
                        {
                            Id = s.MintDoctrineLineId(),
                            PrefabId = o.PrefabId,
                            TargetCount = o.TargetCount,
                            MaxConcurrent = o.MaxConcurrent,
                            ScopeNodeId = o.ScopeNodeId,
                            Priority = o.Priority,
                            Paused = o.Paused,
                            CreatedUtc = o.CreatedUtc,
                        };
                        s.FleetDoctrine.Add(migrated);
                        if (log != null)
                            log.Add(s.Tag + " DOCTRINE_MIGRATED from=" + (o.Id ?? "?")
                                    + " to=" + migrated.Id
                                    + " prefab=" + (migrated.PrefabId ?? "?")
                                    + " target=" + migrated.TargetCount);
                        continue;
                    }

                    StandingOrderKind kind;
                    // A kind this build does not recognise is DROPPED, not loaded as Mine: an order
                    // is an instruction to spend the faction's resources, and running the wrong one
                    // is worse than losing it. (The Events ring makes the opposite call for the same
                    // reason inverted -- a record it cannot name is still history worth keeping.)
                    if (!Enum.TryParse<StandingOrderKind>(o.Kind, out kind)) continue;
                    s.StandingOrders.Add(new StandingOrder
                    {
                        Id = o.Id, Kind = kind, Priority = o.Priority, Paused = o.Paused,
                        CreatedUtc = o.CreatedUtc,
                        HubNodeId = o.HubNodeId, PeriodSeconds = o.PeriodSeconds,
                        OreWeights = o.OreWeights, NextScanUtc = o.NextScanUtc,
                        NodeId = o.NodeId, AllStations = o.AllStations, ItemKey = o.ItemKey, TargetQuantity = o.TargetQuantity,
                    });
                }

            // LEGACY migration: the pre-StandingOrders one-order-per-hub dictionary. Runs only for a
            // file that still carries the old key AND has no new list (a file written by this build
            // has StandingOrders and no MineAutoOrders, so this cannot double-load), and mints ids
            // through the same counter so a migrated order is indistinguishable from a fresh one.
            if (d.MineAutoOrders != null && (d.StandingOrders == null || d.StandingOrders.Count == 0))
                foreach (var kv in d.MineAutoOrders)
                    s.StandingOrders.Add(new StandingOrder
                    {
                        Id = s.MintStandingOrderId(),
                        Kind = StandingOrderKind.Mine,
                        // CreatedUtc deliberately left at default: the old dictionary never recorded
                        // one, and MinValue sorts a migrated order ahead of anything created after
                        // the upgrade at the same rank, which is the correct relative age.
                        HubNodeId = kv.Key,
                        PeriodSeconds = kv.Value.PeriodSeconds,
                        NextScanUtc = kv.Value.NextScanUtc,
                        OreWeights = kv.Value.OreWeights,
                    });

            if (d.ReservationOptIn != null)
                foreach (string capName in d.ReservationOptIn)
                {
                    NodeCapabilityKind cap;
                    if (Enum.TryParse<NodeCapabilityKind>(capName, out cap)) s.ReservationOptIn.Add(cap);
                }

            if (d.InstallPriority != null)
                foreach (var kv in d.InstallPriority)
                {
                    NodeCapabilityKind cap;
                    if (kv.Value != 0 && Enum.TryParse<NodeCapabilityKind>(kv.Key, out cap)) s.InstallPriority[cap] = kv.Value;
                }

            if (d.ReinforceOptIn != null)
                foreach (var kv in d.ReinforceOptIn)
                    if (!string.IsNullOrEmpty(kv.Key)) s.ReinforceOptIn[kv.Key] = kv.Value;

            // Nullable by design: absent means "use the world's tuning", and a doc written before
            // these existed loads as exactly that.
            s.ReinforceIncomeFractionOverride = d.ReinforceIncomeFractionOverride;
            s.ReinforceBudgetFloorOverride = d.ReinforceBudgetFloorOverride;
            s.ReinforceWealthFractionOverride = d.ReinforceWealthFractionOverride;

            // Both flags are written only when true, so an absent one is a genuine "off" and every
            // brain doc predating this feature loads with trade switched off -- which is the
            // behaviour that file was saved under.
            s.TradeBuyOptIn = d.TradeBuyOptIn ?? false;
            s.TradeSellOptIn = d.TradeSellOptIn ?? false;
            s.TradeIncomeFractionOverride = d.TradeIncomeFractionOverride;
            s.TradeBudgetFloorOverride = d.TradeBudgetFloorOverride;
            s.TradeWealthFractionOverride = d.TradeWealthFractionOverride;

            if (d.Relations != null)
                foreach (var kv in d.Relations) s.Relations[kv.Key] = kv.Value;

            // Absent for any world saved before this existed. Deliberately NOT back-filled from
            // Relations here: EarnedRelationTo falls back to the current standing when an entry is
            // missing, so an old world carries its standings forward without writing a ledger entry
            // it never actually earned.
            if (d.EarnedRelations != null)
                foreach (var kv in d.EarnedRelations) s.EarnedRelations[kv.Key] = kv.Value;

            if (d.Fatigue != null)
                foreach (var kv in d.Fatigue) s.Fatigue[kv.Key] = kv.Value;

            if (d.TruceUntil != null)
                foreach (var kv in d.TruceUntil) s.TruceUntil[kv.Key] = kv.Value;
            if (d.LastHostileActUtc != null)
                foreach (var kv in d.LastHostileActUtc) s.LastHostileActUtc[kv.Key] = kv.Value;

            if (d.PactWith != null)
                foreach (string tag in d.PactWith) s.PactWith.Add(tag);

            if (d.Grudges != null)
                foreach (var kv in d.Grudges)
                {
                    if (kv.Value == null) continue;
                    GrudgeKind kind;
                    if (Enum.TryParse<GrudgeKind>(kv.Value.Kind, out kind))
                        s.Grudges[kv.Key] = new GrudgeRecord { Kind = kind, ExpiresUtc = kv.Value.ExpiresUtc };
                }

            if (d.Embargoes != null)
                foreach (string tag in d.Embargoes) s.Embargoes.Add(tag);

            if (d.Proposals != null)
                foreach (var kv in d.Proposals)
                {
                    if (kv.Value == null || kv.Value.Verbs == null || string.IsNullOrEmpty(kv.Key)) continue;
                    var terms = new List<DiplomacyTerm>();
                    for (int i = 0; i < kv.Value.Verbs.Count; i++)
                    {
                        DiplomacyVerb verb;
                        if (!Enum.TryParse<DiplomacyVerb>(kv.Value.Verbs[i], out verb)) continue;
                        string param = kv.Value.Params != null && i < kv.Value.Params.Count ? kv.Value.Params[i] : null;
                        terms.Add(new DiplomacyTerm { Verb = verb, Param = param });
                    }
                    if (terms.Count > 0)
                        s.Proposals[kv.Key] = new DiplomacyMessage { Terms = terms.ToArray(), IssuedUtc = kv.Value.IssuedUtc };
                }

            if (d.DemandPressure != null)
                foreach (var kv in d.DemandPressure)
                {
                    ItemKind k;
                    if (Enum.TryParse<ItemKind>(kv.Key, out k)) s.DemandPressure[k] = kv.Value;
                }

            if (d.Log != null) s.Log.AddRange(d.Log);
            if (d.Events != null)
                foreach (FactionEventDoc e in d.Events)
                {
                    if (e == null || string.IsNullOrEmpty(e.ActorTag)) continue;
                    FactionEventKind kind;
                    // A file written by a newer build can name a kind this one has never heard of.
                    // Keeping it as Unknown preserves the ring's shape and lets the reader show
                    // "something happened" rather than silently shortening the history.
                    if (!Enum.TryParse(e.Kind, false, out kind)) kind = FactionEventKind.Unknown;
                    s.RecentEvents.Add(new FactionEvent
                    {
                        Utc = e.Utc,
                        Kind = kind,
                        ActorTag = e.ActorTag,
                        OtherTag = e.OtherTag,
                        NodeId = e.NodeId,
                        Amount = e.Amount ?? 0,
                        Ratio = e.Ratio ?? 0,
                    });
                }

            if (d.PendingFleets != null)
                foreach (PendingFleetDoc pfd in d.PendingFleets)
                {
                    string kindStr = (isOldShapeDoc && pfd.Kind == "Patrol") ? "Guard" : pfd.Kind; // migrate legacy "Patrol" (= old Guard) → "Guard" — only for docs old enough to need it, else a real current Patrol fleet gets corrupted (the exact live bug this gate fixes)
                    ActionKind kind;
                    if (!Enum.TryParse<ActionKind>(kindStr, out kind)) continue;
                    FleetStopKind currentStopKind = FleetStopKind.Service;
                    if (!string.IsNullOrEmpty(pfd.CurrentStopKind))
                        Enum.TryParse<FleetStopKind>(pfd.CurrentStopKind, out currentStopKind);
                    List<FleetStop> remainingStops = FromStopDocs(pfd.RemainingStops);
                    s.PendingFleets.Add(new PendingFleetAction
                    {
                        Kind = kind,
                        ActorTag = pfd.ActorTag,
                        TargetTag = pfd.TargetTag,
                        OriginNodeId = pfd.OriginNodeId,
                        ReturnNodeId = pfd.ReturnNodeId,
                        InterceptedByFleetId = pfd.InterceptedByFleetId,  // null for pre-FAC-277 brain docs
                        TargetNodeId = pfd.TargetNodeId,
                        OriginPosKm = new Vec3(pfd.OriginX, pfd.OriginY, pfd.OriginZ),
                        TargetPosKm = new Vec3(pfd.TargetX, pfd.TargetY, pfd.TargetZ),
                        DepartedUtc = pfd.DepartedUtc,
                        EstimatedArrivalUtc = pfd.EstimatedArrivalUtc,
                        CargoValue = pfd.CargoValue,
                        Cargo = DeserializeCargo(pfd.Cargo),  // migrates old ItemKind keys → SubTypeId
                        Munitions = pfd.MunitionsArmed ? DeserializeMunitions(pfd.Munitions) : null,
                        MunitionsArmed = pfd.MunitionsArmed,
                        RetreatReason = pfd.RetreatReason,
                        TradeMagnitude = pfd.TradeMagnitude,
                        ConvoySize = pfd.ConvoySize > 0 ? pfd.ConvoySize : 1,  // default 1 for old brain docs
                        Resolved = pfd.Resolved,
                        AgainstIntruder = pfd.AgainstIntruder,
                        CurrentStopKind = currentStopKind,
                        RemainingStops = remainingStops,
                        SuspendedStops = FromStopDocs(pfd.SuspendedStops),
                        PickupItemKey = pfd.PickupItemKey,
                        PickupQuantity = pfd.PickupQuantity,
                        MineWeights = pfd.MineWeights,
                        MineTargetLiters = pfd.MineTargetLiters,
                        MineWanderUp = pfd.MineWanderUp != null && pfd.MineWanderUp.Length == 3
                            ? new Vec3(pfd.MineWanderUp[0], pfd.MineWanderUp[1], pfd.MineWanderUp[2]) : (Vec3?)null,
                        RouteDistanceKm = pfd.RouteDistanceKm,
                        RouteCoveredKm = pfd.RouteCoveredKm,
                        RouteDepartedUtc = pfd.RouteDepartedUtc,
                        LegZones = ZonesFromDoc(pfd.ZoneRunKm, pfd.ZoneRunTiers),
                        RetargetCount = pfd.RetargetCount,
                        LastRetargetUtc = pfd.LastRetargetUtc,
                        PathNodeIds = pfd.PathNodeIds,
                        MaterialisedEntityId = pfd.MaterialisedEntityId,
                        FleetId             = pfd.FleetId,  // null for old brain docs — LogId falls back to DeriveId
                        ForcedRatio         = pfd.ForcedRatio,
                        ForcedRatioCause    = pfd.ForcedRatioCause,
                        FerryRole           = pfd.FerryRole,
                        RelocationRole      = pfd.RelocationRole,
                        IsProcurement       = pfd.IsProcurement,
                        ShipPrefabId        = pfd.ShipPrefabId,
                        CargoCapacityLiters = pfd.CargoCapacityLiters,
                        Squadron            = NormalizeSquadron(pfd.Squadron),
                        EscortEntityIds     = NormalizeEscortIds(pfd.EscortEntityIds),
                        // Read back as written, empty list included: an empty SurvivingHulls means
                        // every member was lost, which is not the same as the null "none lost yet".
                        SurvivingHulls      = pfd.SurvivingHulls,
                        HullIntegrity       = pfd.HullIntegrity,
                        Fight               = FightProgressDoc.To(pfd.Fight),
                        HullsMeasured       = pfd.HullsMeasured,
                        HullsSettled        = pfd.HullsSettled,
                        Intercepted         = false,  // transient — intercepted fleets are cleaned up before flush
                        LastResolvedSegment = -1    // transient — fast-forwarded by ReconcileAndResolveOnLoad
                    });
                    // Integrity that does not line up with the hulls it describes cannot be mapped
                    // to them; the fleet loads whole rather than guessing.
                    {
                        int last = s.PendingFleets.Count - 1;
                        PendingFleetAction loaded = s.PendingFleets[last];
                        if (loaded.HullIntegrity != null)
                        {
                            loaded.HullIntegrity = NormalizeHullIntegrity(loaded.HullIntegrity, loaded.HullSlotCount);
                            s.PendingFleets[last] = loaded;
                        }
                    }
                }
            // A brain can hold distinct fleets under one id; each keeps its hull and cargo, and gets
            // an id of its own before the host keys anything on it.
            PendingFleetAction.RemintDuplicateIds(s.PendingFleets);

            if (d.Sources != null)
                foreach (SourceDoc sd in d.Sources)
                {
                    SpatialNode n = graph.Add(new Vec3(sd.X, sd.Y, sd.Z), sd.ResourceValue, sd.Id);
                    n.Richness = sd.Richness;
                    // Absent in JSON written before FAC-261, which loads as 0 -- a quiet node, and
                    // the same state every node had on every boot before threat was saved at all.
                    n.Threat = sd.Threat;
                    n.OwnerFactionTag = d.Tag;
                    n.HoldStrength = sd.Strength;
                    // sd.Radius is read and dropped, like sd.Kind above: radius is derived from what
                    // the node IS (InfluenceRadius), and a persisted one would pin it to the day it
                    // was saved. Old saves still carry the field; new ones stop writing it. FAC-250.
                    // Backward-compat: if StableStationId is absent in old JSON (defaults to 0),
                    // derive it from the E### id string so materialization works without a re-scan.
                    n.StableStationId = sd.StableStationId;
                    if (n.StableStationId == 0)
                    {
                        long derivedId;
                        if (SourceMapping.TryGetStationId(sd.Id, out derivedId)) n.StableStationId = derivedId;
                    }
                    n.AsteroidFieldSeed = sd.AsteroidFieldSeed;
                    n.StationPrefabId = sd.StationPrefabId;
                    n.PendingStationPrefabId = sd.PendingStationPrefabId;
                    n.StationBookValue = sd.StationBookValue;
                    n.StationWeapons = sd.StationWeapons;
                    n.DisplayName = sd.DisplayName;
                    n.GpsAnchor = n.Position;  // same position, named for CrunchEcon consumers
                    // sd.Kind is read and dropped. A node's kind is derived from its deposit and its
                    // site by ReclassifyNodes, which runs once every brain is loaded; a persisted
                    // ordinal is whatever a retired die rolled and must not outrank the world. See
                    // FAC-102.
                    n.Deposit = FromDepositDoc(sd.Deposit);
                    // Points with no entries beside them is a sample that found nothing, and it has
                    // to come back as an empty deposit rather than as none: "nobody looked" is what
                    // puts a node back in the backfill queue and reads as unsurveyed to a player.
                    // Neither field = never sampled, which is the answer this preserves.
                    if (n.Deposit == null && sd.DepositPoints.HasValue && sd.DepositPoints.Value > 0)
                        n.Deposit = new NodeDeposit();
                    if (n.Deposit != null && sd.DepositPoints.HasValue)
                        n.Deposit.PointsExamined = sd.DepositPoints.Value;
                    if (n.Deposit != null)
                        n.Deposit.Units = sd.DepositUnits.HasValue ? sd.DepositUnits.Value : 0;
                    // Scan-backed nodes are real physical assets; treat missing LastReinforcedUtc as
                    // "just discovered" so they aren't immediately drained of hold strength on first load.
                    n.SafeZoneDownUntilUtc = sd.SafeZoneDownUntilUtc;
                    n.SafeZoneDownByTag = sd.SafeZoneDownByTag;
                    n.ContestedUntilUtc = sd.ContestedUntilUtc;
                    n.ContestedByTag = sd.ContestedByTag;
                    n.LastPlayerAggressionUtc = sd.LastPlayerAggressionUtc;
                    if (sd.Siege != null) sd.Siege.ApplyTo(n);
                    else n.ZoneIntegrity = -1;   // never besieged; the field's own "full" sentinel
                    n.LastReinforcedUtc = (sd.LastReinforcedUtc != DateTime.MinValue)
                        ? sd.LastReinforcedUtc
                        : (SourceMapping.IsScanBacked(sd.Id) ? DateTime.UtcNow : DateTime.MinValue);
                    // No scan-backed fallback here (unlike LastReinforcedUtc above) -- "never had Haul
                    // traffic" is the correct default for every node, old or new.
                    n.AbandonedUtc        = sd.AbandonedUtc;
                    // Absent in JSON written before ship activity shipped: loads at zero credit,
                    // which is the correct default for every node, old or new.
                    n.ShipActivityCredits = sd.ShipActivityCredits;
                    n.ShipActivityAtUtc   = sd.ShipActivityAtUtc;
                    n.LastHaulDepartedUtc = sd.LastHaulDepartedUtc;
                    n.LastHaulArrivedUtc  = sd.LastHaulArrivedUtc;
                    n.LastMinedUtc        = sd.LastMinedUtc;
                    n.MineCooldownUntilUtc = sd.MineCooldownUntilUtc;
                    if (sd.DemandHistory != null)
                        foreach (var kv in sd.DemandHistory)
                            if (kv.Value > 0) n.DemandHistory[kv.Key] = kv.Value;
                    if (sd.SalesHistory != null)
                        foreach (var kv in sd.SalesHistory)
                            if (kv.Value > 0) n.SalesHistory[kv.Key] = kv.Value;
                    if (sd.FuelBurnEma != null)
                        foreach (var kv in sd.FuelBurnEma)
                            if (kv.Value > 0) n.FuelBurnEma[kv.Key] = kv.Value;
                    n.FuelBurnEmaAtUtc = sd.FuelBurnEmaAtUtc;
                    if (sd.MinedEma != null)
                        foreach (var kv in sd.MinedEma)
                            if (kv.Value > 0) n.MinedEma[kv.Key] = kv.Value;
                    n.MinedEmaAtUtc = sd.MinedEmaAtUtc;
                    if (sd.Workers > 0)
                    {
                        n.Workers = sd.Workers;
                        n.WorkersAtUtc = sd.WorkersAtUtc;
                        n.WorkerFoodBank = Math.Max(0.0, sd.WorkerFoodBank);
                    }
                    if (sd.Inventory != null)
                    {
                        // DeserializeCargo migrates old ItemKind keys → SubTypeId; new saves pass through
                        Dictionary<string, long> inv = DeserializeCargo(sd.Inventory);
                        if (inv != null)
                            foreach (var kv in inv)
                                if (kv.Value > 0) n.Inventory[kv.Key] = kv.Value;
                    }
                    if (sd.ReservedInventory != null)
                    {
                        Dictionary<string, long> reserved = DeserializeCargo(sd.ReservedInventory);
                        if (reserved != null)
                            foreach (var kv in reserved)
                                if (kv.Value > 0) n.ReservedInventory[kv.Key] = kv.Value;
                    }
                    n.StationMunitions = SerializeCargo(sd.StationMunitions);
                    n.StationLoot = SerializeCargo(sd.StationLoot);
                    n.LootRestockUtc = sd.LootRestockUtc;
                    if (sd.Capabilities != null)
                    {
                        foreach (var kv in sd.Capabilities)
                        {
                            NodeCapabilityKind cap;
                            if (kv.Value > 0 && Enum.TryParse<NodeCapabilityKind>(kv.Key, out cap))
                                n.Capabilities[cap] = kv.Value;
                        }
                    }
                    if (isOldShapeDoc)
                    {
                        // Old shape: ShipStock/ShipPrefabQueue were role-keyed. Merge every role
                        // bucket's ships into the unified prefab-keyed pool. ShipPrefabQueue is the
                        // source of truth for exact prefab identity; StoreSmall/StoreLarge entries
                        // also seed ListedForSale so sale-reserved stock survives migrated and
                        // still protected. Any count on ShipStock[role] beyond what ShipPrefabQueue
                        // actually lists (legacy bookkeeping drift, or docs old enough to predate
                        // ShipPrefabQueue entirely) becomes unknown-prefab ("") stock rather than
                        // being silently dropped.
                        if (sd.ShipStock != null)
                            foreach (var kv in sd.ShipStock)
                            {
                                if (kv.Value <= 0) continue;
                                string role = kv.Key;
                                bool isStoreRole = role == "StoreSmall" || role == "StoreLarge";
                                List<string> prefabIds = null;
                                bool hasQueue = sd.ShipPrefabQueue != null
                                    && sd.ShipPrefabQueue.TryGetValue(role, out prefabIds)
                                    && prefabIds != null && prefabIds.Count > 0;

                                int migrated = 0;
                                if (hasQueue)
                                {
                                    foreach (string pid in prefabIds)
                                    {
                                        MigrateOneShip(n, pid ?? "", isStoreRole);
                                        migrated++;
                                    }
                                }
                                int unknown = kv.Value - migrated;
                                for (int i = 0; i < unknown; i++) MigrateOneShip(n, "", isStoreRole);
                            }
                    }
                    else
                    {
                        if (sd.ShipStock != null)
                            foreach (var kv in sd.ShipStock)
                                if (kv.Value > 0) n.ShipStock[kv.Key] = kv.Value;
                        if (sd.ListedForSale != null)
                            foreach (var kv in sd.ListedForSale)
                                if (kv.Value > 0) n.ListedForSale[kv.Key] = kv.Value;
                    }

                    // Unknown-prefab ("") stock is permanently undispatchable and unsellable —
                    // ShipPool's suitability checks (cargo/weapon bands/grid size) all default to
                    // zero for an unresolvable prefab id, so nothing ever selects it. Rather than
                    // let it sit forever as dead inventory (its main historical source: the
                    // 2026-08-01 ship-pool-unification migration above, when legacy role-counted
                    // stock outran what ShipPrefabQueue could identify), drop it on every load.
                    n.ShipStock.Remove("");
                    n.ListedForSale.Remove("");

                    n.ShipyardPaused = sd.ShipyardPaused;
                    n.BuildSeq = sd.BuildSeq;
                    LoadDamagedHulls(n, sd.DamagedHulls);

                    if (sd.BuildQueue != null)
                        foreach (BuildQueueEntryDoc bqd in sd.BuildQueue)
                        {
                            // A build named by hull has no role but always an id; a doc with
                            // neither is nothing anything can address.
                            if (string.IsNullOrEmpty(bqd.Role) && string.IsNullOrEmpty(bqd.Id)) continue;
                            var entry = new BuildQueueEntry
                            {
                                Id         = bqd.Id ?? bqd.Role,
                                Role       = string.IsNullOrEmpty(bqd.Role) ? null : bqd.Role,
                                PrefabId   = bqd.PrefabId,
                                Priority   = bqd.Priority,
                                CreatedUtc = bqd.CreatedUtc,
                                ExpiresUtc = bqd.ExpiresUtc,
                                // A doc written before the store roles were retired carries the
                                // purpose in its Role string and no flag; recover it there so an
                                // in-flight shelf build keeps reserving its hull across the upgrade.
                                DestinedForSale = bqd.DestinedForSale
                                                  || IsLegacyStoreRole(bqd.Role),
                                PriorityByPlayer = bqd.PriorityByPlayer,
                                StalledSeconds = bqd.StalledSeconds,
                                ServicedSeconds = bqd.ServicedSeconds,
                                // LastServicedUtc is deliberately NOT persisted: it is a baseline for
                                // a delta that is clamped anyway, and leaving it default means the
                                // first post-restart sweep contributes 0 rather than the whole
                                // downtime. That is exactly the overnight-shutdown case.

                            };
                            if (bqd.Bom != null)
                                foreach (var kv in bqd.Bom) if (kv.Value > 0) entry.Bom[kv.Key] = kv.Value;
                            if (bqd.Collected != null)
                                foreach (var kv in bqd.Collected) if (kv.Value > 0) entry.Collected[kv.Key] = kv.Value;
                            if (bqd.ReservedInputs != null)
                                foreach (var kv in bqd.ReservedInputs) if (kv.Value > 0) entry.ReservedInputs[kv.Key] = kv.Value;
                            if (bqd.RecipeProgress != null)
                                foreach (BuildRecipeProgressDoc rp in bqd.RecipeProgress)
                                    if (!string.IsNullOrEmpty(rp.BlueprintId) && rp.Accum > 0)
                                        entry.RecipeProgress[rp.BlueprintId] = new BuildRecipeSlot { Accum = rp.Accum, LastTick = rp.LastTickUtc };
                            n.BuildQueue.Add(entry);
                        }

                    // A doc written before BuildSeq existed carries 0, and its entry ids were minted
                    // from the queue COUNT -- so minting from 0 would hand a live id straight back
                    // out. Raise the counter past every id the node already holds. Cheap, and it also
                    // repairs a doc whose seq was written before an entry was cancelled.
                    foreach (BuildQueueEntry existing in n.BuildQueue)
                    {
                        int seq = SpatialNode.ParseBuildEntrySeq(existing.Id);
                        if (seq > n.BuildSeq) n.BuildSeq = seq;
                    }

                    // And repair a collision the count-based minting could already have written: two
                    // entries sharing an id resolve to the same build everywhere the production side
                    // addresses one by (nodeId, entryId), so the later of the pair is re-minted.
                    var seenBuildIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (BuildQueueEntry existing in n.BuildQueue)
                        if (string.IsNullOrEmpty(existing.Id) || !seenBuildIds.Add(existing.Id))
                        {
                            existing.Id = n.MintBuildEntryId(existing.Role);
                            seenBuildIds.Add(existing.Id);
                        }

                    foreach (BuildQueueEntry existing in n.BuildQueue)
                    {
                        int keys;
                        long discarded = ClampCollectedToBom(existing, out keys);
                        if (discarded > 0 && log != null)
                            log.Add(s.Tag + " BUILD_COLLECTED_CLAMPED node=" + n.Id + " entry=" + existing.Id
                                    + " prefab=" + (existing.PrefabId ?? "(free)")
                                    + " discarded=" + discarded.ToString(CultureInfo.InvariantCulture)
                                    + " keys=" + keys.ToString(CultureInfo.InvariantCulture));
                    }
                }
            return s;
        }
    }
}
