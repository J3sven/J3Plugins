using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CutsceneTranscripts;

internal sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool ShowButtonDuringCutscenes { get; set; } = true;
    public bool KeepLastTranscriptAfterCutscene { get; set; } = true;
    public bool OpenTranscriptWhenCutsceneEnds { get; set; }
    public int MaxEntries { get; set; } = 250;
    public float ButtonX { get; set; } = 24f;
    public float ButtonY { get; set; } = 120f;
    public float WindowWidth { get; set; } = 520f;
    public float WindowHeight { get; set; } = 520f;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
