using System;
using System.Collections.Generic;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CompanionAccessSettingsStore accessSettings;

    internal CompanionAccessSettings Access => this.accessSettings.Settings;
    internal string? AccessSaveError => this.accessSettings.Error;

    internal void SetAccess(bool inventory, bool quests, bool webSearch, bool? chatHistory = null,
        bool? gameChatReplies = null, bool? characterContext = null, bool? npcDialogue = null, bool? location = null,
        bool? spoilersAllowed = null, bool? currencies = null, bool? duties = null, bool? gcSupply = null,
        bool? targetInfo = null, bool? retainers = null, bool? saddlebag = null, bool? housing = null, bool? fcChest = null,
        bool? armoury = null, bool? collections = null, bool? fashionAccessories = null,
        bool? facewear = null, bool? emotes = null, bool? orchestrion = null,
        bool? achievements = null, bool? aetherCurrents = null, bool? sharedFates = null, bool? trust = null,
        bool? blueMage = null, bool? gatheringLog = null, bool? bestiary = null, bool? craftingLog = null, bool? fishingLog = null,
        bool? variantNotebook = null, bool? shops = null, bool? glamour = null, bool? huntingLog = null, bool? sightseeingLog = null, bool? challengeLog = null, bool? tripleTriad = null, bool? battleChocobo = null, bool? pvp = null, bool? tripleTriadDecks = null, bool? raceChocobo = null, bool? verminion = null, bool? freeCompany = null, bool? occultRecords = null, bool? yokaiCollection = null, bool? collectionTools = null, bool? mahjong = null,
        bool? islandProgress = null, bool? cosmicResearch = null,
        bool? islandMaterials = null, bool? cosmicMissions = null, bool? selectedMacro = null)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return;
            var previous = this.Access;
            var requested = new CompanionAccessSettings(inventory, quests, webSearch, chatHistory ?? previous.ChatHistory,
                gameChatReplies ?? previous.GameChatReplies, characterContext ?? previous.CharacterContext,
                npcDialogue ?? previous.NpcDialogue, location ?? previous.Location,
                spoilersAllowed ?? previous.SpoilersAllowed, currencies ?? previous.Currencies, duties ?? previous.Duties,
                gcSupply ?? previous.GcSupply, targetInfo ?? previous.TargetInfo, retainers ?? previous.Retainers,
                saddlebag ?? previous.Saddlebag, housing ?? previous.Housing, fcChest ?? previous.FcChest,
                armoury ?? previous.Armoury, collections ?? previous.Collections,
                fashionAccessories ?? previous.FashionAccessories, facewear ?? previous.Facewear,
                emotes ?? previous.Emotes, orchestrion ?? previous.Orchestrion,
                achievements ?? previous.Achievements, aetherCurrents ?? previous.AetherCurrents,
                sharedFates ?? previous.SharedFates, trust ?? previous.Trust, blueMage ?? previous.BlueMage,
                gatheringLog ?? previous.GatheringLog, bestiary ?? previous.Bestiary, craftingLog ?? previous.CraftingLog, fishingLog ?? previous.FishingLog,
                variantNotebook ?? previous.VariantNotebook,
                shops ?? previous.Shops, glamour ?? previous.Glamour, huntingLog ?? previous.HuntingLog, sightseeingLog ?? previous.SightseeingLog, challengeLog ?? previous.ChallengeLog,
                tripleTriad ?? previous.TripleTriad, battleChocobo ?? previous.BattleChocobo, pvp ?? previous.Pvp, tripleTriadDecks ?? previous.TripleTriadDecks, raceChocobo ?? previous.RaceChocobo, verminion ?? previous.Verminion, freeCompany ?? previous.FreeCompany, occultRecords ?? previous.OccultRecords, yokaiCollection ?? previous.YokaiCollection, collectionTools ?? previous.CollectionTools, mahjong ?? previous.Mahjong,
                islandProgress ?? previous.IslandProgress, cosmicResearch ?? previous.CosmicResearch,
                islandMaterials ?? previous.IslandMaterials, cosmicMissions ?? previous.CosmicMissions,
                selectedMacro ?? previous.SelectedMacro);
            if (requested == previous && this.AccessSaveError is null) return;
            // A failed save disables effective access, too; never grant unsaved access.
            this.accessSettings.Save(requested);
            var next = this.Access;
            this.ApplyProgressionAccessChangeUnderLock(previous, next);
            if (previous != next)
            {
                ++this.valuationAccessEpoch;
                this.readerObservations.Clear();
                this.ResetMenuCaptureUnderLock();
                this.ApplyRecentReaderAccessChangeUnderLock(previous, next);
                this.ApplyActivityAccessChangeUnderLock(previous, next);
            }
            this.companion.SetGameChatReplies(next.GameChatReplies);
            this.preparedQuestion = null;
            this.ApplyGameContextAccessChangeUnderLock(previous, next);
            if (previous.Inventory && !next.Inventory)
            {
                this.PauseInventoryUnderLock("Inventory access is off; saved data retained.");
                this.inventoryWatching = false;
                this.ClearPedigreeScanUnderLock("Inventory access is off.");
            }
            if (previous.Quests && !next.Quests) this.PauseQuestsForOffline("Quest access is off; saved data retained.");
            if ((previous.Inventory && !next.Inventory || previous.Quests && !next.Quests ||
                 previous.WebSearch && !next.WebSearch || previous.ChatHistory && !next.ChatHistory ||
                 previous.CharacterContext && !next.CharacterContext || previous.NpcDialogue && !next.NpcDialogue ||
                 previous.Location && !next.Location || previous.SpoilersAllowed && !next.SpoilersAllowed ||
                 previous.Currencies && !next.Currencies || previous.Duties && !next.Duties ||
                 previous.GcSupply && !next.GcSupply || previous.TargetInfo && !next.TargetInfo ||
                 previous.Retainers && !next.Retainers || previous.Saddlebag && !next.Saddlebag ||
                 previous.Housing && !next.Housing || previous.FcChest && !next.FcChest ||
                 previous.Armoury && !next.Armoury || previous.Collections && !next.Collections ||
                 previous.FashionAccessories && !next.FashionAccessories || previous.Facewear && !next.Facewear ||
                 previous.Emotes && !next.Emotes || previous.Orchestrion && !next.Orchestrion ||
                 previous.Achievements && !next.Achievements || previous.AetherCurrents && !next.AetherCurrents ||
                 previous.SharedFates && !next.SharedFates || previous.Trust && !next.Trust ||
                 previous.BlueMage && !next.BlueMage || previous.GatheringLog && !next.GatheringLog ||
                 previous.Bestiary && !next.Bestiary || previous.CraftingLog && !next.CraftingLog ||
                 previous.FishingLog && !next.FishingLog || previous.VariantNotebook && !next.VariantNotebook ||
                 previous.Shops && !next.Shops || previous.Glamour && !next.Glamour || previous.HuntingLog && !next.HuntingLog || previous.SightseeingLog && !next.SightseeingLog || previous.ChallengeLog && !next.ChallengeLog ||
                 previous.TripleTriad && !next.TripleTriad || previous.BattleChocobo && !next.BattleChocobo || previous.Pvp && !next.Pvp ||
                 previous.TripleTriadDecks && !next.TripleTriadDecks || previous.RaceChocobo && !next.RaceChocobo || previous.Verminion && !next.Verminion || previous.FreeCompany && !next.FreeCompany || previous.OccultRecords && !next.OccultRecords || previous.YokaiCollection && !next.YokaiCollection || previous.CollectionTools && !next.CollectionTools || previous.Mahjong && !next.Mahjong ||
                 previous.IslandProgress && !next.IslandProgress || previous.CosmicResearch && !next.CosmicResearch ||
                 previous.IslandMaterials && !next.IslandMaterials || previous.CosmicMissions && !next.CosmicMissions ||
                 previous.SelectedMacro && !next.SelectedMacro))
                this.companion.CancelAllQuestions();
            if (this.CanReadCurrentCharacter)
            {
                if (next.Inventory && !previous.Inventory) this.Capture();
                if (next.Quests && !previous.Quests) this.CaptureQuests();
            }
        }
    }

    internal bool CanSendChatQuestion(string question) => this.GetChatQuestionBlockReason(question) is null;
    internal string? GetChatQuestionBlockReason(string question)
    {
        lock (this.stateGate)
        {
            var selected = ChatContextSelection.Create(this.Access, this.CanReadCurrentCharacter,
                this.snapshot is not null || this.pedigrees.HasReadings, this.quests.View.Snapshot is not null);
            return this.QuestionValidationError(question, selected.Inventory);
        }
    }

    internal bool RetryChatQuestion(string chatId, string question)
    {
        lock (this.stateGate)
        {
            var current = this.CompanionView;
            return current.ActiveChatId == chatId && current.RetryQuestion == question &&
                !current.ActiveChatBusy && current.CanTest && this.SendChatQuestion(question,
                    attachments: this.companion.RetryAttachments(chatId));
        }
    }

    internal bool SendChatQuestion(string question, bool replyInGameChat = false, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        lock (this.stateGate)
        {
            var selected = ChatContextSelection.Create(this.Access, this.CanReadCurrentCharacter,
                this.snapshot is not null || this.pedigrees.HasReadings, this.quests.View.Snapshot is not null);
            var sent = this.SendQuestion(question, selected.Inventory, selected.WebSearch, selected.Quests, replyInGameChat, attachments);
            if (sent)
            {
                this.chatCommandNotice = null;
                if (replyInGameChat) this.PrintGameChatInput(question.Trim());
                this.chatWindow.FollowLatestMessage();
            }
            return sent;
        }
    }
}
