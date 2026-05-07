using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CursorTrailIndicators;

internal sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public bool ShowTrail { get; set; } = true;
    public bool ShowCursorRing { get; set; } = true;
    public bool OnlyShowTrailDuringCombat { get; set; }
    public bool OnlyShowRingDuringCombat { get; set; }
    public bool ShakeMouseToRevealRing { get; set; }
    public bool HideWhenGameUiHidden { get; set; } = true;
    public bool HideWhenMouseOutsideViewport { get; set; } = true;
    public int MaxParticles { get; set; } = 96;
    public float ParticleLifetimeSeconds { get; set; } = 0.45f;
    public float ParticleSize { get; set; } = 5.5f;
    public float TrailSpacing { get; set; } = 6f;
    public float RingRadius { get; set; } = 15f;
    public float RingThickness { get; set; } = 2.25f;
    public float ShakeRevealLifetimeSeconds { get; set; } = 1.2f;
    public float ShakeSensitivity { get; set; } = 1f;
    public float ColorRed { get; set; } = 0.20f;
    public float ColorGreen { get; set; } = 0.70f;
    public float ColorBlue { get; set; } = 1.00f;
    public float ColorAlpha { get; set; } = 0.90f;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
