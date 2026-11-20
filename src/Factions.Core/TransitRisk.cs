using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a carrier's route through hostile space is expected to cost it: the slice 5 catch
    /// (<see cref="FleetTrespass"/>) read forward along a planned polyline instead of at the fleet's
    /// current position.
    ///
    /// <para><b>The same rules, applied ahead.</b> A stretch of the route counts where the faction whose
    /// space it is stands below the war line toward the carrier's faction with no pact
    /// (<see cref="FleetTrespass.IsHostile"/>), is not a player faction, and would detect the carrier there
    /// at its own signature. The stay is clocked as <see cref="TrespassDwellBook"/> clocks it: a gap in
    /// detection shorter than <see cref="TrespassPolicy.IntruderMemory"/> keeps the clock running, a
    /// longer one restarts it. The route is caught where that clock reaches the
    /// <see cref="TrespassLadder.ShadowSeconds"/> the owner would apply at that point.</para>
    ///
    /// <para><b>What a catch costs.</b> The convoy verdict the catch would apply
    /// (<see cref="PendingFleetResolver.ClassifyConvoy"/>), against the strongest defence the owner can
    /// field at the catch node or in its region: weaker loses <see cref="FactionTuning.RaidLootFraction"/>
    /// of the cargo and the hulls at <see cref="FactionTuning.ConvoyWeakHullLossChance"/>; equal is a duel
    /// read as even odds, losing at <see cref="FactionTuning.ConvoyDuelHullLossChance"/>; stronger loses
    /// nothing. Scaled by <see cref="FactionTuning.TransitCatchOdds"/>, the chance a Guard is standing at
    /// the node when the clock runs out. Nothing is caught where the owner fields nothing.</para>
    ///
    /// <para>Off while <see cref="WorldModel.FleetTrespassOn"/> is off: without the catch there is no
    /// risk to price.</para></summary>
    public static class TransitRisk
    {
        /// <summary>One continuous stay in one hostile faction's space along a route.</summary>
        public struct Crossing
        {
            public string OwnerTag;
            /// <summary>The owner's node nearest the point the stay is caught, or nearest where it
            /// ended when it is not caught: the node whose Guard makes the catch.</summary>
            public string NodeId;
            /// <summary>The campaign key of <see cref="NodeId"/>: its region, or the node itself in
            /// open space (<c>FactionBrain.CampaignKeyFor</c>).</summary>
            public string RegionKey;
            public double Km;
            /// <summary>Flight time from entering the space to leaving it, gaps included.</summary>
            public double Seconds;
            /// <summary>The shortest shadow time the owner applied along the stay.</summary>
            public double ShadowSeconds;
            public bool Caught;
            /// <summary>What the owner can field at <see cref="NodeId"/>: the larger of the node's own
            /// defence and its region's strongest node.</summary>
            public double CatcherDps;
        }

        /// <summary>Sampling step along a route, the step the zone walk uses.</summary>
        public const double SampleStepKm = LegImpulseProfile.SampleStepKm;

        /// <summary>Sample budget for one route; a longer route samples coarser.</summary>
        public const int MaxSamples = 256;

        /// <summary>Whether routes are priced at all: the catch is on and the host has handed the
        /// world its faction roster.</summary>
        public static bool Applies(WorldModel world)
        {
            return world != null && world.FleetTrespassOn && world.TransitStates != null
                   && world.Influence is InfluenceField;
        }

        /// <summary>Every hostile stay along <paramref name="polylineKm"/> for <paramref name="fleet"/>,
        /// flown in <paramref name="legMinutes"/>. Empty for a fleet the catch never takes as a convoy
        /// (<see cref="FleetTrespass.CatchOf"/>), a player faction's fleet, and a route through no
        /// hostile space.</summary>
        public static List<Crossing> Assess(WorldModel world, FactionTuning t, PendingFleetAction fleet,
                                            IList<Vec3> polylineKm, double legMinutes)
        {
            var result = new List<Crossing>();
            if (!Applies(world) || t == null || polylineKm == null || polylineKm.Count < 2) return result;
            if (FleetTrespass.CatchOf(fleet) != FleetTrespass.CatchKind.Convoy) return result;

            FactionState actor = StateOf(world.TransitStates, fleet.ActorTag);
            if (actor == null || actor.IsPlayerFaction) return result;

            var field = (InfluenceField)world.Influence;
            List<SpatialNode> reaching = field.NodesReaching(polylineKm);
            var hostile = new Dictionary<string, FactionState>(StringComparer.Ordinal);
            for (int i = 0; i < reaching.Count; i++)
            {
                string tag = reaching[i].OwnerFactionTag;
                if (hostile.ContainsKey(tag)) continue;
                FactionState owner = StateOf(world.TransitStates, tag);
                hostile[tag] = owner != null && !owner.IsPlayerFaction && FleetTrespass.IsHostile(owner, actor, t)
                    ? owner : null;
            }
            bool anyHostile = false;
            foreach (var kv in hostile) if (kv.Value != null) { anyHostile = true; break; }
            if (!anyHostile) return result;

            double totalKm = FuelMeter.PolylineLengthKm(polylineKm);
            if (totalKm <= 0) return result;
            double secondsPerKm = Math.Max(0, legMinutes) * 60.0 / totalKm;
            double stepKm = Math.Max(SampleStepKm, totalKm / MaxSamples);
            double memoryS = TrespassPolicy.IntruderMemory.TotalSeconds;

            DateTime now = world.UtcNow;
            double signature = FleetSensing.SignatureOf(fleet, world.PrefabCatalogue, world.ItemVolumes, t, now);
            var sensorsOf = new Dictionary<string, List<SensorSource>>(StringComparer.Ordinal);
            var scratch = new Dictionary<string, double>(StringComparer.Ordinal);
            var open = new Dictionary<string, Stay>(StringComparer.Ordinal);
            var seenHere = new List<string>(2);

            for (int seg = 0; seg < polylineKm.Count - 1; seg++)
            {
                Vec3 a = polylineKm[seg], b = polylineKm[seg + 1];
                double segKm = a.DistanceTo(b);
                if (segKm <= 0) continue;
                int steps = Math.Max(1, (int)Math.Ceiling(segKm / stepKm));
                double sampleKm = segKm / steps;
                double dt = sampleKm * secondsPerKm;

                for (int step = 0; step < steps; step++)
                {
                    double f = (step + 0.5) / steps;
                    var pos = new Vec3(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
                    seenHere.Clear();

                    string top; double topVal, secondVal;
                    field.DominantAt(pos, reaching, scratch, out top, out topVal, out secondVal);
                    FactionState owner;
                    if (top != null && topVal > 0 && topVal >= t.TradeReachThreshold
                        && hostile.TryGetValue(top, out owner) && owner != null
                        && DetectedBy(world, t, owner, sensorsOf, pos, signature))
                    {
                        seenHere.Add(top);
                        Stay stay;
                        if (!open.TryGetValue(top, out stay))
                        {
                            stay = new Stay { Owner = owner, ShadowSeconds = double.MaxValue };
                            open[top] = stay;
                        }
                        stay.Seconds += stay.GapSeconds + dt;
                        stay.GapSeconds = 0;
                        stay.Km += sampleKm;
                        stay.LastPos = pos;

                        double control = topVal + secondVal <= 1e-9 ? 1.0 : topVal / (topVal + secondVal);
                        Personality personality = Personalities.Get(owner.PersonalityId);
                        double shadow = TrespassLadder.ShadowSeconds(personality.Traits.Aggression, control,
                            FleetTrespass.RepFor(owner.RelationTo(actor.Tag), t.HostileRelationThreshold),
                            TrespassLadder.StartsHostile(personality, null));
                        if (shadow < stay.ShadowSeconds) stay.ShadowSeconds = shadow;
                        if (!stay.Caught && stay.Seconds >= shadow) { stay.Caught = true; stay.CatchPos = pos; }
                    }

                    if (open.Count == 0) continue;
                    List<string> closed = null;
                    foreach (var kv in open)
                    {
                        if (seenHere.Contains(kv.Key)) continue;
                        kv.Value.GapSeconds += dt;
                        if (kv.Value.GapSeconds > memoryS)
                        {
                            if (closed == null) closed = new List<string>();
                            closed.Add(kv.Key);
                        }
                    }
                    if (closed == null) continue;
                    for (int i = 0; i < closed.Count; i++)
                    {
                        result.Add(Close(world, t, open[closed[i]], actor.Tag));
                        open.Remove(closed[i]);
                    }
                }
            }
            foreach (var kv in open) result.Add(Close(world, t, kv.Value, actor.Tag));
            return result;
        }

        /// <summary>Expected credits lost to one stay: nothing unless it is caught and the owner fields
        /// something there.</summary>
        public static double LossCredits(Crossing c, double cargoCredits, double hullCredits, double convoyDps,
                                         FactionTuning t)
        {
            if (!c.Caught || c.CatcherDps <= 0 || t == null) return 0;
            double cargo = Math.Max(0, cargoCredits), hulls = Math.Max(0, hullCredits);
            string verdict = PendingFleetResolver.ClassifyConvoy(convoyDps, c.CatcherDps, t);
            double loss;
            if (verdict == "stronger") loss = 0;
            else if (verdict == "equal") loss = 0.5 * (t.RaidLootFraction * cargo + t.ConvoyDuelHullLossChance * hulls);
            else loss = t.RaidLootFraction * cargo + t.ConvoyWeakHullLossChance * hulls;
            return Math.Max(0, t.TransitCatchOdds) * loss;
        }

        /// <summary>Expected credits lost over every stay, never more than the cargo and the hulls.</summary>
        public static double RiskCredits(IList<Crossing> crossings, double cargoCredits, double hullCredits,
                                         double convoyDps, FactionTuning t)
        {
            if (crossings == null) return 0;
            double sum = 0;
            for (int i = 0; i < crossings.Count; i++)
                sum += LossCredits(crossings[i], cargoCredits, hullCredits, convoyDps, t);
            double cap = Math.Max(0, cargoCredits) + Math.Max(0, hullCredits);
            return sum > cap ? cap : sum;
        }

        /// <summary>The fleet's hulls at their component cost.</summary>
        public static double HullCredits(PendingFleetAction fleet, IPrefabCatalogue catalogue)
        {
            if (catalogue == null) return 0;
            double sum = 0;
            foreach (string hull in FuelMeter.HullsOf(fleet))
                if (!string.IsNullOrEmpty(hull)) sum += Math.Max(0, catalogue.Cost(hull));
            return sum;
        }

        /// <summary>The fleet's firepower as the convoy verdict reads it.</summary>
        public static double ConvoyDps(PendingFleetAction fleet, IPrefabCatalogue catalogue)
        {
            return ShipPool.TotalDps(PendingFleetResolver.EffectiveBands(fleet, catalogue));
        }

        /// <summary>The defence <paramref name="owner"/> can field at <paramref name="node"/>: the node's
        /// own (station weapons and fieldable parked hulls) or its region's strongest node, whichever is
        /// larger, since a Guard answering a mark can come from either.</summary>
        public static double CatcherDps(WorldModel world, FactionTuning t, SpatialNode node, string actorTag)
        {
            if (world == null || node == null) return 0;
            double own = 0;
            if (node.StationWeapons != null)
                for (int i = 0; i < node.StationWeapons.Count; i++) own += node.StationWeapons[i].Dps;
            if (world.PrefabCatalogue != null)
                own += ShipPool.TotalDps(CombatStrength.ParkedDefenceBands(node, world.PrefabCatalogue, t));
            NodeRegion region = NodeRegion.Of(node);
            double census = region.IsNone ? 0
                : TerrorCampaigns.RegionCensusDps(world, world.PrefabCatalogue, region, actorTag, t);
            return Math.Max(own, census);
        }

        private sealed class Stay
        {
            public FactionState Owner;
            public double Seconds;
            public double GapSeconds;
            public double Km;
            public double ShadowSeconds;
            public bool Caught;
            public Vec3 CatchPos;
            public Vec3 LastPos;
        }

        private static Crossing Close(WorldModel world, FactionTuning t, Stay stay, string actorTag)
        {
            Vec3 at = stay.Caught ? stay.CatchPos : stay.LastPos;
            SpatialNode node = TrespassPolicy.NearestOwnedNode(world.Graph, stay.Owner.Tag, at);
            return new Crossing
            {
                OwnerTag = stay.Owner.Tag,
                NodeId = node != null ? node.Id : null,
                RegionKey = node != null ? FactionBrain.CampaignKeyFor(NodeRegion.Of(node), node) : null,
                Km = stay.Km,
                Seconds = stay.Seconds,
                ShadowSeconds = stay.ShadowSeconds == double.MaxValue ? 0 : stay.ShadowSeconds,
                Caught = stay.Caught,
                CatcherDps = stay.Caught ? CatcherDps(world, t, node, actorTag) : 0,
            };
        }

        private static bool DetectedBy(WorldModel world, FactionTuning t, FactionState owner,
                                       Dictionary<string, List<SensorSource>> sensorsOf, Vec3 pos, double signature)
        {
            List<SensorSource> sensors;
            if (!sensorsOf.TryGetValue(owner.Tag, out sensors))
            {
                sensors = new List<SensorSource>();
                PlayerSensing.CollectFactionSensors(world.Graph, owner, t, world.UtcNow, sensors);
                sensorsOf[owner.Tag] = sensors;
            }
            return FleetSensing.IsDetectedBy(sensors, pos, signature);
        }

        internal static FactionState StateOf(IList<FactionState> states, string tag)
        {
            if (states == null || tag == null) return null;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && string.Equals(states[i].Tag, tag, StringComparison.Ordinal)) return states[i];
            return null;
        }
    }
}
