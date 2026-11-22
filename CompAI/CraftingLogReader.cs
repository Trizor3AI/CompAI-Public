using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Public definitions are cached; character observations exist only in the returned answer session.</summary>
internal sealed unsafe class CraftingLogReader(IDataManager data)
{
    internal const int CompletedRecipeBitCount = 6407;
    internal const int CompletedRecipeByteCount = 801;
    internal const int NotebookEntriesPerRow = 160;
    internal const int MaxNotebookRows = 4096;
    private CraftingLogCatalog? catalog;

    // Framework-thread only. No IsRecipeComplete calls until Query selects a page.
    internal CraftingLogSession CreateSession()
    {
        try
        {
            var owner = ObserveOwner();
            if (!owner.IsReady)
                return CraftingLogSession.Unavailable("Loaded character crafting-log state is unavailable.");
            var definitions = GetCatalog();
            if (ObserveOwner() != owner)
                return CraftingLogSession.Unavailable("Character state changed while preparing crafting-log definitions.");
            return new CraftingLogSession(definitions.Definitions, ids =>
            {
                if (!CatalogMatchesCurrent(definitions))
                    throw new InvalidOperationException("Public crafting definitions changed; start a new question.");
                var page = CapturePage(owner, ids);
                if (!CatalogMatchesCurrent(definitions))
                    throw new InvalidOperationException("Public crafting definitions changed while reading the page.");
                return page;
            });
        }
        catch (Exception ex)
        {
            return CraftingLogSession.Unavailable($"Crafting notebook definitions are unavailable ({ex.GetType().Name}).");
        }
    }

    private CraftingLogCatalog GetCatalog()
    {
        var notebook = data.GetExcelSheet<RecipeNotebookList>();
        var recipes = data.GetExcelSheet<Recipe>();
        var items = data.GetExcelSheet<Item>();
        var gameData = data.GameData;
        var language = (int)data.Language;
        if (this.catalog is not null && this.catalog.Matches(notebook.RawSheet, recipes.RawSheet, items.RawSheet, gameData, language))
            return this.catalog;

        var notebookRows = notebook.Take(MaxNotebookRows + 1).ToArray();
        if (notebookRows.Length > MaxNotebookRows)
            throw new InvalidOperationException("Crafting notebook exceeds its supported row bound.");
        var ids = new HashSet<uint>();
        foreach (var row in notebookRows)
        {
            if (row.Recipe.Count != NotebookEntriesPerRow)
                throw new InvalidOperationException("Crafting notebook row shape is unsupported.");
            foreach (var reference in row.Recipe)
            {
                // Installed notebook padding uses signed -1. Zero is also empty.
                if (reference.RowId is 0 or uint.MaxValue) continue;
                if (!reference.IsValid) throw new InvalidOperationException("Crafting notebook recipe reference is invalid.");
                ids.Add(reference.RowId);
                if (ids.Count > CraftingLogLookup.MaxCatalog)
                    throw new InvalidOperationException("Crafting notebook exceeds the supported catalog bound.");
            }
        }
        var definitions = new List<CraftingLogDefinition>(ids.Count);
        foreach (var id in ids.Order())
        {
            if (!recipes.TryGetRow(id, out var recipe) || recipe.ItemResult.RowId == 0 ||
                !items.TryGetRow(recipe.ItemResult.RowId, out var item))
                throw new InvalidOperationException("Crafting notebook output item reference is invalid.");
            var name = item.Name.ExtractText().Trim();
            if (!CraftingLogLookup.ValidText(name, CraftingLogLookup.MaxNameLength))
                throw new InvalidOperationException("Crafting notebook recipe name is unavailable.");
            definitions.Add(new(id, name));
        }
        if (definitions.Count == 0) throw new InvalidOperationException("Crafting notebook definitions are empty.");
        var result = new CraftingLogCatalog(notebook.RawSheet, recipes.RawSheet, items.RawSheet, gameData, language, definitions.ToArray());
        if (!CatalogMatchesCurrent(result))
            throw new InvalidOperationException("Public crafting definitions changed while building the catalog.");
        return this.catalog = result;
    }

    private bool CatalogMatchesCurrent(CraftingLogCatalog candidate) =>
        candidate.Matches(data.GetExcelSheet<RecipeNotebookList>().RawSheet, data.GetExcelSheet<Recipe>().RawSheet,
            data.GetExcelSheet<Item>().RawSheet, data.GameData, (int)data.Language);

    private static CraftingLogPageObservation CapturePage(CraftingLogOwner owner, IReadOnlyList<uint> ids)
    {
        if (ids.Count > CraftingLogLookup.MaxResults || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count ||
            !owner.IsReady || ObserveOwner() != owner)
            throw new InvalidOperationException("Crafting-log character state is no longer available.");
        var quests = (QuestManager*)owner.QuestManagerAddress;
        // Equality only: never treat Recipe.Number or a Recipe RowId as a bit index.
        var before = quests->CompletedRecipes.ToArray();
        var result = new Dictionary<uint, bool?>(ids.Count);
        foreach (var id in ids)
        {
            // Installed XML explicitly documents this argument as Recipe sheet RowId.
            // Recipe IDs above6407 are valid; the native method owns that mapping.
            result.Add(id, QuestManager.IsRecipeComplete(id));
        }
        if (ObserveOwner() != owner || !quests->CompletedRecipes.SequenceEqual(before))
            throw new InvalidOperationException("Crafting-log state changed while reading the requested page.");
        return new(DateTimeOffset.UtcNow, result);
    }

    private static CraftingLogOwner ObserveOwner()
    {
        var player = PlayerState.Instance();
        var quests = QuestManager.Instance();
        if (player == null || quests == null || !player->IsLoaded || player->ContentId == 0 ||
            quests->CompletedRecipesBitArray.BitCount != CompletedRecipeBitCount ||
            quests->CompletedRecipes.Length != CompletedRecipeByteCount)
            return default;
        return new((nint)player, (nint)quests, player->ContentId);
    }
}

internal readonly record struct CraftingLogOwner(nint PlayerAddress, nint QuestManagerAddress, ulong ContentId)
{
    internal bool IsReady => PlayerAddress != 0 && QuestManagerAddress != 0 && ContentId != 0;
}

internal sealed record CraftingLogCatalog(object NotebookSheet, object RecipeSheet, object ItemSheet, object GameData,
    int Language, CraftingLogDefinition[] Definitions)
{
    internal bool Matches(object notebookSheet, object recipeSheet, object itemSheet, object gameData, int language) =>
        ReferenceEquals(NotebookSheet, notebookSheet) && ReferenceEquals(RecipeSheet, recipeSheet) &&
        ReferenceEquals(ItemSheet, itemSheet) && ReferenceEquals(GameData, gameData) && Language == language;
}
