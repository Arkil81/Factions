using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the Tactics window: the viewing player's own faction's
    /// <see cref="TacticMemory"/>, assembled by <see cref="TacticsReport"/>. A linked client
    /// subscribes while the window is open; nothing runs while nobody has it open.
    ///
    /// <para>Sent unconditionally on the scanner cadence while subscribed, for the reason
    /// <see cref="PlayerFleetDoctrineManager"/> gives: evidence decays with world time and a fight
    /// can land at any moment, so a revision-gated send would freeze the window on the numbers as
    /// they stood when it opened.</para>
    ///
    /// <para>Read-only. The later player override writes the player-override rung of
    /// <see cref="TacticResolver"/>, read in <c>FactionRuntime.TacticOverrideFor</c>; its command
    /// kinds go beside these and re-resolve the caller's faction the way <see cref="ViewerFaction"/>
    /// does.</para>
    ///
    /// <para>Only the viewer's OWN faction is ever described, re-resolved from the SteamID on every
    /// send: what a faction has learned about fighting whom is not something any other faction may
    /// read.</para>
    /// </summary>
    internal sealed class PlayerTacticsManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        public PlayerTacticsManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.TacticsRequest, OnRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadTacticsRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Tactics request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("TACTICS_SUBSCRIBE steam=" + steamId);
                Tick();
            }
            else
            {
                _streaming.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("TACTICS_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence; does nothing while nobody has the window open.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;
                SendSnapshot(steamId, ViewerFaction(player.IdentityId));
            }
        }

        private void SendSnapshot(ulong steamId, FactionState owner)
        {
            WorldModel world = _runtime.World;
            TacticsReport report = TacticsReport.Build(owner, world.TacticLearning, world.UtcNow);

            var header = new WireTacticsHeader
            {
                FactionTag = report.FactionTag ?? "",
                PriorArm = (byte)report.PriorArm,
                PriorBreakOff = (byte)report.PriorBreakOff,
                Updates = report.Updates,
                Learning = report.Learning,
            };

            var opponents = new List<WireTacticOpponent>(report.Opponents.Count);
            foreach (TacticsOpponentReport o in report.Opponents)
            {
                var contexts = new WireTacticContext[o.Contexts.Count];
                for (int c = 0; c < o.Contexts.Count; c++)
                {
                    TacticsContextReport ctx = o.Contexts[c];
                    var cells = new WireTacticCell[ctx.Cells.Count];
                    for (int k = 0; k < ctx.Cells.Count; k++) cells[k] = ToWire(ctx.Cells[k]);
                    contexts[c] = new WireTacticContext { Context = ctx.Context ?? "", Evidence = (float)ctx.Evidence, Cells = cells };
                }
                opponents.Add(new WireTacticOpponent
                {
                    Tag = o.OpponentTag ?? "",
                    LastUtcTicks = o.LastUtc.Ticks,
                    Preferred = ToWire(o.Preferred),
                    Evidence = (float)o.Evidence,
                    Contexts = contexts,
                });
            }

            var reviews = new List<WireTacticReview>(report.Reviews.Count);
            foreach (TacticReview r in report.Reviews)
            {
                reviews.Add(new WireTacticReview
                {
                    UtcTicks = r.Utc.Ticks,
                    Opponent = r.OpponentTag ?? "",
                    Context = r.Context ?? "",
                    Arm = (byte)r.Arm,
                    BreakOff = (byte)r.BreakOff,
                    Predicted = (float)r.Predicted,
                    Actual = (float)r.Actual,
                    Reward = (float)r.Reward,
                    Mistake = r.Mistake,
                    HullsLost = (byte)Math.Max(0, Math.Min(255, r.HullsLost)),
                    HullsBrought = (byte)Math.Max(0, Math.Min(255, r.HullsBrought)),
                });
            }

            _uplink.Send(steamId, FactionsWire.WriteTacticsSnapshot(header, opponents, reviews));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("TACTICS_SNAPSHOT steam=" + steamId + " tag=" + header.FactionTag
                                     + " opponents=" + opponents.Count + " reviews=" + reviews.Count);
        }

        private static WireTacticCell ToWire(TacticsCellReport c)
        {
            if (c == null) return new WireTacticCell();
            return new WireTacticCell
            {
                Arm = (byte)c.Arm,
                BreakOff = (byte)c.BreakOff,
                Mean = (float)c.Mean,
                Evidence = (float)c.Evidence,
            };
        }

        /// <summary>The registered player faction behind a player identity, or null. The same rule
        /// <see cref="PlayerFleetDoctrineManager"/> applies: an NPC tag must not be reachable from a
        /// client.</summary>
        private FactionState ViewerFaction(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) return null;
            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
        }

        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
            List<ulong> stale = null;
            foreach (ulong steamId in _streaming)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale) _streaming.Remove(steamId);
        }

        public void Close()
        {
            _streaming.Clear();
        }
    }
}
