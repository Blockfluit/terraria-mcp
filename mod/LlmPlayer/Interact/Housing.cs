using System.Reflection;
using LlmPlayer.Core.Build;
using LlmPlayer.Core.Npc;
using LlmPlayer.Observe;
using LlmPlayer.Safety;
using Terraria;
using Terraria.Enums;
using Terraria.ID;
using Terraria.Localization;

namespace LlmPlayer.Interact;

/// <summary>check_housing's room part: vanilla's housing-query verdict for one tile.</summary>
public sealed record HousingVerdict(int X, int Y, bool Valid, string Message, List<string>? Missing, bool Occupied, string? Occupant,
    HousingRoom? Room);

/// <summary>The room vanilla's flood fill found: bounds of its open tiles and how many tiles it counted (frame included).</summary>
public sealed record HousingRoom(int X1, int Y1, int X2, int Y2, int Tiles);

public sealed record NpcResident(string Name, string Type, bool Homeless, int? HomeX, int? HomeY, bool Pet);

/// <summary>check_housing's NPC part.</summary>
public sealed record NpcHousingStatus(List<NpcResident> Residents, List<string> Homeless, List<NpcProspect> NotYetHere, string Note);

/// <summary>
/// Vanilla housing, read exactly as the game does it. <see cref="Query"/> is the housing-query icon
/// (<c>Main.DrawInterface_38_MouseCarriedObject</c> → <c>WorldGen.MoveTownNPC(x, y, -1)</c>, decompiled 1.4.4) minus its chat
/// messages: <c>WorldGen.StartRoomCheck</c> (flood fill: enclosed, 60..750 tiles, background walls without holes),
/// <c>WorldGen.RoomNeeds</c> (light, door, table, chair), the stinkbug check, then <c>WorldGen.ScoreRoom</c> (occupied,
/// evil, or no spot an NPC can stand). Messages are vanilla's localized texts. Main thread only.
/// </summary>
public static class Housing
{
    public static HousingVerdict Query(int x, int y)
    {
        WorldGen.roomCheckFailureReason = TownNPCRoomCheckFailureReason.None;
        if (!WorldGen.StartRoomCheck(x, y))
        {
            string msg = WorldGen.roomCheckFailureReason switch
            {
                TownNPCRoomCheckFailureReason.None => Lang.inter[40].Value,
                var r => Language.GetTextValue("TownNPCHousingFailureReasons." + r),
            };
            return new HousingVerdict(x, y, false, msg + Explain(WorldGen.roomCheckFailureReason), null, false, null, null);
        }
        var room = new HousingRoom(WorldGen.roomX1, WorldGen.roomY1, WorldGen.roomX2, WorldGen.roomY2, WorldGen.numRoomTiles);
        if (!WorldGen.RoomNeeds(WorldGen.prioritizedTownNPCType))
        {
            var missing = new List<string>();
            // RoomNeeds' own flags are private; it derives them from the public houseTile array exactly like this.
            if (!InRoom(TileID.Sets.RoomNeeds.CountsAsTorch)) missing.Add(Language.GetTextValue("Game.HouseLightSource"));
            if (!InRoom(TileID.Sets.RoomNeeds.CountsAsDoor)) missing.Add(Language.GetTextValue("Game.HouseDoor"));
            if (!InRoom(TileID.Sets.RoomNeeds.CountsAsTable)) missing.Add(Language.GetTextValue("Game.HouseTable"));
            if (!InRoom(TileID.Sets.RoomNeeds.CountsAsChair)) missing.Add(Language.GetTextValue("Game.HouseChair"));
            string text = Language.GetTextValue("Game.HouseMissing_" + missing.Count, missing.Cast<object>().ToArray());
            return new HousingVerdict(x, y, false, text, missing, false, null, room);
        }
        int type = WorldGen.prioritizedTownNPCType;
        bool pet = NPCID.Sets.IsTownPet[type];
        if (Flag("roomHasStinkbug") && !pet)
            return new HousingVerdict(x, y, false, Language.GetTextValue("TownNPCHousingFailureReasons.RoomHasAStinkbug"), null, false, null, room);
        if (Flag("roomHasEchoStinkbug") && !pet)
            return new HousingVerdict(x, y, false, Language.GetTextValue("TownNPCHousingFailureReasons.RoomHasAnEchoStinkbug"), null, false, null, room);
        WorldGen.ScoreRoom(-1, type);
        if (WorldGen.hiScore <= 0)
        {
            if (Flag("roomOccupied"))
                return new HousingVerdict(x, y, false, Lang.inter[41].Value, null, true, OccupantOf(room), room);
            if (Flag("roomEvil"))
                return new HousingVerdict(x, y, false, Lang.inter[42].Value, null, false, null, room);
            return new HousingVerdict(x, y, false, Lang.inter[40].Value +
                " (no floor spot an NPC can stand on: needs a solid floor tile with solid tiles left and right and 3x3 open space above)",
                null, false, null, room);
        }
        return new HousingVerdict(x, y, true, Lang.inter[39].Value, null, false, null, room);
    }

    private static bool InRoom(int[] types)
    {
        foreach (int t in types)
            if (t >= 0 && t < WorldGen.houseTile.Length && WorldGen.houseTile[t]) return true;
        return false;
    }

    private static readonly Dictionary<string, FieldInfo?> Fields = new();

    /// <summary>A private static bool of WorldGen's room check (roomOccupied, roomEvil, roomHasStinkbug…), read by
    /// reflection; false if a tModLoader update renamed it.</summary>
    private static bool Flag(string name)
    {
        if (!Fields.TryGetValue(name, out var f))
            Fields[name] = f = typeof(WorldGen).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        return f?.GetValue(null) is true;
    }

    private static string Explain(TownNPCRoomCheckFailureReason r) => r switch
    {
        TownNPCRoomCheckFailureReason.RoomIsTooBig => " (or not enclosed: a gap in the frame or walls lets the room leak out)",
        TownNPCRoomCheckFailureReason.HoleInWallIsTooBig => " (background walls missing: every open tile needs a house wall or block within 2 tiles in its row and column)",
        TownNPCRoomCheckFailureReason.RoomIsTooSmall => " (at least 60 tiles, frame included)",
        TownNPCRoomCheckFailureReason.RoomCheckStartedInASolidTile => " (give a tile inside the room, not a block)",
        TownNPCRoomCheckFailureReason.None => " (not enclosed, or no background walls)",
        _ => "",
    };

    /// <summary>The town NPC whose home spot is in this room (vanilla's home tile is the floor tile it stands on).</summary>
    private static string? OccupantOf(HousingRoom r)
    {
        foreach (var npc in Main.npc)
            if (npc.active && npc.townNPC && !npc.homeless && npc.homeTileX >= r.X1 && npc.homeTileX <= r.X2
                && npc.homeTileY >= r.Y1 && npc.homeTileY <= r.Y2 + 1)
                return npc.GivenOrTypeName;
        return null;
    }

    // ---- NPCs ------------------------------------------------------------------------------------------------

    /// <summary>Town NPCs that never need a house (Old Man, Traveling Merchant, Skeleton Merchant).</summary>
    private static bool NeedsNoHouse(int type) => type is NPCID.OldMan or NPCID.TravellingMerchant or NPCID.SkeletonMerchant;

    /// <summary>Who lives in the world (vanilla's housing menu shows the same), who is homeless, and which useful
    /// NPCs could move in now (live vanilla conditions, <see cref="TownNpcFacts"/>).</summary>
    public static NpcHousingStatus Npcs()
    {
        var residents = new List<NpcResident>();
        var present = new HashSet<int>();
        int townCount = 0;
        foreach (var npc in Main.npc)
        {
            if (!npc.active || !npc.townNPC) continue;
            townCount++; // vanilla counts every active town NPC
            present.Add(npc.type);
            if (NeedsNoHouse(npc.type)) continue;
            residents.Add(new NpcResident(npc.GivenOrTypeName, npc.TypeName, npc.homeless,
                npc.homeless ? null : npc.homeTileX, npc.homeless ? null : npc.homeTileY, NPCID.Sets.IsTownPet[npc.type]));
        }
        var homeless = residents.Where(r => r.Homeless).Select(r => r.Name).ToList();
        var prospects = TownNpcFacts.Prospects(present, townCount, Met);
        return new NpcHousingStatus(residents, homeless, prospects,
            "An NPC that canMoveIn arrives during the day once a free valid house exists (check_housing x,y on it). " +
            "Homeless NPCs move into a free valid house on their own.");
    }

    /// <summary>Live vanilla checks behind each <see cref="SpawnCondition"/>.</summary>
    private static bool Met(SpawnCondition c) => c switch
    {
        SpawnCondition.Always => true,
        SpawnCondition.Coins50Silver => NPC.SpawnAllowed_Merchant(),
        SpawnCondition.LifeCrystalUsed => NPC.SpawnAllowed_Nurse(),
        SpawnCondition.Explosive => NPC.SpawnAllowed_Demolitionist(),
        SpawnCondition.BulletOrGun => NPC.SpawnAllowed_ArmsDealer(),
        SpawnCondition.DyeItem => NPC.SpawnAllowed_DyeTrader(),
        SpawnCondition.AnyBossDefeated => NPC.downedBoss1 || NPC.downedBoss2 || NPC.downedBoss3,
        SpawnCondition.SkeletronDefeated => NPC.downedBoss3,
        SpawnCondition.RescuedAngler => NPC.savedAngler,
        SpawnCondition.RescuedStylist => NPC.savedStylist,
        SpawnCondition.RescuedGoblin => NPC.savedGoblin,
        SpawnCondition.RescuedMechanic => NPC.savedMech,
        _ => false,
    };

    /// <summary>get_state's compact view: homeless NPCs and NPCs that could move in now; null when both are empty.</summary>
    public static object? StateSummary()
    {
        var s = Npcs();
        var canMoveIn = s.NotYetHere.Where(p => p.CanMoveIn).Select(p => p.Name).ToList();
        if (s.Homeless.Count == 0 && canMoveIn.Count == 0) return null;
        var d = new Dictionary<string, object>();
        if (s.Homeless.Count > 0) d["Homeless"] = s.Homeless;
        if (canMoveIn.Count > 0) d["CanMoveIn"] = canMoveIn;
        return d;
    }

    // ---- building sites ----------------------------------------------------------------------------------------

    /// <summary>A tile as build_house sees it (fair vision: unseen tiles are Unknown).</summary>
    public static SiteCell Cell(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10) || !Visibility.IsTileVisible(x, y)) return new SiteCell(SiteTile.Unknown);
        foreach (var room in Protection.HouseRooms)
            if (x >= room.Left && x < room.Right && y >= room.Top && y < room.Bottom) return new SiteCell(SiteTile.NpcHouse);
        var t = Main.tile[x, y];
        var wall = t.WallType == WallID.None ? WallState.None : Main.wallHouse[t.WallType] ? WallState.House : WallState.Other;
        if (t.HasTile && !t.IsActuated)
        {
            int type = t.TileType;
            if (TileClass.IsTree(type)) return new SiteCell(SiteTile.Tree, wall);
            if (Main.tileCut[type]) return Liquidish(t, wall);
            if (TileID.Sets.Platforms[type]) return new SiteCell(SiteTile.Platform, wall);
            if (Main.tileFrameImportant[type] || TileClass.IsProtectedType(type)) return new SiteCell(SiteTile.Object, wall);
            if (Main.tileSolid[type]) return new SiteCell(Main.tileSolidTop[type] ? SiteTile.Platform : SiteTile.Solid, wall);
            return new SiteCell(SiteTile.Object, wall);
        }
        return Liquidish(t, wall);
    }

    private static SiteCell Liquidish(Tile t, WallState wall) =>
        t.LiquidAmount > 0 && (t.LiquidType == LiquidID.Lava || t.LiquidAmount >= 128)
            ? new SiteCell(SiteTile.Liquid, wall)
            : new SiteCell(SiteTile.Air, wall);
}

/// <summary>Which inventory items build_house uses for each part, and the stock it plans with.</summary>
public static class HouseItems
{
    private static readonly int[] OtherWoods =
    {
        ItemID.BorealWood, ItemID.RichMahogany, ItemID.Ebonwood, ItemID.Shadewood, ItemID.PalmWood, ItemID.Pearlwood,
        ItemID.DynastyWood, ItemID.AshWood,
    };

    /// <summary>Frame preference rank (lower = better); -1 = never used for the frame (falling, ore, grass seeds,
    /// furniture, platforms, hazards…).</summary>
    public static int FrameRank(Item it)
    {
        if (it.IsAir || it.createTile < 0 || it.createWall > 0) return -1;
        int t = it.createTile;
        if (!Main.tileSolid[t] || Main.tileSolidTop[t] || Main.tileFrameImportant[t]) return -1;
        if (TileID.Sets.Falling[t] || TileID.Sets.Ore[t] || TileID.Sets.TouchDamageImmediate[t] > 0) return -1;
        if (TileID.Sets.Conversion.Grass[t] || it.Name.Contains("Seeds", StringComparison.OrdinalIgnoreCase)) return -1;
        if (it.type == ItemID.Wood) return 0;
        if (Array.IndexOf(OtherWoods, it.type) >= 0) return 1;
        if (it.type == ItemID.StoneBlock) return 2;
        if (it.Name.EndsWith("Brick", StringComparison.OrdinalIgnoreCase)) return 3;
        if (it.type is ItemID.DirtBlock or ItemID.MudBlock or ItemID.ClayBlock or ItemID.SnowBlock or ItemID.AshBlock) return 4;
        return 5;
    }

    public static bool IsHouseWall(Item it) => !it.IsAir && it.createWall > 0 && Main.wallHouse[it.createWall];
    public static bool IsDoor(Item it) => !it.IsAir && it.createTile == TileID.ClosedDoor;
    public static bool IsChair(Item it) => !it.IsAir && it.createTile == TileID.Chairs;
    public static bool IsTorch(Item it) => !it.IsAir && it.createTile == TileID.Torches;
    /// <summary>3×2 tables (preferred) and 2×1 work benches: the flat surfaces the layout has room for.</summary>
    public static bool IsTable(Item it) => !it.IsAir && it.createTile is TileID.Tables or TileID.Tables2 or TileID.WorkBenches;

    public static int Count(Player p, Func<Item, bool> pred)
    {
        int n = 0;
        for (int i = 0; i < 58; i++)
            if (pred(p.inventory[i])) n += p.inventory[i].stack;
        return n;
    }

    /// <summary>Slot of the item for a part (held item first if it qualifies); -1 if none.</summary>
    public static int SlotFor(Player p, HousePart part)
    {
        switch (part)
        {
            case HousePart.Frame:
            {
                int best = -1, rank = int.MaxValue;
                for (int i = 0; i < 58; i++)
                {
                    int r = FrameRank(p.inventory[i]);
                    if (r >= 0 && (r < rank || (r == rank && i == p.selectedItem))) { best = i; rank = r; }
                }
                return best;
            }
            case HousePart.Table:
                for (int i = 0; i < 58; i++)
                    if (IsTable(p.inventory[i]) && p.inventory[i].createTile != TileID.WorkBenches) return i;
                return First(p, IsTable);
            default:
                return First(p, part switch
                {
                    HousePart.BackWall => IsHouseWall,
                    HousePart.Door => IsDoor,
                    HousePart.Chair => IsChair,
                    _ => IsTorch,
                });
        }
    }

    private static int First(Player p, Func<Item, bool> pred)
    {
        if (pred(p.inventory[p.selectedItem])) return p.selectedItem;
        for (int i = 0; i < 58; i++)
            if (pred(p.inventory[i])) return i;
        return -1;
    }

    /// <summary>What the player has for <see cref="HouseMaterials.Plan"/>. Work Bench in range: vanilla AdjTiles.</summary>
    public static HouseStock Stock(Player p)
    {
        var blocks = new Dictionary<int, (string Name, int Count, int Rank)>();
        for (int i = 0; i < 58; i++)
        {
            var it = p.inventory[i];
            int r = FrameRank(it);
            if (r < 0) continue;
            blocks[it.type] = blocks.TryGetValue(it.type, out var b) ? (b.Name, b.Count + it.stack, r) : (it.Name, it.stack, r);
        }
        var list = blocks.OrderBy(kv => kv.Value.Rank).ThenByDescending(kv => kv.Value.Count)
            .Select(kv => new BlockStock(kv.Key, kv.Value.Name, kv.Value.Count, kv.Key switch
            {
                ItemID.Wood => WallSource.Wood,
                ItemID.DirtBlock => WallSource.Dirt,
                ItemID.StoneBlock => WallSource.Stone,
                _ => WallSource.None,
            })).ToList();
        p.AdjTiles();
        return new HouseStock(Count(p, IsDoor), Count(p, IsTable), Count(p, IsChair), Count(p, IsTorch), Count(p, IsHouseWall), list,
            Count(p, it => !it.IsAir && it.type == ItemID.Gel), p.adjTile[TileID.WorkBenches]);
    }
}
