using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Numerics;
using System.Speech.Synthesis;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chocobot;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/chocobot";
    private const string SubscriberName = "Chocobot";
    private static readonly TimeSpan SubscribeRetryInterval = TimeSpan.FromSeconds(2);

    internal static IPluginLog Log { get; private set; } = null!;
    internal Configuration Configuration { get; }

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly ITextureProvider textureProvider;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly ICallGateSubscriber<string, bool> createSubscriber;
    private readonly ICallGateSubscriber<string, bool> createLegacySubscriber;
    private readonly ICallGateSubscriber<string, bool> unsubscribe;
    private readonly ICallGateSubscriber<Uri?> getServerUri;
    private readonly ICallGateSubscriber<JObject, bool> iinactReceiver;
    private readonly ICallGateProvider<JObject, bool> eventProvider;
    private readonly ConcurrentQueue<JObject> pendingEvents = new();
    private readonly List<TriggerDefinition> triggers = [];
    private readonly List<TimelineDefinition> timelines = [];
    private readonly List<ActiveAlert> alerts = [];
    private readonly Dictionary<string, DateTime> lastTriggerFireAtUtc = [];
    private readonly Dictionary<string, ActiveTimeline> activeTimelines = [];
    private readonly Dictionary<string, bool> encounterState = [];
    private readonly List<string> recentStateChanges = [];
    private readonly List<string> recentStateDiagnostics = [];
    private readonly HashSet<string> scheduledTimelineCues = [];
    private readonly ISharedImmediateTexture? mascotTexture;
    private CancellationTokenSource? webSocketCancellation;
    private Task? webSocketTask;
    private SpeechSynthesizer? speechSynthesizer;
    private bool subscribed;
    private bool usingLegacySubscriber;
    private bool webSocketConnected;
    private bool configOpen;
    private DateTime nextSubscribeAttempt = DateTime.MinValue;
    private string ipcStatus = "IINACT IPC: waiting for IINACT";
    private string? ipcLastError;
    private string webSocketStatus = "WebSocket: waiting for IINACT";
    private string? webSocketLastError;
    private string ttsStatus = "TTS: not initialized";
    private string? ttsLastError;
    private string? ttsVoiceName;
    private string? ttsBackend;
    private int ttsVoiceCount;
    private string? triggerLoadError;
    private string? timelineLoadError;
    private string? currentZone;
    private string? primaryPlayerName;
    private string? primaryPlayerJob;
    private string? primaryPlayerRole;
    private string? lastEventType;
    private string? lastLogLine;
    private string? overlayHiddenReason;
    private DateTime? lastEventAt;
    private DateTime? lastTriggerAt;
    private int receivedEventCount;
    private int webSocketEventCount;
    private int matchedTriggerCount;
    private int timelineSyncCount;
    private int timelineCueCount;
    private bool? lastCombatDataActive;
    private double lastCombatDurationSeconds;

    private sealed record UpcomingTimelineRow(string Text, DateTime StartsAtUtc, DateTime AtUtc);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        ITextureProvider textureProvider,
        IObjectTable objectTable,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.textureProvider = textureProvider;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.condition = condition;
        this.gameGui = gameGui;
        Log = pluginLog;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);
        ClampConfiguration();
        LoadTriggers();
        mascotTexture = LoadMascotTexture();

        createSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("IINACT.CreateSubscriber");
        createLegacySubscriber = pluginInterface.GetIpcSubscriber<string, bool>("IINACT.CreateLegacySubscriber");
        unsubscribe = pluginInterface.GetIpcSubscriber<string, bool>("IINACT.Unsubscribe");
        getServerUri = pluginInterface.GetIpcSubscriber<Uri?>("IINACT.Server.Uri");
        iinactReceiver = pluginInterface.GetIpcSubscriber<JObject, bool>($"IINACT.IpcProvider.{SubscriberName}");
        eventProvider = pluginInterface.GetIpcProvider<JObject, bool>(SubscriberName);
        eventProvider.RegisterFunc(ReceiveIinactEvent);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Chocobot callout settings. Use '/chocobot test' to show a test alert."
        });

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfigWindow;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;

        if (!Configuration.UseWebSocketTransport)
            TrySubscribe();
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenConfigWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        commandManager.RemoveHandler(CommandName);
        StopWebSocketClient();
        speechSynthesizer?.Dispose();
        TryUnsubscribe();
        eventProvider.UnregisterFunc();
    }

    private ISharedImmediateTexture? LoadMascotTexture()
    {
        var iconPath = Path.Combine(pluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "chocobot-icon.png");
        return File.Exists(iconPath)
            ? textureProvider.GetFromFile(iconPath)
            : null;
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
            case "lock":
                Configuration.Locked = !Configuration.Locked;
                Configuration.Save();
                break;
            case "reload":
                LoadTriggers();
                break;
            case "reconnect":
                ResetIinactConnection();
                configOpen = true;
                break;
            case "test":
                AddAlert("test", "Chocobot systems online", TimeSpan.FromSeconds(6));
                break;
            case "testtts":
                SpeakAlert("Chocobot text to speech test");
                configOpen = true;
                break;
            default:
                configOpen = !configOpen;
                break;
        }
    }

    private void Draw()
    {
        RefreshPrimaryPlayerFromDalamud();

        if (Configuration.UseWebSocketTransport)
        {
            EnsureWebSocketClient();
            if (webSocketConnected && subscribed)
                TryUnsubscribe();
            else if (!webSocketConnected && !subscribed)
                TrySubscribe();
        }
        else
        {
            StopWebSocketClient();
            if (!subscribed)
                TrySubscribe();
        }

        DrainPendingEvents();
        ScheduleTimelineCues();
        PruneAlerts();
        SpeakDueAlerts();

        overlayHiddenReason = GetOverlayHiddenReason();
        if (Configuration.Enabled && overlayHiddenReason is null && (alerts.Count > 0 || Configuration.ShowInactiveWindow))
            DrawAlertWindow();

        DrawConfigWindow();
    }

    private string? GetOverlayHiddenReason()
    {
        if (!Configuration.Enabled)
            return null;

        if (!clientState.IsLoggedIn)
            return "Overlay hidden because no character is logged in";

        if (gameGui.GameUiHidden)
            return "Overlay hidden because the game UI is hidden";

        if (pluginInterface.UiBuilder.CutsceneActive
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78])
        {
            return "Overlay hidden because a cutscene is active";
        }

        return null;
    }

    private void OpenConfigWindow()
    {
        configOpen = true;
    }

    private bool ReceiveIinactEvent(JObject data)
    {
        receivedEventCount++;
        lastEventAt = DateTime.Now;
        ipcLastError = null;
        pendingEvents.Enqueue(data);
        return true;
    }

    private void ReceiveWebSocketEvent(JObject data)
    {
        receivedEventCount++;
        webSocketEventCount++;
        lastEventAt = DateTime.Now;
        webSocketLastError = null;
        pendingEvents.Enqueue(data);
    }

    private void DrainPendingEvents()
    {
        while (pendingEvents.TryDequeue(out var data))
            ProcessIinactEvent(data);
    }

    private void ProcessIinactEvent(JObject data)
    {
        if (IsLegacyBroadcast(data))
        {
            ProcessLegacyBroadcast(data);
            return;
        }

        var type = Text(data["type"]);
        lastEventType = type;

        switch (type)
        {
            case "ChangeZone":
                currentZone = Text(data["zoneName"]) ?? Text(data["zone"]);
                ResetTimelines();
                break;
            case "ChangePrimaryPlayer":
                ProcessPrimaryPlayer(data);
                break;
            case "CombatData":
                ProcessCombatData(data);
                break;
            case "LogLine":
                ProcessLogLine(ExtractLogLine(data));
                break;
            default:
                if (Configuration.TriggerOnAllLogLines)
                {
                    var line = ExtractLogLine(data);
                    if (!string.IsNullOrWhiteSpace(line))
                        ProcessLogLine(line);
                }
                break;
        }
    }

    private void ProcessLegacyBroadcast(JObject data)
    {
        var msgType = Text(data["msgtype"]);
        var msg = data["msg"];

        lastEventType = msgType;

        switch (msgType)
        {
            case "ChangeZone":
                if (AsObject(msg) is { } zoneData)
                    currentZone = Text(zoneData["zoneName"]) ?? Text(zoneData["zone"]);
                else
                    currentZone = Text(msg);
                ResetTimelines();
                break;
            case "ChangePrimaryPlayer":
            case "SendCharName":
                ProcessPrimaryPlayer(AsObject(msg) ?? data);
                break;
            case "CombatData":
                ProcessCombatData(AsObject(msg) ?? data);
                break;
            case "LogLine":
                ProcessLogLine(ExtractLogLine(AsObject(msg) ?? data));
                break;
            default:
                if (Configuration.TriggerOnAllLogLines)
                    ProcessLogLine(ExtractLogLine(AsObject(msg) ?? data));
                break;
        }
    }

    private void ProcessLogLine(string line)
    {
        MatchTriggers(TriggerSource.LogLine, line);
        SyncTimelines(line);
    }

    private void ProcessCombatData(JObject data)
    {
        var payload = FindCombatDataPayload(data);
        if (payload is null)
            return;

        var encounter = AsObject(payload["Encounter"] ?? payload["encounter"]);
        if (encounter is null)
            return;

        var isActiveText = Text(data["isActive"]) ?? Text(payload["isActive"]);
        var isActive = string.Equals(isActiveText, "true", StringComparison.OrdinalIgnoreCase);
        var duration = ParseDuration(Text(encounter["duration"]) ?? Text(encounter["DURATION"]));
        var durationSeconds = duration.TotalSeconds;
        var durationRolledBack = durationSeconds + 1 < lastCombatDurationSeconds;
        var combatEnded = lastCombatDataActive != false && !isActive;

        if (durationRolledBack || combatEnded)
            ResetTimelines();

        lastCombatDataActive = isActive;
        lastCombatDurationSeconds = durationSeconds;
    }

    private void MatchTriggers(TriggerSource source, string line)
    {
        if (!Configuration.Enabled || string.IsNullOrWhiteSpace(line))
            return;

        lastLogLine = line;
        var netLogEvent = NetLogEvent.Parse(line);

        foreach (var trigger in triggers.Where(trigger => trigger.Source == source))
        {
            if (!ZoneMatches(trigger.Zone))
                continue;

            var match = trigger.CompiledRegex?.Match(line);
            if (trigger.CompiledRegex is not null && match is not { Success: true })
                continue;

            if (trigger.HasStructuredCriteria && !StructuredTriggerMatches(trigger, netLogEvent, match is { Success: true }))
            {
                if (trigger.StateUpdates.Count > 0 && match is { Success: true })
                    RecordStateDiagnostic($"{trigger.Id}: raw matched but structured did not match event={netLogEvent.EventType ?? "unknown"} id={netLogEvent.Id ?? "unknown"}");
                else if (trigger.StateConditions.Count > 0 && match is { Success: true } && StateConditionsFailed(trigger) is { } stateMismatch)
                    RecordStateDiagnostic($"{trigger.Id}: {stateMismatch}");
                continue;
            }

            if (trigger.TargetSelf && !EventTargetsPrimaryPlayer(netLogEvent))
                continue;

            if (trigger.TargetNotSelf && EventTargetsPrimaryPlayer(netLogEvent))
                continue;

            ApplyStateUpdates(trigger);
            if (trigger.Silent)
            {
                matchedTriggerCount++;
                lastTriggerAt = DateTime.Now;
                continue;
            }

            var now = DateTime.UtcNow;
            if (IsSuppressed(trigger, now))
                continue;

            var text = ResolveText(trigger, match, netLogEvent);
            AddAlert(
                trigger.Id,
                text,
                TimeSpan.FromSeconds(trigger.DurationSeconds),
                TimeSpan.FromSeconds(trigger.CountdownSeconds),
                trigger.Speak);
            lastTriggerFireAtUtc[trigger.Id] = now;
            matchedTriggerCount++;
            lastTriggerAt = DateTime.Now;
        }
    }

    private bool StructuredTriggerMatches(TriggerDefinition trigger, NetLogEvent netLogEvent, bool rawPatternMatched)
    {
        if (!string.IsNullOrWhiteSpace(trigger.EventType)
            && !EventTypeMatches(trigger.EventType, netLogEvent.EventType))
        {
            if (!CanUseRawFallback(trigger, rawPatternMatched, netLogEvent))
                return false;
        }

        if (trigger.NormalizedIds.Count > 0
            && (netLogEvent.NormalizedId is null || !trigger.NormalizedIds.Contains(netLogEvent.NormalizedId)))
        {
            if (!CanUseRawFallback(trigger, rawPatternMatched, netLogEvent))
                return false;
        }

        foreach (var (key, expected) in trigger.StateConditions)
        {
            if (!encounterState.TryGetValue(key, out var actual))
                actual = false;

            if (actual != expected)
                return false;
        }

        if (!RoleConditionsMatch(trigger))
            return false;

        return true;
    }

    private static bool CanUseRawFallback(TriggerDefinition trigger, bool rawPatternMatched, NetLogEvent netLogEvent)
    {
        if (!rawPatternMatched)
            return false;

        if (trigger.StateUpdates.Count == 0 && trigger.StateConditions.Count == 0)
            return false;

        return netLogEvent.EventType is null || netLogEvent.NormalizedId is null;
    }

    private static bool EventTypeMatches(string triggerEventType, string? observedEventType)
    {
        if (string.Equals(triggerEventType, observedEventType, StringComparison.OrdinalIgnoreCase))
            return true;

        // IINACT/OverlayPlugin streams can expose some cactbot StartsUsing IDs
        // only on the resolved Ability line. Keep the structured ID check, but
        // avoid regressing triggers that previously worked via raw ID matching.
        return string.Equals(triggerEventType, "StartsUsing", StringComparison.OrdinalIgnoreCase)
               && string.Equals(observedEventType, "Ability", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyStateUpdates(TriggerDefinition trigger)
    {
        foreach (var (key, value) in trigger.StateUpdates)
        {
            if (!encounterState.TryGetValue(key, out var previous) || previous != value)
            {
                recentStateChanges.Insert(0, $"{FormatTime(DateTime.Now)} {trigger.Id}: {key}={value}");
                if (recentStateChanges.Count > 8)
                    recentStateChanges.RemoveRange(8, recentStateChanges.Count - 8);
            }

            encounterState[key] = value;
        }
    }

    private string? StateConditionsFailed(TriggerDefinition trigger)
    {
        foreach (var (key, expected) in trigger.StateConditions)
        {
            if (!encounterState.TryGetValue(key, out var actual))
                actual = false;

            if (actual != expected)
                return $"state {key} expected {expected} actual {actual}";
        }

        return null;
    }

    private void RecordStateDiagnostic(string message)
    {
        recentStateDiagnostics.Insert(0, $"{FormatTime(DateTime.Now)} {message}");
        if (recentStateDiagnostics.Count > 8)
            recentStateDiagnostics.RemoveRange(8, recentStateDiagnostics.Count - 8);
    }

    private void ProcessPrimaryPlayer(JObject data)
    {
        primaryPlayerName = Text(data["charName"])
                            ?? Text(data["name"])
                            ?? Text(data["playerName"])
                            ?? primaryPlayerName;
        primaryPlayerJob = NormalizeJob(Text(data["job"]) ?? Text(data["Job"]) ?? Text(data["jobName"]) ?? primaryPlayerJob);
        primaryPlayerRole = NormalizeRole(Text(data["role"]) ?? Text(data["Role"]) ?? primaryPlayerRole);
    }

    private void RefreshPrimaryPlayerFromDalamud()
    {
        try
        {
            var localPlayer = objectTable.LocalPlayer;
            var name = TextFromValue(localPlayer?.Name);
            if (!string.IsNullOrWhiteSpace(name))
                primaryPlayerName = name;
            RefreshPrimaryJobFromDalamud(localPlayer);
        }
        catch
        {
            // Best-effort fallback; IINACT events can still provide the player name.
        }
    }

    private void RefreshPrimaryJobFromDalamud(object? localPlayer)
    {
        if (localPlayer is null)
            return;

        var classJob = localPlayer.GetType().GetProperty("ClassJob")?.GetValue(localPlayer);
        var rowId = TryGetRowId(classJob);
        if (rowId is null or 0)
            return;

        primaryPlayerJob = JobFromClassJob(rowId.Value);
        primaryPlayerRole = RoleFromClassJob(rowId.Value);
    }

    private static uint? TryGetRowId(object? value)
    {
        if (value is null)
            return null;

        if (TryConvertUInt(value.GetType().GetProperty("RowId")?.GetValue(value), out var rowId))
            return rowId;

        var nested = value.GetType().GetProperty("Value")?.GetValue(value);
        if (TryConvertUInt(nested?.GetType().GetProperty("RowId")?.GetValue(nested), out rowId))
            return rowId;

        return null;
    }

    private static bool TryConvertUInt(object? value, out uint result)
    {
        switch (value)
        {
            case byte byteValue:
                result = byteValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case int intValue when intValue >= 0:
                result = (uint)intValue;
                return true;
            case string text when uint.TryParse(text, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string? JobFromClassJob(uint rowId)
    {
        return rowId switch
        {
            1 => "GLA",
            2 => "PGL",
            3 => "MRD",
            4 => "LNC",
            5 => "ARC",
            6 => "CNJ",
            7 => "THM",
            19 => "PLD",
            20 => "MNK",
            21 => "WAR",
            22 => "DRG",
            23 => "BRD",
            24 => "WHM",
            25 => "BLM",
            26 => "ACN",
            27 => "SMN",
            28 => "SCH",
            29 => "ROG",
            30 => "NIN",
            31 => "MCH",
            32 => "DRK",
            33 => "AST",
            34 => "SAM",
            35 => "RDM",
            36 => "BLU",
            37 => "GNB",
            38 => "DNC",
            39 => "RPR",
            40 => "SGE",
            41 => "VPR",
            42 => "PCT",
            _ => null
        };
    }

    private static string? RoleFromClassJob(uint rowId)
    {
        return rowId switch
        {
            1 or 3 or 19 or 21 or 32 or 37 => "tank",
            6 or 24 or 28 or 33 or 40 => "healer",
            2 or 4 or 5 or 7 or 20 or 22 or 23 or 25 or 26 or 27 or 29 or 30 or 31 or 34 or 35 or 36 or 38 or 39 or 41 or 42 => "dps",
            _ => null
        };
    }

    private bool RoleConditionsMatch(TriggerDefinition trigger)
    {
        if (trigger.Roles.Count > 0 || trigger.Jobs.Count > 0)
        {
            var roleMatches = primaryPlayerRole is not null && trigger.Roles.Contains(primaryPlayerRole);
            var jobMatches = primaryPlayerJob is not null && trigger.Jobs.Contains(primaryPlayerJob);

            if (!roleMatches && !jobMatches)
                return false;
        }

        if (trigger.NotRoles.Count > 0
            && primaryPlayerRole is not null
            && trigger.NotRoles.Contains(primaryPlayerRole))
            return false;

        if (trigger.NotJobs.Count > 0
            && primaryPlayerJob is not null
            && trigger.NotJobs.Contains(primaryPlayerJob))
            return false;

        return true;
    }

    private static string? NormalizeRole(string? role)
    {
        return string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToLowerInvariant();
    }

    private static string? NormalizeJob(string? job)
    {
        return string.IsNullOrWhiteSpace(job) ? null : job.Trim().ToUpperInvariant();
    }

    private bool EventTargetsPrimaryPlayer(NetLogEvent netLogEvent)
    {
        if (string.IsNullOrWhiteSpace(primaryPlayerName))
            return false;

        if (!string.IsNullOrWhiteSpace(netLogEvent.TargetName))
            return string.Equals(netLogEvent.TargetName, primaryPlayerName, StringComparison.OrdinalIgnoreCase);

        return netLogEvent.RawLine.Contains(primaryPlayerName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSuppressed(TriggerDefinition trigger, DateTime now)
    {
        if (trigger.SuppressSeconds <= 0)
            return false;

        if (!lastTriggerFireAtUtc.TryGetValue(trigger.Id, out var lastFireAtUtc))
            return false;

        return now - lastFireAtUtc < TimeSpan.FromSeconds(trigger.SuppressSeconds);
    }

    private bool ZoneMatches(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
            return true;

        if (string.IsNullOrWhiteSpace(currentZone))
            return false;

        return string.Equals(zone, currentZone, StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeZoneName(zone), NormalizeZoneName(currentZone), StringComparison.Ordinal);
    }

    private static string NormalizeZoneName(string zone)
    {
        var normalized = zone.Trim().ToLowerInvariant();
        if (normalized.StartsWith("the ", StringComparison.Ordinal))
            normalized = normalized[4..];

        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private void SyncTimelines(string line)
    {
        if (!Configuration.Enabled || string.IsNullOrWhiteSpace(line))
            return;

        var now = DateTime.UtcNow;
        foreach (var timeline in timelines)
        {
            if (!ZoneMatches(timeline.Zone))
                continue;

            var matchingSyncs = timeline.Syncs
                .Where(sync => sync.CompiledRegex?.IsMatch(line) == true)
                .ToList();
            if (matchingSyncs.Count == 0)
                continue;

            var sync = matchingSyncs[0];
            if (activeTimelines.TryGetValue(timeline.Id, out var active))
            {
                sync = matchingSyncs
                    .OrderBy(candidate => ((now - TimeSpan.FromSeconds(candidate.TimeSeconds)) - active.AnchorUtc).Duration())
                    .First();

                var anchor = now - TimeSpan.FromSeconds(sync.TimeSeconds);
                var drift = (active.AnchorUtc - anchor).Duration();
                if (drift > TimeSpan.FromSeconds(2))
                {
                    active.Resync(anchor);
                    RemoveScheduledTimelineCues(timeline.Id);
                }
            }
            else
            {
                var anchor = now - TimeSpan.FromSeconds(sync.TimeSeconds);
                activeTimelines[timeline.Id] = new ActiveTimeline(timeline, anchor);
            }

            timelineSyncCount++;
            return;
        }
    }

    private void ScheduleTimelineCues()
    {
        if (!Configuration.Enabled || activeTimelines.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var active in activeTimelines.Values.ToList())
        {
            if (!ZoneMatches(active.Definition.Zone))
                continue;

            foreach (var cue in active.Definition.Cues)
            {
                var cueAt = active.AnchorUtc + TimeSpan.FromSeconds(cue.TimeSeconds);
                var startAt = cueAt - TimeSpan.FromSeconds(cue.BeforeSeconds);
                if (now < startAt || now > cueAt + TimeSpan.FromSeconds(cue.DurationSeconds))
                    continue;

                var scheduleKey = $"{active.Definition.Id}:{cue.Id}:{cueAt.Ticks}";
                if (!scheduledTimelineCues.Add(scheduleKey))
                    continue;

                AddAlert(
                    scheduleKey,
                    cue.AlertText,
                    TimeSpan.FromSeconds(cue.DurationSeconds),
                    cueAt > now ? cueAt - now : TimeSpan.Zero,
                    true);
                timelineCueCount++;
            }
        }
    }

    private void RemoveScheduledTimelineCues(string timelineId)
    {
        scheduledTimelineCues.RemoveWhere(key => key.StartsWith($"{timelineId}:", StringComparison.Ordinal));
        alerts.RemoveAll(alert => alert.TriggerId.StartsWith($"{timelineId}:", StringComparison.Ordinal));
    }

    private void ResetTimelines()
    {
        activeTimelines.Clear();
        encounterState.Clear();
        recentStateChanges.Clear();
        recentStateDiagnostics.Clear();
        scheduledTimelineCues.Clear();
        alerts.RemoveAll(alert => alert.TriggerId.StartsWith("cactbot-timeline-", StringComparison.Ordinal));
        lastCombatDataActive = null;
        lastCombatDurationSeconds = 0;
    }

    private static string ResolveText(TriggerDefinition trigger, System.Text.RegularExpressions.Match? match, NetLogEvent netLogEvent)
    {
        var text = trigger.AlertText ?? trigger.InfoText ?? trigger.Id;
        if (match is null)
            return ResolveEventText(text, netLogEvent);

        foreach (var groupName in trigger.CompiledRegex?.GetGroupNames() ?? [])
        {
            if (int.TryParse(groupName, out var groupNumber))
            {
                text = text.Replace($"${groupNumber}", match.Groups[groupNumber].Value, StringComparison.Ordinal);
                continue;
            }

            text = text.Replace($"${groupName}", match.Groups[groupName].Value, StringComparison.Ordinal);
        }

        return ResolveEventText(text, netLogEvent);
    }

    private static string ResolveEventText(string text, NetLogEvent netLogEvent)
    {
        if (!string.IsNullOrWhiteSpace(netLogEvent.SourceName))
            text = text.Replace("$source", netLogEvent.SourceName, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(netLogEvent.TargetName))
            text = text.Replace("$target", netLogEvent.TargetName, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(netLogEvent.Id))
            text = text.Replace("$id", netLogEvent.Id, StringComparison.Ordinal);

        return text;
    }

    private void AddAlert(string triggerId, string text, TimeSpan duration, TimeSpan? countdown = null, bool speak = true)
    {
        alerts.RemoveAll(alert => string.Equals(alert.TriggerId, triggerId, StringComparison.Ordinal));
        var countdownValue = countdown ?? TimeSpan.Zero;
        var alert = new ActiveAlert(triggerId, text, DateTime.UtcNow, duration, countdownValue, speak);
        alerts.Insert(0, alert);
        if (alerts.Count > Configuration.MaxAlerts)
            alerts.RemoveRange(Configuration.MaxAlerts, alerts.Count - Configuration.MaxAlerts);

        if (speak && countdownValue <= TimeSpan.Zero)
        {
            SpeakAlert(text);
            alert.MarkSpoken();
        }
    }

    private void SpeakAlert(string text)
    {
        if (!Configuration.SpeakAlerts)
            return;

        try
        {
            if (ShouldPreferExternalTts() && TrySpeakWithExternalCommand(text))
                return;

            try
            {
                SpeakWithSystemSpeech(text);
                return;
            }
            catch (Exception ex)
            {
                var windowsTtsError = ex.Message;
                if (TrySpeakWithExternalCommand(text))
                {
                    ttsLastError = $"Windows TTS unavailable, using external command: {windowsTtsError}";
                    return;
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            ttsLastError = ex.Message;
            speechSynthesizer?.Dispose();
            speechSynthesizer = null;
            Log.Debug(ex, "Failed to speak Chocobot alert.");
        }
    }

    private void SpeakWithSystemSpeech(string text)
    {
        speechSynthesizer ??= CreateSpeechSynthesizer();
        speechSynthesizer.Rate = Configuration.TtsRate;
        speechSynthesizer.Volume = Configuration.TtsVolume;
        speechSynthesizer.SpeakAsyncCancelAll();
        speechSynthesizer.SpeakAsync(text);
        ttsBackend = "Windows SAPI";
        ttsLastError = null;
    }

    private bool TrySpeakWithExternalCommand(string text)
    {
        var errors = new List<string>();
        foreach (var candidate in GetExternalTtsCandidates(text))
        {
            try
            {
                using var process = Process.Start(candidate);
                if (process is null)
                {
                    errors.Add($"{candidate.FileName}: did not start");
                    continue;
                }

                ttsBackend = candidate.FileName;
                ttsStatus = $"TTS: launched ({Path.GetFileName(candidate.FileName)})";
                ttsVoiceName = null;
                ttsVoiceCount = 0;
                ttsLastError = null;
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate.FileName}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
            ttsLastError = string.Join("; ", errors);

        return false;
    }

    private static IEnumerable<ProcessStartInfo> GetExternalTtsCandidates(string text)
    {
        foreach (var command in GetExternalTtsCommands())
        {
            var info = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(text);
            yield return info;
        }
    }

    private static IEnumerable<string> GetExternalTtsCommands()
    {
        yield return "/usr/bin/spd-say";
        yield return "/usr/local/bin/spd-say";
        yield return "Z:\\usr\\bin\\spd-say";
        yield return "Z:\\usr\\local\\bin\\spd-say";
        yield return "spd-say";
        yield return "/usr/bin/espeak-ng";
        yield return "/usr/local/bin/espeak-ng";
        yield return "Z:\\usr\\bin\\espeak-ng";
        yield return "Z:\\usr\\local\\bin\\espeak-ng";
        yield return "espeak-ng";
        yield return "/usr/bin/espeak";
        yield return "Z:\\usr\\bin\\espeak";
        yield return "espeak";
    }

    private bool ShouldPreferExternalTts()
    {
        return Configuration.PreferExternalTts
               || !OperatingSystem.IsWindows()
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINEPREFIX"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XL_WINEONLINUX"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
    }

    private SpeechSynthesizer CreateSpeechSynthesizer()
    {
        var synthesizer = new SpeechSynthesizer();
        var voices = synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).ToList();
        ttsVoiceCount = voices.Count;
        if (voices.Count == 0)
            throw new InvalidOperationException("No enabled Windows text-to-speech voices are available.");

        synthesizer.SetOutputToDefaultAudioDevice();
        ttsVoiceName = synthesizer.Voice.Name;
        ttsBackend = "Windows SAPI";
        ttsStatus = $"TTS: ready ({ttsVoiceName})";
        synthesizer.SpeakStarted += (_, _) =>
        {
            ttsStatus = $"TTS: speaking ({ttsVoiceName})";
        };
        synthesizer.SpeakCompleted += (_, args) =>
        {
            if (args.Cancelled)
                ttsStatus = $"TTS: cancelled ({ttsVoiceName})";
            else if (args.Error is not null)
            {
                ttsStatus = "TTS: failed";
                ttsLastError = args.Error.Message;
            }
            else
                ttsStatus = $"TTS: completed ({ttsVoiceName})";
        };

        return synthesizer;
    }

    private void PruneAlerts()
    {
        var now = DateTime.UtcNow;
        alerts.RemoveAll(alert => alert.ExpiresAtUtc <= now);
    }

    private void SpeakDueAlerts()
    {
        var now = DateTime.UtcNow;
        foreach (var alert in alerts.Where(alert => alert.ShouldSpeak(now)))
        {
            SpeakAlert(alert.Text);
            alert.MarkSpoken();
        }
    }

    private void DrawAlertWindow()
    {
        var scale = ImGuiHelpers.GlobalScale * Configuration.AlertScale;
        var now = DateTime.UtcNow;
        var liveAlerts = alerts.Where(alert => alert.IsLive(now)).Take(Configuration.MaxAlerts).ToList();
        var pendingAlerts = alerts
            .Where(alert => alert.IsPending(now))
            .OrderBy(alert => alert.CueAtUtc)
            .Take(Configuration.MaxAlerts)
            .ToList();
        var timelineRows = GetUpcomingTimelineRows(now, Math.Max(0, Configuration.MaxAlerts - pendingAlerts.Count));
        var upcomingCount = pendingAlerts.Count + timelineRows.Count;
        var showReadyPanel = Configuration.ShowInactiveWindow && IsEncounterActiveForReadyPanel(pendingAlerts, timelineRows);

        if (liveAlerts.Count > 0)
            DrawTopScreenAlerts(scale, liveAlerts, now);

        if (!showReadyPanel)
            return;

        var flags = ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoBackground;

        if (Configuration.Locked)
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (Configuration.ClickThrough)
            flags |= ImGuiWindowFlags.NoInputs;

        var windowWidth = upcomingCount > 0 ? 460 * scale : 470 * scale;
        var windowHeight = upcomingCount > 0
            ? 54 * scale + upcomingCount * 38 * scale
            : 68 * scale;
        ImGui.SetNextWindowSize(new Vector2(windowWidth, windowHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8 * scale);

        var visible = true;
        if (!ImGui.Begin("Chocobot Alerts##Chocobot", ref visible, flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        if (upcomingCount > 0)
            DrawUpcomingPanel(scale, pendingAlerts, timelineRows, now);
        else
            DrawInactivePanel(scale);

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private bool IsEncounterActiveForReadyPanel(
        IReadOnlyCollection<ActiveAlert> pendingAlerts,
        IReadOnlyCollection<UpcomingTimelineRow> timelineRows)
    {
        return lastCombatDataActive == true
               || pendingAlerts.Count > 0
               || timelineRows.Count > 0
               || activeTimelines.Count > 0;
    }

    private void DrawTopScreenAlerts(float scale, IReadOnlyList<ActiveAlert> liveAlerts, DateTime now)
    {
        var viewport = ImGui.GetMainViewport();
        var drawList = ImGui.GetForegroundDrawList();
        var count = Math.Min(Configuration.MaxAlerts, liveAlerts.Count);

        for (var i = 0; i < count; i++)
        {
            var alert = liveAlerts[i];
            var fontSize = MathF.Round(ImGui.GetFontSize() * (i == 0 ? 2.45f : 1.65f) * scale);
            var progress = alert.LiveProgress(now);
            var maxWidth = viewport.Size.X * (i == 0 ? 0.78f : 0.64f);
            var text = FitTextScaled(alert.Text, maxWidth, fontSize);
            var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
            var y = viewport.Pos.Y + viewport.Size.Y * 0.17f + i * 50 * scale;
            var iconSize = i == 0 ? 46 * scale : 0;
            var contentWidth = textSize.X + (iconSize > 0 ? iconSize + 16 * scale : 0);
            var cardWidth = Math.Min(viewport.Size.X * 0.84f, contentWidth + 44 * scale);
            var cardHeight = Math.Max(textSize.Y + 22 * scale, i == 0 ? 62 * scale : textSize.Y + 14 * scale);
            var cardPos = PixelSnap(new Vector2(viewport.Pos.X + (viewport.Size.X - cardWidth) * 0.5f, y - 10 * scale));
            var pos = PixelSnap(new Vector2(cardPos.X + (cardWidth - contentWidth) * 0.5f + (iconSize > 0 ? iconSize + 16 * scale : 0), cardPos.Y + (cardHeight - textSize.Y) * 0.5f - 1 * scale));
            var alpha = Math.Clamp(1f - i * 0.16f, 0.45f, 1f);
            var textColor = i == 0
                ? new Vector4(1.0f, 0.96f, 0.72f, alpha)
                : new Vector4(0.92f, 0.96f, 1.0f, alpha);
            var outlineColor = new Vector4(0.015f, 0.018f, 0.02f, 0.96f * alpha);

            drawList.AddRectFilled(
                cardPos + new Vector2(4 * scale, 6 * scale),
                cardPos + new Vector2(cardWidth + 4 * scale, cardHeight + 6 * scale),
                ImGui.GetColorU32(new Vector4(0, 0, 0, 0.34f * alpha)),
                14 * scale);
            drawList.AddRectFilled(
                cardPos,
                cardPos + new Vector2(cardWidth, cardHeight),
                ImGui.GetColorU32(new Vector4(0.035f, 0.045f, 0.052f, 0.68f * alpha)),
                14 * scale);
            drawList.AddRect(
                cardPos,
                cardPos + new Vector2(cardWidth, cardHeight),
                ImGui.GetColorU32(new Vector4(1.0f, 0.74f, 0.28f, 0.50f * alpha)),
                14 * scale,
                ImDrawFlags.None,
                MathF.Max(1.5f, 2 * scale));
            if (i == 0 && TryDrawMascot(drawList, cardPos + new Vector2(17 * scale, (cardHeight - iconSize) * 0.5f), iconSize, alpha))
            {
                drawList.AddCircle(
                    cardPos + new Vector2(17 * scale + iconSize * 0.5f, cardHeight * 0.5f),
                    iconSize * 0.52f,
                    ImGui.GetColorU32(new Vector4(1.0f, 0.80f, 0.34f, 0.36f * alpha)),
                    40,
                    MathF.Max(1, 1.5f * scale));
            }

            DrawOutlinedText(drawList, pos, text, fontSize, textColor, outlineColor, MathF.Round(2 * scale));

            if (i != 0)
                continue;

            var barWidth = cardWidth - 34 * scale;
            var barPos = PixelSnap(new Vector2(cardPos.X + 17 * scale, cardPos.Y + cardHeight - 9 * scale));
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(barWidth, 4 * scale),
                ImGui.GetColorU32(new Vector4(0, 0, 0, 0.48f)),
                2 * scale);
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(barWidth * (1 - progress), 4 * scale),
                ImGui.GetColorU32(new Vector4(1.0f, 0.72f, 0.22f, 0.95f)),
                2 * scale);
        }
    }

    private void DrawInactivePanel(float scale)
    {
        var size = new Vector2(470 * scale, 62 * scale);
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        DrawPanel(drawList, pos, size, scale, new Vector4(0.045f, 0.052f, 0.058f, Configuration.Opacity));
        DrawChocobotFrame(drawList, pos, size, scale, Configuration.Opacity);
        DrawMascotWatermark(drawList, pos + new Vector2(size.X - 78 * scale, size.Y - 78 * scale), 72 * scale, 0.13f * Configuration.Opacity);
        DrawMascotBadge(drawList, pos + new Vector2(14 * scale, 16 * scale), 30 * scale, 1);
        DrawTextShadow(drawList, pos + new Vector2(54 * scale, 22 * scale), "Chocobot ready", new Vector4(0.94f, 0.96f, 0.92f, 1), scale);
        drawList.AddLine(
            pos + new Vector2(54 * scale, 44 * scale),
            pos + new Vector2(size.X - 20 * scale, 44 * scale),
            ImGui.GetColorU32(new Vector4(1.0f, 0.74f, 0.28f, 0.30f * Configuration.Opacity)),
            MathF.Max(1, scale));
        ImGui.Dummy(size);
    }

    private List<UpcomingTimelineRow> GetUpcomingTimelineRows(DateTime now, int maxRows)
    {
        if (maxRows <= 0 || activeTimelines.Count == 0)
            return [];

        return activeTimelines.Values
            .Where(active => ZoneMatches(active.Definition.Zone))
            .SelectMany(active =>
            {
                var orderedEntries = active.Definition.Entries
                    .OrderBy(entry => entry.TimeSeconds)
                    .ToList();
                return orderedEntries.Select((entry, index) =>
                {
                    var previousTime = index > 0
                        ? orderedEntries[index - 1].TimeSeconds
                        : Math.Max(0, entry.TimeSeconds - 15);
                    return new UpcomingTimelineRow(
                        entry.Text,
                        active.AnchorUtc + TimeSpan.FromSeconds(previousTime),
                        active.AnchorUtc + TimeSpan.FromSeconds(entry.TimeSeconds));
                });
            })
            .Where(row => row.AtUtc > now)
            .OrderBy(row => row.AtUtc)
            .Take(maxRows)
            .ToList();
    }

    private void DrawUpcomingPanel(float scale, IReadOnlyList<ActiveAlert> pendingAlerts, IReadOnlyList<UpcomingTimelineRow> timelineRows, DateTime now)
    {
        var rowCount = pendingAlerts.Count + timelineRows.Count;
        var size = new Vector2(460 * scale, 54 * scale + rowCount * 38 * scale);
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        DrawPanel(drawList, pos, size, scale, new Vector4(0.045f, 0.052f, 0.058f, Configuration.Opacity));
        DrawChocobotFrame(drawList, pos, size, scale, Configuration.Opacity);
        DrawMascotWatermark(drawList, pos + new Vector2(size.X - 82 * scale, size.Y - 82 * scale), 74 * scale, 0.16f * Configuration.Opacity);
        DrawMascotBadge(drawList, pos + new Vector2(12 * scale, 10 * scale), 30 * scale, 1);
        DrawTextShadow(drawList, pos + new Vector2(50 * scale, 16 * scale), "Upcoming", new Vector4(0.94f, 0.96f, 0.92f, 1), scale);
        drawList.AddLine(
            pos + new Vector2(50 * scale, 38 * scale),
            pos + new Vector2(size.X - 18 * scale, 38 * scale),
            ImGui.GetColorU32(new Vector4(1.0f, 0.74f, 0.28f, 0.38f * Configuration.Opacity)),
            MathF.Max(1, scale));

        var rowIndex = 0;
        for (var i = 0; i < pendingAlerts.Count; i++)
        {
            var alert = pendingAlerts[i];
            var rowY = 52 * scale + rowIndex * 38 * scale;
            var text = FitTextScaled(alert.Text, size.X - 138 * scale, ImGui.GetFontSize());
            var remaining = $"{alert.CountdownRemaining(now).TotalSeconds:0.0}s";
            var textColor = i == 0
                ? new Vector4(1.0f, 0.96f, 0.72f, 1)
                : new Vector4(0.88f, 0.92f, 0.95f, 1);
            var timeColor = i == 0
                ? new Vector4(1.0f, 0.74f, 0.28f, 1)
                : new Vector4(0.70f, 0.78f, 0.84f, 1);

            DrawUpcomingRowChrome(drawList, pos + new Vector2(12 * scale, rowY - 4 * scale), new Vector2(size.X - 24 * scale, 30 * scale), scale, i == 0, new Vector4(1.0f, 0.70f, 0.18f, 1), Configuration.Opacity);
            DrawTextShadow(drawList, pos + new Vector2(32 * scale, rowY), text, textColor, scale);
            DrawTextRightShadow(drawList, pos.X + size.X - 18 * scale, pos.Y + rowY, remaining, timeColor, scale);

            var barPos = pos + new Vector2(32 * scale, rowY + 22 * scale);
            var barSize = new Vector2(size.X - 64 * scale, 4 * scale);
            var remainingProgress = 1 - alert.CountdownProgress(now);
            drawList.AddRectFilled(
                barPos,
                barPos + barSize,
                ImGui.GetColorU32(new Vector4(1, 1, 1, 0.10f)),
                2 * scale);
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(barSize.X * remainingProgress, barSize.Y),
                ImGui.GetColorU32(new Vector4(1.0f, 0.72f, 0.22f, 0.95f)),
                2 * scale);
            rowIndex++;
        }

        for (var i = 0; i < timelineRows.Count; i++)
        {
            var row = timelineRows[i];
            var rowY = 52 * scale + rowIndex * 38 * scale;
            var text = FitTextScaled(row.Text, size.X - 138 * scale, ImGui.GetFontSize());
            var remaining = row.AtUtc - now;
            var remainingText = FormatCountdown(remaining);
            var textColor = rowIndex == 0
                ? new Vector4(1.0f, 0.96f, 0.72f, 1)
                : new Vector4(0.88f, 0.92f, 0.95f, 1);
            var timeColor = rowIndex == 0
                ? new Vector4(1.0f, 0.74f, 0.28f, 1)
                : new Vector4(0.70f, 0.78f, 0.84f, 1);

            DrawUpcomingRowChrome(drawList, pos + new Vector2(12 * scale, rowY - 4 * scale), new Vector2(size.X - 24 * scale, 30 * scale), scale, rowIndex == 0, new Vector4(0.42f, 0.78f, 1.0f, 1), Configuration.Opacity);
            DrawTextShadow(drawList, pos + new Vector2(32 * scale, rowY), text, textColor, scale);
            DrawTextRightShadow(drawList, pos.X + size.X - 18 * scale, pos.Y + rowY, remainingText, timeColor, scale);

            var barPos = pos + new Vector2(32 * scale, rowY + 22 * scale);
            var barSize = new Vector2(size.X - 64 * scale, 4 * scale);
            var timelineProgress = TimelineProgress(row, now);
            drawList.AddRectFilled(
                barPos,
                barPos + barSize,
                ImGui.GetColorU32(new Vector4(1, 1, 1, 0.10f)),
                2 * scale);
            drawList.AddRectFilled(
                barPos,
                barPos + new Vector2(barSize.X * (1 - timelineProgress), barSize.Y),
                ImGui.GetColorU32(new Vector4(0.42f, 0.78f, 1.0f, 0.82f)),
                2 * scale);
            rowIndex++;
        }

        ImGui.Dummy(size);
    }

    private static float TimelineProgress(UpcomingTimelineRow row, DateTime now)
    {
        var total = row.AtUtc - row.StartsAtUtc;
        if (total.TotalMilliseconds <= 0)
            return 1;

        var elapsed = now - row.StartsAtUtc;
        return Math.Clamp((float)(elapsed.TotalMilliseconds / total.TotalMilliseconds), 0f, 1f);
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 60)
            return $"{remaining.TotalSeconds:0.0}s";

        return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    private static void DrawPanel(ImDrawListPtr drawList, Vector2 pos, Vector2 size, float scale, Vector4 color)
    {
        var opacity = Math.Clamp(color.W, 0f, 1f);
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color), 8 * scale);
        drawList.AddRect(pos, pos + size, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.16f * opacity)), 8 * scale);
        drawList.AddLine(
            pos + new Vector2(10 * scale, 1 * scale),
            pos + new Vector2(size.X - 10 * scale, 1 * scale),
            ImGui.GetColorU32(new Vector4(1.0f, 0.76f, 0.30f, 0.22f * opacity)),
            MathF.Max(1, scale));
    }

    private void DrawChocobotFrame(ImDrawListPtr drawList, Vector2 pos, Vector2 size, float scale, float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        var outer = ImGui.GetColorU32(new Vector4(0.92f, 0.96f, 1.0f, 0.30f * opacity));
        var inner = ImGui.GetColorU32(new Vector4(0.42f, 0.78f, 1.0f, 0.42f * opacity));
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.40f * opacity));
        var inset = 6 * scale;
        var rounding = 12 * scale;

        drawList.AddRect(pos + new Vector2(3 * scale, 4 * scale), pos + size + new Vector2(3 * scale, 4 * scale), shadow, 12 * scale, ImDrawFlags.None, MathF.Max(2, 3 * scale));
        drawList.AddRect(
            pos + new Vector2(inset),
            pos + size - new Vector2(inset),
            outer,
            rounding,
            ImDrawFlags.None,
            MathF.Max(1.5f, 2 * scale));
        drawList.AddRect(
            pos + new Vector2(inset + 4 * scale),
            pos + size - new Vector2(inset + 4 * scale),
            inner,
            MathF.Max(1, rounding - 4 * scale),
            ImDrawFlags.None,
            MathF.Max(1, scale));
    }

    private static void DrawUpcomingRowChrome(ImDrawListPtr drawList, Vector2 pos, Vector2 size, float scale, bool highlighted, Vector4 accent, float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        var bgAlpha = (highlighted ? 0.18f : 0.10f) * opacity;
        var borderAlpha = (highlighted ? 0.30f : 0.16f) * opacity;
        drawList.AddRectFilled(
            pos,
            pos + size,
            ImGui.GetColorU32(new Vector4(0.10f, 0.12f, 0.13f, bgAlpha)),
            6 * scale);
        drawList.AddRect(
            pos,
            pos + size,
            ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, borderAlpha)),
            6 * scale);
        drawList.AddRectFilled(
            pos,
            pos + new Vector2(4 * scale, size.Y),
            ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, 0.72f * opacity)),
            6 * scale);
        drawList.AddCircleFilled(
            pos + new Vector2(12 * scale, size.Y * 0.5f),
            3.5f * scale,
            ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, 0.95f)));
    }

    private void DrawMascotBadge(ImDrawListPtr drawList, Vector2 pos, float size, float alpha)
    {
        var center = pos + new Vector2(size * 0.5f);
        drawList.AddCircleFilled(center, size * 0.56f, ImGui.GetColorU32(new Vector4(0.09f, 0.075f, 0.035f, 0.74f * alpha)), 40);
        drawList.AddCircle(center, size * 0.58f, ImGui.GetColorU32(new Vector4(1.0f, 0.77f, 0.22f, 0.58f * alpha)), 40, MathF.Max(1, 1.5f * (size / 30f)));
        TryDrawMascot(drawList, pos, size, alpha);
    }

    private void DrawMascotWatermark(ImDrawListPtr drawList, Vector2 pos, float size, float alpha)
    {
        TryDrawMascot(drawList, pos, size, alpha);
    }

    private bool TryDrawMascot(ImDrawListPtr drawList, Vector2 pos, float size, float alpha)
    {
        if (mascotTexture is null || !mascotTexture.TryGetWrap(out IDalamudTextureWrap? wrap, out _))
            return false;

        drawList.AddImage(
            wrap.Handle,
            pos,
            pos + new Vector2(size, size),
            Vector2.Zero,
            Vector2.One,
            ImGui.GetColorU32(new Vector4(1, 1, 1, Math.Clamp(alpha, 0f, 1f))));
        return true;
    }

    private static void DrawText(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color)
    {
        drawList.AddText(pos, ImGui.GetColorU32(color), text);
    }

    private static void DrawTextShadow(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color, float scale)
    {
        drawList.AddText(pos + new Vector2(MathF.Max(1, scale), MathF.Max(1, scale)), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.72f)), text);
        DrawText(drawList, pos, text, color);
    }

    private static void DrawTextRight(ImDrawListPtr drawList, float rightX, float y, string text, Vector4 color)
    {
        var textSize = ImGui.CalcTextSize(text);
        DrawText(drawList, new Vector2(rightX - textSize.X, y), text, color);
    }

    private static void DrawTextRightShadow(ImDrawListPtr drawList, float rightX, float y, string text, Vector4 color, float scale)
    {
        var textSize = ImGui.CalcTextSize(text);
        DrawTextShadow(drawList, new Vector2(rightX - textSize.X, y), text, color, scale);
    }

    private static void DrawOutlinedText(ImDrawListPtr drawList, Vector2 pos, string text, float fontSize, Vector4 color, Vector4 outlineColor, float outline)
    {
        var font = ImGui.GetFont();
        var outlineU32 = ImGui.GetColorU32(outlineColor);
        var colorU32 = ImGui.GetColorU32(color);
        drawList.AddText(font, fontSize, pos + new Vector2(outline, outline + 1), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.62f)), text);
        foreach (var offset in new[]
                 {
                     new Vector2(-outline, 0),
                     new Vector2(outline, 0),
                     new Vector2(0, -outline),
                     new Vector2(0, outline),
                 })
        {
            drawList.AddText(font, fontSize, pos + offset, outlineU32, text);
        }

        drawList.AddText(font, fontSize, pos, colorU32, text);
    }

    private static Vector2 PixelSnap(Vector2 value)
    {
        return new Vector2(MathF.Round(value.X), MathF.Round(value.Y));
    }

    private static string FitTextScaled(string text, float maxWidth, float fontSize)
    {
        var scale = fontSize / ImGui.GetFontSize();
        if (ImGui.CalcTextSize(text).X * scale <= maxWidth)
            return text;

        const string ellipsis = "...";
        var fitted = text;
        while (fitted.Length > 0 && ImGui.CalcTextSize(fitted + ellipsis).X * scale > maxWidth)
            fitted = fitted[..^1];

        return fitted.Length == 0 ? ellipsis : fitted + ellipsis;
    }

    private void EnsureWebSocketClient()
    {
        if (webSocketTask is { IsCompleted: false })
            return;

        webSocketCancellation?.Dispose();
        webSocketCancellation = new CancellationTokenSource();
        webSocketTask = Task.Run(() => RunWebSocketClientAsync(webSocketCancellation.Token));
    }

    private void StopWebSocketClient()
    {
        webSocketCancellation?.Cancel();
        webSocketCancellation = null;
        webSocketTask = null;
        webSocketConnected = false;
        webSocketStatus = "WebSocket: disabled";
    }

    private async Task RunWebSocketClientAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var uri = GetMiniParseWebSocketUri();
                webSocketStatus = $"WebSocket: connecting to {uri}";

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(uri, cancellationToken);
                webSocketConnected = true;
                webSocketStatus = $"WebSocket: connected to {uri}";
                webSocketLastError = null;

                await ReceiveWebSocketMessagesAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                webSocketLastError = ex.Message;
                webSocketStatus = "WebSocket: waiting for IINACT server";
                Log.Debug(ex, "IINACT WebSocket connection failed.");
            }
            finally
            {
                webSocketConnected = false;
            }

            try
            {
                await Task.Delay(SubscribeRetryInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReceiveWebSocketMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var message = Encoding.UTF8.GetString(stream.ToArray());
            try
            {
                ReceiveWebSocketEvent(JObject.Parse(message));
            }
            catch (JsonException ex)
            {
                webSocketLastError = ex.Message;
                Log.Debug(ex, "IINACT WebSocket sent invalid JSON.");
            }
        }
    }

    private Uri GetMiniParseWebSocketUri()
    {
        Uri? serverUri = null;
        try
        {
            serverUri = getServerUri.InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "IINACT server URI IPC is not available.");
        }

        serverUri ??= new Uri("ws://127.0.0.1:10501");
        var builder = new UriBuilder(serverUri)
        {
            Host = serverUri.Host is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : serverUri.Host,
            Path = "MiniParse",
            Query = string.Empty,
        };

        if (builder.Scheme != "ws" && builder.Scheme != "wss")
            builder.Scheme = "ws";

        return builder.Uri;
    }

    private void DrawConfigWindow()
    {
        if (!configOpen)
            return;

        if (!ImGui.Begin("Chocobot", ref configOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var changed = false;
        changed |= Checkbox("Enabled", Configuration.Enabled, value => Configuration.Enabled = value);
        changed |= Checkbox("Locked", Configuration.Locked, value => Configuration.Locked = value);
        changed |= Checkbox("Click through", Configuration.ClickThrough, value => Configuration.ClickThrough = value);
        changed |= Checkbox("Use WebSocket transport", Configuration.UseWebSocketTransport, value => Configuration.UseWebSocketTransport = value);
        changed |= Checkbox("Show ready panel", Configuration.ShowInactiveWindow, value => Configuration.ShowInactiveWindow = value);
        changed |= Checkbox("Speak alerts", Configuration.SpeakAlerts, value => Configuration.SpeakAlerts = value);
        changed |= Checkbox("Prefer external TTS", Configuration.PreferExternalTts, value => Configuration.PreferExternalTts = value);
        changed |= Checkbox("Try triggers on all events", Configuration.TriggerOnAllLogLines, value => Configuration.TriggerOnAllLogLines = value);
        changed |= Checkbox("Debug details", Configuration.ShowDebugWindow, value => Configuration.ShowDebugWindow = value);
        changed |= SliderInt("Max alerts", Configuration.MaxAlerts, 1, 10, value => Configuration.MaxAlerts = value);
        changed |= SliderInt("TTS volume", Configuration.TtsVolume, 0, 100, value => Configuration.TtsVolume = value);
        changed |= SliderInt("TTS rate", Configuration.TtsRate, -10, 10, value => Configuration.TtsRate = value);
        changed |= SliderFloat("Opacity", Configuration.Opacity, 0f, 1f, "%.2f", value => Configuration.Opacity = value);
        changed |= SliderFloat("Scale", Configuration.AlertScale, 0.75f, 1.75f, "%.2f", value => Configuration.AlertScale = value);

        if (ImGui.Button("Test alert"))
            AddAlert("test", "Chocobot systems online", TimeSpan.FromSeconds(6));
        ImGui.SameLine();
        if (ImGui.Button("Test TTS"))
            SpeakAlert("Chocobot text to speech test");
        ImGui.SameLine();
        if (ImGui.Button("Reload triggers"))
            LoadTriggers();
        ImGui.SameLine();
        if (ImGui.Button("Reconnect IINACT"))
            ResetIinactConnection();

        ImGui.Separator();
        ImGui.TextUnformatted(webSocketStatus);
        ImGui.TextUnformatted(ipcStatus);
        ImGui.TextUnformatted(ttsStatus);
        if (ttsBackend is not null)
            ImGui.TextUnformatted($"TTS backend: {ttsBackend}");
        if (ttsVoiceCount > 0)
            ImGui.TextUnformatted($"TTS voices: {ttsVoiceCount}");
        if (ttsVoiceName is not null)
            ImGui.TextUnformatted($"TTS voice: {ttsVoiceName}");
        if (webSocketLastError is not null)
            ImGui.TextWrapped($"WebSocket error: {webSocketLastError}");
        if (ipcLastError is not null)
            ImGui.TextWrapped($"IPC error: {ipcLastError}");
        if (ttsLastError is not null)
            ImGui.TextWrapped($"TTS error: {ttsLastError}");
        if (triggerLoadError is not null)
            ImGui.TextWrapped($"Trigger error: {triggerLoadError}");
        if (timelineLoadError is not null)
            ImGui.TextWrapped($"Timeline error: {timelineLoadError}");
        ImGui.TextUnformatted($"Triggers loaded: {triggers.Count}");
        ImGui.TextUnformatted($"Timelines loaded: {timelines.Count}");
        ImGui.TextUnformatted($"Current zone: {currentZone ?? "unknown"}");
        ImGui.TextUnformatted($"Player: {primaryPlayerName ?? "unknown"}");
        ImGui.TextUnformatted($"Job/role: {primaryPlayerJob ?? "unknown"} / {primaryPlayerRole ?? "unknown"}");
        ImGui.TextUnformatted($"State flags: {encounterState.Count}");
        if (overlayHiddenReason is not null)
            ImGui.TextUnformatted(overlayHiddenReason);
        ImGui.TextUnformatted($"State update triggers: {triggers.Count(trigger => trigger.StateUpdates.Count > 0)}");

        if (Configuration.ShowDebugWindow)
        {
            ImGui.TextUnformatted($"Events received: {receivedEventCount}");
            ImGui.TextUnformatted($"WebSocket events: {webSocketEventCount}");
            ImGui.TextUnformatted($"Triggers matched: {matchedTriggerCount}");
            ImGui.TextUnformatted($"Timeline syncs: {timelineSyncCount}");
            ImGui.TextUnformatted($"Timeline cues: {timelineCueCount}");
            ImGui.TextUnformatted($"Active timelines: {activeTimelines.Count}");
            ImGui.TextUnformatted($"Combat active: {lastCombatDataActive?.ToString() ?? "unknown"}");
            ImGui.TextUnformatted($"Combat duration: {lastCombatDurationSeconds:0.0}s");
            if (encounterState.Count > 0)
            {
                ImGui.TextWrapped($"State: {string.Join(", ", encounterState.Select(pair => $"{pair.Key}={pair.Value}"))}");
            }

            foreach (var stateChange in recentStateChanges)
                ImGui.TextUnformatted(stateChange);

            foreach (var diagnostic in recentStateDiagnostics)
                ImGui.TextWrapped(diagnostic);

            ImGui.TextUnformatted($"Last event type: {lastEventType ?? "unknown"}");
            ImGui.TextUnformatted($"Last event: {FormatTime(lastEventAt)}");
            ImGui.TextUnformatted($"Last trigger: {FormatTime(lastTriggerAt)}");
        }

        if (changed)
        {
            ClampConfiguration();
            Configuration.Save();
        }

        ImGui.End();
    }

    private void TrySubscribe()
    {
        if (subscribed)
            return;

        var now = DateTime.UtcNow;
        if (now < nextSubscribeAttempt)
            return;

        nextSubscribeAttempt = now + SubscribeRetryInterval;
        ipcStatus = "IINACT IPC: connecting";
        ipcLastError = null;

        if (Configuration.UseLegacyIpcFirst && TrySubscribeLegacy())
            return;

        if (TrySubscribeModern())
            return;

        if (!Configuration.UseLegacyIpcFirst)
            TrySubscribeLegacy();
    }

    private bool TrySubscribeLegacy()
    {
        try
        {
            if (!createLegacySubscriber.InvokeFunc(SubscriberName))
            {
                TryRemoveIinactSubscriber();
                if (!createLegacySubscriber.InvokeFunc(SubscriberName))
                {
                    ipcStatus = "IINACT IPC: waiting for legacy subscriber";
                    return false;
                }
            }

            subscribed = true;
            usingLegacySubscriber = true;
            ipcStatus = "IINACT IPC: connected (legacy)";
            return true;
        }
        catch (Exception ex)
        {
            ipcStatus = "IINACT IPC: waiting for legacy subscriber";
            ipcLastError = ex.Message;
            Log.Debug(ex, "IINACT legacy subscriber is not available yet.");
            return false;
        }
    }

    private bool TrySubscribeModern()
    {
        try
        {
            if (!createSubscriber.InvokeFunc(SubscriberName))
            {
                TryRemoveIinactSubscriber();
                if (!createSubscriber.InvokeFunc(SubscriberName))
                {
                    ipcStatus = "IINACT IPC: waiting for subscriber";
                    return false;
                }
            }

            if (!SendToIinact(new JObject
                {
                    ["call"] = "subscribe",
                    ["events"] = new JArray("LogLine", "ChangeZone", "CombatData", "ChangePrimaryPlayer")
                }))
            {
                TryRemoveIinactSubscriber();
                ipcStatus = "IINACT IPC: subscribe request rejected";
                return false;
            }

            subscribed = true;
            usingLegacySubscriber = false;
            ipcStatus = "IINACT IPC: connected";
            return true;
        }
        catch (Exception ex)
        {
            TryRemoveIinactSubscriber();
            ipcStatus = "IINACT IPC: waiting for subscriber";
            ipcLastError = ex.Message;
            Log.Debug(ex, "IINACT subscriber is not available yet.");
            return false;
        }
    }

    private void TryUnsubscribe()
    {
        if (!subscribed)
            return;

        try
        {
            if (!usingLegacySubscriber)
            {
                SendToIinact(new JObject
                {
                    ["call"] = "unsubscribe",
                    ["events"] = new JArray("LogLine", "ChangeZone", "CombatData", "ChangePrimaryPlayer")
                });
            }

            TryRemoveIinactSubscriber();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to unsubscribe from IINACT.");
        }

        subscribed = false;
        usingLegacySubscriber = false;
        ipcStatus = "IINACT IPC: disconnected";
    }

    private void TryRemoveIinactSubscriber()
    {
        try
        {
            unsubscribe.InvokeFunc(SubscriberName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to remove IINACT subscriber.");
        }
    }

    private bool SendToIinact(JObject data)
    {
        return iinactReceiver.InvokeFunc(data);
    }

    private void ResetIinactConnection()
    {
        TryUnsubscribe();
        TryRemoveIinactSubscriber();
        subscribed = false;
        usingLegacySubscriber = false;
        webSocketEventCount = 0;
        nextSubscribeAttempt = DateTime.MinValue;
        ipcLastError = null;
        webSocketLastError = null;
        StopWebSocketClient();
        TrySubscribe();
    }

    private void LoadTriggers()
    {
        triggers.Clear();
        timelines.Clear();
        activeTimelines.Clear();
        encounterState.Clear();
        recentStateChanges.Clear();
        recentStateDiagnostics.Clear();
        scheduledTimelineCues.Clear();
        lastTriggerFireAtUtc.Clear();
        triggerLoadError = null;
        timelineLoadError = null;
        var assetsPath = Path.Combine(pluginInterface.AssemblyLocation.Directory!.FullName, "Assets");

        try
        {
            if (!Directory.Exists(assetsPath))
            {
                triggerLoadError = $"Missing trigger directory: {assetsPath}";
                return;
            }

            var errors = new List<string>();
            var files = Directory.GetFiles(assetsPath, "*.json")
                .OrderBy(path => Path.GetFileName(path).Equals("triggers.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(Path.GetFileName)
                .ToList();
            if (files.Count == 0)
            {
                triggerLoadError = $"No trigger files found in: {assetsPath}";
                return;
            }

            foreach (var path in files.Where(path => !Path.GetFileName(path).Contains("timeline", StringComparison.OrdinalIgnoreCase)))
                LoadTriggerFile(path, errors);

            if (errors.Count > 0)
                triggerLoadError = string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? $" and {errors.Count - 3} more" : string.Empty);

            LoadTimelines(assetsPath);
        }
        catch (Exception ex)
        {
            triggerLoadError = ex.Message;
            Log.Warning(ex, "Failed to load Chocobot triggers.");
        }
    }

    private void LoadTimelines(string assetsPath)
    {
        var errors = new List<string>();
        foreach (var path in Directory.GetFiles(assetsPath, "*timeline*.json").OrderBy(Path.GetFileName))
        {
            try
            {
                var loaded = JsonConvert.DeserializeObject<List<TimelineDefinition>>(File.ReadAllText(path)) ?? [];
                foreach (var timeline in loaded)
                {
                    if (timeline.Compile(out var error))
                        timelines.Add(timeline);
                    else if (error is not null)
                        errors.Add($"{Path.GetFileName(path)}: {error}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                Log.Warning(ex, "Failed to load Chocobot timeline file {Path}.", path);
            }
        }

        if (errors.Count > 0)
            timelineLoadError = string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? $" and {errors.Count - 3} more" : string.Empty);
    }

    private void LoadTriggerFile(string path, List<string> errors)
    {
        try
        {
            var loaded = JsonConvert.DeserializeObject<List<TriggerDefinition>>(File.ReadAllText(path)) ?? [];
            foreach (var trigger in loaded)
            {
                if (trigger.Compile(out var error))
                    triggers.Add(trigger);
                else if (error is not null)
                    errors.Add($"{Path.GetFileName(path)}: {error}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            Log.Warning(ex, "Failed to load Chocobot trigger file {Path}.", path);
        }
    }

    private void ClampConfiguration()
    {
        Configuration.MaxAlerts = Math.Clamp(Configuration.MaxAlerts, 1, 10);
        Configuration.TtsVolume = Math.Clamp(Configuration.TtsVolume, 0, 100);
        Configuration.TtsRate = Math.Clamp(Configuration.TtsRate, -10, 10);
        Configuration.Opacity = Math.Clamp(Configuration.Opacity, 0f, 1f);
        Configuration.AlertScale = Math.Clamp(Configuration.AlertScale, 0.75f, 1.75f);
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

    private static bool SliderFloat(string label, float value, float min, float max, string format, Action<float> setter)
    {
        if (!ImGui.SliderFloat(label, ref value, min, max, format))
            return false;

        setter(value);
        return true;
    }

    private static bool IsLegacyBroadcast(JObject data)
    {
        return string.Equals(Text(data["type"]), "broadcast", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLogLine(JObject data)
    {
        if (data["line"] is JArray array)
            return string.Join('|', array.Select(Text));

        if (AsObject(data["msg"]) is { } msgObject)
            return ExtractLogLine(msgObject);

        if (data["msg"] is JArray msgArray)
            return string.Join('|', msgArray.Select(Text));

        return Text(data["line"])
               ?? Text(data["rawLine"])
               ?? Text(data["rawLineString"])
               ?? Text(data["logLine"])
               ?? Text(data["message"])
               ?? Text(data["msg"])
               ?? data.ToString(Formatting.None);
    }

    private static string DescribeEvent(JObject data)
    {
        if (data["line"] is JArray array)
            return string.Join('|', array.Select(Text));

        return Text(data["line"])
               ?? Text(data["rawLine"])
               ?? Text(data["rawLineString"])
               ?? Text(data["logLine"])
               ?? Text(data["msg"])
               ?? data.ToString(Formatting.None);
    }

    private static JObject? FindCombatDataPayload(JObject data, int depth = 0)
    {
        if (depth > 4)
            return null;

        if ((data["Encounter"] is not null || data["encounter"] is not null)
            && (data["Combatant"] is not null || data["combatant"] is not null || data["combatants"] is not null))
            return data;

        foreach (var propertyName in new[] { "msg", "data", "payload", "detail", "event", "Event", "args" })
        {
            if (AsObject(data[propertyName]) is { } nested
                && FindCombatDataPayload(nested, depth + 1) is { } payload)
                return payload;
        }

        foreach (var property in data.Properties())
        {
            if (AsObject(property.Value) is { } nested
                && FindCombatDataPayload(nested, depth + 1) is { } payload)
                return payload;
        }

        return null;
    }

    private static TimeSpan ParseDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return TimeSpan.Zero;

        var parts = duration.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var minutes)
            && int.TryParse(parts[1], out var seconds))
            return new TimeSpan(0, minutes, seconds);

        if (parts.Length == 3
            && int.TryParse(parts[0], out var hours)
            && int.TryParse(parts[1], out minutes)
            && int.TryParse(parts[2], out seconds))
            return new TimeSpan(hours, minutes, seconds);

        return TimeSpan.Zero;
    }

    private static JObject? AsObject(JToken? token)
    {
        return token switch
        {
            JObject obj => obj,
            JValue { Value: string text } => TryParseObject(text),
            _ => null
        };
    }

    private static JObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JObject.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JToken? token)
    {
        return token switch
        {
            null => null,
            JValue value => value.Value?.ToString(),
            _ => token.ToString(Formatting.None)
        };
    }

    private static string? TextFromValue(object? value)
    {
        if (value is null)
            return null;

        if (value is string text)
            return text;

        var textValueProperty = value.GetType().GetProperty("TextValue");
        if (textValueProperty?.GetValue(value) is string textValue)
            return textValue;

        return value.ToString();
    }

    private static string FormatTime(DateTime? time)
    {
        return time is null ? "never" : time.Value.ToString("HH:mm:ss");
    }
}
