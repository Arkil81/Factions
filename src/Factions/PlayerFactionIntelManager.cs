using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// The Factions page's Overview tab (docs/fog-of-war.md → The Factions page: Overview and Members):
    /// answers one <see cref="WireMessageKind.FactionIntelRequest"/> with the viewer's estimate of the
    /// named faction. Stateless: the client asks again while the faction stays selected.
    ///
    /// <para>The estimate is built from the viewer's own stores only (<see cref="FactionIntelEstimate"/>),
    /// so it is a lower bound and qualified by its node count. The viewer's own faction, and any faction
    /// for an admin, is answered from the world exactly; an admin is also sent the treasury.</para>
    /// </summary>
    internal sealed class PlayerFactionIntelManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly PlayerScannerManager _scanner;
        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>();

        public PlayerFactionIntelManager(FactionRuntime runtime, UplinkManager uplink, PlayerScannerManager scanner)
        {
            _runtime = runtime;
            _uplink = uplink;
            _scanner = scanner;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.FactionIntelRequest, OnRequest);
        }

        private void OnRequest(ulong steamId, byte[] data)
        {
            string tag;
            if (!FactionsWire.TryReadFactionIntelRequest(data, out tag))
            {
                FactionsPlugin.Log.Warn("Faction intel request from " + steamId + " was unreadable.");
                return;
            }
            if (string.IsNullOrEmpty(tag) || _runtime == null || _runtime.World == null || MySession.Static == null) return;

            long identityId = LinkedViewers.IdentityFor(steamId);
            WireFactionIntel answer = Build(identityId, tag);
            _uplink.Send(steamId, FactionsWire.WriteFactionIntelSnapshot(answer));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("FACTION_INTEL steam=" + steamId + " tag=" + tag + " known=" + answer.Known
                                     + " nodes=" + answer.KnownNodes + " wealth=" + (WealthBand)answer.Wealth
                                     + " strength=" + (StrengthBand)answer.Strength + " age=" + answer.StrengthAgeSeconds
                                     + " exact=" + answer.Exact + " treasury=" + answer.HasTreasury);
        }

        private WireFactionIntel Build(long identityId, string tag)
        {
            var answer = new WireFactionIntel { Tag = tag };
            FactionTuning tuning = _runtime.World.Tuning;
            string viewerTag = ViewerKnowledge.FactionTag(identityId);
            bool own = !string.IsNullOrEmpty(viewerTag) && string.Equals(viewerTag, tag, StringComparison.OrdinalIgnoreCase);
            bool admin = PlayerAuthority.IsAdmin(identityId);

            if (own || admin)
            {
                FactionIntelCensus.Row row;
                if (!TryExact(tag, out row)) return answer;
                answer.Known = true;
                answer.Exact = true;
                answer.KnownNodes = row.Nodes;
                double score = FactionIntelCensus.StageScore(row);
                double strength = row.DefenceSum + row.StrongestFleet;
                answer.Wealth = (byte)FactionIntelEstimate.Wealth(score, row.Nodes, tuning);
                answer.Strength = (byte)FactionIntelEstimate.Strength(strength, true, tuning);
                answer.Ledger = row.Ledger;
                answer.StrengthDps = (long)strength;
                if (admin)
                {
                    answer.HasTreasury = true;
                    answer.Treasury = _runtime.World.GetTreasury(tag);
                }
                return answer;
            }

            ViewerKnowledge.BuildSources(_runtime, _scanner, identityId, _viewerSources);
            DateTime now = _runtime.World.UtcNow;
            FactionEstimate e = FactionIntelEstimate.Estimate(
                tag, _runtime.World.Graph, _viewerSources, _scanner != null ? _scanner.SightingsFor(identityId) : null,
                now, tuning.DiscoveryHorizon, tuning);
            answer.Known = e.KnownNodes > 0 || e.Strength != StrengthBand.Unknown;
            answer.KnownNodes = e.KnownNodes;
            answer.Wealth = (byte)e.Wealth;
            answer.Strength = (byte)e.Strength;
            if (e.Strength != StrengthBand.Unknown && e.StrengthUtc != default(DateTime))
            {
                double seconds = (now - e.StrengthUtc).TotalSeconds;
                answer.StrengthAgeSeconds = seconds <= 0 ? 0 : seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
            }
            return answer;
        }

        private bool TryExact(string tag, out FactionIntelCensus.Row row)
        {
            row = default(FactionIntelCensus.Row);
            foreach (FactionIntelCensus.Row r in _runtime.FactionIntelRows())
            {
                if (!string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                row = r;
                return true;
            }
            return false;
        }
    }
}
