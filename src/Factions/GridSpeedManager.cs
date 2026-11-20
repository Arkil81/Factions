using System;
using System.Globalization;
using Factions.Core;
using Factions.Wire;
using Sandbox.Definitions;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Applies the operator's grid speed limits to SE and tells linked clients about them.
    ///
    /// <para><b>Why this needs a persistence of its own.</b> SE does not save a grid speed limit with
    /// the world. <c>MySector.GetEnvironmentSettings()</c> — what reaches the sector save — stores the
    /// environment definition's <i>Id</i> and the sun/fog overrides, and returns null outright when
    /// sun and fog match the definition. On load, <c>MySector</c> resolves that Id against the local
    /// definition set, so the numbers come from each machine's own content and mods.
    /// <c>MySector.SaveEnvironmentDefinition()</c> does write them, into
    /// <c>&lt;ContentPath&gt;\Data\Environment.sbc</c> — but it has no call site anywhere in SE, and
    /// that file is the game's content directory: global to every world and replaced by updates.
    /// So <see cref="FactionTuning.GridMaxSpeedLarge"/>/<see cref="FactionTuning.GridMaxSpeedSmall"/>
    /// are the persistence, re-applied on every session load.</para>
    ///
    /// <para><b>Why the client has to be told.</b> The client runs that same resolve-by-Id path
    /// against its own definitions, so a server-side write is not replicated by anything. A client
    /// granted <see cref="WireFeatures.GridSpeed"/> is sent the values and applies them locally,
    /// which is what keeps its own prediction agreeing with the server instead of fighting it. A
    /// client without the feature is simply unchanged.</para>
    ///
    /// <para><b>What "immediate" does and does not cover.</b> Everything that <i>reads</i> the limit
    /// — <c>MyGridPhysics.GetShipMaxLinearVelocity</c>, programmable blocks, the defensive combat
    /// block, and this plugin's own transit floor — picks the new value up on its next call. What
    /// does not change is the Havok ceiling already written onto a loaded grid's rigid body:
    /// <c>SetDefaultRigidBodyMaxVelocities()</c> runs at body creation and on
    /// <c>MyCubeGrid.HierarchyUpdated</c>, so an existing grid keeps its old ceiling until it
    /// reloads or its hierarchy changes. Sweeping live rigid bodies was considered and declined by
    /// the operator; the command says so in its own output rather than leaving it to be discovered.</para>
    /// </summary>
    internal sealed class GridSpeedManager
    {
        private readonly FactionTuning _tuning;
        private readonly UplinkManager _uplink;
        private readonly Action<string> _log;

        public GridSpeedManager(FactionTuning tuning, UplinkManager uplink, Action<string> log)
        {
            _tuning = tuning;
            _uplink = uplink;
            _log = log;
            if (_uplink != null) _uplink.ClientLinked += OnClientLinked;
        }

        public void Close()
        {
            if (_uplink != null) _uplink.ClientLinked -= OnClientLinked;
        }

        /// <summary>The limits SE is enforcing right now, in m/s, read back through the same clamp
        /// SE applies. Returns false when no session is loaded.</summary>
        public static bool TryReadCurrent(out double largeMps, out double smallMps)
        {
            largeMps = 0;
            smallMps = 0;
            MyEnvironmentDefinition env = MySector.EnvironmentDefinition;
            if (env == null) return false;
            largeMps = GridSpeedPolicy.Clamp(env.LargeShipMaxSpeed);
            smallMps = GridSpeedPolicy.Clamp(env.SmallShipMaxSpeed);
            return true;
        }

        /// <summary>
        /// Pushes any persisted override onto SE's environment definition. Called at session load
        /// BEFORE the Nexus authority gate, because a grid speed limit is a property of the world
        /// this process is simulating physics for, not of whether it owns the economy — a deferred
        /// cluster member still runs its own physics.
        /// </summary>
        /// <returns>A log line describing what was applied, or null when nothing was overridden.</returns>
        public static string ApplyPersistedOverride(FactionTuning tuning)
        {
            if (tuning == null) return null;
            MyEnvironmentDefinition env = MySector.EnvironmentDefinition;
            if (env == null) return null;

            bool wantLarge = GridSpeedPolicy.IsOverride(tuning.GridMaxSpeedLarge);
            bool wantSmall = GridSpeedPolicy.IsOverride(tuning.GridMaxSpeedSmall);
            if (!wantLarge && !wantSmall) return null;

            double wasLarge = env.LargeShipMaxSpeed;
            double wasSmall = env.SmallShipMaxSpeed;
            if (wantLarge) env.LargeShipMaxSpeed = (float)GridSpeedPolicy.Clamp(tuning.GridMaxSpeedLarge);
            if (wantSmall) env.SmallShipMaxSpeed = (float)GridSpeedPolicy.Clamp(tuning.GridMaxSpeedSmall);

            return "Grid speed override applied: large "
                   + Fmt(wasLarge) + " -> " + Fmt(env.LargeShipMaxSpeed)
                   + " m/s, small " + Fmt(wasSmall) + " -> " + Fmt(env.SmallShipMaxSpeed) + " m/s.";
        }

        /// <summary>
        /// Sets one or both limits, persists them, recomputes the derived fleet speed floor and tells
        /// every linked client. Either argument may be null to leave that grid size untouched.
        /// </summary>
        /// <returns>A human-readable report for the command that asked.</returns>
        public string Set(double? largeMps, double? smallMps, FactionStore store)
        {
            MyEnvironmentDefinition env = MySector.EnvironmentDefinition;
            if (env == null) return "gridspeed: no session loaded.";
            if (!largeMps.HasValue && !smallMps.HasValue) return Describe();

            double wasLarge = env.LargeShipMaxSpeed;
            double wasSmall = env.SmallShipMaxSpeed;
            double wasFloor = _tuning.FleetSpeedMinKmPerMin;

            if (largeMps.HasValue)
            {
                double v = GridSpeedPolicy.Clamp(largeMps.Value);
                env.LargeShipMaxSpeed = (float)v;
                _tuning.GridMaxSpeedLarge = v;
            }
            if (smallMps.HasValue)
            {
                double v = GridSpeedPolicy.Clamp(smallMps.Value);
                env.SmallShipMaxSpeed = (float)v;
                _tuning.GridMaxSpeedSmall = v;
            }

            // The floor is derived from the large-grid limit at session load; changing the limit
            // without recomputing it leaves the transit model describing the previous world.
            _tuning.FleetSpeedMinKmPerMin = GridSpeedPolicy.SpeedFloorKmPerMin(env.LargeShipMaxSpeed);

            if (store != null)
            {
                try { store.SaveTuning(_tuning); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "gridspeed: saving tuning.json failed; the change is live but will not survive a restart.");
                    return "gridspeed: applied live, but SAVING FAILED -- it will not survive a restart. See the Torch log.";
                }
            }

            int told = Broadcast();

            string report = "gridspeed: large " + Fmt(wasLarge) + " -> " + Fmt(env.LargeShipMaxSpeed)
                          + " m/s, small " + Fmt(wasSmall) + " -> " + Fmt(env.SmallShipMaxSpeed) + " m/s"
                          + "; fleet speed floor " + Fmt(wasFloor) + " -> " + Fmt(_tuning.FleetSpeedMinKmPerMin) + " km/min"
                          + "; persisted to tuning.json; " + told + " client(s) told."
                          + " Already-loaded grids keep their old physics ceiling until they reload;"
                          + (CharacterSpeedCapPatch.Bound
                              ? " characters follow the new limit at once."
                              : " jetpacking characters already in the world snap to the new suit limit"
                                + " once past the old one, until they respawn.");
            if (_log != null) _log(report);
            FactionsPlugin.Log.Info(report);
            return report;
        }

        /// <summary>Read-only report for `factions gridspeed` with no arguments.</summary>
        public string Describe()
        {
            double large, small;
            if (!TryReadCurrent(out large, out small)) return "gridspeed: no session loaded.";

            string source = GridSpeedPolicy.IsOverride(_tuning.GridMaxSpeedLarge)
                            || GridSpeedPolicy.IsOverride(_tuning.GridMaxSpeedSmall)
                ? "tuning.json override"
                : "the world's own environment definition (no override set)";

            return "gridspeed: large=" + Fmt(large) + " m/s, small=" + Fmt(small) + " m/s"
                   + " (from " + source + ")"
                   + "; fleet speed floor " + Fmt(_tuning.FleetSpeedMinKmPerMin) + " km/min"
                   + "; " + (_uplink != null ? _uplink.LinkedCount : 0) + " client(s) linked."
                   + " Set with 'factions gridspeed <large> [small]'.";
        }

        /// <summary>Tells every client granted the feature. Returns how many were told.</summary>
        public int Broadcast()
        {
            double large, small;
            if (_uplink == null || !TryReadCurrent(out large, out small)) return 0;
            return _uplink.SendToAll(WireFeatures.GridSpeed, FactionsWire.WriteGridSpeedConfig(large, small));
        }

        private void OnClientLinked(ulong steamId)
        {
            double large, small;
            if (_uplink == null || !TryReadCurrent(out large, out small)) return;
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.GridSpeed) == 0) return;
            _uplink.Send(steamId, FactionsWire.WriteGridSpeedConfig(large, small));
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
