namespace LlmPlayer.Core.Npc;

/// <summary>Coin amounts (in copper) as text, the way the shop shows them: "1 gold 50 silver".</summary>
public static class CoinMath
{
    public const long Silver = 100, Gold = 100 * Silver, Platinum = 100 * Gold;

    public static string Format(long copper)
    {
        if (copper <= 0) return "free";
        var parts = new List<string>(4);
        void Add(long amount, string unit) { if (amount > 0) parts.Add($"{amount} {unit}"); }
        Add(copper / Platinum, "platinum");
        Add(copper / Gold % 100, "gold");
        Add(copper / Silver % 100, "silver");
        Add(copper % 100, "copper");
        return string.Join(' ', parts);
    }
}

public static class NpcMath
{
    /// <summary>
    /// Vanilla's talk range (Main.HoverOverNPCs, and Player.Update closing the chat): a box of
    /// ±<paramref name="rangeX"/>×16 by ±<paramref name="rangeY"/>×16 px (Player.tileRangeX/Y, default 5/4) around the
    /// player's centre, its corner truncated to int, must strictly overlap the NPC's hitbox (XNA Rectangle.Intersects).
    /// </summary>
    public static bool InTalkRange(float pX, float pY, int pWidth, int pHeight, int rangeX, int rangeY,
        int nX, int nY, int nWidth, int nHeight)
    {
        int l = (int)(pX + pWidth / 2 - rangeX * 16f), t = (int)(pY + pHeight / 2 - rangeY * 16f);
        int r = l + rangeX * 16 * 2, b = t + rangeY * 16 * 2;
        return l < nX + nWidth && nX < r && t < nY + nHeight && nY < b;
    }
}
