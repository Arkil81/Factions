using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>What happened at the station. The byte values are Core's <c>TrafficEventKind</c>;
    /// a value is never renumbered.</summary>
    public enum WireTrafficEvent : byte
    {
        Arrive = 1,
        Depart = 2,
        HostileInbound = 3,
        Queued = 4,
        Held = 5,
        Delayed = 6,
        Diverted = 7,
        Cleared = 8,
        /// <summary>A trespass hail (FAC-483), addressed to the one pilot it is sent to: a friendly
        /// pilot welcomed into a faction's space.</summary>
        HailWelcome = 9,
        /// <summary>A neutral pilot told whose space it is in.</summary>
        HailNotice = 10,
        /// <summary>An enemy pilot warned off.</summary>
        HailWarning = 11,
        /// <summary>An enemy pilot who stayed past the shadow time: the patrol is cleared to engage.</summary>
        HailIntercept = 12,
        /// <summary>A player who has begun occupying the faction's ground (a presence raid): they
        /// will be treated as an aggressor (ProtocolVersion 57).</summary>
        HailOccupation = 13,
        /// <summary>A player still occupying it at a later hold strip: it will not be forgotten.</summary>
        HailOccupationHeld = 14,
    }

    /// <summary>Which line family the controller uses for the fleet. Resolved on the server from the
    /// fleet's action kind, so the client never needs Core's <c>ActionKind</c>; a value is never
    /// renumbered.</summary>
    public enum WireTrafficVoice : byte
    {
        Haul = 1,
        Trade = 2,
        Mine = 3,
        Relief = 4,
        Patrol = 5,
        Hostile = 6,
    }

    /// <summary>One <see cref="WireMessageKind.TrafficCallout"/>.</summary>
    public struct WireTrafficCallout
    {
        public WireTrafficEvent Event;
        public WireTrafficVoice Voice;
        /// <summary>The fleet's faction tag, spelled out by the controller.</summary>
        public string ActorTag;
        /// <summary>Spelled out in phonetics when the name has no clip.</summary>
        public string NodeId;
        /// <summary>Sent rather than looked up on the client, whose map is fog-gated and may not
        /// hold the node.</summary>
        public string NodeName;
        /// <summary>The station's owner, so the client can rank its own faction's traffic first.
        /// Empty when unowned.</summary>
        public string NodeOwnerTag;
        /// <summary>How far the listener was from the station, as a share of its radio range, 0-255,
        /// with their reflectors as they pointed at send time. Picks the static level and whether the
        /// edge fades when <see cref="Aim"/> has no source; otherwise the client recomputes it live.</summary>
        public byte DistanceShare;
        /// <summary>The station's position and this listener's reach to it, for aiming by ear
        /// (ProtocolVersion 37).</summary>
        public WireRadioAim Aim;
        /// <summary>The fleet's id, spelled out after its kind (ProtocolVersion 40). Empty when unknown.</summary>
        public string FleetId;
        /// <summary>Queue events: place in the queue (0 when not queued), whether a departure, and a
        /// divert's destination (ProtocolVersion 40).</summary>
        public byte QueuePosition;
        public bool Departure;
        public string DivertNodeId;
        public string DivertNodeName;
        /// <summary>Which controller voice speaks the line: the station owner's
        /// (<c>ControllerVoice.SpeakerFor</c>), 0 being the original controller. A client whose library
        /// lacks a clip in that voice uses speaker 0's.</summary>
        public byte Speaker;
    }

    /// <summary>One <see cref="WireMessageKind.TrafficPilotCall"/>: what one listener hears of one
    /// pilot's call, already through the physics. Integers on the wire; units in the names.</summary>
    public struct WireTrafficPilotCall
    {
        public WireTrafficEvent Event;
        /// <summary>The station the call is for, so the client can pair it with the controller's answer.</summary>
        public string NodeId;
        /// <summary>Frequency offset of the voice, Hz (sideband Doppler).</summary>
        public short DopplerHz;
        /// <summary>Multipath fading rate, Hz.</summary>
        public ushort FlutterHz;
        /// <summary>Signal-to-noise, tenths of a dB, with no reflector gain: the client adds its
        /// reflectors' gain toward <see cref="Aim"/>'s source as the call plays (ProtocolVersion 37;
        /// before it, the gain at send time was included).</summary>
        public short SnrDeciDb;
        /// <summary>Low-pass for a diffracted path; 3400 when the sky is clear.</summary>
        public ushort CutoffHz;
        /// <summary>Share of the signal arriving by reflection, 0-255.</summary>
        public byte Reflections;
        public sbyte SquelchDb;
        /// <summary>A clear call's level against the controller, dB: a pilot at 1 km in clear sky.
        /// Nearer calls are louder, capped 6 dB above this.</summary>
        public sbyte LevelDb;
        /// <summary>The static's level against the controller for a call at 1 km, dB. Not read by the
        /// client since v0.387.1: its receiver model holds the voice at LevelDb (AGC) and puts the
        /// static at LevelDb - SNR. Kept on the wire so protocol 36 does not move.</summary>
        public sbyte NoiseDb;
        /// <summary>The pilot's position and this listener's reach to them (ProtocolVersion 37).</summary>
        public WireRadioAim Aim;
    }

    public static partial class FactionsWire
    {
        /// <summary>A trespass hail: addressed to one pilot, never collapsed into an advisory.</summary>
        public static bool IsHail(WireTrafficEvent e)
        {
            return e >= WireTrafficEvent.HailWelcome && e <= WireTrafficEvent.HailOccupationHeld;
        }

        public static byte[] WriteTrafficPilotCall(WireTrafficPilotCall c)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TrafficPilotCall);
                w.Write((byte)c.Event);
                w.Write(c.NodeId ?? "");
                w.Write(c.DopplerHz);
                w.Write(c.FlutterHz);
                w.Write(c.SnrDeciDb);
                w.Write(c.CutoffHz);
                w.Write(c.Reflections);
                w.Write(c.SquelchDb);
                w.Write(c.LevelDb);
                w.Write(c.NoiseDb);
                WriteAim(w, c.Aim);
                return ms.ToArray();
            }
        }

        public static bool TryReadTrafficPilotCall(byte[] data, out WireTrafficPilotCall c)
        {
            c = default(WireTrafficPilotCall);
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TrafficPilotCall) return false;
                    byte ev = r.ReadByte();
                    if (ev < (byte)WireTrafficEvent.Arrive || ev > (byte)WireTrafficEvent.Cleared) return false;
                    c.Event = (WireTrafficEvent)ev;
                    c.NodeId = r.ReadString();
                    c.DopplerHz = r.ReadInt16();
                    c.FlutterHz = r.ReadUInt16();
                    c.SnrDeciDb = r.ReadInt16();
                    c.CutoffHz = r.ReadUInt16();
                    c.Reflections = r.ReadByte();
                    c.SquelchDb = r.ReadSByte();
                    c.LevelDb = r.ReadSByte();
                    c.NoiseDb = r.ReadSByte();
                    c.Aim = ReadAim(r);
                    return true;
                }
            }
            catch { c = default(WireTrafficPilotCall); return false; }
        }

        private static void WriteAim(BinaryWriter w, WireRadioAim a)
        {
            w.Write(a.SourceX); w.Write(a.SourceY); w.Write(a.SourceZ);
            w.Write(a.OmniReachKm);
            w.Write(a.DishGainDb); w.Write(a.DishBeamwidthDeg);
            w.Write(a.ParabolicGainDb); w.Write(a.ParabolicBeamwidthDeg);
        }

        private static WireRadioAim ReadAim(BinaryReader r)
        {
            var a = new WireRadioAim();
            a.SourceX = r.ReadSingle(); a.SourceY = r.ReadSingle(); a.SourceZ = r.ReadSingle();
            a.OmniReachKm = r.ReadSingle();
            a.DishGainDb = r.ReadSingle(); a.DishBeamwidthDeg = r.ReadSingle();
            a.ParabolicGainDb = r.ReadSingle(); a.ParabolicBeamwidthDeg = r.ReadSingle();
            return a;
        }

        public static byte[] WriteTrafficVoicePreference(bool enabled)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TrafficVoicePreference);
                w.Write(enabled);
                return ms.ToArray();
            }
        }

        public static bool TryReadTrafficVoicePreference(byte[] data, out bool enabled)
        {
            enabled = true;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TrafficVoicePreference) return false;
                    enabled = r.ReadBoolean();
                    return true;
                }
            }
            catch { enabled = true; return false; }
        }

        public static byte[] WriteTrafficCallout(WireTrafficCallout c)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TrafficCallout);
                w.Write((byte)c.Event);
                w.Write((byte)c.Voice);
                w.Write(c.ActorTag ?? "");
                w.Write(c.NodeId ?? "");
                w.Write(c.NodeName ?? "");
                w.Write(c.NodeOwnerTag ?? "");
                w.Write(c.DistanceShare);
                WriteAim(w, c.Aim);
                w.Write(c.FleetId ?? "");
                w.Write(c.QueuePosition);
                w.Write(c.Departure);
                w.Write(c.DivertNodeId ?? "");
                w.Write(c.DivertNodeName ?? "");
                w.Write(c.Speaker);
                return ms.ToArray();
            }
        }

        /// <summary>False for another kind, a truncated payload, or an event or voice this build does
        /// not know.</summary>
        public static bool TryReadTrafficCallout(byte[] data, out WireTrafficCallout c)
        {
            c = default(WireTrafficCallout);
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TrafficCallout) return false;
                    byte ev = r.ReadByte();
                    byte voice = r.ReadByte();
                    if (ev < (byte)WireTrafficEvent.Arrive || ev > (byte)WireTrafficEvent.HailOccupationHeld) return false;
                    if (voice < (byte)WireTrafficVoice.Haul || voice > (byte)WireTrafficVoice.Hostile) return false;
                    c.Event = (WireTrafficEvent)ev;
                    c.Voice = (WireTrafficVoice)voice;
                    c.ActorTag = r.ReadString();
                    c.NodeId = r.ReadString();
                    c.NodeName = r.ReadString();
                    c.NodeOwnerTag = r.ReadString();
                    c.DistanceShare = r.ReadByte();
                    c.Aim = ReadAim(r);
                    c.FleetId = r.ReadString();
                    c.QueuePosition = r.ReadByte();
                    c.Departure = r.ReadBoolean();
                    c.DivertNodeId = r.ReadString();
                    c.DivertNodeName = r.ReadString();
                    c.Speaker = r.ReadByte();
                    return true;
                }
            }
            catch { c = default(WireTrafficCallout); return false; }
        }
    }
}
