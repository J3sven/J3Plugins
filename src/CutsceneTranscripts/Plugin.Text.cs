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
