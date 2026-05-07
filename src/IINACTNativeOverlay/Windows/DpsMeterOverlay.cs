using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Newtonsoft.Json.Linq;

namespace IINACTNativeOverlay.Windows;

internal sealed class DpsMeterOverlay : IDisposable
{
    private const string WindowName = "IINACT Native DPS Meter##IINACTNativeOverlay";
    private const float MinWidth = 350f;
    private const float DefaultWidth = 450f;
    private const float TabTopOffset = 78f;
    private const float TabHeight = 24f;
    private const float HeaderHeightWithTabs = 128f;
    private const float HeaderHeightWithoutTabs = 90f;

    private static readonly Vector4 PanelTop = new(0.055f, 0.068f, 0.078f, 0.94f);
    private static readonly Vector4 PanelBorder = new(0.45f, 0.55f, 0.64f, 0.26f);
    private static readonly Vector4 HeaderBg = new(0.075f, 0.090f, 0.105f, 0.72f);
    private static readonly Vector4 RowBg = new(0.12f, 0.145f, 0.165f, 0.48f);
    private static readonly Vector4 RowAltBg = new(0.10f, 0.118f, 0.135f, 0.38f);
    private static readonly Vector4 BarBg = new(0.02f, 0.025f, 0.030f, 0.62f);
    private static readonly Vector4 BarFill = new(0.18f, 0.55f, 0.78f, 0.82f);
    private static readonly Vector4 TextPrimary = new(0.93f, 0.96f, 0.98f, 1.00f);
    private static readonly Vector4 TextSecondary = new(0.64f, 0.70f, 0.74f, 1.00f);
    private static readonly Vector4 TextMuted = new(0.43f, 0.50f, 0.55f, 1.00f);
    private static readonly Vector4 LiveGreen = new(0.32f, 0.86f, 0.53f, 1.00f);
    private static readonly Vector4 EndedGrey = new(0.52f, 0.58f, 0.62f, 1.00f);
    private static readonly Vector4 DeathRed = new(0.95f, 0.36f, 0.34f, 1.00f);
    private static readonly Vector4 PlayerGold = new(1.00f, 0.78f, 0.28f, 0.95f);
    private static readonly Vector2 ClassIconGridSize = new(11, 6);

    private static readonly IReadOnlyDictionary<uint, Vector4> JobBarColors = new Dictionary<uint, Vector4>
    {
        [1] = Rgb(21, 28, 100), [19] = Rgb(21, 28, 100),
        [3] = Rgb(153, 23, 23), [21] = Rgb(153, 23, 23),
        [32] = Rgb(136, 14, 79), [37] = Rgb(78, 52, 46),
        [6] = Rgb(117, 117, 117), [24] = Rgb(117, 117, 117),
        [28] = Rgb(121, 134, 203), [33] = Rgb(121, 85, 72), [40] = Rgb(79, 195, 247),
        [2] = Rgb(255, 152, 0), [20] = Rgb(255, 152, 0),
        [4] = Rgb(63, 81, 181), [22] = Rgb(63, 81, 181),
        [29] = Rgb(211, 47, 47), [30] = Rgb(211, 47, 47),
        [34] = Rgb(255, 202, 40), [39] = Rgb(254, 179, 0), [41] = Rgb(216, 67, 21),
        [5] = Rgb(158, 157, 36), [23] = Rgb(158, 157, 36),
        [31] = Rgb(0, 151, 167), [38] = Rgb(244, 143, 177),
        [7] = Rgb(126, 87, 194), [25] = Rgb(126, 87, 194),
        [26] = Rgb(46, 125, 50), [27] = Rgb(46, 125, 50),
        [35] = Rgb(233, 30, 99), [36] = Rgb(0, 185, 247), [42] = Rgb(253, 216, 53),
    };

    private static readonly IReadOnlyDictionary<uint, Vector2> JobIconCells = new Dictionary<uint, Vector2>
    {
        [1] = new(0, 0), [3] = new(1, 0), [2] = new(2, 0), [4] = new(3, 0), [29] = new(4, 0),
        [5] = new(5, 0), [7] = new(6, 0), [26] = new(7, 0), [6] = new(9, 0),
        [19] = new(0, 1), [21] = new(1, 1), [20] = new(2, 1), [22] = new(3, 1),
        [30] = new(4, 1), [23] = new(5, 1), [25] = new(6, 1), [27] = new(7, 1),
        [28] = new(8, 1), [24] = new(9, 1),
        [32] = new(0, 2), [31] = new(1, 2), [33] = new(2, 2), [34] = new(3, 2),
        [35] = new(4, 2), [36] = new(5, 2), [37] = new(6, 2), [38] = new(7, 2),
        [39] = new(8, 2), [40] = new(9, 2), [41] = new(3, 5), [42] = new(4, 5),
    };

    private static readonly IReadOnlyDictionary<string, uint> JobAbbreviations = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = 1, ["PGL"] = 2, ["MRD"] = 3, ["LNC"] = 4, ["ARC"] = 5, ["CNJ"] = 6, ["THM"] = 7,
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23, ["WHM"] = 24, ["BLM"] = 25,
        ["ACN"] = 26, ["SMN"] = 27, ["SCH"] = 28, ["ROG"] = 29, ["NIN"] = 30, ["MCH"] = 31, ["DRK"] = 32,
        ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35, ["BLU"] = 36, ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39,
        ["SGE"] = 40, ["VPR"] = 41, ["PCT"] = 42,
    };

    private readonly Plugin plugin;
    private readonly ISharedImmediateTexture? classIconsTexture;
    private readonly Dictionary<string, uint> encounterJobCache = new(StringComparer.OrdinalIgnoreCase);
    private DpsSnapshot? snapshot;
    private string? currentZone;
    private string? playerName;
    private double lastDurationSeconds;
    private Rect? lastTabBounds;

    internal int CombatDataEvents { get; private set; }
    internal int ParsedCombatDataEvents { get; private set; }
    internal DateTime? LastCombatDataAt { get; private set; }
    internal string Status { get; private set; } = "Waiting for IINACT combat data";
    internal int CurrentRowCount => snapshot?.Rows.Count ?? 0;
    internal bool HasSnapshot => snapshot is not null;
    internal bool SnapshotActive => snapshot?.IsActive == true;

    public DpsMeterOverlay(Plugin plugin)
    {
        this.plugin = plugin;
        var iconPath = Path.Combine(plugin.PluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "classes.png");
        if (File.Exists(iconPath))
            classIconsTexture = plugin.TextureProvider.GetFromFile(iconPath);
    }

    public void Dispose()
    {
    }

    public void ReceiveIinactEvent(JObject data)
    {
        if (string.Equals(Text(data["type"]), "broadcast", StringComparison.OrdinalIgnoreCase))
        {
            ReceiveLegacyBroadcast(data);
            return;
        }

        switch (Text(data["type"]))
        {
            case "CombatData":
                ReceiveCombatData(data);
                break;
            case "ChangeZone":
                currentZone = Text(data["zoneName"]);
                encounterJobCache.Clear();
                snapshot = null;
                lastDurationSeconds = 0;
                break;
            case "ChangePrimaryPlayer":
                playerName = Text(data["charName"]);
                break;
        }
    }

    private void ReceiveLegacyBroadcast(JObject data)
    {
        switch (Text(data["msgtype"]))
        {
            case "CombatData":
                if (AsObject(data["msg"]) is JObject combatData)
                    ReceiveCombatData(combatData);
                else
                    Status = "CombatData event did not contain a JSON object payload";
                break;
            case "ChangeZone":
                if (AsObject(data["msg"]) is JObject zoneData)
                    ReceiveChangeZone(zoneData);
                break;
            case "SendCharName":
                if (AsObject(data["msg"]) is JObject playerData)
                    ReceivePrimaryPlayer(playerData);
                break;
        }
    }

    private void ReceiveChangeZone(JObject data)
    {
        currentZone = Text(data["zoneName"]);
        encounterJobCache.Clear();
        snapshot = null;
        Status = "Waiting for IINACT combat data";
        lastDurationSeconds = 0;
    }

    private void ReceivePrimaryPlayer(JObject data)
    {
        playerName = Text(data["charName"]);
    }

    public void Draw()
    {
        var config = plugin.Configuration;
        if (!config.ShowDpsMeter)
            return;

        config.MaxRows = Math.Clamp(config.MaxRows, 1, 24);
        config.Opacity = Math.Clamp(config.Opacity, 0.15f, 1f);

        UpdateJobCacheFromDalamud();
        if (config.HideOutOfCombat && snapshot is not null && !snapshot.IsActive)
        {
            Status = "Meter hidden because the encounter is inactive";
            return;
        }

        var flags = ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoBackground;

        if (config.Locked)
            flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        if (config.ClickThrough && !IsMouseOverTabStrip())
            flags |= ImGuiWindowFlags.NoInputs;

        var visible = true;
        ImGui.SetNextWindowSize(new Vector2(DefaultWidth * ImGuiHelpers.GlobalScale, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(MinWidth * ImGuiHelpers.GlobalScale, 0),
            new Vector2(float.MaxValue, float.MaxValue));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);

        if (!ImGui.Begin(WindowName, ref visible, flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            SaveVisibilityIfChanged(visible);
            return;
        }

        if (snapshot is null || snapshot.Rows.Count == 0)
            DrawEmptyState(config.Opacity);
        else
            DrawStyledMeter(snapshot, config.Opacity);

        ImGui.End();
        ImGui.PopStyleVar(3);
        SaveVisibilityIfChanged(visible);
    }

    private void ReceiveCombatData(JObject data)
    {
        CombatDataEvents++;
        LastCombatDataAt = DateTime.Now;

        var payload = FindCombatDataPayload(data);
        if (payload is null)
        {
            Status = $"CombatData missing Encounter or Combatant payload. Keys: {DescribeKeys(data)}";
            return;
        }

        var encounter = AsObject(payload["Encounter"] ?? payload["encounter"]);
        var combatants = AsObject(payload["Combatant"] ?? payload["combatant"] ?? payload["combatants"]);
        if (encounter is null || combatants is null)
        {
            Status = $"CombatData missing Encounter or Combatant payload. Keys: {DescribeKeys(payload)}";
            return;
        }

        var duration = ParseDuration(Text(encounter["duration"]) ?? Text(encounter["DURATION"]));
        if (duration.TotalSeconds + 1 < lastDurationSeconds)
            encounterJobCache.Clear();
        lastDurationSeconds = duration.TotalSeconds;

        var contentName = GetContentName(encounter);
        var targetName = GetTargetName(encounter);
        var rows = combatants.Properties()
                             .Select(property => ToRow(property.Name, property.Value as JObject))
                             .Where(row => row is not null)
                             .Select(row => row!)
                             .ToList();

        var mode = ActiveMeterMode();
        var displayedRows = FilterRowsForDisplay(rows, mode)
            .Take(plugin.Configuration.MaxRows)
            .ToList();

        ParsedCombatDataEvents++;
        Status = displayedRows.Count == 0
            ? "CombatData parsed, but no rows are available for the selected tab"
            : $"Showing {displayedRows.Count} {mode.Label.ToLowerInvariant()} row{(displayedRows.Count == 1 ? string.Empty : "s")}";
        snapshot = new DpsSnapshot(
            contentName,
            targetName,
            string.Equals(Text(data["isActive"]), "true", StringComparison.OrdinalIgnoreCase),
            duration,
            Number(encounter["encdps"]) ?? Number(encounter["ENCDPS"]) ?? 0,
            Number(encounter["enchps"]) ?? Number(encounter["ENCHPS"]) ?? Number(encounter["hps"]) ?? 0,
            rows);
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

    private DpsRow? ToRow(string key, JObject? data)
    {
        if (data is null)
            return null;

        var name = Text(data["name"]) ?? key;
        var ownerName = FindOwnerName(name, data);
        var damage = (long)(NumberFirst(data, "damage", "DAMAGE", "totaldamage") ?? 0);
        var dps = NumberFirst(data, "encdps", "ENCDPS", "dps", "DPS") ?? 0;
        var damagePercent = PercentText(TextFirst(data, "damage%", "Damage%", "damagePct", "damagepercent"));
        var healed = (long)(NumberFirst(data, "healed", "HEALED", "heal", "healtotal") ?? 0);
        var hps = NumberFirst(data, "enchps", "ENCHPS", "hps", "HPS") ?? 0;
        var healPercent = PercentText(TextFirst(data, "healed%", "heal%", "Heal%", "healPct", "healpercent"));
        var overhealPercent = PercentText(TextFirst(data, "overHealPct", "OverHealPct", "overheal%", "overhealpct", "OverHeal%"));
        var damageTaken = (long)(NumberFirst(data, "damagetaken", "damageTaken", "DamageTaken", "damage-taken") ?? 0);
        var swings = (int)(NumberFirst(data, "swings", "SWINGS", "hits", "Hits", "heals", "Heals") ?? 0);
        var critPercent = PercentText(TextFirst(data, "crithit%", "crithitPct", "crit%", "critical%", "critheal%", "CritHeal%"));
        var maxHit = TextFirst(data, "maxhit", "maxhitstr", "MaxHit", "MAXHIT") ?? string.Empty;
        var deaths = (int)(NumberFirst(data, "deaths", "Deaths", "KO") ?? 0);
        var jobId = ResolveJobId(name, ownerName, data);
        var isPlayer = IsLocalPlayerRow(name, ownerName);

        return new DpsRow(
            name,
            ownerName,
            damage,
            dps,
            damagePercent,
            healed,
            hps,
            healPercent,
            overhealPercent,
            damageTaken,
            swings,
            critPercent,
            maxHit,
            deaths,
            jobId,
            isPlayer,
            ownerName is not null);
    }

    private bool IsLocalPlayerRow(string? name, string? ownerName)
    {
        return IsYouAlias(name)
               || IsYouAlias(ownerName)
               || NamesEqual(name, playerName)
               || NamesEqual(ownerName, playerName);
    }

    private uint? ResolveJobId(string name, string? ownerName, JObject data)
    {
        var jobId = JobIdFromNumber(NumberFirst(data, "jobId", "JobId", "jobid", "classjob", "ClassJob", "classjobid", "ClassJobId"));
        jobId ??= JobIdFromText(TextFirst(data, "job", "Job", "JOB", "class", "Class"));
        jobId ??= FindJobId(name);
        jobId ??= FindJobId(ownerName);
        jobId ??= FindSnapshotJobId(name);
        jobId ??= FindSnapshotJobId(ownerName);

        if (jobId is { } id)
        {
            AddJob(name, id);
            AddJob(ownerName, id);
        }

        return jobId;
    }

    private uint? FindSnapshotJobId(string? name)
    {
        if (snapshot is null)
            return null;

        foreach (var alias in NameAliases(name))
        {
            foreach (var row in snapshot.Rows)
            {
                if (row.JobId is { } jobId
                    && (NamesEqual(row.Name, alias) || NamesEqual(row.OwnerName, alias)))
                    return jobId;
            }
        }

        return null;
    }

    private static uint? JobIdFromNumber(double? number)
    {
        return number is > 0 and <= uint.MaxValue ? (uint)number.Value : null;
    }

    private static uint? JobIdFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (JobAbbreviations.TryGetValue(trimmed, out var jobId))
            return jobId;

        return JobIdFromNumber(NumberFromText(trimmed));
    }

    private MeterMode ActiveMeterMode()
    {
        plugin.Configuration.MeterTab = Math.Clamp(plugin.Configuration.MeterTab, 0, MeterMode.All.Count - 1);
        return MeterMode.All[plugin.Configuration.MeterTab];
    }

    private IEnumerable<DpsRow> FilterRowsForDisplay(IReadOnlyList<DpsRow> sourceRows, MeterMode mode)
    {
        var rows = plugin.Configuration.MergePets ? MergePetRows(sourceRows) : sourceRows;
        if (plugin.Configuration.SoloMode)
            rows = rows.Where(row => row.IsPlayer || NamesEqual(row.OwnerName, playerName)).ToList();

        return mode.Id switch
        {
            MeterModeId.Tank => rows.Where(row => row.DamageTaken > 0 || row.Deaths > 0)
                                    .OrderByDescending(row => row.DamageTaken)
                                    .ThenByDescending(row => row.Damage),
            MeterModeId.Healing => rows.Where(row => row.Healed > 0)
                                       .OrderByDescending(row => row.EncounterHps)
                                       .ThenByDescending(row => row.Healed),
            _ => rows.Where(row => row.Damage > 0)
                     .OrderByDescending(row => row.EncounterDps)
                     .ThenByDescending(row => row.Damage),
        };
    }

    private List<DpsRow> MergePetRows(IReadOnlyList<DpsRow> rows)
    {
        var merged = new List<DpsRow>();
        var ownerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Where(row => row.OwnerName is null))
        {
            ownerIndex[NameAliases(row.Name).FirstOrDefault() ?? row.Name] = merged.Count;
            merged.Add(row);
        }

        foreach (var pet in rows.Where(row => row.OwnerName is not null))
        {
            var ownerKey = NameAliases(pet.OwnerName).FirstOrDefault() ?? pet.OwnerName!;
            if (ownerIndex.TryGetValue(ownerKey, out var index))
            {
                merged[index] = merged[index].Merge(pet);
                continue;
            }

            ownerIndex[ownerKey] = merged.Count;
            merged.Add(pet with { Name = pet.OwnerName!, OwnerName = null });
        }

        return merged;
    }

    private static string? FindOwnerName(string name, JObject data)
    {
        var owner = TextFirst(data, "owner", "Owner", "petOwner", "PetOwner", "master", "Master");
        if (!string.IsNullOrWhiteSpace(owner) && !NamesEqual(owner, name))
            return owner;

        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || !name.EndsWith(')'))
            return null;

        var candidate = name[(open + 2)..^1].Trim();
        return string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "Pet", StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
    }

    private void SaveVisibilityIfChanged(bool visible)
    {
        if (plugin.Configuration.ShowDpsMeter == visible)
            return;

        plugin.Configuration.ShowDpsMeter = visible;
        plugin.Configuration.Save();
    }

    private static void DrawEmptyState(float opacity)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(ImGui.GetContentRegionAvail().X, MinWidth * scale);
        var height = 78 * scale;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        DrawPanel(drawList, pos, new Vector2(width, height), 7 * scale, opacity);
        DrawText(drawList, pos + new Vector2(16 * scale, 18 * scale), "DPS Meter", TextSecondary, opacity);
        DrawText(drawList, pos + new Vector2(16 * scale, 42 * scale), "Waiting for IINACT combat data", TextPrimary, opacity);

        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawStyledMeter(DpsSnapshot snapshot, float opacity)
    {
        var config = plugin.Configuration;
        var mode = ActiveMeterMode();
        var rows = FilterRowsForDisplay(snapshot.Rows, mode)
            .Take(config.MaxRows)
            .ToList();
        var columns = BuildColumns(mode, config);
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(ImGui.GetContentRegionAvail().X, MinWidth * scale);
        var rowHeight = 30 * scale;
        var headerHeight = (config.HideTabs ? HeaderHeightWithoutTabs : HeaderHeightWithTabs) * scale;
        var footerHeight = 8 * scale;
        var height = headerHeight + rows.Count * rowHeight + footerHeight;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 7 * scale;
        var pad = 14 * scale;
        var maxValue = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(row => ProgressMetric(row, mode)));

        DrawPanel(drawList, pos, new Vector2(width, height), rounding, opacity);
        DrawHeader(drawList, pos, width, headerHeight, pad, scale, snapshot, mode, columns, config.HideTabs, opacity);

        for (var i = 0; i < rows.Count; i++)
            DrawRow(drawList, pos + new Vector2(0, headerHeight + i * rowHeight), width, rowHeight, scale,
                    rows[i], i, maxValue, mode, columns, opacity);

        if (config.HideTabs)
            lastTabBounds = null;
        else
            DrawTabHitTargets(pos, width, headerHeight, scale);
        ImGui.Dummy(new Vector2(width, height));
    }

    private static void DrawPanel(ImDrawListPtr drawList, Vector2 pos, Vector2 size, float rounding, float opacity)
    {
        drawList.AddRectFilled(pos, pos + size, Color(WithOpacity(PanelTop, opacity)), rounding);
        drawList.AddRect(pos, pos + size, Color(WithOpacity(PanelBorder, opacity)), rounding,
                         ImDrawFlags.RoundCornersAll, 1.0f);
    }

    private static void DrawHeader(ImDrawListPtr drawList, Vector2 pos, float width, float height, float pad,
                                   float scale, DpsSnapshot snapshot, MeterMode mode,
                                   IReadOnlyList<RowColumn> columns, bool hideTabs, float opacity)
    {
        var accentColor = snapshot.IsActive ? LiveGreen : EndedGrey;
        var title = ShortenText(snapshot.ContentName, width - (pad * 2) - 110 * scale);
        var target = ShortenText(snapshot.TargetName, width - (pad * 2) - 110 * scale);
        var status = snapshot.IsActive ? "LIVE" : "ENDED";
        var raidRate = mode.Id == MeterModeId.Healing ? FormatNumber(snapshot.EncounterHps) : FormatNumber(snapshot.EncounterDps);
        var raidLabel = mode.Id == MeterModeId.Healing ? "Raid HPS" : "Raid DPS";

        drawList.AddRectFilled(pos, pos + new Vector2(width, height), Color(WithOpacity(HeaderBg, opacity)), 7 * scale,
                               ImDrawFlags.RoundCornersTop);
        drawList.AddRectFilled(pos + new Vector2(0, height - 1 * scale),
                               pos + new Vector2(width, height),
                               Color(WithOpacity(new Vector4(1f, 1f, 1f, 0.10f), opacity)));
        drawList.AddRectFilled(pos, pos + new Vector2(4 * scale, height), Color(WithOpacity(accentColor, opacity)),
                               7 * scale, ImDrawFlags.RoundCornersLeft);

        DrawText(drawList, pos + new Vector2(pad, 11 * scale), title, TextSecondary, opacity);
        DrawText(drawList, pos + new Vector2(pad, 31 * scale), target, TextPrimary, opacity);
        DrawText(drawList, pos + new Vector2(pad, 50 * scale), status, accentColor, opacity);

        DrawTextRight(drawList, pos + new Vector2(width - pad, 11 * scale), raidLabel, TextMuted, opacity);
        DrawTextRight(drawList, pos + new Vector2(width - pad, 31 * scale), raidRate, TextPrimary, opacity);
        DrawTextRight(drawList, pos + new Vector2(width - pad, 50 * scale), FormatDuration(snapshot.Duration), TextMuted, opacity);

        if (!hideTabs)
            DrawTabLabels(drawList, pos, width, height, scale, mode, opacity);
        DrawColumnLabels(drawList, pos, width, height, scale, columns, opacity);
    }

    private static void DrawColumnLabels(ImDrawListPtr drawList, Vector2 pos, float width, float height,
                                         float scale, IReadOnlyList<RowColumn> columns, float opacity)
    {
        var layout = CreateLayout(pos, width, scale, columns);
        var labelY = pos.Y + height - 18 * scale;

        drawList.AddRectFilled(pos + new Vector2(0, height - 25 * scale),
                               pos + new Vector2(width, height - 24 * scale),
                               Color(WithOpacity(new Vector4(1f, 1f, 1f, 0.06f), opacity)));
        DrawText(drawList, new Vector2(layout.NameStart, labelY), "Name", TextMuted, opacity);
        foreach (var column in layout.Columns)
            DrawTextRight(drawList, new Vector2(column.Right, labelY), column.Spec.Label, TextMuted, opacity);
    }

    private static void DrawTabLabels(ImDrawListPtr drawList, Vector2 pos, float width, float height,
                                      float scale, MeterMode mode, float opacity)
    {
        var tabY = pos.Y + TabTopOffset * scale;
        var tabHeight = TabHeight * scale;
        var tabWidth = Math.Min(86 * scale, (width - 28 * scale) / MeterMode.All.Count);
        var x = pos.X + 14 * scale;

        foreach (var tab in MeterMode.All)
        {
            var active = tab.Id == mode.Id;
            var bg = active ? new Vector4(1f, 1f, 1f, 0.13f) : new Vector4(1f, 1f, 1f, 0.04f);
            var color = active ? TextPrimary : TextMuted;
            drawList.AddRectFilled(new Vector2(x, tabY), new Vector2(x + tabWidth - 4 * scale, tabY + tabHeight),
                                   Color(WithTabOpacity(bg, opacity)), 3 * scale);
            DrawText(drawList, new Vector2(x + 8 * scale, tabY + 4 * scale), tab.ShortLabel, color, opacity);
            x += tabWidth;
        }
    }

    private void DrawRow(ImDrawListPtr drawList, Vector2 pos, float width, float height, float scale,
                         DpsRow row, int index, long maxValue, MeterMode mode,
                         IReadOnlyList<RowColumn> columns, float opacity)
    {
        var rowPos = pos + new Vector2(7 * scale, 2 * scale);
        var rowSize = new Vector2(width - 14 * scale, height - 3 * scale);
        var layout = CreateLayout(pos, width, scale, columns);
        var nameStart = layout.NameStart;
        var nameWidth = Math.Max(24 * scale, layout.NameRight - nameStart - 8 * scale);
        var progress = Math.Clamp(ProgressMetric(row, mode) / (float)maxValue, 0f, 1f);
        var rowBg = index % 2 == 0 ? RowBg : RowAltBg;
        var barColor = row.JobId is { } jobId && JobBarColors.TryGetValue(jobId, out var jobColor)
            ? jobColor
            : Lerp(new Vector4(0.13f, 0.34f, 0.48f, 0.92f), BarFill, progress);
        var config = plugin.Configuration;
        var name = ShortenText(DisplayName(row, config), nameWidth);
        var blurName = ShouldBlurName(row, config);
        var rank = (index + 1).ToString(CultureInfo.CurrentCulture);

        drawList.AddRectFilled(rowPos, rowPos + rowSize, Color(WithOpacity(rowBg, opacity)), 3 * scale);
        drawList.AddRectFilled(rowPos, rowPos + rowSize, Color(WithOpacity(BarBg, opacity)), 3 * scale);
        drawList.AddRectFilled(rowPos, rowPos + new Vector2(rowSize.X * progress, rowSize.Y), Color(barColor), 3 * scale);
        if (row.IsPlayer)
            drawList.AddRect(rowPos, rowPos + rowSize, Color(WithOpacity(PlayerGold, opacity)), 3 * scale,
                             ImDrawFlags.RoundCornersAll, 1.0f);
        if (progress > 0.01f && progress < 0.99f)
        {
            drawList.AddRectFilled(rowPos + new Vector2(rowSize.X * progress, 0),
                                   rowPos + new Vector2(rowSize.X * progress + 1 * scale, rowSize.Y),
                                   Color(new Vector4(1f, 1f, 1f, 0.16f)));
        }

        var textY = rowPos.Y + (rowSize.Y - ImGui.GetTextLineHeight()) * 0.5f;
        if (!TryDrawJobIcon(drawList, row.JobId, rowPos + new Vector2(layout.InnerPad, 3 * scale), 21 * scale))
            DrawText(drawList, new Vector2(rowPos.X + layout.InnerPad, textY), rank, TextMuted, opacity);

        if (blurName)
            DrawNamePrivacyMask(drawList, new Vector2(nameStart, textY), name, TextPrimary, opacity, scale);
        else
            DrawText(drawList, new Vector2(nameStart, textY), name, TextPrimary, opacity);

        foreach (var column in layout.Columns)
        {
            var text = FormatCell(row, column.Spec.Kind, mode);
            var color = column.Spec.Kind == RowColumnKind.Deaths && row.Deaths > 0 ? DeathRed : TextSecondary;
            if (column.Spec.Kind is RowColumnKind.PrimaryRate)
                color = TextPrimary;
            DrawTextRight(drawList, new Vector2(column.Right, textY), text, color, opacity);
        }
    }

    private bool TryDrawJobIcon(ImDrawListPtr drawList, uint? jobId, Vector2 pos, float size)
    {
        if (jobId is not { } id)
            return false;

        if (classIconsTexture is null || !JobIconCells.TryGetValue(id, out var cell))
            return false;

        if (!classIconsTexture.TryGetWrap(out IDalamudTextureWrap? wrap, out _))
            return false;

        var uv0 = cell / ClassIconGridSize;
        var uv1 = (cell + Vector2.One) / ClassIconGridSize;
        drawList.AddImage(wrap.Handle, pos, pos + new Vector2(size, size), uv0, uv1);
        return true;
    }

    private void DrawTabHitTargets(Vector2 pos, float width, float headerHeight, float scale)
    {
        var tabY = pos.Y + TabTopOffset * scale;
        var tabHeight = TabHeight * scale;
        var tabWidth = Math.Min(86 * scale, (width - 28 * scale) / MeterMode.All.Count);
        var x = pos.X + 14 * scale;
        lastTabBounds = new Rect(
            new Vector2(x, tabY),
            new Vector2(x + tabWidth * MeterMode.All.Count - 4 * scale, tabY + tabHeight));

        for (var i = 0; i < MeterMode.All.Count; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, tabY));
            if (ImGui.InvisibleButton($"##IINACTNativeOverlayTab{i}", new Vector2(tabWidth - 4 * scale, tabHeight)))
            {
                plugin.Configuration.MeterTab = i;
                plugin.Configuration.Save();
            }

            x += tabWidth;
        }
    }

    private bool IsMouseOverTabStrip()
    {
        return lastTabBounds is { } bounds && bounds.Contains(ImGui.GetMousePos());
    }

    private static RowLayout CreateLayout(Vector2 pos, float width, float scale, IReadOnlyList<RowColumn> columns)
    {
        var rowLeft = pos.X + 7 * scale;
        var rowWidth = width - 14 * scale;
        var innerPad = 9 * scale;
        var iconWidth = 26 * scale;
        var right = rowLeft + rowWidth - innerPad;
        var layoutColumns = new List<RowColumnLayout>(columns.Count);

        foreach (var column in columns.AsEnumerable().Reverse())
        {
            layoutColumns.Insert(0, new RowColumnLayout(column, right));
            right -= column.Width * scale;
        }

        return new RowLayout(rowLeft, innerPad, rowLeft + innerPad + iconWidth, right, layoutColumns);
    }

    private static IReadOnlyList<RowColumn> BuildColumns(MeterMode mode, Configuration config)
    {
        var columns = new List<RowColumn>
        {
            new(mode.Id == MeterModeId.Healing ? "HPS" : "DPS", 76, RowColumnKind.PrimaryRate),
        };

        switch (mode.Id)
        {
            case MeterModeId.Tank:
                columns.Add(new RowColumn("Taken", 88, RowColumnKind.DamageTaken));
                columns.Add(new RowColumn("Healed", 88, RowColumnKind.HealedReceived));
                if (config.ShowDeaths)
                    columns.Add(new RowColumn("KO", 34, RowColumnKind.Deaths));
                break;
            case MeterModeId.Healing:
                columns.Add(new RowColumn("Heal", 88, RowColumnKind.HealTotal));
                if (config.ShowDamagePercent)
                    columns.Add(new RowColumn("%", 44, RowColumnKind.HealPercent));
                if (config.ShowOverhealPercent)
                    columns.Add(new RowColumn("Over", 52, RowColumnKind.OverhealPercent));
                if (config.ShowCritPercent)
                    columns.Add(new RowColumn("Crit", 52, RowColumnKind.CritPercent));
                if (config.ShowSwings)
                    columns.Add(new RowColumn("Casts", 54, RowColumnKind.Swings));
                break;
            default:
                if (config.ShowDamageTotal)
                    columns.Add(new RowColumn("Damage", 88, RowColumnKind.DamageTotal));
                if (config.ShowDamagePercent)
                    columns.Add(new RowColumn("%", 44, RowColumnKind.DamagePercent));
                if (config.ShowDeaths)
                    columns.Add(new RowColumn("KO", 34, RowColumnKind.Deaths));
                if (config.ShowCritPercent)
                    columns.Add(new RowColumn("Crit", 52, RowColumnKind.CritPercent));
                if (config.ShowSwings)
                    columns.Add(new RowColumn("Hits", 50, RowColumnKind.Swings));
                if (config.ShowMaxHit)
                    columns.Add(new RowColumn("Max", 96, RowColumnKind.MaxHit));
                break;
        }

        return columns;
    }

    private static long ProgressMetric(DpsRow row, MeterMode mode)
    {
        return mode.Id switch
        {
            MeterModeId.Tank => row.DamageTaken,
            MeterModeId.Healing => row.Healed,
            _ => row.Damage,
        };
    }

    private static string DisplayName(DpsRow row, Configuration config)
    {
        if (row.IsPlayer)
            return "YOU";

        var name = config.MergePets && row.OwnerName is not null ? row.OwnerName : row.Name;
        return config.AbbreviateNames ? AbbreviateName(name) : name;
    }

    private static bool ShouldBlurName(DpsRow row, Configuration config)
    {
        return config.BlurOtherNames && !row.IsPlayer && !IsYouAlias(row.Name) && !IsYouAlias(row.OwnerName);
    }

    private static string FormatCell(DpsRow row, RowColumnKind kind, MeterMode mode)
    {
        return kind switch
        {
            RowColumnKind.PrimaryRate => mode.Id == MeterModeId.Healing
                ? FormatNumber(row.EncounterHps)
                : FormatNumber(row.EncounterDps),
            RowColumnKind.DamageTotal => FormatNumber(row.Damage),
            RowColumnKind.DamagePercent => row.DamagePercent,
            RowColumnKind.HealTotal => FormatNumber(row.Healed),
            RowColumnKind.HealPercent => row.HealPercent,
            RowColumnKind.OverhealPercent => row.OverhealPercent,
            RowColumnKind.DamageTaken => FormatNumber(row.DamageTaken),
            RowColumnKind.HealedReceived => FormatNumber(row.Healed),
            RowColumnKind.Deaths => row.Deaths > 0 ? row.Deaths.ToString(CultureInfo.CurrentCulture) : string.Empty,
            RowColumnKind.CritPercent => row.CritPercent,
            RowColumnKind.Swings => row.Swings > 0 ? row.Swings.ToString("N0", CultureInfo.CurrentCulture) : string.Empty,
            RowColumnKind.MaxHit => row.MaxHit,
            _ => string.Empty,
        };
    }

    private void UpdateJobCacheFromDalamud()
    {
        try
        {
            var localPlayer = plugin.ObjectTable.LocalPlayer;
            if (localPlayer is not null)
            {
                var localJobId = ClassJobIdFromValue(localPlayer.ClassJob)
                                 ?? ClassJobIdFromValue(plugin.PlayerState.ClassJob);
                var localName = TextFromValue(localPlayer.Name);
                AddJob(localName, localJobId);
                AddJob(playerName, localJobId);
                AddJob("YOU", localJobId);
                AddJob("You", localJobId);
            }

            foreach (var member in plugin.PartyList)
                AddJob(TextFromValue(member.Name), ClassJobIdFromValue(member.ClassJob));
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to update job cache.");
        }
    }

    private void AddJob(string? name, uint? jobId)
    {
        if (string.IsNullOrWhiteSpace(name) || jobId is null or 0)
            return;

        foreach (var alias in NameAliases(name))
            encounterJobCache[alias] = jobId.Value;
    }

    private uint? FindJobId(string? name)
    {
        foreach (var alias in NameAliases(name))
        {
            if (encounterJobCache.TryGetValue(alias, out var jobId))
                return jobId;
        }

        return null;
    }

    private static bool NamesEqual(string? left, string? right)
    {
        foreach (var leftAlias in NameAliases(left))
        {
            foreach (var rightAlias in NameAliases(right))
            {
                if (string.Equals(leftAlias, rightAlias, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool IsYouAlias(string? name)
    {
        return NameAliases(name).Any(alias => string.Equals(alias, "YOU", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(alias, "You", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> NameAliases(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        var trimmed = name.Trim();
        yield return trimmed;

        var normalized = trimmed.Replace('\u00A0', ' ');
        if (!string.Equals(normalized, trimmed, StringComparison.Ordinal))
            yield return normalized;

        var worldSeparator = normalized.IndexOf('@', StringComparison.Ordinal);
        if (worldSeparator > 0)
            yield return normalized[..worldSeparator].Trim();

        var parentheticalWorld = normalized.IndexOf(" (", StringComparison.Ordinal);
        if (parentheticalWorld > 0 && normalized.EndsWith(')'))
            yield return normalized[..parentheticalWorld].Trim();

        if (string.Equals(normalized, "YOU", StringComparison.OrdinalIgnoreCase))
            yield return "You";
    }

    private static string AbbreviateName(string name)
    {
        var normalized = name.Replace('\u00A0', ' ').Trim();
        var worldSeparator = normalized.IndexOf('@', StringComparison.Ordinal);
        if (worldSeparator > 0)
            normalized = normalized[..worldSeparator].Trim();

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return normalized;

        return $"{parts[0]} {parts[^1][0]}.";
    }

    private string GetContentName(JObject encounter)
    {
        var title = Text(encounter["CurrentZoneName"]) ?? currentZone;
        return IsGenericEncounterName(title) ? "DPS Meter" : title!;
    }

    private string GetTargetName(JObject encounter)
    {
        var title = Text(encounter["title"]) ?? Text(encounter["TITLE"]);
        if (!IsGenericEncounterName(title))
            return title!;

        var target = TextFromValue(plugin.TargetManager.Target?.Name);
        return IsGenericEncounterName(target) ? "Current pull" : target!;
    }

    private static bool IsGenericEncounterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var trimmed = name.Trim();
        return string.Equals(trimmed, "Encounter", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "All", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static uint? ClassJobIdFromValue(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return NumberFromObject(value) ?? RowIdFromValue(value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint? RowIdFromValue(object value)
    {
        var valueType = value.GetType();
        foreach (var memberName in new[] { "RowId", "Id", "ClassJobId", "ClassJob" })
        {
            var property = valueType.GetProperty(memberName);
            if (property?.GetValue(value) is { } propertyValue
                && NumberFromObject(propertyValue) is { } propertyNumber and > 0)
                return propertyNumber;

            var field = valueType.GetField(memberName);
            if (field?.GetValue(value) is { } fieldValue
                && NumberFromObject(fieldValue) is { } fieldNumber and > 0)
                return fieldNumber;
        }

        return null;
    }

    private static uint? NumberFromObject(object value)
    {
        try
        {
            return value switch
            {
                byte byteValue => byteValue,
                sbyte sbyteValue when sbyteValue > 0 => (uint)sbyteValue,
                ushort ushortValue => ushortValue,
                short shortValue when shortValue > 0 => (uint)shortValue,
                uint uintValue => uintValue,
                int intValue when intValue > 0 => (uint)intValue,
                ulong ulongValue when ulongValue <= uint.MaxValue => (uint)ulongValue,
                long longValue when longValue is > 0 and <= uint.MaxValue => (uint)longValue,
                Enum enumValue => Convert.ToUInt32(enumValue, CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
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

    private static string? Text(JToken? token)
    {
        var text = token?.ToString();
        return string.IsNullOrWhiteSpace(text) || text == "---" ? null : text;
    }

    private static string? TextFirst(JObject data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Text(data[key]) is { } text)
                return text;
        }

        foreach (var key in keys)
        {
            var property = data.Properties()
                               .FirstOrDefault(item => string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
            if (Text(property?.Value) is { } text)
                return text;
        }

        return null;
    }

    private static JObject? AsObject(JToken? token)
    {
        if (token is JObject obj)
            return obj;

        var text = Text(token);
        if (text is null)
            return null;

        try
        {
            return JObject.Parse(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeKeys(JObject data)
    {
        var keys = data.Properties().Select(property => property.Name).Take(8).ToList();
        return keys.Count == 0 ? "none" : string.Join(", ", keys);
    }

    private static double? Number(JToken? token)
    {
        var text = Text(token);
        if (text is null)
            return null;

        return NumberFromText(text);
    }

    private static double? NumberFromText(string text)
    {
        text = text.Replace(",", string.Empty, StringComparison.Ordinal)
                   .Replace("%", string.Empty, StringComparison.Ordinal)
                   .Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? NumberFirst(JObject data, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Number(data[key]) is { } number)
                return number;
        }

        foreach (var key in keys)
        {
            var property = data.Properties()
                               .FirstOrDefault(item => string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
            if (Number(property?.Value) is { } number)
                return number;
        }

        return null;
    }

    private static string PercentText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0%";

        return value.Contains('%', StringComparison.Ordinal) ? value : value + "%";
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

    private static void DrawText(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color, float opacity)
    {
        _ = opacity;
        drawList.AddText(pos + new Vector2(1, 1), Color(new Vector4(0, 0, 0, 0.72f)), text);
        drawList.AddText(pos, Color(color), text);
    }

    private static void DrawNamePrivacyMask(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color,
                                            float opacity, float scale)
    {
        var textSize = ImGui.CalcTextSize(text);
        var hash = StableHash(text);
        var cursor = pos.X;
        var baselineY = pos.Y + 2 * scale;
        var textHeight = Math.Max(10 * scale, textSize.Y - 3 * scale);
        var wordTopPadding = 2.2f * scale;
        var spaceWidth = Math.Max(5 * scale, ImGui.CalcTextSize(" ").X);

        drawList.AddRectFilled(pos + new Vector2(-1 * scale, 2 * scale),
                               pos + new Vector2(Math.Max(30 * scale, textSize.X + 2 * scale), textHeight + 6 * scale),
                               Color(new Vector4(0f, 0f, 0f, 0.12f * opacity)), 5 * scale);

        var wordStart = 0;
        while (wordStart < text.Length)
        {
            if (char.IsWhiteSpace(text[wordStart]))
            {
                cursor += spaceWidth;
                wordStart++;
                continue;
            }

            var wordEnd = wordStart;
            while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd]))
                wordEnd++;

            var word = text[wordStart..wordEnd];
            var wordWidth = Math.Max(10 * scale, ImGui.CalcTextSize(word).X);
            var wordHash = RotateLeft(hash ^ StableHash(word), wordStart);
            var wordTop = new Vector2(cursor - 2.5f * scale, baselineY + wordTopPadding);
            var wordBottom = new Vector2(cursor + wordWidth + 2.5f * scale, baselineY + textHeight - 1.5f * scale);

            DrawSoftBlurBand(drawList, wordTop, wordBottom, color, opacity, scale);

            var charX = cursor;
            for (var i = 0; i < word.Length; i++)
            {
                var character = word[i].ToString();
                var charWidth = Math.Max(4 * scale, ImGui.CalcTextSize(character).X);
                var characterHash = RotateLeft(wordHash, i * 7);
                var yJitter = ((((characterHash >> 3) & 0x7) - 3) * 0.22f) * scale;
                var lobeTop = new Vector2(charX - 1.3f * scale, baselineY + 3.1f * scale + yJitter);
                var lobeBottom = new Vector2(charX + charWidth + 1.3f * scale, baselineY + textHeight - 2.6f * scale + yJitter);
                var alpha = 0.13f + ((characterHash & 0x7) / 80f);

                drawList.AddRectFilled(lobeTop - new Vector2(1.6f * scale, 1.2f * scale),
                                       lobeBottom + new Vector2(1.6f * scale, 1.2f * scale),
                                       Color(WithOpacity(color, opacity * 0.045f)), 5 * scale);
                drawList.AddRectFilled(lobeTop, lobeBottom,
                                       Color(WithOpacity(color, opacity * alpha)), 4 * scale);

                charX += charWidth;
            }

            cursor += wordWidth + spaceWidth;
            wordStart = wordEnd;
        }
    }

    private static void DrawSoftBlurBand(ImDrawListPtr drawList, Vector2 topLeft, Vector2 bottomRight,
                                         Vector4 color, float opacity, float scale)
    {
        drawList.AddRectFilled(topLeft - new Vector2(3.4f * scale, 2.5f * scale),
                               bottomRight + new Vector2(3.4f * scale, 2.5f * scale),
                               Color(WithOpacity(color, opacity * 0.030f)), 7 * scale);
        drawList.AddRectFilled(topLeft - new Vector2(2.0f * scale, 1.4f * scale),
                               bottomRight + new Vector2(2.0f * scale, 1.4f * scale),
                               Color(WithOpacity(color, opacity * 0.055f)), 6 * scale);
        drawList.AddRectFilled(topLeft,
                               bottomRight,
                               Color(WithOpacity(color, opacity * 0.10f)), 5 * scale);
        drawList.AddRectFilled(topLeft + new Vector2(1.5f * scale, 2.0f * scale),
                               new Vector2(bottomRight.X - 1.5f * scale, topLeft.Y + 3.2f * scale),
                               Color(WithOpacity(new Vector4(1f, 1f, 1f, 0.12f), opacity)), 3 * scale);
    }

    private static void DrawTextRight(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color, float opacity)
    {
        var size = ImGui.CalcTextSize(text);
        DrawText(drawList, new Vector2(pos.X - size.X, pos.Y), text, color, opacity);
    }

    private static string ShortenText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var trimmed = text;
        while (trimmed.Length > 0 && ImGui.CalcTextSize(trimmed + ellipsis).X > maxWidth)
            trimmed = trimmed[..^1];

        return trimmed.Length == 0 ? ellipsis : trimmed + ellipsis;
    }

    private static uint StableHash(string text)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var character in text)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static uint RotateLeft(uint value, int count)
    {
        count &= 31;
        return (value << count) | (value >> (32 - count));
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private static string FormatNumber(long value)
    {
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : duration.ToString(@"m\:ss", CultureInfo.CurrentCulture);
    }

    private static uint Color(Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static Vector4 WithOpacity(Vector4 color, float opacity)
    {
        return new Vector4(color.X, color.Y, color.Z, color.W * opacity);
    }

    private static Vector4 WithTabOpacity(Vector4 color, float opacity)
    {
        return new Vector4(color.X, color.Y, color.Z, color.W * MathF.Max(opacity, 0.85f));
    }

    private static Vector4 Lerp(Vector4 from, Vector4 to, float amount)
    {
        return from + (to - from) * amount;
    }

    private static Vector4 Rgb(int red, int green, int blue)
    {
        return new Vector4(red / 255f, green / 255f, blue / 255f, 0.92f);
    }

    private enum MeterModeId
    {
        Damage,
        Tank,
        Healing,
    }

    private enum RowColumnKind
    {
        PrimaryRate,
        DamageTotal,
        DamagePercent,
        HealTotal,
        HealPercent,
        OverhealPercent,
        DamageTaken,
        HealedReceived,
        Deaths,
        CritPercent,
        Swings,
        MaxHit,
    }

    private sealed record MeterMode(MeterModeId Id, string Label, string ShortLabel)
    {
        public static IReadOnlyList<MeterMode> All { get; } =
        [
            new(MeterModeId.Damage, "Damage", "DPS"),
            new(MeterModeId.Tank, "Taken", "TANK"),
            new(MeterModeId.Healing, "Healing", "HEAL"),
        ];
    }

    private sealed record RowColumn(string Label, float Width, RowColumnKind Kind);

    private sealed record RowColumnLayout(RowColumn Spec, float Right);

    private sealed record RowLayout(float RowLeft, float InnerPad, float NameStart, float NameRight,
                                    IReadOnlyList<RowColumnLayout> Columns);

    private readonly record struct Rect(Vector2 Min, Vector2 Max)
    {
        public bool Contains(Vector2 point)
        {
            return point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;
        }
    }

    private sealed record DpsSnapshot(
        string ContentName,
        string TargetName,
        bool IsActive,
        TimeSpan Duration,
        double EncounterDps,
        double EncounterHps,
        IReadOnlyList<DpsRow> Rows);

    private sealed record DpsRow(
        string Name,
        string? OwnerName,
        long Damage,
        double EncounterDps,
        string DamagePercent,
        long Healed,
        double EncounterHps,
        string HealPercent,
        string OverhealPercent,
        long DamageTaken,
        int Swings,
        string CritPercent,
        string MaxHit,
        int Deaths,
        uint? JobId,
        bool IsPlayer,
        bool IsPet)
    {
        public DpsRow Merge(DpsRow pet)
        {
            return this with
            {
                Damage = Damage + pet.Damage,
                EncounterDps = EncounterDps + pet.EncounterDps,
                DamagePercent = AddPercents(DamagePercent, pet.DamagePercent),
                Healed = Healed + pet.Healed,
                EncounterHps = EncounterHps + pet.EncounterHps,
                HealPercent = AddPercents(HealPercent, pet.HealPercent),
                DamageTaken = DamageTaken + pet.DamageTaken,
                Swings = Swings + pet.Swings,
                Deaths = Deaths + pet.Deaths,
                JobId = JobId ?? pet.JobId,
                IsPlayer = IsPlayer || pet.IsPlayer,
            };
        }
    }

    private static string AddPercents(string left, string right)
    {
        var value = (ParsePercent(left) + ParsePercent(right)).ToString("0.#", CultureInfo.CurrentCulture);
        return value + "%";
    }

    private static double ParsePercent(string percent)
    {
        return double.TryParse(percent.Replace("%", string.Empty, StringComparison.Ordinal).Trim(),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
