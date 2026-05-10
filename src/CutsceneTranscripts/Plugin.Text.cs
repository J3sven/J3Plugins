using Dalamud.Bindings.ImGui;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Reads a nullable native text node into cleaned plain text.
    /// </summary>
    private static string ReadTextNode(AtkTextNode* node)
    {
        return node == null
            ? string.Empty
            : CleanText(node->NodeText.AsDalamudSeString().TextValue);
    }

    /// <summary>
    /// Adds non-empty cleaned text while preserving first occurrence order.
    /// </summary>
    private static void AddText(List<string> texts, string? text)
    {
        text = CleanText(text);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (texts.Any(existing => string.Equals(existing, text, StringComparison.Ordinal)))
            return;

        texts.Add(text);
    }

    /// <summary>
    /// Normalizes captured UI text by trimming blank lines and unifying line endings.
    /// </summary>
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

    /// <summary>
    /// Wraps transcript text to measured ImGui pixel width for custom speech-bubble rendering.
    /// </summary>
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

    /// <summary>
    /// Compares captured text after whitespace normalization to avoid duplicate speaker/body fields.
    /// </summary>
    private static bool TextEquivalent(string left, string right)
    {
        return string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Builds the plain-text clipboard export for the current transcript.
    /// </summary>
    private string BuildTranscriptText()
    {
        return string.Join(
            Environment.NewLine,
            entries.Select(entry => string.IsNullOrWhiteSpace(entry.Speaker)
                ? entry.Text
                : $"{entry.Speaker}: {entry.Text}"));
    }
}
