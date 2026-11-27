namespace Factions.Wire
{
    /// <summary>
    /// Where a moving contact is between the server's samples. Both client features that draw one --
    /// the scanner HUD and the map window -- extrapolate identically, so the rule lives here rather
    /// than once per feature, and is exercised by the wire tests instead of only by a live client.
    ///
    /// <para>Unit-agnostic: position, velocity and target need only agree with each other, and the
    /// velocity's time unit with <c>elapsed</c>. Callers convert the wire's km and km/min to the
    /// metres and seconds SE draws in.</para>
    /// </summary>
    public static class DeadReckon
    {
        /// <summary>
        /// Advances a sample along its velocity, clamped at the target: overshooting past the
        /// destination looks worse than arriving slightly early and waiting. A contact that is not
        /// moving, or is being asked about the instant it was sampled, is simply where it was.
        /// </summary>
        /// <summary>How long a contact with no offered destination keeps coasting on its last
        /// velocity, seconds. The server corrects it every pass; past this the sample is stale and
        /// the marker holds where it reckoned to rather than sailing on.</summary>
        public const double OpenHorizonSeconds = 15.0;

        /// <summary>
        /// Dead reckoning for a contact whose destination was not offered: straight along the last
        /// velocity, with no target to stop at, for at most <see cref="OpenHorizonSeconds"/>. A
        /// fleet decelerating onto a node therefore overshoots by up to one pass, and the next
        /// sample pulls it back; that is the price of not being told where it is going.
        /// </summary>
        public static void ExtrapolateOpen(
            double posX, double posY, double posZ,
            double velX, double velY, double velZ,
            double elapsed,
            out double x, out double y, out double z)
        {
            if (elapsed < 0) elapsed = 0;
            if (elapsed > OpenHorizonSeconds) elapsed = OpenHorizonSeconds;
            x = posX + velX * elapsed;
            y = posY + velY * elapsed;
            z = posZ + velZ * elapsed;
        }

        public static void Extrapolate(
            double posX, double posY, double posZ,
            double velX, double velY, double velZ,
            double targetX, double targetY, double targetZ,
            double elapsed,
            out double x, out double y, out double z)
        {
            x = posX;
            y = posY;
            z = posZ;
            if (elapsed <= 0) return;

            double dx = velX * elapsed;
            double dy = velY * elapsed;
            double dz = velZ * elapsed;
            double travelledSq = dx * dx + dy * dy + dz * dz;
            if (travelledSq <= 0) return;

            double toX = targetX - posX;
            double toY = targetY - posY;
            double toZ = targetZ - posZ;
            double toTargetSq = toX * toX + toY * toY + toZ * toZ;
            // Unconditional, including a sample already sitting on its target: "do not go past it"
            // at zero remaining distance means "stay", not "sail on because the clamp cannot fire".
            if (travelledSq > toTargetSq)
            {
                x = targetX;
                y = targetY;
                z = targetZ;
                return;
            }

            x = posX + dx;
            y = posY + dy;
            z = posZ + dz;
        }
    }
}
