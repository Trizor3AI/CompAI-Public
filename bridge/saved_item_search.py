"""One on-demand name search over existing permission-bound inventory readers.

No new cache or item identity is created: nested results retain their original
source, container, slot, capture time and per-reader validation.
"""
from copy import deepcopy

from quest_lookup import encoded_size, integer
from storage_lookup import clean_text


FIELDS = {"query", "carried_offset", "storage_offset", "limit"}
KEYS = {"inventory": "carried", "storage": "storage"}
UNAVAILABLE = {"reader_unavailable": True}


def unavailable(value):
    return isinstance(value, dict) and set(value) == {"reader_unavailable"} and value["reader_unavailable"] is True


def validate_arguments(value, registrations):
    if not isinstance(value, dict) or set(value) != FIELDS:
        raise RuntimeError("Invalid saved-item search arguments")
    clean_text(value["query"], 200, empty=True)
    integer(value["limit"], 1, 15)
    for reader, key in KEYS.items():
        offset = value[key + "_offset"]
        if offset is not None:
            integer(offset, 0, 2147483647)
            if reader not in registrations:
                raise PermissionError("Saved-item source is not permitted")
    if all(value[key + "_offset"] is None for key in KEYS.values()):
        raise RuntimeError("Choose at least one permitted saved-item source")
    return value


def backend_arguments(value, reader):
    common = {"view": "items", "query": value["query"],
              "offset": value[KEYS[reader] + "_offset"], "limit": value["limit"]}
    return {**common, **({"ids": [], "bags": []} if reader == "inventory" else
                        {"category": "all", "source": ""})}


def reader_counts(arguments):
    return {reader: 1 for reader, key in KEYS.items() if arguments[key + "_offset"] is not None}


def query(arguments, registrations):
    result = {key: None for key in KEYS.values()}
    for reader in reader_counts(arguments):
        registration = registrations[reader]
        args = registration.validate_arguments(backend_arguments(arguments, reader))
        try:
            raw = registration.query(args)
        except PermissionError:
            raise
        except (OSError, TimeoutError):
            raw = dict(UNAVAILABLE)
        if not unavailable(raw):
            raw = registration.validate_result(raw, registration.summary, args)
        result[KEYS[reader]] = raw
    return result


def validate_result(value, registrations, arguments):
    if not isinstance(value, dict) or set(value) != set(KEYS.values()) or encoded_size(value) > 40500:
        raise RuntimeError("Invalid saved-item search result")
    for reader, key in KEYS.items():
        raw = value[key]
        if arguments[key + "_offset"] is None:
            if raw is not None:
                raise RuntimeError("Unrequested saved-item source returned data")
        elif not unavailable(raw):
            registration = registrations[reader]
            registration.validate_result(raw, registration.summary, backend_arguments(arguments, reader))
    return value


TOOL = {"type": "function", "name": "search_saved_items",
        "description": "Default item-name/count/location search across ALL permitted carried bags and saved storage, including crystal/key-item tabs, in one request. Return bounded matching pages with original locations, namespaces and timestamps. No live prices or game interaction.",
        "inputSchema": {"type": "object", "additionalProperties": False,
                        "required": sorted(FIELDS), "properties": {
                            "query": {"type": "string", "maxLength": 200,
                                      "description": "All whitespace-separated item-name terms. Empty lists items; it does not price or rank them."},
                            "carried_offset": {"type": ["integer", "null"], "minimum": 0, "maximum": 2147483647,
                                               "description": "Start at 0. Continue using carried.next_offset; null skips a completed/unavailable source."},
                            "storage_offset": {"type": ["integer", "null"], "minimum": 0, "maximum": 2147483647,
                                               "description": "Start at 0. Continue using storage.next_offset; null skips a completed/unavailable source."},
                            "limit": {"type": "integer", "minimum": 1, "maximum": 15}}}}


def permitted_tool(registrations):
    definition = deepcopy(TOOL)
    for reader, key in KEYS.items():
        if reader not in registrations:
            definition["inputSchema"]["properties"][key + "_offset"] = {
                "type": "null", "description": "This source is not permitted/available for this question."}
    return definition


INSTRUCTIONS = """
For ordinary 'do I have', 'how many', 'where is' or 'my inventories' questions, prefer search_saved_items.
Start both permitted offsets at 0 so saddlebag/retainers are not missed. It scans ALL cached matching items locally;
the page limit limits returned rows, not the search coverage. Each selected source costs one of the shared 12 calls.
The storage section also contains permitted Armoury gear/materia, personal crystals/key items and retainer crystals.
Armoury is actual stored gear, not equipped gear or saved gearset references; attached materia is not loose stock.
Key-item rows have
item_namespace=event_item and full EventItem IDs; preserve this distinction from ordinary Item rows. Do not strip
their IDs, price them, or merge their quantities with ordinary items just because the names are similar.
The carried and storage sections keep their own total_quantity, total_matches and next_offset. Paginate only when
individual locations/details are needed; totals already cover matching rows beyond the page. Null means skipped or
unavailable to this question, never empty; reader_unavailable means unknown. Disabled sources must not be queried.
Carried bags and cached storage can reflect different times. Empty searches never establish absence from unopened
storage. Give counts by source when ownership/use differs: placed furnishings, FC items and retainer offers are
not interchangeable loose personal items. Do not add a retainer's listed offers to assumed saleable bag stock.
Use query_inventory for pedigree comparisons, exact IDs/bag slots and manual Find in bags suggestions. Use
query_storage for category/source filtering or container coverage. Preserve locations from their original reader;
no persistent identity or screen position can be inferred from slot numbers. Do not claim all inventories were
checked if a source was skipped, unavailable or disabled. Name search does not establish live market value.
"""
