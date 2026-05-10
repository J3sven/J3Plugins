using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node;
using KamiToolKit.Premade.Node.Simple;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Native KamiToolKit transcript window with game window chrome and native scrolling content.
    /// </summary>
    private sealed class TranscriptWindow : NativeAddon
    {
        private const float ToolbarHeight = 34f;
        private const float BubbleSpacing = 10f;
        private readonly Plugin plugin;
        private ScrollingAreaNode<VerticalListNode>? scrollingArea;
        private TextNode? emptyTextNode;
        private TextNode? countTextNode;
        private bool closeRequested;
        private bool softVisible = true;
        private int renderedRevision = -1;

        public TranscriptWindow(Plugin plugin)
        {
            this.plugin = plugin;
        }

        public void MarkDirty()
        {
            renderedRevision = -1;
        }

        public bool IsShown => IsOpen && !closeRequested && softVisible;

        /// <summary>
        /// Opens a finalized native addon or reveals one that was hidden without finalizing.
        /// </summary>
        public void RequestOpen()
        {
            softVisible = true;
            closeRequested = false;
            if (IsOpen)
            {
                ApplySoftVisibility();
                MarkDirty();
                return;
            }

            Open();
        }

        /// <summary>
        /// Hides the native node tree without asking KTK or the game to finalize the addon.
        /// </summary>
        public void RequestSoftHide()
        {
            softVisible = false;
            ApplySoftVisibility();
        }

        /// <summary>
        /// Closes the native addon and immediately stops managed refreshes from touching its native nodes.
        /// </summary>
        public void RequestClose()
        {
            closeRequested = true;
            softVisible = false;
            ClearManagedNativeNodeReferences();
            Close();
        }

        /// <summary>
        /// Rebuilds the native node tree only when transcript state has changed.
        /// </summary>
        public void RefreshIfNeeded()
        {
            if (closeRequested || !IsOpen || scrollingArea is null)
                return;

            if (renderedRevision == plugin.transcriptRevision)
                return;

            try
            {
                RebuildTranscriptList();
            }
            catch (NullReferenceException ex) when (IsStaleNativeNodeException(ex))
            {
                ClearManagedNativeNodeReferences();
            }
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
        {
            base.OnSetup(addon, atkValueSpan);
            closeRequested = false;
            softVisible = true;
            BuildToolbar();
            BuildScrollingArea();
            ApplySoftVisibility();
            MarkDirty();
            RefreshIfNeeded();
        }

        protected override void OnShow(AtkUnitBase* addon)
        {
            base.OnShow(addon);
            closeRequested = false;
            softVisible = true;
            ApplySoftVisibility();
            MarkDirty();
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            closeRequested = true;
            softVisible = false;
            ClearManagedNativeNodeReferences();
            base.OnFinalize(addon);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            base.OnUpdate(addon);
            RefreshIfNeeded();
        }

        private void BuildToolbar()
        {
            var start = ContentStartPosition;

            var copyButton = new TextButtonNode
            {
                Position = start,
                Size = new Vector2(74f, 28f),
                String = "Copy",
            };
            copyButton.OnClick = () => ImGui.SetClipboardText(plugin.BuildTranscriptText());
            copyButton.AttachNode(this);

            var clearButton = new TextButtonNode
            {
                Position = start + new Vector2(82f, 0f),
                Size = new Vector2(74f, 28f),
                String = "Clear",
            };
            clearButton.OnClick = plugin.ClearTranscript;
            clearButton.AttachNode(this);

            countTextNode = new TextNode
            {
                Position = start + new Vector2(168f, 5f),
                Size = new Vector2(ContentSize.X - 168f, 22f),
                FontSize = 12,
                LineSpacing = 14,
                TextColor = ColorHelper.GetColor(3),
                TextOutlineColor = ColorHelper.GetColor(7),
            };
            countTextNode.AttachNode(this);
        }

        private void ApplySoftVisibility()
        {
            if (!IsOpen)
                return;

            RootNode.IsVisible = softVisible;

            if (scrollingArea is null)
                return;

            scrollingArea.IsVisible = softVisible;
            scrollingArea.ScrollingCollisionNode.IsVisible = softVisible;
            scrollingArea.ContentAreaClipNode.IsVisible = softVisible;
            scrollingArea.ContentNode.IsVisible = softVisible;
            scrollingArea.ScrollBarNode.IsVisible = softVisible;
            scrollingArea.ScrollBarNode.IsEnabled = softVisible;
            scrollingArea.ScrollBarNode.BackgroundButtonNode.IsVisible = softVisible;
            scrollingArea.ScrollBarNode.ForegroundButtonNode.IsVisible = softVisible;

            if (softVisible)
                scrollingArea.ScrollBarNode.UpdateScrollParams();
        }

        private void BuildScrollingArea()
        {
            var start = ContentStartPosition + new Vector2(0f, ToolbarHeight);
            scrollingArea = new ScrollingAreaNode<VerticalListNode>
            {
                Position = start,
                Size = new Vector2(ContentSize.X, Math.Max(120f, ContentSize.Y - ToolbarHeight)),
                ContentHeight = 1f,
                ScrollSpeed = 34,
                AutoHideScrollBar = true,
            };
            scrollingArea.ContentNode.ItemSpacing = BubbleSpacing;
            scrollingArea.ContentNode.FirstItemSpacing = 2f;
            scrollingArea.ContentNode.FitContents = true;
            scrollingArea.AttachNode(this);

            emptyTextNode = new TextNode
            {
                Position = start + new Vector2(8f, 10f),
                Size = new Vector2(ContentSize.X - 16f, 28f),
                FontSize = 14,
                LineSpacing = 18,
                TextColor = ColorHelper.GetColor(3),
                TextOutlineColor = ColorHelper.GetColor(7),
                String = "No dialog has been recorded yet.",
            };
            emptyTextNode.AttachNode(this);
        }

        private void RebuildTranscriptList()
        {
            if (scrollingArea is null)
                return;

            var maxScrollPosition = Math.Max(0, scrollingArea.ContentHeight - scrollingArea.Height);
            var wasNearBottom = scrollingArea.ScrollPosition >= maxScrollPosition - 12;
            var contentWidth = Math.Max(280f, scrollingArea.ContentNode.Width - 4f);
            scrollingArea.ContentNode.Clear();

            for (var i = 0; i < plugin.entries.Count; i++)
            {
                scrollingArea.ContentNode.AddNode(new TranscriptBubbleNode(plugin, i, plugin.entries[i], contentWidth));
            }

            scrollingArea.FitToContentHeight();
            if (wasNearBottom)
                scrollingArea.ScrollPosition = (int)Math.Max(0, scrollingArea.ContentHeight - scrollingArea.Height);

            if (emptyTextNode is not null)
                emptyTextNode.IsVisible = plugin.entries.Count == 0;

            if (countTextNode is not null)
                countTextNode.String = $"{plugin.entries.Count} line{(plugin.entries.Count == 1 ? string.Empty : "s")}";

            renderedRevision = plugin.transcriptRevision;
        }

        /// <summary>
        /// Drops managed references after KTK has finalized the native nodes they point at.
        /// </summary>
        private void ClearManagedNativeNodeReferences()
        {
            scrollingArea = null;
            emptyTextNode = null;
            countTextNode = null;
            renderedRevision = -1;
        }

        private static bool IsStaleNativeNodeException(NullReferenceException ex)
        {
            return ex.StackTrace?.Contains("KamiToolKit.Nodes.ComponentNode", StringComparison.Ordinal) == true;
        }
    }

    /// <summary>
    /// One native transcript bubble that preserves the original plugin's soft dialogue-card style.
    /// </summary>
    private sealed class TranscriptBubbleNode : ResNode
    {
        private const float PaddingX = 18f;
        private const float PaddingY = 14f;
        private const float SpeakerOverlap = 10f;
        private const float SpeakerShadowInsetX = 13f;
        private const float SpeakerShadowOffsetY = 2f;
        private const float SpeakerShadowHeight = 22f;
        private const float SpeakerShadowPaddingX = 26f;
        private readonly Plugin plugin;
        private readonly TranscriptEntry entry;
        private readonly BackgroundImageNode shadowNode;
        private readonly BackgroundImageNode bubbleNode;
        private readonly BackgroundImageNode highlightNode;
        private readonly BackgroundImageNode topBorderNode;
        private readonly BackgroundImageNode bottomBorderNode;
        private readonly BackgroundImageNode leftBorderNode;
        private readonly BackgroundImageNode rightBorderNode;
        private readonly TextNode bodyTextNode;
        private readonly TextNode? speakerTextNode;
        private readonly SimpleNineGridNode? speakerShadowNode;
        private readonly CircleButtonNode? replayButtonNode;

        public TranscriptBubbleNode(Plugin plugin, int index, TranscriptEntry entry, float width)
        {
            this.plugin = plugin;
            this.entry = entry;

            var speakerColor = string.IsNullOrWhiteSpace(entry.Speaker)
                ? IconGold
                : plugin.GetSpeakerColor(entry.Speaker);

            shadowNode = CreateSolidNode(DialogueShadowColor);
            shadowNode.AttachNode(this);

            bubbleNode = CreateSolidNode(DialogueBoxFill);
            bubbleNode.AttachNode(this);

            highlightNode = CreateSolidNode(DialogueBoxHighlight);
            highlightNode.AttachNode(this);

            topBorderNode = CreateSolidNode(DialogueBoxBorder);
            topBorderNode.AttachNode(this);

            bottomBorderNode = CreateSolidNode(DialogueBoxBorder);
            bottomBorderNode.AttachNode(this);

            leftBorderNode = CreateSolidNode(DialogueBoxBorder);
            leftBorderNode.AttachNode(this);

            rightBorderNode = CreateSolidNode(DialogueBoxBorder);
            rightBorderNode.AttachNode(this);

            bodyTextNode = new TextNode
            {
                FontSize = 14,
                LineSpacing = 18,
                TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
                TextColor = DialogueTextColor,
                TextOutlineColor = Vector4.Zero,
                String = entry.Text,
            };
            bodyTextNode.AttachNode(this);

            if (!string.IsNullOrWhiteSpace(entry.Speaker))
            {
                speakerShadowNode = CreateSpeakerLabelPlate();
                speakerShadowNode.AttachNode(this);

                speakerTextNode = new TextNode
                {
                    FontSize = 14,
                    LineSpacing = 18,
                    TextFlags = TextFlags.Emboss,
                    TextColor = speakerColor,
                    TextOutlineColor = new Vector4(0f, 0f, 0f, 0.72f),
                    String = entry.Speaker,
                };
                speakerTextNode.AttachNode(this);
            }

            if (entry.VoiceClip is { } voiceClip)
            {
                var replayActive = voiceClip.CanReplay && plugin.IsVoiceClipReplayActive(voiceClip);
                replayButtonNode = new CircleButtonNode
                {
                    Size = new Vector2(28f, 28f),
                    Icon = voiceClip.CanReplay ? replayActive ? ButtonIcon.Mute : ButtonIcon.Volume : ButtonIcon.Mute,
                    TextTooltip = voiceClip.CanReplay ? replayActive ? "Stop replay" : "Replay voiced line" : "Replay unavailable",
                };
                if (voiceClip.CanReplay)
                    replayButtonNode.OnClick = () => this.plugin.ToggleVoiceClipReplay(voiceClip);
                else
                    replayButtonNode.Alpha = 0.58f;

                replayButtonNode.AttachNode(this);
            }

            UpdateLayout(index, width);
        }

        private static BackgroundImageNode CreateSolidNode(Vector4 color)
        {
            return new BackgroundImageNode
            {
                Color = color,
            };
        }

        private static SimpleNineGridNode CreateSpeakerLabelPlate()
        {
            return new SimpleNineGridNode
            {
                TexturePath = "ui/uld/ToolTipS.tex",
                TextureCoordinates = Vector2.Zero,
                TextureSize = new Vector2(32f, 24f),
                TopOffset = 10,
                BottomOffset = 10,
                LeftOffset = 15,
                RightOffset = 15,
                Color = SpeakerLabelPlateColor,
            };
        }

        private void UpdateLayout(int index, float width)
        {
            var speakerHeight = string.IsNullOrWhiteSpace(entry.Speaker)
                ? 0f
                : bodyTextNode.LineSpacing + 2f;
            var boxY = Math.Max(0f, speakerHeight - SpeakerOverlap);
            var textWidth = Math.Max(100f, width - PaddingX * 2f);
            bodyTextNode.Size = new Vector2(textWidth, 1000f);
            var textHeight = Math.Max(18f, bodyTextNode.GetTextDrawSize(false).Y);
            var boxHeight = Math.Max(58f, PaddingY * 2f + textHeight);
            var replayOverflow = replayButtonNode is null ? 0f : Math.Max(0f, replayButtonNode.Height * 0.5f - 2f);
            var totalHeight = boxY + boxHeight + replayOverflow + 2f;

            Size = new Vector2(width, totalHeight);
            shadowNode.Position = new Vector2(6f, boxY + 3f);
            shadowNode.Size = new Vector2(width - 8f, boxHeight);
            bubbleNode.Position = new Vector2(4f, boxY);
            bubbleNode.Size = new Vector2(width - 8f, boxHeight);
            highlightNode.Position = new Vector2(7f, boxY + 3f);
            highlightNode.Size = new Vector2(width - 14f, Math.Max(12f, boxHeight * 0.48f));
            highlightNode.Alpha = index % 2 == 0 ? 0.38f : 0.28f;
            topBorderNode.Position = new Vector2(4f, boxY);
            topBorderNode.Size = new Vector2(width - 8f, 2f);
            bottomBorderNode.Position = new Vector2(4f, boxY + boxHeight - 2f);
            bottomBorderNode.Size = new Vector2(width - 8f, 2f);
            leftBorderNode.Position = new Vector2(4f, boxY);
            leftBorderNode.Size = new Vector2(2f, boxHeight);
            rightBorderNode.Position = new Vector2(width - 6f, boxY);
            rightBorderNode.Size = new Vector2(2f, boxHeight);

            bodyTextNode.Position = new Vector2(PaddingX + 4f, boxY + PaddingY);
            bodyTextNode.Size = new Vector2(textWidth, textHeight + 4f);

            if (speakerTextNode is not null && !string.IsNullOrWhiteSpace(entry.Speaker))
            {
                var speakerX = 24f;
                var speakerY = Math.Max(0f, boxY - 10f);
                speakerTextNode.Size = new Vector2(width - speakerX * 2f, 22f);
                var speakerTextWidth = Math.Max(1f, speakerTextNode.GetTextDrawSize(false).X);
                var speakerWidth = Math.Max(58f, speakerTextWidth + SpeakerShadowPaddingX);
                if (speakerShadowNode is not null)
                {
                    speakerShadowNode.Position = new Vector2(speakerX - SpeakerShadowInsetX, speakerY - SpeakerShadowOffsetY);
                    speakerShadowNode.Size = new Vector2(speakerWidth, SpeakerShadowHeight);
                }

                speakerTextNode.Position = new Vector2(speakerX, speakerY);
            }

            if (replayButtonNode is not null)
            {
                replayButtonNode.Position = new Vector2(width - PaddingX - replayButtonNode.Width, boxY + boxHeight - replayButtonNode.Height * 0.5f);
            }
        }
    }
}
