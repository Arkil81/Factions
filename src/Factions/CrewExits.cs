using System;
using System.Collections.Generic;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// The doors a crew can use to get between a hull's deck and the ground
    /// (docs/ground-operations.md, Traversal). An exit is a door on the hull itself with walkable
    /// deck 1.25 m inside it and ground 1 m beyond the hull's box outside it, no more than
    /// <see cref="MaxStepM"/> below the deck. Both points are kept in hull-local coordinates, so a
    /// hull that has not moved keeps its exits; a hull that has moved more than a metre is surveyed
    /// again.
    /// </summary>
    internal static class CrewExits
    {
        internal sealed class Exit
        {
            public IMyDoor Door;
            public Vector3D InsideLocal;
            public Vector3D OutsideLocal;
            /// <summary>Outside the doorway, on its centre line, 0.3 m above deck height: the
            /// crossing rises or drops between here and <see cref="OutsideLocal"/>, and flies level
            /// between here and <see cref="InsideLocal"/>, so it never cuts across the threshold.</summary>
            public Vector3D PorchLocal;
        }

        private sealed class Survey
        {
            public Vector3D At;
            public List<Exit> Exits;
        }

        /// <summary>How far below the deck the ground outside a door may be. A door higher than this
        /// is not a ground exit.</summary>
        internal const double MaxStepM = 3.0;

        private static readonly Dictionary<long, Survey> Surveys = new Dictionary<long, Survey>();
        private static readonly List<MyPhysics.HitInfo> Hits = new List<MyPhysics.HitInfo>();

        internal static List<Exit> Of(MyCubeGrid hull)
        {
            Survey survey;
            Vector3D at = hull.PositionComp.GetPosition();
            if (Surveys.TryGetValue(hull.EntityId, out survey) && Vector3D.DistanceSquared(survey.At, at) < 1)
                return survey.Exits;
            survey = new Survey { At = at, Exits = Find(hull) };
            Surveys[hull.EntityId] = survey;
            return survey.Exits;
        }

        /// <summary>The exit nearest <paramref name="toward"/>, or null when the hull has none.</summary>
        internal static Exit Nearest(MyCubeGrid hull, Vector3D toward)
        {
            Exit best = null;
            double nearest = double.MaxValue;
            foreach (Exit exit in Of(hull))
            {
                double d = Vector3D.DistanceSquared(toward, Vector3D.Transform(exit.OutsideLocal, hull.WorldMatrix));
                if (d < nearest) { nearest = d; best = exit; }
            }
            return best;
        }

        /// <summary>A clear standing spot on the deck beside <paramref name="seat"/>, inside the hull's
        /// box: 1 m then 1.5 m out along the seat's own axes and diagonals, flattened against gravity
        /// (a seat may be mounted on a wall). Standing room is a gravity-upright capsule, not one in the
        /// seated pose. Null when none; <paramref name="why"/> then counts each rejection:
        /// f = no floor under the spot, b = outside the hull's box, c = no standing room.</summary>
        internal static Vector3D? DeckBeside(MyCubeGrid hull, MyCubeBlock seat, MyCharacter character, out string why)
        {
            Vector3D gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(seat.PositionComp.WorldAABB.Center);
            Vector3D up = gravity.LengthSquared() > 1e-4 ? -Vector3D.Normalize(gravity) : seat.WorldMatrix.Up;
            Vector3D centre = seat.PositionComp.WorldAABB.Center;
            MatrixD m = seat.WorldMatrix;
            var axes = new[] { m.Forward, m.Left, m.Right, m.Backward, m.Up, m.Down };
            var sides = new List<Vector3D>();
            foreach (Vector3D axis in axes)
            {
                Vector3D flat = Flatten(axis, up);
                if (flat.LengthSquared() > 0.25) sides.Add(Vector3D.Normalize(flat));
            }
            int count = sides.Count;
            for (int a = 0; a < count; a++)
                for (int b = a + 1; b < count; b++)
                {
                    Vector3D diagonal = sides[a] + sides[b];
                    if (diagonal.LengthSquared() > 0.5) sides.Add(Vector3D.Normalize(diagonal));
                }
            int noFloor = 0, outside = 0, cramped = 0;
            foreach (double reach in new[] { 1.0, 1.5 })
                foreach (Vector3D side in sides)
                {
                    Vector3D? feet = Floor(centre + side * reach + up * 0.5, up, 2.5, hull, true);
                    if (!feet.HasValue) { noFloor++; continue; }
                    if (!Inside(hull, feet.Value, -0.2)) { outside++; continue; }
                    if (!StandingRoom(character, feet.Value, up)) { cramped++; continue; }
                    why = null;
                    return feet;
                }
            why = "f" + noFloor + "b" + outside + "c" + cramped;
            return null;
        }

        /// <summary>A gravity-upright capsule of the character's size at <paramref name="feet"/>
        /// touches nothing but the character himself.</summary>
        private static bool StandingRoom(MyCharacter character, Vector3D feet, Vector3D up)
        {
            float height = character.Definition.CharacterCollisionHeight;
            float radius = character.Definition.CharacterCollisionWidth * character.Definition.CharacterCollisionScale * 0.5f;
            float bottom = radius + 0.08f;
            float top = Math.Max(bottom, height - radius);
            Vector3D forward = Vector3D.CalculatePerpendicularVector(up);
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(MatrixD.CreateWorld(Vector3D.Zero, forward, up));
            HkShape shape = new HkCapsuleShape(new Vector3(0, bottom, 0), new Vector3(0, top, 0), radius);
            try
            {
                return !MyEntities.IsShapePenetrating(shape, ref feet, ref rotation,
                    entity => entity != null && (entity == character || entity == character.CurrentWeapon), 15);
            }
            finally { shape.RemoveReference(); }
        }

        internal static void Forget(long hullId)
        {
            Surveys.Remove(hullId);
        }

        internal static void Close()
        {
            Surveys.Clear();
        }

        private static List<Exit> Find(MyCubeGrid hull)
        {
            var exits = new List<Exit>();
            Vector3D centre = hull.PositionComp.WorldAABB.Center;
            Vector3D gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre);
            if (gravity.LengthSquared() < 1e-4) return exits;
            Vector3D up = -Vector3D.Normalize(gravity);
            MatrixD inverse = hull.PositionComp.WorldMatrixNormalizedInv;
            foreach (MyCubeBlock block in hull.GetFatBlocks())
            {
                var door = block as IMyDoor;
                if (door == null || !block.IsFunctional) continue;
                Vector3D doorAt = block.PositionComp.WorldAABB.Center;
                Vector3D outward = Flatten(block.WorldMatrix.Forward, up);
                if (outward.LengthSquared() < 0.09) continue;
                outward.Normalize();
                Vector3D away = Flatten(doorAt - centre, up);
                double side = Vector3D.Dot(outward, away);
                if (Math.Abs(side) < 0.5) continue;
                if (side < 0) outward = -outward;

                Vector3D? deck = Floor(doorAt - outward * 1.25 + up * 0.5, up, 2.5, hull, true);
                if (!deck.HasValue) continue;
                Vector3D beyond = doorAt;
                for (int step = 1; step <= 40 && Inside(hull, beyond, 0.5); step++) beyond = doorAt + outward * (0.5 * step);
                beyond += outward;
                Vector3D? ground = Floor(beyond + up * 1.0, up, MaxStepM + 2.0, hull, false);
                if (!ground.HasValue || Vector3D.Dot(deck.Value - ground.Value, up) > MaxStepM) continue;
                exits.Add(new Exit
                {
                    Door = door,
                    InsideLocal = Vector3D.Transform(deck.Value, inverse),
                    OutsideLocal = Vector3D.Transform(ground.Value, inverse),
                    PorchLocal = Vector3D.Transform(ground.Value + up * (Vector3D.Dot(deck.Value - ground.Value, up) + 0.3), inverse),
                });
            }
            return exits;
        }

        private static Vector3D Flatten(Vector3D v, Vector3D up)
        {
            return v - up * Vector3D.Dot(v, up);
        }

        private static bool Inside(MyCubeGrid hull, Vector3D world, double margin)
        {
            Vector3D local = Vector3D.Transform(world, hull.PositionComp.WorldMatrixNormalizedInv);
            BoundingBoxD box = hull.PositionComp.LocalAABB;
            box.Inflate(margin);
            return box.Contains(local) == ContainmentType.Contains;
        }

        /// <summary>The nearest standing surface below <paramref name="from"/> within
        /// <paramref name="depth"/>: on the hull when <paramref name="onHull"/>, else anything but the
        /// hull. 5 cm above the hit.</summary>
        internal static Vector3D? Floor(Vector3D from, Vector3D up, double depth, MyCubeGrid hull, bool onHull)
        {
            Hits.Clear();
            MyPhysics.CastRay(from, from - up * depth, Hits, 30);
            double nearest = double.MaxValue;
            Vector3D? found = null;
            foreach (MyPhysics.HitInfo hit in Hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity() as MyEntity;
                if (entity is MyCharacter) continue;
                bool hullHit = entity != null && entity.GetTopMostParent() == hull;
                double d = Vector3D.DistanceSquared(from, hit.Position);
                if (d >= nearest) continue;
                nearest = d;
                found = hullHit == onHull && Vector3D.Dot(hit.HkHitInfo.Normal, up) >= 0.65 ? hit.Position + up * 0.05 : (Vector3D?)null;
            }
            return found;
        }
    }
}
