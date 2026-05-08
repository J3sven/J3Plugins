using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Chocobot;

internal sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public bool UseWebSocketTransport { get; set; } = true;
    public bool UseLegacyIpcFirst { get; set; } = true;
    public bool ShowDebugWindow { get; set; }
    public bool ShowInactiveWindow { get; set; } = true;
    public bool SpeakAlerts { get; set; } = true;
    public bool PreferExternalTts { get; set; } = true;
    public bool TriggerOnAllLogLines { get; set; } = true;
    public int MaxAlerts { get; set; } = 5;
    public int TtsVolume { get; set; } = 100;
    public int TtsRate { get; set; }
    public float Opacity { get; set; } = 0.88f;
    public float AlertScale { get; set; } = 1.0f;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
