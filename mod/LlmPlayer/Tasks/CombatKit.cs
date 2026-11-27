using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Observe;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace LlmPlayer.Tasks;

/// <summary>A weapon chosen from the inventory: its slot, the item, and the stats it was scored with.</summary>
public sealed record Weapon(int Slot, Item Item, WeaponCandidate Stats)
{
    public WeaponStyle Style => Stats.Style;
    public float Reach => Stats.ReachTiles;
}

/// <summary>
/// Game-side combat helpers shared by <see cref="AttackTask"/> and <see cref="Reflexes"/>: NPC traits for Core's
/// <see cref="TargetRules"/>, weapon candidates for <see cref="WeaponScoring"/>, distances/line of sight, and the
/// aim-and-use control intent. Main thread only.
/// </summary>
public static class CombatKit
{
    public static NpcTraits Traits(NPC n) =>
        new(n.whoAmI, n.friendly, n.townNPC, n.CountsAsACritter, n.dontTakeDamage, n.immortal, n.lifeMax,
            n.Center.X / 16f, n.Center.Y / 16f);

    /// <summary>Visible, active NPCs (fair vision) as traits.</summary>
    public static List<NpcTraits> VisibleNpcs(Player p)
    {
        var screen = Visibility.Screen;
        var list = new List<NpcTraits>();
        foreach (var n in Main.npc)
            if (n.active && Visibility.IsEntityVisible(n, screen, p)) list.Add(Traits(n));
        return list;
    }

    /// <summary>Vector (tiles) from the player's centre to the nearest point of the NPC's hitbox.</summary>
    public static Vector2 Offset(Player p, NPC n)
    {
        var c = p.Center;
        var box = n.Hitbox;
        float nx = Math.Clamp(c.X, box.Left, box.Right), ny = Math.Clamp(c.Y, box.Top, box.Bottom);
        return new Vector2(nx - c.X, ny - c.Y) / 16f;
    }

    public static bool LineOfSight(Player p, NPC n) =>
        Collision.CanHitLine(p.position, p.width, p.height, n.position, n.width, n.height);

    /// <summary>
    /// Nearest visible attackable NPC the weapon can hit right now (<see cref="ThreatRules"/>): within melee reach, or
    /// within the ranged band's max with line of sight; an NPC that hurt the player by contact within the last second
    /// also counts up to one tile beyond melee reach. Distances are to the nearest point of the NPC's hitbox
    /// (<see cref="Offset"/>). Null if none.
    /// </summary>
    public static NPC? NearestThreat(Player p, Weapon w)
    {
        var screen = Visibility.Screen;
        NPC? best = null;
        float bestDist = float.MaxValue;
        foreach (var n in Main.npc)
        {
            if (!n.active || !TargetRules.IsAttackable(Traits(n)) || !Visibility.IsEntityVisible(n, screen, p)) continue;
            float d = Offset(p, n).Length();
            if (d >= bestDist) continue;
            int sinceContact = TicksSinceContact(n);
            if (d > ThreatRules.Range(w.Style, w.Reach, sinceContact)) continue;
            if (ThreatRules.NeedsLineOfSight(w.Style, sinceContact) && !LineOfSight(p, n)) continue;
            best = n;
            bestDist = d;
        }
        return best;
    }

    // ---- contact hits -------------------------------------------------------------------------------------------

    private static readonly (int Type, uint Tick)[] LastContact = new (int, uint)[Main.maxNPCs];

    internal static void RecordContact(NPC npc)
    {
        if (npc.whoAmI >= 0 && npc.whoAmI < Main.maxNPCs) LastContact[npc.whoAmI] = (npc.type, Main.GameUpdateCount);
    }

    /// <summary>Ticks since this NPC last hurt the local player by touching it, or int.MaxValue if it never did.</summary>
    public static int TicksSinceContact(NPC n)
    {
        if (n.whoAmI < 0 || n.whoAmI >= Main.maxNPCs) return int.MaxValue;
        var (type, tick) = LastContact[n.whoAmI];
        if (tick == 0 || type != n.type || tick > Main.GameUpdateCount) return int.MaxValue;
        uint dt = Main.GameUpdateCount - tick;
        return dt > int.MaxValue ? int.MaxValue : (int)dt;
    }

    // ---- weapons --------------------------------------------------------------------------------------------------

    /// <summary>Highest expected-DPS weapon usable right now (ammo / mana available), or null. Only a convenience for
    /// <c>attack</c> without <c>weapon</c> and for fallbacks: the LLM chooses weapons.</summary>
    /// <param name="targetDefense">The target's defense, so a low-damage fast weapon doesn't win against armor.</param>
    /// <param name="prefer">Prefer this style (a strategy's need) when any such weapon is usable.</param>
    /// <param name="useAmmo">Prefer weapons firing this ammo class (an ammo the LLM chose), when any is usable.</param>
    /// <param name="exclude">Never pick this item (the chosen weapon that just became unusable).</param>
    public static Weapon? BestWeapon(Player p, int targetDefense = 0, WeaponStyle? prefer = null, int useAmmo = 0, Item? exclude = null)
    {
        var candidates = Candidates(p, exclude);
        WeaponCandidate? best = null;
        if (useAmmo > 0)
            best = WeaponFallback.Pick(candidates.Where(c => p.inventory[c.Slot].useAmmo == useAmmo), targetDefense, prefer);
        best ??= WeaponFallback.Pick(candidates, targetDefense, prefer);
        return best is { } b ? new Weapon(b.Slot, p.inventory[b.Slot], b) : null;
    }

    /// <summary>Every weapon candidate in slots 0-57 (none while the player can't use items).</summary>
    public static List<WeaponCandidate> Candidates(Player p, Item? exclude = null)
    {
        var list = new List<WeaponCandidate>();
        if (p.noItems || p.cursed) return list;
        for (int i = 0; i < 58; i++)
            if (!ReferenceEquals(p.inventory[i], exclude) && Candidate(p, i) is { } c) list.Add(c);
        return list;
    }

    /// <summary>Where <paramref name="item"/> is now (slot 0-57): the same object, else the first item of its type
    /// (HeldItemLease and the player move items between slots). -1 if gone.</summary>
    public static int Locate(Player p, Item item)
    {
        for (int i = 0; i < 58; i++)
            if (ReferenceEquals(p.inventory[i], item) && !item.IsAir) return i;
        for (int i = 0; i < 58; i++)
            if (!p.inventory[i].IsAir && p.inventory[i].type == item.type) return i;
        return -1;
    }

    /// <summary>Slots 0-57 as Core's <see cref="AmmoOrder"/> sees them.</summary>
    public static AmmoSlot[] AmmoSlots(Player p)
    {
        var a = new AmmoSlot[AmmoOrder.SearchedSlots];
        for (int i = 0; i < a.Length; i++)
        {
            var it = p.inventory[i];
            a[i] = it.IsAir ? default : new AmmoSlot(it.ammo, it.type, it.stack);
        }
        return a;
    }

    private static Dictionary<int, string>? _ammoNames;

    /// <summary>A short name for an ammo class (<c>Item.useAmmo</c> / <c>Item.ammo</c>): "arrow", "bullet", "gel"…;
    /// otherwise the class's base item name, lower case. Null for 0.</summary>
    public static string? AmmoClassName(int ammoClass)
    {
        if (ammoClass <= 0) return null;
        _ammoNames ??= new Dictionary<int, string>
        {
            [AmmoID.Arrow] = "arrow", [AmmoID.Bullet] = "bullet", [AmmoID.Rocket] = "rocket", [AmmoID.Dart] = "dart",
            [AmmoID.Gel] = "gel", [AmmoID.FallenStar] = "fallen star", [AmmoID.Sand] = "sand", [AmmoID.Snowball] = "snowball",
            [AmmoID.Coin] = "coin", [AmmoID.Solution] = "solution", [AmmoID.Stake] = "stake", [AmmoID.Flare] = "flare",
        };
        return _ammoNames.TryGetValue(ammoClass, out var n) ? n : Lang.GetItemNameValue(ammoClass).ToLowerInvariant();
    }

    /// <summary>
    /// How much ammo the weapon has left: every inventory stack (ammo slots and main inventory) of the ammo type it
    /// uses; null if it uses none. Vanilla picks the next matching stack by itself when one runs out
    /// (<see cref="Player.ChooseAmmo"/>: ammo slots first, then the inventory), so this is the whole supply.
    /// </summary>
    public static int? AmmoCount(Player p, Item weapon)
    {
        if (weapon.useAmmo <= 0) return null;
        int n = 0;
        for (int i = 0; i < 58; i++)
        {
            var it = p.inventory[i];
            if (!it.IsAir && it.ammo == weapon.useAmmo && it.stack > 0) n += it.stack;
        }
        return n;
    }

    /// <summary>Is the chosen weapon still in the inventory (wherever a held-item swap put it) and usable (ammo / mana)?</summary>
    public static bool StillUsable(Player p, Weapon w) =>
        Locate(p, w.Item) is var s and >= 0 && ReferenceEquals(p.inventory[s], w.Item) && Candidate(p, s) is { Usable: true };

    /// <summary>Score inputs for inventory slot <paramref name="slot"/>, or null if the item isn't a weapon we use.</summary>
    public static WeaponCandidate? Candidate(Player p, int slot)
    {
        var it = p.inventory[slot];
        if (!IsWeapon(it)) return null;

        Item? ammo = it.useAmmo > 0 ? p.ChooseAmmo(it) : null;
        if (ammo != null && IsExplosive(ammo.shoot)) return null;

        bool usable = (it.useAmmo <= 0 || p.HasAmmo(it)) && (it.mana <= 0 || p.CheckMana(it, -1, false, true));
        int damage = p.GetWeaponDamage(it) + (ammo?.damage ?? 0);
        bool melee = it.CountsAsClass(DamageClass.Melee) || IsWhip(it);
        float reach = melee ? MeleeReach(it) : CombatStance.RangedMaxTiles;
        return new WeaponCandidate(slot, damage, it.useTime, it.useAnimation, it.reuseDelay, p.GetWeaponCrit(it),
            it.autoReuse, it.channel, melee ? WeaponStyle.Melee : WeaponStyle.Ranged,
            it.shootSpeed + (ammo?.shootSpeed ?? 0f), reach, usable);
    }

    /// <summary>
    /// Damaging, usable-by-hand items. Excluded: ammo itself, placeables, potions, fishing poles, summon staves and
    /// sentries (they don't attack the chosen target), and anything shooting explosives (they can break the user's
    /// tiles).
    /// </summary>
    public static bool IsWeapon(Item it) => !it.IsAir && ItemRules.IsWeapon(Traits(it));

    /// <summary>Map an item to the game-independent traits Core's <see cref="ItemRules"/> decides on.</summary>
    public static ItemTraits Traits(Item it) => new(
        Damage: it.damage,
        NoUseStyle: it.useStyle == ItemUseStyleID.None,
        IsAmmo: it.ammo > 0,
        Places: it.createTile >= 0 || it.createWall >= 0,
        FishingPole: it.fishingPole > 0,
        Heals: it.healLife > 0 || it.potion,
        IsSentry: it.sentry,
        IsSummonNonWhip: it.CountsAsClass(DamageClass.Summon) && !IsWhip(it),
        ShootsExplosive: IsExplosive(it.shoot),
        Pick: it.pick,
        Axe: it.axe,
        Hammer: it.hammer);

    private static bool IsWhip(Item it) => it.shoot > 0 && it.shoot < ProjectileID.Sets.IsAWhip.Length && ProjectileID.Sets.IsAWhip[it.shoot];

    public static bool IsExplosive(int projType)
    {
        if (projType <= 0) return false;
        if (projType < ProjectileID.Sets.Explosive.Length && ProjectileID.Sets.Explosive[projType]) return true;
        return ContentSamples.ProjectilesByType.TryGetValue(projType, out var proj) && proj.aiStyle == ProjAIStyleID.Explosive;
    }

    private static float MeleeReach(Item it)
    {
        bool projectileMelee = it.noMelee && it.shoot > 0;
        if (projectileMelee && ContentSamples.ProjectilesByType.TryGetValue(it.shoot, out var proj) && proj.aiStyle == ProjAIStyleID.ShortSword)
            return 2.5f;
        return WeaponScoring.MeleeReachTiles(Math.Max(it.width, it.height), it.scale, projectileMelee);
    }

    // ---- controls -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Aim at the target (leading it for projectiles) and press use when <paramref name="fire"/>. Weapons without
    /// autoReuse need the button released for a tick between uses: <paramref name="lastUse"/> carries last tick's
    /// press. Only sets CursorTile and UseItem on <paramref name="intent"/>; movement keys are left as they are.
    /// </summary>
    public static ControlIntent Aim(ControlIntent intent, Player p, Weapon w, NPC target, bool fire, ref bool lastUse)
    {
        var c = target.Center;
        if (w.Style == WeaponStyle.Ranged)
        {
            var (ax, ay) = AimLead.AimPoint(p.Center.X, p.Center.Y, c.X, c.Y, target.velocity.X, target.velocity.Y, w.Stats.ShootSpeed);
            c = new Vector2(ax, ay);
        }
        intent.CursorTile = new TilePoint((int)Math.Floor(c.X / 16f), (int)Math.Floor(c.Y / 16f));

        bool use = fire;
        if (use && !w.Stats.AutoReuse && !w.Stats.Channel && p.itemAnimation == 0 && lastUse) use = false; // release
        intent.UseItem = use;
        lastUse = use;
        return intent;
    }

    /// <summary>
    /// Walk one step toward (<paramref name="dir"/> = +1 right / -1 left) if the next columns are safe
    /// (<see cref="StepSafety"/>); jump when blocked by a wall or when <paramref name="wantUp"/>. Jump is pulsed via
    /// <paramref name="lastJump"/> (vanilla needs a release between jumps).
    /// </summary>
    public static ControlIntent Step(ControlIntent intent, Player p, ITileGrid grid, int dir, bool wantUp, ref bool lastJump)
    {
        var (sx, sy) = StateReader.StandingTile(p);
        if (dir == 0 || !StepSafety.IsSafe(grid, new TilePoint(sx, sy), dir))
        {
            lastJump = false;
            return intent;
        }
        intent.Left = dir < 0;
        intent.Right = dir > 0;
        bool onGround = p.velocity.Y == 0f;
        bool blocked = Math.Abs(p.velocity.X) < 0.1f;
        bool jump = !lastJump && onGround && (blocked || wantUp);
        intent.Jump = jump || (lastJump && p.velocity.Y < 0f); // hold while rising for full height
        lastJump = intent.Jump;
        return intent;
    }

    // ---- kills ----------------------------------------------------------------------------------------------------

    private static readonly (int Type, uint Tick)[] Killed = new (int, uint)[Main.maxNPCs];

    internal static void RecordKill(NPC npc)
    {
        if (npc.whoAmI >= 0 && npc.whoAmI < Main.maxNPCs) Killed[npc.whoAmI] = (npc.type, Main.GameUpdateCount);
    }

    /// <summary>Did NPC slot <paramref name="id"/> (of <paramref name="type"/>) die at or after tick <paramref name="since"/>?</summary>
    public static bool WasKilled(int id, int type, uint since) =>
        id >= 0 && id < Main.maxNPCs && Killed[id].Type == type && Killed[id].Tick >= since && Killed[id].Tick != 0;
}

/// <summary>Records which NPC last hurt the local player by contact (auto-defend treats it as a threat a little
/// beyond reach, <see cref="ThreatRules"/>).</summary>
public sealed class CombatContactTracker : ModPlayer
{
    public override void OnHitByNPC(NPC npc, Player.HurtInfo hurtInfo)
    {
        if (Player.whoAmI == Main.myPlayer) CombatKit.RecordContact(npc);
    }
}

/// <summary>Records NPC deaths so an attack can tell "killed" from "despawned".</summary>
public sealed class CombatKillTracker : GlobalNPC
{
    public override void OnKill(NPC npc) => CombatKit.RecordKill(npc);
}
