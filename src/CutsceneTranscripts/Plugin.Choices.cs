using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CutsceneTranscripts;

public sealed unsafe partial class Plugin
{
    /// <summary>
    /// Returns whether a choice addon was seen recently enough to avoid overlapping the transcript button.
    /// </summary>
    private bool IsChoiceAddonVisible()
    {
        var now = DateTimeOffset.Now;
        return choiceStates.Values.Any(state => now - state.LastSeenAt <= VisibleAddonGracePeriod);
    }

    private void OnChoicePostUpdate(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var shouldCapture = ShouldCaptureChoice(args);
        if (!shouldCapture || !args.Addon.IsVisible)
            return;

        CacheChoiceState(args);
    }

    /// <summary>
    /// Observes choice submission events so selected dialogue options can be added to the transcript.
    /// </summary>
    private void OnChoiceReceiveEvent(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var shouldCapture = ShouldCaptureChoice(args);
        if (!shouldCapture || !args.Addon.IsVisible)
            return;

        if (args is not AddonReceiveEventArgs receiveArgs)
            return;

        var isSubmitEvent = IsChoiceSubmitEvent(receiveArgs);
        var listItemIndex = ReadListItemIndex(receiveArgs);
        var state = CacheChoiceState(args, receiveArgs.EventParam, listItemIndex, EventParamMayBeChoiceIndex(receiveArgs));
        if (state == null)
            return;

        if (!isSubmitEvent)
            return;

        state.SubmitSeen = true;
        TryRecordChoice(state, preferEventParam: eventType == AddonEvent.PreReceiveEvent);
    }

    private void OnChoiceFinalize(AddonEvent eventType, AddonArgs args)
    {
        if (args.Addon.IsNull)
        {
            return;
        }

        var address = args.Addon.Address;
        if (choiceStates.TryGetValue(address, out var state))
        {
            if (state.SubmitSeen)
                TryRecordChoice(state, preferEventParam: false);
        }

        choiceStates.Remove(address);
    }

    /// <summary>
    /// Limits choice capture to active or very recent cutscene flows to avoid recording ordinary menus.
    /// </summary>
    private bool ShouldCaptureChoice(AddonArgs args)
    {
        if (!Configuration.Enabled)
            return false;

        if (IsCutsceneActive() || lastCutsceneActive)
            return true;

        if (args.AddonName.Contains("CutScene", StringComparison.OrdinalIgnoreCase) && entries.Count > 0)
            return true;

        return lastCutsceneActiveAt != default
            && DateTimeOffset.Now - lastCutsceneActiveAt <= TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Stores the latest visible choice options and selection hints for one choice addon instance.
    /// </summary>
    private ChoiceState? CacheChoiceState(AddonArgs args, int eventParam = -1, int listItemIndex = -1, bool eventParamMayBeChoiceIndex = true)
    {
        if (args.Addon.IsNull)
            return null;

        var address = args.Addon.Address;
        if (!choiceStates.TryGetValue(address, out var state))
        {
            state = new ChoiceState { AddonName = args.AddonName };
            choiceStates[address] = state;
        }

        state.LastSeenAt = DateTimeOffset.Now;

        var options = ReadChoiceOptions(args);
        if (options.Count > 0)
        {
            state.Options.Clear();
            state.Options.AddRange(options);
        }

        var selectedIndex = ReadSelectedChoiceIndex(args);
        if (IsValidChoiceIndex(state, selectedIndex))
            state.SelectedIndex = selectedIndex;

        if (eventParam >= 0)
        {
            state.LastEventParam = eventParam;
            state.LastEventParamMayBeChoiceIndex = eventParamMayBeChoiceIndex;
        }

        if (IsValidChoiceIndex(state, listItemIndex))
        {
            state.ListItemIndex = listItemIndex;
        }
        else if (listItemIndex > 0 && IsValidChoiceIndex(state, listItemIndex - 1))
        {
            state.ListItemIndex = listItemIndex - 1;
        }

        return state;
    }

    private static List<string> ReadChoiceOptions(AddonArgs args)
    {
        return args.AddonName switch
        {
            "SelectString" => ReadSelectStringOptions((AddonSelectString*)args.Addon.Address),
            "SelectYesno" => ReadGenericChoiceOptions((AtkUnitBase*)args.Addon.Address, preferFinalPair: true),
            "CutSceneSelectString" => ReadCutSceneSelectStringOptions((AtkUnitBase*)args.Addon.Address),
            _ => ReadGenericChoiceOptions((AtkUnitBase*)args.Addon.Address)
        };
    }

    private static int ReadSelectedChoiceIndex(AddonArgs args)
    {
        return args.AddonName switch
        {
            "SelectString" => ReadSelectStringSelectedIndex((AddonSelectString*)args.Addon.Address),
            _ => ReadGenericSelectedIndex((AtkUnitBase*)args.Addon.Address)
        };
    }

    /// <summary>
    /// Resolves the best selected choice index from event data and cached UI state, then records it once.
    /// </summary>
    private void TryRecordChoice(ChoiceState state, bool preferEventParam)
    {
        if (state.Recorded)
            return;

        var indices = new List<int> { state.ListItemIndex };
        if (preferEventParam && state.LastEventParamMayBeChoiceIndex)
            indices.Add(state.LastEventParam);

        indices.Add(state.SelectedIndex);

        if (!preferEventParam && state.LastEventParamMayBeChoiceIndex)
            indices.Add(state.LastEventParam);

        foreach (var index in indices)
        {
            if (!IsValidChoiceIndex(state, index))
                continue;

            AddChoiceEntry(state.Options[index]);
            state.Recorded = true;
            return;
        }

        if (state.Options.Count == 1)
        {
            AddChoiceEntry(state.Options[0]);
            state.Recorded = true;
            return;
        }
    }

    private static bool IsValidChoiceIndex(ChoiceState state, int index)
    {
        return index >= 0 && index < state.Options.Count;
    }

    private static bool IsChoiceSubmitEvent(AddonReceiveEventArgs args)
    {
        return args.AtkEventType is AddonEventType.MouseClick
            or AddonEventType.MouseUp
            or AddonEventType.ButtonClick
            or AddonEventType.ListButtonPress
            or AddonEventType.ListItemClick
            or AddonEventType.ListItemDoubleClick
            or AddonEventType.ListItemSelect
            or AddonEventType.DialogueSubmit;
    }

    private static bool EventParamMayBeChoiceIndex(AddonReceiveEventArgs args)
    {
        return args.AtkEventType is not (AddonEventType.ListButtonPress
            or AddonEventType.ListItemClick
            or AddonEventType.ListItemDoubleClick
            or AddonEventType.ListItemSelect);
    }

    private static int ReadListItemIndex(AddonReceiveEventArgs args)
    {
        if (args.AtkEventData == 0)
            return -1;

        var eventData = (AtkEventData*)args.AtkEventData;
        return eventData == null
            ? -1
            : eventData->ListItemData.SelectedIndex;
    }

    private static List<string> ReadSelectStringOptions(AddonSelectString* addon)
    {
        var options = new List<string>();
        if (addon == null || addon->PopupMenu.EntryNames == null)
            return options;

        var count = Math.Clamp(addon->PopupMenu.EntryCount, 0, 100);
        for (var i = 0; i < count; i++)
            AddText(options, addon->PopupMenu.EntryNames[i].AsDalamudSeString().TextValue);

        return options;
    }

    private static int ReadSelectStringSelectedIndex(AddonSelectString* addon)
    {
        if (addon == null || addon->PopupMenu.List == null)
            return -1;

        var list = addon->PopupMenu.List;
        var count = addon->PopupMenu.EntryCount;
        var candidates = new[]
        {
            list->SelectedItemIndex,
            list->HeldItemIndex,
            list->HoveredItemIndex,
            list->HoveredItemIndex2,
            list->HoveredItemIndex3
        };

        return candidates.FirstOrDefault(index => index >= 0 && index < count, -1);
    }

    private static int ReadGenericSelectedIndex(AtkUnitBase* addon)
    {
        if (addon == null)
            return -1;

        return ReadGenericSelectedIndex(addon->RootNode);
    }

    private static int ReadGenericSelectedIndex(AtkResNode* node)
    {
        if (node == null)
            return -1;

        if (node->Type == NodeType.Component)
        {
            var list = ((AtkComponentNode*)node)->GetAsAtkComponentList();
            var selected = ReadComponentListSelectedIndex(list);
            if (selected >= 0)
                return selected;
        }

        var child = node->ChildNode;
        while (child != null)
        {
            var selected = ReadGenericSelectedIndex(child);
            if (selected >= 0)
                return selected;

            child = child->PrevSiblingNode;
        }

        return -1;
    }

    private static int ReadComponentListSelectedIndex(AtkComponentList* list)
    {
        if (list == null)
            return -1;

        var count = list->ListLength;
        var candidates = new[]
        {
            list->SelectedItemIndex,
            list->HeldItemIndex,
            list->HoveredItemIndex,
            list->HoveredItemIndex2,
            list->HoveredItemIndex3
        };

        return candidates.FirstOrDefault(index => index >= 0 && index < count, -1);
    }

    private static List<string> ReadCutSceneSelectStringOptions(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        if (addon == null)
            return texts;

        CollectTextNodes(addon->RootNode, texts);
        CollectAtkValueStrings(addon, texts);

        if (texts.Count > 1)
            texts.RemoveAt(0);

        return texts
            .Where(text => text.Length <= 240)
            .ToList();
    }

    /// <summary>
    /// Extracts readable option text from generic choice addons using text nodes and AtkValue payloads.
    /// </summary>
    private static List<string> ReadGenericChoiceOptions(AtkUnitBase* addon, bool preferFinalPair = false)
    {
        var texts = new List<string>();
        if (addon == null)
            return texts;

        CollectTextNodes(addon->RootNode, texts);
        CollectAtkValueStrings(addon, texts);

        if (texts.Count == 0)
            return texts;

        if (preferFinalPair)
        {
            var shortTexts = texts.Where(text => text.Length <= 80 && !text.Contains('\n')).TakeLast(2).ToList();
            if (shortTexts.Count > 0)
                return shortTexts;
        }

        return texts
            .Where(text => text.Length <= 240)
            .ToList();
    }

    /// <summary>
    /// Recursively collects string values from an addon's AtkValue array with conservative count limits.
    /// </summary>
    private static void CollectAtkValueStrings(AtkUnitBase* addon, List<string> texts)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
            return;

        var count = Math.Min(addon->AtkValuesCount, (ushort)100);
        for (var i = 0; i < count; i++)
            CollectAtkValueString(addon->AtkValues + i, texts);
    }

    private static void CollectAtkValueString(AtkValue* value, List<string> texts)
    {
        if (value == null)
            return;

        if (IsStringAtkValueType(value->Type))
        {
            AddText(texts, value->GetValueAsString());
            return;
        }

        if (value->Type is AtkValueType.Vector or AtkValueType.ManagedVector)
        {
            var count = Math.Min(value->GetVectorSize(), 100u);
            for (var i = 0u; i < count; i++)
                CollectAtkValueString(value->GetVectorValue(i), texts);
        }
    }

    private static bool IsStringAtkValueType(AtkValueType type)
    {
        return type is AtkValueType.String
            or AtkValueType.WideString
            or AtkValueType.String8
            or AtkValueType.ManagedString;
    }

    /// <summary>
    /// Walks an addon node tree and collects unique text node values.
    /// </summary>
    private static void CollectTextNodes(AtkResNode* node, List<string> texts)
    {
        if (node == null)
            return;

        if (node->Type == NodeType.Text)
            AddText(texts, ((AtkTextNode*)node)->NodeText.AsDalamudSeString().TextValue);

        var child = node->ChildNode;
        while (child != null)
        {
            CollectTextNodes(child, texts);
            child = child->PrevSiblingNode;
        }
    }
}
