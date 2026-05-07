using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using IINACTNativeOverlay.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IINACTNativeOverlay;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/iinactoverlay";
    private const string SubscriberName = "IINACTNativeOverlay";

    internal IDalamudPluginInterface PluginInterface { get; }
    internal ICommandManager CommandManager { get; }
    internal ITextureProvider TextureProvider { get; }
    internal IObjectTable ObjectTable { get; }
    internal IPartyList PartyList { get; }
    internal ITargetManager TargetManager { get; }
    internal IPlayerState PlayerState { get; }
    internal IClientState ClientState { get; }
    internal ICondition Condition { get; }
    internal IGameGui GameGui { get; }
    internal static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }

    private readonly ICallGateSubscriber<string, bool> createSubscriber;
    private readonly ICallGateSubscriber<string, bool> createLegacySubscriber;
    private readonly ICallGateSubscriber<string, bool> unsubscribe;
    private readonly ICallGateSubscriber<Uri?> getServerUri;
    private readonly ICallGateSubscriber<JObject, bool> iinactReceiver;
    private readonly ICallGateProvider<JObject, bool> eventProvider;
    private readonly DpsMeterOverlay overlay;
    private readonly ConcurrentQueue<JObject> pendingEvents = new();
    private CancellationTokenSource? webSocketCancellation;
    private Task? webSocketTask;
    private bool subscribed;
    private bool usingLegacySubscriber;
    private bool configOpen;
    private DateTime nextSubscribeAttempt = DateTime.MinValue;
    private DateTime? lastEventAt;
    private int receivedEventCount;
    private string ipcStatus = "IINACT IPC: waiting for IINACT";
    private string? ipcLastError;
    private bool webSocketConnected;
    private DateTime? lastWebSocketEventAt;
    private int webSocketEventCount;
    private string webSocketStatus = "WebSocket: waiting for IINACT";
    private string? webSocketLastError;
    private string? overlayHiddenReason;

    private static readonly TimeSpan SubscribeRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WebSocketRetryInterval = TimeSpan.FromSeconds(2);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        ITextureProvider textureProvider,
        IObjectTable objectTable,
        IPartyList partyList,
        ITargetManager targetManager,
        IPlayerState playerState,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog pluginLog)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        TextureProvider = textureProvider;
        ObjectTable = objectTable;
        PartyList = partyList;
        TargetManager = targetManager;
        PlayerState = playerState;
        ClientState = clientState;
        Condition = condition;
        GameGui = gameGui;
        Log = pluginLog;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        createSubscriber = PluginInterface.GetIpcSubscriber<string, bool>("IINACT.CreateSubscriber");
        createLegacySubscriber = PluginInterface.GetIpcSubscriber<string, bool>("IINACT.CreateLegacySubscriber");
        unsubscribe = PluginInterface.GetIpcSubscriber<string, bool>("IINACT.Unsubscribe");
        getServerUri = PluginInterface.GetIpcSubscriber<Uri?>("IINACT.Server.Uri");
        iinactReceiver = PluginInterface.GetIpcSubscriber<JObject, bool>($"IINACT.IpcProvider.{SubscriberName}");
        eventProvider = PluginInterface.GetIpcProvider<JObject, bool>(SubscriberName);
        eventProvider.RegisterFunc(ReceiveIinactEvent);

        overlay = new DpsMeterOverlay(this);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles the IINACT native DPS overlay."
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;

        if (!Configuration.UseWebSocketTransport)
            TrySubscribe();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        CommandManager.RemoveHandler(CommandName);
        StopWebSocketClient();
        TryUnsubscribe();
        eventProvider.UnregisterFunc();
        overlay.Dispose();
    }

    private void Draw()
    {
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

        if (!Configuration.UseWebSocketTransport && !subscribed)
            TrySubscribe();

        overlayHiddenReason = GetOverlayHiddenReason();
        if (overlayHiddenReason is null)
            overlay.Draw();

        DrawConfigWindow();
    }

    private string? GetOverlayHiddenReason()
    {
        if (!Configuration.ShowDpsMeter)
            return null;

        if (!ClientState.IsLoggedIn)
            return "Meter hidden because no character is logged in";

        if (GameGui.GameUiHidden)
            return "Meter hidden because the game UI is hidden";

        if (PluginInterface.UiBuilder.CutsceneActive
            || Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Condition[ConditionFlag.WatchingCutscene]
            || Condition[ConditionFlag.WatchingCutscene78])
        {
            return "Meter hidden because a cutscene is active";
        }

        return null;
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
        lastWebSocketEventAt = DateTime.Now;
        webSocketLastError = null;
        pendingEvents.Enqueue(data);
    }

    private void DrainPendingEvents()
    {
        while (pendingEvents.TryDequeue(out var data))
            overlay.ReceiveIinactEvent(data);
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
                await Task.Delay(WebSocketRetryInterval, cancellationToken);
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

        if (TrySubscribeLegacy())
            return;

        TrySubscribeModern();
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
                    ipcStatus = "IINACT IPC: waiting for IINACT legacy IPC";
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
            ipcStatus = "IINACT IPC: waiting for IINACT legacy IPC";
            ipcLastError = ex.Message;
            Log.Debug(ex, "IINACT legacy IPC subscriber is not available yet.");
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
                    ipcStatus = "IINACT IPC: waiting for IINACT IPC";
                    return false;
                }
            }

            if (!SendToIinact(new JObject
                {
                    ["call"] = "subscribe",
                    ["events"] = new JArray("CombatData", "ChangeZone", "ChangePrimaryPlayer")
                }))
            {
                TryRemoveIinactSubscriber();
                ipcStatus = "IINACT IPC: subscribe request was rejected";
                return false;
            }

            subscribed = true;
            usingLegacySubscriber = false;
            ipcStatus = "IINACT IPC: connected (modern)";
            return true;
        }
        catch (Exception ex)
        {
            TryRemoveIinactSubscriber();
            ipcStatus = "IINACT IPC: waiting for IINACT IPC";
            ipcLastError = ex.Message;
            Log.Debug(ex, "IINACT IPC subscriber is not available yet.");
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
                    ["events"] = new JArray("CombatData", "ChangeZone", "ChangePrimaryPlayer")
                });
            }

            TryRemoveIinactSubscriber();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to unsubscribe from IINACT IPC.");
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
            Log.Debug(ex, "Failed to remove IINACT IPC subscriber.");
        }
    }

    private void ResetIinactConnection()
    {
        TryUnsubscribe();
        TryRemoveIinactSubscriber();
        subscribed = false;
        usingLegacySubscriber = false;
        receivedEventCount = 0;
        webSocketEventCount = 0;
        lastEventAt = null;
        lastWebSocketEventAt = null;
        nextSubscribeAttempt = DateTime.MinValue;
        ipcStatus = "IINACT IPC: reconnecting";
        ipcLastError = null;
        webSocketLastError = null;
        StopWebSocketClient();
        TrySubscribe();
    }

    private bool SendToIinact(JObject data)
    {
        return iinactReceiver.InvokeFunc(data);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                Configuration.ShowDpsMeter = true;
                break;
            case "off":
                Configuration.ShowDpsMeter = false;
                break;
            case "lock":
                Configuration.Locked = !Configuration.Locked;
                break;
            case "clickthrough":
            case "click-through":
                Configuration.ClickThrough = !Configuration.ClickThrough;
                break;
            case "solo":
                Configuration.SoloMode = !Configuration.SoloMode;
                break;
            case "mergepets":
            case "merge-pets":
                Configuration.MergePets = !Configuration.MergePets;
                break;
            case "privacy":
            case "blur":
                Configuration.BlurOtherNames = !Configuration.BlurOtherNames;
                break;
            case "damage":
            case "d":
                Configuration.MeterTab = 0;
                break;
            case "taken":
            case "tank":
            case "t":
                Configuration.MeterTab = 1;
                break;
            case "healing":
            case "heal":
            case "h":
                Configuration.MeterTab = 2;
                break;
            case "config":
            case "settings":
                OpenConfigWindow();
                break;
            case "reconnect":
                OpenConfigWindow();
                ResetIinactConnection();
                break;
            case "websocket":
            case "ws":
                Configuration.UseWebSocketTransport = true;
                StopWebSocketClient();
                break;
            case "ipc":
                Configuration.UseWebSocketTransport = false;
                StopWebSocketClient();
                break;
            default:
                Configuration.ShowDpsMeter = !Configuration.ShowDpsMeter;
                break;
        }

        Configuration.Save();
    }

    private void OpenConfigWindow()
    {
        configOpen = true;
    }

    private void DrawConfigWindow()
    {
        if (!configOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(360, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("IINACT Native Overlay Settings##IINACTNativeOverlayConfig", ref configOpen))
        {
            ImGui.End();
            return;
        }

        DrawConnectionStatus();
        if (ImGui.Button("Reconnect"))
            ResetIinactConnection();
        ImGui.Separator();

        var changed = false;

        var show = Configuration.ShowDpsMeter;
        if (ImGui.Checkbox("Show DPS meter", ref show))
        {
            Configuration.ShowDpsMeter = show;
            changed = true;
        }

        var locked = Configuration.Locked;
        if (ImGui.Checkbox("Lock position", ref locked))
        {
            Configuration.Locked = locked;
            changed = true;
        }

        var clickThrough = Configuration.ClickThrough;
        if (ImGui.Checkbox("Click-through", ref clickThrough))
        {
            Configuration.ClickThrough = clickThrough;
            changed = true;
        }

        var useWebSocket = Configuration.UseWebSocketTransport;
        if (ImGui.Checkbox("Use IINACT WebSocket", ref useWebSocket))
        {
            Configuration.UseWebSocketTransport = useWebSocket;
            if (!useWebSocket)
                StopWebSocketClient();
            changed = true;
        }

        var hideOutOfCombat = Configuration.HideOutOfCombat;
        if (ImGui.Checkbox("Hide when combat ends", ref hideOutOfCombat))
        {
            Configuration.HideOutOfCombat = hideOutOfCombat;
            changed = true;
        }

        var tabs = new[] { "Damage", "Taken", "Healing" };
        var meterTab = Math.Clamp(Configuration.MeterTab, 0, tabs.Length - 1);
        if (ImGui.Combo("Meter tab", ref meterTab, tabs, tabs.Length))
        {
            Configuration.MeterTab = meterTab;
            changed = true;
        }

        var hideTabs = Configuration.HideTabs;
        if (ImGui.Checkbox("Hide tabs", ref hideTabs))
        {
            Configuration.HideTabs = hideTabs;
            changed = true;
        }

        var soloMode = Configuration.SoloMode;
        if (ImGui.Checkbox("Solo mode", ref soloMode))
        {
            Configuration.SoloMode = soloMode;
            changed = true;
        }

        var mergePets = Configuration.MergePets;
        if (ImGui.Checkbox("Merge pet stats", ref mergePets))
        {
            Configuration.MergePets = mergePets;
            changed = true;
        }

        var blurOtherNames = Configuration.BlurOtherNames;
        if (ImGui.Checkbox("Blur other player names", ref blurOtherNames))
        {
            Configuration.BlurOtherNames = blurOtherNames;
            changed = true;
        }

        var abbreviateNames = Configuration.AbbreviateNames;
        if (ImGui.Checkbox("Abbreviate names", ref abbreviateNames))
        {
            Configuration.AbbreviateNames = abbreviateNames;
            changed = true;
        }

        var maxRows = Configuration.MaxRows;
        if (ImGui.SliderInt("Rows", ref maxRows, 1, 24))
        {
            Configuration.MaxRows = maxRows;
            changed = true;
        }

        var opacity = Configuration.Opacity;
        if (ImGui.SliderFloat("Background opacity", ref opacity, 0.15f, 1f, "%.2f"))
        {
            Configuration.Opacity = opacity;
            changed = true;
        }

        if (ImGui.CollapsingHeader("Columns##IINACTNativeOverlayColumns"))
        {
            var showDamageTotal = Configuration.ShowDamageTotal;
            if (ImGui.Checkbox("Damage total", ref showDamageTotal))
            {
                Configuration.ShowDamageTotal = showDamageTotal;
                changed = true;
            }

            var showDamagePercent = Configuration.ShowDamagePercent;
            if (ImGui.Checkbox("Percent", ref showDamagePercent))
            {
                Configuration.ShowDamagePercent = showDamagePercent;
                changed = true;
            }

            var showDeaths = Configuration.ShowDeaths;
            if (ImGui.Checkbox("Deaths", ref showDeaths))
            {
                Configuration.ShowDeaths = showDeaths;
                changed = true;
            }

            var showCritPercent = Configuration.ShowCritPercent;
            if (ImGui.Checkbox("Critical percent", ref showCritPercent))
            {
                Configuration.ShowCritPercent = showCritPercent;
                changed = true;
            }

            var showSwings = Configuration.ShowSwings;
            if (ImGui.Checkbox("Swings/casts", ref showSwings))
            {
                Configuration.ShowSwings = showSwings;
                changed = true;
            }

            var showMaxHit = Configuration.ShowMaxHit;
            if (ImGui.Checkbox("Max hit", ref showMaxHit))
            {
                Configuration.ShowMaxHit = showMaxHit;
                changed = true;
            }

            var showOverhealPercent = Configuration.ShowOverhealPercent;
            if (ImGui.Checkbox("Overheal percent", ref showOverhealPercent))
            {
                Configuration.ShowOverhealPercent = showOverhealPercent;
                changed = true;
            }
        }

        if (changed)
            Configuration.Save();

        ImGui.End();
    }

    private void DrawConnectionStatus()
    {
        ImGui.TextUnformatted(Configuration.UseWebSocketTransport ? webSocketStatus : ipcStatus);
        ImGui.TextWrapped(overlayHiddenReason ?? overlay.Status);

        if (!ImGui.CollapsingHeader("Diagnostics##IINACTNativeOverlayDiagnostics"))
            return;

        ImGui.TextUnformatted(ipcStatus);
        ImGui.TextUnformatted(webSocketStatus);
        ImGui.TextUnformatted($"Events received: {receivedEventCount}");
        ImGui.TextUnformatted(lastEventAt is { } seen ? $"Last event: {seen:T}" : "Last event: none");
        ImGui.TextUnformatted($"WebSocket events: {webSocketEventCount}");
        ImGui.TextUnformatted(lastWebSocketEventAt is { } wsSeen ? $"Last WebSocket event: {wsSeen:T}" : "Last WebSocket event: none");
        if (!string.IsNullOrWhiteSpace(webSocketLastError))
            ImGui.TextWrapped($"Last WebSocket error: {webSocketLastError}");
        ImGui.TextUnformatted($"CombatData events: {overlay.CombatDataEvents}");
        ImGui.TextUnformatted($"Parsed CombatData: {overlay.ParsedCombatDataEvents}");
        ImGui.TextUnformatted(overlay.LastCombatDataAt is { } combatSeen
            ? $"Last CombatData: {combatSeen:T}"
            : "Last CombatData: none");
        ImGui.TextUnformatted($"Rows: {overlay.CurrentRowCount}, active: {(overlay.SnapshotActive ? "yes" : "no")}");
        if (!string.IsNullOrWhiteSpace(ipcLastError))
            ImGui.TextWrapped($"Last IPC error: {ipcLastError}");
    }
}
