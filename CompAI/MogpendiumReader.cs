using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Owned Mogpendium view and bounded active lists, corroborated with local public definitions. Never reads opaque mission flags.</summary>
internal sealed unsafe class MogpendiumReader
{
    private readonly IGameGui gui;
    private readonly MogpendiumCatalog catalog;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal MogpendiumReader(IDataManager data, IGameGui gui) { this.gui = gui; catalog = new(data); }

    internal ActivitySnapshot? Capture() => Capture(out _);

    internal ActivitySnapshot? Capture(out long? selectedSeason)
    {
        selectedSeason = null;
        try
        {
            var before = Observe();
            if (before is null) return null;
            var firstEvent = CopyEvent((AtkUnitBase*)before.Value.Addon);
            if (firstEvent is null) return null;
            var seasons = catalog.GetSeasons();
            var season = SelectSeason(firstEvent, seasons);
            if (season is null || before != Observe() || firstEvent != CopyEvent((AtkUnitBase*)before.Value.Addon) || !catalog.Matches()) return null;
            // An owned event change invalidates older event pages even when this page has
            // no supported objective detail (for example a pending event).
            selectedSeason = season.Id;
            var first = CopyPage((AtkUnitBase*)before.Value.Addon);
            if (first is null)
            {
                if (before != Observe() || firstEvent != CopyEvent((AtkUnitBase*)before.Value.Addon) || !catalog.Matches()) selectedSeason = null;
                return null;
            }
            if (firstEvent != first.Context.Event) { selectedSeason = null; return null; }
            MogpendiumObservedList? list = null;
            if (first.Context.Category is 1 or 2)
                MogpendiumActiveList.TryCopy((AtkUnitBase*)before.Value.Addon, first.Context.Category, out list);
            var result = DescribePage(DateTimeOffset.UtcNow, first, seasons, list);
            if (before == Observe() && firstEvent == CopyEvent((AtkUnitBase*)before.Value.Addon) &&
                first == CopyPage((AtkUnitBase*)before.Value.Addon) && catalog.Matches())
            {
                if (list is null || MogpendiumActiveList.TryCopy((AtkUnitBase*)before.Value.Addon, first.Context.Category, out var repeatedList) &&
                    MogpendiumActiveList.Same(list, repeatedList) && before == Observe() &&
                    first == CopyPage((AtkUnitBase*)before.Value.Addon) && catalog.Matches()) return result;
            }
            selectedSeason = null;
            return null;
        }
        catch (Exception) { selectedSeason = null; return null; }
    }

    private MogpendiumOwner? Observe()
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null) return null;
        var agent = module->GetAgentByInternalId(AgentId.MoogleCollection);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->NameString != "MoogleCollection") return null;
        var handle = gui.GetAddonByName("MoogleCollection");
        if (handle.IsNull || handle.Address != (nint)addon || handle.Id != agent->AddonId || !handle.IsReady || !handle.IsVisible) return null;
        return new((nint)player, player->ContentId, (nint)module, (nint)manager, (nint)agent, (nint)addon, agent->AddonId);
    }

    // Event/detail paths were corroborated by the four 0.41.1 captures. The separate
    // active-list copier follows only the typed backing-item associations proven in
    // 0.42.2; this fixed-path detail projection never descends into recycled lists.
    internal static MogpendiumEvent? CopyEvent(AtkUnitBase* addon)
    {
        if (addon == null || !Find(&addon->UldManager, 512, [53, 54, 58], out var root) ||
            !ReadText(root.GetValueOrDefault(53u), 52, 256, out var heading) || heading is null ||
            !ReadText(root.GetValueOrDefault(54u), 52, 128, out var currency) || currency is null ||
            !ReadText(root.GetValueOrDefault(58u), 52, 128, out var secondary)) return null;
        return new(heading, currency, secondary);
    }

    internal static MogpendiumView? CopyView(AtkUnitBase* addon) => CopyPage(addon)?.Detail;

    internal static MogpendiumPage? CopyPage(AtkUnitBase* addon)
    {
        if (addon == null || !Find(&addon->UldManager, 512, [53, 54, 58, 72, 73, 74, 75, 8, 34, 38], out var root)) return null;
        if (!ReadText(root.GetValueOrDefault(53u), 52, 256, out var heading) || heading is null ||
            !ReadText(root.GetValueOrDefault(54u), 52, 128, out var currency) || currency is null ||
            !ReadText(root.GetValueOrDefault(58u), 52, 128, out var secondaryCurrency)) return null;
        var category = -1; string? description = null;
        for (var index = 0; index < 4; ++index)
        {
            if (!ReadText(root.GetValueOrDefault((uint)(72 + index)), 71, 256, out var value)) return null;
            if (value is null) continue;
            if (category != -1) return null;
            category = index; description = value;
        }
        if (category == -1 || !FindOptionalComponent(root.GetValueOrDefault(8u), [3], out var titleNodes) ||
            !FindOptionalComponent(root.GetValueOrDefault(34u), [6], out var overviewNodes) ||
            !FindOptionalComponent(root.GetValueOrDefault(38u), [7, 9], out var rewardNodes) ||
            !ReadText(titleNodes.GetValueOrDefault(3u), 8, 256, out var title) ||
            !ReadText(overviewNodes.GetValueOrDefault(6u), 34, 256, out var overview) ||
            !ReadText(rewardNodes.GetValueOrDefault(7u), 5, 32, out var reward) ||
            !ReadText(rewardNodes.GetValueOrDefault(9u), 5, 32, out var secondaryRewardText)) return null;
        var context = new MogpendiumContext(new(heading, currency, secondaryCurrency), category, description!);
        // A fully absent detail pane is different from malformed or partially copied
        // detail. Only the former can independently retain a complete observed list.
        if (title is null && overview is null && reward is null && secondaryRewardText is null) return new(context, null);
        if (title is null || overview is null || !TryReward(reward, out var primaryReward)) return null;
        uint? secondaryReward = null;
        if (secondaryRewardText is not null)
        {
            if (!TryReward(secondaryRewardText, out var count) || secondaryCurrency is null) return null;
            secondaryReward = count;
        }
        return new(context, new(heading, currency, secondaryCurrency, category, description!, title, overview, primaryReward, secondaryReward));
    }

    private static bool FindOptionalComponent(nint address, uint[] ids, out Dictionary<uint, nint> nodes)
    {
        nodes = [];
        var node = (AtkResNode*)address;
        if (node == null || !Visible(node)) return true;
        if ((ushort)node->Type < 1000) return false;
        var component = ((AtkComponentNode*)node)->Component;
        return (nuint)component >= 65536 && (nuint)component % (nuint)IntPtr.Size == 0 && Find(&component->UldManager, 64, ids, out nodes);
    }

    private static bool Find(AtkUldManager* manager, int maximum, uint[] ids, out Dictionary<uint, nint> found)
    {
        found = [];
        if (manager == null || manager->NodeListCount > maximum) return false;
        var count = manager->NodeListCount; var list = manager->NodeList;
        if (count > 0 && list == null) return false;
        for (var i = 0; i < count; ++i)
        {
            var node = list[i];
            if (node != null && ids.Contains(node->NodeId) && !found.TryAdd(node->NodeId, (nint)node)) return false;
        }
        return manager->NodeList == list && manager->NodeListCount == count;
    }

    internal static bool ReadText(nint address, uint expectedParent, int maximum, out string? value)
    {
        value = null;
        var node = (AtkResNode*)address;
        if (node == null || !Visible(node)) return true;
        if (node->Type != NodeType.Text || node->ParentNode == null || node->ParentNode->NodeId != expectedParent) return false;
        var text = (AtkTextNode*)node;
        var used = text->NodeText.BufUsed;
        if (used is < 1 or > 1025 || text->NodeText.BufSize < used ||
            (byte*)text->NodeText.StringPtr == null) return false;
        var bytes = new ReadOnlySpan<byte>((byte*)text->NodeText.StringPtr, (int)used);
        if (bytes[^1] != 0) return false;
        string parsed;
        try
        {
            var seString = new ReadOnlySeStringSpan(bytes[..^1]);
            foreach (var payload in seString)
            {
                if (payload.Type == ReadOnlySePayloadType.Invalid) return false;
                if (payload.Type == ReadOnlySePayloadType.Text) _ = StrictUtf8.GetCharCount(payload.Body);
                else if (!payload.Validate()) return false;
            }
            parsed = Normalize(SeString.Parse(bytes[..^1].ToArray()).TextValue);
        }
        catch (Exception) { return false; }
        if (parsed.Length == 0 || parsed.Length > maximum || parsed.Any(char.IsControl)) return false;
        value = parsed;
        return true;
    }

    private static bool Visible(AtkResNode* node)
    {
        for (var depth = 0; node != null && depth < 32; ++depth, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0) return false;
        return node == null;
    }

    internal static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    internal static bool TryReward(string? text, out uint amount)
    {
        amount = 0;
        return text is { Length: > 1 } && text[0] == '×' &&
            uint.TryParse(text.AsSpan(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount is >= 1 and <= ushort.MaxValue;
    }

    internal static ActivitySnapshot? Describe(DateTimeOffset capturedAt, MogpendiumView view, MogpendiumSeason[] seasons,
        MogpendiumObservedList? observedList = null) => DescribePage(capturedAt,
            new(new(new(view.Heading, view.CurrencyName, view.SecondaryCurrencyName), view.Category, view.Description), view), seasons, observedList);

    internal static ActivitySnapshot? DescribePage(DateTimeOffset capturedAt, MogpendiumPage page, MogpendiumSeason[] seasons,
        MogpendiumObservedList? observedList = null)
    {
        var context = page.Context; var view = page.Detail;
        if (capturedAt == default || context.Category is < 0 or > 3 || view is not null &&
            (view.Category != context.Category || view.Description != context.Description ||
                new MogpendiumEvent(view.Heading, view.CurrencyName, view.SecondaryCurrencyName) != context.Event ||
                !ActivitySnapshot.ValidText(view.Title, 256) || !ActivitySnapshot.ValidText(view.Overview, 256))) return null;
        var selectedSeason = SelectSeason(context.Event, seasons);
        if (selectedSeason is null) return null;
        if (selectedSeason.Categories.Length != 4 || selectedSeason.CategoryDescriptions.Length != 4 || selectedSeason.CategoryLabels.Length != 4 ||
            Normalize(selectedSeason.CategoryDescriptions[context.Category]) != context.Description) return null;
        var objectives = selectedSeason.Categories[context.Category];
        if (objectives.Length is < 1 or > 200) return null;
        // Match the entire localized public instruction, including target variants and
        // exclusion notes. A substring such as Halatali must never bind Halatali (Hard).
        var selected = view is null ? [] : objectives.Where(objective => MogpendiumCatalog.TryMatchOverview(objective, view.Overview)).ToArray();
        if (view is not null && (view.PrimaryReward is 0 or > ushort.MaxValue || view.SecondaryReward is 0 or > ushort.MaxValue ||
            view.SecondaryReward is not null && selectedSeason.SecondaryCurrencyItemId is null)) return null;
        var hasSelection = view is not null && selected.Length == 1 && (selected[0].RewardCount is not { } fixedReward || fixedReward == view.PrimaryReward);
        if (context.Category is 0 or 3 && !hasSelection) return null;
        var listRows = ObservedRows(context.Category, observedList, objectives);
        if (context.Category is 1 or 2 && !hasSelection && listRows is null) return null;
        var category = MogpendiumContract.Categories[context.Category];
        var rows = new List<ActivityEntry>
        {
            new(1, "event", "Selected event", Text: context.Event.Heading, Value: selectedSeason.Id, Unit: "season"),
            new(2, "event", "Selected category", Text: selectedSeason.CategoryLabels[context.Category]),
            new(3, "coverage", "Unknown state", Text: MogpendiumContract.Unknowns),
            new(4, "event", "Reward currency", Text: selectedSeason.CurrencyName, ReferenceId: selectedSeason.CurrencyItemId, ReferenceKind: "item"),
        };
        if (selectedSeason.SecondaryCurrencyName is { } secondaryName && selectedSeason.SecondaryCurrencyItemId is { } secondaryId)
            rows.Add(new(5, "event", "Secondary reward currency", Text: secondaryName, ReferenceId: secondaryId, ReferenceKind: "item"));
        if (hasSelection)
        {
            rows.Add(new(10, "selection", selected[0].Name, Text: view!.Overview, ReferenceId: selected[0].ContentId, ReferenceKind: "cs_bonus_content"));
            rows.Add(new(11, "selection", "Displayed reward", Value: view.PrimaryReward, Unit: "items", ReferenceId: selectedSeason.CurrencyItemId, ReferenceKind: "item"));
            if (view.SecondaryReward is { } extra)
                rows.Add(new(12, "selection", "Displayed secondary reward", Value: extra, Unit: "items", ReferenceId: selectedSeason.SecondaryCurrencyItemId, ReferenceKind: "item"));
        }
        if (context.Category == 0)
            rows.AddRange(objectives.Select((objective, index) => new ActivityEntry((uint)(100 + index), "catalog", objective.Name,
                Text: MogpendiumContract.Definition, Value: objective.RewardCount, Unit: objective.RewardCount is null ? null : "items",
                ReferenceId: objective.ContentId, ReferenceKind: "cs_bonus_content")));
        if (listRows is not null) rows.AddRange(listRows);
        return new(category, capturedAt, true, rows);
    }

    private static ActivityEntry[]? ObservedRows(int category, MogpendiumObservedList? list, MogpendiumObjective[] objectives)
    {
        if (category is not (1 or 2) || list?.Choices is not { Length: > 0 } choices || choices.Length % category != 0 ||
            choices.Length / category > MogpendiumActiveList.MaxWeeks) return null;
        var rows = new List<ActivityEntry>();
        var weeks = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < choices.Length; ++index)
        {
            var choice = choices[index];
            if (choice is null || !ActivitySnapshot.ValidText(choice.Label, 128) || !ActivitySnapshot.ValidText(choice.WeekLabel, 128) ||
                choice.Slot != index % category + 1 ||
                (choice.Slot == 1 ? !weeks.Add(choice.WeekLabel) : choice.WeekLabel != choices[index - 1].WeekLabel)) return null;
            var matching = MogpendiumCatalog.MatchDisplayedLabel(objectives, choice.Label);
            // Weekly leaf names identify specific duties. Minimog labels such as
            // Save the Queen or Fishing can cover several public targets; preserve
            // those displayed labels without inventing a target or mission pairing.
            if (category == 1 && matching is null) return null;
            rows.Add(new((uint)(100 + index), "observed_list", choice.Label, Text: choice.WeekLabel, Slot: choice.Slot,
                ReferenceId: matching?.ContentId, ReferenceKind: matching is null ? null : "cs_bonus_content"));
        }
        return rows.ToArray();
    }

    internal static MogpendiumSeason? SelectSeason(MogpendiumEvent observed, MogpendiumSeason[] seasons)
    {
        var matches = seasons.Where(season => Normalize(season.Heading) == observed.Heading && Normalize(season.CurrencyName) == observed.CurrencyName &&
            (season.SecondaryCurrencyName is null ? observed.SecondaryCurrencyName is null : Normalize(season.SecondaryCurrencyName) == observed.SecondaryCurrencyName)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal sealed record MogpendiumEvent(string Heading, string CurrencyName, string? SecondaryCurrencyName);
internal sealed record MogpendiumContext(MogpendiumEvent Event, int Category, string Description);
internal sealed record MogpendiumPage(MogpendiumContext Context, MogpendiumView? Detail);
internal sealed record MogpendiumView(string Heading, string CurrencyName, string? SecondaryCurrencyName, int Category,
    string Description, string Title, string Overview, uint PrimaryReward, uint? SecondaryReward);
internal readonly record struct MogpendiumOwner(nint Player, ulong Owner, nint Module, nint Manager, nint Agent, nint Addon, uint AddonId);
