using System.ComponentModel;
using LlmPlayer.Bridge;
using Terraria.ModLoader.Config;

namespace LlmPlayer.Config;

public sealed class LlmConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;

    /// <summary>Current values, or defaults if the config isn't loaded yet.</summary>
    public static LlmConfig Current => Terraria.ModLoader.ModContent.GetInstance<LlmConfig>() ?? new LlmConfig();

    [DefaultValue(7878)]
    [Range(1024, 65535)]
    public int Port = 7878;

    [DefaultValue("0.0.0.0")]
    public string BindAddress = "0.0.0.0";

    [DefaultValue(20)]
    [Range(0, 200)]
    public int ProtectSpawnRadius = 20;

    [DefaultValue(5)]
    [Range(1, 50)]
    public int BackupKeep = 5;

    [DefaultValue(true)]
    public bool AutoDefend = true;

    [DefaultValue(0.35f)]
    [Range(0f, 1f)]
    public float QuickHealThreshold = 0.35f;

    [DefaultValue(false)]
    public bool PauseWhenIdle = false;

    [DefaultValue(true)]
    public bool ShowPathOverlay = true;

    /// <summary>
    /// Allow the developer bridge command dev_save_and_exit (scripts/dev-reload.sh): save the player and world, then
    /// exit the game. Off by default: anyone who can reach the bridge port could close the game (it always saves first).
    /// </summary>
    [DefaultValue(false)]
    public bool DevCommands = false;

    public override void OnChanged() => BridgeSystem.OnConfigChanged(this);
}
