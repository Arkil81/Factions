using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A candidate target as the agent read it, for the commander who asked.</summary>
    public sealed class NexusWingTarget
    {
        public long Id;
        public bool Exists;
        public bool Hostile;
        /// <summary>World metres.</summary>
        public double X, Y, Z;

        public static NexusWingTarget From(WingTargetReading t)
        {
            return new NexusWingTarget { Id = t.EntityId, Exists = t.Exists, Hostile = t.Hostile, X = t.PosM.X, Y = t.PosM.Y, Z = t.PosM.Z };
        }

        public WingTargetReading ToReading()
        {
            return new WingTargetReading { EntityId = Id, Exists = Exists, Hostile = Hostile, PosM = new Vec3(X, Y, Z) };
        }
    }

    /// <summary>An antenna on a station grid as the agent read it, for a station's call.</summary>
    public sealed class NexusWingAntenna
    {
        public long Id;
        public bool Exists, Functional, Enabled, Powered, Broadcasting;
        public double RadiusM;
        public long Owner;
        public string OwnerTag;
        public double X, Y, Z;

        public static NexusWingAntenna From(long id, WingAntennaReading a)
        {
            return new NexusWingAntenna
            {
                Id = id, Exists = a.Exists, Functional = a.Functional, Enabled = a.Enabled, Powered = a.Powered,
                Broadcasting = a.Broadcasting, RadiusM = a.RadiusM, Owner = a.OwnerIdentityId, OwnerTag = a.OwnerFactionTag,
                X = a.PosM.X, Y = a.PosM.Y, Z = a.PosM.Z,
            };
        }

        public WingAntennaReading ToReading()
        {
            return new WingAntennaReading
            {
                Exists = Exists, Functional = Functional, Enabled = Enabled, Powered = Powered, Broadcasting = Broadcasting,
                RadiusM = RadiusM, OwnerIdentityId = Owner, OwnerFactionTag = OwnerTag, PosM = new Vec3(X, Y, Z),
            };
        }
    }

    /// <summary>
    /// A wing command given on a Nexus agent, or a refresh of the readings an order given there
    /// rests on. The antenna, the target and the locks are entities on the agent, so the agent reads
    /// them; the order, the hulls' fleets and their flight are the authority's, which decides
    /// (<see cref="NexusWingReadings"/>). A refresh (<see cref="Command"/> 0) is sent each second
    /// while the order stands, and its antenna reads as gone on the authority once refreshes stop.
    /// </summary>
    public sealed class NexusWingCall
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        /// <summary>The agent's number for a command, echoed in the answer; 0 for a refresh.</summary>
        public long Id;
        /// <summary>A <see cref="WingCommand"/>; 0 for a refresh.</summary>
        public int Command;
        public int Via;
        public ulong Steam;
        /// <summary>The commander's identity on the agent; the antenna's owner and the target's
        /// hostility were read against it.</summary>
        public long Identity;
        /// <summary>The commander's SE faction tag on the agent; null when in none.</summary>
        public string Tag;
        public long Antenna;
        /// <summary>The grid the antenna stands on.</summary>
        public long AntennaGrid;
        public bool AExists, AFunctional, AEnabled, APowered, ABroadcasting;
        public double ARadiusM;
        public long AOwner;
        public string AOwnerTag;
        public double Ax, Ay, Az;
        public long PresserLock, Crosshair, PilotLock, CombatLock, Retained;
        /// <summary>Every candidate target named above, read for <see cref="Identity"/>.</summary>
        public List<NexusWingTarget> Targets;

        /// <summary><i>Protect me</i>: the call, or its refresh, is the station's that
        /// <see cref="AntennaGrid"/> is. The fields below describe that grid.</summary>
        public bool Station;
        public bool SStatic;
        /// <summary>The station grid's own locks: a seat's pilot, a combat AI block, a cockpit's
        /// retained target.</summary>
        public long SPilot, SCombat, SRetained;
        /// <summary>The nearest grid the antenna's owner is hostile to, within
        /// <see cref="StationCallRules.HostileNearM"/>; zero for none.</summary>
        public long SHostileNear;
        /// <summary>The grid that last hit the station, and how long ago in seconds; zero for none
        /// inside <see cref="StationCallRules.DamageMemory"/>.</summary>
        public long SHit;
        public double SHitAgeS;
        /// <summary>Every antenna on the station grid.</summary>
        public List<NexusWingAntenna> SAntennas;
        /// <summary>The threats named above, read for the antenna's owner (<see cref="AOwner"/>).</summary>
        public List<NexusWingTarget> STargets;

        public void SetAntenna(WingAntennaReading a, long gridId)
        {
            AntennaGrid = gridId;
            AExists = a.Exists; AFunctional = a.Functional; AEnabled = a.Enabled; APowered = a.Powered;
            ABroadcasting = a.Broadcasting; ARadiusM = a.RadiusM; AOwner = a.OwnerIdentityId; AOwnerTag = a.OwnerFactionTag;
            Ax = a.PosM.X; Ay = a.PosM.Y; Az = a.PosM.Z;
        }

        public WingAntennaReading AntennaReading()
        {
            return new WingAntennaReading
            {
                Exists = AExists, Functional = AFunctional, Enabled = AEnabled, Powered = APowered,
                Broadcasting = ABroadcasting, RadiusM = ARadiusM, OwnerIdentityId = AOwner, OwnerFactionTag = AOwnerTag,
                PosM = new Vec3(Ax, Ay, Az),
            };
        }

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusWingCall call)
        {
            call = NexusHandsCodec.Decode<NexusWingCall>(payload, NexusHandsCodec.Lean);
            if (call == null || call.Version != CurrentVersion) { call = null; return false; }
            return true;
        }
    }

    /// <summary>The authority's answer to a <see cref="NexusWingCall"/>: a command's outcome, or,
    /// to a refresh, that the order it was for has ended and the agent may stop reading.</summary>
    public sealed class NexusWingResult
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public long Id;
        public int Command;
        public bool Ok;
        public string Message;
        public long Antenna;
        /// <summary>The commander the call was for, as the agent knows them.</summary>
        public long Identity;
        /// <summary>No order stands on <see cref="Antenna"/> for this commander any more; for a
        /// station's call, no call stands on <see cref="StationGrid"/>.</summary>
        public bool Ended;
        /// <summary>The station grid, when the call answered was a station's; zero otherwise.</summary>
        public long StationGrid;

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusWingResult result)
        {
            result = NexusHandsCodec.Decode<NexusWingResult>(payload, NexusHandsCodec.Lean);
            if (result == null || result.Version != CurrentVersion) { result = null; return false; }
            return true;
        }
    }

    /// <summary>
    /// Authority: the antennas and targets agents read for their wing orders, kept for as long as
    /// they are refreshed. A reading older than <see cref="Fresh"/> is not served, so an antenna
    /// whose agent stopped refreshing reads as gone and its order's links lapse.
    /// </summary>
    public sealed class NexusWingReadings
    {
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

        private struct Antenna { public WingAntennaReading Reading; public long Grid; public DateTime Utc; }
        private struct Target { public WingTargetReading Reading; public DateTime Utc; }

        /// <summary>A station grid on an agent, as its call last read it.</summary>
        public struct StationGrid
        {
            public bool Static;
            public long Pilot, Combat, Retained, HostileNear;
            public List<long> Antennas;
            public DateTime Utc;
        }

        private readonly Dictionary<long, Antenna> _antennas = new Dictionary<long, Antenna>();
        private readonly Dictionary<long, Dictionary<long, Target>> _targets = new Dictionary<long, Dictionary<long, Target>>();
        private readonly Dictionary<long, StationGrid> _stations = new Dictionary<long, StationGrid>();

        public int StationCount { get { return _stations.Count; } }

        public int AntennaCount { get { return _antennas.Count; } }

        public void Put(NexusWingCall call, DateTime nowUtc)
        {
            if (call == null || call.Antenna == 0) return;
            _antennas[call.Antenna] = new Antenna { Reading = call.AntennaReading(), Grid = call.AntennaGrid, Utc = nowUtc };
            PutTargets(call.Identity, call.Targets, nowUtc);
            if (!call.Station || call.AntennaGrid == 0) return;
            var ids = new List<long>(call.SAntennas != null ? call.SAntennas.Count : 0);
            if (call.SAntennas != null)
                for (int i = 0; i < call.SAntennas.Count; i++)
                {
                    NexusWingAntenna a = call.SAntennas[i];
                    if (a == null || a.Id == 0) continue;
                    ids.Add(a.Id);
                    if (a.Id != call.Antenna)
                        _antennas[a.Id] = new Antenna { Reading = a.ToReading(), Grid = call.AntennaGrid, Utc = nowUtc };
                }
            if (!ids.Contains(call.Antenna)) ids.Add(call.Antenna);
            _stations[call.AntennaGrid] = new StationGrid
            {
                Static = call.SStatic, Pilot = call.SPilot, Combat = call.SCombat, Retained = call.SRetained,
                HostileNear = call.SHostileNear, Antennas = ids, Utc = nowUtc,
            };
            PutTargets(call.AOwner, call.STargets, nowUtc);
        }

        private void PutTargets(long identity, List<NexusWingTarget> targets, DateTime nowUtc)
        {
            if (targets == null || targets.Count == 0) return;
            Dictionary<long, Target> byCommander;
            if (!_targets.TryGetValue(identity, out byCommander))
            {
                byCommander = new Dictionary<long, Target>();
                _targets[identity] = byCommander;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                NexusWingTarget t = targets[i];
                if (t != null && t.Id != 0) byCommander[t.Id] = new Target { Reading = t.ToReading(), Utc = nowUtc };
            }
        }

        /// <summary>A station grid an agent's call is reading, while its readings are fresh.</summary>
        public bool TryStation(long gridId, DateTime nowUtc, out StationGrid station)
        {
            if (gridId != 0 && _stations.TryGetValue(gridId, out station) && nowUtc - station.Utc <= Fresh) return true;
            station = default(StationGrid);
            return false;
        }

        public bool TryAntenna(long antennaId, DateTime nowUtc, out WingAntennaReading reading, out long gridId)
        {
            Antenna a;
            if (antennaId != 0 && _antennas.TryGetValue(antennaId, out a) && nowUtc - a.Utc <= Fresh)
            {
                reading = a.Reading;
                gridId = a.Grid;
                return true;
            }
            reading = default(WingAntennaReading);
            gridId = 0;
            return false;
        }

        public bool TryTarget(long targetId, long commanderIdentityId, DateTime nowUtc, out WingTargetReading reading)
        {
            Dictionary<long, Target> byCommander;
            Target t;
            if (targetId != 0 && _targets.TryGetValue(commanderIdentityId, out byCommander)
                && byCommander.TryGetValue(targetId, out t) && nowUtc - t.Utc <= Fresh)
            {
                reading = t.Reading;
                return true;
            }
            reading = default(WingTargetReading);
            return false;
        }

        /// <summary>Drops readings no longer served.</summary>
        public void Prune(DateTime nowUtc)
        {
            List<long> drop = null;
            foreach (KeyValuePair<long, Antenna> kv in _antennas)
                if (nowUtc - kv.Value.Utc > Fresh) (drop ?? (drop = new List<long>())).Add(kv.Key);
            if (drop != null) for (int i = 0; i < drop.Count; i++) _antennas.Remove(drop[i]);
            List<long> gone = null;
            foreach (KeyValuePair<long, StationGrid> kv in _stations)
                if (nowUtc - kv.Value.Utc > Fresh) (gone ?? (gone = new List<long>())).Add(kv.Key);
            if (gone != null) for (int i = 0; i < gone.Count; i++) _stations.Remove(gone[i]);
            List<long> empty = null;
            foreach (KeyValuePair<long, Dictionary<long, Target>> c in _targets)
            {
                List<long> stale = null;
                foreach (KeyValuePair<long, Target> kv in c.Value)
                    if (nowUtc - kv.Value.Utc > Fresh) (stale ?? (stale = new List<long>())).Add(kv.Key);
                if (stale != null) for (int i = 0; i < stale.Count; i++) c.Value.Remove(stale[i]);
                if (c.Value.Count == 0) (empty ?? (empty = new List<long>())).Add(c.Key);
            }
            if (empty != null) for (int i = 0; i < empty.Count; i++) _targets.Remove(empty[i]);
        }
    }
}
