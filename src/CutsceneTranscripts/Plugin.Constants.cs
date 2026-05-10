using System.Numerics;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    private const string CommandName = "/cutscenetranscript";
    private const string ShortCommandName = "/cstranscript";
    private const int MaxTranscriptEntries = 250;
    private static readonly TimeSpan VisibleAddonGracePeriod = TimeSpan.FromMilliseconds(500);
    private static readonly string[] ChoiceAddonNames =
    [
        "SelectString",
        "CutSceneSelectString",
        "SelectIconString",
        "SelectYesno",
        "SelectOk"
    ];
    private static readonly Vector4[] SpeakerColorPalette =
    [
        new(0.45f, 0.78f, 1.00f, 1.00f),
        new(1.00f, 0.68f, 0.38f, 1.00f),
        new(0.58f, 0.90f, 0.54f, 1.00f),
        new(0.95f, 0.56f, 0.72f, 1.00f),
        new(0.76f, 0.68f, 1.00f, 1.00f),
        new(1.00f, 0.86f, 0.38f, 1.00f),
        new(0.42f, 0.91f, 0.84f, 1.00f),
        new(0.86f, 0.62f, 0.42f, 1.00f),
        new(0.72f, 0.88f, 1.00f, 1.00f),
        new(0.93f, 0.77f, 0.96f, 1.00f),
        new(0.70f, 0.94f, 0.72f, 1.00f),
        new(1.00f, 0.74f, 0.58f, 1.00f)
    ];
    private static readonly Vector4 DialogueBoxFill = new(0.86f, 0.80f, 0.67f, 1.00f);
    private static readonly Vector4 DialogueBoxHighlight = new(0.96f, 0.91f, 0.80f, 0.38f);
    private static readonly Vector4 DialogueBoxBorder = new(0.34f, 0.29f, 0.21f, 0.95f);
    private static readonly Vector4 DialogueTextColor = new(0.08f, 0.07f, 0.05f, 1.00f);
    private static readonly Vector4 DialogueShadowColor = new(0.00f, 0.00f, 0.00f, 0.28f);
    private static readonly Vector4 SpeakerLabelPlateColor = new(0.10f, 0.08f, 0.05f, 0.82f);
    private static readonly Vector4 IconFill = new(0.13f, 0.11f, 0.08f, 0.92f);
    private static readonly Vector4 IconFillHover = new(0.23f, 0.18f, 0.11f, 0.96f);
    private static readonly Vector4 IconGold = new(0.77f, 0.58f, 0.28f, 1.00f);
}
