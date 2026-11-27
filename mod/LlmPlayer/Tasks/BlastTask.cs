using LlmPlayer.Bridge;
using LlmPlayer.Control;
using LlmPlayer.Core.Bridge;
using LlmPlayer.Core.Combat;
using LlmPlayer.Core.Gather;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Interact;
using LlmPlayer.Core.Nav;
using LlmPlayer.Core.Observe;
using LlmPlayer.Core.Tasks;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;
using Terraria.ID;

namespace LlmPlayer.Tasks;

/// <summary>What blast checked and planned before starting (also the command's reply).</summary>
public sealed record BlastSetup(TilePoint Target, ushort TargetType, string TargetName, Item Item, ExplosiveSpec Spec,
    BlastPlan Plan, BlastReport Report, bool AllowOrb, bool AllowBossSummon);

/// <summary>What blast reports in the task's result.</summary>
public sealed record BlastResult(string Item, int X, int Y, string Target, bool TargetDestroyed, int Blasts, int Destroyed,
    IReadOnlyDictionary<string, int> DestroyedByName, List<ItemDelta> PickedUp);

/// <summary>
/// Blow up a tile with a thrown explosive, the only path that may use explosives (attack, auto-defend and use_item
/// refuse them). Real controls and vanilla behaviour only:
/// <list type="number">
/// <item>Walk (<see cref="MoveToTask"/>, exact) to the stand <see cref="BlastPlanner"/> chose: out of the blast's damage
/// square + margin, with a predicted throw (through known tiles only) that explodes at the target.</item>
/// <item>Settle, then re-aim from the real hitbox centre (<see cref="BlastPlanner.BestThrow"/>) and re-run the safety
/// check (<see cref="BlastSafety"/>, real tiles, orb count) against the new prediction; refused → fail.</item>
/// <item>Hold the explosive (<see cref="HeldItemLease"/>), aim the cursor at the planned tile and press "use" until the
/// game throws it. From here until the explosion the task is <see cref="IUninterruptibleTask.Uninterruptible"/>:
/// auto-defend can't take the controls (and walk nowhere).</item>
/// <item>Fuse: track the thrown projectile (own projectile, fair); if it rolls so that its damage square + margin
/// reaches the hitbox, walk (and jump) away from it; otherwise stand still.</item>
/// <item>After the explosion: is the target gone? If not, plan and throw once more. Then pick up what the blast
/// dropped (radius 12, 6 s). Result: <see cref="BlastResult"/>.</item>
/// </list>
/// </summary>
public sealed class BlastTask : IGameTask<GameCtx>, IUninterruptibleTask
{
    public const int MaxBlasts = 2, MaxReplans = 3, ThrowTimeoutTicks = 60, SettleMaxTicks = 90, CollectRadius = 12, CollectTicks = 6 * 60;

    private enum Phase { Walk, Settle, Throw, Fuse, Aftermath, Collect }

    private readonly BlastSetup _s;
    private readonly HeldItemLease _lease = new();
    private readonly bool[] _itemActiveAtStart = new bool[Main.maxItems];
    private readonly bool[] _projActive = new bool[Main.maxProjectiles];
    private readonly Dictionary<string, int> _destroyedByName = new(StringComparer.Ordinal);
    private List<SlotItem>? _invBefore;
    private Phase _phase = Phase.Walk;
    private BlastPlan _plan;
    private MoveToTask? _move;
    private PickUpTask? _collect;
    private HashSet<TilePoint> _expected = new();
    private Dictionary<TilePoint, ushort> _before = new();
    private int _ticks, _blasts, _replans, _projIndex = -1, _projIdentity, _stillTicks, _retreatStuck, _alignTicks;
    private const float AlignPx = 2f;
    private const int MaxAlignTicks = 90;
    private bool _started, _targetGone;

    public string Name => "blast";
    public string? Reason { get; private set; }
    public string? Progress { get; private set; }
    public object? Result { get; private set; }

    /// <summary>From the press until the explosion: auto-defend must not take the controls.</summary>
    public bool Uninterruptible => _phase is Phase.Throw or Phase.Fuse;

    public BlastTask(BlastSetup setup)
    {
        _s = setup;
        _plan = setup.Plan;
        Progress = $"walking to {_plan.Stand} to throw a {_s.Spec.Name} at {_s.Target}";
    }

    public GameTaskStatus Tick(GameCtx ctx)
    {
        var p = ctx.Player;
        if (!_started)
        {
            _started = true;
            for (int i = 0; i < Main.maxItems; i++) _itemActiveAtStart[i] = Main.item[i].active;
            _invBefore = InventorySnapshot(p);
        }
        _ticks++;
        return _phase switch
        {
            Phase.Walk => Walk(ctx),
            Phase.Settle => Settle(ctx),
            Phase.Throw => Throw(ctx),
            Phase.Fuse => Fuse(ctx),
            Phase.Aftermath => Aftermath(ctx),
            _ => Collect(ctx),
        };
    }

    private GameTaskStatus Walk(GameCtx ctx)
    {
        var p = ctx.Player;
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k, ctx.Grid);
        if (here == _plan.Stand && k.OnGround)
        {
            StopMove(ctx);
            return Enter(Phase.Settle, ctx);
        }
        _move ??= new MoveToTask(_plan.Stand, allowDig: true, allowBuild: true, tolerance: 0, name: Name);
        var st = _move.Tick(ctx);
        Progress = $"walking to {_plan.Stand} ({_move.Progress}) to throw a {_s.Spec.Name} at {_s.Target}";
        if (st == GameTaskStatus.Running) return st;
        string? why = _move.Reason;
        _move = null;
        if (st == GameTaskStatus.Failed) return Finish(ctx, GameTaskStatus.Failed, $"can't reach the throwing spot {_plan.Stand}: {why}");
        return Enter(Phase.Settle, ctx);
    }

    private GameTaskStatus Settle(GameCtx ctx)
    {
        var p = ctx.Player;
        ControlState.Pending = default;
        // Line the hitbox up with the planned stand: StandingTile rounds, so the player can be up to half a tile off
        // the spot the throw was planned from. Tap toward it while slow (vanilla friction stops the rest).
        float err = (_plan.Stand.X + PlayerPhysics.CenterOffset) * 16f - p.position.X;
        if (MathF.Abs(err) > AlignPx && _alignTicks < MaxAlignTicks && p.velocity.Y == 0f)
        {
            _alignTicks++;
            _stillTicks = 0;
            if (MathF.Abs(p.velocity.X) < 0.4f)
                ControlState.Pending = new ControlIntent { Left = err < 0, Right = err > 0 };
            Progress = $"lining up to throw at {_s.Target}";
            return GameTaskStatus.Running;
        }
        _stillTicks = p.velocity.X == 0f && p.velocity.Y == 0f ? _stillTicks + 1 : 0;
        Progress = $"steadying to throw at {_s.Target}";
        if (_stillTicks < 10 && _ticks < SettleMaxTicks) return GameTaskStatus.Running;

        // Re-aim from where the player really is, then re-check safety for that prediction.
        var grid = Snapshot(ctx.Grid, _s.Target);
        var hb = RealHitbox(p);
        var th = BlastPlanner.BestThrow(grid, p.Center.X / 16f, p.Center.Y / 16f, _s.Target, _s.Spec, out _, out _, hb);
        if (th is not { } t || !BlastPlanner.HitboxSafe(hb, _s.Target.X + 0.5f, _s.Target.Y + 0.5f, _s.Spec))
            return Replan(ctx, "no safe throw from here");
        var report = BlastSafety.Check(BlastWorld.Reader(p), _s.Target, t.Result.X, t.Result.Y, _s.Spec,
            WorldGen.shadowOrbCount, _s.AllowOrb, _s.AllowBossSummon);
        if (report.Refusal != null) return Finish(ctx, GameTaskStatus.Failed, $"refused before throwing: {report.Refusal}");
        _plan = _plan with { Cursor = t.Cursor, DetX = t.Result.X, DetY = t.Result.Y, Miss = t.Miss, FlightTicks = t.Result.Ticks };
        _expected = report.Destroys.ToHashSet();
        _expected.Add(_s.Target);
        _before = _expected.Where(e => Main.tile[e.X, e.Y].HasTile).ToDictionary(e => e, e => Main.tile[e.X, e.Y].TileType);
        for (int i = 0; i < Main.maxProjectiles; i++) _projActive[i] = Main.projectile[i].active;
        return Enter(Phase.Throw, ctx);
    }

    private GameTaskStatus Throw(GameCtx ctx)
    {
        var p = ctx.Player;
        int slot = Slot(p);
        if (slot < 0) return Finish(ctx, GameTaskStatus.Failed, $"no {_s.Item.Name} left in the inventory");
        if (FindThrown(p) is { } proj)
        {
            _projIndex = proj.whoAmI;
            _projIdentity = proj.identity;
            _blasts++;
            ControlState.Pending = default(ControlIntent) with { CursorTile = _plan.Cursor };
            return Enter(Phase.Fuse, ctx);
        }
        Progress = $"throwing a {_s.Spec.Name} at {_s.Target}";
        if (!_lease.Hold(p, slot))
        {
            ControlState.Pending = default(ControlIntent) with { CursorTile = _plan.Cursor };
            if (_ticks > HoldTimeout) return Finish(ctx, GameTaskStatus.Failed, "couldn't switch to the explosive (busy using another item)");
            return GameTaskStatus.Running;
        }
        if (_ticks > HoldTimeout + ThrowTimeoutTicks)
            return Finish(ctx, GameTaskStatus.Failed, $"the game didn't throw the {_s.Spec.Name} (on cooldown or not usable here)");
        ControlState.Pending = default(ControlIntent) with { UseItem = true, CursorTile = _plan.Cursor };
        return GameTaskStatus.Running;
    }

    private const int HoldTimeout = 300;

    private GameTaskStatus Fuse(GameCtx ctx)
    {
        var p = ctx.Player;
        var proj = _projIndex >= 0 ? Main.projectile[_projIndex] : null;
        if (proj == null || !proj.active || proj.identity != _projIdentity || proj.type != _s.Item.shoot)
            return Enter(Phase.Aftermath, ctx);
        if (_ticks > _s.Spec.FuseTicks + 120) return Finish(ctx, GameTaskStatus.Failed, $"the {_s.Spec.Name} didn't explode");

        float cx = proj.Center.X / 16f, cy = proj.Center.Y / 16f;
        var hb = RealHitbox(p);
        int left = Math.Max(0, proj.timeLeft - 3);
        if (BlastPlanner.HitboxSafe(hb, cx, cy, _s.Spec))
        {
            Progress = $"{_s.Spec.Name} lit at ({(int)cx},{(int)cy}), {left / 60f:0.0} s to go; staying clear";
            ControlState.Pending = default;
            _retreatStuck = 0;
            return GameTaskStatus.Running;
        }
        // It rolled toward us: walk away from it, jumping when blocked.
        int dir = BlastPlanner.RetreatDirection(hb, cx);
        _retreatStuck = Math.Abs(p.velocity.X) < 0.5f ? _retreatStuck + 1 : 0;
        Progress = $"{_s.Spec.Name} rolled close ({(int)cx},{(int)cy}), {left / 60f:0.0} s to go; backing off";
        ControlState.Pending = new ControlIntent { Left = dir < 0, Right = dir > 0, Jump = _retreatStuck > 4 };
        return GameTaskStatus.Running;
    }

    private GameTaskStatus Aftermath(GameCtx ctx)
    {
        ControlState.Pending = default;
        if (_ticks < 3) return GameTaskStatus.Running; // let the tile updates land
        _lease.Restore(ctx.Player);
        foreach (var (at, type) in _before)
        {
            var t = Main.tile[at.X, at.Y];
            if (t.HasTile && t.TileType == type) continue;
            string n = TileClass.Name(type);
            _destroyedByName[n] = _destroyedByName.TryGetValue(n, out var c) ? c + 1 : 1;
        }
        _before.Clear();
        _targetGone = !(Main.tile[_s.Target.X, _s.Target.Y] is { HasTile: true } tt && tt.TileType == _s.TargetType);
        if (!_targetGone)
        {
            if (_blasts >= MaxBlasts)
                return Done(ctx, GameTaskStatus.Failed, $"{_s.TargetName} at {_s.Target} survived {_blasts} blasts");
            return Replan(ctx, "target survived; trying once more");
        }
        _collect = new PickUpTask(CollectRadius, CollectTicks, i => !_itemActiveAtStart[i], Name);
        return Enter(Phase.Collect, ctx);
    }

    private GameTaskStatus Collect(GameCtx ctx)
    {
        var c = _collect!.Tick(ctx);
        Progress = $"blasted {_s.TargetName}; collecting drops: {_collect.Progress}";
        if (c == GameTaskStatus.Running) return GameTaskStatus.Running;
        _collect = null;
        return Done(ctx, GameTaskStatus.Succeeded, $"blasted {_s.TargetName} at {_s.Target} with {_blasts} {_s.Spec.Name}{(_blasts > 1 ? "s" : "")}");
    }

    /// <summary>Choose a new stand from here (after a failed aim, or for the second blast).</summary>
    private GameTaskStatus Replan(GameCtx ctx, string why)
    {
        var p = ctx.Player;
        if (++_replans > MaxReplans) return Done(ctx, GameTaskStatus.Failed, $"{why}; gave up after {MaxReplans} replans");
        var k = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var plan = BlastPlanner.Plan(Snapshot(ctx.Grid, _s.Target), NavMath.StandingTile(k, ctx.Grid), _s.Target, _s.Spec, out var reason);
        if (plan == null) return Done(ctx, GameTaskStatus.Failed, $"{why}: {reason}");
        _plan = plan;
        return Enter(Phase.Walk, ctx);
    }

    private GameTaskStatus Enter(Phase phase, GameCtx ctx)
    {
        _phase = phase;
        _ticks = _stillTicks = _retreatStuck = 0;
        if (phase is Phase.Walk or Phase.Settle or Phase.Aftermath) ControlState.Pending = default;
        return GameTaskStatus.Running;
    }

    private GameTaskStatus Done(GameCtx ctx, GameTaskStatus status, string reason)
    {
        var gained = InteractMath.Diff(_invBefore ?? new(), InventorySnapshot(ctx.Player)).Where(d => d.Count > 0).ToList();
        Result = new BlastResult(_s.Spec.Name, _s.Target.X, _s.Target.Y, _s.TargetName, _targetGone, _blasts,
            _destroyedByName.Values.Sum(), new SortedDictionary<string, int>(_destroyedByName), gained);
        return Finish(ctx, status, reason);
    }

    public void OnCancel(GameCtx ctx)
    {
        _collect?.OnCancel(ctx);
        _collect = null;
        Cleanup(ctx);
    }

    private GameTaskStatus Finish(GameCtx ctx, GameTaskStatus status, string? reason)
    {
        Reason = reason;
        Cleanup(ctx);
        return status;
    }

    private void Cleanup(GameCtx ctx)
    {
        StopMove(ctx);
        ControlState.Pending = default;
        _lease.Restore(ctx.Player);
    }

    private void StopMove(GameCtx ctx)
    {
        _move?.OnCancel(ctx);
        _move = null;
    }

    private int Slot(Player p)
    {
        for (int i = 0; i < 58; i++)
            if (ReferenceEquals(p.inventory[i], _s.Item) && !p.inventory[i].IsAir) return i;
        return -1;
    }

    /// <summary>A projectile of the explosive's type, ours, that wasn't there before the press.</summary>
    private Projectile? FindThrown(Player p)
    {
        for (int i = 0; i < Main.maxProjectiles; i++)
        {
            var pr = Main.projectile[i];
            if (pr.active && !_projActive[i] && pr.owner == p.whoAmI && pr.type == _s.Item.shoot) return pr;
        }
        return null;
    }

    private static (float L, float T, float R, float B) RealHitbox(Player p) =>
        (p.position.X / 16f, p.position.Y / 16f, (p.position.X + p.width) / 16f, (p.position.Y + p.height) / 16f);

    /// <summary>Fair-vision tiles around the target, copied once (the planner simulates thousands of steps).</summary>
    public static ArrayTileGrid Snapshot(ITileGrid live, TilePoint target)
    {
        int r = BlastPlanner.SearchRadius + 16;
        var tiles = new TileInfo[2 * r + 1, 2 * r + 1];
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
                tiles[dx + r, dy + r] = live.Get(target.X + dx, target.Y + dy);
        return new ArrayTileGrid(target.X - r, target.Y - r, tiles);
    }

    private static List<SlotItem> InventorySnapshot(Player p)
    {
        var list = new List<SlotItem>();
        for (int i = 0; i < 58; i++)
            if (p.inventory[i] is { IsAir: false } it) list.Add(new SlotItem(i, it.Name, it.stack));
        return list;
    }

    /// <summary>
    /// Validate and plan a blast. Throws <see cref="CommandError"/>: bad_params (outside the world, nothing there,
    /// explosion-proof, not a supported explosive, no safe stand/throw), not_visible, protected_tile (safety refusal),
    /// missing_item (no explosive). Without <paramref name="itemName"/> it tries the explosives in the inventory in
    /// <see cref="Explosives.Preference"/> order and takes the first with a safe plan.
    /// </summary>
    public static BlastSetup Resolve(Player p, int x, int y, string? itemName, bool allowOrb, bool allowBossSummon)
    {
        if (!WorldGen.InWorld(x, y, 10)) throw new CommandError(ErrorCodes.BadParams, $"({x},{y}) is outside the world");
        if (!Visibility.IsTileVisible(x, y)) throw new CommandError(ErrorCodes.NotVisible, $"({x},{y}) is not visible (unexplored or dark)");
        var target = new TilePoint(x, y);
        var bt = BlastWorld.Tile(x, y, Visibility.Screen, p);
        var tile = Main.tile[x, y];
        string tname = bt.Name ?? "tile";
        switch (bt.Kind)
        {
            case BlastTileKind.Empty: throw new CommandError(ErrorCodes.BadParams, $"nothing to blast at ({x},{y})");
            case BlastTileKind.Altar: throw new CommandError(ErrorCodes.BadParams, $"{tname} at ({x},{y}) is explosion-proof (and a crafting station)");
            case BlastTileKind.Immune: throw new CommandError(ErrorCodes.BadParams, $"{tname} at ({x},{y}) is explosion-proof");
            case BlastTileKind.Protected: throw new CommandError(ErrorCodes.ProtectedTile, $"{tname} at ({x},{y}) is protected (furniture, chest, door, station)");
            case BlastTileKind.Collectible: throw new CommandError(ErrorCodes.BadParams, $"{tname} at ({x},{y}) is loot: use collect");
            case BlastTileKind.Orb when !allowOrb:
                throw new CommandError(ErrorCodes.BadParams, $"{tname} at ({x},{y}): pass allow_orb to break it on purpose");
        }
        if (bt.ProtectedArea) throw new CommandError(ErrorCodes.ProtectedTile, $"({x},{y}) is in an NPC house or the spawn area");

        var candidates = new List<(Item Item, ExplosiveKind Kind)>();
        if (itemName != null)
        {
            var it = p.inventory[ItemLookup.FindSlot(p, itemName)];
            var k = BlastWorld.KindOf(it)
                ?? throw new CommandError(ErrorCodes.BadParams, $"{it.Name} isn't an explosive blast can throw (Bomb, Sticky Bomb, Dynamite, Sticky Dynamite)");
            candidates.Add((it, k));
        }
        else
        {
            foreach (var kind in Explosives.Preference)
                for (int i = 0; i < 58; i++)
                    if (p.inventory[i] is { IsAir: false } it && BlastWorld.KindOf(it) == kind)
                    {
                        candidates.Add((it, kind));
                        break;
                    }
            if (candidates.Count == 0)
                throw new CommandError(ErrorCodes.MissingItem, "no explosive in inventory (Sticky Bomb, Bomb, Sticky Dynamite or Dynamite; the Demolitionist sells Bombs and Dynamite)");
        }

        var grid = Snapshot(new GameTileGrid(p), target);
        var k0 = NavMath.FromPixels(p.position.X, p.position.Y, p.velocity.X, p.velocity.Y, p.height);
        var here = NavMath.StandingTile(k0, grid);
        var reader = BlastWorld.Reader(p);
        CommandError? first = null;
        foreach (var (item, kind) in candidates)
        {
            var spec = BlastWorld.SpecOf(item, kind);
            var plan = BlastPlanner.Plan(grid, here, target, spec, out var why);
            if (plan == null)
            {
                first ??= new CommandError(ErrorCodes.BadParams, $"{spec.Name}: {why}");
                continue;
            }
            var report = BlastSafety.Check(reader, target, plan.DetX, plan.DetY, spec, WorldGen.shadowOrbCount, allowOrb, allowBossSummon);
            if (report.Refusal != null)
            {
                first ??= new CommandError(ErrorCodes.ProtectedTile, $"{spec.Name}: {report.Refusal}");
                continue;
            }
            return new BlastSetup(target, tile.TileType, tname, item, spec, plan, report, allowOrb, allowBossSummon);
        }
        throw first!;
    }
}

/// <summary>The real-world tile facts <see cref="BlastSafety"/> needs (vanilla explosion-proof rules from the wiki's
/// Explosion-proof items page). Main thread only.</summary>
public static class BlastWorld
{
    public static ExplosiveKind? KindOf(Item it) => it.IsAir ? null : it.type switch
    {
        ItemID.Bomb => ExplosiveKind.Bomb,
        ItemID.StickyBomb => ExplosiveKind.StickyBomb,
        ItemID.Dynamite => ExplosiveKind.Dynamite,
        ItemID.StickyDynamite => ExplosiveKind.StickyDynamite,
        _ => null,
    };

    /// <summary>Core's spec with the live item's shootSpeed and its projectile's timeLeft and width.</summary>
    public static ExplosiveSpec SpecOf(Item it, ExplosiveKind kind)
    {
        ContentSamples.ProjectilesByType.TryGetValue(it.shoot, out var proj);
        return Explosives.Spec(kind, it.shootSpeed, proj?.timeLeft, proj?.width);
    }

    public static Func<int, int, BlastTile> Reader(Player p)
    {
        var screen = Visibility.Screen;
        return (x, y) => Tile(x, y, screen, p);
    }

    public static BlastTile Tile(int x, int y, ScreenRect screen, Player p)
    {
        if (!WorldGen.InWorld(x, y)) return new BlastTile(BlastTileKind.Immune, "world edge");
        bool vis = Visibility.IsTileVisible(x, y, screen, p);
        bool area = Protection.IsProtectedArea(x, y);
        var t = Main.tile[x, y];
        bool wall = t.WallType != 0;
        if (!t.HasTile) return new BlastTile(BlastTileKind.Empty, null, vis, area, Wall: wall);
        int type = t.TileType;
        string name = TileClass.Name(type);
        var valuable = TileClass.Valuable(t);
        if (valuable is ValuableKind.Orb or ValuableKind.Altar or ValuableKind.EnchantedSword or ValuableKind.LifeCrystal
            or ValuableKind.LifeFruit or ValuableKind.Pot)
        {
            var (ox, oy) = TileClass.ObjectOrigin(x, y, t);
            var origin = Main.tile[ox, oy];
            if (!origin.HasTile || origin.TileType != t.TileType) (ox, oy) = (x, y);
            string vname = Valuables.Name(valuable.Value, Main.tile[ox, oy].TileFrameX);
            var kind = valuable switch
            {
                ValuableKind.Orb => BlastTileKind.Orb,
                ValuableKind.Altar => BlastTileKind.Altar,
                _ => BlastTileKind.Collectible,
            };
            return new BlastTile(kind, vname, vis, area, new TilePoint(ox, oy), wall);
        }
        if (valuable == ValuableKind.Chest || TileClass.IsProtectedTile(t))
            return new BlastTile(BlastTileKind.Protected, name, vis, area, Wall: wall);
        if (SupportRules.SupportsProtectedObject(IsFurniture, x, y))
            return new BlastTile(BlastTileKind.Protected, $"{name} holding up furniture", vis, area, Wall: wall);
        if (IsExplosionProof(type) || (!Main.tileAxe[type] && !WorldGen.CanKillTile(x, y)))
            return new BlastTile(BlastTileKind.Immune, name, vis, area, Wall: wall);
        return new BlastTile(BlastTileKind.Block, name, vis, area, Wall: wall);
    }

    /// <summary>Protected objects other than orbs (allow_orb decides) and altars (explosion-proof).</summary>
    private static bool IsFurniture(int x, int y)
    {
        if (!WorldGen.InWorld(x, y) || Main.tile[x, y] is not { HasTile: true } t) return false;
        if (TileClass.Valuable(t) is ValuableKind.Orb or ValuableKind.Altar) return false;
        return TileClass.IsProtectedTile(t);
    }

    /// <summary>Wiki "Explosion-proof items": dungeon bricks, Lihzahrd brick, Chlorophyte, Hardmode ores (to regular
    /// bombs), and before Hardmode Meteorite / Hellstone / Hellforge.</summary>
    private static bool IsExplosionProof(int type) =>
        Main.tileDungeon[type]
        || type is TileID.LihzahrdBrick or TileID.Chlorophyte or TileID.ChlorophyteBrick or TileID.LihzahrdAltar
            or TileID.Cobalt or TileID.Palladium or TileID.Mythril or TileID.Orichalcum or TileID.Adamantite or TileID.Titanium
        || (!Main.hardMode && type is TileID.Meteorite or TileID.Hellstone or TileID.Hellforge);
}
