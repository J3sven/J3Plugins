using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Small native addon that exposes the transcript window from cutscene dialogue.
    /// </summary>
    private sealed class TranscriptOpenButtonAddon : NativeAddon
    {
        public static readonly Vector2 ButtonSize = new(30f, 30f);
        private readonly Plugin plugin;
        private CircleButtonNode? buttonNode;
        private Vector2 requestedPosition;
        private bool requestedVisibility;

        public TranscriptOpenButtonAddon(Plugin plugin)
        {
            this.plugin = plugin;
        }

        /// <summary>
        /// Creates a real window component for KTK while hiding every visual chrome node.
        /// </summary>
        public static WindowNodeBase CreateInvisibleWindowNode()
        {
            var windowNode = new WindowNode
            {
                Size = ButtonSize,
            };

            windowNode.BackgroundImageNode.IsVisible = false;
            windowNode.BackgroundNode.IsVisible = false;
            windowNode.BorderNode.IsVisible = false;
            windowNode.CloseButtonNode.IsVisible = false;
            windowNode.ConfigurationButtonNode.IsVisible = false;
            windowNode.InformationButtonNode.IsVisible = false;
            windowNode.DividingLineNode.IsVisible = false;
            windowNode.HeaderCollisionNode.IsVisible = false;
            windowNode.HeaderContainerNode.IsVisible = false;
            windowNode.SubtitleNode.IsVisible = false;
            windowNode.TitleNode.IsVisible = false;

            return windowNode;
        }

        /// <summary>
        /// Updates visibility and screen position without closing the native addon between cutscene frames.
        /// </summary>
        public void SetButtonState(bool visible, Vector2 position)
        {
            requestedVisibility = visible;
            requestedPosition = position;

            if (visible && AddonId == 0)
                Open();

            ApplyButtonState();
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
        {
            base.OnSetup(addon, atkValueSpan);

            buttonNode = new CircleButtonNode
            {
                Size = ButtonSize,
                Icon = ButtonIcon.Document,
                TextTooltip = "Open transcript",
                OnClick = plugin.ToggleTranscriptWindow,
            };
            buttonNode.AttachNode(this);

            ApplyButtonState();
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            base.OnUpdate(addon);
            ApplyButtonState();
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            buttonNode = null;
            base.OnFinalize(addon);
        }

        private void ApplyButtonState()
        {
            if (AddonId == 0 || buttonNode is null)
                return;

            SetWindowPosition(requestedPosition);
            buttonNode.Icon = plugin.transcriptWindow.IsShown ? ButtonIcon.Cross : ButtonIcon.Document;
            buttonNode.TextTooltip = plugin.transcriptWindow.IsShown ? "Close transcript" : "Open transcript";
            RootNode.IsVisible = requestedVisibility;
            buttonNode.IsVisible = requestedVisibility;
            buttonNode.IsEnabled = requestedVisibility;
        }
    }
}
