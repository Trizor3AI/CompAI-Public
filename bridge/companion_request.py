"""Normalize supported wire versions once before application/provider logic."""
from copy import deepcopy
from dataclasses import dataclass

from inventory_question import validate_request, validate_history
from model_selection import DEFAULT_MODEL, DEFAULT_EFFORT
from quest_context import validate_quests
from quest_lookup import validate_summary as validate_quest_summary
from inventory_lookup import validate_summary as validate_inventory_summary
from currency_lookup import validate_summary as validate_currency_summary
from duty_lookup import validate_summary as validate_duty_summary
from collection_lookup import validate_summary as validate_collection_summary
from progression_lookup import validate_summary as validate_progression_summary
from crafting_log_lookup import validate_summary as validate_crafting_log_summary
from menu_lookup import validate_summary as validate_menu_summary
from activity_lookup import validate_summary as validate_activity_summary
from game_definitions_lookup import validate_summary as validate_definitions_summary
from gc_supply_lookup import validate_summary as validate_gc_summary
from npc_target_lookup import validate_summary as validate_target_summary
from storage_lookup import validate_summary as validate_storage_summary
from saved_item_valuation import validate_summary as validate_valuation_summary
from chat_history_lookup import validate_summary as validate_history_summary, validate_notes
from personality import validate_personality
from game_context_lookup import validate_summary as validate_game_context_summary
from npc_dialogue_lookup import validate_summary as validate_npc_dialogue_summary
from character_details_lookup import validate_summary as validate_character_details_summary
from personality_details_lookup import validate_summary as validate_personality_details_summary


@dataclass(frozen=True)
class QuestionRequest:
    source_version: int
    provider: str
    question: str
    snapshot: dict | None
    model: str
    effort: str
    web_search: bool
    history: list
    quests: dict | None
    quest_lookup: dict | None
    inventory_lookup: dict | None
    currency_lookup: dict | None
    duty_lookup: dict | None
    game_definitions_lookup: dict | None
    gc_supply_lookup: dict | None
    npc_target_lookup: dict | None
    storage_lookup: dict | None
    history_lookup: dict | None
    chat_notes: str
    personality: dict | None
    game_context_lookup: dict | None
    npc_dialogue_lookup: dict | None
    character_details_lookup: dict | None
    spoilers_allowed: bool | None
    personality_details_lookup: dict | None
    includes_history: bool
    includes_notes: bool
    includes_personality: bool
    optional_web_search: bool
    public_data_allowed: bool
    attachments: list
    valuation_lookup: dict | None = None
    collection_lookup: dict | None = None
    progression_lookup: dict | None = None
    crafting_log_lookup: dict | None = None
    menu_lookup: dict | None = None
    activity_lookup: dict | None = None

    def context(self, references):
        result = {"inventory": self.snapshot, "reference": references}
        if self.includes_history:
            result["history"] = self.history
        if self.includes_notes:
            result["chat_notes"] = self.chat_notes
        if self.includes_personality:
            result["personality"] = self.personality
        if self.spoilers_allowed is not None:
            result["spoilers_allowed"] = self.spoilers_allowed
        for name in ("personality_details_lookup", "history_lookup", "quests", "inventory_lookup",
                     "game_context_lookup", "npc_dialogue_lookup", "character_details_lookup", "quest_lookup", "currency_lookup", "duty_lookup",
                     "game_definitions_lookup", "gc_supply_lookup", "npc_target_lookup", "storage_lookup", "valuation_lookup", "collection_lookup", "progression_lookup", "crafting_log_lookup", "menu_lookup", "activity_lookup"):
            value = getattr(self, name)
            if value is not None:
                result[name] = value
        return result


def normalize_request(request, now=None):
    # Existing validators retain the exact per-version field/permission contracts.
    # Copy only after validation bounds the input; no nested caller aliases remain.
    question, snapshot = validate_request(request, now=now)
    request = deepcopy(request)
    version = request["version"]
    return QuestionRequest(
        source_version=version, provider=request["provider"] if version >= 21 else "codex",
        question=question, snapshot=deepcopy(snapshot),
        model=request["model"] if version >= 7 else DEFAULT_MODEL,
        effort=request["reasoning_effort"] if version >= 7 else DEFAULT_EFFORT,
        web_search=request.get("web_search", False),
        history=validate_history(request["history"], flexible=version >= 9) if version >= 3 else [],
        quests=validate_quests(request["quests"], now) if version == 5 else None,
        quest_lookup=validate_quest_summary(request["quest_lookup"], now) if version >= 6 else None,
        inventory_lookup=validate_inventory_summary(request["inventory_lookup"], now) if version >= 8 else None,
        currency_lookup=validate_currency_summary(request["currency_lookup"], now) if version >= 15 else None,
        duty_lookup=validate_duty_summary(request["duty_lookup"], now) if version >= 16 else None,
        game_definitions_lookup=validate_definitions_summary(request["game_definitions_lookup"]) if version >= 17 else None,
        gc_supply_lookup=validate_gc_summary(request["gc_supply_lookup"], now) if version >= 17 else None,
        npc_target_lookup=validate_target_summary(request["npc_target_lookup"], now) if version >= 17 else None,
        storage_lookup=validate_storage_summary(request["storage_lookup"]) if version >= 20 else None,
        history_lookup=validate_history_summary(request["history_lookup"]) if version >= 9 else None,
        chat_notes=validate_notes(request["chat_notes"]) if version >= 9 else "",
        personality=validate_personality(request["personality"]) if version >= 10 else None,
        game_context_lookup=validate_game_context_summary(request["game_context_lookup"], now, version=version) if version >= 11 else None,
        npc_dialogue_lookup=validate_npc_dialogue_summary(request["npc_dialogue_lookup"], now) if version >= 13 else None,
        character_details_lookup=validate_character_details_summary(request["character_details_lookup"], now) if version >= 13 else None,
        spoilers_allowed=request["spoilers_allowed"] if version >= 13 else None,
        personality_details_lookup=validate_personality_details_summary(request["personality_details_lookup"]) if version >= 14 else None,
        includes_history=version >= 3, includes_notes=version >= 9, includes_personality=version >= 10,
        optional_web_search=version >= 6, public_data_allowed=version >= 14 and request.get("web_search", False),
        # validate_request checked the bytes before the request was copied.
        attachments=request.get("attachments", []),
        valuation_lookup=validate_valuation_summary(request["valuation_lookup"]) if version >= 22 else None,
        collection_lookup=validate_collection_summary(request["collection_lookup"], version=version) if version >= 23 else None,
        progression_lookup=validate_progression_summary(request["progression_lookup"], version=version) if version >= 25 else None,
        crafting_log_lookup=validate_crafting_log_summary(request["crafting_log_lookup"]) if version >= 28 else None,
        menu_lookup=validate_menu_summary(request["menu_lookup"]) if version >= 31 else None,
        activity_lookup=validate_activity_summary(request["activity_lookup"], version=version) if version >= 33 else None,
    )


def recheck_request(request, prepared):
    """Revalidate expiry and every approved field immediately before dispatch."""
    current = normalize_request(request)
    if current.provider != prepared.provider:
        raise RuntimeError("Provider changed during request preparation; no fallback")
    if current.web_search != prepared.web_search:
        raise RuntimeError("Online permission changed during request preparation")
    if (current.source_version, current.model, current.effort) != (prepared.source_version, prepared.model, prepared.effort):
        raise RuntimeError("Model/reasoning changed during request preparation")
    if current != prepared:
        raise RuntimeError("Approved context, reader permissions or snapshot changed during request preparation")
    return current
