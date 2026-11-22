using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CompAI;

internal sealed record CompanionAccessSettings(bool Inventory = false, bool Quests = false, bool WebSearch = false,
    bool ChatHistory = false, bool GameChatReplies = false, bool CharacterContext = false, bool NpcDialogue = false,
    bool Location = false, bool SpoilersAllowed = false, bool Currencies = false, bool Duties = false,
    bool GcSupply = false, bool TargetInfo = false, bool Retainers = false, bool Saddlebag = false,
    bool Housing = false, bool FcChest = false, bool Armoury = false, bool Collections = false,
    bool FashionAccessories = false, bool Facewear = false, bool Emotes = false, bool Orchestrion = false,
    bool Achievements = false, bool AetherCurrents = false, bool SharedFates = false, bool Trust = false,
    bool BlueMage = false, bool GatheringLog = false, bool Bestiary = false,
    bool CraftingLog = false, bool FishingLog = false, bool VariantNotebook = false, bool Shops = false, bool Glamour = false, bool HuntingLog = false, bool SightseeingLog = false, bool ChallengeLog = false, bool TripleTriad = false, bool BattleChocobo = false, bool Pvp = false, bool TripleTriadDecks = false, bool RaceChocobo = false, bool Verminion = false, bool FreeCompany = false, bool OccultRecords = false, bool YokaiCollection = false, bool CollectionTools = false, bool Mahjong = false,
    bool IslandProgress = false, bool CosmicResearch = false,
    bool IslandMaterials = false, bool CosmicMissions = false, bool SelectedMacro = false);

// Access choices must be durable before granting access. An unreadable file or failed save
// disables all effective access; it never silently enables a fallback configuration.
internal sealed class CompanionAccessSettingsStore
{
    private const int MaxFileBytes = 4096;
    private const int UnableToRemoveReplacedFile = unchecked((int)0x80070497);
    private const int ReplacementRetryCount = 2;
    private const int ReplacementRetryDelayMs = 10;
    private readonly object gate = new();
    private readonly string path;
    private readonly Action<string, string> replaceFile;
    private readonly Action<int> retryDelay;
    private CompanionAccessSettings settings = new();
    private string? error;

    internal CompanionAccessSettings Settings { get { lock (this.gate) return this.settings; } }
    internal string? Error { get { lock (this.gate) return this.error; } }

    internal CompanionAccessSettingsStore(string path)
        : this(path, static (source, destination) => File.Replace(source, destination, null), Thread.Sleep)
    {
    }

    internal CompanionAccessSettingsStore(string path, Action<string, string> replaceFile, Action<int> retryDelay)
    {
        ArgumentNullException.ThrowIfNull(replaceFile);
        ArgumentNullException.ThrowIfNull(retryDelay);
        this.replaceFile = replaceFile;
        this.retryDelay = retryDelay;
        this.path = string.Empty;
        try
        {
            this.path = Path.GetFullPath(path);
            this.settings = this.Load();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (IsStorageError(ex))
        {
            this.error = $"Access settings could not be loaded; all access is off ({ex.GetType().Name}).";
        }
    }

    internal bool Save(CompanionAccessSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (this.gate)
        {
            string? temporaryPath = null;
            try
            {
                if (this.path.Length == 0) throw new IOException("Invalid settings path.");
                var directory = Path.GetDirectoryName(this.path)!;
                Directory.CreateDirectory(directory);
                RejectLink(this.path);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(this.path)}.{Guid.NewGuid():N}.tmp");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 26,
                    inventory = value.Inventory,
                    quests = value.Quests,
                    webSearch = value.WebSearch,
                    chatHistory = value.ChatHistory,
                    gameChatReplies = value.GameChatReplies,
                    characterContext = value.CharacterContext,
                    npcDialogue = value.NpcDialogue,
                    location = value.Location,
                    spoilersAllowed = value.SpoilersAllowed,
                    currencies = value.Currencies,
                    duties = value.Duties,
                    gcSupply = value.GcSupply,
                    targetInfo = value.TargetInfo,
                    retainers = value.Retainers, saddlebag = value.Saddlebag, housing = value.Housing, fcChest = value.FcChest,
                    armoury = value.Armoury,
                    collections = value.Collections,
                    fashionAccessories = value.FashionAccessories,
                    facewear = value.Facewear,
                    emotes = value.Emotes,
                    orchestrion = value.Orchestrion,
                    achievements = value.Achievements,
                    aetherCurrents = value.AetherCurrents,
                    sharedFates = value.SharedFates,
                    trust = value.Trust,
                    blueMage = value.BlueMage,
                    gatheringLog = value.GatheringLog,
                    bestiary = value.Bestiary,
                    craftingLog = value.CraftingLog,
                    fishingLog = value.FishingLog,
                    variantNotebook = value.VariantNotebook,
                    shops = value.Shops,
                    glamour = value.Glamour,
                    huntingLog = value.HuntingLog,
                    sightseeingLog = value.SightseeingLog,
                    challengeLog = value.ChallengeLog,
                    tripleTriad = value.TripleTriad, battleChocobo = value.BattleChocobo, pvp = value.Pvp,
                    tripleTriadDecks = value.TripleTriadDecks,
                    raceChocobo = value.RaceChocobo,
                    verminion = value.Verminion,
                    freeCompany = value.FreeCompany,
                    occultRecords = value.OccultRecords,
                    yokaiCollection = value.YokaiCollection,
                    collectionTools = value.CollectionTools,
                    mahjong = value.Mahjong,
                    islandProgress = value.IslandProgress,
                    cosmicResearch = value.CosmicResearch,
                    islandMaterials = value.IslandMaterials,
                    cosmicMissions = value.CosmicMissions,
                    selectedMacro = value.SelectedMacro,

                });
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(this.path)) this.ReplaceWithRetry(temporaryPath);
                else File.Move(temporaryPath, this.path);
                this.settings = value;
                this.error = null;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                this.settings = new();
                this.error = $"Access settings were not saved; all access is off for this session ({ex.GetType().Name}). Previous saved choices may return after reload.";
                return false;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (IsStorageError(ex)) { }
                }
            }
        }
    }

    private void ReplaceWithRetry(string temporaryPath)
    {
        for (var attempt = 0; ; ++attempt)
        {
            RejectLink(temporaryPath);
            RejectLink(this.path);
            if (!File.Exists(temporaryPath) || !File.Exists(this.path))
                throw new IOException("Settings replacement input is unavailable.");
            try
            {
                this.replaceFile(temporaryPath, this.path);
                return;
            }
            catch (IOException ex) when (ex.HResult == UnableToRemoveReplacedFile && attempt < ReplacementRetryCount)
            {
                // ReplaceFileW documents ERROR_UNABLE_TO_REMOVE_REPLACED (1175) as
                // leaving both files at their original names. Retry that same atomic
                // operation only; 1176/1177 can change the files and must not be retried.
                // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
                if (!File.Exists(temporaryPath) || !File.Exists(this.path)) throw;
                this.retryDelay(ReplacementRetryDelayMs);
            }
        }
    }

    private CompanionAccessSettings Load()
    {
        RejectLink(this.path);
        using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Oversized access settings.");
        var bytes = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new InvalidDataException("Oversized access settings.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected settings object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inventory = false;
        var quests = false;
        var webSearch = false;
        var chatHistory = false;
        var gameChatReplies = false;
        var characterContext = false;
        var npcDialogue = false;
        var location = false;
        var spoilersAllowed = false;
        var currencies = false;
        var duties = false;
        var gcSupply = false;
        var targetInfo = false;
        var retainers = false; var saddlebag = false; var housing = false; var fcChest = false;
        var armoury = false;
        var collections = false;
        var fashionAccessories = false;
        var facewear = false;
        var emotes = false;
        var orchestrion = false;
        var achievements = false;
        var aetherCurrents = false;
        var sharedFates = false;
        var trust = false;
        var blueMage = false;
        var gatheringLog = false;
        var bestiary = false;
        var craftingLog = false;
        var fishingLog = false;
        var variantNotebook = false;
        var shops = false;
        var glamour = false;
        var huntingLog = false;
        var sightseeingLog = false;
        var challengeLog = false;
        var tripleTriad = false; var battleChocobo = false; var pvp = false;
        var tripleTriadDecks = false; var raceChocobo = false; var verminion = false; var freeCompany = false; var occultRecords = false; var yokaiCollection = false; var collectionTools = false; var mahjong = false;
        var islandProgress = false; var cosmicResearch = false;
        var islandMaterials = false; var cosmicMissions = false; var selectedMacro = false;
        var schemaVersion = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate access setting.");
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out schemaVersion) || schemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26))
                        throw new InvalidDataException("Unsupported settings version.");
                    break;
                case "inventory": inventory = ReadBoolean(property.Value); break;
                case "quests": quests = ReadBoolean(property.Value); break;
                case "webSearch": webSearch = ReadBoolean(property.Value); break;
                case "chatHistory": chatHistory = ReadBoolean(property.Value); break;
                case "gameChatReplies": gameChatReplies = ReadBoolean(property.Value); break;
                case "characterContext": characterContext = ReadBoolean(property.Value); break;
                case "npcDialogue": npcDialogue = ReadBoolean(property.Value); break;
                case "location": location = ReadBoolean(property.Value); break;
                case "spoilersAllowed": spoilersAllowed = ReadBoolean(property.Value); break;
                case "currencies": currencies = ReadBoolean(property.Value); break;
                case "duties": duties = ReadBoolean(property.Value); break;
                case "gcSupply": gcSupply = ReadBoolean(property.Value); break;
                case "targetInfo": targetInfo = ReadBoolean(property.Value); break;
                case "retainers": retainers = ReadBoolean(property.Value); break;
                case "saddlebag": saddlebag = ReadBoolean(property.Value); break;
                case "housing": housing = ReadBoolean(property.Value); break;
                case "fcChest": fcChest = ReadBoolean(property.Value); break;
                case "armoury": armoury = ReadBoolean(property.Value); break;
                case "collections": collections = ReadBoolean(property.Value); break;
                case "fashionAccessories": fashionAccessories = ReadBoolean(property.Value); break;
                case "facewear": facewear = ReadBoolean(property.Value); break;
                case "emotes": emotes = ReadBoolean(property.Value); break;
                case "orchestrion": orchestrion = ReadBoolean(property.Value); break;
                case "achievements": achievements = ReadBoolean(property.Value); break;
                case "aetherCurrents": aetherCurrents = ReadBoolean(property.Value); break;
                case "sharedFates": sharedFates = ReadBoolean(property.Value); break;
                case "trust": trust = ReadBoolean(property.Value); break;
                case "blueMage": blueMage = ReadBoolean(property.Value); break;
                case "gatheringLog": gatheringLog = ReadBoolean(property.Value); break;
                case "bestiary": bestiary = ReadBoolean(property.Value); break;
                case "craftingLog": craftingLog = ReadBoolean(property.Value); break;
                case "fishingLog": fishingLog = ReadBoolean(property.Value); break;
                case "variantNotebook": variantNotebook = ReadBoolean(property.Value); break;
                case "shops": shops = ReadBoolean(property.Value); break;
                case "glamour": glamour = ReadBoolean(property.Value); break;
                case "huntingLog": huntingLog = ReadBoolean(property.Value); break;
                case "sightseeingLog": sightseeingLog = ReadBoolean(property.Value); break;
                case "challengeLog": challengeLog = ReadBoolean(property.Value); break;
                case "tripleTriad": tripleTriad = ReadBoolean(property.Value); break;
                case "battleChocobo": battleChocobo = ReadBoolean(property.Value); break;
                case "pvp": pvp = ReadBoolean(property.Value); break;
                case "tripleTriadDecks": tripleTriadDecks = ReadBoolean(property.Value); break;
                case "raceChocobo": raceChocobo = ReadBoolean(property.Value); break;
                case "verminion": verminion = ReadBoolean(property.Value); break;
                case "freeCompany": freeCompany = ReadBoolean(property.Value); break;
                case "occultRecords": occultRecords = ReadBoolean(property.Value); break;
                case "yokaiCollection": yokaiCollection = ReadBoolean(property.Value); break;
                case "collectionTools": collectionTools = ReadBoolean(property.Value); break;
                case "mahjong": mahjong = ReadBoolean(property.Value); break;
                case "islandProgress": islandProgress = ReadBoolean(property.Value); break;
                case "cosmicResearch": cosmicResearch = ReadBoolean(property.Value); break;
                case "islandMaterials": islandMaterials = ReadBoolean(property.Value); break;
                case "cosmicMissions": cosmicMissions = ReadBoolean(property.Value); break;
                case "selectedMacro": selectedMacro = ReadBoolean(property.Value); break;

                default: throw new InvalidDataException("Unknown access setting.");
            }
        }
        if (!seen.Contains("schemaVersion") || !seen.Contains("inventory") || !seen.Contains("quests") ||
            !seen.Contains("webSearch") || schemaVersion == 1 && seen.Contains("chatHistory") ||
            schemaVersion >= 2 && !seen.Contains("chatHistory") ||
            schemaVersion < 3 && seen.Contains("gameChatReplies") ||
            schemaVersion >= 3 && !seen.Contains("gameChatReplies") ||
            schemaVersion < 4 && (seen.Contains("characterContext") || seen.Contains("npcDialogue")) ||
            schemaVersion >= 4 && (!seen.Contains("characterContext") || !seen.Contains("npcDialogue")) ||
            schemaVersion < 5 && seen.Contains("location") || schemaVersion >= 5 && !seen.Contains("location") ||
            schemaVersion < 6 && seen.Contains("spoilersAllowed") || schemaVersion >= 6 && !seen.Contains("spoilersAllowed") ||
            schemaVersion < 7 && seen.Contains("currencies") || schemaVersion >= 7 && !seen.Contains("currencies") ||
            schemaVersion < 8 && seen.Contains("duties") || schemaVersion >= 8 && !seen.Contains("duties") ||
            schemaVersion < 9 && seen.Contains("gcSupply") || schemaVersion >= 9 && !seen.Contains("gcSupply") ||
            schemaVersion < 10 && seen.Contains("targetInfo") || schemaVersion >= 10 && !seen.Contains("targetInfo"))
            throw new InvalidDataException("Missing or unsupported access setting.");
        foreach (var field in new[] { "retainers", "saddlebag", "housing", "fcChest" })
            if (seen.Contains(field) != (schemaVersion >= 11)) throw new InvalidDataException("Invalid storage permission.");
        if (seen.Contains("armoury") != (schemaVersion >= 12))
            throw new InvalidDataException("Invalid Armoury permission.");
        if (seen.Contains("collections") != (schemaVersion >= 13))
            throw new InvalidDataException("Invalid collections permission.");
        foreach (var field in new[] { "fashionAccessories", "facewear", "emotes", "orchestrion" })
            if (seen.Contains(field) != (schemaVersion >= 14))
                throw new InvalidDataException("Invalid collectible permission.");
        foreach (var field in new[] { "achievements", "aetherCurrents", "sharedFates", "trust" })
            if (seen.Contains(field) != (schemaVersion >= 15))
                throw new InvalidDataException("Invalid progression permission.");
        foreach (var field in new[] { "blueMage", "gatheringLog" })
            if (seen.Contains(field) != (schemaVersion >= 16))
                throw new InvalidDataException("Invalid book or log permission.");
        // Version 1 never permitted older-message lookups. Preserve that boundary.
        // Versions 1 and 2 never enabled replies in local game chat.
        // Versions 1 through 3 never permitted character context or NPC dialogue.
        // Version 4's combined character/location switch already authorized both categories.
        if (seen.Contains("bestiary") != (schemaVersion >= 17))
            throw new InvalidDataException("Invalid bestiary permission.");
        foreach (var field in new[] { "craftingLog", "fishingLog" })
            if (seen.Contains(field) != (schemaVersion >= 18))
                throw new InvalidDataException("Invalid notebook permission.");
        if (seen.Contains("variantNotebook") != (schemaVersion >= 19))
            throw new InvalidDataException("Invalid Variant notebook permission.");
        foreach (var field in new[] { "shops", "glamour", "huntingLog", "sightseeingLog", "challengeLog" })
            if (seen.Contains(field) != (schemaVersion >= 20))
                throw new InvalidDataException("Invalid menu or log permission.");
        foreach (var field in new[] { "tripleTriad", "battleChocobo", "pvp" })
            if (seen.Contains(field) != (schemaVersion >= 21))
                throw new InvalidDataException("Invalid activity permission.");
        foreach (var field in new[] { "tripleTriadDecks", "raceChocobo", "verminion", "freeCompany", "occultRecords", "yokaiCollection", "collectionTools" })
            if (seen.Contains(field) != (schemaVersion >= 22)) throw new InvalidDataException("Invalid expanded activity permission.");
        if (seen.Contains("mahjong") != (schemaVersion >= 23)) throw new InvalidDataException("Invalid Mahjong permission.");
        foreach (var field in new[] { "islandProgress", "cosmicResearch" })
            if (seen.Contains(field) != (schemaVersion >= 24)) throw new InvalidDataException("Invalid Island or Cosmic progression permission.");
        foreach (var field in new[] { "islandMaterials", "cosmicMissions" })
            if (seen.Contains(field) != (schemaVersion >= 25)) throw new InvalidDataException("Invalid Island materials or Cosmic mission permission.");
        if (seen.Contains("selectedMacro") != (schemaVersion >= 26)) throw new InvalidDataException("Invalid selected macro permission.");
        if (schemaVersion == 4) location = characterContext;
        // Existing configurations never opted into story spoilers.
        // Versions 1 through 6 never granted currency access, even with inventory enabled.
        // Versions 1 through 9 never granted player/other-target access, even with NPC dialogue enabled.
        // Versions 1 through 12 never granted mount or minion unlock access.
        // Versions 1 through 13 never granted the other collectible categories.
        // Versions 1 through 14 never granted achievement or travel/content progression access.
        return new(inventory, quests, webSearch, chatHistory, gameChatReplies, characterContext, npcDialogue, location,
            spoilersAllowed, currencies, duties, gcSupply, targetInfo, retainers, saddlebag, housing, fcChest, armoury, collections,
            fashionAccessories, facewear, emotes, orchestrion, achievements, aetherCurrents, sharedFates, trust, blueMage, gatheringLog, bestiary, craftingLog, fishingLog, variantNotebook, shops, glamour, huntingLog, sightseeingLog, challengeLog, tripleTriad, battleChocobo, pvp, tripleTriadDecks, raceChocobo, verminion, freeCompany, occultRecords, yokaiCollection, collectionTools, mahjong, islandProgress, cosmicResearch, islandMaterials, cosmicMissions, selectedMacro);
    }

    private static bool ReadBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException("Expected boolean access setting."),
    };

    private static void RejectLink(string path)
    {
        // GetAttributes reports broken links too, unlike File.Exists on some platforms.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked access settings are unsupported.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
}
