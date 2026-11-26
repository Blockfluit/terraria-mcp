namespace LlmPlayer.Core.Combat;

/// <summary>
/// When a visible attackable NPC counts as a threat auto-defend should answer. Distances are in tiles from the
/// player's centre to the nearest point of the NPC's hitbox.
/// <list type="bullet">
/// <item>Melee: within the weapon's reach. Ranged: within <see cref="CombatStance.RangedMaxTiles"/> with line of
/// sight.</item>
/// <item>Contact: an NPC that hurt the player by touching it within the last <see cref="ContactMemoryTicks"/> also
/// counts within reach + <see cref="ContactExtraTiles"/> (melee) and needs no line of sight (it was touching us), so a
/// zombie whose hitbox reaches just past the computed swing reach still gets fought.</item>
/// </list>
/// </summary>
public static class ThreatRules
{
    public const int ContactMemoryTicks = 60;
    public const float ContactExtraTiles = 1f;

    /// <param name="ticksSinceContact">Ticks since this NPC last hit the player by contact; negative or
    /// <see cref="int.MaxValue"/> when it never did.</param>
    public static bool RecentContact(int ticksSinceContact) => ticksSinceContact is >= 0 and <= ContactMemoryTicks;

    /// <summary>How far away (tiles) the NPC may be and still count as a threat.</summary>
    public static float Range(WeaponStyle style, float weaponReach, int ticksSinceContact)
    {
        if (style == WeaponStyle.Ranged) return CombatStance.RangedMaxTiles;
        return RecentContact(ticksSinceContact) ? weaponReach + ContactExtraTiles : weaponReach;
    }

    public static bool NeedsLineOfSight(WeaponStyle style, int ticksSinceContact) =>
        style == WeaponStyle.Ranged && !RecentContact(ticksSinceContact);

    public static bool IsThreat(WeaponStyle style, float weaponReach, float distance, int ticksSinceContact, bool lineOfSight) =>
        distance <= Range(style, weaponReach, ticksSinceContact) && (lineOfSight || !NeedsLineOfSight(style, ticksSinceContact));
}
