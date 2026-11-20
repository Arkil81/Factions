using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Factions.Wire;

namespace Factions
{
    /// <summary>
    /// Sends station traffic to the FactionsUI traffic-control voice (FAC-419, docs/atc-voice.md).
    /// Each player whose client was granted <see cref="WireFeatures.TrafficVoice"/>, and who has not
    /// switched the voice off, hears as far as their own set reaches (<see cref="RadioReceiver"/>): a
    /// suit a short way, a ship's controls further, antennas as far as the ship's scanner sees, and a
    /// dish further along its axis. Per
    /// event they get up to two messages, each on its own reach:
    /// <list type="bullet">
    /// <item>a <see cref="WireMessageKind.TrafficPilotCall"/> -- the pilot calling in, carried over the
    /// radio physics from the pilot's ship to that listener (<see cref="TrafficVoiceMap.PilotCall"/>),
    /// when the pilot is inside <see cref="RadioPath.PilotRangeKm"/>;</item>
    /// <item>a <see cref="WireMessageKind.TrafficCallout"/> -- the controller's answer, when the
    /// station is inside <see cref="RadioPath.StationRangeKm"/>, with how far the listener is as a
    /// share of that reach.</item>
    /// </list>
    /// Both carry the source and the reach with no reflector gain (<see cref="WireRadioAim"/>), and a
    /// call is sent when the listener's best reflector COULD bring it in: the client hears it through
    /// its reflectors as they point while it plays, so a listener aims by ear (docs/atc-voice.md).
    /// Nothing is capped per minute: calls layer across stations, and the client bounds what plays.
    /// The client decides what is spoken and in what order. A radio is heard by whoever is in range,
    /// so there is no faction or relation filter.
    ///
    /// <para>It also answers <see cref="FactionRuntime.SpeaksTraffic"/>, which takes those players off
    /// the grid-lifecycle chat whispers. A player who switches the voice off
    /// (<see cref="WireMessageKind.TrafficVoicePreference"/>) is back on them.</para>
    ///
    /// <para>Behind <c>factions debug uplink</c>, one <c>TRAFFIC_CALLOUT</c> line per event. It is
    /// logged even when nobody is in range, so the event stream can be read on a live server; it
    /// costs about 1.7 ms per arrivals pass while armed (v0.385.0's acceptance).</para>
    /// </summary>
    internal sealed class TrafficCallouts
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly Func<List<TrafficListener>> _listeners;
        private readonly Func<long, double> _broadcastKm;
        private readonly HashSet<ulong> _voiceOff = new HashSet<ulong>();
        private readonly Func<FactionTuning> _tuning;
        private readonly Func<SensorOcclusion> _occlusion;

        /// <summary>Authority on a Nexus cluster: each event, with its node name, owner and speaker,
        /// for the agents to play to their own players. Called whether or not anyone here heard it.</summary>
        public Action<TrafficEvent, string, string, byte> Relay;

        /// <summary>Authority on a Nexus cluster: a hail to a pilot who is not connected here, for the
        /// agent they are on (steam id, hail, faction, speaker, node, node name). True when queued.</summary>
        public Func<ulong, WireTrafficEvent, string, byte, SpatialNode, string, bool> RelayHail;
        /// <param name="listeners">Every online player, with position, velocity and radio set.</param>
        /// <param name="broadcastKm">A materialised hull's own broadcast radius, 0 for none.</param>
        public TrafficCallouts(FactionRuntime runtime, UplinkManager uplink, Func<List<TrafficListener>> listeners,
                               Func<long, double> broadcastKm)
        {
            _runtime = runtime;
            _uplink = uplink;
            _listeners = listeners;
            _broadcastKm = broadcastKm;
            _tuning = () => _runtime != null ? _runtime.Tuning : null;
            _occlusion = () => _runtime != null ? _runtime.SensorOcclusion : null;
            if (_runtime == null || _uplink == null || _listeners == null) return;
            _runtime.TrafficSink = Send;
            _runtime.SpeaksTraffic = Speaks;
            _uplink.RegisterHandler(WireMessageKind.TrafficVoicePreference, OnPreference);
        }

        /// <summary>On a Nexus agent: no runtime and no events of its own. It plays the authority's
        /// (<see cref="Play"/>) to the players connected here, whose radios and nearby hulls are
        /// this server's entities.</summary>
        public TrafficCallouts(UplinkManager uplink, Func<FactionTuning> tuning, Func<SensorOcclusion> occlusion,
                               Func<List<TrafficListener>> listeners, Func<long, double> broadcastKm)
        {
            _uplink = uplink;
            _tuning = tuning;
            _occlusion = occlusion;
            _listeners = listeners;
            _broadcastKm = broadcastKm;
            if (_uplink == null || _listeners == null) return;
            _uplink.RegisterHandler(WireMessageKind.TrafficVoicePreference, OnPreference);
        }

        public void Close()
        {
            if (_runtime == null) return;
            _runtime.TrafficSink = null;
            _runtime.SpeaksTraffic = null;
        }

        private bool Speaks(ulong steamId)
        {
            return (_uplink.GrantedFeatures(steamId) & WireFeatures.TrafficVoice) != 0 && !_voiceOff.Contains(steamId);
        }

        private void OnPreference(ulong steamId, byte[] data)
        {
            bool enabled;
            if (!FactionsWire.TryReadTrafficVoicePreference(data, out enabled)) return;
            if (enabled) _voiceOff.Remove(steamId); else _voiceOff.Add(steamId);
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("TRAFFIC_VOICE_PREFERENCE steam=" + steamId + " enabled=" + enabled);
        }

        /// <summary>Speaks a trespass hail to the one pilot it addresses, in the faction's own voice,
        /// when their client has the traffic voice on (<see cref="TrafficVoiceMap.Hail"/>). False when
        /// it was not sent, so the caller's line can say so; the chat hail goes either way.</summary>
        public bool SendHail(ulong steamId, WireTrafficEvent ev, string ownerTag, SpatialNode node, string nodeName)
        {
            if (_runtime == null || steamId == 0UL || node == null) return false;
            byte speaker = ControllerVoice.SpeakerOf(_runtime.States, ownerTag);
            bool local;
            bool sent = Hail(steamId, ev, ownerTag, speaker, node, nodeName, out local);
            if (local) return sent;
            Func<ulong, WireTrafficEvent, string, byte, SpatialNode, string, bool> relay = RelayHail;
            return relay != null && relay(steamId, ev, ownerTag, speaker, node, nodeName);
        }

        /// <summary>Speaks a hail to a pilot connected here. <paramref name="local"/> is false when
        /// they are not among this server's listeners.</summary>
        public bool Hail(ulong steamId, WireTrafficEvent ev, string ownerTag, byte speaker, SpatialNode node,
                         string nodeName, out bool local)
        {
            local = false;
            if (steamId == 0UL || node == null || _listeners == null) return false;
            List<TrafficListener> listeners = _listeners();
            for (int i = 0; i < listeners.Count; i++)
            {
                if (listeners[i].SteamId != steamId) continue;
                local = true;
                FactionTuning tuning = _tuning != null ? _tuning() : null;
                if (tuning == null || !Speaks(steamId)) return false;
                WireTrafficCallout hail = TrafficVoiceMap.Hail(ev, ownerTag, speaker, node, nodeName, listeners[i], tuning);
                return _uplink.Send(steamId, FactionsWire.WriteTrafficCallout(hail));
            }
            return false;
        }

        private void Send(TrafficEvent e, string nodeName, string ownerTag)
        {
            byte speaker = ControllerVoice.SpeakerOf(_runtime.States, ownerTag);
            Play(e, nodeName, ownerTag, speaker);
            Action<TrafficEvent, string, string, byte> relay = Relay;
            if (relay != null) relay(e, nodeName, ownerTag, speaker);
        }

        /// <summary>Plays one event to every listener here who could hear it.</summary>
        public void Play(TrafficEvent e, string nodeName, string ownerTag, byte speaker)
        {
            if (e == null || _uplink == null || _listeners == null) return;
            WireTrafficVoice voice;
            if (!TrafficVoiceMap.TryVoice(e, out voice)) return;
            FactionTuning tuning = _tuning != null ? _tuning() : null;
            if (tuning == null) return;
            SensorOcclusion occlusion = (_occlusion != null ? _occlusion() : null) ?? SensorOcclusion.None;
            double wavelength = RadioPath.WavelengthM(tuning.AtcCarrierMHz);
            double broadcast = e.PilotEntityId > 0 && _broadcastKm != null ? _broadcastKm(e.PilotEntityId) : 0;

            List<TrafficListener> listeners = _listeners();
            int inRange = 0, granted = 0, sent = 0, pilots = 0, offBeam = 0;
            double bestDish = 0;
            for (int i = 0; i < listeners.Count; i++)
            {
                TrafficListener l = listeners[i];
                TrafficHearing h = TrafficVoiceMap.Hear(e, l, broadcast, tuning);
                // Sent when a reflector COULD bring it in: the client hears it through its reflectors
                // as they point while it plays. No per-minute cap: calls layer across stations
                // (operator, 2026-09-29), and the client bounds what plays at once.
                bool station = h.StationCould, pilot = h.PilotCould;
                if (!station && !pilot) continue;
                inRange++;
                if (!Speaks(l.SteamId)) continue;
                granted++;
                if (h.DishDb > bestDish) bestDish = h.DishDb;

                if (pilot)
                {
                    double nu = occlusion.WorstDiffractionNu(e.PilotPosKm, l.PosKm, wavelength);
                    WireTrafficPilotCall call = TrafficVoiceMap.PilotCall(e, l, nu, h.PilotOmniKm, tuning);
                    if (_uplink.Send(l.SteamId, FactionsWire.WriteTrafficPilotCall(call)))
                    {
                        pilots++;
                        if (!h.PilotNow) offBeam++;
                    }
                }

                if (!station) continue;
                var callout = new WireTrafficCallout
                {
                    Event = (WireTrafficEvent)(byte)e.Kind,
                    Voice = voice,
                    ActorTag = e.ActorTag,
                    NodeId = e.NodeId,
                    NodeName = nodeName,
                    NodeOwnerTag = ownerTag,
                    DistanceShare = TrafficVoiceMap.Share(h.ToNodeKm, h.StationNowKm),
                    Aim = TrafficVoiceMap.Aim(e.NodePosKm, h.StationOmniKm, tuning),
                    FleetId = e.FleetId,
                    QueuePosition = (byte)Math.Max(0, Math.Min(255, e.QueuePosition)),
                    Departure = e.Departure,
                    DivertNodeId = e.DivertNodeId,
                    DivertNodeName = e.DivertNodeName,
                    Speaker = speaker,
                };
                if (_uplink.Send(l.SteamId, FactionsWire.WriteTrafficCallout(callout)))
                {
                    sent++;
                    if (!h.StationNow) offBeam++;
                }
            }
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("TRAFFIC_CALLOUT ev=" + e.Kind + " voice=" + voice + " tag=" + (e.ActorTag ?? "?")
                                     + " node=" + (e.NodeId ?? "?") + " sig=" + e.PilotSignature.ToString("0.##", CultureInfo.InvariantCulture)
                                     + " inRange=" + inRange + " granted=" + granted + " sent=" + sent + " pilots=" + pilots
                                     + " offBeam=" + offBeam + " dishDb=" + bestDish.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }
}
