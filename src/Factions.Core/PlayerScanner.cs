using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Factions.Core
{
    public enum ScannerContactKind
    {
        Station,
        Outpost,
        OreField,
        RareOreField,
        IceField,
        Refinery,
        FuelDepot,
        Fleet,
        /// <summary>A raid that has reached its target and is fighting there right now
        /// (<see cref="FleetStopKind.Engage"/>). Detected by the ordinary sensor rule like any other
        /// fleet — it simply reads very loud, because <c>FleetSensing</c> gives the dwell a large
        /// excitation and a floor (<see cref="FactionTuning.SignatureEngagementFloor"/>). So a better
        /// scanner is worth MORE against a raid than against traffic, rather than being bypassed by a
        /// special case; and a stealthy approach stays possible while a stealthy fight does not.
        /// Appended, so an older client renders it as a generic marker rather than losing the
        /// feed.</summary>
        RaidEngagement
    }

    public struct ScannerFleetCandidate
    {
        public string Key;
        public Vec3 PositionKm;
        /// <summary>Instantaneous velocity along the current leg, km per minute, sampled from the
        /// live acceleration profile rather than re-derived from it. Lets a client dead-reckon
        /// between scanner passes without carrying a copy of the easing curve.</summary>
        public Vec3 VelocityKmPerMin;
        /// <summary>End of the current leg, so dead reckoning can be clamped there.</summary>
        public Vec3 TargetPosKm;
        public long EntityId;
        /// <summary>Faction that owns the fleet. Resolved to a player-relative relationship by the
        /// host, which is the only tier that can see SE faction standings.</summary>
        public string OwnerTag;
        /// <summary>How loud this convoy is, from <see cref="FleetSensing.SignatureOf"/>. Computed
        /// once per fleet by the host rather than once per viewer.
        ///
        /// <para>Zero means "not supplied" and is read as the reference 1.0, not as silence — the
        /// same "unknown, do not gate" convention <see cref="FleetSensing.SignatureOf"/> uses for an
        /// unscanned prefab. A caller that does not set it gets the flat range this branch had
        /// before signature existed.</para></summary>
        public double Signature;
        /// <summary>Radius the materialised hull is announcing itself over, km — a working antenna
        /// with broadcasting on, read from the lead grid. Zero for an abstract fleet, which has no
        /// grid and therefore nothing for SE's own HUD to draw (FAC-140).</summary>
        public double BroadcastRadiusKm;
        /// <summary>The node this fleet is currently fighting over, when it is on its
        /// <see cref="FleetStopKind.Engage"/> dwell; null for every other fleet and every other leg.
        /// Non-null is what promotes the contact to a <see cref="ScannerContactKind.RaidEngagement"/>
        /// broadcast, and names the place in its label.</summary>
        public string EngagementTargetNodeId;
        /// <summary>The convoy's mission, as an <see cref="ActionKind"/> ordinal. Carried so the
        /// HUD can draw the same mission silhouette the map does — <see cref="ScannerContactKind"/>
        /// answers what sort of contact this is, not what it is doing, and the two are separate
        /// axes on purpose.</summary>
        public byte MissionKind;
        /// <summary>Hulls in the convoy, for the strength mark above the marker. Zero means a
        /// legacy fleet that never wrote a size, and reads as one.</summary>
        public int ConvoySize;
        /// <summary>How the squadron is fighting (<see cref="Factions.Core.TacticHint.Phrase"/>), appended to the
        /// contact's label; null while it is not flying a combat manoeuvre.</summary>
        public string TacticHint;
    }

    public struct ScannerContact
    {
        public string Key;
        public ScannerContactKind Kind;
        public string Label;
        /// <summary>The node's own name, by the map's rule (<see cref="PlayerScanner.NodeName"/>);
        /// null for a fleet and for a node with none. Separate from <see cref="Label"/>, which stays
        /// the class: the client shows the name only on request, and the unlinked-player GPS
        /// fallback keeps its marker name abstract.</summary>
        public string DisplayName;
        public Vec3 PositionKm;
        /// <summary>Non-zero only for a contact that moves. A node is anchored, so the client is
        /// told nothing about its motion and draws it exactly where the position says.</summary>
        public Vec3 VelocityKmPerMin;
        /// <summary>End of the current leg, paired with <see cref="VelocityKmPerMin"/>.</summary>
        public Vec3 TargetPosKm;
        public bool Moving;
        public long EntityId;
        public bool Unclaimed;
        /// <summary>Owning faction tag, or the neutral tag when claimable. Carried as a plain
        /// string so Core stays SE-free; the host turns it into a per-player relationship.</summary>
        public string OwnerTag;
        /// <summary>The convoy's mission, as an <see cref="ActionKind"/> ordinal; zero for every
        /// contact that is not a fleet. Drives the HUD silhouette, which is the same table the map
        /// marker uses.</summary>
        public byte FleetKind;
        /// <summary>Hulls in the convoy; zero for a contact that is not a fleet, and for a legacy
        /// fleet that never wrote a size.</summary>
        public int ConvoySize;
    }

    public sealed class ScannerContactDelta
    {
        public readonly List<ScannerContact> NewContacts = new List<ScannerContact>();
        public readonly List<ScannerContact> UpdatedContacts = new List<ScannerContact>();
        public readonly List<string> RemovedKeys = new List<string>();
    }

    /// <summary>Runtime-only opaque identifiers for scanner GPS transport. Tokens are random,
    /// stable for one contact membership lifetime, and reveal no domain key material.</summary>
    public sealed class ScannerOpaqueTokenRegistry
    {
        private readonly Dictionary<string, string> _byKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _tokens =
            new HashSet<string>(StringComparer.Ordinal);

        public int Count { get { return _byKey.Count; } }

        public string GetOrCreate(string key)
        {
            key = key ?? "";
            string token;
            if (_byKey.TryGetValue(key, out token)) return token;
            do { token = "FSCAN:" + Guid.NewGuid().ToString("N"); }
            while (!_tokens.Add(token));
            _byKey[key] = token;
            return token;
        }

        public bool Remove(string key)
        {
            string token;
            if (!_byKey.TryGetValue(key ?? "", out token)) return false;
            _byKey.Remove(key ?? "");
            _tokens.Remove(token);
            return true;
        }

        public void Clear()
        {
            _byKey.Clear();
            _tokens.Clear();
        }
    }

    /// <summary>Tracks membership of one player's current scanner sphere. Existing contacts can be
    /// refreshed silently; only contacts absent from the previous pass are classified as new.</summary>
    public sealed class ScannerContactTracker
    {
        private Dictionary<string, ScannerContact> _contacts =
            new Dictionary<string, ScannerContact>(StringComparer.Ordinal);

        public ScannerContactDelta Reconcile(IEnumerable<ScannerContact> desired)
        {
            var delta = new ScannerContactDelta();
            var next = new Dictionary<string, ScannerContact>(StringComparer.Ordinal);
            if (desired != null)
            {
                foreach (ScannerContact contact in desired)
                {
                    if (string.IsNullOrEmpty(contact.Key) || next.ContainsKey(contact.Key)) continue;
                    next[contact.Key] = contact;
                    if (_contacts.ContainsKey(contact.Key)) delta.UpdatedContacts.Add(contact);
                    else delta.NewContacts.Add(contact);
                }
            }
            foreach (string key in _contacts.Keys)
                if (!next.ContainsKey(key)) delta.RemovedKeys.Add(key);
            _contacts = next;
            return delta;
        }

        public void Clear()
        {
            _contacts.Clear();
        }
    }

    public enum ScannerGpsUpdateMode
    {
        Add,
        Refresh,
        Modify
    }

    public static class PlayerScanner
    {
        public static bool IsTemporaryGps(ScannerContactKind kind)
        {
            return kind != ScannerContactKind.Fleet;
        }

        public static ScannerGpsUpdateMode SelectGpsUpdateMode(
            bool hasExistingHash, int existingHash, int nextHash)
        {
            if (!hasExistingHash) return ScannerGpsUpdateMode.Add;
            return existingHash == nextHash
                ? ScannerGpsUpdateMode.Refresh
                : ScannerGpsUpdateMode.Modify;
        }

        /// <summary>Stable scanner identity for a fleet. Current fleets use their persisted FleetId.
        /// Legacy fleets predate that field, so include the mission fields that differentiated
        /// same-tick Hauls (origin and item manifest) instead of collapsing them by actor/tick.</summary>
        public static string FleetContactKey(PendingFleetAction fleet)
        {
            if (!string.IsNullOrEmpty(fleet.FleetId)) return fleet.FleetId;
            var key = new StringBuilder();
            key.Append(fleet.ActorTag).Append('|').Append(fleet.Kind).Append('|')
               .Append(fleet.DepartedUtc.Ticks).Append('|').Append(fleet.OriginNodeId).Append('|')
               .Append(fleet.TargetNodeId).Append('|').Append(fleet.TargetTag).Append('|')
               .Append(fleet.PickupItemKey);
            if (fleet.Cargo != null && fleet.Cargo.Count > 0)
            {
                var itemKeys = new List<string>(fleet.Cargo.Keys);
                itemKeys.Sort(StringComparer.Ordinal);
                foreach (string itemKey in itemKeys)
                    key.Append('|').Append(itemKey).Append('=').Append(fleet.Cargo[itemKey]);
            }
            return key.ToString();
        }

        public static IList<ScannerContact> Resolve(
            int level,
            Vec3 originKm,
            double rangeKm,
            IEnumerable<SpatialNode> nodes,
            IEnumerable<ScannerFleetCandidate> fleets,
            FactionTuning tuning = null,
            IItemPriceCatalogue prices = null,
            IItemRegistry registry = null,
            SensorOcclusion occlusion = null)
        {
            var result = new List<ScannerContact>();
            if (level < 1 || rangeKm <= 0) return result;
            double rangeSq = rangeKm * rangeKm;

            if (nodes != null)
            {
                foreach (SpatialNode node in nodes)
                {
                    if (node == null || string.IsNullOrEmpty(node.Id) || NodeContent.IsEmptySpace(node)) continue;
                    if (DistanceSquared(originKm, node.Position) > rangeSq) continue;
                    // A station behind a planet is not on the HUD. Tested after the range compare, so
                    // the geometry only runs for what was about to become a contact.
                    if (occlusion != null && !occlusion.IsClear(originKm, node.Position)) continue;
                    ScannerContactKind kind;
                    string label;
                    Classify(node, tuning, prices, registry, out kind, out label);
                    bool unclaimed = NeutralFaction.IsClaimable(node.OwnerFactionTag);
                    if (unclaimed) label += "(free)";
                    result.Add(new ScannerContact
                    {
                        Key = "node:" + node.Id,
                        Kind = kind,
                        Label = label,
                        DisplayName = NodeName(node),
                        PositionKm = node.Position,
                        Unclaimed = unclaimed,
                        OwnerTag = node.OwnerFactionTag
                    });
                }
            }

            if (fleets != null)
            {
                foreach (ScannerFleetCandidate fleet in fleets)
                {
                    if (string.IsNullOrEmpty(fleet.Key)) continue;

                    // Range scales with what is flying: the same rule the map's fleet fog applies,
                    // so one convoy cannot be a HUD contact and absent from the map at one distance.
                    // A raid fighting at its target reaches further through this same rule rather than
                    // around it -- FleetSensing gives the dwell a large excitation and a floor, so a
                    // fight is loud and a better scanner is worth MORE against it, not bypassed by it.
                    double signature = fleet.Signature > 0 ? fleet.Signature : 1.0;
                    if (!Sensing.Detects(originKm, rangeKm, fleet.PositionKm, signature,
                                         fleet.BroadcastRadiusKm, occlusion)) continue;

                    // Engaging is a different thing to see than a convoy under way: it is parked, it
                    // names the place it is fighting over, and it is the one contact a player can act
                    // on inside a known window. Presentation only -- visibility above is the same rule.
                    bool engaging = !string.IsNullOrEmpty(fleet.EngagementTargetNodeId);
                    result.Add(new ScannerContact
                    {
                        Key = "fleet:" + fleet.Key,
                        Kind = engaging ? ScannerContactKind.RaidEngagement : ScannerContactKind.Fleet,
                        Label = TacticHint.ContactLabel(
                            engaging ? "Raid: " + PlaceName(fleet.EngagementTargetNodeId, nodes) : "Fleet",
                            fleet.TacticHint),
                        PositionKm = fleet.PositionKm,
                        VelocityKmPerMin = engaging ? new Vec3() : fleet.VelocityKmPerMin,
                        TargetPosKm = engaging ? new Vec3() : fleet.TargetPosKm,
                        Moving = !engaging,
                        EntityId = fleet.EntityId,
                        OwnerTag = fleet.OwnerTag,
                        // Passed through unfiltered, deliberately. Whether a viewer has earned the
                        // right to read another faction's intent is a disclosure question, it is
                        // open on both surfaces rather than only this one, and it is answered in
                        // one place for both or not at all (FAC-373).
                        FleetKind = fleet.MissionKind,
                        ConvoySize = fleet.ConvoySize
                    });
                }
            }
            return result;
        }

        /// <summary>What a scanner contact reads as. Asked of the ground and the station rather than
        /// of a node label — <c>NodeKind</c> is gone, and its <c>Refinery</c>/<c>FuelDepot</c> arms
        /// with it: those were capabilities wearing a label, and nothing derives them from a node any
        /// more. Both members stay in <see cref="ScannerContactKind"/> for the client's sake; they are
        /// simply never produced. See FAC-102.</summary>
        private static void Classify(SpatialNode node, FactionTuning tuning, IItemPriceCatalogue prices,
            IItemRegistry registry, out ScannerContactKind kind, out string label)
        {
            bool station = node.StableStationId > 0 || !string.IsNullOrEmpty(node.StationPrefabId);
            if (station)
            {
                kind = node.Zone == NodeZone.Surface ? ScannerContactKind.Outpost : ScannerContactKind.Station;
                label = kind == ScannerContactKind.Outpost ? "Outpost" : "Station";
                return;
            }

            if (NodeContent.IsIce(node, tuning))
            {
                kind = ScannerContactKind.IceField; label = "IceField"; return;
            }
            if (NodeContent.IsRare(node, tuning, prices, registry))
            {
                kind = ScannerContactKind.RareOreField; label = "RareOreField"; return;
            }
            if (NodeContent.IsMineable(node))
            {
                kind = ScannerContactKind.OreField; label = "OreField"; return;
            }
            kind = ScannerContactKind.Outpost; label = "Outpost";
        }

        /// <summary>A node's name as the map shows it: none when the name is empty or is only the
        /// node's id (<c>MapWorld.MarkDistinctNames</c> labels neither), so the two surfaces name
        /// the same nodes. FAC-143.</summary>
        public static string NodeName(SpatialNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.DisplayName) || node.DisplayName == node.Id)
                return null;
            return node.DisplayName;
        }

        /// <summary>A node's own name for a label, falling back to its id. The graph is already in
        /// hand for the node sweep above, so this needs no host round trip -- and an engagement
        /// broadcast is very often about a node the viewer has never been near, whose id would mean
        /// nothing to them.</summary>
        private static string PlaceName(string nodeId, IEnumerable<SpatialNode> nodes)
        {
            if (nodes != null)
                foreach (SpatialNode n in nodes)
                    if (n != null && n.Id == nodeId)
                        return string.IsNullOrEmpty(n.DisplayName) ? nodeId : n.DisplayName;
            return nodeId;
        }

        private static double DistanceSquared(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    public sealed class ScannerProfileStore
    {
        private const int SchemaVersion = 1;
        private readonly string _path;
        private readonly int _defaultLevel;
        private readonly Dictionary<string, ScannerProfileDoc> _profiles =
            new Dictionary<string, ScannerProfileDoc>(StringComparer.Ordinal);
        private bool _dirty;

        public ScannerProfileStore(string storageDir, int defaultLevel)
        {
            _path = Path.Combine(storageDir, "scanner_profiles.json");
            _defaultLevel = Math.Max(0, defaultLevel);
            Load();
        }

        public int GetLevel(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return 0;
            ScannerProfileDoc profile;
            if (_profiles.TryGetValue(steamId, out profile)) return Math.Max(0, profile.Level);
            _profiles[steamId] = new ScannerProfileDoc { Level = _defaultLevel };
            _dirty = true;
            return _defaultLevel;
        }

        public void SetLevel(string steamId, int level)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            int repaired = Math.Max(0, level);
            ScannerProfileDoc profile;
            if (_profiles.TryGetValue(steamId, out profile) && profile.Level == repaired) return;
            _profiles[steamId] = new ScannerProfileDoc { Level = repaired };
            _dirty = true;
        }

        public void SaveIfDirty()
        {
            if (!_dirty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + ".tmp";
            var file = new ScannerProfileFile { SchemaVersion = SchemaVersion, Profiles = _profiles };
            File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented));
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
            try
            {
                if (!File.Exists(_path)) return;
                ScannerProfileFile file = JsonConvert.DeserializeObject<ScannerProfileFile>(File.ReadAllText(_path));
                if (file == null || file.Profiles == null) return;
                foreach (KeyValuePair<string, ScannerProfileDoc> pair in file.Profiles)
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                        _profiles[pair.Key] = new ScannerProfileDoc { Level = Math.Max(0, pair.Value.Level) };
            }
            catch
            {
                _profiles.Clear();
            }
        }

        private sealed class ScannerProfileFile
        {
            public int SchemaVersion { get; set; }
            public Dictionary<string, ScannerProfileDoc> Profiles { get; set; }
        }

        private sealed class ScannerProfileDoc
        {
            public int Level { get; set; }
        }
    }
}
