using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Tells each linked player which of their blocks the load governor's toolbox has paused, so
    /// FactionsUI can say so in each block's terminal (docs/load-governor.md, step 3).
    ///
    /// <para><b>Why the client writes it.</b> <c>MyTerminalBlock.RefreshCustomInfo</c> runs only on
    /// the machine that calls it and is not replicated, so text written here would exist only in the
    /// server's copy of the block.</para>
    ///
    /// <para><b>Full state, sent on change.</b> After every <see cref="BlockThrottle"/> rebuild each
    /// online player is sent their whole list if it differs from what they were last sent, and a
    /// player who was never told anything and has nothing paused is not sent an empty one. A client
    /// that links is sent its list afresh.</para>
    /// </summary>
    internal sealed class BlockPauseNotices : IDisposable
    {
        private readonly UplinkManager _uplink;
        private readonly Dictionary<ulong, long> _lastSent = new Dictionary<ulong, long>();

        internal BlockPauseNotices(UplinkManager uplink)
        {
            _uplink = uplink;
            BlockThrottle.Rebuilt += Publish;
            if (_uplink != null) _uplink.ClientLinked += OnLinked;
        }

        public void Dispose()
        {
            BlockThrottle.Rebuilt -= Publish;
            if (_uplink != null) _uplink.ClientLinked -= OnLinked;
        }

        private void Publish()
        {
            if (_uplink == null || MySession.Static == null) return;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                if (player != null && !player.IsBot) SendIfChanged(player);
        }

        private void OnLinked(ulong steamId)
        {
            _lastSent.Remove(steamId);
            if (MySession.Static == null) return;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                if (player != null && player.Id.SteamId == steamId) { SendIfChanged(player); return; }
        }

        private void SendIfChanged(MyPlayer player)
        {
            ulong steamId = player.Id.SteamId;
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.BlockPause) == 0) return;

            List<WirePausedBlock> blocks = BlocksFor(player.DisplayName ?? "");

            long hash = Hash(blocks);
            long last;
            bool told = _lastSent.TryGetValue(steamId, out last);
            if (!told && blocks.Count == 0) return;
            if (told && last == hash) return;

            if (_uplink.Send(steamId, FactionsWire.WriteBlockPauseStatus(blocks)))
                _lastSent[steamId] = hash;
        }

        /// <summary>The player's paused blocks, plus their scripts the limiter slows (class 3) or
        /// stops (class 4).</summary>
        private static List<WirePausedBlock> BlocksFor(string party)
        {
            var blocks = new List<WirePausedBlock>();
            List<WirePausedBlock> paused;
            if (BlockThrottle.BlocksByParty.TryGetValue(party, out paused)) blocks.AddRange(paused);
            Factions.Core.PbLimiter limiter = PbLimiterPatches.Limiter;
            if (limiter == null) return blocks;
            foreach (Factions.Core.PbLimiter.BlockView b in limiter.Limited())
            {
                if (!string.Equals(BlockThrottle.PartyOfGrid(b.GridId), party, StringComparison.Ordinal)) continue;
                blocks.Add(new WirePausedBlock
                {
                    EntityId = b.BlockId,
                    Class = b.State == Factions.Core.PbLimiter.State.Stopped ? WirePausedBlock.ScriptStopped : WirePausedBlock.ScriptSlowed,
                });
            }
            return blocks;
        }

        /// <summary>Order-independent: a rebuild walks grids and blocks in whatever order the game
        /// holds them, and the same set in a new order is not a change worth a message.</summary>
        private static long Hash(List<WirePausedBlock> blocks)
        {
            unchecked
            {
                long h = blocks.Count;
                foreach (WirePausedBlock b in blocks)
                    h += (b.EntityId * 1000003L) ^ (b.Class * 7L + (b.ByAdmin ? 1L : 0L));
                return h;
            }
        }
    }
}
