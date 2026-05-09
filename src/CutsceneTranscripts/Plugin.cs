using System.Numerics;
using Dalamud.Game.Addon.Events;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CutsceneTranscripts;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cutscenetranscript";
    private const string ShortCommandName = "/cstranscript";
    private static readonly TimeSpan VisibleAddonGracePeriod = TimeSpan.FromMilliseconds(500);
    private static readonly string[] ChoiceAddonNames =
    [
        "SelectString",
        "CutSceneSelectString",
        "SelectIconString",
        "SelectYesno",
        "SelectOk"
    ];
    private static readonly Vector4[] SpeakerColorPalette =
    [
        new(0.45f, 0.78f, 1.00f, 1.00f),
        new(1.00f, 0.68f, 0.38f, 1.00f),
        new(0.58f, 0.90f, 0.54f, 1.00f),
        new(0.95f, 0.56f, 0.72f, 1.00f),
        new(0.76f, 0.68f, 1.00f, 1.00f),
        new(1.00f, 0.86f, 0.38f, 1.00f),
        new(0.42f, 0.91f, 0.84f, 1.00f),
        new(0.86f, 0.62f, 0.42f, 1.00f),
        new(0.72f, 0.88f, 1.00f, 1.00f),
        new(0.93f, 0.77f, 0.96f, 1.00f),
        new(0.70f, 0.94f, 0.72f, 1.00f),
        new(1.00f, 0.74f, 0.58f, 1.00f)
    ];
    private static readonly Vector4 DialogueBoxFill = new(0.86f, 0.80f, 0.67f, 1.00f);
    private static readonly Vector4 DialogueBoxHighlight = new(0.96f, 0.91f, 0.80f, 0.38f);
    private static readonly Vector4 DialogueBoxBorder = new(0.34f, 0.29f, 0.21f, 0.95f);
    private static readonly Vector4 DialogueTextColor = new(0.08f, 0.07f, 0.05f, 1.00f);
    private static readonly Vector4 DialogueShadowColor = new(0.00f, 0.00f, 0.00f, 0.28f);
    private static readonly Vector4 IconFill = new(0.13f, 0.11f, 0.08f, 0.92f);
    private static readonly Vector4 IconFillHover = new(0.23f, 0.18f, 0.11f, 0.96f);
    private static readonly Vector4 IconGold = new(0.77f, 0.58f, 0.28f, 1.00f);
    private static readonly Vector4 WindowBg = new(0.10f, 0.09f, 0.07f, 0.96f);
    private static readonly Vector4 WindowBorder = new(0.67f, 0.52f, 0.27f, 0.86f);
    private static readonly Vector4 WindowBorderDark = new(0.02f, 0.02f, 0.02f, 0.78f);
    private static readonly Vector4 ToolbarBg = new(0.18f, 0.14f, 0.09f, 0.70f);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly ITextureProvider textureProvider;
    private readonly IGameGui gameGui;
    private readonly List<TranscriptEntry> entries = [];
    private readonly Dictionary<nint, ChoiceState> choiceStates = [];
    private readonly Dictionary<string, Vector4> speakerColors = [];
    private readonly List<VoiceCaptureProbe> voiceCaptureProbes = [];
    private readonly ISharedImmediateTexture? speakerShadowTexture;
    private bool transcriptOpen;
    private bool configOpen;
    private bool lastCutsceneActive;
    private DateTimeOffset lastCutsceneActiveAt;
    private string? lastObservedTalkKey;
    private string? lastTranscriptEntryKey;
    private string? lastDialogSpeaker;
    private TalkWindowBounds? talkWindowBounds;
    private DateTimeOffset lastTalkWindowBoundsAt;

    internal static IPluginLog Log { get; private set; } = null!;
    internal Configuration Configuration { get; }

    private sealed record TranscriptEntry(DateTimeOffset Timestamp, string? Speaker, string Text, VoiceClipRef? VoiceClip);
    private sealed record VoiceClipRef(string Path, uint SoundNumber, bool CanReplay = true);
    private readonly record struct VoiceSoundCandidate(
        string Path,
        uint SoundNumber,
        float Elapsed,
        float Volume,
        SoundVolumeCategory VolumeCategory,
        bool IsPlaying,
        bool IsLoading,
        bool IsPositional,
        bool IsAutoRelease,
        int MidiNote);
    private readonly record struct TalkWindowBounds(Vector2 Position, Vector2 Size);

    private sealed class VoiceCaptureProbe
    {
        public int EntryIndex { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public DateTimeOffset NextSampleAt { get; set; }
    }

    private sealed class ChoiceState
    {
        public string AddonName { get; init; } = string.Empty;
        public List<string> Options { get; } = [];
        public int SelectedIndex { get; set; } = -1;
        public int ListItemIndex { get; set; } = -1;
        public int LastEventParam { get; set; } = -1;
        public bool LastEventParamMayBeChoiceIndex { get; set; } = true;
        public DateTimeOffset LastSeenAt { get; set; }
        public bool SubmitSeen { get; set; }
        public bool Recorded { get; set; }
    }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IAddonLifecycle addonLifecycle,
        ICondition condition,
        IObjectTable objectTable,
        ITextureProvider textureProvider,
        IGameGui gameGui,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.addonLifecycle = addonLifecycle;
        this.condition = condition;
        this.objectTable = objectTable;
        this.textureProvider = textureProvider;
        this.gameGui = gameGui;
        Log = pluginLog;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);
        ClampConfiguration();
        speakerShadowTexture = LoadSpeakerShadowTexture();

        pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the current cutscene transcript. Use 'config' for settings or 'clear' to clear the transcript."
        });
        commandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the current cutscene transcript."
        });

        addonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnTalkPostUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnTalkPostUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnTalkFinalize);
        addonLifecycle.RegisterListener(AddonEvent.PostUpdate, ChoiceAddonNames, OnChoicePostUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ChoiceAddonNames, OnChoicePostUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, ChoiceAddonNames, OnChoiceReceiveEvent);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, ChoiceAddonNames, OnChoiceReceiveEvent);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, ChoiceAddonNames, OnChoiceFinalize);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenTranscriptWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenTranscriptWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        addonLifecycle.UnregisterListener(OnTalkPostUpdate, OnTalkFinalize, OnChoicePostUpdate, OnChoiceReceiveEvent, OnChoiceFinalize);
        commandManager.RemoveHandler(CommandName);
        commandManager.RemoveHandler(ShortCommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                Configuration.Enabled = true;
                Configuration.Save();
                break;
            case "off":
                Configuration.Enabled = false;
                Configuration.Save();
                break;
            case "clear":
                ClearTranscript();
                break;
            case "config":
            case "settings":
                configOpen = true;
                break;
            default:
                transcriptOpen = !transcriptOpen;
                break;
        }
    }

    private void OpenTranscriptWindow()
    {
        transcriptOpen = true;
    }

    private void OpenConfigWindow()
    {
        configOpen = true;
    }

    private void Draw()
    {
        var cutsceneActive = IsCutsceneActive();
        if (!lastCutsceneActive && cutsceneActive)
        {
            ClearTranscript();
        }
        else if (lastCutsceneActive && !cutsceneActive)
        {
            if (Configuration.OpenTranscriptWhenCutsceneEnds && entries.Count > 0)
            {
                transcriptOpen = true;
            }
            else
            {
                if (!Configuration.OpenTranscriptWhenCutsceneEnds)
                    transcriptOpen = false;

                if (!Configuration.KeepLastTranscriptAfterCutscene)
                    ClearTranscript();
            }
        }

        if (cutsceneActive)
            lastCutsceneActiveAt = DateTimeOffset.Now;

        lastCutsceneActive = cutsceneActive;

        if (Configuration.Enabled
            && Configuration.ShowButtonDuringCutscenes
            && cutsceneActive
            && entries.Count > 0
            && IsTalkWindowVisible()
            && !IsChoiceAddonVisible())
            DrawTranscriptIconButton();

        DrawTranscriptWindow();
        DrawConfigWindow();
        ProcessVoiceCaptureProbes();
    }

    private bool IsCutsceneActive()
    {
        return pluginInterface.UiBuilder.CutsceneActive
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78];
    }

    private void DrawTranscriptIconButton()
    {
        var scale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        var buttonSize = new Vector2(30f * scale, 30f * scale);
        var anchored = TryGetRecentTalkWindowBounds(out var bounds);
        var windowPos = anchored
            ? new Vector2(bounds.Position.X + bounds.Size.X - buttonSize.X - 18f * scale, bounds.Position.Y + 12f * scale)
            : new Vector2(Configuration.ButtonX, Configuration.ButtonY);

        ImGui.SetNextWindowPos(windowPos, anchored ? ImGuiCond.Always : ImGuiCond.FirstUseEver);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!ImGui.Begin("##CutsceneTranscriptButton", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        if (ImGui.InvisibleButton("##OpenTranscript", buttonSize))
            transcriptOpen = true;

        DrawTranscriptIcon(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), buttonSize, ImGui.IsItemHovered());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open transcript");

        if (!anchored)
        {
            var pos = ImGui.GetWindowPos();
            if (Vector2.Distance(pos, new Vector2(Configuration.ButtonX, Configuration.ButtonY)) > 0.5f)
            {
                Configuration.ButtonX = pos.X;
                Configuration.ButtonY = pos.Y;
                Configuration.Save();
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawTranscriptIcon(ImDrawListPtr drawList, Vector2 pos, Vector2 size, bool hovered)
    {
        var scale = size.X / 30f;
        var end = pos + size;
        var rounding = 5f * scale;
        drawList.AddRectFilled(pos + new Vector2(2f * scale, 3f * scale), end + new Vector2(2f * scale, 3f * scale),
                               ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.34f)), rounding);
        drawList.AddRectFilled(pos, end, ImGui.GetColorU32(hovered ? IconFillHover : IconFill), rounding);
        drawList.AddRect(pos, end, ImGui.GetColorU32(IconGold), rounding, ImDrawFlags.RoundCornersAll, 1.2f * scale);

        var pageMin = pos + new Vector2(8f * scale, 6f * scale);
        var pageMax = pos + new Vector2(22f * scale, 24f * scale);
        drawList.AddRectFilled(pageMin, pageMax, ImGui.GetColorU32(new Vector4(0.82f, 0.75f, 0.58f, 1f)), 2f * scale);
        drawList.AddRect(pageMin, pageMax, ImGui.GetColorU32(new Vector4(0.24f, 0.18f, 0.10f, 0.95f)), 2f * scale, ImDrawFlags.RoundCornersAll, 1f * scale);
        for (var i = 0; i < 3; i++)
        {
            var y = pageMin.Y + (6f + i * 4f) * scale;
            drawList.AddLine(new Vector2(pageMin.X + 3f * scale, y), new Vector2(pageMax.X - 3f * scale, y),
                             ImGui.GetColorU32(new Vector4(0.24f, 0.18f, 0.10f, 0.70f)), 1f * scale);
        }
    }

    private bool IsChoiceAddonVisible()
    {
        var now = DateTimeOffset.Now;
        return choiceStates.Values.Any(state => now - state.LastSeenAt <= VisibleAddonGracePeriod);
    }

    private void DrawTranscriptWindow()
    {
        if (!transcriptOpen)
            return;

        var scale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        ImGui.SetNextWindowSize(new Vector2(Configuration.WindowWidth, Configuration.WindowHeight), ImGuiCond.FirstUseEver);
        PushTranscriptWindowStyle(scale);
        if (!ImGui.Begin("Cutscene Transcript", ref transcriptOpen))
        {
            ImGui.End();
            PopTranscriptWindowStyle();
            return;
        }

        DrawTranscriptWindowFrame(scale);
        DrawTranscriptToolbar(scale);

        if (entries.Count == 0)
        {
            ImGui.TextDisabled("No dialog has been recorded yet.");
        }
        else
        {
            for (var i = 0; i < entries.Count; i++)
                DrawDialogueEntry(i, entries[i]);

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 24f)
                ImGui.SetScrollHereY(1f);
        }

        var size = ImGui.GetWindowSize();
        if (Vector2.Distance(size, new Vector2(Configuration.WindowWidth, Configuration.WindowHeight)) > 1f)
        {
            Configuration.WindowWidth = Math.Clamp(size.X, 320f, 1200f);
            Configuration.WindowHeight = Math.Clamp(size.Y, 240f, 900f);
            Configuration.Save();
        }

        ImGui.End();
        PopTranscriptWindowStyle();
    }

    private static void PushTranscriptWindowStyle(float scale)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 12f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f * scale, 8f * scale));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, WindowBorder);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.12f, 0.10f, 0.07f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.22f, 0.17f, 0.10f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.17f, 0.10f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.34f, 0.25f, 0.13f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.43f, 0.31f, 0.14f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.62f, 0.48f, 0.25f, 0.62f));
    }

    private static void PopTranscriptWindowStyle()
    {
        ImGui.PopStyleColor(8);
        ImGui.PopStyleVar(5);
    }

    private static void DrawTranscriptWindowFrame(float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var end = pos + size;
        var rounding = 7f * scale;

        drawList.AddRect(pos + new Vector2(1f * scale, 1f * scale), end - new Vector2(1f * scale, 1f * scale),
                         ImGui.GetColorU32(WindowBorderDark), rounding, ImDrawFlags.RoundCornersAll, 2.5f * scale);
        drawList.AddRect(pos + new Vector2(2f * scale, 2f * scale), end - new Vector2(2f * scale, 2f * scale),
                         ImGui.GetColorU32(WindowBorder), rounding, ImDrawFlags.RoundCornersAll, 1f * scale);
    }

    private void DrawTranscriptToolbar(float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var toolbarPos = ImGui.GetCursorScreenPos();
        var toolbarHeight = ImGui.GetFrameHeight() + 8f * scale;
        var toolbarEnd = toolbarPos + new Vector2(ImGui.GetContentRegionAvail().X, toolbarHeight);
        drawList.AddRectFilled(toolbarPos - new Vector2(4f * scale, 2f * scale),
                               toolbarEnd + new Vector2(4f * scale, 2f * scale),
                               ImGui.GetColorU32(ToolbarBg), 5f * scale);

        ImGui.SetCursorScreenPos(toolbarPos + new Vector2(4f * scale, 4f * scale));
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(BuildTranscriptText());

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            ClearTranscript();

        ImGui.SameLine();
        ImGui.TextDisabled($"{entries.Count} line{(entries.Count == 1 ? string.Empty : "s")}");
        ImGui.SetCursorScreenPos(new Vector2(toolbarPos.X, toolbarPos.Y + toolbarHeight + 8f * scale));
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawDialogueEntry(int entryIndex, TranscriptEntry entry)
    {
        var scale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var availableWidth = Math.Max(280f * scale, ImGui.GetContentRegionAvail().X);
        var boxWidth = Math.Max(260f * scale, availableWidth - 8f * scale);
        var boxX = cursor.X + 4f * scale;
        var speakerHeight = string.IsNullOrWhiteSpace(entry.Speaker)
            ? 0f
            : ImGui.GetTextLineHeight() + 2f * scale;
        var speakerOverlap = string.IsNullOrWhiteSpace(entry.Speaker) ? 0f : 8f * scale;
        var boxY = cursor.Y + Math.Max(0f, speakerHeight - speakerOverlap);
        var paddingX = 18f * scale;
        var paddingY = 14f * scale;
        var lineHeight = ImGui.GetTextLineHeight() * 1.18f;
        var replayButtonSize = entry.VoiceClip == null ? 0f : 22f * scale;
        var replaySpace = entry.VoiceClip == null ? 0f : replayButtonSize + 10f * scale;
        var wrapWidth = Math.Max(80f * scale, boxWidth - paddingX * 2f - replaySpace);
        var lines = WrapText(entry.Text, wrapWidth);
        var boxHeight = Math.Max(58f * scale, paddingY * 2f + lines.Count * lineHeight);
        var boxPos = new Vector2(boxX, boxY);
        var boxEnd = boxPos + new Vector2(boxWidth, boxHeight);
        var rounding = 19f * scale;

        drawList.AddRectFilled(boxPos + new Vector2(2f * scale, 3f * scale),
                               boxEnd + new Vector2(2f * scale, 3f * scale),
                               ImGui.GetColorU32(DialogueShadowColor), rounding);
        drawList.AddRectFilled(boxPos, boxEnd, ImGui.GetColorU32(DialogueBoxFill), rounding);
        drawList.AddRectFilled(boxPos + new Vector2(3f * scale, 3f * scale),
                               new Vector2(boxEnd.X - 3f * scale, boxPos.Y + boxHeight * 0.48f),
                               ImGui.GetColorU32(DialogueBoxHighlight), rounding * 0.82f);
        drawList.AddRect(boxPos, boxEnd, ImGui.GetColorU32(DialogueBoxBorder), rounding,
                         ImDrawFlags.RoundCornersAll, 1.35f * scale);

        var textPos = boxPos + new Vector2(paddingX, paddingY);
        for (var i = 0; i < lines.Count; i++)
            drawList.AddText(textPos + new Vector2(0f, i * lineHeight), ImGui.GetColorU32(DialogueTextColor), lines[i]);

        if (entry.VoiceClip is { } voiceClip)
            DrawReplayVoiceButton(drawList, boxEnd - new Vector2(paddingX + replayButtonSize, paddingY + replayButtonSize * 0.25f), replayButtonSize, voiceClip, scale);

        if (!string.IsNullOrWhiteSpace(entry.Speaker))
        {
            var speakerPos = new Vector2(boxX + 20f * scale, boxY - 10f * scale);
            DrawSpeakerTag(drawList, speakerPos, entry.Speaker, GetSpeakerColor(entry.Speaker), scale);
        }

        ImGui.Dummy(new Vector2(availableWidth, boxY - cursor.Y + boxHeight + 10f * scale));
    }

    private void DrawReplayVoiceButton(ImDrawListPtr drawList, Vector2 pos, float size, VoiceClipRef voiceClip, float scale)
    {
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(pos);
        ImGui.PushID($"voice-{voiceClip.Path}");
        var clicked = ImGui.InvisibleButton("replay", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(voiceClip.CanReplay ? "Replay voiced line" : "Replay unavailable");
        ImGui.PopID();
        ImGui.SetCursorScreenPos(cursor);

        var center = pos + new Vector2(size * 0.5f);
        var fill = !voiceClip.CanReplay
            ? new Vector4(0.16f, 0.15f, 0.13f, 0.74f)
            : hovered
            ? new Vector4(0.34f, 0.25f, 0.13f, 0.96f)
            : new Vector4(0.20f, 0.15f, 0.09f, 0.86f);
        var iconColor = voiceClip.CanReplay
            ? IconGold
            : new Vector4(0.56f, 0.52f, 0.45f, 0.82f);
        drawList.AddCircleFilled(center, size * 0.5f, ImGui.GetColorU32(fill), 18);
        drawList.AddCircle(center, size * 0.5f - 0.5f * scale, ImGui.GetColorU32(voiceClip.CanReplay ? WindowBorder : DialogueBoxBorder), 18, 1f * scale);

        var icon = ImGui.GetColorU32(iconColor);
        var speakerMin = pos + new Vector2(size * 0.26f, size * 0.41f);
        var speakerMax = pos + new Vector2(size * 0.39f, size * 0.59f);
        drawList.AddRectFilled(speakerMin, speakerMax, icon, 1.2f * scale);

        var coneTop = pos + new Vector2(size * 0.39f, size * 0.39f);
        var coneMid = pos + new Vector2(size * 0.55f, size * 0.29f);
        var coneBottom = pos + new Vector2(size * 0.55f, size * 0.71f);
        var coneLeftBottom = pos + new Vector2(size * 0.39f, size * 0.61f);
        drawList.AddQuadFilled(coneTop, coneMid, coneBottom, coneLeftBottom, icon);

        drawList.PathClear();
        drawList.PathArcTo(center + new Vector2(size * 0.03f, 0f), size * 0.18f, -0.55f, 0.55f, 8);
        drawList.PathStroke(icon, ImDrawFlags.None, 1.5f * scale);
        drawList.PathClear();
        drawList.PathArcTo(center + new Vector2(size * 0.03f, 0f), size * 0.30f, -0.50f, 0.50f, 10);
        drawList.PathStroke(icon, ImDrawFlags.None, 1.3f * scale);

        if (clicked && voiceClip.CanReplay)
            ReplayVoiceClip(voiceClip);
    }

    private ISharedImmediateTexture? LoadSpeakerShadowTexture()
    {
        var shadowPath = Path.Combine(pluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "speaker-shadow-v2.png");
        return File.Exists(shadowPath)
            ? textureProvider.GetFromFile(shadowPath)
            : null;
    }

    private void DrawSpeakerTag(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color, float scale)
    {
        var textSize = ImGui.CalcTextSize(text);
        if (speakerShadowTexture?.TryGetWrap(out IDalamudTextureWrap? wrap, out _) == true)
        {
            var shadowWidth = Math.Max(122f * scale, textSize.X + 86f * scale);
            var shadowHeight = 23f * scale;
            var shadowPos = pos - new Vector2(13f * scale, 3f * scale);
            drawList.AddImage(wrap.Handle, shadowPos, shadowPos + new Vector2(shadowWidth, shadowHeight));
        }
        else
        {
            drawList.AddText(pos + new Vector2(0f, 3.0f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.28f)), text);
            drawList.AddText(pos + new Vector2(1.6f * scale, 2.2f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.34f)), text);
        }

        drawList.AddText(pos + new Vector2(1.0f * scale, 1.0f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.70f)), text);
        drawList.AddText(pos, ImGui.GetColorU32(color), text);
    }

    private void DrawConfigWindow()
    {
        if (!configOpen)
            return;

        if (!ImGui.Begin("Cutscene Transcript Settings", ref configOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var changed = false;
        changed |= Checkbox("Enabled", Configuration.Enabled, value => Configuration.Enabled = value);
        changed |= Checkbox("Show transcript button during cutscenes", Configuration.ShowButtonDuringCutscenes, value => Configuration.ShowButtonDuringCutscenes = value);
        changed |= Checkbox("Keep last transcript after cutscene", Configuration.KeepLastTranscriptAfterCutscene, value => Configuration.KeepLastTranscriptAfterCutscene = value);
        changed |= Checkbox("Open transcript when cutscene ends", Configuration.OpenTranscriptWhenCutsceneEnds, value => Configuration.OpenTranscriptWhenCutsceneEnds = value);
        changed |= SliderInt("Max recorded lines", Configuration.MaxEntries, 25, 1000, value => Configuration.MaxEntries = value);

        if (changed)
        {
            ClampConfiguration();
            TrimEntries();
            Configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Recorded lines: {entries.Count}");
        if (ImGui.Button("Clear Transcript"))
            ClearTranscript();

        ImGui.End();
    }

    private void OnTalkPostUpdate(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull || !args.Addon.IsVisible)
        {
            talkWindowBounds = null;
            return;
        }

        var cutsceneActive = IsCutsceneActive();
        var addon = (AddonTalk*)args.Addon.Address;
        if (cutsceneActive)
            UpdateTalkWindowBounds(addon);

        if (!Configuration.Enabled || !cutsceneActive)
            return;

        CaptureTalkAddon(addon);
    }

    private void OnTalkFinalize(AddonEvent eventType, AddonArgs args)
    {
        lastObservedTalkKey = null;
        talkWindowBounds = null;
    }

    private void UpdateTalkWindowBounds(AddonTalk* addon)
    {
        var root = addon->RootNode;
        if (root == null)
        {
            talkWindowBounds = null;
            return;
        }

        var width = root->Width * root->ScaleX;
        var height = root->Height * root->ScaleY;
        if (width <= 0 || height <= 0)
        {
            talkWindowBounds = null;
            return;
        }

        talkWindowBounds = new TalkWindowBounds(new Vector2(root->ScreenX, root->ScreenY), new Vector2(width, height));
        lastTalkWindowBoundsAt = DateTimeOffset.Now;
    }

    private bool TryGetRecentTalkWindowBounds(out TalkWindowBounds bounds)
    {
        if (talkWindowBounds is { } current
            && DateTimeOffset.Now - lastTalkWindowBoundsAt <= TimeSpan.FromSeconds(1.5))
        {
            bounds = current;
            return true;
        }

        bounds = default;
        return false;
    }

    private bool IsTalkWindowVisible()
    {
        return talkWindowBounds is not null
            && DateTimeOffset.Now - lastTalkWindowBoundsAt <= VisibleAddonGracePeriod;
    }

    private void OnChoicePostUpdate(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var shouldCapture = ShouldCaptureChoice(args);
        if (!shouldCapture || !args.Addon.IsVisible)
            return;

        CacheChoiceState(args);
    }

    private void OnChoiceReceiveEvent(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var shouldCapture = ShouldCaptureChoice(args);
        if (!shouldCapture || !args.Addon.IsVisible)
            return;

        if (args is not AddonReceiveEventArgs receiveArgs)
            return;

        var isSubmitEvent = IsChoiceSubmitEvent(receiveArgs);
        var listItemIndex = ReadListItemIndex(receiveArgs);
        var state = CacheChoiceState(args, receiveArgs.EventParam, listItemIndex, EventParamMayBeChoiceIndex(receiveArgs));
        if (state == null)
            return;

        if (!isSubmitEvent)
            return;

        state.SubmitSeen = true;
        TryRecordChoice(state, preferEventParam: eventType == AddonEvent.PreReceiveEvent);
    }

    private void OnChoiceFinalize(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var address = args.Addon.Address;
        if (choiceStates.TryGetValue(address, out var state))
        {
            if (state.SubmitSeen)
                TryRecordChoice(state, preferEventParam: false);
        }

        choiceStates.Remove(address);
    }

    private void CaptureTalkAddon(AddonTalk* addon)
    {
        if (addon == null)
            return;

        var texts = new List<string>();
        AddText(texts, addon->String268.AsDalamudSeString().TextValue);
        AddText(texts, addon->String2D0.AsDalamudSeString().TextValue);
        AddText(texts, addon->String338.AsDalamudSeString().TextValue);
        AddText(texts, addon->String408.AsDalamudSeString().TextValue);
        AddText(texts, addon->String470.AsDalamudSeString().TextValue);
        AddText(texts, addon->String4D8.AsDalamudSeString().TextValue);
        AddText(texts, addon->String540.AsDalamudSeString().TextValue);
        AddText(texts, ReadTextNode(addon->AtkTextNode220));
        AddText(texts, ReadTextNode(addon->AtkTextNode228));
        AddText(texts, ReadTextNode(addon->AtkTextNode238));
        AddText(texts, ReadTextNode(addon->AtkTextNode240));
        AddText(texts, ReadTextNode(addon->AtkTextNode248));

        if (texts.Count == 0)
            return;

        var talkKey = string.Join("\n", texts);
        if (string.Equals(talkKey, lastObservedTalkKey, StringComparison.Ordinal))
            return;

        lastObservedTalkKey = talkKey;
        AddTranscriptEntry(texts);
    }

    private void AddTranscriptEntry(List<string> texts)
    {
        var body = texts.OrderByDescending(text => text.Length).First();
        string? speaker = null;

        foreach (var candidate in texts)
        {
            if (TextEquivalent(candidate, body) || candidate.Length > 80 || candidate.Contains('\n'))
                continue;

            speaker = candidate;
            break;
        }

        if (string.IsNullOrWhiteSpace(speaker))
            speaker = lastDialogSpeaker;
        else
            lastDialogSpeaker = speaker;

        var entryKey = $"{speaker}\n{body}";
        if (string.Equals(entryKey, lastTranscriptEntryKey, StringComparison.Ordinal))
            return;

        lastTranscriptEntryKey = entryKey;
        var voiceClip = TryCaptureVoiceClip();
        entries.Add(new TranscriptEntry(DateTimeOffset.Now, speaker, body, voiceClip));
        TrimEntries();
        StartVoiceCaptureProbe(entries.Count - 1);
    }

    private void AddChoiceEntry(string choiceText)
    {
        choiceText = CleanText(choiceText);
        if (string.IsNullOrWhiteSpace(choiceText))
            return;

        var playerName = GetPlayerName();
        var entryKey = $"{playerName}\n{choiceText}";
        if (string.Equals(entryKey, lastTranscriptEntryKey, StringComparison.Ordinal))
            return;

        lastTranscriptEntryKey = entryKey;
        entries.Add(new TranscriptEntry(DateTimeOffset.Now, playerName, choiceText, null));
        TrimEntries();
    }

    private string GetPlayerName()
    {
        var name = objectTable.LocalPlayer?.Name.TextValue;
        return string.IsNullOrWhiteSpace(name)
            ? "Player"
            : name;
    }

    private bool ShouldCaptureChoice(AddonArgs args)
    {
        if (!Configuration.Enabled)
            return false;

        if (IsCutsceneActive() || lastCutsceneActive)
            return true;

        if (args.AddonName.Contains("CutScene", StringComparison.OrdinalIgnoreCase) && entries.Count > 0)
            return true;

        return lastCutsceneActiveAt != default
            && DateTimeOffset.Now - lastCutsceneActiveAt <= TimeSpan.FromSeconds(30);
    }

    private ChoiceState? CacheChoiceState(AddonArgs args, int eventParam = -1, int listItemIndex = -1, bool eventParamMayBeChoiceIndex = true)
    {
        if (args.Addon.IsNull)
            return null;

        var address = args.Addon.Address;
        if (!choiceStates.TryGetValue(address, out var state))
        {
            state = new ChoiceState { AddonName = args.AddonName };
            choiceStates[address] = state;
        }

        state.LastSeenAt = DateTimeOffset.Now;

        var options = ReadChoiceOptions(args);
        if (options.Count > 0)
        {
            state.Options.Clear();
            state.Options.AddRange(options);
        }

        var selectedIndex = ReadSelectedChoiceIndex(args);
        if (IsValidChoiceIndex(state, selectedIndex))
            state.SelectedIndex = selectedIndex;

        if (eventParam >= 0)
        {
            state.LastEventParam = eventParam;
            state.LastEventParamMayBeChoiceIndex = eventParamMayBeChoiceIndex;
        }

        if (IsValidChoiceIndex(state, listItemIndex))
        {
            state.ListItemIndex = listItemIndex;
        }
        else if (listItemIndex > 0 && IsValidChoiceIndex(state, listItemIndex - 1))
        {
            state.ListItemIndex = listItemIndex - 1;
        }

        return state;
    }

    private static List<string> ReadChoiceOptions(AddonArgs args)
    {
        return args.AddonName switch
        {
            "SelectString" => ReadSelectStringOptions((AddonSelectString*)args.Addon.Address),
            "SelectYesno" => ReadGenericChoiceOptions((AtkUnitBase*)args.Addon.Address, preferFinalPair: true),
            "CutSceneSelectString" => ReadCutSceneSelectStringOptions((AtkUnitBase*)args.Addon.Address),
            _ => ReadGenericChoiceOptions((AtkUnitBase*)args.Addon.Address)
        };
    }

    private static int ReadSelectedChoiceIndex(AddonArgs args)
    {
        return args.AddonName switch
        {
            "SelectString" => ReadSelectStringSelectedIndex((AddonSelectString*)args.Addon.Address),
            _ => ReadGenericSelectedIndex((AtkUnitBase*)args.Addon.Address)
        };
    }

    private void TryRecordChoice(ChoiceState state, bool preferEventParam)
    {
        if (state.Recorded)
            return;

        var indices = new List<int> { state.ListItemIndex };
        if (preferEventParam && state.LastEventParamMayBeChoiceIndex)
            indices.Add(state.LastEventParam);

        indices.Add(state.SelectedIndex);

        if (!preferEventParam && state.LastEventParamMayBeChoiceIndex)
            indices.Add(state.LastEventParam);

        foreach (var index in indices)
        {
            if (!IsValidChoiceIndex(state, index))
                continue;

            AddChoiceEntry(state.Options[index]);
            state.Recorded = true;
            return;
        }

        if (state.Options.Count == 1)
        {
            AddChoiceEntry(state.Options[0]);
            state.Recorded = true;
            return;
        }
    }

    private static bool IsValidChoiceIndex(ChoiceState state, int index)
    {
        return index >= 0 && index < state.Options.Count;
    }

    private static bool IsChoiceSubmitEvent(AddonReceiveEventArgs args)
    {
        return args.AtkEventType is AddonEventType.MouseClick
            or AddonEventType.MouseUp
            or AddonEventType.ButtonClick
            or AddonEventType.ListButtonPress
            or AddonEventType.ListItemClick
            or AddonEventType.ListItemDoubleClick
            or AddonEventType.ListItemSelect
            or AddonEventType.DialogueSubmit;
    }

    private static bool EventParamMayBeChoiceIndex(AddonReceiveEventArgs args)
    {
        return args.AtkEventType is not (AddonEventType.ListButtonPress
            or AddonEventType.ListItemClick
            or AddonEventType.ListItemDoubleClick
            or AddonEventType.ListItemSelect);
    }

    private static int ReadListItemIndex(AddonReceiveEventArgs args)
    {
        if (args.AtkEventData == 0)
            return -1;

        var eventData = (AtkEventData*)args.AtkEventData;
        return eventData == null
            ? -1
            : eventData->ListItemData.SelectedIndex;
    }

    private static List<string> ReadSelectStringOptions(AddonSelectString* addon)
    {
        var options = new List<string>();
        if (addon == null || addon->PopupMenu.EntryNames == null)
            return options;

        var count = Math.Clamp(addon->PopupMenu.EntryCount, 0, 100);
        for (var i = 0; i < count; i++)
            AddText(options, addon->PopupMenu.EntryNames[i].AsDalamudSeString().TextValue);

        return options;
    }

    private static int ReadSelectStringSelectedIndex(AddonSelectString* addon)
    {
        if (addon == null || addon->PopupMenu.List == null)
            return -1;

        var list = addon->PopupMenu.List;
        var count = addon->PopupMenu.EntryCount;
        var candidates = new[]
        {
            list->SelectedItemIndex,
            list->HeldItemIndex,
            list->HoveredItemIndex,
            list->HoveredItemIndex2,
            list->HoveredItemIndex3
        };

        return candidates.FirstOrDefault(index => index >= 0 && index < count, -1);
    }

    private static int ReadGenericSelectedIndex(AtkUnitBase* addon)
    {
        if (addon == null)
            return -1;

        return ReadGenericSelectedIndex(addon->RootNode);
    }

    private static int ReadGenericSelectedIndex(AtkResNode* node)
    {
        if (node == null)
            return -1;

        if (node->Type == NodeType.Component)
        {
            var list = ((AtkComponentNode*)node)->GetAsAtkComponentList();
            var selected = ReadComponentListSelectedIndex(list);
            if (selected >= 0)
                return selected;
        }

        var child = node->ChildNode;
        while (child != null)
        {
            var selected = ReadGenericSelectedIndex(child);
            if (selected >= 0)
                return selected;

            child = child->PrevSiblingNode;
        }

        return -1;
    }

    private static int ReadComponentListSelectedIndex(AtkComponentList* list)
    {
        if (list == null)
            return -1;

        var count = list->ListLength;
        var candidates = new[]
        {
            list->SelectedItemIndex,
            list->HeldItemIndex,
            list->HoveredItemIndex,
            list->HoveredItemIndex2,
            list->HoveredItemIndex3
        };

        return candidates.FirstOrDefault(index => index >= 0 && index < count, -1);
    }

    private static List<string> ReadCutSceneSelectStringOptions(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        if (addon == null)
            return texts;

        CollectTextNodes(addon->RootNode, texts);
        CollectAtkValueStrings(addon, texts);

        if (texts.Count > 1)
            texts.RemoveAt(0);

        return texts
            .Where(text => text.Length <= 240)
            .ToList();
    }

    private static List<string> ReadGenericChoiceOptions(AtkUnitBase* addon, bool preferFinalPair = false)
    {
        var texts = new List<string>();
        if (addon == null)
            return texts;

        CollectTextNodes(addon->RootNode, texts);
        CollectAtkValueStrings(addon, texts);

        if (texts.Count == 0)
            return texts;

        if (preferFinalPair)
        {
            var shortTexts = texts.Where(text => text.Length <= 80 && !text.Contains('\n')).TakeLast(2).ToList();
            if (shortTexts.Count > 0)
                return shortTexts;
        }

        return texts
            .Where(text => text.Length <= 240)
            .ToList();
    }

    private static void CollectAtkValueStrings(AtkUnitBase* addon, List<string> texts)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
            return;

        var count = Math.Min(addon->AtkValuesCount, (ushort)100);
        for (var i = 0; i < count; i++)
            CollectAtkValueString(addon->AtkValues + i, texts);
    }

    private static void CollectAtkValueString(AtkValue* value, List<string> texts)
    {
        if (value == null)
            return;

        if (IsStringAtkValueType(value->Type))
        {
            AddText(texts, value->GetValueAsString());
            return;
        }

        if (value->Type is AtkValueType.Vector or AtkValueType.ManagedVector)
        {
            var count = Math.Min(value->GetVectorSize(), 100u);
            for (var i = 0u; i < count; i++)
                CollectAtkValueString(value->GetVectorValue(i), texts);
        }
    }

    private static bool IsStringAtkValueType(AtkValueType type)
    {
        return type is AtkValueType.String
            or AtkValueType.WideString
            or AtkValueType.String8
            or AtkValueType.ManagedString;
    }

    private static void CollectTextNodes(AtkResNode* node, List<string> texts)
    {
        if (node == null)
            return;

        if (node->Type == NodeType.Text)
            AddText(texts, ((AtkTextNode*)node)->NodeText.AsDalamudSeString().TextValue);

        var child = node->ChildNode;
        while (child != null)
        {
            CollectTextNodes(child, texts);
            child = child->PrevSiblingNode;
        }
    }

    private static string ReadTextNode(AtkTextNode* node)
    {
        return node == null
            ? string.Empty
            : CleanText(node->NodeText.AsDalamudSeString().TextValue);
    }

    private static void AddText(List<string> texts, string? text)
    {
        text = CleanText(text);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (texts.Any(existing => string.Equals(existing, text, StringComparison.Ordinal)))
            return;

        texts.Add(text);
    }

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(
            "\n",
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Trim();
    }

    private static List<string> WrapText(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            WrapParagraph(paragraph, maxWidth, lines);

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }

    private static void WrapParagraph(string paragraph, float maxWidth, List<string> lines)
    {
        paragraph = paragraph.Trim();
        if (string.IsNullOrEmpty(paragraph))
            return;

        var current = string.Empty;
        foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0)
            {
                AddWrappedWord(word, maxWidth, lines, ref current);
                continue;
            }

            var candidate = $"{current} {word}";
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = string.Empty;
            AddWrappedWord(word, maxWidth, lines, ref current);
        }

        if (current.Length > 0)
            lines.Add(current);
    }

    private static void AddWrappedWord(string word, float maxWidth, List<string> lines, ref string current)
    {
        if (ImGui.CalcTextSize(word).X <= maxWidth)
        {
            current = word;
            return;
        }

        var segment = string.Empty;
        foreach (var character in word)
        {
            var candidate = segment + character;
            if (segment.Length > 0 && ImGui.CalcTextSize(candidate).X > maxWidth)
            {
                lines.Add(segment);
                segment = character.ToString();
                continue;
            }

            segment = candidate;
        }

        current = segment;
    }

    private static bool TextEquivalent(string left, string right)
    {
        return string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void TrimEntries()
    {
        while (entries.Count > Configuration.MaxEntries)
            entries.RemoveAt(0);
    }

    private void ClearTranscript()
    {
        entries.Clear();
        choiceStates.Clear();
        voiceCaptureProbes.Clear();
        speakerColors.Clear();
        lastObservedTalkKey = null;
        lastTranscriptEntryKey = null;
        lastDialogSpeaker = null;
    }

    private Vector4 GetSpeakerColor(string speaker)
    {
        if (speakerColors.TryGetValue(speaker, out var color))
            return color;

        color = SpeakerColorPalette[speakerColors.Count % SpeakerColorPalette.Length];
        speakerColors[speaker] = color;
        return color;
    }

    private void StartVoiceCaptureProbe(int entryIndex)
    {
        var now = DateTimeOffset.Now;
        voiceCaptureProbes.Add(new VoiceCaptureProbe
        {
            EntryIndex = entryIndex,
            EndsAt = now + TimeSpan.FromSeconds(1),
            NextSampleAt = now
        });
    }

    private VoiceClipRef? TryCaptureVoiceClip()
    {
        var candidates = ReadActiveSoundCandidates();
        var candidate = TryFindPlayableVoiceClipCandidate(candidates);
        if (candidate != null)
            return new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber);

        candidate = TryFindAnyVoiceClipCandidate(candidates);
        if (candidate == null)
            return null;

        return new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber, CanReplay: false);
    }

    private static VoiceSoundCandidate? TryFindPlayableVoiceClipCandidate(IEnumerable<VoiceSoundCandidate> candidates)
    {
        foreach (var candidate in candidates
                     .Where(IsVoiceCandidate)
                     .Where(IsReliableVoiceCandidate)
                     .OrderByDescending(candidate => candidate.IsPlaying)
                     .ThenBy(candidate => candidate.Elapsed))
            return candidate;

        return null;
    }

    private static VoiceSoundCandidate? TryFindAnyVoiceClipCandidate(IEnumerable<VoiceSoundCandidate> candidates)
    {
        foreach (var candidate in candidates
                     .Where(IsVoiceCandidate)
                     .OrderByDescending(IsReliableVoiceCandidate)
                     .ThenByDescending(candidate => candidate.IsPlaying)
                     .ThenBy(candidate => candidate.Elapsed))
            return candidate;

        return null;
    }

    private static bool IsVoiceCandidate(VoiceSoundCandidate candidate)
    {
        if (candidate.SoundNumber != 0)
            return false;

        if (candidate.Volume <= 0.001f)
            return false;

        return candidate.Path.Contains("/sound/VOICE", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/sound/VOICEM", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/sound/VOICEF", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/vo_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReliableVoiceCandidate(VoiceSoundCandidate candidate)
    {
        return candidate.IsPositional || candidate.IsPlaying;
    }

    private void ReplayVoiceClip(VoiceClipRef voiceClip)
    {
        var soundManager = SoundManager.Instance();
        if (soundManager == null)
        {
            Log.Warning("Could not replay voice clip because SoundManager.Instance() was null.");
            return;
        }

        try
        {
            var soundData = soundManager->PlayCutsceneVoSound(voiceClip.Path);
            if (soundData == null)
            {
                soundData = soundManager->PlaySound(
                    voiceClip.Path,
                    1f,
                    0,
                    0f,
                    0f,
                    0f,
                    1f,
                    0,
                    voiceClip.SoundNumber,
                    true,
                    SoundVolumeCategory.BypassVolumeRules,
                    false,
                    -1,
                    false,
                    false,
                    false,
                    false);
            }

            if (soundData == null)
                return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to replay voice clip {Path}", voiceClip.Path);
        }
    }

    private void ProcessVoiceCaptureProbes()
    {
        if (voiceCaptureProbes.Count == 0)
            return;

        var now = DateTimeOffset.Now;
        for (var i = voiceCaptureProbes.Count - 1; i >= 0; i--)
        {
            var probe = voiceCaptureProbes[i];
            if (now > probe.EndsAt)
            {
                voiceCaptureProbes.RemoveAt(i);
                continue;
            }

            if (now < probe.NextSampleAt)
                continue;

            TryAttachVoiceClipFromSample(probe, ReadActiveSoundCandidates());
            probe.NextSampleAt = now + TimeSpan.FromMilliseconds(250);
        }
    }

    private void TryAttachVoiceClipFromSample(VoiceCaptureProbe probe, IReadOnlyList<VoiceSoundCandidate> candidates)
    {
        if (probe.EntryIndex < 0 || probe.EntryIndex >= entries.Count)
            return;

        var entry = entries[probe.EntryIndex];
        if (entry.VoiceClip?.CanReplay == true)
            return;

        var candidate = TryFindPlayableVoiceClipCandidate(candidates);
        if (candidate != null)
        {
            entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber) };
            return;
        }

        if (entry.VoiceClip != null)
            return;

        candidate = TryFindAnyVoiceClipCandidate(candidates);
        if (candidate == null)
            return;

        entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber, CanReplay: false) };
    }

    private List<VoiceSoundCandidate> ReadActiveSoundCandidates()
    {
        var candidates = new List<VoiceSoundCandidate>();
        var soundManager = SoundManager.Instance();
        if (soundManager == null)
            return candidates;

        var current = soundManager->ActiveSoundDataListHead;
        var visited = new HashSet<nint>();
        for (var i = 0; current != null && i < 256; i++)
        {
            var address = (nint)current;
            if (!visited.Add(address))
                break;

            TryAddSoundCandidate(current, candidates);
            current = (SoundData*)current->Next;
        }

        return candidates
            .OrderBy(candidate => candidate.Elapsed)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryAddSoundCandidate(SoundData* soundData, List<VoiceSoundCandidate> candidates)
    {
        if (soundData == null || !soundData->IsActive)
            return;

        var fileName = soundData->GetFileName();
        if (!fileName.HasValue)
            return;

        var path = fileName.ToString();
        if (string.IsNullOrWhiteSpace(path))
            return;

        candidates.Add(new VoiceSoundCandidate(
            path,
            soundData->GetSoundNumber(),
            soundData->GetElapsedTime(),
            soundData->GetVolume(),
            soundData->VolumeCategory,
            soundData->IsPlaying(),
            soundData->GetIsLoadingSoundResource(),
            soundData->GetIsPositional(),
            soundData->GetIsAutoReleaseEnabled(),
            soundData->GetMidiNote()));
    }

    private string BuildTranscriptText()
    {
        return string.Join(
            Environment.NewLine,
            entries.Select(entry => string.IsNullOrWhiteSpace(entry.Speaker)
                ? entry.Text
                : $"{entry.Speaker}: {entry.Text}"));
    }

    private void ClampConfiguration()
    {
        Configuration.MaxEntries = Math.Clamp(Configuration.MaxEntries, 25, 1000);
        Configuration.ButtonX = Math.Clamp(Configuration.ButtonX, 0f, 7680f);
        Configuration.ButtonY = Math.Clamp(Configuration.ButtonY, 0f, 4320f);
        Configuration.WindowWidth = Math.Clamp(Configuration.WindowWidth, 320f, 1200f);
        Configuration.WindowHeight = Math.Clamp(Configuration.WindowHeight, 240f, 900f);
    }

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
}
