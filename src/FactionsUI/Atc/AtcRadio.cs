using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace FactionsUI.Atc;

/// <summary>
/// The local player's own radio, for aiming by ear (FAC-419, docs/atc-voice.md): where they are,
/// and which way each reflector aboard the ship they are flying points, right now. Every playing
/// call asks it for the gain toward its source each frame, so turning the ship is heard at once and
/// costs no message.
///
/// <para>The same rules as the server's <c>ShipAntennas.Read</c>: the whole mechanical group, only
/// working antennas the player may use, and <see cref="RadioAim.TryKindOf"/> for which are
/// reflectors. A suit has none. Which blocks are aboard is re-read every five seconds or on a new
/// grid; their axes every frame.</para>
///
/// <para><b>Which antenna hears a call</b> (<see cref="Pilot"/>, <see cref="Controller"/>): each one
/// hears it at the ship's all-round signal plus its own gain toward the source (none for an
/// omnidirectional antenna), against its own squelch (<see cref="AtcSquelch"/>). The call is heard
/// through whichever clears its squelch by the most; if none does, it is not heard.</para>
/// </summary>
internal static class AtcRadio
{
    private static readonly List<(WireReflector? Kind, MyRadioAntenna Block)> Antennas = new();
    private static readonly List<MyCubeGrid> Group = new();
    private static long _gridId;
    private static DateTime _readAt;

    /// <summary>The listener's position, km. Null before the first frame with a character.</summary>
    public static Vector3D? PosKm { get; private set; }

    public static int AntennaCount => Antennas.Count;

    /// <summary>Per frame, before any line mixes.</summary>
    public static void Refresh(DateTime now)
    {
        MySession session = MySession.Static;
        Vector3D? pos = session?.LocalCharacter?.PositionComp.GetPosition();
        PosKm = pos.HasValue ? pos.Value / 1000.0 : (Vector3D?)null;

        var grid = session?.TopMostControlledEntity as MyCubeGrid;
        if (grid == null || grid.Closed) { Antennas.Clear(); _gridId = 0; return; }
        if (grid.EntityId == _gridId && (now - _readAt).TotalSeconds < 5) return;
        _gridId = grid.EntityId;
        _readAt = now;
        Read(grid, session.LocalPlayerId);
    }

    private static void Read(MyCubeGrid grid, long identity)
    {
        Antennas.Clear();
        Group.Clear();
        try { MyCubeGridGroups.Static?.Mechanical?.GetGroupNodes(grid, Group); }
        catch (Exception) { Group.Clear(); }
        if (!Group.Contains(grid)) Group.Add(grid);
        foreach (MyCubeGrid member in Group)
        {
            if (member == null || member.Closed) continue;
            foreach (MyRadioAntenna antenna in member.GetFatBlocks<MyRadioAntenna>())
            {
                if (antenna == null) continue;
                MyRelationsBetweenPlayerAndBlock relation = antenna.GetUserRelationToOwner(identity);
                if (relation != MyRelationsBetweenPlayerAndBlock.Owner
                    && relation != MyRelationsBetweenPlayerAndBlock.FactionShare
                    && relation != MyRelationsBetweenPlayerAndBlock.NoOwnership) continue;
                Antennas.Add((RadioAim.TryKindOf(antenna.BlockDefinition?.Id.SubtypeName, out WireReflector kind) ? kind : null, antenna));
            }
        }
        Group.Clear();
    }

    /// <summary>Distance to the call's source, km, or null when either end is unknown.</summary>
    public static double? DistanceKm(in WireRadioAim aim)
    {
        if (!aim.HasSource || !PosKm.HasValue) return null;
        return Vector3D.Distance(PosKm.Value, new Vector3D(aim.SourceX, aim.SourceY, aim.SourceZ));
    }

    /// <summary>One antenna's gain toward the call's source right now, dB: 0 for an omnidirectional one.</summary>
    private static double GainDb(WireReflector? kind, MyRadioAntenna block, in WireRadioAim aim, Vector3D to)
    {
        if (kind == null) return 0;
        RadioAim.PatternOf(kind.Value, aim, out double peak, out double beam);
        MatrixD m = block.WorldMatrix;
        Vector3D axis = kind == WireReflector.Parabolic ? m.Backward : m.Up;
        return RadioAim.GainDb(peak, beam, axis.X, axis.Y, axis.Z, to.X, to.Y, to.Z);
    }

    /// <summary>The best gain any working reflector has toward the call's source right now, dB.</summary>
    public static double GainDb(in WireRadioAim aim)
    {
        if (!aim.HasSource || !PosKm.HasValue) return 0;
        Vector3D to = new Vector3D(aim.SourceX, aim.SourceY, aim.SourceZ) - PosKm.Value;
        double best = 0;
        foreach ((WireReflector? kind, MyRadioAntenna block) in Antennas)
        {
            if (block.Closed || !block.IsWorking) continue;
            double g = GainDb(kind, block, aim, to);
            if (g > best) best = g;
        }
        return best;
    }

    /// <summary>How a call is heard: through which antenna's signal and squelch.</summary>
    public readonly struct Reception
    {
        public readonly double SnrDb, SquelchDb, Share;
        public Reception(double snr, double squelch, double share) { SnrDb = snr; SquelchDb = squelch; Share = share; }
        public bool Open => SnrDb >= SquelchDb;
    }

    /// <summary>A pilot call at <paramref name="baseSnrDb"/> with no antenna gain. With no antenna aboard
    /// (a suit, a bare seat), the call's own squelch applies.</summary>
    public static Reception Pilot(in WireRadioAim aim, double baseSnrDb, double defaultSquelchDb)
    {
        Reception best = new Reception(baseSnrDb, defaultSquelchDb, 0);
        if (Antennas.Count == 0 || !PosKm.HasValue) return best;
        Vector3D to = aim.HasSource ? new Vector3D(aim.SourceX, aim.SourceY, aim.SourceZ) - PosKm.Value : Vector3D.Zero;
        bool any = false;
        foreach ((WireReflector? kind, MyRadioAntenna block) in Antennas)
        {
            if (block.Closed || !block.IsWorking) continue;
            double snr = baseSnrDb + (aim.HasSource ? GainDb(kind, block, aim, to) : 0);
            double squelch = AtcSquelch.Get(block.EntityId, kind.HasValue);
            if (!any || snr - squelch > best.SnrDb - best.SquelchDb) best = new Reception(snr, squelch, 0);
            any = true;
        }
        return best;
    }

    /// <summary>The controller's signal on the approved distance ladder (docs/atc-voice.md, "Who hears
    /// it"): the static under the voice IS the signal-to-noise, so under 25% of reach reads clean
    /// (<see cref="CleanDb"/>), then 25, 12 and 5 dB; past the reach it is gone.</summary>
    public const double CleanDb = 40;

    public static double LadderSnrDb(double share) =>
        share < 0.25 ? CleanDb : share < 0.5 ? 25 : share < 0.8 ? 12 : share < 1.0 ? 5 : -100;

    /// <summary>A controller line: each antenna's share of its own reach (the omni reach times its gain)
    /// on the ladder, against its squelch. Without an aim (an older server) the share sent is used.</summary>
    public static Reception Controller(in WireRadioAim aim, byte fallbackShare)
    {
        double? d = DistanceKm(aim);
        if (!d.HasValue)
        {
            double f = fallbackShare / 255.0;
            return new Reception(LadderSnrDb(f), AtcSquelch.NoAntennaDb, f);
        }
        double omniShare = RadioAim.Share(d.Value, aim.OmniReachKm, 0);
        Reception best = new Reception(LadderSnrDb(omniShare), AtcSquelch.NoAntennaDb, omniShare);
        if (Antennas.Count == 0) return best;
        Vector3D to = new Vector3D(aim.SourceX, aim.SourceY, aim.SourceZ) - PosKm.Value;
        bool any = false;
        foreach ((WireReflector? kind, MyRadioAntenna block) in Antennas)
        {
            if (block.Closed || !block.IsWorking) continue;
            double share = RadioAim.Share(d.Value, aim.OmniReachKm, GainDb(kind, block, aim, to));
            double snr = LadderSnrDb(share), squelch = AtcSquelch.Get(block.EntityId, kind.HasValue);
            if (!any || snr - squelch > best.SnrDb - best.SquelchDb) best = new Reception(snr, squelch, share);
            any = true;
        }
        return best;
    }

    public static void Reset()
    {
        Antennas.Clear();
        _gridId = 0;
        PosKm = null;
    }
}
