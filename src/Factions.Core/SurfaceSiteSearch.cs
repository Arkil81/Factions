using System;

namespace Factions.Core
{
    /// <summary>
    /// Where a surface station stands when the terrain under its node is too steep for it.
    ///
    /// <para>A station is founded at its node, and nothing on the founding or reclassification path
    /// looks at the ground: a node can land on a cliff face and its station is then snapped onto the
    /// closest surface point and half buried in the rock (production, 2026-11-21: METC Caravan Post
    /// standing against a sheer wall, the landing pad hanging in the air).</para>
    ///
    /// <para>The test is SE's own for planetary installations
    /// (<c>MyPlanetSurfacePlacement.CheckEdgeSlopes</c>): a site is refused when the ground under any
    /// corner of the footprint lies more than <c>DOT_MISALIGN_LIMIT = 0.245</c> along "up" from the
    /// centre, i.e. at a rise over run of <see cref="MaxGrade"/>. Here it is measured on a ring of
    /// samples at the footprint's reach, which is rotation-free and needs no oriented box.</para>
    ///
    /// <para>Pure and deterministic: the same terrain always yields the same choice, so a station
    /// that is moved for a spawn and not persisted lands in the same place on every spawn.</para>
    /// </summary>
    public static class SurfaceSiteSearch
    {
        /// <summary>Rise over run SE tolerates between a footprint's centre and its corners:
        /// tan(asin(0.245)).</summary>
        public const double MaxGrade = 0.2527;

        /// <summary>Samples on the footprint ring; the centre is one more.</summary>
        public const int RingSamples = 8;

        /// <summary>Candidate rings searched outward from the node.</summary>
        public const int Rings = 6;

        /// <summary>Candidates per ring.</summary>
        public const int Directions = 8;

        /// <summary>Smallest spacing between candidate rings, so a small prefab still searches
        /// past the edge of a cliff.</summary>
        public const double MinStepM = 20.0;

        /// <summary>Best-effort move when nothing within the search is level enough: only taken
        /// when it at least halves the grade.</summary>
        public const double BestEffortFraction = 0.5;

        /// <summary>The worst rise over run between the ground at (<paramref name="eastM"/>,
        /// <paramref name="northM"/>) and the ground on a ring of <paramref name="reachM"/> around it.
        /// <paramref name="altitudeAt"/> answers the terrain altitude in metres at a tangent-plane
        /// offset from the node.</summary>
        public static double GradeAt(Func<double, double, double> altitudeAt, double eastM, double northM,
                                     double reachM, out double worstRiseM)
        {
            double reach = Math.Max(reachM, 1.0);
            double centre = altitudeAt(eastM, northM);
            worstRiseM = 0.0;
            for (int i = 0; i < RingSamples; i++)
            {
                double angle = 2.0 * Math.PI * i / RingSamples;
                double h = altitudeAt(eastM + reach * Math.Cos(angle), northM + reach * Math.Sin(angle));
                double rise = Math.Abs(h - centre);
                if (rise > worstRiseM) worstRiseM = rise;
            }
            return worstRiseM / reach;
        }

        /// <summary>The footprint's grade at the node alone: what the sweep reports, nine samples.</summary>
        public static double GradeAtNode(Func<double, double, double> altitudeAt, double reachM, out double worstRiseM)
        {
            return GradeAt(altitudeAt, 0.0, 0.0, reachM, out worstRiseM);
        }

        /// <summary>Picks the site: the node itself when it is level enough, else the most level
        /// candidate on the nearest ring that has a level-enough one, else the most level candidate
        /// anywhere if that at least halves the grade, else the node.</summary>
        public static SurfaceSiteChoice Choose(Func<double, double, double> altitudeAt, double reachM)
        {
            double reach = Math.Max(reachM, 1.0);
            int probes = RingSamples + 1;
            double rise;
            double original = GradeAt(altitudeAt, 0.0, 0.0, reach, out rise);

            var choice = new SurfaceSiteChoice
            {
                ReachM = reach,
                OriginalGrade = original,
                OriginalRiseM = rise,
                Grade = original,
                RiseM = rise,
                Level = original <= MaxGrade
            };
            if (choice.Level) { choice.Probes = probes; return choice; }

            double step = Math.Max(reach, MinStepM);
            bool haveAny = false;
            double anyGrade = double.MaxValue, anyRise = 0, anyEast = 0, anyNorth = 0;

            for (int ring = 1; ring <= Rings; ring++)
            {
                // Odd rings are turned half a direction so successive rings cover each other's gaps.
                double turn = (ring % 2 == 1) ? Math.PI / Directions : 0.0;
                double radius = ring * step;
                bool haveLevel = false;
                double levelGrade = double.MaxValue, levelRise = 0, levelEast = 0, levelNorth = 0;

                for (int d = 0; d < Directions; d++)
                {
                    double angle = turn + 2.0 * Math.PI * d / Directions;
                    double east = radius * Math.Cos(angle);
                    double north = radius * Math.Sin(angle);
                    double r;
                    double g = GradeAt(altitudeAt, east, north, reach, out r);
                    probes += RingSamples + 1;

                    if (g < anyGrade) { haveAny = true; anyGrade = g; anyRise = r; anyEast = east; anyNorth = north; }
                    if (g <= MaxGrade && g < levelGrade)
                    {
                        haveLevel = true; levelGrade = g; levelRise = r; levelEast = east; levelNorth = north;
                    }
                }

                if (haveLevel)
                {
                    choice.Moved = true;
                    choice.Level = true;
                    choice.EastM = levelEast;
                    choice.NorthM = levelNorth;
                    choice.Grade = levelGrade;
                    choice.RiseM = levelRise;
                    choice.Probes = probes;
                    return choice;
                }
            }

            if (haveAny && anyGrade <= original * BestEffortFraction)
            {
                choice.Moved = true;
                choice.EastM = anyEast;
                choice.NorthM = anyNorth;
                choice.Grade = anyGrade;
                choice.RiseM = anyRise;
            }
            choice.Probes = probes;
            return choice;
        }

        /// <summary>The furthest the search can move a station of this reach.</summary>
        public static double MaxMoveM(double reachM)
        {
            return Rings * Math.Max(Math.Max(reachM, 1.0), MinStepM);
        }
    }

    /// <summary>What <see cref="SurfaceSiteSearch.Choose"/> decided.</summary>
    public struct SurfaceSiteChoice
    {
        /// <summary>True when the site is not the node.</summary>
        public bool Moved;
        /// <summary>True when the chosen site is within <see cref="SurfaceSiteSearch.MaxGrade"/>.</summary>
        public bool Level;
        /// <summary>Tangent-plane offset of the chosen site from the node, metres.</summary>
        public double EastM, NorthM;
        public double ReachM;
        public double OriginalGrade, OriginalRiseM;
        public double Grade, RiseM;
        /// <summary>Terrain samples taken.</summary>
        public int Probes;

        public double DistanceM { get { return Math.Sqrt(EastM * EastM + NorthM * NorthM); } }
    }
}
