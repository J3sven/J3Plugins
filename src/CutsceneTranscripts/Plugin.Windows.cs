using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    private const float TranscriptButtonOutsideMargin = 0f;
    private const float TranscriptButtonTopOffset = 12f;

    /// <summary>
    /// Positions the native cutscene transcript button next to the Talk window when it should be visible.
    /// </summary>
    private void UpdateTranscriptOpenButton(bool visible)
    {
        if (!visible)
        {
            transcriptOpenButton.SetButtonState(false, Vector2.Zero);
            return;
        }

        var bounds = talkWindowBounds;
        var currentBounds = bounds.GetValueOrDefault();
        var anchored = bounds is not null
            && DateTimeOffset.Now - lastTalkWindowBoundsAt <= TimeSpan.FromSeconds(1.5);
        var buttonSize = TranscriptOpenButtonAddon.ButtonSize;
        var windowPos = anchored
            ? new Vector2(currentBounds.Position.X + currentBounds.Size.X + TranscriptButtonOutsideMargin, currentBounds.Position.Y + TranscriptButtonTopOffset)
            : new Vector2(Configuration.ButtonX, Configuration.ButtonY);

        transcriptOpenButton.SetButtonState(true, windowPos);
    }

    /// <summary>
    /// Draws the settings window contents; visibility policy is enforced by <see cref="ConfigWindow"/>.
    /// </summary>
    private void DrawConfigWindowContents()
    {
        var changed = false;
        changed |= Checkbox("Enabled", Configuration.Enabled, value => Configuration.Enabled = value);
        changed |= Checkbox("Show transcript button during cutscenes", Configuration.ShowButtonDuringCutscenes, value => Configuration.ShowButtonDuringCutscenes = value);
        changed |= Checkbox("Keep last transcript after cutscene", Configuration.KeepLastTranscriptAfterCutscene, value => Configuration.KeepLastTranscriptAfterCutscene = value);
        changed |= Checkbox("Open transcript when cutscene ends", Configuration.OpenTranscriptWhenCutsceneEnds, value => Configuration.OpenTranscriptWhenCutsceneEnds = value);

        if (changed)
        {
            ClampConfiguration();
            TrimEntries();
            Configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Recorded lines: {entries.Count}");
        if (ImGui.Button("Clear Transcript"))
            ClearTranscript();
    }

    private static bool Checkbox(string label, bool value, Action<bool> setter)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setter(value);
        return true;
    }
}
