using System;
using System.Collections.Generic;
using Factions.Core;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRageMath;

namespace Factions
{
    /// <summary>FAC-440: same-ship escape using native FlyTo, then a checked stuck teleport.
    /// Population/hostility stay with callers; flight steering can also serve later EVA combat.</summary>
    internal sealed class CrewRecovery
    {
        private readonly CrewEscape _escape = new CrewEscape();
        private readonly List<MyPhysics.HitInfo> _hits = new List<MyPhysics.HitInfo>();
        private MatrixD? _safeLocal;
        private MatrixD _destinationLocal;
        private float _embeddedSeconds;
        private int _checkAt;
        internal bool Embedded { get; private set; }
        internal bool Flying => _escape.Flying;
        internal int Flights, Teleports, NoDestination;
        internal string Last = "none";

        internal bool Tick(MyAgentBot bot, MyCubeGrid grid, MyCharacter character, MyCharacter target, bool hasSight, int frame)
        {
            if (character.Physics == null || !character.Physics.Enabled || character.Parent != null) return false;
            MatrixD destination = _destinationLocal * grid.WorldMatrix;
            bool reached = Flying && Vector3D.DistanceSquared(character.PositionComp.GetPosition(), destination.Translation) <= 1
                && Safe(character, grid, character.PositionComp.GetPosition());
            bool wasFlying = Flying;
            CrewEscapeAction action = _escape.Advance(1.0 / 60, reached);
            if (wasFlying && (!Flying || action == CrewEscapeAction.Teleport))
            {
                StopFlight(bot, character);
                if (action == CrewEscapeAction.Teleport) Teleport(bot, grid, character);
                else Last = "flight_arrived";
                return true;
            }
            if (Flying) return true;
            if (frame < _checkAt) return false;
            _checkAt = frame + 30;
            Embedded = !Clear(character, character.PositionComp.GetPosition(), true, grid);
            _embeddedSeconds = Embedded ? _embeddedSeconds + 0.5f : 0;
            if (!Embedded && (target == null || hasSight) && Safe(character, grid, character.PositionComp.GetPosition()))
                _safeLocal = character.WorldMatrix * grid.PositionComp.WorldMatrixNormalizedInv;
            if (_embeddedSeconds < 2 || _escape.CooldownSeconds > 0) return false;
            return Begin(bot, grid, character, target);
        }

        internal bool Begin(MyAgentBot bot, MyCubeGrid grid, MyCharacter character, MyCharacter target)
        {
            if (Flying) return true;
            if (_escape.CooldownSeconds > 0) return false;
            MatrixD destination;
            if (!Destination(grid, character, target, out destination))
            { NoDestination++; Last = "no_safe_destination"; return false; }
            _destinationLocal = destination * grid.PositionComp.WorldMatrixNormalizedInv;
            Embedded = !Clear(character, character.PositionComp.GetPosition(), true, grid);
            bool routeClear = !Embedded && ClearRoute(character, destination.Translation);
            CrewEscapeAction action = _escape.Begin(Embedded, routeClear, true);
            bot.Navigation.StopImmediate(true);
            bot.Navigation.StopAiming();
            if (action == CrewEscapeAction.Fly)
            {
                bot.Navigation.FlyTo(destination.Translation, grid);
                Flights++;
                Last = "flight_started";
                return true;
            }
            if (action == CrewEscapeAction.Teleport)
            {
                Teleport(bot, grid, character);
                return true;
            }
            return false;
        }

        private void StopFlight(MyAgentBot bot, MyCharacter character)
        {
            bot.Navigation.StopImmediate(true);
            character.JetpackComp?.TurnOnJetpack(false);
            character.MoveAndRotate(Vector3.Zero, Vector2.Zero, 0);
        }
        private void Teleport(MyAgentBot bot, MyCubeGrid grid, MyCharacter character)
        {
            MatrixD destination = _destinationLocal * grid.WorldMatrix;
            StopFlight(bot, character);
            // Revalidate at the instant of teleport: another actor or a moving door may occupy it.
            if (Safe(character, grid, destination.Translation))
            {
                character.Teleport(destination);
                character.Physics.LinearVelocity = grid.Physics != null
                    ? grid.Physics.GetVelocityAtPoint(destination.Translation) : Vector3.Zero;
                character.Physics.AngularVelocity = Vector3.Zero;
                Teleports++;
                Last = Embedded ? "embedded_teleport" : "stuck_teleport";
                _embeddedSeconds = 0;
            }
            else { NoDestination++; Last = "destination_no_longer_safe"; }
            _escape.Finish();
        }
        private bool Destination(MyCubeGrid grid, MyCharacter character, MyCharacter target, out MatrixD destination)
        {
            destination = character.WorldMatrix;
            if (_safeLocal.HasValue)
            {
                MatrixD remembered = _safeLocal.Value * grid.WorldMatrix;
                if (Vector3D.DistanceSquared(remembered.Translation, character.PositionComp.GetPosition()) >= 1
                    && Vector3D.DistanceSquared(remembered.Translation, character.PositionComp.GetPosition()) <= 225
                    && Safe(character, grid, remembered.Translation))
                { destination.Translation = remembered.Translation; return true; }
            }
            if (target == null) return false;
            Vector3D origin = target.PositionComp.GetPosition();
            // Fixed, bounded candidate order. Never overlap the player or another character.
            for (int i = 0; i < 8; i++)
            {
                Vector3D axis = i % 4 == 0 ? grid.WorldMatrix.Right : i % 4 == 1 ? grid.WorldMatrix.Left
                    : i % 4 == 2 ? grid.WorldMatrix.Forward : grid.WorldMatrix.Backward;
                Vector3D candidate = origin + axis * (i < 4 ? 2 : 4);
                if (Vector3D.DistanceSquared(candidate, character.PositionComp.GetPosition()) > 225) continue;
                Vector3D up = character.WorldMatrix.Up;
                _hits.Clear();
                MyPhysics.CastRay(candidate + up, candidate - up * 3, _hits, 30);
                double nearest = double.MaxValue;
                MyPhysics.HitInfo? support = null;
                foreach (var hit in _hits)
                {
                    var entity = hit.HkHitInfo.GetHitEntity();
                    if (entity == character || entity == character.CurrentWeapon) continue;
                    double distance = Vector3D.DistanceSquared(candidate + up, hit.Position);
                    if (distance < nearest) { nearest = distance; support = hit; }
                }
                if (!support.HasValue || support.Value.HkHitInfo.GetHitEntity()?.GetTopMostParent() != grid
                    || Vector3D.Dot(support.Value.HkHitInfo.Normal, up) < 0.65) continue;
                candidate = support.Value.Position + up * 0.1;
                if (!Safe(character, grid, candidate)) continue;
                destination.Translation = candidate;
                return true;
            }
            return false;
        }
        internal bool Safe(MyCharacter character, MyCubeGrid grid, Vector3D feet)
        {
            var inverse = grid.PositionComp.WorldMatrixNormalizedInv;
            Vector3D head = feet + character.WorldMatrix.Up * character.Definition.CharacterCollisionHeight;
            if (grid.PositionComp.LocalAABB.Contains(Vector3D.Transform(feet, inverse)) == ContainmentType.Disjoint
                || grid.PositionComp.LocalAABB.Contains(Vector3D.Transform(head, inverse)) == ContainmentType.Disjoint
                || !Clear(character, feet, false, grid)) return false;
            _hits.Clear();
            Vector3D up = character.WorldMatrix.Up;
            MyPhysics.CastRay(feet + up * 0.15, feet - up * 0.35, _hits, 30);
            double nearest = double.MaxValue;
            MyPhysics.HitInfo? support = null;
            foreach (var hit in _hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity();
                if (entity == character || entity == character.CurrentWeapon) continue;
                double distance = Vector3D.DistanceSquared(feet + up * 0.15, hit.Position);
                if (distance < nearest) { nearest = distance; support = hit; }
            }
            return support.HasValue && support.Value.HkHitInfo.GetHitEntity()?.GetTopMostParent() == grid
                && Vector3D.Dot(support.Value.HkHitInfo.Normal, up) >= 0.65;
        }
        // Shared standing validation for tactical walking destinations, without relocation.
        internal bool TryStanding(MyCharacter character, MyCubeGrid grid, Vector3D candidate, out Vector3D feet)
        {
            feet = candidate;
            Vector3D up = character.WorldMatrix.Up;
            _hits.Clear();
            MyPhysics.CastRay(candidate + up, candidate - up * 3, _hits, 30);
            double nearest = double.MaxValue;
            MyPhysics.HitInfo? support = null;
            foreach (var hit in _hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity();
                if (entity == character || entity == character.CurrentWeapon) continue;
                double distance = Vector3D.DistanceSquared(candidate + up, hit.Position);
                if (distance < nearest) { nearest = distance; support = hit; }
            }
            if (!support.HasValue || support.Value.HkHitInfo.GetHitEntity()?.GetTopMostParent() != grid
                || Vector3D.Dot(support.Value.HkHitInfo.Normal, up) < 0.65) return false;
            feet = support.Value.Position + up * 0.1;
            return Safe(character, grid, feet);
        }
        private bool ClearRoute(MyCharacter character, Vector3D destination)
        {
            Vector3D start = character.PositionComp.GetPosition();
            int steps = (int)Math.Ceiling(Vector3D.Distance(start, destination) / 0.4);
            if (steps > 38) return false;
            for (int i = 1; i <= steps; i++)
                if (!Clear(character, Vector3D.Lerp(start, destination, (double)i / steps), false, null)) return false;
            return true;
        }
        internal static bool Clear(MyCharacter character, Vector3D feet, bool torsoOnly, MyCubeGrid grid)
        {
            float height = character.Definition.CharacterCollisionHeight;
            float radius = torsoOnly ? 0.2f : character.Definition.CharacterCollisionWidth * character.Definition.CharacterCollisionScale * 0.5f;
            float bottom = torsoOnly ? height * 0.35f : radius + 0.08f;
            float top = torsoOnly ? height * 0.7f : Math.Max(bottom, height - radius);
            HkShape shape = new HkCapsuleShape(new Vector3(0, bottom, 0), new Vector3(0, top, 0), radius);
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(character.WorldMatrix.GetOrientation());
            try
            {
                return !MyEntities.IsShapePenetrating(shape, ref feet, ref rotation,
                    entity => torsoOnly ? entity == null || entity.GetTopMostParent() != grid
                        : entity != null && (entity == character || entity == character.CurrentWeapon), 15);
            }
            finally { shape.RemoveReference(); }
        }
    }
}
