using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Sandbox.Definitions;             // MyAmmoDefinition, MyMissileAmmoDefinition
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;            // MyGunBase, MyLargeTurretBase
using VRage.Game.Entity;               // MyEntity
using VRage.Game.ModAPI;               // IMyGunObject<T>
using VRage.Game.ObjectBuilders.Components;   // MySafeZoneAction
using VRage.Sync;
using VRageMath;
using IngameGun = Sandbox.ModAPI.Ingame.IMyUserControllableGun;
using IngameTurret = Sandbox.ModAPI.Ingame.IMyLargeTurretBase;

namespace Factions
{
    /// <summary>
    /// Aimed fire: a fighting hull whose fixed guns carry most of its firepower points its nose at a
    /// lead-predicted intercept and pulls their triggers inside an aim cone; every turret on a
    /// fighting hull is focused on the hull's combat target. Only while the hull's fight has
    /// <see cref="CombatState.AimedFire"/>. The geometry is Core's (<see cref="AimedFire"/>); this is
    /// the reading of guns, ammo and grids, and the SE calls.
    ///
    /// <para><b>Firing.</b> <c>IMyUserControllableGun.Shoot</c> — the terminal's own <i>Shoot</i>
    /// switch: it sets the gun's <c>m_forceShoot</c> sync, which replicates, and the gun then fires
    /// through its own update, <c>CanShoot</c>, reload and ammo, so rounds come out of the magazines
    /// <see cref="FleetMunitionLoader"/> loaded and are counted by <c>MyGunBase.ConsumeAmmo</c>. Set
    /// only on a transition, and always set back off when the fight or the control ends — a gun left
    /// on keeps firing, and the flag is saved with the block.</para>
    ///
    /// <para><b>Turret focus.</b> SE's Warfare 2 focus (<c>MyLargeTurretBase.m_focusedTarget</c>,
    /// private, reflected): the turret's own AI puts the focused grid first in its target selection and
    /// still picks the block, checks visibility and fires. The grid group's lock system clears a focus
    /// on any lock change, so it is re-asserted at every read. The spike behind this is in
    /// docs/issues/FAC-403.</para>
    ///
    /// <para><b>Fire into a zone</b> (FAC-490). SE's turret AI never targets
    /// anything inside a safe zone that forbids shooting, focus included, so a target sitting in one
    /// gets the hull's turrets driven at it instead: each turret in range takes the target's centre as
    /// a manual position (<c>IMyLargeTurretBase.SetTarget(Vector3D)</c>, which the AI aims at but
    /// never fires on) and its Shoot switch is on while its barrel points into the zone. Not focused
    /// meanwhile — a focus write resets the targeting. Handed back with
    /// <c>ResetTargetingToDefault</c> once the zone is down or the fight ends. A round aimed at a
    /// point checks no line of sight.</para>
    ///
    /// <para><b>Paced</b> (<see cref="ZoneSiegePacing"/>). Fire into a zone — driven turrets and fixed
    /// guns alike — follows the siege rather than the trigger: open to start it, a
    /// keep-alive burst whenever the zone has gone two thirds of its refill time without a hit, and
    /// continuous fire only from the moment a full pool would just be emptied as the breach comes due.
    /// Re-decided at the 1 Hz read.</para>
    /// </summary>
    internal sealed partial class FleetFlightController
    {
        /// <summary>A hull's guns are re-read this often while it fights, so a gun destroyed, or one
        /// that has switched magazine, reaches the aim.</summary>
        private static readonly TimeSpan GunsRefresh = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan AimTraceInterval = TimeSpan.FromSeconds(5);

        private sealed class GunGroup
        {
            public double LaunchMs, DesiredMs, AccelMs2;
            public bool Inherits;
            public double RangeM;
            public double Dps;
            public readonly List<MyCubeBlock> Guns = new List<MyCubeBlock>();
            public bool Firing;

            // The last tick's reading, for the trace and the transition line.
            public double ErrRad = double.NaN;
            public double ConeRad = double.NaN;
            public double InterceptRangeM = double.NaN;
            public double TimeSec = double.NaN;
            public FireGate Gate = FireGate.NoSolution;
        }

        private sealed class AimState
        {
            public DateTime GunsReadUtc;
            public readonly List<GunGroup> Groups = new List<GunGroup>();
            public readonly List<MyLargeTurretBase> Turrets = new List<MyLargeTurretBase>();
            public double AimableDps, FixedDps, TotalDps;
            public bool AimsNose;
            public int Primary = -1;

            public long TargetId;
            public bool TargetHostile;
            public long FocusTargetId;
            public int Refocused;
            /// <summary>The hull is flying <see cref="ManoeuvreKind.Disengage"/>: its guns hold fire
            /// and its turrets keep their own targeting until it fights again.</summary>
            public bool Disengaging;
            public long AnnouncedTargetId;
            public DateTime NextTraceUtc;

            /// <summary>The turrets this hull is driving into its target's zone, and the zone they aim
            /// into. Empty while the target is not inside one.</summary>
            public readonly List<DrivenTurret> Driven = new List<DrivenTurret>();
            public Vector3D ZoneCentre;
            public double ZoneRadius;
            public long ZoneId;

            /// <summary>The target sits in a zone that forbids shooting, so every gun's fire on it is
            /// paced; <see cref="ZoneFireAllowed"/> is this second's answer.</summary>
            public bool ZonePaced;
            public bool ZoneFireAllowed;
            public ZoneFireCall ZoneCall;
            public DateTime LastZoneBurstUtc;
        }

        private sealed class DrivenTurret
        {
            public MyLargeTurretBase Turret;
            public bool Firing;
        }

        private static readonly FieldInfo FocusedTargetField =
            typeof(MyLargeTurretBase).GetField("m_focusedTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FocusedTargetByField =
            typeof(MyLargeTurretBase).GetField("m_focusedTargetBy", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool _focusMissLogged;

        /// <summary>A missile round carries its firer's velocity only while all three inheritance
        /// patches are bound; a projectile round always does.</summary>
        private static bool MissilesInherit
        {
            get
            {
                return MissileVelocityInheritancePatch.Bound && MissileVelocityCeilingPatch.Bound
                    && MissileSpeedHoldPatch.Bound;
            }
        }

        /// <summary>The once-a-second aim read, after the tactical read has a live target: re-reads
        /// the hull's guns every <see cref="GunsRefresh"/>, whether its target is still hostile, and
        /// focuses its turrets on the target.</summary>
        private void ReadAim(FlightState st, CombatState c, DateTime nowUtc)
        {
            MyCubeGrid grid = st.Grid;
            if (grid == null || grid.Closed || grid.MarkedForClose || !TargetLive(c)) return;

            AimState a = st.Aim;
            if (a == null)
            {
                a = new AimState();
                st.Aim = a;
            }
            if (a.GunsReadUtc == DateTime.MinValue || nowUtc - a.GunsReadUtc >= GunsRefresh)
                ReadGuns(st, a, nowUtc);

            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            a.TargetId = c.Target.EntityId;
            a.TargetHostile = owner != 0 && IsHostileGrid(c.Target, grid, owner);

            bool engage = a.TargetHostile && !a.Disengaging;
            MySafeZone noShoot = engage ? NoShootZoneAt(c.Target) : null;
            PaceZoneFire(st, a, c.Target, noShoot, nowUtc);
            if (noShoot != null)
            {
                FocusTurrets(st, a, 0);
                DriveTurrets(st, a, c.Target, noShoot);
            }
            else
            {
                if (a.Driven.Count > 0) ReleaseTurrets(st, a, engage ? "zone_open" : "not_engaged");
                FocusTurrets(st, a, engage ? a.TargetId : 0);
            }

            if (DebugTactics && a.AnnouncedTargetId != a.TargetId)
            {
                a.AnnouncedTargetId = a.TargetId;
                GunGroup p = a.Primary >= 0 ? a.Groups[a.Primary] : null;
                Log.Info("AIM_ENGAGE eid=" + st.EntityId
                         + " target=" + a.TargetId
                         + " aims=" + (a.AimsNose ? "yes" : "no")
                         + " fixedDps=" + a.FixedDps.ToString("F0")
                         + " alignedDps=" + a.AimableDps.ToString("F0")
                         + " totalDps=" + a.TotalDps.ToString("F0")
                         + " groups=" + a.Groups.Count
                         + " turrets=" + a.Turrets.Count
                         + (p != null
                             ? " speed=" + p.DesiredMs.ToString("F0") + " inherits=" + (p.Inherits ? "yes" : "no")
                               + " range=" + p.RangeM.ToString("F0")
                             : ""));
            }
        }

        /// <summary>Sorts a hull's functional weapons into turrets and nose-aligned fixed-gun groups
        /// (one group per round ballistics), and decides whether it aims its nose
        /// (<see cref="AimedFire.AimsNose"/>). A gun that stops qualifying is switched off.</summary>
        private void ReadGuns(FlightState st, AimState a, DateTime nowUtc)
        {
            a.GunsReadUtc = nowUtc;
            List<GunGroup> old = a.Groups.Count > 0 ? new List<GunGroup>(a.Groups) : null;
            a.Groups.Clear();
            a.Turrets.Clear();
            a.AimableDps = a.FixedDps = a.TotalDps = 0;
            a.Primary = -1;

            Vector3D nose = ReferenceMatrix(st).Forward;
            bool missilesInherit = MissilesInherit;
            foreach (MyCubeBlock block in st.Grid.GetFatBlocks())
            {
                if (block == null || !block.IsFunctional) continue;
                MyCubeBlockDefinition def = block.BlockDefinition;
                if (def == null || !WeaponStatsHelper.IsScoreableWeapon(def)) continue;
                WeaponBand stats = WeaponStats(def);
                if (stats.Dps <= 0) continue;
                a.TotalDps += stats.Dps;

                var turret = block as MyLargeTurretBase;
                if (turret != null)
                {
                    a.Turrets.Add(turret);
                    continue;
                }
                if (!(block is IngameGun)) continue;
                a.FixedDps += stats.Dps;
                if (Vector3D.Dot(block.WorldMatrix.Forward, nose) < AimedFire.AlignedGunCos) continue;

                var gunObject = block as IMyGunObject<MyGunBase>;
                MyAmmoDefinition ammo = gunObject != null && gunObject.GunBase != null
                    ? gunObject.GunBase.CurrentAmmoDefinition
                    : null;
                if (ammo == null) continue;

                double launch, desired, accel;
                bool inherits;
                var missile = ammo as MyMissileAmmoDefinition;
                if (missile != null)
                {
                    desired = missile.DesiredSpeed;
                    launch = missile.MissileSkipAcceleration ? desired : missile.MissileInitialSpeed;
                    accel = missile.MissileSkipAcceleration ? 0 : missile.MissileAcceleration;
                    inherits = missilesInherit;
                }
                else
                {
                    desired = launch = ammo.DesiredSpeed;
                    accel = 0;
                    inherits = true;
                }
                if (!(desired > 0)) continue;

                GunGroup g = FindGroup(a.Groups, launch, desired, accel, inherits);
                if (g == null)
                {
                    g = new GunGroup { LaunchMs = launch, DesiredMs = desired, AccelMs2 = accel, Inherits = inherits };
                    a.Groups.Add(g);
                }
                g.Guns.Add(block);
                g.Dps += stats.Dps;
                if (stats.RangeM > g.RangeM) g.RangeM = stats.RangeM;
                a.AimableDps += stats.Dps;
            }

            for (int i = 0; i < a.Groups.Count; i++)
                if (a.Primary < 0 || a.Groups[i].Dps > a.Groups[a.Primary].Dps) a.Primary = i;
            a.AimsNose = AimedFire.AimsNose(a.AimableDps, a.TotalDps);

            // Carry each group's trigger state across the re-read; a gun no longer in any firing group
            // is switched off.
            if (old == null) return;
            for (int i = 0; i < old.Count; i++)
            {
                GunGroup was = old[i];
                if (!was.Firing) continue;
                GunGroup now = FindGroup(a.Groups, was.LaunchMs, was.DesiredMs, was.AccelMs2, was.Inherits);
                if (now != null && a.AimsNose) now.Firing = true;
                for (int j = 0; j < was.Guns.Count; j++)
                    if (now == null || !a.AimsNose || !now.Guns.Contains(was.Guns[j])) SetShooting(was.Guns[j], false);
                if (now != null && a.AimsNose)
                    for (int j = 0; j < now.Guns.Count; j++)
                        if (!was.Guns.Contains(now.Guns[j])) SetShooting(now.Guns[j], true);
            }
        }

        private static GunGroup FindGroup(List<GunGroup> groups, double launch, double desired, double accel, bool inherits)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                GunGroup g = groups[i];
                if (g.Inherits == inherits && Math.Abs(g.LaunchMs - launch) < 1 && Math.Abs(g.DesiredMs - desired) < 1
                    && Math.Abs(g.AccelMs2 - accel) < 1)
                    return g;
            }
            return null;
        }

        private static WeaponBand WeaponStats(MyCubeBlockDefinition def)
        {
            WeaponBand stats;
            if (!WeaponStatsByDefinition.TryGetValue(def.Id, out stats))
            {
                float dps, rangeM;
                WeaponStatsHelper.ComputeWeaponStats(def, out dps, out rangeM);
                stats = new WeaponBand { Dps = dps, RangeM = rangeM };
                WeaponStatsByDefinition[def.Id] = stats;
            }
            return stats;
        }

        /// <summary>Per tick, for a hull whose fight has aimed fire: faces the primary gun group's
        /// intercept (turning the command's facing only — translation is the manoeuvre's) and gates
        /// every group's trigger. Returns whether the facing is an aim, which lets the attitude
        /// pitch. Ends the aim when the hull is not fighting with aimed fire.</summary>
        private bool AimTick(FlightState st, CombatState combat, ManoeuvreKind kind, Vector3 velWorld,
                             ref FlightCommand cmd, DateTime nowUtc)
        {
            AimState a = st.Aim;
            // A retreating hull turns its tail to the enemy, so it holds fire: its guns stop and the
            // turret focus this controller set is cleared (the turrets' own AI still defends it).
            if (kind == ManoeuvreKind.Disengage)
            {
                if (a != null && !a.Disengaging) HoldFire(st, a);
                return false;
            }
            if (a != null) a.Disengaging = false;
            if (combat == null || !combat.AimedFire)
            {
                if (a != null) EndAim(st, combat == null ? "fight_over" : "switched_off");
                return false;
            }
            if (a == null) return false;
            if (a.Driven.Count > 0) ZoneFireTick(st, a);

            // A target the last read did not see is not aimed at: the guns hold fire.
            bool mayAim = a.AimsNose && a.Primary >= 0 && a.TargetId == combat.TargetId;
            bool paceAllows = !a.ZonePaced || a.ZoneFireAllowed;
            MyCubeGrid target = combat.Target;
            Vector3D targetPos = target.PositionComp.WorldVolume.Center;
            double targetRadius = target.PositionComp.WorldVolume.Radius;
            Vector3 tv = target.Physics != null ? target.Physics.LinearVelocity : Vector3.Zero;
            Vec3 targetVel = new Vec3(tv.X, tv.Y, tv.Z);
            Vec3 shooterVel = new Vec3(velWorld.X, velWorld.Y, velWorld.Z);
            double distance = (targetPos - st.Grid.PositionComp.WorldVolume.Center).Length();

            for (int i = 0; i < a.Groups.Count; i++)
            {
                GunGroup g = a.Groups[i];
                MyCubeBlock gun = FirstLiveGun(g);
                bool fire = false;
                if (mayAim && gun != null)
                {
                    Vector3D muzzle = gun.WorldMatrix.Translation;
                    Vector3D axis = gun.WorldMatrix.Forward;
                    double speed = AimedFire.EffectiveSpeedMs(g.LaunchMs, g.DesiredMs, g.AccelMs2, distance);
                    AimSolution sol = AimedFire.Intercept(ToVec3(muzzle), shooterVel, ToVec3(targetPos), targetVel,
                                                          speed, g.Inherits);
                    g.TimeSec = sol.Valid ? sol.TimeSec : double.NaN;
                    g.InterceptRangeM = sol.Valid ? (ToVector3D(sol.InterceptM) - muzzle).Length() : double.NaN;
                    g.ErrRad = sol.Valid ? AimedFire.AngleRad(ToVec3(axis), sol.AimDir) : double.NaN;
                    g.ConeRad = AimedFire.ConeRad(sol.Valid ? g.InterceptRangeM : distance, targetRadius);
                    g.Gate = AimedFire.Gate(a.TargetHostile, g.Firing, sol, g.ErrRad, g.ConeRad, g.InterceptRangeM, g.RangeM);
                    fire = g.Gate == FireGate.Fire && paceAllows;

                    if (i == a.Primary && sol.Valid)
                    {
                        Vec3 noseDir = AimedFire.NoseFor(sol.AimDir, ToVec3(axis), ToVec3(ReferenceMatrix(st).Forward));
                        Vector3D pos = st.Grid.PositionComp.GetPosition();
                        cmd.FaceTargetWorld = ToVec3(pos + ToVector3D(noseDir) * 1000.0);
                    }
                }
                else
                {
                    g.Gate = a.TargetHostile ? FireGate.OffAxis : FireGate.NotHostile;
                }
                if (fire != g.Firing) SetGroupFiring(st, a, g, fire, i);
            }

            if (DebugTactics && a.Primary >= 0 && nowUtc >= a.NextTraceUtc)
            {
                a.NextTraceUtc = nowUtc + AimTraceInterval;
                GunGroup p = a.Groups[a.Primary];
                Log.Info("AIM_TRACE eid=" + st.EntityId
                         + " target=" + a.TargetId
                         + " kind=" + kind
                         + " errDeg=" + Deg(p.ErrRad)
                         + " coneDeg=" + Deg(p.ConeRad)
                         + " range=" + distance.ToString("F0")
                         + " tof=" + (double.IsNaN(p.TimeSec) ? "-" : p.TimeSec.ToString("F2"))
                         + " gate=" + p.Gate
                         + " firing=" + FiringGroups(a) + "/" + a.Groups.Count
                         + " refocused=" + a.Refocused);
            }
            return mayAim;
        }

        private static MyCubeBlock FirstLiveGun(GunGroup g)
        {
            for (int i = 0; i < g.Guns.Count; i++)
            {
                MyCubeBlock b = g.Guns[i];
                if (b != null && !b.Closed && !b.MarkedForClose && b.IsFunctional) return b;
            }
            return null;
        }

        private static int FiringGroups(AimState a)
        {
            int n = 0;
            for (int i = 0; i < a.Groups.Count; i++) if (a.Groups[i].Firing) n++;
            return n;
        }

        private static string Deg(double rad)
        {
            return double.IsNaN(rad) ? "-" : (rad * 180.0 / Math.PI).ToString("F1");
        }

        /// <summary>The start of a <see cref="ManoeuvreKind.Disengage"/>: every gun group is switched
        /// off (<c>AIM_FIRE … state=off reason=disengage</c> per group) and the turret focus cleared.</summary>
        private static void HoldFire(FlightState st, AimState a)
        {
            a.Disengaging = true;
            for (int i = 0; i < a.Groups.Count; i++) SetGroupFiring(st, a, a.Groups[i], false, i, "disengage");
            if (a.Driven.Count > 0) ReleaseTurrets(st, a, "disengage");
            if (a.FocusTargetId != 0) FocusTurrets(st, a, 0);
        }

        private static void SetGroupFiring(FlightState st, AimState a, GunGroup g, bool on, int index,
                                           string reason = null)
        {
            g.Firing = on;
            for (int i = 0; i < g.Guns.Count; i++) SetShooting(g.Guns[i], on);
            if (DebugTactics)
                Log.Info("AIM_FIRE eid=" + st.EntityId
                         + " target=" + a.TargetId
                         + " group=" + index
                         + " state=" + (on ? "on" : "off")
                         + " guns=" + g.Guns.Count
                         + " errDeg=" + Deg(g.ErrRad)
                         + " coneDeg=" + Deg(g.ConeRad)
                         + " range=" + (double.IsNaN(g.InterceptRangeM) ? "-" : g.InterceptRangeM.ToString("F0"))
                         + " reach=" + g.RangeM.ToString("F0")
                         + " tof=" + (double.IsNaN(g.TimeSec) ? "-" : g.TimeSec.ToString("F2"))
                         + " reason=" + (reason ?? g.Gate.ToString()));
        }

        private static void SetShooting(MyCubeBlock block, bool on)
        {
            if (block == null || block.Closed) return;
            var gun = block as IngameGun;
            if (gun == null) return;
            try
            {
                if (gun.Shoot != on) gun.Shoot = on;
            }
            catch (Exception ex)
            {
                Log.Warn("FleetFlightController: setting Shoot=" + on + " on block " + block.EntityId + " failed: " + ex.Message);
            }
        }

        /// <summary>Focuses every turret of the hull on <paramref name="targetId"/> (0 clears a focus
        /// this controller set). Written only where the turret holds something else, since a change
        /// resets the turret's current target; a write over a focus the lock system cleared counts
        /// in <see cref="AimState.Refocused"/>.</summary>
        private static void FocusTurrets(FlightState st, AimState a, long targetId)
        {
            if (FocusedTargetField == null || FocusedTargetByField == null)
            {
                if (!_focusMissLogged)
                {
                    _focusMissLogged = true;
                    Log.Warn("FleetFlightController: MyLargeTurretBase.m_focusedTarget did not resolve; "
                             + "turrets on fighting hulls keep their own targeting.");
                }
                return;
            }

            int written = 0;
            for (int i = 0; i < a.Turrets.Count; i++)
            {
                MyLargeTurretBase t = a.Turrets[i];
                if (t == null || t.Closed || t.MarkedForClose) continue;
                try
                {
                    var focus = FocusedTargetField.GetValue(t) as Sync<long, SyncDirection.FromServer>;
                    var by = FocusedTargetByField.GetValue(t) as Sync<long, SyncDirection.FromServer>;
                    if (focus == null || by == null) continue;
                    long current = focus.Value;
                    if (targetId == 0)
                    {
                        // Clears only a focus this controller wrote.
                        if (current == 0 || current != a.FocusTargetId || by.Value != st.EntityId) continue;
                        by.Value = 0;
                        focus.Value = 0;
                        written++;
                        continue;
                    }
                    if (current == targetId && by.Value == st.EntityId) continue;
                    by.Value = st.EntityId;
                    focus.Value = targetId;
                    written++;
                }
                catch (Exception ex)
                {
                    Log.Warn("FleetFlightController: turret focus on block " + t.EntityId + " failed: " + ex.Message);
                }
            }

            bool changed = a.FocusTargetId != targetId;
            if (!changed && written > 0) a.Refocused += written;
            a.FocusTargetId = targetId;
            if (changed && DebugTactics && (targetId != 0 || written > 0))
                Log.Info("TURRET_FOCUS eid=" + st.EntityId + " target=" + targetId + " turrets=" + a.Turrets.Count
                         + " written=" + written);
        }

        // ---- fire into a zone (FAC-490) -----------------------------------------------------------

        /// <summary>The enabled safe zone around <paramref name="target"/>'s centre that forbids
        /// shooting, or null when SE would let a turret fire at it.</summary>
        private static MySafeZone NoShootZoneAt(MyCubeGrid target)
        {
            if (target == null || target.Closed) return null;
            Vector3D centre = target.PositionComp.WorldVolume.Center;
            if (MySessionComponentSafeZones.IsActionAllowed(centre, MySafeZoneAction.Shooting, 0L, 0uL)) return null;
            var box = new BoundingBoxD(centre - new Vector3D(1, 1, 1), centre + new Vector3D(1, 1, 1));
            foreach (MySafeZone z in MySessionComponentSafeZones.GetSafeZonesInAABB(box))
                if (z != null && z.Enabled && !z.AllowedActions.HasFlag(MySafeZoneAction.Shooting)) return z;
            return null;
        }

        /// <summary>This second's pacing for fire on a target inside <paramref name="zone"/> (none: no
        /// pacing), from the fleet's whole weapon DPS. Logs <c>ZONE_FIRE_PACE eid=… call=…</c> under
        /// 'factions debug tactics' when the call changes.</summary>
        private void PaceZoneFire(FlightState st, AimState a, MyCubeGrid target, MySafeZone zone, DateTime nowUtc)
        {
            if (zone == null)
            {
                a.ZonePaced = false;
                a.ZoneFireAllowed = false;
                return;
            }
            double dps = a.TotalDps;
            IReadOnlyList<WeaponBand> bands = FleetBandsLookup != null ? FleetBandsLookup(st.EntityId) : null;
            if (bands != null && bands.Count > 0) dps = ShipPool.TotalDps(bands);
            double sinceBurst = a.LastZoneBurstUtc == DateTime.MinValue ? double.MaxValue
                                                                         : (nowUtc - a.LastZoneBurstUtc).TotalSeconds;
            ZoneFireCall call = ZonePacing != null ? ZonePacing(target.EntityId, dps, sinceBurst) : ZoneFireCall.Push;
            bool wasPaced = a.ZonePaced;
            a.ZonePaced = true;
            a.ZoneFireAllowed = ZoneSiegePacing.Fires(call);
            if (a.ZoneFireAllowed) a.LastZoneBurstUtc = nowUtc;
            if (DebugTactics && (!wasPaced || call != a.ZoneCall))
                Log.Info("ZONE_FIRE_PACE eid=" + st.EntityId + " target=" + target.EntityId + " zone=" + zone.EntityId
                         + " call=" + call + " dps=" + dps.ToString("F0"));
            a.ZoneCall = call;
        }

        /// <summary>The once-a-second half: every live turret within reach of the zone's surface is
        /// aimed at the target's centre, one that has fallen out of reach is handed back. Logs
        /// <c>ZONE_FIRE_ENGAGE</c> under 'factions debug tactics' when the zone or the set changes.</summary>
        private static void DriveTurrets(FlightState st, AimState a, MyCubeGrid target, MySafeZone zone)
        {
            BoundingSphereD zv = zone.PositionComp.WorldVolume;
            Vector3D aim = target.PositionComp.WorldVolume.Center;
            bool newZone = a.ZoneId != zone.EntityId;
            a.ZoneId = zone.EntityId;
            a.ZoneCentre = zv.Center;
            a.ZoneRadius = zv.Radius;
            int before = a.Driven.Count;

            for (int i = 0; i < a.Turrets.Count; i++)
            {
                MyLargeTurretBase t = a.Turrets[i];
                int at = DrivenIndex(a, t);
                bool live = t != null && !t.Closed && !t.MarkedForClose && t.IsFunctional;
                double toSurface = live ? (t.PositionComp.GetPosition() - zv.Center).Length() - zv.Radius : double.MaxValue;
                if (!live || toSurface > t.ShootRange)
                {
                    if (at >= 0) { Release(a.Driven[at]); a.Driven.RemoveAt(at); }
                    continue;
                }
                try
                {
                    if (at < 0 || !t.TargetingSystem.TargetPrediction.IsTargetPositionManual)
                        ((IngameTurret)t).SetTarget(aim);
                    if (at < 0) a.Driven.Add(new DrivenTurret { Turret = t });
                }
                catch (Exception ex)
                {
                    Log.Warn("FleetFlightController: aiming turret " + t.EntityId + " into a zone failed: " + ex.Message);
                }
            }
            // Turrets the last gun read no longer lists (destroyed, or no longer a weapon).
            for (int i = a.Driven.Count - 1; i >= 0; i--)
                if (!a.Turrets.Contains(a.Driven[i].Turret)) { Release(a.Driven[i]); a.Driven.RemoveAt(i); }

            if (DebugTactics && (newZone || a.Driven.Count != before))
                Log.Info("ZONE_FIRE_ENGAGE eid=" + st.EntityId + " target=" + target.EntityId + " zone=" + zone.EntityId
                         + " radius=" + zv.Radius.ToString("F0")
                         + " range=" + (aim - st.Grid.PositionComp.GetPosition()).Length().ToString("F0")
                         + " driven=" + a.Driven.Count + "/" + a.Turrets.Count);
        }

        /// <summary>Per tick: each driven turret's Shoot switch follows whether its barrel points into
        /// the zone, inside the same cone the fixed guns use with the zone as the target.</summary>
        private static void ZoneFireTick(FlightState st, AimState a)
        {
            for (int i = 0; i < a.Driven.Count; i++)
            {
                DrivenTurret d = a.Driven[i];
                MyLargeTurretBase t = d.Turret;
                if (t == null || t.Closed) continue;
                MyEntity barrel = t.Barrel != null ? t.Barrel.Entity : null;
                MatrixD m = barrel != null ? barrel.WorldMatrix : t.WorldMatrix;
                Vector3D toZone = a.ZoneCentre - m.Translation;
                double range = toZone.Length();
                bool fire = false;
                if (range > 1)
                {
                    double err = AimedFire.AngleRad(ToVec3(m.Forward), ToVec3(toZone / range));
                    fire = a.ZoneFireAllowed && err <= AimedFire.ConeRad(range, a.ZoneRadius);
                }
                if (fire == d.Firing) continue;
                d.Firing = fire;
                SetShooting(t, fire);
            }
        }

        /// <summary>Hands every driven turret back to its own AI. Logs <c>ZONE_FIRE_END</c> under
        /// 'factions debug tactics'.</summary>
        private static void ReleaseTurrets(FlightState st, AimState a, string reason)
        {
            int n = a.Driven.Count;
            for (int i = 0; i < n; i++) Release(a.Driven[i]);
            a.Driven.Clear();
            a.ZoneId = 0;
            if (DebugTactics)
                Log.Info("ZONE_FIRE_END eid=" + st.EntityId + " reason=" + reason + " turrets=" + n);
        }

        private static void Release(DrivenTurret d)
        {
            MyLargeTurretBase t = d.Turret;
            if (t == null || t.Closed) return;
            SetShooting(t, false);
            d.Firing = false;
            try { ((IngameTurret)t).ResetTargetingToDefault(); }
            catch (Exception ex)
            {
                Log.Warn("FleetFlightController: handing turret " + t.EntityId + " back to its AI failed: " + ex.Message);
            }
        }

        private static int DrivenIndex(AimState a, MyLargeTurretBase t)
        {
            for (int i = 0; i < a.Driven.Count; i++) if (a.Driven[i].Turret == t) return i;
            return -1;
        }

        /// <summary>Stops every gun this hull's aim switched on and clears the turret focus it set;
        /// drops the aim state. Safe on a closing grid.</summary>
        private static void EndAim(FlightState st, string reason)
        {
            AimState a = st.Aim;
            if (a == null) return;
            st.Aim = null;
            int stopped = 0;
            for (int i = 0; i < a.Groups.Count; i++)
            {
                GunGroup g = a.Groups[i];
                if (!g.Firing) continue;
                g.Firing = false;
                stopped++;
                for (int j = 0; j < g.Guns.Count; j++) SetShooting(g.Guns[j], false);
            }
            if (a.Driven.Count > 0) ReleaseTurrets(st, a, reason);
            if (a.FocusTargetId != 0) FocusTurrets(st, a, 0);
            if (DebugTactics)
                Log.Info("AIM_END eid=" + st.EntityId + " reason=" + reason
                         + " stoppedGroups=" + stopped + " refocused=" + a.Refocused);
        }
    }
}
