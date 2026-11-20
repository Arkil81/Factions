using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Factions.Wire;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Applies the operator's missile speed multiplier to SE's ammo definitions and tells linked
    /// clients about it.
    ///
    /// <para><b>Why a multiplier exists.</b> SE's ammo content is balanced against a 100 m/s grid
    /// speed limit. <see cref="GridSpeedManager"/> raises this world's limit well past that and the
    /// ammo does not move with it, so vanilla artillery and the assault cannon (both 500 m/s) fire
    /// at exactly the large-grid cap and never close on a receding grid.
    /// <see cref="MissileVelocityInheritancePatch"/> fixes the half of that which is about the
    /// launcher's own motion; this fixes the half that is about the world's scale, and only the
    /// operator can decide the number. <see cref="MunitionSpeedPolicy"/> owns the arithmetic.</para>
    ///
    /// <para><b>Only missile-class ammo is scaled.</b> <c>ProjectileAmmoDefinition</c> rounds
    /// (gatling, autocannon) already inherit the firer's velocity through
    /// <c>MyProjectile.SetInitialVelocities</c> with no clamp, so they scale with the world on their
    /// own; multiplying them as well would move them twice.</para>
    ///
    /// <para><b>Why baselines are kept.</b> The scale is settable live, and a definition that has
    /// already been multiplied is not the definition the next multiplier should be applied to. Every
    /// field is stored as it loaded on first touch and every apply recomputes from that, so setting
    /// 5 and then 2 gives 2x the definition rather than 10x it — and setting 1 puts the world back
    /// exactly as its content authors wrote it.</para>
    ///
    /// <para><b>Why the client has to be told.</b> A missile replicates as an object builder and
    /// each client then simulates its own copy against its OWN definitions, clamping it at its own
    /// <c>DesiredSpeed</c>. A server-side scale is invisible there, so a client granted
    /// <see cref="WireFeatures.GridSpeed"/> is sent the multiplier and applies it locally — the same
    /// arrangement, and the same feature bit, as the grid speed limits it already mirrors.</para>
    /// </summary>
    internal sealed class MunitionSpeedManager
    {
        /// <summary>A missile ammo definition's speed fields exactly as its content author wrote
        /// them. Captured once, before this ever multiplies anything.</summary>
        private struct Baseline
        {
            public float DesiredSpeed;
            public float InitialSpeed;
            public float Acceleration;
        }

        private static readonly Dictionary<MyDefinitionId, Baseline> Baselines =
            new Dictionary<MyDefinitionId, Baseline>();

        /// <summary>The scale currently written onto the definitions, which is not the same thing as
        /// the tuning value until an apply has run.</summary>
        private static double _appliedScale = MunitionSpeedPolicy.NoScale;

        private readonly FactionTuning _tuning;
        private readonly UplinkManager _uplink;
        private readonly Action<string> _log;

        public MunitionSpeedManager(FactionTuning tuning, UplinkManager uplink, Action<string> log)
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

        /// <summary>The multiplier the definitions are actually carrying right now.</summary>
        public static double AppliedScale { get { return _appliedScale; } }

        /// <summary>
        /// Pushes any persisted multiplier onto the loaded ammo definitions. Called at session load
        /// beside <see cref="GridSpeedManager.ApplyPersistedOverride"/> and for the same reason: how
        /// fast a round flies is a property of the world this process simulates physics for, not of
        /// whether it owns the economy, so a deferred cluster member applies it too.
        /// </summary>
        /// <returns>A log line describing what was applied, or null when nothing was overridden.</returns>
        public static string ApplyPersistedOverride(FactionTuning tuning)
        {
            if (tuning == null) return null;
            if (!MunitionSpeedPolicy.IsOverride(tuning.MissileSpeedScale)) return null;
            return Apply(tuning.MissileSpeedScale);
        }

        /// <summary>
        /// Sets the multiplier, persists it, re-applies the definitions from their baselines and
        /// tells every linked client.
        /// </summary>
        /// <returns>A human-readable report for the command that asked.</returns>
        public string Set(double scale, FactionStore store)
        {
            double was = _appliedScale;
            _tuning.MissileSpeedScale = MunitionSpeedPolicy.Clamp(scale);
            string applied = Apply(_tuning.MissileSpeedScale);

            if (store != null)
            {
                try { store.SaveTuning(_tuning); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "missilespeed: saving tuning.json failed; the change is live but will not survive a restart.");
                    return "missilespeed: applied live, but SAVING FAILED -- it will not survive a restart. See the Torch log.";
                }
            }

            int told = Broadcast();

            string report = "missilespeed: scale " + Fmt(was) + " -> " + Fmt(_appliedScale)
                          + "; " + applied
                          + "; persisted to tuning.json; " + told + " client(s) told."
                          + " Rounds already in flight keep the speed they launched with.";
            if (_log != null) _log(report);
            FactionsPlugin.Log.Info(report);
            return report;
        }

        /// <summary>Read-only report for `factions missilespeed` with no arguments.</summary>
        public string Describe()
        {
            if (MyDefinitionManager.Static == null) return "missilespeed: no definitions loaded.";

            string fastestName;
            float fastestSpeed;
            int count = Survey(out fastestName, out fastestSpeed);

            string inheritance = MissileVelocityInheritancePatch.HasApplied
                ? "firer velocity inherited (" + (MissileVelocityCeilingPatch.HasRaised
                    ? "ceiling raised at least once"
                    : "no shot has yet needed a raised ceiling")
                  + "; " + MissileFirerFrame.Framed + " round(s) flown in their firer's frame"
                  + "; " + MissileSpawnAnchors.Anchored + " spawn(s) moved with their firer, largest "
                  + Fmt(MissileSpawnAnchors.MaxShiftMeters) + " m)"
                : "firer-velocity patch bound but not yet exercised";

            return "missilespeed: scale=" + Fmt(_appliedScale)
                   + " over " + count + " missile ammo definition(s)"
                   + (fastestName != null
                        ? "; fastest " + fastestName + " " + Fmt(fastestSpeed) + " m/s"
                        : "")
                   + "; " + inheritance
                   + "; " + (_uplink != null ? _uplink.LinkedCount : 0) + " client(s) linked."
                   + " Projectile ammo (gatling, autocannon) is not scaled -- it already inherits."
                   + " Set with 'factions missilespeed <scale>' (1 = the definitions as loaded).";
        }

        /// <summary>Tells every client granted the feature. Returns how many were told.</summary>
        public int Broadcast()
        {
            if (_uplink == null) return 0;
            return _uplink.SendToAll(WireFeatures.GridSpeed,
                                     FactionsWire.WriteMunitionSpeedConfig(_appliedScale));
        }

        private void OnClientLinked(ulong steamId)
        {
            if (_uplink == null) return;
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.GridSpeed) == 0) return;
            _uplink.Send(steamId, FactionsWire.WriteMunitionSpeedConfig(_appliedScale));
        }

        /// <summary>
        /// Writes the scale onto every missile ammo definition, always recomputed from the captured
        /// baseline rather than from whatever is on the definition now.
        /// </summary>
        /// <returns>A phrase describing what was touched, for the caller's log line.</returns>
        private static string Apply(double scale)
        {
            if (MyDefinitionManager.Static == null) return "no definitions loaded, so nothing was scaled";

            double clamped = MunitionSpeedPolicy.Clamp(scale);
            int viaMagazines, viaTypedStore;
            List<MyMissileAmmoDefinition> ammo = MissileAmmo(out viaMagazines, out viaTypedStore);

            string fastestName = null;
            float fastestSpeed = 0f;
            foreach (MyMissileAmmoDefinition def in ammo)
            {
                Baseline b;
                if (!Baselines.TryGetValue(def.Id, out b))
                {
                    b = new Baseline
                    {
                        DesiredSpeed = def.DesiredSpeed,
                        InitialSpeed = def.MissileInitialSpeed,
                        Acceleration = def.MissileAcceleration
                    };
                    Baselines[def.Id] = b;
                }

                def.DesiredSpeed = MunitionSpeedPolicy.ScaleSpeed(b.DesiredSpeed, clamped);
                def.MissileInitialSpeed = MunitionSpeedPolicy.ScaleSpeed(b.InitialSpeed, clamped);
                def.MissileAcceleration = MunitionSpeedPolicy.ScaleAcceleration(b.Acceleration, clamped);

                if (def.DesiredSpeed > fastestSpeed)
                {
                    fastestSpeed = def.DesiredSpeed;
                    fastestName = def.Id.SubtypeName;
                }
            }

            _appliedScale = clamped;

            // Both enumeration counts are reported rather than just the union: SE keeps resolved
            // definitions in disjoint stores, and a route that silently returns nothing produces a
            // plausible-looking line with half the world unscaled.
            return "x" + Fmt(clamped) + " over " + ammo.Count + " missile ammo definition(s)"
                   + " (" + viaMagazines + " via ammo magazines, " + viaTypedStore + " via the typed store)"
                   + (fastestName != null
                        ? "; fastest is now " + fastestName + " at " + Fmt(fastestSpeed) + " m/s ("
                          + Fmt(fastestSpeed / 60.0) + " m per physics frame)"
                        : "");
        }

        /// <summary>Counts what a scale would touch and names the fastest round, without writing
        /// anything.</summary>
        private static int Survey(out string fastestName, out float fastestSpeed)
        {
            fastestName = null;
            fastestSpeed = 0f;
            int viaMagazines, viaTypedStore;
            List<MyMissileAmmoDefinition> ammo = MissileAmmo(out viaMagazines, out viaTypedStore);
            foreach (MyMissileAmmoDefinition def in ammo)
            {
                if (def.DesiredSpeed <= fastestSpeed) continue;
                fastestSpeed = def.DesiredSpeed;
                fastestName = def.Id.SubtypeName;
            }
            return ammo.Count;
        }

        /// <summary>
        /// Every missile ammo definition this world can fire.
        ///
        /// <para><c>MyDefinitionManager</c> exposes no enumerator for ammo — the dictionary is
        /// internal and only <c>TryGetAmmoDefinition(id)</c> is public — so the set is reached
        /// through the ammo magazines that name it, which is also exactly the set a weapon can load.
        /// The typed store is walked as well because SE's resolved definitions live in two disjoint
        /// dictionaries and neither is the whole picture; the union is what gets scaled and both
        /// counts are reported so an empty route is visible rather than silent.</para>
        /// </summary>
        private static List<MyMissileAmmoDefinition> MissileAmmo(out int viaMagazines, out int viaTypedStore)
        {
            var found = new Dictionary<MyDefinitionId, MyMissileAmmoDefinition>();
            // The two stores overlap, so magazines are de-duplicated before being counted --
            // otherwise the reported number says how many dictionary entries were walked rather
            // than how many magazines exist.
            var magazinesSeen = new HashSet<MyDefinitionId>();
            viaTypedStore = 0;

            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
                AddFromMagazine(def, found, magazinesSeen);
            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions<MyDefinitionBase>())
                AddFromMagazine(def, found, magazinesSeen);
            viaMagazines = magazinesSeen.Count;

            foreach (MyAmmoDefinition def in MyDefinitionManager.Static.GetAllDefinitions<MyAmmoDefinition>())
            {
                var missile = def as MyMissileAmmoDefinition;
                if (missile == null) continue;
                viaTypedStore++;
                found[missile.Id] = missile;
            }

            return new List<MyMissileAmmoDefinition>(found.Values);
        }

        /// <summary>Resolves one definition as an ammo magazine and records the missile ammo it
        /// names.</summary>
        private static void AddFromMagazine(MyDefinitionBase def,
                                            Dictionary<MyDefinitionId, MyMissileAmmoDefinition> found,
                                            HashSet<MyDefinitionId> magazinesSeen)
        {
            var magazine = def as MyAmmoMagazineDefinition;
            if (magazine == null) return;
            if (!magazinesSeen.Add(magazine.Id)) return;

            MyAmmoDefinition ammo;
            if (!MyDefinitionManager.Static.TryGetAmmoDefinition(magazine.AmmoDefinitionId, out ammo)) return;

            var missile = ammo as MyMissileAmmoDefinition;
            if (missile == null) return;

            found[missile.Id] = missile;
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
