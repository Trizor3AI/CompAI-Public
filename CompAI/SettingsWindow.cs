using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace CompAI;

internal sealed partial class SettingsWindow : Window
{
    private static readonly string VersionLabel = "CompAI " + typeof(Plugin).Assembly.GetName().Version?.ToString(3);
    private readonly Plugin plugin;
    private readonly ConnectionErrorClipboard errorClipboard = new();
    private ChatTheme? windowTheme;
    private bool selectPedigrees;
    private bool selectAccess;
    private readonly QuestPanel questPanel = new();
    private readonly PersonalizationPanel personalizationPanel = new();
    private Vector3? gameChatUserColorDraft;
    private bool accountWebsiteOpenFailed;
    private bool supportWebsiteOpenFailed;
    private ProgressionClearRequest? progressionClearRequest;
    private bool openProgressionClear;
    private string? progressionClearNotice;

    internal void OpenPedigrees() { this.IsOpen = true; this.selectPedigrees = true; }

    internal SettingsWindow(Plugin plugin) : base("CompAI settings##ffctx")
    {
        this.plugin = plugin;
        this.Size = new Vector2(680, 560);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(4096, 4096),
        };
        this.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void PreDraw()
    {
        this.windowTheme = ChatTheme.Push();
        var workSize = ImGui.GetMainViewport().WorkSize;
        var scale = ImGuiHelpers.GlobalScale;
        if (!float.IsFinite(scale) || scale <= 0 || workSize.X <= 0 || workSize.Y <= 0) return;

        // WindowSystem scales these properties; viewport dimensions are already pixels.
        // Bound remembered sizes too, without replacing a user's ordinary saved size.
        var maximum = new Vector2(Math.Min(4096, workSize.X * 0.95f / scale), workSize.Y * 0.90f / scale);
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Vector2.Min(new Vector2(520, 380), maximum),
            MaximumSize = maximum,
        };
        this.Size = Vector2.Min(new Vector2(680, 560), maximum);
    }

    public override void PostDraw()
    {
        this.windowTheme?.Dispose();
        this.windowTheme = null;
    }

    public override void Draw()
    {
        // A reset can leave ImGui's saved geometry intact. Recover an offscreen
        // main-viewport position once on opening; do not move detached windows.
        var viewport = ImGui.GetMainViewport();
        if (ImGui.IsWindowAppearing() && ImGui.GetWindowViewport().ID == viewport.ID)
        {
            var position = ImGui.GetWindowPos();
            var maximum = Vector2.Max(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - ImGui.GetWindowSize());
            var visiblePosition = Vector2.Clamp(position, viewport.WorkPos, maximum);
            if (visiblePosition != position) ImGui.SetWindowPos(visiblePosition, ImGuiCond.Always);
        }
        // Only tab bodies scroll; clear any parent offset retained by an older layout.
        ImGui.SetScrollY(0);
        this.plugin.OpenPendingLoginUrl();
        if (this.plugin.CoreCommandWarning is { } commandWarning) ImGui.TextWrapped(commandWarning);
        this.DrawSettingsHeader();
        if (!ImGui.BeginTabBar("ContextTabs", ImGuiTabBarFlags.FittingPolicyScroll))
            return;
        if (ImGui.BeginTabItem("Access", this.selectAccess ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            this.selectAccess = false;
            ImGui.BeginChild("##access_content", Vector2.Zero, false);
            this.DrawAccess();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Account"))
        {
            ImGui.BeginChild("##account_content", Vector2.Zero, false);
            this.DrawAccount();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Personalization"))
        {
            ImGui.BeginChild("##personalization_content", Vector2.Zero, false);
            ChatTheme.SectionHeading("Interface");
            var hideKofi = this.plugin.HideKofi;
            if (ImGui.Checkbox("Hide Ko-fi buttons", ref hideKofi))
                this.plugin.SetHideKofi(hideKofi);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hides both support buttons. Saves automatically.");
            if (this.plugin.UiSettingsError is { } uiError) ImGui.TextWrapped(uiError);
            ImGui.Separator();
            this.personalizationPanel.Draw(this.plugin);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Game data", this.selectPedigrees ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            this.DrawGameData();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Storage"))
        {
            ImGui.BeginChild("##storage_content", Vector2.Zero, false);
            this.DrawStorage();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Beta"))
        {
            ImGui.BeginChild("##beta_content", Vector2.Zero, false);
            this.DrawBeta();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Diagnostics"))
        {
            ImGui.BeginChild("##diagnostics_content", Vector2.Zero, false);
            this.DrawDiagnostics();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        this.DrawProgressionClearModal();
    }

    private void DrawSettingsHeader()
    {
        var rowStart = ImGui.GetCursorPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var rowRight = rowStart.X + rowWidth;
        var style = ImGui.GetStyle();
        var rowHeight = ImGui.GetFrameHeight();
        var rowBottom = rowStart.Y + rowHeight;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(VersionLabel);
        ImGui.SameLine();
        if (ImGui.Button("Open chat")) this.plugin.OpenChat();

        if (!this.plugin.HideKofi)
        {
            var inset = style.ItemSpacing.X;
            var supportSize = SupportButton.GetSize(rowWidth - inset, wordmark: true);
            var nextItemX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + style.ItemSpacing.X;
            var supportX = rowRight - inset - supportSize.X;
            if (supportX >= nextItemX)
                ImGui.SameLine(supportX);
            var slotStart = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(Math.Max(slotStart.X, supportX), slotStart.Y + (rowHeight - supportSize.Y) / 2));
            SupportButton.Draw(supportSize, ref this.supportWebsiteOpenFailed, wordmark: true);
            rowBottom = Math.Max(rowBottom, slotStart.Y + rowHeight);
        }

        // Match the padding below the title bar, centering the normal-height row
        // in the whole header band without an extra dummy/group spacing row.
        ImGui.SetCursorPos(new Vector2(rowStart.X, rowBottom + style.WindowPadding.Y));
        if (!this.plugin.HideKofi && this.supportWebsiteOpenFailed)
        {
            ImGui.TextWrapped(SupportButton.OpenFailedMessage);
            ImGui.Spacing();
        }
    }

    private void DrawProgressionClearModal()
    {
        if (this.openProgressionClear)
        {
            ImGui.OpenPopup("Clear saved progression?##progression_clear");
            this.openProgressionClear = false;
        }
        if (!ImGui.BeginPopupModal("Clear saved progression?##progression_clear", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextWrapped("Remove these saved observations for this character? Access will be turned off so an open menu does not immediately refill them. Your game progress is unchanged.");
        ImGui.PopTextWrapPos();
        if (ImGui.Button("Clear"))
        {
            this.progressionClearNotice = this.progressionClearRequest is { } request && this.plugin.ClearSavedProgression(request)
                ? "Saved observations cleared. Re-enable access to record pages again."
                : "Character changed or cache is loading. Try clearing again.";
            this.progressionClearRequest = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { this.progressionClearRequest = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawSavedProgression(string category, string label)
    {
        ImGui.PushID(category);
        ImGui.TextWrapped(label + ": " + this.plugin.SavedProgressionStatus(category));
        var request = this.plugin.PrepareProgressionClear(category);
        ImGui.BeginDisabled(request is null);
        if (ImGui.SmallButton("Clear saved " + label))
        {
            this.progressionClearRequest = request;
            this.openProgressionClear = true;
        }
        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private void DrawGameData()
    {
        if (!ImGui.BeginTabBar("GameDataTabs", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        if (ImGui.BeginTabItem("Inventory"))
        {
            ImGui.BeginChild("##inventory_content", Vector2.Zero, false);
            if (this.plugin.Access.Inventory) this.DrawInventory();
            else this.DrawAccessDisabled("Inventory");
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Pedigrees", this.selectPedigrees ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            this.selectPedigrees = false;
            ImGui.BeginChild("##pedigrees_content", Vector2.Zero, false);
            if (this.plugin.Access.Inventory) PedigreePanel.Draw(this.plugin);
            else this.DrawAccessDisabled("Inventory");
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Quests"))
        {
            ImGui.BeginChild("##quests_content", Vector2.Zero, false);
            if (this.plugin.Access.Quests) this.questPanel.Draw(this.plugin);
            else this.DrawAccessDisabled("Quest journal");
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Other storage"))
        {
            ImGui.BeginChild("##other_storage_content", Vector2.Zero, false);
            this.DrawObservedStorage();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Export"))
        {
            ImGui.BeginChild("##export_content", Vector2.Zero, false);
            if (this.plugin.Access.Inventory) this.DrawExport();
            else this.DrawAccessDisabled("Inventory");
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawAccess()
    {
        ImGui.TextWrapped("Choose what the companion may look up when you send a message. Applies to all chats and saves automatically.");
        ImGui.Spacing();
        ChatTheme.SectionHeading("Game readers");
        var access = this.plugin.Access;
        var inventory = access.Inventory;
        var quests = access.Quests;
        var webSearch = access.WebSearch;
        var chatHistory = access.ChatHistory;
        var gameChatReplies = access.GameChatReplies;
        var characterContext = access.CharacterContext;
        var location = access.Location;
        var npcDialogue = access.NpcDialogue;
        var spoilersAllowed = access.SpoilersAllowed;
        var currencies = access.Currencies;
        var duties = access.Duties;
        var gcSupply = access.GcSupply;
        var targetInfo = access.TargetInfo;
        var retainers = access.Retainers; var saddlebag = access.Saddlebag; var housing = access.Housing; var fcChest = access.FcChest;
        var armoury = access.Armoury;
        var collections = access.Collections;
        var fashionAccessories = access.FashionAccessories;
        var facewear = access.Facewear;
        var emotes = access.Emotes;
        var orchestrion = access.Orchestrion;
        var achievements = access.Achievements;
        var aetherCurrents = access.AetherCurrents;
        var sharedFates = access.SharedFates;
        var trust = access.Trust;
        var blueMage = access.BlueMage;
        var gatheringLog = access.GatheringLog;
        var bestiary = access.Bestiary;
        var craftingLog = access.CraftingLog;
        var fishingLog = access.FishingLog;
        var variantNotebook = access.VariantNotebook;
        var shops = access.Shops;
        var glamour = access.Glamour;
        var huntingLog = access.HuntingLog;
        var sightseeingLog = access.SightseeingLog;
        var challengeLog = access.ChallengeLog;
        var tripleTriad = access.TripleTriad; var battleChocobo = access.BattleChocobo; var pvp = access.Pvp;
        var tripleTriadDecks = access.TripleTriadDecks; var raceChocobo = access.RaceChocobo; var verminion = access.Verminion; var freeCompany = access.FreeCompany; var occultRecords = access.OccultRecords; var collectionTools = access.CollectionTools; var mahjong = access.Mahjong;
        var islandProgress = access.IslandProgress; var cosmicResearch = access.CosmicResearch;
        var islandMaterials = access.IslandMaterials; var cosmicMissions = access.CosmicMissions;
        var selectedMacro = access.SelectedMacro;
        ImGui.BeginDisabled(inventory && currencies && duties && gcSupply && quests && characterContext && location && npcDialogue && targetInfo && retainers && saddlebag && housing && fcChest && armoury && collections && fashionAccessories && facewear && emotes && orchestrion && achievements && aetherCurrents && sharedFates && trust && blueMage && gatheringLog && bestiary && craftingLog && fishingLog && variantNotebook && shops && glamour && huntingLog && sightseeingLog && challengeLog && tripleTriad && battleChocobo && pvp && tripleTriadDecks && raceChocobo && verminion && freeCompany && occultRecords && collectionTools && mahjong && islandProgress && cosmicResearch && islandMaterials && cosmicMissions && selectedMacro);
        var changed = ImGui.SmallButton("Enable all readers");
        ImGui.EndDisabled();
        ShowItemHelp("Enables the game readers below. Online access, older-message lookup, spoilers and game-chat replies keep their current settings.");
        if (changed) inventory = currencies = duties = gcSupply = quests = characterContext = location = npcDialogue = targetInfo = retainers = saddlebag = housing = fcChest = armoury = collections = fashionAccessories = facewear = emotes = orchestrion = achievements = aetherCurrents = sharedFates = trust = blueMage = gatheringLog = bestiary = craftingLog = fishingLog = variantNotebook = shops = glamour = huntingLog = sightseeingLog = challengeLog = tripleTriad = battleChocobo = pvp = tripleTriadDecks = raceChocobo = verminion = freeCompany = occultRecords = collectionTools = mahjong = islandProgress = cosmicResearch = islandMaterials = cosmicMissions = selectedMacro = true;
        ImGui.SameLine();
        ImGui.BeginDisabled(!inventory && !currencies && !duties && !gcSupply && !quests && !characterContext && !location && !npcDialogue && !targetInfo && !retainers && !saddlebag && !housing && !fcChest && !armoury && !collections && !fashionAccessories && !facewear && !emotes && !orchestrion && !achievements && !aetherCurrents && !sharedFates && !trust && !blueMage && !gatheringLog && !bestiary && !craftingLog && !fishingLog && !variantNotebook && !shops && !glamour && !huntingLog && !sightseeingLog && !challengeLog && !tripleTriad && !battleChocobo && !pvp && !tripleTriadDecks && !raceChocobo && !verminion && !freeCompany && !occultRecords && !collectionTools && !mahjong && !islandProgress && !cosmicResearch && !islandMaterials && !cosmicMissions && !selectedMacro);
        var disableReaders = ImGui.SmallButton("Disable all readers");
        ImGui.EndDisabled();
        ShowItemHelp("Disables the game readers below. Other permissions and answer preferences keep their current settings.");
        if (disableReaders)
        {
            inventory = currencies = duties = gcSupply = quests = characterContext = location = npcDialogue = targetInfo = retainers = saddlebag = housing = fcChest = armoury = collections = fashionAccessories = facewear = emotes = orchestrion = achievements = aetherCurrents = sharedFates = trust = blueMage = gatheringLog = bestiary = craftingLog = fishingLog = variantNotebook = shops = glamour = huntingLog = sightseeingLog = challengeLog = tripleTriad = battleChocobo = pvp = tripleTriadDecks = raceChocobo = verminion = freeCompany = occultRecords = collectionTools = mahjong = islandProgress = cosmicResearch = islandMaterials = cosmicMissions = selectedMacro = false;
            changed = true;
        }
        // Snapshot metadata once for this draw; no game reader is invoked by the access UI.
        var readerStatuses = this.plugin.ReaderStatuses();
        ImGui.Spacing();
        ChatTheme.SectionHeading("Character and world");
        if (BeginReaderOptions("reader-access-character"))
        {
            changed |= ReaderAccessOption(readerStatuses, "character_details", "Character info", ref characterContext,
                "Your name, jobs, levels, EXP, equipped gear and saved gearsets.",
                "Uses available character details when needed for a reply, including the current unsynced level. Saved gearsets may differ from currently equipped gear.");
            changed |= ReaderAccessOption(readerStatuses, "location", "Location", ref location,
                "Current zone, current world and home world. No exact coordinates.",
                "Named location is available for relevant answers while this permission is on.");
            changed |= ReaderAccessOption(readerStatuses, "npc_dialogue", "NPC dialogue and selected NPC", ref npcDialogue,
                "Recent observed NPC dialogue and the identity of your selected NPC.",
                "Keeps up to 128 NPC Talk lines from the past 30 minutes within a text limit. Earlier-area lines are marked. Memory only: clears on logout, character change, reload or permission removal. Dialogue may be missed. Selected NPC identity includes its name and game-data ID, not shops or coordinates. No player chat or interaction automation.");
            changed |= ReaderAccessOption(readerStatuses, "npc_target", "Players and other selected targets", ref targetInfo,
                "Selected target only: visible name, world, job, level and basic object identity.",
                "May include a Lodestone character search link, which is not a verified profile match. No nearby-player scans, equipment inspection or player chat. NPC identity uses the separate permission above.");
            changed |= ReaderAccessOption(readerStatuses, "selected_macro", "Selected macro", ref selectedMacro,
                "Open User Macros and select one macro to refresh its name and 15 lines.",
                "Used only when the assistant requests it; retained for 15 minutes in this login. No other macros are read, and macros are never executed.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Inventory and storage");
        if (BeginReaderOptions("reader-access-inventory"))
        {
            changed |= ReaderAccessOption(readerStatuses, "island_materials", "Island materials", ref islandMaterials,
                "Open Isleventory to refresh discovered materials and Felicitous Token. Kept for 15 minutes in this login. Key tools, other tokens, undiscovered items, a complete inventory and island ownership are not established.");
            changed |= ReaderAccessOption(readerStatuses, "inventory", "Inventory (bags, crystals, key items)", ref inventory,
                "Carried bags, shards/crystals/clusters, key items and pedigree readings.",
                "Saved personal inventory lookups while answering. Browse crystals and key items under Game data → Other storage. Retainers and shared storage use their separate permissions.");
            changed |= ReaderAccessOption(readerStatuses, "armoury", "Armoury Chest", ref armoury,
                "Remembers owned equipment in loaded Armoury slots, including HQ and attached materia.",
                "Saved per character under Game data → Other storage. Equipped gear and saved gearsets use Character info. Armoury slots do not prove an item is saleable and are excluded from sale-value rankings.");
            changed |= ReaderAccessOption(readerStatuses, "currencies", "Currencies", ref currencies,
                "Supported balances, including ventures, scrips and hunt currencies.",
                "Cosmic credits need the loaded exploration zone. Islekeep's Index or the island export menu refreshes cowries; they are remembered for 15 minutes in this login. No disk cache. Missing balances remain unknown; weekly caps and other activity-specific currencies may be unsupported.");
            changed |= ReaderAccessOption(readerStatuses, "retainers", "Retainers and sale listings", ref retainers,
                "Remembers bags and asking prices when you open a retainer's menus.",
                "Last observed contents and sale offers only, not completed sales. Refresh by visiting the retainer. Saved per character.");
            changed |= ReaderAccessOption(readerStatuses, "saddlebag", "Chocobo saddlebags", ref saddlebag,
                "Remembers loaded standard and premium saddlebags.",
                "Open the saddlebag to refresh it. Unavailable premium storage is unknown, not empty.");
            changed |= ReaderAccessOption(readerStatuses, "housing", "Housing furnishings", ref housing,
                "Remembers loaded placed furnishings and storeroom contents.",
                "Open housing furnishing menus. Each observed property is separate; unloaded rooms or storage pages remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "fc_chest", "Free Company chest", ref fcChest,
                "Remembers chest pages you can access.",
                "Open the FC member list once, then the chest. Only observed pages with verified FC membership are saved. Other members can change them later.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Shops and glamour");
        if (BeginReaderOptions("reader-access-menus"))
        {
            changed |= ReaderAccessOption(readerStatuses, "shops", "NPC shops and exchanges", ref shops,
                "Remembers the last supported shop or exchange for 5 minutes after observation. Open it, let it load, then close it and ask. A different shop replaces the previous one. Unsupported costs remain unknown; no purchases or automatic menu actions.");
            changed |= ReaderAccessOption(readerStatuses, "glamour", "Glamour storage and plates", ref glamour,
                "Remembers loaded armoire, dresser and saved plate observations per character. Open each menu and let it load; last-known observations remain after closing or reloading. Save plate edits before refreshing. Plate references are not extra owned items.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Quests, duties and deliveries");
        if (BeginReaderOptions("reader-access-quests"))
        {
            changed |= ReaderAccessOption(readerStatuses, "quests", "Quest journal", ref quests,
                "Active and observed completed quests; progress coverage may be incomplete.",
                "Allows name/ID lookups of saved quest records while answering. Enables Quests under Game data.");
            changed |= ReaderAccessOption(readerStatuses, "duties", "Duty unlocks and clears", ref duties,
                "Observed unlocks and completions for supported instance duties.",
                "Read on demand, with no polling or disk cache. Unset or unsupported flags remain unknown. This is not a complete history or a guarantee of queue eligibility.");
            changed |= ReaderAccessOption(readerStatuses, "gc_supply", "Grand Company deliveries", ref gcSupply,
                "Remembers observed delivery requests for 15 minutes in this login.",
                "Open Supply or Provisioning and let it load, then close it and ask. Requests, quantities and rewards are last known; listed requests may already have been delivered. Completion and bonus stars remain unknown. Clears on logout or reload; no expert deliveries or automatic hand-ins. Inventory access is separate.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Collectible unlocks");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        ShowItemHelp("Read locally on demand, with no polling or disk cache. An unconfirmed unlock remains unknown; this cannot produce a complete missing-collection list.");
        ImGui.TextWrapped("Observed unlocks for your current character. Each category has separate access.");
        if (BeginReaderOptions("reader-access-collections"))
        {
            changed |= ReaderAccessOption(readerStatuses, "collections", "Mounts and minions", ref collections,
                "Ask about a named mount or minion. Unconfirmed unlocks remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "fashion_accessories", "Fashion accessories", ref fashionAccessories,
                "Ask about a named fashion accessory. Unconfirmed unlocks remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "facewear", "Facewear", ref facewear,
                "Ask about a named facewear style. Unconfirmed unlocks remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "emotes", "Emotes", ref emotes,
                "Ask about a named emote. Unlock does not establish current usability.");
            changed |= ReaderAccessOption(readerStatuses, "orchestrion", "Orchestrion rolls", ref orchestrion,
                "Ask about registered music, separately from physical orchestrion rolls.");
            changed |= ReaderAccessOption(readerStatuses, "triple_triad", "Triple Triad cards", ref tripleTriad,
                "Registered cards from your current character, read when asked. Unconfirmed cards stay unknown. Decks, NPC victories and physical inventory cards are separate.");
            changed |= ReaderAccessOption(readerStatuses, "collection_tools", "Collection tools and books", ref collectionTools,
                "Collection tool availability and Mogpendium: Standard catalog, observed Weekly/Minimog lists and supported selected details. Open the category to refresh; retained for 15 minutes in this login. The selected detail's week, completion and reward claims remain unknown. Field Record selected entry/total and supported Portable Archive selected title are included; unseen entries are unknown.");
            if (collectionTools)
            {
                ReaderStatusDetail(readerStatuses, "field_record", "Field Record");
                ReaderStatusDetail(readerStatuses, "portable_archive", "Portable Archive");
            }
            changed |= ReaderAccessOption(readerStatuses, "occult_records", "Occult Records", ref occultRecords,
                "Observed unlocked Occult Record entries. Unconfirmed entries remain unknown; story text is not read.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Progression");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        ShowItemHelp("Readers marked Open to refresh remember loaded menu observations while access is enabled. Unobserved pages remain unknown; hover each reader for its retention period.");
        if (BeginReaderOptions("reader-access-progression"))
        {
            changed |= ReaderAccessOption(readerStatuses, "achievements", "Achievements", ref achievements,
                "Open Achievements to load completion. Select a counter entry and let it load; its observed counter remains available for 15 minutes in this login after closing. A loaded counter may belong to a previous selection and is identified by its own achievement. Counter observations clear on logout or reload. Reward claims are not included.");
            changed |= ReaderAccessOption(readerStatuses, "aether_currents", "Aether current zones", ref aetherCurrents,
                "Zone completion and confirmed discoveries out of each zone's current list, read when asked. Unconfirmed currents remain unknown; no exact missing list, current locations or mount speed.");
            changed |= ReaderAccessOption(readerStatuses, "shared_fates", "Shared FATE", ref sharedFates,
                "Remembers ranks and progress from each Shared FATE page you open. Wait for the page to settle. Last-known observations survive closing the menu, travel and reload; unvisited pages remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "trust", "Trust", ref trust,
                "Remembers member levels and EXP from each loaded Trust group you open. Last-known observations survive closing the menu, travel and reload. They do not establish current duty eligibility.");
            changed |= ReaderAccessOption(readerStatuses, "variant_notebook", "Variant notebook", ref variantNotebook,
                "Checks observed completed Variant notebook records when asked. Unconfirmed records remain unknown. Does not establish Criterion clears, rewards or route walkthroughs.");
            changed |= ReaderAccessOption(readerStatuses, "battle_chocobo", "Battle chocobo", ref battleChocobo,
                "Rank, EXP, role ranks and unspent skill points from loaded companion data. Training, feeding and racing pedigrees are not included.");
            changed |= ReaderAccessOption(readerStatuses, "pvp", "PvP progression", ref pvp,
                "Open PvP Profile to refresh personal rank/EXP, current Series level/EXP target and public reward previews. Observations last 15 minutes in this login. Competitive tiers, history and reward claims are not included.");
            changed |= ReaderAccessOption(readerStatuses, "island_progress", "Island Sanctuary progress", ref islandProgress,
                "Open the visible Islekeep's Index for displayed Sanctuary rank and EXP. Observations last 15 minutes in this login and do not establish island ownership. Materials use the separate Island materials permission; cowries use Currencies.");
            changed |= ReaderAccessOption(readerStatuses, "cosmic_research", "Cosmic research", ref cosmicResearch,
                "Open Cosmic Research Information in the loaded Cosmic zone. Reads all 11 jobs' stages and banked research, retained for 15 minutes after closing in this login. Required amounts, caps, rates and upgrade eligibility are not established. Mission medals use the separate Cosmic mission medals permission. Dronebits are not included.");
            changed |= ReaderAccessOption(readerStatuses, "cosmic_missions", "Cosmic mission medals", ref cosmicMissions,
                "Browse Cosmic Mission Log views to retain observed Silver and Gold medals across jobs and planets. Up to 512 mission/job entries; each lasts 15 minutes after observation in this login. Planet labels and unvisited or missing medals remain unknown.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Activities and Free Company");
        if (BeginReaderOptions("reader-access-activities"))
        {
            changed |= ReaderAccessOption(readerStatuses, "triple_triad_decks", "Triple Triad decks (Open to refresh)", ref tripleTriadDecks,
                "Open Gold Saucer → Card Decks. Reads five configured decks and their card slots, separate from registered-card ownership. Retained for 15 minutes in this login.");
            changed |= ReaderAccessOption(readerStatuses, "race_chocobo", "Racing chocobo (Open to refresh)", ref raceChocobo,
                "Open Gold Saucer → Chocobo. Reads current/max stats, rank, EXP, rating/class, color, abilities, pedigree and parent stars, and available training sessions. Retained for 15 minutes in this login.");
            changed |= ReaderAccessOption(readerStatuses, "verminion", "Verminion hotbar (Open to refresh)", ref verminion,
                "Open the Lord of Verminion Gold Saucer tab. Reads configured minion slots, not rankings or results. Retained for 15 minutes in this login.");
            changed |= ReaderAccessOption(readerStatuses, "mahjong", "Mahjong (Open to refresh)", ref mahjong,
                "Open Gold Saucer → Mahjong. Reads the displayed rank, points, next-rank text, matches played and ratings. Retained for 15 minutes in this login; no inferred rankings or reward claims.");
            changed |= ReaderAccessOption(readerStatuses, "free_company", "Free Company info (Open to refresh)", ref freeCompany,
                "Open the main Free Company menu for supported profile/board text, ranks and public entitlements. Topics or the Company Credit item shop supplies the displayed balance; it is not joined to a named FC or spending permission. Observations last 15 minutes in this login. No chat, roster, action lists or detailed rank permissions.");
            ImGui.EndTable();
        }
        if (ImGui.TreeNode("Saved Shared FATE and Trust"))
        {
            this.DrawSavedProgression("shared_fates", "Shared FATE");
            this.DrawSavedProgression("trust", "Trust");
            if (this.progressionClearNotice is { } clearNotice) ImGui.TextWrapped(clearNotice);
            ImGui.TreePop();
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Books and logs");
        if (BeginReaderOptions("reader-access-books"))
        {
            changed |= ReaderAccessOption(readerStatuses, "hunting_log", "Hunting log", ref huntingLog,
                "Remembers each loaded hunting book and rank per character. Open the view and let it settle; last-known observations survive closing, travel and reload. Unseen or unsupported progress remains unknown.");
            changed |= ReaderAccessOption(readerStatuses, "sightseeing_log", "Sightseeing log", ref sightseeingLog,
                "Checks observed completed sightseeing entries for the loaded character. Unconfirmed entries remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "challenge_log", "Challenge log", ref challengeLog,
                "Remembers observed Challenge Log tabs for 15 minutes, up to the Tuesday 08:00 UTC weekly reset. Open each tab and let it settle, then close it and ask. Completion and supported counters are last known; unseen entries remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "blue_mage_spells", "Blue Mage spellbook", ref blueMage,
                "Observed learned spells from the full local spellbook catalog, separate from equipped actions. Ask by name or displayed spell number. Unconfirmed entries remain unknown.");
            changed |= ReaderAccessOption(readerStatuses, "beastmaster_bestiary", "Master's Bestiary", ref bestiary,
                "Open the normal Bestiary and let it load. Observed permanent familiar unlocks remain available for 15 minutes in this login after closing; they clear on logout or reload. Unconfirmed entries remain unknown. Mirage and temporary content pets are excluded.");
            changed |= ReaderAccessOption(readerStatuses, "crafting_log", "Crafting log", ref craftingLog,
                "Checks crafted-before entries only for recipes matching your question. Public recipe definitions remain available separately. Unconfirmed entries stay unknown; no full-log completion total is inferred.");
            changed |= ReaderAccessOption(readerStatuses, "fishing_log", "Fishing and spearfishing logs", ref fishingLog,
                "Observed caught-before entries from the fishing and visible spearfishing logs. Unconfirmed entries stay unknown. This does not read catch size, records, current stock or fishing conditions.");
            changed |= ReaderAccessOption(readerStatuses, "gathering_log", "Gathering log", ref gatheringLog,
                "Observed gathered-before entries from the mining/botany notebook. This does not include fishing, current node availability, collectability or items you currently own. Unconfirmed entries remain unknown.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Reader details and saved data"))
        {
            ImGui.TextWrapped("Send allows lookups for that reply; full game context is not attached automatically. Open to refresh readers collect locally while their menus load, so asking does not require the menu to remain open. Hover each reader for retention and coverage.");
            ImGui.TextWrapped("Turning access off stops collection and cancels pending answers. NPC dialogue is cleared; saved inventory, quest caches and chat history remain. See Storage for their locations and retention.");
            ImGui.TextWrapped("Public recipe, action and quest definitions use installed game data and remain available without personal-reader or online access.");
        }
        ImGui.Spacing();
        ChatTheme.SectionHeading("Conversation access");
        changed |= AccessOption("Online access (web and game data)", ref webSearch,
            "Shares public queries with web research, XIVAPI and Universalis.",
            "These services receive public search terms, item IDs and requested worlds or data centres. Private reader logs and personality details must stay out of searches. Results may be cached; market uploads may be outdated. AI tool use consumes subscription allowance.");
        changed |= AccessOption("Look up older messages in this chat", ref chatHistory,
            "Searches earlier messages in this chat. Other chats stay private.",
            "Recent context and saved chat notes are sent separately. Old inventory or quest facts are historical, not current game state. Lookups use subscription allowance.");
        ImGui.Spacing();
        ChatTheme.SectionHeading("Answer preferences");
        changed |= AccessOption("Allow story spoilers", ref spoilersAllowed,
            "Off by default. Allows story details relevant to your question.",
            "When off, replies avoid story revelations beyond observed progress. Changes affect future responses; turning this off cancels an answer in progress.");
        changed |= AccessOption("Reply in game chat (Debug)", ref gameChatReplies,
            "Show slash-command replies locally in a tab with Debug messages enabled.",
            "Slash-command questions do not open CompAI. Window messages and retries stay in the plugin window. Use /compai alone to open it. Replies remain saved; nothing is posted to other players.");
        if (gameChatReplies) this.DrawGameChatColor();
        if (changed) this.plugin.SetAccess(inventory, quests, webSearch, chatHistory, gameChatReplies,
            characterContext: characterContext, npcDialogue: npcDialogue, location: location, spoilersAllowed: spoilersAllowed,
            currencies: currencies, duties: duties, gcSupply: gcSupply, targetInfo: targetInfo, retainers: retainers, saddlebag: saddlebag, housing: housing, fcChest: fcChest, armoury: armoury, collections: collections,
            fashionAccessories: fashionAccessories, facewear: facewear, emotes: emotes, orchestrion: orchestrion,
            achievements: achievements, aetherCurrents: aetherCurrents, sharedFates: sharedFates, trust: trust, blueMage: blueMage, gatheringLog: gatheringLog, bestiary: bestiary, craftingLog: craftingLog, fishingLog: fishingLog, variantNotebook: variantNotebook, shops: shops, glamour: glamour, huntingLog: huntingLog, sightseeingLog: sightseeingLog, challengeLog: challengeLog, tripleTriad: tripleTriad, battleChocobo: battleChocobo, pvp: pvp, tripleTriadDecks: tripleTriadDecks, raceChocobo: raceChocobo, verminion: verminion, freeCompany: freeCompany, occultRecords: occultRecords, collectionTools: collectionTools, mahjong: mahjong, islandProgress: islandProgress, cosmicResearch: cosmicResearch, islandMaterials: islandMaterials, cosmicMissions: cosmicMissions, selectedMacro: selectedMacro);
        if (this.plugin.AccessSaveError is { } error)
        {
            ImGui.TextWrapped("Access settings: " + error);
            if (ImGui.Button("Retry saving access settings"))
            {
                var current = this.plugin.Access;
                this.plugin.SetAccess(current.Inventory, current.Quests, current.WebSearch, current.ChatHistory,
                    current.GameChatReplies, characterContext: current.CharacterContext, npcDialogue: current.NpcDialogue,
                    location: current.Location, spoilersAllowed: current.SpoilersAllowed, currencies: current.Currencies, duties: current.Duties,
                    gcSupply: current.GcSupply, targetInfo: current.TargetInfo, retainers: current.Retainers,
                    saddlebag: current.Saddlebag, housing: current.Housing, fcChest: current.FcChest, armoury: current.Armoury, collections: current.Collections,
                    fashionAccessories: current.FashionAccessories, facewear: current.Facewear, emotes: current.Emotes, orchestrion: current.Orchestrion,
                    achievements: current.Achievements, aetherCurrents: current.AetherCurrents, sharedFates: current.SharedFates, trust: current.Trust, blueMage: current.BlueMage, gatheringLog: current.GatheringLog, bestiary: current.Bestiary, craftingLog: current.CraftingLog, fishingLog: current.FishingLog, variantNotebook: current.VariantNotebook, shops: current.Shops, glamour: current.Glamour, huntingLog: current.HuntingLog, sightseeingLog: current.SightseeingLog, challengeLog: current.ChallengeLog, tripleTriad: current.TripleTriad, battleChocobo: current.BattleChocobo, pvp: current.Pvp, tripleTriadDecks: current.TripleTriadDecks, raceChocobo: current.RaceChocobo, verminion: current.Verminion, freeCompany: current.FreeCompany, occultRecords: current.OccultRecords, collectionTools: current.CollectionTools, mahjong: current.Mahjong, islandProgress: current.IslandProgress, cosmicResearch: current.CosmicResearch, islandMaterials: current.IslandMaterials, cosmicMissions: current.CosmicMissions, selectedMacro: current.SelectedMacro);
            }
        }
    }

    private static bool AccessOption(string label, ref bool enabled, string summary, string details)
    {
        var changed = ImGui.Checkbox(label, ref enabled);
        ShowItemHelp(details);
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        ShowItemHelp(details);
        ImGui.TextWrapped(summary);
        ImGui.Spacing();
        return changed;
    }

    private static void ShowItemHelp(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
    private void DrawGameChatColor()
    {
        var saved = this.plugin.GameChatUserColor;
        var color = this.gameChatUserColorDraft ?? new Vector3((saved >> 16) / 255f,
            ((saved >> 8) & 255) / 255f, (saved & 255) / 255f);
        if (ImGui.ColorEdit3("Your message colour", ref color)) this.gameChatUserColorDraft = color;
        var selected = ((uint)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255) << 16) |
            ((uint)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255) << 8) |
            (uint)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
        ImGui.BeginDisabled(selected == saved && this.plugin.GameChatStyleError is null);
        if (ImGui.Button("Save colour") && this.plugin.SetGameChatUserColor(selected))
            this.gameChatUserColorDraft = null;
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Reset colour") && this.plugin.SetGameChatUserColor(GameChatStyleStore.DefaultUserColorRgb))
            this.gameChatUserColorDraft = null;
        ImGui.TextWrapped("Reply colours are set in Personalization.");
        ShowItemHelp("Automatic personality colours stay the same after reload or rename.");
        if (this.plugin.GameChatStyleError is { } error) ImGui.TextWrapped(error);
    }

    private void DrawAccessDisabled(string category)
    {
        ImGui.TextWrapped($"{category} access is off. Enable it in Access to use this section. Saved data is retained.");
        if (ImGui.Button("Open access settings")) this.selectAccess = true;
    }

    private void DrawExport()
    {
        ImGui.TextWrapped("Save inventory and pedigree readings as a text list and JSON. Files stay local.");
        var view = this.plugin.View;
        var export = this.plugin.ExportView;
        if (view.Snapshot is { } snapshot)
        {
            ImGui.TextUnformatted($"Inventory capture: {snapshot.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            if (view.Changed) ImGui.TextWrapped("Inventory changed since capture. Exporting keeps the saved values; use Capture inventory for updated counts.");
        }
        else ImGui.TextWrapped("No inventory capture. Pedigree readings can still be exported on their own.");
        ImGui.BeginDisabled(view.Pending || export.Busy || !this.plugin.ContextCanCapture);
        if (ImGui.Button("Capture inventory")) this.plugin.Capture();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Read pedigrees")) this.OpenPedigrees();
        ImGui.Separator();
        ImGui.BeginDisabled(!export.HasData || view.Pending || export.Busy);
        if (ImGui.Button("Export inventory + pedigrees")) this.plugin.SaveContextExport();
        if (ImGui.Button("Copy list to clipboard"))
        {
            var text = this.plugin.CopyContextExport();
            if (text is not null) ImGui.SetClipboardText(text);
        }
        ImGui.EndDisabled();
        if (view.Pending) ImGui.TextUnformatted("Waiting for inventory capture...");
        if (!export.HasData) ImGui.TextWrapped("Capture inventory or scan a pedigree to enable export.");
        ImGui.TextWrapped(export.Status);
        ImGui.TextWrapped("Exports remain saved until you delete them. See Storage for locations and sizes.");
        if (export.LastDirectory is { } path)
        {
            if (ImGui.Button("Open export folder")) this.plugin.OpenExportFolder();
            ImGui.SameLine();
            if (ImGui.Button("Copy folder path")) ImGui.SetClipboardText(path);
            ImGui.TextWrapped(path);
        }
        if (ImGui.CollapsingHeader("Export details"))
        {
            ImGui.TextWrapped("Includes item names, counts, bag/slot locations, capture times and pedigree source/status. Saved-capture pedigrees and current scan readings are listed separately.");
            ImGui.TextWrapped("Each export creates a dated folder inside the plugin configuration's exports folder. Nothing is sent to AI.");
        }
    }

    private void DrawInventory()
    {
        ImGui.TextWrapped(this.plugin.ContextCacheStatus);
        ImGui.BeginDisabled(!this.plugin.ContextCanCapture || this.plugin.View.Pending);
        if (ImGui.Button("Capture inventory now"))
            this.plugin.Capture();
        ImGui.EndDisabled();
        ShowItemHelp("Refreshes the local cache. The companion can query the latest saved inventory while answering.");
        ImGui.SameLine();
        ImGui.BeginDisabled(this.plugin.ContextCacheLoading);
        if (ImGui.Button("Clear inventory cache"))
            this.plugin.Clear();
        ImGui.EndDisabled();
        ShowItemHelp("Removes this character's saved inventory and turns inventory access off.");
        ImGui.TextWrapped("Clear removes this character's saved inventory and turns inventory access off.");

        ImGui.Separator();
        var view = this.plugin.View;
        var snapshot = view.Snapshot;
        if (view.Pending)
            ImGui.TextUnformatted("Waiting for inventory capture...");
        if (snapshot is null)
        {
            ImGui.TextUnformatted($"Inventory: unavailable ({view.UnavailableReason ?? "not captured"})");
        }
        else
        {
            var captured = snapshot.CapturedAtUtc;
            ImGui.TextUnformatted($"Captured: {captured:yyyy-MM-dd HH:mm:ss} UTC");
            ImGui.TextWrapped(view.UnavailableReason ?? (view.Changed
                ? "Showing last-known inventory; waiting for refresh."
                : "Counts match the most recent local check."));
            if (this.plugin.InventoryAutoUpdating)
            {
                ImGui.TextUnformatted("Auto-update active");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(this.plugin.InventoryLastChecked is { } checkedAt
                        ? $"Last checked {checkedAt:yyyy-MM-dd HH:mm:ss} UTC. Local checks do not use AI tokens."
                        : "Waiting for inventory to become available. Local checks do not use AI tokens.");
            }
            ImGui.TextWrapped("Carried bags 1-4 only. Completeness is unverified.");

            foreach (var container in snapshot.Containers)
            {
                if (!ImGui.CollapsingHeader(container.Type.ToString()))
                    continue;
                if (container.UnavailableReason is { } reason)
                {
                    ImGui.TextUnformatted($"Status: unavailable ({reason})");
                    continue;
                }

                ImGui.TextUnformatted($"{container.Items.Count} occupied slots of {container.SlotCount} observed");
                ShowItemHelp($"Source: IGameInventory.GetInventoryItems({container.Type})");
                foreach (var item in container.Items)
                {
                    ImGui.TextUnformatted($"{item.Name ?? "[name unavailable]"}{(item.IsHq ? " (HQ)" : "")} x {item.Quantity}");
                    ShowItemHelp($"Slot {item.Slot}; item ID {item.ItemId}; base ID {item.BaseItemId}.");
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Coverage details"))
        {
            ImGui.TextWrapped("This inventory reader does not collect armoury, currency, retainers, Free Company or housing storage. Currencies have a separate access setting.");
            ImGui.TextWrapped("Racing chocobo state, stats, abilities, rank, XP, colour, gear, sessions and rating are not collected. The native source is not runtime validated.");
            ImGui.TextWrapped("Use Pedigrees to read and check parentage from visible card tooltips.");
        }
    }

    private void DrawAccount()
    {
        var companion = this.plugin.CompanionView;
        ChatTheme.SectionHeading("AI account");
        ImGui.BeginDisabled(companion.Busy);
        ImGui.SetNextItemWidth(240f);
        if (ImGui.BeginCombo("Provider", CompanionConnection.ProviderLabel(this.plugin.CurrentProvider)))
        {
            foreach (var provider in new[] { "codex", "copilot", "grok" })
            {
                var unavailable = CompanionConnection.ProviderUnavailableReason(provider);
                ImGui.BeginDisabled(unavailable is not null);
                if (ImGui.Selectable(CompanionConnection.ProviderLabel(provider), provider == this.plugin.CurrentProvider))
                    this.plugin.SetProvider(provider);
                ImGui.EndDisabled();
                if (unavailable is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(unavailable);
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        ShowItemHelp("Changing provider selects its default for new chats (Auto for Grok). Existing chats keep their saved model; use the model picker beside Send to change an existing chat.");
        companion = this.plugin.CompanionView;
        ImGui.TextWrapped("Connection: " + companion.State);
        this.errorClipboard.Draw(companion.ConnectionError);
        ImGui.TextWrapped("Account: " + companion.Account);
        ImGui.TextWrapped("Reported allowance: " + companion.Quota);
        if (!companion.Busy)
        {
            ImGui.BeginDisabled(CompanionConnection.ProviderUnavailableReason(this.plugin.CurrentProvider) is not null);
            if (ImGui.Button("Sign in with " + CompanionConnection.ProviderLabel(this.plugin.CurrentProvider))) this.plugin.CompanionAction("login");
            ShowItemHelp("Opens the official sign-in page in your browser. Saved chats remain available while signed out.");
            SameLineIfFits("Refresh status");
            if (ImGui.Button("Refresh status")) this.plugin.CompanionAction("status");
            var localCredentials = this.plugin.CurrentProvider is "copilot" or "grok";
            var signOutLabel = localCredentials ? "Clear saved sign-in" : "Sign out";
            SameLineIfFits(signOutLabel);
            ImGui.BeginDisabled(!localCredentials && !companion.IsSignedIn);
            if (ImGui.Button(signOutLabel)) this.plugin.CompanionAction("logout");
            ImGui.EndDisabled();
            ShowItemHelp(localCredentials
                ? "Removes this provider's encrypted sign-in from CompAI. It does not revoke access at the provider or sign out other apps. Saved chats are retained. See Connection details to open the provider's account website."
                : "Signs out this companion. Saved chats are retained.");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Cancel companion action"))
            this.plugin.CancelCompanion();
        if (!string.IsNullOrEmpty(companion.LoginUrl))
        {
            if (this.plugin.LoginUserCode is { Length: > 0 } code)
            {
                ImGui.TextUnformatted("Device code: " + code);
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy code")) ImGui.SetClipboardText(code);
            }
            if (ImGui.Button("Copy official login link")) ImGui.SetClipboardText(companion.LoginUrl);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(450f);
                ImGui.TextUnformatted(companion.LoginUrl);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
        if (companion.SendBlockReason is { } reason) ImGui.TextWrapped(reason);
        if (companion.StorageError is { } storageError) ImGui.TextWrapped("Chat storage: " + storageError);
        ImGui.Spacing();
        ChatTheme.SectionHeading("Default for new chats");
        ModelPicker.DrawCompact(this.plugin, defaults: true);
        ModelPicker.DrawStatus(this.plugin, defaults: true);
        ImGui.TextWrapped("Existing chats keep their own model and reasoning settings.");
        ShowItemHelp("Each chat has its own model and reasoning controls. Available choices come from this companion's validated catalog; account entitlement is checked when sending.");
        ImGui.Spacing();
        ChatTheme.SectionHeading("Replies in all chats");
        var responseMinutes = this.plugin.ResponseLimitMinutes;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.BeginCombo("Response limit", responseMinutes == 0 ? "Automatic" : $"{responseMinutes} min"))
        {
            if (ImGui.Selectable("Automatic", responseMinutes == 0)) this.plugin.SetResponseLimitMinutes(0);
            if (responseMinutes == 0) ImGui.SetItemDefaultFocus();
            for (var minutes = 1; minutes <= 30; minutes++)
            {
                if (ImGui.Selectable($"{minutes} min", responseMinutes == minutes)) this.plugin.SetResponseLimitMinutes(minutes);
                if (responseMinutes == minutes) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ShowItemHelp("Automatic allows 5 minutes at Medium or below, and 10 minutes at High or above. Other provider-controlled or unknown reasoning levels use 5 minutes. You can Cancel at any time; questions are never retried automatically.");
        ImGui.TextWrapped("Applies to the next question in any chat, with any provider.");
        if (this.plugin.ResponseLimitError is { } responseLimitError) ImGui.TextWrapped(responseLimitError);
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Connection details"))
        {
            ImGui.TextWrapped(companion.PythonPrerequisite);
            if (this.plugin.CurrentProvider == "codex")
            {
                ImGui.TextWrapped("Python and the Codex runtime are included. Separate Python and ChatGPT desktop installations are not required.");
                ImGui.TextWrapped("Replies use your ChatGPT subscription's shared Codex/Work allowance. This is separate from the ChatGPT chat allowance.");
            }
            else if (this.plugin.CurrentProvider == "grok")
            {
                ImGui.TextWrapped("Grok (Beta): tested with the Free tier only; paid subscription tiers are unverified. No API-key or paid-credit fallback. Windows encrypts this companion's saved sign-in for your user account. Sign in again when the Grok access token expires.");
                ImGui.TextWrapped("Local readers and permitted public game-data tools are supported. Hosted web search and image input are unavailable on this connection.");
            }
            else if (CompanionConnection.ProviderUnavailableReason(this.plugin.CurrentProvider) is { } unavailable)
                ImGui.TextWrapped(unavailable);
            if (this.plugin.CurrentProvider is "grok" or "copilot")
            {
                ImGui.TextWrapped("Clear saved sign-in removes only this installation's saved credential. Review provider-side sessions or authorized apps on the provider's account website if you also want to revoke access.");
                if (ImGui.Button("Open provider account website"))
                {
                    try
                    {
                        Dalamud.Utility.Util.OpenLink(this.plugin.CurrentProvider == "copilot"
                            ? "https://github.com/settings/applications" : "https://accounts.x.ai/");
                        this.accountWebsiteOpenFailed = false;
                    }
                    catch { this.accountWebsiteOpenFailed = true; }
                }
                if (this.accountWebsiteOpenFailed) ImGui.TextWrapped("Could not open the browser. Visit your provider's account website manually.");
            }
            ImGui.TextWrapped("Game context and public game-data access are controlled in Access.");
        }
    }

    private void DrawDiagnostics()
    {
        if (ImGui.Button("Copy support summary")) ImGui.SetClipboardText(this.plugin.SupportSummary());
        ShowItemHelp("Local troubleshooting only: versions, readiness and reader states. No AI access, account names, character IDs, paths, logs or chat text. Copy and share manually when needed.");
        ImGui.SameLine();
        if (ImGui.Button("Copy measurements")) ImGui.SetClipboardText(PerformanceDiagnostics.Report);
        ImGui.SameLine();
        if (ImGui.Button("Clear measurements")) PerformanceDiagnostics.Clear();
        if (ImGui.CollapsingHeader("Measurements")) ImGui.TextWrapped(PerformanceDiagnostics.Report);
        ImGui.Spacing();
        this.DrawChallengeDiagnostic();
        if (!ImGui.CollapsingHeader("Connection test")) return;
        var companion = this.plugin.CompanionView;
        ImGui.TextUnformatted("Synthetic answer: " + companion.Answer);
        ImGui.TextWrapped("Uses subscription allowance. Sends pretend data only.");
        ImGui.BeginDisabled(companion.Busy || !companion.CanTest || this.plugin.SyntheticTestBlockReason is not null);
        if (ImGui.Button("Run synthetic test"))
            this.plugin.CompanionAction("test");
        ImGui.EndDisabled();
        ShowItemHelp("Two pretend TEST TOKEN stacks (3 + 2), using GPT-6 Sol / Medium. This test never sends inventory.");
        if (this.plugin.SyntheticTestBlockReason is { } reason) ImGui.TextWrapped(reason);
    }

    private static void SameLineIfFits(string buttonLabel)
    {
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize(buttonLabel).X + style.FramePadding.X * 2;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + width <= right) ImGui.SameLine();
    }
}
