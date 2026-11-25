namespace LlmPlayer.Core.Nav;

/// <summary>Player movement capabilities. Defaults are conservative vanilla values;
/// the mod builds the real profile from player stats (ProfileBuilder).</summary>
public sealed record MovementProfile(
    int MaxJumpUp = 5,
    int MaxJumpGap = 6,
    int SafeFallTiles = 20,
    bool NoFallDamage = false,
    int PickPower = 0,
    int BuildItemCount = 0);
