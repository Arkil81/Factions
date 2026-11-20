using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Magnate-tier "presence stands in for Reinforce": a player faction's own member standing at
    /// one of its owned nodes accrues toward one <see cref="WorldModel.Reinforce"/> action every
    /// <see cref="FactionTuning.PresenceReinforceTargetSeconds"/> of continuous presence (see
    /// docs/economy-territory.md -> "Player-faction claiming" -> Growing). Presence reads as
    /// regional, not pinpoint: every node the faction owns within
    /// <see cref="FactionTuning.PresenceReinforceMaxHops"/> graph-hops accrues too, at a rate scaled
    /// by <see cref="FactionTuning.PresenceReinforceHopFalloff"/> per hop.
    ///
    /// <para>Accrual policy (accumulate/fire/reset) is single-sourced in
    /// <see cref="PresenceReinforce"/> (Factions.Core, offline-tested); this class is purely the
    /// SE-facing adapter — presence-to-faction resolution and real elapsed time.</para>
    ///
    /// <para>Runs on the game thread, on the same slow tick as <see cref="PlayerPresenceManager"/>,
    /// after it — <see cref="Tick"/> reads <see cref="SpatialNode.Presences"/>, which that manager
    /// populates.</para>
    /// </summary>
    internal sealed class PresenceReinforceManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        // Guards against crediting a huge presence span after a long pause/lag spike between ticks
        // (e.g. the sim was paused, or this tick was badly delayed) — cap a single tick's contribution
        // to a bit more than the nominal ~5s cadence rather than trusting an unbounded wall-clock delta.
        private const double MaxDeltaSeconds = 30.0;

        private readonly FactionRuntime _runtime;
        private readonly WorldModel _world;
        private DateTime? _lastTickUtc;

        public PresenceReinforceManager(FactionRuntime runtime)
        {
            _runtime = runtime;
            _world = runtime.World;
        }

        public void Tick()
        {
            if (_world?.Graph == null || MySession.Static == null) return;

            // Unlike FactionRuntime's own tick dispatch (FactionRuntime.cs, "if (Speed <= 0) return;"
            // before any economy work runs), this manager is wired into FactionsPlugin's GPS-tick
            // block, which is NOT gated by sim pause -- GPS/scanner functionality is meant to keep
            // working while paused. That means unpaused wall-clock time was accruing here even while
            // the sim itself was frozen. Skip entirely while paused, matching "pausing pauses active
            // economic time" (docs/economy-territory.md's Income formula section), and drop the last-
            // tick timestamp so resuming doesn't credit the whole paused span as one giant delta.
            if (_runtime.Speed <= 0) { _lastTickUtc = null; return; }

            DateTime now = DateTime.UtcNow;
            double deltaSeconds = _lastTickUtc.HasValue
                ? Math.Min((now - _lastTickUtc.Value).TotalSeconds, MaxDeltaSeconds)
                : 0;
            _lastTickUtc = now;
            if (deltaSeconds <= 0) return;

            double targetSeconds = _world.Tuning != null ? _world.Tuning.PresenceReinforceTargetSeconds : 1800.0;
            int maxHops = _world.Tuning != null ? _world.Tuning.PresenceReinforceMaxHops : 3;
            double hopFalloff = _world.Tuning != null ? _world.Tuning.PresenceReinforceHopFalloff : 0.5;

            // Pass 1: the nodes a member is actually standing at, grouped by owning player faction.
            Dictionary<string, List<string>> sourcesByTag = null;
            Dictionary<string, FactionState> ownerByTag = null;

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (string.IsNullOrEmpty(node.OwnerFactionTag)
                    || NeutralFaction.IsClaimable(node.OwnerFactionTag)
                    || node.Presences.Count == 0) continue;

                FactionState owner = _runtime.FindFaction(node.OwnerFactionTag);
                if (owner == null || !owner.IsPlayerFaction) continue;

                if (!OwningFactionMemberPresent(node, owner.Tag)) continue;

                if (sourcesByTag == null)
                {
                    sourcesByTag = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    ownerByTag = new Dictionary<string, FactionState>(StringComparer.Ordinal);
                }

                List<string> sources;
                if (!sourcesByTag.TryGetValue(owner.Tag, out sources))
                {
                    sources = new List<string>();
                    sourcesByTag[owner.Tag] = sources;
                    ownerByTag[owner.Tag] = owner;
                }
                sources.Add(node.Id);
            }

            if (sourcesByTag == null) return;   // nobody present: no graph walk at all

            // Pass 2: one bounded hop walk per faction, crediting its owned nodes within range.
            var fired = new List<PresenceReinforceFire>();
            foreach (KeyValuePair<string, List<string>> kv in sourcesByTag)
            {
                FactionState owner = ownerByTag[kv.Key];
                fired.Clear();
                PresenceReinforce.AccumulateWithSpread(owner, _world.Graph, kv.Value,
                    deltaSeconds, targetSeconds, maxHops, hopFalloff, fired);

                for (int i = 0; i < fired.Count; i++)
                {
                    PresenceReinforceFire f = fired[i];
                    _world.Reinforce(owner.Tag, f.NodeId);

                    SpatialNode node;
                    string hold = _world.Graph.Nodes.TryGetValue(f.NodeId, out node)
                        ? node.HoldStrength.ToString("0.00") : "?";
                    Log.Info("Presence reinforce: " + owner.Tag + " reinforced " + f.NodeId
                        + " (" + f.Hops + " hop(s) from presence, hold now " + hold + ")");
                }
            }
        }

        private static bool OwningFactionMemberPresent(SpatialNode node, string ownerTag)
        {
            for (int i = 0; i < node.Presences.Count; i++)
            {
                Presence p = node.Presences[i];
                if (p.Kind != ActorKind.Player) continue;
                long identityId;
                if (!long.TryParse(p.ActorId, out identityId)) continue;
                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
                if (faction != null && string.Equals(faction.Tag, ownerTag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
