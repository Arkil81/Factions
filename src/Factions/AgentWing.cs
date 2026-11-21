using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// A Nexus agent's half of the wing actions: the two doors of <see cref="PlayerWingManager"/>
    /// (a client's toolbar press, a block's action), served where the antenna, the target and the
    /// locks are. The agent reads them and relays the call; the authority decides the order and flies
    /// the hulls, which it already does on this server's fleet grids
    /// (<see cref="NexusFleetRegistry.AskWing"/>).
    ///
    /// <para><b>While an order stands</b> its antenna and targets are read here once a second and sent
    /// as a refresh (<see cref="NexusWingCall"/> with no command). The authority answers a refresh only
    /// to say the order has ended. Should refreshes stop, the antenna reads as gone there and the
    /// order's links lapse on the grace a switched-off antenna gets.</para>
    ///
    /// <para><b><i>Protect me</i></b> is a station's call, read throughout the call rather than once:
    /// the station grid's antennas, its locks, the nearest hostile and the grid that last hit it go
    /// with the call and with each second's refresh (<see cref="NexusWingCall.Station"/>). Hits on
    /// this server's Factions stations, and on any grid whose call stands, are kept here for that.</para>
    /// <para>The host's own call, as the authority raises it for the NPC stations it stands: once a
    /// second, an NPC station here hit by a grid hostile to it calls through its working antenna
    /// (<see cref="WingVia.Host"/>), or has its refusal written once for the attack; one with no
    /// working antenna is named in the report as unable to call (<see cref="MutedNodes"/>).</para>
    /// </summary>
    internal sealed class AgentWing
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1);
        /// <summary>An order is refreshed for at most this long without the authority answering it.</summary>
        private static readonly TimeSpan WatchCap = TimeSpan.FromHours(6);

        internal const string NoServer = "Wing refused: the server that commands wings could not be reached.";
        internal const string NoAnswer = "Wing: the command was not answered. Try again shortly.";

        private sealed class Asked
        {
            public ulong Steam;
            public WireWingCommand Command;
            public bool Block;
        }

        private sealed class Watch
        {
            public NexusWingCall Call;
            public DateTime NextUtc;
            public DateTime SinceUtc;
        }

        private struct Hit { public long By; public DateTime Utc; }

        private readonly UplinkManager _uplink;
        private readonly NexusCapturePending<Asked> _pending = new NexusCapturePending<Asked>();
        /// <summary>By commander identity: the authority keeps one order per commander. A station's
        /// call is kept under its grid's id negated, as the authority keeps its order.</summary>
        private readonly Dictionary<long, Watch> _watch = new Dictionary<long, Watch>();
        private readonly List<long> _scratch = new List<long>();
        private readonly Dictionary<long, Hit> _hits = new Dictionary<long, Hit>();
        private DateTime _hitsPrunedUtc;
        private readonly HashSet<long> _stationGrids = new HashSet<long>();
        private readonly List<KeyValuePair<string, long>> _stationScratch = new List<KeyValuePair<string, long>>();
        private DateTime _stationsReadUtc;

        /// <summary>Lists this server's materialised Factions station grids; set by the host. Read
        /// once a second into a set, so a damage event costs one lookup.</summary>
        internal Action<List<KeyValuePair<string, long>>> ReadStationGrids;

        /// <summary>Binds the damage hook the hits arrive through; idempotent, set by the host.</summary>
        internal Action EnsureHits;

        /// <summary>The node's owner tag when it is an NPC faction the authority runs, as the latest
        /// snapshot has it; null otherwise. Set by the host.</summary>
        internal Func<string, string> NpcOwnerOf;

        /// <summary>The DecisionLog, for the host call's refusal; set by the host.</summary>
        internal Action<string> Decision;

        /// <summary>A host call is raised again only for a hit after the last raise, and not sooner
        /// than this.</summary>
        internal static readonly TimeSpan HostRaiseEvery = TimeSpan.FromSeconds(5);

        private readonly List<string> _muted = new List<string>();
        private readonly Dictionary<long, DateTime> _hostRaised = new Dictionary<long, DateTime>();
        private readonly HashSet<long> _hostRefusedLogged = new HashSet<long>();
        private readonly List<long> _antennaScratch = new List<long>();

        public AgentWing(UplinkManager uplink)
        {
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.WingCommandRequest, OnRequest);
            _uplink.WingResultReceived = OnResult;
            if (WingAntennaActions.Register(OnBlockPress))
                Log.Info("Wing actions registered on antennas (" + WingActions.All.Length + "), relayed to the authority.");
            else
                Log.Warn("Wing actions NOT registered on antennas: " + WingAntennaActions.FailureReason);
        }

        public void Close()
        {
            WingAntennaActions.Unregister();
            if (_uplink != null) _uplink.WingResultReceived = null;
        }

        public int Watching { get { return _watch.Count; } }

        private void OnRequest(ulong steamId, byte[] data)
        {
            long antennaId, crosshair;
            WireWingCommand command;
            if (!FactionsWire.TryReadWingCommandRequest(data, out antennaId, out command, out crosshair))
            {
                Log.Warn("Wing command from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.WingCommand) == 0) return;
            MyPlayer player = WingWorld.PlayerOfSteam(steamId);
            if (player == null)
            {
                _uplink.Send(steamId, FactionsWire.WriteWingCommandResult(false, command, "Wing refused: the server could not identify you."));
                return;
            }
            WingCommand mapped = PlayerWingManager.Map(command);
            long identity = player.Identity.IdentityId;
            NexusWingCall call = Read(steamId, identity, WingVia.Client, antennaId);
            call.Command = (int)mapped;
            if (mapped == WingCommand.ProtectMe) ReadStation(call);
            else
            {
                call.PresserLock = WingWorld.PresserLock(identity);
                call.Crosshair = WingWorld.GridOf(crosshair);
                ReadTargets(call);
            }
            if (!Send(call, new Asked { Steam = steamId, Command = command }))
                _uplink.Send(steamId, FactionsWire.WriteWingCommandResult(false, command, NoServer));
        }

        private void OnBlockPress(IMyTerminalBlock block, WireWingCommand command)
        {
            try
            {
                if (block == null || !MyAPIGateway.Multiplayer.IsServer) return;
                WingCommand mapped = PlayerWingManager.Map(command);
                NexusWingCall call = Read(0, block.OwnerId, WingVia.Block, block.EntityId);
                call.Command = (int)mapped;
                if (mapped == WingCommand.ProtectMe) ReadStation(call);
                else
                {
                    long pilot, combat, retained;
                    WingWorld.GridLocks(block.CubeGrid != null ? block.CubeGrid.EntityId : 0, out pilot, out combat, out retained);
                    call.PilotLock = pilot;
                    call.CombatLock = combat;
                    call.Retained = retained;
                    ReadTargets(call);
                }
                Send(call, new Asked { Command = command, Block = true });
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Wing action from block " + (block != null ? block.EntityId : 0) + " failed.");
            }
        }

        private static NexusWingCall Read(ulong steamId, long identity, WingVia via, long antennaId)
        {
            long gridId;
            WingAntennaReading antenna = WingWorld.ReadAntenna(antennaId, out gridId);
            var call = new NexusWingCall
            {
                Via = (int)via, Steam = steamId, Identity = identity, Tag = WingWorld.FactionTagOf(identity), Antenna = antennaId,
            };
            call.SetAntenna(antenna, gridId);
            return call;
        }

        /// <summary>Reads every lock the call names, for its commander.</summary>
        private static void ReadTargets(NexusWingCall call)
        {
            var ids = new List<long>(5);
            foreach (long id in new[] { call.PresserLock, call.Crosshair, call.PilotLock, call.CombatLock, call.Retained })
                if (id != 0 && !ids.Contains(id)) ids.Add(id);
            if (ids.Count == 0) { call.Targets = null; return; }
            call.Targets = new List<NexusWingTarget>(ids.Count);
            for (int i = 0; i < ids.Count; i++) call.Targets.Add(NexusWingTarget.From(WingWorld.ReadTarget(ids[i], call.Identity)));
        }

        /// <summary>A station's call: the grid its antenna stands on, as the authority's call reads a
        /// station of its own (<see cref="FactionRuntime.CallStation"/>).</summary>
        private void ReadStation(NexusWingCall call)
        {
            call.Station = true;
            long grid = call.AntennaGrid;
            if (grid == 0) return;
            DateTime now = DateTime.UtcNow;
            call.SStatic = WingWorld.IsStaticGrid(grid);
            long pilot, combat, retained;
            WingWorld.GridLocks(grid, out pilot, out combat, out retained);
            call.SPilot = pilot; call.SCombat = combat; call.SRetained = retained;
            call.SHostileNear = WingWorld.NearestHostileGrid(grid, call.AOwner, StationCallRules.HostileNearM);
            Hit hit;
            if (_hits.TryGetValue(grid, out hit) && now - hit.Utc <= StationCallRules.DamageMemory)
            {
                call.SHit = hit.By;
                call.SHitAgeS = (now - hit.Utc).TotalSeconds;
            }
            var antennas = new List<long>();
            WingWorld.Antennas(grid, antennas);
            if (antennas.Count > 0)
            {
                call.SAntennas = new List<NexusWingAntenna>(antennas.Count);
                for (int i = 0; i < antennas.Count; i++)
                {
                    long ignored;
                    call.SAntennas.Add(NexusWingAntenna.From(antennas[i], WingWorld.ReadAntenna(antennas[i], out ignored)));
                }
            }
            var ids = new List<long>(3);
            foreach (long id in new[] { call.SHit, call.SCombat, call.SHostileNear })
                if (id != 0 && !ids.Contains(id)) ids.Add(id);
            if (ids.Count == 0) return;
            call.STargets = new List<NexusWingTarget>(ids.Count);
            for (int i = 0; i < ids.Count; i++) call.STargets.Add(NexusWingTarget.From(WingWorld.ReadTarget(ids[i], call.AOwner)));
        }

        /// <summary>The nodes whose NPC station here has no working antenna, at the last read; null
        /// for none.</summary>
        internal List<string> MutedNodes()
        {
            return _muted.Count > 0 ? new List<string>(_muted) : null;
        }

        /// <summary>Once a second, over this server's Factions stations: which NPC stations cannot call,
        /// and which were hit by a hostile grid and call through their antenna, as the authority's
        /// <c>ReadNpcStations</c> does over its own.</summary>
        private void ReadOwnStations(DateTime nowUtc)
        {
            _muted.Clear();
            Func<string, string> npcOwnerOf = NpcOwnerOf;
            if (npcOwnerOf == null) return;
            for (int i = 0; i < _stationScratch.Count; i++)
            {
                string nodeId = _stationScratch[i].Key;
                long grid = _stationScratch[i].Value;
                string tag = nodeId != null ? npcOwnerOf(nodeId) : null;
                if (tag == null || grid == 0) continue;
                WingRefusal firstRefusal;
                long antennaId = WorkingAntenna(grid, tag, out firstRefusal);
                if (antennaId == 0) _muted.Add(nodeId);
                if (_watch.ContainsKey(-grid)) continue;

                Hit hit;
                if (!_hits.TryGetValue(grid, out hit) || nowUtc - hit.Utc > StationCallRules.DamageMemory) continue;
                DateTime raised;
                if (_hostRaised.TryGetValue(grid, out raised) && (hit.Utc <= raised || nowUtc - raised < HostRaiseEvery)) continue;
                long owner = WingWorld.GridOwner(grid);
                WingTargetReading attacker = WingWorld.ReadTarget(hit.By, owner);
                if (!attacker.Exists || !attacker.Hostile) continue;
                if (antennaId != 0)
                {
                    _hostRaised[grid] = nowUtc;
                    NexusWingCall call = Read(0, owner, WingVia.Host, antennaId);
                    call.Command = (int)WingCommand.ProtectMe;
                    ReadStation(call);
                    Send(call, new Asked { Block = true });
                }
                else if (_hostRefusedLogged.Add(grid))
                {
                    Action<string> decision = Decision;
                    if (decision != null)
                        decision("STATION_CALL_REFUSED tag=" + tag + " node=" + nodeId + " antenna=0 via=host"
                                 + " reason=" + WingRules.Token(firstRefusal) + " attacker=" + hit.By);
                }
            }
        }

        /// <summary>A working antenna of <paramref name="tag"/> on the grid, or zero, with the first
        /// antenna's refusal.</summary>
        private long WorkingAntenna(long grid, string tag, out WingRefusal firstRefusal)
        {
            firstRefusal = WingRefusal.NoAntenna;
            WingWorld.Antennas(grid, _antennaScratch);
            for (int i = 0; i < _antennaScratch.Count; i++)
            {
                long g;
                WingRefusal r = StationCallRules.CheckAntenna(WingWorld.ReadAntenna(_antennaScratch[i], out g), tag);
                if (r == WingRefusal.None) return _antennaScratch[i];
                if (firstRefusal == WingRefusal.NoAntenna) firstRefusal = r;
            }
            return 0;
        }

        /// <summary>Whether a grid's hits are kept: one of this server's Factions stations, or a grid
        /// whose station call stands.</summary>
        internal bool WatchesStation(long gridId)
        {
            if (gridId == 0) return false;
            if (_watch.Count > 0 && _watch.ContainsKey(-gridId)) return true;
            return _stationGrids.Count > 0 && _stationGrids.Contains(gridId);
        }

        /// <summary>A watched station grid was hit by <paramref name="attackerGridId"/>.</summary>
        internal void RecordHit(long stationGridId, long attackerGridId)
        {
            if (attackerGridId == 0 || attackerGridId == stationGridId) return;
            _hits[stationGridId] = new Hit { By = attackerGridId, Utc = DateTime.UtcNow };
        }

        private bool Send(NexusWingCall call, Asked asked)
        {
            if (_uplink == null) return false;
            call.Id = _pending.Add(asked, DateTime.UtcNow);
            if (!_uplink.ForwardFrame(RelayKind.WingCall, call.Steam, call.Encode(), "wing"))
            {
                _pending.Take(call.Id);
                return false;
            }
            DateTime now = DateTime.UtcNow;
            long key = call.Station ? -call.AntennaGrid : call.Identity;
            if (call.Station && call.AntennaGrid == 0) return true;
            Watch w;
            if (!_watch.TryGetValue(key, out w) || w.Call.Antenna != call.Antenna)
                _watch[key] = w = new Watch { SinceUtc = now };
            w.Call = call;
            w.NextUtc = now + RefreshEvery;
            return true;
        }

        private void OnResult(ulong steamId, byte[] payload)
        {
            NexusWingResult r;
            if (!NexusWingResult.TryDecode(payload, out r))
            {
                Log.Warn("NEXUS_WING_RESULT_UNREADABLE bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            if (r.Ended)
            {
                Watch w;
                if (r.StationGrid != 0) _watch.Remove(-r.StationGrid);
                else if (_watch.TryGetValue(r.Identity, out w) && w.Call.Antenna == r.Antenna) _watch.Remove(r.Identity);
            }
            if (r.Id == 0) return;
            Asked asked = _pending.Take(r.Id);
            if (asked == null || asked.Block || asked.Steam == 0) return;
            _uplink.Send(asked.Steam, FactionsWire.WriteWingCommandResult(r.Ok, asked.Command, r.Message ?? ""));
        }

        /// <summary>Lapses unanswered commands and refreshes the readings of every order standing.
        /// Called every frame on the agent.</summary>
        public void Tick(DateTime nowUtc)
        {
            Action ensureHits = EnsureHits;
            if (ensureHits != null) ensureHits();
            Action<List<KeyValuePair<string, long>>> readStations = ReadStationGrids;
            if (readStations != null && nowUtc - _stationsReadUtc >= TimeSpan.FromSeconds(1))
            {
                _stationsReadUtc = nowUtc;
                _stationScratch.Clear();
                readStations(_stationScratch);
                _stationGrids.Clear();
                for (int i = 0; i < _stationScratch.Count; i++) _stationGrids.Add(_stationScratch[i].Value);
                ReadOwnStations(nowUtc);
            }
            if (_hits.Count > 0 && nowUtc - _hitsPrunedUtc > StationCallRules.DamageMemory)
            {
                _hitsPrunedUtc = nowUtc;
                _scratch.Clear();
                foreach (KeyValuePair<long, Hit> kv in _hits)
                    if (nowUtc - kv.Value.Utc > StationCallRules.DamageMemory) _scratch.Add(kv.Key);
                for (int i = 0; i < _scratch.Count; i++)
                {
                    _hits.Remove(_scratch[i]);
                    _hostRaised.Remove(_scratch[i]);
                    _hostRefusedLogged.Remove(_scratch[i]);
                }
            }
            List<Asked> lapsed = _pending.Lapse(nowUtc);
            if (lapsed != null)
                for (int i = 0; i < lapsed.Count; i++)
                    if (!lapsed[i].Block && lapsed[i].Steam != 0)
                        _uplink.Send(lapsed[i].Steam, FactionsWire.WriteWingCommandResult(false, lapsed[i].Command, NoAnswer));

            if (_watch.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<long, Watch> kv in _watch)
            {
                Watch w = kv.Value;
                if (nowUtc - w.SinceUtc > WatchCap) { _scratch.Add(kv.Key); continue; }
                if (nowUtc < w.NextUtc) continue;
                w.NextUtc = nowUtc + RefreshEvery;
                NexusWingCall was = w.Call;
                NexusWingCall call = Read(was.Steam, was.Identity, (WingVia)was.Via, was.Antenna);
                if (was.Station) ReadStation(call);
                else
                {
                    call.PresserLock = was.PresserLock; call.Crosshair = was.Crosshair; call.PilotLock = was.PilotLock;
                    call.CombatLock = was.CombatLock; call.Retained = was.Retained;
                    ReadTargets(call);
                }
                _uplink.ForwardFrame(RelayKind.WingCall, call.Steam, call.Encode(), "wing-refresh");
            }
            for (int i = 0; i < _scratch.Count; i++) _watch.Remove(_scratch[i]);
        }
    }
}
