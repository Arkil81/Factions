namespace Factions.Core
{
    /// <summary>How a squadron fights: which range it seeks. See docs/fleet-tactics.md.</summary>
    public enum TacticArm
    {
        Brawl = 0,
        Standoff = 1,
        Kite = 2,
        AttackRun = 3,
    }

    /// <summary>The surviving fraction at which a squadron leaves a fight it is losing.</summary>
    public enum BreakOffLevel
    {
        Low = 0,
        Mid = 1,
        High = 2,
    }
}
