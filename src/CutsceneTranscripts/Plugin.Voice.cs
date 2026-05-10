using FFXIVClientStructs.FFXIV.Client.Sound;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Starts a short delayed sampling window for voice audio that may begin just after text appears.
    /// </summary>
    private void StartVoiceCaptureProbe(int entryIndex)
    {
        var now = DateTimeOffset.Now;
        voiceCaptureProbes.Add(new VoiceCaptureProbe
        {
            EntryIndex = entryIndex,
            EndsAt = now + TimeSpan.FromSeconds(1),
            NextSampleAt = now
        });
    }

    /// <summary>
    /// Attempts to identify the active voice line associated with the latest captured dialogue.
    /// </summary>
    private VoiceClipRef? TryCaptureVoiceClip()
    {
        var candidates = ReadActiveSoundCandidates();
        var candidate = TryFindPlayableVoiceClipCandidate(candidates);
        if (candidate != null)
            return new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber);

        candidate = TryFindAnyVoiceClipCandidate(candidates);
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
    /// Filters active sounds to likely cutscene voice assets and excludes numbered non-voice sound effects.
    /// </summary>
    private static bool IsVoiceCandidate(VoiceSoundCandidate candidate)
    {
        if (candidate.SoundNumber != 0)
            return false;

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

    /// <summary>
    /// Replays a captured voice asset through the game sound manager without modifying transcript state.
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
            var soundData = soundManager->PlayCutsceneVoSound(voiceClip.Path);
            if (soundData == null)
            {
                soundData = soundManager->PlaySound(
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
            }

            if (soundData == null)
                return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to replay voice clip {Path}", voiceClip.Path);
        }
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

        var candidate = TryFindPlayableVoiceClipCandidate(candidates);
        if (candidate != null)
        {
            entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber) };
            return;
        }

        if (entry.VoiceClip != null)
            return;

        candidate = TryFindAnyVoiceClipCandidate(candidates);
        if (candidate == null)
            return;

        entries[probe.EntryIndex] = entry with { VoiceClip = new VoiceClipRef(candidate.Value.Path, candidate.Value.SoundNumber, CanReplay: false) };
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
