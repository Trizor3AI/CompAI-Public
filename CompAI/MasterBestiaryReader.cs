using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CompAI;

/// <summary>Framework-thread only. Observes the ordinary open notebook without requesting pet data.</summary>
internal sealed unsafe class MasterBestiaryReader(IDataManager data)
{
    internal const string Category = "beastmaster_bestiary";
    internal const int SupportedPetCount = 50;
    private BestiaryDefinition[]? catalog;

    internal CollectionSnapshot Capture()
    {
        try
        {
            var before = Observe();
            if (!before.IsReady) return Unavailable();
            var manager = (XBMManager*)before.ManagerAddress;
            var notebook = (AgentXBMMonsterNotebook*)before.NotebookAddress;
            var result = Describe(DateTimeOffset.UtcNow, GetCatalog(), id =>
            {
                // The pinned source labels selected/entry PetIds as XBMPet rows,
                // and documents that these two bitfields come from the same packet:
                // https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentXBMMonsterNotebook.cs
                // This supports the API ID domain; it is not a decoded bit-index
                // contract or in-game validation. Never pass linked general Pet IDs.
                var first = new BestiaryUnlockObservation(manager->IsPetUnlocked(id), notebook->IsPetUnlocked(id));
                var second = new BestiaryUnlockObservation(manager->IsPetUnlocked(id), notebook->IsPetUnlocked(id));
                if (first != second)
                    throw new InvalidOperationException("Bestiary observations changed during reading.");
                return first;
            }, expectedUnlockedCount: before.NumUnlockedPets);
            return IsStable(before, Observe()) ? result : Unavailable();
        }
        catch (Exception ex)
        {
            return CollectionSnapshot.Unavailable(Category, $"Master's Bestiary read unavailable ({ex.GetType().Name}).");
        }
    }

    private static BestiaryMenuObservation Observe()
    {
        var player = PlayerState.Instance();
        var manager = XBMManager.Instance();
        var notebook = AgentXBMMonsterNotebook.Instance();
        if (player == null || manager == null || notebook == null || !player->IsLoaded || player->ContentId == 0 ||
            !notebook->IsAgentActive() || !notebook->IsAddonShown() || !notebook->IsAddonReady() ||
            manager->UnlockedPetsBitArray.BitCount != SupportedPetCount ||
            notebook->UnlockedPetsBitArray.BitCount != SupportedPetCount ||
            manager->UnlockedPets.Length != 7 || notebook->UnlockedPets.Length != 7)
            return default;

        // Hex strings are bounded equality fingerprints, not interpreted bit indices.
        // Notebook.ContentId is an XBMContent row, not the player's ContentId.
        return new((nint)player, (nint)manager, (nint)notebook, player->ContentId, player->IsLoaded, true,
            notebook->AddonId, manager->State, manager->NumUnlockedPets, manager->HasNewUnlockedPets,
            notebook->Source, notebook->ContentId, notebook->UsesContentPetData, notebook->IsNewPetListReceived,
            notebook->RequestPetListRebuild, notebook->RequestNotebookRebuild, notebook->RequestNotebookDetailRebuild,
            Convert.ToHexString(manager->UnlockedPets), Convert.ToHexString(notebook->UnlockedPets));
    }

    internal static bool IsStable(BestiaryMenuObservation before, BestiaryMenuObservation after) =>
        before.IsReady && after.IsReady && before == after;

    // Only these public sheet reads use the installed pending schema. A shape,
    // hash, missing-reference or name failure makes the whole category unavailable.
#pragma warning disable PendingExcelSchema
    private BestiaryDefinition[] GetCatalog()
    {
        if (this.catalog is not null) return this.catalog;
        var rows = data.GetExcelSheet<Lumina.Excel.Sheets.Experimental.XBMPet>()
            .Take(SupportedPetCount + 2).ToArray();
        if (rows.Length != SupportedPetCount + 1 || rows.Count(row => row.RowId == 0) != 1)
            throw new InvalidOperationException("The Bestiary sheet shape is unsupported.");
        var definitions = rows.Where(row => row.RowId != 0).Select(row =>
        {
            if (row.Pet.RowId == 0 || !row.Pet.IsValid)
                throw new InvalidOperationException("A Bestiary Pet reference is unavailable.");
            return new BestiaryDefinition(row.RowId, row.Pet.Value.Name.ExtractText().Trim(), row.Pet.RowId);
        }).ToArray();
        if (!IsSupportedCatalog(definitions))
            throw new InvalidOperationException("The Bestiary catalog is unsupported or ambiguous.");
        return this.catalog = definitions.OrderBy(row => row.Id).ToArray();
    }
#pragma warning restore PendingExcelSchema

    internal static bool IsSupportedCatalog(IEnumerable<BestiaryDefinition> definitions)
    {
        var rows = definitions.Take(SupportedPetCount + 1).ToArray();
        return rows.Length == SupportedPetCount && rows.All(row => row is not null &&
            row.Id is > 0 and <= SupportedPetCount && row.PetId != 0 &&
            CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) &&
            rows.Select(row => row.Id).Distinct().Count() == SupportedPetCount &&
            rows.Select(row => row.PetId).Distinct().Count() == SupportedPetCount;
    }

    internal static CollectionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<BestiaryDefinition> definitions,
        Func<uint, BestiaryUnlockObservation> observe, bool loaded = true, int? expectedUnlockedCount = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(observe);
        if (!loaded) return Unavailable();
        var rows = definitions.Take(CollectionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > CollectionSnapshot.MaxEntries ||
            rows.Any(row => row is null || row.Id == 0 || !CollectionSnapshot.ValidText(row.Name, CollectionSnapshot.MaxNameLength)) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            expectedUnlockedCount is < 0 or > SupportedPetCount)
            return CollectionSnapshot.Unavailable(Category, "The Bestiary catalog or observed count is invalid.");
        var entries = new List<CollectionEntry>();
        foreach (var row in rows)
        {
            if (row.Id > SupportedPetCount || row.PetId == 0)
            {
                entries.Add(new(row.Id, row.Name, "unknown", null,
                    "This entry has no supported XBMPet/Pet mapping; capture state is unknown."));
                continue;
            }
            var observed = observe(row.Id);
            if (observed.ManagerUnlocked != observed.NotebookUnlocked)
                return CollectionSnapshot.Unavailable(Category, "The Bestiary manager and open notebook disagree; wait for loading and try again.");
            entries.Add(new(row.Id, row.Name, observed.ManagerUnlocked ? "unlocked" : "not_observed_unlocked",
                observed.ManagerUnlocked ? true : null, observed.ManagerUnlocked ? null :
                    "No matching positive observation is available; native false does not establish an uncaptured familiar."));
        }
        if (expectedUnlockedCount.HasValue && entries.Count(row => row.Unlocked == true) != expectedUnlockedCount.Value)
            return CollectionSnapshot.Unavailable(Category, "The Bestiary observed count and named catalog disagree; wait for loading and try again.");
        return new(Category, capturedAt, true, entries);
    }

    private static CollectionSnapshot Unavailable() => CollectionSnapshot.Unavailable(Category,
        "A stable received ordinary Master's Bestiary is unavailable. Open the notebook yourself, wait for it to populate, then ask again.");
}

internal sealed record BestiaryDefinition(uint Id, string Name, uint PetId);
internal readonly record struct BestiaryUnlockObservation(bool ManagerUnlocked, bool NotebookUnlocked);

internal readonly record struct BestiaryMenuObservation(nint PlayerAddress, nint ManagerAddress, nint NotebookAddress,
    ulong PlayerContentId, bool PlayerLoaded, bool MenuReady, uint AddonId, XBMManager.DataState State,
    int NumUnlockedPets, bool HasNewUnlockedPets, uint Source, uint ContentId, bool UsesContentPetData,
    bool IsNewPetListReceived, bool RequestPetListRebuild, bool RequestNotebookRebuild, bool RequestNotebookDetailRebuild,
    string? ManagerFingerprint, string? NotebookFingerprint)
{
    internal bool IsReady => PlayerAddress != 0 && ManagerAddress != 0 && NotebookAddress != 0 &&
        PlayerContentId != 0 && PlayerLoaded && MenuReady && AddonId != 0 &&
        State == XBMManager.DataState.Received && NumUnlockedPets is >= 0 and <= MasterBestiaryReader.SupportedPetCount &&
        Source == 0 && ContentId == 0 && !UsesContentPetData &&
        !RequestPetListRebuild && !RequestNotebookRebuild && !RequestNotebookDetailRebuild &&
        ManagerFingerprint is { Length: 14 } && ManagerFingerprint == NotebookFingerprint;
}
