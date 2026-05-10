using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Draws the small floating transcript button, anchored to the Talk window when possible.
    /// </summary>
    private void DrawTranscriptIconButton()
    {
        var scale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        var buttonSize = new Vector2(30f * scale, 30f * scale);
        var bounds = talkWindowBounds;
        var currentBounds = bounds.GetValueOrDefault();
        var anchored = bounds is not null
            && DateTimeOffset.Now - lastTalkWindowBoundsAt <= TimeSpan.FromSeconds(1.5);
        var windowPos = anchored
            ? new Vector2(currentBounds.Position.X + currentBounds.Size.X - buttonSize.X - 18f * scale, currentBounds.Position.Y + 12f * scale)
            : new Vector2(Configuration.ButtonX, Configuration.ButtonY);

        ImGui.SetNextWindowPos(windowPos, anchored ? ImGuiCond.Always : ImGuiCond.FirstUseEver);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!ImGui.Begin("##CutsceneTranscriptButton", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        if (ImGui.InvisibleButton("##OpenTranscript", buttonSize))
            transcriptWindow.IsOpen = true;

        DrawTranscriptIcon(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), buttonSize, ImGui.IsItemHovered());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open transcript");

        if (!anchored)
        {
            var pos = ImGui.GetWindowPos();
            if (Vector2.Distance(pos, new Vector2(Configuration.ButtonX, Configuration.ButtonY)) > 0.5f)
            {
                Configuration.ButtonX = pos.X;
                Configuration.ButtonY = pos.Y;
                Configuration.Save();
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Draws the glyph used by the floating transcript button.
    /// </summary>
    private void DrawTranscriptIcon(ImDrawListPtr drawList, Vector2 pos, Vector2 size, bool hovered)
    {
        var scale = size.X / 30f;
        var end = pos + size;
        var rounding = 5f * scale;
        drawList.AddRectFilled(pos + new Vector2(2f * scale, 3f * scale), end + new Vector2(2f * scale, 3f * scale),
                               ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.34f)), rounding);
        drawList.AddRectFilled(pos, end, ImGui.GetColorU32(hovered ? IconFillHover : IconFill), rounding);
        drawList.AddRect(pos, end, ImGui.GetColorU32(IconGold), rounding, ImDrawFlags.RoundCornersAll, 1.2f * scale);

        var pageMin = pos + new Vector2(8f * scale, 6f * scale);
        var pageMax = pos + new Vector2(22f * scale, 24f * scale);
        drawList.AddRectFilled(pageMin, pageMax, ImGui.GetColorU32(new Vector4(0.82f, 0.75f, 0.58f, 1f)), 2f * scale);
        drawList.AddRect(pageMin, pageMax, ImGui.GetColorU32(new Vector4(0.24f, 0.18f, 0.10f, 0.95f)), 2f * scale, ImDrawFlags.RoundCornersAll, 1f * scale);
        for (var i = 0; i < 3; i++)
        {
            var y = pageMin.Y + (6f + i * 4f) * scale;
            drawList.AddLine(new Vector2(pageMin.X + 3f * scale, y), new Vector2(pageMax.X - 3f * scale, y),
                             ImGui.GetColorU32(new Vector4(0.24f, 0.18f, 0.10f, 0.70f)), 1f * scale);
        }
    }

    private void PreDrawTranscriptWindow()
    {
        transcriptWindowScale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        PushTranscriptWindowStyle(transcriptWindowScale);
    }

    /// <summary>
    /// Draws the main transcript window contents; Windowing owns the surrounding ImGui.Begin/End.
    /// </summary>
    private void DrawTranscriptWindowContents()
    {
        DrawTranscriptWindowFrame(transcriptWindowScale);
        DrawTranscriptToolbar(transcriptWindowScale);

        if (entries.Count == 0)
        {
            ImGui.TextDisabled("No dialog has been recorded yet.");
        }
        else
        {
            for (var i = 0; i < entries.Count; i++)
                DrawDialogueEntry(i, entries[i]);

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 24f)
                ImGui.SetScrollHereY(1f);
        }

        var size = ImGui.GetWindowSize();
        if (Vector2.Distance(size, new Vector2(Configuration.WindowWidth, Configuration.WindowHeight)) > 1f)
        {
            Configuration.WindowWidth = Math.Clamp(size.X, 320f, 1200f);
            Configuration.WindowHeight = Math.Clamp(size.Y, 240f, 900f);
            Configuration.Save();
        }
    }

    private void PostDrawTranscriptWindow()
    {
        PopTranscriptWindowStyle();
    }

    /// <summary>
    /// Applies the custom visual style used by the transcript window for the duration of one draw.
    /// </summary>
    private static void PushTranscriptWindowStyle(float scale)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 12f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f * scale, 8f * scale));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, WindowBorder);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.12f, 0.10f, 0.07f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.22f, 0.17f, 0.10f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.17f, 0.10f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.34f, 0.25f, 0.13f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.43f, 0.31f, 0.14f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.62f, 0.48f, 0.25f, 0.62f));
    }

    private static void PopTranscriptWindowStyle()
    {
        ImGui.PopStyleColor(8);
        ImGui.PopStyleVar(5);
    }

    private static void DrawTranscriptWindowFrame(float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var end = pos + size;
        var rounding = 7f * scale;

        drawList.AddRect(pos + new Vector2(1f * scale, 1f * scale), end - new Vector2(1f * scale, 1f * scale),
                         ImGui.GetColorU32(WindowBorderDark), rounding, ImDrawFlags.RoundCornersAll, 2.5f * scale);
        drawList.AddRect(pos + new Vector2(2f * scale, 2f * scale), end - new Vector2(2f * scale, 2f * scale),
                         ImGui.GetColorU32(WindowBorder), rounding, ImDrawFlags.RoundCornersAll, 1f * scale);
    }

    private void DrawTranscriptToolbar(float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var toolbarPos = ImGui.GetCursorScreenPos();
        var toolbarHeight = ImGui.GetFrameHeight() + 8f * scale;
        var toolbarEnd = toolbarPos + new Vector2(ImGui.GetContentRegionAvail().X, toolbarHeight);
        drawList.AddRectFilled(toolbarPos - new Vector2(4f * scale, 2f * scale),
                               toolbarEnd + new Vector2(4f * scale, 2f * scale),
                               ImGui.GetColorU32(ToolbarBg), 5f * scale);

        ImGui.SetCursorScreenPos(toolbarPos + new Vector2(4f * scale, 4f * scale));
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(BuildTranscriptText());

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            ClearTranscript();

        ImGui.SameLine();
        ImGui.TextDisabled($"{entries.Count} line{(entries.Count == 1 ? string.Empty : "s")}");
        ImGui.SetCursorScreenPos(new Vector2(toolbarPos.X, toolbarPos.Y + toolbarHeight + 8f * scale));
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Renders one captured dialogue line as a speech bubble with optional replay control.
    /// </summary>
    private void DrawDialogueEntry(int entryIndex, TranscriptEntry entry)
    {
        var scale = Math.Max(0.85f, ImGui.GetFontSize() / 17f);
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var availableWidth = Math.Max(280f * scale, ImGui.GetContentRegionAvail().X);
        var boxWidth = Math.Max(260f * scale, availableWidth - 8f * scale);
        var boxX = cursor.X + 4f * scale;
        var speakerHeight = string.IsNullOrWhiteSpace(entry.Speaker)
            ? 0f
            : ImGui.GetTextLineHeight() + 2f * scale;
        var speakerOverlap = string.IsNullOrWhiteSpace(entry.Speaker) ? 0f : 8f * scale;
        var boxY = cursor.Y + Math.Max(0f, speakerHeight - speakerOverlap);
        var paddingX = 18f * scale;
        var paddingY = 14f * scale;
        var lineHeight = ImGui.GetTextLineHeight() * 1.18f;
        var replayButtonSize = entry.VoiceClip == null ? 0f : 22f * scale;
        var replaySpace = entry.VoiceClip == null ? 0f : replayButtonSize + 10f * scale;
        var wrapWidth = Math.Max(80f * scale, boxWidth - paddingX * 2f - replaySpace);
        var lines = WrapText(entry.Text, wrapWidth);
        var boxHeight = Math.Max(58f * scale, paddingY * 2f + lines.Count * lineHeight);
        var boxPos = new Vector2(boxX, boxY);
        var boxEnd = boxPos + new Vector2(boxWidth, boxHeight);
        var rounding = 19f * scale;

        drawList.AddRectFilled(boxPos + new Vector2(2f * scale, 3f * scale),
                               boxEnd + new Vector2(2f * scale, 3f * scale),
                               ImGui.GetColorU32(DialogueShadowColor), rounding);
        drawList.AddRectFilled(boxPos, boxEnd, ImGui.GetColorU32(DialogueBoxFill), rounding);
        drawList.AddRectFilled(boxPos + new Vector2(3f * scale, 3f * scale),
                               new Vector2(boxEnd.X - 3f * scale, boxPos.Y + boxHeight * 0.48f),
                               ImGui.GetColorU32(DialogueBoxHighlight), rounding * 0.82f);
        drawList.AddRect(boxPos, boxEnd, ImGui.GetColorU32(DialogueBoxBorder), rounding,
                         ImDrawFlags.RoundCornersAll, 1.35f * scale);

        var textPos = boxPos + new Vector2(paddingX, paddingY);
        for (var i = 0; i < lines.Count; i++)
            drawList.AddText(textPos + new Vector2(0f, i * lineHeight), ImGui.GetColorU32(DialogueTextColor), lines[i]);

        if (entry.VoiceClip is { } voiceClip)
            DrawReplayVoiceButton(drawList, boxEnd - new Vector2(paddingX + replayButtonSize, paddingY + replayButtonSize * 0.25f), replayButtonSize, voiceClip, scale);

        if (!string.IsNullOrWhiteSpace(entry.Speaker))
        {
            var speakerPos = new Vector2(boxX + 20f * scale, boxY - 10f * scale);
            DrawSpeakerTag(drawList, speakerPos, entry.Speaker, GetSpeakerColor(entry.Speaker), scale);
        }

        ImGui.Dummy(new Vector2(availableWidth, boxY - cursor.Y + boxHeight + 10f * scale));
    }

    /// <summary>
    /// Draws and handles the voice replay button for a captured voiced line.
    /// </summary>
    private void DrawReplayVoiceButton(ImDrawListPtr drawList, Vector2 pos, float size, VoiceClipRef voiceClip, float scale)
    {
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(pos);
        ImGui.PushID($"voice-{voiceClip.Path}");
        var clicked = ImGui.InvisibleButton("replay", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(voiceClip.CanReplay ? "Replay voiced line" : "Replay unavailable");
        ImGui.PopID();
        ImGui.SetCursorScreenPos(cursor);

        var center = pos + new Vector2(size * 0.5f);
        var fill = !voiceClip.CanReplay
            ? new Vector4(0.16f, 0.15f, 0.13f, 0.74f)
            : hovered
            ? new Vector4(0.34f, 0.25f, 0.13f, 0.96f)
            : new Vector4(0.20f, 0.15f, 0.09f, 0.86f);
        var iconColor = voiceClip.CanReplay
            ? IconGold
            : new Vector4(0.56f, 0.52f, 0.45f, 0.82f);
        drawList.AddCircleFilled(center, size * 0.5f, ImGui.GetColorU32(fill), 18);
        drawList.AddCircle(center, size * 0.5f - 0.5f * scale, ImGui.GetColorU32(voiceClip.CanReplay ? WindowBorder : DialogueBoxBorder), 18, 1f * scale);

        var icon = ImGui.GetColorU32(iconColor);
        var speakerMin = pos + new Vector2(size * 0.26f, size * 0.41f);
        var speakerMax = pos + new Vector2(size * 0.39f, size * 0.59f);
        drawList.AddRectFilled(speakerMin, speakerMax, icon, 1.2f * scale);

        var coneTop = pos + new Vector2(size * 0.39f, size * 0.39f);
        var coneMid = pos + new Vector2(size * 0.55f, size * 0.29f);
        var coneBottom = pos + new Vector2(size * 0.55f, size * 0.71f);
        var coneLeftBottom = pos + new Vector2(size * 0.39f, size * 0.61f);
        drawList.AddQuadFilled(coneTop, coneMid, coneBottom, coneLeftBottom, icon);

        drawList.PathClear();
        drawList.PathArcTo(center + new Vector2(size * 0.03f, 0f), size * 0.18f, -0.55f, 0.55f, 8);
        drawList.PathStroke(icon, ImDrawFlags.None, 1.5f * scale);
        drawList.PathClear();
        drawList.PathArcTo(center + new Vector2(size * 0.03f, 0f), size * 0.30f, -0.50f, 0.50f, 10);
        drawList.PathStroke(icon, ImDrawFlags.None, 1.3f * scale);

        if (clicked && voiceClip.CanReplay)
            ReplayVoiceClip(voiceClip);
    }

    /// <summary>
    /// Loads the speaker-label shadow texture from the packaged plugin assets.
    /// </summary>
    private ISharedImmediateTexture? LoadSpeakerShadowTexture()
    {
        var shadowPath = Path.Combine(pluginInterface.AssemblyLocation.Directory!.FullName, "Assets", "speaker-shadow-v2.png");
        return File.Exists(shadowPath)
            ? textureProvider.GetFromFile(shadowPath)
            : null;
    }

    /// <summary>
    /// Draws a speaker name tag above a dialogue bubble, using the packaged shadow texture when available.
    /// </summary>
    private void DrawSpeakerTag(ImDrawListPtr drawList, Vector2 pos, string text, Vector4 color, float scale)
    {
        var textSize = ImGui.CalcTextSize(text);
        if (speakerShadowTexture?.TryGetWrap(out IDalamudTextureWrap? wrap, out _) == true)
        {
            var shadowWidth = Math.Max(122f * scale, textSize.X + 86f * scale);
            var shadowHeight = 23f * scale;
            var shadowPos = pos - new Vector2(13f * scale, 3f * scale);
            drawList.AddImage(wrap.Handle, shadowPos, shadowPos + new Vector2(shadowWidth, shadowHeight));
        }
        else
        {
            drawList.AddText(pos + new Vector2(0f, 3.0f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.28f)), text);
            drawList.AddText(pos + new Vector2(1.6f * scale, 2.2f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.34f)), text);
        }

        drawList.AddText(pos + new Vector2(1.0f * scale, 1.0f * scale), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.70f)), text);
        drawList.AddText(pos, ImGui.GetColorU32(color), text);
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
