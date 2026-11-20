using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How loud a thing is to a sensor. Dimensionless and clamped: 1.0 is the reference object, and
    /// the bounds exist so nothing is ever invisible and nothing is ever visible across the system.
    ///
    /// <para>Every input is already scanned from real prefab data by <see cref="IPrefabCatalogue"/>,
    /// so this adds no host work. The shape (exponents and weights) is a model and lives here as
    /// constants; the ranges it multiplies are balance and live in <see cref="FactionTuning"/>.</para>
    /// </summary>
    public static class Signature
    {
        /// <summary>The quietest anything can be. Deliberately low enough that the mass term keeps
        /// discriminating all the way down: at 0.25 it truncated 32 of the 292 catalogue hulls to one
        /// identical value, so a 3,221 kg container and an 8,481 kg drone were equally quiet. At 0.10
        /// only two hulls clamp, and both are 130 kg piston tops rather than ships.</summary>
        public const double Min = 0.10;

        /// <summary>Upper bound for <b>node</b> signatures, and the conservative radius
        /// <c>NodeProximityIndex</c> is sized against.
        ///
        /// <para><b>Ships are deliberately not bounded by this.</b> The model's reference hull is
        /// 500 t, and real player ships run 10,000 t and up — twenty times the reference and beyond —
        /// so a bound sized to the NPC catalogue sits far inside the player distribution. A 50,000 t
        /// capital at 40,000 DPS scores 8.9 at rest and 26.7 while jumping; clamping that to 4
        /// deletes most of the effect. See <see cref="FactionTuning.SignatureShipCeiling"/>, which is
        /// unbounded by default.</para></summary>
        public const double Max = 4.0;

        /// <summary>Dry mass of the object the formula calls "1.0". A vanilla large-grid hauler is
        /// the intended reference point.</summary>
        public const double ReferenceMassKg = 500000.0;

        /// <summary>Mass moves signature sub-linearly -- a hull ten times heavier is loud, not ten
        /// times louder.</summary>
        public const double MassExponent = 0.35;

        public const double WeaponWeight = 0.30;

        /// <summary>Armament of the object the weapon term calls "1.0" — the median armed hull,
        /// 2,286 DPS across the 195 armed hulls of the 344-prefab catalogue (2026-09-17). A median
        /// warship keeps the flat 1.30 the term used to apply to everything armed, so this is a
        /// spread around that value rather than a shift of it.</summary>
        public const double ReferenceDps = 2300.0;

        /// <summary>Armament, like mass, moves signature sub-linearly. The catalogue spans 21.6 to
        /// 90,635 DPS — a factor of 4,196 — and a cube root keeps the whole weapon term inside
        /// 1.06 .. 2.02, so a gunboat is louder than a freighter without armament outweighing hull
        /// mass. At a square root the top hull's weapon term alone was 3.3x.</summary>
        public const double WeaponExponent = 1.0 / 3.0;

        public const double CargoWeight = 0.25;

        /// <summary>Signature for a ship or convoy.
        /// <paramref name="weaponDps"/> is summed armament; 0 is unarmed and costs nothing. It moves
        /// signature by magnitude rather than by a flag, so a gunboat and a battleship no longer read
        /// the same -- see <see cref="ReferenceDps"/> for why a median warship still lands where the
        /// old flat multiplier put it.
        /// <paramref name="cargoFill"/> is 0..1 of capacity. <paramref name="convoySize"/> counts
        /// hulls; it contributes as a square root, because ten ships are not ten times as loud as one.
        /// <paramref name="stateMultiplier"/> is the "running dark" hook and is 1.0 today -- nothing
        /// yet modulates it, and the model is complete without it.</summary>
        public static double ForShip(
            double dryMassKg, double weaponDps, double cargoFill, int convoySize, double stateMultiplier)
        {
            return ForShip(dryMassKg, weaponDps, cargoFill, convoySize, stateMultiplier, 0.0);
        }

        /// <summary>As above, with an optional upper bound.
        ///
        /// <para><b>There is no default ceiling, and that is deliberate.</b> The mass and armament
        /// exponents already compress a 90,000x mass span and a 4,196x armament span,
        /// and they flatten pathological input on their own — a modded 100-kilotonne hull reaches only
        /// 6.4, a weapon framework misreporting a million DPS only 3.3. What a ceiling would actually
        /// cut is the legitimate top of the player distribution: a big ship under jump should light up
        /// like a pulsar and be seen at interplanetary range, which is the mechanic, not an overflow.</para>
        ///
        /// <para><paramref name="ceiling"/> at or below zero means unbounded. The floor always applies:
        /// nothing is ever completely invisible.</para></summary>
        public static double ForShip(
            double dryMassKg, double weaponDps, double cargoFill, int convoySize, double stateMultiplier,
            double ceiling)
        {
            double mass = dryMassKg > 0 ? Math.Pow(dryMassKg / ReferenceMassKg, MassExponent) : 1.0;
            double factor = mass;
            if (weaponDps > 0)
                factor *= 1.0 + WeaponWeight * Math.Pow(weaponDps / ReferenceDps, WeaponExponent);
            factor *= 1.0 + CargoWeight * Clamp01(cargoFill);
            if (convoySize > 1) factor *= Math.Sqrt(convoySize);
            if (stateMultiplier > 0) factor *= stateMultiplier;

            if (factor < Min) return Min;
            if (ceiling > 0 && factor > ceiling) return ceiling;
            return factor;
        }

        /// <summary>Signature for a node. A station is loud and it does not move: you do not sneak a
        /// refinery past a picket. An undeveloped field is quieter but never silent.</summary>
        public static double ForNode(SpatialNode node)
        {
            if (node == null) return Min;
            bool station = node.StableStationId > 0 || !string.IsNullOrEmpty(node.StationPrefabId);
            double factor = station ? 2.0 : 0.75;
            if (node.Capabilities != null && node.Capabilities.Count > 0) factor *= 1.25;
            return Clamp(factor);
        }

        public static double Clamp(double factor)
        {
            if (factor < Min) return Min;
            if (factor > Max) return Max;
            return factor;
        }

        private static double Clamp01(double v)
        {
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }
    }

    /// <summary>
    /// The detection test itself: one multiply, one distance compare, and — when the caller supplies
    /// a <see cref="SensorOcclusion"/> — whether a planet stands in the way.
    ///
    /// <para>Both fleet consumers now run through here -- the map's fog and
    /// <see cref="PlayerScanner"/>'s fleet branch -- so one convoy cannot be a HUD contact and
    /// absent from the map at the same distance. <see cref="PlayerScanner"/>'s <b>node</b> branch is
    /// still a flat sphere: a node's signature barely varies, and converting it would mostly just
    /// widen station detection.</para>
    /// </summary>
    public static class Sensing
    {
        /// <summary>Range at which this sensor detects a target of that signature.
        ///
        /// <para>Only the floor is applied here. The <b>ceiling belongs to whoever produced the
        /// signature</b> — <see cref="Signature.ForNode"/> bounds itself, and
        /// <see cref="Signature.ForShip"/> is deliberately unbounded so a jumping capital is seen at the
        /// range it earns. Re-clamping at the consumer would have silently capped every ship at
        /// <see cref="Signature.Max"/> no matter what the model said.</para></summary>
        public static double DetectionRangeKm(double sensorStrengthKm, double signatureFactor)
        {
            if (sensorStrengthKm <= 0 || signatureFactor <= 0) return 0;
            double factor = signatureFactor < Signature.Min ? Signature.Min : signatureFactor;
            return sensorStrengthKm * factor;
        }

        /// <summary>As above, floored at what the target is announcing about itself.
        ///
        /// <para>SE's own antenna channel draws a broadcasting grid on every receiver inside its
        /// radius, and that radius is the <b>target's</b> setting, not the observer's. We neither own
        /// that channel nor can suppress it, so a model that detects such a grid at less than its own
        /// broadcast radius is not applying fog — it is contradicting what is already on screen.
        /// This is a floor and never a cap: a loud hull is still found far beyond what it broadcasts
        /// (FAC-140).</para>
        ///
        /// <para><paramref name="targetBroadcastKm"/> at or below zero is "not broadcasting", which
        /// is also what an unsupplied value reads as — the floor then does nothing and the range is
        /// the sensor's own.</para></summary>
        public static double DetectionRangeKm(
            double sensorStrengthKm, double signatureFactor, double targetBroadcastKm)
        {
            double range = DetectionRangeKm(sensorStrengthKm, signatureFactor);
            // A sensor that cannot detect the reference object detects nothing, broadcast or not:
            // the floor answers "how far does this carry", not "is anyone listening".
            if (sensorStrengthKm <= 0) return 0;
            return targetBroadcastKm > range ? targetBroadcastKm : range;
        }

        public static bool Detects(
            Vec3 sensorPosKm, double sensorStrengthKm, Vec3 targetPosKm, double signatureFactor)
        {
            return Detects(sensorPosKm, sensorStrengthKm, targetPosKm, signatureFactor, 0.0, null);
        }

        /// <summary>As above, with the target's own broadcast radius as a floor. See
        /// <see cref="DetectionRangeKm(double,double,double)"/>.</summary>
        public static bool Detects(
            Vec3 sensorPosKm, double sensorStrengthKm, Vec3 targetPosKm, double signatureFactor,
            double targetBroadcastKm)
        {
            return Detects(sensorPosKm, sensorStrengthKm, targetPosKm, signatureFactor,
                           targetBroadcastKm, null);
        }

        /// <summary>As above, with a planet able to stand in the way.
        ///
        /// <para><b>Occlusion blinds the sensor half and never the broadcast floor.</b> A grid
        /// announcing itself is drawn on SE's own HUD by a channel we neither own nor can suppress,
        /// and it does no line-of-sight test — so hiding such a grid behind a body would contradict
        /// what is already on screen rather than apply fog (FAC-140). Everything the sensor earns on
        /// its own strength is blocked.</para>
        ///
        /// <para>The line is tested only once the target is in range at all: a pair that fails the
        /// distance test needs no geometry, which is what keeps the sweep's cost where it was.</para>
        ///
        /// <para>A null <paramref name="occlusion"/> is a clear sky, so every caller that has no
        /// planet catalogue behaves exactly as it did before occlusion existed.</para></summary>
        public static bool Detects(
            Vec3 sensorPosKm, double sensorStrengthKm, Vec3 targetPosKm, double signatureFactor,
            double targetBroadcastKm, SensorOcclusion occlusion)
        {
            double range = DetectionRangeKm(sensorStrengthKm, signatureFactor);
            double broadcast = sensorStrengthKm > 0 && targetBroadcastKm > 0 ? targetBroadcastKm : 0;
            double outer = range > broadcast ? range : broadcast;
            if (outer <= 0) return false;

            double distanceSq = DistanceSquared(sensorPosKm, targetPosKm);
            if (distanceSq > outer * outer) return false;
            if (broadcast > 0 && distanceSq <= broadcast * broadcast) return true;
            if (occlusion != null && !occlusion.IsClear(sensorPosKm, targetPosKm)) return false;
            return distanceSq <= range * range;
        }

        /// <summary>Sensor strength of an owned node: a passive envelope every station has, widened
        /// by an installed <see cref="NodeCapabilityKind.SensorArray"/>. Owning a node is not the
        /// same as watching the space around it -- that gap is what the capability is for.</summary>
        public static double NodeStrengthKm(SpatialNode node, FactionTuning t)
        {
            if (node == null || t == null) return 0;
            double strength = t.SensorNodePassiveRangeKm;
            int level;
            if (node.Capabilities != null
                && node.Capabilities.TryGetValue(NodeCapabilityKind.SensorArray, out level)
                && level > 0)
            {
                strength += t.SensorArrayRangeKmPerLevel * level;
            }
            return strength;
        }

        /// <summary>Squared distance from a point to the segment ab -- the exact test behind a swept
        /// sensor. Degenerates to point-to-point when the segment has no length, which is the normal
        /// case for a fleet that has not moved between two samples.</summary>
        public static double DistanceToSegmentSquared(Vec3 p, Vec3 a, Vec3 b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double lengthSq = abx * abx + aby * aby + abz * abz;
            if (lengthSq <= 0) return DistanceSquared(p, a);
            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby + (p.Z - a.Z) * abz) / lengthSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double dx = p.X - (a.X + abx * t);
            double dy = p.Y - (a.Y + aby * t);
            double dz = p.Z - (a.Z + abz * t);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>Whether a sensor sweeping from <paramref name="fromKm"/> to <paramref name="toKm"/>
        /// detects the target at any point along the way.</summary>
        public static bool DetectsSwept(
            Vec3 fromKm, Vec3 toKm, double sensorStrengthKm, Vec3 targetPosKm, double signatureFactor)
        {
            return DetectsSwept(fromKm, toKm, sensorStrengthKm, targetPosKm, signatureFactor, null);
        }

        /// <summary>As above, with a planet able to stand in the way. The line is taken from the point
        /// on the leg where the sensor came closest to the target — the point the range test itself
        /// answered on.</summary>
        public static bool DetectsSwept(
            Vec3 fromKm, Vec3 toKm, double sensorStrengthKm, Vec3 targetPosKm, double signatureFactor,
            SensorOcclusion occlusion)
        {
            double range = DetectionRangeKm(sensorStrengthKm, signatureFactor);
            if (range <= 0) return false;
            if (DistanceToSegmentSquared(targetPosKm, fromKm, toKm) > range * range) return false;
            return occlusion == null || occlusion.IsClearFromSegment(fromKm, toKm, targetPosKm);
        }

        public static double DistanceSquared(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    /// <summary>
    /// One observer's persisted memory of the world: node id -> when it was last detected.
    ///
    /// <para>Nodes only. A remembered fleet has moved, so the memory would be worthless, and
    /// admitting fleets here is what would make the store unbounded.</para>
    ///
    /// <para><b>Expiry is evaluated at read, never by a sweep</b> -- the same absolute-deadline idiom
    /// <see cref="SpatialNode.MineCooldownUntilUtc"/> uses. <see cref="Compact"/> exists only to keep
    /// the persisted file from carrying entries nothing can read any more; it is never what makes an
    /// entry expire. The retention bound on this collection is
    /// <see cref="FactionTuning.DiscoveryHorizon"/>, and the size bound is the node count.</para>
    /// </summary>
    /// <summary>
    /// One entry in a discovered set: when the node was last detected, and -- when the detection
    /// carried it -- what was true about the node at that moment.
    ///
    /// <para><b>The snapshot is what makes a belief able to be wrong.</b> Identity and position are
    /// static and are read live off the graph; owner, hold and resource value are not, and a faction
    /// acting on the live values of a node it has not looked at in a week is omniscient in the only
    /// way that matters. A raider striking where your defence <i>used to be</i> needs this.</para>
    ///
    /// <para><see cref="HasDetail"/> is false for an entry that was recorded without one -- a brain
    /// written before this existed, or a player scanner contact, which stamps the timestamp only. The
    /// node is still known; its detail is read live until the next detection fills this in. That
    /// self-heals within one sweep, and it is why the flag is here rather than a null owner tag doing
    /// double duty.</para>
    /// </summary>
    public struct KnownNode
    {
        public DateTime LastSeenUtc;
        public bool HasDetail;
        public string OwnerTag;
        public double HoldStrength;
        public double ResourceValue;
    }

    public sealed class FactionKnowledge
    {
        private readonly Dictionary<string, KnownNode> _entries =
            new Dictionary<string, KnownNode>(StringComparer.Ordinal);

        public int Count { get { return _entries.Count; } }

        /// <summary>Node id -> when it was last detected. Kept as the timestamp-only view because
        /// every caller that counts, compacts or persists entries wants exactly that; the detail
        /// hangs off <see cref="Snapshots"/>.</summary>
        public IEnumerable<KeyValuePair<string, DateTime>> Entries
        {
            get
            {
                foreach (KeyValuePair<string, KnownNode> pair in _entries)
                    yield return new KeyValuePair<string, DateTime>(pair.Key, pair.Value.LastSeenUtc);
            }
        }

        public IEnumerable<KeyValuePair<string, KnownNode>> Snapshots { get { return _entries; } }

        /// <summary>Records a detection that carries what was seen. The first creates the entry;
        /// every later one renews it and overwrites the detail with the fresher observation.</summary>
        public void Touch(SpatialNode node, DateTime utc)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) return;
            KnownNode existing;
            if (_entries.TryGetValue(node.Id, out existing) && existing.LastSeenUtc >= utc) return;
            _entries[node.Id] = new KnownNode
            {
                LastSeenUtc = utc,
                HasDetail = true,
                OwnerTag = node.OwnerFactionTag,
                HoldStrength = node.HoldStrength,
                ResourceValue = node.ResourceValue,
            };
        }

        /// <summary>Records a detection that carries only a timestamp -- the player scanner's shape,
        /// where the contact is resolved for a HUD rather than for a brain. Never moves a timestamp
        /// backwards, so an out-of-order replay cannot un-learn a node, and never discards detail an
        /// earlier detection captured.</summary>
        public void Touch(string nodeId, DateTime utc)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            KnownNode existing;
            if (_entries.TryGetValue(nodeId, out existing))
            {
                if (existing.LastSeenUtc >= utc) return;
                existing.LastSeenUtc = utc;
                _entries[nodeId] = existing;
                return;
            }
            _entries[nodeId] = new KnownNode { LastSeenUtc = utc, HasDetail = false };
        }

        /// <summary>True while the entry exists and has not lapsed. A non-positive horizon is treated
        /// as "no expiry" rather than "expire everything": a tuning file that never carried the key
        /// must not silently blind every faction.</summary>
        public bool HasLive(string nodeId, DateTime nowUtc, TimeSpan horizon)
        {
            KnownNode entry;
            if (string.IsNullOrEmpty(nodeId)) return false;
            if (!_entries.TryGetValue(nodeId, out entry)) return false;
            if (horizon <= TimeSpan.Zero) return true;
            return nowUtc - entry.LastSeenUtc < horizon;
        }

        public bool TryGetLastSeen(string nodeId, out DateTime lastSeenUtc)
        {
            KnownNode entry;
            if (_entries.TryGetValue(nodeId ?? "", out entry)) { lastSeenUtc = entry.LastSeenUtc; return true; }
            lastSeenUtc = default(DateTime);
            return false;
        }

        /// <summary>The last-known detail for a node, if the entry is live and carries any. Expiry is
        /// applied here too: a lapsed entry is forgotten, so it must not answer with stale detail.</summary>
        public bool TryGetSnapshot(string nodeId, DateTime nowUtc, TimeSpan horizon, out KnownNode snapshot)
        {
            snapshot = default(KnownNode);
            if (string.IsNullOrEmpty(nodeId)) return false;
            KnownNode entry;
            if (!_entries.TryGetValue(nodeId, out entry)) return false;
            if (horizon > TimeSpan.Zero && nowUtc - entry.LastSeenUtc >= horizon) return false;
            if (!entry.HasDetail) return false;
            snapshot = entry;
            return true;
        }

        /// <summary>Drops lapsed entries. Returns how many went. Purely a file-size measure -- calling
        /// it or not calling it cannot change what <see cref="HasLive"/> answers.</summary>
        public int Compact(DateTime nowUtc, TimeSpan horizon)
        {
            if (horizon <= TimeSpan.Zero || _entries.Count == 0) return 0;
            List<string> expired = null;
            foreach (KeyValuePair<string, KnownNode> pair in _entries)
            {
                if (nowUtc - pair.Value.LastSeenUtc < horizon) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
            }
            if (expired == null) return 0;
            for (int i = 0; i < expired.Count; i++) _entries.Remove(expired[i]);
            return expired.Count;
        }

        public void Clear()
        {
            _entries.Clear();
        }

        public void LoadFrom(IEnumerable<KeyValuePair<string, DateTime>> entries)
        {
            _entries.Clear();
            if (entries == null) return;
            foreach (KeyValuePair<string, DateTime> pair in entries)
                if (!string.IsNullOrEmpty(pair.Key))
                    _entries[pair.Key] = new KnownNode { LastSeenUtc = pair.Value, HasDetail = false };
        }

        public void LoadFrom(IEnumerable<KeyValuePair<string, KnownNode>> entries)
        {
            _entries.Clear();
            if (entries == null) return;
            foreach (KeyValuePair<string, KnownNode> pair in entries)
                if (!string.IsNullOrEmpty(pair.Key)) _entries[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// What an observer knows, read at query time from three independent clauses. Nothing here is a
    /// seeding pass: a faction's opening knowledge is a *floor computed on read*, so it stays correct
    /// as territory changes instead of being frozen by whatever was true at genesis.
    /// </summary>
    public static class KnowledgeView
    {
        /// <summary>Ownership implies discovery, and so does adjacency to what you own.
        ///
        /// <para>This is the genesis seed and slice C's bootstrap, and it is deliberately one rule:
        /// on a geometrically-built graph a neighbour is often a nearby rival station, so early
        /// contact arises from the map rather than from a special case, while distant factions stay
        /// unknown until someone flies there. Without the neighbour clause
        /// <see cref="SpatialGraph.Frontier"/> is empty and Expand can never fire.</para></summary>
        public static bool IsBirthright(
            SpatialGraph graph, string ownerTag, string nodeId, double rangeKm)
        {
            if (graph == null || string.IsNullOrEmpty(ownerTag) || string.IsNullOrEmpty(nodeId)) return false;
            SpatialNode node;
            if (!graph.Nodes.TryGetValue(nodeId, out node) || node == null) return false;
            if (node.OwnerFactionTag == ownerTag) return true;
            if (rangeKm <= 0) return false;
            double rangeSq = rangeKm * rangeKm;
            for (int i = 0; i < node.Neighbors.Count; i++)
            {
                SpatialNode neighbour;
                if (!graph.Nodes.TryGetValue(node.Neighbors[i], out neighbour) || neighbour == null) continue;
                if (neighbour.OwnerFactionTag != ownerTag) continue;
                if (Sensing.DistanceSquared(node.Position, neighbour.Position) <= rangeSq) return true;
            }
            return false;
        }

        public static bool IsDiscovered(
            SpatialGraph graph, string ownerTag, FactionKnowledge knowledge,
            string nodeId, DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm)
        {
            if (knowledge != null && knowledge.HasLive(nodeId, nowUtc, horizon)) return true;
            return IsBirthright(graph, ownerTag, nodeId, birthrightRangeKm);
        }

        /// <summary>A viewer's knowledge is the union of every store they can draw on, plus the
        /// birthright of the faction they belong to.
        ///
        /// <para>A player passes their own store and their faction's; a solo player passes only their
        /// own and a null <paramref name="ownerTag"/>. <b>The union is computed at read, never
        /// merged into a store.</b> That is what makes joining a faction grant its map and leaving
        /// take it back while the player keeps what they personally detected -- and it is why
        /// join-learn-leave is not an exploit here: there is no write to walk away with.</para></summary>
        public static bool IsDiscoveredBy(
            SpatialGraph graph, string ownerTag, IList<FactionKnowledge> sources,
            string nodeId, DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm)
        {
            if (sources != null)
                for (int i = 0; i < sources.Count; i++)
                    if (sources[i] != null && sources[i].HasLive(nodeId, nowUtc, horizon)) return true;
            return IsBirthright(graph, ownerTag, nodeId, birthrightRangeKm);
        }

        public static HashSet<string> KnownNodeIdsFor(
            SpatialGraph graph, string ownerTag, IList<FactionKnowledge> sources,
            DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm)
        {
            var known = KnownNodeIds(graph, ownerTag, null, nowUtc, horizon, birthrightRangeKm);
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    if (sources[i] == null) continue;
                    foreach (KeyValuePair<string, DateTime> pair in sources[i].Entries)
                        if (horizon <= TimeSpan.Zero || nowUtc - pair.Value < horizon) known.Add(pair.Key);
                }
            }
            known.Remove(null);
            known.Remove("");
            return known;
        }

        /// <summary>Every node id the observer knows right now. Materialised for callers that need the
        /// whole set; the per-node predicate above is the cheaper path for a single question.</summary>
        public static HashSet<string> KnownNodeIds(
            SpatialGraph graph, string ownerTag, FactionKnowledge knowledge,
            DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (graph != null && !string.IsNullOrEmpty(ownerTag))
            {
                double rangeSq = birthrightRangeKm > 0 ? birthrightRangeKm * birthrightRangeKm : 0;
                foreach (SpatialNode node in graph.Nodes.Values)
                {
                    if (node == null || node.OwnerFactionTag != ownerTag) continue;
                    known.Add(node.Id);
                    if (rangeSq <= 0) continue;
                    for (int i = 0; i < node.Neighbors.Count; i++)
                    {
                        SpatialNode neighbour;
                        if (!graph.Nodes.TryGetValue(node.Neighbors[i], out neighbour) || neighbour == null) continue;
                        if (Sensing.DistanceSquared(node.Position, neighbour.Position) <= rangeSq)
                            known.Add(neighbour.Id);
                    }
                }
            }
            if (knowledge != null)
            {
                foreach (KeyValuePair<string, DateTime> pair in knowledge.Entries)
                    if (horizon <= TimeSpan.Zero || nowUtc - pair.Value < horizon) known.Add(pair.Key);
            }
            known.Remove(null);
            known.Remove("");
            return known;
        }
    }

    /// <summary>
    /// A node as an observer currently believes it to be. Identity and position come off the live
    /// graph -- they are static, and a remembered position would be the same position. Owner, hold
    /// and resource value come from the last detection when the observer has one, which is what lets
    /// a faction be <i>wrong</i> about a rival it has not looked at recently.
    /// </summary>
    public struct NodeBelief
    {
        public SpatialNode Node;
        public string OwnerTag;
        public double HoldStrength;
        public double ResourceValue;

        /// <summary>True when these values are the world's own rather than a memory of it: an owned
        /// node, an ungated read, or a known node no detection has yet captured detail for.</summary>
        public bool IsLive;

        public string Id { get { return Node == null ? null : Node.Id; } }
    }

    /// <summary>
    /// The gate slice C turns on. One place decides what a faction may consider and what values it
    /// reads while considering it, so a caller cannot accidentally take the omniscient path.
    ///
    /// <para><b>With <see cref="FactionTuning.KnowledgeGatedBrains"/> off, every node is yielded with
    /// live values</b> -- byte-for-byte the behaviour before this existed, which is what makes the
    /// flag a real revert rather than a different code path that hopefully agrees.</para>
    /// </summary>
    public static class KnowledgeGate
    {
        /// <summary>What the observer may consider, and what it believes about each. Yields owned
        /// nodes too: callers filter by owner as they always did, and an observer that could not see
        /// its own estate here would be a different bug in every one of them.</summary>
        public static IEnumerable<NodeBelief> Beliefs(
            SpatialGraph graph, string ownerTag, FactionKnowledge knowledge,
            DateTime nowUtc, FactionTuning tuning)
        {
            if (graph == null) yield break;
            bool gated = tuning != null && tuning.KnowledgeGatedBrains;
            TimeSpan horizon = tuning == null ? TimeSpan.Zero : tuning.DiscoveryHorizon;
            double birthright = tuning == null ? 0 : tuning.BirthrightRangeKm;

            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null) continue;
                NodeBelief belief;
                if (!gated) { belief = Live(node); yield return belief; continue; }
                if (TryBelief(graph, ownerTag, knowledge, node, nowUtc, horizon, birthright, out belief))
                    yield return belief;
            }
        }

        /// <summary>One node, gated. False means Unknown -- the observer has no business considering
        /// it at all, which is a stronger statement than "considers it worthless".</summary>
        public static bool TryBelief(
            SpatialGraph graph, string ownerTag, FactionKnowledge knowledge, SpatialNode node,
            DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm, out NodeBelief belief)
        {
            return TryBelief(graph, ownerTag, knowledge, null, node, nowUtc, horizon, birthrightRangeKm, out belief);
        }

        /// <summary>As above for a viewer who draws on several stores -- a player, whose own
        /// detections and whose faction's are unioned at read and never merged
        /// (<see cref="KnowledgeView.IsDiscoveredBy"/>).
        ///
        /// <para>The freshest detail wins. Two stores can both remember a node from different
        /// moments, and the later observation is the one the viewer actually has; picking the first
        /// store in the list would make the answer depend on the order they were assembled in.</para></summary>
        public static bool TryBelief(
            SpatialGraph graph, string ownerTag, IList<FactionKnowledge> sources, SpatialNode node,
            DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm, out NodeBelief belief)
        {
            return TryBelief(graph, ownerTag, null, sources, node, nowUtc, horizon, birthrightRangeKm, out belief);
        }

        /// <summary>The one body behind both overloads. <paramref name="knowledge"/> and
        /// <paramref name="sources"/> are the same question asked with one store or several; a single
        /// store is not wrapped in a list because brains call this per node per think.</summary>
        private static bool TryBelief(
            SpatialGraph graph, string ownerTag, FactionKnowledge knowledge, IList<FactionKnowledge> sources,
            SpatialNode node, DateTime nowUtc, TimeSpan horizon, double birthrightRangeKm, out NodeBelief belief)
        {
            belief = default(NodeBelief);
            if (node == null) return false;

            // Ownership is checked first and answers live: you know your own estate accurately, and
            // no detection or timestamp is involved. Without this an owned node could expire.
            if (!string.IsNullOrEmpty(ownerTag) && node.OwnerFactionTag == ownerTag)
            {
                belief = Live(node);
                return true;
            }

            KnownNode snapshot;
            bool remembered = knowledge != null
                ? knowledge.TryGetSnapshot(node.Id, nowUtc, horizon, out snapshot)
                : TryFreshestSnapshot(sources, node.Id, nowUtc, horizon, out snapshot);
            if (remembered)
            {
                belief = new NodeBelief
                {
                    Node = node,
                    OwnerTag = snapshot.OwnerTag,
                    HoldStrength = snapshot.HoldStrength,
                    ResourceValue = snapshot.ResourceValue,
                    IsLive = false,
                };
                return true;
            }

            // Known, but with no detail to remember: the birthright floor, or an entry recorded
            // before snapshots existed. Live values are the best available answer, and the next
            // detection replaces them with a real memory.
            bool known = sources != null
                ? KnowledgeView.IsDiscoveredBy(graph, ownerTag, sources, node.Id, nowUtc, horizon, birthrightRangeKm)
                : KnowledgeView.IsDiscovered(graph, ownerTag, knowledge, node.Id, nowUtc, horizon, birthrightRangeKm);
            if (known)
            {
                belief = Live(node);
                return true;
            }
            return false;
        }

        private static bool TryFreshestSnapshot(
            IList<FactionKnowledge> sources, string nodeId, DateTime nowUtc, TimeSpan horizon,
            out KnownNode freshest)
        {
            freshest = default(KnownNode);
            if (sources == null) return false;
            bool have = false;
            for (int i = 0; i < sources.Count; i++)
            {
                KnownNode snapshot;
                if (sources[i] == null) continue;
                if (!sources[i].TryGetSnapshot(nodeId, nowUtc, horizon, out snapshot)) continue;
                if (have && snapshot.LastSeenUtc <= freshest.LastSeenUtc) continue;
                freshest = snapshot;
                have = true;
            }
            return have;
        }

        private static NodeBelief Live(SpatialNode node)
        {
            return new NodeBelief
            {
                Node = node,
                OwnerTag = node.OwnerFactionTag,
                HoldStrength = node.HoldStrength,
                ResourceValue = node.ResourceValue,
                IsLive = true,
            };
        }
    }

    /// <summary>
    /// The write side. Detection is the only thing that ever writes a discovered entry, and these are
    /// the two places detection happens.
    ///
    /// <para>Fleets are sensors but never targets here: a fleet is never entered into a discovered
    /// set, so the only thing a sweep can produce is node knowledge. That is what keeps this bounded
    /// -- the expensive hunter-versus-prey scan belongs to the tactical tier, not to slice A.</para>
    /// </summary>
    public static class KnowledgeSweep
    {
        /// <summary>Every owned node is a sensor. Returns how many entries were written or renewed.
        ///
        /// <para>Cost is bounded twice over: the index restricts each query to nearby cells, and the
        /// query radius is the sensor's range against the *loudest possible* signature, so the
        /// broad-phase is a conservative superset that the exact per-node test then filters.</para></summary>
        public static int SweepOwnedNodes(
            SpatialGraph graph, NodeProximityIndex index, FactionState state,
            FactionTuning tuning, DateTime nowUtc, SensorOcclusion occlusion = null)
        {
            if (graph == null || index == null || state == null || tuning == null) return 0;
            var candidates = new List<SpatialNode>();
            int touched = 0;
            foreach (SpatialNode sensor in graph.Nodes.Values)
            {
                if (sensor == null || sensor.OwnerFactionTag != state.Tag) continue;
                double strength = Sensing.NodeStrengthKm(sensor, tuning);
                if (strength <= 0) continue;
                candidates.Clear();
                index.QueryWithin(graph, sensor.Position, strength * Signature.Max, candidates);
                for (int i = 0; i < candidates.Count; i++)
                {
                    SpatialNode target = candidates[i];
                    if (target == null || NodeContent.IsEmptySpace(target)) continue;
                    if (target.OwnerFactionTag == state.Tag) continue;   // already known by ownership
                    if (!Sensing.Detects(sensor.Position, strength, target.Position,
                                         Signature.ForNode(target), 0.0, occlusion))
                        continue;
                    state.Knowledge.Touch(target, nowUtc);
                    touched++;
                }
            }
            return touched;
        }

        /// <summary>A fleet surveys what it passes, along the whole leg it flew since the previous
        /// sweep -- not merely where it happened to be when sampled.
        ///
        /// <para>Sampling the point was the original shape and it did not work: at the shipped
        /// defaults a fleet covers 4500 km between sweeps (1500 km/min against the 180 s global pass)
        /// while its sensor sees 60 km across, so it surveyed 1.33% of a route and found things
        /// essentially only at its destination. Sweeping the segment makes coverage independent of
        /// cadence, which is the property that matters: no sweep interval anyone would choose can
        /// out-run a fleet, so the fix cannot be a faster tick.</para>
        ///
        /// <para><paramref name="fromKm"/> is derived by the caller from the fleet's own progress one
        /// sweep ago, so nothing new is persisted and a restart simply re-sweeps a segment it already
        /// swept -- idempotent, because Touch only ever renews.</para></summary>
        public static int SweepFleetSegment(
            SpatialGraph graph, NodeProximityIndex index, FactionState state,
            Vec3 fromKm, Vec3 toKm, FactionTuning tuning, DateTime nowUtc,
            List<SpatialNode> scratch = null, HashSet<string> scratchSeen = null,
            SensorOcclusion occlusion = null)
        {
            if (graph == null || index == null || state == null || tuning == null) return 0;
            double strength = tuning.SensorFleetRangeKm;
            if (strength <= 0) return 0;
            List<SpatialNode> candidates = scratch ?? new List<SpatialNode>();
            candidates.Clear();
            index.QueryWithinSegment(graph, fromKm, toKm, strength * Signature.Max, candidates, scratchSeen);
            int touched = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                SpatialNode target = candidates[i];
                if (target == null || NodeContent.IsEmptySpace(target)) continue;
                if (target.OwnerFactionTag == state.Tag) continue;
                if (!Sensing.DetectsSwept(fromKm, toKm, strength, target.Position,
                                          Signature.ForNode(target), occlusion))
                    continue;
                state.Knowledge.Touch(target, nowUtc);
                touched++;
            }
            return touched;
        }
    }
}
