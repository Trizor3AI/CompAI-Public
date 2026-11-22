using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace CompAI;

// A confirmation belongs to the character/cache view that was open when it was requested.
internal sealed record StorageClearRequest(ulong OwnerId, int Epoch, string? Category, string? SourceKey)
{
    internal bool Valid => this.OwnerId != 0 && (this.Category is null
        ? this.SourceKey is null : StorageSnapshot.Categories.Contains(this.Category)) &&
        (this.SourceKey is null || !string.IsNullOrWhiteSpace(this.SourceKey));

    internal bool MatchesCharacter(ulong ownerId, int epoch) => this.Valid && this.OwnerId == ownerId && this.Epoch == epoch;

    internal ImmutableArray<StorageContainerCapture> Apply(ImmutableArray<StorageContainerCapture> captures)
    {
        if (!this.Valid) throw new ArgumentException("Invalid storage clear selection.");
        return captures.Where(c => !(this.Category is null || c.Category == this.Category) ||
            !(this.SourceKey is null || c.SourceKey == this.SourceKey)).ToImmutableArray();
    }
}

internal static class StorageBrowser
{
    internal static string CategoryLabel(string category) => category switch {
        "crystals" => "Crystals, shards and clusters", "key_items" => "Key items",
        "armoury" => "Armoury Chest",
        "retainers" => "Retainers", "saddlebag" => "Saddlebags", "housing" => "Housing",
        "fc_chest" => "Free Company chests", _ => category };

    internal static string ContainerLabel(string container)
    {
        if (container == "Crystals" || container == "RetainerCrystals") return "Crystals, shards and clusters";
        if (container == "KeyItems") return "Key items";
        var armoury = container switch {
            "ArmoryMainHand" => "Main hands", "ArmoryOffHand" => "Off hands", "ArmoryHead" => "Head",
            "ArmoryBody" => "Body", "ArmoryHands" => "Hands", "ArmoryLegs" => "Legs", "ArmoryFeets" => "Feet",
            "ArmoryEar" => "Earrings", "ArmoryNeck" => "Necklaces", "ArmoryWrist" => "Bracelets",
            "ArmoryRings" => "Rings", "ArmorySoulCrystal" => "Soul crystals", _ => null };
        if (armoury is not null) return armoury;
        foreach (var (prefix, label) in new[] { ("RetainerPage", "Page "),
            ("PremiumSaddleBag", "Premium page "), ("SaddleBag", "Page ") })
            if (container.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(container.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var page))
                return label + page;
        return container;
    }

    internal static bool Matches(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);

    internal static bool ContainerMatches(StorageContainerCapture capture, string query) =>
        Matches(CategoryLabel(capture.Category), query) || Matches(capture.SourceLabel, query) ||
        Matches(ContainerLabel(capture.Container), query);

    internal static bool Matches(StorageContainerCapture capture, string query) => ContainerMatches(capture, query) ||
        capture.Items.Any(item => Matches(item.Name, query) || item.BaseItemId.ToString(CultureInfo.InvariantCulture) == query);
}
