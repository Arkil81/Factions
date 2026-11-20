using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// A pilot's speed from how far their ship moved between two passes, beside the physics velocity
    /// the host also reads.
    ///
    /// <para><b>Why physics velocity is not enough.</b> The FSD supercruise mod moves a grid with a
    /// teleport-style world-matrix write, so <c>Physics.LinearVelocity</c> reads near zero the whole
    /// way (the same reason the signature's supercruise term measures displacement). Live
    /// 2026-10-19: a pilot covered 348 km in 30 s through IMPR space and every TRESPASS_CROSSING line
    /// read <c>speed=0</c>, so the trespass ladder took a supercruising ship for one lingering after
    /// its Intercept warning (<see cref="TrespassLadder.IsLingering"/>) and would have charged it
    /// reputation for passing through.</para>
    ///
    /// <para><b>Keyed by pilot, stamped with the grid.</b> Changing ships restarts the measurement
    /// rather than reading the distance between two hulls as travel. A jump also reads as very fast,
    /// which is correct here: a ship that jumped is not lingering.</para>
    /// </summary>
    public sealed class TravelSpeedBook
    {
        /// <summary>Two samples closer together than this give no new reading: a short baseline turns
        /// position jitter into speed. The trespass pass runs every 5 s.</summary>
        public const double MinSampleSeconds = 1.0;

        private struct Sample
        {
            public long GridEntityId;
            public Vec3 PositionKm;
            public DateTime At;
            public double SpeedMs;
        }

        private readonly Dictionary<long, Sample> _last = new Dictionary<long, Sample>();

        /// <summary>The speed implied by the pilot's movement since their previous sample, m/s; 0 for
        /// a first sample or a new grid.</summary>
        public double Observe(long identityId, long gridEntityId, Vec3 positionKm, DateTime nowUtc)
        {
            Sample prior;
            if (_last.TryGetValue(identityId, out prior) && prior.GridEntityId == gridEntityId)
            {
                double seconds = (nowUtc - prior.At).TotalSeconds;
                if (seconds < MinSampleSeconds) return prior.SpeedMs;
                double speed = positionKm.DistanceTo(prior.PositionKm) * 1000.0 / seconds;
                _last[identityId] = new Sample { GridEntityId = gridEntityId, PositionKm = positionKm, At = nowUtc, SpeedMs = speed };
                return speed;
            }
            _last[identityId] = new Sample { GridEntityId = gridEntityId, PositionKm = positionKm, At = nowUtc, SpeedMs = 0 };
            return 0;
        }

        /// <summary>The speed the trespass ladder judges: the faster of the two readings, so neither
        /// a teleport-moved ship (physics 0) nor a first sample (displacement 0) reads as stopped
        /// when the other knows better.</summary>
        public static double Effective(double physicsMs, double travelMs)
        {
            return Math.Max(physicsMs, travelMs);
        }

        /// <summary>Forgets pilots no longer online.</summary>
        public void Prune(ICollection<long> onlineIdentities)
        {
            if (_last.Count == 0) return;
            List<long> gone = null;
            foreach (long id in _last.Keys)
                if (onlineIdentities == null || !onlineIdentities.Contains(id))
                    (gone ?? (gone = new List<long>())).Add(id);
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) _last.Remove(gone[i]);
        }

        public int Count { get { return _last.Count; } }
    }
}
