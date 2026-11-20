using System;

namespace Factions.Core
{
    /// <summary>
    /// How far Factions backs off its own one-off work while frames stall (FAC-445 slice 4). The governor's
    /// stall tail (<see cref="LoadGovernor.Decision.TailPressure"/>) halves <see cref="Scale"/> each window
    /// it reads Short, down to <see cref="Floor"/>; Hold keeps it; Clear doubles it back toward 1. The host
    /// multiplies its per-frame budgets by it (spawn admission, assembly and disassembly chunks, the
    /// economy-tick slicer, the store refresh), so the work is not dropped, only spread over more frames.
    ///
    /// <para><b>Whatever caused the stall</b> (operator, 2026-10-05: *"Yield on any slow frames"*): when SE's
    /// own entity update, physics or a collection is already stalling, Factions' work on the same frames
    /// compounds it. Players are never stepped on it.</para>
    /// </summary>
    public sealed class StallBackoff
    {
        /// <summary>The lowest scale: at it a 25 ms assembly target is ~6 ms and spawns are four times as
        /// far apart (operator, 2026-10-05: *".25 is good"*).</summary>
        public const double Floor = 0.25;

        public double Scale { get; private set; } = 1.0;

        /// <summary>One window's step; true when <see cref="Scale"/> changed.</summary>
        public bool Update(LoadGovernor.Pressure tail)
        {
            double before = Scale;
            if (tail == LoadGovernor.Pressure.Short) Scale = Math.Max(Floor, Scale * 0.5);
            else if (tail == LoadGovernor.Pressure.Clear) Scale = Math.Min(1.0, Scale * 2.0);
            return Scale != before;
        }

        /// <summary>Back to 1 at once; true when it was below. For the switch turning off.</summary>
        public bool Reset()
        {
            bool was = Scale < 1.0;
            Scale = 1.0;
            return was;
        }
    }
}
