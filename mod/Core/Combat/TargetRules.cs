namespace LlmPlayer.Core.Combat;

/// <summary>What target selection needs to know about one NPC. (X, Y) is any consistent position unit (tiles).</summary>
public readonly record struct NpcTraits(
    int Id, bool Friendly, bool TownNpc, bool Critter, bool DontTakeDamage, bool Immortal, int LifeMax, float X, float Y);

public enum TargetVerdict { Ok, TownNpc, Critter, Friendly, Invulnerable }

/// <summary>
/// Who may be attacked: hostile NPCs only. Town NPCs, critters and any other friendly NPC are refused (the safety
/// rule "never hurt town NPCs or critters"); invulnerable ones (dontTakeDamage, immortal, lifeMax ≤ 5 — vanilla's own
/// homing filter, which also excludes target dummies) are pointless to fight.
/// </summary>
public static class TargetRules
{
    public static TargetVerdict Check(in NpcTraits n) =>
        n.TownNpc ? TargetVerdict.TownNpc
        : n.Critter ? TargetVerdict.Critter
        : n.Friendly ? TargetVerdict.Friendly
        : n.DontTakeDamage || n.Immortal || n.LifeMax <= 5 ? TargetVerdict.Invulnerable
        : TargetVerdict.Ok;

    public static bool IsAttackable(in NpcTraits n) => Check(n) == TargetVerdict.Ok;

    /// <summary>Nearest attackable NPC within <paramref name="maxRange"/> (Euclidean) of (x, y), or null.
    /// Callers pass only visible NPCs.</summary>
    public static NpcTraits? PickNearest(IEnumerable<NpcTraits> npcs, float x, float y, float maxRange)
    {
        NpcTraits? best = null;
        float bestD2 = maxRange * maxRange;
        foreach (var n in npcs)
        {
            if (!IsAttackable(n)) continue;
            float dx = n.X - x, dy = n.Y - y, d2 = dx * dx + dy * dy;
            if (d2 > bestD2) continue;
            if (best is { } b && d2 == bestD2 && b.Id < n.Id) continue;
            best = n;
            bestD2 = d2;
        }
        return best;
    }
}
