using FFXIVClientStructs.FFXIV.Client.Sound;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    private const float VoiceAttachMaxElapsedSeconds = 0.75f;
    private SoundData* activeReplaySoundData;
    private VoiceClipRef? activeReplayVoiceClip;

    /// <summary>
    /// Starts a short delayed sampling window for voice audio that may begin just after text appears.
    /// </summary>
    private void StartVoiceCaptureProbe(int entryIndex, IReadOnlyList<VoiceSoundCandidate> initialCandidates)
    {
        var now = DateTimeOffset.Now;
        voiceCaptureProbes.Add(new VoiceCaptureProbe
        {
            EntryIndex = entryIndex,
            EndsAt = now + TimeSpan.FromSeconds(1),
            NextSampleAt = now,
            StaleVoiceKeys = initialCandidates
                .Where(IsVoiceCandidate)
                .Where(candidate => candidate.Elapsed > VoiceAttachMaxElapsedSeconds)
                .Select(GetVoiceCandidateKey)
                .ToHashSet(StringComparer.Ordinal)
        });
    }

    /// <summary>
    /// Attempts to identify the active voice line associated with the latest captured dialogue.
    /// </summary>
    private VoiceClipRef? TryCaptureVoiceClip(IReadOnlyList<VoiceSoundCandidate> candidates)
    {
        var currentDialogueCandidates = candidates.Where(IsRecentlyStartedVoiceCandidate);
        var candidate = TryFindPlayableVoiceClipCandidate(currentDialogueCandidates);
        if (candidate != null)
            return new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber);

        candidate = TryFindAnyVoiceClipCandidate(currentDialogueCandidates);
        if (candidate == null)
            return null;

        return new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber, CanReplay: false);
    }

    private static VoiceSoundCandidate? TryFindPlayableVoiceClipCandidate(IEnumerable<VoiceSoundCandidate> candidates)
    {
        foreach (var candidate in candidates
                     .Where(IsVoiceCandidate)
                     .Where(IsReliableVoiceCandidate)
                     .OrderByDescending(candidate => candidate.IsPlaying)
                     .ThenBy(candidate => candidate.Elapsed))
            return candidate;

        return null;
    }

    private static VoiceSoundCandidate? TryFindAnyVoiceClipCandidate(IEnumerable<VoiceSoundCandidate> candidates)
    {
        foreach (var candidate in candidates
                     .Where(IsVoiceCandidate)
                     .OrderByDescending(IsReliableVoiceCandidate)
                     .ThenByDescending(candidate => candidate.IsPlaying)
                     .ThenBy(candidate => candidate.Elapsed))
            return candidate;

        return null;
    }

    /// <summary>
    /// Filters active sounds to likely cutscene voice assets.
    /// </summary>
    private static bool IsVoiceCandidate(VoiceSoundCandidate candidate)
    {
        if (candidate.Volume <= 0.001f)
            return false;

        return candidate.Path.Contains("/sound/VOICE", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/sound/VOICEM", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/sound/VOICEF", StringComparison.OrdinalIgnoreCase)
            || candidate.Path.Contains("/vo_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReliableVoiceCandidate(VoiceSoundCandidate candidate)
    {
        return candidate.IsPositional || candidate.IsPlaying;
    }

    private static bool IsRecentlyStartedVoiceCandidate(VoiceSoundCandidate candidate)
    {
        return IsVoiceCandidate(candidate) && candidate.Elapsed <= VoiceAttachMaxElapsedSeconds;
    }

    private static bool IsNewVoiceCandidateForProbe(VoiceCaptureProbe probe, VoiceSoundCandidate candidate)
    {
        return IsRecentlyStartedVoiceCandidate(candidate) && !probe.StaleVoiceKeys.Contains(GetVoiceCandidateKey(candidate));
    }

    private static string GetVoiceCandidateKey(VoiceSoundCandidate candidate)
    {
        return $"{candidate.Path}\n{candidate.SoundNumber}";
    }

    /// <summary>
    /// Starts replay for a voice clip, or stops it when that same replay is already active.
    /// </summary>
    private void ToggleVoiceClipReplay(VoiceClipRef voiceClip)
    {
        if (IsVoiceClipReplayActive(voiceClip))
        {
            StopActiveVoiceReplay();
            return;
        }

        StopActiveVoiceReplay(markChanged: false);
        ReplayVoiceClip(voiceClip);
    }

    /// <summary>
    /// Replays a captured voice asset through the game sound manager and tracks it for abort controls.
    /// </summary>
    private void ReplayVoiceClip(VoiceClipRef voiceClip)
    {
        var soundManager = SoundManager.Instance();
        if (soundManager == null)
        {
            Log.Warning("Could not replay voice clip because SoundManager.Instance() was null.");
            return;
        }

        try
        {
            var soundData = soundManager->PlaySound(
                voiceClip.Path,
                1f,
                0,
                0f,
                0f,
                0f,
                1f,
                0,
                voiceClip.SoundNumber,
                true,
                SoundVolumeCategory.BypassVolumeRules,
                false,
                -1,
                false,
                false,
                false,
                false);

            if (soundData == null && voiceClip.SoundNumber == 0)
            {
                soundData = soundManager->PlayCutsceneVoSound(voiceClip.Path);
            }

            if (soundData == null)
                return;

            activeReplaySoundData = soundData;
            activeReplayVoiceClip = voiceClip;
            MarkTranscriptChanged();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to replay voice clip {Path}", voiceClip.Path);
        }
    }

    /// <summary>
    /// Stops the plugin-owned replay sound without affecting native cutscene voice playback.
    /// </summary>
    private void StopActiveVoiceReplay(bool markChanged = true)
    {
        try
        {
            if (TryGetActiveReplaySoundData(out var soundData))
                soundData->Stop(0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop replayed voice clip.");
        }
        finally
        {
            activeReplaySoundData = null;
            activeReplayVoiceClip = null;
            if (markChanged)
                MarkTranscriptChanged();
        }
    }

    /// <summary>
    /// Reports whether the requested voice clip is the currently active replay.
    /// </summary>
    private bool IsVoiceClipReplayActive(VoiceClipRef voiceClip)
    {
        return activeReplayVoiceClip == voiceClip && TryGetActiveReplaySoundData(out var soundData) && soundData->IsPlaying();
    }

    /// <summary>
    /// Clears stale replay state after the sound naturally ends or leaves the active sound list.
    /// </summary>
    private void RefreshActiveVoiceReplayState()
    {
        if (activeReplaySoundData == null)
            return;

        if (TryGetActiveReplaySoundData(out var soundData) && soundData->IsPlaying())
            return;

        activeReplaySoundData = null;
        activeReplayVoiceClip = null;
        MarkTranscriptChanged();
    }

    /// <summary>
    /// Finds the tracked replay sound only if it is still active and still points to the same voice asset.
    /// </summary>
    private bool TryGetActiveReplaySoundData(out SoundData* soundData)
    {
        soundData = null;
        if (activeReplaySoundData == null || activeReplayVoiceClip is null)
            return false;

        var soundManager = SoundManager.Instance();
        if (soundManager == null)
            return false;

        var current = soundManager->ActiveSoundDataListHead;
        var visited = new HashSet<nint>();
        for (var i = 0; current != null && i < 256; i++)
        {
            var address = (nint)current;
            if (!visited.Add(address))
                break;

            if (current == activeReplaySoundData && IsSameVoiceClip(current, activeReplayVoiceClip))
            {
                soundData = current;
                return true;
            }

            current = (SoundData*)current->Next;
        }

        return false;
    }

    private static bool IsSameVoiceClip(SoundData* soundData, VoiceClipRef voiceClip)
    {
        if (soundData == null || !soundData->IsActive || soundData->GetSoundNumber() != voiceClip.SoundNumber)
            return false;

        var fileName = soundData->GetFileName();
        return fileName.HasValue && string.Equals(fileName.ToString(), voiceClip.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Advances delayed voice probes and attaches a playable voice clip when one becomes visible.
    /// </summary>
    private void ProcessVoiceCaptureProbes()
    {
        if (voiceCaptureProbes.Count == 0)
            return;

        var now = DateTimeOffset.Now;
        for (var i = voiceCaptureProbes.Count - 1; i >= 0; i--)
        {
            var probe = voiceCaptureProbes[i];
            if (now > probe.EndsAt)
            {
                voiceCaptureProbes.RemoveAt(i);
                continue;
            }

            if (now < probe.NextSampleAt)
                continue;

            TryAttachVoiceClipFromSample(probe, ReadActiveSoundCandidates());
            probe.NextSampleAt = now + TimeSpan.FromMilliseconds(250);
        }
    }

    /// <summary>
    /// Updates an existing transcript entry with a better voice candidate from a later audio sample.
    /// </summary>
    private void TryAttachVoiceClipFromSample(VoiceCaptureProbe probe, IReadOnlyList<VoiceSoundCandidate> candidates)
    {
        if (probe.EntryIndex < 0 || probe.EntryIndex >= entries.Count)
            return;

        var entry = entries[probe.EntryIndex];
        if (entry.VoiceClip?.CanReplay == true)
            return;

        var currentDialogueCandidates = candidates.Where(candidate => IsNewVoiceCandidateForProbe(probe, candidate));
        var candidate = TryFindPlayableVoiceClipCandidate(currentDialogueCandidates);
        if (candidate != null)
        {
            entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber) };
            MarkTranscriptChanged();
            return;
        }

        if (entry.VoiceClip != null)
            return;

        candidate = TryFindAnyVoiceClipCandidate(currentDialogueCandidates);
        if (candidate == null)
            return;

        entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber, CanReplay: false) };
        MarkTranscriptChanged();
    }

    /// <summary>
    /// Reads the active sound list into managed candidate records while guarding against cycles.
    /// </summary>
    private List<VoiceSoundCandidate> ReadActiveSoundCandidates()
    {
        var candidates = new List<VoiceSoundCandidate>();
        var soundManager = SoundManager.Instance();
        if (soundManager == null)
            return candidates;

        var current = soundManager->ActiveSoundDataListHead;
        var visited = new HashSet<nint>();
        for (var i = 0; current != null && i < 256; i++)
        {
            var address = (nint)current;
            if (!visited.Add(address))
                break;

            TryAddSoundCandidate(current, candidates);
            current = (SoundData*)current->Next;
        }

        return candidates
            .OrderBy(candidate => candidate.Elapsed)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Copies the sound fields needed for voice matching from an active native sound node.
    /// </summary>
    private static void TryAddSoundCandidate(SoundData* soundData, List<VoiceSoundCandidate> candidates)
    {
        if (soundData == null || !soundData->IsActive)
            return;

        var fileName = soundData->GetFileName();
        if (!fileName.HasValue)
            return;

        var path = fileName.ToString();
        if (string.IsNullOrWhiteSpace(path))
            return;

        candidates.Add(new VoiceSoundCandidate(
            path,
            soundData->GetSoundNumber(),
            soundData->GetElapsedTime(),
            soundData->GetVolume(),
            soundData->VolumeCategory,
            soundData->IsPlaying(),
            soundData->GetIsLoadingSoundResource(),
            soundData->GetIsPositional(),
            soundData->GetIsAutoReleaseEnabled(),
            soundData->GetMidiNote()));
    }
}
