using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Samples the game's Talk addon while a cutscene is active and records new dialogue text.
    /// </summary>
    private void OnTalkPostUpdate(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull || !args.Addon.IsVisible)
        {
            talkWindowBounds = null;
            return;
        }

        var cutsceneActive = IsCutsceneActive();
        var addon = (AddonTalk*)args.Addon.Address;
        if (cutsceneActive)
            UpdateTalkWindowBounds(addon);

        if (!Configuration.Enabled || !cutsceneActive)
            return;

        CaptureTalkAddon(addon);
    }

    /// <summary>
    /// Clears transient Talk addon state when the addon is destroyed.
    /// </summary>
    private void OnTalkFinalize(AddonEvent eventType, AddonArgs args)
    {
        lastObservedTalkKey = null;
        talkWindowBounds = null;
    }

    /// <summary>
    /// Stores the visible Talk window bounds so the transcript button can anchor to the game dialogue box.
    /// </summary>
    private void UpdateTalkWindowBounds(AddonTalk* addon)
    {
        var root = addon->RootNode;
        if (root == null)
        {
            talkWindowBounds = null;
            return;
        }

        var width = root->Width * root->ScaleX;
        var height = root->Height * root->ScaleY;
        if (width <= 0 || height <= 0)
        {
            talkWindowBounds = null;
            return;
        }

        talkWindowBounds = new TalkWindowBounds(new Vector2(root->ScreenX, root->ScreenY), new Vector2(width, height));
        lastTalkWindowBoundsAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// Returns whether a Talk window was seen recently enough to be considered visible this frame.
    /// </summary>
    private bool IsTalkWindowVisible()
    {
        return talkWindowBounds is not null
            && DateTimeOffset.Now - lastTalkWindowBoundsAt <= VisibleAddonGracePeriod;
    }

    /// <summary>
    /// Reads all known dialogue/speaker fields from the Talk addon and records the line if it changed.
    /// </summary>
    private void CaptureTalkAddon(AddonTalk* addon)
    {
        if (addon == null)
            return;

        var texts = new List<string>();
        AddText(texts, addon->String268.AsDalamudSeString().TextValue);
        AddText(texts, addon->String2D0.AsDalamudSeString().TextValue);
        AddText(texts, addon->String338.AsDalamudSeString().TextValue);
        AddText(texts, addon->String408.AsDalamudSeString().TextValue);
        AddText(texts, addon->String470.AsDalamudSeString().TextValue);
        AddText(texts, addon->String4D8.AsDalamudSeString().TextValue);
        AddText(texts, addon->String540.AsDalamudSeString().TextValue);
        AddText(texts, ReadTextNode(addon->AtkTextNode220));
        AddText(texts, ReadTextNode(addon->AtkTextNode228));
        AddText(texts, ReadTextNode(addon->AtkTextNode238));
        AddText(texts, ReadTextNode(addon->AtkTextNode240));
        AddText(texts, ReadTextNode(addon->AtkTextNode248));

        if (texts.Count == 0)
            return;

        var talkKey = string.Join("\n", texts);
        if (string.Equals(talkKey, lastObservedTalkKey, StringComparison.Ordinal))
            return;

        lastObservedTalkKey = talkKey;
        AddTranscriptEntry(texts);
    }

    /// <summary>
    /// Converts captured Talk addon text candidates into one transcript entry and optional voice clip metadata.
    /// </summary>
    private void AddTranscriptEntry(List<string> texts)
    {
        var body = texts.OrderByDescending(text => text.Length).First();
        string? speaker = null;

        foreach (var candidate in texts)
        {
            if (TextEquivalent(candidate, body) || candidate.Length > 80 || candidate.Contains('\n'))
                continue;

            speaker = candidate;
            break;
        }

        if (string.IsNullOrWhiteSpace(speaker))
            speaker = lastDialogSpeaker;
        else
            lastDialogSpeaker = speaker;

        var entryKey = $"{speaker}\n{body}";
        if (string.Equals(entryKey, lastTranscriptEntryKey, StringComparison.Ordinal))
            return;

        lastTranscriptEntryKey = entryKey;
        voiceCaptureProbes.Clear();
        var voiceCandidates = ReadActiveSoundCandidates();
        var voiceClip = TryCaptureVoiceClip(voiceCandidates);
        entries.Add(new TranscriptEntry(DateTimeOffset.Now, speaker, body, voiceClip));
        TrimEntries();
        MarkTranscriptChanged();
        StartVoiceCaptureProbe(entries.Count - 1, voiceCandidates);
    }

    /// <summary>
    /// Records a player choice in the transcript as a line spoken by the local player.
    /// </summary>
    private void AddChoiceEntry(string choiceText)
    {
        choiceText = CleanText(choiceText);
        if (string.IsNullOrWhiteSpace(choiceText))
            return;

        var playerName = GetPlayerName();
        var entryKey = $"{playerName}\n{choiceText}";
        if (string.Equals(entryKey, lastTranscriptEntryKey, StringComparison.Ordinal))
            return;

        lastTranscriptEntryKey = entryKey;
        voiceCaptureProbes.Clear();
        entries.Add(new TranscriptEntry(DateTimeOffset.Now, playerName, choiceText, null));
        TrimEntries();
        MarkTranscriptChanged();
    }

    private string GetPlayerName()
    {
        var name = objectTable.LocalPlayer?.Name.TextValue;
        return string.IsNullOrWhiteSpace(name)
            ? "Player"
            : name;
    }

    /// <summary>
    /// Keeps the in-memory transcript bounded so long cutscenes do not grow state indefinitely.
    /// </summary>
    private void TrimEntries()
    {
        while (entries.Count > MaxTranscriptEntries)
            entries.RemoveAt(0);
    }

    /// <summary>
    /// Clears the current transcript and all per-cutscene de-duplication/capture state.
    /// </summary>
    private void ClearTranscript()
    {
        entries.Clear();
        choiceStates.Clear();
        voiceCaptureProbes.Clear();
        speakerColors.Clear();
        lastObservedTalkKey = null;
        lastTranscriptEntryKey = null;
        lastDialogSpeaker = null;
        MarkTranscriptChanged();
    }

    /// <summary>
    /// Assigns stable colors to speakers for the current transcript session.
    /// </summary>
    private Vector4 GetSpeakerColor(string speaker)
    {
        if (speakerColors.TryGetValue(speaker, out var color))
            return color;

        color = SpeakerColorPalette[speakerColors.Count % SpeakerColorPalette.Length];
        speakerColors[speaker] = color;
        return color;
    }
}
