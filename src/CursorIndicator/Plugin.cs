using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CursorTrailIndicators;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cursorindicator";
    private const string LegacyCommandName = "/cursortrail";

    internal Configuration Configuration { get; }
    internal IGameGui GameGui { get; }
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly CursorTrailRenderer renderer;
    private bool configOpen;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameGui gameGui,
        ICondition condition,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.condition = condition;
        GameGui = gameGui;
        Log = pluginLog;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);

        renderer = new CursorTrailRenderer(Configuration);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the cursor indicator settings."
        });
        commandManager.AddHandler(LegacyCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the cursor indicator settings."
        });

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfigWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenConfigWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        commandManager.RemoveHandler(CommandName);
        commandManager.RemoveHandler(LegacyCommandName);
    }

    private void OnCommand(string command, string args)
    {
        configOpen = !configOpen;
    }

    private void OpenConfigWindow()
    {
        configOpen = true;
    }

    private void Draw()
    {
        if (Configuration.Enabled
            && (!Configuration.HideWhenGameUiHidden || !GameGui.GameUiHidden))
        {
            renderer.Draw(condition[ConditionFlag.InCombat]);
        }

        DrawConfigWindow();
    }

    private void DrawConfigWindow()
    {
        if (!configOpen)
            return;

        if (!ImGui.Begin("Cursor Indicator", ref configOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var changed = false;

        SectionTitle("General");
        changed |= Checkbox("Enabled", Configuration.Enabled, value => Configuration.Enabled = value);
        changed |= Checkbox("Hide when game UI is hidden", Configuration.HideWhenGameUiHidden, value => Configuration.HideWhenGameUiHidden = value);
        changed |= Checkbox("Hide when mouse leaves viewport", Configuration.HideWhenMouseOutsideViewport, value => Configuration.HideWhenMouseOutsideViewport = value);

        SectionTitle("Trail");
        changed |= Checkbox("Show trail", Configuration.ShowTrail, value => Configuration.ShowTrail = value);
        changed |= Checkbox("Only during combat##Trail", Configuration.OnlyShowTrailDuringCombat, value => Configuration.OnlyShowTrailDuringCombat = value);

        changed |= SliderInt("Max particles", Configuration.MaxParticles, 16, 256, value => Configuration.MaxParticles = value);
        changed |= SliderFloat("Particle lifetime", Configuration.ParticleLifetimeSeconds, 0.1f, 1.5f, "%.2fs", value => Configuration.ParticleLifetimeSeconds = value);
        changed |= SliderFloat("Particle size", Configuration.ParticleSize, 1f, 16f, "%.1f", value => Configuration.ParticleSize = value);
        changed |= SliderFloat("Trail spacing", Configuration.TrailSpacing, 1f, 24f, "%.1f", value => Configuration.TrailSpacing = value);

        SectionTitle("Ring");
        changed |= Checkbox("Show ring", Configuration.ShowCursorRing, value => Configuration.ShowCursorRing = value);
        changed |= Checkbox("Only during combat##Ring", Configuration.OnlyShowRingDuringCombat, value => Configuration.OnlyShowRingDuringCombat = value);
        changed |= Checkbox("Shake mouse to reveal", Configuration.ShakeMouseToRevealRing, value => Configuration.ShakeMouseToRevealRing = value);
        changed |= SliderFloat("Ring radius", Configuration.RingRadius, 4f, 40f, "%.1f", value => Configuration.RingRadius = value);
        changed |= SliderFloat("Ring thickness", Configuration.RingThickness, 1f, 8f, "%.1f", value => Configuration.RingThickness = value);
        changed |= SliderFloat("Shake reveal lifetime", Configuration.ShakeRevealLifetimeSeconds, 0.2f, 4f, "%.2fs", value => Configuration.ShakeRevealLifetimeSeconds = value);
        changed |= SliderFloat("Shake sensitivity", Configuration.ShakeSensitivity, 0.25f, 3f, "%.2fx", value => Configuration.ShakeSensitivity = value);

        SectionTitle("Appearance");
        var color = ConfigurationColor;
        if (ImGui.ColorEdit4("Color", ref color, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            Configuration.ColorRed = color.X;
            Configuration.ColorGreen = color.Y;
            Configuration.ColorBlue = color.Z;
            Configuration.ColorAlpha = color.W;
            changed = true;
        }

        if (changed)
        {
            ClampConfiguration();
            Configuration.Save();
        }

        ImGui.End();
    }

    private static void SectionTitle(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(label);
    }

    private System.Numerics.Vector4 ConfigurationColor => new(
        Configuration.ColorRed,
        Configuration.ColorGreen,
        Configuration.ColorBlue,
        Configuration.ColorAlpha);

    private static bool Checkbox(string label, bool value, Action<bool> setter)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setter(value);
        return true;
    }

    private static bool SliderInt(string label, int value, int min, int max, Action<int> setter)
    {
        if (!ImGui.SliderInt(label, ref value, min, max))
            return false;

        setter(value);
        return true;
    }

    private static bool SliderFloat(string label, float value, float min, float max, string format, Action<float> setter)
    {
        if (!ImGui.SliderFloat(label, ref value, min, max, format))
            return false;

        setter(value);
        return true;
    }

    private void ClampConfiguration()
    {
        Configuration.MaxParticles = Math.Clamp(Configuration.MaxParticles, 16, 256);
        Configuration.ParticleLifetimeSeconds = Math.Clamp(Configuration.ParticleLifetimeSeconds, 0.1f, 1.5f);
        Configuration.ParticleSize = Math.Clamp(Configuration.ParticleSize, 1f, 16f);
        Configuration.TrailSpacing = Math.Clamp(Configuration.TrailSpacing, 1f, 24f);
        Configuration.RingRadius = Math.Clamp(Configuration.RingRadius, 4f, 40f);
        Configuration.RingThickness = Math.Clamp(Configuration.RingThickness, 1f, 8f);
        Configuration.ShakeRevealLifetimeSeconds = Math.Clamp(Configuration.ShakeRevealLifetimeSeconds, 0.2f, 4f);
        Configuration.ShakeSensitivity = Math.Clamp(Configuration.ShakeSensitivity, 0.25f, 3f);
        Configuration.ColorRed = Math.Clamp(Configuration.ColorRed, 0f, 1f);
        Configuration.ColorGreen = Math.Clamp(Configuration.ColorGreen, 0f, 1f);
        Configuration.ColorBlue = Math.Clamp(Configuration.ColorBlue, 0f, 1f);
        Configuration.ColorAlpha = Math.Clamp(Configuration.ColorAlpha, 0f, 1f);
    }
}
