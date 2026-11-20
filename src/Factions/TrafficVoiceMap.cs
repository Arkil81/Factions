using System;
using Factions.Core;
using Factions.Wire;

namespace Factions
{
    /// <summary>An online player who might hear the radio: where they are, how fast they are moving
    /// (m/s), and the set they are listening on.</summary>
    internal struct TrafficListener
    {
        public ulong SteamId;
        public Vec3 PosKm;
        public Vec3 VelMs;
        public RadioReceiver Radio;
    }

    /// <summary>The traffic voice's mapping from Core's fleet facts to the wire (FAC-419). Kept apart
    /// from <c>TrafficCallouts</c>, which needs the uplink, so the test project can link it.</summary>
    /// <summary>How one listener stands to one event's two voices (FAC-419): the reach with no
    /// reflector gain, the reach as their reflectors point now, and the reach if the best of them were
    /// turned onto the source.</summary>
    internal struct TrafficHearing
    {
        public double ToNodeKm, ToPilotKm;
        public double StationOmniKm, StationNowKm, StationBestKm;
        public double PilotOmniKm, PilotNowKm, PilotBestKm;
        public double DishDb;

        public bool StationNow { get { return StationNowKm > 0 && ToNodeKm < StationNowKm; } }
        public bool StationCould { get { return StationBestKm > 0 && ToNodeKm < StationBestKm; } }
        public bool PilotNow { get { return PilotNowKm > 0 && ToPilotKm < PilotNowKm; } }
        public bool PilotCould { get { return PilotBestKm > 0 && ToPilotKm < PilotBestKm; } }
    }

    internal static class TrafficVoiceMap
    {
        /// <summary>This listener's reach to the station and to the pilot, now and at best.</summary>
        public static TrafficHearing Hear(TrafficEvent e, TrafficListener l, double broadcastKm, FactionTuning t)
        {
            var h = new TrafficHearing();
            if (t == null) return h;
            double best = RadioPath.GainFactor(RadioPath.MaxPeakDb(l.Radio, t));
            h.ToNodeKm = l.PosKm.DistanceTo(e.NodePosKm);
            h.ToPilotKm = l.PosKm.DistanceTo(e.PilotPosKm);
            h.StationOmniKm = RadioPath.StationRangeKm(l.Radio, t);
            h.StationNowKm = RadioPath.StationRangeKm(l.Radio, t, l.PosKm, e.NodePosKm);
            h.StationBestKm = h.StationOmniKm * best;
            h.PilotOmniKm = RadioPath.PilotRangeKm(l.Radio, e.PilotSignature, broadcastKm, t);
            h.PilotNowKm = RadioPath.PilotRangeKm(l.Radio, e.PilotSignature, broadcastKm, t, l.PosKm, e.PilotPosKm);
            h.PilotBestKm = h.PilotOmniKm * best;
            h.DishDb = RadioPath.DirectionalGainDb(l.Radio, l.PosKm, e.NodePosKm, t);
            return h;
        }

        /// <summary>The aim a client needs to hear a call through its own reflectors as it turns.</summary>
        public static WireRadioAim Aim(Vec3 sourceKm, double omniReachKm, FactionTuning t)
        {
            return new WireRadioAim
            {
                SourceX = (float)sourceKm.X, SourceY = (float)sourceKm.Y, SourceZ = (float)sourceKm.Z,
                OmniReachKm = (float)omniReachKm,
                DishGainDb = (float)t.AtcDishGainDb, DishBeamwidthDeg = (float)t.AtcDishBeamwidthDeg,
                ParabolicGainDb = (float)t.AtcParabolicGainDb, ParabolicBeamwidthDeg = (float)t.AtcParabolicBeamwidthDeg,
            };
        }

        /// <summary>What one listener hears of a pilot's call (FAC-419 slice 5): the radio path from
        /// the pilot's ship to the listener, through <see cref="RadioPath"/>, with the tuning's
        /// constants. <paramref name="nu"/> is the worst planet diffraction on that line
        /// (<see cref="SensorOcclusion.WorstDiffractionNu"/>); negative infinity for clear sky.
        /// <paramref name="rangeKm"/> is how far this listener hears this pilot with no reflector
        /// gain (<see cref="TrafficHearing.PilotOmniKm"/>), whose gain lifts the signal; the client adds
        /// its reflectors' gain as the call plays, toward <see cref="WireTrafficPilotCall.Aim"/>.</summary>
        public static WireTrafficPilotCall PilotCall(TrafficEvent e, TrafficListener l, double nu, double rangeKm, FactionTuning t)
        {
            double dx = e.PilotPosKm.X - l.PosKm.X, dy = e.PilotPosKm.Y - l.PosKm.Y, dz = e.PilotPosKm.Z - l.PosKm.Z;
            double dKm = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double rvx = e.PilotVelMs.X - l.VelMs.X, rvy = e.PilotVelMs.Y - l.VelMs.Y, rvz = e.PilotVelMs.Z - l.VelMs.Z;
            // Rate the distance changes: the relative velocity along the line from listener to pilot.
            double radial = dKm > 1e-6 ? (dx * rvx + dy * rvy + dz * rvz) / dKm : 0.0;
            double pilotSpeed = Math.Sqrt(e.PilotVelMs.X * e.PilotVelMs.X + e.PilotVelMs.Y * e.PilotVelMs.Y + e.PilotVelMs.Z * e.PilotVelMs.Z);
            double listenerSpeed = Math.Sqrt(l.VelMs.X * l.VelMs.X + l.VelMs.Y * l.VelMs.Y + l.VelMs.Z * l.VelMs.Z);
            // Either end moving through the reflections sweeps the pattern.
            double sweep = Math.Sqrt(pilotSpeed * pilotSpeed + listenerSpeed * listenerSpeed);

            double loss = RadioPath.KnifeEdgeLossDb(nu);
            return new WireTrafficPilotCall
            {
                Event = (WireTrafficEvent)(byte)e.Kind,
                NodeId = e.NodeId,
                DopplerHz = (short)Clamp(Math.Round(RadioPath.DopplerHz(t.AtcCarrierMHz, radial)), short.MinValue, short.MaxValue),
                FlutterHz = (ushort)Clamp(Math.Round(RadioPath.FlutterHz(t.AtcCarrierMHz, sweep)), 0, ushort.MaxValue),
                SnrDeciDb = (short)Clamp(Math.Round((RadioPath.SnrDb(t.AtcSnrAt1kmDb, dKm, loss) + RadioPath.AntennaGainDb(rangeKm, t)) * 10), short.MinValue, short.MaxValue),
                CutoffHz = (ushort)Clamp(Math.Round(RadioPath.MuffleCutoffHz(loss, t.AtcMuffleFloorHz, t.AtcMuffleFloorAtLossDb)), 50, 3400),
                Reflections = (byte)Clamp(Math.Round(t.AtcReflections * 255), 0, 255),
                SquelchDb = (sbyte)Clamp(Math.Round(t.AtcSquelchDb), sbyte.MinValue, sbyte.MaxValue),
                LevelDb = (sbyte)Clamp(Math.Round(t.AtcPilotLevelDb), sbyte.MinValue, sbyte.MaxValue),
                NoiseDb = (sbyte)Clamp(Math.Round(t.AtcPilotLevelDb - t.AtcSnrAt1kmDb), sbyte.MinValue, sbyte.MaxValue),
                Aim = Aim(e.PilotPosKm, rangeKm, t),
            };
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>Which line family a fleet gets, or false when the controller says nothing about
        /// it. An attacker's own arrivals and departures are not called: it gets the hostile warning,
        /// and a station would not clear a raid to dock.</summary>
        public static bool TryVoice(TrafficEvent e, out WireTrafficVoice voice)
        {
            voice = WireTrafficVoice.Haul;
            bool hostile = e.FleetKind == ActionKind.Raid || e.FleetKind == ActionKind.Conquer
                           || e.FleetKind == ActionKind.Retaliate;
            if (e.Kind == TrafficEventKind.HostileInbound)
            {
                voice = WireTrafficVoice.Hostile;
                return hostile;
            }
            if (hostile) return false;
            switch (e.FleetKind)
            {
                case ActionKind.Haul:
                case ActionKind.Ferry: voice = WireTrafficVoice.Haul; return true;
                case ActionKind.Trade:
                case ActionKind.Gift: voice = WireTrafficVoice.Trade; return true;
                case ActionKind.Mine: voice = WireTrafficVoice.Mine; return true;
                case ActionKind.Reinforce: voice = WireTrafficVoice.Relief; return true;
                case ActionKind.Guard:
                case ActionKind.Patrol: voice = WireTrafficVoice.Patrol; return true;
                default: return false;
            }
        }

        /// <summary>A trespass hail to the one pilot it addresses (FAC-483), as a controller callout
        /// from <paramref name="node"/>, the faction's node nearest the pilot. The faction transmits to
        /// a ship its sensors already see, so a hail is never out of reach: past the listener's own
        /// station reach it is heard at the edge of it, faint and fading.</summary>
        public static WireTrafficCallout Hail(WireTrafficEvent ev, string ownerTag, byte speaker, SpatialNode node,
                                              string nodeName, TrafficListener l, FactionTuning t)
        {
            double reach = RadioPath.StationRangeKm(l.Radio, t);
            double distance = l.PosKm.DistanceTo(node.Position);
            double heard = Math.Max(reach, distance / HailEdgeShare);
            return new WireTrafficCallout
            {
                Event = ev,
                Voice = ev == WireTrafficEvent.HailWarning || ev == WireTrafficEvent.HailIntercept
                        || ev == WireTrafficEvent.HailOccupation || ev == WireTrafficEvent.HailOccupationHeld
                    ? WireTrafficVoice.Hostile : WireTrafficVoice.Patrol,
                ActorTag = ownerTag,
                NodeId = node.Id,
                NodeName = nodeName,
                NodeOwnerTag = ownerTag,
                DistanceShare = Share(distance, heard),
                Aim = Aim(node.Position, heard, t),
                Speaker = speaker,
            };
        }

        /// <summary>The farthest share of reach a hail is placed at: inside the edge band, where the
        /// static is heaviest and the voice fades.</summary>
        public const double HailEdgeShare = 0.9;

        /// <summary>How far a listener is, as a share of the radio range: 0 at the station, 255 at the
        /// edge or beyond. One byte on the wire; the client maps it to a static level.</summary>
        public static byte Share(double distanceKm, double rangeKm)
        {
            double s = rangeKm > 0 ? distanceKm / rangeKm : 1;
            if (s <= 0) return 0;
            if (s >= 1) return 255;
            return (byte)Math.Round(s * 255);
        }
    }
}
