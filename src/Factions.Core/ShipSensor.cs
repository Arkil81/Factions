using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One antenna on a player's ship, as the host measured it.</summary>
    public struct AntennaReading
    {
        /// <summary>Block volume in m³ -- the definition's <c>Size</c> in cubes times the cube
        /// edge cubed. The only field that tells antenna variants apart.</summary>
        public double VolumeM3;

        /// <summary>Configured broadcast radius, metres.</summary>
        public double RadiusM;

        /// <summary>Broadcasting is switched on. Off, the antenna still listens.</summary>
        public bool Broadcasting;
    }

    /// <summary>
    /// A player ship's sensor strength, from the antennas it carries.
    ///
    /// <code>
    /// antennaKm = aperture x (Passive + Active x radius / ReferenceM)
    /// sensorKm  = Floor + sqrt(sum of antennaKm²)
    /// </code>
    ///
    /// <para>Aperture multiplies both halves, so a bigger antenna listens and illuminates better
    /// alike. Several antennas combine as a root sum of squares: n identical antennas are worth
    /// sqrt(n) of one, the same diminishing-returns curve <see cref="Signature.ForShip"/> applies to
    /// convoy size, and one large antenna still outweighs several small ones.</para>
    ///
    /// <para>The passive half costs no signature. Only broadcasting feeds the emission term, so an
    /// antenna is eyes bought with hull volume and loudness paid only when it transmits.</para>
    /// </summary>
    public static class ShipSensor
    {
        public static double ApertureOf(double volumeM3, FactionTuning t)
        {
            if (t == null || volumeM3 <= 0 || t.SensorAntennaReferenceVolumeM3 <= 0) return 0;
            return Math.Pow(volumeM3 / t.SensorAntennaReferenceVolumeM3, t.SensorApertureExponent);
        }

        /// <summary>Range one antenna adds on its own, before combining.</summary>
        public static double AntennaKm(AntennaReading antenna, FactionTuning t)
        {
            double aperture = ApertureOf(antenna.VolumeM3, t);
            if (aperture <= 0) return 0;
            double share = 0;
            if (antenna.Broadcasting && t.SensorAntennaReferenceM > 0 && antenna.RadiusM > 0)
                share = Math.Min(1.0, antenna.RadiusM / t.SensorAntennaReferenceM);
            return aperture * (t.SensorPassiveKm + t.SensorActiveKm * share);
        }

        public static double StrengthKm(IList<AntennaReading> antennas, FactionTuning t)
        {
            if (t == null) return 0;
            double sumSq = 0;
            if (antennas != null)
            {
                for (int i = 0; i < antennas.Count; i++)
                {
                    double km = AntennaKm(antennas[i], t);
                    sumSq += km * km;
                }
            }
            return Math.Max(0, t.SensorFloorKm) + Math.Sqrt(sumSq);
        }

        /// <summary>One reference antenna broadcasting at full radius. The range a client scales its
        /// distance falloff against, and what the bridge probe assumes.</summary>
        public static double ReferenceKm(FactionTuning t)
        {
            if (t == null) return 0;
            return Math.Max(0, t.SensorFloorKm) + t.SensorPassiveKm + t.SensorActiveKm;
        }
    }
}
