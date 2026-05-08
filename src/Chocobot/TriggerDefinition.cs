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

    [JsonProperty("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonProperty("targetSelf")]
    public bool TargetSelf { get; set; }

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

    public bool Compile(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Id))
        {
            error = "Trigger is missing id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Pattern))
        {
            error = $"{Id} is missing pattern.";
            return false;
        }

        try
        {
            CompiledRegex = new Regex(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
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
}

internal enum TriggerSource
{
    LogLine,
    ChangeZone,
}
