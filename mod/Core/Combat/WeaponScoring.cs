namespace LlmPlayer.Core.Combat;

/// <summary>Melee = fight up close (swords, spears, yoyos, flails, whips); Ranged = keep distance (guns, bows, magic,
/// thrown).</summary>
public enum WeaponStyle { Melee, Ranged }

/// <summary>
/// One weapon in the inventory, with the player's modifiers already applied to <see cref="Damage"/> (and ammo damage
/// added). <see cref="Usable"/> = has the ammo / mana it needs right now. <see cref="ShootSpeed"/> is the projectile's
/// speed in pixels per tick (0 for pure swings). <see cref="ReachTiles"/>: melee reach, or the preferred max range.
/// </summary>
public readonly record struct WeaponCandidate(
    int Slot, int Damage, int UseTime, int UseAnimation, int ReuseDelay, int CritChance, bool AutoReuse, bool Channel,
    WeaponStyle Style, float ShootSpeed, float ReachTiles, bool Usable);

public static class WeaponScoring
{
    /// <summary>
    /// Expected damage per second against a defenceless target: damage × (1 + crit%) × uses per cycle × 60 / cycle
    /// ticks. A cycle is one use animation plus reuseDelay; ranged weapons whose useTime is shorter than their
    /// animation fire several times per animation (Clockwork Assault Rifle). Weapons without autoReuse (and not
    /// channelled) need one released tick between uses.
    /// </summary>
    public static double Dps(in WeaponCandidate w) => Dps(w, 0);

    /// <summary><see cref="Dps(in WeaponCandidate)"/> against a target with this defense: each hit loses
    /// defense/2 (Classic mode), at least 1 damage remains. Seen in game: against the Eye of Cthulhu (12 defense) the
    /// AI switched from a bow (~7 per hit after defense) to a Wand of Sparking (8 → ~2 per hit).</summary>
    public static double Dps(in WeaponCandidate w, int targetDefense)
    {
        if (w.Damage <= 0) return 0;
        int perHit = Math.Max(1, w.Damage - targetDefense / 2);
        int anim = Math.Max(1, w.Style == WeaponStyle.Melee ? w.UseAnimation : Math.Max(w.UseAnimation, w.UseTime));
        int uses = w.Style == WeaponStyle.Melee ? 1 : Math.Max(1, (int)Math.Ceiling(anim / (double)Math.Max(1, w.UseTime)));
        int cycle = anim + Math.Max(0, w.ReuseDelay) + (w.AutoReuse || w.Channel ? 0 : 1);
        return perHit * (1 + Math.Clamp(w.CritChance, 0, 100) / 100.0) * uses * 60.0 / cycle;
    }

    /// <summary>
    /// How much of a weapon's DPS counts when choosing: short melee means standing inside the enemy's contact range
    /// for every hit. Seen in game: a Copper Shortsword (2.5 tiles) beat a Copper Bow of about the same DPS and the
    /// player took 126 damage killing one zombie. Ranged 1.0; melee under 3 tiles 0.6, under 4 tiles 0.8, else 1.0.
    /// </summary>
    public static double SafetyFactor(in WeaponCandidate w) =>
        w.Style == WeaponStyle.Ranged ? 1.0 : w.ReachTiles < 3f ? 0.6 : w.ReachTiles < 4f ? 0.8 : 1.0;

    /// <summary>The value weapons are chosen by: <see cref="Dps"/> × <see cref="SafetyFactor"/>.</summary>
    public static double Score(in WeaponCandidate w, int targetDefense = 0) => Dps(w, targetDefense) * SafetyFactor(w);

    /// <summary>Best usable weapon by <see cref="Score"/>; ties go to the lower slot. Null if none.</summary>
    public static WeaponCandidate? Best(IEnumerable<WeaponCandidate> weapons, int targetDefense = 0)
    {
        WeaponCandidate? best = null;
        double bestDps = 0;
        foreach (var w in weapons)
        {
            if (!w.Usable) continue;
            double dps = Score(w, targetDefense);
            if (dps <= 0) continue;
            if (best is { } b && (dps < bestDps || (dps == bestDps && b.Slot < w.Slot))) continue;
            best = w;
            bestDps = dps;
        }
        return best;
    }

    /// <summary>
    /// How far (tiles, from the player's centre) a melee weapon connects: a swing covers about the item's sprite size
    /// beyond the player's half-width; projectile melee (spears, yoyos, flails, boomerangs) reaches about 4 tiles.
    /// Never below 2.
    /// </summary>
    public static float MeleeReachTiles(int sizePx, float scale, bool projectileMelee)
    {
        if (projectileMelee) return 4f;
        return Math.Max(2f, sizePx * scale / 16f + 1f);
    }
}
