using System;
using System.Collections.Generic;
using Sandbox.Engine.Physics;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRageMath;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>FAC-440: bounded same-hull walking cover search. Collision/standing and native
    /// reachability are checked before accepting a goal; living actors never count as cover.</summary>
    internal sealed class CrewCover
    {
        private readonly List<MyPhysics.HitInfo> _hits = new List<MyPhysics.HitInfo>();
        private Vector3D _local, _originLocal, _rightLocal, _forwardLocal;
        private int _candidate, _checkAt, _pathAt;
        private bool _searching;
        internal bool Found, InCover;
        internal int Searches, Failures;
        internal string Last = "none";
        internal void Reset()
        {
            Found = InCover = _searching = false;
            _candidate = 0;
        }
        private bool Blocked(MyCharacter character, MyCubeGrid grid, MyCharacter threat, Vector3D to)
        {
            Vector3D from = threat.GetHeadMatrix(true).Translation;
            _hits.Clear();
            MyPhysics.CastRay(from, to, _hits, 30);
            double nearest = double.MaxValue;
            MyEntity obstacle = null;
            foreach (var hit in _hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity() as MyEntity;
                if (entity == character || entity == threat || entity == character.CurrentWeapon as MyEntity
                    || entity == threat.CurrentWeapon as MyEntity || entity is MyCharacter) continue;
                double distance = Vector3D.DistanceSquared(from, hit.Position);
                if (distance < nearest) { nearest = distance; obstacle = entity; }
            }
            return obstacle != null && obstacle.GetTopMostParent() == grid
                && Math.Sqrt(nearest) < Vector3D.Distance(from, to) - 0.2;
        }
        internal bool Hidden(MyCharacter character, MyCubeGrid grid, MyCharacter threat, Vector3D feet)
        {
            return threat != null && !threat.IsDead
                && Blocked(character, grid, threat, feet + character.WorldMatrix.Up * 0.9)
                && Blocked(character, grid, threat, feet + character.WorldMatrix.Up * 1.7);
        }
        internal bool Update(MyAgentBot bot, MyCubeGrid grid, MyCharacter character, MyCharacter threat,
            CrewRecovery geometry, int frame)
        {
            if (threat == null) { Reset(); return false; }
            if (frame < _checkAt) return InCover;
            _checkAt = frame + 6;
            Vector3D feet = character.PositionComp.GetPosition();
            InCover = geometry.Safe(character, grid, feet) && Hidden(character, grid, threat, feet);
            if (InCover) { Last = "arrived"; bot.Navigation.StopImmediate(true); return true; }
            Vector3D goal = Vector3D.Transform(_local, grid.WorldMatrix);
            if (Found && (!geometry.Safe(character, grid, goal) || !Hidden(character, grid, threat, goal)))
            { Reset(); bot.Navigation.StopImmediate(true); Last = "cover_exposed"; }
            if (!Found)
            {
                if (!_searching)
                {
                    _originLocal = Vector3D.Transform(feet, grid.PositionComp.WorldMatrixNormalizedInv);
                    _rightLocal = Vector3D.TransformNormal(character.WorldMatrix.Right, grid.PositionComp.WorldMatrixNormalizedInv);
                    _forwardLocal = Vector3D.TransformNormal(character.WorldMatrix.Forward, grid.PositionComp.WorldMatrixNormalizedInv);
                    _searching = true;
                    Searches++;
                }
                // At most two candidates per check, 32 per attempt, with a one-second retry pause.
                for (int attempt = 0; attempt < 2 && _candidate < 32; attempt++, _candidate++)
                {
                    double angle = (_candidate % 8) * Math.PI / 4;
                    Vector3D local = _originLocal + (_rightLocal * Math.Cos(angle) + _forwardLocal * Math.Sin(angle))
                        * (2.5 * (1 + _candidate / 8));
                    Vector3D candidate, reachable;
                    if (!geometry.TryStanding(character, grid, Vector3D.Transform(local, grid.WorldMatrix), out candidate)
                        || !Hidden(character, grid, threat, candidate)
                        || !bot.Navigation.CheckReachability(candidate, 0.5f, out reachable)
                        || !geometry.Safe(character, grid, reachable) || !Hidden(character, grid, threat, reachable)) continue;
                    _local = Vector3D.Transform(reachable, grid.PositionComp.WorldMatrixNormalizedInv);
                    goal = reachable;
                    Found = true;
                    bot.Navigation.StopImmediate(true);
                    _pathAt = frame;
                    Last = "walking";
                    break;
                }
                if (!Found && _candidate >= 32)
                { Failures++; Reset(); Last = "no_cover"; _checkAt = frame + 60; }
            }
            if (Found && frame >= _pathAt && (!bot.Navigation.Navigating || bot.Navigation.Stuck))
            {
                bot.Navigation.AimWithMovement();
                bot.Navigation.Goto(goal, 0.5f, grid);
                character.Walk();
                _pathAt = frame + 60;
            }
            return false;
        }
    }
}
