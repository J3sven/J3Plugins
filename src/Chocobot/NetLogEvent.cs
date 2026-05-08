namespace Chocobot;

internal sealed class NetLogEvent
{
    private NetLogEvent(string rawLine, string? eventType, string? id, string? sourceName, string? targetName)
    {
        RawLine = rawLine;
        EventType = eventType;
        Id = id;
        SourceName = sourceName;
        TargetName = targetName;
    }

    public string RawLine { get; }

    public string? EventType { get; }

    public string? Id { get; }

    public string? SourceName { get; }

    public string? TargetName { get; }

    public string? NormalizedId => Id is null ? null : TriggerDefinition.NormalizeId(Id);

    public static NetLogEvent Parse(string line)
    {
        var fields = line.Split('|');
        if (fields.Length == 0)
            return new NetLogEvent(line, null, null, null, null);

        return fields[0] switch
        {
            "20" => FromFields(line, "StartsUsing", Field(fields, 4), Field(fields, 3), Field(fields, 7)),
            "21" or "22" => FromFields(line, "Ability", Field(fields, 4), Field(fields, 3), Field(fields, 7)),
            "26" => FromFields(line, "GainsEffect", Field(fields, 2), Field(fields, 6), Field(fields, 8)),
            "30" => FromFields(line, "LosesEffect", Field(fields, 2), Field(fields, 6), Field(fields, 8)),
            "27" => FromFields(line, "HeadMarker", Field(fields, 6), null, Field(fields, 3)),
            "35" => FromFields(line, "Tether", Field(fields, 6), Field(fields, 3), Field(fields, 5)),
            _ => new NetLogEvent(line, null, null, null, null),
        };
    }

    private static NetLogEvent FromFields(string rawLine, string eventType, string? id, string? sourceName, string? targetName)
    {
        return new NetLogEvent(
            rawLine,
            eventType,
            EmptyToNull(id),
            EmptyToNull(sourceName),
            EmptyToNull(targetName));
    }

    private static string? Field(string[] fields, int index)
    {
        return index >= 0 && index < fields.Length ? fields[index] : null;
    }

    private static string? EmptyToNull(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
