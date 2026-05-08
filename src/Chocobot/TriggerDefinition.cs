using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Chocobot;

internal sealed class TriggerDefinition
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("zone")]
    public string? Zone { get; set; }

    [JsonProperty("source")]
    public TriggerSource Source { get; set; } = TriggerSource.LogLine;

    [JsonProperty("eventType")]
    public string? EventType { get; set; }

    [JsonProperty("ids")]
    public List<string> Ids { get; set; } = [];

    [JsonProperty("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonProperty("targetSelf")]
    public bool TargetSelf { get; set; }

    [JsonProperty("targetNotSelf")]
    public bool TargetNotSelf { get; set; }

    [JsonProperty("roles")]
    public HashSet<string> Roles { get; set; } = [];

    [JsonProperty("notRoles")]
    public HashSet<string> NotRoles { get; set; } = [];

    [JsonProperty("jobs")]
    public HashSet<string> Jobs { get; set; } = [];

    [JsonProperty("notJobs")]
    public HashSet<string> NotJobs { get; set; } = [];

    [JsonProperty("stateConditions")]
    public Dictionary<string, bool> StateConditions { get; set; } = [];

    [JsonProperty("stateUpdates")]
    public Dictionary<string, bool> StateUpdates { get; set; } = [];

    [JsonProperty("silent")]
    public bool Silent { get; set; }

    [JsonProperty("info")]
    public string? InfoText { get; set; }

    [JsonProperty("alert")]
    public string? AlertText { get; set; }

    [JsonProperty("duration")]
    public float DurationSeconds { get; set; } = 5f;

    [JsonProperty("countdown")]
    public float CountdownSeconds { get; set; }

    [JsonProperty("countdownSeconds")]
    private float CountdownSecondsAlias
    {
        set => CountdownSeconds = value;
    }

    [JsonProperty("speak")]
    public bool Speak { get; set; } = true;

    [JsonProperty("suppress")]
    public float SuppressSeconds { get; set; }

    [JsonProperty("suppressSeconds")]
    private float SuppressSecondsAlias
    {
        set => SuppressSeconds = value;
    }

    [JsonIgnore]
    public Regex? CompiledRegex { get; private set; }

    [JsonIgnore]
    public HashSet<string> NormalizedIds { get; private set; } = [];

    [JsonIgnore]
    public bool HasStructuredCriteria =>
        !string.IsNullOrWhiteSpace(EventType)
        || NormalizedIds.Count > 0
        || TargetSelf
        || TargetNotSelf
        || Roles.Count > 0
        || NotRoles.Count > 0
        || Jobs.Count > 0
        || NotJobs.Count > 0
        || StateConditions.Count > 0;

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = "Trigger is missing id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Pattern) && Ids.Count == 0)
        {
            error = $"{Id} is missing pattern or structured ids.";
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(Pattern))
                CompiledRegex = new Regex(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            NormalizedIds = Ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(NormalizeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DurationSeconds = Math.Clamp(DurationSeconds, 1f, 30f);
            CountdownSeconds = Math.Clamp(CountdownSeconds, 0f, 120f);
            SuppressSeconds = Math.Clamp(SuppressSeconds, 0f, 120f);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{Id} regex failed: {ex.Message}";
            return false;
        }
    }

    public static string NormalizeId(string id)
    {
        var normalized = id.Trim().TrimStart('0').ToUpperInvariant();
        return normalized.Length == 0 ? "0" : normalized;
    }
}

internal enum TriggerSource
{
    LogLine,
    ChangeZone,
}
