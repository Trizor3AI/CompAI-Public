"""Fixed application tool registry; existing modules own all FFXIV validation."""
from dataclasses import dataclass

import quest_lookup
import inventory_lookup
import currency_lookup
import duty_lookup
import collection_lookup
import progression_lookup
import crafting_log_lookup
import menu_lookup
import activity_lookup
import game_definitions_lookup
import gc_supply_lookup
import npc_target_lookup
import storage_lookup
import saved_item_search
import saved_item_valuation
import chat_history_lookup
import game_context_lookup
import npc_dialogue_lookup
import character_details_lookup
import personality_details_lookup
import game_data_tools
import gear_plan_lookup


@dataclass(frozen=True)
class ToolRegistration:
    definition: dict
    summary: dict | None
    query: object
    validate_arguments: object
    validate_result: object
    reader: str
    label: str
    instructions: str
    reader_counts: object = None


# Registration, prompt instructions and dispatch share this one ordered inventory.
LOCAL_TOOLS = (
    ("quest_lookup", "quests", "Quest", quest_lookup),
    ("inventory_lookup", "inventory", "Inventory", inventory_lookup),
    ("currency_lookup", "currencies", "Currencies", currency_lookup),
    ("duty_lookup", "duties", "Duties", duty_lookup),
    ("collection_lookup", "collections", "Collections", collection_lookup),
    ("progression_lookup", "progression", "Progression", progression_lookup),
    ("crafting_log_lookup", "crafting_log", "Crafting log", crafting_log_lookup),
    ("menu_lookup", "menus", "Shops and glamour", menu_lookup),
    ("activity_lookup", "activities", "Activities", activity_lookup),
    ("game_definitions_lookup", "game_definitions", "Game definitions", game_definitions_lookup),
    ("gc_supply_lookup", "gc_supply", "Grand Company deliveries", gc_supply_lookup),
    ("npc_target_lookup", "npc_target", "Selected target", npc_target_lookup),
    ("storage_lookup", "storage", "Storage", storage_lookup),
    ("history_lookup", "history", "Chat history", chat_history_lookup),
    ("game_context_lookup", "game_context", "Game context", game_context_lookup),
    ("npc_dialogue_lookup", "npc_dialogue", "NPC dialogue", npc_dialogue_lookup),
    ("character_details_lookup", "character_details", "Character details", character_details_lookup),
    ("personality_details_lookup", "personality_details", "Personality details", personality_details_lookup),
)
TOOL_LABELS = {"query_" + reader: label for _, reader, label, _ in LOCAL_TOOLS}
TOOL_LABELS["query_chat_history"] = TOOL_LABELS.pop("query_history")
TOOL_LABELS["query_selected_target"] = TOOL_LABELS["query_npc_target"]
TOOL_LABELS.update(find_item="Item search", get_item="Item details", get_market_data="Market data", get_gear_plan="Gear plan")
TOOL_LABELS["search_saved_items"] = "Saved items"
TOOL_LABELS["price_saved_items"] = "Saved-item pricing"
READER_NAMES = tuple(reader for _, reader, _, _ in LOCAL_TOOLS) + ("public_data", "valuation")


def build_registry(request, readers, public_data, failure_type):
    result = {}
    for field, reader, label, module in LOCAL_TOOLS:
        summary = getattr(request, field)
        if summary is None:
            continue
        query = readers.get(reader)
        if not callable(query):
            raise failure_type("reader", label + " lookup is unavailable; no local reader")
        validate_result = module.validate_result
        if reader == "activities":
            # Activity categories can gain bounded fields in a later wire version.
            # Bind the approved envelope version so legacy requests cannot accept those fields.
            version = request.source_version
            definition = module.permitted_tool(summary, version=version)
            instructions = module.permitted_instructions(summary, version=version)
            validate_result = lambda value, metadata, arguments, module=module, version=version: module.validate_result(
                value, metadata, arguments, version=version)
        elif reader in ("game_context", "npc_target", "game_definitions", "storage", "collections", "progression", "menus"):
            definition = module.permitted_tool(summary)
            instructions = module.permitted_instructions(summary)
        else:
            definition, instructions = module.TOOL, module.INSTRUCTIONS
        validate_args = module.validate_arguments
        if reader in ("game_context", "personality_details", "game_definitions", "storage", "collections", "progression", "menus", "activities"):
            validate_args = lambda args, module=module, summary=summary: module.validate_arguments(args, summary)
        result[definition["name"]] = ToolRegistration(definition, summary, query,
            validate_args, validate_result, reader, label, instructions)
    if "query_storage" in result:
        sources = {reader: result[name] for reader, name in
                   (("inventory", "query_inventory"), ("storage", "query_storage")) if name in result}
        result["search_saved_items"] = ToolRegistration(saved_item_search.permitted_tool(sources), sources,
            lambda args: saved_item_search.query(args, sources),
            lambda args: saved_item_search.validate_arguments(args, sources),
            saved_item_search.validate_result, "inventory" if "inventory" in sources else "storage",
            "Saved items", saved_item_search.INSTRUCTIONS, saved_item_search.reader_counts)
    if public_data is not None:
        if not request.public_data_allowed:
            raise failure_type("permission", "Public game data is not permitted")
        public_data.allowed_gear_plan_ids = gear_plan_lookup.share_ids_in_texts(
            [request.question] + [row.get("question", "") for row in request.history if isinstance(row, dict)])
        for definition in game_data_tools.TOOLS:
            name = definition["name"]
            if name == "get_gear_plan" and not public_data.allowed_gear_plan_ids:
                continue
            result[name] = ToolRegistration(definition, None,
                lambda args, name=name: public_data.call(name, args),
                lambda args, name=name: game_data_tools.validate_arguments(name, args),
                lambda data, meta, args: game_data_tools.validate_result(data),
                "public_data", TOOL_LABELS[name], "")
        summary = getattr(request, "valuation_lookup", None)
        if summary is not None:
            query = readers.get("valuation")
            if not callable(query):
                raise failure_type("reader", "Saved-item pricing is unavailable; no local reader")
            valuation = saved_item_valuation.SavedItemValuation(summary, query, public_data)
            result["price_saved_items"] = ToolRegistration(saved_item_valuation.permitted_tool(summary), summary,
                valuation.query, lambda args: saved_item_valuation.validate_arguments(args, summary),
                valuation.validate_result, "valuation", "Saved-item pricing", saved_item_valuation.INSTRUCTIONS)
    return result


def registry_instructions(registry):
    result = "".join(tool.instructions for tool in registry.values())
    if any(tool.reader == "public_data" for tool in registry.values()):
        result += game_data_tools.permitted_instructions(registry)
    return result
