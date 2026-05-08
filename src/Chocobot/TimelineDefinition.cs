using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Chocobot;

internal sealed class TimelineDefinition
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("zone")]
    public string? Zone { get; set; }

    [JsonProperty("syncs")]
    public List<TimelineSyncDefinition> Syncs { get; set; } = [];

    [JsonProperty("entries")]
    public List<TimelineEntryDefinition> Entries { get; set; } = [];

    [JsonProperty("cues")]
    public List<TimelineCueDefinition> Cues { get; set; } = [];

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = "Timeline is missing id.";
            return false;
        }

        foreach (var sync in Syncs)
        {
            if (!sync.Compile(out error))
                return false;
        }

        foreach (var entry in Entries)
        {
            if (!entry.Compile(out error))
                return false;
        }

        foreach (var cue in Cues)
        {
            if (!cue.Compile(out error))
                return false;
        }

        return true;
    }
}

internal sealed class TimelineSyncDefinition
{
    [JsonProperty("time")]
    public float TimeSeconds { get; set; }

    [JsonProperty("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonIgnore]
    public Regex? CompiledRegex { get; private set; }

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            error = "Timeline sync is missing pattern.";
            return false;
        }

        try
        {
            CompiledRegex = new Regex(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            TimeSeconds = Math.Clamp(TimeSeconds, 0f, 3600f);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Timeline sync regex failed: {ex.Message}";
            return false;
        }
    }
}

internal sealed class TimelineEntryDefinition
{
    [JsonProperty("time")]
    public float TimeSeconds { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Text))
        {
            error = "Timeline entry is missing text.";
            return false;
        }

        TimeSeconds = Math.Clamp(TimeSeconds, 0f, 3600f);
        return true;
    }
}

internal sealed class TimelineCueDefinition
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("time")]
    public float TimeSeconds { get; set; }

    [JsonProperty("before")]
    public float BeforeSeconds { get; set; }

    [JsonProperty("alert")]
    public string AlertText { get; set; } = string.Empty;

    [JsonProperty("duration")]
    public float DurationSeconds { get; set; } = 5f;

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = "Timeline cue is missing id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AlertText))
        {
            error = $"{Id} is missing alert text.";
            return false;
        }

        TimeSeconds = Math.Clamp(TimeSeconds, 0f, 3600f);
        BeforeSeconds = Math.Clamp(BeforeSeconds, 0f, 120f);
        DurationSeconds = Math.Clamp(DurationSeconds, 1f, 30f);
        return true;
    }
}
