using Dalamud.Configuration;
using Dalamud.Plugin;

namespace IINACTNativeOverlay;

internal sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;
    public bool ShowDpsMeter { get; set; } = true;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public bool HideOutOfCombat { get; set; }
    public bool UseWebSocketTransport { get; set; } = true;
    public int MeterTab { get; set; }
    public bool HideTabs { get; set; }
    public bool SoloMode { get; set; }
    public bool MergePets { get; set; } = true;
    public bool BlurOtherNames { get; set; }
    public bool AbbreviateNames { get; set; }
    public bool ShowDamagePercent { get; set; } = true;
    public bool ShowDamageTotal { get; set; } = true;
    public bool ShowDeaths { get; set; } = true;
    public bool ShowCritPercent { get; set; }
    public bool ShowSwings { get; set; }
    public bool ShowMaxHit { get; set; }
    public bool ShowOverhealPercent { get; set; } = true;
    public int MaxRows { get; set; } = 8;
    public float Opacity { get; set; } = 0.75f;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
