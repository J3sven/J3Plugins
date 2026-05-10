using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// One visible transcript row captured from dialogue text or a player choice.
    /// </summary>
    private sealed record TranscriptEntry(DateTimeOffset Timestamp, string? Speaker, string Text, VoiceClipRef? VoiceClip);

    /// <summary>
    /// Minimal sound reference needed to replay or explain why a voice line cannot be replayed.
    /// </summary>
    private sealed record VoiceClipRef(string Path, uint SoundNumber, bool CanReplay = true);

    /// <summary>
    /// Snapshot of a native active sound node used by the voice matching heuristics.
    /// </summary>
    private readonly record struct VoiceSoundCandidate(
        string Path,
        uint SoundNumber,
        float Elapsed,
        float Volume,
        SoundVolumeCategory VolumeCategory,
        bool IsPlaying,
        bool IsLoading,
        bool IsPositional,
        bool IsAutoRelease,
        int MidiNote);

    /// <summary>
    /// Last observed screen rectangle for the game's Talk window.
    /// </summary>
    private readonly record struct TalkWindowBounds(Vector2 Position, Vector2 Size);

    /// <summary>
    /// Delayed voice lookup for transcript entries whose voice asset starts after text capture.
    /// </summary>
    private sealed class VoiceCaptureProbe
    {
        public int EntryIndex { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        public DateTimeOffset NextSampleAt { get; set; }
    }

    /// <summary>
    /// Dalamud Windowing wrapper for the transcript window that may remain visible during cutscenes.
    /// </summary>
    private sealed class TranscriptWindow : Window
    {
        private readonly Plugin plugin;

        public TranscriptWindow(Plugin plugin)
            : base("Cutscene Transcript")
        {
            this.plugin = plugin;
            Size = new Vector2(plugin.Configuration.WindowWidth, plugin.Configuration.WindowHeight);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void PreDraw()
        {
            plugin.PreDrawTranscriptWindow();
        }

        public override void Draw()
        {
            plugin.DrawTranscriptWindowContents();
        }

        public override void PostDraw()
        {
            plugin.PostDrawTranscriptWindow();
        }
    }

    /// <summary>
    /// Dalamud Windowing wrapper for regular settings UI, hidden while cutscenes are active.
    /// </summary>
    private sealed class ConfigWindow : Window
    {
        private readonly Plugin plugin;

        public ConfigWindow(Plugin plugin)
            : base("Cutscene Transcript Settings", ImGuiWindowFlags.AlwaysAutoResize, true)
        {
            this.plugin = plugin;
        }

        public override bool DrawConditions()
        {
            return !plugin.IsCutsceneActive();
        }

        public override void Draw()
        {
            plugin.DrawConfigWindowContents();
        }
    }

    /// <summary>
    /// Cached state for one visible choice addon until it is submitted or finalized.
    /// </summary>
    private sealed class ChoiceState
    {
        public string AddonName { get; init; } = string.Empty;
        public List<string> Options { get; } = [];
        public int SelectedIndex { get; set; } = -1;
        public int ListItemIndex { get; set; } = -1;
        public int LastEventParam { get; set; } = -1;
        public bool LastEventParamMayBeChoiceIndex { get; set; } = true;
        public DateTimeOffset LastSeenAt { get; set; }
        public bool SubmitSeen { get; set; }
        public bool Recorded { get; set; }
    }
}
