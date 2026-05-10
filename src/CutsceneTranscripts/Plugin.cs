using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly WindowSystem windowSystem = new("CutsceneTranscripts");
    private readonly TranscriptWindow transcriptWindow;
    private readonly TranscriptOpenButtonAddon transcriptOpenButton;
    private readonly ConfigWindow configWindow;
    private readonly List<TranscriptEntry> entries = [];
    private readonly Dictionary<nint, ChoiceState> choiceStates = [];
    private readonly Dictionary<string, Vector4> speakerColors = [];
    private readonly List<VoiceCaptureProbe> voiceCaptureProbes = [];
    private bool lastCutsceneActive;
    private DateTimeOffset lastCutsceneActiveAt;
    private string? lastObservedTalkKey;
    private string? lastTranscriptEntryKey;
    private string? lastDialogSpeaker;
    private TalkWindowBounds? talkWindowBounds;
    private DateTimeOffset lastTalkWindowBoundsAt;
    private int transcriptRevision;

    internal static IPluginLog Log { get; private set; } = null!;
    internal Configuration Configuration { get; }

    /// <summary>
    /// Wires Dalamud services, addon listeners, commands, and plugin windows for the transcript overlay.
    /// </summary>
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IAddonLifecycle addonLifecycle,
        ICondition condition,
        IObjectTable objectTable,
        IGameGui gameGui,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.addonLifecycle = addonLifecycle;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        Log = pluginLog;

        KamiToolKitLibrary.Initialize(pluginInterface, "CutsceneTranscripts");

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);
        ClampConfiguration();
        transcriptWindow = new TranscriptWindow(this)
        {
            InternalName = "CutsceneTranscript",
            Title = "Cutscene Transcript",
            Size = new Vector2(Configuration.WindowWidth, Configuration.WindowHeight),
            ContentPadding = new Vector2(12f, 6f),
            RespectCloseAll = false,
            DisableClose = true,
            DisableCloseTransition = true,
        };
        transcriptOpenButton = new TranscriptOpenButtonAddon(this)
        {
            InternalName = "CutsceneTranscriptButton",
            Title = "Cutscene Transcript",
            Size = TranscriptOpenButtonAddon.ButtonSize,
            ContentPadding = Vector2.Zero,
            OpenWindowSoundEffectId = 0,
            RememberClosePosition = false,
            RespectCloseAll = false,
            DisableCloseTransition = true,
            OpenInBounds = false,
            CreateWindowNode = TranscriptOpenButtonAddon.CreateInvisibleWindowNode,
        };
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

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

    /// <summary>
    /// Unregisters all Dalamud hooks and command handlers owned by this plugin.
    /// </summary>
    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenTranscriptWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        transcriptOpenButton.Dispose();
        transcriptWindow.Dispose();
        windowSystem.RemoveAllWindows();
        KamiToolKitLibrary.Dispose();
        addonLifecycle.UnregisterListener(OnTalkPostUpdate, OnTalkFinalize, OnChoicePostUpdate, OnChoiceReceiveEvent, OnChoiceFinalize);
        commandManager.RemoveHandler(CommandName);
        commandManager.RemoveHandler(ShortCommandName);
    }

    /// <summary>
    /// Handles slash commands for toggling capture, clearing the transcript, and opening plugin windows.
    /// </summary>
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
                OpenConfigWindow();
                break;
            default:
                ToggleTranscriptWindow();
                break;
        }
    }

    private void ToggleTranscriptWindow()
    {
        if (transcriptWindow.IsShown)
            transcriptWindow.RequestSoftHide();
        else
            transcriptWindow.RequestOpen();
    }

    private void OpenTranscriptWindow()
    {
        transcriptWindow.RequestOpen();
    }

    private void OpenConfigWindow()
    {
        if (!IsCutsceneActive())
            configWindow.IsOpen = true;
    }

    /// <summary>
    /// Main frame tick: tracks cutscene transitions, draws eligible UI, and advances delayed voice capture probes.
    /// </summary>
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
                transcriptWindow.RequestOpen();
            }
            else
            {
                if (!Configuration.OpenTranscriptWhenCutsceneEnds)
                {
                    transcriptWindow.RequestSoftHide();
                }

                if (!Configuration.KeepLastTranscriptAfterCutscene)
                    ClearTranscript();
            }
        }

        if (cutsceneActive)
            lastCutsceneActiveAt = DateTimeOffset.Now;

        lastCutsceneActive = cutsceneActive;

        var showTranscriptOpenButton = Configuration.Enabled
            && Configuration.ShowButtonDuringCutscenes
            && cutsceneActive
            && entries.Count > 0
            && IsTalkWindowVisible()
            && !IsChoiceAddonVisible();
        UpdateTranscriptOpenButton(showTranscriptOpenButton);

        ProcessVoiceCaptureProbes();
        RefreshActiveVoiceReplayState();
        transcriptWindow.RefreshIfNeeded();
        windowSystem.Draw();
    }

    /// <summary>
    /// Combines Dalamud's cutscene UI state with game condition flags that cover different cutscene modes.
    /// </summary>
    private bool IsCutsceneActive()
    {
        return pluginInterface.UiBuilder.CutsceneActive
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78];
    }

    /// <summary>
    /// Normalizes persisted positional settings to keep windows/buttons recoverable across monitor layouts.
    /// </summary>
    private void ClampConfiguration()
    {
        Configuration.ButtonX = Math.Clamp(Configuration.ButtonX, 0f, 7680f);
        Configuration.ButtonY = Math.Clamp(Configuration.ButtonY, 0f, 4320f);
        Configuration.WindowWidth = Math.Clamp(Configuration.WindowWidth, 320f, 1200f);
        Configuration.WindowHeight = Math.Clamp(Configuration.WindowHeight, 240f, 900f);
    }

    /// <summary>
    /// Marks native transcript UI content stale after capture, replay availability, or clearing changes.
    /// </summary>
    private void MarkTranscriptChanged()
    {
        transcriptRevision++;
        transcriptWindow.MarkDirty();
    }

}
