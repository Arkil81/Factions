using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;        // MyEntityThrustComponent, MyGridGyroSystem
using VRageMath;

namespace Factions
{
    /// <summary>What a wing commander's order asks of one hull this second. The runtime builds it from
    /// the hull's <see cref="WingOrder"/> at the wing read; see docs/fleet-tactics.md, Package 2.</summary>
    internal struct WingDirective
    {
        /// <summary>The commander's grid to form on; zero when the wing is not formed.</summary>
        public long AnchorId;
        /// <summary>The focus target; zero for none.</summary>
        public long TargetId;
        public bool Holding;
        public bool BreakingOff;
        /// <summary>The commander rung of <see cref="TacticResolver"/>.</summary>
        public TacticOrder Order;
        /// <summary>This hull's slot in the wing's formation.</summary>
        public int Slot;
        public string Label;
        /// <summary>The hull's fleet's weapon bands, for a hull whose own could not be read.</summary>
        public IReadOnlyList<WeaponBand> FleetBands;
    }

    /// <summary>
    /// A commander's order flying a hull: the top rung of <see cref="TacticResolver"/> made physical.
    /// A commanded hull is flown as a <see cref="ManoeuvreKind.FormUp"/> — on the commander's grid
    /// when the wing is formed, on itself (brake and hold) when it is not — with a fight of its own on
    /// the focus target in place of its fleet's, until the runtime ends the order
    /// (<see cref="EndWing"/>), which releases it for the fleet's own flight to re-take.
    ///
    /// <para><b>The fight.</b> Once a second: the focus target while it lives; the plan from
    /// <see cref="TacticResolver.Resolve"/> with the order as the commander rung, so <i>hold</i>
    /// fights Standoff and never closes; <i>break off</i> flies
    /// <see cref="ManoeuvreKind.Disengage"/> to <see cref="WingRules.BreakOffSeparationM"/> and then
    /// holds. A focused hull fires aimed and focuses its turrets on the target (Warfare 2 focus,
    /// see <see cref="ReadAim"/>). It is the player's command. A formed hull's bearing on the target is fanned by its slot, as an escort's is.</para>
    /// </summary>
    internal sealed partial class FleetFlightController
    {
        private sealed class WingState
        {
            public long TargetId;
            public bool Holding;
            public bool BreakingOff;
            public bool Separated;
            public TacticOrder Order;
            public string Label;
            public IReadOnlyList<WeaponBand> FleetBands;
            public DateTime NextReadUtc;
        }

        private readonly Dictionary<long, WingState> _wing = new Dictionary<long, WingState>();

        private WingState WingOf(long entityId)
        {
            if (_wing.Count == 0) return null;
            WingState w;
            return _wing.TryGetValue(entityId, out w) ? w : null;
        }

        /// <summary>A commander holds the hull or has it formed on their grid: formation wins, so it
        /// keeps no orbit floor and makes no evasive burst (<see cref="CombatAgility.Motion"/>).</summary>
        private static bool FormationHeld(FlightState st, WingState w)
        {
            return w != null && (w.Holding || st.AnchorId != 0);
        }

        /// <summary>True while a commander's order flies the grid.</summary>
        public bool IsWinged(long entityId)
        {
            return _wing.ContainsKey(entityId);
        }

        /// <summary>Puts a hull under a commander's order, or re-points one already under it. Takes
        /// the grid from whatever flies it — the cruise autopilot, an approach, a fight or a
        /// formation slot — and returns whether it is now under the order. Refused, and asked again
        /// at the next wing read, when the grid is gone or cannot brake.</summary>
        public bool CommandWing(long entityId, WingDirective d)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st))
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Physics == null
                    || grid.MarkedForClose || grid.Closed)
                    return false;
                MyEntityThrustComponent thrust = grid.Components.Get<MyEntityThrustComponent>();
                MyGridGyroSystem gyros = grid.GridSystems != null ? grid.GridSystems.GyroSystem : null;
                if (thrust == null) return false;
                double decel = EstimateDecelMs2(grid, thrust, grid.Physics.LinearVelocity);
                if (FlightManoeuvre.AbortsOnLostBraking(ManoeuvreKind.FormUp, decel)) return false;
                st = TakeOver(entityId, grid, thrust, gyros, grid.PositionComp.GetPosition(), ManoeuvreKind.FormUp);
            }

            WingState w;
            bool fresh = !_wing.TryGetValue(entityId, out w);
            if (fresh)
            {
                w = new WingState();
                _wing[entityId] = w;
                // Whatever fight the hull had was its fleet's.
                st.Combat = null;
                if (DebugFlight)
                    Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=FormUp wing=" + (d.Label ?? "?")
                             + " anchor=" + d.AnchorId + " target=" + d.TargetId);
            }

            st.Kind = ManoeuvreKind.FormUp;
            st.IsEscort = true;
            st.Arrived = false;
            if (st.AnchorId != d.AnchorId)
            {
                st.AnchorId = d.AnchorId;
                st.Anchor = null;
                st.AnchorReference = null;
            }
            st.SlotIndex = d.Slot < 0 ? 0 : d.Slot;

            bool changed = fresh || w.TargetId != d.TargetId || w.Holding != d.Holding || w.BreakingOff != d.BreakingOff;
            if (d.BreakingOff && (!w.BreakingOff || w.TargetId != d.TargetId)) w.Separated = false;
            w.TargetId = d.TargetId;
            w.Holding = d.Holding;
            w.BreakingOff = d.BreakingOff;
            w.Order = d.Order;
            w.Label = d.Label;
            w.FleetBands = d.FleetBands;
            if (changed) w.NextReadUtc = DateTime.MinValue;
            return true;
        }

        /// <summary>Ends a commander's order on a hull and releases it; the caller re-wires its own
        /// flight. A no-op for a hull no order flies.</summary>
        public void EndWing(long entityId)
        {
            if (!_wing.ContainsKey(entityId)) return;
            Release(entityId);
        }

        /// <summary>The per-tick half of a commanded hull: keeps its anchor resolved and, once a
        /// second, its formation spacing and its fight. Returns whether it has a live anchor.</summary>
        private bool TickWing(FlightState st, WingState w, DateTime nowUtc)
        {
            bool anchorLive = false;
            if (st.AnchorId != 0)
            {
                ResolveAnchor(st);
                anchorLive = st.Anchor != null;
                if (anchorLive && st.AnchorReference == null) st.AnchorReference = SeatedController(st.Anchor);
            }
            else
            {
                st.Anchor = null;
                st.AnchorReference = null;
            }

            if (nowUtc >= w.NextReadUtc)
            {
                w.NextReadUtc = nowUtc + CombatReadInterval;
                if (anchorLive) ReadFormationSpacing(st);
                ReadWingFight(st, w, nowUtc);
            }
            return anchorLive;
        }

        /// <summary>A commander's grid seldom carries a remote control; its nose is the cockpit its
        /// pilot sits in, else any ship controller on it.</summary>
        private static MyCubeBlock SeatedController(MyCubeGrid grid)
        {
            MyCubeBlock any = null;
            foreach (MyShipController sc in grid.GetFatBlocks<MyShipController>())
            {
                if (sc == null || sc.Closed) continue;
                if (sc.Pilot != null) return sc;
                if (any == null) any = sc;
            }
            return any;
        }

        private void ReadWingFight(FlightState st, WingState w, DateTime nowUtc)
        {
            MyCubeGrid target = null;
            if (w.TargetId != 0 && !(w.BreakingOff && w.Separated))
            {
                MyCubeGrid g;
                if (MyEntities.TryGetEntityById(w.TargetId, out g, false) && !g.Closed && !g.MarkedForClose)
                    target = g;
            }
            if (target == null)
            {
                EndWingFight(st, "no_target");
                return;
            }

            Vector3D self = st.Grid.PositionComp.WorldVolume.Center;
            Vector3D targetPos = target.PositionComp.WorldVolume.Center;
            if (w.BreakingOff && (targetPos - self).Length() >= WingRules.BreakOffSeparationM)
            {
                w.Separated = true;
                EndWingFight(st, "broke_off");
                return;
            }

            CombatState c = st.Combat;
            if (c == null)
            {
                c = new CombatState();
                st.Combat = c;
            }
            c.FleetLabel = w.Label;
            c.AnchorWorld = self;
            c.AimedFire = !w.BreakingOff;
            if (c.OwnBands == null || nowUtc - c.OwnBandsUtc >= TargetBandsRefresh)
            {
                c.OwnBandsUtc = nowUtc;
                IReadOnlyList<WeaponBand> own = EstimateBands(st.Grid);
                c.OwnBands = own ?? w.FleetBands;
            }
            c.Target = target;
            ReadTargetBands(c, null, nowUtc);

            TacticOrder order = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = c.TargetId,
                AnchorEntityId = st.EntityId,
                Commander = w.Order,
            });
            double[] brackets = _tuning != null ? _tuning.CombatRangeBracketsM : null;
            CombatPlan plan = TacticalRead.Plan(order, c.OwnBands, c.TargetBands, brackets);
            if (w.BreakingOff)
            {
                plan.Kind = ManoeuvreKind.Disengage;
                plan.HoldRangeM = WingRules.BreakOffSeparationM;
            }

            bool kindChanged = !c.Active || plan.Kind != c.Plan.Kind;
            if (kindChanged) c.RunInbound = true;
            c.Plan = plan;
            c.Active = true;

            // A formed wing fans out from the commander's bearing on the target, as escorts fan from
            // their lead's; an unformed hull takes its own line.
            c.SpreadBearing = Vector3D.Zero;
            if (st.Anchor != null && !w.BreakingOff)
            {
                Vector3D anchorPos = st.Anchor.PositionComp.WorldVolume.Center;
                Vector3 gravity = st.Grid.Physics != null ? st.Grid.Physics.Gravity : Vector3.Zero;
                Vector3D axis = gravity.LengthSquared() > 0.0025f ? -(Vector3D)gravity : AnchorMatrix(st).Up;
                double step = Formation.SpreadStepRad(st.SlotSpacingM, plan.HoldRangeM, st.FormationSize);
                Vec3 bearing = Formation.SpreadBearing(ToVec3(anchorPos - targetPos), ToVec3(axis), st.SlotIndex, step);
                c.SpreadBearing = new Vector3D(bearing.X, bearing.Y, bearing.Z);
            }

            if (kindChanged && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + plan.Kind
                         + " target=" + c.TargetId
                         + " sep=" + (targetPos - self).Length().ToString("F0") + "m"
                         + " hold=" + plan.HoldRangeM.ToString("F0") + "m"
                         + " wing=" + (w.Label ?? "?"));
            if (DebugTactics && (c.LoggedTargetId != c.TargetId || c.LoggedArm != plan.Arm || c.LoggedHoldM != plan.HoldRangeM))
            {
                c.LoggedTargetId = c.TargetId;
                c.LoggedArm = plan.Arm;
                c.LoggedHoldM = plan.HoldRangeM;
                Log.Info("FLIGHT_TACTIC fleet=" + (w.Label ?? "?")
                         + " eid=" + st.EntityId
                         + " target=" + c.TargetId
                         + " arm=" + plan.Arm
                         + " breakoff=" + plan.BreakOff
                         + " source=" + order.Source
                         + " kind=" + plan.Kind
                         + " hold=" + plan.HoldRangeM.ToString("F0")
                         + " range=" + (targetPos - self).Length().ToString("F0"));
            }

            if (c.AimedFire) ReadAim(st, c, nowUtc);
        }

        /// <summary>Back to the order's formation or hold: drops the hull's fight, if it had one.</summary>
        private static void EndWingFight(FlightState st, string reason)
        {
            if (st.Combat == null) return;
            bool wasActive = st.Combat.Active;
            st.Combat = null;
            if (wasActive && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=FormUp anchor=" + st.AnchorId + " reason=" + reason);
        }
    }
}
