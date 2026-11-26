namespace LlmPlayer.Core.Combat;

/// <summary>Game-independent facts about an item, mapped from Terraria's Item by the mod.</summary>
public readonly record struct ItemTraits(
    int Damage,
    bool NoUseStyle = false,
    bool IsAmmo = false,
    bool Places = false,
    bool FishingPole = false,
    bool Heals = false,
    bool IsSentry = false,
    bool IsSummonNonWhip = false,
    bool ShootsExplosive = false,
    int Pick = 0,
    int Axe = 0,
    int Hammer = 0);

public static class ItemRules
{
    /// <summary>Can break tiles or walls when used: pickaxes, axes, hammers, explosives.</summary>
    public static bool DestroysTiles(in ItemTraits t) => t.Pick > 0 || t.Axe > 0 || t.Hammer > 0 || t.ShootsExplosive;

    /// <summary>
    /// Damaging, usable-by-hand items the AI may fight with. Excludes anything that destroys tiles (a hammer aimed
    /// at an enemy smashes the wall behind it), ammo, placeables, potions, fishing poles, summons and sentries.
    /// </summary>
    public static bool IsWeapon(in ItemTraits t) =>
        t.Damage > 0 && !t.NoUseStyle && !t.IsAmmo && !t.Places && !t.FishingPole && !t.Heals
        && !t.IsSentry && !t.IsSummonNonWhip && !DestroysTiles(t);
}
