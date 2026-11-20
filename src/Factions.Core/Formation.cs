using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Squadron formation geometry and the rules that decide which hulls the flight
    /// controller takes: where an escort's slot sits on its anchor, where it holds its range in a
    /// fight so hulls do not share one point, which grid it forms on when its anchor is gone, and
    /// how many grids one bubble may have under native control. SE-free; the host reads positions
    /// and orientations and passes them in. See docs/fleet-tactics.md, slice 2.
    ///
    /// <para><b>Anchor frame.</b> A slot offset is (X right, Y up, Z aft) of the anchor: right is
    /// <c>forward × up</c>, which is SE's own convention, and aft is minus forward.</para></summary>
    public static class Formation
    {
        /// <summary>Clearance kept between two neighbouring hulls' bounding spheres. Wider than the
        /// flight avoidance margin, so velocity-matched hulls in their slots never read as a threat
        /// to each other.</summary>
        public const double SlotGapM = 60.0;

        /// <summary>No slot spacing under this, whatever the hulls' size.</summary>
        public const double MinSlotSpacingM = 100.0;

        /// <summary>How far from its slot an escort counts as in it. Inside it the escort only
        /// matches its anchor's velocity.</summary>
        public const double SlotToleranceM = 10.0;

        /// <summary>Grids under native control within <see cref="BubbleRadiusM"/> of one another
        /// before an escort is refused and left on the vanilla autopilot. Leads are never refused;
        /// they count toward it.</summary>
        public const int MaxControlledPerBubble = 8;

        /// <summary>Radius of the neighbourhood <see cref="MaxControlledPerBubble"/> counts over.</summary>
        public const double BubbleRadiusM = 5000.0;

        /// <summary>Least angle between neighbouring hulls' bearings from a shared target in a fight.</summary>
        public const double MinSpreadStepRad = 15.0 * Math.PI / 180.0;

        /// <summary>No hull's bearing further than this from the lead's, so the outermost escorts of
        /// a large squadron never end up on the far side of the target from it.</summary>
        public const double MaxSpreadRad = 150.0 * Math.PI / 180.0;

        /// <summary>The spacing every slot of a formation is measured in: the largest hull's
        /// diameter plus <see cref="SlotGapM"/>, at least <see cref="MinSlotSpacingM"/>. One number
        /// per formation, never per hull: slots sized from different spacings can coincide.</summary>
        public static double SlotSpacingM(double largestRadiusM)
        {
            double r = largestRadiusM > 0 ? largestRadiusM : 0;
            double spacing = 2.0 * r + SlotGapM;
            return spacing < MinSlotSpacingM ? MinSlotSpacingM : spacing;
        }

        /// <summary>The offset of slot <paramref name="slotIndex"/> in the anchor frame: a wedge
        /// behind the anchor, alternating right and left, one rank further back and further out
        /// every second slot. Any two slots, and any slot and the anchor, are at least
        /// <c>spacing·√2</c> apart.</summary>
        public static Vec3 SlotOffset(int slotIndex, double spacingM)
        {
            int i = slotIndex < 0 ? 0 : slotIndex;
            int rank = i / 2 + 1;
            double side = (i % 2 == 0) ? 1.0 : -1.0;
            return new Vec3(side * rank * spacingM, 0, rank * spacingM);
        }

        /// <summary>World position of an anchor-frame offset. <paramref name="forward"/> and
        /// <paramref name="up"/> need not be unit or exactly orthogonal; up is re-orthogonalised
        /// against forward. A degenerate frame (no forward) places the slot on world axes so the
        /// result is still deterministic.</summary>
        public static Vec3 SlotWorld(Vec3 anchorM, Vec3 forward, Vec3 up, Vec3 slot)
        {
            Vec3 f, u, r;
            Frame(forward, up, out f, out u, out r);
            return new Vec3(
                anchorM.X + r.X * slot.X + u.X * slot.Y - f.X * slot.Z,
                anchorM.Y + r.Y * slot.X + u.Y * slot.Y - f.Y * slot.Z,
                anchorM.Z + r.Z * slot.X + u.Z * slot.Y - f.Z * slot.Z);
        }

        /// <summary>An orthonormal (forward, up, right) from a possibly sloppy pair.</summary>
        public static void Frame(Vec3 forward, Vec3 up, out Vec3 f, out Vec3 u, out Vec3 r)
        {
            f = Unit(forward);
            if (IsZero(f)) f = new Vec3(0, 0, -1);

            Vec3 upOrtho = Sub(up, Scale(f, Dot(up, f)));
            u = Unit(upOrtho);
            if (IsZero(u))
            {
                // Up missing or along forward: any perpendicular will do, chosen deterministically.
                Vec3 axis = Math.Abs(f.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                u = Unit(Sub(axis, Scale(f, Dot(axis, f))));
            }
            r = Cross(f, u);
        }

        /// <summary>The grid an escort forms on: <paramref name="preferred"/> (the resolved order's
        /// anchor) while it lives, else the lead, else the first live hull in squadron order — never
        /// the escort itself. Zero when none lives: the escort then goes back to the cruise autopilot
        /// on its fleet's waypoint.</summary>
        public static long PickAnchor(long preferred, long lead, IReadOnlyList<long> escorts, long self,
                                      Func<long, bool> alive)
        {
            if (Usable(preferred, self, alive)) return preferred;
            if (Usable(lead, self, alive)) return lead;
            if (escorts != null)
                for (int i = 0; i < escorts.Count; i++)
                    if (Usable(escorts[i], self, alive)) return escorts[i];
            return 0;
        }

        private static bool Usable(long eid, long self, Func<long, bool> alive)
        {
            return eid > 0 && eid != self && (alive == null || alive(eid));
        }

        /// <summary>The escort's slot on <paramref name="anchor"/>: its position in
        /// <paramref name="escorts"/>, not counting the anchor or any empty id. -1 when it is not
        /// in the list.</summary>
        public static int SlotIndex(IReadOnlyList<long> escorts, long self, long anchor)
        {
            if (escorts == null) return -1;
            int slot = 0;
            for (int i = 0; i < escorts.Count; i++)
            {
                long eid = escorts[i];
                if (eid == self) return slot;
                if (eid > 0 && eid != anchor) slot++;
            }
            return -1;
        }

        /// <summary>How many of the first <paramref name="count"/> positions lie within
        /// <paramref name="radiusM"/> of <paramref name="center"/>.</summary>
        public static int CountWithin(Vec3 center, Vec3[] positions, int count, double radiusM)
        {
            if (positions == null) return 0;
            if (count > positions.Length) count = positions.Length;
            double r2 = radiusM * radiusM;
            int n = 0;
            for (int i = 0; i < count; i++)
                if (positions[i].DistanceSquaredTo(center) <= r2) n++;
            return n;
        }

        /// <summary>Whether one more grid may be taken under native control where
        /// <paramref name="controlledNearby"/> already are.</summary>
        public static bool AdmitsControl(int controlledNearby, int cap)
        {
            return cap > 0 && controlledNearby < cap;
        }

        /// <summary>The angle between neighbouring bearings from a target for a squadron of
        /// <paramref name="escortCount"/> escorts fighting at <paramref name="rangeM"/>: wide
        /// enough that neighbours sit a slot spacing apart on the ring, at least
        /// <see cref="MinSpreadStepRad"/>, and narrow enough that the outermost stays within
        /// <see cref="MaxSpreadRad"/> of the lead.</summary>
        public static double SpreadStepRad(double spacingM, double rangeM, int escortCount)
        {
            double step = rangeM > 1e-6 ? spacingM / rangeM : MaxSpreadRad;
            if (step < MinSpreadStepRad) step = MinSpreadStepRad;
            int outerRank = escortCount > 0 ? (escortCount + 1) / 2 : 1;
            double widest = MaxSpreadRad / outerRank;
            return step > widest ? widest : step;
        }

        /// <summary>The bearing from a target at which escort <paramref name="slotIndex"/> holds its
        /// range: the lead's bearing <paramref name="leadBearing"/> turned about
        /// <paramref name="axis"/> by the same alternating ranks as <see cref="SlotOffset"/>, so the
        /// lead keeps its own bearing and its escorts fan out either side of it. Unit length. An
        /// axis along the bearing, or none, is replaced by a deterministic perpendicular.</summary>
        public static Vec3 SpreadBearing(Vec3 leadBearing, Vec3 axis, int slotIndex, double stepRad)
        {
            Vec3 b = Unit(leadBearing);
            if (IsZero(b)) b = new Vec3(1, 0, 0);

            Vec3 k = Unit(Sub(axis, Scale(b, Dot(axis, b))));
            if (IsZero(k))
            {
                Vec3 fallback = Math.Abs(b.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                k = Unit(Sub(fallback, Scale(b, Dot(fallback, b))));
            }

            int i = slotIndex < 0 ? 0 : slotIndex;
            int rank = i / 2 + 1;
            double side = (i % 2 == 0) ? 1.0 : -1.0;
            double angle = side * rank * stepRad;

            // Rodrigues about k, which is perpendicular to b: b·cosθ + (k×b)·sinθ.
            Vec3 kxb = Cross(k, b);
            return Unit(Add(Scale(b, Math.Cos(angle)), Scale(kxb, Math.Sin(angle))));
        }

        // ---- vector helpers (Vec3 carries no operators) ----------------------------------------

        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }
        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static bool IsZero(Vec3 v) { return v.X == 0 && v.Y == 0 && v.Z == 0; }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        private static Vec3 Unit(Vec3 v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return len > 1e-9 ? Scale(v, 1.0 / len) : new Vec3(0, 0, 0);
        }
    }
}
