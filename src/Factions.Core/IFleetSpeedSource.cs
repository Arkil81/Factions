namespace Factions.Core
{
    /// <summary>The two speeds a leg is flown at, in the units each half of the burn model wants.
    /// Zero in either field means "use the tuning default for it".</summary>
    public struct LegSpeeds
    {
        /// <summary>Thruster-phase speed, m/s — the counterpart of
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/>. Hover impulse is inversely
        /// proportional to it and every Δv term is proportional to it.</summary>
        public double ThrusterMs;

        /// <summary>FSD-phase speed, km/min — the counterpart of
        /// <see cref="FactionTuning.FleetSpeedKmPerMin"/>.</summary>
        public double CruiseKmPerMin;

        /// <summary>Both defaults. What the fuel model prices with when no speed source is wired.</summary>
        public static LegSpeeds Default { get { return new LegSpeeds(); } }
    }

    /// <summary>What speed a given fleet actually flies. The fuel model asks; it does not decide.
    ///
    /// <para>This is the seam a fleet-speed model plugs into, and the reason the burn model needs
    /// one: burn is not monotonic in speed. Hover is paid per second and so falls as speed rises,
    /// while Δv is paid per metre per second and so rises with it, which means capping a fleet's
    /// speed can raise or lower what it burns depending on how much of its leg sits inside gravity.
    /// Pricing every leg at one global speed cannot express that.</para>
    ///
    /// <para>Optional: <see cref="WorldModel.FleetSpeeds"/> is null until something wires one, and
    /// the fuel model then prices at the tuning defaults exactly as before.</para></summary>
    public interface IFleetSpeedSource
    {
        /// <summary>The speeds <paramref name="fleet"/> flies this leg. Called once per priced leg,
        /// inside the Torch update tick at roughly 1.6 dispatches a second, so it must be a lookup
        /// rather than a computation.</summary>
        LegSpeeds SpeedsFor(PendingFleetAction fleet);
    }
}
