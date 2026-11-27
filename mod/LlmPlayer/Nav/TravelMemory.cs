using LlmPlayer.Bridge;
using LlmPlayer.Core.Grid;
using LlmPlayer.Core.Nav;
using LlmPlayer.Observe;
using LlmPlayer.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace LlmPlayer.Nav;

/// <summary>
/// Records where the local player stands into the world's <see cref="TrailGraph"/> (whoever drives: the user's walks
/// count too), every game update while on the ground. One graph per world (<see cref="ExploreCavesTask.WorldKey"/>),
/// in memory for the session. Dying or leaving the world breaks the chain; teleports break it by themselves (a jump in
/// position). move_to routes far goals over it (<see cref="TrailLeg"/>).
/// </summary>
public sealed class TravelMemory : ModSystem
{
    private static readonly Dictionary<string, TrailGraph> Worlds = new();

    /// <summary>The current world's trail (created on first use).</summary>
    public static TrailGraph Current
    {
        get
        {
            var key = ExploreCavesTask.WorldKey;
            if (!Worlds.TryGetValue(key, out var g)) Worlds[key] = g = new TrailGraph();
            return g;
        }
    }

    public override void OnModLoad() => BridgeSystem.Tick += Tick;

    public override void Unload() => Worlds.Clear();

    public override void OnWorldUnload()
    {
        if (Worlds.TryGetValue(ExploreCavesTask.WorldKey, out var g)) g.Break();
    }

    private static void Tick()
    {
        if (Main.gameMenu || Main.LocalPlayer is not { active: true } p) return;
        var g = Current;
        if (p.dead || p.ghost)
        {
            g.Break();
            return;
        }
        // On the ground: no vertical motion, not grappling or flying a mount.
        if (p.velocity.Y != 0f || p.grappling[0] >= 0 || (p.mount.Active && p.mount.CanFly())) return;
        var (x, y) = StateReader.StandingTile(p);
        g.Record(new TilePoint(x, y), y <= Main.worldSurface);
    }
}
